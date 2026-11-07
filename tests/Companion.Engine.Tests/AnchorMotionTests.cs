using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.EngineNav.Testing;

namespace Companion.Engine.Tests;

/// <summary>
/// A screen anchor that bobs between captures (live 2026-10-14: the crafting page's hanging "Make" title moves about
/// 4 px up and down): a declared MotionTolerancePx lets it count as the same place; without one the step waits, and a
/// target that never settles is stepped back after a few tries instead of waiting until the budget runs out.
/// </summary>
[TestClass]
public sealed class AnchorMotionTests : ScenarioBase
{
    private const string Anchor = "craft.list.anchor";
    private static readonly PointI AnchorAt = new(485, 123);

    private static ProcedureDefinition Scroll() =>
        new("test.scroll", "drag the list", [new StepDefinition("scroll", StatePredicate.Any, new DragAction(Anchor, new PointI(-65, 737), new PointI(0, -280)), Expectation.Nothing, 3000, StepFlags.None)], ProcedureLimits.Default);

    private static MatchResult At(int dy) => new(Anchor, true, AnchorAt.Offset(0, dy), 0.97, 0.5, Math.Abs(dy), MatchFailure.None);

    private void Bobbing(int? tolerance)
    {
        H.Templates.Add(FakeTemplates.Def(Anchor, AnchorAt.X, AnchorAt.Y, TemplateStatus.Verified, 1, null, TargetTags.ScreenAnchor) with { MotionTolerancePx = tolerance });
        H.Vision.LocateScript[Anchor] = new Queue<MatchResult>(Enumerable.Range(0, 400).Select(k => At(k % 2 == 0 ? -4 : 1)));
    }

    [TestMethod]
    public void ABobbingAnchor_WithADeclaredTolerance_IsUsed()
    {
        Bobbing(8);

        var r = H.Run(Scroll());

        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(1, H.Game.DragCount);
        Assert.IsFalse(AttemptsOf(r, "scroll").Any(a => (a.DetailCode ?? "").StartsWith("template.moving:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ABobbingAnchor_WithoutATolerance_IsSteppedBackInsteadOfWaitingForever()
    {
        Bobbing(null);

        var r = H.Run(Scroll());

        Assert.AreEqual(0, H.Game.DragCount, "never sent while the anchor disagrees by more than the aim tolerance");
        AssertSoftEnd(HaltCode.PreconditionLostExhausted, r);
        var moving = AttemptsOf(r, "scroll").Count(a => (a.DetailCode ?? "").StartsWith("template.moving:", StringComparison.Ordinal));
        Assert.IsTrue(moving <= 10 * (ProcedureLimits.Default.MaxRestarts + 1), $"capped, not until the budget ran out ({moving})");
    }

    [TestMethod]
    public void RepeatedSlidMismatches_AreSteppedBack_InsteadOfUsingUpTheBudget()
    {
        // live 2026-10-14 (job 35e59bca): 30 SlidTemplateMismatch aborts in a row on one card ended in BudgetExceeded
        H.Templates.Add(FakeTemplates.Def(Anchor, AnchorAt.X, AnchorAt.Y, TemplateStatus.Verified, 1, null, TargetTags.ScreenAnchor));
        for (var k = 0; k < 200; k++)
        {
            H.Game.ClickScript.Enqueue(_ => H.Game.NotDeliveredClick(InputDisposition.AbortedAfterFocus, AbortReason.SlidTemplateMismatch, InputPhase.P3SlideToDown, 4));
        }
        var card = new ProcedureDefinition("test.card", "select a card", [new StepDefinition("card", StatePredicate.Any, new ClickAction(Anchor, new PointI(-121, 546)), Expectation.Nothing, 3000, StepFlags.None)], ProcedureLimits.Default);

        var r = H.Run(card);

        // pauses for the cursor to move first (3 x 30 s), then ends Deferred: no press reached the game, so no input was
        // used and no HALT is latched (audit R1.1)
        AssertStatus(ProcedureStatus.Deferred, r);
        Assert.AreEqual(H.Game.Keys(GameKey.Escape), r.InputBatches, "only delivered Escs are inputs");
        Assert.IsTrue(r.Attempts.Count(a => a.DetailCode == "mismatch.pause") >= 1, "paused before giving up");
        var mismatches = r.Attempts.Count(a => a.Outcome == StepOutcome.Popup);
        Assert.IsTrue(mismatches <= 5 * 4 * (ProcedureLimits.Default.MaxRestarts + 1), $"capped, not endless ({mismatches})");
    }

    [TestMethod]
    public void AQuickAttributionClick_ReadsTheLogForWindowsBriefly_NotTheFullWindow()
    {
        // 2026-10-31 (the user: fast Tea House auto-play): a board press waited the full 1.5 s for windows it might open
        H.Templates.Add(FakeTemplates.Def(Anchor, AnchorAt.X, AnchorAt.Y, TemplateStatus.Verified, 1, null, TargetTags.ScreenAnchor));
        TimeSpan AfterTheClick(StepFlags flags)
        {
            var press = new ProcedureDefinition("test.press" + (int)flags, "a board press", [new StepDefinition("sq", StatePredicate.Any, new ClickAction(Anchor, new PointI(-121, 546)), Expectation.Nothing, 3000, flags)], ProcedureLimits.Default);
            var r = H.Run(press);
            AssertStatus(ProcedureStatus.Succeeded, r);
            return H.Clock.Elapsed - H.Game.Timeline[^1].At;
        }

        var full = AfterTheClick(StepFlags.None);
        var quick = AfterTheClick(StepFlags.QuickAttribution);
        Assert.IsTrue(full >= HardRules.AttributionWindow, $"full {full}");
        Assert.IsTrue(quick < full - TimeSpan.FromMilliseconds(800), $"quick {quick}, full {full}");
    }

    private static ProcedureDefinition QuickPress(StatePredicate pre) =>
        new("test.quick", "a board press", [new StepDefinition("sq", pre, new ClickAction(Anchor, new PointI(-121, 546)), Expectation.Nothing, 3000, StepFlags.QuickAttribution)], ProcedureLimits.Default);

    [TestMethod]
    public void TheNextPress_WaitsForTheLastQuickClicksFullWindow()
    {
        // safety review 2026-10-31: the rest of the full attribution window is read before the next input
        H.Templates.Add(FakeTemplates.Def(Anchor, AnchorAt.X, AnchorAt.Y, TemplateStatus.Verified, 1, null, TargetTags.ScreenAnchor));
        AssertStatus(ProcedureStatus.Succeeded, H.Run(QuickPress(StatePredicate.Any)));
        AssertStatus(ProcedureStatus.Succeeded, H.Run(QuickPress(StatePredicate.Any)));

        var clicks = H.Game.Timeline.Where(t => t.Call.StartsWith("click:", StringComparison.Ordinal)).ToList();
        Assert.HasCount(2, clicks);
        Assert.IsTrue(clicks[1].At - clicks[0].At >= HardRules.AttributionWindow, $"{clicks[1].At - clicks[0].At}");
        Assert.IsTrue(H.Journal.OfKind(JournalKind.Decision).Any(d => FakeJournal.Str(d, "event") == "late.check"));
    }

    [TestMethod]
    public void AForbiddenUiAQuickClickOpenedAfterItsShortRead_IsTheSameHalt_BeforeTheNextPress()
    {
        // safety review 2026-10-31 (finding 1): a shop the press opened 1.1 s after it is ours, not foreign
        H.Templates.Add(FakeTemplates.Def(Anchor, AnchorAt.X, AnchorAt.Y, TemplateStatus.Verified, 1, null, TargetTags.ScreenAnchor));
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, () => H.Clock.At(TimeSpan.FromMilliseconds(1000), () => H.Log.Show("UI_SHOP_MAIN"))));
        AssertStatus(ProcedureStatus.Succeeded, H.Run(QuickPress(StatePredicate.Any)));

        var r = H.Run(QuickPress(StatePredicate.Any));

        AssertHalt(HaltCode.ForbiddenUi, r);
        Assert.AreEqual(1, H.Game.ClickCount, "the second press never went out");
        Assert.AreEqual(1, H.Game.Keys(GameKey.Escape), "exactly one Esc: the compensation for the shop");
    }

    [TestMethod]
    public void APayLineAfterTheShortRead_IsTheSameHalt()
    {
        H.Templates.Add(FakeTemplates.Def(Anchor, AnchorAt.X, AnchorAt.Y, TemplateStatus.Verified, 1, null, TargetTags.ScreenAnchor));
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, () => H.Clock.At(TimeSpan.FromMilliseconds(1000), H.Log.Pay)));
        AssertStatus(ProcedureStatus.Succeeded, H.Run(QuickPress(StatePredicate.Any)));

        var r = H.Run(QuickPress(StatePredicate.Any));

        AssertHalt(HaltCode.ForbiddenUi, r);
        Assert.AreEqual(1, H.Game.ClickCount);
    }

    [TestMethod]
    public void APopupAQuickClickOpenedLate_EndsTheNextRun_WithoutAnEsc()
    {
        // safety review 2026-10-31 (finding 2): no stray-popup Esc and no second press over a window our press opened
        H.Templates.Add(FakeTemplates.Def(Anchor, AnchorAt.X, AnchorAt.Y, TemplateStatus.Verified, 1, null, TargetTags.ScreenAnchor));
        H.Log.Show("UI_LIFESKILL_MAKE_VIEW");
        var onMake = new StatePredicate(TopUi: "UI_LIFESKILL_MAKE_VIEW");
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, () => H.Clock.At(TimeSpan.FromMilliseconds(1000), () => H.Log.Show("UI_HOME_PLANT_VIEW"))));
        AssertStatus(ProcedureStatus.Succeeded, H.Run(QuickPress(onMake)));

        var r = H.Run(QuickPress(onMake));

        Assert.AreEqual("late.opened:UI_HOME_PLANT_VIEW", r.EndDetail);
        Assert.AreEqual(1, H.Game.ClickCount);
        Assert.AreEqual(0, H.Game.Keys(GameKey.Escape));
    }

    [TestMethod]
    public void InFront_AWindowTheUserOpensAfterAQuickClick_IsNotTakenForOurs()
    {
        // re-review 2026-11-02 (foreground mode): the late read must not credit the user's own forbidden window to our
        // press (a ForbiddenUi HALT plus an Esc on the user's window)
        H.Templates.Add(FakeTemplates.Def(Anchor, AnchorAt.X, AnchorAt.Y, TemplateStatus.Verified, 1, null, TargetTags.ScreenAnchor));
        H.Game.ForegroundMode = true;
        H.Game.UserActivates();
        H.Clock.Advance(TimeSpan.FromSeconds(10));
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, () => H.Clock.At(TimeSpan.FromMilliseconds(1000), () =>
        {
            H.Game.LastGameUseMono = H.Clock.Monotonic; // the user's own click in the game
            H.Log.Show("UI_SHOP_MAIN");
        })));
        AssertStatus(ProcedureStatus.Succeeded, H.Run(QuickPress(StatePredicate.Any)));

        var r = H.Run(QuickPress(StatePredicate.Any));

        Assert.AreNotEqual(ProcedureStatus.Halted, r.Status, r.EndDetail);
        Assert.AreEqual(0, H.Game.Keys(GameKey.Escape), "no Esc on the user's window");
        Assert.AreEqual(1, H.Game.ClickCount, "no second press over it");
    }

    [TestMethod]
    public void AnAttemptKeepsItsOwnCause_WhenItsDecisionHasADetailOfItsOwn()
    {
        // 2026-10-31 live: the favourites scroll-back looked for "template.notFound" in DetailCode, which the step-back and
        // the exhausted end had overwritten ("preLost.stepBack", "exhausted.PreconditionLostExhausted"), so it never ran
        H.Templates.Add(FakeTemplates.Def(Anchor, AnchorAt.X, AnchorAt.Y, TemplateStatus.Verified, 1, null, TargetTags.ScreenAnchor));
        H.Vision.LocateScript[Anchor] = new Queue<MatchResult>(Enumerable.Range(0, 400).Select(_ => new MatchResult(Anchor, false, AnchorAt, 0.1, 0, 0, MatchFailure.BelowThreshold)));

        var r = H.Run(QuickPress(StatePredicate.Any));

        Assert.AreEqual(0, r.Clicks);
        var last = r.Attempts.Last(a => a.StepId == "sq");
        Assert.AreNotEqual("template.notFound:" + Anchor, last.DetailCode, "the decision's own detail: " + last.DetailCode);
        Assert.AreEqual("template.notFound:" + Anchor, last.Cause);
        Assert.IsTrue(last.CauseStartsWith("template.notFound"));
    }

    [TestMethod]
    public void TheLocatePair_IsTheStableGapApart_NotTheCaptureThrottle()
    {
        // 2026-10-31 sweep: each click's two locate captures waited the 2/s capture throttle (~0.59 s); F07 asks for 300 ms
        H.Templates.Add(FakeTemplates.Def(Anchor, AnchorAt.X, AnchorAt.Y, TemplateStatus.Verified, 1, null, TargetTags.ScreenAnchor));
        var start = H.Clock.Elapsed;
        AssertStatus(ProcedureStatus.Succeeded, H.Run(QuickPress(StatePredicate.Any)));
        var clickAt = H.Game.Timeline.Single(t => t.Call.StartsWith("click:", StringComparison.Ordinal)).At;
        Assert.IsTrue(clickAt - start < TimeSpan.FromMilliseconds(500) + HardRules.StableCaptureGap, $"locate took {clickAt - start}");
    }

    [TestMethod]
    public void TheTolerance_AppliesOnlyToScreenAnchors_AndIsClamped()
    {
        var d = FakeTemplates.Def(Anchor, 1, 1, TemplateStatus.Verified, 1, null, TargetTags.ScreenAnchor);
        Assert.AreEqual(HardRules.AimTolerancePx, d.EffectiveMotionTolerancePx, "nothing declared");
        Assert.AreEqual(6, (d with { MotionTolerancePx = 6 }).EffectiveMotionTolerancePx);
        Assert.AreEqual(HardRules.MaxAnchorMotionPx, (d with { MotionTolerancePx = 50 }).EffectiveMotionTolerancePx);
        Assert.AreEqual(HardRules.AimTolerancePx, (d with { MotionTolerancePx = 0 }).EffectiveMotionTolerancePx);
        Assert.AreEqual(HardRules.AimTolerancePx, (d with { Tags = TargetTags.None, MotionTolerancePx = 8 }).EffectiveMotionTolerancePx, "click targets keep the strict aim");
    }
}

/// <summary>
/// A stray Esc-safe popup right above the screen a step needs (live 2026-10-15: a press on the Make view in the homestead
/// also opened the farm-plot menu behind it) is closed with one Esc and the step retried, instead of resetting everything.
/// </summary>
[TestClass]
public sealed class StrayPopupTests : ScenarioBase
{
    private static ProcedureDefinition NeedsMakeView() =>
        new("test.make", "a step on the Make view", [new StepDefinition("look", new StatePredicate(TopUi: "UI_LIFESKILL_MAKE_VIEW"), new WaitAction(10), Expectation.Nothing, 3000, StepFlags.None)], ProcedureLimits.Default);

    [TestMethod]
    public void AStrayPlotMenuAboveTheMakeView_IsClosedWithOneEsc_ThenTheStepRuns()
    {
        H.Log.Show("UI_LIFESKILL_MAKE_VIEW");
        H.Log.Show("UI_HOME_PLANT_VIEW");

        var r = H.Run(NeedsMakeView());

        AssertStatus(ProcedureStatus.Succeeded, r);
        var popup = r.Attempts.Single(a => a.Outcome == StepOutcome.Popup);
        Assert.AreEqual(("UI_HOME_PLANT_VIEW", RecoveryAction.CloseThenRetry, "popup.esc"), (popup.UiSeen, popup.Decision, popup.DetailCode));
        Assert.AreEqual(1, H.Game.Keys(GameKey.Escape), "only the stray popup is closed");
    }

    [TestMethod]
    public void AStrayPopupOverTheScreenAnOpenStepWouldOpen_IsClosed_AndTheStepSkips()
    {
        // live 2026-10-15: a team recommendation popup over the Ability view made the open step reset and reopen it
        H.Log.Show("UI_LIFESKILL_MAKE_VIEW");
        H.Log.Show("UI_TEAM_REOCMMEND_VIEW");
        var open = new ProcedureDefinition("test.open", "open unless open", [new StepDefinition("open", StatePredicate.Any, new WaitAction(10),
            new Expectation([new UiExpectation(UiVerb.Show, "UI_LIFESKILL_MAKE_VIEW")]), 3000, StepFlags.SkipIfSatisfied)], ProcedureLimits.Default);

        var r = H.Run(open);

        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual("UI_TEAM_REOCMMEND_VIEW", r.Attempts.Single(a => a.Outcome == StepOutcome.Popup).UiSeen);
        Assert.AreEqual(1, H.Game.Keys(GameKey.Escape), "only the popup is closed, nothing is reopened");
        Assert.AreEqual("skip.satisfied", r.Attempts.Last().DetailCode);
    }

    [TestMethod]
    public void AnythingElseOnTop_IsStillAPreconditionProblem()
    {
        H.Log.Show("UI_BAG_MAIN"); // Esc-safe, but the Make view is not right below it

        var r = H.Run(NeedsMakeView());

        Assert.IsFalse(r.Attempts.Any(a => a.Outcome == StepOutcome.Popup), "not a stray popup: the precondition rules apply");
    }
}
