using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Perception;
using Companion.Perception.Internal;

namespace Companion.Perception.Tests;

/// <summary>Parse rules, digit templates and the reader catalog / seed data.</summary>
[TestClass]
public sealed class ParsingAndCatalogTests
{
    [TestMethod]
    [DataRow("18/18", PairSemantics.StoredCapacity, true, 18L, 18L)]
    [DataRow("5/60", PairSemantics.AssignedSlots, true, 5L, 60L)]
    [DataRow("120/3", PairSemantics.OwnedRequired, true, 120L, 3L)]
    [DataRow("19/18", PairSemantics.StoredCapacity, false, null, null)]
    [DataRow("4/0", PairSemantics.OwnedRequired, false, null, null)]
    [DataRow("18/", PairSemantics.StoredCapacity, false, null, null)]
    [DataRow("18-18", PairSemantics.StoredCapacity, false, null, null)]
    [DataRow("18/18", PairSemantics.None, false, null, null)]
    [DataRow("5/300", PairSemantics.Progress, true, 5L, 300L)]
    [DataRow("300/300", PairSemantics.Progress, true, 300L, 300L)]
    [DataRow("301/300", PairSemantics.Progress, false, null, null)]
    [DataRow("5/0", PairSemantics.Progress, false, null, null)]
    public void Pairs(string text, PairSemantics semantics, bool ok, long? a, long? b)
    {
        var p = ValueParser.Parse(ValueParser.NormalizeNumeric(text), ParseRule.Pair, semantics);
        Assert.AreEqual(ok, p.Ok, p.Code);
        Assert.AreEqual(a, p.Value);
        Assert.AreEqual(b, p.Second);
    }

    [TestMethod]
    [DataRow("14M", 14_000_000L, 14_999_999L, null)]
    [DataRow("7146K", 7_146_000L, 7_146_999L, null)]
    [DataRow("1.5K", 1_500L, 1_599L, null)]
    [DataRow("55156", 55_156L, 55_156L, 55_156L)]
    [DataRow("2.25M", 2_250_000L, 2_259_999L, null)]
    public void Abbreviated(string text, long min, long max, long? exact)
    {
        var p = ValueParser.Parse(ValueParser.NormalizeNumeric(text), ParseRule.Abbreviated, PairSemantics.None);
        Assert.IsTrue(p.Ok, p.Code);
        Assert.AreEqual(new ValueRange(min, max), p.Range);
        Assert.AreEqual(exact, p.Value);
    }

    [TestMethod]
    public void AbbreviatedAndIntRejectNoise()
    {
        Assert.IsFalse(ValueParser.Parse("14MM", ParseRule.Abbreviated, PairSemantics.None).Ok);
        Assert.IsFalse(ValueParser.Parse("1.5", ParseRule.Abbreviated, PairSemantics.None).Ok);
        Assert.IsFalse(ValueParser.Parse("12a", ParseRule.Int, PairSemantics.None).Ok);
        Assert.AreEqual(10L, ValueParser.Parse(ValueParser.NormalizeNumeric("1O"), ParseRule.Int, PairSemantics.None).Value);
        Assert.AreEqual("1234", ValueParser.NormalizeNumeric(" 1,234 "));
    }

    [TestMethod]
    public void ItemCells()
    {
        Assert.AreEqual("cell-empty", ValueParser.Parse("", ParseRule.ItemCellCount, PairSemantics.None).Code);
        Assert.IsFalse(ValueParser.Parse("", ParseRule.ItemCellCount, PairSemantics.None).Ok);
        var lv = ValueParser.Parse("Lv65", ParseRule.ItemCellCount, PairSemantics.None);
        Assert.IsTrue(lv.Ok);
        Assert.IsNull(lv.Value);
        Assert.AreEqual("Lv65", lv.Text);
        Assert.AreEqual(697L, ValueParser.Parse("697", ParseRule.ItemCellCount, PairSemantics.None).Value);
        Assert.IsFalse(ValueParser.Parse("III17", ParseRule.ItemCellCount, PairSemantics.None).Ok);
    }

    [TestMethod]
    public void Bounds()
    {
        var p = ValueParser.Parse("18/18", ParseRule.Pair, PairSemantics.StoredCapacity);
        Assert.IsTrue(ValueParser.WithinBounds(p, 0, 99));
        Assert.IsFalse(ValueParser.WithinBounds(p, 0, 10));
        var r = ValueParser.Parse("14M", ParseRule.Abbreviated, PairSemantics.None);
        Assert.IsFalse(ValueParser.WithinBounds(r, 0, 14_500_000));
    }

    [TestMethod]
    public void DigitReader_ReadsSyntheticGlyphs()
    {
        var reader = new DigitTemplateReader(new Dictionary<string, IReadOnlyDictionary<char, Contracts.Vision.GrayImage>>
        {
            ["synthetic-dark"] = Support.SyntheticFont(),
            ["synthetic-light"] = Support.SyntheticFont(lightOnDark: true),
        });
        CollectionAssert.AreEquivalent(new[] { "synthetic-dark", "synthetic-light" }, reader.FontStyles.ToArray());
        foreach (var s in new[] { "18/18", "697", "12/12", "5/60", "14M", "7146K", "2749134" })
        {
            var r = reader.Read(Support.Render(s), "synthetic-dark");
            Assert.IsTrue(r.Ok, $"{s}: {r}");
            Assert.AreEqual(s, r.Text);
            Assert.IsTrue(r.MinGlyphScore >= 0.75, r.ToString());
            var l = reader.Read(Support.Render(s, lightOnDark: true), "synthetic-light");
            Assert.AreEqual(s, l.Text, l.ToString());
        }

        Assert.IsFalse(reader.Read(Support.Render("18/18"), "no-such-font").Ok);
        Assert.IsFalse(new DigitTemplateReader().Read(Support.Render("18/18"), "synthetic-dark").Ok);

        // the parameterless reader carries only the built-in (surveyed) fonts
        CollectionAssert.AreEquivalent(new[] { "parchment-pair", "badge-dark", "counter-dark", "stock-count-dark" }, new DigitTemplateReader().FontStyles.ToArray());
        Assert.AreEqual(0, new DigitTemplateReader(new Dictionary<string, IReadOnlyDictionary<char, Contracts.Vision.GrayImage>>()).FontStyles.Count);
    }

    [TestMethod]
    public void DigitReader_RefusesLettersItHasNoGlyphFor()
    {
        var reader = new DigitTemplateReader(new Dictionary<string, IReadOnlyDictionary<char, Contracts.Vision.GrayImage>> { ["d"] = Support.SyntheticFont() });
        var r = reader.Read(Support.Render("Lv62"), "d");
        Assert.IsFalse(r.Ok, r.ToString());
    }

    [TestMethod]
    public void SeedReadersJson_Loads_MakeTabReadersReady_OthersStillNeedSurvey()
    {
        var json = File.ReadAllText(Path.Combine(Support.CompanionRoot, "data", DataFileNames.Readers));
        var readers = JsonSerializer.Deserialize<ReaderDefinition[]>(json, ContractJson.Options)!;
        CollectionAssert.AreEquivalent(new[] { "bag.grid", "craft.list", "craft.other.tab", "craft.pairs", "craft.product", "craft.quantity", "lumi.products" }, readers.Select(r => r.Id).ToArray());
        var surveyed = new[] { "craft.pairs", "craft.product" };
        var probes = new Dictionary<string, string> { ["craft.list"] = "craft.list.anchor", ["craft.other.tab"] = "craft.make.tab" };
        var catalog = new ReaderCatalog();
        foreach (var r in readers)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(r.Notes));
            if (probes.TryGetValue(r.Id, out var anchor))
            {
                // the identity probes of the recipe list page and of another tab: no regions, one anchor on the Make view
                Assert.AreEqual((ReaderStatus.Ready, 0, "UI_LIFESKILL_MAKE_VIEW"), (r.Status, r.Regions.Count, r.Screen.LogUiName), r.Id);
                CollectionAssert.AreEqual(new[] { anchor }, r.Screen.AnchorTemplateIds.ToArray(), r.Id);
            }
            else if (surveyed.Contains(r.Id))
            {
                Assert.AreEqual(ReaderStatus.Ready, r.Status, r.Id);
                StringAssert.Contains(r.Notes, "live_ability_make_full.png", r.Id + ": measured on the full-scale capture");
            }
            else
            {
                Assert.AreEqual(ReaderStatus.NeedsSurvey, r.Status, r.Id);
                Assert.AreEqual(0, r.Regions.Count, r.Id + ": no invented coordinates");
            }

            catalog.Add(r, "core");
        }

        catalog.Seal();
        Assert.AreEqual("UI_HOME_PET_VIEW", catalog.Get("lumi.products")!.Screen.LogUiName);
        Assert.AreEqual("UI_BAG_MAIN", catalog.Get("bag.grid")!.Screen.LogUiName);
        Assert.AreEqual("UI_LIFESKILL_MAKE_VIEW", catalog.Get("craft.pairs")!.Screen.LogUiName);
        Assert.AreEqual("UI_LIFESKILL_MAKE_VIEW", catalog.Get("craft.product")!.Screen.LogUiName);
        Assert.AreEqual(7, catalog.All.Count);
        Assert.ThrowsExactly<InvalidOperationException>(() => catalog.Add(readers[0] with { Id = "late" }, "core"));
    }

    [TestMethod]
    public void ReaderCatalog_ValidatesReadyReaders()
    {
        var c = new ReaderCatalog();
        c.Add(Support.Reader("ok", ReaderStatus.Ready, "UI_X", [], Support.Region("a", ParseRule.Int)), "t");
        Assert.ThrowsExactly<ArgumentException>(() => c.Add(Support.Reader("ok", ReaderStatus.NeedsSurvey, "UI_X", []), "t"));
        Assert.ThrowsExactly<ArgumentException>(() => c.Add(Support.Reader("empty", ReaderStatus.Ready, "UI_X", []), "t"));
        Assert.ThrowsExactly<ArgumentException>(() => c.Add(Support.Reader("pair", ReaderStatus.Ready, "UI_X", [], Support.Region("a", ParseRule.Pair)), "t"));
        Assert.ThrowsExactly<ArgumentException>(() => c.Add(Support.Reader("outside", ReaderStatus.Ready, "UI_X", [], Support.Region("a", ParseRule.Int, rect: new RectI(1900, 1000, 2000, 1100))), "t"));
        Assert.ThrowsExactly<ArgumentException>(() => c.Add(Support.Reader("noid", ReaderStatus.Ready, null, [], Support.Region("a", ParseRule.Int)), "t"));
        Assert.ThrowsExactly<ArgumentException>(() => c.Add(Support.Reader("dup.region", ReaderStatus.Ready, "UI_X", [], Support.Region("a", ParseRule.Int), Support.Region("a", ParseRule.Int)), "t"));
        Assert.ThrowsExactly<ArgumentException>(() => c.Add(Support.Reader("tiny.icon", ReaderStatus.Ready, "UI_X", [], Support.Region("i", ParseRule.IconFingerprint, font: null, rect: new RectI(100, 100, 120, 120))), "t"));
        Assert.ThrowsExactly<ArgumentException>(() => c.Add(Support.Reader("bad.rule", ReaderStatus.Ready, "UI_X", [], Support.Region("i", (ParseRule)99)), "t"));
        Assert.ThrowsExactly<ArgumentException>(() => c.Add(Support.Reader("row.nosem", ReaderStatus.Ready, "UI_X", [], Support.Region("m", ParseRule.TileRow, rect: new RectI(100, 100, 700, 189))), "t"));
        Assert.ThrowsExactly<ArgumentException>(() => c.Add(Support.Reader("row.small", ReaderStatus.Ready, "UI_X", [], Support.Region("m", ParseRule.TileRow, PairSemantics.OwnedRequired, rect: new RectI(100, 100, 700, 120))), "t"));
        Assert.ThrowsExactly<ArgumentException>(() => c.Add(Support.Reader("label.nosem", ReaderStatus.Ready, "UI_X", [], Support.Region("r", ParseRule.LabeledPair)), "t"));
        Assert.ThrowsExactly<ArgumentException>(() => c.Add(Support.Reader("grid.small", ReaderStatus.Ready, "UI_X", [], Support.Region("g", ParseRule.IconGrid, rect: new RectI(100, 100, 700, 189))), "t"));
        Assert.ThrowsExactly<ArgumentException>(() => c.Add(Support.Reader("grid.nofont", ReaderStatus.Ready, "UI_X", [], Support.Region("g", ParseRule.IconGrid, font: null, rect: new RectI(300, 270, 1540, 812))), "t"));
        c.Add(Support.Reader("grid", ReaderStatus.Ready, "UI_X", [], Support.Region("g", ParseRule.IconGrid, rect: new RectI(300, 270, 1540, 812))), "t");
        c.Add(Support.Reader("row", ReaderStatus.Ready, "UI_X", [], Support.Region("m", ParseRule.TileRow, PairSemantics.OwnedRequired, rect: new RectI(100, 100, 700, 189))), "t");
        c.Add(Support.Reader("label", ReaderStatus.Ready, "UI_X", [], Support.Region("r", ParseRule.LabeledPair, PairSemantics.OwnedRequired) with { Optional = true }), "t");
        c.Add(Support.Reader("icon", ReaderStatus.Ready, "UI_X", [], Support.Region("i", ParseRule.IconFingerprint, font: null, rect: new RectI(100, 100, 187, 189))), "t");
        c.Add(Support.Reader("stub", ReaderStatus.NeedsSurvey, "UI_X", []), "t");

        // an identity probe: ready without regions, but only with anchors (a log UI name alone proves no screen)
        Assert.ThrowsExactly<ArgumentException>(() => c.Add(Support.Reader("probe.noanchor", ReaderStatus.Ready, "UI_X", []), "t"));
        c.Add(Support.Reader("probe", ReaderStatus.Ready, "UI_X", ["a.anchor"]), "t");
        c.Seal();
        Assert.IsNotNull(c.Get("ok"));
        Assert.AreEqual(0, c.Get("probe")!.Regions.Count);
        Assert.IsNull(c.Get("missing"));
    }
}
