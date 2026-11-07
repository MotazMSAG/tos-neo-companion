using Companion.Contracts;
using Companion.Contracts.Features;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Perception;
using Companion.Contracts.Vision;
using Companion.Features.Kit;

namespace Companion.Features.Tests;

/// <summary>
/// Regression tests for the Release 1 decisions of the rules audit (INCIDENTS.md, R1.4 to R1.8). Each test pins the
/// decision that was wrong; the whole trip and goal loop are verified live.
/// </summary>
[TestClass]
public sealed class Release1DecisionTests
{
    private static readonly GatherTarget Grape = new(1001, "scarlet grape", 10);
    private static readonly GatherTarget Juice = new(1002, "golden juice", 10);
    private static readonly GatherTarget Essence = new(1003, "wisdom essence", 10);

    private static GatherOutcome Outcome(string code, JobResult? stop = null) => new(stop, [], [], code);

    // R1.4: a Begin deferred because the user was playing was "gather.notStarted"; two switched gathering off
    [TestMethod]
    public void ADeferredTrip_GetsItsOwnCode_AndKeepsTheCause()
    {
        var labelled = Gathering.Label(Outcome("gather.notStarted", JobKit.Failed("gather.deferred:commit.userActive")), sawDeferred: true, walkIncomplete: false);

        Assert.AreEqual("gather.deferred", labelled.Code);
        Assert.AreEqual("gather.notStarted", labelled.StepCode);
        Assert.AreEqual("gather.deferred:commit.userActive", labelled.Stop!.Code);
        Assert.IsTrue(labelled.EngineDeferred);
    }

    [TestMethod]
    public void ARealNonStart_KeepsItsCode()
    {
        var labelled = Gathering.Label(Outcome("gather.notStarted"), sawDeferred: false, walkIncomplete: false);

        Assert.AreEqual("gather.notStarted", labelled.Code);
        Assert.IsNull(labelled.StepCode);
    }

    [TestMethod]
    public void DeferredTrips_NeitherCountNorReset_TheFailureRun()
    {
        var deferred = Gathering.Label(Outcome("gather.notStarted", JobKit.Failed("gather.deferred:commit.userActive")), sawDeferred: true, walkIncomplete: false);
        var notStarted = Outcome("gather.notStarted");

        var r = Gathering.NextFailure(null, 0, deferred);
        r = Gathering.NextFailure(r.Code, r.Count, deferred);
        Assert.AreEqual(0, r.Count, "two deferred trips do not switch gathering off");

        r = Gathering.NextFailure(r.Code, r.Count, notStarted);
        r = Gathering.NextFailure(r.Code, r.Count, deferred);
        Assert.AreEqual(1, r.Count, "a deferral between two real non-starts does not reset the run");
        Assert.AreEqual(2, Gathering.NextFailure(r.Code, r.Count, notStarted).Count);
        Assert.AreEqual(0, Gathering.NextFailure(r.Code, r.Count, new GatherOutcome(null, [Grape], [], "gather.done")).Count);
    }

    // R1.5: a walk cut short (page unreadable, top never reached) reported unlearned targets "not in the helper"
    [TestMethod]
    public void AnIncompleteWalk_NeverReportsNotFound()
    {
        var learned = new HashSet<long> { Grape.ItemId };

        var (notSeen, notFound) = Gathering.Unmet([Grape, Essence], learned, queueFull: false, walkIncomplete: true);

        Assert.AreEqual(0, notFound.Count);
        CollectionAssert.AreEquivalent(new[] { Grape, Essence }, notSeen);
    }

    [TestMethod]
    public void ACompleteWalk_ReportsUnlearnedTargetsNotFound()
    {
        var learned = new HashSet<long> { Grape.ItemId };

        var (notSeen, notFound) = Gathering.Unmet([Grape, Essence], learned, queueFull: false, walkIncomplete: false);

        CollectionAssert.AreEqual(new[] { Essence }, notFound);
        CollectionAssert.AreEqual(new[] { Grape }, notSeen);
    }

    // R1.6: a capped or failed list walk was reported as "Not found in the Make list"
    [TestMethod]
    [DataRow("list.topNotReached")]
    [DataRow("list.dragIncomplete")]
    [DataRow("list.capped")]
    [DataRow("capture.failed")]
    [DataRow("read.screenMismatch")]
    public void OnlyAWalkToTheListEnd_ProvesACardAbsent(string code)
    {
        Assert.IsFalse(RecipeList.ProvesAbsent(code));
        Assert.IsTrue(RecipeList.ProvesAbsent("list.notFound"));
    }

    // R1.7: GatherAll dropped every target after a failed trip and reported "done"
    [TestMethod]
    public void AFailedTrip_KeepsEveryUntriedTarget()
    {
        var failed = Outcome("gather.notOpened");

        var next = Gathering.NextTargets([Grape, Juice, Essence], failed);

        CollectionAssert.AreEqual(new[] { Grape, Juice, Essence }, next);
        Assert.AreEqual("gather.notOpened", Gathering.TripFailure(failed));
    }

    [TestMethod]
    public void ACompletedTrip_PassesOnTheDeferredAndTheNotSeen()
    {
        var done = new GatherOutcome(null, [Grape], [], "gather.done") { Deferred = [Juice], NotSeen = [Essence] };

        var next = Gathering.NextTargets([Grape, Juice, Essence], done);

        CollectionAssert.AreEqual(new[] { Juice, Essence }, next);
        Assert.IsNull(Gathering.TripFailure(done));
    }

    [TestMethod]
    public void AFailedTrip_DropsOnlyWhatItSettled()
    {
        var partly = new GatherOutcome(JobKit.Failed("gather.deferred:commit.userActive"), [], [Essence], "gather.deferred") { AtLimit = [Juice] };

        CollectionAssert.AreEqual(new[] { Grape }, Gathering.NextTargets([Grape, Juice, Essence], partly));
    }

    // R1.8: a missing or day-old count capped the goal to 0 without pressing Make
    [TestMethod]
    public void OnlyAFreshExactCount_IsUsed()
    {
        var (_, env, _) = T.LoadReal();
        var context = new FakeJobContext(env.Procedures);
        var knowledge = context.KnowledgeFake!;
        var now = context.ClockFake.UtcNow;
        var pepper = ItemKey.FromDisplayName("peppercorn");
        var rosemary = ItemKey.FromDisplayName("rosemary");
        var water = ItemKey.FromDisplayName("water");
        var salt = ItemKey.FromDisplayName("salt");
        knowledge.Items[rosemary] = Entry(rosemary, 0, now.AddDays(-1), stale: true);
        knowledge.Items[water] = Entry(water, 40, now.AddMinutes(-5), stale: false);
        knowledge.Items[salt] = Entry(salt, 12, now.AddHours(-2), stale: false);
        knowledge.StaleSince = now.AddHours(-1);

        Assert.IsNull(JobKit.FreshCount(context, pepper), "no entry is unknown, not 0");
        Assert.IsNull(JobKit.FreshCount(context, rosemary), "a stale entry is unknown");
        Assert.IsNull(JobKit.FreshCount(context, salt), "a count from before the latest stale mark is unknown");
        Assert.AreEqual(40L, JobKit.FreshCount(context, water));
    }

    // R1.10: 4 rounds of the same exhausted gather finish in 12 min made the engine's 4th soft end a HALT (10-16 10:49)
    [TestMethod]
    public void TheSecondExhaustedDeferralInTheWindow_EndsTheGoal()
    {
        var t0 = TimeSpan.FromHours(1);

        Assert.IsFalse(JobKit.SoftEndsNearLimit([t0], t0 + TimeSpan.FromMinutes(3)), "one is retried");
        Assert.IsTrue(JobKit.SoftEndsNearLimit([t0, t0 + TimeSpan.FromMinutes(3)], t0 + TimeSpan.FromMinutes(3)), "the second ends the goal, before the engine's HALT");
        Assert.IsFalse(JobKit.SoftEndsNearLimit([t0, t0 + TimeSpan.FromMinutes(45)], t0 + TimeSpan.FromMinutes(45)), "outside the 30-min window it is a new one");
    }

    private static StockEntry Entry(ItemKey item, long count, DateTimeOffset at, bool stale) =>
        new(item, count, null, StockSource.Observed, at, null, 1, "test", stale);
}

/// <summary>R1.9: the gear notice message follows the outcome, and the box toggle is never clicked twice for one notice.</summary>
[TestClass]
public sealed class GearNoticeDismissTests
{
    private sealed class NoticeScreen(bool shown) : IScreenService
    {
        public bool Shown { get; set; } = shown;

        /// <summary>The box reads empty (the box-empty reader finds it).</summary>
        public bool BoxEmpty { get; set; } = true;

        public CaptureResult Capture(CancellationToken ct) => new(CaptureProblem.NotConnected, null, TimeSpan.Zero);

        public HudClassification ClassifyHudStable(CancellationToken ct) =>
            new(HudState.Unknown, 0, 0, false, false, "fake");

        public ScreenReadResult Read(string readerId, CancellationToken ct) =>
            new(readerId, Shown && (readerId != Gathering.GearBoxEmptyReader || BoxEmpty) ? ReadStatus.Ok : ReadStatus.ScreenMismatch, [], null, DateTimeOffset.UnixEpoch);
    }

    private static StepAttemptRecord Proceeded(string step) =>
        new(step, 1, StepOutcome.Ok, OutcomeClass.Ok, RecoveryAction.Proceed,
            null, true, null, null, DateTimeOffset.UnixEpoch);

    [TestInitialize]
    public void Reset() => Gathering.ResetGearNoticeState();

    [TestMethod]
    public void AStopAfterTheTick_SaysNotDismissed_AndTheNextRunPressesOnlyIgnore()
    {
        var (_, env, _) = T.LoadReal();
        var screen = new NoticeScreen(shown: true);
        var context = new FakeJobContext(env.Procedures) { ScreenOverride = screen };
        // the 2026-10-22 21:16 journal: 'box' was clicked, then the user paused and stopped before 'ignore'
        context.Runner.Script = p => T.Result(p.Id, ProcedureStatus.Stopped, attempts: [Proceeded("box")]);

        Gathering.DismissGearNotice(context, CancellationToken.None);

        Assert.IsFalse(context.NotifierFake.Sent.Any(n => n.Text.Contains("pressed Ignore", StringComparison.Ordinal)), "nothing claims Ignore was pressed");
        Assert.AreEqual("gear.notice.open", context.NotifierFake.Sent.Single().Code);

        context.Runner.Script = null; // the next run goes through
        Gathering.DismissGearNotice(context, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "ignore" }, context.Runner.Runs[^1].Steps.Select(s => s.Id).ToArray(), "the box is not clicked again (it would untick)");
        Assert.AreEqual("gear.notice", context.NotifierFake.Sent[^1].Code);
    }

    // incident 20 (2026-10-24): the notice came back 24 times in one trip; a box that does not read empty is never clicked
    [TestMethod]
    public void ABoxThatDoesNotReadEmpty_IsNeverClicked_OnlyIgnoreIsPressed()
    {
        var (_, env, _) = T.LoadReal();
        var context = new FakeJobContext(env.Procedures) { ScreenOverride = new NoticeScreen(shown: true) { BoxEmpty = false } };

        Gathering.DismissGearNotice(context, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "ignore" }, context.Runner.Runs.Single().Steps.Select(s => s.Id).ToArray());
        Assert.IsTrue(context.JournalFake.Records.Any(j => Equals(j.Fields.GetValueOrDefault("event"), "gear.notice") && Equals(j.Fields.GetValueOrDefault("boxEmpty"), false)));
    }

    // 2026-10-31 audit (10-28: 26 notices in 8 minutes; the box read ticked after 5 clicks and the notice still came back
    // about 13 s later): "Don't show again today" never held, so a notice is answered with Ignore only
    [TestMethod]
    public void ANotice_IsAnsweredWithIgnoreOnly_TheBoxIsNeverClicked()
    {
        var (_, env, _) = T.LoadReal();
        var screen = new NoticeScreen(shown: true);
        var context = new FakeJobContext(env.Procedures) { ScreenOverride = screen };

        Gathering.DismissGearNotice(context, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "ignore" }, context.Runner.Runs.Select(r => r.Steps.Single().Id).ToArray());
        Assert.AreEqual("gear.notice", context.NotifierFake.Sent.Single().Code);
    }

    [TestMethod]
    public void ANoticeThatKeepsComingBack_IsAStorm_AfterFourInFiveMinutes()
    {
        var (_, env, _) = T.LoadReal();
        var screen = new NoticeScreen(shown: true);
        var context = new FakeJobContext(env.Procedures) { ScreenOverride = screen };
        for (var i = 0; i < Gathering.GearStormCount - 1; i++)
        {
            Gathering.DismissGearNotice(context, CancellationToken.None);
            screen.Shown = true;
            context.ClockFake.Monotonic += TimeSpan.FromSeconds(13);
        }

        Assert.IsFalse(Gathering.GearNoticeStorm(context.Clock.Monotonic), "three answers are not a storm yet");
        Gathering.DismissGearNotice(context, CancellationToken.None);
        Assert.IsTrue(Gathering.GearNoticeStorm(context.Clock.Monotonic));
        context.ClockFake.Monotonic += Gathering.GearStormWindow + TimeSpan.FromSeconds(1);
        Assert.IsFalse(Gathering.GearNoticeStorm(context.Clock.Monotonic), "it passes once the notices stop");
    }
}
