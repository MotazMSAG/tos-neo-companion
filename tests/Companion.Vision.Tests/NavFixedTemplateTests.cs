using Companion.Contracts;
using Companion.Contracts.Vision;
using Companion.TemplateCutter;
using Companion.Vision.Internal;

namespace Companion.Vision.Tests;

/// <summary>
/// The NavFixed tag (SPEC 7.3, EVIDENCE c13): set on exactly the four fixed HUD navigation templates, never together with
/// Commit, Forbidden, a gate or an anchor (catalog validation and the cutter refuse it).
/// </summary>
[TestClass]
public sealed class NavFixedTemplateTests
{
    [TestMethod]
    public void CommittedTemplates_NavFixedOnTheFourHudNavigationButtonsAndTheGatherTabOnly()
    {
        // the Ability view's right-column Gather tab since 2026-10-16 (fixed, read-only navigation; cross-monitor for a
        // cursor on the left part of the game's monitor)
        var defs = TestData.LoadDefinitions();
        CollectionAssert.AreEquivalent(
            new[] { "hud.bag", "hud.toggle.collapsed", "hud.toggle.expanded", "menu.ability", "gather.tab", "craft.make.tab", "stock.tab" },
            defs.Where(d => d.Tags.HasFlag(TargetTags.NavFixed)).Select(d => d.Id).ToArray());
        foreach (var d in defs.Where(d => d.Tags.HasFlag(TargetTags.NavFixed)))
        {
            Assert.AreEqual(TemplateStatus.Verified, d.Status, d.Id);
            Assert.AreEqual(AutomationSwitch.None, d.Gate, d.Id);
            Assert.AreEqual(TargetTags.None, d.Tags & (TargetTags.Commit | TargetTags.Forbidden), d.Id);
            Assert.IsNull(d.AnchorId, d.Id);
        }

        var catalog = TestData.LoadCatalog();
        Assert.IsTrue(catalog.IsClickable("hud.toggle.expanded") && catalog.IsClickable("menu.ability"));
    }

    [TestMethod]
    public void Catalog_RefusesNavFixedOnCommitGatedOrAnchoredTemplates()
    {
        var png = PngCodec.Encode24(SyntheticVisionTests.Pattern(40, 40, 3));
        var nav = SyntheticVisionTests.Def("syn.nav", new PointI(1000, 500)) with { Tags = TargetTags.Nav | TargetTags.NavFixed };
        var ok = new TemplateCatalog();
        ok.Add(nav, png, null, "test");

        foreach (var bad in new[]
                 {
                     nav with { Tags = nav.Tags | TargetTags.Commit, Gate = AutomationSwitch.Crafting },
                     nav with { Tags = nav.Tags | TargetTags.Forbidden },
                     nav with { Gate = AutomationSwitch.Homestead },
                     nav with { AnchorId = "syn.other", OffsetFromAnchor = new PointI(10, 0) },
                 })
        {
            var c = new TemplateCatalog();
            var ex = Assert.ThrowsExactly<ArgumentException>(() => c.Add(bad, png, null, "test"));
            StringAssert.StartsWith(ex.Message, "template.navFixedInvalid:syn.nav");
        }
    }

    [TestMethod]
    public void Cutter_RefusesNavFixedWithCommitOrGate()
    {
        var png = PngCodec.Encode24(SyntheticVisionTests.Pattern(200, 200, 4));
        var req = new CutRequest("test.nav", "test.png", SourceLayout.ClientCropFull, new PointI(1700, 250), new RectI(1718, 282, 1778, 342), TemplateStatus.Verified, [], null, TargetTags.Nav | TargetTags.NavFixed);
        Assert.AreEqual("navfixed-needs-plain-navigation", TemplateCutterCore.Cut(png, req with { Gate = AutomationSwitch.Crafting }).Refusal);
        Assert.AreEqual("navfixed-needs-plain-navigation", TemplateCutterCore.Cut(png, req with { Tags = req.Tags | TargetTags.Commit, Gate = AutomationSwitch.Crafting }).Refusal);
    }
}
