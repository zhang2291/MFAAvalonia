using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
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
    private static readonly IBrush GoldBrush = SolidColorBrush.Parse("#C2964E");
    private static readonly IBrush PurpleBrush = SolidColorBrush.Parse("#8B5CF6");
    private static readonly IBrush BlueBrush = SolidColorBrush.Parse("#4B6FA8");
    private static readonly IBrush GreenBrush = SolidColorBrush.Parse("#16A34A");
    private static readonly IBrush GreenBgBrush = SolidColorBrush.Parse("#1D16A34A");
    private static readonly IBrush RedBrush = SolidColorBrush.Parse("#EF4444");
    private static readonly IBrush RedBgBrush = SolidColorBrush.Parse("#22DC2626");
    private static readonly IBrush MutedBrush = SolidColorBrush.Parse("#6B7280");
    private static readonly IBrush MutedBgBrush = SolidColorBrush.Parse("#14808080");

    public sealed class CurrencyCard
    {
        public string Name { get; init; } = "";
        public string Quantity { get; init; } = "未识别";
        public string SubText { get; init; } = "";
        public bool HasSubText => !string.IsNullOrEmpty(SubText);
    }

    public sealed class Row
    {
        public InventorySnapshot.Item Item { get; init; } = new();
        public string Name => Item.Name;
        public string Category => Item.Category;
        public string Rarity { get; init; } = "普";
        public IBrush RarityDotBrush => Rarity switch
        {
            "狂" => GoldBrush,
            "危" => PurpleBrush,
            _ => BlueBrush,
        };
        public InventoryComparison.Entry? Comparison { get; init; }
        public string Quantity => Comparison?.Current ?? FormatQuantity(Item.Quantity);
        public string Before => "历史：" + Comparison?.Before;
        public string Change => Comparison?.Change ?? "";
        public bool IsComparison => Comparison != null;
        public IBrush ChangeBg => Comparison?.Difference switch
        {
            > 0 => GreenBgBrush,
            < 0 => RedBgBrush,
            _ => MutedBgBrush,
        };
        public IBrush ChangeFg => Comparison?.Difference switch
        {
            > 0 => GreenBrush,
            < 0 => RedBrush,
            _ => MutedBrush,
        };
        public string Note => Comparison?.Reason is { Length: > 0 } reason ? reason : Item.Note;
        public bool HasNote => !string.IsNullOrEmpty(Note);
        public Bitmap? Icon { get; init; }
        public bool HasIcon => Icon != null;
        public bool HasNoIcon => Icon == null;

        private static string FormatQuantity(string raw) =>
            long.TryParse(raw, out var count) ? count.ToString("N0") : raw;
    }

    private InventorySnapshot? _snapshot;
    private List<Row> _rows = new();
    private List<Row>? _comparisonRows;
    private string? _comparedFileName;
    private string? _pendingDeleteFileName;
    private long _comparisonGeneration;
    private string _selectedRarity = "全部";
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
                SetStatus("");
            }
            await Refresh(false);
        };
    }

    private void SetStatus(string? text)
    {
        var visible = !string.IsNullOrWhiteSpace(text);
        StatusBanner.IsVisible = visible;
        StatusText.Text = text ?? "";
    }

    private void UpdateButtons()
    {
        var hasHistorySelection = HistorySelector.SelectedItem is InventoryHistory.Entry;
        CollectButton.IsEnabled = !_busy && !Running;
        StopButton.IsEnabled = OwnsCapture;
        StopButton.IsVisible = OwnsCapture;
        ExportButton.IsEnabled = !_busy && _snapshot != null;
        CompareButton.IsEnabled = !_busy && _snapshot != null;
        RunComparisonButton.IsEnabled = !_busy && _snapshot != null && hasHistorySelection;
        RenameSnapshotButton.IsEnabled = !_busy && hasHistorySelection;
        DeleteSnapshotButton.IsEnabled = !_busy && hasHistorySelection;
    }

    private async Task Guard(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        UpdateButtons();
        try { await action(); }
        catch (Exception exception)
        {
            SetStatus(exception switch {
                Newtonsoft.Json.JsonException => "仓库记录 JSON 格式错误，请检查快照文件或重新采集。",
                FormatException or InvalidCastException => "仓库记录字段格式错误，请检查快照文件或重新采集。",
                InvalidDataException => exception.Message,
                UnauthorizedAccessException => "无法访问仓库记录，请检查文件读取权限。",
                IOException => "仓库记录读写失败，文件可能已被移除或正在使用，请刷新后重试。",
                _ => exception.Message
            });
            LoggerHelper.Error("仓库资源页面：" + exception.Message, exception);
        }
        finally { _busy = false; UpdateButtons(); }
    }

    private Task Refresh(bool manual = true) => Guard(async () =>
    {
        if (!File.Exists(SnapshotPath))
        {
            if (_snapshot != null) ClearSnapshot();
            if (manual) SetStatus("连接设备后点击“开始识别”。识别过程不会使用或出售仓库物品。");
            return;
        }
        // Collector atomically replaces latest.json. Keep one in-memory snapshot for UI and exports.
        var json = await File.ReadAllTextAsync(SnapshotPath);
        if (json == _loadedJson) return;
        var catalogPath = Path.Combine(AppPaths.DataRoot, "resource", "base", "image", "仓库", "catalog.json");
        var catalogJson = File.Exists(catalogPath) ? await File.ReadAllTextAsync(catalogPath) : null;
        var snapshot = InventorySnapshot.Parse(json, AppPaths.DataRoot, catalogJson);
        var rows = snapshot.Items.Select(item => new Row
        {
            Item = item,
            Rarity = ClassifyRarity(item.Name),
            Icon = LoadIcon(item.ImagePath),
        }).ToList();
        var oldRows = _rows;
        _snapshot = snapshot;
        _rows = rows;
        _loadedJson = json;
        ClearComparisonRows();
        if (ComparisonPanel.IsVisible)
            ComparisonText.Text = "当前快照已更新，请选择历史快照并点击“开始对比”。";

        var summaries = snapshot.Currencies.Select(item =>
        {
            if (item.Name == "异方晶")
            {
                return item.NumericQuantity is long crystals
                    ? new CurrencyCard
                    {
                        Name = "异方晶",
                        Quantity = crystals.ToString("N0"),
                        SubText = $"≈ {crystals / 180} 抽",
                    }
                    : new CurrencyCard { Name = "异方晶", Quantity = "未识别" };
            }
            return new CurrencyCard
            {
                Name = item.Name,
                Quantity = item.NumericQuantity is long value ? value.ToString("N0") : item.Quantity,
            };
        }).ToList();
        foreach (var name in InventorySnapshot.TicketNames)
        {
            var found = snapshot.Items.FirstOrDefault(item => item.Name == name);
            summaries.Add(new CurrencyCard
            {
                Name = name,
                Quantity = found == null ? "未识别" : (found.NumericQuantity is long n ? (name == "搜索令" ? $"{n} 张" : n.ToString("N0")) : found.Quantity),
            });
        }
        CurrencyCards.ItemsSource = summaries;

        var disCount = snapshot.Currencies.FirstOrDefault(c => c.Name == "狄斯币")?.NumericQuantity;
        var essenceCount = snapshot.Currencies.FirstOrDefault(c => c.Name is "狂厄精粹" or "狂乱精粹")?.NumericQuantity
            ?? snapshot.Items.FirstOrDefault(i => i.Name is "狂厄精粹" or "狂乱精粹")?.NumericQuantity;
        if (disCount is > 0)
        {
            Lv90Banner.IsVisible = true;
            Lv90Text.Text = essenceCount is > 0
                ? $"通用估算：当前精粹约够 {essenceCount.Value / 2_400_000.0:F1} 名、狄斯币约够 {disCount.Value / 2_000_000.0:F1} 名狂级升至 90 级（按常规养成消耗估算，不依赖角色表）"
                : $"通用估算：当前狄斯币约可供 {disCount.Value / 2_000_000.0:F1} 名狂级升至 90 级（按常规养成消耗估算，不依赖角色表）";
        }
        else
        {
            Lv90Banner.IsVisible = false;
        }

        var timestamp = DateTimeOffset.TryParse(snapshot.CapturedAt, out var time)
            ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : snapshot.CapturedAt;
        SnapshotText.Text = $"更新于 {timestamp} · 共 {rows.Count} 项材料与道具";
        UpdateRarityCounts();
        ApplyFilter();
        foreach (var row in oldRows) row.Icon?.Dispose();
    });

    private static Bitmap? LoadIcon(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try { using var stream = File.OpenRead(path); return Bitmap.DecodeToWidth(stream, 64); }
        catch (Exception exception) when (exception is IOException or ArgumentException) { return null; }
    }

    private static string ClassifyRarity(string name)
    {
        if (name.Contains("狂级") || name.Contains("超阶") || name.Contains("三阶") ||
            name.Contains("意识晶核") || name.Contains("沉雾泪晶") || name.Contains("暮辉晶") ||
            name.Contains("曲铜晶") || name.Contains("狂乱"))
            return "狂";
        if (name.Contains("危级") || name.Contains("高阶") || name.Contains("二阶") ||
            name.Contains("异化") || name.Contains("赤晶") || name.Contains("精粹"))
            return "危";
        return "普";
    }

    private void UpdateRarityCounts()
    {
        var source = _comparisonRows ?? _rows;
        RarityAllBtn.Content = $"全部 ({source.Count})";
        RarityGoldBtn.Content = $"狂 ({source.Count(r => r.Rarity == "狂")})";
        RarityPurpleBtn.Content = $"危 ({source.Count(r => r.Rarity == "危")})";
        RarityBlueBtn.Content = $"普 ({source.Count(r => r.Rarity == "普")})";
    }

    private void ClearSnapshot()
    {
        _snapshot = null; _loadedJson = "";
        ClearComparisonRows();
        ComparisonPanel.IsVisible = false;
        CurrencyCards.ItemsSource = null;
        Lv90Banner.IsVisible = false;
        ItemsCards.ItemsSource = null;
        foreach (var row in _rows) row.Icon?.Dispose();
        _rows.Clear();
        SnapshotText.Text = "尚未采集仓库资源";
        UpdateRarityCounts();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (!_ready) return;
        var search = SearchBox.Text?.Trim() ?? "";
        var rows = (_comparisonRows ?? _rows)
            .Where(row => _selectedRarity == "全部" || row.Rarity == _selectedRarity)
            .Where(row => string.IsNullOrEmpty(search) || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(row => _comparisonRows == null || ChangesOnly.IsChecked != true || row.Comparison?.Changed == true)
            .ToList();
        ItemsCards.ItemsSource = rows;
        FilteredCountText.Text = _snapshot == null ? "" : $"显示 {rows.Count} 项";
        EmptyText.IsVisible = rows.Count == 0;
        EmptyText.Text = _snapshot == null ? "暂无记录，连接设备后点击“开始识别”。"
            : _comparisonRows != null && ChangesOnly.IsChecked == true ? "当前筛选下没有可确认的数量变更；无法比较的项目可取消“只看变更”查看。"
            : "没有符合筛选条件的物品。";
    }

    private void OnRarityClick(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        _selectedRarity = tag;
        foreach (var b in new[] { RarityAllBtn, RarityGoldBtn, RarityPurpleBtn, RarityBlueBtn })
            b.Classes.Set("active", (b.Tag as string) == tag);
        ApplyFilter();
    }

    private void ReloadHistorySelector(string? selectFileName = null)
    {
        var entries = InventoryHistory.ListEntries(AppPaths.DataRoot);
        HistorySelector.ItemsSource = entries;
        HistorySelector.SelectedItem = selectFileName == null ? null : entries.FirstOrDefault(e => e.FileName == selectFileName);
    }

    private void OnHistorySelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        RenamePanel.IsVisible = false;
        DeleteConfirmPanel.IsVisible = false;
        _pendingDeleteFileName = null;
        UpdateButtons();
    }

    private async void OnOpenComparison(object? sender, RoutedEventArgs args) => await Guard(() =>
    {
        ReloadHistorySelector();
        ComparisonPanel.IsVisible = true;
        RenamePanel.IsVisible = false;
        DeleteConfirmPanel.IsVisible = false;
        _pendingDeleteFileName = null;
        ClearComparisonRows();
        ComparisonText.Text = HistorySelector.ItemCount == 0 ? "暂无历史快照，请先采集仓库资源。"
            : "选择历史快照后点击“开始对比”。差值为当前数量减去历史数量。";
        UpdateRarityCounts();
        ApplyFilter();
        return Task.CompletedTask;
    });

    private void OnRenameSnapshot(object? sender, RoutedEventArgs args)
    {
        if (HistorySelector.SelectedItem is not InventoryHistory.Entry entry) return;
        DeleteConfirmPanel.IsVisible = false;
        _pendingDeleteFileName = null;
        RenameBox.Text = entry.Label;
        RenamePanel.IsVisible = true;
    }

    private void OnCancelRename(object? sender, RoutedEventArgs args)
    {
        RenamePanel.IsVisible = false;
    }

    private async void OnConfirmRename(object? sender, RoutedEventArgs args) => await Guard(() =>
    {
        if (HistorySelector.SelectedItem is not InventoryHistory.Entry entry) return Task.CompletedTask;
        InventoryHistory.Rename(AppPaths.DataRoot, entry.FileName, RenameBox.Text);
        RenamePanel.IsVisible = false;
        ReloadHistorySelector(entry.FileName);
        SetStatus("已更新历史快照名称。");
        return Task.CompletedTask;
    });

    private void OnDeleteSnapshot(object? sender, RoutedEventArgs args)
    {
        if (HistorySelector.SelectedItem is not InventoryHistory.Entry entry) return;
        RenamePanel.IsVisible = false;
        _pendingDeleteFileName = entry.FileName;
        DeleteConfirmText.Text = $"确定删除“{entry.DisplayText}”吗？删除后无法恢复。";
        DeleteConfirmPanel.IsVisible = true;
    }

    private void OnCancelDelete(object? sender, RoutedEventArgs args)
    {
        DeleteConfirmPanel.IsVisible = false;
        _pendingDeleteFileName = null;
    }

    private async void OnConfirmDelete(object? sender, RoutedEventArgs args) => await Guard(() =>
    {
        var fileName = _pendingDeleteFileName;
        DeleteConfirmPanel.IsVisible = false;
        _pendingDeleteFileName = null;
        if (string.IsNullOrEmpty(fileName)) return Task.CompletedTask;
        InventoryHistory.Delete(AppPaths.DataRoot, fileName);
        if (_comparedFileName == fileName)
        {
            ClearComparisonRows();
            UpdateRarityCounts();
            ApplyFilter();
            ComparisonText.Text = "所对比的历史快照已删除，请重新选择快照。";
        }
        ReloadHistorySelector();
        SetStatus("已删除历史快照。");
        return Task.CompletedTask;
    });

    private async void OnCompare(object? sender, RoutedEventArgs args) => await Guard(async () =>
    {
        var current = _snapshot;
        var generation = _comparisonGeneration;
        if (current == null) return;
        if (HistorySelector.SelectedItem is not InventoryHistory.Entry selected)
        {
            ComparisonText.Text = "请先选择一份历史快照。";
            return;
        }
        var fileName = selected.FileName;
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
        _comparedFileName = fileName;
        _comparisonRows = comparison.Entries.Select(entry => new Row
        {
            Item = entry.Item,
            Rarity = ClassifyRarity(entry.Item.Name),
            Comparison = entry,
            Icon = LoadIcon(entry.Item.ImagePath),
        }).ToList();
        var historyTitle = string.IsNullOrEmpty(selected.Label) ? DisplayTime(history.CapturedAt) : $"{selected.Label} ({DisplayTime(history.CapturedAt)})";
        ComparisonText.Text = $"历史 {historyTitle} → 当前 {DisplayTime(current.CapturedAt)} · "
            + $"确认变更 {comparison.Entries.Count(entry => entry.Changed)} 项，无法比较 {comparison.Entries.Count(entry => !entry.Difference.HasValue)} 项。"
            + comparison.Notice;
        UpdateRarityCounts();
        ApplyFilter();
    });

    private static string DisplayTime(string timestamp) => DateTimeOffset.TryParse(timestamp, out var time)
        ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : timestamp;

    private void ClearComparisonRows()
    {
        _comparisonGeneration++;
        _comparedFileName = null;
        if (_comparisonRows != null)
            foreach (var row in _comparisonRows) row.Icon?.Dispose();
        _comparisonRows = null;
    }

    private void OnCloseComparison(object? sender, RoutedEventArgs args)
    {
        ClearComparisonRows();
        RenamePanel.IsVisible = false;
        DeleteConfirmPanel.IsVisible = false;
        _pendingDeleteFileName = null;
        ComparisonPanel.IsVisible = false;
        ChangesOnly.IsChecked = false;
        UpdateRarityCounts();
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
            if (processor.ViewModel?.CurrentResource != "官服")
                throw new InvalidOperationException("请先在首页选择官服中文资源及设备。");
            var task = new MaaInterface.MaaInterfaceTask {
                Name = "仓库识别", Entry = "仓库识别", Check = true,
                Resource = new List<string> { "官服" }
            };
            if (Running || !processor.TryStartTasks(new List<DragItemViewModel> { new(task) }, ignoreCheckedState: true))
                throw new InvalidOperationException("已有任务正在运行，请结束后再识别仓库。");
            _captureProcessor = processor;
            _captureGeneration = processor.TaskRunGeneration;
            SetStatus("正在使用当前实例识别仓库。结果自动保存，运行日志在监控页；下方仍可查看上次快照。");
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
            SetStatus("已导出全部仓库记录（包含未识别项和冲突，不受当前筛选影响）。");
        });
    }
}
