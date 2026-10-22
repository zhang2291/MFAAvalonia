using MFAAvalonia.Helper;
using Newtonsoft.Json.Linq;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var root = Path.Combine(Path.GetTempPath(), "inventory-contract-tests");
var data = new JObject {
    ["version"] = 1, ["status"] = "partial", ["captured_at"] = "2026-10-23T01:00:00+08:00",
    ["currencies"] = new JObject { ["异方晶"] = null, ["狄斯币"] = 0 },
    ["categories"] = new JObject {
        ["道具"] = new JObject {
            ["status"] = "complete", ["reason"] = "已到列表底部",
            ["items"] = new JObject {
                ["ticket"] = new JObject { ["id"] = "ticket", ["name"] = "搜索令", ["quantity"] = 17 },
                ["conflict"] = new JObject { ["name"] = "材料|测试", ["quantity"] = 8, ["conflicting_quantities"] = new JArray(8, JValue.CreateNull(), 9) }
            },
            ["unknown"] = new JObject { ["crop"] = new JObject { ["quantity"] = null, ["page"] = 0, ["image"] = "../../outside.png" } }
        }
    }
};
var catalog = """{"items":[{"id":"ticket","template":"仓库/ticket.png"}]}""";
var snapshot = InventorySnapshot.Parse(data.ToString(), root, catalog);
Check(!snapshot.Complete, "Partial snapshots must remain partial.");
Check(snapshot.Currencies.Single(x => x.Name == "异方晶").Quantity == "未识别", "Missing quantity was changed to zero.");
Check(snapshot.Currencies.Single(x => x.Name == "狄斯币").Quantity == "0", "Actual zero was changed to unknown.");
Check(snapshot.Items.Single(x => x.Id == "ticket").Quantity == "17", "Ticket count lost.");
Check(snapshot.Items.Single(x => x.Id == "ticket").ImagePath.EndsWith("ticket.png"), "Catalog icon not resolved.");
var conflict = snapshot.Items.Single(x => x.Id == "conflict");
Check(conflict.Conflict && conflict.Quantity == "8 / 未识别 / 9", "Conflicting null count not preserved.");
var unknown = snapshot.Items.Single(x => x.Unknown);
Check(unknown.Quantity == "未识别" && unknown.ImagePath == "" && unknown.Note.StartsWith("第 1 页"), "Unknown item or image boundary incorrect.");
Check(snapshot.Json == data.ToString(), "JSON export did not preserve snapshot.");
var markdown = snapshot.ToMarkdown();
Check(markdown.Contains("材料\\|测试") && markdown.Contains("8 / 未识别 / 9") && markdown.Contains("未识别物品"), "Markdown lost conflicts or unknown rows.");
Check(markdown.Contains("未识别不代表库存为零"), "Partial warning missing.");
try { InventorySnapshot.Parse("{\"version\":2}", root, null); throw new Exception("Unknown schema accepted."); }
catch (InvalidDataException) { }
Check(snapshot.CategoryNotes.Contains("消耗品：本轮尚未识别"), "Missing category not explained.");
var withoutIcons = InventorySnapshot.Parse(data.ToString(), root, "{broken-json");
Check(withoutIcons.Items.Count == snapshot.Items.Count && withoutIcons.Items.Single(x => x.Id == "ticket").Quantity == "17"
    && withoutIcons.Items.Single(x => x.Id == "ticket").ImagePath == "", "Broken icon catalog hid saved counts.");
var negativeData = (JObject)data.DeepClone();
negativeData["currencies"]!["狄斯币"] = -1;
negativeData["categories"]!["道具"]!["items"]!["ticket"]!["quantity"] = -17;
var negative = InventorySnapshot.Parse(negativeData.ToString(), root, null);
Check(negative.Currencies.Single(x => x.Name == "狄斯币").Quantity == "未识别", "Negative currency accepted.");
Check(negative.Items.Single(x => x.Id == "ticket").Quantity == "未识别"
    && negative.Items.Single(x => x.Id == "ticket").Note == "数量未识别", "Negative item accepted.");
Console.WriteLine("PASS: partial/zero/null/negative/conflict/unknown/path/JSON/Markdown/missing-category/broken-catalog contracts.");

if (args.Length > 0)
{
    var realRoot = Path.GetFullPath(args[0]);
    var real = InventorySnapshot.Parse(File.ReadAllText(Path.Combine(realRoot, "data/inventory/latest.json")), realRoot,
        File.ReadAllText(Path.Combine(realRoot, "resource/base/image/仓库/catalog.json")));
    Check(real.Items.Count > 0 && real.Currencies.Count > 0, "Real snapshot parsed empty.");
    foreach (var name in InventorySnapshot.TicketNames)
        Check(real.Items.Any(x => x.Name == name), "Real snapshot missing ticket: " + name);
    Check(real.Items.Where(x => !string.IsNullOrEmpty(x.ImagePath)).All(x => File.Exists(x.ImagePath)), "Real snapshot references missing image.");
    Console.WriteLine($"PASS: actual snapshot has {real.Items.Count(x => !x.Unknown)} known, {real.Items.Count(x => x.Unknown)} unknown; "
        + string.Join(", ", real.Items.Where(x => InventorySnapshot.TicketNames.Contains(x.Name)).Select(x => $"{x.Name}={x.Quantity}")));
}
