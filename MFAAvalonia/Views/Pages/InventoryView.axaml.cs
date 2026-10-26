using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MFAAvalonia.Views.Pages;

public partial class InventoryView : UserControl
{
    public sealed class Row
    {
        public InventorySnapshot.Item Item { get; init; } = new();
        public string Name => Item.Name;
        public string Category => Item.Category;
        public InventoryComparison.Entry? Comparison { get; init; }
        public string Quantity => Comparison?.Current ?? Item.Quantity;
        public string Before => "历史：" + Comparison?.Before;
        public string Change => Comparison?.Change ?? "";
        public bool IsComparison => Comparison != null;
        public string Note => Comparison?.Reason is { Length: > 0 } reason ? reason : Item.Note;
        public bool HasNote => !string.IsNullOrEmpty(Note);
        public Bitmap? Icon { get; init; }
    }

    private InventorySnapshot? _snapshot;
    private List<Row> _rows = new();
    private List<Row>? _comparisonRows;
    private long _comparisonGeneration;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _busy;
    private bool _ready;
    private string _loadedJson = "";
    private MaaProcessor? _captureProcessor;
    private long _captureGeneration;
    private bool OwnsCapture => _captureProcessor?.IsTaskRunActive == true && _captureProcessor.TaskRunGeneration == _captureGeneration;
    private static bool Running => MaaProcessorManager.Instance.Instances.Any(p => p.IsTaskRunActive);
    private static string SnapshotPath => Path.Combine(AppPaths.DataRoot, "data", "inventory", "latest.json");

    public InventoryView()
    {
        InitializeComponent();
        _ready = true;
        Loaded += async (_, _) => { await Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += async (_, _) =>
        {
            UpdateButtons();
            if (_captureProcessor != null && !OwnsCapture)
            {
                _captureProcessor = null;
                StatusText.Text = "";
            }
            await Refresh(false);
        };
    }

    private void UpdateButtons()
    {
        CollectButton.IsEnabled = !_busy && !Running;
        StopButton.IsEnabled = OwnsCapture;
        ExportButton.IsEnabled = !_busy && _snapshot != null;
        CompareButton.IsEnabled = !_busy && _snapshot != null;
        RunComparisonButton.IsEnabled = !_busy && _snapshot != null && HistorySelector.ItemCount > 0;
    }

    private async Task Guard(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        UpdateButtons();
        try { await action(); }
        catch (Exception exception)
        {
            StatusText.Text = exception switch {
                Newtonsoft.Json.JsonException => "仓库记录 JSON 格式错误，请检查快照文件或重新采集。",
                FormatException or InvalidCastException => "仓库记录字段格式错误，请检查快照文件或重新采集。",
                InvalidDataException => exception.Message,
                UnauthorizedAccessException => "无法访问仓库记录，请检查文件读取权限。",
                IOException => "仓库记录读写失败，文件可能已被移除或正在使用，请刷新后重试。",
                _ => exception.Message
            };
            LoggerHelper.Error("仓库资源页面：" + exception.Message, exception);
        }
        finally { _busy = false; UpdateButtons(); }
    }

    private Task Refresh(bool manual = true) => Guard(async () =>
    {
        if (!File.Exists(SnapshotPath))
        {
            if (_snapshot != null) ClearSnapshot();
            if (manual) StatusText.Text = "连接设备后开始识别。识别过程不会使用或出售仓库物品。";
            return;
        }
        // Collector atomically replaces latest.json. Keep one in-memory snapshot for UI and exports.
        var json = await File.ReadAllTextAsync(SnapshotPath);
        if (json == _loadedJson) return;
        var catalogPath = Path.Combine(AppPaths.DataRoot, "resource", "base", "image", "仓库", "catalog.json");
        var catalogJson = File.Exists(catalogPath) ? await File.ReadAllTextAsync(catalogPath) : null;
        var snapshot = InventorySnapshot.Parse(json, AppPaths.DataRoot, catalogJson);
        var rows = snapshot.Items.Select(item => new Row { Item = item, Icon = LoadIcon(item.ImagePath) }).ToList();
        var oldRows = _rows;
        _snapshot = snapshot;
        _rows = rows;
        _loadedJson = json;
        ClearComparisonRows();
        if (ComparisonPanel.IsVisible)
            ComparisonText.Text = "当前快照已更新，请选择历史快照并点击“开始对比”。";
        // Pull equivalence is display-only; keep the stored/exported currency amount intact.
        var summaries = snapshot.Currencies.Select(item => item.Name == "异方晶"
            ? new InventorySnapshot.Item {
                Name = "异方晶 / 抽数",
                Quantity = item.NumericQuantity is long crystals
                    ? $"{crystals} / {crystals / 180}" : "未识别 / —"
            } : item).ToList();
        foreach (var name in InventorySnapshot.TicketNames)
            summaries.Add(snapshot.Items.FirstOrDefault(item => item.Name == name)
                ?? new InventorySnapshot.Item { Name = name, Quantity = "未识别" });
        CurrencyCards.ItemsSource = summaries;
        var timestamp = DateTimeOffset.TryParse(snapshot.CapturedAt, out var time)
            ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : snapshot.CapturedAt;
        SnapshotText.Text = $"更新于 {timestamp}";
        ApplyFilter();
        foreach (var row in oldRows) row.Icon?.Dispose();
    });

    private static Bitmap? LoadIcon(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try { using var stream = File.OpenRead(path); return Bitmap.DecodeToWidth(stream, 64); }
        catch (Exception exception) when (exception is IOException or ArgumentException) { return null; }
    }

    private void ClearSnapshot()
    {
        _snapshot = null; _loadedJson = "";
        ClearComparisonRows();
        ComparisonPanel.IsVisible = false;
        CurrencyCards.ItemsSource = null;
        ItemsCards.ItemsSource = null;
        foreach (var row in _rows) row.Icon?.Dispose();
        _rows.Clear();
        SnapshotText.Text = "尚未采集仓库资源";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (!_ready) return;
        var search = SearchBox.Text?.Trim() ?? "";
        var rows = (_comparisonRows ?? _rows)
            .Where(row => row.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(row => _comparisonRows == null || ChangesOnly.IsChecked != true || row.Comparison?.Changed == true).ToList();
        ItemsCards.ItemsSource = rows;
        EmptyText.IsVisible = rows.Count == 0;
        EmptyText.Text = _snapshot == null ? "暂无记录，连接设备后点击“开始识别”。"
            : _comparisonRows != null && ChangesOnly.IsChecked == true ? "当前筛选下没有可确认的数量变更；无法比较的项目可取消“只看变更”查看。"
            : "没有符合筛选条件的物品。";
    }

    private async void OnOpenComparison(object? sender, RoutedEventArgs args) => await Guard(() =>
    {
        HistorySelector.ItemsSource = InventoryHistory.List(AppPaths.DataRoot);
        HistorySelector.SelectedIndex = -1;
        ComparisonPanel.IsVisible = true;
        ClearComparisonRows();
        ComparisonText.Text = HistorySelector.ItemCount == 0 ? "暂无历史快照，请先采集仓库资源。"
            : "选择历史快照后点击“开始对比”。差值为当前数量减去历史数量。";
        ApplyFilter();
        return Task.CompletedTask;
    });

    private async void OnCompare(object? sender, RoutedEventArgs args) => await Guard(async () =>
    {
        var current = _snapshot;
        var generation = _comparisonGeneration;
        if (current == null) return;
        if (HistorySelector.SelectedItem is not string fileName)
        {
            ComparisonText.Text = "请先选择一份历史快照。";
            return;
        }
        // Only selected, enumerated filenames are accepted; history is never an arbitrary file picker.
        if (!InventoryHistory.List(AppPaths.DataRoot).Contains(fileName))
            throw new InvalidDataException("所选历史快照已不可用，请关闭对比后重新选择。");
        var json = await File.ReadAllTextAsync(InventoryHistory.Resolve(AppPaths.DataRoot, fileName));
        var catalogPath = Path.Combine(AppPaths.DataRoot, "resource", "base", "image", "仓库", "catalog.json");
        var catalog = File.Exists(catalogPath) ? await File.ReadAllTextAsync(catalogPath) : null;
        // Closing the panel while the file is loading must not restore comparison results afterwards.
        if (!ComparisonPanel.IsVisible || generation != _comparisonGeneration || !ReferenceEquals(current, _snapshot)) return;
        var history = InventorySnapshot.Parse(json, AppPaths.DataRoot, catalog);
        var comparison = InventoryComparison.Compare(history, current);
        ClearComparisonRows();
        _comparisonRows = comparison.Entries.Select(entry => new Row {
            Item = entry.Item, Comparison = entry, Icon = LoadIcon(entry.Item.ImagePath)
        }).ToList();
        ComparisonText.Text = $"历史 {DisplayTime(history.CapturedAt)} → 当前 {DisplayTime(current.CapturedAt)}\n"
            + $"确认变更 {comparison.Entries.Count(entry => entry.Changed)} 项，无法比较 {comparison.Entries.Count(entry => !entry.Difference.HasValue)} 项。"
            + comparison.Notice;
        ApplyFilter();
    });

    private static string DisplayTime(string timestamp) => DateTimeOffset.TryParse(timestamp, out var time)
        ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : timestamp;

    private void ClearComparisonRows()
    {
        _comparisonGeneration++;
        if (_comparisonRows != null)
            foreach (var row in _comparisonRows) row.Icon?.Dispose();
        _comparisonRows = null;
    }

    private void OnCloseComparison(object? sender, RoutedEventArgs args)
    {
        ClearComparisonRows();
        ComparisonPanel.IsVisible = false;
        ChangesOnly.IsChecked = false;
        ApplyFilter();
    }

    private void OnChangesOnlyChanged(object? sender, RoutedEventArgs args) => ApplyFilter();

    private void OnSearchChanged(object? sender, TextChangedEventArgs args) => ApplyFilter();
    private async void OnRefresh(object? sender, RoutedEventArgs args) => await Refresh();
    private async void OnCollect(object? sender, RoutedEventArgs args)
    {
        if (Running) return;
        await Guard(() =>
        {
            var processor = MaaProcessorManager.Instance.Current;
            if (processor.ViewModel?.CurrentResource is not ("官服" or "B服"))
                throw new InvalidOperationException("请先在首页选择官服或 B服中文资源及设备。");
            var task = new MaaInterface.MaaInterfaceTask {
                Name = "仓库识别", Entry = "仓库识别", Check = true,
                Resource = new List<string> { "官服", "B服" }
            };
            if (Running || !processor.TryStartTasks(new List<DragItemViewModel> { new(task) }, ignoreCheckedState: true))
                throw new InvalidOperationException("已有任务正在运行，请结束后再识别仓库。");
            _captureProcessor = processor;
            _captureGeneration = processor.TaskRunGeneration;
            StatusText.Text = "正在使用当前实例识别仓库。结果自动保存，运行日志在监控页；下方仍可查看上次快照。";
            return Task.CompletedTask;
        });
    }

    private void OnStop(object? sender, RoutedEventArgs args)
    {
        // Only stop the run this page started, even after switching instances or starting another task.
        if (OwnsCapture) _captureProcessor!.Stop(MFATask.MFATaskStatus.STOPPED);
    }

    private async void OnExportJson(object? sender, RoutedEventArgs args) => await Export("json");
    private async void OnExportMarkdown(object? sender, RoutedEventArgs args) => await Export("md");
    private async Task Export(string extension)
    {
        var snapshot = _snapshot;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (snapshot == null || storage == null) return;
        // Capture the snapshot before opening the dialog; a running scan cannot mix two exports.
        var content = extension == "json" ? snapshot.Json : snapshot.ToMarkdown();
        await Guard(async () =>
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions {
                Title = "导出全部仓库资源", SuggestedFileName = $"仓库资源-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}",
                DefaultExtension = extension, ShowOverwritePrompt = true,
                FileTypeChoices = new[] { new FilePickerFileType(extension) { Patterns = new[] { "*." + extension } } }
            });
            if (file == null) return;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(content);
            StatusText.Text = "已导出全部仓库记录（包含未识别项和冲突，不受当前筛选影响）。";
        });
    }
}
