using System.Globalization;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Features.Kit;

namespace Companion.Feature.Crafting;

/// <summary>
/// CraftLoop {slot, count, batch}: repeats one round of the data procedure "crafting.loop" (live run 2026-10-14 map):
/// <list type="number">
/// <item>open the Ability view (core OpenAbility: Reset, ExpandMenu, click menu.ability);</item>
/// <item>close the phase announcement UI_LIFE_STAGE_PREVIEW with one Esc, only if it is open;</item>
/// <item>slot 1..10: click that My Favorites cell, placed from the located "Recent" anchor (never a raw point);</item>
/// <item>batch max: press the max-quantity button;</item>
/// <item>batch typed (2026-10-15): before each round the navigation procedure "crafting.qty" presses the quantity and
/// types the round's count on the game's keypad (min(count left, 999); the game clamps it to the most it can make), so one
/// Make press makes the whole batch. Typing is not a commit, so it runs outside the commit procedure (its far presses may
/// use the off-screen anchor patch rule; live 2026-10-15 the strict check failed 25 times on a cut anchor); when typing
/// fails, Make is not pressed;</item>
/// <item>press Make (commit step, crafting gate, character Allowed) and await the craft without input.</item>
/// </list>
/// Items of a round = the craft session's ticks (is_block lines after the first; one UI_ITEM_GET_TIP each). The loop
/// ends when count items are made (count &gt; 0), when a round makes nothing or no craft starts (materials out), or on
/// stop / HALT / any other procedure end. A fixed 3 s pause separates rounds. Every round is journaled.
/// The optional <c>item</c> (2026-10-15) is the game's item id of the product the caller expects in the slot (the tray
/// passes its recipe's product item id): when the latest favourites scan stored another product item id for that slot,
/// the job is refused before any input ("slotItem.changed"); the id is journaled with every round.
/// Slot 0 (2026-10-15) keeps the recipe selected in the game: the open step is left out, because reopening the Ability
/// view selects another recipe (live, Make was pressed on a water recipe after the list walk had selected Roast
/// Chicken); the Make press needs the Make view on top. With an <c>item</c> (the Craft plan after its list walk) the
/// selected product's tip is read before the first Make (crafting.productTip, the log names its id); another product
/// refuses the job ("selectedItem.changed") with nothing crafted.
/// </summary>
internal sealed class CraftLoopJobHandler : IJobHandler
{
    internal const string JobName = "CraftLoop";
    internal const string Procedure = "crafting.loop";
    internal const string SlotStepPrefix = "slot.";
    internal const string MaxStep = "max";
    internal const string MakeStep = "make";
    internal const string OpenStep = "open";
    internal const string BatchOne = "1";
    internal const string BatchMax = "max";
    internal const string BatchTyped = "typed";
    internal const string QtyStep = "qty";
    internal const string QtyProcedure = "crafting.qty";

    /// <summary>Largest count typed for one round (the keypad takes three digits here; the game clamps it anyway).</summary>
    internal const int MaxTyped = 999;

    /// <summary>
    /// The most one craft session makes (DISCOVERIES "Craft batch cap": 50 per session; journal 2026-10-23..28: 36 rounds
    /// typed 100-999, none made more than 51). A larger count left is typed as this: one keypad press less (~4 s) a round;
    /// the craft wait keeps its bound from the count left (a slow dish took 222 s for 50).
    /// </summary>
    internal const int SessionCap = 50;

    /// <summary>The steps of <see cref="QtyProcedure"/>, in order (the digit steps go between clear and OK).</summary>
    internal static readonly string[] TypedSteps = ["qty", "qty.settle", "key.clear", "key.ok", "qty.done"];
    internal const int MaxSlot = 10;
    internal const int MaxCount = 100_000;

    /// <summary>Largest item id accepted (18 digits).</summary>
    internal const long MaxItemId = 999_999_999_999_999_999;

    /// <summary>A max batch is not read back: the craft wait gets the largest tick bound (its timeout scales with it).</summary>
    internal const int MaxBatchTicks = 999;

    /// <summary>The end when a favourite slot's selected product is not the expected one (after one more selection).</summary>
    internal const string SlotChanged = "slotItem.changed";

    /// <summary>Fixed pause between rounds (human pace, no randomness).</summary>
    internal static readonly TimeSpan RoundPause = TimeSpan.FromSeconds(3);

    /// <summary>Parameter specs (validated by the job catalog before the handler runs).</summary>
    internal static readonly IReadOnlyList<JobParamSpec> Params =
    [
        new JobParamSpec("slot", JobParamType.Int, false, 0, MaxSlot, MaxLength: 2),
        new JobParamSpec("count", JobParamType.Int, true, 0, MaxCount, MaxLength: 6),
        new JobParamSpec("batch", JobParamType.Enum, false, Allowed: [BatchOne, BatchMax, BatchTyped], MaxLength: 5),
        new JobParamSpec("item", JobParamType.Int, false, 1, MaxItemId, MaxLength: 18),
    ];

    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        // Switch and permission first: a switched-off or not-Allowed job runs nothing (R10, KS-02).
        var refusal = JobKit.CheckCommitGates(context, AutomationSwitch.Crafting);
        if (refusal is not null)
        {
            return refusal;
        }

        if (!TryReadParams(parameters, out var slot, out var count, out var max, out var typed, out var item, out var bad))
        {
            return JobKit.Refused(bad!);
        }

        // the product the caller expects in the slot: refused when the latest scan stored another item there
        if (item is { } expected && slot > 0 && context.Knowledge is { } knowledge && SlotRecipe(knowledge, slot) is { ProductItemId: { } stored } && stored != expected)
        {
            return JobKit.Refused("slotItem.changed");
        }

        var keepSelection = slot == 0;
        // the product every Make must have: the caller's item, else the product the latest scan stored in the slot. The
        // selection is verified by its product tip before every Make (2026-10-16 safety review: a favourite slot click is
        // never checked by the engine, and the commit gate's user-activity window starts at the latest verification)
        var expectedId = item ?? (slot > 0 && context.Knowledge is { } known && SlotRecipe(known, slot) is { ProductItemId: { } inSlot } ? inSlot : null);
        var verifySelection = expectedId is not null;
        string[] needed = [Procedure, .. verifySelection ? new[] { ProductTip.Procedure } : [], .. typed ? new[] { QtyProcedure } : []];
        refusal = JobKit.CheckProcedures(context, needed);
        if (refusal is not null)
        {
            return refusal;
        }

        var tipData = verifySelection ? context.ProcedureCatalog.Get(ProductTip.Procedure) : null;
        if (tipData is not null && !ProductTip.IsExpectedShape(tipData))
        {
            return JobKit.Failed("procedure.invalid:" + ProductTip.Procedure);
        }

        var loopData = context.ProcedureCatalog.Get(Procedure)!;
        var qtyData = typed ? context.ProcedureCatalog.Get(QtyProcedure) : null;
        int TypedCount(long total) => typed ? (int)Math.Min(MaxTyped, count > 0 ? count - total : MaxTyped) : 0;
        var round = BuildRound(loopData, slot, max, keepSelection, TypedCount(0));
        if (round is null || (typed && (qtyData is null || BuildQty(qtyData, 1) is null)))
        {
            return JobKit.Failed("procedure.invalid:" + Procedure);
        }

        var ui = context.UiState;
        if (ui.Craft.InProgress)
        {
            return JobKit.Refused("craft.inProgress");
        }

        if (ui.Busy is BusyKind.Revive or BusyKind.Match)
        {
            return JobKit.Refused("busy:" + ui.Busy);
        }

        var tally = new Tally(slot, count, max ? BatchMax : typed ? BatchTyped : BatchOne, item);
        if (tipData is not null && keepSelection && VerifySelected(context, tipData, expectedId!.Value, slot, ct) is { } wrong)
        {
            // the selected recipe must be the expected product before anything is crafted (the log names the tip's id)
            return End(context, tally, wrong);
        }

        while (true)
        {
            if (tally.Rounds > 0)
            {
                PauseBetweenRounds(context, tally, ct);
                refusal = JobKit.CheckCommitGates(context, AutomationSwitch.Crafting);
                if (refusal is not null)
                {
                    return End(context, tally, refusal);
                }
            }

            if (typed || verifySelection)
            {
                // select (open, slot), verify the selected product, then type the count (navigation), then Make (commit):
                // selecting a recipe resets the game's quantity to 1 (live 2026-10-15: a count typed before the slot click
                // made 1), and no Make when the count was not typed or the product is not the expected one
                var n = TypedCount(tally.Total);
                if (typed && tally.Rounds > 0 && AfterBatch(context, qtyData!, keepSelection, ct) is { } lost)
                {
                    return End(context, tally, lost);
                }

                var full = BuildRound(loopData, slot, max, keepSelection, n)!;
                var makeAt = full.Steps.Select((s, i) => (s, i)).First(x => x.s.Id == MakeStep).i;
                round = full with { Steps = [.. full.Steps.Skip(makeAt)] };
                if (makeAt > 0)
                {
                    var selected = ScanFavoritesJobHandler.RunOnFavorites(context, full with { Steps = [.. full.Steps.Take(makeAt)] }, ct);
                    tally.Add(selected, round: false);
                    if (selected.Status != ProcedureStatus.Succeeded)
                    {
                        return End(context, tally, JobKit.FromProcedure(selected, "round."));
                    }
                }

                // every Make's selection is verified (slot 0's first round was verified before the loop)
                if (verifySelection && (tally.Rounds > 0 || !keepSelection) && VerifySelected(context, tipData!, expectedId!.Value, slot, ct) is { } changed)
                {
                    // audit R2.12 (incident 14): a slot click the user's mouse disturbed selected another recipe; the slot
                    // had not moved, but the loop gave up and the goal walked the whole list. A favourite is selected and
                    // checked once more; only a second wrong product counts as a moved slot
                    if (changed.Code != SlotChanged || makeAt == 0)
                    {
                        return End(context, tally, changed);
                    }

                    JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "craftLoop.reselect"), ("slot", (long)slot)));
                    var again = ScanFavoritesJobHandler.RunOnFavorites(context, full with { Steps = [.. full.Steps.Take(makeAt)] }, ct);
                    tally.Add(again, round: false);
                    if (again.Status != ProcedureStatus.Succeeded)
                    {
                        return End(context, tally, JobKit.FromProcedure(again, "round."));
                    }

                    if (VerifySelected(context, tipData!, expectedId!.Value, slot, ct, settled: true) is { } still)
                    {
                        return End(context, tally, still);
                    }
                }

                if (typed)
                {
                    var typedRun = context.Procedures.Run(BuildQty(qtyData!, Math.Min(n, SessionCap))!, context.Run, ct);
                    if (typedRun.Status != ProcedureStatus.Succeeded)
                    {
                        return End(context, tally, typedRun.Status is ProcedureStatus.Halted or ProcedureStatus.Stopped or ProcedureStatus.Cancelled or ProcedureStatus.GameExited or ProcedureStatus.Deferred
                            ? JobKit.FromProcedure(typedRun, "qty.")
                            : JobKit.Failed("qty.notTyped"));
                    }
                }
            }

            var before = context.UiState;
            var run = context.Procedures.Run(round, context.Run, ct);
            tally.Add(run);
            long produced = 0;
            if (run.Status == ProcedureStatus.Succeeded)
            {
                var session = context.UiState.Craft;
                if (CraftFlow.StartedAfter(session, before))
                {
                    if (session.StationUi is null)
                    {
                        JournalRound(context, tally, 0, run);
                        return End(context, tally, JobKit.Halted(HaltCode.BlockedCraftSpot));
                    }

                    produced = session.Ticks;
                }
            }
            else if (!NoCraftStarted(run))
            {
                // the press started a craft before the round ended (the user took over right after Make): the game crafts
                // it anyway; wait for the session to end and count it, so the goal loop does not make it again (2026-10-16
                // safety review)
                if (CraftFlow.StartedAfter(context.UiState.Craft, before) && WaitSessionEnd(context, ct) is { Ticks: > 0 } ended)
                {
                    tally.Total += ended.Ticks;
                    ApplyCraft(context, expectedId, ended.Ticks, context.UiState.AsOf);
                    JournalRound(context, tally, ended.Ticks, run);
                    return End(context, tally, JobKit.FromProcedure(run, "round."));
                }

                JournalRound(context, tally, 0, run);
                return End(context, tally, JobKit.FromProcedure(run, "round."));
            }

            tally.Total += produced;
            JournalRound(context, tally, produced, run);
            if (produced > 0)
            {
                // materials were used and items made: the counts change by the craft (audit R2.2)
                ApplyCraft(context, expectedId, produced, context.UiState.AsOf);
            }

            if (produced == 0 && NoCraftStarted(run))
            {
                // incident 26 (2026-10-26): a Make press with no effect brought up a popup the log does not describe (it
                // dimmed the view, and the favourites could not be read after it): the log's top and one screenshot are
                // kept as evidence
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(
                    ("event", "craftLoop.noEffect"), ("top", Codes.Safe(context.UiState.AdvisoryTop ?? "-")), ("stack", Codes.Safe(string.Join("+", context.UiState.AdvisoryStack)))));
                Snapshot(context, ct);
            }

            if (produced == 0)
            {
                // No craft started or nothing made: most likely out of materials. The end of an open-ended loop.
                var done = count == 0 && tally.Total > 0;
                var code = tally.Total > 0 ? "loop.materialsOut" : "loop.noCraft";
                return End(context, tally, JobKit.Result(done ? JobState.Succeeded : JobState.Failed, code, MissingMaterial(context, before, expectedId)));
            }

            if (count > 0 && tally.Total >= count)
            {
                return End(context, tally, JobKit.Result(JobState.Succeeded, "loop.countReached"));
            }
        }
    }

    /// <summary>
    /// The round for these parameters: the slot step of <paramref name="slot"/> only (none for 0), the max step only for
    /// a max batch, the typed-quantity steps with the digits of <paramref name="typed"/> only for a typed batch (a repeated
    /// digit gets its own step id), the await bound set from the batch. Null when the data procedure is not the expected
    /// shape.
    /// </summary>
    internal static ProcedureDefinition? BuildRound(ProcedureDefinition data, int slot, bool max, bool keepSelection = false, int typed = 0)
    {
        if (data.Kind != ProcedureKind.Commit || data.Gate != AutomationSwitch.Crafting
            || data.Steps.Count(s => s.Flags.HasFlag(StepFlags.Commit)) != 1
            || data.Steps.Count(s => s.Action is AwaitCraftAction) != 1
            || !data.Steps.Any(s => s.Id == MakeStep && s.Flags.HasFlag(StepFlags.Commit) && s.Action is ClickAction { Offset: null }))
        {
            return null;
        }

        var slotStep = slot > 0 ? SlotStepId(slot) : null;
        if (slotStep is not null && !data.Steps.Any(s => s.Id == slotStep && s.Action is ClickAction { Offset: not null }))
        {
            return null;
        }

        if (max && !data.Steps.Any(s => s.Id == MaxStep && s.Action is ClickAction))
        {
            return null;
        }


        var steps = new List<StepDefinition>();
        foreach (var s in data.Steps)
        {
            if (keepSelection && s.Id == OpenStep)
            {
                continue; // slot 0 after a verified selection: a reopen would select another recipe
            }

            if (s.Id.StartsWith(SlotStepPrefix, StringComparison.Ordinal))
            {
                if (s.Id == slotStep)
                {
                    steps.Add(s);
                }

                continue;
            }

            if (s.Id == MaxStep)
            {
                if (max)
                {
                    steps.Add(s);
                }

                continue;
            }

            steps.Add(s.Action is AwaitCraftAction ? s with { Action = new AwaitCraftAction(max ? MaxBatchTicks : typed > 0 ? typed : 1) } : s);
        }

        return data with { Steps = steps };
    }

    /// <summary>
    /// Reads the selected recipe's product tip (a selection-verifying run: the engine's commit window starts at it) and
    /// compares it with <paramref name="expected"/>. Null when it is the expected product; else the job's end: the tip
    /// run's own result when it failed (a deferral stays a deferral), "slotItem.changed" for a favourite slot (the goal
    /// loop then finds the card in the list), "selectedItem.changed" for the kept selection. <paramref name="settled"/>:
    /// the second look after selecting the slot again; another dish then makes the slot's stored recipe give it up.
    /// </summary>
    private static JobResult? VerifySelected(IJobContext context, ProcedureDefinition tipData, long expected, int slot, CancellationToken ct, bool settled = false)
    {
        // the tip can only be read on the Make view (live 2026-10-16: 24 tip runs after a gathering trip or a popup failed
        // their precondition and filled the soft-end ledger towards a HALT): without it the round ends "loop.viewLost"
        // and the goal loop selects the recipe again
        if (context.UiState.AdvisoryTop != "UI_LIFESKILL_MAKE_VIEW")
        {
            JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "craftLoop.viewLost"), ("top", context.UiState.AdvisoryTop)));
            return JobKit.Failed("loop.viewLost");
        }

        var tip = ProductTip.Read(context, tipData, ct);
        if (!tip.Succeeded)
        {
            return JobKit.FromProcedure(tip.Run, "tip.");
        }

        if (tip.Tip?.ItemId == expected || SameDish(context, tip.Tip?.ItemName, expected))
        {
            return null;
        }

        JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "craftLoop.selectionChanged"), ("slot", (long)slot), ("expected", expected), ("seen", tip.Tip?.ItemId)));
        if (settled && slot > 0 && tip.Tip is { } seen && context.Knowledge is { } knowledge
            && ScreenRecipes.ReleaseSlot(knowledge, slot, seen.ItemId, ItemKey.FromDisplayName(seen.ItemName)) is { } released)
        {
            // incident 29 (2026-10-28): slot 4 held Longevity Sashimi for days while the store still gave it to Sashimi;
            // every goal clicked it, read the wrong dish twice and walked the list
            JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "favorites.slotReleased"), ("slot", (long)slot), ("recipe", Codes.Safe(released)), ("seen", seen.ItemId)));
        }

        return JobKit.Refused(slot > 0 ? SlotChanged : "selectedItem.changed");
    }

    /// <summary>Waits (at most 10 minutes) for a running craft session to end; the ended session, or null.</summary>
    private static CraftSessionInfo? WaitSessionEnd(IJobContext context, CancellationToken ct)
    {
        var until = context.Clock.Monotonic + TimeSpan.FromMinutes(10);
        while (context.UiState.Craft.InProgress && context.Clock.Monotonic < until)
        {
            context.Clock.Sleep(TimeSpan.FromSeconds(1), ct);
        }

        return context.UiState.Craft.InProgress ? null : context.UiState.Craft;
    }

    /// <summary>
    /// The same dish at another quality grade (live 2026-10-16: Salad Savory's recipe stored 1064010, the selected
    /// product's tip logged 1064011; 35 rounds were refused as a moved slot): the tip's logged name is the name of the
    /// recipe product (or learned picture) with the expected id.
    /// </summary>
    internal static bool SameDish(IJobContext context, string? seenName, long expected)
    {
        if (context.Knowledge is not { } knowledge || ItemKey.FromDisplayName(seenName ?? string.Empty) is not { Value.Length: > 0 } seen)
        {
            return false;
        }

        return knowledge.GetRecipes().Any(r => r.ProductItemId == expected && r.Product == seen)
            || knowledge.GetIconItems().Any(i => i.ItemId == expected && i.Item == seen);
    }

    /// <summary>
    /// Between two typed batches (live 2026-10-16): a dish's "Improved" appraisal popup (or another Esc-safe popup) shows
    /// when a batch ends; one Esc closes it and the Make view under it comes back. A recipe kept selected in the game
    /// (slot 0) cannot be selected again here: when the Make view is gone the loop ends "loop.viewLost" (no halt; the
    /// Craft plan finds the card again). A favourite is selected again by its round. Null to go on.
    /// </summary>
    private static JobResult? AfterBatch(IJobContext context, ProcedureDefinition qtyData, bool keepSelection, CancellationToken ct)
    {
        const string MakeView = "UI_LIFESKILL_MAKE_VIEW";
        // incident 16 (2026-10-23): a batch can wear the cooking gear out; its notice covers the Make view (no Esc closes
        // it) and is answered before the next batch's clicks
        if (Gathering.DismissGearNotice(context, ct) is { Status: ProcedureStatus.Halted or ProcedureStatus.Stopped or ProcedureStatus.Cancelled or ProcedureStatus.GameExited or ProcedureStatus.Deferred } notice)
        {
            return JobKit.FromProcedure(notice, "notice.");
        }

        for (var i = 0; i < 2; i++)
        {
            var top = context.UiState.AdvisoryTop;
            if (top is null || top == MakeView || !Companion.Contracts.Safety.UiCatalog.EscSafe.Contains(top))
            {
                break;
            }

            JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "craftLoop.popupClosed"), ("ui", top)));
            var esc = context.Procedures.Run(qtyData with { Steps = [.. qtyData.Steps.Where(s => s.Id == "esc")] }, context.Run, ct);
            if (esc.Status is ProcedureStatus.Halted or ProcedureStatus.Stopped or ProcedureStatus.Cancelled or ProcedureStatus.GameExited or ProcedureStatus.Deferred)
            {
                return JobKit.FromProcedure(esc, "round.");
            }

            context.Clock.Sleep(TimeSpan.FromMilliseconds(600), ct);
        }

        return keepSelection && context.UiState.AdvisoryTop != MakeView ? JobKit.Failed("loop.viewLost") : null;
    }

    /// <summary>
    /// The typing run of <paramref name="typed"/> from the data procedure <see cref="QtyProcedure"/>: the quantity press,
    /// the keypad's clear, its digits (a repeated digit gets its own step id), OK and the settle. Null when the data
    /// procedure is not the expected shape (a navigation without gate, every step present).
    /// </summary>
    internal static ProcedureDefinition? BuildQty(ProcedureDefinition data, int typed)
    {
        var byId = data.Steps.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        if (data.Kind != ProcedureKind.Navigation || data.Gate != AutomationSwitch.None || typed < 1
            || TypedSteps.Any(id => !byId.ContainsKey(id))
            || Enumerable.Range(0, 10).Any(d => !byId.ContainsKey("key." + d.ToString(CultureInfo.InvariantCulture))))
        {
            return null;
        }

        return data with { Steps = [.. TypedRound(byId, typed)] };
    }

    /// <summary>The quantity press, the keypad's clear, the digits of <paramref name="typed"/>, OK and the settle.</summary>
    private static IEnumerable<StepDefinition> TypedRound(Dictionary<string, StepDefinition> byId, int typed)
    {
        yield return byId["qty"];
        yield return byId["qty.settle"];
        yield return byId["key.clear"];
        var seen = new Dictionary<char, int>();
        foreach (var d in typed.ToString(CultureInfo.InvariantCulture))
        {
            var n = seen[d] = seen.GetValueOrDefault(d) + 1;
            var key = byId["key." + d];
            yield return n == 1 ? key : key with { Id = key.Id + "-" + n.ToString(CultureInfo.InvariantCulture) };
        }

        yield return byId["key.ok"];
        yield return byId["qty.done"];
    }

    /// <summary>Step id of a favourites slot ("slot.01" .. "slot.10").</summary>
    internal static string SlotStepId(int slot) => SlotStepPrefix + slot.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>The recipe the latest favourites scan stored for a slot (source "screen:craft.fav/&lt;slot&gt;"), or null.</summary>
    internal static Recipe? SlotRecipe(ICharacterKnowledge knowledge, int slot)
    {
        var source = ScreenRecipes.FavoritesSourcePrefix + slot.ToString(CultureInfo.InvariantCulture);
        return knowledge.GetRecipes()
            .Where(r => string.Equals(r.SourceCapture, source, StringComparison.Ordinal))
            .OrderByDescending(r => r.ObservedAtUtc)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>The diagnostics capture of the Make view (incident 26); at most one per <see cref="SnapshotEvery"/>.</summary>
    internal const string SnapshotProcedure = "crafting.makeSnapshot";

    /// <summary>The least time between two diagnostics captures (a goal ends its loop with "no effect" every round).</summary>
    internal static readonly TimeSpan SnapshotEvery = TimeSpan.FromMinutes(30);

    private static TimeSpan? _lastSnapshot;

    /// <summary>One screenshot of the Make view as it is now (capture only), at most one per 30 minutes.</summary>
    private static void Snapshot(IJobContext context, CancellationToken ct)
    {
        var now = context.Clock.Monotonic;
        if ((_lastSnapshot is { } last && now - last < SnapshotEvery && now >= last) || context.ProcedureCatalog.Get(SnapshotProcedure) is not { } shot)
        {
            return;
        }

        _lastSnapshot = now;
        var run = context.Procedures.Run(shot, context.Run, ct);
        JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "craftLoop.snapshot"), ("status", run.Status.ToString()), ("detail", run.EndDetail)));
    }

    /// <summary>
    /// True when the round ended because pressing Make registered but started no craft (no station UI): the engine
    /// ends a commit step with no effect as Cancelled "commit.noEffect" and never presses it again.
    /// </summary>
    internal static bool NoCraftStarted(ProcedureResult run) =>
        run.Status == ProcedureStatus.Cancelled
        && run.Attempts.Count > 0
        && run.Attempts[^1].StepId == MakeStep
        && run.Attempts[^1].DetailCode == "commit.noEffect";

    private static bool TryReadParams(JobParameters parameters, out int slot, out long count, out bool max, out bool typed, out long? item, out string? bad)
    {
        slot = 0;
        count = 0;
        max = false;
        typed = false;
        item = null;
        bad = null;
        var values = parameters.Values;
        if (values.TryGetValue("slot", out var s))
        {
            if (s is not long sl || sl < 0 || sl > MaxSlot)
            {
                bad = "param.range:slot";
                return false;
            }

            slot = (int)sl;
        }

        if (!values.TryGetValue("count", out var c) || c is not long n)
        {
            bad = "param.missing:count";
            return false;
        }

        if (n is < 0 or > MaxCount)
        {
            bad = "param.range:count";
            return false;
        }

        count = n;
        if (values.TryGetValue("batch", out var b))
        {
            if (b is not string batch || (batch != BatchOne && batch != BatchMax && batch != BatchTyped))
            {
                bad = "param.enum:batch";
                return false;
            }

            max = batch == BatchMax;
            typed = batch == BatchTyped;
        }

        if (values.TryGetValue("item", out var i))
        {
            if (i is not long id || id < 1 || id > MaxItemId)
            {
                bad = "param.range:item";
                return false;
            }

            item = id;
        }

        return true;
    }

    private static void PauseBetweenRounds(IJobContext context, Tally tally, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            context.Clock.Sleep(RoundPause, ct);
        }
        catch (OperationCanceledException)
        {
            // The feature host ends the job Cancelled (stop or client cancel); the totals stay in the journal.
            JobKit.Journal(context, JournalKind.Stock, Procedure, With(tally.Data(), ("event", "craftLoop.end"), ("code", "cancelled")));
            throw;
        }
    }

    private static void JournalRound(IJobContext context, Tally tally, long produced, ProcedureResult run) =>
        JobKit.Journal(context, JournalKind.Stock, Procedure, With(
            tally.Data(),
            ("event", "craftLoop.round"),
            ("round", (long)tally.Rounds),
            ("produced", produced),
            ("status", run.Status.ToString())));

    /// <summary>
    /// The material the game named when Make started nothing (live 2026-10-15: Make opens UI_ITEM_TIPS_PASS_TOUCH and the
    /// log names the missing item, e.g. "Water 20330037"): reported as "missingItemId" / "missingItem" and recorded as
    /// short, so the next plan makes or gathers it. Null without such a tip.
    /// </summary>
    /// <remarks>
    /// Audit R2.2 (rule 2): the game only says the bag holds less than one craft needs, not 0. With the recipe's amount
    /// per craft known, it is stored as the range 0 .. amount-1 (an exact 0 when one is needed); without it, nothing is
    /// stored (the named material is still reported, and the goal loop treats it as missing).
    /// </remarks>
    internal static IReadOnlyDictionary<string, object?>? MissingMaterial(IJobContext context, UiStateSnapshot before, long? productItemId = null)
    {
        var ui = context.UiState;
        if (ui.LastItemTip is not { ItemId: > 0 } tip || !tip.LoggedAfter(before.AsOf) || tip == before.LastItemTip)
        {
            return null;
        }

        var item = ItemKey.FromDisplayName(tip.ItemName ?? string.Empty);
        if (item.Value.Length > 0 && ui.AsOf is { } at && context.Knowledge is { } knowledge
            && RecipeOf(knowledge, productItemId)?.Materials.FirstOrDefault(m => m.Item == item || m.ItemId == tip.ItemId) is { Qty: > 0 } per)
        {
            var shortObs = per.Qty <= 1
                ? new StockObservation(item, 0, null, ObservationKind.CraftViewPair, context.Clock.UtcNow, at, 0.6, "craft.missing", tip.ItemId)
                : new StockObservation(item, null, new ValueRange(0, per.Qty - 1), ObservationKind.CraftViewPair, context.Clock.UtcNow, at, 0.6, "craft.missing", tip.ItemId);
            knowledge.ApplyObservations([shortObs]);
        }

        JobKit.Journal(context, JournalKind.Stock, Procedure, JobKit.Data(("event", "craftLoop.missing"), ("itemId", tip.ItemId)));
        return JobKit.Data(("missingItemId", tip.ItemId), ("missingItem", item.Value));
    }

    /// <summary>The recipe whose product has <paramref name="productItemId"/> (the latest observed), or null.</summary>
    private static Recipe? RecipeOf(ICharacterKnowledge knowledge, long? productItemId) =>
        productItemId is { } id
            ? knowledge.GetRecipes().Where(r => r.ProductItemId == id).OrderByDescending(r => r.ObservedAtUtc).FirstOrDefault()
            : null;

    /// <summary>
    /// Audit R2.2 (rule 3): after a round that made items, the stock changes by the craft instead of every count being
    /// marked outdated (the next round planned from the pre-craft counts and made a sub-product again, live 2026-10-21).
    /// The product gains made x output, each material loses amount x made, at the craft's log position. A count that
    /// already includes the craft (observed after it) is left alone; one the change cannot be applied to (no exact count,
    /// or not orderable against the log) is marked outdated alone; a success-rate recipe marks only its product. Without
    /// the recipe or a log position, every count is marked outdated as before.
    /// </summary>
    internal static void ApplyCraft(IJobContext context, long? productItemId, long produced, LogPosition? at)
    {
        if (context.Knowledge is not { } knowledge || produced <= 0)
        {
            return;
        }

        var now = context.Clock.UtcNow;
        var recipe = RecipeOf(knowledge, productItemId);
        if (recipe is null || at is null || recipe.OutputQty < 1)
        {
            knowledge.MarkStale(StaleScope.AllStock, null, "craft.loop", now);
            return;
        }

        var stock = knowledge.GetStock().Items;
        void Apply(ItemKey item, long delta, string cause)
        {
            if (stock.TryGetValue(item, out var e) && e.ObservedAtLog is { } seen && seen.SameFile(at) && seen.Offset >= at.Offset)
            {
                return; // observed after the craft: the count already includes it
            }

            if (!knowledge.ApplyDerived(new DerivedStockDelta(item, delta, at, cause)))
            {
                knowledge.MarkStale(StaleScope.Item, item, cause + ".skipped", now);
            }
        }

        if (recipe.SuccessRate is not null)
        {
            knowledge.MarkStale(StaleScope.Item, recipe.Product, "craft.successRate", now);
        }
        else
        {
            Apply(recipe.Product, produced * recipe.OutputQty, "craft.tick");
        }

        foreach (var m in recipe.Materials.Where(m => m.Qty > 0))
        {
            Apply(m.Item, -m.Qty * produced, "craft.consumed");
        }

        knowledge.Save();
    }

    private static JobResult End(IJobContext context, Tally tally, JobResult result)
    {
        var data = tally.Data();
        if (result.Data is not null)
        {
            foreach (var (k, v) in result.Data)
            {
                data.TryAdd(k, v);
            }
        }

        JobKit.Journal(context, JournalKind.Stock, Procedure, With(tally.Data(), ("event", "craftLoop.end"), ("code", result.Code)));
        return result with { Data = data };
    }

    private static Dictionary<string, object?> With(Dictionary<string, object?> data, params (string Key, object? Value)[] extra)
    {
        foreach (var (k, v) in extra)
        {
            data[k] = v;
        }

        return data;
    }

    /// <summary>Totals of one job (codes and counts only).</summary>
    private sealed class Tally(int slot, long count, string batch, long? item)
    {
        public int Rounds { get; private set; }

        public long Total { get; set; }

        private int Inputs { get; set; }

        private int Clicks { get; set; }

        private int Esc { get; set; }

        public void Add(ProcedureResult r, bool round = true)
        {
            Rounds += round ? 1 : 0;
            Inputs += r.InputBatches;
            Clicks += r.Clicks;
            Esc += r.EscPresses;
        }

        public Dictionary<string, object?> Data() => JobKit.Data(
            ("rounds", (long)Rounds),
            ("total", Total),
            ("count", count),
            ("slot", (long)slot),
            ("batch", batch),
            ("itemId", item),
            ("inputs", (long)Inputs),
            ("clicks", (long)Clicks),
            ("esc", (long)Esc));
    }
}
