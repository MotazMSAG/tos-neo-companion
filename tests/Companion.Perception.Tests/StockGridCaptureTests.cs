using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Perception;
using Companion.Contracts.Vision;
using Companion.Perception.Internal;
using Companion.Vision;

namespace Companion.Perception.Tests;

/// <summary>
/// The Stock tab's grid reader (<see cref="ParseRule.IconGrid"/>, the inventory feature's readers.json) on every
/// full-scale Stock capture of 2026-10-15 (tests\TestData\images, copies of proofs\out: the Ability sub-tab at the top
/// and after 1-3 drags, Homestead, Tailor, Hyperweather), with the feature's templates, the real Windows OCR and the
/// built-in "stock-count-dark" font. Every whole tile is found; every count reads right or Unknown, never wrong; faded
/// tiles read 0; the values that read Ok today are asserted, so a font or reader change that loses one fails here.
/// Read-only; no window is touched.
/// </summary>
[TestClass]
public sealed class StockGridCaptureTests
{
    private const string MakeView = "UI_LIFESKILL_MAKE_VIEW";

    /// <summary>A faded tile (no count: 0).</summary>
    private const long F = -1;

    /// <summary>A tile in colour that shows no count (Unknown).</summary>
    private const long U = -2;

    private static readonly Lazy<VisionEngine> Vision = new(() => new VisionEngine(LoadTemplates()));
    private static readonly Lazy<ReaderCatalog> Readers = new(LoadReaders);

    /// <summary>Ground truth (read off the images): per capture, its reader and every whole tile's count, row by row.</summary>
    internal static readonly IReadOnlyList<Truth> Captures =
    [
        new("stock_ability_top_full.png", "inventory.stock.ability", [F, F, F, 114, 16, 120, F, F, F, F, F, 145]),
        new("stock_ability_s1_full.png", "inventory.stock.ability", [F, 200, 52, 146]),
        new("stock_ability_s2_full.png", "inventory.stock.ability", [F, F, F, 48, F, F, 44, 27, 12, 25, 47, U]),
        new("stock_ability_s3_full.png", "inventory.stock.ability", [614, F, 41, 91, 110, 301, F, F, 16, F, 28, 2]),
        new("stock_homestead_full.png", "inventory.stock.homestead", [2, 4, 16, 13, 22, 10, 30, 25, 22, 39, 13, 27, 34, U, 16, 11, 20, 35]),
        new("stock_tailor_full.png", "inventory.stock.tailor", [23550, 1821, 8, 1049, 391]),
        new("stock_hyperweather_full.png", "inventory.stock.hyperweather", [2, F, F]),
    ];

    public TestContext TestContext { get; set; } = null!;

    private static string InventoryData => Path.Combine(Support.CompanionRoot, "features", "Companion.Feature.Inventory", "data");

    private static TemplateCatalog LoadTemplates()
    {
        var c = new TemplateCatalog();
        foreach (var dir in new[] { Path.Combine(Support.CompanionRoot, "templates"), InventoryData })
        {
            foreach (var d in JsonSerializer.Deserialize<TemplateDefinition[]>(File.ReadAllText(Path.Combine(dir, DataFileNames.Templates)), ContractJson.Options)!)
            {
                c.Add(d, File.ReadAllBytes(Path.Combine(dir, d.ImageFile)), d.MaskFile is null ? null : File.ReadAllBytes(Path.Combine(dir, d.MaskFile)), "test");
            }
        }

        c.Seal();
        return c;
    }

    private static ReaderCatalog LoadReaders()
    {
        var c = new ReaderCatalog();
        foreach (var r in JsonSerializer.Deserialize<ReaderDefinition[]>(File.ReadAllText(Path.Combine(InventoryData, DataFileNames.Readers)), ContractJson.Options)!)
        {
            c.Add(r, "feature:inventory");
        }

        c.Seal();
        return c;
    }

    private static ReaderDefinition Reader(string id) => Readers.Value.Get(id)!;

    private static ScreenReader RealReader() => new(Vision.Value, MakeTabReaderTests.Ocr, new DigitTemplateReader());

    private static ReadValue V(ScreenReadResult r, string region) => r.Values.Single(v => v.RegionId == region);

    private void RequireOcr()
    {
        if (!MakeTabReaderTests.Ocr.IsAvailable)
        {
            Assert.Inconclusive("Windows OCR for en-US is not installed on this PC");
        }
    }

    private void Dump(string file, ScreenReadResult r) =>
        TestContext.WriteLine($"{file} {r.ReaderId} {r.Status}: " + string.Join("; ", r.Values.Select(v => $"{v.RegionId} {v.Status} {v.Value} c{v.Confidence} g{v.GlyphScore} {v.DetailCode}")));

    [TestMethod]
    public void TheReaders_AreReady_OnTheGridArea_AndAnchoredOnTheStockTabAndTheirSubTab()
    {
        var all = Readers.Value.All.Select(r => r.Id).ToArray();
        CollectionAssert.AreEquivalent(new[] { "inventory.stock.grid", "inventory.stock.ability", "inventory.stock.homestead", "inventory.stock.tailor", "inventory.stock.hyperweather" }, all);
        foreach (var r in Readers.Value.All)
        {
            Assert.AreEqual((ReaderStatus.Ready, MakeView), (r.Status, r.Screen.LogUiName), r.Id);
            Assert.AreEqual("stock.anchor", r.Screen.AnchorTemplateIds[0], r.Id);
            var grid = r.Regions.Single();
            Assert.IsTrue(grid is { Id: "grid", Rule: ParseRule.IconGrid, AnchorId: null, FontStyle: BuiltInDigitFonts.StockCount, Min: 0 }, r.Id);
            Assert.AreEqual(new RectI(300, 270, 1540, 812), grid.ClientRect, "the visible grid, above the Homestead footer (y 830+)");
        }

        Assert.HasCount(1, Reader("inventory.stock.grid").Screen.AnchorTemplateIds);
        Assert.AreEqual("stock.sub.homestead.on", Reader("inventory.stock.homestead").Screen.AnchorTemplateIds[1]);
    }

    [TestMethod]
    public void EveryWholeTileIsFound_InSixColumns_AndTheCutRowsAreLeftOut()
    {
        int[] lefts = [356, 568, 779, 989, 1200, 1411];
        foreach (var t in Captures)
        {
            var g = IconGrid.Detect(BuiltInFontTests.Capture(t.File), Reader(t.Reader).Regions[0].ClientRect);
            Assert.AreEqual((t.Counts.Length > 0 ? "tiles" : "no-tiles", t.Counts.Length), (g.Code, g.Tiles.Count), t.File);

            // the end of the list is on screen only where nothing is drawn below the last row (Tailor, Hyperweather);
            // elsewhere a section header, cut tiles or the "more" arrow at the bottom centre show that the list goes on
            Assert.AreEqual(t.File is "stock_tailor_full.png" or "stock_hyperweather_full.png", g.EndVisible, t.File);
            foreach (var tile in g.Tiles)
            {
                Assert.IsTrue(lefts.Any(x => Math.Abs(tile.Box.Left - x) <= 1), $"{t.File} {tile.Box}");
                Assert.IsTrue(tile.Box.Width is >= 113 and <= 117 && tile.Box.Height is >= 118 and <= 121, $"{t.File} {tile.Box}");
                Assert.IsTrue(tile.Box.Top >= 270 && tile.Box.Bottom <= 812, $"{t.File} {tile.Box}: whole tiles only");
            }
        }
    }

    [TestMethod]
    public void EveryCount_IsRightOrUnknown_NeverWrong_AndFadedTilesReadZero()
    {
        RequireOcr();
        var ok = 0;
        var total = 0;
        foreach (var t in Captures)
        {
            var r = RealReader().Read(Reader(t.Reader), BuiltInFontTests.Capture(t.File), Support.Ui(MakeView));
            Dump(t.File, r);
            Assert.AreEqual((ReadStatus.Ok, (long?)t.Counts.Length), (V(r, "grid").Status, V(r, "grid").Value), t.File);
            Assert.AreEqual(t.File is "stock_tailor_full.png" or "stock_hyperweather_full.png" ? "tiles-end" : "tiles", V(r, "grid").DetailCode, t.File);
            for (var k = 1; k <= t.Counts.Length; k++)
            {
                var count = V(r, "count." + k);
                var truth = t.Counts[k - 1];
                total++;
                switch (truth)
                {
                    case F:
                        Assert.AreEqual((ReadStatus.Ok, (long?)0, "faded"), (count.Status, count.Value, count.DetailCode), $"{t.File} count.{k}: a faded tile is 0");
                        Assert.AreEqual("icon-faded", V(r, "icon." + k).DetailCode, $"{t.File} icon.{k}");
                        ok++;
                        break;
                    case U:
                        Assert.AreEqual((ReadStatus.Unknown, "count-absent"), (count.Status, count.DetailCode), $"{t.File} count.{k}: no count, not faded");
                        break;
                    default:
                        if (count.Status == ReadStatus.Ok)
                        {
                            Assert.AreEqual(truth, count.Value, $"{t.File} count.{k}: never a wrong number");
                            ok++;
                        }
                        else
                        {
                            TestContext.WriteLine($"{t.File} count.{k} ({truth}) Unknown: {count.DetailCode}");
                        }

                        Assert.IsNotNull(count.Rect, $"{t.File} count.{k} carries the word's rect");
                        break;
                }
            }
        }

        TestContext.WriteLine($"{ok}/{total} counts read");
        Assert.AreEqual(total - Captures.Sum(t => t.Counts.Count(c => c == U)), ok, "every count of the captures reads today (faded = 0); only the two tiles without a count are Unknown");
    }

    [TestMethod]
    public void WithoutOcr_TheDigitTemplatesAloneReadTheStrongCounts_AndAContradictingOcrMakesThemUnknown()
    {
        // OCR that sees nothing: a count is still taken from strong glyphs alone (the digits-only rule), never wrong
        var blind = new ScreenReader(Vision.Value, new FakeOcr(), new DigitTemplateReader());

        // OCR that reads another plausible number everywhere: the readers disagree, so no count is taken
        var liar = new FakeOcr();
        liar.Text[Binarization.DarkOnLight] = "7";
        liar.Text[Binarization.LightOnDark] = "7";
        var contradicted = new ScreenReader(Vision.Value, liar, new DigitTemplateReader());
        int digitsOnly = 0, refused = 0;
        foreach (var t in Captures)
        {
            var frame = BuiltInFontTests.Capture(t.File);
            var a = blind.Read(Reader(t.Reader), frame, Support.Ui(MakeView));
            var b = contradicted.Read(Reader(t.Reader), frame, Support.Ui(MakeView));
            for (var k = 1; k <= t.Counts.Length; k++)
            {
                var truth = t.Counts[k - 1];
                var va = V(a, "count." + k);
                var vb = V(b, "count." + k);
                if (truth == F)
                {
                    Assert.AreEqual(((long?)0, (long?)0), (va.Value, vb.Value), $"{t.File} count.{k}: a faded tile needs no OCR");
                    continue;
                }

                if (va.Status == ReadStatus.Ok)
                {
                    Assert.AreEqual((truth, "digits-only"), (va.Value!.Value, va.DetailCode), $"{t.File} count.{k}");
                    digitsOnly++;
                }

                Assert.AreNotEqual(ReadStatus.Ok, vb.Status, $"{t.File} count.{k}: OCR said 7, the templates {truth}");
                refused++;
            }
        }

        TestContext.WriteLine($"digits-only {digitsOnly}, refused {refused}");
        Assert.IsGreaterThan(30, digitsOnly, "most counts read from their strong glyphs alone");
    }

    [TestMethod]
    public void IconKeys_AreStable_DistinctTilesLookDifferent_AndStockKeysStayFarFromMakeTabKeys()
    {
        // the Stock module takes a learned icon only within 2 bits and when every other item's icon lies 4 bits farther
        // (StockIcons in the inventory feature): every two tiles of one screen are more than 4 bits apart (the closest: two
        // strawberry cones, 5 bits); the hue bits keep recoloured families apart (golden vs sage leaf 34 bits, bags 17+)
        const int strict = 4;
        var keys = new List<(string File, int K, string Key)>();
        foreach (var t in Captures)
        {
            var g = IconGrid.Detect(BuiltInFontTests.Capture(t.File), Reader(t.Reader).Regions[0].ClientRect);
            keys.AddRange(g.Tiles.Select((tile, i) => (t.File, i + 1, tile.Key)));
            Assert.IsTrue(g.Tiles.All(tile => IconFingerprint.IsKey(tile.Key)), t.File);
        }

        var closest = int.MaxValue;
        foreach (var group in keys.GroupBy(k => k.File))
        {
            var list = group.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    var d = IconFingerprint.Distance(list[i].Key, list[j].Key);
                    closest = Math.Min(closest, d);
                    Assert.IsTrue(d > strict, $"{group.Key} tiles {list[i].K} and {list[j].K} look alike: {d} bits");
                }
            }
        }

        TestContext.WriteLine($"closest two tiles of one screen: {closest} bits");
        var s3 = keys.Where(k => k.File == "stock_ability_s3_full.png").ToList();
        Assert.IsGreaterThan(24, IconFingerprint.Distance(s3[8].Key, s3[10].Key), "the golden (Lv50) and the sage (Lv30) leaf: one drawing, two colours");

        // the Make tab's material keys (another tile size and the plain fingerprint) stay far from every Stock key
        var make = BuiltInFontTests.Capture("live_ability_make_full.png");
        var makeKeys = new[] { new RectI(1088, 674, 1175, 763), new RectI(1220, 674, 1307, 763), new RectI(1352, 674, 1439, 763) }
            .Select(r => IconFingerprint.Read(make, r).Key!)
            .ToList();
        var nearest = keys.Min(k => makeKeys.Min(m => IconFingerprint.Distance(k.Key, m)));
        TestContext.WriteLine($"nearest Stock key to a Make-tab key: {nearest} bits");
        Assert.IsGreaterThan(3 * IconFingerprint.SameIconMaxDistance, nearest, "a Stock key is never taken for a Make-tab material");

        // the same frame shifted by whole pixels (another scroll offset) gives the same keys: the count is left out
        var frame = BuiltInFontTests.Capture("stock_homestead_full.png");
        var shifted = Shift(frame, -7);
        var a = IconGrid.Detect(frame, new RectI(300, 270, 1540, 812)).Tiles;
        var b = IconGrid.Detect(shifted, new RectI(300, 270, 1540, 812)).Tiles;
        Assert.HasCount(a.Count, b);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.AreEqual(a[i].Box.Offset(0, -7), b[i].Box);
            Assert.AreEqual(a[i].Key, b[i].Key, $"tile {i + 1}");
        }
    }

    [TestMethod]
    public void TheCountsHaloSeparatesThemFromTheIcon_AndALevelLabelIsNeverTheCount()
    {
        // "Lv50 48" (Ability, 3rd drag): the label is a word of its own on the left; the count is the last word
        var s2 = BuiltInFontTests.Capture("stock_ability_s2_full.png");
        var lv = IconGrid.FindCount(s2, new RectI(989, 376, 1104, 496))!;
        Assert.AreEqual((2, new RectI(1070, 468, 1100, 488)), (lv.Glyphs, lv.Box));

        // a faded "Lv60" tile has no count (its label is faded: no dark ink)
        Assert.IsNull(IconGrid.FindCount(s2, new RectI(357, 376, 471, 496)));
        Assert.IsTrue(IconGrid.IsFaded(s2, new RectI(357, 376, 471, 496)));

        // the long count of the Tailor sub-tab is drawn smaller and still found whole
        var tailor = IconGrid.FindCount(BuiltInFontTests.Capture("stock_tailor_full.png"), new RectI(357, 300, 471, 419))!;
        Assert.AreEqual((5, 16), (tailor.Glyphs, tailor.Box.Height));
    }

    [TestMethod]
    public void OnTheMakeTab_OrAnotherSubTab_TheReadersReportAScreenMismatch()
    {
        var make = RealReader().Read(Reader("inventory.stock.grid"), BuiltInFontTests.Capture("live_ability_make_full.png"), Support.Ui(MakeView));
        Assert.AreEqual(ReadStatus.ScreenMismatch, make.Status, "the Make tab is not the Stock tab");
        var home = BuiltInFontTests.Capture("stock_homestead_full.png");
        Assert.AreEqual(ReadStatus.ScreenMismatch, RealReader().Read(Reader("inventory.stock.ability"), home, Support.Ui(MakeView)).Status, "Homestead is selected, not Ability");
        Assert.AreEqual(ReadStatus.ScreenMismatch, RealReader().Read(Reader("inventory.stock.tailor"), home, Support.Ui(MakeView)).Status);
        Assert.AreEqual(ReadStatus.ScreenMismatch, RealReader().Read(Reader("inventory.stock.homestead"), home, Support.Ui()).Status, "the log must show the Ability view on top");
    }

    /// <summary>The frame with its grid area moved up by <paramref name="dy"/> (&lt; 0) pixels, as after a small scroll.</summary>
    private static Frame Shift(Frame f, int dy)
    {
        var px = (byte[])f.Bgra.Clone();
        for (var y = 270; y < 812; y++)
        {
            var from = y - dy;
            if (from < 270 || from >= 812)
            {
                continue;
            }

            Array.Copy(f.Bgra, (from * f.Stride) + (300 * 4), px, (y * f.Stride) + (300 * 4), (1540 - 300) * 4);
        }

        return f with { Bgra = px };
    }

    /// <summary>A capture, its reader and the count of every whole tile (F faded = 0, U no count = Unknown).</summary>
    internal sealed record Truth(string File, string Reader, long[] Counts);
}
