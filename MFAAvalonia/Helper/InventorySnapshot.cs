using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MFAAvalonia.Helper;

/// <summary>Read-only view of one warehouse scan. Missing/conflicting counts are never treated as zero.</summary>
public sealed class InventorySnapshot
{
    public sealed class Item
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Category { get; init; } = "";
        public string Quantity { get; init; } = "未识别";
        public string Note { get; init; } = "";
        public string ImagePath { get; init; } = "";
        public bool Unknown { get; init; }
        public bool Conflict { get; init; }
        // Only exact, non-conflicting counts may participate in arithmetic.
        public long? NumericQuantity { get; init; }
    }

    public string Json { get; private init; } = "";
    public string CapturedAt { get; private init; } = "";
    public bool Complete { get; private init; }
    public List<Item> Items { get; } = new();
    public List<Item> Currencies { get; } = new();
    public List<string> CategoryNotes { get; } = new();
    public static readonly string[] TicketNames = ["搜索令", "都市搜索令"];

    public static InventorySnapshot Parse(string json, string dataRoot, string? catalogJson)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        var data = JObject.Load(reader);
        if (data["version"]?.Type != JTokenType.Integer || data.Value<int?>("version") != 1 || data["categories"] is not JObject categories
            || data["currencies"] is not JObject currencies)
            throw new InvalidDataException("仓库记录格式不支持，请重新采集。");
        var snapshot = new InventorySnapshot {
            Json = json, CapturedAt = data.Value<string>("captured_at") ?? "未知时间",
            Complete = data.Value<string>("status") == "complete"
                && categories["道具"] is JObject && categories["消耗品"] is JObject
        };
        var imageRoot = Path.Combine(dataRoot, "resource", "base", "image");
        var inventoryRoot = Path.Combine(dataRoot, "data", "inventory", "captures");
        var catalog = new Dictionary<string, string>();
        // Icons are optional; an incomplete resource update must not hide saved inventory counts.
        if (!string.IsNullOrWhiteSpace(catalogJson))
        {
            try
            {
                foreach (var entry in (JObject.Parse(catalogJson)["items"] as JArray)?.OfType<JObject>() ?? [])
                    if (entry["id"]?.Type == JTokenType.String && entry["template"]?.Type == JTokenType.String)
                        catalog[entry.Value<string>("id")!] = SafePath(imageRoot, entry.Value<string>("template"));
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
            {
                catalog.Clear();
            }
        }
        foreach (var category in new[] { "道具", "消耗品" })
            if (categories[category] is not JObject)
                snapshot.CategoryNotes.Add($"{(category == "道具" ? "材料" : category)}：本轮尚未识别");
        foreach (var currency in currencies.Properties())
            snapshot.Currencies.Add(new Item { Id = currency.Name, Name = currency.Name, Category = "货币",
                Quantity = Count(currency.Value), NumericQuantity = ExactCount(currency.Value) });
        foreach (var category in categories.Properties())
        {
            if (category.Value is not JObject body) continue;
            var label = category.Name == "道具" ? "材料" : category.Name;
            snapshot.CategoryNotes.Add($"{label}：{(body.Value<string>("status") == "complete" ? "完整" : "部分识别")} · {body.Value<string>("reason") ?? "未记录原因"}");
            foreach (var property in (body["items"] as JObject)?.Properties() ?? [])
            {
                if (property.Value is not JObject item)
                    throw new InvalidDataException("仓库物品记录格式错误，请检查快照文件或重新采集。");
                var conflicts = item["conflicting_quantities"] as JArray;
                var conflict = conflicts?.Count > 1;
                var id = item.Value<string>("id") ?? property.Name;
                snapshot.Items.Add(new Item {
                    Id = id, Name = item.Value<string>("name") ?? id, Category = label,
                    Quantity = conflict ? string.Join(" / ", conflicts!.Select(Count)) : Count(item["quantity"]),
                    Conflict = conflict, Note = conflict ? "读数冲突，需复核" : Count(item["quantity"]) == "未识别" ? "数量未识别" : "",
                    NumericQuantity = conflict ? null : ExactCount(item["quantity"]),
                    ImagePath = catalog.GetValueOrDefault(id, "")
                });
            }
            foreach (var property in (body["unknown"] as JObject)?.Properties() ?? [])
            {
                if (property.Value is not JObject unknown)
                    throw new InvalidDataException("仓库未识别物品记录格式错误，请检查快照文件或重新采集。");
                snapshot.Items.Add(new Item {
                    Id = property.Name, Name = "未识别物品", Category = label, Unknown = true,
                    Quantity = Count(unknown["quantity"]), Note = $"第 {(unknown.Value<int?>("page") ?? 0) + 1} 页 · 等待补充图标",
                    ImagePath = SafePath(inventoryRoot, unknown.Value<string>("image"))
                });
            }
        }
        return snapshot;
    }

    private static long? ExactCount(JToken? value) => value?.Type == JTokenType.Integer
        && long.TryParse(value.ToString(), out var count) && count >= 0 ? count : null;

    private static string Count(JToken? value) => ExactCount(value)?.ToString() ?? "未识别";

    // Catalog/unknown-image paths may only read images from their respective resource/data folder.
    private static string SafePath(string root, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return "";
        var path = Path.GetFullPath(Path.Combine(root, candidate));
        return path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? path : "";
    }

    public string ToMarkdown()
    {
        static string Escape(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        var text = new StringBuilder($"# 仓库资源汇总\n\n采集时间：{CapturedAt}\n\n状态：{(Complete ? "完整" : "部分识别；未识别不代表库存为零")}\n\n");
        foreach (var item in Currencies) text.AppendLine($"- {Escape(item.Name)}：{item.Quantity}");
        foreach (var note in CategoryNotes) text.AppendLine($"\n{Escape(note)}");
        text.AppendLine("\n| 分类 | 物品 | 数量 | 备注 |\n|---|---|---:|---|");
        foreach (var item in Items) text.AppendLine($"| {Escape(item.Category)} | {Escape(item.Name)} | {item.Quantity} | {Escape(item.Note)} |");
        return text.ToString();
    }
}

/// <summary>Explicit comparison: an absent item is unknown, even in a complete scan.</summary>
public sealed class InventoryComparison
{
    public sealed class Entry
    {
        public InventorySnapshot.Item Item { get; init; } = new();
        public string Before { get; init; } = "未记录";
        public string Current { get; init; } = "未记录";
        public long? Difference { get; init; }
        public string Reason { get; init; } = "";
        public bool Changed => Difference.HasValue && Difference.Value != 0;
        public string Change => Difference.HasValue
            ? Difference.Value > 0 ? $"+{Difference.Value}" : Difference.Value == 0 ? "无变化" : Difference.Value.ToString()
            : "无法比较";
    }

    public List<Entry> Entries { get; } = new();
    public string Notice { get; private init; } = "";

    public static InventoryComparison Compare(InventorySnapshot before, InventorySnapshot current)
    {
        var result = new InventoryComparison {
            Notice = before.Complete && current.Complete
                ? "仅两次都有明确数量的物品计算差值；未记录、未识别和冲突项无法比较。"
                : "包含部分识别快照：仅两次都有明确数量的物品计算差值，缺失及未知项无法比较，不代表库存为零。"
        };
        // Unknown crops have no stable identity and must never be paired across scans.
        static string Key(InventorySnapshot.Item item) => item.Category + "\0" + (item.Category == "货币" ? item.Name : item.Id);
        var oldItems = before.Currencies.Concat(before.Items).Where(item => !item.Unknown).GroupBy(Key).ToDictionary(g => g.Key, g => g.ToList());
        var newItems = current.Currencies.Concat(current.Items).Where(item => !item.Unknown).GroupBy(Key).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var key in newItems.Keys.Union(oldItems.Keys))
        {
            var oldGroup = oldItems.GetValueOrDefault(key);
            var newGroup = newItems.GetValueOrDefault(key);
            var oldItem = oldGroup?.FirstOrDefault();
            var newItem = newGroup?.FirstOrDefault();
            var reason = oldItem == null ? "历史未记录，无法比较" : newItem == null ? "当前未记录，无法比较"
                : oldGroup!.Count != 1 || newGroup!.Count != 1 ? "物品标识重复，无法比较"
                : oldItem.Conflict || newItem.Conflict ? "数量有冲突，无法比较"
                : !oldItem.NumericQuantity.HasValue || !newItem.NumericQuantity.HasValue ? "数量未识别，无法比较" : "";
            result.Entries.Add(new Entry {
                Item = newItem ?? oldItem!, Before = oldItem?.Quantity ?? "未记录", Current = newItem?.Quantity ?? "未记录",
                Difference = reason.Length == 0 ? newItem!.NumericQuantity!.Value - oldItem!.NumericQuantity!.Value : null,
                Reason = reason
            });
        }
        foreach (var item in current.Items.Where(item => item.Unknown))
            result.Entries.Add(new Entry { Item = item, Current = item.Quantity, Reason = "当前未识别物品，无法匹配历史" });
        foreach (var item in before.Items.Where(item => item.Unknown))
            result.Entries.Add(new Entry { Item = item, Before = item.Quantity, Reason = "历史未识别物品，无法匹配当前" });
        return result;
    }
}

/// <summary>History is selected only from the fixed inventory/history directory, never an arbitrary path.</summary>
public static class InventoryHistory
{
    public sealed class Entry
    {
        public string FileName { get; init; } = "";
        public string Label { get; init; } = "";
        public string DisplayText => string.IsNullOrWhiteSpace(Label) ? FileName : $"{Label} · {FileName}";
        public override string ToString() => DisplayText;
    }

    private static string InventoryRoot(string dataRoot) => Path.GetFullPath(Path.Combine(dataRoot, "data", "inventory"));
    private static string HistoryRoot(string dataRoot) => Path.GetFullPath(Path.Combine(InventoryRoot(dataRoot), "history"));
    private static string MetaPath(string dataRoot) => Path.GetFullPath(Path.Combine(InventoryRoot(dataRoot), "history-meta.json"));

    public static List<string> List(string dataRoot)
    {
        var root = HistoryRoot(dataRoot);
        if (!Directory.Exists(root)) return new();
        EnsureRegular(root);
        return Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .Select(Path.GetFileName).OfType<string>().OrderByDescending(name => name, StringComparer.Ordinal).ToList();
    }

    public static List<Entry> ListEntries(string dataRoot)
    {
        var labels = LoadLabels(dataRoot);
        return List(dataRoot).Select(fileName => new Entry
        {
            FileName = fileName,
            Label = labels.GetValueOrDefault(fileName, ""),
        }).ToList();
    }

    public static Dictionary<string, string> LoadLabels(string dataRoot)
    {
        var meta = MetaPath(dataRoot);
        if (!File.Exists(meta)) return new();
        try
        {
            EnsureRegular(meta);
            var obj = JObject.Parse(File.ReadAllText(meta, Encoding.UTF8));
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in obj.Properties())
            {
                if (prop.Value.Type != JTokenType.String) continue;
                var text = prop.Value.Value<string>()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(text) && text.Length <= 40 && text.All(ch => ch >= 32))
                    result[prop.Name] = text;
            }
            return result;
        }
        catch
        {
            return new();
        }
    }

    public static string Rename(string dataRoot, string fileName, string? label)
    {
        Resolve(dataRoot, fileName);
        var normalized = (label ?? "").Trim();
        if (normalized.Length > 40 || normalized.Any(ch => ch < 32))
            throw new InvalidDataException("快照名称须在 40 个可见字符以内。");
        var labels = LoadLabels(dataRoot);
        if (string.IsNullOrEmpty(normalized))
            labels.Remove(fileName);
        else
            labels[fileName] = normalized;
        SaveLabels(dataRoot, labels);
        return normalized;
    }

    public static void Delete(string dataRoot, string fileName)
    {
        var path = Resolve(dataRoot, fileName);
        File.Delete(path);
        var labels = LoadLabels(dataRoot);
        if (labels.Remove(fileName))
        {
            try { SaveLabels(dataRoot, labels); }
            catch { }
        }
    }

    private static void SaveLabels(string dataRoot, Dictionary<string, string> labels)
    {
        var invRoot = InventoryRoot(dataRoot);
        Directory.CreateDirectory(invRoot);
        EnsureRegular(invRoot);
        var meta = MetaPath(dataRoot);
        if (File.Exists(meta)) EnsureRegular(meta);
        var json = JsonConvert.SerializeObject(new SortedDictionary<string, string>(labels, StringComparer.Ordinal), Formatting.None);
        var temp = Path.Combine(invRoot, $".history-meta-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, meta, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static string Resolve(string dataRoot, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName)
            || fileName.IndexOfAny(['/', '\\', ':']) >= 0 || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("历史快照文件名无效，请重新选择。");
        var root = HistoryRoot(dataRoot);
        EnsureRegular(root);
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("历史快照路径无效，请重新选择。");
        EnsureRegular(path);
        return path;
    }

    private static void EnsureRegular(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("历史快照目录或文件为链接，无法读取。");
    }
}
