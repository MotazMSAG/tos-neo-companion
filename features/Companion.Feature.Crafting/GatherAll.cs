using System.Globalization;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Features.Kit;

namespace Companion.Feature.Crafting;

/// <summary>
/// GatherAll (2026-10-16, the user: "a gathering button that gathers all possible resources for known recipes"): every
/// raw material of the known recipes (a material with a game item id that no known recipe makes) is a target, as many as
/// the helper takes (the game clamps the typed count to the stock limit's room). The shared <see cref="Gathering"/>
/// module queues up to 4 per trip. A material the helper does not list (a homestead crop) is reported, not an error; a
/// halt, stop or cancel ends the job at once.
/// </summary>
/// <remarks>
/// It refills, the driest first (2026-10-23, the user: "it should go and refill my stocks"; "does it top up lowest dry
/// resources first? if not, make it do that"). The game takes at most about 200 of a material per trip (60 for some), so
/// one trip per material left Thyme at 202/500. First one walk reads every material's picker (owned / stock limit,
/// <see cref="Gathering.Survey"/>); then every trip takes the 4 emptiest materials (<see cref="Gathering.Driest"/>), the
/// levels updated by what each trip gathered, until each material is full (its tile greyed or its picker with no room),
/// not in the helper, or no trip makes progress. Each trip is journaled with what it picked and gathered and reported to
/// the status card.
/// </remarks>
internal sealed class GatherAllJobHandler : IJobHandler
{
    internal const string JobName = "GatherAll";

    /// <summary>The count typed for every target: the game sets the most the stock limit allows.</summary>
    internal const int FillQty = 9_999;

    /// <summary>Trips at most (a runaway guard: each trip must gather or leave targets behind).</summary>
    internal const int MaxTrips = 30;

    /// <summary>
    /// Deferred trips (or surveys) in a row that are paused and tried again, at most (incident 22, 2026-10-24: two trips
    /// deferred while the user played ended the run with nothing gathered); a trip that gathers starts the count over.
    /// </summary>
    internal const int MaxDeferrals = 8;

    /// <summary>Deferred trips end the run after this long without a trip that gathered.</summary>
    internal static readonly TimeSpan DeferralWindow = TimeSpan.FromMinutes(75);

    /// <summary>The journal's procedure id for this job's records.</summary>
    private const string Journal = "crafting.gatherAll";

    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        // the crafting switch and the character first (safety review 2026-10-23: with the switch off a trip typed 4 targets
        // into the helper's queue and stopped before Begin, and the next trip typed into that queue again)
        var refusal = JobKit.CheckCommitGates(context, AutomationSwitch.Crafting) ?? JobKit.RequireKnowledge(context);
        if (refusal is not null)
        {
            return refusal;
        }

        var recipes = context.Knowledge!.GetRecipes();
        var made = recipes.Select(r => r.Product).ToHashSet();
        var remaining = recipes.SelectMany(r => r.Materials)
            .Where(m => m.ItemId is > 0 && !made.Contains(m.Item))
            .GroupBy(m => m.ItemId!.Value)
            .Select(g => new GatherTarget(g.Key, g.First().Item.Value, FillQty))
            .ToList();
        if (remaining.Count == 0)
        {
            return JobKit.Result(JobState.Succeeded, "gatherAll.nothing");
        }

        var all = remaining.ToDictionary(t => t.ItemId);
        var done = new HashSet<long>();
        var levels = new Dictionary<long, StockLevel>();
        var trips = new Dictionary<long, int>();
        var misses = new Dictionary<long, int>();
        var gathered = new List<string>();
        var notInHelper = new List<string>();
        var full = new List<string>();
        var notSeen = new List<string>();
        string? lastFailure = null;
        var tripCount = 0;
        var fruitlessRun = 0;
        var failedRun = 0;
        var deferrals = 0;
        var deferredTotal = 0;
        string? deferredWhy = null;
        var exhausted = new List<TimeSpan>();
        var lastProgress = context.Clock.Monotonic;
        var toldOutOfReach = false;
        JobKit.Journal(context, JournalKind.Stock, Journal, JobKit.Data(("event", "gatherAll.start"), ("materials", (long)all.Count)));

        // 1. how full is each material (2026-10-23, the user: "top up lowest dry resources first"): one walk reads every
        //    picker and queues nothing
        context.ReportActivity("job", "gatherAll", JobKit.Data(("trip", 0L), ("phase", "survey"), ("left", (long)all.Count)));
        // incident 22 (2026-10-24): a deferred survey is tried again after a pause, 3 times in all (the trips read levels
        // too); the pause also follows the last one, so the first trip never comes right after an exhausted recovery
        var survey = Gathering.Survey(context, [.. all.Values], ct);
        var gaveUp = false;
        for (var tries = 1; survey.Code == "gather.deferred" && survey.Stop is not { State: JobState.Halted or JobState.Stopped or JobState.Cancelled }; tries++)
        {
            Merge(levels, survey.Levels);
            if (!PauseForDeferral("survey", survey.Stop?.Code))
            {
                gaveUp = true;
                lastFailure = survey.Code;
                break;
            }

            if (tries >= 3)
            {
                break;
            }

            survey = Gathering.Survey(context, [.. all.Values], ct);
        }

        if (survey.Stop is { State: JobState.Halted or JobState.Stopped or JobState.Cancelled } surveyStop)
        {
            return surveyStop;
        }

        Merge(levels, survey.Levels);
        Settle(survey.AtLimit, full);
        Settle(survey.NotFound, notInHelper);
        foreach (var (id, level) in levels.Where(kv => kv.Value is { Owned: { } o, Limit: { } l } && o >= l))
        {
            if (all.TryGetValue(id, out var t))
            {
                Settle([t], full); // its picker showed it full
            }
        }

        // incident 21 (2026-10-24): 17 materials the survey's walk did not meet were picked first, made two trips of
        // nothing and ended the run
        Settle(Gathering.Unshown(survey).Where(t => !done.Contains(t.ItemId)), notSeen);

        JobKit.Journal(context, JournalKind.Stock, Journal, JobKit.Data(
            ("event", "gatherAll.survey"), ("code", survey.Code), ("read", (long)survey.Levels.Count), ("full", (long)full.Count), ("notGatherable", (long)notInHelper.Count),
            ("notSeen", (long)notSeen.Count)));

        // 2. trips, the driest materials first, until every material is full, not in the helper, or no trip moves
        for (var trip = 1; trip <= MaxTrips && !gaveUp; trip++)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = all.Values.Where(t => !done.Contains(t.ItemId)).ToList();
            if (candidates.Count == 0)
            {
                break;
            }

            var pick = Gathering.Driest(candidates, levels, trips, Gathering.MaxTargets);
            tripCount = trip;
            context.ReportActivity("job", "gatherAll", JobKit.Data(("trip", (long)trip), ("phase", "driest"), ("left", (long)candidates.Count), ("next", Levels(pick))));
            var outcome = Gathering.Run(context, pick, ct);
            if (outcome.Stop is { State: JobState.Halted or JobState.Stopped or JobState.Cancelled } stop)
            {
                return stop;
            }

            Merge(levels, outcome.Levels);
            var settledBefore = done.Count;
            if (outcome.Code == "gather.switchOff")
            {
                lastFailure = outcome.Code;
                break; // the switch went off: no more trips (safety review 2026-10-23)
            }

            if (outcome.Gathered.Count > 0 || outcome.TripCompleted)
            {
                // a trip that gathered, or one that completed though its Finish was kept out of reach (incident 24)
                deferrals = 0;
                lastProgress = context.Clock.Monotonic;
            }

            if (outcome.Code is "gather.deferred" or Gathering.StillGathering && outcome.Gathered.Count == 0)
            {
                // incident 22 (2026-10-24): the engine deferred the trip (the user was playing, a press kept missing): it
                // is not a failed trip; after a pause the same trip is tried again (the levels it read are kept)
                JobKit.Journal(context, JournalKind.Stock, Journal, JobKit.Data(
                    ("event", "gatherAll.trip"), ("trip", (long)trip), ("phase", "driest"), ("picked", Levels(pick)), ("code", outcome.Code), ("cause", outcome.Stop?.Code),
                    ("gathered", 0L), ("queued", string.Empty), ("full", 0L), ("left", (long)candidates.Count)));
                if (!PauseForDeferral("trip", outcome.Stop?.Code ?? outcome.Code))
                {
                    lastFailure = outcome.Code;
                    break;
                }

                trip--;
                continue;
            }


            foreach (var g in outcome.Gathered)
            {
                trips[g.ItemId] = trips.GetValueOrDefault(g.ItemId) + 1;
                gathered.Add(g.Name);
                if (levels.TryGetValue(g.ItemId, out var l) && l.Owned is { } owned)
                {
                    levels[g.ItemId] = l with { Owned = owned + g.Qty }; // the picker's count plus what the trip gathered
                }
            }

            Settle(outcome.AtLimit.Concat(outcome.Filled), full);
            Settle(outcome.NotFound, notInHelper);

            // a pick that a trip which went through neither gathered nor settled (not seen in the list) is left (reported
            // under "notSeen"), so the driest-first choice cannot pick it forever
            var failure = Gathering.TripFailure(outcome);
            failedRun = failure is not null && outcome.Gathered.Count == 0 ? failedRun + 1 : 0;
            if (failure is null)
            {
                Settle(Gathering.Unshown(pick.Where(p => !done.Contains(p.ItemId)), outcome, misses), notSeen);
            }

            // nothing gathered and nothing settled or dropped: counted after the drops, which narrow the choice
            var fruitless = outcome.Gathered.Count == 0 && done.Count == settledBefore;
            fruitlessRun = fruitless ? fruitlessRun + 1 : 0;

            var left = all.Values.Count(t => !done.Contains(t.ItemId));
            JobKit.Journal(context, JournalKind.Stock, Journal, JobKit.Data(
                ("event", "gatherAll.trip"), ("trip", (long)trip), ("phase", "driest"), ("picked", Levels(pick)), ("code", outcome.Code), ("cause", outcome.Stop?.Code),
                ("gathered", (long)outcome.Gathered.Count), ("queued", CraftPlanRun.ShortList(outcome.Gathered.Select(t => t.Name + " x" + t.Qty.ToString(CultureInfo.InvariantCulture)))),
                ("full", (long)(outcome.AtLimit.Count + outcome.Filled.Count)), ("left", (long)left)));
            if (failedRun >= 2)
            {
                lastFailure = failure;
                break; // two failed trips in a row with nothing gathered (the same code or not; safety review 2026-10-23)
            }

            lastFailure = failure ?? lastFailure;
            if (fruitlessRun >= 2)
            {
                break; // two trips in a row that gathered, settled and dropped nothing: another would do the same
            }
        }

        context.ReportActivity("job", null);
        var leftOver = all.Values.Where(t => !done.Contains(t.ItemId)).ToList();
        var data = JobKit.Data(
            ("gathered", CraftPlanRun.ShortList(gathered.Distinct())),
            ("atStockLimit", CraftPlanRun.ShortList(full.Distinct())),
            ("notGatherable", CraftPlanRun.ShortList(notInHelper.Distinct())),
            ("notSeen", CraftPlanRun.ShortList(notSeen.Distinct())),
            ("left", CraftPlanRun.ShortList(leftOver.Select(t => t.Name))),
            ("trips", (long)tripCount),
            ("lastCode", lastFailure),
            ("deferred", deferredTotal > 0 ? deferredTotal.ToString(CultureInfo.InvariantCulture) + " times" : null),
            ("deferredWhy", deferredWhy));
        var code = leftOver.Count > 0 ? "gatherAll.incomplete" : gathered.Count > 0 ? "gatherAll.done" : "gatherAll.nothingGathered";
        JobKit.Journal(context, JournalKind.Stock, Journal, JobKit.Data(
            ("event", "gatherAll.end"), ("code", code), ("trips", (long)tripCount), ("gathered", (long)gathered.Distinct().Count()),
            ("full", (long)full.Distinct().Count()), ("notGatherable", (long)notInHelper.Distinct().Count()), ("notSeen", (long)notSeen.Distinct().Count()),
            ("left", (long)leftOver.Count)));
        if (leftOver.Count > 0)
        {
            // never "done" while materials are left untried or not full (audit R1.7)
            return JobKit.Result(JobState.Failed, code) with { Data = data };
        }

        return JobKit.Result(gathered.Count > 0 || full.Count > 0 ? JobState.Succeeded : JobState.Failed, code) with { Data = data };

        // a trip or survey the engine deferred: a pause, then the same again. False when the run must end instead: the
        // deferral count, or 75 minutes without a trip that gathered. Near the engine's soft-end allowance (a HALT on the 4th
        // exhausted recovery in 30 minutes, audit R1.10) the pause lasts until the older ones are out of that window
        bool PauseForDeferral(string what, string? cause)
        {
            var now = context.Clock.Monotonic;
            deferredWhy = cause ?? deferredWhy;
            if (cause is not null && cause.Contains("exhausted.", StringComparison.Ordinal))
            {
                exhausted.Add(now);
            }

            var outOfReach = JobKit.OutOfReach(cause);
            if ((deferrals >= MaxDeferrals && !outOfReach) || now - lastProgress >= (outOfReach ? JobKit.OutOfReachWindow : DeferralWindow))
            {
                JobKit.Journal(context, JournalKind.Decision, Journal, JobKit.Data(
                    ("event", "gatherAll.deferralsOver"), ("what", what), ("deferrals", (long)deferrals), ("cause", cause)));
                return false;
            }

            var pause = outOfReach ? CraftPlanRun.DeferPauseMax : JobKit.ClearOfSoftEnds(CraftPlanRun.DeferPause(deferrals++), JobKit.Ledger(context, exhausted, now), now);
            toldOutOfReach |= JobKit.TellOutOfReach(context, cause, toldOutOfReach);
            deferredTotal++;
            JobKit.Journal(context, JournalKind.Decision, Journal, JobKit.Data(
                ("event", "gatherAll.deferred"), ("what", what), ("deferrals", (long)deferrals), ("pauseS", (long)pause.TotalSeconds), ("cause", cause)));
            context.ReportActivity("step", "gatherAll.pause", JobKit.Data(("cause", cause), ("untilUtc", context.Clock.UtcNow + pause)));
            try
            {
                context.Clock.Sleep(pause, ct);
            }
            finally
            {
                context.ReportActivity("step", null);
            }

            return true;
        }

        void Settle(IEnumerable<GatherTarget> targets, List<string> into)
        {
            foreach (var t in targets)
            {
                done.Add(t.ItemId);
                into.Add(t.Name);
            }
        }

        // "thyme 2/500, water 160/999": owned / stock limit as the pickers showed them (codes allow no percent sign)
        string Levels(IEnumerable<GatherTarget> targets) => Codes.Safe(string.Join(", ", targets.Select(t =>
            t.Name + (levels.TryGetValue(t.ItemId, out var l) && l is { Owned: { } o, Limit: { } lim } ? " " + o.ToString(CultureInfo.InvariantCulture) + "/" + lim.ToString(CultureInfo.InvariantCulture) : string.Empty))));
    }

    private static void Merge(Dictionary<long, StockLevel> into, IReadOnlyDictionary<long, StockLevel> from)
    {
        foreach (var (id, level) in from)
        {
            into[id] = level;
        }
    }
}
