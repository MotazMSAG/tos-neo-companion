using Companion.Contracts;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Planning;
using Companion.Features.Kit;

namespace Companion.Features.Tests;

/// <summary>Regression tests for the Release 2 decisions (INCIDENTS.md, R2.3, R2.5, R2.6, R2.9).</summary>
[TestClass]
public sealed class Release2DecisionTests
{
    private static readonly GatherTarget Juice = new(20330051, "golden juice", 75);

    private static GatherOutcome Outcome(string code) => new(null, [], [], code);

    // R2.5: only "not started" had a budget; a timeout or a failed finish repeated every round
    [TestMethod]
    public void AnyTripFailureTwiceInARow_EndsGathering_AndAnotherKindStartsOver()
    {
        var r = Gathering.NextFailure(null, 0, Outcome("gather.timeout"));
        Assert.AreEqual(("gather.timeout", 1), r);
        r = Gathering.NextFailure(r.Code, r.Count, Outcome("gather.timeout"));
        Assert.AreEqual(("gather.timeout", 2), r, "the same failure twice: gathering ends for the goal");

        r = Gathering.NextFailure("gather.timeout", 1, Outcome("gather.finishFailed"));
        Assert.AreEqual(("gather.finishFailed", 1), r, "another kind of failure starts its own run");
        Assert.AreEqual(((string?)null, 0), Gathering.NextFailure(r.Code, r.Count, Outcome("gather.notSeen")), "a walk that went through resets it");
    }

    // R2.3: "same plan, no progress, twice" needs a plan identity
    [TestMethod]
    public void ThePlanFingerprint_ChangesWithCraftsBatchesAndShortfalls_Only()
    {
        static CraftPlan Plan(int batches, long missing) => new(
            [new PlannedCraft(1, "cake|lifeskill|0", new ItemKey("scallion fish cake"), batches, batches, batches, [], new TimeRange(TimeSpan.Zero, TimeSpan.Zero))],
            missing > 0 ? [new Shortfall(new ItemKey("golden juice"), missing, ShortfallKind.RawMaterial)] : [],
            [], new TimeRange(TimeSpan.Zero, TimeSpan.Zero), true);

        Assert.AreEqual(JobKit.PlanFingerprint(Plan(15, 75)), JobKit.PlanFingerprint(Plan(15, 75)));
        Assert.AreNotEqual(JobKit.PlanFingerprint(Plan(15, 75)), JobKit.PlanFingerprint(Plan(15, 0)), "a trip that gathered changes the plan");
        Assert.AreNotEqual(JobKit.PlanFingerprint(Plan(15, 0)), JobKit.PlanFingerprint(Plan(14, 0)));
    }

    // R2.6: a greyed tile is never pressed, so only the same icon proves "at its limit"
    [TestMethod]
    public void OnlyAStrictPictureMatch_ProvesAGreyedTileIsAtItsLimit()
    {
        Assert.IsTrue(Gathering.FadedProvesLimit(StockIcons.SameIconMaxBits));
        Assert.IsFalse(Gathering.FadedProvesLimit(StockIcons.SameIconMaxBits + 1));
        Assert.IsFalse(Gathering.FadedProvesLimit(Gathering.SameKeyBits), "the loose walk bound is not proof");
    }

    // R2.9: a blocked rule was walked, read and planned every 10 minutes
    [TestMethod]
    public void ABlockedRuleBacksOff_10_20_40_ThenEvery60Minutes()
    {
        CollectionAssert.AreEqual(new[] { 10.0, 20.0, 40.0, 60.0, 60.0 }, Enumerable.Range(1, 5).Select(n => JobKit.RuleBackoff(n).TotalMinutes).ToArray());
    }

    // R2.4: what a trip filled is reported apart from "at its limit"
    [TestMethod]
    public void AFilledTarget_IsGatheredAndFilled_NotAtLimit()
    {
        var done = new GatherOutcome(null, [Juice], [], "gather.done") { Filled = [Juice] };

        Assert.IsNull(Gathering.TripFailure(done));
        Assert.AreEqual(0, done.AtLimit.Count, "filled by this trip is not evidence that the helper refuses it");
        CollectionAssert.AreEqual(Array.Empty<GatherTarget>(), Gathering.NextTargets([Juice], done), "gathered: not tried again by Gather everything");
    }
}

/// <summary>2026-10-23, the user: "it should go and refill my stocks": what a trip gathered without filling is topped up.</summary>
[TestClass]
public sealed class RefillDecisionTests
{
    private static readonly GatherTarget Thyme = new(20330050, "thyme", 200);
    private static readonly GatherTarget Toothclam = new(20330052, "toothclam", 121);
    private static readonly GatherTarget Essence = new(20330054, "wisdom essence", 60);

    [TestMethod]
    public void WhatATripGatheredWithoutFillingIt_IsToppedUp_AndFullOnesAreNot()
    {
        // live 2026-10-23: Thyme 2/500 took 200 (not full), Toothclam filled its room, Wisdom Essence's room was unknown
        var done = new GatherOutcome(null, [Thyme, Toothclam, Essence], [], "gather.done") { Filled = [Toothclam] };

        CollectionAssert.AreEqual(new[] { Thyme, Essence }, Gathering.NotFilled(done), "unknown room counts as not filled: the next trip shows it");
    }

    [TestMethod]
    public void AFailedTrip_TopsUpNothing()
    {
        var failed = new GatherOutcome(JobKit.Failed("gather.deferred:commit.userActive"), [], [], "gather.deferred");

        Assert.AreEqual(0, Gathering.NotFilled(failed).Count);
    }
}

/// <summary>2026-10-23, the user: "does it top up lowest dry resources first? if not, make it do that".</summary>
[TestClass]
public sealed class DriestFirstTests
{
    private static GatherTarget T(long id, string name) => new(id, name, 9_999);

    [TestMethod]
    public void TheEmptiestMaterialsGoFirst_UnknownGoesLast_TiesByFewerTrips()
    {
        var thyme = T(1, "thyme");      // 2/500
        var water = T(2, "water");      // 160/999
        var toothclam = T(3, "toothclam"); // 479/500
        var essence = T(4, "wisdom essence"); // no stock line: unknown
        var grape = T(5, "scarlet grape");   // 30/300, same fill as below after a trip
        var juice = T(6, "golden juice");    // 30/300
        var levels = new Dictionary<long, StockLevel>
        {
            [1] = new(2, 500), [2] = new(160, 999), [3] = new(479, 500), [4] = new(null, null), [5] = new(30, 300), [6] = new(30, 300),
        };
        var trips = new Dictionary<long, int> { [5] = 1 };

        var pick = Gathering.Driest([toothclam, essence, water, grape, juice, thyme], levels, trips, 4);

        CollectionAssert.AreEqual(new[] { thyme, juice, grape, water }, pick, "0.4%, 10% (no trip yet), 10% (one trip), 16%; 96% and unknown wait");
    }

    // incident 21 (2026-10-24): unknown counted as half full, so 4 materials the walk could not see went before water at 539/999
    [TestMethod]
    public void AnUnknownFill_GoesAfterAnAlmostFullOne()
    {
        var water = T(1, "water");
        var grape = T(2, "scarlet grape"); // not seen: no level at all
        var essence = T(3, "wisdom essence"); // seen, no stock line
        var levels = new Dictionary<long, StockLevel> { [1] = new(980, 999), [3] = new(null, null) };

        var pick = Gathering.Driest([grape, essence, water], levels, new Dictionary<long, int>(), 4);

        CollectionAssert.AreEqual(new[] { water, grape, essence }, pick);
    }

    [TestMethod]
    public void APickACompleteWalkDidNotMeet_IsLeftAtOnce_AfterAnIncompleteWalkOnTheSecondMiss()
    {
        var water = T(1, "water");
        var grape = T(2, "scarlet grape");
        var misses = new Dictionary<long, int>();
        var complete = new GatherOutcome(null, [water], [], "gather.notSeen");

        CollectionAssert.AreEqual(new[] { grape }, Gathering.Unshown([water, grape], complete, misses), "water was gathered");

        var juice = T(3, "golden juice");
        var incomplete = new GatherOutcome(null, [], [], "gather.notSeen") { WalkIncomplete = true };
        Assert.AreEqual(0, Gathering.Unshown([juice], incomplete, misses).Count, "one more try");
        CollectionAssert.AreEqual(new[] { juice }, Gathering.Unshown([juice], incomplete, misses));
    }

    [TestMethod]
    public void OnlyACompleteSurveyWalk_LeavesWhatItDidNotMeet()
    {
        var grape = T(2, "scarlet grape");
        var surveyed = new GatherOutcome(null, [], [], "gather.surveyed") { NotSeen = [grape] };

        CollectionAssert.AreEqual(new[] { grape }, Gathering.Unshown(surveyed).ToArray());
        Assert.AreEqual(0, Gathering.Unshown(surveyed with { WalkIncomplete = true }).Count);
        Assert.AreEqual(0, Gathering.Unshown(surveyed with { Code = "gather.queueFailed" }).Count);
    }

    [TestMethod]
    public void AStockLevel_KnowsItsFill_OnlyWithBothNumbers()
    {
        Assert.AreEqual(0.5, new StockLevel(150, 300).Fill);
        Assert.IsNull(new StockLevel(150, null).Fill);
        Assert.IsNull(new StockLevel(null, 300).Fill);
        Assert.AreEqual(1.0, new StockLevel(320, 300).Fill, "never above full");
    }
}

/// <summary>Roadmap B ("keep food at N"): rule mode re-checks soon after the character auto-eats.</summary>
[TestClass]
public sealed class FoodRecheckTests
{
    [TestMethod]
    public void AnAutoEat_EndsRuleModesWait_AfterAShortSettle()
    {
        var (host, env, _) = T.LoadReal();
        var ctx = new FakeJobContext(env.Procedures);
        ctx.SwitchesFake.Crafting = true;
        // a rule whose recipe is unknown: the pass makes nothing and rule mode waits (10 min)
        ctx.KnowledgeFake!.Rules.Add(new Companion.Contracts.Knowledge.KeepStockRule(new ItemKey("salad savory"), 120));
        var start = ctx.ClockFake.Monotonic;
        var ateAt = new DateTime(2026, 10, 23, 20, 0, 0);
        using var cts = new CancellationTokenSource();
        ctx.UiProvider = () =>
        {
            var waits = ctx.JournalFake.Records.Count(j => Equals(j.Fields.GetValueOrDefault("event"), "keepStock.waiting"));
            if (waits >= 2)
            {
                cts.Cancel(); // the second pass started: enough
            }

            return ctx.Clock.Monotonic - start >= TimeSpan.FromMinutes(2) ? ctx.Ui with { LastAutoEat = ateAt } : ctx.Ui;
        };

        JobResult? ended = null;
        try
        {
            ended = T.Handler(host, "KeepStockLoop").Execute(ctx, new JobParameters(new Dictionary<string, object>()), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // the test stopped rule mode after its second pass
        }

        Assert.IsTrue(ended is null || ended.State == JobState.Cancelled, "rule mode runs until stopped: " + ended?.Code);

        Assert.IsTrue(ctx.JournalFake.Records.Any(j => Equals(j.Fields.GetValueOrDefault("event"), "keepStock.foodEaten")));
        Assert.IsTrue(ctx.ClockFake.Monotonic - start < TimeSpan.FromMinutes(5), $"re-checked after {(ctx.ClockFake.Monotonic - start).TotalMinutes} min, not 10");
    }
}

/// <summary>Release 3 (audit R3.2 #32): an engine-decided cancel keeps its reason in the job's code.</summary>
[TestClass]
public sealed class CancelDetailTests
{
    [TestMethod]
    public void AnEngineCancel_KeepsItsDetail_AndAPlainCancelStaysPlain()
    {
        var expired = T.Result("crafting.loop", Companion.Contracts.Engine.ProcedureStatus.Cancelled) with { EndDetail = "park.expired" };
        var plain = T.Result("crafting.loop", Companion.Contracts.Engine.ProcedureStatus.Cancelled);

        Assert.AreEqual("round.cancelled:park.expired", JobKit.FromProcedure(expired, "round.").Code);
        Assert.AreEqual("round.cancelled", JobKit.FromProcedure(plain, "round.").Code);
    }
}

/// <summary>Safety review 2026-10-23: Gather everything checks the crafting switch before any input.</summary>
[TestClass]
public sealed class GatherAllSwitchTests
{
    [TestMethod]
    public void WithTheCraftingSwitchOff_GatherEverythingRefuses_BeforeAnyInput()
    {
        var (host, env, _) = T.LoadReal();
        var ctx = new FakeJobContext(env.Procedures);
        ctx.SwitchesFake.Crafting = false;
        ctx.KnowledgeFake!.Recipes.Add(new Recipe("cake|lifeskill|0", new ItemKey("cake"), 1, "lifeskill",
            [new MaterialRequirement(new ItemKey("thyme"), 6, 20330050)], [], null, DateTimeOffset.UnixEpoch, "screen:craft.fav/1", false, RecipeConfidence.Complete, null, null));

        var r = T.Handler(host, "GatherAll").Execute(ctx, new JobParameters(new Dictionary<string, object>()), CancellationToken.None);

        Assert.AreEqual(JobState.Refused, r.State, r.Code);
        Assert.AreEqual(0, ctx.Runner.Runs.Count, "nothing pressed");
    }
}
