using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Planning;
using Companion.Features;

namespace Companion.Features.Tests;

/// <summary>
/// The goal loop (the Craft job) against a fake engine: a favourite recipe, a scripted product tip per check, a craft
/// session per Make. Regression tests for incident 14 (R2.10) and the no-progress rule (R2.3).
/// </summary>
[TestClass]
public sealed class Release2PlanTests
{
    private static readonly ItemKey Cake = new("scallion fish cake");

    private static readonly Recipe CakeRecipe = new(
        "scallion fish cake|lifeskill|0", Cake, 1, "lifeskill", [new MaterialRequirement(new ItemKey("golden juice"), 5, 20330051)], [], null,
        DateTimeOffset.UnixEpoch, "screen:craft.fav/7", false, RecipeConfidence.Complete, null, null, ProductItemId: 1063000);

    [TestMethod]
    public void EachGoalsFirstFavouritesRead_ReopensTheAbilityView_WhateverTheSlot()
    {
        // 2026-10-31 audit: the slot-number rule of the favourites scan skipped the open for slots 2-10; after cooking the
        // game re-shows the Make view scrolled to the dish, so every second queued goal missed My Favorites (the reopen
        // found them 144 times of 144)
        var w = new PlanWorld { Tips = new Queue<long>([1063000, 1063000]) };

        w.Run(1);

        var first = w.Ctx.Runner.Runs.First(r => r.Id == "crafting.scanFavorites");
        Assert.IsTrue(first.Steps.Any(s => s.Id == "slot.07"), "the cake's slot 7");
        Assert.IsFalse(first.Steps.Single(s => s.Id == "open").Flags.HasFlag(StepFlags.SkipIfSatisfied), "the first read opens the view again");
    }

    [TestMethod]
    public void AGoalWhoseFavouritesStayHidden_TellsTheUserOnce_AndEndsAfterTwoQuietTries()
    {
        // 2026-10-31 audit: a quiet retry presses nothing, so the screen never changes by itself; the goal waited silently
        // (5 and 25 minute pauses, up to 3 hours) and blocked the queue behind it
        var hidden = new StepAttemptRecord("slot.07", 3, StepOutcome.PreconditionLost, OutcomeClass.Counted, RecoveryAction.End, null, false, null,
            "exhausted.PreconditionLostExhausted", DateTimeOffset.UnixEpoch, "template.notFound:craft.fav.anchor");
        var w = new PlanWorld { Favorites = p => T.Result(p.Id, ProcedureStatus.Deferred, attempts: [hidden]) with { EndDetail = "exhausted.PreconditionLostExhausted" } };
        var start = w.Ctx.Clock.Monotonic;

        w.Run(3);

        Assert.AreEqual(2, w.Events("plan.deferred"));
        Assert.AreEqual(1, w.Events("plan.quietGiveUp"));
        Assert.AreEqual(1, w.Ctx.NotifierFake.Sent.Count(n => n.Code == "plan.screenHidden"));
        Assert.IsTrue(w.Ctx.Clock.Monotonic - start < TimeSpan.FromHours(1), "not the 3-hour wait: " + (w.Ctx.Clock.Monotonic - start));
    }

    // live 2026-10-23 (incident 14): 60 cakes made, then a disturbed slot click; the slot-moved retry's 0 replaced the 60
    [TestMethod]
    public void WhatALoopMadeBeforeTheSlotCheckFailed_CountsTowardsTheGoal()
    {
        // the first check passes, then two wrong products (a moved slot: the second look sees the same other dish): the
        // loop ends "slotItem.changed" after 2 cakes; the slot is given up (incident 29) and every later round looks for
        // the cake in the list, which this fake never allows
        var w = new PlanWorld { Tips = new Queue<long>([1063000, 1061000, 1061000, 1063000]), TicksPerMake = new Queue<int>([2, 1]) };

        var r = w.Run(3);

        Assert.AreEqual("plan.deferred", r.Code);
        Assert.AreEqual(2L, r.Data!["made"], "the 2 made before the slot check count");
        Assert.AreEqual(1, w.Events("plan.slotMoved"));
        Assert.AreEqual(2L, w.Ctx.JournalFake.Records.First(j => Equals(j.Fields.GetValueOrDefault("event"), "plan.slotMoved")).Fields["madeBefore"]);
        Assert.AreEqual(1, w.Events("favorites.slotReleased"));
        Assert.AreEqual("screen:craft.fav.old/7", w.Ctx.KnowledgeFake!.Recipes.Single().SourceCapture);
    }

    // R2.3, live 2026-10-21: the same plan round after round, nothing of the goal made, budgets reset each round
    [TestMethod]
    public void TheSamePlanAgainAfterARoundWithoutProgress_EndsTheGoal()
    {
        // every Make starts no craft and the game names Golden Juice missing; the plan never changes
        var w = new PlanWorld { Tips = new Queue<long>(Enumerable.Repeat(1063000L, 20)), TicksPerMake = new Queue<int>(Enumerable.Repeat(-1, 20)), NameMissing = true };

        var r = w.Run(3);

        Assert.AreEqual("plan.noProgress", r.Code);
        Assert.AreEqual("noProgress", r.Data!["endReason"]);
        Assert.IsLessThanOrEqualTo(2, w.Makes, "at most one more round with the same plan");
    }

    // incident 16 (2026-10-23): the cooking gear's notice sat over the Make view; the goal clicks the favourites under it
    [TestMethod]
    public void TheGearNotice_IsAnsweredBeforeTheGoalReadsTheMakeTab()
    {
        var w = new PlanWorld { Tips = new Queue<long>([1063000]), TicksPerMake = new Queue<int>([1]) };
        w.Ctx.ScreenOverride = new NoticeOnce();

        Assert.AreEqual("plan.done", w.Run(1).Code);

        var ids = w.Ctx.Runner.Runs.Select(p => p.Id).ToList();
        Assert.AreEqual("crafting.gearNotice", ids[0], "the notice first: " + string.Join(",", ids));
        Assert.IsTrue(ids.IndexOf("crafting.scanFavorites") > 0, "then the Make-tab read");
    }

    /// <summary>A screen whose first look shows the gear notice; every later look shows no notice.</summary>
    private sealed class NoticeOnce : Companion.Contracts.Engine.IScreenService
    {
        private bool _shown = true;

        public Companion.Contracts.GameIo.CaptureResult Capture(CancellationToken ct) => new(Companion.Contracts.GameIo.CaptureProblem.NotConnected, null, TimeSpan.Zero);

        public Companion.Contracts.Vision.HudClassification ClassifyHudStable(CancellationToken ct) => new(Companion.Contracts.Vision.HudState.Unknown, 0, 0, false, false, "fake");

        public Companion.Contracts.Perception.ScreenReadResult Read(string readerId, CancellationToken ct)
        {
            var shown = _shown && readerId == "crafting.gear.notice";
            _shown &= !shown;
            return new(readerId, shown ? Companion.Contracts.Perception.ReadStatus.Ok : Companion.Contracts.Perception.ReadStatus.ScreenMismatch, [], null, DateTimeOffset.UnixEpoch);
        }
    }

    private sealed class PlanWorld
    {
        private readonly FeatureHost _host;
        private long _offset = 1000;

        public PlanWorld()
        {
            var (host, env, _) = T.LoadReal();
            _host = host;
            env.Procedures.Put(new ProcedureDefinition("OpenAbility", "core", [new StepDefinition("open", StatePredicate.Any, new ClickAction("menu.ability"), Expectation.Nothing)], ProcedureLimits.Default));
            Ctx = new FakeJobContext(env.Procedures) { Ui = T.Snapshot(_offset) with { AdvisoryStack = ["UI_LIFESKILL_MAKE_VIEW"] } };
            Ctx.SwitchesFake.Crafting = true;
            Ctx.KnowledgeFake!.Recipes.Add(CakeRecipe);
            Ctx.Runner.Script = Engine;
            Ctx.PlannerFake.Script = Plan;
        }

        public FakeJobContext Ctx { get; }

        /// <summary>The product ids the selected recipe's tip logs, one per check.</summary>
        public Queue<long> Tips { get; init; } = new();

        /// <summary>Crafts per Make press (-1: Make started no craft).</summary>
        public Queue<int> TicksPerMake { get; init; } = new();

        /// <summary>A Make that starts no craft logs Golden Juice's tip (the game names it missing).</summary>
        public bool NameMissing { get; init; }

        /// <summary>The Make-tab refresh's runs (default: done, no reads).</summary>
        public Func<ProcedureDefinition, ProcedureResult>? Favorites { get; init; }

        public int Makes { get; private set; }

        public JobResult Run(long qty) =>
            T.Handler(_host, "Craft").Execute(Ctx, new JobParameters(new Dictionary<string, object> { ["item"] = Cake, ["qty"] = qty }), CancellationToken.None);

        public int Events(string name) => Ctx.JournalFake.Records.Count(j => Equals(j.Fields.GetValueOrDefault("event"), name));

        private static CraftPlan Plan(PlanRequest request)
        {
            var qty = request.Goals.Single().Qty;
            return new CraftPlan(
                [new PlannedCraft(1, CakeRecipe.Id, Cake, qty, qty, qty, [], new TimeRange(TimeSpan.Zero, TimeSpan.Zero))],
                [], [], new TimeRange(TimeSpan.Zero, TimeSpan.Zero), true);
        }

        private ProcedureResult Engine(ProcedureDefinition p)
        {
            switch (p.Id)
            {
                case "crafting.productTip":
                    _offset += 10;
                    var id = Tips.Count > 0 ? Tips.Dequeue() : 1063000;
                    Ctx.Ui = Ctx.Ui with { AsOf = T.Pos(_offset), LastItemTip = new ItemTipInfo("Selected", id, new DateTime(2026, 10, 23, 13, 0, 0), T.Pos(_offset)) };
                    return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 2);
                case "crafting.scanFavorites" when Favorites is not null:
                    return Favorites(p);
                case "crafting.gearNotice": // the gear notice's Ignore
                case "crafting.qty":
                case "crafting.scanFavorites": // the Make-tab refresh: no reads, the slot is skipped
                    return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 1);
                case "crafting.loop" when p.Steps.Any(s => s.Action is AwaitCraftAction):
                    Makes++;
                    var ticks = TicksPerMake.Count > 0 ? TicksPerMake.Dequeue() : 1;
                    if (ticks < 0)
                    {
                        if (NameMissing)
                        {
                            _offset += 10;
                            Ctx.Ui = Ctx.Ui with { AsOf = T.Pos(_offset), LastItemTip = new ItemTipInfo("Golden Juice", 20330051, new DateTime(2026, 10, 23, 13, 40, 4), T.Pos(_offset)) };
                        }

                        var attempt = new StepAttemptRecord("make", 1, StepOutcome.None, OutcomeClass.Counted, RecoveryAction.End, null, true, null, "commit.noEffect", DateTimeOffset.UnixEpoch);
                        return T.Result(p.Id, ProcedureStatus.Cancelled, inputs: 4, attempts: [attempt]);
                    }

                    var start = _offset + 10;
                    _offset += 1000;
                    Ctx.Ui = T.Snapshot(_offset, new CraftSessionInfo(false, "UI_LIFE_ALCHEMIST_MAKE_POT", DateTime.Now, T.Pos(start), ticks + 1, DateTime.Now, DateTime.Now, true)) with { AdvisoryStack = ["UI_LIFESKILL_MAKE_VIEW"] };
                    return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 5);
                case "crafting.loop":
                    return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: p.Steps.Count); // the selection part of a round
                default:
                    // the Make-list walk (the slot-moved fallback): not possible now
                    return T.Result(p.Id, ProcedureStatus.Deferred) with { EndDetail = "unreachable.CrossMonitor" };
            }
        }
    }
}
