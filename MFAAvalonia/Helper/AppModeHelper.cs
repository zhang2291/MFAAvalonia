using MFAAvalonia.Extensions.MaaFW;
using Newtonsoft.Json.Linq;
using System;
using System.IO;

namespace MFAAvalonia.Helper;

internal static class AppModeHelper
{
    private static bool _confirmedMbccTools;

    public static bool IsMbccTools
    {
        get
        {
            if (_confirmedMbccTools)
                return true;

            var detected = DetectMbccTools();
            if (detected)
                _confirmedMbccTools = true;
            return detected;
        }
    }

    private static bool DetectMbccTools()
    {
        if (string.Equals(MaaProcessor.Interface?.Name, "MBCCtools", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var rootDirectory in new[] { AppPaths.DataRoot, AppContext.BaseDirectory })
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rootDirectory))
                    continue;

                var interfacePath = Path.Combine(rootDirectory, "interface.json");
                if (!File.Exists(interfacePath))
                    continue;

                var root = JObject.Parse(File.ReadAllText(interfacePath));
                if (string.Equals(root["name"]?.ToString(), "MBCCtools", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(root["title"]?.ToString(), "MBCCtools", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(root["mirrorchyan_rid"]?.ToString(), "MBCCtools", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // 启动早期路径或文件可能尚未就绪，下一次检测会重试。
            }
        }

        return false;
    }
}
