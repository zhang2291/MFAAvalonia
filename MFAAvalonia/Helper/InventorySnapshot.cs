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
        if (data.Value<int?>("version") != 1 || data["categories"] is not JObject categories
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
            snapshot.Currencies.Add(new Item { Name = currency.Name, Category = "货币", Quantity = Count(currency.Value) });
        foreach (var category in categories.Properties())
        {
            if (category.Value is not JObject body) continue;
            var label = category.Name == "道具" ? "材料" : category.Name;
            snapshot.CategoryNotes.Add($"{label}：{(body.Value<string>("status") == "complete" ? "完整" : "部分识别")} · {body.Value<string>("reason") ?? "未记录原因"}");
            foreach (var property in (body["items"] as JObject)?.Properties() ?? [])
            {
                var item = property.Value;
                var conflicts = item["conflicting_quantities"] as JArray;
                var conflict = conflicts?.Count > 1;
                var id = item.Value<string>("id") ?? property.Name;
                snapshot.Items.Add(new Item {
                    Id = id, Name = item.Value<string>("name") ?? id, Category = label,
                    Quantity = conflict ? string.Join(" / ", conflicts!.Select(Count)) : Count(item["quantity"]),
                    Conflict = conflict, Note = conflict ? "读数冲突，需复核" : Count(item["quantity"]) == "未识别" ? "数量未识别" : "",
                    ImagePath = catalog.GetValueOrDefault(id, "")
                });
            }
            foreach (var property in (body["unknown"] as JObject)?.Properties() ?? [])
                snapshot.Items.Add(new Item {
                    Id = property.Name, Name = "未识别物品", Category = label, Unknown = true,
                    Quantity = Count(property.Value["quantity"]), Note = $"第 {(property.Value.Value<int?>("page") ?? 0) + 1} 页 · 等待补充图标",
                    ImagePath = SafePath(inventoryRoot, property.Value.Value<string>("image"))
                });
        }
        return snapshot;
    }

    private static string Count(JToken? value) => value?.Type == JTokenType.Integer
        && long.TryParse(value.ToString(), out var count) && count >= 0 ? count.ToString() : "未识别";

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
