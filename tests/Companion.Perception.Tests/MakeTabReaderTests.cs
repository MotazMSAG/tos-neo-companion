using System.Text;
using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Perception;
using Companion.Contracts.Vision;
using Companion.Perception.Internal;
using Companion.Vision;
using Companion.Vision.Internal;

namespace Companion.Perception.Tests;

/// <summary>
/// The Make-tab readers (data\readers.json: craft.pairs, craft.product) on the real full-scale capture of the live run
/// of 2026-10-14 (tests\TestData\images\live_ability_make_full.png: White Toast selected, 25 owned, materials 40/1, 45/3, 340/3,
/// no "Requires" line, no counter), with the real templates, the real Windows OCR and the built-in digit fonts; the
/// 4-material layout (2026-10-14) on that capture's own tiles moved to the 4-tile places, and on the half-scale
/// c14_after.png of proof p0_c14 (2026-10-15 02:15, the tests' own copy in tests\TestData\images; Vanilla Cream: 406/6,
/// 614/3, 1/1, 300/5, no "Requires" line, counter 5/300, 0 owned), where a value is either read right or Unknown, never
/// wrong. Read-only; no window is touched.
/// </summary>
[TestClass]
public sealed class MakeTabReaderTests
{
    private const string MakeView = "UI_LIFESKILL_MAKE_VIEW";
    internal static readonly WinRtOcr Ocr = new();
    internal static readonly Lazy<VisionEngine> RealVision = new(() => new VisionEngine(LoadTemplates()));
    private static readonly Lazy<ReaderCatalog> Readers = new(LoadReaders);

    public TestContext TestContext { get; set; } = null!;

    private static Frame MakeTab(int dx = 0, int dy = 0) =>
        dx == 0 && dy == 0
            ? BuiltInFontTests.Capture("live_ability_make_full.png")
            : SourceImages.ToFrame(Support.Image("live_ability_make_full.png"), SourceLayout.ClientCropFull, new PointI(dx, dy));

    private static TemplateCatalog LoadTemplates()
    {
        var dir = Path.Combine(Support.CompanionRoot, "templates");
        var defs = JsonSerializer.Deserialize<TemplateDefinition[]>(File.ReadAllText(Path.Combine(dir, DataFileNames.Templates)), ContractJson.Options)!;
        var c = new TemplateCatalog();
        foreach (var d in defs)
        {
            c.Add(d, File.ReadAllBytes(Path.Combine(dir, d.ImageFile)), d.MaskFile is null ? null : File.ReadAllBytes(Path.Combine(dir, d.MaskFile)), "core");
        }

        c.Seal();
        return c;
    }

    private static ReaderCatalog LoadReaders()
    {
        var json = File.ReadAllText(Path.Combine(Support.CompanionRoot, "data", DataFileNames.Readers));
        var c = new ReaderCatalog();
        foreach (var r in JsonSerializer.Deserialize<ReaderDefinition[]>(json, ContractJson.Options)!)
        {
            c.Add(r, "core");
        }

        c.Seal();
        return c;
    }

    internal static ReaderDefinition Reader(string id) => Readers.Value.Get(id)!;

    private void RequireOcr()
    {
        if (!Ocr.IsAvailable)
        {
            Assert.Inconclusive("Windows OCR for en-US is not installed on this PC");
        }
    }

    internal static ScreenReader RealReader(IOcr? ocr = null) => new(RealVision.Value, ocr ?? Ocr, new DigitTemplateReader());

    internal static ReadValue V(ScreenReadResult r, string region) => r.Values.Single(v => v.RegionId == region);

    private string Dump(ScreenReadResult r)
    {
        var s = string.Join("; ", r.Values.Select(v => $"{v.RegionId} {v.Status} {v.Value}/{v.Second} '{v.Text}' c{v.Confidence} {v.DetailCode}"));
        TestContext.WriteLine($"{r.ReaderId} {r.Status}: {s}");
        return s;
    }

    [TestMethod]
    public void TheListProbe_ConfirmsTheRecipeListPage_AndMismatchesOnAnotherTabOrUi()
    {
        // craft.list (2026-10-15): an identity probe, no regions; the Make view stays on top on every tab of the right-hand
        // column, so only the list's "Make" title tells the recipe list page from the Stock (or Learn) tab.
        var probe = Reader("craft.list");
        foreach (var file in new[] { "live_ability_make_full.png", "make_fav5_full.png" })
        {
            var r = RealReader().Read(probe, BuiltInFontTests.Capture(file), Support.Ui(MakeView));
            Assert.AreEqual(ReadStatus.Ok, r.Status, file);
            Assert.IsEmpty(r.Values, file);
            Assert.IsTrue(r.Anchors!["craft.list.anchor"].ChebyshevDistanceTo(new PointI(485, 123)) <= 4, file);
        }

        var stock = SourceImages.ToFrame(Support.Image("stock_ability_top_full.png"), SourceLayout.ClientFull);
        Assert.AreEqual(ReadStatus.ScreenMismatch, RealReader().Read(probe, stock, Support.Ui(MakeView)).Status, "the Stock tab: the list is not shown");
        Assert.AreEqual(ReadStatus.ScreenMismatch, RealReader().Read(probe, MakeTab(), Support.Ui("UI_ITEM_TIPS_PASS_TOUCH")).Status, "another UI on top");

        // craft.other.tab: another tab is shown (the unselected Make tab to go back with), never on the Make tab itself
        var other = Reader("craft.other.tab");
        var away = RealReader().Read(other, stock, Support.Ui(MakeView));
        Assert.AreEqual(ReadStatus.Ok, away.Status);
        Assert.AreEqual(new PointI(1740, 300), away.Anchors!["craft.make.tab"]);
        Assert.AreEqual(ReadStatus.ScreenMismatch, RealReader().Read(other, MakeTab(), Support.Ui(MakeView)).Status, "the Make tab is the selected one");
    }

    [TestMethod]
    public void Readers_AreReadyAndAnchoredOnTheMaterialsHeader()
    {
        foreach (var id in new[] { "craft.pairs", "craft.product" })
        {
            var r = Reader(id);
            Assert.AreEqual(ReaderStatus.Ready, r.Status, id);
            Assert.AreEqual(MakeView, r.Screen.LogUiName);
            CollectionAssert.AreEqual(new[] { "craft.materials.header" }, r.Screen.AnchorTemplateIds.ToArray());
            Assert.IsTrue(r.Regions.All(g => g.AnchorId == "craft.materials.header"), id);
        }

        CollectionAssert.AreEqual(new[] { "materials", "requires" }, Reader("craft.pairs").Regions.Select(g => g.Id).ToArray());
        var materials = Reader("craft.pairs").Regions[0];
        Assert.IsTrue(materials is { Rule: ParseRule.TileRow, Pair: PairSemantics.OwnedRequired, FontStyle: BuiltInDigitFonts.ParchmentPair, Optional: false }, materials.ToString());
        Assert.AreEqual(new RectI(940, 674, 1587, 763), materials.ClientRect.Offset(1270, 641), "the band of the materials row (header at (1270,641)), centred on the row");
        var requires = Reader("craft.pairs").Regions[1];
        Assert.IsTrue(requires is { Rule: ParseRule.LabeledPair, Pair: PairSemantics.OwnedRequired, FontStyle: BuiltInDigitFonts.ParchmentPair, Optional: true }, requires.ToString());
        CollectionAssert.AreEqual(new[] { "product.name", "product.owned", "product.counter" }, Reader("craft.product").Regions.Select(g => g.Id).ToArray());
        var counter = Reader("craft.product").Regions[2];
        Assert.IsTrue(counter is { Rule: ParseRule.Pair, Pair: PairSemantics.Progress, Optional: true }, counter.ToString());
    }

    /// <summary>The icon keys of the fixed-slot reader (before 2026-10-14), so stored keys stay valid.</summary>
    private static string[] FixedSlotKeys(Frame f) =>
        new[] { new RectI(1088, 674, 1175, 763), new RectI(1220, 674, 1307, 763), new RectI(1352, 674, 1439, 763) }
            .Select(rect => IconFingerprint.Read(f, rect).Key!)
            .ToArray();

    [TestMethod]
    public void Pairs_AreReadOnTheRealCapture_WithAnIconKeyPerMaterial()
    {
        RequireOcr();
        var r = RealReader().Read(Reader("craft.pairs"), MakeTab(), Support.Ui(MakeView));
        Dump(r);
        Assert.AreEqual(ReadStatus.Ok, r.Status);
        Assert.AreEqual((40L, 1L), (V(r, "pair.1").Value, V(r, "pair.1").Second));
        Assert.AreEqual((45L, 3L), (V(r, "pair.2").Value, V(r, "pair.2").Second));
        Assert.AreEqual((340L, 3L), (V(r, "pair.3").Value, V(r, "pair.3").Second));
        Assert.AreEqual((ReadStatus.Ok, 3L, "tiles"), (V(r, "materials").Status, V(r, "materials").Value, V(r, "materials").DetailCode));
        Assert.IsFalse(r.Values.Any(v => v.RegionId is "icon.4" or "pair.4"));
        Assert.AreEqual((ReadStatus.Ok, (long?)null, "absent"), (V(r, "requires").Status, V(r, "requires").Value, V(r, "requires").DetailCode), "White Toast shows no 'Requires' line");
        var keys = new[] { "icon.1", "icon.2", "icon.3" }.Select(k => V(r, k).Text!).ToArray();
        Assert.IsTrue(keys.All(IconFingerprint.IsKey), string.Join(",", keys));
        CollectionAssert.AreEqual(FixedSlotKeys(MakeTab()), keys, "the detected tiles are the fixed slots' boxes: stored icon keys stay valid");
        for (var i = 0; i < 3; i++)
        {
            for (var j = i + 1; j < 3; j++)
            {
                var d = IconFingerprint.Distance(keys[i], keys[j]);
                TestContext.WriteLine($"icon distance {i + 1}-{j + 1}: {d} bits");
                Assert.IsTrue(d > 3 * IconFingerprint.SameIconMaxDistance, $"materials {i + 1} and {j + 1} must not look alike: {d}");
            }
        }

        // The same screen read again gives the same keys (identity is stable).
        var again = RealReader().Read(Reader("craft.pairs"), MakeTab(), Support.Ui(MakeView));
        CollectionAssert.AreEqual(keys, new[] { "icon.1", "icon.2", "icon.3" }.Select(k => V(again, k).Text).ToArray());
    }

    [TestMethod]
    public void Product_TitleAndOwnedCount_AreReadOnTheRealCapture()
    {
        RequireOcr();
        var r = RealReader().Read(Reader("craft.product"), MakeTab(), Support.Ui(MakeView));
        Dump(r);
        Assert.AreEqual(ReadStatus.Ok, r.Status);
        Assert.AreEqual("White Toast", V(r, "product.name").Text);
        Assert.AreEqual(25L, V(r, "product.owned").Value);
        Assert.IsNull(V(r, "product.owned").Second);
        Assert.AreEqual((ReadStatus.Ok, (long?)null, "absent"), (V(r, "product.counter").Status, V(r, "product.counter").Value, V(r, "product.counter").DetailCode), "White Toast shows no counter");
    }

    [TestMethod]
    public void AShiftedPanel_IsFollowedThroughTheAnchor_AndGivesTheSameValuesAndKeys()
    {
        RequireOcr();
        var reader = RealReader();
        var at = reader.Read(Reader("craft.pairs"), MakeTab(), Support.Ui(MakeView));
        foreach (var (dx, dy) in new[] { (2, -1), (-3, 2) })
        {
            var r = reader.Read(Reader("craft.pairs"), MakeTab(dx, dy), Support.Ui(MakeView));
            Dump(r);
            Assert.AreEqual(ReadStatus.Ok, r.Status, $"shift ({dx},{dy})");
            Assert.AreEqual((340L, 3L), (V(r, "pair.3").Value, V(r, "pair.3").Second));
            foreach (var k in new[] { "icon.1", "icon.2", "icon.3" })
            {
                Assert.AreEqual(0, IconFingerprint.Distance(V(at, k).Text!, V(r, k).Text!), k);
            }

            var p = reader.Read(Reader("craft.product"), MakeTab(dx, dy), Support.Ui(MakeView));
            Assert.AreEqual(("White Toast", 25L), (V(p, "product.name").Text, V(p, "product.owned").Value), Dump(p));
        }
    }

    [TestMethod]
    public void TheAnnouncementOrAnotherTopUi_IsAScreenMismatch_WithNoOcr()
    {
        var ocr = new FakeOcr();
        var reader = RealReader(ocr);

        // live_ability_view_full.png: the phase announcement covers the panel (no header anchor)
        var covered = BuiltInFontTests.Capture("live_ability_view_full.png");
        var r = reader.Read(Reader("craft.pairs"), covered, Support.Ui(MakeView));
        Assert.AreEqual(ReadStatus.ScreenMismatch, r.Status);
        Assert.IsTrue(r.Values.All(v => v.DetailCode == "anchor-not-found"));

        // the log says another UI is on top
        var top = reader.Read(Reader("craft.product"), MakeTab(), Support.Ui(MakeView, "UI_LIFE_STAGE_PREVIEW"));
        Assert.AreEqual(ReadStatus.ScreenMismatch, top.Status);
        Assert.AreEqual(0, ocr.Calls.Count);
    }

    [TestMethod]
    public void AMissingTile_LeavesTheRowOffCentre_SoItIsUnknown_NeverAShorterList()
    {
        RequireOcr();
        var f = MakeTab();
        var bgra = (byte[])f.Bgra.Clone();
        var bg = Pixel(f, 1470, 720); // parchment right of the third tile
        for (var y = 668; y < 795; y++)
        {
            for (var x = 1340; x < 1460; x++)
            {
                Array.Copy(bg, 0, bgra, y * f.Stride + x * 4, 4);
            }
        }

        var r = RealReader().Read(Reader("craft.pairs"), f with { Bgra = bgra }, Support.Ui(MakeView));
        Dump(r);
        Assert.AreEqual((ReadStatus.Unknown, "tiles-irregular"), (V(r, "materials").Status, V(r, "materials").DetailCode));
        Assert.IsFalse(r.Values.Any(v => v.RegionId.StartsWith("icon.", StringComparison.Ordinal) || v.RegionId.StartsWith("pair.", StringComparison.Ordinal)), "nothing read from an inconsistent row");
    }

    // ------------------------------------------------------------------ 3 or 4 materials (tiles detected, 2026-10-14)

    /// <summary>
    /// The full-scale capture with its materials row rebuilt as the 4-material layout (tiles at x 1022|1154|1286|1418,
    /// 132 px apart, as c14_after.png shows at half scale): the capture's own tiles 1, 2, 3, 1 with their pair text.
    /// </summary>
    private static Frame FourMaterials(params int[] lefts)
    {
        var f = MakeTab();
        var bgra = (byte[])f.Bgra.Clone();
        for (var y = 664; y < 800; y++)
        {
            for (var x = 950; x < 1612; x++)
            {
                Array.Copy(f.Bgra, (y * f.Stride) + (1000 * 4), bgra, (y * f.Stride) + (x * 4), 4); // plain parchment
            }
        }

        var sources = new[] { 1088, 1220, 1352, 1088 };
        lefts = lefts.Length == 0 ? [1022, 1154, 1286, 1418] : lefts;
        for (var k = 0; k < lefts.Length; k++)
        {
            // tile (87x89 + 2 px parchment) and the 120x24 pair box under it
            Copy(f, bgra, new RectI(sources[k] - 2, 672, sources[k] + 89, 765), lefts[k] - sources[k]);
            Copy(f, bgra, new RectI(sources[k] - 16, 766, sources[k] + 104, 790), lefts[k] - sources[k]);
        }

        return f with { Bgra = bgra };
    }

    private static void Copy(Frame src, byte[] dst, RectI from, int dx)
    {
        for (var y = from.Top; y < from.Bottom; y++)
        {
            Array.Copy(src.Bgra, (y * src.Stride) + (from.Left * 4), dst, (y * src.Stride) + ((from.Left + dx) * 4), from.Width * 4);
        }
    }

    [TestMethod]
    public void FourMaterials_AreDetectedWhereTheyAre_WithTheirKeysAndPairs()
    {
        RequireOcr();
        var three = RealReader().Read(Reader("craft.pairs"), MakeTab(), Support.Ui(MakeView));
        var r = RealReader().Read(Reader("craft.pairs"), FourMaterials(), Support.Ui(MakeView));
        Dump(r);
        Assert.AreEqual(ReadStatus.Ok, r.Status);
        Assert.AreEqual((4L, "tiles"), (V(r, "materials").Value, V(r, "materials").DetailCode));
        CollectionAssert.AreEqual(
            new[] { V(three, "icon.1").Text, V(three, "icon.2").Text, V(three, "icon.3").Text, V(three, "icon.1").Text },
            new[] { "icon.1", "icon.2", "icon.3", "icon.4" }.Select(k => V(r, k).Text).ToArray(),
            "the same tile gives the same key in any slot");
        Assert.AreEqual((40L, 1L), (V(r, "pair.1").Value, V(r, "pair.1").Second));
        Assert.AreEqual((45L, 3L), (V(r, "pair.2").Value, V(r, "pair.2").Second));
        Assert.AreEqual((340L, 3L), (V(r, "pair.3").Value, V(r, "pair.3").Second));
        Assert.AreEqual((40L, 1L), (V(r, "pair.4").Value, V(r, "pair.4").Second));
    }

    [TestMethod]
    public void TilesAtUnevenSpacing_OrNoTileButInk_AreUnknown_AndAnEmptyRowIsNoMaterials()
    {
        var reader = RealReader(new FakeOcr());
        var uneven = reader.Read(Reader("craft.pairs"), FourMaterials(1022, 1154, 1300), Support.Ui(MakeView));
        Assert.AreEqual((ReadStatus.Unknown, "tiles-irregular"), (V(uneven, "materials").Status, V(uneven, "materials").DetailCode));
        Assert.IsFalse(uneven.Values.Any(v => v.RegionId.StartsWith("icon.", StringComparison.Ordinal)), "never a guessed layout");

        var empty = reader.Read(Reader("craft.pairs"), EmptyRow(), Support.Ui(MakeView));
        Assert.AreEqual((ReadStatus.Ok, 0L, "no-tiles"), (V(empty, "materials").Status, V(empty, "materials").Value, V(empty, "materials").DetailCode));
        Assert.AreEqual(ReadStatus.Ok, empty.Status);

        var f = EmptyRow();
        var bgra = (byte[])f.Bgra.Clone();
        Copy(MakeTab(), bgra, new RectI(1100, 690, 1160, 750), 100); // a tile's picture without its frame
        var smudge = reader.Read(Reader("craft.pairs"), f with { Bgra = bgra }, Support.Ui(MakeView));
        Assert.AreEqual((ReadStatus.Unknown, "tiles-not-found"), (V(smudge, "materials").Status, V(smudge, "materials").DetailCode));
    }

    private static Frame EmptyRow()
    {
        var f = MakeTab();
        var bgra = (byte[])f.Bgra.Clone();
        for (var y = 664; y < 800; y++)
        {
            for (var x = 950; x < 1612; x++)
            {
                Array.Copy(f.Bgra, (y * f.Stride) + (1000 * 4), bgra, (y * f.Stride) + (x * 4), 4);
            }
        }

        return f with { Bgra = bgra };
    }

    [TestMethod]
    public void TheRequiresLine_IsReadFromItsLastWord_OnFullScalePixels()
    {
        // No full-scale capture shows the line yet: build it from this capture's own text, a word of the description
        // ("Recovery", brown on parchment) as the label and the "45/3" of the second material 15 px after it.
        RequireOcr();
        var f = MakeTab();
        var bgra = (byte[])f.Bgra.Clone();
        Copy(f, bgra, new RectI(1326, 284, 1452, 316), 1040 - 1326, dy: 803 - 284);
        Copy(f, bgra, new RectI(1235, 765, 1295, 788), 1176 - 1235, dy: 810 - 765);
        var line = f with { Bgra = bgra };

        var reader = RealReader();
        var (value, code) = reader.LastWord(line, new RectI(1036, 800, 1340, 838));
        Assert.AreEqual("label-value", code);
        Assert.IsTrue(value is { } v && Math.Abs(v.Left - 1175) <= 2 && Math.Abs(v.Right - 1237) <= 2 && v.Top == 800 && v.Bottom == 838, $"the value box (with its blank margin), not the label: {value}");

        var r = reader.Read(Reader("craft.pairs"), line, Support.Ui(MakeView));
        Dump(r);
        Assert.AreEqual((ReadStatus.Ok, 45L, 3L), (V(r, "requires").Status, V(r, "requires").Value, V(r, "requires").Second));
        Assert.AreEqual((40L, 1L), (V(r, "pair.1").Value, V(r, "pair.1").Second), "the materials are unaffected");
    }

    private static void Copy(Frame src, byte[] dst, RectI from, int dx, int dy)
    {
        for (var y = from.Top; y < from.Bottom; y++)
        {
            Array.Copy(src.Bgra, (y * src.Stride) + (from.Left * 4), dst, ((y + dy) * src.Stride) + ((from.Left + dx) * 4), from.Width * 4);
        }
    }

    // ------------------------------------------------------------------ the half-scale Vanilla Cream capture (c14)

    /// <summary>c14_after.png (half scale, tests\TestData\images) upscaled to the client: never enough for a template match, enough for the geometry.</summary>
    private static Frame VanillaCream() => SourceImages.ToFrame(Support.Image("c14_after.png"), SourceLayout.ClientHalf);

    /// <summary>The Make readers with absolute regions at the header's place on the half-scale capture (the anchor itself scores 0.89 there).</summary>
    private static ReaderDefinition AtHeader(string id, PointI header)
    {
        var r = Reader(id);
        return r with
        {
            Screen = new ScreenIdentity(MakeView, []),
            Regions = r.Regions.Select(g => g with { AnchorId = null, ClientRect = g.ClientRect.Offset(header.X, header.Y) }).ToArray(),
        };
    }

    [TestMethod]
    public void HalfScaleVanillaCream_FourTilesAreFoundAtTheFourTilePlaces()
    {
        var header = new PointI(1271, 641); // NCC 0.89 at +1 px from the full-scale place
        var band = Reader("craft.pairs").Regions[0].ClientRect.Offset(header.X, header.Y);
        var row = TileRow.Detect(VanillaCream(), band);
        TestContext.WriteLine(row.Code + ": " + string.Join(" ", row.Tiles));
        Assert.AreEqual("tiles", row.Code);
        Assert.HasCount(4, row.Tiles);
        var expected = new[] { 1022, 1154, 1286, 1418 };
        for (var k = 0; k < 4; k++)
        {
            Assert.IsTrue(Math.Abs(row.Tiles[k].Left - expected[k]) <= 2, $"tile {k + 1} at {row.Tiles[k]}");
            Assert.IsTrue(Math.Abs(row.Tiles[k].Top - 675) <= 2 && Math.Abs(row.Tiles[k].Height - 89) <= 2, row.Tiles[k].ToString());
        }

        // The 3-material capture's row is the 3-tile layout: centred on the same column.
        var three = TileRow.Detect(MakeTab(), Reader("craft.pairs").Regions[0].ClientRect.Offset(1270, 641));
        Assert.AreEqual((1088 + 1439) / 2.0, (row.Tiles[0].Left + row.Tiles[3].Right) / 2.0, 3, "both layouts are centred alike");
        Assert.HasCount(3, three.Tiles);
    }

    [TestMethod]
    public void HalfScaleVanillaCream_EveryValueIsReadRightOrUnknown_NeverWrong()
    {
        RequireOcr();
        var frame = VanillaCream();
        var header = new PointI(1271, 641);
        var reader = RealReader();
        var pairs = reader.Read(AtHeader("craft.pairs", header), frame, Support.Ui(MakeView));
        var product = reader.Read(AtHeader("craft.product", header), frame, Support.Ui(MakeView));
        Dump(pairs);
        Dump(product);

        Assert.AreEqual((ReadStatus.Ok, 4L), (V(pairs, "materials").Status, V(pairs, "materials").Value));
        var keys = Enumerable.Range(1, 4).Select(k => V(pairs, "icon." + k).Text!).ToArray();
        Assert.AreEqual(4, keys.Distinct(StringComparer.Ordinal).Count(), "four different materials");
        var truth = new (long, long)[] { (406, 6), (614, 3), (1, 1), (300, 5) };
        var read = 0;
        for (var k = 1; k <= 4; k++)
        {
            var v = V(pairs, "pair." + k);
            if (v.Status == ReadStatus.Ok)
            {
                read++;
                Assert.AreEqual(truth[k - 1], (v.Value!.Value, v.Second!.Value), "pair." + k);
            }
        }

        // No "Requires" line on this capture: its place is plain parchment, so the optional region reads "absent" (never a
        // value made up from the materials above it).
        var line = Reader("craft.pairs").Regions[1].ClientRect.Offset(header.X, header.Y);
        Assert.IsTrue(reader.IsPlain(frame, line));
        var requires = V(pairs, "requires");
        Assert.AreEqual((ReadStatus.Ok, (long?)null, (long?)null, "absent"), (requires.Status, requires.Value, requires.Second, requires.DetailCode));

        var name = V(product, "product.name");
        if (name.Status == ReadStatus.Ok)
        {
            Assert.AreEqual("Vanilla Cream", name.Text);
        }

        var owned = V(product, "product.owned");
        if (owned.Status == ReadStatus.Ok)
        {
            Assert.AreEqual((long?)0, owned.Value, "none owned");
        }

        var counter = V(product, "product.counter");
        Assert.AreNotEqual("absent", counter.DetailCode, "the bar is there");
        if (counter.Status == ReadStatus.Ok)
        {
            Assert.AreEqual((5L, 300L), (counter.Value, counter.Second));
        }

        TestContext.WriteLine($"half scale: {read}/4 pairs, requires {requires.Status} ({requires.DetailCode}), name {name.Status}, owned {owned.Status} ({owned.DetailCode}), counter {counter.Status} ({counter.DetailCode})");
    }

    [TestMethod]
    public void TextCutByTheRegionEdge_IsUnknown_NeverAPartialNumber()
    {
        var narrow = Support.Reader("narrow", ReaderStatus.Ready, MakeView, [],
            Support.Region("p", ParseRule.Pair, PairSemantics.OwnedRequired, BuiltInDigitFonts.ParchmentPair, 0, 9999999, new RectI(1370, 766, 1440, 790)));
        var ocr = new FakeOcr { Text = { [Binarization.DarkOnLight] = "40/3", [Binarization.LightOnDark] = "40/3" } };
        var v = new ScreenReader(RealVision.Value, ocr, new DigitTemplateReader()).Read(narrow, MakeTab(), Support.Ui(MakeView)).Values.Single();
        Assert.AreEqual((ReadStatus.Unknown, "text-truncated"), (v.Status, v.DetailCode));
        Assert.AreEqual(0, ocr.Calls.Count);
    }

    // ------------------------------------------------------------------ the digit-template fallback

    [TestMethod]
    public void GarbledOcr_FallsBackToStrongDigitTemplates_WithLowerConfidence()
    {
        // What Windows OCR actually returned for these crops at 3x DarkOnLight / LightOnDark (2026-10-14 runs).
        var ocr = new FakeOcr { Text = { [Binarization.DarkOnLight] = "34013", [Binarization.LightOnDark] = "" } };
        var r = new ScreenReader(RealVision.Value, ocr, new DigitTemplateReader()).Read(Reader("craft.pairs"), MakeTab(), Support.Ui(MakeView));
        Dump(r);
        foreach (var (k, owned, required) in new[] { ("pair.1", 40L, 1L), ("pair.2", 45L, 3L), ("pair.3", 340L, 3L) })
        {
            var v = V(r, k);
            Assert.AreEqual((ReadStatus.Ok, owned, required, "digits-only"), (v.Status, v.Value, v.Second, v.DetailCode), k);
            Assert.IsTrue(v.Confidence is > 0.4 and <= 0.5, v.Confidence.ToString());
        }
    }

    [TestMethod]
    public void OcrReadingADifferentPlausibleValue_BlocksTheFallback()
    {
        // pair1t at 3x DarkOnLight really read "40/7" once: a plausible pair that disagrees with the templates.
        var ocr = new FakeOcr { Text = { [Binarization.DarkOnLight] = "40/7", [Binarization.LightOnDark] = "" } };
        var r = new ScreenReader(RealVision.Value, ocr, new DigitTemplateReader()).Read(Reader("craft.pairs"), MakeTab(), Support.Ui(MakeView));
        Dump(r);
        Assert.AreEqual(ReadStatus.Unknown, V(r, "pair.1").Status);
        Assert.IsNull(V(r, "pair.1").Value);
        Assert.AreEqual("ocr-digits-disagree", V(r, "pair.1").DetailCode);
    }

    [TestMethod]
    public void AnOcrTimeoutOrNoTemplateFont_NeverFallsBack()
    {
        var timeout = new FakeOcr { Status = OcrStatus.Timeout };
        var r = new ScreenReader(RealVision.Value, timeout, new DigitTemplateReader()).Read(Reader("craft.pairs"), MakeTab(), Support.Ui(MakeView));
        Assert.IsTrue(r.Values.Where(v => v.RegionId.StartsWith("pair.", StringComparison.Ordinal)).All(v => v.Status == ReadStatus.Unknown && v.DetailCode == "ocr-timeout"), Dump(r));

        var garbled = new FakeOcr { Text = { [Binarization.DarkOnLight] = "Aon", [Binarization.LightOnDark] = "" } };
        var noFonts = new ScreenReader(RealVision.Value, garbled, new DigitTemplateReader(new Dictionary<string, IReadOnlyDictionary<char, GrayImage>>()));
        var n = noFonts.Read(Reader("craft.pairs"), MakeTab(), Support.Ui(MakeView));
        Assert.IsTrue(n.Values.Where(v => v.RegionId.StartsWith("pair.", StringComparison.Ordinal)).All(v => v.Status == ReadStatus.Unknown && v.DetailCode == "digit-font-missing"), Dump(n));

        // the icons do not depend on OCR
        Assert.IsTrue(n.Values.Where(v => v.RegionId.StartsWith("icon.", StringComparison.Ordinal)).All(v => v.Status == ReadStatus.Ok && IconFingerprint.IsKey(v.Text)));
    }

    [TestMethod]
    public void BuiltInFonts_ReadTheirSourceTexts_AndOtherInstancesOfTheSameGlyphs()
    {
        var digits = new DigitTemplateReader();
        CollectionAssert.IsSubsetOf(new[] { BuiltInDigitFonts.ParchmentPair, BuiltInDigitFonts.BadgeDark }, digits.FontStyles.ToArray());
        var make = MakeTab();
        var cases = new (string Font, RectI Rect, string Text)[]
        {
            (BuiltInDigitFonts.ParchmentPair, new RectI(1072, 766, 1192, 790), "40/1"),
            (BuiltInDigitFonts.ParchmentPair, new RectI(1205, 766, 1325, 790), "45/3"),
            (BuiltInDigitFonts.ParchmentPair, new RectI(1336, 766, 1456, 790), "340/3"), // '3', '4', '0' cut from the other pairs
            (BuiltInDigitFonts.BadgeDark, new RectI(1198, 416, 1262, 444), "25"),
            (BuiltInDigitFonts.BadgeDark, new RectI(530, 215, 640, 250), "96499"),
            (BuiltInDigitFonts.BadgeDark, new RectI(820, 262, 892, 298), "963"),
        };
        foreach (var (font, rect, text) in cases)
        {
            var d = digits.ReadDetailed(RealVision.Value.Crop(make, rect), font);
            TestContext.WriteLine($"{font} '{text}': '{d.Text}' ok {d.Ok} min {d.MinGlyphScore:F3} margin {d.MinMargin:F3}");
            Assert.IsTrue(d.Ok, $"{font} '{text}': {d}");
            Assert.AreEqual(text, d.Text);
            if (font == BuiltInDigitFonts.ParchmentPair)
            {
                // other instances of the pair glyphs are strong enough for the digits-only rule
                Assert.IsTrue(d.MinGlyphScore >= DigitTemplateReader.StrongGlyphScore && d.MinMargin >= DigitTemplateReader.StrongGlyphMargin, d.ToString());
            }
        }
    }

    // ------------------------------------------------------------------ honest OCR accuracy (report only)

    /// <summary>Reports what Windows OCR alone reads on the five Make-tab values at each scale and binarisation.</summary>
    [TestMethod]
    public void Accuracy_WindowsOcr_OnTheMakeTabValues()
    {
        RequireOcr();
        var make = MakeTab();
        var cases = new (string Truth, RectI Rect)[]
        {
            ("40/1", new RectI(1072, 766, 1192, 790)),
            ("45/3", new RectI(1205, 766, 1325, 790)),
            ("340/3", new RectI(1336, 766, 1456, 790)),
            ("25", new RectI(1198, 416, 1262, 444)),
            ("White Toast", new RectI(1318, 220, 1580, 256)),
        };
        var sb = new StringBuilder("== Windows OCR on live_ability_make_full.png ==\n");
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (truth, rect) in cases)
        {
            var crop = RealVision.Value.Crop(make, rect);
            var line = new StringBuilder($"  '{truth}':");
            foreach (var scale in new[] { 3.0, 4.0, 5.0 })
            {
                foreach (var b in new[] { Binarization.None, Binarization.DarkOnLight, Binarization.LightOnDark })
                {
                    var o = Ocr.Recognize(crop, new OcrOptions("en-US", scale, b), TimeSpan.FromSeconds(3));
                    var got = truth.Contains(' ', StringComparison.Ordinal) ? ValueParser.NormalizeText(o.Text) : ValueParser.NormalizeNumeric(o.Text);
                    var key = $"{scale}x {b}";
                    if (got == truth)
                    {
                        hits[key] = hits.GetValueOrDefault(key) + 1;
                    }

                    line.Append($" {key}='{got}'{(got == truth ? "+" : string.Empty)}");
                }
            }

            sb.AppendLine(line.ToString());
        }

        foreach (var (k, n) in hits.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"  {k}: {n}/{cases.Length}");
        }

        TestContext.WriteLine(sb.ToString());
    }

    private static byte[] Pixel(Frame f, int x, int y) => f.Bgra.AsSpan(y * f.Stride + x * 4, 4).ToArray();
}
