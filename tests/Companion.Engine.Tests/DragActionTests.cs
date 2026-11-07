using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.EngineNav.Testing;

namespace Companion.Engine.Tests;

/// <summary>
/// The engine's DragAction (SPEC 6.4, 7.3a) against the simulated game: located anchor + offset, both ends inside the
/// client and reachable, one input batch, journaled like a click, never retried once delivered (the caller checks the
/// effect), refused before any input when it cannot run safely.
/// </summary>
[TestClass]
public sealed class DragActionTests : ScenarioBase
{
    private const string Anchor = "craft.list.anchor";
    private static readonly PointI AnchorAt = new(485, 123);
    private static readonly PointI Offset = new(-65, 737);
    private static readonly PointI Up280 = new(0, -280);

    private void Prepare(TemplateStatus status = TemplateStatus.Verified, TargetTags tags = TargetTags.ScreenAnchor, AutomationSwitch gate = AutomationSwitch.None) =>
        H.Templates.Add(FakeTemplates.Def(Anchor, AnchorAt.X, AnchorAt.Y, status, 1, null, tags, gate));

    private static ProcedureDefinition Scroll(PointI? offset = null, PointI? delta = null, StepFlags flags = StepFlags.None, Expectation? expect = null) =>
        new("test.scroll", "drag the list", [new StepDefinition("scroll", StatePredicate.Any, new DragAction(Anchor, offset ?? Offset, delta ?? Up280), expect ?? Expectation.Nothing, 3000, flags)], ProcedureLimits.Default);

    [TestMethod]
    public void ADrag_PressesAtTheLocatedAnchorPlusOffset_AndIsOneInputBatch()
    {
        Prepare();
        var r = H.Run(Scroll());

        AssertStatus(ProcedureStatus.Succeeded, r);
        var req = H.Game.DragRequests.Single();
        Assert.AreEqual((Anchor, new PointI(420, 860), AnchorAt, Up280), (req.TemplateId, req.Start, req.TemplateCenter, req.Delta));
        Assert.IsFalse(req.NearDenyZone, "no deny zone on the Make view");
        Assert.AreEqual((1, 1, 0), (r.InputBatches, r.Clicks, H.Game.ClickCount));
        Assert.AreEqual("drag.done", AttemptsOf(r, "scroll").Single().DetailCode);

        var input = H.Journal.OfKind(JournalKind.Input).Single();
        Assert.AreEqual(("drag", "14", "14", "-280"), (FakeJournal.Str(input, "kind"), FakeJournal.Str(input, "steps"), FakeJournal.Str(input, "stepsDone"), FakeJournal.Str(input, "dragDy")));
        Assert.IsTrue(H.Journal.OfKind(JournalKind.Slide).Any(), "slide evidence journaled like a click's");
        Assert.AreEqual("drag", FakeJournal.Str(H.Journal.OfKind(JournalKind.StepStart).Single(), "action"));
    }

    [TestMethod]
    public void BothEndsMustBeReady_BeforeTheDragIsSent()
    {
        Prepare();
        var queried = new List<PointI?>();
        var endBusy = 3;
        H.Game.ReadinessOverride = q =>
        {
            if (q.Kind != InputKind.Click)
            {
                return null;
            }

            queried.Add(q.TargetClientPoint);
            return q.TargetClientPoint == new PointI(420, 580) && endBusy-- > 0 ? H.Game.Report(NotReadyReason.CursorMoving) : null;
        };

        var r = H.Run(Scroll());

        AssertStatus(ProcedureStatus.Succeeded, r);
        CollectionAssert.Contains(queried, (PointI?)new PointI(420, 860));
        CollectionAssert.Contains(queried, (PointI?)new PointI(420, 580));
        Assert.AreEqual(1, H.Game.DragCount, "sent once, after the release point became reachable");
    }

    [TestMethod]
    public void ADeliveredDrag_IsNeverRetried_EvenWhenCutShort()
    {
        Prepare();
        H.Game.DragScript.Enqueue(req => H.Game.SentDrag(req, stepsDone: 3, movedAfterDown: true));

        var r = H.Run(Scroll());

        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(1, H.Game.DragCount);
        Assert.AreEqual("drag.partial", AttemptsOf(r, "scroll").Single().DetailCode, "the caller measures how far the list moved");
    }

    [TestMethod]
    public void ADragThatSentNothing_WaitsAndIsRetried()
    {
        Prepare();
        H.Game.DragScript.Enqueue(req => H.Game.NotDeliveredDrag(req, InputDisposition.NotSent, AbortReason.NotReady, 0));

        var r = H.Run(Scroll());

        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(2, H.Game.DragCount);
        CollectionAssert.AreEqual(new[] { StepOutcome.NotSent, StepOutcome.Ok }, H.Outcomes(r).ToArray());
    }

    [TestMethod]
    public void AReleaseThatOpensAForbiddenUi_Halts()
    {
        Prepare();
        H.Game.OnDrag = _ => H.Log.Show("UI_RECHARGE");

        var r = H.Run(Scroll());

        AssertHalt(HaltCode.ForbiddenUi, r);
        Assert.AreEqual(1, H.Game.DragCount);
    }

    [TestMethod]
    public void AReleasePointOutsideTheClient_IsRefusedWithZeroInputs()
    {
        Prepare();
        var r = H.Run(Scroll(new PointI(-65, 150))); // press at (420,273), release 7 px above the client
        AssertStatus(ProcedureStatus.Refused, r);
        Assert.AreEqual(0, H.Game.Inputs);
        Assert.AreEqual("drag.outsideClient", r.Attempts.Last().DetailCode);
    }

    [TestMethod]
    public void AProvisionalAnchor_EndsNeedsSurvey_AndSlideClicksOff_IsRefused_BothWithZeroInputs()
    {
        Prepare(TemplateStatus.Provisional);
        AssertStatus(ProcedureStatus.NeedsSurvey, H.Run(Scroll()));
        Assert.AreEqual(0, H.Game.Inputs);

        Prepare();
        H.Switches.SlideClickEnabled = false;
        AssertStatus(ProcedureStatus.Refused, H.Run(Scroll()));
        Assert.AreEqual(0, H.Game.Inputs);
    }

    [TestMethod]
    public void ACommitTemplate_IsNeverADragAnchor()
    {
        Prepare(tags: TargetTags.Commit, gate: AutomationSwitch.Crafting);
        H.Switches.Crafting = true;
        var r = H.Run(Scroll(), JobKind.Commit);
        AssertStatus(ProcedureStatus.Refused, r);
        Assert.AreEqual(0, H.Game.Inputs);
    }

    [TestMethod]
    public void MalformedDragSteps_AreInvalidData()
    {
        Prepare();
        var bad = new[]
        {
            Scroll(delta: new PointI(0, 0)),
            Scroll(delta: new PointI(0, -(HardRules.MaxDragDistancePx + 1))),
            Scroll(offset: new PointI(2000, 0)),
            Scroll(expect: new Expectation(Companion.Contracts.Logs.UiExpectation.Opened("UI_BAG_MAIN"))),
            Scroll(flags: StepFlags.Commit) with { Gate = AutomationSwitch.Crafting, Kind = ProcedureKind.Commit },
        };
        foreach (var p in bad)
        {
            var r = H.Run(p);
            AssertStatus(ProcedureStatus.Refused, r);
            Assert.AreEqual(0, H.Game.Inputs);
        }
    }
}
