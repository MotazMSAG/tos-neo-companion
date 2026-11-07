using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.EngineNav.Testing;

namespace Companion.Engine.Tests;

/// <summary>
/// The crafting feature's product-tip run (crafting.productTip, live 2026-10-15) through the real engine against the
/// simulated game: one click on the product icon placed from the located materials header (the live point (1130,375)),
/// the item tip shown, then one Esc that closes it and leaves the Make view on top; read-only, no switch, any permission.
/// </summary>
[TestClass]
public sealed class ProductTipRoundTests : ScenarioBase
{
    private static ProcedureDefinition TipData()
    {
        var file = Harness.DataFile(Path.Combine("features", "crafting", "procedures.json"));
        return JsonSerializer.Deserialize<ProcedureDefinition[]>(File.ReadAllBytes(file), ContractJson.Options)!.Single(p => p.Id == "crafting.productTip");
    }

    private void Prepare()
    {
        H.Templates.Add(FakeTemplates.Def("craft.materials.header", 1270, 641, TemplateStatus.Verified, 1, null, TargetTags.ScreenAnchor) with { FireOn = FireOn.Up });
        H.Switches.Crafting = false;
        H.Log.Show("UI_LIFESKILL_MAKE_VIEW"); // a recipe is selected on the Make tab
        H.Game.ClickScript.Enqueue(req => req.TemplateId == "craft.materials.header"
            ? H.Game.SentClick(req, () => H.Log.Show("UI_ITEM_TIPS_PASS_TOUCH"))
            : null);
    }

    [TestMethod]
    public void TheTipRun_ClicksTheProductIconFromTheMaterialsHeader_ThenOneEscClosesTheTip()
    {
        Prepare();
        var r = H.Run(TipData());

        AssertStatus(ProcedureStatus.Succeeded, r);
        var click = H.Game.ClickRequests.Single();
        Assert.AreEqual(("craft.materials.header", new PointI(1130, 375), new PointI(1270, 641)), (click.TemplateId, click.ClientPoint, click.TemplateCenter),
            "the live product icon point = located header + (-140,-266)");
        Assert.AreEqual(1, H.Keys(GameKey.Escape), "one Esc, for the tip only");
        CollectionAssert.AreEqual(new[] { "UI_LIFESKILL_MAKE_VIEW" }, H.Log.State.AdvisoryStack.ToArray(), "the Make view is on top again");
        Assert.AreEqual((1, 1), (r.Clicks, r.EscPresses));
        Assert.AreEqual(0, H.Game.ClickRequests.Count(c => c.TemplateId == "craft.make"), "never crafts");
    }

    [TestMethod]
    public void SlideClicksOff_IsRefusedWithZeroInputs()
    {
        Prepare();
        H.Switches.SlideClickEnabled = false;
        AssertStatus(ProcedureStatus.Refused, H.Run(TipData()));
        Assert.AreEqual(0, H.Game.Inputs);
    }
}
