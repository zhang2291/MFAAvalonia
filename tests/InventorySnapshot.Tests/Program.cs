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

// Changes require exact quantities on both sides; even complete snapshots do not imply missing = zero.
var laterData = (JObject)data.DeepClone();
laterData["captured_at"] = "2026-10-24T01:00:00+08:00";
laterData["currencies"]!["异方晶"] = 180;
laterData["currencies"]!["狄斯币"] = 5;
var laterItems = (JObject)laterData["categories"]!["道具"]!["items"]!;
laterItems["ticket"]!["quantity"] = 12;
laterItems["red-ticket"] = new JObject { ["id"] = "red-ticket", ["name"] = "都市搜索令", ["quantity"] = 3 };
laterItems["new-zero"] = new JObject { ["name"] = "新增零库存", ["quantity"] = 0 };
var later = InventorySnapshot.Parse(laterData.ToString(), root, null);
var comparison = InventoryComparison.Compare(snapshot, later);
Check(comparison.Entries.Single(x => x.Item.Name == "搜索令").Difference == -5, "Blue ticket decrement missing.");
Check(comparison.Entries.Single(x => x.Item.Name == "狄斯币").Difference == 5, "Currency increment missing.");
Check(comparison.Entries.Single(x => x.Item.Name == "都市搜索令").Difference == null, "Missing red ticket treated as zero.");
Check(comparison.Entries.Single(x => x.Item.Name == "异方晶").Difference == null, "Null currency treated as zero.");
Check(comparison.Entries.Single(x => x.Item.Id == "new-zero").Difference == null, "Absent item treated as zero.");
Check(comparison.Entries.Single(x => x.Item.Id == "conflict").Difference == null, "Conflicting quantity compared numerically.");
Check(comparison.Entries.Count(x => x.Item.Unknown) == 2 && comparison.Entries.Where(x => x.Item.Unknown).All(x => x.Difference == null),
    "Unknown crops were matched across scans.");
Check(comparison.Notice.Contains("部分识别") && comparison.Notice.Contains("无法比较"), "Partial comparison warning missing.");
Check(comparison.Entries.Count(x => x.Changed) == 2, "Changed filter includes unconfirmed changes.");
var identical = InventoryComparison.Compare(later, later);
Check(identical.Entries.Where(x => !x.Item.Unknown && !x.Item.Conflict).All(x => x.Difference == 0), "Identical exact quantities changed.");
Check(identical.Entries.All(x => !x.Changed), "Identical snapshot has changed rows.");
var reverse = InventoryComparison.Compare(later, snapshot);
Check(reverse.Entries.Single(x => x.Item.Id == "new-zero").Difference == null, "Missing current item became a negative delta.");
var bothTickets = (JObject)laterData.DeepClone();
bothTickets["categories"]!["道具"]!["items"]!["red-ticket"]!["quantity"] = 8;
var ticketChanges = InventoryComparison.Compare(later, InventorySnapshot.Parse(bothTickets.ToString(), root, null));
Check(ticketChanges.Entries.Single(x => x.Item.Name == "都市搜索令").Difference == 5, "Red ticket change missing.");
var categoryCollision = (JObject)laterData.DeepClone();
categoryCollision["categories"]!["消耗品"] = new JObject { ["status"] = "complete", ["items"] = new JObject {
    ["ticket"] = new JObject { ["id"] = "ticket", ["name"] = "同ID消耗品", ["quantity"] = 100 }
} };
Check(InventoryComparison.Compare(later, InventorySnapshot.Parse(categoryCollision.ToString(), root, null)).Entries
    .Single(x => x.Item.Name == "同ID消耗品").Difference == null, "Same id in another category matched.");
var invalidCounts = InventoryComparison.Compare(snapshot, negative);
Check(invalidCounts.Entries.Single(x => x.Item.Id == "ticket").Difference == null, "Negative count compared numerically.");

var historyRoot = Path.Combine(Path.GetTempPath(), "inventory-history-" + Guid.NewGuid().ToString("N"));
var historyFolder = Path.Combine(historyRoot, "data", "inventory", "history");
try
{
    Check(InventoryHistory.List(historyRoot).Count == 0, "Missing history should be empty.");
    Directory.CreateDirectory(historyFolder);
    File.WriteAllText(Path.Combine(historyFolder, "20261023.json"), data.ToString());
    File.WriteAllText(Path.Combine(historyFolder, "20261024.json"), laterData.ToString());
    File.WriteAllText(Path.Combine(historyFolder, "notes.txt"), "not a snapshot");
    Directory.CreateDirectory(Path.Combine(historyFolder, "nested"));
    File.WriteAllText(Path.Combine(historyFolder, "nested", "nested.json"), "{}");
    Check(InventoryHistory.List(historyRoot).SequenceEqual(new[] { "20261024.json", "20261023.json" }), "History ordering or folder boundary incorrect.");
    Check(InventoryHistory.Resolve(historyRoot, "20261023.json") == Path.Combine(historyFolder, "20261023.json"), "Valid history path rejected.");
    foreach (var invalidName in new[] { "../latest.json", "..\\latest.json", "C:\\outside.json", "nested/nested.json", "notes.txt", "20261023.json:stream", "" })
    {
        try { InventoryHistory.Resolve(historyRoot, invalidName); throw new Exception("Unsafe history accepted: " + invalidName); }
        catch (InvalidDataException) { }
    }
}
finally
{
    // This dedicated temporary folder was created by this test only.
    if (Directory.Exists(historyRoot)) Directory.Delete(historyRoot, true);
}
Console.WriteLine("PASS: manual comparison currency/tickets/decrease/zero/null/missing/conflict/unknown/category identity and history path contracts.");

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
    foreach (var fileName in InventoryHistory.List(realRoot))
    {
        var history = InventorySnapshot.Parse(File.ReadAllText(InventoryHistory.Resolve(realRoot, fileName)), realRoot, null);
        var actualComparison = InventoryComparison.Compare(history, real);
        Check(actualComparison.Entries.Where(x => x.Item.Unknown || x.Item.Conflict).All(x => x.Difference == null),
            "Real conflict or unknown became an exact change.");
        Console.WriteLine($"PASS: real history {fileName}: {actualComparison.Entries.Count(x => x.Changed)} changed, {actualComparison.Entries.Count(x => x.Difference == null)} unavailable.");
    }
}
