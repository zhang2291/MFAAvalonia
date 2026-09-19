using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using MFAAvalonia.Helper;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http.Headers;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace MFAAvalonia.Controls;

internal sealed class MpePendingExport : EventArgs
{
    internal MpePendingExport(string temporaryPath, string suggestedName)
    {
        TemporaryPath = temporaryPath;
        SuggestedName = suggestedName;
    }

    internal string TemporaryPath { get; }
    internal string SuggestedName { get; }
}

internal sealed class MpeWebViewHost : NativeControlHost
{
    private const string BundledMpeVersion = "1.10.0";

    private readonly string _projectRoot;
    private readonly string _webRoot;
    private readonly string _url;
    private CoreWebView2Controller? _controller;
    private nint _hostHwnd;
    private bool _destroyed;
    private int _generation;

    internal event EventHandler? NavigationCompleted;
    internal event EventHandler<string>? InitializationFailed;
    internal event EventHandler<MpePendingExport>? PipelineExportReady;

    internal MpeWebViewHost(string projectRoot, string webRoot, string url)
    {
        _projectRoot = projectRoot;
        _webRoot = webRoot;
        _url = url;
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(UpdateControllerBounds);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
            return base.CreateNativeControlCore(parent);

        // SukiSideMenu 切换页面时会把 NativeControlHost 从视觉树中移除，
        // Avalonia 因而会调用 DestroyNativeControlCore。再次切回同一页面时，
        // 同一个控件实例会重新 Create；这里必须允许 WebView2 Controller 重建。
        _destroyed = false;
        var generation = Interlocked.Increment(ref _generation);
        var placeholder = base.CreateNativeControlCore(parent);
        _hostHwnd = placeholder.Handle;
        LoggerHelper.Debug($"MaaPipelineEditor WebView2 Host 已创建：hwnd={_hostHwnd}, generation={generation}");
        _ = InitializeWebViewAsync(_hostHwnd, generation);
        return placeholder;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Shutdown();
        base.DestroyNativeControlCore(control);
    }

    internal void Shutdown()
    {
        _destroyed = true;
        Interlocked.Increment(ref _generation);
        try
        {
            _controller?.Close();
        }
        catch
        {
            // Native host teardown must not block application shutdown.
        }

        _controller = null;
        _hostHwnd = 0;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
            Dispatcher.UIThread.Post(UpdateControllerBounds);
    }

    private async Task InitializeWebViewAsync(nint parentHwnd, int generation)
    {
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MFAAvalonia",
                "WebView2");
            Directory.CreateDirectory(userData);

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            if (_destroyed || generation != _generation || parentHwnd != _hostHwnd)
                return;

            var controller = await environment.CreateCoreWebView2ControllerAsync(parentHwnd);
            if (_destroyed || generation != _generation || parentHwnd != _hostHwnd)
            {
                controller.Close();
                return;
            }

            _controller = controller;
            Configure(controller.CoreWebView2, generation);
            await InstallPrivateModeBootstrapAsync(controller.CoreWebView2, generation);
            if (_destroyed || generation != _generation || parentHwnd != _hostHwnd)
            {
                controller.Close();
                _controller = null;
                return;
            }

            UpdateControllerBounds();
            _controller.IsVisible = true;
                        _controller.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                try
                {
                    await _controller.CoreWebView2.ExecuteScriptAsync("""
(() => {
  try {
    const c = JSON.parse(localStorage.getItem('mpe_last_controller') || 'null');
    if (c && c.type === 'adb') {
      localStorage.setItem('_mpe_config', JSON.stringify({...JSON.parse(localStorage.getItem('_mpe_config') || '{}'), autoConnectLastController: true}));
      console.info('[MFA] Triggering MPE auto controller connect');
      setTimeout(() => window.dispatchEvent(new Event('mpe:auto-connect')), 300);
    }
  } catch(e) { console.warn(e); }
})();
""");
                }
                catch { }
            };
            _controller.CoreWebView2.Navigate(_url);
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"MaaPipelineEditor WebView2 初始化失败：{ex.Message}", ex);
            Dispatcher.UIThread.Post(() => InitializationFailed?.Invoke(this, ex.Message));
        }
    }

    private async Task InstallPrivateModeBootstrapAsync(CoreWebView2 webView, int generation)
    {
        if (_destroyed || generation != _generation)
            return;

        var script = """
(() => {
  try {
    localStorage.setItem('mpe_newcomer_passed', 'true');
    localStorage.setItem('mpe_last_version', '1.10.0');
    localStorage.setItem('_mpe_stared', 'true');
    localStorage.setItem('mpe_debug_configuration_source_v1', 'project_interface');
    const cfg = JSON.parse(localStorage.getItem('_mpe_config') || '{}');
    // Keep MPE's own controller state machine enabled. The previous private mode override disabled autoConnect,
    // which prevented FlowScope from receiving controller_created and left the device state disconnected.
    cfg.autoConnectLastController = true;
    const seededController = __MFA_LAST_CONTROLLER__;
    if (seededController) {
      localStorage.setItem('mpe_last_controller', JSON.stringify(seededController));

      const NativeWebSocket = window.WebSocket;
      if (NativeWebSocket && !NativeWebSocket.prototype.__mfaControllerSendPatched) {
        const nativeSend = NativeWebSocket.prototype.send;
        Object.defineProperty(NativeWebSocket.prototype, '__mfaControllerSendPatched', {
          value: true,
          configurable: true
        });
        NativeWebSocket.prototype.send = function(data) {
          try {
            const outgoing = typeof data === 'string' ? JSON.parse(data) : null;
            if (outgoing?.path === '/system/handshake' && !this.__mfaControllerHooked) {
              this.__mfaControllerHooked = true;
              this.__mfaControllerRequested = false;
              this.addEventListener('message', event => {
                try {
                  const message = typeof event.data === 'string' ? JSON.parse(event.data) : null;
                  if (message?.path !== '/system/handshake/response'
                      || !message?.data?.success
                      || this.__mfaControllerRequested) return;

                  this.__mfaControllerRequested = true;
                  nativeSend.call(this, JSON.stringify({
                    path: '/etl/mfw/create_adb_controller',
                    data: seededController.params
                  }));
                  console.info('[MFA] Requested MPE controller connection', seededController.params.address);
                } catch (error) {
                  console.warn('[MFA] Failed to auto-connect MPE controller', error);
                }
              });
            }
          } catch (error) {
            console.warn('[MFA] Failed to inspect MPE WebSocket message', error);
          }
          return nativeSend.call(this, data);
        };
      }
    }
    cfg.enableLiveScreen = false;
    cfg.configHandlingMode = 'none';
    localStorage.setItem('_mpe_config', JSON.stringify(cfg));
  } catch (error) {
    console.warn('[MFA] Failed to initialize MaaPipelineEditor private mode state', error);
  }
})();
""";
        script = script.Replace("__MFA_LAST_CONTROLLER__", BuildSeededControllerJson(), StringComparison.Ordinal);

        await webView.AddScriptToExecuteOnDocumentCreatedAsync(script);
        LoggerHelper.Info($"MaaPipelineEditor private mode bootstrap installed for v{BundledMpeVersion}.");
    }

    private string BuildSeededControllerJson()
    {
        try
        {
            var instancePath = Path.Combine(_projectRoot, "config", "instances", "default.json");
            if (!File.Exists(instancePath))
                return "null";

            using var document = JsonDocument.Parse(File.ReadAllText(instancePath));
            if (!document.RootElement.TryGetProperty("AdbDevice", out var device)
                || device.ValueKind != JsonValueKind.Object)
                return "null";

            var name = device.TryGetProperty("Name", out var nameElement) ? nameElement.GetString() : null;
            var adbPath = device.TryGetProperty("AdbPath", out var pathElement) ? pathElement.GetString() : null;
            var address = device.TryGetProperty("AdbSerial", out var serialElement) ? serialElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(adbPath) || string.IsNullOrWhiteSpace(address))
                return "null";

            var screencapMask = device.TryGetProperty("ScreencapMethods", out var screencapElement)
                ? screencapElement.GetUInt64()
                : 0UL;
            var inputMask = device.TryGetProperty("InputMethods", out var inputElement)
                ? inputElement.GetUInt64()
                : 0UL;
            var screencapMethods = ConvertAdbScreencapMethods(screencapMask);
            var inputMethods = ConvertAdbInputMethods(inputMask);
            var config = device.TryGetProperty("Config", out var configElement) ? configElement.GetString() : "";

            return JsonSerializer.Serialize(new
            {
                type = "adb",
                @params = new
                {
                    adb_path = adbPath,
                    address,
                    name,
                    screencap_methods = screencapMethods,
                    input_methods = inputMethods,
                    config
                },
                deviceInfo = new
                {
                    adb_path = adbPath,
                    address,
                    name,
                    screencap_methods = screencapMethods,
                    input_methods = inputMethods,
                    config
                }
            });
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"读取 MBCCtools 已选设备用于 MPE 自动连接失败：{ex.Message}");
            return "null";
        }
    }

    private static string[] ConvertAdbScreencapMethods(ulong methods)
    {
        var result = new List<string>();
        AddFlag(result, methods, 1UL, "EncodeToFileAndPull");
        AddFlag(result, methods, 2UL, "Encode");
        AddFlag(result, methods, 4UL, "RawWithGzip");
        AddFlag(result, methods, 8UL, "RawByNetcat");
        AddFlag(result, methods, 16UL, "MinicapDirect");
        AddFlag(result, methods, 32UL, "MinicapStream");
        AddFlag(result, methods, 64UL, "EmulatorExtras");

        return result.Count > 0
            ? result.ToArray()
            : ["EncodeToFileAndPull", "Encode", "RawWithGzip", "MinicapDirect", "MinicapStream", "EmulatorExtras"];
    }

    private static string[] ConvertAdbInputMethods(ulong methods)
    {
        var result = new List<string>();
        AddFlag(result, methods, 1UL, "AdbShell");
        AddFlag(result, methods, 2UL, "MinitouchAndAdbKey");
        AddFlag(result, methods, 4UL, "Maatouch");
        AddFlag(result, methods, 8UL, "EmulatorExtras");

        return result.Count > 0
            ? result.ToArray()
            : ["AdbShell", "MinitouchAndAdbKey", "Maatouch", "EmulatorExtras"];
    }

    private static void AddFlag(List<string> result, ulong methods, ulong flag, string name)
    {
        if ((methods & flag) != 0)
            result.Add(name);
    }

    private void Configure(CoreWebView2 webView, int generation)
    {
        webView.SetVirtualHostNameToFolderMapping(
            "mpe.codax.site",
            _webRoot,
            CoreWebView2HostResourceAccessKind.Allow);

        webView.Settings.AreDevToolsEnabled = false;
        webView.Settings.AreDefaultContextMenusEnabled = true;
        webView.Settings.IsStatusBarEnabled = false;
        webView.Settings.IsZoomControlEnabled = true;
        webView.NavigationStarting += (_, e) =>
        {
            if (IsEditorNavigation(e.Uri))
                return;

            e.Cancel = true;
            OpenExternal(e.Uri);
        };
        webView.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        webView.DownloadStarting += (_, e) => HandleDownloadStarting(e);
        webView.NavigationCompleted += async (_, e) =>
        {
            if (!e.IsSuccess)
            {
                Dispatcher.UIThread.Post(() =>
                    InitializationFailed?.Invoke(this, $"页面加载失败：{e.WebErrorStatus}"));
                return;
            }

            await VerifyEditorMountedAsync(webView, generation);
        };
        webView.ProcessFailed += (_, e) =>
            Dispatcher.UIThread.Post(() =>
                InitializationFailed?.Invoke(this, $"WebView2 进程异常：{e.ProcessFailedKind}"));
    }

    private void HandleDownloadStarting(CoreWebView2DownloadStartingEventArgs e)
    {
        var suggestedName = GetOriginalDownloadFileName(e.DownloadOperation.ContentDisposition)
            ?? Path.GetFileName(e.ResultFilePath);
        if (string.IsNullOrWhiteSpace(suggestedName))
            suggestedName = "pipeline.json";

        var extension = Path.GetExtension(suggestedName);
        if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".jsonc", StringComparison.OrdinalIgnoreCase))
            return;

        var invalidChars = Path.GetInvalidFileNameChars();
        var safeName = string.Concat(suggestedName.Select(ch => invalidChars.Contains(ch) ? '_' : ch));
        if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(safeName)))
            safeName = "pipeline" + extension;

        var tempDirectory = Path.Combine(Path.GetTempPath(), "MFAAvalonia", "MpeExport");
        Directory.CreateDirectory(tempDirectory);
        var temporaryPath = Path.Combine(
            tempDirectory,
            $"{Guid.NewGuid():N}-{safeName}");

        e.ResultFilePath = temporaryPath;
        e.Handled = true;
        var operation = e.DownloadOperation;

        void OnStateChanged(object? _, object? __)
        {
            if (operation.State == CoreWebView2DownloadState.InProgress)
                return;

            operation.StateChanged -= OnStateChanged;
            try
            {
                if (operation.State == CoreWebView2DownloadState.Completed && File.Exists(temporaryPath))
                {
                    LoggerHelper.Info($"MaaPipelineEditor 导出内容已准备完成，等待选择 MBCCtools 资源目标：{safeName}");
                    Dispatcher.UIThread.Post(() =>
                        PipelineExportReady?.Invoke(this, new MpePendingExport(temporaryPath, safeName)));
                }
                else if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception ex)
            {
                LoggerHelper.Error($"处理 MaaPipelineEditor 导出失败：{ex.Message}", ex);
                Dispatcher.UIThread.Post(() => InitializationFailed?.Invoke(this, $"处理 Pipeline 导出失败：{ex.Message}"));
            }
        }

        operation.StateChanged += OnStateChanged;
    }

    private static string? GetOriginalDownloadFileName(string? contentDisposition)
    {
        if (string.IsNullOrWhiteSpace(contentDisposition)
            || !ContentDispositionHeaderValue.TryParse(contentDisposition, out var header))
            return null;

        var fileName = header.FileNameStar ?? header.FileName;
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        return Path.GetFileName(fileName.Trim().Trim('"'));
    }

    private static bool IsEditorNavigation(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
            return true;

        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
               && uri.Host.Equals("mpe.codax.site", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.StartsWith("/stable/", StringComparison.OrdinalIgnoreCase);
    }

    private static void OpenExternal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;
        if (uri.Scheme is not ("http" or "https"))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            LoggerHelper.Info($"MaaPipelineEditor 外部链接已交给系统浏览器：{uri.AbsoluteUri}");
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"打开 MaaPipelineEditor 外部链接失败：{ex.Message}");
        }
    }

    private async Task VerifyEditorMountedAsync(CoreWebView2 webView, int generation)
    {
        for (var attempt = 0; attempt < 32 && !_destroyed && generation == _generation; attempt++)
        {
            try
            {
                var result = await webView.ExecuteScriptAsync(
                    "document.getElementById('root')?.childElementCount ?? 0");
                if (int.TryParse(result, out var childCount) && childCount > 0)
                {
                    LoggerHelper.Info($"MaaPipelineEditor 页面已挂载：root children={childCount}, url={_url}");
                    var reloading = await EnsurePrivateModeStateAsync(webView, generation);
                    if (reloading)
                        return;

                    Dispatcher.UIThread.Post(() => NavigationCompleted?.Invoke(this, EventArgs.Empty));
                    return;
                }
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning($"检查 MaaPipelineEditor 页面状态失败：{ex.Message}");
            }

            await Task.Delay(250);
        }

        if (!_destroyed && generation == _generation)
        {
            LoggerHelper.Error($"MaaPipelineEditor 页面未成功挂载：{_url}");
            Dispatcher.UIThread.Post(() =>
                InitializationFailed?.Invoke(this,
                    "页面资源已加载，但编辑器没有成功启动。请检查内置前端资源路径或 WebView2 控制台。"));
        }
    }

    private async Task<bool> EnsurePrivateModeStateAsync(CoreWebView2 webView, int generation)
    {
        if (_destroyed || generation != _generation)
            return false;

        var seededController = BuildSeededControllerJson();
        var script = $$"""
(() => {
  try {
    const seeded = {{seededController}};
    const cfg = JSON.parse(localStorage.getItem('_mpe_config') || '{}');
    const last = JSON.parse(localStorage.getItem('mpe_last_controller') || 'null');
    const targetAddress = seeded?.params?.address || null;
    const storedAddress = last?.params?.address || null;
    const needsConfig = cfg.autoConnectLastController !== true;
    const needsController = !!seeded && storedAddress !== targetAddress;

    cfg.autoConnectLastController = true;
    cfg.enableLiveScreen = false;
    cfg.configHandlingMode = 'none';
    localStorage.setItem('_mpe_config', JSON.stringify(cfg));
    if (seeded)
      localStorage.setItem('mpe_last_controller', JSON.stringify(seeded));

    const reloadKey = 'mfa_mpe_controller_bootstrap_reload_v1';
    const alreadyReloaded = sessionStorage.getItem(reloadKey) === '1';
    const reloading = (needsConfig || needsController) && !alreadyReloaded;
    if (reloading) {
      sessionStorage.setItem(reloadKey, '1');
      setTimeout(() => location.reload(), 0);
    } else if (!needsConfig && !needsController) {
      sessionStorage.removeItem(reloadKey);
    }

    return { autoConnect: cfg.autoConnectLastController, targetAddress, storedAddress,
      seeded: !!seeded, needsConfig, needsController, reloading };
  } catch (error) {
    return { error: String(error), reloading: false };
  }
})()
""";

        try
        {
            var result = await webView.ExecuteScriptAsync(script);
            LoggerHelper.Info($"MaaPipelineEditor controller bootstrap state: {result}");
            return result.Contains("\"reloading\":true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"Failed to verify MaaPipelineEditor controller bootstrap state: {ex.Message}");
            return false;
        }
    }

    internal async Task StartDemoAsync()
    {
        var webView = _controller?.CoreWebView2;
        if (webView == null || _destroyed)
            return;

        try
        {
            var result = await webView.ExecuteScriptAsync(MpeDemoScript.Start);
            LoggerHelper.Info($"MaaPipelineEditor 交互演示已启动：{result}");
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"启动 MaaPipelineEditor 交互演示失败：{ex.Message}");
        }
    }

    internal async Task StopDemoAsync()
    {
        var webView = _controller?.CoreWebView2;
        if (webView == null || _destroyed)
            return;

        try
        {
            var result = await webView.ExecuteScriptAsync(MpeDemoScript.Stop);
            LoggerHelper.Info($"MaaPipelineEditor 交互演示已退出：{result}");
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"退出 MaaPipelineEditor 交互演示失败：{ex.Message}");
        }
    }
    private async Task InstallChineseUiTranslationAsync(CoreWebView2 webView, int generation)
    {
        if (_destroyed || generation != _generation)
            return;

        const string script = """
(() => {
  if (window.__mfaZhTranslationInstalled) return 'already-installed';
  window.__mfaZhTranslationInstalled = true;

  const translations = new Map([
    ['Recognition', '识别方式（Recognition）'],
    ['Action', '执行动作（Action）'],
    ['recognition', '识别方式（recognition）'],
    ['action', '执行动作（action）'],
    ['others', '其他参数（others）'],
    ['key', '节点名（key）'],
    ['DirectHit', '直接命中（DirectHit，不进行识别）'],
    ['OCR', 'OCR 文字识别'],
    ['TemplateMatch', '模板匹配（TemplateMatch / 找图）'],
    ['ColorMatch', '颜色匹配（ColorMatch / 找色）'],
    ['FeatureMatch', '特征匹配（FeatureMatch / 增强找图）'],
    ['And', '组合识别：全部满足（And）'],
    ['Or', '组合识别：任一满足（Or）'],
    ['NeuralNetworkClassify', '神经网络分类（NeuralNetworkClassify）'],
    ['NeuralNetworkDetect', '神经网络目标检测（NeuralNetworkDetect）'],
    ['Custom', '自定义（Custom）'],
    ['DoNothing', '不执行动作（DoNothing）'],
    ['Click', '点击（Click）'],
    ['Swipe', '滑动（Swipe）'],
    ['Scroll', '鼠标滚轮（Scroll）'],
    ['ClickKey', '单击按键（ClickKey）'],
    ['LongPress', '长按（LongPress）'],
    ['MultiSwipe', '多指滑动（MultiSwipe）'],
    ['TouchDown', '触摸按下（TouchDown）'],
    ['TouchMove', '移动触点（TouchMove）'],
    ['TouchUp', '抬起触点（TouchUp）'],
    ['LongPressKey', '长按按键（LongPressKey）'],
    ['KeyDown', '按键按下（KeyDown）'],
    ['KeyUp', '按键松开（KeyUp）'],
    ['InputText', '输入文本（InputText）'],
    ['StartApp', '启动应用（StartApp）'],
    ['StopApp', '关闭应用（StopApp）'],
    ['StopTask', '停止当前任务（StopTask）'],
    ['Command', '执行本机命令（Command）'],
    ['Shell', '执行 ADB Shell 命令（Shell）'],
    ['Screencap', '保存截图（Screencap）'],
    ['Key', '按键（Key，旧版）'],
    ['next', '后继节点（next）'],
    ['on_error', '异常后继节点（on_error）'],
    ['timeout', '识别超时（timeout）'],
    ['retry', '重试次数（retry）'],
    ['roi', '识别区域（roi）'],
    ['roi_offset', '识别区域偏移（roi_offset）'],
    ['expected', '期望内容（expected）'],
    ['threshold', '匹配阈值（threshold）'],
    ['template', '模板图片（template）'],
    ['target', '操作目标（target）'],
    ['target_offset', '目标偏移（target_offset）'],
    ['pre_delay', '执行前等待（pre_delay）'],
    ['post_delay', '执行后等待（post_delay）']
  ]);

  const shouldSkip = (node) => {
    const el = node.parentElement;
    return !el || !!el.closest('.monaco-editor, textarea, input, pre, code, script, style, [contenteditable="true"]');
  };

  const translateText = (node) => {
    if (!node || node.nodeType !== Node.TEXT_NODE || shouldSkip(node)) return;
    const raw = node.nodeValue ?? '';
    const text = raw.trim();
    const translated = translations.get(text);
    if (!translated || translated === text) return;
    const start = raw.indexOf(text);
    node.nodeValue = raw.slice(0, start) + translated + raw.slice(start + text.length);
  };

  const translateTree = (root) => {
    if (!root) return;
    if (root.nodeType === Node.TEXT_NODE) {
      translateText(root);
      return;
    }
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    let node;
    while ((node = walker.nextNode())) translateText(node);
  };

  translateTree(document.body);
  new MutationObserver((mutations) => {
    for (const mutation of mutations) {
      if (mutation.type === 'characterData') translateText(mutation.target);
      for (const added of mutation.addedNodes) translateTree(added);
    }
  }).observe(document.body, { subtree: true, childList: true, characterData: true });

  return 'installed';
})()
""";

        try
        {
            var result = await webView.ExecuteScriptAsync(script);
            LoggerHelper.Info($"MaaPipelineEditor 中文术语翻译已注入：{result}");
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"注入 MaaPipelineEditor 中文术语翻译失败：{ex.Message}");
        }
    }

    private void UpdateControllerBounds()
    {
        if (_controller == null || _hostHwnd == 0)
            return;

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var width = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        var height = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));
        if (width <= 1 || height <= 1)
            return;

        // NativeControlHost 的占位 HWND 在窗口最大化/侧栏伸缩后可能仍保留旧尺寸。
        // 显式同步 HWND 与 WebView2 Controller，避免右侧/底部出现黑边。
        SetWindowPos(
            _hostHwnd,
            nint.Zero,
            0,
            0,
            width,
            height,
            SwpNoMove | SwpNoZOrder | SwpNoActivate);
        _controller.Bounds = new Rectangle(0, 0, width, height);
        _controller.NotifyParentWindowPositionChanged();
    }

    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);
}


