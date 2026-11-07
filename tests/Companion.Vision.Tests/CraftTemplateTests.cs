using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Vision;
using Companion.Vision;
using Companion.Vision.Internal;

namespace Companion.Vision.Tests;

/// <summary>
/// The crafting templates cut from the live run of 2026-10-14 (live_ability_make_full.png, Make tab open, White Toast
/// selected; the tests' own copy in tests\TestData\images) against that capture, the half-scale pair of proof p0_c14
/// (2026-10-15 02:15) and the same view covered by the phase announcement.
/// </summary>
[TestClass]
public sealed class CraftTemplateTests
{
    private static TemplateCatalog catalog = null!;
    private static VisionEngine vision = null!;

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        catalog = TestData.LoadCatalog();
        vision = new VisionEngine(catalog);
    }

    private static Frame MakeTab(int dx = 0, int dy = 0) =>
        dx == 0 && dy == 0
            ? SourceImages.ToFrame(TestData.Image("live_ability_make_full.png"), SourceLayout.ClientFull)
            : SourceImages.ToFrame(TestData.Image("live_ability_make_full.png"), SourceLayout.ClientCropFull, new PointI(dx, dy));

    /// <summary>The favourites slot offsets from the crafting feature's data (crafting.loop, slot.01..slot.10).</summary>
    private static IReadOnlyList<PointI> SlotOffsets()
    {
        var file = Path.Combine(TestData.CompanionRoot, "features", "Companion.Feature.Crafting", "data", "procedures.json");
        var loop = JsonSerializer.Deserialize<ProcedureDefinition[]>(File.ReadAllBytes(file), ContractJson.Options)!.Single(p => p.Id == "crafting.loop");
        return Enumerable.Range(1, 10)
            .Select(i => loop.Steps.Single(s => s.Id == "slot." + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture)).Action)
            .Cast<ClickAction>()
            .Select(c =>
            {
                Assert.AreEqual("craft.fav.anchor", c.TemplateId);
                return c.Offset!.Value;
            })
            .ToList();
    }

    private static double GrayStd(Frame f, PointI centre)
    {
        var g = vision.Crop(f, RectI.CenteredAt(centre, 40, 40));
        var mean = g.Pixels.Average(p => (double)p);
        return Math.Sqrt(g.Pixels.Average(p => (p - mean) * (p - mean)));
    }

    [TestMethod]
    public void Definitions_AreVerifiedFullScaleAndTheMakeButtonIsAGatedCommit()
    {
        var make = catalog.Get("craft.make")!;
        Assert.AreEqual((TemplateStatus.Verified, 1, TargetTags.Commit, AutomationSwitch.Crafting), (make.Status, make.SourceScale, make.Tags, make.Gate));
        var makeAt = make.ExpectedCenter;
        var maxAt = catalog.Get("craft.qty.max")!.ExpectedCenter;
        Assert.AreEqual(new PointI(1472, 870), makeAt);
        Assert.AreEqual(FireOn.Up, make.FireOn);
        Assert.AreEqual(new PointI(1264, 868), maxAt);
        Assert.IsFalse(catalog.Get("craft.qty.max")!.Tags.HasFlag(TargetTags.Commit), "max quantity sets, it does not commit");
        var anchor = catalog.Get("craft.fav.anchor")!;
        Assert.AreEqual((TargetTags.ScreenAnchor, new PointI(591, 756), 40), (anchor.Tags, anchor.ExpectedCenter, anchor.SearchRadius));
        var header = catalog.Get("craft.materials.header")!;
        Assert.AreEqual((TargetTags.ScreenAnchor, new PointI(1270, 641), AutomationSwitch.None), (header.Tags, header.ExpectedCenter, header.Gate), "reader identity anchor only");
        foreach (var id in new[] { "craft.make", "craft.qty.max", "craft.fav.anchor", "craft.materials.header", "craft.list.anchor" })
        {
            Assert.IsTrue(catalog.IsClickable(id), id);
            StringAssert.Contains(catalog.Get(id)!.Source, "live_ability_make_full.png");
        }
    }

    [TestMethod]
    public void MakeTab_EveryTemplateIsFoundAtItsPlaceAndUnique()
    {
        var f = MakeTab();
        foreach (var id in new[] { "craft.make", "craft.qty.max", "craft.fav.anchor", "craft.materials.header", "craft.list.anchor" })
        {
            var m = vision.Locate(f, id);
            TestContext.WriteLine($"{id} {m}");
            Assert.IsTrue(m.Found, m.ToString());
            Assert.IsTrue(m.Center.ChebyshevDistanceTo(catalog.Get(id)!.ExpectedCenter) <= 1, m.ToString());
            Assert.IsTrue(m.Score >= 0.99, m.ToString());
            Assert.IsTrue(m.Margin >= 0.10, m.ToString());
            Assert.IsTrue(vision.VerifyAt(f, id, m.Center, 2).Found, id + " verifies at its centre (slid check)");
        }
    }

    [TestMethod]
    public void SlotOffsets_LandOnTheNineFavouriteCardsAndTheEmptyTenthCell()
    {
        // The capture shows 9 favourites (2 rows of 5, the 10th cell empty). Each slot = located anchor + offset.
        var f = MakeTab();
        var anchor = vision.Locate(f, "craft.fav.anchor");
        Assert.IsTrue(anchor.Found, anchor.ToString());
        var offsets = SlotOffsets();
        for (var i = 0; i < 10; i++)
        {
            var p = anchor.Center.Offset(offsets[i].X, offsets[i].Y);
            var std = GrayStd(f, p);
            TestContext.WriteLine($"slot {i + 1} at ({p.X},{p.Y}) gray std {std:F1}");
            if (i < 9)
            {
                Assert.IsTrue(std > 15, $"slot {i + 1} should be on a recipe card: std {std:F1}");
            }
            else
            {
                Assert.IsTrue(std < 5, $"slot 10 is the empty cell: std {std:F1}");
            }
        }

        // Cell centres measured on the capture (cards ~98x100 px, ~113 px apart, rows 140 px apart).
        Assert.AreEqual(new PointI(364, 498), anchor.Center.Offset(offsets[0].X, offsets[0].Y));
        Assert.AreEqual(new PointI(816, 638), anchor.Center.Offset(offsets[9].X, offsets[9].Y));
    }

    [TestMethod]
    public void SlotOffsets_FollowTheAnchorWhenTheViewIsShifted_AndNothingIsPlacedBeyondTheRadius()
    {
        // A list scrolled by 20 px moves the anchor and the cards together: the slots still land on the cards.
        var shifted = MakeTab(0, -20);
        var anchor = vision.Locate(shifted, "craft.fav.anchor");
        Assert.IsTrue(anchor.Found, anchor.ToString());
        Assert.AreEqual(new PointI(591, 736), anchor.Center);
        var offsets = SlotOffsets();
        Assert.IsTrue(GrayStd(shifted, anchor.Center.Offset(offsets[0].X, offsets[0].Y)) > 15);
        Assert.IsTrue(GrayStd(shifted, anchor.Center.Offset(offsets[9].X, offsets[9].Y)) < 5);

        // Shifted beyond the 40 px radius: no anchor, so no slot click at all.
        var far = vision.Locate(MakeTab(0, -60), "craft.fav.anchor");
        TestContext.WriteLine(far.ToString());
        Assert.IsFalse(far.Found, far.ToString());
    }

    [TestMethod]
    public void ListAnchor_IsTheMakePageTitle_CutFromTheCapture_AndFixedWhileTheListScrolls()
    {
        var def = catalog.Get("craft.list.anchor")!;
        Assert.AreEqual((TemplateStatus.Verified, 1, TargetTags.ScreenAnchor, new PointI(485, 123), 16, AutomationSwitch.None, FireOn.Up),
            (def.Status, def.SourceScale, def.Tags, def.ExpectedCenter, def.SearchRadius, def.Gate, def.FireOn));
        StringAssert.Contains(def.Source, "live_ability_make_full.png ClientFull offset (0,0) scale 1; client rect (424,99)-(546,147)");

        // provenance: the PNG is exactly the capture's client rect
        var png = PngCodec.Decode(File.ReadAllBytes(Path.Combine(TestData.TemplatesDir, def.ImageFile)));
        var cut = SourceImages.ToClient(TestData.Image("live_ability_make_full.png"), SourceLayout.ClientFull, default, out _).CropCopy(new RectI(424, 99, 546, 147));
        Assert.AreEqual((cut.Width, cut.Height), (png.Width, png.Height));
        Assert.IsTrue(cut.Data.Zip(png.Data, (x, y) => Math.Abs(x - y)).Max() <= 1);

        // The title is unique around its place, and nothing near it looks alike.
        var m = vision.Locate(MakeTab(), "craft.list.anchor");
        Assert.IsTrue(m.Found && m.Center == new PointI(485, 123) && m.Score >= 0.99 && m.Margin >= 0.3, m.ToString());

        // The title is a hanging tag that bobs about 4 px: "the same place" is the template's declared drift
        // (templates.json motionTolerancePx 8), as the engine compares two located centres, never the 1 px aim.
        var drift = def.EffectiveMotionTolerancePx;
        Assert.AreEqual(8, drift);

        // make_fav5_full.png (full scale, 2026-10-15 01:46) caught the tag 4 px up: still the same place.
        var bobbed = vision.Locate(SourceImages.ToFrame(TestData.Image("make_fav5_full.png"), SourceLayout.ClientFull), "craft.list.anchor");
        TestContext.WriteLine($"bobbed {bobbed}");
        Assert.IsTrue(bobbed.Found && bobbed.Center == new PointI(485, 119), bobbed.ToString());
        Assert.IsTrue(bobbed.Center.ChebyshevDistanceTo(def.ExpectedCenter) <= drift);

        // Proof c14 (half scale, before and after a 300 px drag of the list): the title stays where it is.
        var before = vision.Locate(SourceImages.ToFrame(TestData.Image("c14_before.png"), SourceLayout.ClientHalf), "craft.list.anchor");
        var after = vision.Locate(SourceImages.ToFrame(TestData.Image("c14_after.png"), SourceLayout.ClientHalf), "craft.list.anchor");
        TestContext.WriteLine($"before {before}; after {after}");
        Assert.IsTrue(before.Found && after.Found, $"{before} / {after}");
        Assert.IsTrue(before.Center.ChebyshevDistanceTo(after.Center) <= drift, $"the list scrolled, the anchor did not: {before.Center} / {after.Center}");
        foreach (var half in new[] { before, after })
        {
            Assert.IsTrue(half.Center.ChebyshevDistanceTo(def.ExpectedCenter) <= 6, $"half-scale captures land within a few pixels: {half.Center}");
        }
    }

    [TestMethod]
    public void TheUnselectedMakeTab_IsCutFromTheStockCapture_FoundWhereAnotherTabIsShown_AndNeverOnTheMakeTab()
    {
        // craft.make.tab (2026-10-15): the list scan's way back to the recipe list after a stray press switched the tab
        var def = catalog.Get("craft.make.tab")!;
        Assert.AreEqual((TemplateStatus.Verified, 1, TargetTags.Nav | TargetTags.NavFixed, AutomationSwitch.None, new PointI(1740, 300)), (def.Status, def.SourceScale, def.Tags, def.Gate, def.ExpectedCenter));
        Assert.IsTrue(catalog.IsClickable("craft.make.tab"));
        StringAssert.Contains(def.Source, "tests/TestData/images/stock_ability_top_full.png");
        StringAssert.Contains(def.Source, "client rect (1690,278)-(1790,322)");
        var png = PngCodec.Decode(File.ReadAllBytes(Path.Combine(TestData.TemplatesDir, def.ImageFile)));
        var cut = SourceImages.ToClient(TestData.Image("stock_ability_top_full.png"), SourceLayout.ClientFull, default, out _).CropCopy(new RectI(1690, 278, 1790, 322));
        Assert.AreEqual((cut.Width, cut.Height), (png.Width, png.Height));
        Assert.IsTrue(cut.Data.Zip(png.Data, (x, y) => Math.Abs(x - y)).Max() <= 1, "provenance: the PNG is exactly the capture's client rect");

        foreach (var file in new[] { "stock_ability_top_full.png", "stock_ability_s2_full.png", "stock_homestead_full.png", "stock_tailor_full.png" })
        {
            var f = SourceImages.ToFrame(TestData.Image(file), SourceLayout.ClientFull);
            var m = vision.Locate(f, "craft.make.tab");
            TestContext.WriteLine($"{file} {m}");
            Assert.IsTrue(m.Found && m.Center == new PointI(1740, 300) && m.Score >= 0.99 && m.Margin >= 0.1, $"{file} {m}");
            Assert.IsFalse(vision.Locate(f, "craft.list.anchor").Found, file + ": the recipe list is not shown");
        }

        foreach (var file in new[] { "live_ability_make_full.png", "make_fav1_whitetoast_full.png", "make_fav5_full.png" })
        {
            var f = SourceImages.ToFrame(TestData.Image(file), SourceLayout.ClientFull);
            Assert.IsFalse(vision.Locate(f, "craft.make.tab").Found, file + ": the Make tab is selected there");
            Assert.IsTrue(vision.Locate(f, "craft.list.anchor").Found, file);
        }
    }

    [TestMethod]
    public void AnnouncementOverTheMakeView_NoCraftTemplateIsFound()
    {
        // live_ability_view_full.png: UI_LIFE_STAGE_PREVIEW ("New Ability Phase") covers the Make view.
        var f = SourceImages.ToFrame(TestData.Proof("live_ability_view_full.png"), SourceLayout.ClientFull);
        foreach (var id in new[] { "craft.make", "craft.qty.max", "craft.fav.anchor", "craft.materials.header", "craft.list.anchor" })
        {
            var m = vision.Locate(f, id);
            TestContext.WriteLine($"{id} {m}");
            Assert.IsFalse(m.Found, m.ToString());
        }
    }

    [TestMethod]
    public void Hud_NoCraftTemplateIsFound()
    {
        var f = TestData.C3bStripFrame();
        foreach (var id in new[] { "craft.make", "craft.qty.max", "craft.fav.anchor", "craft.materials.header", "craft.list.anchor" })
        {
            Assert.IsFalse(vision.Locate(f, id).Found, id);
        }
    }
}
