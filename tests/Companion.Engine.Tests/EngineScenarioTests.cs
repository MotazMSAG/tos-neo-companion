using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Host;
using Companion.Contracts.Logs;
using Companion.Contracts.Perception;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.Engine;
using Companion.EngineNav.Testing;

namespace Companion.Engine.Tests;

/// <summary>Base: a fresh harness per test; every journal record written must pass the schema lint (SPEC 13 #30).</summary>
public abstract class ScenarioBase
{
    protected Harness H { get; private set; } = null!;

    [TestInitialize]
    public void InitHarness() => H = new Harness();

    [TestCleanup]
    public void LintJournal() => H.AssertJournalClean();

    protected static void AssertStatus(ProcedureStatus expected, ProcedureResult r) =>
        Assert.AreEqual(expected, r.Status, $"status; outcomes: {string.Join(",", r.Attempts.Select(a => a.StepId + ":" + a.Outcome + ">" + a.Decision + "(" + a.DetailCode + ")"))}");

    protected static void AssertHalt(HaltCode code, ProcedureResult r)
    {
        AssertStatus(ProcedureStatus.Halted, r);
        Assert.IsNotNull(r.Halt);
        Assert.AreEqual(code, r.Halt!.Code);
    }

    /// <summary>An exhausted ladder's soft end (FALLBACKS.md): Deferred with the would-be HALT code, no latched HALT.</summary>
    protected static void AssertSoftEnd(HaltCode code, ProcedureResult r)
    {
        AssertStatus(ProcedureStatus.Deferred, r);
        Assert.IsNull(r.Halt);
        Assert.AreEqual(StepOutcome.Deferred, r.FinalOutcome);
        Assert.AreEqual("exhausted." + code, r.EndDetail);
    }

    protected IEnumerable<StepAttemptRecord> AttemptsOf(ProcedureResult r, string stepId) => r.Attempts.Where(a => a.StepId == stepId);
}

[TestClass]
public sealed class BasicScenarioTests : ScenarioBase
{
    [TestMethod]
    public void OpenBagKey_HappyPath_ResetBEsc()
    {
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(2, H.Keys(GameKey.Escape));
        Assert.AreEqual(1, H.Keys(GameKey.B));
        Assert.AreEqual(0, H.Game.ClickCount);
        Assert.AreEqual(3, r.InputBatches);
        Assert.AreEqual(2, r.EscPresses);
        CollectionAssert.AreEqual(new[] { StepOutcome.Ok, StepOutcome.Ok, StepOutcome.Ok }, r.Attempts.Select(a => a.Outcome).ToArray());
        Assert.AreEqual(EngineState.Idle, H.Runner.Status.State);
        Assert.IsTrue(H.Journal.OfKind(JournalKind.AutoCollect).Count() >= 2, "auto-collect journaled before and after");
    }

    [TestMethod]
    public void OpenBagClick_HappyPath_UsesNearDenyZoneStillness()
    {
        var r = H.Run("OpenBagClick");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(1, H.Game.ClickCount);
        var req = H.Game.ClickRequests.Single();
        Assert.AreEqual("hud.bag", req.TemplateId);
        Assert.AreEqual(new PointI(1748, 312), req.ClientPoint);
        Assert.IsTrue(req.NearDenyZone, "timed-offer zone is about 55 px away (SPEC 9)");
        Assert.IsTrue(H.Journal.OfKind(JournalKind.Slide).Any(), "slide marks journaled after the slide-back");
    }

    [TestMethod]
    public void UnknownProcedure_IsRefused()
    {
        var r = H.Run("NoSuchProcedure");
        AssertStatus(ProcedureStatus.Refused, r);
        Assert.AreEqual(0, H.Game.Inputs);
    }

    [TestMethod]
    public void NavigateAction_InlinesThePathAndArrives()
    {
        var proc = new ProcedureDefinition("NavBag", "navigate to the bag",
            [new StepDefinition("go", StatePredicate.Any, new NavigateAction("bag"), Expectation.Nothing)], ProcedureLimits.Default);
        var r = H.Run(proc);
        AssertStatus(ProcedureStatus.Succeeded, r);
        // ties are broken by button id: bag.click.c < bag.key.c
        Assert.AreEqual(1, H.Game.ClickCount);
        Assert.AreEqual("UI_BAG_MAIN", H.Log.State.AdvisoryTop);
        Assert.IsTrue(r.Attempts.Any(a => a.StepId.StartsWith("nav.hud.collapsed.bag/hop1", StringComparison.Ordinal)));
    }
}

[TestClass]
public sealed class CursorAndAbortScenarioTests : ScenarioBase
{
    // (1) cursor moved before the click: NotSent, uncounted, patience -> Park
    [TestMethod]
    public void S01_CursorNeverStill_NotSentUncounted_PatienceParks()
    {
        H.Game.ReadinessOverride = q => q.Kind == InputKind.Click ? H.Game.Report(NotReadyReason.CursorMoving) : null;
        using var cts = new CancellationTokenSource();
        H.Clock.At(TimeSpan.FromMinutes(13), cts.Cancel);
        var r = H.Run("OpenBagClick", ct: cts.Token);
        AssertStatus(ProcedureStatus.Cancelled, r);
        Assert.AreEqual(0, H.Game.ClickCount, "no click while the cursor is not still");
        var open = AttemptsOf(r, "open").ToList();
        Assert.IsTrue(open.Count > 10);
        Assert.IsTrue(open.All(a => a.Outcome == StepOutcome.NotSent && a.Class == OutcomeClass.Wait));
        Assert.AreEqual(RecoveryAction.Park, open[^1].Decision);
        Assert.IsTrue(open.Take(open.Count - 1).All(a => a.Decision == RecoveryAction.WaitThenRetry));
        Assert.IsTrue(H.Journal.OfKind(JournalKind.Park).Any(p => FakeJournal.Str(p, "event") == "start"));
        Assert.AreEqual(1, r.InputBatches, "only the reset Esc");
    }

    // (1) aborted after focus: back-off 2, 4, 8 ... 60 s and never more than 6 focus pairs per minute
    [TestMethod]
    public void S01_AbortedAfterFocus_BacksOff_AndRespectsFocusPairCap()
    {
        for (var k = 0; k < 8; k++)
        {
            H.Game.ClickScript.Enqueue(_ => H.Game.NotDeliveredClick(InputDisposition.AbortedAfterFocus, AbortReason.CursorMoved, InputPhase.P2FocusToSlide, 4));
        }
        var r = H.Run("OpenBagClick");
        AssertStatus(ProcedureStatus.Succeeded, r);
        var clicks = H.Game.Timeline.Where(t => t.Call.StartsWith("click:", StringComparison.Ordinal)).Select(t => t.At).ToList();
        Assert.AreEqual(9, clicks.Count);
        double[] backoff = [2, 4, 8, 16, 32, 60, 60, 60];
        for (var k = 1; k < clicks.Count; k++)
        {
            Assert.IsTrue((clicks[k] - clicks[k - 1]).TotalSeconds >= backoff[k - 1], $"gap {k}: {(clicks[k] - clicks[k - 1]).TotalSeconds}");
        }
        foreach (var t in clicks)
        {
            Assert.IsTrue(clicks.Count(o => o >= t && o < t + TimeSpan.FromMinutes(1)) <= HardRules.MaxFocusPairsPerMinute);
        }
        var open = AttemptsOf(r, "open").ToList();
        Assert.AreEqual(8, open.Count(a => a.Outcome == StepOutcome.AbortedAfterFocus && a.Decision == RecoveryAction.WaitThenRetry));
        Assert.AreEqual(1, r.Clicks, "only the delivered click counts as a click");
    }

    [TestMethod]
    public void S01_AbortedAfterFocus_Forever_ParksAfterPatience()
    {
        for (var k = 0; k < 40; k++)
        {
            H.Game.ClickScript.Enqueue(_ => H.Game.NotDeliveredClick(InputDisposition.AbortedAfterFocus, AbortReason.CursorMoved, InputPhase.P2FocusToSlide, 4));
        }
        using var cts = new CancellationTokenSource();
        H.Clock.At(TimeSpan.FromMinutes(11), cts.Cancel);
        var r = H.Run("OpenBagClick", ct: cts.Token);
        AssertStatus(ProcedureStatus.Cancelled, r);
        Assert.IsTrue(H.Journal.OfKind(JournalKind.Park).Any(p => FakeJournal.Str(p, "reason") == nameof(StepOutcome.AbortedAfterFocus)));
        Assert.IsTrue(r.Attempts.Where(a => a.Outcome == StepOutcome.AbortedAfterFocus).All(a => a.Class == OutcomeClass.Wait));
        Assert.IsTrue(r.InputBatches <= 25);
    }

    // (2) cursor moved in P4: Uncertain -> settle -> Ok (late), never a second click
    [TestMethod]
    public void S02_CursorMovedInP4_SettleFindsTheUi_Ok()
    {
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, () => H.Clock.At(TimeSpan.FromSeconds(5), () => H.Log.Show("UI_BAG_MAIN")), movedInP4: true));
        var r = H.Run("OpenBagClick");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(1, H.Game.ClickCount);
        var open = AttemptsOf(r, "open").Single();
        Assert.AreEqual(OutcomeClass.Ok, open.Class);
    }

    // (2) Uncertain four times uncounted, the fifth is counted
    [TestMethod]
    public void S02_CursorMovedInP4_FourUncounted_FifthCounted()
    {
        for (var k = 0; k < 5; k++)
        {
            H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, null, movedInP4: true));
        }
        var r = H.Run("OpenBagClick");
        AssertStatus(ProcedureStatus.Succeeded, r);
        var open = AttemptsOf(r, "open").ToList();
        Assert.AreEqual(5, open.Count(a => a.Outcome == StepOutcome.Uncertain));
        Assert.AreEqual(4, open.Count(a => a.Outcome == StepOutcome.Uncertain && a.Decision == RecoveryAction.RetryUncounted));
        Assert.AreEqual(RecoveryAction.RetryCounted, open.Where(a => a.Outcome == StepOutcome.Uncertain).Last().Decision);
        Assert.AreEqual(StepOutcome.Ok, open[^1].Outcome);
        Assert.AreEqual(6, H.Game.ClickCount);
    }

    // (5) press not registered -> uncounted retry
    [TestMethod]
    public void S05_PressNotRegistered_RetryUncounted()
    {
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, null, pressBlock: false));
        var r = H.Run("OpenBagClick");
        AssertStatus(ProcedureStatus.Succeeded, r);
        var first = AttemptsOf(r, "open").First();
        Assert.AreEqual(StepOutcome.PressNotRegistered, first.Outcome);
        Assert.AreEqual(OutcomeClass.Wait, first.Class);
        Assert.AreEqual(RecoveryAction.RetryUncounted, first.Decision);
        Assert.AreEqual(2, H.Game.ClickCount);
    }
}

[TestClass]
public sealed class WrongUiScenarioTests : ScenarioBase
{
    // (3) wrong UI with a verified aim: Esc, one retry if unique, second -> suspect + HALT RecalibrateTemplate
    [TestMethod]
    public void S03_WrongUiVerifiedAim_EscRetryOnce_SecondHaltsRecalibrate()
    {
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, () => H.Log.Show("UI_HOME_PET_VIEW")));
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, () => H.Log.Show("UI_HOME_PET_VIEW")));
        var r = H.Run("OpenBagClick");
        AssertHalt(HaltCode.RecalibrateTemplate, r);
        Assert.AreEqual("hud.bag", r.Halt!.Subject);
        Assert.AreEqual(2, r.Attempts.Count(a => a.Outcome == StepOutcome.WrongUiVerifiedAim));
        Assert.IsTrue(r.Attempts.Where(a => a.Outcome == StepOutcome.WrongUiVerifiedAim).All(a => a.AimVerified));
        Assert.AreEqual(RecoveryAction.RetryCounted, r.Attempts.First(a => a.Outcome == StepOutcome.WrongUiVerifiedAim).Decision);
        CollectionAssert.Contains(H.Templates.SuspectCalls, "hud.bag");
        Assert.AreEqual(2, H.Game.ClickCount);
        Assert.IsTrue(H.Game.KeyRequests.Any(k => k.Key == GameKey.Escape && k.ExpectedUi.Any(e => e.UiName == "UI_HOME_PET_VIEW")), "the wrong UI was closed with a log-verified Esc");
        Assert.AreEqual(1, H.Halt.Reports.Count);
    }

    [TestMethod]
    public void S03_WrongUiVerifiedAim_NotUniqueOnFreshCapture_HaltsAtOnce()
    {
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, () =>
        {
            H.Log.Show("UI_HOME_PET_VIEW");
            H.Vision.LocateFailure["hud.bag"] = MatchFailure.LowMargin;
        }));
        var r = H.Run("OpenBagClick");
        AssertHalt(HaltCode.RecalibrateTemplate, r);
        Assert.AreEqual(1, H.Game.ClickCount);
        CollectionAssert.Contains(H.Templates.SuspectCalls, "hud.bag");
    }

    // (3) unverified aim -> uncounted, Esc if ours and Esc-safe
    [TestMethod]
    public void S03_WrongUiUnverified_EscAndUncountedRetry()
    {
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, () => H.Log.Show("UI_HOME_PET_VIEW"), movedInP4: true));
        var r = H.Run("OpenBagClick");
        AssertStatus(ProcedureStatus.Succeeded, r);
        var wrong = r.Attempts.Single(a => a.Outcome == StepOutcome.WrongUiUnverified);
        Assert.AreEqual(OutcomeClass.Wait, wrong.Class);
        Assert.AreEqual(RecoveryAction.CloseThenRetry, wrong.Decision);
        Assert.IsFalse(wrong.AimVerified);
        Assert.AreEqual(0, H.Templates.SuspectCalls.Count);
        Assert.AreEqual(2, H.Game.ClickCount);
    }

    // (25) unknown UI after our input: one Esc + survey capture, second in the job -> HALT
    [TestMethod]
    public void S25_UnknownUi_EscAndSurveyCapture_ThenRetry()
    {
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, () => H.Log.Show("UI_ZZZ_NEVER_SEEN")));
        var r = H.Run("OpenBagClick");
        AssertStatus(ProcedureStatus.Succeeded, r);
        var u = r.Attempts.Single(a => a.Outcome == StepOutcome.UnknownUi);
        Assert.AreEqual(RecoveryAction.CloseThenRetry, u.Decision);
        Assert.IsTrue(H.Artifacts.Images.Any(i => i.Category == ArtifactCategory.Survey && i.Label.Contains("unknown-ui_zzz_never_seen", StringComparison.Ordinal)));
        Assert.IsTrue(H.Game.KeyRequests.Any(k => k.ExpectedUi.Any(e => e.UiName == "UI_ZZZ_NEVER_SEEN")));
    }

    [TestMethod]
    public void S25_UnknownUiTwice_Halts()
    {
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, () => H.Log.Show("UI_ZZZ_ONE")));
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, () => H.Log.Show("UI_ZZZ_TWO")));
        var r = H.Run("OpenBagClick");
        AssertHalt(HaltCode.UnknownUiTwice, r);
        Assert.AreEqual("UI_ZZZ_TWO", r.Halt!.Subject);
    }

    // (12) forbidden UI after our click: exactly one Esc, then HALT
    [TestMethod]
    public void S12_ForbiddenAfterOurClick_ExactlyOneEscThenHalt()
    {
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, () => H.Log.Show("UI_SHOP_MAIN")));
        var r = H.Run("OpenBagClick");
        AssertHalt(HaltCode.ForbiddenUi, r);
        var afterClick = H.Game.Calls.SkipWhile(c => !c.StartsWith("click:", StringComparison.Ordinal)).Skip(1).ToList();
        CollectionAssert.AreEqual(new[] { "key:Escape" }, afterClick, "exactly one Esc after the click");
        Assert.AreEqual(1, H.Halt.Reports.Count);
        Assert.IsTrue(H.Notifier.Sent.Any(n => n.Kind == NotificationKind.Halt && n.Code == "halt.ForbiddenUi"));
        Assert.AreEqual(StopSource.Engine, H.Stop.Current?.Source);
        Assert.AreEqual(EngineState.Halted, H.Runner.Status.State);
    }

    [TestMethod]
    public void S12_PayLineAfterOurKey_Halts()
    {
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () => H.Game.DefaultKeyEffect(req.Key)));
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, H.Log.Pay));
        var r = H.Run("OpenBagKey");
        AssertHalt(HaltCode.ForbiddenUi, r);
    }

    // (12) foreign forbidden UI: wait, zero Esc
    [TestMethod]
    public void S12_ForeignForbidden_WaitsWithoutEsc()
    {
        H.Log.Show("UI_RECHARGE");
        H.Clock.Advance(TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        H.Clock.At(TimeSpan.FromMinutes(2), cts.Cancel);
        var r = H.Run("OpenBagKey", ct: cts.Token);
        AssertStatus(ProcedureStatus.Cancelled, r);
        Assert.AreEqual(0, H.Game.Inputs, "no Esc on a foreign forbidden UI");
        Assert.AreEqual(1, H.Notifier.Sent.Count(n => n.Code == "wait.foreignBlockingUi"));
        Assert.IsTrue(H.Journal.OfKind(JournalKind.Wait).Any(w => FakeJournal.Str(w, "reason") == nameof(StepOutcome.ForeignBlockingUi)));
    }

    // (13) popup between locate and DOWN -> Popup, one Esc, retry
    [TestMethod]
    public void S13_PopupBetweenLocateAndDown_PopupEscRetry()
    {
        H.Game.ClickScript.Enqueue(_ =>
        {
            H.Log.Show("UI_ITEM_REWARD");
            return H.Game.NotDeliveredClick(InputDisposition.AbortedAfterFocus, AbortReason.LogGatePopup, InputPhase.P3SlideToDown, 4);
        });
        var r = H.Run("OpenBagClick");
        AssertStatus(ProcedureStatus.Succeeded, r);
        var popup = r.Attempts.Single(a => a.Outcome == StepOutcome.Popup);
        Assert.AreEqual(RecoveryAction.CloseThenRetry, popup.Decision);
        Assert.AreEqual("UI_ITEM_REWARD", popup.UiSeen);
        Assert.AreEqual(2, H.Game.ClickCount);
        Assert.IsTrue(H.Game.KeyRequests.Any(k => k.ExpectedUi.Any(e => e.UiName == "UI_ITEM_REWARD")));
    }

    // (15) an Esc opens a UI -> HALT EscOpenedUi, no further Esc
    [TestMethod]
    public void S15_EscOpensUi_HaltNoFurtherEsc()
    {
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () => H.Log.Show("UI_MAIL")));
        var r = H.Run("OpenBagKey");
        AssertHalt(HaltCode.EscOpenedUi, r);
        Assert.AreEqual(1, H.Keys(GameKey.Escape));
        Assert.AreEqual(1, H.Game.Inputs);
    }

    // (15b) an Esc closes a popup and the Esc-safe view under it shows again: revealed, not opened; the reset goes on
    [TestMethod]
    public void S15b_EscRevealsAnEscSafeViewUnderAPopup_TheResetGoesOn()
    {
        // live 2026-10-16: closing a dish's "Improved" popup re-showed the Make view and the reset halted EscOpenedUi
        H.Log.Show("UI_LIFE_FOOD_APPRAISE_UP");
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () =>
        {
            H.Log.HideTop("UI_LIFE_FOOD_APPRAISE_UP");
            H.Log.Show("UI_LIFESKILL_MAKE_VIEW");
        }));
        var r = H.Run("OpenBagKey");
        Assert.AreNotEqual(ProcedureStatus.Halted, r.Status, r.Halt?.Code.ToString());
        Assert.IsGreaterThanOrEqualTo(2, H.Keys(GameKey.Escape), "the revealed view is closed by the next Esc");
    }

    // (24) world press -> HALT
    [TestMethod]
    public void S24_WorldPressIndicator_Halts()
    {
        // no raycast line naming a UI object: the indicator decides
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, () => H.Log.Show("UI_LOCK_TARGET"), pressBlock: false));
        var r = H.Run("OpenBagClick");
        AssertHalt(HaltCode.WorldPress, r);
        Assert.AreEqual(0, H.Game.Calls.SkipWhile(c => !c.StartsWith("click:", StringComparison.Ordinal)).Skip(1).Count(), "no Esc for a world press");
    }

    [TestMethod]
    public void S24_AnIndicatorWhileTheRaycastNamesAUiObjectAtOurAim_IsNoWorldPress()
    {
        // live 2026-10-21: a drag on the Helper's list (raycast 'Image_Bg1') met an NPC speech bubble and latched a HALT
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, () => { H.Log.Show("UI_LOCK_TARGET"); H.Log.Show("UI_NPC_TALK"); H.Log.Show("UI_BAG_MAIN"); }, objectName: "Image_Bg1"));
        var r = H.Run("OpenBagClick");
        Assert.IsNull(r.Halt, "the game's raycast says the press hit the interface");
        Assert.IsFalse(Companion.Contracts.Safety.UiCatalog.WorldPressIndicators.Contains("UI_NPC_TALK"), "an ambient NPC bubble is no indicator");
    }

    [TestMethod]
    public void S24_AnIndicatorTheGameKeepsRepeatingByItself_IsNoWorldPress()
    {
        // live 2026-11-05 02:25: UI_LOCK_ARROW re-showed every 3 s for hours; one repeat fell just before a gathering drag
        // ended (no raycast to clear it) and latched a false WORLD_PRESS
        // (the game logs each show as 'show_ui' + 'open const ui'; SentClick posts DOWN 0.5 s in, the effect at 0.6 s)
        H.Game.ClickScript.Enqueue(req =>
        {
            Arrows(3);
            H.Clock.Advance(TimeSpan.FromSeconds(2.4));
            return H.Game.SentClick(req, () => { ArrowShow(); H.Log.Show("UI_BAG_MAIN"); }, pressBlock: false);
        });
        var r = H.Run("OpenBagClick");
        Assert.AreNotEqual(HaltCode.WorldPress, r.Halt?.Code, "the arrow was repeating before our press");
    }

    [TestMethod]
    public void S24_TwoEarlierArrows_AreNoRhythm_AndStillHalt()
    {
        // safety review: two target taps of the user's own, 3 s apart, must not mask our press
        H.Game.ClickScript.Enqueue(req =>
        {
            Arrows(2);
            H.Clock.Advance(TimeSpan.FromSeconds(2.4));
            return H.Game.SentClick(req, ArrowShow, pressBlock: false);
        });
        AssertHalt(HaltCode.WorldPress, H.Run("OpenBagClick"));
    }

    [TestMethod]
    public void S24_AnArrowOffTheBeat_StillHalts()
    {
        // the game's arrow repeats every 3 s, but this one came 1.5 s after the last: not the game's rhythm
        H.Game.ClickScript.Enqueue(req =>
        {
            Arrows(3);
            H.Clock.Advance(TimeSpan.FromSeconds(0.9));
            return H.Game.SentClick(req, ArrowShow, pressBlock: false);
        });
        AssertHalt(HaltCode.WorldPress, H.Run("OpenBagClick"));
    }

    [TestMethod]
    public void S24_ALockTargetWhileTheArrowRepeats_StillHalts()
    {
        H.Game.ClickScript.Enqueue(req =>
        {
            Arrows(3);
            H.Clock.Advance(TimeSpan.FromSeconds(2.4));
            return H.Game.SentClick(req, () => { ArrowShow(); H.Log.Show("UI_LOCK_TARGET"); }, pressBlock: false);
        });
        AssertHalt(HaltCode.WorldPress, H.Run("OpenBagClick"));
    }

    [TestMethod]
    public void S24_OneIndicatorJustBeforeOurPress_StillHalts()
    {
        // a single earlier show (both of its log lines) is no rhythm: a new indicator at our press still halts
        H.Game.ClickScript.Enqueue(req =>
        {
            ArrowShow();
            H.Clock.Advance(TimeSpan.FromSeconds(2.4));
            return H.Game.SentClick(req, ArrowShow, pressBlock: false);
        });
        AssertHalt(HaltCode.WorldPress, H.Run("OpenBagClick"));
    }

    /// <summary>One game show of the lock arrow: both lines the game writes.</summary>
    private void ArrowShow()
    {
        H.Log.Show("UI_LOCK_ARROW");
        H.Log.Ui(UiVerb.OpenConst, "UI_LOCK_ARROW");
    }

    /// <summary><paramref name="count"/> arrow shows 3 s apart, the clock left at the last one.</summary>
    private void Arrows(int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                H.Clock.Advance(TimeSpan.FromSeconds(3));
            }

            ArrowShow();
        }
    }

    [TestMethod]
    public void S24_EmptyRaycastAtVerifiedAim_Halts()
    {
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, null, objectName: string.Empty));
        var r = H.Run("OpenBagClick");
        AssertHalt(HaltCode.WorldPress, r);
        Assert.IsTrue(r.Attempts.Single(a => a.Outcome == StepOutcome.WorldPress).AimVerified);
    }

    [TestMethod]
    public void GuideBanAfterOurClick_Halts()
    {
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, H.Log.GuideBan));
        var r = H.Run("OpenBagClick");
        AssertHalt(HaltCode.GuideBan, r);
    }
}

[TestClass]
public sealed class LadderScenarioTests : ScenarioBase
{
    // (4) nothing happened x3 -> step back -> x3 -> reset+restart x2 -> ends Deferred (the fallback ladder's last rung,
    // FALLBACKS.md: no latched HALT) within budgets
    [TestMethod]
    public void S04_NothingHappens_Ladder_StepBack_Restarts_Deferred()
    {
        H.Game.BOpensBag = false;
        var r = H.Run("OpenBagKey");
        AssertSoftEnd(HaltCode.CountedFailuresExhausted, r);
        var open = AttemptsOf(r, "open").ToList();
        Assert.AreEqual(12, open.Count(a => a.Outcome == StepOutcome.None));
        Assert.IsTrue(open.All(a => a.Class == OutcomeClass.Counted));
        var decisions = open.Select(a => a.Decision).ToList();
        Assert.AreEqual(1, decisions.Count(d => d == RecoveryAction.StepBack));
        Assert.AreEqual(2, decisions.Count(d => d == RecoveryAction.ResetAndRestart));
        Assert.AreEqual(RecoveryAction.End, decisions[^1]);
        Assert.AreEqual(12, H.Keys(GameKey.B));
        Assert.IsTrue(r.InputBatches <= 25 && r.EscPresses <= 8, $"within budgets: {r.InputBatches} inputs, {r.EscPresses} Esc");
    }

    // the backstop: soft ends are counted per runner; the 4th exhausted ladder within 30 minutes is the HALT
    [TestMethod]
    public void S04b_TheFourthExhaustedLadderWithinTheWindow_Halts()
    {
        H.Game.BOpensBag = false;
        for (var n = 0; n < HardRules.SoftEndsBeforeHalt; n++)
        {
            AssertSoftEnd(HaltCode.CountedFailuresExhausted, H.Run("OpenBagKey"));
        }

        var r = H.Run("OpenBagKey");
        AssertHalt(HaltCode.CountedFailuresExhausted, r);
        Assert.AreEqual("open", r.Halt!.Subject);
    }

    // a press unreachable from where the cursor rests (another monitor) ends Deferred after the short limit, not after
    // the 10-minute patience and hours of parking (live 2026-10-16: the Make tab)
    [TestMethod]
    public void S04c_AnUnreachablePress_EndsDeferredAfterTheShortLimit()
    {
        H.Game.ReadinessOverride = q => q.Kind == InputKind.Click ? H.Game.Report(NotReadyReason.CrossMonitor) : null;
        var start = H.Clock.Monotonic;
        var r = H.Run("OpenBagClick");
        AssertStatus(ProcedureStatus.Deferred, r);
        Assert.AreEqual("unreachable.CrossMonitor", r.EndDetail);
        Assert.IsNull(r.Halt);
        Assert.AreEqual(0, H.Game.ClickCount);
        var waited = H.Clock.Monotonic - start;
        Assert.IsTrue(waited >= HardRules.UnreachableLimit && waited < HardRules.UnreachableLimit + TimeSpan.FromSeconds(10), waited.ToString());
        Assert.IsFalse(H.Journal.OfKind(JournalKind.Park).Any(p => FakeJournal.Str(p, "event") == "start"), "never parked");
    }

    // the user moving the mouse is not "unreachable": that still waits (retry when it rests) and parks after the patience
    [TestMethod]
    public void S04d_OnlyCursorPlaceReasonsAreBounded()
    {
        Assert.AreEqual("CrossMonitor", ProcedureRunner.Unreachable("ready.CrossMonitor"));
        Assert.AreEqual("NoCover", ProcedureRunner.Unreachable("ready.NoCover"));
        Assert.IsNull(ProcedureRunner.Unreachable("ready.CursorMoving"));
        Assert.IsNull(ProcedureRunner.Unreachable("ready.Cooldown"));
        Assert.IsNull(ProcedureRunner.Unreachable("notSent.CrossMonitor"));
        Assert.IsNull(ProcedureRunner.Unreachable(null));
    }

    // (10) template not found in 2 captures -> PreconditionLost -> step back / reset -> recovers
    [TestMethod]
    public void S10_TemplateMissingTwice_PreconditionLost_StepBack_Recovers()
    {
        var miss = new MatchResult("hud.bag", false, default, 0.4, 0.3, 0, MatchFailure.BelowThreshold);
        H.Vision.LocateScript["hud.bag"] = new Queue<MatchResult>([miss, miss]);
        var r = H.Run("OpenBagClick");
        AssertStatus(ProcedureStatus.Succeeded, r);
        var lost = r.Attempts.First(a => a.Outcome == StepOutcome.PreconditionLost);
        Assert.AreEqual(OutcomeClass.Counted, lost.Class);
        Assert.AreEqual(RecoveryAction.StepBack, lost.Decision);
        Assert.AreEqual(1, H.Game.ClickCount);
    }

    [TestMethod]
    public void S10_TemplateAlwaysMissing_PreconditionLostExhausted()
    {
        H.Vision.LocateFailure["hud.bag"] = MatchFailure.BelowThreshold;
        var r = H.Run("OpenBagClick");
        AssertSoftEnd(HaltCode.PreconditionLostExhausted, r);
        Assert.AreEqual(3, r.Attempts.Count(a => a.Outcome == StepOutcome.PreconditionLost));
        Assert.AreEqual(0, H.Game.ClickCount);
    }

    // (10) far from the expected place -> LayoutChanged HALT
    [TestMethod]
    public void S10_TemplateOutsideRadius_LayoutChangedHalt()
    {
        H.Vision.LocateFailure["hud.bag"] = MatchFailure.OutsideRadius;
        var r = H.Run("OpenBagClick");
        AssertHalt(HaltCode.LayoutChanged, r);
        Assert.AreEqual("hud.bag", r.Halt!.Subject);
        Assert.AreEqual(0, H.Game.ClickCount);
    }

    [TestMethod]
    public void S10_LocateOutsideRadius_DuringClickLocate_LayoutChanged()
    {
        var proc = new ProcedureDefinition("ClickBag", "click bag", [
            new StepDefinition("reset", StatePredicate.Any, new ResetAction(), new Expectation([UiExpectation.HiddenTop(null)])),
            new StepDefinition("open", new StatePredicate(HudClean: true), new ClickAction("hud.bag"), new Expectation(UiExpectation.Opened("UI_BAG_MAIN"))),
        ], ProcedureLimits.Default);
        H.Vision.LocateFailure["hud.bag"] = MatchFailure.OutsideRadius;
        var r = H.Run(proc);
        AssertHalt(HaltCode.LayoutChanged, r);
        Assert.AreEqual(0, H.Game.ClickCount);
    }

    // a window that opened moments ago is still sliding in (live 2026-10-16: the Gather tab right after the Ability view's
    // show): a displaced target then is waited for, not a LayoutChanged HALT
    [TestMethod]
    public void S10_OutsideRadiusRightAfterAWindowOpened_IsAnimation_WaitedFor()
    {
        H.Templates.Add(FakeTemplates.Def("test.inbag", 900, 500, TemplateStatus.Verified, 1, null, TargetTags.Nav));
        var proc = new ProcedureDefinition("ClickInBag", "click in bag", [
            new StepDefinition("reset", StatePredicate.Any, new ResetAction(), new Expectation([UiExpectation.HiddenTop(null)])),
            new StepDefinition("open", new StatePredicate(HudClean: true), new ClickAction("hud.bag"), new Expectation(UiExpectation.Opened("UI_BAG_MAIN"))),
            new StepDefinition("inside", StatePredicate.Any, new ClickAction("test.inbag"), Expectation.Nothing),
        ], ProcedureLimits.Default);
        H.Vision.LocateScript["test.inbag"] = new Queue<MatchResult>([new MatchResult("test.inbag", false, default, 0.9, 0.3, 0, MatchFailure.OutsideRadius)]);

        var r = H.Run(proc);

        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.IsTrue(r.Attempts.Any(a => a.DetailCode == "template.animating:test.inbag"), "waited for the animation");
        Assert.AreEqual(2, H.Game.ClickCount, "the bag, then the target once settled");
    }

    [TestMethod]
    public void DenyZone_ClickPointInsideInflatedZone_LayoutChanged()
    {
        var proc = new ProcedureDefinition("ClickBag", "click bag", [
            new StepDefinition("reset", StatePredicate.Any, new ResetAction(), new Expectation([UiExpectation.HiddenTop(null)])),
            new StepDefinition("open", new StatePredicate(HudClean: true), new ClickAction("hud.bag"), new Expectation(UiExpectation.Opened("UI_BAG_MAIN"))),
        ], ProcedureLimits.Default);
        var inZone = new MatchResult("hud.bag", true, new PointI(1700, 312), 0.95, 0.5, 2, MatchFailure.None);
        H.Vision.LocateScript["hud.bag"] = new Queue<MatchResult>([inZone, inZone]);
        var r = H.Run(proc);
        AssertHalt(HaltCode.LayoutChanged, r);
        Assert.AreEqual(0, H.Game.ClickCount);
    }

    // (21) budget -> HALT BudgetExceeded
    [TestMethod]
    public void S21_InputBudget_HaltBudgetExceeded()
    {
        var bag = H.Procedures.Get("OpenBagKey")!;
        var tight = bag with { Id = "OpenBagKeyTight", Limits = bag.Limits with { MaxInputBatches = 2 } };
        var r = H.Run(tight);
        AssertHalt(HaltCode.BudgetExceeded, r);
        Assert.AreEqual(2, H.Game.Inputs, "the third batch is never sent");
    }

    [TestMethod]
    public void S21_ClickBudget_HaltBudgetExceeded()
    {
        var p = H.Procedures.Get("OpenBagClick")!;
        var tight = p with { Id = "OpenBagClickTight", Limits = p.Limits with { MaxClicks = 1 } };
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, null, pressBlock: false));
        var r = H.Run(tight);
        AssertHalt(HaltCode.BudgetExceeded, r);
        Assert.AreEqual(1, H.Game.ClickCount);
    }

    [TestMethod]
    public void RepeatedState_PopupLoop_EndsWithoutRunningAway()
    {
        // every click is aborted by an Esc-safe popup: Esc + retry forever until the budget or the cycle detector stops it
        for (var k = 0; k < 30; k++)
        {
            H.Game.ClickScript.Enqueue(req =>
            {
                H.Log.Show("UI_ITEM_REWARD");
                return H.Game.SentClick(req, () => H.Log.Show("UI_ITEM_REWARD"), movedInP4: true);
            });
        }
        var r = H.Run("OpenBagClick");
        // a budget overrun stays a HALT; a repeated state or an exhausted ladder ends Deferred (FALLBACKS.md)
        var code = r.Status == ProcedureStatus.Halted ? r.Halt!.Code.ToString() : r.EndDetail ?? "";
        Assert.IsTrue(r.Status == ProcedureStatus.Halted && r.Halt!.Code == HaltCode.BudgetExceeded
            || r.Status == ProcedureStatus.Deferred && code is "exhausted.RepeatedState" or "exhausted.CountedFailuresExhausted" or "exhausted.PreconditionLostExhausted", r.Status + " " + code);
        Assert.IsTrue(r.InputBatches <= 25);
    }
}

[TestClass]
public sealed class WindowScenarioTests : ScenarioBase
{
    private static WindowIncident Incident(IncidentKind kind, bool restored) =>
        new(kind, new RectI(-8, -8, 1928, 1040), new RectI(-838, 327, 1098, 1375), restored, restored ? RestoreRung.MoveOnly : RestoreRung.Failed, "test");

    // (6) window incident restored -> pause 30 s, continue
    [TestMethod]
    public void S06_IncidentRestored_Pauses30s_AndContinues()
    {
        H.Game.ClickScript.Enqueue(_ => H.Game.NotDeliveredClick(InputDisposition.Incident, AbortReason.WindowChanged, InputPhase.P7AfterSlideBack, 4,
            Incident(IncidentKind.NotHomeAfterInput, restored: true)));
        var r = H.Run("OpenBagClick");
        AssertStatus(ProcedureStatus.Succeeded, r);
        var clicks = H.Game.Timeline.Where(t => t.Call.StartsWith("click:", StringComparison.Ordinal)).Select(t => t.At).ToList();
        Assert.AreEqual(2, clicks.Count);
        Assert.IsTrue(clicks[1] - clicks[0] >= TimeSpan.FromSeconds(30));
        Assert.AreEqual(RecoveryAction.WaitThenRetry, r.Attempts.Single(a => a.Outcome == StepOutcome.WindowIncidentRestored).Decision);
    }

    // (6) third restored incident within an hour -> HALT
    [TestMethod]
    public void S06_ThirdIncidentInAnHour_Halts()
    {
        for (var k = 0; k < 3; k++)
        {
            H.Game.ClickScript.Enqueue(_ => H.Game.NotDeliveredClick(InputDisposition.Incident, AbortReason.WindowChanged, InputPhase.P7AfterSlideBack, 4,
                Incident(IncidentKind.NotHomeAfterInput, restored: true)));
        }
        var r = H.Run("OpenBagClick");
        AssertHalt(HaltCode.TooManyWindowIncidents, r);
        Assert.AreEqual(3, H.Game.ClickCount);
    }

    [TestMethod]
    public void S06_RestoreFailed_Halts()
    {
        H.Game.ClickScript.Enqueue(_ => H.Game.NotDeliveredClick(InputDisposition.Incident, AbortReason.WindowChanged, InputPhase.P7AfterSlideBack, 4,
            Incident(IncidentKind.RestoreFailed, restored: false)));
        var r = H.Run("OpenBagClick");
        AssertHalt(HaltCode.RestoreFailed, r);
        Assert.IsTrue(H.Notifier.Sent.Any(n => n.Code == "halt.RestoreFailed" && n.Text.Contains("title bar", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void S06_SelfResize_Halts()
    {
        H.Game.ClickScript.Enqueue(_ => H.Game.NotDeliveredClick(InputDisposition.Incident, AbortReason.WindowChanged, InputPhase.P4DownConsumption, 4,
            Incident(IncidentKind.SelfResize, restored: true)));
        var r = H.Run("OpenBagClick");
        AssertHalt(HaltCode.GameSelfResize, r);
    }

    [TestMethod]
    public void CursorClipIncident_Halts()
    {
        H.Game.ClickScript.Enqueue(_ => H.Game.NotDeliveredClick(InputDisposition.Incident, AbortReason.CursorClipChanged, InputPhase.P3SlideToDown, 4,
            Incident(IncidentKind.CursorClip, restored: true)));
        var r = H.Run("OpenBagClick");
        AssertHalt(HaltCode.CursorClipChanged, r);
    }

    // (7) game foreground mid-step: UserActive, 5 s cooldown, resume from the latest step whose Pre holds
    [TestMethod]
    public void S07_GameForegroundMidStep_UserActive_CooldownThenResume()
    {
        TimeSpan leftAt = default;
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () => H.Game.DefaultKeyEffect(req.Key)));
        H.Game.KeyScript.Enqueue(_ =>
        {
            H.Game.UserActivates();
            H.Clock.At(TimeSpan.FromSeconds(20), () =>
            {
                H.Game.UserLeaves();
                leftAt = H.Clock.Elapsed;
            });
            return H.Game.NotDeliveredKey(InputDisposition.UserTookOver, AbortReason.GameForeground, 4);
        });
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.IsTrue(r.Attempts.Any(a => a.Outcome == StepOutcome.UserActive));
        var afterTakeover = H.Game.Timeline.Skip(2).ToList();
        Assert.IsTrue(afterTakeover.Count > 0);
        Assert.IsTrue(afterTakeover.All(t => t.At >= leftAt + HardRules.CooldownAfterGameForeground), "no input during the cooldown");
        Assert.AreEqual("key:Escape", afterTakeover[0].Call, "the Esc mark is no longer valid after the user was active: Reset again");
    }

    // (7) commit job: user activity ends the run Deferred (not a stop: the goal pauses and comes back, FALLBACKS.md)
    [TestMethod]
    public void S07_GameForegroundMidStep_CommitJobDeferred()
    {
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () => H.Game.DefaultKeyEffect(req.Key)));
        H.Game.KeyScript.Enqueue(_ =>
        {
            H.Game.UserActivates();
            return H.Game.NotDeliveredKey(InputDisposition.UserTookOver, AbortReason.GameForeground, 4);
        });
        var r = H.Run("OpenBagKey", JobKind.Commit);
        AssertStatus(ProcedureStatus.Deferred, r);
        Assert.AreEqual(StepOutcome.Deferred, r.FinalOutcome);
        Assert.AreEqual("commit.userActive", r.EndDetail);
        Assert.AreEqual(2, H.Game.Inputs);
    }

    // (8) minimized: no input at all (in particular no slide) while iconic
    [TestMethod]
    public void S08_Minimized_NoInputUntilRestored()
    {
        H.Game.Iconic = true;
        H.Clock.At(TimeSpan.FromSeconds(40), () => H.Game.Iconic = false);
        var r = H.Run("OpenBagClick");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.IsTrue(H.Game.Timeline.All(t => t.At >= TimeSpan.FromSeconds(40)));
        Assert.IsTrue(H.Journal.OfKind(JournalKind.Wait).Any(w => FakeJournal.Str(w, "detail") == "game.iconic"));
    }

    [TestMethod]
    public void S08_MinimizedDuringClick_UserTookOver_NoRetryWhileIconic()
    {
        H.Game.ClickScript.Enqueue(_ =>
        {
            H.Game.Iconic = true;
            H.Clock.At(TimeSpan.FromSeconds(30), () => H.Game.Iconic = false);
            return H.Game.NotDeliveredClick(InputDisposition.UserTookOver, AbortReason.GameIconic, InputPhase.P2FocusToSlide, 4);
        });
        var r = H.Run("OpenBagClick");
        AssertStatus(ProcedureStatus.Succeeded, r);
        var clicks = H.Game.Timeline.Where(t => t.Call.StartsWith("click:", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(2, clicks.Count);
        Assert.IsTrue(H.Game.Timeline.Skip(H.Game.Timeline.IndexOf(clicks[0]) + 1).All(t => t.At - clicks[0].At >= TimeSpan.FromSeconds(29)));
    }

    // (18) identity change (the game was closed and opened again) -> the job ends like a game exit, no HALT
    [TestMethod]
    public void S18_IdentityChange_EndsLikeAGameExit()
    {
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () => H.Game.DefaultKeyEffect(req.Key)));
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () =>
        {
            H.Game.CurrentIdentity = H.Game.Id with { Pid = 9999 };
        }));
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.GameExited, r);
        Assert.IsNull(r.Halt, "no latched HALT for a game restart");
    }

    // (17) log rotation mid-job -> GameExited, no restore attempts
    [TestMethod]
    public void S17_RotationMidJob_GameExited()
    {
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () => H.Game.DefaultKeyEffect(req.Key)));
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, H.Log.Rotate));
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.GameExited, r);
        Assert.AreEqual(2, H.Game.Inputs, "nothing after the rotation");
        Assert.AreEqual(0, H.Halt.Reports.Count);
    }

    // incident 28 (2026-10-28): the game was closed and opened again between jobs; the old window was kept
    [TestMethod]
    public void AGameOpenedAgain_IsConnectedAtTheNextJob_NotReportedAsExited()
    {
        H.Game.Exists = false; // the remembered window is gone
        H.Game.NewWindowOnConnect = true;
        var connects = H.Game.Connects;

        var r = H.Run("OpenBagKey");

        Assert.AreEqual(connects + 1, H.Game.Connects, "connected to the new window at the start");
        Assert.AreNotEqual(ProcedureStatus.GameExited, r.Status);
    }

    [TestMethod]
    public void AGameStillClosed_EndsAsExited_AfterOneConnectTry()
    {
        H.Game.Exists = false;
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.GameExited, r);
        Assert.AreEqual(0, H.Game.Inputs);
    }

    [TestMethod]
    public void GameGone_GameExited()
    {
        H.Game.KeyScript.Enqueue(_ => H.Game.NotDeliveredKey(InputDisposition.GameGone, AbortReason.IdentityChanged, 0));
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.GameExited, r);
    }

    [TestMethod]
    public void ExtraGameWindow_Halts()
    {
        H.Game.ExtraWindows = 1;
        var r = H.Run("OpenBagKey");
        AssertHalt(HaltCode.ExtraGameWindow, r);
        Assert.AreEqual(0, H.Game.Inputs);
    }

    [TestMethod]
    public void DisplayModeWrong_WaitsAndNotifiesOnce()
    {
        H.Game.Prefs = H.Game.Prefs with { FullscreenMode = 1 };
        H.Clock.At(TimeSpan.FromSeconds(30), () => H.Game.Prefs = H.Game.Prefs with { FullscreenMode = 3 });
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(1, H.Notifier.Sent.Count(n => n.Code == "wait.displayMode"));
        Assert.IsTrue(H.Game.Timeline.All(t => t.At >= TimeSpan.FromSeconds(30)));
    }
}

[TestClass]
public sealed class GateScenarioTests : ScenarioBase
{
    // (14) loading: zero inputs until clear
    [TestMethod]
    public void S14_Loading_NoInputUntilClear()
    {
        H.Log.Busy = BusyKind.Loading;
        H.Clock.At(TimeSpan.FromSeconds(45), () => H.Log.Busy = BusyKind.None);
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.IsTrue(H.Game.Timeline.All(t => t.At >= TimeSpan.FromSeconds(45)));
        Assert.IsTrue(H.Journal.OfKind(JournalKind.Wait).Any(w => FakeJournal.Str(w, "reason") == nameof(StepOutcome.Busy)));
    }

    // (16) user UI (UI_MAIL) open at job start: park + notify once, resume when the user closes it
    [TestMethod]
    public void S16_UserUiOpen_ParksAndNotifiesOnce_ResumesWhenClosed()
    {
        H.Log.Show("UI_MAIL");
        H.Clock.At(TimeSpan.FromMinutes(3), () => H.Log.Ui(UiVerb.AddToCloseList, "UI_MAIL"));
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(StepOutcome.UserUiOpen, r.Attempts[0].Outcome);
        Assert.AreEqual(RecoveryAction.Park, r.Attempts[0].Decision);
        Assert.AreEqual(1, H.Notifier.Sent.Count(n => n.Code == "park.userUiOpen"));
        Assert.IsTrue(H.Game.Timeline.All(t => t.At >= TimeSpan.FromMinutes(3)), "the user's mail was never closed by us");
        Assert.IsTrue(H.Journal.OfKind(JournalKind.Park).Any(p => FakeJournal.Str(p, "event") == "resume"));
    }

    // (16) with the policy: Reset closes it
    [TestMethod]
    public void S16_UserUiOpen_WithPolicy_ResetClosesIt()
    {
        H.Config.SetPolicy(new PolicyConfig(CloseUserUisAtJobStart: true));
        H.Log.Show("UI_MAIL");
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual("key:Escape", H.Game.Calls[0]);
        Assert.IsTrue(H.Game.KeyRequests[0].ExpectedUi.Any(e => e.UiName == "UI_MAIL"));
        Assert.AreEqual(3, H.Keys(GameKey.Escape));
    }

    // (19) focus failed once -> retry after 5 s; twice -> HALT
    [TestMethod]
    public void S19_FocusFailedOnce_RetryAfter5s()
    {
        H.Game.KeyScript.Enqueue(_ => H.Game.NotDeliveredKey(InputDisposition.FocusFailed, AbortReason.None, 4));
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Succeeded, r);
        var ff = r.Attempts.Single(a => a.Outcome == StepOutcome.FocusFailedOnce);
        Assert.AreEqual(RecoveryAction.WaitThenRetry, ff.Decision);
        var t = H.Game.Timeline;
        Assert.IsTrue(t[1].At - t[0].At >= TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public void S19_FocusFailedTwice_Halts()
    {
        H.Game.KeyScript.Enqueue(_ => H.Game.NotDeliveredKey(InputDisposition.FocusFailed, AbortReason.None, 4));
        H.Game.KeyScript.Enqueue(_ => H.Game.NotDeliveredKey(InputDisposition.FocusFailed, AbortReason.None, 4));
        var r = H.Run("OpenBagKey");
        AssertHalt(HaltCode.FocusFailed, r);
        Assert.IsTrue(H.Notifier.Sent.Any(n => n.Text.Contains("administrator", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void IntegrityMismatch_Halts()
    {
        H.Game.KeyScript.Enqueue(_ => H.Game.NotDeliveredKey(InputDisposition.IntegrityMismatch, AbortReason.None, 1));
        var r = H.Run("OpenBagKey");
        AssertHalt(HaltCode.IntegrityMismatch, r);
    }

    // (20) stop during a wait -> Stopped
    [TestMethod]
    public void S20_StopDuringWait_Stopped()
    {
        H.Log.Busy = BusyKind.Loading;
        H.Clock.At(TimeSpan.FromSeconds(20), () => H.Stop.RequestStop(StopSource.Tray, "tray"));
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Stopped, r);
        Assert.AreEqual(0, H.Game.Inputs);
        Assert.AreEqual(0, H.Halt.Reports.Count);
    }

    [TestMethod]
    public void S20_StopSetBeforeRun_StoppedWithoutInput()
    {
        H.Stop.RequestStop(StopSource.Hotkey, "hotkey");
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Stopped, r);
        Assert.AreEqual(0, H.Game.Inputs);
    }

    // (20) pause / resume
    [TestMethod]
    public void S20_PauseResume_NoInputWhilePaused()
    {
        H.Pause.Pause("tray");
        EngineState? seen = null;
        H.Clock.At(TimeSpan.FromSeconds(10), () => seen = H.Runner.Status.State);
        H.Clock.At(TimeSpan.FromSeconds(30), () => H.Pause.Resume("tray"));
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(EngineState.Paused, seen);
        Assert.IsTrue(H.Game.Timeline.All(t => t.At >= TimeSpan.FromSeconds(30)));
    }

    [TestMethod]
    public void HaltLatched_RefusedWithoutInput()
    {
        H.Halt.Halt(new HaltReport(HaltCode.GuideBan, null, "x", "y", null, FakeClock.Epoch, null));
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Refused, r);
        Assert.AreEqual(0, H.Game.Inputs);
    }

    [TestMethod]
    public void UntrustedBuild_HaltsTamperedFiles()
    {
        H.Trust.BuildTrusted = false;
        var r = H.Run("OpenBagKey");
        AssertHalt(HaltCode.TamperedFiles, r);
        Assert.AreEqual(0, H.Game.Inputs);
    }

    // (22) throttle -> 60 s back-off
    [TestMethod]
    public void S22_BackgroundThrottle_DoesNotPause()
    {
        // The game throttles its own background requests all day; a throttle line that does not follow one of our
        // inputs must not pause us (live run 2026-10-14).
        H.Log.LastThrottle = H.Clock.GameNow;
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.IsTrue(H.Game.Timeline[0].At < HardRules.ThrottleBackoff);
        Assert.IsFalse(H.Journal.OfKind(JournalKind.Wait).Any(w => FakeJournal.Str(w, "reason") == nameof(StepOutcome.Throttled)));
    }

    // (23) auto-collect 1 / unknown -> wait; 2 confirmed in the tray -> no wait; change during a step -> HALT
    [TestMethod]
    public void S23_AutoCollectActive_Waits()
    {
        H.Log.AutoCollect = 1;
        H.Clock.At(TimeSpan.FromSeconds(30), () => H.Log.AutoCollect = 0);
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.IsTrue(H.Game.Timeline.All(t => t.At >= TimeSpan.FromSeconds(30)));
        // a trip that is out (1) is a known state: no "needs you" notice (incident 18, 2026-10-23)
        Assert.AreEqual(0, H.Notifier.Sent.Count(n => n.Code == "wait.autoCollect"));
    }

    [TestMethod]
    public void S23_AnUnknownAutoCollectStatus_Waits_AndAsksTheUser()
    {
        H.Log.AutoCollect = 7;
        H.Clock.At(TimeSpan.FromSeconds(30), () => H.Log.AutoCollect = 0);
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(1, H.Notifier.Sent.Count(n => n.Code == "wait.autoCollect"), "a status the companion does not know needs the user's confirmation");
    }

    [TestMethod]
    public void S23_DuringAutoCollect_IsAllowedOnAPlainClickOnly()
    {
        // 2026-10-18: the gear durability Notice shows only during gathering; its two clicks carry the flag (the gate
        // exception itself was checked live)
        var p = H.Procedures.Get("OpenBagClick")!;
        var click = p with { Id = "x.click", Steps = p.Steps.Where(s => s.Action is ClickAction).Select(s => s with { Flags = StepFlags.DuringAutoCollect }).ToArray() };
        Assert.HasCount(0, Companion.Engine.Internal.StepValidation.Validate(click));

        var key = H.Procedures.Get("OpenBagKey")!;
        var badKey = key with { Id = "x.badKey", Steps = key.Steps.Select(s => s.Action is KeyAction ? s with { Flags = s.Flags | StepFlags.DuringAutoCollect } : s).ToArray() };
        StringAssert.Contains(string.Join(",", Companion.Engine.Internal.StepValidation.Validate(badKey)), "step.duringAutoCollect");
        var both = click with { Steps = click.Steps.Select(s => s with { Flags = StepFlags.DuringAutoCollect | StepFlags.ChangesAutoCollect }).ToArray() };
        StringAssert.Contains(string.Join(",", Companion.Engine.Internal.StepValidation.Validate(both)), "step.duringAutoCollect");
    }

    [TestMethod]
    public void SelectionVerified_IsAllowedOnAPlainClickOnly_NeverOnTheCommit()
    {
        var p = H.Procedures.Get("OpenBagClick")!;
        var click = p with { Id = "x.sel", Steps = p.Steps.Where(s => s.Action is ClickAction).Select(s => s with { Flags = StepFlags.SelectionVerified }).ToArray() };
        Assert.HasCount(0, Companion.Engine.Internal.StepValidation.Validate(click));
        var commit = click with { Steps = click.Steps.Select(s => s with { Flags = StepFlags.SelectionVerified | StepFlags.Commit }).ToArray() };
        StringAssert.Contains(string.Join(",", Companion.Engine.Internal.StepValidation.Validate(commit)), "step.selectionVerified");
        var key = H.Procedures.Get("OpenBagKey")!;
        var onKey = key with { Id = "x.selKey", Steps = key.Steps.Select(s => s.Action is KeyAction ? s with { Flags = s.Flags | StepFlags.SelectionVerified } : s).ToArray() };
        StringAssert.Contains(string.Join(",", Companion.Engine.Internal.StepValidation.Validate(onKey)), "step.selectionVerified");
    }

    [TestMethod]
    public void QuickAttribution_IsAllowedOnAPlainClickThatExpectsNothing_Only()
    {
        // 2026-10-31: the Tea House board squares (their job confirms each press from the log and checks the top window)
        var ok = new ProcedureDefinition("x.quick", "x", [new StepDefinition("c", StatePredicate.Any, new ClickAction("hud.bag"), Expectation.Nothing, 3000, StepFlags.QuickAttribution)], ProcedureLimits.Default);
        Assert.HasCount(0, Companion.Engine.Internal.StepValidation.Validate(ok));
        var p = H.Procedures.Get("OpenBagClick")!;
        var expects = p with { Id = "x.quickExpects", Steps = p.Steps.Where(s => s.Action is ClickAction).Select(s => s with { Flags = StepFlags.QuickAttribution }).ToArray() };
        StringAssert.Contains(string.Join(",", Companion.Engine.Internal.StepValidation.Validate(expects)), "step.quickAttribution", "a click that expects a window keeps the full read");
        var commit = ok with { Steps = [ok.Steps[0] with { Flags = StepFlags.QuickAttribution | StepFlags.Commit }] };
        StringAssert.Contains(string.Join(",", Companion.Engine.Internal.StepValidation.Validate(commit)), "step.quickAttribution");
        var key = ok with { Steps = [new StepDefinition("k", StatePredicate.Any, new KeyAction(GameKey.Escape), Expectation.Nothing, 3000, StepFlags.QuickAttribution)] };
        StringAssert.Contains(string.Join(",", Companion.Engine.Internal.StepValidation.Validate(key)), "step.quickAttribution");
        StringAssert.Contains(string.Join(",", Companion.Engine.Internal.StepValidation.Validate(ok with { Gate = AutomationSwitch.Crafting })), "step.quickAttribution", "never in a gated procedure");
        StringAssert.Contains(string.Join(",", Companion.Engine.Internal.StepValidation.Validate(ok with { Kind = ProcedureKind.Commit })), "step.quickAttribution", "never in a commit procedure");
    }

    [TestMethod]
    public void S23_AutoCollectUnknown_TreatedAsOff()
    {
        // The game logs the status only when it changes; no line since the game started means it was never switched
        // on (live run 2026-10-14: waiting here blocked every job).
        H.Log.AutoCollect = null;
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(0, H.Notifier.Sent.Count(n => n.Code == "wait.autoCollect"));
    }

    [TestMethod]
    public void S23_AutoCollectTwo_WaitsUntilConfirmedInTray()
    {
        H.Log.AutoCollect = 2;
        H.Clock.At(TimeSpan.FromSeconds(25), () => H.Config.SetPolicy(new PolicyConfig(TreatAutoCollectStatusAsIdle: [2])));
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.IsTrue(H.Game.Timeline.All(t => t.At >= TimeSpan.FromSeconds(25)));
    }

    [TestMethod]
    public void S23_AutoCollectChangesDuringStep_Halts()
    {
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () => H.Game.DefaultKeyEffect(req.Key)));
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () =>
        {
            H.Log.Show("UI_BAG_MAIN");
            H.Log.AutoCollect = 1;
        }));
        var r = H.Run("OpenBagKey");
        AssertHalt(HaltCode.AutoCollectChanged, r);
    }

    [TestMethod]
    public void S23_AClickMeantToChangeAutoCollect_DoesNotHalt_OthersStill()
    {
        // The Gathering Helper's Begin (2026-10-15): its press changes the status 0 -> 1 on purpose.
        H.Templates.Add(FakeTemplates.Def("test.begin", 760, 300, TemplateStatus.Verified, 1, HudState.Collapsed, TargetTags.Nav));
        StepDefinition Begin(StepFlags flags) => new("begin", new StatePredicate(HudClean: true), new ClickAction("test.begin"), Expectation.Nothing, Flags: flags);
        void BeginStartsGathering() => H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, () => H.Log.AutoCollect = 1));

        BeginStartsGathering();
        var flagged = new ProcedureDefinition("Begin", "test", [Begin(StepFlags.ChangesAutoCollect)], ProcedureLimits.Default);
        AssertStatus(ProcedureStatus.Succeeded, H.Run(flagged));

        H.Log.AutoCollect = 0;
        BeginStartsGathering();
        AssertHalt(HaltCode.AutoCollectChanged, H.Run(flagged with { Id = "Plain", Steps = [Begin(StepFlags.None)] }));
    }

    // (26) clock jump: restart timers, settle the log, continue
    [TestMethod]
    public void S26_ClockJump_SettlesAndContinues()
    {
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () =>
        {
            H.Game.DefaultKeyEffect(req.Key);
            H.Clock.JumpWall(TimeSpan.FromHours(2));
        }));
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.IsTrue(H.Journal.OfKind(JournalKind.Wait).Any(w => FakeJournal.Str(w, "reason") == nameof(StepOutcome.ClockJump)));
    }

    [TestMethod]
    public void CenterOutOfReach_AimsInsideTheButton()
    {
        // Only the exact centre breaks the monitor rule; a point inside the button is reachable (live run 2026-10-14).
        PointI? centre = null;
        H.Game.ReadinessOverride = q =>
        {
            if (q.Kind != InputKind.Click || q.TargetClientPoint is not { } p)
            {
                return null;
            }
            centre ??= p;
            return p == centre ? H.Game.Report(NotReadyReason.MonitorRule) : null;
        };
        var r = H.Run("OpenBagClick");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(1, H.Game.ClickCount);
        Assert.IsTrue(H.Journal.OfKind(JournalKind.Decision).Any(d => FakeJournal.Str(d, "event") == "aim.offset"));
    }

    [TestMethod]
    public void LogQuietBeforeActing_StillActs()
    {
        // An idle game can log nothing for a long time; that is not a reason to refuse to act (live run 2026-10-14).
        // A log that stays dead after we act is still caught by the post-action checks.
        H.Log.QuietSince = H.Clock.UtcNow - TimeSpan.FromSeconds(6);
        H.Run("OpenBagKey");
        Assert.IsTrue(H.Game.Inputs > 0);
    }

    [TestMethod]
    public void CombatRecent_DelaysClicksOnly()
    {
        H.Log.LastCombatOverride = H.Clock.GameNow;
        var r = H.Run("OpenBagClick");
        AssertStatus(ProcedureStatus.Succeeded, r);
        var click = H.Game.Timeline.First(t => t.Call.StartsWith("click:", StringComparison.Ordinal));
        Assert.IsTrue(click.At >= HardRules.CombatRecent);
        Assert.IsTrue(H.Game.Timeline[0].At < HardRules.CombatRecent, "the reset Esc (a key) is not delayed by combat");
    }

    [TestMethod]
    public void CraftInProgress_NoInput()
    {
        H.Log.Craft = new CraftSessionInfo(true, "UI_LIFE_ALCHEMIST_MAKE_POT", H.Clock.GameNow, null, 3, H.Clock.GameNow, null, false);
        H.Clock.At(TimeSpan.FromSeconds(40), () => H.Log.Craft = CraftSessionInfo.None);
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.IsTrue(H.Game.Timeline.All(t => t.At >= TimeSpan.FromSeconds(40)));
    }
}

[TestClass]
public sealed class ToggleScenarioTests : ScenarioBase
{
    // (11) toggle already in target: Ok with zero clicks
    [TestMethod]
    public void S11_ToggleAlreadyInTarget_OkWithoutClick()
    {
        H.Game.Hud = HudState.Expanded;
        var r = H.Run("ExpandMenu");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(0, H.Game.ClickCount);
        Assert.AreEqual(StepOutcome.Ok, AttemptsOf(r, "toggle").Single().Outcome);
    }

    [TestMethod]
    public void S11_ToggleExpands()
    {
        var r = H.Run("ExpandMenu");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(1, H.Game.ClickCount);
        Assert.AreEqual("hud.toggle.collapsed", H.Game.ClickRequests.Single().TemplateId, "the toggle of the CURRENT state is clicked");
        Assert.AreEqual(HudState.Expanded, H.Game.Hud);
    }

    // (11) toggle pressed but the HUD stays unchanged -> None (counted)
    [TestMethod]
    public void S11_TogglePressedHudUnchanged_NoneCounted()
    {
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, null));
        var r = H.Run("ExpandMenu");
        AssertStatus(ProcedureStatus.Succeeded, r);
        var first = AttemptsOf(r, "toggle").First();
        Assert.AreEqual(StepOutcome.None, first.Outcome);
        Assert.AreEqual(OutcomeClass.Counted, first.Class);
        Assert.AreEqual(RecoveryAction.RetryCounted, first.Decision);
        Assert.AreEqual(2, H.Game.ClickCount);
    }

    [TestMethod]
    public void S11_TogglePressNotRegistered_RetryAllowed()
    {
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, null, pressBlock: false));
        var r = H.Run("ExpandMenu");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(StepOutcome.PressNotRegistered, AttemptsOf(r, "toggle").First().Outcome);
    }

    // (11) HUD unknown -> wait (no click)
    [TestMethod]
    public void S11_HudUnknown_WaitsWithoutClick()
    {
        H.Vision.HudOverride = HudState.Unknown;
        H.Clock.At(TimeSpan.FromSeconds(15), () => H.Vision.HudOverride = null);
        var r = H.Run("ExpandMenu");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.IsTrue(AttemptsOf(r, "toggle").Any(a => a.Outcome == StepOutcome.PreconditionTransient && a.Class == OutcomeClass.Wait));
        var click = H.Game.Timeline.Single(t => t.Call.StartsWith("click:", StringComparison.Ordinal));
        Assert.IsTrue(click.At >= TimeSpan.FromSeconds(15));
    }

    [TestMethod]
    public void S28_CollapseMenu_ProvisionalX_NeedsSurveyZeroInputs()
    {
        var r = H.Run("CollapseMenu");
        AssertStatus(ProcedureStatus.NeedsSurvey, r);
        Assert.AreEqual(StepOutcome.NeedsSurvey, r.FinalOutcome);
        Assert.AreEqual(0, H.Game.Inputs);
        Assert.AreEqual(0, H.Game.Captures);
    }

    [TestMethod]
    public void CollapseMenu_AfterCalibrationVerified_Collapses()
    {
        H.Templates.Verify("hud.toggle.expanded");
        H.Game.Hud = HudState.Expanded;
        var r = H.Run("CollapseMenu");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual("hud.toggle.expanded", H.Game.ClickRequests.Single().TemplateId);
        Assert.AreEqual(HudState.Collapsed, H.Game.Hud);
    }

    // (29) Calibrate (2026-10-14: the menu stays expanded for good): captures as found + expanded, never collapses,
    // nothing owed, no notification.
    [TestMethod]
    public void S29_Calibrate_EndsExpanded_NothingPendingNoNotification()
    {
        var r = H.Run("Calibrate", JobKind.Maintenance);
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(0, r.Pending.Count, string.Join(",", r.Pending.Select(p => p.Code)));
        Assert.AreEqual(0, H.Notifier.Sent.Count, string.Join(",", H.Notifier.Sent.Select(n => n.Code)));
        Assert.AreEqual(1, H.Game.ClickCount, "only the expand toggle");
        Assert.AreEqual("hud.toggle.collapsed", H.Game.ClickRequests.Single().TemplateId);
        Assert.AreEqual(HudState.Expanded, H.Game.Hud);
        var calib = H.Artifacts.Images.Where(i => i.Category == ArtifactCategory.Calibration).Select(i => i.Label).ToList();
        Assert.AreEqual(2, calib.Count);
        Assert.IsTrue(calib.Any(l => l.EndsWith("/initial", StringComparison.Ordinal)) && calib.Any(l => l.EndsWith("/expanded", StringComparison.Ordinal)));
        Assert.AreEqual(1, calib.Select(l => l.Split('/')[0]).Distinct().Count(), "one calibration folder per run");
        Assert.AreEqual(2, r.Artifacts.Count);
    }

    [TestMethod]
    public void Calibrate_WithTheXVerified_StillNeverCollapses()
    {
        H.Templates.Verify("hud.toggle.expanded");
        var r = H.Run("Calibrate", JobKind.Maintenance);
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(0, r.Pending.Count);
        Assert.AreEqual(0, H.Notifier.Sent.Count);
        Assert.AreEqual(2, H.Artifacts.Images.Count(i => i.Category == ArtifactCategory.Calibration));
        Assert.AreEqual(HudState.Expanded, H.Game.Hud);
        Assert.IsFalse(H.Game.ClickRequests.Any(c => c.TemplateId == "hud.toggle.expanded"), "the X (collapse) is never clicked");

        // Already expanded: no click at all, still expanded at the end.
        var again = H.Run("Calibrate", JobKind.Maintenance);
        AssertStatus(ProcedureStatus.Succeeded, again);
        Assert.AreEqual(1, H.Game.ClickCount);
        Assert.AreEqual(HudState.Expanded, H.Game.Hud);
    }
}

[TestClass]
public sealed class PreflightScenarioTests : ScenarioBase
{
    private ProcedureDefinition CommitProcedure()
    {
        H.Templates.Add(FakeTemplates.Def("test.make", 960, 900, TemplateStatus.Verified, 1, null, TargetTags.Commit, AutomationSwitch.Crafting));
        return new ProcedureDefinition("TestCommit", "commit test", [
            new StepDefinition("reset", StatePredicate.Any, new ResetAction(), new Expectation([UiExpectation.HiddenTop(null)])),
            new StepDefinition("make", new StatePredicate(HudClean: true), new ClickAction("test.make"), new Expectation(UiExpectation.Opened("UI_LIFE_ALCHEMIST_MAKE_POT")), 3000, StepFlags.Commit),
        ], ProcedureLimits.Default, ProcedureKind.Commit, AutomationSwitch.Crafting);
    }

    // a window opened before the companion started is not in the log (live 2026-10-17: the Make view stayed open over a
    // restart): the reset identifies it by its readers' screen anchors and closes it with one Esc, instead of calling a
    // covered HUD clean
    [TestMethod]
    public void Reset_AnUnloggedEscSafeWindow_IdentifiedByItsAnchors_GetsOneEsc()
    {
        var reset = new ProcedureDefinition("TestReset", "reset", [new StepDefinition("reset", StatePredicate.Any, new ResetAction(), new Expectation([UiExpectation.HiddenTop(null)]))], ProcedureLimits.Default);
        AssertStatus(ProcedureStatus.Succeeded, H.Run(reset)); // the log now knows a clean HUD
        H.Templates.Add(FakeTemplates.Def("test.make.anchor", 485, 123, TemplateStatus.Verified, 1, null, TargetTags.ScreenAnchor));
        H.Readers.Put(new ReaderDefinition("test.list", new ScreenIdentity("UI_LIFESKILL_MAKE_VIEW", ["test.make.anchor"]), [], ReaderStatus.Ready, null));
        H.Vision.HudOverride = HudState.Covered; // the Make view is on screen, the log does not know it
        H.Game.AfterEsc = () => H.Vision.HudOverride = null;
        var escBefore = H.Keys(GameKey.Escape);

        var r = H.Run(reset with { Id = "TestReset2" });

        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(1, H.Keys(GameKey.Escape) - escBefore, "one Esc for the identified window");
        Assert.IsTrue(H.Journal.OfKind(JournalKind.Decision).Any(d => FakeJournal.Str(d, "event") == "reset.unlogged" && FakeJournal.Str(d, "ui") == "UI_LIFESKILL_MAKE_VIEW"));
        Assert.IsNull(H.Vision.HudOverride, "the HUD is clean again");
    }

    // the commit gate's user-activity window (2026-10-16 safety review): it starts at the job's start, or at the latest
    // successful selection-verifying run, and a verification serves the next commit run only
    private ProcedureResult[] GateScenario(bool verifyAfterActivity, bool verifyBeforeActivity)
    {
        var make = CommitProcedure();
        H.Switches.Crafting = true;
        var tip = new ProcedureDefinition("TestTip", "tip", [new StepDefinition("look", StatePredicate.Any, new WaitAction(10), Expectation.Nothing)],
            ProcedureLimits.Default, ProcedureKind.Navigation, AutomationSwitch.None, VerifiesSelection: true);
        var warm = new ProcedureDefinition("TestWarm", "warm", [new StepDefinition("wait", StatePredicate.Any, new WaitAction(10), Expectation.Nothing)], ProcedureLimits.Default);
        var ctx = H.Context(JobKind.Commit, AutomationSwitch.Crafting, "gate-job");
        var results = new List<ProcedureResult> { H.Runner.Run(warm, ctx, default) }; // the job starts
        H.Clock.Advance(TimeSpan.FromMinutes(1));
        if (verifyBeforeActivity)
        {
            results.Add(H.Runner.Run(tip, ctx, default));
        }

        H.Game.UserActivates(); // the user plays a moment, then leaves
        H.Clock.Advance(TimeSpan.FromSeconds(5));
        H.Game.UserLeaves();
        H.Clock.Advance(TimeSpan.FromMinutes(2)); // past the readiness cooldown
        if (verifyAfterActivity)
        {
            results.Add(H.Runner.Run(tip, ctx, default));
        }

        results.Add(H.Runner.Run(make, ctx, default));
        return [.. results];
    }

    [TestMethod]
    public void CommitGate_ActivityDuringTheJob_WithoutAFreshVerification_DefersTheMake()
    {
        var r = GateScenario(verifyAfterActivity: false, verifyBeforeActivity: false)[^1];
        AssertStatus(ProcedureStatus.Deferred, r);
        Assert.AreEqual("commit.userActivity", r.EndDetail);
        Assert.AreEqual(0, H.Game.ClickCount, "Make never pressed");
    }

    [TestMethod]
    public void CommitGate_AVerificationBeforeTheActivity_DoesNotCoverIt()
    {
        var r = GateScenario(verifyAfterActivity: false, verifyBeforeActivity: true)[^1];
        AssertStatus(ProcedureStatus.Deferred, r);
        Assert.AreEqual("commit.userActivity", r.EndDetail);
        Assert.AreEqual(0, H.Game.ClickCount, "Make never pressed");
    }

    [TestMethod]
    public void CommitGate_AVerificationAfterTheActivity_LetsTheMakeGo_AndServesOneCommitRunOnly()
    {
        var results = GateScenario(verifyAfterActivity: true, verifyBeforeActivity: false);
        Assert.AreNotEqual("commit.userActivity", results[^1].EndDetail);
        Assert.AreEqual(1, H.Game.ClickCount, "Make pressed once");

        // the verification was used up by that commit run: another Make in the same job needs a new one
        var again = H.Runner.Run(CommitProcedure() with { Id = "TestCommit2" }, H.Context(JobKind.Commit, AutomationSwitch.Crafting, "gate-job"), default);
        AssertStatus(ProcedureStatus.Deferred, again);
        Assert.AreEqual("commit.userActivity", again.EndDetail);
        Assert.AreEqual(1, H.Game.ClickCount, "no second Make");
    }

    // (27) automation switch off -> Refused with zero inputs
    [TestMethod]
    public void S27_AutomationOff_RefusedZeroInputs()
    {
        var p = CommitProcedure();
        var r = H.Run(p, JobKind.Commit, AutomationSwitch.Crafting);
        AssertStatus(ProcedureStatus.Refused, r);
        Assert.AreEqual(0, H.Game.Inputs);
        Assert.AreEqual(0, H.Game.Captures);
    }

    // 4.33 (one knowledge profile, no per-character permission): with its switch on, a commit run goes ahead whoever
    // is logged in; the switch is the only gate
    [TestMethod]
    public void S27_SwitchOn_TheCommitRunNeedsNoCharacterPermission()
    {
        var p = CommitProcedure();
        H.Switches.Crafting = true;
        var r = H.Run(p, JobKind.Commit, AutomationSwitch.Crafting);
        Assert.AreNotEqual(ProcedureStatus.Refused, r.Status, r.EndDetail);
        Assert.IsFalse(r.Attempts.Any(a => a.DetailCode?.Contains("character", StringComparison.Ordinal) == true), "no character check");
        Assert.AreEqual(1, H.Game.ClickCount, "Make pressed once");
    }

    [TestMethod]
    public void SlideClicksOff_ClickProcedureRefused_KeyProcedureRuns()
    {
        H.Switches.SlideClickEnabled = false;
        var click = H.Run("OpenBagClick");
        AssertStatus(ProcedureStatus.Refused, click);
        Assert.AreEqual(0, H.Game.Inputs);
        var key = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Succeeded, key);
    }

    // (28) NeedsSurvey pre-flight -> zero inputs
    [TestMethod]
    public void S28_OpenAbility_ProvisionalTemplate_NeedsSurveyZeroInputs()
    {
        var r = H.Run("OpenAbility");
        AssertStatus(ProcedureStatus.NeedsSurvey, r);
        Assert.AreEqual(0, H.Game.Inputs);
        Assert.AreEqual(0, H.Game.Captures);
        Assert.IsTrue(H.Journal.Records.Any(rec => FakeJournal.Str(rec, "detail") == "template.provisional:menu.ability"));
    }

    [TestMethod]
    public void S28_NeedsSurveyStubOrReader_ZeroInputs()
    {
        var stub = new ProcedureDefinition("Stub", "stub", [
            new StepDefinition("reset", StatePredicate.Any, new ResetAction(), Expectation.Nothing),
            new StepDefinition("read", StatePredicate.Any, new ReadAction("bag.grid", "bag"), Expectation.Nothing),
        ], ProcedureLimits.Default);
        var r = H.Run(stub);
        AssertStatus(ProcedureStatus.NeedsSurvey, r);
        Assert.AreEqual(0, H.Game.Inputs);
        var stub2 = new ProcedureDefinition("Stub2", "stub", [
            new StepDefinition("reset", StatePredicate.Any, new ResetAction(), Expectation.Nothing),
            new StepDefinition("todo", StatePredicate.Any, new NeedsSurveyAction("craft.commit"), Expectation.Nothing),
        ], ProcedureLimits.Default);
        AssertStatus(ProcedureStatus.NeedsSurvey, H.Run(stub2));
        Assert.AreEqual(0, H.Game.Inputs);
    }

    [TestMethod]
    public void NavigateToNeedsSurveyScreen_NeedsSurveyZeroInputs()
    {
        var p = new ProcedureDefinition("NavLumi", "nav", [new StepDefinition("go", StatePredicate.Any, new NavigateAction("lumi_hut"), Expectation.Nothing)], ProcedureLimits.Default);
        var r = H.Run(p);
        AssertStatus(ProcedureStatus.NeedsSurvey, r);
        Assert.AreEqual(0, H.Game.Inputs);
    }

    [TestMethod]
    public void ReadAction_ReadyReader_ResultStored_NotHaltOnUnknown()
    {
        H.ScreenReader.Next = Companion.Contracts.Perception.ReadStatus.Unknown;
        var p = new ProcedureDefinition("ReadTest", "read", [new StepDefinition("read", StatePredicate.Any, new ReadAction("test.ready", "bag"), Expectation.Nothing)], ProcedureLimits.Default);
        var r = H.Run(p, JobKind.ReadOnlyScan);
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(Companion.Contracts.Perception.ReadStatus.Unknown, r.Reads["bag"].Status);
    }

    [TestMethod]
    public void ReadAction_JournalsOneCodePerRegion_WithTheGlyphScore_AndNeverTheText()
    {
        // live 2026-10-14: whole recipe panels read Unknown with no way to tell which check failed
        H.ScreenReader.Next = Companion.Contracts.Perception.ReadStatus.Unknown;
        H.ScreenReader.Values =
        [
            new("pair.1", Companion.Contracts.Perception.ReadStatus.Ok, 40, 1, "40/1", null, 0.995, "pair", 0.991),
            new("pair.2", Companion.Contracts.Perception.ReadStatus.Unknown, null, null, null, null, 0, "digit-low-score", 0.7414),
            new("product.name", Companion.Contracts.Perception.ReadStatus.Ok, null, null, "White Toast", null, 0.6, "text-agree"),
        ];
        var p = new ProcedureDefinition("ReadTest", "read", [new StepDefinition("read", StatePredicate.Any, new ReadAction("test.ready", "bag"), Expectation.Nothing)], ProcedureLimits.Default);
        AssertStatus(ProcedureStatus.Succeeded, H.Run(p, JobKind.ReadOnlyScan));
        var rec = H.Journal.Records.Single(r => FakeJournal.Str(r, "event") == "read");
        Assert.AreEqual("Ok:pair:c100:g991", FakeJournal.Str(rec, "r.pair.1"));
        Assert.AreEqual("Unknown:digit-low-score:c0:g741", FakeJournal.Str(rec, "r.pair.2"));
        Assert.AreEqual("Ok:text-agree:c60", FakeJournal.Str(rec, "r.product.name"), "no glyph score without a digit read");
        Assert.IsFalse(rec.Fields.Values.Any(v => v?.ToString()?.Contains("White Toast", StringComparison.OrdinalIgnoreCase) == true), "never the read text (item names)");
        Assert.IsFalse(rec.Fields.Values.Any(v => v?.ToString()?.Contains("40/1", StringComparison.Ordinal) == true), "never the read numbers as text");
    }

    [TestMethod]
    public void FullFrameCapture_OnlyInSurveyJobs()
    {
        var p = new ProcedureDefinition("Shot", "shot", [new StepDefinition("cap", StatePredicate.Any, new CaptureAction("full", CaptureScope.FullFrameMasked), Expectation.Nothing)], ProcedureLimits.Default);
        AssertStatus(ProcedureStatus.Refused, H.Run(p, JobKind.Navigation));
        Assert.AreEqual(0, H.Artifacts.Images.Count);
        AssertStatus(ProcedureStatus.Succeeded, H.Run(p, JobKind.Survey));
        Assert.AreEqual(ArtifactCategory.Survey, H.Artifacts.Images.Single().Category);
    }

    [TestMethod]
    public void CallProcedure_SharesBudget_AndDepthIsLimited()
    {
        var inner = new ProcedureDefinition("OuterCall", "call", [new StepDefinition("call", StatePredicate.Any, new CallProcedureAction("OpenBagKey"), Expectation.Nothing)],
            ProcedureLimits.Default with { MaxInputBatches = 2 });
        var r = H.Run(inner);
        AssertHalt(HaltCode.BudgetExceeded, r);
        Assert.AreEqual(2, H.Game.Inputs);
    }

    [TestMethod]
    public void InvalidAdHocProcedure_Refused()
    {
        var bad = new ProcedureDefinition("Bad", "bad", [new StepDefinition("b", StatePredicate.Any, new KeyAction(GameKey.B), Expectation.Nothing)], ProcedureLimits.Default);
        var r = H.Run(bad);
        AssertStatus(ProcedureStatus.Refused, r);
        Assert.AreEqual(0, H.Game.Inputs, "a letter key without Pre.HudClean is refused");
    }

    [TestMethod]
    public void AwaitCraft_NoInput_EndsWhenSessionEnds()
    {
        H.Log.Craft = new CraftSessionInfo(true, "UI_LIFE_ALCHEMIST_MAKE_POT", H.Clock.GameNow, null, 2, H.Clock.GameNow, null, false);
        H.Clock.At(TimeSpan.FromSeconds(12), () => H.Log.Craft = H.Log.Craft with { InProgress = false, IsBlockCount = 5, EndedAt = H.Clock.GameNow });
        var p = new ProcedureDefinition("Await", "await", [new StepDefinition("await", StatePredicate.Any, new AwaitCraftAction(4), Expectation.Nothing)], ProcedureLimits.Default);
        var r = H.Run(p);
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(0, H.Game.Inputs);
    }

    [TestMethod]
    public void AwaitCraft_IsBlockWithoutStation_BlockedCraftSpot()
    {
        H.Log.Craft = new CraftSessionInfo(true, null, H.Clock.GameNow, null, 1, H.Clock.GameNow, null, false);
        var p = new ProcedureDefinition("Await", "await", [new StepDefinition("await", StatePredicate.Any, new AwaitCraftAction(4), Expectation.Nothing)], ProcedureLimits.Default);
        var r = H.Run(p);
        AssertHalt(HaltCode.BlockedCraftSpot, r);
    }
}

[TestClass]
public sealed class LateAndJournalScenarioTests : ScenarioBase
{
    // (9) log late: LateSuccess and no second click
    [TestMethod]
    public void S09_LateLog_LateSuccess_NoSecondClick()
    {
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, () => H.Clock.At(TimeSpan.FromSeconds(4), () => H.Log.Show("UI_BAG_MAIN"))));
        var r = H.Run("OpenBagClick");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(StepOutcome.LateSuccess, AttemptsOf(r, "open").Single().Outcome);
        Assert.AreEqual(1, H.Game.ClickCount);
    }

    [TestMethod]
    public void FirstOpenOfAUi_WaitsSixSeconds()
    {
        H.Log.Shown.Remove("UI_BAG_MAIN");
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () => H.Game.DefaultKeyEffect(req.Key)));
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () => H.Clock.At(TimeSpan.FromSeconds(5), () => H.Log.Show("UI_BAG_MAIN"))));
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(StepOutcome.Ok, AttemptsOf(r, "open").Single().Outcome, "5 s is inside the 6 s first-open timeout");
    }

    // (30) every journal record passes the lint (also checked after every test)
    [TestMethod]
    public void S30_JournalRecordsAreCodesOnly()
    {
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, () => H.Log.Show("UI_HOME_PET_VIEW"), movedInP4: true));
        H.Run("OpenBagClick");
        H.Run("Calibrate", JobKind.Maintenance);
        Assert.IsTrue(H.Journal.Records.Count > 20);
        foreach (var kind in new[] { JournalKind.StepStart, JournalKind.Decision, JournalKind.Input, JournalKind.Slide, JournalKind.AutoCollect })
        {
            Assert.IsTrue(H.Journal.OfKind(kind).Any(), kind.ToString());
        }
        H.AssertJournalClean();
        var decision = H.Journal.OfKind(JournalKind.Decision).First(d => d.Fields.ContainsKey("outcome"));
        foreach (var key in new[] { "outcome", "class", "action", "counted", "uncounted", "stateHash", "inputs" })
        {
            Assert.IsTrue(decision.Fields.ContainsKey(key), key);
        }
    }

    [TestMethod]
    public void HaltWritesDiagnosticsBundle_WhenRuntimeDirExists()
    {
        var paths = TestPaths.ForTest("diag");
        Directory.CreateDirectory(paths.RuntimeDir);
        var artifacts = new ArtifactStore(paths, H.Clock, new PassFilter());
        var runner = new ProcedureRunner(H.Game, H.Log, H.Classifier, H.Vision, H.Templates, H.Readers, H.Screen, H.Procedures, H.Map, H.Identifier, H.Planner,
            H.Journal, artifacts, H.Halt, H.Notifier, H.Switches, H.Config, H.Trust, H.Stop, H.Pause, H.Clock, paths);
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, () => H.Log.Show("UI_SHOP_MAIN")));
        var r = runner.Run("OpenBagClick", H.Context(), CancellationToken.None);
        AssertHalt(HaltCode.ForbiddenUi, r);
        var dir = r.Halt!.DiagnosticsPath;
        Assert.IsNotNull(dir);
        Assert.IsTrue(File.Exists(Path.Combine(dir!, "halt.json")));
        Assert.IsTrue(File.Exists(Path.Combine(dir!, "attempts.json")));
        Assert.IsTrue(File.Exists(Path.Combine(dir!, "journal.jsonl")));
        Assert.IsTrue(File.Exists(Path.Combine(dir!, "log.log")));
        Assert.IsTrue(File.Exists(Path.Combine(dir!, "hud-bag.png")), string.Join(",", Directory.GetFiles(dir!).Select(Path.GetFileName)));
        Assert.IsTrue(dir!.StartsWith(paths.DiagnosticsDir, StringComparison.OrdinalIgnoreCase));
        var halt = System.Text.Json.JsonSerializer.Deserialize<HaltReport>(File.ReadAllBytes(Path.Combine(dir!, "halt.json")), ContractJson.Options);
        Assert.AreEqual(HaltCode.ForbiddenUi, halt!.Code);
    }

    private sealed class PassFilter : ILogPrivacyFilter
    {
        public bool IsAllowed(string rawLine) => true;

        public string? Filter(string rawLine) => rawLine;
    }
}

[TestClass]
public sealed class ParkScenarioTests : ScenarioBase
{
    [TestMethod]
    public void Park_CompensatesWithEscForUisThisJobOpened()
    {
        H.Templates.Add(FakeTemplates.Def("test.bagcell", 960, 500, TemplateStatus.Verified, 1, null, TargetTags.Nav));
        H.Game.ReadinessOverride = q => q.Kind == InputKind.Click ? H.Game.Report(NotReadyReason.CursorMoving) : null;
        var proc = new ProcedureDefinition("BagCell", "open the bag, click a cell", [
            new StepDefinition("reset", StatePredicate.Any, new ResetAction(), new Expectation([UiExpectation.HiddenTop(null)])),
            new StepDefinition("open", new StatePredicate(HudClean: true), new KeyAction(GameKey.B), new Expectation(UiExpectation.Opened("UI_BAG_MAIN")), 3000, StepFlags.CompensateOnPark),
            new StepDefinition("cell", new StatePredicate(TopUi: "UI_BAG_MAIN"), new ClickAction("test.bagcell"), new Expectation([new UiExpectation(UiVerb.Show, "UI_ITEM_TIPS")])),
        ], ProcedureLimits.Default);
        using var cts = new CancellationTokenSource();
        H.Clock.At(TimeSpan.FromMinutes(15), cts.Cancel);
        var r = H.Run(proc, ct: cts.Token);
        AssertStatus(ProcedureStatus.Cancelled, r);
        Assert.AreEqual(0, H.Game.ClickCount);
        var afterB = H.Game.KeyRequests.SkipWhile(k => k.Key != GameKey.B).Skip(1).ToList();
        Assert.AreEqual(1, afterB.Count, "exactly one compensation Esc");
        Assert.IsTrue(afterB[0].ExpectedUi.Any(e => e.UiName == "UI_BAG_MAIN"));
        Assert.IsNull(H.Log.State.AdvisoryTop, "the bag this job opened was closed on park");
        Assert.IsTrue(H.Journal.OfKind(JournalKind.Park).Any(p => FakeJournal.Str(p, "event") == "start"));
    }

    [TestMethod]
    public void Park_ExpiresAfterFourHours_Cancelled_NotifiesAtThirtyMinutes()
    {
        H.Log.Show("UI_MAIL");
        var r = H.Run("OpenBagKey");
        AssertStatus(ProcedureStatus.Cancelled, r);
        Assert.AreEqual(0, H.Game.Inputs);
        Assert.AreEqual(1, H.Notifier.Sent.Count(n => n.Code == "park.userUiOpen"));
        Assert.AreEqual(1, H.Notifier.Sent.Count(n => n.Code == "park.long"));
        Assert.AreEqual(1, H.Notifier.Sent.Count(n => n.Code == "park.expired"));
        Assert.IsTrue(H.Clock.Elapsed >= HardRules.ParkExpiry);
        Assert.IsTrue(H.Journal.OfKind(JournalKind.Park).Any(p => FakeJournal.Str(p, "event") == "expire"));
    }

    [TestMethod]
    public void Park_CompensationSkippedWhileGateClosed_RecordedAsPending()
    {
        var proc = new ProcedureDefinition("BagWait", "open the bag then wait on a closed gate", [
            new StepDefinition("reset", StatePredicate.Any, new ResetAction(), new Expectation([UiExpectation.HiddenTop(null)])),
            new StepDefinition("open", new StatePredicate(HudClean: true), new KeyAction(GameKey.B), new Expectation(UiExpectation.Opened("UI_BAG_MAIN")), 3000, StepFlags.CompensateOnPark),
            new StepDefinition("close", new StatePredicate(TopUi: "UI_BAG_MAIN"), new KeyAction(GameKey.Escape), new Expectation([UiExpectation.HiddenTop("UI_BAG_MAIN")])),
        ], ProcedureLimits.Default);
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () => H.Game.DefaultKeyEffect(req.Key)));
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () =>
        {
            H.Game.DefaultKeyEffect(req.Key);
            H.Game.Prefs = H.Game.Prefs with { FullscreenMode = 1 };
        }));
        using var cts = new CancellationTokenSource();
        H.Clock.At(TimeSpan.FromMinutes(12), cts.Cancel);
        var r = H.Run(proc, ct: cts.Token);
        AssertStatus(ProcedureStatus.Cancelled, r);
        Assert.AreEqual(2, H.Game.Inputs, "no Esc while the display mode gate is closed");
        CollectionAssert.Contains(r.Pending.Select(p => p.Code).ToList(), "compensate.esc");
    }
}

[TestClass]
public sealed class InterferenceScenarioTests : ScenarioBase
{
    [TestMethod]
    public void ForeignStackUiDuringStep_UserInterference_ParksWithoutTouchingIt()
    {
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () => H.Game.DefaultKeyEffect(req.Key)));
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () => H.Clock.At(TimeSpan.FromSeconds(2), () => H.Log.Show("UI_MAIL"))));
        using var cts = new CancellationTokenSource();
        H.Clock.At(TimeSpan.FromMinutes(3), cts.Cancel);
        var r = H.Run("OpenBagKey", ct: cts.Token);
        AssertStatus(ProcedureStatus.Cancelled, r);
        var ui = r.Attempts.First(a => a.Outcome == StepOutcome.UserInterference);
        Assert.AreEqual(RecoveryAction.Park, ui.Decision);
        Assert.AreEqual("UI_MAIL", ui.UiSeen);
        Assert.IsFalse(H.Game.KeyRequests.Any(k => k.ExpectedUi.Any(e => e.UiName == "UI_MAIL")), "the user's UI is never closed by us");
        Assert.AreEqual(2, H.Game.Inputs);
    }

    [TestMethod]
    public void ForeignStackUiDuringStep_CommitJobDeferred()
    {
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () => H.Game.DefaultKeyEffect(req.Key)));
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () => H.Clock.At(TimeSpan.FromSeconds(2), () => H.Log.Show("UI_MAIL"))));
        var r = H.Run("OpenBagKey", JobKind.Commit);
        AssertStatus(ProcedureStatus.Deferred, r);
        Assert.AreEqual("commit.userInterference", r.EndDetail);
        Assert.AreEqual(2, H.Game.Inputs);
    }
}

/// <summary>
/// The engine's opt-in for GameIO's off-screen anchor patch rule (SPEC 7.3): only a screen anchor pressed at a step
/// offset larger than an aim, away from deny zones, ungated and outside gated or commit procedures opts in.
/// </summary>
[TestClass]
public sealed class PatchOptInScenarioTests : ScenarioBase
{
    private static StepDefinition Click(string id, string templateId, PointI offset) =>
        new(id, new StatePredicate(HudClean: true), new ClickAction(templateId, offset), Expectation.Nothing);

    [TestMethod]
    public void PatchOptIn_IsOffForAimSizedOffsets_NearDenyZones_AndGatedTargets()
    {
        // A screen anchor on the HUD at (760,300): its deny zones (8.3) apply. (300,0) is the positive control.
        H.Templates.Add(FakeTemplates.Def("test.anchor", 760, 300, TemplateStatus.Verified, 1, HudState.Collapsed, TargetTags.ScreenAnchor));
        H.Templates.Add(FakeTemplates.Def("test.anchor.gated", 760, 300, TemplateStatus.Verified, 1, HudState.Collapsed, TargetTags.ScreenAnchor, AutomationSwitch.Homestead));
        var p = new ProcedureDefinition("PatchOptIn", "test", [
            new StepDefinition("reset", StatePredicate.Any, new ResetAction(), new Expectation([UiExpectation.HiddenTop(null)])),
            Click("cell", "test.anchor", new PointI(300, 0)),
            Click("aim", "test.anchor", new PointI(12, 8)),
            Click("nearChat", "test.anchor", new PointI(-200, 400)),
            Click("gated", "test.anchor.gated", new PointI(300, 0)),
        ], ProcedureLimits.Default);

        AssertStatus(ProcedureStatus.Succeeded, H.Run(p));

        CollectionAssert.AreEqual(
            new[] { ("cell", true, false), ("aim", false, false), ("nearChat", false, true), ("gated", false, false) },
            H.Game.ClickRequests.Select(c => (c.StepId, c.PatchVerify, c.NearDenyZone)).ToArray());
        Assert.IsTrue(H.Game.ClickRequests.All(c => (c.LocatedFrame is not null) == c.PatchVerify), "the frame goes with the opt-in only");
    }

    [TestMethod]
    public void PatchOptIn_IsOffInAGatedProcedure_ForClicksAndDrags()
    {
        // e.g. crafting.prepare: a navigation procedure gated by the crafting switch.
        H.Templates.Add(FakeTemplates.Def("craft.list.anchor", 485, 123, TemplateStatus.Verified, 1, null, TargetTags.ScreenAnchor));
        var p = new ProcedureDefinition("GatedNav", "test", [
            new StepDefinition("card", StatePredicate.Any, new ClickAction("craft.list.anchor", new PointI(-121, 375)), Expectation.Nothing),
            new StepDefinition("scroll", StatePredicate.Any, new DragAction("craft.list.anchor", new PointI(-65, 737), new PointI(0, -280)), Expectation.Nothing),
        ], ProcedureLimits.Default, ProcedureKind.Navigation, AutomationSwitch.Crafting);

        AssertStatus(ProcedureStatus.Succeeded, H.Run(p));

        Assert.IsFalse(H.Game.ClickRequests.Single().PatchVerify);
        Assert.IsNull(H.Game.ClickRequests.Single().LocatedFrame);
        Assert.IsFalse(H.Game.DragRequests.Single().PatchVerify);
        Assert.IsNull(H.Game.DragRequests.Single().LocatedFrame);

        // The same steps in an ungated navigation procedure opt in.
        AssertStatus(ProcedureStatus.Succeeded, H.Run(p with { Id = "UngatedNav", Gate = AutomationSwitch.None }));
        Assert.IsTrue(H.Game.ClickRequests[^1].PatchVerify);
        Assert.IsTrue(H.Game.DragRequests[^1].PatchVerify);

        // Inside a Craft (commit) job the read-only walk still opts in: the procedures decide, not the job (2026-10-15);
        // the commit procedure crafting.loop keeps the strict check (CraftLoopRoundTests).
        AssertStatus(ProcedureStatus.Succeeded, H.Run(p with { Id = "WalkInCraftJob", Gate = AutomationSwitch.None }, JobKind.Commit, AutomationSwitch.Crafting));
        Assert.IsTrue(H.Game.ClickRequests[^1].PatchVerify);
        Assert.IsTrue(H.Game.DragRequests[^1].PatchVerify);
    }
}
