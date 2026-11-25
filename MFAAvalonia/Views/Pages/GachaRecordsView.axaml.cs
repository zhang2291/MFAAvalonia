using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
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
    private static readonly IBrush GoldBrush = SolidColorBrush.Parse("#AD833E");
    private static readonly IBrush GoldBgBrush = SolidColorBrush.Parse("#24AD833E");
    private static readonly IBrush PurpleBrush = SolidColorBrush.Parse("#7C5CBF");
    private static readonly IBrush PurpleBgBrush = SolidColorBrush.Parse("#247C5CBF");
    private static readonly IBrush BlueBrush = SolidColorBrush.Parse("#4B6FA8");
    private static readonly IBrush BlueBgBrush = SolidColorBrush.Parse("#244B6FA8");
    private static readonly IBrush GreenBrush = SolidColorBrush.Parse("#3F7A5E");
    private static readonly IBrush RedBrush = SolidColorBrush.Parse("#A84944");
    private static readonly IBrush MutedBrush = SolidColorBrush.Parse("#6B7280");
    private static readonly IBrush SlateDotBrush = SolidColorBrush.Parse("#CBD5E1");

    public sealed class NavItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string SubTitle { get; set; } = "";
        public bool HasSubTitle => !string.IsNullOrEmpty(SubTitle);
        public int Total { get; set; }
    }

    public sealed class HitItem
    {
        public string Avatar { get; set; } = "";
        public Bitmap? AvatarBitmap { get; set; }
        public bool HasAvatar => AvatarBitmap != null;
        public bool HasNoAvatar => AvatarBitmap == null;
        public string Name { get; set; } = "";
        public string Meta { get; set; } = "";
        public string Pulls { get; set; } = "";
        public string Note { get; set; } = "";
        public bool HasNote => !string.IsNullOrEmpty(Note);
        public double PullValue { get; set; }
        public bool ShowBar => PullValue > 0;
        public IBrush IntervalBrush { get; set; } = MutedBrush;
        public string UpText { get; set; } = "";
        public bool HasUpBadge => !string.IsNullOrEmpty(UpText);
        public IBrush UpBg => UpText == "UP" ? GoldBgBrush : UpText == "大保底" ? BlueBgBrush : SolidColorBrush.Parse("#22DC2626");
        public IBrush UpFg => UpText == "UP" ? GoldBrush : UpText == "大保底" ? BlueBrush : RedBrush;
    }

    public sealed class PoolSummaryItem
    {
        public string Pool { get; set; } = "";
        public int Total { get; set; }
        public int Gold { get; set; }
        public string SummaryText => $"{Total} 抽 · {Gold} 狂";
    }

    public sealed class DetailItem
    {
        public string Time { get; set; } = "";
        public string Pool { get; set; } = "";
        public string Type { get; set; } = "";
        public string Name { get; set; } = "";
        public string Rarity { get; set; } = "";
        public string Pulls { get; set; } = "";
        public string PullsLabel => string.IsNullOrEmpty(Pulls) ? "" : (Pulls == "—" ? "首狂" : $"第 {Pulls} 抽出狂");
        public IBrush BadgeBg => Rarity == "狂" ? GoldBgBrush : Rarity == "危" ? PurpleBgBrush : BlueBgBrush;
        public IBrush BadgeFg => Rarity == "狂" ? GoldBrush : Rarity == "危" ? PurpleBrush : BlueBrush;
        public FontWeight NameWeight => Rarity == "狂" ? FontWeight.SemiBold : FontWeight.Normal;
        public IBrush PullsBrush => int.TryParse(Pulls, out var n) ? BrushForInterval(n) : MutedBrush;
    }

    public sealed class PullGroupItem
    {
        public string Avatar { get; set; } = "";
        public string HeaderTitle { get; set; } = "";
        public string HeaderMeta { get; set; } = "";
        public IBrush BadgeBg { get; set; } = BlueBgBrush;
        public IBrush BadgeFg { get; set; } = BlueBrush;
        public List<IBrush> Dots { get; set; } = new();
        public List<DetailItem> Rows { get; set; } = new();
    }

    private bool _loading;
    private bool _busy;
    private bool _stopOnKnown = true;
    private string _selectedCategoryId = "all";
    private string _selectedRarity = "全部";
    private List<HitItem> _hitItems = new();
    private List<DetailItem> _allDetails = new();
    private List<string> _skippedLines = new();
    private MaaProcessor? _captureProcessor;
    private long _captureGeneration;
    private DateTimeOffset _captureStartedAt;
    private bool OwnsCapture => _captureProcessor?.IsTaskRunActive == true && _captureProcessor.TaskRunGeneration == _captureGeneration;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string Pool => PoolBox.SelectedIndex > 0 ? PoolBox.SelectedItem?.ToString() ?? "" : "";
    private bool Running => MaaProcessorManager.Instance.Instances.Any(p => p.IsTaskRunActive);

    public GachaRecordsView()
    {
        InitializeComponent();
        Loaded += async (_, _) => { await Refresh(); _timer.Start(); };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            foreach (var h in _hitItems) h.AvatarBitmap?.Dispose();
            _hitItems.Clear();
        };
        _timer.Tick += async (_, _) =>
        {
            UpdateButtons();
            if (OwnsCapture)
            {
                PollLiveProgress();
            }
            else if (_captureProcessor != null)
            {
                _captureProcessor = null;
                var summary = ReadFinalProgressSummary();
                await Refresh();
                SetStatus(summary ?? "采集已结束，已采集的记录自动保留。", showStop: false);
            }
        };
    }

    private static IBrush BrushForInterval(int interval) => interval <= 30 ? GreenBrush : interval <= 55 ? GoldBrush : RedBrush;

    private void SetStatus(string? text, bool showStop = false)
    {
        var visible = !string.IsNullOrWhiteSpace(text) || showStop;
        StatusBanner.IsVisible = visible;
        StatusText.Text = text ?? "";
        StopButton.IsVisible = showStop;
    }

    private void PollLiveProgress()
    {
        var prog = ReadProgressJson();
        if (prog == null) return;
        var page = prog.Value<int?>("page");
        var pages = prog.Value<int?>("pages") ?? 0;
        var added = prog.Value<int?>("new") ?? 0;
        var known = prog.Value<int?>("known") ?? 0;
        var elapsed = prog.Value<double?>("elapsed") ?? 0;
        var speed = pages > 0 ? $" · 平均 {elapsed / pages:F1} s/页" : "";
        var pageText = page.HasValue ? $"第 {page.Value} 页" : "启动中…";
        SetStatus($"采集中 · {pageText} · 新增 +{added} · 已知 {known}{speed}", showStop: true);
    }

    private string? ReadFinalProgressSummary()
    {
        var prog = ReadProgressJson();
        if (prog == null) return null;
        var state = prog.Value<string>("state") ?? "";
        var status = prog.Value<string>("status") ?? "";
        var reason = prog.Value<string>("reason") ?? "";
        var added = prog.Value<int?>("new") ?? 0;
        var pages = prog.Value<int?>("pages") ?? 0;
        var reasonSuffix = string.IsNullOrWhiteSpace(reason) ? "" : $"（{reason}）";
        if (state == "done" && status == "completed")
            return $"采集已完成：本次新增 +{added} 条（共扫描 {pages} 页）。";
        if (status == "partial")
            return $"采集未完整结束{reasonSuffix}：已保留新增 +{added} 条（共扫描 {pages} 页）。";
        if (state == "running" || state == "finishing")
            return $"采集已提前停止：已保留新增 +{added} 条（共扫描 {pages} 页）。";
        return $"采集已结束{reasonSuffix}：本次新增 +{added} 条（共扫描 {pages} 页）。";
    }

    private JObject? ReadProgressJson()
    {
        try
        {
            var path = Path.Combine(AppPaths.DataRoot, "data", "record", "gacha", ".progress.json");
            if (!File.Exists(path)) return null;
            var obj = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            var updated = obj.Value<double?>("updated") ?? 0;
            if (updated < _captureStartedAt.ToUnixTimeSeconds() - 2) return null;
            return obj;
        }
        catch { return null; }
    }

    private void UpdateButtons()
    {
        var canCollect = !_busy && !Running && _selectedCategoryId != "all" && _selectedCategoryId != "legacy";
        CollectButton.IsEnabled = canCollect;
        ReportButton.IsEnabled = !_busy;
        StopButton.IsEnabled = OwnsCapture;
        CategoryList.IsEnabled = !_busy;
        AddCategoryButton.IsEnabled = !_busy && !Running;
        DeleteMenuItem.IsEnabled = !_busy && !Running && _selectedCategoryId != "all" && _selectedCategoryId != "legacy";
        ModeFastButton.IsEnabled = !_busy && !Running;
        ModeFullButton.IsEnabled = !_busy && !Running;
        PoolBox.IsEnabled = !_busy;
        ImportButton.IsEnabled = !_busy && !Running;
    }

    private async Task<JObject> Command(string operation, params string[] args)
    {
        var root = AppPaths.DataRoot;
        var python = Path.Combine(root, "agent", "python", "python.exe");
        var script = Path.Combine(root, "agent", "gacha_records.py");
        if (!File.Exists(python) || !File.Exists(script)) throw new InvalidOperationException("缺少抽卡记录组件，请使用已更新的本地测试包。");
        var info = new ProcessStartInfo(python)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
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
        catch (Exception e) { SetStatus(e.Message); LoggerHelper.Error("抽卡记录页面：" + e.Message, e); }
        finally { _busy = false; UpdateButtons(); }
    }

    private Task Refresh() => Guard(async () =>
    {
        var category = _selectedCategoryId;
        var pool = Pool;
        var data = await Command("snapshot", "--category", category, "--pool", pool);
        category = data.Value<string>("selected_category") ?? category;
        _selectedCategoryId = category;

        _loading = true;
        try
        {
            // 1. 左侧池子类型导航
            var summaries = data["summaries"] as JArray ?? new JArray();
            var navItems = new List<NavItem>
            {
                new() { Id = "all", Name = "全部", SubTitle = "跨池汇总 · 不计保底", Total = data.Value<int?>("all_total") ?? 0 },
            };
            foreach (var s in summaries)
            {
                var id = s.Value<string>("Id") ?? "";
                var name = s.Value<string>("Name") ?? "";
                var gt = s.Value<string>("GameType") ?? "";
                var sub = !string.IsNullOrEmpty(gt) && gt != name ? $"游戏「{gt}」" : "";
                navItems.Add(new NavItem { Id = id, Name = name, SubTitle = sub, Total = s.Value<int?>("Total") ?? 0 });
            }
            CategoryList.ItemsSource = navItems;
            var activeNav = navItems.FirstOrDefault(x => x.Id == category) ?? navItems[0];
            CategoryList.SelectedItem = activeNav;
            CategoryTitle.Text = activeNav.Name;

            // 2. 损坏/跳过行提示条
            _skippedLines = data["skipped"]?.Values<string>().Where(x => x != null).Select(x => x!).ToList() ?? new();
            SkippedBanner.IsVisible = _skippedLines.Count > 0;
            if (_skippedLines.Count > 0)
            {
                SkippedText.Text = $"档案中有 {_skippedLines.Count} 行无法解析，已跳过显示（原文件未修改）";
            }

            // 3. 4 格统计卡
            var isAll = category == "all";
            var total = data.Value<int?>("total") ?? 0;
            var gold = data.Value<int?>("gold") ?? 0;
            TotalText.Text = total.ToString();
            GoldText.Text = gold.ToString();
            RateText.Text = $"出狂率 {data.Value<string>("rate") ?? "—"}";
            AverageText.Text = data.Value<string>("average") ?? "—";

            var median = data.Value<int?>("median");
            var best = data.Value<int?>("best");
            var worst = data.Value<int?>("worst");
            var spreadParts = new List<string>();
            if (median.HasValue) spreadParts.Add($"中位 {median.Value}");
            if (best.HasValue && worst.HasValue && best.Value != worst.Value) spreadParts.Add($"欧 {best.Value} / 非 {worst.Value}");
            SpreadText.Text = spreadParts.Count > 0 ? string.Join(" · ", spreadParts) : "抽 / 狂";

            var pity = data.Value<int?>("current_pity");
            var upWins = data.Value<int?>("up_wins") ?? 0;
            var upLosses = data.Value<int?>("up_losses") ?? 0;
            var upTotal = upWins + upLosses;
            UpRateText.Text = data.Value<string>("up_rate") ?? "—";
            UpRateText.Foreground = upTotal == 0 ? MutedBrush : (upWins * 2 >= upTotal ? GreenBrush : RedBrush);
            var upSummary = data.Value<string>("up_summary");
            UpSubText.Text = !string.IsNullOrEmpty(upSummary) ? upSummary : "常驻 / 未知池跳过";

            var expectedUp = data.Value<double?>("expected_up");
            ExpectedUpText.Text = data.Value<string>("expected_up_text") ?? "—";
            ExpectedUpText.Foreground = expectedUp switch
            {
                <= 55.0 => GreenBrush,
                <= 75.0 => GoldBrush,
                > 75.0 => RedBrush,
                _ => MutedBrush,
            };
            ExpectedUpSubText.Text = data.Value<string>("expected_up_sub") ?? "未判定池不纳入计算";

            if (isAll || !pity.HasValue)
            {
                PityText.Text = "—";
                PityRemainText.Text = "跨池不计保底";
                PityBar.Value = 0;
            }
            else
            {
                PityText.Text = $"{pity.Value} / 80";
                PityRemainText.Text = $"距保底 {Math.Max(0, 80 - pity.Value)} 抽";
                PityBar.Value = Math.Min(80, pity.Value);
                PityBar.Foreground = pity.Value >= 65 ? RedBrush : GoldBrush;
            }

            // 4. 狂级出货列表（含可选头像与自动单字回退 + UP/歪 标签）
            var hitsArr = data["hits"] as JArray ?? new JArray();
            var hitItems = new List<HitItem>();
            var avatarCache = new Dictionary<string, Bitmap?>();
            var oldHits = _hitItems;
            foreach (var h in hitsArr)
            {
                var name = h.Value<string>("Name") ?? "";
                var hPool = h.Value<string>("Pool") ?? "";
                var time = h.Value<string>("Time") ?? "";
                var pullCount = h.Value<int?>("PullCount");
                var pullsText = h.Value<string>("Pulls") ?? "—";
                var note = h.Value<string>("Note") ?? "";
                var upStatus = h.Value<string>("UpStatus") ?? "";
                if (!avatarCache.TryGetValue(name, out var avaBmp))
                {
                    avaBmp = LoadSinnerAvatar(name);
                    avatarCache[name] = avaBmp;
                }
                else if (avaBmp != null)
                {
                    avaBmp = LoadSinnerAvatar(name);
                }
                hitItems.Add(new HitItem
                {
                    Avatar = string.IsNullOrEmpty(name) ? "?" : name[..1],
                    AvatarBitmap = avaBmp,
                    Name = name,
                    Meta = $"{hPool} · {(time.Length >= 16 ? time[..16] : time)}",
                    Pulls = pullsText,
                    Note = note == "首狂" ? "首狂·垫抽未知" : note,
                    PullValue = pullCount ?? 0,
                    IntervalBrush = pullCount.HasValue ? BrushForInterval(pullCount.Value) : MutedBrush,
                    UpText = upStatus,
                });
            }
            _hitItems = hitItems;
            HitsList.ItemsSource = hitItems;
            HitsCountText.Text = $"共 {hitItems.Count}";
            NoHitsText.IsVisible = hitItems.Count == 0;
            foreach (var old in oldHits) old.AvatarBitmap?.Dispose();

            // 5. 各卡池小计
            var poolSums = data["pool_summaries"]?.ToObject<List<PoolSummaryItem>>() ?? new();
            PoolSummariesList.ItemsSource = poolSums;

            // 6. 卡池筛选与完整明细
            var pools = new[] { "全部卡池" }.Concat(data["pools"]?.Values<string>().Where(x => x != null).Select(x => x!) ?? Array.Empty<string>()).ToList();
            PoolBox.ItemsSource = pools;
            PoolBox.SelectedIndex = Math.Max(0, pools.IndexOf(pool));

            _allDetails = data["details"]?.ToObject<List<DetailItem>>() ?? new();
            if (_allDetails.Count > 0)
            {
                var oldest = _allDetails[^1].Time;
                var newest = _allDetails[0].Time;
                DateRangeText.Text = $"{(oldest.Length >= 10 ? oldest[..10] : oldest)} 至 {(newest.Length >= 10 ? newest[..10] : newest)}";
            }
            else
            {
                DateRangeText.Text = "";
            }
            ApplyDetailFilters();

            if (_captureProcessor == null)
            {
                SetStatus(category == "legacy" ? "旧档案保留为未分类。采集前请先在左侧选择「新角色」「常驻」等具体类型。" : null, showStop: false);
            }
        }
        finally { _loading = false; }
    });

    private void ApplyDetailFilters()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var filtered = _allDetails.Where(d =>
            (_selectedRarity == "全部" || d.Rarity == _selectedRarity) &&
            (string.IsNullOrEmpty(query) ||
             d.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             d.Pool.Contains(query, StringComparison.OrdinalIgnoreCase))
        ).ToList();

        var groupTen = GroupTenCheck.IsChecked == true;
        GroupedDetailsList.IsVisible = groupTen;
        DetailsGrid.IsVisible = !groupTen;

        if (groupTen)
        {
            var groups = BuildPullGroups(filtered);
            GroupedDetailsList.ItemsSource = groups;
            FilteredCountText.Text = $"{groups.Count} 组（共 {filtered.Count} 条）";
        }
        else
        {
            DetailsGrid.ItemsSource = filtered;
            FilteredCountText.Text = $"{filtered.Count} 条记录";
        }
    }

    private static List<PullGroupItem> BuildPullGroups(List<DetailItem> records)
    {
        var rawGroups = new List<List<DetailItem>>();
        foreach (var r in records)
        {
            if (rawGroups.Count > 0)
            {
                var last = rawGroups[^1];
                if (last[0].Time == r.Time && last[0].Pool == r.Pool && last.Count < 10)
                {
                    last.Add(r);
                    continue;
                }
            }
            rawGroups.Add(new List<DetailItem> { r });
        }

        var result = new List<PullGroupItem>(rawGroups.Count);
        foreach (var list in rawGroups)
        {
            var goldRows = list.Where(x => x.Rarity == "狂").ToList();
            var purpleCount = list.Count(x => x.Rarity == "危");
            var best = goldRows.FirstOrDefault() ?? list.FirstOrDefault(x => x.Rarity == "危") ?? list[0];
            var title = list.Count == 1
                ? $"单抽 · {best.Name}"
                : goldRows.Count > 0
                    ? $"{(list.Count == 10 ? "十连" : $"{list.Count}连")} · 出 {string.Join("、", goldRows.Select(x => x.Name))}"
                    : purpleCount > 0
                        ? $"{(list.Count == 10 ? "十连" : $"{list.Count}连")} · {purpleCount} 危"
                        : $"{(list.Count == 10 ? "十连" : $"{list.Count}连")} · 全普";
            var orderedRows = list.OrderBy(x => x.Rarity == "狂" ? 0 : x.Rarity == "危" ? 1 : 2).ToList();
            result.Add(new PullGroupItem
            {
                Avatar = string.IsNullOrEmpty(best.Name) ? "?" : best.Name[..1],
                HeaderTitle = title,
                HeaderMeta = $"{best.Pool} · {best.Time}",
                BadgeBg = best.BadgeBg,
                BadgeFg = best.BadgeFg,
                Dots = list.Select(x => x.Rarity == "狂" ? GoldBrush : x.Rarity == "危" ? PurpleBrush : SlateDotBrush).ToList(),
                Rows = orderedRows,
            });
        }
        return result;
    }

    private async void OnCategoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || CategoryList.SelectedItem is not NavItem nav) return;
        _selectedCategoryId = nav.Id;
        _loading = true; PoolBox.SelectedIndex = 0; _loading = false;
        await Refresh();
    }

    private async void OnPoolChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_loading) await Refresh();
    }

    private void OnFilterChanged(object? sender, RoutedEventArgs e)
    {
        if (!_loading) ApplyDetailFilters();
    }

    private void OnRarityClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        _selectedRarity = tag;
        foreach (var b in new[] { RarityAllBtn, RarityGoldBtn, RarityPurpleBtn, RarityBlueBtn })
        {
            b.Classes.Set("active", (b.Tag as string) == tag);
        }
        ApplyDetailFilters();
    }

    private void OnModeFast(object? sender, RoutedEventArgs e)
    {
        _stopOnKnown = true;
        ModeFastButton.Classes.Set("active", true);
        ModeFullButton.Classes.Set("active", false);
    }

    private void OnModeFull(object? sender, RoutedEventArgs e)
    {
        _stopOnKnown = false;
        ModeFastButton.Classes.Set("active", false);
        ModeFullButton.Classes.Set("active", true);
    }

    private async void OnViewSkipped(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || _skippedLines.Count == 0) return;
        var body = string.Join("\n", _skippedLines.Take(50)) + (_skippedLines.Count > 50 ? $"\n……其余 {_skippedLines.Count - 50} 行已省略" : "");
        var dlg = new Window
        {
            Title = $"无法解析的记录行 ({_skippedLines.Count})",
            Width = 520,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Grid
            {
                Margin = new Avalonia.Thickness(16),
                RowDefinitions = RowDefinitions.Parse("Auto,*,Auto"),
                RowSpacing = 10,
                Children =
                {
                    new TextBlock { Text = "以下行在读取时未通过格式校验，已跳过显示（原文件未被修改）：", FontSize = 12, Opacity = 0.75 },
                    new ScrollViewer { [Grid.RowProperty] = 1, Content = new SelectableTextBlock { Text = body, FontSize = 12, TextWrapping = TextWrapping.Wrap } },
                    new Button { [Grid.RowProperty] = 2, Content = "知道了", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right },
                },
            },
        };
        if (dlg.Content is Grid g && g.Children[2] is Button ok) ok.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(owner);
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e) => await Refresh();

    private async void OnAddCategory(object? sender, RoutedEventArgs e)
    {
        if (Running || _busy || TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = GachaCategoryDialogs.Add(async name =>
        {
            if (Running) throw new InvalidOperationException("有任务正在运行，请结束后再添加类型。");
            await Command("add", "--name", name);
        });
        var added = await dialog.ShowDialog<string?>(owner);
        if (added != null) await Refresh();
    }

    private async void OnDeleteCategory(object? sender, RoutedEventArgs e)
    {
        if (Running || _busy || _selectedCategoryId is "all" or "legacy") return;
        var category = _selectedCategoryId;
        var name = (CategoryList.SelectedItem as NavItem)?.Name ?? category;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = GachaCategoryDialogs.Delete(name);
        var choice = await dialog.ShowDialog<string?>(owner);
        if (choice is not ("backup" or "permanent") || Running) return;
        JObject? result = null;
        await Guard(async () =>
        {
            result = choice == "permanent"
                ? await Command("delete", "--category", category, "--permanent")
                : await Command("delete", "--category", category);
        });
        if (result != null)
        {
            _selectedCategoryId = "default-0";
            _loading = true; PoolBox.SelectedIndex = 0; _loading = false;
            await Refresh();
            SetStatus(choice == "permanent" ? "该类型的当前记录已永久删除。" : "类型已删除，原记录已备份：" + result.Value<string>("backup"));
        }
    }

    private async void OnCollect(object? sender, RoutedEventArgs e)
    {
        if (Running || _selectedCategoryId is "all" or "legacy") return;
        await Guard(async () =>
        {
            await Command("snapshot", "--category", _selectedCategoryId);
            var archive = "record/gacha/" + _selectedCategoryId;
            var pipeline = new JObject();
            var source = JObject.Parse(await File.ReadAllTextAsync(Path.Combine(AppPaths.DataRoot, "resource", "base", "pipeline", "抽卡记录.json")));
            foreach (var node in new[] { "抽卡记录_开始采集", "抽卡记录汇总" })
                pipeline[node] = new JObject { ["action"] = new JObject { ["param"] = new JObject { ["custom_action_param"] = new JObject { ["archive"] = archive, ["report"] = archive + ".md" } } } };
            foreach (var node in new[] { "抽卡记录_识别追踪类型", "抽卡记录_回首页", "抽卡记录_单页识别" })
            {
                var parameters = (JObject)source[node]!["recognition"]!["param"]!["custom_recognition_param"]!.DeepClone();
                parameters["archive"] = archive;
                parameters["stop_on_known"] = _stopOnKnown;
                pipeline[node] = new JObject { ["recognition"] = new JObject { ["param"] = new JObject { ["custom_recognition_param"] = parameters } } };
            }
            var task = new MaaInterface.MaaInterfaceTask
            {
                Name = "抽卡记录",
                Entry = "抽卡记录",
                Check = true,
                Resource = new List<string> { "官服" },
                PipelineOverride = pipeline.ToObject<Dictionary<string, JToken>>(),
            };
            var processor = MaaProcessorManager.Instance.Current;
            if (processor.ViewModel?.CurrentResource != "官服") throw new InvalidOperationException("当前采集仅支持官服中文界面，请先在首页选择资源和设备。");
            if (Running || !processor.TryStartTasks(new List<DragItemViewModel> { new(task) }, ignoreCheckedState: true))
                throw new InvalidOperationException("已有任务开始运行，请等待结束后再采集。");
            _captureProcessor = processor;
            _captureGeneration = processor.TaskRunGeneration;
            _captureStartedAt = DateTimeOffset.UtcNow;
            SetStatus($"正在采集到「{(CategoryList.SelectedItem as NavItem)?.Name}」…", showStop: true);
        });
    }

    private void OnStop(object? sender, RoutedEventArgs e)
    {
        if (OwnsCapture) _captureProcessor!.Stop(MFATask.MFATaskStatus.STOPPED);
    }

    private async void OnExportExcel(object? sender, RoutedEventArgs e) => await Export("xlsx");
    private async void OnExportMarkdown(object? sender, RoutedEventArgs e) => await Export("md");

    private async Task Export(string extension)
    {
        if (Running) { SetStatus("请等待采集结束后导出，避免导出尚未写完的记录。"); return; }
        var file = await TopLevel.GetTopLevel(this)!.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出全部抽卡记录",
            SuggestedFileName = "全部抽卡记录." + extension,
            DefaultExtension = extension,
            FileTypeChoices = new[] { new FilePickerFileType(extension) { Patterns = new[] { "*." + extension } } },
            ShowOverwritePrompt = true,
        });
        if (file == null) return;
        if (Running) { SetStatus("有任务开始运行，请等待结束后再导出。"); return; }
        await Guard(async () =>
        {
            var result = await Command("export-all", "--path", file.Path.LocalPath);
            SetStatus($"已导出 {result["count"]} 条记录到 {file.Path.LocalPath}");
        });
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (Running) return;
        var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入全部类型记录（请确认同一游戏账号）",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Excel") { Patterns = new[] { "*.xlsx" } } },
        });
        if (files.Count == 0 || Running) return;
        string? message = null;
        await Guard(async () =>
        {
            var result = await Command("import-all", "--path", files[0].Path.LocalPath);
            message = $"已按类型合并，新增 {result["count"]} 条记录。";
        });
        if (message != null) { await Refresh(); SetStatus(message); }
    }

    private static Bitmap? LoadSinnerAvatar(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var root = Path.GetFullPath(Path.Combine(AppPaths.DataRoot, "resource", "base", "image", "avatars"));
        if (!Directory.Exists(root)) return null;
        foreach (var ext in new[] { "webp", "png", "jpg", "jpeg" })
        {
            var candidate = Path.GetFullPath(Path.Combine(root, $"{name}.{ext}"));
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(candidate)) continue;
            try
            {
                using var stream = File.OpenRead(candidate);
                return Bitmap.DecodeToWidth(stream, 96);
            }
            catch { return null; }
        }
        return null;
    }

    private async void OnOpenReport(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var darkPoster = true;
        var hostBorder = new Border();

        Border BuildPoster(bool dark)
        {
            var bg = SolidColorBrush.Parse(dark ? "#0B0C10" : "#F4F4F6");
            var cardBg = SolidColorBrush.Parse(dark ? "#16171E" : "#FFFFFF");
            var bd = SolidColorBrush.Parse(dark ? "#2A2C36" : "#DCDCE3");
            var fg = SolidColorBrush.Parse(dark ? "#F4F4F5" : "#141418");
            var sub = SolidColorBrush.Parse(dark ? "#91919B" : "#63636B");
            var gold = SolidColorBrush.Parse(dark ? "#E5B769" : "#A67628");
            var goldBg = SolidColorBrush.Parse(dark ? "#33E5B769" : "#20AD833E");
            var green = SolidColorBrush.Parse(dark ? "#4ADE80" : "#059669");

            var kpiGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,*,*,*"), ColumnSpacing = 10 };
            var kpis = new[]
            {
                ("总抽数", TotalText.Text ?? "0", fg),
                ("狂总数", GoldText.Text ?? "0", gold),
                ("平均出狂", AverageText.Text ?? "—", fg),
                ("小保底不歪率", UpRateText.Text ?? "—", UpRateText.Text == "—" ? sub : green),
            };
            for (var i = 0; i < kpis.Length; i++)
            {
                var (label, value, color) = kpis[i];
                var box = new Border
                {
                    [Grid.ColumnProperty] = i,
                    Background = cardBg,
                    BorderBrush = bd,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(12, 10),
                    Child = new StackPanel
                    {
                        Spacing = 3,
                        Children =
                        {
                            new TextBlock { Text = label, FontSize = 11, Foreground = sub },
                            new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = color },
                        },
                    },
                };
                kpiGrid.Children.Add(box);
            }

            var wall = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var h in _hitItems)
            {
                Control avatarContent = h.AvatarBitmap != null
                    ? new Image { Source = h.AvatarBitmap, Width = 44, Height = 44, Stretch = Stretch.UniformToFill }
                    : new TextBlock { Text = h.Avatar, Foreground = gold, FontWeight = FontWeight.Bold, FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var pillText = string.IsNullOrEmpty(h.UpText) ? h.Pulls : $"{h.Pulls} · {h.UpText}";
                var cell = new Border
                {
                    Width = 104,
                    Margin = new Thickness(0, 0, 8, 8),
                    Padding = new Thickness(8, 10),
                    Background = cardBg,
                    BorderBrush = bd,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Child = new StackPanel
                    {
                        Spacing = 5,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Children =
                        {
                            new Border
                            {
                                Width = 44, Height = 44, CornerRadius = new CornerRadius(10),
                                Background = goldBg, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center,
                                Child = avatarContent,
                            },
                            new TextBlock { Text = h.Name, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = fg, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                            new TextBlock { Text = pillText, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = h.IntervalBrush, TextAlignment = TextAlignment.Center },
                        },
                    },
                };
                wall.Children.Add(cell);
            }

            return new Border
            {
                Width = 480,
                Background = bg,
                Padding = new Thickness(20),
                CornerRadius = new CornerRadius(14),
                Child = new StackPanel
                {
                    Spacing = 14,
                    Children =
                    {
                        new Grid
                        {
                            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                            Children =
                            {
                                new StackPanel
                                {
                                    Spacing = 2,
                                    Children =
                                    {
                                        new TextBlock { Text = "MBCCTOOLS · ARREST REPORT", FontSize = 10, Foreground = sub, LetterSpacing = 1 },
                                        new TextBlock { Text = $"{CategoryTitle.Text} · 抽卡战报", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = fg },
                                    },
                                },
                                new TextBlock { [Grid.ColumnProperty] = 1, Text = DateRangeText.Text ?? "", FontSize = 11, Foreground = sub, VerticalAlignment = VerticalAlignment.Bottom },
                            },
                        },
                        kpiGrid,
                        new TextBlock { Text = $"狂级出货档案（共 {_hitItems.Count} 名）", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = sub },
                        _hitItems.Count > 0 ? wall : new TextBlock { Text = "暂无狂级记录", FontSize = 12, Foreground = sub, Margin = new Thickness(0, 12) },
                        new Border
                        {
                            BorderBrush = bd, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0),
                            Child = new Grid
                            {
                                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                                Children =
                                {
                                    new TextBlock { Text = "由 MBCCtools 本地离线生成", FontSize = 11, Foreground = sub },
                                    new TextBlock { [Grid.ColumnProperty] = 1, Text = $"当前已垫 {PityText.Text}", FontSize = 11, Foreground = sub },
                                },
                            },
                        },
                    },
                },
            };
        }

        hostBorder.Child = BuildPoster(darkPoster);
        var toggleBtn = new Button { Content = "切换浅色海报", FontSize = 12, Padding = new Thickness(12, 5) };
        toggleBtn.Click += (_, _) =>
        {
            darkPoster = !darkPoster;
            toggleBtn.Content = darkPoster ? "切换浅色海报" : "切换深色海报";
            hostBorder.Child = BuildPoster(darkPoster);
        };
        var saveBtn = new Button { Content = "保存长图 PNG…", FontSize = 12, Padding = new Thickness(14, 5) };
        var closeBtn = new Button { Content = "关闭", FontSize = 12, Padding = new Thickness(12, 5) };
        var dlg = new Window
        {
            Title = "抽卡战报长图预览",
            Width = 540,
            Height = 680,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Grid
            {
                Margin = new Thickness(16),
                RowDefinitions = RowDefinitions.Parse("Auto,*,Auto"),
                RowSpacing = 12,
                Children =
                {
                    new Grid
                    {
                        ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                        Children =
                        {
                            new TextBlock { Text = "抽卡战报长图预览", FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                            toggleBtn,
                        },
                    },
                    new ScrollViewer { [Grid.RowProperty] = 1, Content = hostBorder, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
                    new StackPanel
                    {
                        [Grid.RowProperty] = 2,
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { closeBtn, saveBtn },
                    },
                },
            },
        };
        Grid.SetColumn(toggleBtn, 1);
        closeBtn.Click += (_, _) => dlg.Close();
        saveBtn.Click += async (_, _) =>
        {
            if (hostBorder.Child is not Control poster) return;
            var file = await dlg.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存抽卡战报长图",
                SuggestedFileName = $"{CategoryTitle.Text}-抽卡战报-{DateTime.Now:yyyyMMdd}.png",
                DefaultExtension = "png",
                FileTypeChoices = new[] { new FilePickerFileType("PNG 图片") { Patterns = new[] { "*.png" } } },
                ShowOverwritePrompt = true,
            });
            if (file == null) return;
            var bounds = poster.Bounds;
            if (bounds.Width < 1 || bounds.Height < 1) return;
            const double scale = 2.0;
            var px = new PixelSize((int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale));
            using var rtb = new RenderTargetBitmap(px, new Vector(96 * scale, 96 * scale));
            rtb.Render(poster);
            await using var stream = await file.OpenWriteAsync();
            rtb.Save(stream);
            SetStatus($"战报长图已保存到 {file.Path.LocalPath}");
            dlg.Close();
        };
        await dlg.ShowDialog(owner);
    }
}
