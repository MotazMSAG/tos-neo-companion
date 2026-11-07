using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.EngineNav.Testing;

namespace Companion.Engine.Tests;

/// <summary>
/// The inventory feature's Stock scan runs (inventory.scanStock, inventory.stockTip, 2026-10-15) through the real engine
/// against the simulated game: the right-hand Stock tab is pressed on its own template and expects no UI event (the game
/// logs only "LostFocuse Label_Text"); every sub-tab press, drag and tile press hangs off the located selected Stock tab;
/// the tip opens and one Esc closes it; nothing is committed; no switch, any permission.
/// </summary>
[TestClass]
public sealed class ScanStockRoundTests : ScenarioBase
{
    private static ProcedureDefinition[] Data()
    {
        var file = Harness.DataFile(Path.Combine("features", "inventory", "procedures.json"));
        return JsonSerializer.Deserialize<ProcedureDefinition[]>(File.ReadAllBytes(file), ContractJson.Options)!;
    }

    /// <summary>A run as the ScanStock handler builds it (open skipped while the Make view is on top).</summary>
    private static ProcedureDefinition Run(params string[] steps)
    {
        var data = Data().Single(p => p.Id == "inventory.scanStock");
        return data with
        {
            Steps = data.Steps
                .Where(s => s.Id is "open" or "announce.close" || steps.Contains(s.Id))
                .Select(s => s.Id == "open" ? s with { Flags = s.Flags | StepFlags.SkipIfSatisfied } : s)
                .ToArray(),
        };
    }

    private void Prepare()
    {
        H.Templates.Verify("menu.ability");
        H.Templates.Add(FakeTemplates.Def("stock.anchor", 1755, 577, TemplateStatus.Verified, 1, null, TargetTags.ScreenAnchor) with { FireOn = FireOn.Up, SearchRadius = 12 });
        H.Templates.Add(FakeTemplates.Def("stock.tab", 1740, 577, TemplateStatus.Verified, 1, null, TargetTags.Nav | TargetTags.NavFixed) with { FireOn = FireOn.Up, SearchRadius = 12 });
        H.Templates.Add(FakeTemplates.Def("craft.make.tab", 1740, 300, TemplateStatus.Verified, 1, null, TargetTags.Nav | TargetTags.NavFixed) with { FireOn = FireOn.Up, SearchRadius = 12 });
        H.Switches.Crafting = false;
        H.Log.Show("UI_LIFESKILL_MAKE_VIEW"); // the Ability view is open
    }

    [TestMethod]
    public void TheStockTabPress_ExpectsNoUiEvent_AndPressesTheTabOnItsOwnTemplate()
    {
        Prepare();
        var r = H.Run(Run("stock.open", "stock.settle"));

        AssertStatus(ProcedureStatus.Succeeded, r);
        var click = H.Game.ClickRequests.Single();
        Assert.AreEqual(("stock.tab", new PointI(1740, 577), (PointI?)null), (click.TemplateId, click.ClientPoint, click.TemplateCenter), "pressed at the tab's own centre");
        Assert.AreEqual((1, 0), (r.Clicks, r.EscPresses), "no Reset: the Ability view was on top");
        CollectionAssert.AreEqual(new[] { "UI_LIFESKILL_MAKE_VIEW" }, H.Log.State.AdvisoryStack.ToArray(), "a tab switch inside the view");
    }

    [TestMethod]
    public void EachSubTabPress_LandsOnItsSubTab_FromTheLocatedStockAnchor()
    {
        Prepare();
        var expected = new Dictionary<string, PointI>
        {
            ["sub.ability"] = new(380, 226),
            ["sub.homestead"] = new(610, 226),
            ["sub.tailor"] = new(842, 226),
            ["sub.hyperweather"] = new(1072, 226),
        };
        foreach (var (step, point) in expected)
        {
            AssertStatus(ProcedureStatus.Succeeded, H.Run(Run(step, "sub.settle")));
            var click = H.Game.ClickRequests[^1];
            Assert.AreEqual(("stock.anchor", point, new PointI(1755, 577)), (click.TemplateId, click.ClientPoint, click.TemplateCenter), step);
            Assert.IsTrue(click.PatchVerify, step + ": a far anchor + offset press may use the off-screen anchor patch rule");
        }

        Assert.AreEqual(0, H.Keys(GameKey.Escape));
    }

    [TestMethod]
    public void TheDragRuns_PressInTheGridsGap_AndMove240Px_OneInputBatchEach()
    {
        Prepare();
        var top = H.Run(Run("top", "scroll.settle"));
        var scroll = H.Run(Run("scroll", "scroll.settle"));

        AssertStatus(ProcedureStatus.Succeeded, top);
        AssertStatus(ProcedureStatus.Succeeded, scroll);
        Assert.HasCount(2, H.Game.DragRequests);
        Assert.AreEqual((new PointI(940, 420), new PointI(0, 240)), (H.Game.DragRequests[0].Start, H.Game.DragRequests[0].Delta), "to the top: the content moves down");
        Assert.AreEqual((new PointI(940, 600), new PointI(0, -240)), (H.Game.DragRequests[1].Start, H.Game.DragRequests[1].Delta), "on: the content moves up");
        Assert.IsTrue(H.Game.DragRequests.All(d => d.TemplateId == "stock.anchor" && d.TemplateCenter == new PointI(1755, 577) && d.PatchVerify));
        Assert.AreEqual((1, 1), (top.InputBatches, scroll.InputBatches));
        Assert.AreEqual(0, H.Game.ClickCount, "a drag is not a click");
    }

    [TestMethod]
    public void TheTipRun_PressesTheTileFromTheStockAnchor_ThenOneEscClosesTheTip()
    {
        Prepare();
        var tip = Data().Single(p => p.Id == "inventory.stockTip");
        tip = tip with { Steps = tip.Steps.Select(s => s.Id == "tip.open" ? s with { Action = new ClickAction("stock.anchor", new PointI(1468 - 1755, 683 - 577)) } : s).ToArray() };
        H.Game.ClickScript.Enqueue(req => req.TemplateId == "stock.anchor" ? H.Game.SentClick(req, () => H.Log.Show("UI_ITEM_TIPS_PASS_TOUCH")) : null);

        var r = H.Run(tip);

        AssertStatus(ProcedureStatus.Succeeded, r);
        var click = H.Game.ClickRequests.Single();
        Assert.AreEqual(("stock.anchor", new PointI(1468, 683)), (click.TemplateId, click.ClientPoint), "the 6th column's icon = located anchor + the job's offset");
        Assert.AreEqual(1, H.Keys(GameKey.Escape), "one Esc, for the tip only");
        CollectionAssert.AreEqual(new[] { "UI_LIFESKILL_MAKE_VIEW" }, H.Log.State.AdvisoryStack.ToArray(), "the Ability view is on top again");
    }

    [TestMethod]
    public void TheBackRun_PressesTheMakeTab_AndSlideClicksOffRefusesWithZeroInputs()
    {
        Prepare();
        AssertStatus(ProcedureStatus.Succeeded, H.Run(Run("back", "back.settle")));
        Assert.AreEqual(("craft.make.tab", new PointI(1740, 300)), (H.Game.ClickRequests.Single().TemplateId, H.Game.ClickRequests.Single().ClientPoint));

        H.Switches.SlideClickEnabled = false;
        var inputs = H.Game.Inputs;
        AssertStatus(ProcedureStatus.Refused, H.Run(Run("sub.tailor", "sub.settle")));
        Assert.AreEqual(inputs, H.Game.Inputs);
    }
}
