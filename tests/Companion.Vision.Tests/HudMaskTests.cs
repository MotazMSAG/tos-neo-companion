using Companion.Contracts;
using Companion.Contracts.Vision;
using Companion.TemplateCutter;
using Companion.Vision.Internal;

namespace Companion.Vision.Tests;

/// <summary>
/// The HUD icons sit on SEE-THROUGH discs (live 2026-10-14: <c>menu.ability</c>, cut over the street scene, scored 0.877
/// over the barrels scene and ScanFavorites halted). The four HUD templates carry per-pixel masks that keep only the
/// glyph, label and opaque outline. Each one must be found over every scene of its HUD state, and not in the other state.
/// </summary>
[TestClass]
public sealed class HudMaskTests
{
    private const string ExpandedStreet = "cal_expanded_street_x1440.png";
    private const string ExpandedBarrels = "live_hud_expanded_barrels_x1440.png";
    private const string CollapsedStreet = "cal_collapsed_street_x1440.png";
    private const string CollapsedForest = "c3b_hud_right_x1440.png";

    private static readonly string[] Expanded = [ExpandedStreet, ExpandedBarrels];
    private static readonly string[] Collapsed = [CollapsedStreet, CollapsedForest];

    private static TemplateCatalog catalog = null!;
    private static VisionEngine vision = null!;

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        catalog = TestData.LoadCatalog();
        vision = new VisionEngine(catalog);
    }

    [TestMethod]
    public void HudTemplates_CarryTheirMask_BadgeRectsStayMasked()
    {
        var defs = TestData.LoadDefinitions().ToDictionary(d => d.Id);
        CollectionAssert.AreEquivalent(
            new[] { "hud.bag", "hud.toggle.collapsed", "hud.toggle.expanded", "menu.ability" },
            StableMask.Requests().Select(r => r.TemplateId).ToArray());
        foreach (var req in StableMask.Requests())
        {
            var d = defs[req.TemplateId];
            Assert.AreEqual(req.MaskFile, d.MaskFile, d.Id);
            Assert.AreEqual(TemplateStatus.Verified, d.Status, d.Id);
            Assert.AreEqual(1, d.SourceScale, d.Id);
            Assert.AreEqual(0.90, d.Threshold, d.Id);
            Assert.AreEqual(0.10, d.MinMargin, d.Id);
            Assert.IsNotNull(d.MaskRects, d.Id);

            var img = PngCodec.Decode(File.ReadAllBytes(Path.Combine(TestData.TemplatesDir, d.ImageFile)));
            var keep = StableMask.Decode(File.ReadAllBytes(Path.Combine(TestData.TemplatesDir, d.MaskFile!)), out var w, out var h);
            Assert.AreEqual((img.Width, img.Height), (w, h), d.Id);
            foreach (var r in d.MaskRects)
            {
                for (var y = r.Top; y < r.Bottom; y++)
                {
                    for (var x = r.Left; x < r.Right; x++)
                    {
                        Assert.IsFalse(keep[y * w + x], $"{d.Id}: badge rect pixel ({x},{y}) is compared");
                    }
                }
            }

            var kept = keep.Count(k => k) / (double)keep.Length;
            TestContext.WriteLine($"{d.Id}: mask keeps {keep.Count(k => k)}/{keep.Length} px ({kept:P0})");
            Assert.IsTrue(kept is > 0.2 and < 0.6, $"{d.Id}: kept {kept:P0} (glyph only, background out)");
        }
    }

    /// <summary>Provenance: the committed masks are exactly what <see cref="StableMask"/> derives from the captures.</summary>
    [TestMethod]
    public void CommittedMasks_AreReproducedFromTheCaptures()
    {
        var defs = TestData.LoadDefinitions().ToDictionary(d => d.Id);
        foreach (var req in StableMask.Requests())
        {
            var d = defs[req.TemplateId];
            var template = PngCodec.Decode(File.ReadAllBytes(Path.Combine(TestData.TemplatesDir, d.ImageFile)));
            var crops = new List<BgraImage>();
            foreach (var capture in req.Captures)
            {
                var (crop, center, score) = StableMask.AlignedCrop(TestData.Proof(capture), d, template);
                TestContext.WriteLine($"{d.Id} {capture}: aligned at ({center.X},{center.Y}) score {score:F4}");
                Assert.AreEqual(d.ExpectedCenter, center, $"{d.Id} {capture}: the stored crop is aligned with the capture");
                crops.Add(crop);
            }

            var derived = StableMask.Derive(template, crops, d.MaskRects ?? []);
            var committed = StableMask.Decode(File.ReadAllBytes(Path.Combine(TestData.TemplatesDir, d.MaskFile!)), out _, out _);
            CollectionAssert.AreEqual(committed, derived, d.Id);
        }
    }

    [TestMethod]
    public void EachHudTemplate_FoundOverEverySceneOfItsState_NotInTheOtherState()
    {
        var cases = new (string Id, string[] Present, string[] Absent)[]
        {
            ("hud.bag", [.. Expanded, .. Collapsed], []),
            ("hud.toggle.collapsed", Collapsed, Expanded),
            ("hud.toggle.expanded", Expanded, Collapsed),
            ("menu.ability", Expanded, Collapsed),
        };

        foreach (var (id, present, absent) in cases)
        {
            var d = catalog.Get(id)!;
            foreach (var capture in present)
            {
                var frame = TestData.StripFrame(capture);
                var m = vision.Locate(frame, id);
                TestContext.WriteLine($"{id,-21} {capture,-38} {m}");
                Assert.IsTrue(m.Found, $"{id} {capture}: {m}");
                Assert.AreEqual(MatchFailure.None, m.Failure, $"{id} {capture}: {m}");
                Assert.IsTrue(m.Center.ChebyshevDistanceTo(d.ExpectedCenter) <= 2, $"{id} {capture}: {m}");
                Assert.IsTrue(m.Score >= d.Threshold, $"{id} {capture}: {m}");
                Assert.IsTrue(m.Margin >= d.MinMargin, $"{id} {capture}: {m}");
                Assert.IsTrue(m.Score >= 0.99, $"{id} {capture}: masked score {m}");
                Assert.IsTrue(vision.VerifyAt(frame, id, m.Center, 2).Found, $"{id} {capture}: slid check");
            }

            foreach (var capture in absent)
            {
                var m = vision.Locate(TestData.StripFrame(capture), id);
                TestContext.WriteLine($"{id,-21} {capture,-38} {m}");
                Assert.IsFalse(m.Found, $"{id} {capture} (other HUD state): {m}");
                Assert.IsTrue(m.Score < d.Threshold, $"{id} {capture} (other HUD state): {m}");
            }
        }
    }

    [TestMethod]
    public void ClassifyHud_RightStateOverEveryScene()
    {
        foreach (var (captures, truth) in new[] { (Expanded, HudState.Expanded), (Collapsed, HudState.Collapsed) })
        {
            foreach (var capture in captures)
            {
                var h = vision.ClassifyHud(TestData.StripFrame(capture));
                TestContext.WriteLine($"{capture,-38} {h}");
                Assert.AreEqual(truth, h.State, $"{capture}: {h}");
                Assert.IsTrue(h.SignatureOk && h.BagFound, $"{capture}: {h}");
            }
        }
    }

    /// <summary>The live failure the masks fix: with the badge rects only, Ability is lost over the barrels scene.</summary>
    [TestMethod]
    public void WithoutTheMasks_AbilityIsLostOverTheBarrelsScene()
    {
        var rectsOnly = new TemplateCatalog();
        foreach (var d in TestData.LoadDefinitions())
        {
            rectsOnly.Add(d with { MaskFile = null }, File.ReadAllBytes(Path.Combine(TestData.TemplatesDir, d.ImageFile)), null, "core");
        }

        rectsOnly.Seal();
        var m = new VisionEngine(rectsOnly).Locate(TestData.StripFrame(ExpandedBarrels), "menu.ability");
        TestContext.WriteLine($"rects only: {m}");
        Assert.IsFalse(m.Found, m.ToString());
        Assert.AreEqual(MatchFailure.BelowThreshold, m.Failure, m.ToString());
        Assert.IsTrue(vision.Locate(TestData.StripFrame(ExpandedBarrels), "menu.ability").Found);
    }
}
