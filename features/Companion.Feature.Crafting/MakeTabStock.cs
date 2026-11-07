using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Features.Kit;

namespace Companion.Feature.Crafting;

/// <summary>
/// Fresh owned counts from the Make tab (2026-10-23, the user: "read needed stock from the make tab since everything is
/// there already, no need for scrolling"; a Scallion Fish Cake goal gathered Water for Thick Fish Soup while 157 soups
/// were in the bag, because the stored soup count was a day old). For each My Favorites slot given, the favourites scan's
/// slot run selects the recipe (open, click, settle, read craft.product and craft.pairs); the product is named (its item
/// tip only when the panel matches no known recipe) and the owned counts of the product and of every material shown are
/// folded into the store as fresh observations. Read-only, no list scrolling. Unread values stay as they were. One
/// "stock.makeTab" journal record per slot (codes and counts only).
/// </summary>
internal static class MakeTabStock
{
    /// <summary>
    /// Reads the slots in order. Returns the number of counts observed; the procedure end that must end the caller's job
    /// (a halt, stop, cancel or game exit), else null; and the cause when a slot run was deferred, else null (audit R2.1:
    /// a deferred refresh was skipped silently and the plan used the old counts). A failed read only skips its slot.
    /// </summary>
    internal static (int Observed, JobResult? Stop, string? Deferred, int SoftEnds, bool Quiet) Refresh(IJobContext context, IReadOnlyList<int> slots, CancellationToken ct)
    {
        var data = context.ProcedureCatalog.Get(ScanFavoritesJobHandler.Procedure);
        var tipData = context.ProcedureCatalog.Get(ProductTip.Procedure);
        if (context.Knowledge is not { } knowledge || data is null || tipData is null || slots.Count == 0
            || !ScanFavoritesJobHandler.IsExpectedShape(data) || !ProductTip.IsExpectedShape(tipData))
        {
            return (0, null, null, 0, false);
        }

        var panel = new RecipePanel(context, tipData, ScanFavoritesJobHandler.Procedure, () => context.Clock.Sleep(ScanFavoritesJobHandler.SlotPause, ct), ct);
        var observed = 0;
        var first = true;
        foreach (var slot in slots.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var def = ScanFavoritesJobHandler.BuildSlot(data, slot);
            if (!first)
            {
                // the Make view is already open after the first slot: no reset, no reopen
                def = def with { Steps = [.. def.Steps.Select(s => s.Id == ScanFavoritesJobHandler.OpenStep ? s with { Flags = s.Flags | StepFlags.SkipIfSatisfied } : s)] };
            }
            else
            {
                // the first slot always reopens the Ability view (2026-10-31 audit: BuildSlot skips the open for slots 2-10, a
                // rule of the sequential favourites scan; after cooking the game re-shows the Make view scrolled to the dish,
                // so every second queued goal missed My Favorites: the reopen found them 144 times of 144, the skip 0 of 4)
                def = def with { Steps = [.. def.Steps.Select(s => s.Id == ScanFavoritesJobHandler.OpenStep ? s with { Flags = s.Flags & ~StepFlags.SkipIfSatisfied } : s)] };
            }

            first = false;
            var run = ScanFavoritesJobHandler.RunOnFavorites(context, def, ct, out var reran);
            if (run.Status != ProcedureStatus.Succeeded)
            {
                Journal(context, slot, "procedure." + run.Status, 0, 0, null, run.EndDetail);
                // the engine's soft ends this refresh spent (two when the slot ran again after the top drags), and "quiet":
                // one run that pressed nothing (the screen its step needs was not on the game; 2026-10-31 sweep)
                var softEnds = run.EndDetail?.StartsWith("exhausted.", StringComparison.Ordinal) == true ? (reran ? 2 : 1) : 0;
                var quiet = softEnds == 1 && run.InputBatches == 0 && run.Clicks == 0 && run.EscPresses == 0;
                return (observed, IsStop(run) ? JobKit.FromProcedure(run, "makeTab.") : null,
                    run.Status == ProcedureStatus.Deferred ? JobKit.FromProcedure(run, "makeTab.").Code : null, softEnds, quiet);
            }

            if (JobKit.CheckRead(run, ScanFavoritesJobHandler.ProductKey, out var product) is not null
                || JobKit.CheckRead(run, ScanFavoritesJobHandler.PairsKey, out var pairs) is not null
                || product is null || pairs is null)
            {
                Journal(context, slot, "read.failed", 0, 0, null);
                continue;
            }

            var read = panel.Fold(product, pairs);
            if (read.IsBlank)
            {
                Journal(context, slot, "blank", 0, 0, null);
                continue;
            }

            var (tip, named) = panel.NameProduct(read, skipKnown: true);
            if (!tip.Succeeded && IsStop(tip.Run))
            {
                Journal(context, slot, "tip." + tip.Run.Status, 0, read.UnknownSlots, null);
                return (observed, JobKit.FromProcedure(tip.Run, "makeTab."), null, 0, false);
            }

            var final = tip.Succeeded ? named : read;
            var n = ScreenRecipes.Observe(knowledge, final);
            observed += n;
            Journal(context, slot, "observed", n, final.UnknownSlots, final.ProductItemId);
        }

        return (observed, null, null, 0, false);
    }

    /// <summary>The favourites slots of the goal's recipe and of every sub-recipe below it that is a favourite.</summary>
    internal static List<int> SlotsFor(ItemKey goal, IReadOnlyList<Recipe> recipes)
    {
        var slots = new List<int>();
        var seen = new HashSet<ItemKey>();
        var queue = new Queue<ItemKey>([goal]);
        while (queue.Count > 0)
        {
            var item = queue.Dequeue();
            if (!seen.Add(item) || recipes.FirstOrDefault(r => r.Product == item) is not { } recipe)
            {
                continue;
            }

            if (CraftPlanRun.FavoriteSlot(recipe) is { } slot && !slots.Contains(slot))
            {
                slots.Add(slot);
            }

            foreach (var m in recipe.Materials)
            {
                queue.Enqueue(m.Item);
            }
        }

        return slots;
    }

    private static bool IsStop(ProcedureResult r) =>
        r.Status is ProcedureStatus.Halted or ProcedureStatus.Stopped or ProcedureStatus.Cancelled or ProcedureStatus.GameExited;

    private static void Journal(IJobContext context, int slot, string code, int observed, int unknown, long? productItemId, string? detail = null) =>
        JobKit.Journal(context, JournalKind.Stock, ScanFavoritesJobHandler.Procedure, JobKit.Data(
            ("event", "stock.makeTab"), ("slot", (long)slot), ("code", code), ("observed", (long)observed),
            ("unknown", (long)unknown), ("itemId", productItemId), ("detail", detail)));
}
