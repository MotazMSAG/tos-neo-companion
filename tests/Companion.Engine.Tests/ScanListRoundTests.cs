using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.EngineNav.Testing;

namespace Companion.Engine.Tests;

/// <summary>
/// The crafting feature's ScanRecipeList runs (crafting.scanList) through the real engine against the simulated game:
/// the card click and both drags are placed from the located list anchor ("Make" title), the drags are one input batch
/// each, nothing is ever committed, and the runs need no switch and no permission.
/// </summary>
[TestClass]
public sealed class ScanListRoundTests : ScenarioBase
{
    private static ProcedureDefinition ScanData()
    {
        var file = Harness.DataFile(Path.Combine("features", "crafting", "procedures.json"));
        return JsonSerializer.Deserialize<ProcedureDefinition[]>(File.ReadAllBytes(file), ContractJson.Options)!.Single(p => p.Id == "crafting.scanList");
    }

    /// <summary>A run as the ScanRecipeList handler builds it (open skipped while the Make view is on top).</summary>
    private static ProcedureDefinition Run(params string[] steps)
    {
        var data = ScanData();
        return data with
        {
            Steps = data.Steps
                .Where(s => s.Id is "open" or "announce.close" || steps.Contains(s.Id) || (steps.Contains("read.*") && s.Action is ReadAction))
                .Select(s => s.Id == "open" ? s with { Flags = s.Flags | StepFlags.SkipIfSatisfied } : s)
                .ToArray(),
        };
    }

    private void Prepare()
    {
        H.Templates.Verify("menu.ability");
        H.Templates.Add(FakeTemplates.Def("craft.list.anchor", 485, 123, TemplateStatus.Verified, 1, null, TargetTags.ScreenAnchor) with { FireOn = FireOn.Up, SearchRadius = 16 });
        H.Readers.SetReady("craft.product", "UI_LIFESKILL_MAKE_VIEW");
        H.Readers.SetReady("craft.pairs", "UI_LIFESKILL_MAKE_VIEW");
        H.Switches.Crafting = false;
        H.Log.Show("UI_LIFESKILL_MAKE_VIEW"); // the Make view is open
    }

    [TestMethod]
    public void ACardRun_ClicksTheCardFromTheListAnchor_AndReadsBothReaders()
    {
        Prepare();
        var card = Run("card", "settle", "read.*");
        card = card with { Steps = card.Steps.Select(s => s.Id == "card" ? s with { Action = new ClickAction("craft.list.anchor", new PointI(331, 732)) } : s).ToArray() };

        var r = H.Run(card);

        AssertStatus(ProcedureStatus.Succeeded, r);
        var click = H.Game.ClickRequests.Single();
        Assert.AreEqual(("craft.list.anchor", new PointI(816, 855), FireOn.Up), (click.TemplateId, click.ClientPoint, click.FireOn), "the 5th card of the Recent row = located anchor + offset");
        Assert.AreEqual(new PointI(485, 123), click.TemplateCenter, "verified while slid at the anchor itself");
        Assert.AreEqual(0, H.Game.DragCount);
        CollectionAssert.AreEquivalent(new[] { "product", "pairs" }, r.Reads.Keys.ToArray());
        Assert.AreEqual(0, H.Keys(GameKey.Escape), "the Make view was on top: no Reset, no Esc");
    }

    [TestMethod]
    public void TheDragRuns_PressInTheListAndMoveTwoRows_OneInputBatchEach()
    {
        Prepare();
        var top = H.Run(Run("top", "scroll.settle"));
        var scroll = H.Run(Run("scroll", "scroll.settle"));

        AssertStatus(ProcedureStatus.Succeeded, top);
        AssertStatus(ProcedureStatus.Succeeded, scroll);
        Assert.HasCount(2, H.Game.DragRequests);
        Assert.AreEqual((new PointI(420, 420), new PointI(0, 280)), (H.Game.DragRequests[0].Start, H.Game.DragRequests[0].Delta), "to the top: the content moves down");
        Assert.AreEqual((new PointI(420, 860), new PointI(0, -280)), (H.Game.DragRequests[1].Start, H.Game.DragRequests[1].Delta), "scrolling on: the content moves up");
        Assert.IsTrue(H.Game.DragRequests.All(d => d.TemplateId == "craft.list.anchor" && d.TemplateCenter == new PointI(485, 123)));
        Assert.AreEqual((1, 1), (top.InputBatches, scroll.InputBatches));
        Assert.AreEqual("drag.done", scroll.Attempts.Single(a => a.StepId == "scroll").DetailCode);
        Assert.AreEqual(0, H.Game.ClickCount, "a drag is not a click");
    }

    [TestMethod]
    public void ACardRun_PassesItsLocatedFrame_AndOptsIntoThePatchRule()
    {
        // Live run 2026-10-14 (job 35e59bca): the 2nd-row card slid the list anchor above the screen; GameIO may then check
        // the card's own patch against the frame the anchor was located in (SPEC 7.3 off-screen anchor patch rule).
        Prepare();
        var r = H.Run(Run("card", "settle", "read.*"));

        AssertStatus(ProcedureStatus.Succeeded, r);
        var click = H.Game.ClickRequests.Single();
        Assert.AreEqual((new PointI(364, 498), new PointI(485, 123)), (click.ClientPoint, click.TemplateCenter), "anchor + (-121,375)");
        Assert.IsTrue(click.PatchVerify);
        Assert.IsFalse(click.NavFixed);
        Assert.IsNotNull(click.LocatedFrame);
        Assert.IsFalse(click.LocatedFrame.WhileSlid);
        Assert.AreEqual(click.LocatedAtMonotonic, click.LocatedFrame.CapturedAtMonotonic, "the frame the request's locate time names");
        Assert.AreEqual(click.LocatedAtLog, click.LocatedFrame.LogAt);
        var input = H.Journal.OfKind(JournalKind.Input).Single(j => j.StepId == "card");
        Assert.AreEqual(true, input.Fields["patchVerify"]);
        Assert.AreEqual(false, input.Fields["navFixed"]);
    }

    [TestMethod]
    public void TheDragRuns_PassTheirLocatedFrame_AndOptIn()
    {
        Prepare();
        AssertStatus(ProcedureStatus.Succeeded, H.Run(Run("top", "scroll.settle")));
        AssertStatus(ProcedureStatus.Succeeded, H.Run(Run("scroll", "scroll.settle")));

        Assert.HasCount(2, H.Game.DragRequests);
        foreach (var d in H.Game.DragRequests)
        {
            Assert.IsTrue(d.PatchVerify, d.StepId);
            Assert.IsNotNull(d.LocatedFrame, d.StepId);
            Assert.AreEqual(d.LocatedAtMonotonic, d.LocatedFrame.CapturedAtMonotonic, d.StepId);
            Assert.AreEqual(d.LocatedAtLog, d.LocatedFrame.LogAt, d.StepId);
        }

        Assert.IsTrue(H.Journal.OfKind(JournalKind.Input).Where(j => j.StepId is "top" or "scroll").All(j => true.Equals(j.Fields["patchVerify"])));
    }

    [TestMethod]
    public void SlideClicksOff_RefusesTheDragRunsWithZeroInputs()
    {
        Prepare();
        H.Switches.SlideClickEnabled = false;
        AssertStatus(ProcedureStatus.Refused, H.Run(Run("scroll", "scroll.settle")));
        Assert.AreEqual(0, H.Game.Inputs);
    }
}
