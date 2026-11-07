using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Host;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.EngineNav.Testing;

namespace Companion.Engine.Tests;

/// <summary>
/// Foreground mode (SPEC 7.3b, the user's decision 2026-11-02): the game in front with the user resting on it is not
/// "user active"; the user's input is. Inputs in that mode send no focus messages and have no press block.
/// </summary>
[TestClass]
public sealed class ForegroundModeEngineTests : ScenarioBase
{
    private void GameInFront()
    {
        H.Game.ForegroundMode = true;
        H.Game.UserActivates();
        H.Clock.Advance(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public void GameInFront_UserResting_KeysAndClicksRun_WithoutAFocusFailure()
    {
        GameInFront();

        AssertStatus(ProcedureStatus.Succeeded, H.Run("OpenBagKey"));
        var r = H.Run("OpenBagClick");

        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(1, H.Game.ClickCount);
        Assert.IsFalse(r.Attempts.Any(a => a.Outcome is StepOutcome.FocusFailedOnce or StepOutcome.FocusFailed or StepOutcome.UserActive),
            "foreground mode reports no RecoverFocus by design");
        Assert.IsTrue(H.Journal.OfKind(JournalKind.Input).Any(i => FakeJournal.Str(i, "mode") == nameof(InputMode.Foreground)), "the mode is journaled");
    }

    [TestMethod]
    public void GameInFront_UserTyping_IsUserActive_NotAnInput()
    {
        GameInFront();
        H.Game.ForegroundNotReady = NotReadyReason.UserInputRecent;
        H.Clock.At(TimeSpan.FromSeconds(30), () => H.Game.ForegroundNotReady = NotReadyReason.None);

        var r = H.Run("OpenBagKey");

        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.IsTrue(H.Game.Timeline.All(t => t.At >= TimeSpan.FromSeconds(30)), "nothing pressed while the user typed");
    }

    [TestMethod]
    public void GameInFront_SwitchOff_StillWaitsAsBefore()
    {
        H.Game.UserActivates(); // ForegroundMode stays off: readiness says GameForeground
        H.Clock.At(TimeSpan.FromSeconds(20), () => H.Game.UserLeaves());

        var r = H.Run("OpenBagKey");

        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.IsTrue(H.Game.Timeline.All(t => t.At >= TimeSpan.FromSeconds(20) + HardRules.CooldownAfterGameForeground));
    }

    [TestMethod]
    public void AClickWithoutResult_IsNotRepeatedBlind_AsAnUnregisteredPress()
    {
        // cover mode: no press block = the game did not register the press, an uncounted retry; foreground mode has no
        // press block ever, so a delivered press without a result must not be repeated as if it never happened
        GameInFront();
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, effect: null));

        var r = H.Run("OpenBagClick");

        Assert.IsFalse(r.Attempts.Any(a => a.Outcome == StepOutcome.PressNotRegistered), "no blind repeat");
        Assert.IsTrue(r.Attempts.Any(a => a.Outcome == StepOutcome.None && a.DetailCode?.Contains("click.nothing", StringComparison.Ordinal) == true)
            || r.Attempts.Any(a => a.CauseStartsWith("click.nothing")), "counted as nothing happened");
    }

    [TestMethod]
    public void TheUsersInputDuringAPress_IsTheirActivity_NeverABlindRepeat()
    {
        // safety review 2026-11-02 (M2, M3): what opened may be the user's; the press is not repeated as a failed one,
        // the run waits for the user and resumes from the latest step whose precondition holds
        GameInFront();
        H.Game.UserInputDuringInputs = true;
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, effect: null));

        var r = H.Run("OpenBagClick");

        Assert.IsTrue(r.Attempts.Any(a => a.Outcome == StepOutcome.UserActive && a.CauseStartsWith("foreground.userInput")), "the user's activity");
        Assert.IsFalse(r.Attempts.Any(a => a.Outcome is StepOutcome.PressNotRegistered or StepOutcome.Uncertain));
    }

    [TestMethod]
    public void TheUsersClickOnTheSlidGame_BeforeOurDown_IsTheirActivity()
    {
        // the slide put our target under the cursor, so the user's click pressed it: never repeated as a failed press
        GameInFront();
        H.Game.ClickScript.Enqueue(_ => H.Game.NotDeliveredClick(InputDisposition.AbortedAfterFocus, AbortReason.UserButton, InputPhase.P3SlideToDown, 0) with
        {
            Mode = InputMode.Foreground,
            Cursor = new CursorEvidence(new PointI(918, 672), false, false, false, UserButton: true, false),
        });

        var r = H.Run("OpenBagClick");

        Assert.IsTrue(r.Attempts.Any(a => a.Outcome == StepOutcome.UserActive && a.CauseStartsWith("aborted.userInputInGame")));
        Assert.IsFalse(r.Attempts.Any(a => a.Outcome == StepOutcome.AbortedAfterFocus), "not the back-off retry of a failed press");
    }

    [TestMethod]
    public void InputAfterOurPress_InsideTheReadWindow_IsAlsoTheUsers()
    {
        // GameIO's watchdog ends with the section; the game-use clock covers the rest of the attribution window (M3)
        GameInFront();
        H.Game.ClickScript.Enqueue(req =>
        {
            var rep = H.Game.SentClick(req, effect: null);
            H.Game.LastGameUseMono = H.Clock.Monotonic; // the user clicks in the game right after our press
            return rep;
        });

        var r = H.Run("OpenBagClick");

        Assert.IsTrue(r.Attempts.Any(a => a.Outcome == StepOutcome.UserActive && a.CauseStartsWith("foreground.userInput")));
    }

    [TestMethod]
    public void TheUserSwitchingAwayAfterOurDown_TheLogDecides()
    {
        GameInFront();
        H.Game.ClickScript.Enqueue(req =>
        {
            var rep = H.Game.SentClick(req, () => H.Game.DefaultClickEffect(req.TemplateId), disposition: InputDisposition.UserTookOver);
            H.Game.UserLeaves();
            return rep with { Abort = AbortReason.ForegroundLost };
        });

        var r = H.Run("OpenBagClick");

        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(1, H.Game.ClickCount, "the bag opened: the delivered press is not repeated");
    }

    [TestMethod]
    public void AGameThatStoppedRespondingWithOurPressUnread_IsAHalt()
    {
        GameInFront();
        var undrained = new WindowIncident(IncidentKind.UndrainedSlideBack, null, null, true, RestoreRung.None, "drain.timeout");
        H.Game.ClickScript.Enqueue(req => H.Game.SentClick(req, effect: null, incident: undrained, disposition: InputDisposition.Incident));

        var r = H.Run("OpenBagClick");

        AssertHalt(HaltCode.GameStoppedMidPress, r);
        Assert.AreEqual(1, H.Game.ClickCount, "never retried");
    }

    [TestMethod]
    public void ALetterKeyRefusedForAnOpenChat_IsABoundedWait()
    {
        // a stale chat entry in the log (the chat often closes without a line) must not hold a letter key forever
        GameInFront();
        var chatChecks = 0;
        H.Game.ReadinessOverride = q => q.LetterKey && chatChecks++ == 0
            ? H.Game.Report(NotReadyReason.ChatOpen) with { Mode = InputMode.Foreground, SinceGameUse = TimeSpan.FromMinutes(1) }
            : null;
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () => H.Game.DefaultKeyEffect(req.Key)));
        H.Game.KeyScript.Enqueue(_ => H.Game.NotDeliveredKey(InputDisposition.NotSent, AbortReason.NotReady, 0));

        var r = H.Run("OpenBagKey");

        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.IsTrue(r.Attempts.Any(a => a.Outcome == StepOutcome.NotSent && a.CauseStartsWith("notSent.ChatOpen")),
            string.Join(",", r.Attempts.Select(a => a.Outcome + ":" + a.DetailCode + "/" + a.Cause)));
    }

    [TestMethod]
    public void TheUserSwitchingAwayAfterOurKey_TheLogDecides_NoSecondPress()
    {
        GameInFront();
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () => H.Game.DefaultKeyEffect(req.Key)));
        H.Game.KeyScript.Enqueue(req =>
        {
            var rep = H.Game.SentKey(req, () => H.Game.DefaultKeyEffect(req.Key)); // B opened the bag
            H.Game.UserLeaves();
            return rep with { Disposition = InputDisposition.UserTookOver, Abort = AbortReason.ForegroundLost };
        });

        var r = H.Run("OpenBagKey");

        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(1, H.Keys(GameKey.B), "the delivered B is not pressed again");
    }

    [TestMethod]
    public void AWindowTheUserOpensAroundTheResetEsc_IsTheirActivity_NotAHalt()
    {
        // re-review 2026-11-02: in front, a user hotkey just after our Esc must not read as "Esc opened a window"
        GameInFront();
        H.Game.KeyScript.Enqueue(req =>
        {
            var rep = H.Game.SentKey(req, () => H.Game.DefaultKeyEffect(req.Key));
            H.Log.Show("UI_MAIL"); // the user's own key (t: Mail) right after our Esc
            return rep with { UserInputDuring = true };
        });

        var r = H.Run("OpenBagKey");

        Assert.AreNotEqual(ProcedureStatus.Halted, r.Status, string.Join(",", r.Attempts.Select(a => a.Outcome + ":" + a.DetailCode)));
        Assert.IsTrue(r.Attempts.Any(a => a.Outcome == StepOutcome.UserActive && a.CauseStartsWith("reset.userInput")),
            string.Join(",", r.Attempts.Select(a => a.Outcome + ":" + a.DetailCode + "/" + a.Cause)));
    }

    [TestMethod]
    public void TheUserSwitchingAwayMidKey_IsUserActive()
    {
        GameInFront();
        H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, () => H.Game.DefaultKeyEffect(req.Key)));
        H.Game.KeyScript.Enqueue(_ =>
        {
            H.Game.UserLeaves();
            return H.Game.NotDeliveredKey(InputDisposition.NotSent, AbortReason.ForegroundLost, 0);
        });

        var r = H.Run("OpenBagKey");

        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.IsTrue(r.Attempts.Any(a => a.Outcome == StepOutcome.UserActive), "the switch away is the user's activity");
    }

    private ProcedureDefinition CommitProcedure()
    {
        H.Templates.Add(FakeTemplates.Def("test.make", 960, 900, TemplateStatus.Verified, 1, null, TargetTags.Commit, AutomationSwitch.Crafting));
        return new ProcedureDefinition("TestCommit", "commit test", [
            new StepDefinition("reset", StatePredicate.Any, new ResetAction(), new Expectation([UiExpectation.HiddenTop(null)])),
            new StepDefinition("make", new StatePredicate(HudClean: true), new ClickAction("test.make"), new Expectation(UiExpectation.Opened("UI_LIFE_ALCHEMIST_MAKE_POT")), 3000, StepFlags.Commit),
        ], ProcedureLimits.Default, ProcedureKind.Commit, AutomationSwitch.Crafting);
    }

    private ProcedureResult CommitWithTheGameInFront(bool userUsesTheGameDuringTheJob)
    {
        var make = CommitProcedure();
        H.Switches.Crafting = true;
        GameInFront();
        var warm = new ProcedureDefinition("TestWarm", "warm", [new StepDefinition("wait", StatePredicate.Any, new WaitAction(10), Expectation.Nothing)], ProcedureLimits.Default);
        var ctx = H.Context(JobKind.Commit, AutomationSwitch.Crafting, "fg-job");
        AssertStatus(ProcedureStatus.Succeeded, H.Runner.Run(warm, ctx, default)); // the job starts
        H.Clock.Advance(TimeSpan.FromMinutes(1));
        if (userUsesTheGameDuringTheJob)
        {
            H.Game.LastGameUseMono = H.Clock.Monotonic; // a click in the game, which stays in front
        }

        H.Clock.Advance(TimeSpan.FromMinutes(1));
        return H.Runner.Run(make, ctx, default);
    }

    [TestMethod]
    public void CommitGate_TheGameInFrontWithTheUserResting_IsNotUserActivity()
    {
        var r = CommitWithTheGameInFront(userUsesTheGameDuringTheJob: false);

        Assert.AreNotEqual("commit.userActivity", r.EndDetail);
        Assert.AreEqual(1, H.Game.ClickCount, "Make pressed once");
    }

    [TestMethod]
    public void CommitGate_TheUsersInputInTheGameDuringTheJob_DefersTheMake()
    {
        var r = CommitWithTheGameInFront(userUsesTheGameDuringTheJob: true);

        AssertStatus(ProcedureStatus.Deferred, r);
        Assert.AreEqual("commit.userActivity", r.EndDetail);
        Assert.AreEqual(0, H.Game.ClickCount, "Make never pressed");
    }
}
