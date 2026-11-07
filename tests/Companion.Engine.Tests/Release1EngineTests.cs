using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.EngineNav.Testing;

namespace Companion.Engine.Tests;

/// <summary>Regression tests for the Release 1 engine items of the rules audit (INCIDENTS.md, R1.1 to R1.3).</summary>
[TestClass]
public sealed class Release1EngineTests : ScenarioBase
{
    // R1.1 (incident 5): halts with "inputs 30, clicks 0"; aborts after the focus messages used up the input budget
    [TestMethod]
    public void AbortedPresses_DoNotUseTheInputBudget()
    {
        for (var k = 0; k < 5; k++)
        {
            H.Game.ClickScript.Enqueue(_ => H.Game.NotDeliveredClick(InputDisposition.AbortedAfterFocus, AbortReason.CursorMoved, InputPhase.P2FocusToSlide, 4));
        }

        var r = H.Run("OpenBagClick");

        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(H.Game.Inputs - 5, r.InputBatches, "only delivered presses are inputs (the reset Esc, the click, the closing Esc)");
        Assert.IsTrue(H.Journal.OfKind(JournalKind.Input).Any(j => FakeJournal.Str(j, "aborted") == "5"), "the aborts are journaled apart");
    }

    [TestMethod]
    public void EndlessAborts_EndDeferred_NeverAHalt()
    {
        for (var k = 0; k < 400; k++)
        {
            H.Game.ClickScript.Enqueue(_ => H.Game.NotDeliveredClick(InputDisposition.AbortedAfterFocus, AbortReason.SlidTemplateMismatch, InputPhase.P3SlideToDown, 4));
        }

        var r = H.Run("OpenBagClick");

        AssertStatus(ProcedureStatus.Deferred, r);
        Assert.IsNull(r.Halt, "nothing reached the game: no latched HALT");
        Assert.AreEqual(H.Game.Keys(GameKey.Escape), r.InputBatches, "only the delivered reset Esc is an input");
        Assert.AreEqual(0, r.Clicks);
    }

    // R1.2 (incident 5, 10-18 16:12): a dialog Esc cannot close; every nested reset pressed 2 more Escs until the HALT
    [TestMethod]
    public void AFailingReset_NeverRunsAnotherReset()
    {
        H.Log.Show("UI_ITEM_REWARD");
        for (var k = 0; k < 40; k++)
        {
            H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, null)); // the Esc arrives, nothing closes
        }

        var r = H.Run("OpenBagKey"); // starts with a reset step

        Assert.AreEqual(ProcedureStatus.Deferred, r.Status, r.EndDetail);
        Assert.AreEqual("exhausted.reset.noProgress", r.EndDetail, "a soft end in the engine's ledger");
        Assert.IsTrue(H.Game.Keys(GameKey.Escape) <= 2, $"Escs pressed: {H.Game.Keys(GameKey.Escape)}");
        Assert.IsFalse(H.Journal.Records.Any(j => j.ProcedureId == "Reset.internal"), "no internal reset ran");
    }

    // safety review 2026-10-23: a reset that fails in every run (rule mode comes back) must still escalate
    [TestMethod]
    public void AResetThatFailsInEveryRun_IsTheHaltOnTheFourthWithinThirtyMinutes()
    {
        H.Log.Show("UI_ITEM_REWARD");
        for (var k = 0; k < 200; k++)
        {
            H.Game.KeyScript.Enqueue(req => H.Game.SentKey(req, null));
        }

        var runs = Enumerable.Range(0, 4).Select(_ => H.Run("OpenBagKey")).ToList();

        Assert.IsTrue(runs.Take(3).All(r => r.Status == ProcedureStatus.Deferred), string.Join(",", runs.Select(r => r.Status)));
        AssertHalt(HaltCode.PreconditionLostExhausted, runs[3]);
        Assert.IsTrue(H.Game.Keys(GameKey.Escape) <= 4 * 2, "never more than the reset's own Escs per run");
    }

    // safety review 2026-10-23: a user takeover aborts with an "io." detail and escaped the abort bound
    [TestMethod]
    public void EveryKindOfAbort_CountsTowardsTheAbortBound()
    {
        for (var k = 0; k < 200; k++)
        {
            H.Game.KeyScript.Enqueue(_ => H.Game.NotDeliveredKey(InputDisposition.UserTookOver, AbortReason.None, 4));
        }

        var r = H.Run("OpenBagKey");

        AssertStatus(ProcedureStatus.Deferred, r);
        Assert.AreEqual("exhausted.aborted", r.EndDetail);
        Assert.IsTrue(H.Game.KeyRequests.Count <= ProcedureLimits.Default.MaxInputBatches + 2, $"key batches: {H.Game.KeyRequests.Count}");
    }

    // R1.3 (incident 10): the keyboard-layout guard refused the key 1,084 times in 18 minutes
    [TestMethod]
    public void ARefusedKeyForTheLayout_EndsDeferredWithinTheLimit()
    {
        for (var k = 0; k < 2000; k++)
        {
            H.Game.KeyScript.Enqueue(_ => H.Game.NotDeliveredKey(InputDisposition.NotSent, AbortReason.KeyboardLayoutChanged, 0));
        }
        using var cts = new CancellationTokenSource();
        H.Clock.At(TimeSpan.FromMinutes(45), cts.Cancel);

        var r = H.Run("OpenBagKey", ct: cts.Token);

        AssertStatus(ProcedureStatus.Deferred, r);
        Assert.AreEqual("keyboardLayout", r.EndDetail);
        Assert.IsTrue(H.Game.KeyRequests.Count <= HardRules.UnreachableLimit.TotalSeconds + 5, $"attempts: {H.Game.KeyRequests.Count}");
        Assert.IsTrue(H.Notifier.Sent.Any(n => n.Code == "wait.keyboardLayout"), "the user is told to switch the layout back");
    }

    // R2.8 (audit 25): job-wide counts lived for the whole job; a rule-mode job runs for days
    [TestMethod]
    public void TwoFocusFailuresHoursApart_AreNotAHalt_ButTwoInAWindowStillAre()
    {
        var job = H.Context(jobId: "rule-mode-job");
        H.Game.KeyScript.Enqueue(_ => H.Game.NotDeliveredKey(InputDisposition.FocusFailed, AbortReason.None, 4));
        AssertStatus(ProcedureStatus.Succeeded, H.Runner.Run("OpenBagKey", job, CancellationToken.None));

        H.Clock.Advance(TimeSpan.FromHours(2));
        H.Game.KeyScript.Enqueue(_ => H.Game.NotDeliveredKey(InputDisposition.FocusFailed, AbortReason.None, 4));
        AssertStatus(ProcedureStatus.Succeeded, H.Runner.Run("OpenBagKey", job, CancellationToken.None));

        H.Clock.Advance(TimeSpan.FromMinutes(5));
        H.Game.KeyScript.Enqueue(_ => H.Game.NotDeliveredKey(InputDisposition.FocusFailed, AbortReason.None, 4));
        AssertHalt(HaltCode.FocusFailed, H.Runner.Run("OpenBagKey", job, CancellationToken.None));
    }

    // R2.8 (audit 49): the park's 4-hour expiry kept running while the user had paused
    [TestMethod]
    public void APauseHoldsTheParksClock()
    {
        for (var k = 0; k < 4000; k++)
        {
            H.Game.KeyScript.Enqueue(_ => H.Game.NotDeliveredKey(InputDisposition.NotSent, AbortReason.SendTimeout, 0));
        }

        var parkStart = TimeSpan.FromMinutes(10);
        H.Clock.At(parkStart + TimeSpan.FromSeconds(1), () => H.Pause.Pause("test"));
        H.Clock.At(parkStart + TimeSpan.FromHours(5), () => H.Pause.Resume("test"));
        using var cts = new CancellationTokenSource();
        H.Clock.At(TimeSpan.FromHours(7), cts.Cancel);

        var r = H.Run("OpenBagKey", ct: cts.Token);

        Assert.AreNotEqual("park.expired", r.EndDetail, "5 hours paused are not 5 hours parked");
        AssertStatus(ProcedureStatus.Deferred, r);
        StringAssert.StartsWith(r.EndDetail, "exhausted.park.repeated:");
    }

    [TestMethod]
    public void ASecondParkForTheSameCause_EndsTheProcedure()
    {
        for (var k = 0; k < 3000; k++)
        {
            H.Game.KeyScript.Enqueue(_ => H.Game.NotDeliveredKey(InputDisposition.NotSent, AbortReason.SendTimeout, 0));
        }
        using var cts = new CancellationTokenSource();
        H.Clock.At(TimeSpan.FromMinutes(45), cts.Cancel);

        var r = H.Run("OpenBagKey", ct: cts.Token);

        AssertStatus(ProcedureStatus.Deferred, r);
        StringAssert.StartsWith(r.EndDetail, "exhausted.park.repeated:");
        Assert.AreEqual(1, H.Journal.OfKind(JournalKind.Park).Count(p => FakeJournal.Str(p, "event") == "start"), "one park, then the end");
    }
}
