using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MFAAvalonia.Views.Pages;

public partial class GachaRecordsView : UserControl
{
    public sealed class Category { public string id { get; set; } = ""; public string name { get; set; } = ""; }
    public sealed class Detail { public string Time { get; set; } = ""; public string Pool { get; set; } = ""; public string Type { get; set; } = ""; public string Name { get; set; } = ""; public string Rarity { get; set; } = ""; public string Pulls { get; set; } = ""; }
    public sealed class Summary { public string Name { get; set; } = ""; public int Total { get; set; } public int Gold { get; set; } public string Average { get; set; } = ""; }
    private bool _loading;
    private bool _busy;
    private MaaProcessor? _captureProcessor;
    private long _captureGeneration;
    private bool OwnsCapture => _captureProcessor?.IsTaskRunActive == true && _captureProcessor.TaskRunGeneration == _captureGeneration;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string CategoryId => (CategoryBox.SelectedItem as Category)?.id ?? "default-0";
    private string Pool => PoolBox.SelectedIndex > 0 ? PoolBox.SelectedItem?.ToString() ?? "" : "";
    private bool Running => MaaProcessorManager.Instance.Instances.Any(p => p.IsTaskRunActive);

    public GachaRecordsView()
    {
        InitializeComponent();
        Loaded += async (_, _) => { await Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += async (_, _) =>
        {
            UpdateButtons();
            if (_captureProcessor != null && !OwnsCapture)
            {
                _captureProcessor = null;
                await Refresh();
                StatusText.Text = "采集任务已结束，已刷新当前档案。完整性请结合运行日志确认。";
            }
        };
    }

    private void UpdateButtons()
    {
        CollectButton.IsEnabled = !_busy && !Running && CategoryId != "legacy";
        StopButton.IsEnabled = OwnsCapture;
        CategoryBox.IsEnabled = !_busy && !Running;
        PoolBox.IsEnabled = !_busy;
        ModeBox.IsEnabled = !_busy && !Running;
        ImportButton.IsEnabled = !_busy && !Running;
    }

    private async Task<JObject> Command(string operation, params string[] args)
    {
        var root = AppPaths.DataRoot;
        var python = Path.Combine(root, "agent", "python", "python.exe");
        var script = Path.Combine(root, "agent", "gacha_records.py");
        if (!File.Exists(python) || !File.Exists(script)) throw new InvalidOperationException("缺少抽卡记录组件，请使用已更新的本地测试包。");
        var info = new ProcessStartInfo(python) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        info.Environment["PYTHONUTF8"] = "1";
        foreach (var arg in new[] { script, operation, "--root", root }.Concat(args)) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("无法启动记录组件");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var result = JObject.Parse(await output);
        if (process.ExitCode != 0) throw new IOException(result.Value<string>("error") ?? await errors);
        return result;
    }

    private async Task Guard(Func<Task> operation)
    {
        if (_busy) return;
        _busy = true; UpdateButtons();
        try { await operation(); }
        catch (Exception e) { StatusText.Text = e.Message; LoggerHelper.Error("抽卡记录页面：" + e.Message, e); }
        finally { _busy = false; UpdateButtons(); }
    }

    private Task Refresh() => Guard(async () =>
    {
        var category = CategoryId; var pool = Pool;
        var data = await Command("snapshot", "--category", category, "--pool", pool);
        _loading = true;
        try
        {
            var categories = data["categories"]!.ToObject<List<Category>>()!;
            CategoryBox.ItemsSource = categories;
            CategoryBox.SelectedItem = categories.First(c => c.id == category);
            var pools = new[] { "全部实际卡池" }.Concat(data["pools"]!.Values<string>()).ToList();
            PoolBox.ItemsSource = pools; PoolBox.SelectedIndex = Math.Max(0, pools.IndexOf(pool));
            SummaryText.Text = $"{categories.First(c => c.id == category).name}  ·  {data["total"]} 抽   /   狂 {data["gold"]} 个   /   平均 {data["average"]} 抽一个狂";
            CategoryGrid.ItemsSource = data["summaries"]!.ToObject<List<Summary>>();
            HitsGrid.ItemsSource = data["hits"]!.ToObject<List<Detail>>();
            DetailsGrid.ItemsSource = data["details"]!.ToObject<List<Detail>>();
            if (_captureProcessor == null) StatusText.Text = category == "legacy" ? "旧档案保留为未分类。采集前请选新角色、限定等类型；本次页面中所有记录都会归入选定类型。" : "类型独立保存；请先手动打开对应游戏记录页。不要将多个账号导入同一份档案。";
        }
        finally { _loading = false; }
    });

    private async void OnCategoryChanged(object? sender, SelectionChangedEventArgs e) { if (!_loading) { _loading = true; PoolBox.SelectedIndex = 0; _loading = false; await Refresh(); } }
    private async void OnPoolChanged(object? sender, SelectionChangedEventArgs e) { if (!_loading) await Refresh(); }
    private async void OnRefresh(object? sender, RoutedEventArgs e) => await Refresh();
    private async void OnAddCategory(object? sender, RoutedEventArgs e)
    {
        if (Running) return;
        var added = false;
        await Guard(async () => { await Command("add", "--name", NewCategoryBox.Text ?? ""); NewCategoryBox.Text = ""; added = true; });
        if (added) await Refresh();
    }

    private async void OnCollect(object? sender, RoutedEventArgs e)
    {
        if (Running || CategoryId == "legacy") return;
        await Guard(async () =>
        {
            // Validate category before constructing the per-run immutable archive override.
            await Command("snapshot", "--category", CategoryId);
            var archive = "record/gacha/" + CategoryId;
            var pipeline = new JObject();
            var source = JObject.Parse(await File.ReadAllTextAsync(Path.Combine(AppPaths.DataRoot, "resource", "base", "pipeline", "抽卡记录.json")));
            foreach (var node in new[] { "抽卡记录_开始采集", "抽卡记录汇总" })
                pipeline[node] = new JObject { ["action"] = new JObject { ["param"] = new JObject { ["custom_action_param"] = new JObject { ["archive"] = archive, ["report"] = archive + ".md" } } } };
            foreach (var node in new[] { "抽卡记录_识别追踪类型", "抽卡记录_回首页", "抽卡记录_单页识别" })
            {
                var parameters = (JObject)source[node]!["recognition"]!["param"]!["custom_recognition_param"]!.DeepClone();
                parameters["archive"] = archive;
                parameters["stop_on_known"] = ModeBox.SelectedIndex == 1;
                pipeline[node] = new JObject { ["recognition"] = new JObject { ["param"] = new JObject { ["custom_recognition_param"] = parameters } } };
            }
            var task = new MaaInterface.MaaInterfaceTask { Name = "抽卡记录", Entry = "抽卡记录", Check = true,
                Resource = new List<string> { "官服", "B服" }, PipelineOverride = pipeline.ToObject<Dictionary<string, JToken>>() };
            var processor = MaaProcessorManager.Instance.Current;
            if (processor.ViewModel?.CurrentResource is not ("官服" or "B服")) throw new InvalidOperationException("当前采集仅支持官服或 B服中文界面，请先在首页选择资源和设备。");
            if (Running || !processor.TryStartTasks(new List<DragItemViewModel> { new(task) }, ignoreCheckedState: true))
                throw new InvalidOperationException("已有任务开始运行，请等待结束后再采集。");
            _captureProcessor = processor;
            _captureGeneration = processor.TaskRunGeneration;
            StatusText.Text = $"正在采集到“{(CategoryBox.SelectedItem as Category)?.name}”。类型已锁定；运行日志可在监控页查看。";
        });
    }

    private void OnStop(object? sender, RoutedEventArgs e) { if (OwnsCapture) _captureProcessor!.Stop(MFATask.MFATaskStatus.STOPPED); }
    private async void OnExportExcel(object? sender, RoutedEventArgs e) => await Export("xlsx");
    private async void OnExportMarkdown(object? sender, RoutedEventArgs e) => await Export("md");
    private async Task Export(string extension)
    {
        if (Running) { StatusText.Text = "请等待采集结束后导出，避免导出尚未写完的记录。"; return; }
        var category = CategoryId; var pool = Pool;
        var file = await TopLevel.GetTopLevel(this)!.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
            Title = "导出抽卡记录", SuggestedFileName = "抽卡记录." + extension, DefaultExtension = extension,
            FileTypeChoices = new[] { new FilePickerFileType(extension) { Patterns = new[] { "*." + extension } } }, ShowOverwritePrompt = true });
        if (file == null) return;
        if (Running) { StatusText.Text = "有任务开始运行，请等待结束后再导出。"; return; }
        await Guard(async () => { var result = await Command("export", "--category", category, "--pool", pool, "--path", file.Path.LocalPath); StatusText.Text = $"已导出 {result["count"]} 条记录到 {file.Path.LocalPath}"; });
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (Running) return;
        var category = CategoryId;
        var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = "导入当前类型（请确认同一游戏账号）", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Excel") { Patterns = new[] { "*.xlsx" } } } });
        if (files.Count == 0 || Running) return;
        string? message = null;
        await Guard(async () => { var result = await Command("import", "--category", category, "--path", files[0].Path.LocalPath); message = $"导入完成，新增 {result["count"]} 条。"; });
        if (message != null) { await Refresh(); StatusText.Text = message; }
    }
}
