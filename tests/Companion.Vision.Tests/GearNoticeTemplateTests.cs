using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Vision;
using Companion.Vision;
using Companion.Vision.Internal;

namespace Companion.Vision.Tests;

/// <summary>
/// The gear durability Notice templates (crafting feature, 2026-10-18) against the live capture they were cut from
/// (tests\TestData\images\gear_notice_full.png, a copy of proofs\out\glove_now.png) and a Make tab capture without it.
/// </summary>
[TestClass]
public sealed class GearNoticeTemplateTests
{
    private static TemplateCatalog catalog = null!;
    private static VisionEngine vision = null!;

    private static string CraftingData => Path.Combine(TestData.CompanionRoot, "features", "Companion.Feature.Crafting", "data");

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        catalog = new TemplateCatalog();
        foreach (var dir in new[] { TestData.TemplatesDir, CraftingData })
        {
            foreach (var d in JsonSerializer.Deserialize<TemplateDefinition[]>(File.ReadAllText(Path.Combine(dir, DataFileNames.Templates)), ContractJson.Options)!)
            {
                catalog.Add(d, File.ReadAllBytes(Path.Combine(dir, d.ImageFile)), d.MaskFile is null ? null : File.ReadAllBytes(Path.Combine(dir, d.MaskFile)), "test");
            }
        }

        catalog.Seal();
        vision = new VisionEngine(catalog);
    }

    [TestMethod]
    [DataRow("gear.notice.box", 858, 610)]
    [DataRow("gear.notice.replace", 1127, 715)]
    public void TheNoticeTemplates_AreFoundOnTheLiveCapture(string id, int x, int y)
    {
        var frame = SourceImages.ToFrame(TestData.Image("gear_notice_full.png"), SourceLayout.ClientFull);
        var m = vision.Locate(frame, id);
        Assert.IsTrue(m.Found, $"{id}: {m}");
        Assert.IsTrue(Math.Abs(m.Center.X - x) <= 2 && Math.Abs(m.Center.Y - y) <= 2, $"{id}: {m}");
    }

    [TestMethod]
    [DataRow("gear.notice.box")]
    [DataRow("gear.notice.replace")]
    public void TheNoticeTemplates_AreFoundOnALaterLiveCapture(string id)
    {
        // live 2026-10-18 20:30, Sapientia Ruins, HUD expanded (gear_notice_live2.png)
        var frame = SourceImages.ToFrame(TestData.Image("gear_notice_live2.png"), SourceLayout.ClientFull);
        var m = vision.Locate(frame, id);
        Assert.IsTrue(m.Found, $"{id}: {m}");
        Assert.AreEqual(FrameQuality.Ok, vision.Assess(frame));
    }

    [TestMethod]
    [DataRow("gear.notice.box")]
    [DataRow("gear.notice.replace")]
    public void TheNoticeTemplates_AreFoundForAnotherGearOverTheMakeView(string id)
    {
        // live 2026-10-23 18:55, the cooking gear's notice ("Cheese Bandana Durability is insufficient") over the Make view
        // after the last craft of a goal (gear_notice_bandana_full.png): another gear name, the same box and buttons
        var frame = SourceImages.ToFrame(TestData.Image("gear_notice_bandana_full.png"), SourceLayout.ClientFull);
        var m = vision.Locate(frame, id);
        Assert.IsTrue(m.Found, $"{id}: {m}");
    }

    // incident 20 (2026-10-24): the box is a toggle; it is clicked only when it is seen empty
    [TestMethod]
    [DataRow("gear_notice_full.png")]
    [DataRow("gear_notice_live2.png")]
    [DataRow("gear_notice_bandana_full.png")]
    public void TheEmptyBox_IsFoundOnEveryLiveNotice(string image)
    {
        var frame = SourceImages.ToFrame(TestData.Image(image), SourceLayout.ClientFull);
        var m = vision.Locate(frame, "gear.notice.emptybox");
        Assert.IsTrue(m.Found, $"{image}: {m}");
    }

    [TestMethod]
    [DataRow("gear_notice_ticked_synthetic.png")]
    [DataRow("live_ability_make_full.png")]
    public void TheEmptyBox_IsNotFound_WithAMarkInIt_OrWithoutTheNotice(string image)
    {
        // gear_notice_ticked_synthetic.png: gear_notice_full.png with a tick drawn in the box (no capture of the game's own
        // ticked box exists yet; any mark must fail the empty-box match)
        var frame = SourceImages.ToFrame(TestData.Image(image), SourceLayout.ClientFull);
        Assert.IsFalse(vision.Locate(frame, "gear.notice.emptybox").Found, image);
    }

    [TestMethod]
    public void TheFavouritesAnchor_IsClickable_ForTheOffScreenPatchRule()
    {
        // 2026-10-21: the off-screen anchor patch rule needs IsClickable (the craft loop's slot clicks use it)
        Assert.IsTrue(catalog.IsClickable("craft.fav.anchor"));
        Assert.IsTrue(catalog.Get("craft.fav.anchor")!.Tags.HasFlag(TargetTags.ScreenAnchor));
    }

    [TestMethod]
    public void TheNoticeTemplates_AreNotFoundWithoutTheNotice()
    {
        var frame = SourceImages.ToFrame(TestData.Image("live_ability_make_full.png"), SourceLayout.ClientFull);
        Assert.IsFalse(vision.Locate(frame, "gear.notice.box").Found);
        Assert.IsFalse(vision.Locate(frame, "gear.notice.replace").Found);
    }
}
