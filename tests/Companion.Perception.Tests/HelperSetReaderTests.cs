using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Perception;
using Companion.Contracts.Vision;
using Companion.Perception.Internal;
using Companion.Vision;

namespace Companion.Perception.Tests;

/// <summary>
/// The gathering target picker's "Stock Limit: a/b" line (2026-10-15, the crafting feature's readers.json) on the
/// full-scale capture tests\TestData\images\gather_set_full.png (Mushroom 353/500), with the feature's templates and the
/// real Windows OCR: owned 353, limit 500. Read-only.
/// </summary>
[TestClass]
public sealed class HelperSetReaderTests
{
    private static readonly Lazy<VisionEngine> Vision = new(() => new VisionEngine(LoadTemplates()));

    public TestContext TestContext { get; set; } = null!;

    private static string CraftingData => Path.Combine(Support.CompanionRoot, "features", "Companion.Feature.Crafting", "data");

    private static TemplateCatalog LoadTemplates()
    {
        var c = new TemplateCatalog();
        foreach (var dir in new[] { Path.Combine(Support.CompanionRoot, "templates"), CraftingData })
        {
            foreach (var d in JsonSerializer.Deserialize<TemplateDefinition[]>(File.ReadAllText(Path.Combine(dir, DataFileNames.Templates)), ContractJson.Options)!)
            {
                c.Add(d, File.ReadAllBytes(Path.Combine(dir, d.ImageFile)), d.MaskFile is null ? null : File.ReadAllBytes(Path.Combine(dir, d.MaskFile)), "test");
            }
        }

        c.Seal();
        return c;
    }

    private static ReaderDefinition Reader(string id) =>
        JsonSerializer.Deserialize<ReaderDefinition[]>(File.ReadAllText(Path.Combine(CraftingData, DataFileNames.Readers)), ContractJson.Options)!.Single(r => r.Id == id);

    [TestMethod]
    [DataRow("crafting.gather.set", "UI_LIFESKILL_AUTO_COLLECT_SET")]
    [DataRow("crafting.gather.set.silent", "UI_LIFESKILL_AUTO_COLLECT")]
    public void TheStockLimitLine_ReadsOwnedAndLimit(string reader, string top)
    {
        if (!MakeTabReaderTests.Ocr.IsAvailable)
        {
            Assert.Inconclusive("Windows OCR for en-US is not installed on this PC");
        }

        var r = new ScreenReader(Vision.Value, MakeTabReaderTests.Ocr, new DigitTemplateReader()).Read(Reader(reader), BuiltInFontTests.Capture("gather_set_full.png"), Support.Ui(top));
        TestContext.WriteLine(string.Join("; ", r.Values.Select(v => $"{v.RegionId} {v.Status} '{v.Text}' {v.DetailCode}")));
        var limit = r.Values.Single(v => v.RegionId == "limit");
        Assert.AreEqual(ReadStatus.Ok, limit.Status, limit.DetailCode);
        StringAssert.Contains(limit.Text!.Replace(" ", string.Empty, StringComparison.Ordinal), "353/500", limit.Text);
    }

    [TestMethod]
    [DataRow("gather_set_full.png", 147L, true)]
    [DataRow("gather_set_lv60_full.png", 20L, false)]
    public void TheQuantityBox_ReadsTheFreeRoom_AndALongDescriptionHidesTheLimitLine(string file, long room, bool limitShown)
    {
        if (!MakeTabReaderTests.Ocr.IsAvailable)
        {
            Assert.Inconclusive("Windows OCR for en-US is not installed on this PC");
        }

        var r = new ScreenReader(Vision.Value, MakeTabReaderTests.Ocr, new DigitTemplateReader()).Read(Reader("crafting.gather.set"), BuiltInFontTests.Capture(file), Support.Ui("UI_LIFESKILL_AUTO_COLLECT_SET"));
        TestContext.WriteLine(string.Join("; ", r.Values.Select(v => $"{v.RegionId} {v.Status} '{v.Text}' {v.Value} {v.DetailCode}")));
        var value = r.Values.Single(v => v.RegionId == "room");
        Assert.AreEqual((ReadStatus.Ok, room.ToString(System.Globalization.CultureInfo.InvariantCulture)), (value.Status, value.Text?.Trim()), value.DetailCode);
        Assert.AreEqual(limitShown, r.Values.Single(v => v.RegionId == "limit").Status == ReadStatus.Ok);
    }

    [TestMethod]
    [DataRow("gather_queued.png", "0/2", null)]
    [DataRow("gather_paused_full.png", "200/200", "0/18")]
    public void TheQueueSlots_ReadWhatTheGameAccepted(string file, string slot1, string? slot2)
    {
        if (!MakeTabReaderTests.Ocr.IsAvailable)
        {
            Assert.Inconclusive("Windows OCR for en-US is not installed on this PC");
        }

        var r = new ScreenReader(Vision.Value, MakeTabReaderTests.Ocr, new DigitTemplateReader()).Read(Reader("crafting.gather.queue"), BuiltInFontTests.Capture(file), Support.Ui("UI_LIFESKILL_AUTO_COLLECT"));
        TestContext.WriteLine(string.Join("; ", r.Values.Select(v => $"{v.RegionId} {v.Status} '{v.Text}' {v.DetailCode}")));
        string? Text(string id) => r.Values.Single(v => v.RegionId == id) is { Status: ReadStatus.Ok, Text: { Length: > 0 } t } ? t.Replace(" ", string.Empty, StringComparison.Ordinal) : null;
        Assert.AreEqual(slot1, Text("slot1"));
        Assert.AreEqual(slot2, Text("slot2"));
        Assert.IsNull(Text("slot4"), "an empty slot reads nothing");
    }
}
