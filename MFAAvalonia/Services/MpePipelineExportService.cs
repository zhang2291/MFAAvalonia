using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MFAAvalonia.Services;

internal sealed record MpePipelineRegistrationResult(
    string PipelinePath,
    string TaskName,
    string Entry,
    bool InterfaceChanged);

internal enum MpeResourceTarget
{
    Base,
    Bilibili,
    Global
}

internal sealed record MpePipelineSaveResult(
    string PipelinePath,
    string Entry,
    string ResourceName);

internal static class MpePipelineExportService
{
    internal static string GetTargetPath(
        string projectRoot,
        string fileName,
        MpeResourceTarget target)
    {
        var folder = target switch
        {
            MpeResourceTarget.Base => "base",
            MpeResourceTarget.Bilibili => "bilibili",
            MpeResourceTarget.Global => "global",
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
        };
        return Path.Combine(projectRoot, "resource", folder, "pipeline", SanitizeFileName(fileName));
    }

    internal static MpePipelineSaveResult SaveToResource(
        string projectRoot,
        string temporaryPath,
        string fileName,
        MpeResourceTarget target,
        bool overwrite = true)
    {
        var resourceName = target switch
        {
            MpeResourceTarget.Base => "官服/公共",
            MpeResourceTarget.Bilibili => "B服",
            MpeResourceTarget.Global => "国际服",
            _ => target.ToString()
        };

        var targetPath = GetTargetPath(projectRoot, fileName, target);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        // MPE/浏览器在导出一个已经存在的文件时，文件名有时会自动变成 xxx1.json。
        // MaaFramework 要求同一个资源层内的节点 key 全局唯一；把这种“另存一份”放进 pipeline
        // 会导致整个资源包加载失败。若导出内容与某个现有 Pipeline 存在节点重叠，
        // 将其视为对原文件的编辑，直接覆盖原文件，而不是生成重复文件。
        var requestedPath = targetPath;
        targetPath = ResolveExistingPipelinePath(targetPath, temporaryPath);

        if (File.Exists(targetPath) && !overwrite)
            throw new IOException($"目标文件已存在：{targetPath}");

        ValidatePipelineJson(temporaryPath);
        File.Copy(temporaryPath, targetPath, overwrite);
        if (!string.Equals(Path.GetFullPath(requestedPath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase)
            && File.Exists(requestedPath))
        {
            // Browser/MPE may already have materialized an auto-suffixed duplicate (xxx1.json).
            // Once the edited content has been written back to the original file, remove that duplicate
            // so MaaFramework does not reject the whole resource bundle because of repeated node keys.
            File.Delete(requestedPath);
        }
        // 临时导出文件由调用方在整套“保存 Pipeline + 发布 interface”成功后统一清理。
        // 这样若 interface 写入/校验失败，用户仍可直接修改配置后重试，不必重新从 MPE 导出。
        var entry = DetectEntry(targetPath, Path.GetFileNameWithoutExtension(targetPath));
        return new MpePipelineSaveResult(targetPath, entry, resourceName);
    }

    internal static MpePipelineRegistrationResult Register(string projectRoot, string pipelinePath)
    {
        var taskName = Path.GetFileNameWithoutExtension(pipelinePath);
        var entry = DetectEntry(pipelinePath, taskName);
        var interfacePath = Path.Combine(projectRoot, "interface.json");
        if (!File.Exists(interfacePath))
            throw new FileNotFoundException("未找到 MBCCtools interface.json。", interfacePath);

        var root = JObject.Parse(File.ReadAllText(interfacePath, Encoding.UTF8));
        var tasks = root["task"] as JArray;
        if (tasks == null)
        {
            tasks = new JArray();
            root["task"] = tasks;
        }

        var existingByName = tasks.OfType<JObject>()
            .FirstOrDefault(item => string.Equals((string?)item["name"], taskName, StringComparison.Ordinal));
        var existingByEntry = tasks.OfType<JObject>()
            .FirstOrDefault(item => string.Equals((string?)item["entry"], entry, StringComparison.Ordinal));

        var changed = false;
        if (existingByName != null)
        {
            if (!string.Equals((string?)existingByName["entry"], entry, StringComparison.Ordinal))
            {
                existingByName["entry"] = entry;
                changed = true;
            }
        }
        else if (existingByEntry == null)
        {
            tasks.Add(new JObject
            {
                ["name"] = taskName,
                ["entry"] = entry,
                ["default_check"] = false
            });
            changed = true;
        }

        if (changed)
            WriteInterfaceSafely(interfacePath, root);

        return new MpePipelineRegistrationResult(pipelinePath, taskName, entry, changed);
    }

    internal static MpePipelineRegistrationResult UpsertTask(
        string projectRoot,
        string pipelinePath,
        string taskName,
        string? entry,
        bool defaultCheck,
        string? description,
        IReadOnlyCollection<string>? resources,
        IReadOnlyCollection<string>? optionNames = null)
    {
        if (string.IsNullOrWhiteSpace(taskName))
            throw new ArgumentException("任务名称不能为空。", nameof(taskName));

        var resolvedEntry = string.IsNullOrWhiteSpace(entry)
            ? DetectEntry(pipelinePath, Path.GetFileNameWithoutExtension(pipelinePath))
            : entry.Trim();
        var interfacePath = Path.Combine(projectRoot, "interface.json");
        if (!File.Exists(interfacePath))
            throw new FileNotFoundException("未找到 MBCCtools interface.json。", interfacePath);

        var root = JObject.Parse(File.ReadAllText(interfacePath, Encoding.UTF8));
        ValidateEntryForResources(projectRoot, root, resolvedEntry, resources);
        var tasks = root["task"] as JArray;
        if (tasks == null)
        {
            tasks = new JArray();
            root["task"] = tasks;
        }
        var task = tasks.OfType<JObject>()
            .FirstOrDefault(item => string.Equals((string?)item["name"], taskName, StringComparison.Ordinal));
        var changed = task == null;
        task ??= new JObject();
        if (task.Parent == null)
            tasks.Add(task);

        changed |= SetValue(task, "name", taskName);
        changed |= SetValue(task, "entry", resolvedEntry);
        changed |= SetValue(task, "default_check", defaultCheck);
        changed |= SetOptionalValue(task, "description", description);
        changed |= SetStringArray(task, "resource", resources);
        changed |= SetStringArray(task, "option", optionNames);

        if (changed)
            WriteInterfaceSafely(interfacePath, root);

        return new MpePipelineRegistrationResult(pipelinePath, taskName, resolvedEntry, changed);
    }

    private static void ValidateEntryForResources(
        string projectRoot,
        JObject interfaceRoot,
        string entry,
        IReadOnlyCollection<string>? resources)
    {
        var selected = resources?.Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim()).Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (selected.Count == 0)
            return;

        if (interfaceRoot["resource"] is not JArray resourceArray)
            throw new InvalidDataException("interface.json 未定义 resource，无法校验任务适用资源。");

        foreach (var resourceName in selected)
        {
            var resource = resourceArray.OfType<JObject>().FirstOrDefault(item =>
                string.Equals((string?)item["name"], resourceName, StringComparison.Ordinal));
            if (resource == null)
                throw new InvalidDataException($"interface.json 中不存在资源“{resourceName}”。");

            var paths = resource["path"] switch
            {
                JValue { Type: JTokenType.String } value => new[] { value.Value<string>()! },
                JArray array => array.Values<string>().Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray(),
                _ => []
            };

            var found = false;
            foreach (var relativePath in paths)
            {
                var pipelineDir = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar), "pipeline");
                if (!Directory.Exists(pipelineDir))
                    continue;

                var files = Directory.EnumerateFiles(pipelineDir, "*.json", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(pipelineDir, "*.jsonc", SearchOption.AllDirectories));
                foreach (var file in files)
                {
                    try
                    {
                        var pipeline = JObject.Parse(File.ReadAllText(file, Encoding.UTF8));
                        if (pipeline.Property(entry) != null)
                        {
                            found = true;
                            break;
                        }
                    }
                    catch
                    {
                        // 未完成的其它 Pipeline 不应阻止发布当前任务。
                    }
                }
                if (found) break;
            }

            if (!found)
                throw new InvalidDataException($"入口节点“{entry}”在资源“{resourceName}”中不存在，请调整适用资源或 entry。");
        }
    }

    private static string ResolveExistingPipelinePath(string requestedPath, string temporaryPath)
    {
        var directory = Path.GetDirectoryName(requestedPath)!;
        var incoming = JObject.Parse(File.ReadAllText(temporaryPath, Encoding.UTF8));
        var incomingKeys = incoming.Properties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (incomingKeys.Count == 0)
            return requestedPath;

        // An existing exact filename is normally the file the user intentionally edited.
        // Only redirect it when it clearly looks like a browser auto-suffixed duplicate
        // (foo1.json -> foo.json) and both files overlap the incoming Pipeline nodes.
        if (File.Exists(requestedPath))
        {
            var originalCandidate = GetAutoSuffixedOriginalPath(requestedPath);
            if (originalCandidate == null || !File.Exists(originalCandidate))
                return requestedPath;

            var requestedOverlap = CountNodeOverlap(requestedPath, incomingKeys);
            var originalOverlap = CountNodeOverlap(originalCandidate, incomingKeys);
            return originalOverlap > 0 && originalOverlap >= requestedOverlap
                ? originalCandidate
                : requestedPath;
        }

        var matches = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(directory, "*.jsonc", SearchOption.TopDirectoryOnly))
            .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(requestedPath), StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                try
                {
                    var existing = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                    var overlap = existing.Properties().Count(property => incomingKeys.Contains(property.Name));
                    return (Path: path, Overlap: overlap);
                }
                catch
                {
                    return (Path: path, Overlap: 0);
                }
            })
            .Where(item => item.Overlap > 0)
            .OrderByDescending(item => item.Overlap)
            .ToList();

        if (matches.Count == 0)
            return requestedPath;

        if (matches.Count > 1 && matches[0].Overlap == matches[1].Overlap)
            throw new InvalidDataException("导出的 Pipeline 与多个现有文件存在重复节点，无法安全判断应覆盖哪个文件。请先整理重复节点。");

        return matches[0].Path;
    }

    private static string? GetAutoSuffixedOriginalPath(string requestedPath)
    {
        var stem = Path.GetFileNameWithoutExtension(requestedPath);
        var index = stem.Length;
        while (index > 0 && char.IsDigit(stem[index - 1]))
            index--;

        if (index == stem.Length || index == 0)
            return null;

        var originalName = stem[..index] + Path.GetExtension(requestedPath);
        return Path.Combine(Path.GetDirectoryName(requestedPath)!, originalName);
    }

    private static int CountNodeOverlap(string path, HashSet<string> incomingKeys)
    {
        try
        {
            var existing = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            return existing.Properties().Count(property => incomingKeys.Contains(property.Name));
        }
        catch
        {
            return 0;
        }
    }

    private static void ValidatePipelineJson(string path)
    {
        var root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
        if (!root.Properties().Any())
            throw new InvalidDataException("导出的 Pipeline 中没有节点，未覆盖现有文件。");
    }

    private static void WriteInterfaceSafely(string interfacePath, JObject root)
    {
        var content = root.ToString(Formatting.Indented) + Environment.NewLine;
        var backupPath = interfacePath + ".bak";
        var tempPath = interfacePath + ".tmp";
        File.Copy(interfacePath, backupPath, true);
        File.WriteAllText(tempPath, content, new UTF8Encoding(false));
        File.Move(tempPath, interfacePath, true);
    }

    private static string SanitizeFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".jsonc", StringComparison.OrdinalIgnoreCase))
            extension = ".json";
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var invalid = Path.GetInvalidFileNameChars();
        stem = string.Concat(stem.Select(ch => invalid.Contains(ch) ? '_' : ch)).Trim();
        if (string.IsNullOrWhiteSpace(stem))
            stem = "pipeline";
        return stem + extension;
    }

    private static bool SetValue(JObject target, string name, JToken value)
    {
        if (JToken.DeepEquals(target[name], value))
            return false;
        target[name] = value;
        return true;
    }

    private static bool SetOptionalValue(JObject target, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (target.Property(name) == null)
                return false;
            target.Property(name)!.Remove();
            return true;
        }
        return SetValue(target, name, value.Trim());
    }

    private static bool SetStringArray(JObject target, string name, IReadOnlyCollection<string>? values)
    {
        var normalized = values?.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (normalized.Count == 0)
        {
            if (target.Property(name) == null)
                return false;
            target.Property(name)!.Remove();
            return true;
        }
        return SetValue(target, name, new JArray(normalized));
    }

    private static string DetectEntry(string pipelinePath, string fallback)
    {
        var json = File.ReadAllText(pipelinePath, Encoding.UTF8);
        var root = JObject.Parse(json);
        var keys = root.Properties().Select(property => property.Name).ToList();
        if (keys.Count == 0)
            throw new InvalidDataException("导出的 Pipeline 中没有节点。请至少创建一个节点后再导出。");

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in root.Properties().Select(property => property.Value).OfType<JObject>())
        {
            CollectNodeReferences(node["next"], referenced);
            CollectNodeReferences(node["on_error"], referenced);
        }

        var roots = keys.Where(key => !referenced.Contains(key)).ToList();
        var sameAsFileName = keys.FirstOrDefault(key =>
            string.Equals(key, fallback, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(sameAsFileName))
            return sameAsFileName;
        if (roots.Count > 0)
            return roots[0];
        return keys[0];
    }

    private static void CollectNodeReferences(JToken? token, HashSet<string> references)
    {
        switch (token)
        {
            case JValue { Type: JTokenType.String } value:
                var text = value.Value<string>();
                if (!string.IsNullOrWhiteSpace(text))
                    references.Add(text);
                break;
            case JArray array:
                foreach (var item in array)
                    CollectNodeReferences(item, references);
                break;
        }
    }
}
