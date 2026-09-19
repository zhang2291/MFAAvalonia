using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;

namespace MFAAvalonia.Services;

internal sealed class EditableOptionCase
{
    internal JObject Raw { get; init; } = new();
    internal string Name { get; set; } = string.Empty;
    internal string Icon { get; set; } = string.Empty;
    internal string PipelineOverrideJson { get; set; } = "{}";
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? "未命名选项" : Name;
}

internal sealed class PipelineNodeChoice
{
    internal required string Name { get; init; }
    internal required string Source { get; init; }
    public override string ToString() => string.IsNullOrWhiteSpace(Source) ? Name : $"{Name}  [{Source}]";
}

internal sealed class EditableOption
{
    internal JObject Raw { get; init; } = new();
    internal string Name { get; set; } = string.Empty;
    internal string Description { get; set; } = string.Empty;
    internal string DefaultCase { get; set; } = string.Empty;
    internal ObservableCollection<EditableOptionCase> Cases { get; } = [];
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? "未命名设置" : Name;
}
internal sealed class EditableTask
{
    internal JObject Raw { get; init; } = new();
    internal string Name { get; set; } = string.Empty;
    internal string Entry { get; set; } = string.Empty;
    internal string Description { get; set; } = string.Empty;
    internal bool DefaultCheck { get; set; }
    internal HashSet<string> Resources { get; } = new(StringComparer.Ordinal);
    internal ObservableCollection<EditableOption> Options { get; } = [];
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? "未命名任务" : Name;
}

internal sealed class InterfaceEditorDocument
{
    internal required string ProjectRoot { get; init; }
    internal required string InterfacePath { get; init; }
    internal required JObject Root { get; init; }
    internal ObservableCollection<EditableTask> Tasks { get; } = [];
    internal Dictionary<string, EditableOption> OptionsByName { get; } = new(StringComparer.Ordinal);
}

internal static class InterfaceTaskEditorService
{
    internal static InterfaceEditorDocument Load(string projectRoot)
    {
        var interfacePath = Path.Combine(projectRoot, "interface.json");
        if (!File.Exists(interfacePath))
            throw new FileNotFoundException("未找到 interface.json。", interfacePath);
        var root = JObject.Parse(File.ReadAllText(interfacePath, Encoding.UTF8));
        var document = new InterfaceEditorDocument
        {
            ProjectRoot = projectRoot,
            InterfacePath = interfacePath,
            Root = root
        };

        var optionRoot = root["option"] as JObject;
        if (optionRoot == null)
        {
            optionRoot = new JObject();
            root["option"] = optionRoot;
        }
        foreach (var property in optionRoot.Properties())
        {
            if (property.Value is not JObject optionObject)
                continue;
            var option = new EditableOption
            {
                Raw = optionObject,
                Name = property.Name,
                Description = (string?)optionObject["description"] ?? string.Empty,
                DefaultCase = (string?)optionObject["default_case"] ?? string.Empty
            };
            if (optionObject["cases"] is JArray cases)
            {
                foreach (var item in cases.OfType<JObject>())
                    option.Cases.Add(ReadCase(item));
            }
            document.OptionsByName[property.Name] = option;
        }

        if (root["task"] is not JArray taskArray)
        {
            taskArray = new JArray();
            root["task"] = taskArray;
        }
        foreach (var item in taskArray.OfType<JObject>())
        {
            var task = new EditableTask
            {
                Raw = item,
                Name = (string?)item["name"] ?? string.Empty,
                Entry = (string?)item["entry"] ?? string.Empty,
                Description = (string?)item["description"] ?? (string?)item["doc"] ?? string.Empty,
                DefaultCheck = (bool?)item["default_check"] ?? false
            };
            foreach (var resource in ReadStringList(item["resource"]))
                task.Resources.Add(resource);
            if (task.Resources.Count == 0)
            {
                task.Resources.Add("官服");
                task.Resources.Add("B服");
                task.Resources.Add("国际服");
            }
            foreach (var optionName in ReadStringList(item["option"]))
            {
                if (!document.OptionsByName.TryGetValue(optionName, out var option))
                {
                    option = new EditableOption { Name = optionName };
                    document.OptionsByName[optionName] = option;
                }
                task.Options.Add(option);
            }
            document.Tasks.Add(task);
        }

        return document;
    }

    internal static EditableTask CreateTask()
    {
        return new EditableTask
        {
            Name = "新任务",
            Entry = string.Empty,
            DefaultCheck = false
        };
    }
    internal static EditableOption CreateOption(string? name = null)
    {
        return new EditableOption
        {
            Name = string.IsNullOrWhiteSpace(name) ? "新设置" : name.Trim()
        };
    }

    internal static EditableOptionCase CreateCase()
    {
        return new EditableOptionCase
        {
            Name = "新选项",
            PipelineOverrideJson = "{}"
        };
    }

    internal static IReadOnlyList<PipelineNodeChoice> GetPipelineNodes(
        InterfaceEditorDocument document,
        IEnumerable<string>? resourceNames)
    {
        var selected = new HashSet<string>(resourceNames ?? [], StringComparer.Ordinal);
        var resourcePaths = new List<string>();
        if (document.Root["resource"] is JArray resources)
        {
            foreach (var resource in resources.OfType<JObject>())
            {
                var name = (string?)resource["name"] ?? string.Empty;
                if (selected.Count > 0 && !selected.Contains(name)) continue;
                resourcePaths.AddRange(ReadStringList(resource["path"]));
            }
        }
        var byName = new Dictionary<string, PipelineNodeChoice>(StringComparer.Ordinal);
        foreach (var relativePath in resourcePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var pipelineDir = Path.Combine(document.ProjectRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar), "pipeline");
            if (!Directory.Exists(pipelineDir)) continue;
            foreach (var file in Directory.EnumerateFiles(pipelineDir, "*.json", SearchOption.AllDirectories)
                         .Concat(Directory.EnumerateFiles(pipelineDir, "*.jsonc", SearchOption.AllDirectories))
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var root = JObject.Parse(File.ReadAllText(file, Encoding.UTF8));
                    var source = Path.GetRelativePath(document.ProjectRoot, file).Replace('\\', '/');
                    foreach (var property in root.Properties())
                        byName[property.Name] = new PipelineNodeChoice { Name = property.Name, Source = source };
                }
                catch { }
            }
        }
        return byName.Values.OrderBy(item => item.Name, StringComparer.CurrentCulture).ToList();
    }

    internal static void Save(InterfaceEditorDocument document)
    {
        var duplicateTaskName = document.Tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.Name))
            .GroupBy(task => task.Name.Trim(), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (!string.IsNullOrWhiteSpace(duplicateTaskName))
            throw new InvalidDataException($"任务名称“{duplicateTaskName}”重复，请先修改后再保存。" );

        var taskArray = new JArray();
        var usedOptions = new HashSet<EditableOption>();
        var resourceNodeIndex = BuildResourceNodeIndex(document);
        foreach (var task in document.Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Name))
                throw new InvalidDataException("存在未填写名称的任务。" );
            if (string.IsNullOrWhiteSpace(task.Entry))
                throw new InvalidDataException($"任务“{task.Name}”没有设置 entry。" );
            if (task.Resources.Count == 0)
                throw new InvalidDataException($"任务“{task.Name}”至少需要选择一个适用资源。" );
            ValidateTaskEntry(task, resourceNodeIndex);

            foreach (var option in task.Options)
                usedOptions.Add(option);

            var raw = task.Raw;
            raw["name"] = task.Name.Trim();
            raw["entry"] = task.Entry.Trim();
            raw["default_check"] = task.DefaultCheck;
            if (raw.Property("doc") != null && raw.Property("description") == null)
                SetOptionalString(raw, "doc", task.Description);
            else
                SetOptionalString(raw, "description", task.Description);
            SetStringList(raw, "resource", task.Resources);
            SetStringList(raw, "option", task.Options.Select(option => option.Name));
            taskArray.Add(raw);
        }
        document.Root["task"] = taskArray;
        var optionObject = new JObject();
        document.Root["option"] = optionObject;
        foreach (var option in usedOptions)
        {
            if (string.IsNullOrWhiteSpace(option.Name))
                throw new InvalidDataException("存在未填写名称的任务设置。" );

            if (!string.IsNullOrWhiteSpace(option.DefaultCase)
                && !option.Cases.Any(optionCase => string.Equals(optionCase.Name, option.DefaultCase, StringComparison.Ordinal)))
                throw new InvalidDataException($"设置“{option.Name}”的默认选项“{option.DefaultCase}”不存在于 cases 中。" );

            var raw = option.Raw;
            // 任务管理当前编辑的是单选 cases；显式写入 type，避免 MPE Project Interface Schema
            // 将只有两个 cases 的旧配置同时判定为 select 和 switch。
            raw["type"] = "select";
            SetOptionalString(raw, "description", option.Description);
            SetOptionalString(raw, "default_case", option.DefaultCase);
            var cases = new JArray();
            foreach (var optionCase in option.Cases)
            {
                if (string.IsNullOrWhiteSpace(optionCase.Name))
                    throw new InvalidDataException($"设置“{option.Name}”存在未命名选项。" );
                var caseRaw = optionCase.Raw;
                caseRaw["name"] = optionCase.Name.Trim();
                SetOptionalString(caseRaw, "icon", optionCase.Icon);
                caseRaw["pipeline_override"] = ParseOverride(optionCase.PipelineOverrideJson);
                cases.Add(caseRaw);
            }
            raw["cases"] = cases;
            optionObject[option.Name.Trim()] = raw;
        }

        var content = document.Root.ToString(Formatting.Indented) + Environment.NewLine;
        var backupPath = document.InterfacePath + ".bak";
        var tempPath = document.InterfacePath + ".tmp";
        File.Copy(document.InterfacePath, backupPath, true);
        File.WriteAllText(tempPath, content, new UTF8Encoding(false));
        File.Move(tempPath, document.InterfacePath, true);
    }

    private static Dictionary<string, HashSet<string>> BuildResourceNodeIndex(InterfaceEditorDocument document)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (document.Root["resource"] is not JArray resources)
            return result;

        foreach (var resource in resources.OfType<JObject>())
        {
            var name = (string?)resource["name"];
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var nodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var relativePath in ReadStringList(resource["path"]))
            {
                var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
                var pipelineDir = Path.Combine(document.ProjectRoot, normalized, "pipeline");
                if (!Directory.Exists(pipelineDir))
                    continue;

                var files = Directory.EnumerateFiles(pipelineDir, "*.json", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(pipelineDir, "*.jsonc", SearchOption.AllDirectories));
                foreach (var file in files)
                {
                    try
                    {
                        var root = JObject.Parse(File.ReadAllText(file, Encoding.UTF8));
                        foreach (var property in root.Properties())
                            nodes.Add(property.Name);
                    }
                    catch
                    {
                        // 单个未完成的 Pipeline 不阻止编辑其它任务；运行时仍会由 MaaFW 给出解析错误。
                    }
                }
            }
            result[name] = nodes;
        }
        return result;
    }

    private static void ValidateTaskEntry(
        EditableTask task,
        IReadOnlyDictionary<string, HashSet<string>> resourceNodeIndex)
    {
        var entry = task.Entry.Trim();
        foreach (var resource in task.Resources)
        {
            if (!resourceNodeIndex.TryGetValue(resource, out var nodes))
                throw new InvalidDataException($"任务“{task.Name}”引用了不存在的资源“{resource}”。" );
            if (!nodes.Contains(entry))
                throw new InvalidDataException(
                    $"任务“{task.Name}”的 entry“{entry}”在资源“{resource}”中不存在。请先保存对应 Pipeline，或调整适用资源。" );
        }
    }

    private static EditableOptionCase ReadCase(JObject raw)
    {
        return new EditableOptionCase
        {
            Raw = raw,
            Name = (string?)raw["name"] ?? string.Empty,
            Icon = (string?)raw["icon"] ?? string.Empty,
            PipelineOverrideJson = raw["pipeline_override"]?.ToString(Formatting.Indented) ?? "{}"
        };
    }
    private static JObject ParseOverride(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new JObject();
        var token = JToken.Parse(json);
        if (token is not JObject result)
            throw new InvalidDataException("pipeline_override 必须是 JSON 对象。" );
        return result;
    }

    private static List<string> ReadStringList(JToken? token)
    {
        return token switch
        {
            null => [],
            JValue { Type: JTokenType.String } value =>
                string.IsNullOrWhiteSpace(value.Value<string>()) ? [] : [value.Value<string>()!],
            JArray array => array.Values<string>()
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToList(),
            _ => []
        };
    }

    private static void SetOptionalString(JObject target, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            target.Property(name)?.Remove();
        else
            target[name] = value.Trim();
    }
    private static void SetStringList(JObject target, string name, IEnumerable<string>? values)
    {
        var normalized = values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];
        if (normalized.Count == 0)
        {
            target.Property(name)?.Remove();
            return;
        }
        target[name] = new JArray(normalized);
    }
}
