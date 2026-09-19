using MFAAvalonia.Helper;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Services;

internal sealed record MpeLaunchInfo(
    string ProjectRoot,
    string WebRoot,
    string EditorUrl);

internal static class MpeIntegrationService
{
    private const int DefaultBridgePort = 9066;
    private const int MaxBridgePort = 9076;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? _bridgeProcess;

    static MpeIntegrationService()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopBridge();
    }

    internal static async Task<MpeLaunchInfo> EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Pipeline 编辑器当前仅支持 Windows Desktop。");

        await Gate.WaitAsync(cancellationToken);
        try
        {
            // Each editor session owns exactly one LocalBridge. If a previous session did not
            // unload cleanly, stop only the process started by this integration before creating
            // the next one. External/stale bridges are left untouched and skipped by port scan.
            StopBridge();

            var projectRoot = ResolveProjectRoot();
            var toolRoot = Path.Combine(projectRoot, "tools", "MaaPipelineEditor");
            var bridgePath = Path.Combine(toolRoot, "mpelb.exe");
            var webRoot = Path.Combine(toolRoot, "web");
            NormalizeBundledWebBase(webRoot);
            ValidateInstallation(bridgePath, webRoot, toolRoot);

            var bridgePort = await SelectBridgePortAsync(cancellationToken);
            var configPath = Path.Combine(toolRoot, "mfa-localbridge.json");
            await WriteBridgeConfigAsync(configPath, bridgePort, cancellationToken);

            StartBridge(bridgePath, projectRoot, toolRoot, configPath, bridgePort);
            await WaitForBridgeAsync(bridgePort, cancellationToken);
            var url = $"https://mpe.codax.site/stable/index.html?link_lb=1&port={bridgePort}";
            return new MpeLaunchInfo(projectRoot, webRoot, url);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void NormalizeBundledWebBase(string webRoot)
    {
        var stableRoot = Path.Combine(webRoot, "stable");
        if (!Directory.Exists(stableRoot))
            return;

        var patched = 0;
        foreach (var file in Directory.EnumerateFiles(stableRoot, "*.*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                                    || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                                    || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)))
        {
            var text = File.ReadAllText(file, Encoding.UTF8);
            if (!text.Contains("/production/", StringComparison.Ordinal))
                continue;
            File.WriteAllText(file, text.Replace("/production/", "/stable/", StringComparison.Ordinal), new UTF8Encoding(false));
            patched++;
        }
        if (patched > 0)
            LoggerHelper.Info($"MaaPipelineEditor 前端路径已自动适配 /stable/，修正文件数：{patched}");
    }

    private static void ValidateInstallation(string bridgePath, string webRoot, string toolRoot)
    {
        if (!File.Exists(bridgePath))
            throw new FileNotFoundException("未找到 MaaPipelineEditor LocalBridge。", bridgePath);
        var editorIndex = Path.Combine(webRoot, "stable", "index.html");
        if (!File.Exists(editorIndex))
            throw new FileNotFoundException("未找到 MaaPipelineEditor 前端文件。", editorIndex);

    }

    private static async Task WriteBridgeConfigAsync(string configPath, int bridgePort, CancellationToken cancellationToken)
    {
        var config = new Dictionary<string, object?>
        {
            ["server"] = new Dictionary<string, object?>
            {
                ["port"] = bridgePort,
                ["host"] = "localhost",
                ["allowed_origins"] = new[] { "https://mpe.codax.site", "http://localhost", "http://127.0.0.1", "http://[::1]" }
            },
            ["file"] = new Dictionary<string, object?>
            {
                ["exclude"] = new[] { "node_modules", ".git", "dist", "build" },
                ["extensions"] = new[] { ".json", ".jsonc" },
                ["max_depth"] = 10,
                ["max_files"] = 10000
            },
            ["log"] = new Dictionary<string, object?>
            {
                ["level"] = "INFO",
                ["dir"] = "./logs",
                ["push_to_client"] = false
            },
            ["maafw"] = new Dictionary<string, object?> { ["enabled"] = true },
            ["interface"] = new Dictionary<string, object?> { ["path"] = "interface.json" }
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(configPath, json, cancellationToken);
    }

    private static void StartBridge(string bridgePath, string projectRoot, string toolRoot, string configPath, int bridgePort)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = bridgePath,
            WorkingDirectory = toolRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configPath);
        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(projectRoot);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(bridgePort.ToString());
        startInfo.ArgumentList.Add("--interface");
        startInfo.ArgumentList.Add("interface.json");
        startInfo.ArgumentList.Add("--portable");

        _bridgeProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 MaaPipelineEditor LocalBridge。");

        _bridgeProcess.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                LoggerHelper.Debug($"[MPELB] {e.Data}");
        };
        _bridgeProcess.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                LoggerHelper.Warning($"[MPELB] {e.Data}");
        };
        _bridgeProcess.BeginOutputReadLine();
        _bridgeProcess.BeginErrorReadLine();
        LoggerHelper.Info($"MaaPipelineEditor LocalBridge 已启动：root={projectRoot}, port={bridgePort}");
    }

    private static async Task WaitForBridgeAsync(int bridgePort, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 40; i++)
        {
            if (_bridgeProcess is { HasExited: true })
                throw new InvalidOperationException($"MaaPipelineEditor LocalBridge 启动失败，退出码：{_bridgeProcess.ExitCode}");
            if (await IsPortOpenAsync(bridgePort, cancellationToken))
                return;
            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"等待 MaaPipelineEditor LocalBridge 端口 {bridgePort} 超时。");
    }

    private static async Task<int> SelectBridgePortAsync(CancellationToken cancellationToken)
    {
        // A stale bridge can keep 9066 open. Reusing a merely-open port can connect the UI
        // to an old instance, so always start this editor session on a free owned port.
        for (var port = DefaultBridgePort; port <= MaxBridgePort; port++)
        {
            if (!await IsPortOpenAsync(port, cancellationToken))
                return port;
        }

        throw new InvalidOperationException($"MaaPipelineEditor LocalBridge ports {DefaultBridgePort}-{MaxBridgePort} are all in use.");
    }

    private static async Task<bool> IsPortOpenAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(350);
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    internal static string ResolveProjectRoot()
    {
        var configured = Environment.GetEnvironmentVariable("MFA_MPE_PROJECT_ROOT");
        if (IsProjectRoot(configured))
            return Path.GetFullPath(configured!);

        // Debug/发布目录也会包含复制后的 interface.json 与 resource，不能把 bin 目录误判为 MBCCtools 源项目。
        // 优先寻找同时安装了 MaaPipelineEditor 的真实项目根目录。
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory != null && depth < 10; depth++, directory = directory.Parent)
        {
            var sibling = Path.Combine(directory.FullName, "MBCCtools");
            if (IsProjectRoot(sibling) && HasMpeInstallation(sibling))
                return Path.GetFullPath(sibling);

            if (IsProjectRoot(directory.FullName) && HasMpeInstallation(directory.FullName))
                return directory.FullName;
        }

        // MPE 尚未安装时，仍尽量返回真实项目候选，让后续校验给出具体缺失文件。
        directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory != null && depth < 10; depth++, directory = directory.Parent)
        {
            var sibling = Path.Combine(directory.FullName, "MBCCtools");
            if (IsProjectRoot(sibling))
                return Path.GetFullPath(sibling);
        }

        throw new DirectoryNotFoundException(
            "未找到 MBCCtools 项目根目录。可通过环境变量 MFA_MPE_PROJECT_ROOT 显式指定。");
    }

    private static bool HasMpeInstallation(string path)
    {
        return File.Exists(Path.Combine(path, "tools", "MaaPipelineEditor", "mpelb.exe"))
               && File.Exists(Path.Combine(path, "tools", "MaaPipelineEditor", "web", "stable", "index.html"));
    }

    private static bool IsProjectRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        return File.Exists(Path.Combine(path, "interface.json"))
               && Directory.Exists(Path.Combine(path, "resource"));
    }

    internal static void StopBridge()
    {
        try
        {
            if (_bridgeProcess is { HasExited: false })
                _bridgeProcess.Kill(entireProcessTree: true);
            _bridgeProcess?.Dispose();
            _bridgeProcess = null;
            LoggerHelper.Debug("MaaPipelineEditor LocalBridge 已停止。");
        }
        catch
        {
            // 应用退出时忽略清理失败。
        }
    }
}

