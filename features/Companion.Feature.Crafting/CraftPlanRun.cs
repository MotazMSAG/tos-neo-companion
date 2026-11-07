using System.Globalization;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Planning;
using Companion.Features.Kit;

namespace Companion.Feature.Crafting;

/// <summary>
/// The Craft job's run (2026-10-15): "ask for N, get N". Rounds of plan (the pure planner: sub-recipes first, log-named
/// recipes trusted), gather (the raw materials the plan lacks, through the shared <see cref="Gathering"/> module) and
/// craft (the proven loop <see cref="CraftLoopJobHandler"/> on the recipe's My Favorites slot, or after the list walk
/// selected its card). Fallbacks, never a halt for them (the project motto): a material Make names as missing is
/// recorded and made or gathered next round; a material the Gathering Helper does not list is reported; a recipe that
/// cannot be found is skipped and reported. A real halt or stop ends the job. The result lists what was made, gathered,
/// skipped and still missing, in plain codes.
/// </summary>
internal static class CraftPlanRun
{
    internal const string Procedure = "crafting.plan";
    private const string FavoritePrefix = "screen:craft.fav/";

    /// <summary>
    /// The count each round plans for in forever mode (qty 0, 2026-10-16: the user, "craft forever while I sleep"): the
    /// loop plans this many, gathers, crafts, and plans again until stopped or blocked.
    /// </summary>
    internal const int ForeverChunk = 50; // one game session (50); 100 needed more than a stock limit holds (live 2026-10-16: 600 grapes typed, the game set 1 and the helper paused)

    /// <summary>
    /// Rounds in a row that gathered but made nothing before the round crafts anyway (the runaway guard that replaced the
    /// old 30-round cap: a stock reading that does not move after a gathering trip would otherwise gather forever).
    /// </summary>
    internal const int MaxGatherOnlyRounds = 5;

    /// <summary>Rounds in a row that made, gathered and learned nothing in forever mode, retried after a pause, at most.</summary>
    internal const int MaxIdleRounds = 3;

    /// <summary>
    /// Rounds that made nothing because a press was deferred (FALLBACKS.md: unreachable from where the cursor rested, or
    /// its recovery ran out) and are tried again after a pause, at most; the pauses double from
    /// <see cref="DeferPauseStart"/> up to <see cref="DeferPauseMax"/> (about 25 minutes in all).
    /// </summary>
    internal const int MaxDeferrals = 8;

    /// <summary>
    /// Deferred rounds end the goal after this long without progress (the goal made more, or a trip gathered), whatever
    /// the deferral count (audit R2.3).
    /// </summary>
    internal static readonly TimeSpan DeferralWindow = TimeSpan.FromMinutes(30);

    internal static readonly TimeSpan DeferPauseStart = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DeferPauseMax = TimeSpan.FromMinutes(5);

    /// <summary>Quiet deferrals (one run that pressed nothing: the screen its step needs was not showing) a goal waits through.</summary>
    internal const int MaxQuietRounds = 2;

    /// <summary>The user's message when a goal waits because the screen it needs is not showing (2026-10-31 audit).</summary>
    internal const string QuietText = "A crafting goal is waiting: the Make window does not show what it needs (another window is in front of it, or My Favorites is out of sight). Close the other window or scroll the Make list to the top; the goal tries again in a few minutes and ends after two more tries.";

    /// <summary>
    /// Makes <paramref name="qty"/> of <paramref name="item"/> (2026-10-15, "ask for 150, get 150"): rounds of plan
    /// (what is still missing), gather (the raw materials the plan lacks, through the Gathering module), craft (sub-recipes
    /// first), until the goal is made. Make is the final truth: a material it names as missing is recorded as 0, so the
    /// next round crafts it (it has a recipe) or gathers it. Ends when the goal is reached, or when a round makes and
    /// gathers nothing (the report says what is missing and why); a real halt or stop ends at once.
    /// </summary>
    internal static JobResult Run(IJobContext context, ItemKey item, int qty, CancellationToken ct)
    {
        var report = new Report();
        var refreshed = false;
        var deferrals = 0;
        var idle = 0;
        var gatherOnly = 0;
        string? gatherFailure = null;
        var gatherFailures = 0;
        string? exit = null;
        string? lastFingerprint = null;
        var lastRoundStalled = false;
        var lastProgress = context.Clock.Monotonic;
        var toldOutOfReach = false;
        var quietRounds = 0;
        var forever = qty == 0;
        int Want() => forever ? ForeverChunk : (int)(qty - report.GoalMade);
        CraftPlan? plan = null;

        // after a round that could not do its work now (a press deferred, the stock not readable): a pause, then plan
        // again. False when the goal must end instead (exit says why): the engine's soft-end allowance (audit R1.10), the
        // deferral count, or 30 minutes without progress (audit R2.3: rounds with 10-minute waits made 8 deferrals last
        // 73 minutes)
        bool PauseForDeferral(int round)
        {
            var now = context.Clock.Monotonic;
            // 2026-10-31 (the queue's second goal ended after 37 s on two runs that pressed nothing: the favourites header
            // was not on the game): a deferral that pressed nothing waits like an out-of-reach one, each retry paced clear of
            // the engine's soft-end allowance (as Gather everything does); one that pressed still ends at its second
            var quiet = report.LastQuiet;
            var ledger = JobKit.Ledger(context, report.Exhausted, now);
            if (quiet && ++quietRounds > MaxQuietRounds)
            {
                // 2026-10-31 audit: a quiet retry presses nothing, so the screen never changes by itself; after the user has
                // been told (below) and two tries, the goal ends and the queue moves on
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "plan.quietGiveUp"), ("round", (long)round), ("cause", report.LastCause)));
                exit = "deferrals.quiet";
                return false;
            }

            if (quiet && quietRounds == 1)
            {
                context.Notifier.Notify(new Companion.Contracts.Host.Notification(Companion.Contracts.Host.NotificationKind.NeedsUser, "plan.screenHidden", QuietText));
            }

            if (JobKit.SoftEndsNearLimit(ledger, now) && !quiet)
            {
                // audit R1.10 (rule 6): the engine HALTs on its 4th exhausted recovery in 30 min, and this loop's own
                // retries pushed it there (live 2026-10-16: 4 rounds of the same failed gather finish, then a HALT)
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "plan.exhausted"), ("round", (long)round), ("cause", report.LastCause)));
                exit = "exhausted";
                return false;
            }

            // incident 27 (2026-10-27): only the mouse's resting place kept the last 17 crafts from happening; such pauses
            // do not use up the deferral count and may go on for 3 hours (they never count as engine soft ends)
            var outOfReach = JobKit.OutOfReach(report.LastCause);
            var patient = outOfReach || quiet;
            if ((deferrals >= MaxDeferrals && !patient) || now - lastProgress >= (patient ? JobKit.OutOfReachWindow : DeferralWindow))
            {
                exit = deferrals >= MaxDeferrals && !patient ? "deferrals.exhausted" : "deferrals.timeLimit";
                return false;
            }

            var pause = outOfReach ? DeferPauseMax : quiet ? JobKit.ClearOfSoftEnds(DeferPauseMax, ledger, now) : JobKit.ClearOfSoftEnds(DeferPause(deferrals++), ledger, now);
            toldOutOfReach |= JobKit.TellOutOfReach(context, report.LastCause, toldOutOfReach);
            JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(
                ("event", "plan.deferred"), ("round", (long)round), ("deferrals", (long)deferrals), ("pauseS", (long)pause.TotalSeconds),
                ("cause", report.LastCause), ("quiet", quiet)));
            context.ReportActivity("step", "plan.pause", JobKit.Data(("cause", report.LastCause), ("untilUtc", context.Clock.UtcNow + pause)));
            context.Clock.Sleep(pause, ct);
            context.ReportActivity("step", null);
            return true;
        }

        for (var round = 1; forever || report.GoalMade < qty; round++)
        {
            ct.ThrowIfCancellationRequested();
            report.RoundDeferred = false;
            context.ReportActivity("job", "plan", JobKit.Data(("item", Codes.Safe(item.Value)), ("made", report.GoalMade), ("qty", (long)qty), ("round", (long)round)));

            // incident 16 (2026-10-23): crafting wears the gear out too, and its notice covers the Make view; it is looked
            // for before the Make-tab read (which clicks under it), not only before a craft step
            if (Gathering.DismissGearNotice(context, ct) is { } roundNotice && roundNotice.Status != ProcedureStatus.Succeeded)
            {
                var end = JobKit.FromProcedure(roundNotice, "notice.");
                if (roundNotice.Status is ProcedureStatus.Halted or ProcedureStatus.Stopped or ProcedureStatus.Cancelled)
                {
                    return Finish(context, plan, report, "plan.stopped", "stopped") with { State = end.State, Code = end.Code };
                }

                // the notice is still up: the Make-tab read would click under it (safety review 2026-10-23); wait instead
                report.Defer("notice", end.Code, context.Clock.Monotonic);
                if (PauseForDeferral(round))
                {
                    continue;
                }

                break;
            }

            // fresh owned counts from the Make tab for the goal and its favourite sub-recipes before planning (2026-10-23:
            // a day-old "0 Thick Fish Soup" made a Scallion Fish Cake goal gather Water while 157 soups were in the bag)
            var (_, stockStop, stockDeferred, stockSoftEnds, stockQuiet) = MakeTabStock.Refresh(context, MakeTabStock.SlotsFor(item, context.Knowledge!.GetRecipes()), ct);
            if (stockStop is { } stockEnd)
            {
                return Finish(context, plan, report, "plan.stopped", "stopped") with { State = stockEnd.State, Code = stockEnd.Code };
            }

            if (stockDeferred is { } notFresh)
            {
                // audit R2.1 (rule 3): a refresh that could not be done now left the old counts, and planning from them is
                // what incident 11 did; the round waits like any deferral instead
                report.Defer("stock", notFresh, context.Clock.Monotonic, stockSoftEnds, stockQuiet);
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "plan.stockNotFresh"), ("round", (long)round), ("cause", notFresh)));
                if (PauseForDeferral(round))
                {
                    continue;
                }

                break;
            }

            var (p, recipes) = MakePlan(context, item, Want());
            plan = p;
            if (!refreshed && plan.Issues.Any(i => i.Kind == PlanIssueKind.NeedsVerification))
            {
                // fallback: a partly read recipe is re-read once from My Favorites (live 2026-10-15: Aquatic Feast was
                // stored partial before the real digit glyphs), then planned again
                refreshed = true;
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "plan.refresh"), ("via", "ScanFavorites")));
                var scan = new ScanFavoritesJobHandler().Execute(context, new JobParameters(new Dictionary<string, object>()), ct);
                if (scan.State is JobState.Halted or JobState.Stopped or JobState.Cancelled)
                {
                    return scan;
                }

                if (IsDeferred(scan))
                {
                    refreshed = false; // the re-read could not be done now: tried again after the pause (2026-10-16 review)
                    report.Defer("refresh", scan.Code, context.Clock.Monotonic);
                }

                (plan, recipes) = MakePlan(context, item, Want());
            }

            JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(
                ("event", "plan.made"),
                ("round", (long)round),
                ("crafts", (long)plan.Crafts.Count),
                ("shortfalls", (long)plan.Shortfalls.Count),
                ("issues", string.Join(",", plan.Issues.Select(i => i.Kind.ToString()).Distinct())),
                ("committable", plan.Committable)));

            // audit R2.3 (rule 6, "same plan, no progress, twice"): a round that made none of the goal and gathered nothing,
            // followed by the same plan again, ends the goal (live 2026-10-21: Archiberry Juice was made three rounds in a
            // row while Archi Cookie made 0, and each round reset the retry budgets)
            var fingerprint = Fingerprint(plan);
            if (lastRoundStalled && fingerprint == lastFingerprint)
            {
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(
                    ("event", "plan.noProgress"), ("round", (long)round), ("crafts", (long)plan.Crafts.Count), ("shortfalls", (long)plan.Shortfalls.Count)));
                exit = "noProgress";
                break;
            }

            lastFingerprint = fingerprint;
            lastRoundStalled = false; // set again at the end of a round that made no progress

            // incident 30 (2026-10-28): a material that can be neither gathered nor crafted caps the goal BEFORE the trip.
            // The cap below ran only after a round that gathered nothing, so Longevity Sashimi x100 went on gathering for
            // the whole goal (2 more trips, ~28 min) after Rosemary (a homestead good) had capped it at the 41 made. A cap at
            // what is made ends the goal now; a smaller one plans (and gathers) only for what it allows
            long? cappedTo = null;
            if (Feasible(context, item, plan, recipes, report) is { } early && early.Count < Want())
            {
                report.CappedBy = Needed(plan, early.By);
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(
                    ("event", "plan.capped"), ("count", early.Count), ("by", early.By), ("need", report.CappedBy), ("stage", "beforeGather")));
                if (early.Count <= 0)
                {
                    exit = "capped";
                    break;
                }

                cappedTo = early.Count;
                (plan, recipes) = MakePlan(context, item, (int)early.Count);
            }

            // 1. raw materials the plan lacks: gather them, then plan again with the gathered stock
            var targets = GatherTargets(context, plan, recipes)
                .Where(t => !report.NotGatherable.Contains(t.Name) && !report.AtLimit.Contains(t.Name) && !report.Filled.Contains(t.Name)).ToList();
            // collect fully first, then craft fully (the user, 2026-10-16: fewest trips and craft sessions; gathering time
            // depends on the item count, the same in any order); a material at its stock limit is gathered on the trip
            // after crafting used some of it
            var gatheredThisRound = 0;
            if (targets.Count > 0 && !report.GatheringBlocked)
            {
                var gathered = Gathering.Run(context, targets, ct);
                // audit R2.5 (rule 6): the same trip failure twice in a row (not started, a timeout, a failed finish...)
                // ends gathering for this job; only "not started" had a budget, the others repeated every round. A trip
                // held because the user was playing neither counts nor resets (audit R1.4)
                (gatherFailure, gatherFailures) = Gathering.NextFailure(gatherFailure, gatherFailures, gathered);
                if (gatherFailures >= 2 || gathered.Code == "gather.gearNoticeStorm")
                {
                    report.GatheringBlocked = true;
                    report.GatheringBlockedBy = gatherFailure;
                    JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "plan.gatheringBlocked"), ("round", (long)round), ("code", gatherFailure)));
                }

                JobKit.Journal(context, JournalKind.Stock, Procedure, JobKit.Data(("event", "plan.gather"), ("round", (long)round), ("code", gathered.Code),
                    ("gathered", (long)gathered.Gathered.Count), ("cause", gathered.Stop?.Code), ("stepCode", gathered.StepCode), ("walkIncomplete", gathered.WalkIncomplete)));
                if (gathered.Stop is { } stop && stop.State is JobState.Halted or JobState.Stopped or JobState.Cancelled)
                {
                    return Finish(context, plan, report, "plan.stopped", "stopped") with { State = stop.State, Code = stop.Code };
                }

                if (gathered.EngineDeferred || gathered.Code == Gathering.StillGathering)
                {
                    // craft what the stock allows, gather again later (incident 23: a trip still out comes back later)
                    report.Defer("gathering", gathered.Stop?.Code ?? gathered.Code, context.Clock.Monotonic);
                }
                else
                {
                    report.Deferred.Remove("gathering"); // a later walk went through
                }

                gatheredThisRound = gathered.Gathered.Count;
                report.Gathered.AddRange(gathered.Gathered.Select(t => t.Name + " x" + t.Qty.ToString(CultureInfo.InvariantCulture)));
                foreach (var g in gathered.Gathered)
                {
                    // audit R2.4 (rule 1): Make's "missing" was true before this trip; it is not the state after it
                    report.Named.Remove(g.Name);
                }

                if (!gathered.EngineDeferred)
                {
                    // a walk cut short by a deferred press did not see the whole list: "not found" proves nothing then
                    report.NotGatherable.UnionWith(gathered.NotFound.Select(t => t.Name));
                }

                // audit R2.4: "at its limit" (a greyed tile, a picker with no room) is evidence the helper cannot add any;
                // "filled to its limit by this trip" only means this trip took all it could (not gathered again until
                // crafting uses some), never "the helper refuses it"
                report.AtLimit.UnionWith(gathered.AtLimit.Select(t => t.Name));
                report.Filled.UnionWith(gathered.Filled.Select(t => t.Name));
                if (gatheredThisRound > 0 || gathered.TripCompleted)
                {
                    // incident 24 (2026-10-25): a 15-minute trip whose Finish the user's mouse kept out of reach counted as
                    // "no progress", and the goal gave up after 11 minutes of waiting for the mouse
                    lastProgress = context.Clock.Monotonic;
                }

                if (gatheredThisRound > 0 && ++gatherOnly <= MaxGatherOnlyRounds)
                {
                    continue;
                }
            }

            if (plan.Crafts.Count == 0)
            {
                exit = "noCrafts";
                break;
            }

            // a direct material of the goal that can be neither gathered nor crafted caps the goal (no sub-recipe is made
            // for crafts that cannot happen; live 2026-10-16: Peppercorn, not in the Gathering Helper, capped Clam Chowder);
            // after the trip only a cap lower than the one applied before it (one this round's trip just learned)
            if (Feasible(context, item, plan, recipes, report) is { } cap && cap.Count < (cappedTo ?? Want()))
            {
                report.CappedBy = Needed(plan, cap.By);
                if (cap.Count <= 0)
                {
                    exit = "capped";
                    break;
                }

                (plan, recipes) = MakePlan(context, item, (int)cap.Count);
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "plan.capped"), ("count", cap.Count), ("by", cap.By)));
            }

            // 2. the crafts in order (sub-recipes first)
            var (madeBefore, namedBefore, goalBefore) = (report.Made, report.Named.Count, report.GoalMade);
            if (Craft(context, item, plan, recipes, report, ct) is { } halted)
            {
                return halted;
            }

            if (report.ReplanRequested)
            {
                report.ReplanRequested = false; // a fresh count came in mid-round: plan again with it (audit R2.1)
                continue;
            }

            if (report.Made == madeBefore && report.Named.Count == namedBefore)
            {
                if (forever && !report.RoundDeferred && idle < MaxIdleRounds)
                {
                    // forever: a round without progress is paused and tried again a few times before the loop ends
                    var wait = DeferPause(idle++);
                    JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(
                        ("event", "plan.idle"), ("round", (long)round), ("idle", (long)idle), ("pauseS", (long)wait.TotalSeconds)));
                    context.ReportActivity("step", "plan.idle", JobKit.Data(("untilUtc", context.Clock.UtcNow + wait)));
                    context.Clock.Sleep(wait, ct);
                    context.ReportActivity("step", null);
                    continue;
                }

                if (report.RoundDeferred)
                {
                    // fallback (FALLBACKS.md): a press could not happen now (the cursor rested where no press reaches, or
                    // a recovery ran out): pause, then plan again from the stock instead of ending the goal
                    if (PauseForDeferral(round))
                    {
                        continue;
                    }

                    break;
                }

                exit = "nothingMade"; // nothing made and nothing new learned: another round would do the same
                break;
            }

            if (report.Made > madeBefore)
            {
                // crafting used materials: one at its stock limit may have room now: look again next round (a material
                // that is not in the Gathering Helper at all stays excluded). Only the materials this round's crafts use
                // (2026-10-31 audit: Golden Juice stayed full while Archiberry Juice was made, and three walks looked for it)
                var used = plan.Crafts.SelectMany(c => c.Consumes).Select(m => m.Item.Value).ToHashSet(StringComparer.Ordinal);
                report.AtLimit.RemoveWhere(used.Contains);
                report.Filled.RemoveWhere(used.Contains);
            }

            if (report.GoalMade > goalBefore)
            {
                // progress is the goal itself (audit R2.3: a sub-recipe made again every round counted as progress and
                // reset the budgets)
                (deferrals, idle, gatherOnly) = (0, 0, 0);
                lastProgress = context.Clock.Monotonic;
            }

            lastRoundStalled = !report.RoundDeferred && report.GoalMade == goalBefore && gatheredThisRound == 0;
        }

        // incident 16: the last batch can wear the gear out; its notice is dismissed (and reported) before the goal ends
        Gathering.DismissGearNotice(context, ct);
        context.ReportActivity("job", null);

        var code = !forever && report.GoalMade >= qty ? "plan.done"
            : exit == "exhausted" ? "plan.exhausted"
            : exit == "noProgress" ? "plan.noProgress"
            : plan is null ? "plan.blocked"
            : exit is "deferrals.exhausted" or "deferrals.timeLimit" || report.RoundDeferred ? "plan.deferred"
            : report.Made > 0 || report.Gathered.Count > 0 ? "plan.partial"
            : plan.Issues.Any(i => i.Kind == PlanIssueKind.NeedsVerification) ? "plan.needsVerification"
            : plan.Shortfalls.Count > 0 || report.Named.Count > 0 ? "plan.shortfall"
            : plan.Issues.Count > 0 ? "plan.blocked"
            : plan.Crafts.Count == 0 ? "plan.nothingToDo"
            : "plan.partial";
        return Finish(context, plan, report, code, !forever && report.GoalMade >= qty ? "done" : exit ?? "stopped");
    }

    private static string Fingerprint(CraftPlan plan) => JobKit.PlanFingerprint(plan);

    /// <summary>Runs one plan's crafts in order; a halt, stop or cancel of a step ends the job (returned), else null.</summary>
    private static JobResult? Craft(IJobContext context, ItemKey item, CraftPlan plan, IReadOnlyList<Recipe> recipes, Report report, CancellationToken ct)
    {
        foreach (var craft in Merged(plan.Crafts))
        {
            ct.ThrowIfCancellationRequested();

            // the gear durability notice covers the screen and no Esc closes it: dismissed for the day first (2026-10-18)
            if (Gathering.DismissGearNotice(context, ct) is { Status: ProcedureStatus.Halted or ProcedureStatus.Stopped or ProcedureStatus.Cancelled } noticeStop)
            {
                return Finish(context, plan, report, "plan.stopped", "stopped") with { State = JobKit.FromProcedure(noticeStop, "notice.").State, Code = JobKit.FromProcedure(noticeStop, "notice.").Code };
            }

            var recipe = recipes.FirstOrDefault(r => string.Equals(r.Id, craft.RecipeId, StringComparison.Ordinal));
            var slot = FavoriteSlot(recipe);
            long? foundId = null;
            if (slot is null && recipe?.ProductItemId is { } wanted)
            {
                // not a favourite: walk the Make list until its card is selected (ScanRecipeList find mode), then craft
                // the current recipe (slot 0)
                var intermediate = craft.Product != item;
                var wasFresh = FreshCount(context, craft.Product) is not null;
                var found = Find(context, wanted, ct);
                if (found.State is JobState.Halted or JobState.Stopped or JobState.Cancelled)
                {
                    return Finish(context, plan, report, "plan.stopped", "stopped") with { State = found.State, Code = found.Code };
                }

                if (found.State is JobState.GameExited or JobState.Parked or JobState.Refused or JobState.NeedsSurvey)
                {
                    return Finish(context, plan, report, "plan.stopped", "stopped") with { State = found.State, Code = found.Code };
                }

                slot = found.Code == "list.found" ? 0 : null;
                foundId = FoundId(found);
                // audit R1.6 (rule 2): only a walk that reached the list's end says "not found"; a capped walk, a drag that
                // never settled or a failed capture proves nothing: deferred like a deferral, tried again after the pause
                if (IsDeferred(found) || (slot is null && !ProvesAbsent(found)))
                {
                    report.Defer(craft.Product.Value, found.Code, context.Clock.Monotonic);
                    JournalStep(context, craft, found.Code, 0);
                    continue; // the next craft may be reachable; this one is tried again next round
                }

                if (slot is not null && intermediate && !wasFresh && FreshCount(context, craft.Product) is { } fresh && report.Replanned.Add(craft.Product.Value))
                {
                    // audit R2.1 (rule 3, incident 11): the plan counted this sub-product from an old or missing count; the
                    // card the walk just read gave a fresh one: plan again with it before crafting (once per product)
                    JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "plan.replan"), ("product", craft.Product.Value), ("owned", fresh)));
                    report.ReplanRequested = true;
                    return null;
                }
            }

            if (slot is null)
            {
                report.Skipped.Add(craft.Product.Value);
                JournalStep(context, craft, "recipe.notFound", 0);
                continue;
            }

            var values = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["slot"] = (long)slot.Value,
                ["count"] = (long)Math.Max(1, craft.Batches),
                ["batch"] = CraftLoopJobHandler.BatchTyped, // one Make press per batch (the game clamps the typed count)
            };
            // the id the game logged for the card the find selected wins: the same dish at another grade has another id
            // (live 2026-10-15: Salad Savory 1064010 became 1064011), and CraftLoop refuses a selected product that
            // differs from "item"
            if ((foundId ?? recipe?.ProductItemId) is { } id)
            {
                values["item"] = id;
            }

            var loop = new CraftLoopJobHandler().Execute(context, new JobParameters(values), ct);
            var stepMade = Total(loop);
            if (loop.Code == "slotItem.changed" && recipe?.ProductItemId is { } moved && slot > 0)
            {
                // audit R2.10 (incident 14): what the loop made before the slot check failed counts; the retry's result
                // replaced it (60 Scallion Fish Cakes made, the report said 0, and "at limit" was never cleared)
                Credit(report, item, craft, stepMade);
                var before = stepMade;

                // fallback: the favourite slot now holds another recipe (the latest scan saw it): find the card in the list
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "plan.slotMoved"), ("slot", (long)slot.Value), ("madeBefore", before)));
                var found = Find(context, moved, ct);
                if (found.State is JobState.Halted or JobState.Stopped or JobState.Cancelled)
                {
                    return Finish(context, plan, report, "plan.stopped", "stopped") with { State = found.State, Code = found.Code };
                }

                if (IsDeferred(found))
                {
                    report.Defer(craft.Product.Value, found.Code, context.Clock.Monotonic); // the find could not be done now: pause, plan again
                    JournalStep(context, craft, found.Code, before);
                    continue;
                }

                if (found.Code == "list.found")
                {
                    values["slot"] = 0L;
                    if (FoundId(found) is { } logged)
                    {
                        values["item"] = logged;
                    }

                    loop = new CraftLoopJobHandler().Execute(context, new JobParameters(values), ct);
                    var after = Total(loop);
                    Credit(report, item, craft, after);
                    stepMade = before + after;
                }
            }
            else
            {
                Credit(report, item, craft, stepMade);
            }

            JournalStep(context, craft, loop.Code, stepMade);
            if (loop.State is JobState.Halted or JobState.Stopped || (loop.State == JobState.Cancelled && ct.IsCancellationRequested))
            {
                // unsafe or stopped by the user: no fallback continues past a real halt
                return Finish(context, plan, report, "plan.stopped", "stopped") with { State = loop.State, Code = loop.Code };
            }

            if (loop.State == JobState.Cancelled)
            {
                // the engine ended the round itself (a Make press it could not confirm, the user took over during it):
                // not the user's stop (FALLBACKS.md): pause and plan again from the stock (live 2026-10-16: 61 made, then
                // one unconfirmed press ended the whole forever goal)
                report.Defer(craft.Product.Value, "engineEnded:" + loop.Code, context.Clock.Monotonic);
            }

            if (IsDeferred(loop))
            {
                report.Defer(craft.Product.Value, loop.Code, context.Clock.Monotonic);
            }

            if (loop.Data is { } ld && ld.TryGetValue("missingItem", out var m) && m is string missing && missing.Length > 0 && !report.Named.Contains(missing))
            {
                report.Named.Add(missing); // recorded by the loop as short: the next round makes or gathers it
            }

            if (stepMade < craft.Batches)
            {
                report.EndedEarly.Add(craft.Product.Value);
                break; // later steps need this one's output: plan again
            }
        }

        return null;
    }

    /// <summary>The crafts a CraftLoop result made (its data "total").</summary>
    private static long Total(JobResult loop) => loop.Data is { } d && d.TryGetValue("total", out var t) && t is long n ? n : 0;

    /// <summary>Adds crafts of one step to the report (and to the goal's count when the step is the goal).</summary>
    private static void Credit(Report report, ItemKey item, PlannedCraft craft, long made)
    {
        report.Made += made;
        if (made > 0)
        {
            report.Deferred.Remove(craft.Product.Value); // made after all: not "deferred" in the report (2026-10-16 review)
        }

        if (craft.Product == item)
        {
            report.GoalMade += made;
        }
    }

    /// <summary>
    /// How many of the goal its blocked direct materials allow: a material the plan lacks that has no recipe and that the
    /// Gathering Helper does not list allows owned / per-craft goals. Null when no such material limits the goal.
    /// </summary>
    private static (long Count, string By)? Feasible(IJobContext context, ItemKey item, CraftPlan plan, IReadOnlyList<Recipe> recipes, Report report)
    {
        var goal = recipes.FirstOrDefault(r => r.Product == item);
        if (goal is null || goal.OutputQty < 1)
        {
            return null;
        }

        var stock = context.Knowledge?.GetStock().Items;
        (long Count, string By)? best = null;
        foreach (var m in goal.Materials)
        {
            // only a material the Gathering Helper does not offer blocks; one Make named missing but gatherable (live
            // 2026-10-16 review: Wisdom Essence) is gathered next round instead. Also blocks (2026-10-21): one Make named
            // missing that gathering cannot supply in this job (the helper greys its tile, as "at the stock limit", while
            // the bag has none, e.g. a full Crab; or gathering does not start): Archi Cookie was pressed round after
            // round with 0 Scarlet Grape
            var raw = !recipes.Any(r => r.Product == m.Item);
            if (!raw && m.Qty >= 1 && SubRecipeCap(context, m.Item, recipes, stock, report) is { } sub)
            {
                // a sub-recipe blocked by a material that cannot be gathered (2026-10-23: Thick Fish Soup needs Rosemary,
                // a homestead good): the goal gets what the bag's sub-products plus that material allow
                var subCount = sub.Available / m.Qty * goal.OutputQty;
                if (best is null || subCount < best.Value.Count)
                {
                    best = (subCount, sub.By);
                }

                continue;
            }

            var namedMissing = report.Named.Contains(m.Item.Value);
            var blocked = raw
                && ((plan.Shortfalls.Any(s => s.Item == m.Item) && report.NotGatherable.Contains(m.Item.Value))
                    || (namedMissing && (report.AtLimit.Contains(m.Item.Value) || report.NotGatherable.Contains(m.Item.Value) || report.GatheringBlocked)));
            if (!blocked || m.Qty < 1)
            {
                continue;
            }

            if (namedMissing && report.AtLimit.Contains(m.Item.Value) && report.HelperRefused.Add(m.Item.Value))
            {
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "plan.helperRefuses"), ("item", m.Item.Value)));
            }

            // audit R1.8 (rules 2 and 3): a missing, range-only or outdated count is unknown, not 0. Make's own "missing"
            // in this job is the game's truth: fewer than one craft needs, so it allows no craft (nothing in this job adds
            // to a blocked material; a trip that gathers it clears the "missing", R2.4). Any other cap needs a fresh exact
            // count, else Make decides (it crafts, or names the material missing and the next round caps)
            long owned;
            if (namedMissing)
            {
                owned = 0;
            }
            else if (FreshCount(context, m.Item) is { } fresh)
            {
                owned = fresh;
            }
            else
            {
                CapUnknown(context, report, m.Item.Value);
                continue;
            }

            var count = owned / m.Qty * goal.OutputQty;
            if (best is null || count < best.Value.Count)
            {
                best = (count, m.Item.Value);
            }
        }

        return best;
    }

    private static long? FreshCount(IJobContext context, ItemKey item) => JobKit.FreshCount(context, item);

    private static void CapUnknown(IJobContext context, Report report, string item)
    {
        if (report.CapUnknown.Add(item))
        {
            JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "plan.capUnknown"), ("item", item)));
        }
    }

    /// <summary>
    /// The material that caps the goal with how many of it the plan still lacks ("rosemary x118", incident 30), or its name
    /// alone when the plan does not count it short (Make named it missing).
    /// </summary>
    private static string Needed(CraftPlan plan, string by) =>
        plan.Shortfalls.FirstOrDefault(f => f.Item.Value == by) is { Missing: > 0 } s
            ? by + " x" + s.Missing.ToString(CultureInfo.InvariantCulture)
            : by;

    /// <summary>
    /// How many of a sub-product the bag plus its craftable amount allow when one of its raw materials cannot be gathered
    /// in this job (not in the Gathering Helper, or Make named it missing and gathering cannot supply it), or null when
    /// none of its materials blocks.
    /// </summary>
    private static (long Available, string By)? SubRecipeCap(IJobContext context, ItemKey product, IReadOnlyList<Recipe> recipes, IReadOnlyDictionary<ItemKey, StockEntry>? stock, Report report)
    {
        var sub = recipes.FirstOrDefault(r => r.Product == product);
        if (sub is null || sub.OutputQty < 1)
        {
            return null;
        }

        (long Craftable, string By)? tightest = null;
        foreach (var sm in sub.Materials)
        {
            var named = report.Named.Contains(sm.Item.Value) && (report.AtLimit.Contains(sm.Item.Value) || report.GatheringBlocked);
            var blocked = sm.Qty >= 1 && !recipes.Any(r => r.Product == sm.Item) && (report.NotGatherable.Contains(sm.Item.Value) || named);
            if (!blocked)
            {
                continue;
            }

            // the same rule as Feasible (audit R1.8): Make's "missing" allows no craft; anything else needs a fresh count
            var have = named ? 0 : FreshCount(context, sm.Item);
            if (have is not { } owned)
            {
                CapUnknown(context, report, sm.Item.Value);
                continue;
            }

            var craftable = owned / sm.Qty * sub.OutputQty;
            if (tightest is null || craftable < tightest.Value.Craftable)
            {
                tightest = (craftable, sm.Item.Value);
            }
        }

        if (tightest is not { } t)
        {
            return null;
        }

        // the sub-products in the bag: unknown or outdated means no cap (the craft itself tells)
        if (FreshCount(context, product) is not { } products)
        {
            CapUnknown(context, report, product.Value);
            return null;
        }

        return (products + t.Craftable, t.By);
    }

    /// <summary>
    /// Walks the Make list until the card of <paramref name="productItemId"/> is selected (ScanRecipeList find mode).
    /// Fallback: a walk that ended because the Make view was reopened under it (live 2026-10-16: a reward popup after a
    /// batch) only lost its list position, so the walk starts again, at most twice more.
    /// </summary>
    private static JobResult Find(IJobContext context, long productItemId, CancellationToken ct)
    {
        JobResult found = null!;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            found = new ScanRecipeListJobHandler().Execute(context, new JobParameters(new Dictionary<string, object> { ["find"] = productItemId }), ct);
            if (found.Code != "list.viewReopened")
            {
                break;
            }

            JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "plan.findAgain"), ("attempt", (long)attempt + 1)));
        }

        return found;
    }

    /// <summary>The item id the game logged for the card a find selected (ScanRecipeList data "itemId"), if any.</summary>
    internal static long? FoundId(JobResult found) =>
        found.Code == "list.found" && found.Data is { } d && d.TryGetValue("itemId", out var v) && v is long id && id > 0 ? id : null;

    /// <summary>A list find proves the card is absent only when the walk reached the list's end (audit R1.6, rule 2).</summary>
    internal static bool ProvesAbsent(JobResult found) => RecipeList.ProvesAbsent(found.Code);

    /// <summary>A job result that ended on the engine's Deferred rung (its code carries "deferred", any prefix).</summary>
    internal static bool IsDeferred(JobResult r) =>
        r.State == JobState.Failed && r.Code.Contains("deferred", StringComparison.Ordinal);

    /// <summary>The pause before deferred round <paramref name="n"/> (0-based): 30 s doubling, at most 5 minutes.</summary>
    internal static TimeSpan DeferPause(int n) =>
        TimeSpan.FromSeconds(Math.Min(DeferPauseMax.TotalSeconds, DeferPauseStart.TotalSeconds * Math.Pow(2, Math.Min(n, 10))));

    /// <summary>
    /// The plan's crafts in order, consecutive crafts of the same recipe merged into one (the planner splits a large
    /// count, e.g. 99 + 50; a typed batch loop makes any count, the game capping one session at 50, so one list walk and
    /// one craft step serve both).
    /// </summary>
    internal static List<PlannedCraft> Merged(IEnumerable<PlannedCraft> crafts)
    {
        var merged = new List<PlannedCraft>();
        foreach (var c in crafts.OrderBy(c => c.Order))
        {
            if (merged.Count > 0 && string.Equals(merged[^1].RecipeId, c.RecipeId, StringComparison.Ordinal))
            {
                merged[^1] = merged[^1] with { Batches = merged[^1].Batches + c.Batches };
                continue;
            }

            merged.Add(c);
        }

        return merged;
    }

    /// <summary>
    /// The plan's raw shortfalls with a known game item id (a recipe material's id, else a learned picture's), as
    /// gathering targets (the amount still missing; the helper clamps it to the stock limit).
    /// </summary>
    internal static List<GatherTarget> GatherTargets(IJobContext context, CraftPlan plan, IReadOnlyList<Recipe> recipes)
    {
        var ids = new Dictionary<ItemKey, long>();
        foreach (var m in recipes.SelectMany(r => r.Materials))
        {
            if (m.ItemId is { } mid and > 0)
            {
                ids.TryAdd(m.Item, mid);
            }
        }

        foreach (var icon in context.Knowledge?.GetIconItems() ?? [])
        {
            if (ItemKey.FromDisplayName(icon.Name) is { Value.Length: > 0 } key)
            {
                ids.TryAdd(key, icon.ItemId);
            }
        }

        // the whole need = what is missing + what the plan counted as owned (the picker's exact count decides at the helper)
        var stock = context.Knowledge?.GetStock().Items;
        long Owned(ItemKey item) => stock is not null && stock.TryGetValue(item, out var e) && e.Count is { } c ? c : 0;
        return [.. plan.Shortfalls
            .Where(s => s.Missing > 0 && ids.ContainsKey(s.Item))
            .Select(s => new GatherTarget(ids[s.Item], s.Item.Value, (int)Math.Min(s.Missing, 9_999), s.Missing + Owned(s.Item)))];
    }

    private static (CraftPlan Plan, IReadOnlyList<Recipe> Recipes) MakePlan(IJobContext context, ItemKey item, int qty)
    {
        var knowledge = context.Knowledge!;
        var recipes = knowledge.GetRecipes();
        var stock = knowledge.GetStock();
        var plan = context.Planner.Plan(new PlanRequest(
            [new CraftGoal(item, qty)],
            knowledge.GetKeepStockRules(),
            recipes,
            stock.Items,
            PlannerPolicy.CraftJobs,
            stock.StaleSince,
            context.Clock.UtcNow));
        return (plan, recipes);
    }

    /// <summary>The My Favorites slot the recipe was learned from, or null.</summary>
    internal static int? FavoriteSlot(Recipe? recipe) =>
        recipe?.SourceCapture is { } s && s.StartsWith(FavoritePrefix, StringComparison.Ordinal)
        && int.TryParse(s.AsSpan(FavoritePrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= CraftLoopJobHandler.MaxSlot
            ? n
            : null;

    private static void JournalStep(IJobContext context, PlannedCraft craft, string code, long made) =>
        JobKit.Journal(context, JournalKind.Stock, Procedure, JobKit.Data(
            ("event", "plan.step"),
            ("order", (long)craft.Order),
            ("product", craft.Product.Value),
            ("batches", (long)craft.Batches),
            ("made", made),
            ("code", code)));

    private static JobResult Finish(IJobContext context, CraftPlan? plan, Report report, string code, string exit)
    {
        // every value must stay a code (allowed characters, at most 120 long): a longer or other string is replaced by
        // "redacted" in the job result (live 2026-10-16: "order" with " > " and a long "issues" came back redacted)
        var data = JobKit.Data(
            ("crafts", (long)(plan?.Crafts.Count ?? 0)),
            ("made", report.GoalMade),
            ("madeAll", report.Made),
            ("notFound", ShortList(report.Skipped.Distinct())),
            ("endedEarly", ShortList(report.EndedEarly.Distinct())),
            ("missing", ShortList(plan?.Shortfalls.Select(s => s.Item.Value + " x" + s.Missing.ToString(CultureInfo.InvariantCulture)) ?? [])),
            ("gameSaysMissing", ShortList(report.Named)),
            ("gathered", ShortList(report.Gathered)),
            ("notGatherable", ShortList(report.NotGatherable)),
            ("atStockLimit", ShortList(report.AtLimit.Union(report.Filled))),
            ("limitedBy", ShortList(report.CappedBy is { } by ? [by] : [])),
            ("deferred", ShortList(report.Deferred)),
            ("deferredWhy", report.LastCause),
            ("gatheringBlocked", report.GatheringBlocked ? report.GatheringBlockedBy ?? "yes" : string.Empty),
            ("endReason", exit),
            ("issues", ShortList(plan?.Issues.Select(i => i.Kind + ":" + i.Item.Value).Distinct() ?? [])),
            ("order", ShortList(plan is null ? [] : Merged(plan.Crafts).Select(c => c.Product.Value + " x" + c.Batches.ToString(CultureInfo.InvariantCulture)))));
        // the reason the loop ended, not only its totals (audit R3.2 #33: the end code was guessed from the counters)
        JobKit.Journal(context, JournalKind.Stock, Procedure, JobKit.Data(("event", "plan.end"), ("code", code), ("exit", exit), ("made", report.GoalMade), ("madeAll", report.Made)));
        var state = code is "plan.done" or "plan.nothingToDo" ? JobState.Succeeded : JobState.Failed;
        return JobKit.Result(state, code) with { Data = data };
    }

    /// <summary>
    /// A report list as a code: items made safe (a character outside the code set becomes '_'), joined with ", ", cut
    /// to fit <see cref="Codes.MaxLength"/> with ", +N more" when it is longer.
    /// </summary>
    internal static string ShortList(IEnumerable<string> items)
    {
        var all = items.Select(Codes.Safe).ToList();
        var text = string.Empty;
        for (var n = 0; n < all.Count; n++)
        {
            var next = n == 0 ? all[n] : text + ", " + all[n];
            var rest = all.Count - n - 1;
            var tail = rest > 0 ? ", +" + rest.ToString(CultureInfo.InvariantCulture) + " more" : string.Empty;
            if ((next + tail).Length > Codes.MaxLength)
            {
                var left = all.Count - n;
                return n == 0 ? Codes.Safe("+" + left.ToString(CultureInfo.InvariantCulture) + " items") : text + ", +" + left.ToString(CultureInfo.InvariantCulture) + " more";
            }

            text = next;
        }

        return text;
    }

    /// <summary>What a goal run did, across its rounds.</summary>
    private sealed class Report
    {
        public long GoalMade { get; set; }

        public long Made { get; set; }

        public List<string> Skipped { get; } = [];

        public List<string> EndedEarly { get; } = [];

        public List<string> Named { get; } = [];

        /// <summary>Materials the helper greys out (at its limit) while Make found none in the bag (2026-10-21).</summary>
        public HashSet<string> HelperRefused { get; } = new(StringComparer.Ordinal);

        /// <summary>Materials a cap was skipped for because their count was unknown or outdated (journaled once).</summary>
        public HashSet<string> CapUnknown { get; } = new(StringComparer.Ordinal);

        public List<string> Gathered { get; } = [];

        public HashSet<string> NotGatherable { get; } = new(StringComparer.Ordinal);

        /// <summary>Materials the helper cannot add to now: a greyed tile, or a picker with no room (evidence).</summary>
        public HashSet<string> AtLimit { get; } = new(StringComparer.Ordinal);

        /// <summary>Materials a trip of this job filled to their limit: not gathered again until crafting uses some (audit R2.4).</summary>
        public HashSet<string> Filled { get; } = new(StringComparer.Ordinal);

        /// <summary>Sub-products planned again after a list walk read a fresh count (once each, audit R2.1).</summary>
        public HashSet<string> Replanned { get; } = new(StringComparer.Ordinal);

        /// <summary>The round ended early to plan again with a fresh count.</summary>
        public bool ReplanRequested { get; set; }

        /// <summary>The trip failure that repeated and ended gathering for this job (audit R2.5).</summary>
        public string? GatheringBlockedBy { get; set; }

        public string? CappedBy { get; set; }

        /// <summary>What was deferred (a product, or "gathering"): unreachable now, tried again after a pause.</summary>
        public HashSet<string> Deferred { get; } = new(StringComparer.Ordinal);

        /// <summary>The same gathering trip failure twice in a row (a broken gathering tool?): gathering stopped for this job.</summary>
        public bool GatheringBlocked { get; set; }

        /// <summary>Something was deferred in the current round.</summary>
        public bool RoundDeferred { get; set; }

        /// <summary>The cause of the latest deferral (the sub-result's code), journaled with plan.deferred (rule 4).</summary>
        public string? LastCause { get; private set; }

        /// <summary>The latest deferral was one exhausted run that pressed nothing (the screen it needs was not on the game).</summary>
        public bool LastQuiet { get; private set; }

        /// <summary>When deferrals whose engine recovery ran out ("exhausted.") happened (monotonic, audit R1.10).</summary>
        public List<TimeSpan> Exhausted { get; } = [];

        public void Defer(string what, string? cause, TimeSpan now, int softEnds = 1, bool quiet = false)
        {
            Deferred.Add(what);
            RoundDeferred = true;
            LastCause = cause ?? LastCause;
            LastQuiet = quiet;
            if (cause is not null && cause.Contains("exhausted.", StringComparison.Ordinal))
            {
                Exhausted.AddRange(Enumerable.Repeat(now, Math.Max(1, softEnds))); // one per engine soft end
            }
        }
    }
}
