using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.GameIo;
using Companion.Contracts.Vision;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Perception;
using Companion.Contracts.Safety;
using Companion.Features.Kit;

namespace Companion.Features.Tests;

/// <summary>
/// Incident 22 (2026-10-24): Gather everything with the game minimized. A failed capture was taken as "the helper is
/// open", the survey's first drag waited 3.5 hours and found no helper; two trips deferred while the user played ended
/// the run with nothing gathered.
/// </summary>
[TestClass]
public sealed class Incident22Tests
{
    private static ScreenReadResult R(ReadStatus status, int values = 0, bool anchors = false) =>
        new("r", status, [.. Enumerable.Range(0, values).Select(i => new ReadValue("v" + i, status, null, null, null, null, 0, "test"))], null, DateTimeOffset.UnixEpoch,
            anchors ? new Dictionary<string, PointI> { ["a"] = new(1, 1) } : null);

    [TestMethod]
    public void OnlyARealCapture_IsSeen_AFailedOneIsNeitherSeenNorProofOfAbsence()
    {
        Assert.IsTrue(JobKit.Seen(R(ReadStatus.Ok)), "an identity probe with no regions");
        Assert.IsTrue(JobKit.Seen(R(ReadStatus.Unknown, values: 2)), "shown, a value unclear");
        Assert.IsTrue(JobKit.Seen(R(ReadStatus.Unknown, anchors: true)));
        Assert.IsFalse(JobKit.Seen(R(ReadStatus.Unknown)), "the capture failed (game minimized)");
        Assert.IsFalse(JobKit.Seen(R(ReadStatus.ScreenMismatch, values: 1)));
        Assert.IsFalse(JobKit.Seen(R(ReadStatus.NeedsSurvey)));
    }

    /// <summary>The core procedure the gather walk calls (not in the feature data).</summary>
    internal static void PutOpenAbility(TestEnv env) =>
        env.Procedures.Put(new ProcedureDefinition("OpenAbility", "core", [new StepDefinition("open", StatePredicate.Any, new ClickAction("menu.ability"), Expectation.Nothing)], ProcedureLimits.Default));

    /// <summary>Every read fails as with a minimized game window: Unknown with nothing in it.</summary>
    private sealed class MinimizedScreen : IScreenService
    {
        public CaptureResult Capture(CancellationToken ct) => new(CaptureProblem.WrongSize, null, TimeSpan.Zero);

        public HudClassification ClassifyHudStable(CancellationToken ct) => new(HudState.Unknown, 0, 0, false, false, "fake");

        public ScreenReadResult Read(string readerId, CancellationToken ct) => new(readerId, ReadStatus.Unknown, [], null, DateTimeOffset.UnixEpoch);
    }

    [TestMethod]
    public void WithTheGameMinimized_TheSurveyOpensTheHelperFirst_AndNeverDragsAListItCannotSee()
    {
        var (_, env, _) = T.LoadReal();
        PutOpenAbility(env);
        var context = new FakeJobContext(env.Procedures) { ScreenOverride = new MinimizedScreen() };
        context.SwitchesFake.Crafting = true;

        var outcome = Gathering.Survey(context, [new GatherTarget(20330023, "water", 9_999)], CancellationToken.None);

        Assert.IsTrue(context.Runner.Runs.Count > 0, outcome.Code + " " + outcome.Stop?.Code);
        Assert.AreEqual("open", context.Runner.Runs[0].Steps[0].Id, "the open steps come first (the engine waits there for the window)");
        Assert.IsFalse(context.Runner.Runs.Any(r => r.Steps.Any(s => s.Id is "top" or "scroll" or "tile")), "no drag or press on a list nobody saw");
        Assert.AreEqual("gather.notOpened", outcome.Code);
    }

    [TestMethod]
    public void NearTheSoftEndAllowance_ARetryWaitsUntilAllButTheNewestAreOutOfTheWindow()
    {
        var now = TimeSpan.FromHours(5);
        var minute = TimeSpan.FromMinutes(1);
        Assert.AreEqual(minute, JobKit.ClearOfSoftEnds(minute, [], now));
        Assert.AreEqual(minute, JobKit.ClearOfSoftEnds(minute, [now - minute], now), "one exhausted end: the plain pause");

        // the 2026-10-24 run: the survey and trip 1 both exhausted their recovery, 2 minutes apart
        var pause = JobKit.ClearOfSoftEnds(minute, [now - TimeSpan.FromMinutes(2), now], now);
        Assert.AreEqual(TimeSpan.FromMinutes(28) + TimeSpan.FromSeconds(10), pause, "the retry comes when only the newest is inside the 30 minutes");

        Assert.AreEqual(minute, JobKit.ClearOfSoftEnds(minute, [now - TimeSpan.FromMinutes(40), now], now), "an exhausted end outside the window does not count");
        Assert.AreEqual(TimeSpan.FromMinutes(40), JobKit.ClearOfSoftEnds(TimeSpan.FromMinutes(40), [now - TimeSpan.FromMinutes(2), now], now), "a longer pause stays");
    }

    [TestMethod]
    public void DeferredTrips_ArePausedAndTriedAgain_AndNoRetryCanBeTheEnginesFourthSoftEnd()
    {
        var (host, env, _) = T.LoadReal();
        PutOpenAbility(env);
        var ctx = new FakeJobContext(env.Procedures) { ScreenOverride = new MinimizedScreen() };
        ctx.SwitchesFake.Crafting = true;
        ctx.KnowledgeFake!.Recipes.Add(new Recipe("cake|lifeskill|0", new ItemKey("cake"), 1, "lifeskill",
            [new MaterialRequirement(new ItemKey("thyme"), 6, 20330050)], [], null, DateTimeOffset.UnixEpoch, "screen:craft.fav/1", false, RecipeConfidence.Complete, null, null));
        // every run ends as on 2026-10-24: the engine's recovery ran out (one soft end each)
        var softEnds = new List<TimeSpan>();
        ctx.Runner.Script = p =>
        {
            softEnds.Add(ctx.Clock.Monotonic);
            return new ProcedureResult(p.Id, ProcedureStatus.Deferred, StepOutcome.Deferred, null, 0, 0, 0, [], new Dictionary<string, ScreenReadResult>(), [], [], "exhausted.PreconditionLostExhausted");
        };

        var r = T.Handler(host, "GatherAll").Execute(ctx, new JobParameters(new Dictionary<string, object>()), CancellationToken.None);

        Assert.AreEqual("gatherAll.incomplete", r.Code);
        var pauses = ctx.JournalFake.Records.Count(j => Equals(j.Fields.GetValueOrDefault("event"), "gatherAll.deferred"));
        Assert.IsTrue(pauses >= 3, "two deferred runs no longer end it (" + pauses + " pauses)");
        Assert.AreEqual(1, ctx.JournalFake.Records.Count(j => Equals(j.Fields.GetValueOrDefault("event"), "gatherAll.deferralsOver")), "and it still ends");
        Assert.IsTrue(softEnds.Count <= 12, "bounded: " + softEnds.Count);
        foreach (var t in softEnds)
        {
            var inWindow = softEnds.Count(s => s <= t && t - s <= HardRules.SoftEndWindow);
            Assert.IsTrue(inWindow <= 2, "at most 2 soft ends in any 30 minutes (the engine HALTs on the 4th): " + inWindow + " at " + t);
        }

        Assert.IsNotNull(r.Data?["deferred"]);
    }
}

/// <summary>
/// Incident 23 (2026-10-24): two Sauteed Clams goals ended "partly done" at 12: each Wisdom Essence trip (60, about 20 s
/// each) outlived its 13-minute limit while the character was still gathering, and two "timeouts" stopped gathering.
/// </summary>
[TestClass]
public sealed class Incident23Tests
{
    [TestInitialize]
    public void Reset() => Gathering.ResetGearNoticeState();

    [TestMethod]
    public void ATripOfSixtyWisdomEssence_FitsItsLimit()
    {
        Assert.IsTrue(Gathering.WaitLimit(60) >= TimeSpan.FromMinutes(19) + TimeSpan.FromSeconds(37), Gathering.WaitLimit(60).ToString());
    }

    [TestMethod]
    public void ATripStillOut_IsNeitherAFailureNorAReset()
    {
        var still = new GatherOutcome(null, [], [], Gathering.StillGathering);
        var timeout = new GatherOutcome(null, [], [], "gather.timeout");

        var (code, count) = Gathering.NextFailure(null, 0, still);
        (code, count) = Gathering.NextFailure(code, count, still);
        Assert.AreEqual(0, count, "two trips still out do not stop gathering");

        (code, count) = Gathering.NextFailure(code, count, timeout);
        (code, count) = Gathering.NextFailure(code, count, still);
        Assert.AreEqual(("gather.timeout", 1), (code, count), "and do not reset a real failure either");
    }

    [TestMethod]
    public void AWalkThatFindsATripStillOut_WaitsForIt_AndSaysItIsStillOut()
    {
        var (_, env, _) = T.LoadReal();
        Incident22Tests.PutOpenAbility(env);
        var context = new FakeJobContext(env.Procedures) { Ui = T.Snapshot(1000) with { AutoCollectStatus = 1 } };
        context.SwitchesFake.Crafting = true;
        var start = context.Clock.Monotonic;

        var outcome = Gathering.Run(context, [new GatherTarget(20330054, "wisdom essence", 60)], CancellationToken.None);

        Assert.AreEqual(Gathering.StillGathering, outcome.Code);
        Assert.IsTrue(context.Clock.Monotonic - start >= Gathering.WaitLimit(0), "it waited");
        Assert.AreEqual(0, context.Runner.Runs.Count, "nothing pressed while the character gathers");
    }
}

/// <summary>
/// Incident 24 (2026-10-25): a Seafood Potsticker goal gathered 200 thyme in 15 minutes, then the user's mouse rested on
/// the other monitor and Finish and Make were out of reach; the 15-minute trip counted as "no progress" and the goal
/// gave up after 11 minutes of waiting.
/// </summary>
[TestClass]
public sealed class Incident24Tests
{
    [TestInitialize]
    public void Reset() => Gathering.ResetGearNoticeState();

    /// <summary>The helper's list is on screen; nothing else is.</summary>
    private sealed class HelperScreen : IScreenService
    {
        public CaptureResult Capture(CancellationToken ct) => new(CaptureProblem.WrongSize, null, TimeSpan.Zero);

        public HudClassification ClassifyHudStable(CancellationToken ct) => new(HudState.Unknown, 0, 0, false, false, "fake");

        public ScreenReadResult Read(string readerId, CancellationToken ct) =>
            new(readerId, readerId == Gathering.GridReader ? ReadStatus.Ok : ReadStatus.ScreenMismatch, [], null, DateTimeOffset.UnixEpoch);
    }

    [TestMethod]
    public void ATripThatCompleted_IsReported_EvenWhenItsFinishWasOutOfReach()
    {
        var (_, env, _) = T.LoadReal();
        Incident22Tests.PutOpenAbility(env);
        var context = new FakeJobContext(env.Procedures) { ScreenOverride = new HelperScreen() };
        context.SwitchesFake.Crafting = true;
        var done = context.Clock.Monotonic + TimeSpan.FromMinutes(5);
        context.UiProvider = () => T.Snapshot(1000) with { AutoCollectStatus = context.Clock.Monotonic < done ? 1 : 2 };
        context.Runner.Script = p => p.Steps.Any(s => s.Id == "finish")
            ? new ProcedureResult(p.Id, ProcedureStatus.Deferred, StepOutcome.Deferred, null, 0, 0, 0, [], new Dictionary<string, ScreenReadResult>(), [], [], "unreachable.CrossMonitor")
            : T.Result(p.Id, ProcedureStatus.Succeeded);

        var outcome = Gathering.Run(context, [new GatherTarget(20330050, "thyme", 200)], CancellationToken.None);

        Assert.AreEqual("gather.deferred", outcome.Code, outcome.StepCode);
        Assert.IsTrue(outcome.TripCompleted, "the character gathered: progress for the goal's limit");
    }

    [TestMethod]
    public void AWalkThatWaitedForNothing_ReportsNoCompletedTrip()
    {
        var (_, env, _) = T.LoadReal();
        Incident22Tests.PutOpenAbility(env);
        var context = new FakeJobContext(env.Procedures) { ScreenOverride = new HelperScreen(), Ui = T.Snapshot(1000) with { AutoCollectStatus = 2 } };
        context.SwitchesFake.Crafting = true;
        context.Runner.Script = p => new ProcedureResult(p.Id, ProcedureStatus.Deferred, StepOutcome.Deferred, null, 0, 0, 0, [], new Dictionary<string, ScreenReadResult>(), [], [], "unreachable.CrossMonitor");

        var outcome = Gathering.Run(context, [new GatherTarget(20330050, "thyme", 200)], CancellationToken.None);

        Assert.IsFalse(outcome.TripCompleted, "a queue found complete, its Finish out of reach again: no new progress (no endless reset of the limit)");
    }

    [TestMethod]
    public void AnOutOfReachPause_IsToldOnce_OtherPausesAreNot()
    {
        var context = new FakeJobContext(new FakeProcedureBuilder());

        Assert.IsFalse(JobKit.TellOutOfReach(context, "round.deferred:commit.userActive", false));
        Assert.IsTrue(JobKit.TellOutOfReach(context, "round.deferred:unreachable.CrossMonitor", false));
        Assert.IsFalse(JobKit.TellOutOfReach(context, "gather.deferred:unreachable.CrossMonitor", true), "once per job");

        Assert.AreEqual("job.outOfReach", context.NotifierFake.Sent.Single().Code);
    }
}

/// <summary>
/// Incident 27 (2026-10-27): Basil Crushed Tea x138 made 121, gathered everything for the last 17, then every press was
/// refused for 40 minutes ("CoverRejected": the window under the resting mouse was not a valid cover); the goal gave up
/// after 8 pauses and the notice spoke of another monitor.
/// </summary>
[TestClass]
public sealed class Incident27Tests
{
    [TestMethod]
    public void TheNotice_NamesTheRightPlace_ForTheMouse()
    {
        var context = new FakeJobContext(new FakeProcedureBuilder());
        Assert.IsTrue(JobKit.TellOutOfReach(context, "round.deferred:unreachable.CoverRejected", false));
        Assert.AreEqual(JobKit.OutOfCoverText, context.NotifierFake.Sent.Single().Text);

        var other = new FakeJobContext(new FakeProcedureBuilder());
        JobKit.TellOutOfReach(other, "gather.deferred:unreachable.CrossMonitor", false);
        Assert.AreEqual(JobKit.OutOfReachText, other.NotifierFake.Sent.Single().Text);
        Assert.IsFalse(JobKit.OutOfReach("makeTab.deferred:exhausted.PreconditionLostExhausted"));
    }

    [TestMethod]
    public void GatherEverything_WaitsUpToThreeHours_ForTheMouse_NotEightPauses()
    {
        var (host, env, _) = T.LoadReal();
        Incident22Tests.PutOpenAbility(env);
        var ctx = new FakeJobContext(env.Procedures) { ScreenOverride = new MinimizedScreenForReach() };
        ctx.SwitchesFake.Crafting = true;
        ctx.KnowledgeFake!.Recipes.Add(new Recipe("cake|lifeskill|0", new ItemKey("cake"), 1, "lifeskill",
            [new MaterialRequirement(new ItemKey("thyme"), 6, 20330050)], [], null, DateTimeOffset.UnixEpoch, "screen:craft.fav/1", false, RecipeConfidence.Complete, null, null));
        ctx.Runner.Script = p => new ProcedureResult(p.Id, ProcedureStatus.Deferred, StepOutcome.Deferred, null, 0, 0, 0, [], new Dictionary<string, ScreenReadResult>(), [], [], "unreachable.CoverRejected");
        var start = ctx.Clock.Monotonic;

        var r = T.Handler(host, "GatherAll").Execute(ctx, new JobParameters(new Dictionary<string, object>()), CancellationToken.None);

        var pauses = ctx.JournalFake.Records.Count(j => Equals(j.Fields.GetValueOrDefault("event"), "gatherAll.deferred"));
        Assert.IsTrue(pauses > GatherAllMaxDeferrals, "more than the 8 pauses of other causes: " + pauses);
        Assert.IsTrue(ctx.Clock.Monotonic - start >= JobKit.OutOfReachWindow, "it waited the 3 hours");
        Assert.IsTrue(pauses <= 40, "and still ends: " + pauses);
        Assert.AreEqual("gatherAll.incomplete", r.Code);
        Assert.AreEqual(JobKit.OutOfCoverText, ctx.NotifierFake.Sent.Single(n => n.Code == "job.outOfReach").Text);
    }

    /// <summary>GatherAllJobHandler.MaxDeferrals (the feature's internals are not visible here).</summary>
    private const int GatherAllMaxDeferrals = 8;

    private sealed class MinimizedScreenForReach : IScreenService
    {
        public CaptureResult Capture(CancellationToken ct) => new(CaptureProblem.WrongSize, null, TimeSpan.Zero);

        public HudClassification ClassifyHudStable(CancellationToken ct) => new(HudState.Unknown, 0, 0, false, false, "fake");

        public ScreenReadResult Read(string readerId, CancellationToken ct) => new(readerId, ReadStatus.Unknown, [], null, DateTimeOffset.UnixEpoch);
    }
}
