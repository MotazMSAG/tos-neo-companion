using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Planning;
using Companion.Knowledge.Planning;
using static Companion.Knowledge.Tests.Fx;

namespace Companion.Knowledge.Tests;

[TestClass]
public sealed class PlannerTests
{
    private readonly Planner _planner = new();

    private static Dictionary<ItemKey, StockEntry> Stock(params (string Item, long Count)[] entries) =>
        entries.ToDictionary(e => I(e.Item), e => S(e.Item, e.Count));

    private static PlanRequest Request(
        IEnumerable<CraftGoal> goals,
        IEnumerable<Recipe> recipes,
        IReadOnlyDictionary<ItemKey, StockEntry> stock,
        PlannerPolicy? policy = null,
        IEnumerable<KeepStockRule>? keep = null,
        DateTimeOffset? staleSince = null) =>
        new([.. goals], [.. keep ?? []], [.. recipes], stock, policy ?? PlannerPolicy.Default, staleSince);

    private static CraftGoal Goal(string item, int qty) => new(I(item), qty);

    private static string Shape(CraftPlan plan) => string.Join(" | ", plan.Crafts.Select(c => $"{c.Order}:{c.RecipeId}x{c.Batches}"));

    [TestMethod]
    public void SingleRecipe()
    {
        var plan = _planner.Plan(Request(
            [Goal("fake ingot", 5)],
            [R("fake-ingot", "fake ingot", 1, ("fake ore", 2))],
            Stock(("fake ore", 20))));
        Assert.IsTrue(plan.Committable);
        Assert.AreEqual("1:fake-ingotx5", Shape(plan));
        var c = plan.Crafts.Single();
        Assert.AreEqual(I("fake ingot"), c.Product);
        Assert.AreEqual(5L, c.ExpectedOutputMin);
        Assert.AreEqual(5L, c.ExpectedOutputMax);
        CollectionAssert.AreEqual(new[] { new MaterialRequirement(I("fake ore"), 10) }, c.Consumes.ToArray());
        Assert.IsEmpty(plan.Shortfalls);
        Assert.IsEmpty(plan.Issues);
    }

    [TestMethod]
    public void ThreeLevelRecursionOrdersDependenciesFirst()
    {
        var plan = _planner.Plan(Request(
            [Goal("fake sword", 2)],
            [
                R("fake-sword", "fake sword", 1, ("fake blade", 1), ("fake grip", 1)),
                R("fake-blade", "fake blade", 1, ("fake ingot", 2)),
                R("fake-ingot", "fake ingot", 1, ("fake ore", 3)),
            ],
            Stock(("fake ore", 100), ("fake grip", 5), ("fake blade", 0), ("fake ingot", 0))));
        Assert.IsTrue(plan.Committable, string.Join(",", plan.Issues.Select(i => i.DetailCode)));
        Assert.AreEqual("1:fake-ingotx4 | 2:fake-bladex2 | 3:fake-swordx2", Shape(plan));
        Assert.AreEqual(12, plan.Crafts[0].Consumes.Single().Qty);
    }

    [TestMethod]
    public void SharedIntermediateIsAggregated()
    {
        // Each fake glue craft yields 5; two products need 2 + 3 = 5 in total: ONE batch, made before both.
        var plan = _planner.Plan(Request(
            [Goal("fake chair", 1), Goal("fake table", 1)],
            [
                R("fake-chair", "fake chair", 1, ("fake glue", 2), ("fake plank", 1)),
                R("fake-table", "fake table", 1, ("fake glue", 3), ("fake plank", 2)),
                R("fake-glue", "fake glue", 5, ("fake resin", 1)),
            ],
            Stock(("fake glue", 0), ("fake plank", 10), ("fake resin", 4))));
        Assert.IsTrue(plan.Committable);
        Assert.AreEqual("1:fake-gluex1 | 2:fake-chairx1 | 3:fake-tablex1", Shape(plan));
    }

    [TestMethod]
    public void DiamondSharedIntermediateIsMadeOnceBeforeBothParents()
    {
        var plan = _planner.Plan(Request(
            [Goal("fake top", 1)],
            [
                R("fake-top", "fake top", 1, ("fake left", 1), ("fake right", 1)),
                R("fake-left", "fake left", 1, ("fake core", 2)),
                R("fake-right", "fake right", 1, ("fake core", 3)),
                R("fake-core", "fake core", 5, ("fake raw", 1)),
            ],
            Stock(("fake left", 0), ("fake right", 0), ("fake core", 0), ("fake raw", 1))));
        Assert.IsTrue(plan.Committable);
        Assert.AreEqual("1:fake-corex1 | 2:fake-leftx1 | 3:fake-rightx1 | 4:fake-topx1", Shape(plan));
        Assert.AreEqual(5L, plan.Crafts[0].ExpectedOutputMin);
    }

    [TestMethod]
    public void SharedIntermediateUsesStockFirst()
    {
        var plan = _planner.Plan(Request(
            [Goal("fake chair", 2), Goal("fake table", 1)],
            [
                R("fake-chair", "fake chair", 1, ("fake glue", 2)),
                R("fake-table", "fake table", 1, ("fake glue", 3)),
                R("fake-glue", "fake glue", 2, ("fake resin", 1)),
            ],
            Stock(("fake glue", 3), ("fake resin", 10))));
        // need 4 + 3 = 7 glue, 3 in stock, 4 more = 2 batches of 2.
        Assert.AreEqual(2, plan.Crafts.Where(c => c.RecipeId == "fake-glue").Sum(c => c.Batches));
        Assert.AreEqual("fake-glue", plan.Crafts[0].RecipeId);
        Assert.IsTrue(plan.Committable);
    }

    [TestMethod]
    public void CycleIsAvoidedThroughAnAlternateRecipe()
    {
        // fake-b-loop makes B from A while A is made from B: a cycle. fake-b-raw makes B from raw C.
        var recipes = new[]
        {
            R("fake-a", "fake a", 1, ("fake b", 1)),
            R("fake-b-loop", "fake b", 3, ("fake a", 1)),
            R("fake-b-raw", "fake b", 1, ("fake c", 2)),
        };
        var plan = _planner.Plan(Request([Goal("fake a", 2)], recipes, Stock(("fake a", 0), ("fake b", 0), ("fake c", 10))));
        Assert.IsTrue(plan.Committable, string.Join(",", plan.Issues.Select(i => $"{i.Kind}:{i.DetailCode}")));
        Assert.AreEqual("1:fake-b-rawx2 | 2:fake-ax2", Shape(plan));
        Assert.IsFalse(plan.Issues.Any(i => i.Kind == PlanIssueKind.Cycle));
    }

    [TestMethod]
    public void CycleWithoutAlternateIsReported()
    {
        var recipes = new[]
        {
            R("fake-a", "fake a", 1, ("fake b", 1)),
            R("fake-b", "fake b", 1, ("fake a", 1)),
        };
        var plan = _planner.Plan(Request([Goal("fake a", 1)], recipes, Stock(("fake a", 0), ("fake b", 0))));
        Assert.IsFalse(plan.Committable);
        Assert.IsTrue(plan.Issues.Any(i => i.Kind == PlanIssueKind.Cycle && i.Item == I("fake a")));
    }

    [TestMethod]
    public void CycleIsHarmlessWhenStockCoversTheInnerNeed()
    {
        var recipes = new[]
        {
            R("fake-a", "fake a", 1, ("fake b", 1)),
            R("fake-b", "fake b", 1, ("fake a", 1)),
        };
        var plan = _planner.Plan(Request([Goal("fake a", 1)], recipes, Stock(("fake a", 0), ("fake b", 1))));
        Assert.IsTrue(plan.Committable);
        Assert.AreEqual("1:fake-ax1", Shape(plan));
    }

    [TestMethod]
    public void KeepStockReserveIsNotAvailableToGoals()
    {
        var recipes = new[] { R("fake-ingot", "fake ingot", 1, ("fake ore", 6)) };
        var keep = new[] { new KeepStockRule(I("fake ore"), 6) };
        var plan = _planner.Plan(Request([Goal("fake ingot", 1)], recipes, Stock(("fake ore", 10)), keep: keep));
        Assert.IsFalse(plan.Committable);
        var s = plan.Shortfalls.Single();
        Assert.AreEqual(I("fake ore"), s.Item);
        Assert.AreEqual(2L, s.Missing);
        Assert.AreEqual(ShortfallKind.RawMaterial, s.Kind);

        var ok = _planner.Plan(Request([Goal("fake ingot", 1)], recipes, Stock(("fake ore", 12)), keep: keep));
        Assert.IsTrue(ok.Committable);
    }

    [TestMethod]
    public void UnknownStockIsAShortfallAndNotCommittable()
    {
        var recipes = new[] { R("fake-ingot", "fake ingot", 1, ("fake ore", 2), ("fake coal", 1)) };
        var plan = _planner.Plan(Request([Goal("fake ingot", 3)], recipes, Stock(("fake ore", 50))));
        Assert.IsFalse(plan.Committable);
        var s = plan.Shortfalls.Single();
        Assert.AreEqual(I("fake coal"), s.Item);
        Assert.AreEqual(ShortfallKind.UnknownStock, s.Kind);
        Assert.AreEqual(3L, s.Missing);
        Assert.IsTrue(plan.Issues.Any(i => i.Kind == PlanIssueKind.UnknownStock && i.Item == I("fake coal")));
    }

    [TestMethod]
    public void UnknownIntermediateIsPlannedButBlocks()
    {
        var recipes = new[]
        {
            R("fake-sword", "fake sword", 1, ("fake ingot", 1)),
            R("fake-ingot", "fake ingot", 1, ("fake ore", 1)),
        };
        var plan = _planner.Plan(Request([Goal("fake sword", 1)], recipes, Stock(("fake ore", 5))));
        Assert.AreEqual("1:fake-ingotx1 | 2:fake-swordx1", Shape(plan));
        Assert.IsFalse(plan.Committable);
        Assert.IsTrue(plan.Issues.Any(i => i.Kind == PlanIssueKind.UnknownStock && i.Item == I("fake ingot")));
        Assert.IsEmpty(plan.Shortfalls);
    }

    [TestMethod]
    public void RangeStockUsesItsLowerBound()
    {
        var stock = new Dictionary<ItemKey, StockEntry>
        {
            [I("fake ore")] = S("fake ore", 0) with { Count = null, Range = new ValueRange(1000, 1099) },
        };
        var recipes = new[] { R("fake-ingot", "fake ingot", 1, ("fake ore", 1)) };
        Assert.IsTrue(_planner.Plan(Request([Goal("fake ingot", 1000)], recipes, stock, PlannerPolicy.Default with { MaxBatchPerCraft = 1000 })).Committable);
        var short1 = _planner.Plan(Request([Goal("fake ingot", 1001)], recipes, stock, PlannerPolicy.Default with { MaxBatchPerCraft = 2000 }));
        Assert.AreEqual(1L, short1.Shortfalls.Single().Missing);
    }

    [TestMethod]
    public void StaleStockIsNotCommittable()
    {
        var recipes = new[] { R("fake-ingot", "fake ingot", 1, ("fake ore", 2)) };
        var staleEntry = new Dictionary<ItemKey, StockEntry> { [I("fake ore")] = S("fake ore", 50, stale: true) };
        var p1 = _planner.Plan(Request([Goal("fake ingot", 1)], recipes, staleEntry));
        Assert.IsFalse(p1.Committable);
        Assert.IsTrue(p1.Issues.Any(i => i.Kind == PlanIssueKind.StaleStock));
        Assert.HasCount(1, p1.Crafts, "the plan is still shown");

        var markedAfter = _planner.Plan(Request([Goal("fake ingot", 1)], recipes, Stock(("fake ore", 50)), staleSince: T0.AddMinutes(1)));
        Assert.IsFalse(markedAfter.Committable);

        var markedBefore = _planner.Plan(Request([Goal("fake ingot", 1)], recipes, Stock(("fake ore", 50)), staleSince: T0.AddMinutes(-1)));
        Assert.IsTrue(markedBefore.Committable, "observations newer than the mark are fresh");
    }

    [TestMethod]
    public void VolatileStockPastItsFreshWindowIsStale_WhenThePlanKnowsTheTime()
    {
        // 2026-10-15: the shared homestead's goods (the Stock tab's Homestead sub-tab) are trusted for a short while only
        var recipes = new[] { R("fake-ingot", "fake ingot", 1, ("fake ore", 2)) };
        var stock = new Dictionary<ItemKey, StockEntry> { [I("fake ore")] = S("fake ore", 50) with { FreshUntilUtc = T0.AddMinutes(10) } };
        PlanRequest At(DateTimeOffset? now) => Request([Goal("fake ingot", 1)], recipes, stock) with { NowUtc = now };

        Assert.IsTrue(_planner.Plan(At(T0.AddMinutes(5))).Committable, "inside the window");
        var late = _planner.Plan(At(T0.AddMinutes(11)));
        Assert.IsFalse(late.Committable, "past the window: re-read before crafting");
        Assert.IsTrue(late.Issues.Any(i => i.Kind == PlanIssueKind.StaleStock && i.Item == I("fake ore")));
        Assert.IsTrue(_planner.Plan(At(null)).Committable, "a request without its time does not check windows");
        Assert.IsTrue(_planner.Plan(Request([Goal("fake ingot", 1)], recipes, Stock(("fake ore", 50))) with { NowUtc = T0.AddDays(9) }).Committable, "no window: never stale by time");
        Assert.IsTrue(stock[I("fake ore")].IsStaleAt(T0.AddMinutes(11)) && !stock[I("fake ore")].IsStaleAt(T0.AddMinutes(10)));
    }

    [TestMethod]
    public void SuccessRateUsesALowerBoundYield()
    {
        var recipe = R("fake-gem", "fake gem", 1, ("fake shard", 1)) with { SuccessRate = 0.5 };
        var plan = _planner.Plan(Request([Goal("fake gem", 3)], [recipe], Stock(("fake shard", 100))));
        var c = plan.Crafts.Single();
        Assert.AreEqual(6, c.Batches);
        Assert.AreEqual(3L, c.ExpectedOutputMin);
        Assert.AreEqual(6L, c.ExpectedOutputMax);
        Assert.IsTrue(plan.Committable);

        var odd = _planner.Plan(Request([Goal("fake gem", 7)], [recipe with { OutputQty = 2, SuccessRate = 0.3 }], Stock(("fake shard", 100))));
        var o = odd.Crafts.Single();
        Assert.IsGreaterThanOrEqualTo(7L, o.ExpectedOutputMin);
        Assert.IsLessThan(7L, (long)Math.Floor((o.Batches - 1) * 2 * 0.3 + 1e-9), "fewest batches");
    }

    [TestMethod]
    public void BatchesAreSplitByTheCap()
    {
        var recipes = new[] { R("fake-nail", "fake nail", 1, ("fake ore", 1)) };
        var plan = _planner.Plan(Request([Goal("fake nail", 250)], recipes, Stock(("fake ore", 1000))));
        Assert.AreEqual("1:fake-nailx99 | 2:fake-nailx99 | 3:fake-nailx52", Shape(plan));
        Assert.IsTrue(plan.Issues.Any(i => i.Kind == PlanIssueKind.BatchLimited && i.DetailCode == "batch.split"));
        Assert.IsTrue(plan.Committable, "a split is informational");
        Assert.AreEqual(250L, plan.Crafts.Sum(c => c.ExpectedOutputMin));

        var uiMax = _planner.Plan(Request([Goal("fake nail", 25)], recipes, Stock(("fake ore", 1000)), PlannerPolicy.Default with { MaxBatchPerCraft = 10 }));
        Assert.AreEqual("1:fake-nailx10 | 2:fake-nailx10 | 3:fake-nailx5", Shape(uiMax));
    }

    [TestMethod]
    public void FewestCraftsChoosesTheCheaperRecipeAndPreferredOverridesIt()
    {
        var recipes = new[]
        {
            R("fake-potion-long", "fake potion", 1, ("fake extract", 1)),
            R("fake-extract", "fake extract", 1, ("fake herb", 2)),
            R("fake-potion-short", "fake potion", 1, ("fake herb", 3)),
        };
        var stock = Stock(("fake herb", 100), ("fake extract", 0));
        var cheap = _planner.Plan(Request([Goal("fake potion", 4)], recipes, stock));
        Assert.AreEqual("1:fake-potion-shortx4", Shape(cheap));

        var preferred = PlannerPolicy.Default with
        {
            Choice = RecipeChoicePolicy.Preferred,
            PreferredRecipeByItem = new Dictionary<string, string> { ["fake potion"] = "fake-potion-long" },
        };
        var pref = _planner.Plan(Request([Goal("fake potion", 4)], recipes, stock, preferred));
        Assert.AreEqual("1:fake-extractx4 | 2:fake-potion-longx4", Shape(pref));

        var missingPreferred = preferred with { PreferredRecipeByItem = new Dictionary<string, string> { ["fake potion"] = "fake-none" } };
        Assert.AreEqual("1:fake-potion-shortx4", Shape(_planner.Plan(Request([Goal("fake potion", 4)], recipes, stock, missingPreferred))));
    }

    [TestMethod]
    public void FewestCraftsPrefersAFeasibleRecipe()
    {
        var recipes = new[]
        {
            R("fake-a-cheap", "fake potion", 2, ("fake rare", 1)),
            R("fake-b-ok", "fake potion", 1, ("fake herb", 1)),
        };
        var plan = _planner.Plan(Request([Goal("fake potion", 2)], recipes, Stock(("fake herb", 5), ("fake rare", 0))));
        Assert.AreEqual("1:fake-b-okx2", Shape(plan));
        Assert.IsTrue(plan.Committable);
    }

    [TestMethod]
    public void TieBreakIsDeterministicVerifiedFirstThenId()
    {
        var verifiedB = R("fake-b", "fake potion", 1, ("fake herb", 1));
        var unverifiedA = R("fake-a", "fake potion", 1, ("fake herb", 1)) with { UserVerified = false };
        var allow = PlannerPolicy.Default with { AllowUnverifiedRecipes = true };
        var stock = Stock(("fake herb", 10));
        Assert.AreEqual("1:fake-bx2", Shape(_planner.Plan(Request([Goal("fake potion", 2)], [unverifiedA, verifiedB], stock, allow))));

        var verifiedA = unverifiedA with { UserVerified = true };
        var p1 = _planner.Plan(Request([Goal("fake potion", 2)], [verifiedB, verifiedA], stock));
        var p2 = _planner.Plan(Request([Goal("fake potion", 2)], [verifiedA, verifiedB], stock));
        Assert.AreEqual("1:fake-ax2", Shape(p1));
        Assert.AreEqual(JsonSerializer.Serialize(p1, ContractJson.Options), JsonSerializer.Serialize(p2, ContractJson.Options));
    }

    [TestMethod]
    public void UnverifiedOrPartialRecipeNeedsVerification()
    {
        var unverified = R("fake-ingot", "fake ingot", 1, ("fake ore", 1)) with { UserVerified = false };
        var plan = _planner.Plan(Request([Goal("fake ingot", 2)], [unverified], Stock(("fake ore", 10))));
        Assert.IsFalse(plan.Committable);
        Assert.IsEmpty(plan.Crafts, "no planned craft from an unverified recipe by default");
        Assert.AreEqual("recipe.unverified", plan.Issues.Single(i => i.Kind == PlanIssueKind.NeedsVerification).DetailCode);

        var partial = unverified with { UserVerified = true, Confidence = RecipeConfidence.Partial };
        var preview = _planner.Plan(Request([Goal("fake ingot", 2)], [partial], Stock(("fake ore", 10)),
            PlannerPolicy.Default with { AllowUnverifiedRecipes = true }));
        Assert.AreEqual("1:fake-ingotx2", Shape(preview));
        Assert.IsFalse(preview.Committable, "a preview with unverified recipes is never committable");
        Assert.AreEqual("recipe.partial", preview.Issues.Single(i => i.Kind == PlanIssueKind.NeedsVerification).DetailCode);
    }

    [TestMethod]
    public void DepthLimitIsReported()
    {
        var recipes = new[]
        {
            R("fake-l1", "fake l1", 1, ("fake l2", 1)),
            R("fake-l2", "fake l2", 1, ("fake l3", 1)),
            R("fake-l3", "fake l3", 1, ("fake raw", 1)),
        };
        var stock = Stock(("fake l2", 0), ("fake l3", 0), ("fake raw", 9));
        Assert.IsTrue(_planner.Plan(Request([Goal("fake l1", 1)], recipes, stock, PlannerPolicy.Default with { MaxDepth = 3 })).Committable);
        var plan = _planner.Plan(Request([Goal("fake l1", 1)], recipes, stock, PlannerPolicy.Default with { MaxDepth = 2 }));
        Assert.IsFalse(plan.Committable);
        Assert.IsTrue(plan.Issues.Any(i => i.Kind == PlanIssueKind.DepthExceeded && i.Item == I("fake l3")));
    }

    [TestMethod]
    public void GoalWithoutRecipeIsNoRecipe()
    {
        var plan = _planner.Plan(Request([Goal("fake unknown thing", 1)], [], Stock()));
        Assert.IsFalse(plan.Committable);
        Assert.AreEqual(PlanIssueKind.NoRecipe, plan.Issues.Single().Kind);
    }

    [TestMethod]
    public void ReplanAfterPartialCompletion()
    {
        var recipes = new[]
        {
            R("fake-sword", "fake sword", 1, ("fake blade", 1)),
            R("fake-blade", "fake blade", 1, ("fake ingot", 2)),
            R("fake-ingot", "fake ingot", 2, ("fake ore", 3)),
        };
        var stock = Stock(("fake ore", 30), ("fake blade", 0), ("fake ingot", 0));
        var first = _planner.Plan(Request([Goal("fake sword", 3)], recipes, stock));
        Assert.AreEqual("1:fake-ingotx3 | 2:fake-bladex3 | 3:fake-swordx3", Shape(first));
        Assert.IsTrue(first.Committable);

        // The first craft ran (resynced stock: ore 30-9, ingot +6). Same goal: the ingot craft drops out.
        var afterIngots = Stock(("fake ore", 21), ("fake blade", 0), ("fake ingot", 6));
        var second = _planner.Plan(Request([Goal("fake sword", 3)], recipes, afterIngots));
        Assert.AreEqual("1:fake-bladex3 | 2:fake-swordx3", Shape(second));

        // Stopped early: only 2 blades were made (ingot 6-4). Re-plan makes the missing blade from what is left.
        var stoppedEarly = Stock(("fake ore", 21), ("fake blade", 2), ("fake ingot", 2));
        var third = _planner.Plan(Request([Goal("fake sword", 3)], recipes, stoppedEarly));
        Assert.AreEqual("1:fake-bladex1 | 2:fake-swordx3", Shape(third));

        // Two swords produced already: the caller lowers the goal to what is still wanted.
        var last = _planner.Plan(Request([Goal("fake sword", 1)], recipes, Stock(("fake ore", 21), ("fake blade", 1), ("fake ingot", 0))));
        Assert.AreEqual("1:fake-swordx1", Shape(last));
        Assert.IsTrue(last.Committable);
    }

    [TestMethod]
    public void EstimatesAreRangesFromTheTickCadence()
    {
        var plain = R("fake-a", "fake a", 1, ("fake raw", 1));
        var learned = R("fake-b", "fake b", 1, ("fake raw", 1)) with { MedianTickCadence = TimeSpan.FromSeconds(2) };
        var plan = _planner.Plan(Request([Goal("fake a", 10), Goal("fake b", 10)], [plain, learned], Stock(("fake raw", 100))));
        Assert.AreEqual(new TimeRange(TimeSpan.FromSeconds(19), TimeSpan.FromSeconds(30)), plan.Crafts[0].Estimate);
        Assert.AreEqual(new TimeRange(TimeSpan.FromSeconds(18), TimeSpan.FromSeconds(24)), plan.Crafts[1].Estimate);
        Assert.AreEqual(new TimeRange(TimeSpan.FromSeconds(37), TimeSpan.FromSeconds(54)), plan.Estimate);
    }

    [TestMethod]
    public void BadDataNeverThrows()
    {
        var plan = _planner.Plan(new PlanRequest(null!, null!, null!, null!, null!, null));
        Assert.IsTrue(plan.Committable);
        Assert.IsEmpty(plan.Crafts);

        var broken = new[]
        {
            R("fake-a", "fake a", 0, ("fake raw", 1)),
            R("fake-a2", "fake a", 1, ("fake raw", 1)) with { Materials = null! },
            null!,
            R("fake-dup", "fake b", 1, ("fake raw", 1)),
            R("fake-dup", "fake b", 1, ("fake raw", 2)),
        };
        var p = _planner.Plan(Request([Goal("fake a", 1), Goal("fake b", 1), new CraftGoal(I("fake a"), -3), null!], broken, Stock(("fake raw", 5))));
        Assert.IsFalse(p.Committable);
        Assert.IsEmpty(p.Crafts);
        Assert.IsTrue(p.Issues.All(i => i.Kind == PlanIssueKind.NeedsVerification && i.DetailCode == "recipe.invalid"));

        var huge = _planner.Plan(Request([Goal("fake a", int.MaxValue)],
            [R("fake-a", "fake a", 1, ("fake b", int.MaxValue)), R("fake-b", "fake b", 1, ("fake c", int.MaxValue)), R("fake-c", "fake c", 1, ("fake raw", int.MaxValue))],
            Stock(("fake b", 0), ("fake c", 0), ("fake raw", 0))));
        Assert.IsFalse(huge.Committable);

        Assert.IsFalse(_planner.Plan(null!).Committable);
    }

    [TestMethod]
    public void SelfConsumingRecipeNeverFeedsOnItsOwnOutput()
    {
        // Upgrading 2 fake gems into 3 (with a catalyst) needs gems from stock, never from its own output.
        var recipes = new[] { R("fake-gem-up", "fake gem", 3, ("fake gem", 2), ("fake catalyst", 1)) };
        var plan = _planner.Plan(Request([Goal("fake gem", 3)], recipes, Stock(("fake gem", 1), ("fake catalyst", 5))));
        Assert.IsFalse(plan.Committable);
        Assert.IsTrue(plan.Issues.Any(i => i.Kind == PlanIssueKind.Cycle));

        var ok = _planner.Plan(Request([Goal("fake gem", 3)], recipes, Stock(("fake gem", 2), ("fake catalyst", 5))));
        Assert.IsTrue(ok.Committable);
        Assert.AreEqual("1:fake-gem-upx1", Shape(ok));
    }
}
