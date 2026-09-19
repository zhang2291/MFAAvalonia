using Avalonia.Controls;
using Avalonia.Interactivity;
using MFAAvalonia.Controls;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace MFAAvalonia.Views.Pages;

public partial class PipelineEditorView : UserControl
{
    private bool _started;
    private MpeWebViewHost? _webView;
    private string? _projectRoot;
    private MpePendingExport? _pendingExport;

    public PipelineEditorView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_started)
            return;

        _started = true;
        await StartEditorAsync();
    }
    private async Task StartEditorAsync()
    {
        LoadingPanel.IsVisible = true;
        LoadingProgress.IsVisible = true;
        RetryButton.IsVisible = false;
        StatusText.Text = "正在启动 LocalBridge，并连接 MBCCtools 资源目录…";

        try
        {
            var launch = await MpeIntegrationService.EnsureReadyAsync();
            _projectRoot = launch.ProjectRoot;
            StatusText.Text = $"正在加载 MaaPipelineEditor…\n项目：{launch.ProjectRoot}";

            if (_webView != null)
                StopEditor();

            WebViewContainer.Content = null;
            _webView = new MpeWebViewHost(launch.ProjectRoot, launch.WebRoot, launch.EditorUrl);
            _webView.NavigationCompleted += OnNavigationCompleted;
            _webView.InitializationFailed += OnInitializationFailed;
            _webView.PipelineExportReady += OnPipelineExportReady;
            WebViewContainer.Content = _webView;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            LoggerHelper.Error($"启动 MaaPipelineEditor 失败：{ex.Message}", ex);
        }
    }
    private void OnNavigationCompleted(object? sender, EventArgs e)
    {
        LoadingPanel.IsVisible = false;
        LoadingProgress.IsVisible = false;
        RetryButton.IsVisible = false;
    }

    private void OnInitializationFailed(object? sender, string message)
    {
        ShowError(message);
    }

    private void OnPipelineExportReady(object? sender, MpePendingExport e)
    {
        _pendingExport = e;
        ExportSourceText.Text = $"MPE 已导出：{e.SuggestedName}\n请选择保存资源和主页发布信息。";
        ExportFileNameBox.Text = e.SuggestedName;
        TaskNameBox.Text = Path.GetFileNameWithoutExtension(e.SuggestedName);
        EntryBox.Text = string.Empty;
        DescriptionBox.Text = string.Empty;
        DefaultCheckBox.IsChecked = false;
        BaseTargetRadio.IsChecked = true;
        ApplyDefaultResourceSelection(MpeResourceTarget.Base);
        PublishTaskCheck.IsChecked = true;
        PublishOptionsPanel.IsVisible = true;
        ExportErrorText.IsVisible = false;
        ExportPanel.IsVisible = true;
    }

    private void OnExportTargetChanged(object? sender, RoutedEventArgs e)
    {
        ApplyDefaultResourceSelection(GetSelectedTarget());
    }

    private void ApplyDefaultResourceSelection(MpeResourceTarget target)
    {
        // base 是公共资源层，官服/B服/国际服都会加载；
        // bilibili/global 则是各自服务器的增量覆盖层。
        OfficialResourceCheck.IsChecked = target == MpeResourceTarget.Base;
        BilibiliResourceCheck.IsChecked = target is MpeResourceTarget.Base or MpeResourceTarget.Bilibili;
        GlobalResourceCheck.IsChecked = target is MpeResourceTarget.Base or MpeResourceTarget.Global;
    }
    private void OnPublishTaskChanged(object? sender, RoutedEventArgs e)
    {
        PublishOptionsPanel.IsVisible = PublishTaskCheck.IsChecked == true;
    }

    private MpeResourceTarget GetSelectedTarget()
    {
        if (BilibiliTargetRadio.IsChecked == true)
            return MpeResourceTarget.Bilibili;
        if (GlobalTargetRadio.IsChecked == true)
            return MpeResourceTarget.Global;
        return MpeResourceTarget.Base;
    }

    private List<string> GetSelectedResources()
    {
        var resources = new List<string>();
        if (OfficialResourceCheck.IsChecked == true) resources.Add("官服");
        if (BilibiliResourceCheck.IsChecked == true) resources.Add("B服");
        if (GlobalResourceCheck.IsChecked == true) resources.Add("国际服");
        return resources;
    }

    private void OnCancelExportClick(object? sender, RoutedEventArgs e)
    {
        if (_pendingExport != null && File.Exists(_pendingExport.TemporaryPath))
        {
            try { File.Delete(_pendingExport.TemporaryPath); }
            catch { }
        }
        _pendingExport = null;
        ExportPanel.IsVisible = false;
    }

    private void OnSaveExportClick(object? sender, RoutedEventArgs e)
    {
        if (_pendingExport == null || string.IsNullOrWhiteSpace(_projectRoot))
            return;

        ExportErrorText.IsVisible = false;
        try
        {
            var fileName = string.IsNullOrWhiteSpace(ExportFileNameBox.Text)
                ? _pendingExport.SuggestedName
                : ExportFileNameBox.Text.Trim();
            var target = GetSelectedTarget();
            var resources = GetSelectedResources();
            if (PublishTaskCheck.IsChecked == true && resources.Count == 0)
                throw new InvalidDataException("发布到主页任务列表时，至少选择一个适用资源。" );

            var saved = MpePipelineExportService.SaveToResource(
                _projectRoot,
                _pendingExport.TemporaryPath,
                fileName,
                target);
            var published = false;
            if (PublishTaskCheck.IsChecked == true)
            {
                var taskName = string.IsNullOrWhiteSpace(TaskNameBox.Text)
                    ? Path.GetFileNameWithoutExtension(saved.PipelinePath)
                    : TaskNameBox.Text.Trim();
                var entry = string.IsNullOrWhiteSpace(EntryBox.Text) ? saved.Entry : EntryBox.Text.Trim();
                MpePipelineExportService.UpsertTask(
                    _projectRoot,
                    saved.PipelinePath,
                    taskName,
                    entry,
                    DefaultCheckBox.IsChecked == true,
                    DescriptionBox.Text,
                    resources);
                published = true;
            }

            bool applied;
            string reloadMessage;
            if (string.Equals(
                    Path.GetFullPath(_projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(AppPaths.DataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                applied = MaaProcessor.ReloadInterfaceAndTasks(out reloadMessage);
            }
            else
            {
                applied = false;
                reloadMessage = $"已保存到 {_projectRoot}，但当前运行资源目录是 {AppPaths.DataRoot}。请从保存后的 MBCCtools 目录启动再测试，或重新打包后应用。";
            }
            var title = published ? "Pipeline 已保存并发布" : "Pipeline 已保存";
            if (applied)
                ToastHelper.Info(title, reloadMessage);
            else
                ToastHelper.Warn(title, $"文件已保存，但尚未热应用：{reloadMessage}");

            LoggerHelper.Info($"MPE Pipeline 已保存：{saved.PipelinePath}, entry={saved.Entry}, published={published}, applied={applied}");
            if (File.Exists(_pendingExport.TemporaryPath))
            {
                try { File.Delete(_pendingExport.TemporaryPath); }
                catch (Exception cleanupEx) { LoggerHelper.Warning($"清理 MPE 临时导出文件失败：{cleanupEx.Message}"); }
            }
            _pendingExport = null;
                ExportPanel.IsVisible = false;
        }
        catch (Exception ex)
        {
            ExportErrorText.Text = ex.Message;
            ExportErrorText.IsVisible = true;
            LoggerHelper.Error($"保存 MPE Pipeline 失败：{ex.Message}", ex);
        }
    }
    private async void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        await StartEditorAsync();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        StopEditor();
        MpeIntegrationService.StopBridge();
        _started = false;
    }

    private void StopEditor()
    {
        if (_webView == null)
            return;

        _webView.NavigationCompleted -= OnNavigationCompleted;
        _webView.InitializationFailed -= OnInitializationFailed;
        _webView.PipelineExportReady -= OnPipelineExportReady;
        WebViewContainer.Content = null;
        _webView.Shutdown();
        _webView = null;
    }

    private void ShowError(string message)
    {
        LoadingPanel.IsVisible = true;
        LoadingProgress.IsVisible = false;
        RetryButton.IsVisible = true;
        StatusText.Text = $"Pipeline 编辑器启动失败：{message}";
    }
}
