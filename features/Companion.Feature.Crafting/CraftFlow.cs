using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Planning;
using Companion.Features.Kit;

namespace Companion.Feature.Crafting;

/// <summary>A cap on how many crafts a job may start (per job, or the keep-stock caps).</summary>
internal interface ICraftBudget
{
    /// <summary>Null if one more craft may start, else a stop code.</summary>
    string? BeforeCraft();

    /// <summary>Records a started craft.</summary>
    void AfterCraft();
}

/// <summary>
/// The craft flow (SPEC 11.4), shared by Craft and KeepStock:
/// <list type="number">
/// <item>switch on and character Allowed (checked by the handlers first);</item>
/// <item>no craft in progress, no revive or match (the engine gates combat, busy, foreground and CR-07 idleness);</item>
/// <item>resync: navigate to the Ability view and read craft.pairs; fold the owned counts;</item>
/// <item>plan from the resynced stock; it must be committable;</item>
/// <item>select the recipe, set the quantity, read the requested quantity (journaled; must be 1..planned batches);</item>
/// <item>commit (Commit step) and await the craft with the requested ticks (no input while crafting);</item>
/// <item>verify the start: a craft session that began after the commit, with its station UI (an is_block without the
/// station = HALT BlockedCraftSpot);</item>
/// <item>derived delta = ticks x outputQty on the product; consumed materials are marked stale;</item>
/// <item>ticks below the requested quantity = stopped early: no re-commit, re-read and re-plan; a second early stop on
/// the same recipe = HALT CraftStoppedEarlyTwice;</item>
/// <item>repeat (re-plan after every batch) until the goals are met or a cap is reached.</item>
/// </list>
/// Nothing runs while any of the three procedures still carries a needsSurvey stub.
/// </summary>
internal static class CraftFlow
{
    internal const string ResyncProcedure = "crafting.resync";
    internal const string PrepareProcedure = "crafting.prepare";
    internal const string CommitProcedure = "crafting.commit";
    internal const string PairsKey = "pairs";
    internal const string QuantityKey = "quantity";
    internal const string Coverage = "ability:craft.pairs";

    /// <summary>Resync/plan cycles per job (a craft per cycle at most).</summary>
    internal const int MaxIterations = 24;

    internal static readonly IReadOnlyList<string> Procedures = [ResyncProcedure, PrepareProcedure, CommitProcedure];

    /// <summary>Everything that must hold before the first procedure runs (no input on failure).</summary>
    public static JobResult? CheckReady(IJobContext ctx)
    {
        var refusal = JobKit.CheckProcedures(ctx, Procedures);
        if (refusal is not null)
        {
            return refusal;
        }

        var commit = ctx.ProcedureCatalog.Get(CommitProcedure)!;
        var commitSteps = commit.Steps.Count(s => s.Flags.HasFlag(StepFlags.Commit));
        var awaitSteps = commit.Steps.Count(s => s.Action is AwaitCraftAction);
        if (commit.Kind != ProcedureKind.Commit || commit.Gate != AutomationSwitch.Crafting || commitSteps != 1 || awaitSteps != 1)
        {
            return JobKit.Failed("procedure.invalid:" + CommitProcedure);
        }

        var ui = ctx.UiState;
        if (ui.Craft.InProgress)
        {
            return JobKit.Refused("craft.inProgress");
        }

        if (ui.Busy is BusyKind.Revive or BusyKind.Match)
        {
            return JobKit.Refused("busy:" + ui.Busy);
        }

        return null;
    }

    /// <summary>Runs the flow. <paramref name="goals"/> gets the current stock and the output produced so far.</summary>
    public static JobResult Run(
        IJobContext ctx,
        Func<StockView, IReadOnlyDictionary<ItemKey, long>, IReadOnlyList<CraftGoal>> goals,
        bool reserveKeepStock,
        ICraftBudget budget,
        CancellationToken ct)
    {
        var knowledge = ctx.Knowledge!;
        var produced = new Dictionary<ItemKey, long>();
        var earlyStops = new Dictionary<string, int>(StringComparer.Ordinal);
        var summary = new Summary();

        for (var iteration = 0; ; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            if (iteration >= MaxIterations)
            {
                return JobKit.Failed("craft.tooManyIterations", summary.Data());
            }

            if (iteration > 0 && goals(knowledge.GetStock(), produced).Count == 0)
            {
                return JobKit.Result(JobState.Succeeded, "goal.met", summary.Data());
            }

            // 3. Resync.
            var resync = ctx.Procedures.Run(ResyncProcedure, ctx.Run, ct);
            summary.Add(resync);
            if (resync.Status != ProcedureStatus.Succeeded)
            {
                return With(JobKit.FromProcedure(resync, "resync."), summary);
            }

            var readProblem = JobKit.CheckRead(resync, PairsKey, out var pairs);
            if (readProblem is not null)
            {
                return With(readProblem, summary);
            }

            var observations = ReadFold.Observations(pairs!, ObservationKind.CraftViewPair, Coverage);
            if (observations.Count == 0)
            {
                return JobKit.Failed("resync.noValues", summary.Data());
            }

            knowledge.ApplyObservations(observations);
            knowledge.Save();
            JobKit.Journal(ctx, JournalKind.Stock, ResyncProcedure, JobKit.Data(("source", "craft.pairs"), ("observed", (long)observations.Count)));

            // 4. Plan from the resynced stock.
            var stock = knowledge.GetStock();
            var wanted = goals(stock, produced);
            if (wanted.Count == 0)
            {
                return JobKit.Result(JobState.Succeeded, "goal.met", summary.Data());
            }

            var plan = ctx.Planner.Plan(new PlanRequest(
                wanted,
                reserveKeepStock ? knowledge.GetKeepStockRules() : [],
                knowledge.GetRecipes(),
                stock.Items,
                PlannerPolicy.Default,
                stock.StaleSince,
                ctx.Clock.UtcNow));
            JobKit.Journal(ctx, JournalKind.Plan, null, JobKit.Data(
                ("committable", plan.Committable),
                ("crafts", (long)plan.Crafts.Count),
                ("shortfalls", (long)plan.Shortfalls.Count),
                ("issues", (long)plan.Issues.Count)));
            if (!plan.Committable)
            {
                var data = summary.Data();
                data["shortfalls"] = (long)plan.Shortfalls.Count;
                data["issues"] = (long)plan.Issues.Count;
                if (plan.Issues.Count > 0)
                {
                    data["firstIssue"] = plan.Issues[0].Kind.ToString();
                }

                if (plan.Shortfalls.Count > 0)
                {
                    data["firstShortfall"] = plan.Shortfalls[0].Kind.ToString();
                }

                return JobKit.Failed("plan.notCommittable", data);
            }

            if (plan.Crafts.Count == 0)
            {
                return JobKit.Result(JobState.Succeeded, "plan.nothingToCraft", summary.Data());
            }

            var next = plan.Crafts.OrderBy(c => c.Order).ThenBy(c => c.RecipeId, StringComparer.Ordinal).First();
            var recipe = knowledge.GetRecipes().FirstOrDefault(r => string.Equals(r.Id, next.RecipeId, StringComparison.Ordinal));
            if (recipe is null || next.Batches < 1 || recipe.OutputQty < 1)
            {
                return JobKit.Failed("plan.invalidCraft", summary.Data());
            }

            var stop = budget.BeforeCraft();
            if (stop is not null)
            {
                return JobKit.Result(summary.Crafts > 0 ? JobState.Succeeded : JobState.Refused, stop, summary.Data());
            }

            // 5. Select the recipe, set the quantity, read the requested quantity.
            var prepare = ctx.Procedures.Run(PrepareProcedure, ctx.Run, ct);
            summary.Add(prepare);
            if (prepare.Status != ProcedureStatus.Succeeded)
            {
                return With(JobKit.FromProcedure(prepare, "prepare."), summary);
            }

            var quantityProblem = JobKit.CheckRead(prepare, QuantityKey, out var quantityRead);
            if (quantityProblem is not null)
            {
                return With(quantityProblem, summary);
            }

            var requested = ReadFold.IntValue(quantityRead!, ReadFold.QuantityRegion);
            JobKit.Journal(ctx, JournalKind.Plan, PrepareProcedure, JobKit.Data(
                ("planned", (long)next.Batches),
                ("requested", requested),
                ("order", (long)next.Order)));
            if (requested is not { } q || q < 1 || q > next.Batches)
            {
                // Never commit a quantity that was not read back exactly within the plan.
                return JobKit.Failed("prepare.quantityMismatch", summary.Data());
            }

            // 7. Commit, then await the craft (no input while crafting).
            var before = ctx.UiState;
            var commit = ctx.Procedures.Run(WithRequestedTicks(ctx.ProcedureCatalog.Get(CommitProcedure)!, (int)q), ctx.Run, ct);
            summary.Add(commit);
            if (commit.Status != ProcedureStatus.Succeeded)
            {
                return With(JobKit.FromProcedure(commit, "commit."), summary);
            }

            budget.AfterCraft();
            summary.Crafts++;
            var after = ctx.UiState;

            // 8. Verify the start.
            var session = after.Craft;
            if (!StartedAfter(session, before))
            {
                return JobKit.Failed("craft.notStarted", summary.Data());
            }

            if (session.StationUi is null)
            {
                return JobKit.Halted(HaltCode.BlockedCraftSpot, summary.Data());
            }

            // 11. Derived delta.
            var ticks = session.Ticks;
            summary.Ticks += ticks;
            var gained = ApplyDerived(ctx, knowledge, recipe, ticks, after.AsOf ?? session.StartPosition);
            produced[recipe.Product] = produced.GetValueOrDefault(recipe.Product) + gained;
            JobKit.Journal(ctx, JournalKind.Stock, CommitProcedure, JobKit.Data(
                ("source", "craft.tick"),
                ("ticks", (long)ticks),
                ("requested", q),
                ("gained", gained),
                ("viewReshown", session.CraftViewReshown)));

            // 12. Stopped early: no re-commit; re-read and re-plan. Twice on the same recipe = HALT.
            if (ticks < q)
            {
                summary.EarlyStops++;
                var stops = earlyStops.GetValueOrDefault(recipe.Id) + 1;
                earlyStops[recipe.Id] = stops;
                if (stops >= 2)
                {
                    return JobKit.Halted(HaltCode.CraftStoppedEarlyTwice, summary.Data());
                }
            }
        }
    }

    /// <summary>The commit procedure with its AwaitCraft step set to the requested ticks.</summary>
    internal static ProcedureDefinition WithRequestedTicks(ProcedureDefinition commit, int ticks) =>
        commit with
        {
            Steps = commit.Steps
                .Select(s => s.Action is AwaitCraftAction ? s with { Action = new AwaitCraftAction(ticks) } : s)
                .ToArray(),
        };

    /// <summary>True if the session in <paramref name="session"/> began after the pre-commit snapshot.</summary>
    internal static bool StartedAfter(CraftSessionInfo session, UiStateSnapshot before)
    {
        if (session.StartPosition is not { } start || (session.IsBlockCount < 1 && session.StationUi is null))
        {
            return false;
        }

        if (before.Craft.StartPosition is { } old && old.SameFile(start) && old.Offset == start.Offset)
        {
            return false;
        }

        return before.AsOf is not { } b || (start.SameFile(b) && start.Offset > b.Offset);
    }

    /// <summary>
    /// Product += ticks x outputQty (exact recipes); success-rate recipes and skipped deltas mark the product stale
    /// instead. Consumed materials are marked stale (the next resync re-reads them). Returns the lower-bound gain.
    /// </summary>
    private static long ApplyDerived(IJobContext ctx, ICharacterKnowledge knowledge, Recipe recipe, int ticks, LogPosition? at)
    {
        var now = ctx.Clock.UtcNow;
        long gained = 0;
        if (ticks > 0)
        {
            var full = (long)ticks * recipe.OutputQty;
            if (recipe.SuccessRate is { } rate)
            {
                gained = (long)Math.Floor(full * Math.Clamp(rate, 0.0, 1.0));
                knowledge.MarkStale(StaleScope.Item, recipe.Product, "craft.successRate", now);
            }
            else
            {
                gained = full;
                var applied = at is not null && knowledge.ApplyDerived(new DerivedStockDelta(recipe.Product, full, at, "craft.tick"));
                if (!applied)
                {
                    knowledge.MarkStale(StaleScope.Item, recipe.Product, "craft.derivedSkipped", now);
                }
            }

            foreach (var m in recipe.Materials)
            {
                knowledge.MarkStale(StaleScope.Item, m.Item, "craft.consumed", now);
            }
        }

        knowledge.Save();
        return gained;
    }

    private static JobResult With(JobResult r, Summary s)
    {
        var data = s.Data();
        if (r.Data is not null)
        {
            foreach (var (k, v) in r.Data)
            {
                data.TryAdd(k, v);
            }
        }

        return r with { Data = data };
    }

    private sealed class Summary
    {
        public int Crafts { get; set; }

        public long Ticks { get; set; }

        public int EarlyStops { get; set; }

        public int Inputs { get; private set; }

        public int Clicks { get; private set; }

        public int Esc { get; private set; }

        public int Runs { get; private set; }

        public void Add(ProcedureResult r)
        {
            Runs++;
            Inputs += r.InputBatches;
            Clicks += r.Clicks;
            Esc += r.EscPresses;
        }

        public Dictionary<string, object?> Data() => JobKit.Data(
            ("crafts", (long)Crafts),
            ("ticks", Ticks),
            ("earlyStops", (long)EarlyStops),
            ("runs", (long)Runs),
            ("inputs", (long)Inputs),
            ("clicks", (long)Clicks),
            ("esc", (long)Esc));
    }
}
