using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Features.Kit;

namespace Companion.Feature.Crafting;

/// <summary>
/// Rule mode (2026-10-16, the user: "salad at 120, x at 100, y at 140, then I go AFK"): the keep-stock rules of the
/// character, kept forever. For each rule: select the recipe in the Make list (ScanRecipeList find mode), read the owned
/// count in the badge under the product (craft.product, region product.owned), and make the difference through the goal
/// loop (<see cref="CraftPlanRun"/>: sub-recipes, gathering, typed batches). After a pass that made nothing (every rule
/// met, or blocked), it waits <see cref="RecheckEvery"/> and checks again; it ends only when stopped or cancelled
/// (rule mode off), a HALT, or when there are no rules. A rule whose recipe or count cannot be read is skipped and
/// journaled; it never stops the others.
/// </summary>
internal sealed class KeepStockLoopJobHandler : IJobHandler
{
    internal const string JobName = "KeepStockLoop";
    internal const string Procedure = "crafting.keepStock";

    /// <summary>The wait after a pass that made nothing.</summary>
    internal static readonly TimeSpan RecheckEvery = TimeSpan.FromMinutes(10);

    /// <summary>The wait after a pass that was held off (the user used the game, or the game could not be reached).</summary>
    internal static readonly TimeSpan RecheckSoon = TimeSpan.FromMinutes(1);

    /// <summary>The back-off of a blocked rule (<see cref="JobKit.RuleBackoff"/>).</summary>
    private static TimeSpan Backoff(int times) => JobKit.RuleBackoff(times);

    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        var refusal = JobKit.CheckCommitGates(context, AutomationSwitch.Crafting) ?? JobKit.RequireKnowledge(context);
        if (refusal is not null)
        {
            return refusal;
        }

        long madeAll = 0;
        var blocked = new Dictionary<ItemKey, (long Owned, string Reason, int Times, TimeSpan Until)>();
        TimeSpan? lastEarly = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var knowledge = context.Knowledge!;
            var rules = knowledge.GetKeepStockRules();
            if (rules.Count == 0)
            {
                return JobKit.Result(JobState.Succeeded, "keepStock.noRules");
            }

            long madePass = 0;
            var heldOff = false;
            var (met, stuck) = (0, 0);
            string? lastReason = null;
            foreach (var rule in rules)
            {
                ct.ThrowIfCancellationRequested();
                if (blocked.TryGetValue(rule.Item, out var b) && context.Clock.Monotonic < b.Until)
                {
                    stuck++; // backed off: not walked, read or planned until its time
                    lastReason = b.Reason;
                    continue;
                }

                var recipe = knowledge.GetRecipes().FirstOrDefault(r => r.Product == rule.Item && r.ProductItemId is > 0);
                if (recipe?.ProductItemId is not { } productId)
                {
                    Journal(context, "keepStock.noRecipe", rule.Item.Value, null, rule.Min);
                    continue;
                }

                // select it (a favourite or not, the list holds every learned recipe), then read the owned badge
                var found = new ScanRecipeListJobHandler().Execute(context, new JobParameters(new Dictionary<string, object> { ["find"] = productId }), ct);
                if (found.State is JobState.Halted or JobState.Stopped or JobState.Cancelled)
                {
                    return found;
                }

                if (CraftPlanRun.IsDeferred(found))
                {
                    // not now (live 2026-10-17: the game was the active window): tried again soon, not reported missing;
                    // the cause is journaled (audit #17: every deferral read "you were using the game")
                    heldOff = true;
                    Journal(context, "keepStock.deferred", rule.Item.Value, null, rule.Min, found.Code);
                    continue;
                }

                if (found.Code != "list.found")
                {
                    Journal(context, "keepStock.notFound", rule.Item.Value, null, rule.Min, found.Code);
                    stuck++;
                    lastReason = found.Code;
                    continue;
                }

                var read = context.Screen.Read(ScanFavoritesJobHandler.ProductReader, ct);
                if (ReadFold.OkValue(read, ReadFold.ProductOwnedRegion)?.Value is not { } owned)
                {
                    Journal(context, "keepStock.ownedUnreadable", rule.Item.Value, null, rule.Min);
                    stuck++;
                    lastReason = "ownedUnreadable";
                    continue;
                }

                var need = rule.Min - owned;
                Journal(context, "keepStock.check", rule.Item.Value, owned, rule.Min);
                if (need <= 0)
                {
                    met++;
                    blocked.Remove(rule.Item);
                    continue;
                }

                var result = CraftPlanRun.Run(context, rule.Item, (int)Math.Min(need, 999), ct);
                if (result.State is JobState.Halted or JobState.Stopped || (result.State == JobState.Cancelled && ct.IsCancellationRequested))
                {
                    return result;
                }

                var made = result.Data is { } d && d.TryGetValue("made", out var m) && m is long n ? n : 0;
                madePass += made;
                heldOff |= result.Code == "plan.deferred";
                if (made > 0 || result.Code == "plan.deferred")
                {
                    blocked.Remove(rule.Item);
                    continue;
                }

                // made nothing for a reason of its own: back off this rule; the same owned count and reason again doubles
                // the wait, anything new starts it over
                var reason = result.Data is { } rd && rd.TryGetValue("limitedBy", out var lb) && lb is string by && by.Length > 0 ? result.Code + ":" + by : result.Code;
                var times = blocked.TryGetValue(rule.Item, out var prior) && prior.Owned == owned && prior.Reason == reason ? prior.Times + 1 : 1;
                var backoff = Backoff(times);
                blocked[rule.Item] = (owned, reason, times, context.Clock.Monotonic + backoff);
                stuck++;
                lastReason = reason;
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(
                    ("event", "keepStock.blocked"), ("item", rule.Item.Value), ("owned", owned), ("reason", reason), ("backoffMin", (long)backoff.TotalMinutes)));
            }

            madeAll += madePass;
            if (madePass == 0)
            {
                // every rule met (or blocked for now): check again later; soon when the pass was only held off. The counts
                // say which (audit #18: "all rules met" was shown while a rule was blocked)
                var wait = heldOff ? RecheckSoon : RecheckEvery;
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "keepStock.waiting"), ("minutes", (long)wait.TotalMinutes), ("heldOff", heldOff), ("madeAll", madeAll),
                    ("met", (long)met), ("blocked", (long)stuck), ("reason", lastReason)));
                // at most one early re-check per 10 minutes (safety review 2026-10-23: eats a minute apart re-ran every pass)
                var allowEarly = lastEarly is not { } le || context.Clock.Monotonic - le >= RecheckEvery;
                if (WaitOrFoodEaten(context, wait, allowEarly, ct))
                {
                    lastEarly = context.Clock.Monotonic;
                }
            }
        }
    }

    /// <summary>
    /// Waits <paramref name="wait"/>, ending early (after a short settle) when the game logs an auto-eat (roadmap B, "keep
    /// food at N": the character ate, so a food rule may be below its number now). The blocked rules keep their back-off.
    /// </summary>
    private static bool WaitOrFoodEaten(IJobContext context, TimeSpan wait, bool allowEarly, CancellationToken ct)
    {
        var ate = context.UiState.LastAutoEat;
        var until = context.Clock.Monotonic + wait;
        context.ReportActivity("step", "keepStock.wait", JobKit.Data(("untilUtc", context.Clock.UtcNow + wait)));
        try
        {
            return WaitUntil(context, until, allowEarly ? ate : null, allowEarly, ct);
        }
        finally
        {
            context.ReportActivity("step", null);
        }
    }

    /// <summary>Sleeps until <paramref name="until"/>; true when an auto-eat ended it early.</summary>
    private static bool WaitUntil(IJobContext context, TimeSpan until, DateTime? ate, bool allowEarly, CancellationToken ct)
    {
        while (context.Clock.Monotonic < until)
        {
            context.Clock.Sleep(TimeSpan.FromSeconds(Math.Min(30, Math.Max(1, (until - context.Clock.Monotonic).TotalSeconds))), ct);
            if (allowEarly && context.UiState.LastAutoEat is { } now && now != ate)
            {
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "keepStock.foodEaten")));
                context.Clock.Sleep(FoodSettle, ct);
                return true;
            }
        }

        return false;
    }

    /// <summary>The settle after an auto-eat before the rules are checked (the eats come in a burst).</summary>
    internal static readonly TimeSpan FoodSettle = TimeSpan.FromSeconds(60);

    private static void Journal(IJobContext context, string code, string item, long? owned, int min, string? cause = null) =>
        JobKit.Journal(context, JournalKind.Stock, Procedure, JobKit.Data(("event", code), ("item", item), ("owned", owned), ("keepAt", (long)min), ("cause", cause)));
}
