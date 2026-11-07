using System.Diagnostics;
using Companion.Contracts;
using Companion.Contracts.Vision;
using Companion.Vision;

namespace Companion.Vision.Tests;

/// <summary>The real templates against the real proof captures (read-only).</summary>
[TestClass]
public sealed class HudTemplateTests
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

    [TestMethod]
    public void TemplatesJson_HasTheSeedSetWithTheRightStatus()
    {
        var defs = TestData.LoadDefinitions().ToDictionary(d => d.Id);
        CollectionAssert.AreEquivalent(
            new[] { "hud.bag", "hud.toggle.collapsed", "hud.toggle.expanded", "menu.ability", "craft.make", "craft.qty.max", "craft.fav.anchor", "craft.materials.header", "craft.list.anchor", "craft.make.tab", "gather.tab", "stock.tab" },
            defs.Keys.ToArray());
        foreach (var d in defs.Values)
        {
            Assert.IsTrue(File.Exists(Path.Combine(TestData.TemplatesDir, d.ImageFile)), d.ImageFile);
            Assert.IsTrue(d.MaskFile is null || File.Exists(Path.Combine(TestData.TemplatesDir, d.MaskFile)), d.MaskFile);
            Assert.IsFalse(string.IsNullOrWhiteSpace(d.Source), d.Id);
        }

        Assert.AreEqual(TemplateStatus.Verified, defs["hud.bag"].Status);
        Assert.AreEqual(1, defs["hud.bag"].SourceScale);
        Assert.AreEqual(TemplateStatus.Verified, defs["hud.toggle.collapsed"].Status);
        Assert.AreEqual(HudState.Collapsed, defs["hud.toggle.collapsed"].Hud);
        // Replaced by full-scale cuts from the live Calibrate run of 2026-10-14.
        Assert.AreEqual(TemplateStatus.Verified, defs["hud.toggle.expanded"].Status);
        Assert.AreEqual(1, defs["hud.toggle.expanded"].SourceScale);
        Assert.AreEqual(TemplateStatus.Verified, defs["menu.ability"].Status);
        Assert.AreEqual(1, defs["menu.ability"].SourceScale);
        Assert.IsFalse(defs.ContainsKey("menu.homestead"), "menu.homestead must stay absent until Calibrate");
    }

    [TestMethod]
    public void Clickability_VerifiedOnly()
    {
        Assert.IsTrue(catalog.IsClickable("hud.bag"));
        Assert.IsTrue(catalog.IsClickable("hud.toggle.collapsed"));
        Assert.IsTrue(catalog.IsClickable("hud.toggle.expanded"));
        Assert.IsTrue(catalog.IsClickable("menu.ability"));
        Assert.IsFalse(catalog.IsClickable("menu.homestead"));
    }

    [TestMethod]
    public void C3b_BagFoundAtItsPlace()
    {
        var m = vision.Locate(TestData.C3bStripFrame(), "hud.bag");
        TestContext.WriteLine($"hud.bag {m}");
        Assert.IsTrue(m.Found, m.ToString());
        Assert.AreEqual(MatchFailure.None, m.Failure);
        Assert.IsTrue(m.Center.ChebyshevDistanceTo(new PointI(1748, 312)) <= 2, m.ToString());
        Assert.IsTrue(m.Score >= 0.99, m.ToString());
        Assert.IsTrue(m.Margin >= 0.10, m.ToString());
    }

    [TestMethod]
    public void C3b_CollapsedToggleFound_ExpandedNot()
    {
        var f = TestData.C3bStripFrame();
        var c = vision.Locate(f, "hud.toggle.collapsed");
        var e = vision.Locate(f, "hud.toggle.expanded");
        TestContext.WriteLine($"collapsed {c}\nexpanded {e}");
        Assert.IsTrue(c.Found, c.ToString());
        Assert.IsTrue(c.Center.ChebyshevDistanceTo(new PointI(1855, 318)) <= 2, c.ToString());
        Assert.IsFalse(e.Found, e.ToString());
    }

    [TestMethod]
    public void C3b_ClassifiesCollapsed()
    {
        var sw = Stopwatch.StartNew();
        var h = vision.ClassifyHud(TestData.C3bStripFrame());
        sw.Stop();
        TestContext.WriteLine($"{h} in {sw.ElapsedMilliseconds} ms");
        Assert.AreEqual(HudState.Collapsed, h.State, h.ToString());
        Assert.IsTrue(h.SignatureOk);
        Assert.IsTrue(h.BagFound);
    }

    [TestMethod]
    public void C1_UpscaledIsExpandedOrUnknown_NeverCollapsed()
    {
        var f = TestData.HalfClientFrame("c1_ability.png");
        var h = vision.ClassifyHud(f);
        TestContext.WriteLine(h.ToString());
        Assert.AreNotEqual(HudState.Collapsed, h.State, h.ToString());
        Assert.IsTrue(h.State is HudState.Expanded or HudState.Unknown, h.ToString());
        Assert.IsFalse(vision.Locate(f, "hud.toggle.collapsed").Found);
    }

    [TestMethod]
    public void OpenFullScreenUis_AreNotHud()
    {
        foreach (var f in new[] { TestData.HalfWindowFrame("b_after_B.png"), TestData.HalfClientFrame("c3_Ability.png"), TestData.HalfClientFrame("c3_Bag.png") })
        {
            var h = vision.ClassifyHud(f);
            TestContext.WriteLine(h.ToString());
            Assert.IsFalse(h.BagFound, h.ToString());
            Assert.IsFalse(h.SignatureOk, h.ToString());
            Assert.IsTrue(h.State is HudState.Covered or HudState.Absent or HudState.Unknown, h.ToString());
        }
    }

    [TestMethod]
    public void BagShiftedBeyondRadius_IsOutsideRadius()
    {
        var m = vision.Locate(TestData.C3bStripFrame(dx: -40), "hud.bag");
        TestContext.WriteLine(m.ToString());
        Assert.IsFalse(m.Found);
        Assert.AreEqual(MatchFailure.OutsideRadius, m.Failure, m.ToString());
        Assert.AreEqual(40, m.DistanceFromExpected, m.ToString());
    }

    [TestMethod]
    public void BagShiftedWithinRadius_IsFoundWhereItIs()
    {
        var m = vision.Locate(TestData.C3bStripFrame(dx: -10, dy: 6), "hud.bag");
        Assert.IsTrue(m.Found, m.ToString());
        Assert.AreEqual(new PointI(1738, 318), m.Center);
    }

    [TestMethod]
    public void VerifyAt_ChecksPositionAndClickability()
    {
        var f = TestData.C3bStripFrame();
        var ok = vision.VerifyAt(f, "hud.bag", new PointI(1749, 313), 2);
        Assert.IsTrue(ok.Found, ok.ToString());
        var shifted = vision.VerifyAt(TestData.C3bStripFrame(dx: 6), "hud.bag", new PointI(1748, 312), 2);
        Assert.IsFalse(shifted.Found, shifted.ToString());
        Assert.IsFalse(vision.VerifyAt(f, "menu.ability", new PointI(1734, 452), 2).Found, "Ability is hidden on the collapsed HUD");
        Assert.AreEqual(MatchFailure.TemplateMissing, vision.VerifyAt(f, "menu.homestead", new PointI(1732, 444), 2).Failure);
    }

    /// <summary>
    /// Robustness survey on every capture that shows the right HUD (half-scale ones upscaled x2: blurrier than live
    /// full-scale captures, so scores are a lower bound). Ground truth by viewing the images. Asserts only that the
    /// classifier never states the WRONG state; it prints the scores for the report.
    /// </summary>
    [TestMethod]
    public void Survey_NeverWrongState()
    {
        var cases = new (string Name, Frame Frame, HudState Truth)[]
        {
            ("c3b strip (full)", TestData.C3bStripFrame(), HudState.Collapsed),
            ("c3b_hud (half)", TestData.HalfClientFrame("c3b_hud.png"), HudState.Collapsed),
            ("c3_Bag_before (half)", TestData.HalfClientFrame("c3_Bag_before.png"), HudState.Collapsed),
            ("c1_ability (half, old layout)", TestData.HalfClientFrame("c1_ability.png"), HudState.Expanded),
            ("g_after_Esc (half window, old layout)", TestData.HalfWindowFrame("g_after_Esc.png"), HudState.Expanded),
            ("a_capture (half window)", TestData.HalfWindowFrame("a_capture.png"), HudState.Unknown),
            ("b_after_B (bag open)", TestData.HalfWindowFrame("b_after_B.png"), HudState.Covered),
            ("c3_Ability (Lumi's Hut)", TestData.HalfClientFrame("c3_Ability.png"), HudState.Covered),
            ("c3_Bag (bag open)", TestData.HalfClientFrame("c3_Bag.png"), HudState.Covered),
        };

        foreach (var (name, f, truth) in cases)
        {
            var h = vision.ClassifyHud(f);
            var bag = vision.Locate(f, "hud.bag");
            TestContext.WriteLine($"{name,-40} truth {truth,-9} -> {h.State,-9} bag {bag.Score:F3}/{bag.SecondBest:F3} {bag.Failure} @({bag.Center.X},{bag.Center.Y}) col {h.CollapsedToggleScore:F3} exp {h.ExpandedToggleScore:F3} {h.DetailCode}");
            if (truth is HudState.Collapsed or HudState.Expanded)
            {
                Assert.IsTrue(h.State == truth || h.State == HudState.Unknown, $"{name}: {h}");
            }
            else if (truth == HudState.Covered)
            {
                Assert.IsFalse(h.SignatureOk, $"{name}: {h}");
            }
        }
    }
}
