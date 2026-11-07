using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Host;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Perception;
using Companion.Contracts.Planning;
using Companion.Feature.Crafting;

namespace Companion.Features.Tests;

/// <summary>
/// The craft flow end to end against fakes, with the real crafting procedures in their SURVEYED shape (stubs lifted,
/// the Make button as a commit click). The fake engine simulates the craft session in the UI model.
/// </summary>
[TestClass]
public sealed class CraftFlowTests
{
    private static readonly ItemKey Potion = new("small potion");
    private static readonly ItemKey Herb = new("herb");
    private static readonly ItemKey Water = new("water");

    private static readonly Recipe PotionRecipe = new(
        "small potion|lifeskill|0", Potion, 1, "lifeskill", [new MaterialRequirement(Herb, 2), new MaterialRequirement(Water, 1)], [],
        null, DateTimeOffset.UnixEpoch, null, true, RecipeConfidence.Complete, null, null);

    private static JobParameters CraftParams(long qty) => new(new Dictionary<string, object> { ["item"] = Potion, ["qty"] = qty });

    [TestMethod]
    public void CraftResyncsPlansPreparesCommitsAwaitsAndDerivesStock()
    {
        var w = new CraftWorld();
        w.Knowledge.Rules.Add(new KeepStockRule(Herb, 4));

        var r = w.Run("Craft", CraftParams(3));

        Assert.AreEqual((JobState.Succeeded, "goal.met"), (r.State, r.Code), r.Code);
        CollectionAssert.AreEqual(new[] { "crafting.resync", "crafting.prepare", "crafting.commit" }, w.Ctx.Runner.RunIds.ToArray());

        // The commit run is the surveyed commit procedure with the requested ticks set from the READ quantity.
        var commit = w.Ctx.Runner.Runs[2];
        Assert.AreEqual((ProcedureKind.Commit, AutomationSwitch.Crafting), (commit.Kind, commit.Gate));
        Assert.IsTrue(commit.Steps.Single(s => s.Flags.HasFlag(StepFlags.Commit)).Action is ClickAction);
        Assert.AreEqual(3, commit.Steps.Select(s => s.Action).OfType<AwaitCraftAction>().Single().RequestedTicks);

        // Plan: goal = qty, keep-stock minimums reserved, verified recipe, resynced stock.
        var plan = w.Ctx.PlannerFake.Requests.Single();
        Assert.AreEqual(new CraftGoal(Potion, 3), plan.Goals.Single());
        Assert.AreEqual(Herb, plan.KeepStock.Single().Item);
        Assert.AreEqual(10L, plan.Stock[Herb].Count);

        // Stock: observations from the crafting view, derived product delta, consumed materials stale.
        Assert.HasCount(2, w.Knowledge.Observations);
        Assert.IsTrue(w.Knowledge.Observations.All(o => o.Kind == ObservationKind.CraftViewPair));
        var delta = w.Knowledge.Derived.Single();
        Assert.AreEqual((Potion, 3L, "craft.tick"), (delta.Item, delta.Delta, delta.CauseCode));
        Assert.AreEqual(5L, w.Knowledge.Items[Potion].Count);
        CollectionAssert.AreEquivalent(new[] { Herb, Water }, w.Knowledge.StaleMarks.Where(m => m.Reason == "craft.consumed").Select(m => m.Item!.Value).ToArray());
        Assert.IsTrue(w.Knowledge.Saves > 0);

        Assert.AreEqual(1L, r.Data!["crafts"]);
        Assert.AreEqual(3L, r.Data["ticks"]);
        Assert.AreEqual(0L, r.Data["earlyStops"]);
        Assert.IsTrue(w.Ctx.JournalFake.Records.Any(j => j.Kind == JournalKind.Plan));
        Assert.IsTrue(w.Ctx.JournalFake.Records.Any(j => j.Kind == JournalKind.Stock && Equals(j.Fields["source"], "craft.tick")));
    }

    [TestMethod]
    public void AQuantityCappedByTheUiIsSplitAndReplannedAfterEveryBatch()
    {
        var w = new CraftWorld { UiMaxQuantity = 3 };

        var r = w.Run("Craft", CraftParams(5));

        Assert.AreEqual((JobState.Succeeded, "goal.met"), (r.State, r.Code));
        CollectionAssert.AreEqual(
            new[] { "crafting.resync", "crafting.prepare", "crafting.commit", "crafting.resync", "crafting.prepare", "crafting.commit" },
            w.Ctx.Runner.RunIds.ToArray());
        CollectionAssert.AreEqual(new[] { 3, 2 }, w.RequestedTicks);
        CollectionAssert.AreEqual(new[] { 5, 2 }, w.Ctx.PlannerFake.Requests.Select(q => q.Goals.Single().Qty).ToArray());
        Assert.AreEqual(2L, r.Data!["crafts"]);
    }

    [TestMethod]
    public void AnEarlyStopIsNeverRecommittedBlindlyAndTwoOnTheSameRecipeHalt()
    {
        var w = new CraftWorld { TicksFor = q => q - 1 };

        var r = w.Run("Craft", CraftParams(3));

        Assert.AreEqual((JobState.Halted, nameof(HaltCode.CraftStoppedEarlyTwice)), (r.State, r.Code));
        CollectionAssert.AreEqual(new[] { 3, 1 }, w.RequestedTicks, "re-read and re-planned before the second commit");
        Assert.AreEqual(2, w.Ctx.Runner.Runs.Count(p => p.Id == "crafting.commit"));
        Assert.AreEqual(2L, r.Data!["earlyStops"]);
    }

    [TestMethod]
    public void AnIsBlockWithoutTheStationUiHaltsBlockedCraftSpot()
    {
        var w = new CraftWorld { StationUi = null };
        var r = w.Run("Craft", CraftParams(3));
        Assert.AreEqual((JobState.Halted, nameof(HaltCode.BlockedCraftSpot)), (r.State, r.Code));
        Assert.HasCount(0, w.Knowledge.Derived);
    }

    [TestMethod]
    public void ANotCommittablePlanStopsBeforeAnyCraftInput()
    {
        var w = new CraftWorld { Committable = false };
        var r = w.Run("Craft", CraftParams(3));
        Assert.AreEqual((JobState.Failed, "plan.notCommittable"), (r.State, r.Code));
        CollectionAssert.AreEqual(new[] { "crafting.resync" }, w.Ctx.Runner.RunIds.ToArray());
        Assert.AreEqual("UnknownStock", r.Data!["firstIssue"]);
    }

    [TestMethod]
    public void AReadBackQuantityOutsideThePlanIsNeverCommitted()
    {
        var w = new CraftWorld { QuantityOverride = 7 };
        var r = w.Run("Craft", CraftParams(3));
        Assert.AreEqual((JobState.Failed, "prepare.quantityMismatch"), (r.State, r.Code));
        CollectionAssert.DoesNotContain(w.Ctx.Runner.RunIds.ToArray(), "crafting.commit");

        var unread = new CraftWorld { QuantityUnknown = true };
        var r2 = unread.Run("Craft", CraftParams(3));
        Assert.AreEqual((JobState.Failed, "prepare.quantityMismatch"), (r2.State, r2.Code));
        CollectionAssert.DoesNotContain(unread.Ctx.Runner.RunIds.ToArray(), "crafting.commit");
    }

    [TestMethod]
    public void ACraftAlreadyInProgressOrAReviveRefusesWithZeroInputs()
    {
        var busy = new CraftWorld();
        busy.Ctx.Ui = T.Snapshot(100, new CraftSessionInfo(true, "UI_LIFE_ALCHEMIST_MAKE_POT", DateTime.Now, T.Pos(50), 3, null, null, false));
        Assert.AreEqual((JobState.Refused, "craft.inProgress"), (busy.Run("Craft", CraftParams(3)).State, busy.LastCode));
        Assert.HasCount(0, busy.Ctx.Runner.Runs);

        var dead = new CraftWorld();
        dead.Ctx.Ui = T.Snapshot(100, busy: BusyKind.Revive);
        Assert.AreEqual(JobState.Refused, dead.Run("Craft", CraftParams(3)).State);
        Assert.AreEqual("busy:Revive", dead.LastCode);
        Assert.HasCount(0, dead.Ctx.Runner.Runs);
    }

    [TestMethod]
    public void AHaltInsideTheCommitProcedureEndsTheJobHalted()
    {
        var w = new CraftWorld { CommitHalt = HaltCode.ForbiddenUi };
        var r = w.Run("Craft", CraftParams(3));
        Assert.AreEqual((JobState.Halted, "ForbiddenUi"), (r.State, r.Code));
        Assert.HasCount(0, w.Knowledge.Derived);
    }

    [TestMethod]
    public void KeepStockCraftsUpToTheMinimumWithoutReservingItAndHonoursTheCaps()
    {
        var w = new CraftWorld(new KeepStockConfig(Enabled: true, MaxCraftsPerHour: 6, MaxDailyCrafts: 1));
        w.Knowledge.Rules.Add(new KeepStockRule(Potion, 5));

        var r = w.Run("KeepStock", JobParameters.Empty);

        Assert.AreEqual((JobState.Succeeded, "goal.met"), (r.State, r.Code));
        Assert.AreEqual(new CraftGoal(Potion, 3), w.Ctx.PlannerFake.Requests.Single().Goals.Single());
        Assert.HasCount(0, w.Ctx.PlannerFake.Requests.Single().KeepStock);
        Assert.AreEqual(5L, w.Knowledge.Items[Potion].Count);

        // Daily cap (1) reached: the next trigger is refused before any input.
        w.Knowledge.Items[Potion] = w.Knowledge.Items[Potion] with { Count = 1, Stale = false };
        var runsBefore = w.Ctx.Runner.Runs.Count;
        var capped = w.Run("KeepStock", JobParameters.Empty);
        Assert.AreEqual((JobState.Refused, "keepStock.cap.daily"), (capped.State, capped.Code));
        Assert.AreEqual(runsBefore, w.Ctx.Runner.Runs.Count);
    }

    [TestMethod]
    public void KeepStockNeverCraftsForUnknownOrStaleStockAndIsOffByDefault()
    {
        var w = new CraftWorld(new KeepStockConfig(Enabled: true));
        w.Knowledge.Items.Remove(Potion);
        w.Knowledge.Rules.Add(new KeepStockRule(Potion, 5));
        var r = w.Run("KeepStock", JobParameters.Empty);
        Assert.AreEqual((JobState.Succeeded, "goal.met"), (r.State, r.Code));
        CollectionAssert.AreEqual(new[] { "crafting.resync" }, w.Ctx.Runner.RunIds.ToArray());
        Assert.HasCount(0, w.Ctx.PlannerFake.Requests);

        var off = new CraftWorld();
        off.Knowledge.Rules.Add(new KeepStockRule(Potion, 5));
        var refused = off.Run("KeepStock", JobParameters.Empty);
        Assert.AreEqual((JobState.Refused, "keepStock.disabled"), (refused.State, refused.Code));
        Assert.HasCount(0, off.Ctx.Runner.Runs);
    }

    /// <summary>Fake game world for the craft flow.</summary>
    private sealed class CraftWorld
    {
        private readonly FeatureHost _host;
        private long _offset = 1000;
        private long _herb = 10;
        private long _water = 10;
        private int _lastPlanned;

        public CraftWorld(KeepStockConfig? keepStock = null)
        {
            var (host, env, _) = T.LoadReal();
            _host = host;
            Ctx = new FakeJobContext(T.SurveyedCatalog(env.Procedures))
            {
                Config = CompanionConfig.Default with { KeepStock = keepStock ?? new KeepStockConfig() },
                Ui = T.Snapshot(1000),
            };
            Ctx.SwitchesFake.Crafting = true;
            Knowledge.Recipes.Add(PotionRecipe);
            Knowledge.Items[Potion] = new StockEntry(Potion, 2, null, StockSource.Observed, DateTimeOffset.UnixEpoch, T.Pos(500), 0.9, "bag:visible", false);
            Ctx.Runner.Script = Engine;
            Ctx.PlannerFake.Script = Plan;
        }

        public FakeJobContext Ctx { get; }

        public FakeKnowledge Knowledge => Ctx.KnowledgeFake!;

        public Func<int, int> TicksFor { get; init; } = q => q;

        public string? StationUi { get; init; } = "UI_LIFE_ALCHEMIST_MAKE_POT";

        public int? UiMaxQuantity { get; init; }

        public long? QuantityOverride { get; init; }

        public bool QuantityUnknown { get; init; }

        public bool Committable { get; init; } = true;

        public HaltCode? CommitHalt { get; init; }

        public List<int> RequestedTicks { get; } = [];

        public string? LastCode { get; private set; }

        public JobResult Run(string job, JobParameters parameters)
        {
            // the registered Craft job now runs CraftPlanRun; CraftFlow (still used by KeepStock) is tested through the old run
            var handler = job == "Craft" ? new CraftFlowGoalJobHandler() : T.Handler(_host, job);
            var r = handler.Execute(Ctx, parameters, CancellationToken.None);
            LastCode = r.Code;
            return r;
        }

        private CraftPlan Plan(PlanRequest request)
        {
            var goal = request.Goals.Single();
            _lastPlanned = Math.Min(goal.Qty, 99);
            var crafts = new[]
            {
                new PlannedCraft(1, PotionRecipe.Id, Potion, _lastPlanned, _lastPlanned, _lastPlanned, [], new TimeRange(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3))),
            };
            var issues = Committable ? Array.Empty<PlanIssue>() : [new PlanIssue(PlanIssueKind.UnknownStock, Water, null, "stock.unknown")];
            return new CraftPlan(crafts, [], issues, new TimeRange(TimeSpan.Zero, TimeSpan.Zero), Committable);
        }

        private ProcedureResult Engine(ProcedureDefinition p)
        {
            _offset += 100;
            switch (p.Id)
            {
                case "crafting.resync":
                    return T.Result(p.Id, ProcedureStatus.Succeeded, new Dictionary<string, ScreenReadResult>
                    {
                        ["pairs"] = T.Read("craft.pairs", _offset, T.Text("name.01", "Herb"), T.Pair("pair.01", _herb, 2), T.Text("name.02", "Water"), T.Pair("pair.02", _water, 1)),
                    }, inputs: 2);

                case "crafting.prepare":
                    var q = QuantityOverride ?? Math.Min(_lastPlanned, UiMaxQuantity ?? int.MaxValue);
                    var value = QuantityUnknown ? T.Unknown("quantity") : T.Int("quantity", q);
                    return T.Result(p.Id, ProcedureStatus.Succeeded, new Dictionary<string, ScreenReadResult>
                    {
                        ["quantity"] = T.Read("craft.quantity", _offset, value),
                    }, inputs: 2);

                case "crafting.commit":
                    if (CommitHalt is { } code)
                    {
                        return T.Result(p.Id, ProcedureStatus.Halted, halt: new HaltReport(code, "UI_RECHARGE", "j", p.Id, "commit", DateTimeOffset.UnixEpoch, null), inputs: 1);
                    }

                    var requested = p.Steps.Select(s => s.Action).OfType<AwaitCraftAction>().Single().RequestedTicks;
                    RequestedTicks.Add(requested);
                    var ticks = Math.Max(0, TicksFor(requested));
                    var start = _offset + 10;
                    _offset += 1000;
                    _herb -= 2 * ticks;
                    _water -= ticks;
                    Ctx.Ui = T.Snapshot(_offset, new CraftSessionInfo(false, StationUi, DateTime.Now, T.Pos(start), ticks + 1, DateTime.Now, DateTime.Now, true));
                    return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 1);

                default:
                    throw new InvalidOperationException("unexpected procedure " + p.Id);
            }
        }
    }
}
