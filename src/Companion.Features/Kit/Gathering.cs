using System.Globalization;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Host;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Perception;
using Companion.Contracts.Safety;

namespace Companion.Features.Kit;

/// <summary>One material to gather: the game's item id, its name (for reports) and how many more to gather.</summary>
/// <param name="ItemId">Game item id (the log's item tip id).</param>
/// <param name="Name">Item name (reports only).</param>
/// <param name="Qty">How many more to gather (the game clamps a target to the stock limit).</param>
/// <param name="Need">Optional: the whole amount the plan needs; with the picker's exact owned count the target becomes
/// min(need - owned, room), and nothing when the owned count already covers it.</param>
internal sealed record GatherTarget(long ItemId, string Name, int Qty, long? Need = null);

/// <summary>A material's stock as its picker showed it: owned and the stock limit (each null when not shown).</summary>
internal sealed record StockLevel(long? Owned, long? Limit)
{
    /// <summary>How full it is (0 empty .. 1 full), or null when the owned count or the limit is unknown.</summary>
    public double? Fill => Owned is { } o && Limit is { } l && l > 0 ? Math.Clamp((double)o / l, 0, 1) : null;
}

/// <summary>What one gathering run did.</summary>
/// <param name="Stop">A halt, stop or cancel of a procedure run (the caller ends with it), else null.</param>
/// <param name="Gathered">Targets that were queued and completed (Finish pressed).</param>
/// <param name="NotFound">Targets whose material the helper's list does not show.</param>
/// <param name="Code">"gather.done", "gather.enough" (the picker showed enough owned), "gather.notFound", "gather.notOpened", "gather.notStarted", "gather.timeout",
/// "gather.cancelled", "gather.switchOff", "gather.queueFailed", "gather.finishFailed", or "gather.deferred" (an engine
/// deferral ended the trip: the user was playing, a press was out of reach, or a recovery ran out; the cause is the
/// Stop's code and the step's own code is <see cref="GatherOutcome.StepCode"/>).</param>
internal sealed record GatherOutcome(JobResult? Stop, IReadOnlyList<GatherTarget> Gathered, IReadOnlyList<GatherTarget> NotFound, string Code)
{
    /// <summary>
    /// Targets the helper cannot add to now: a recognised greyed tile, or a picker with no room (evidence). Not the ones
    /// this run filled (audit R2.4: "filled by us" read as "the helper refuses it" and ended a goal, incident 14).
    /// </summary>
    public IReadOnlyList<GatherTarget> AtLimit { get; init; } = [];

    /// <summary>Targets this run gathered up to their stock limit (they are also in Gathered).</summary>
    public IReadOnlyList<GatherTarget> Filled { get; init; } = [];

    /// <summary>What the pickers of this run showed per item id: owned and stock limit (null when the line was not shown).</summary>
    public IReadOnlyDictionary<long, StockLevel> Levels { get; init; } = new Dictionary<long, StockLevel>();

    /// <summary>Learned materials the walk did not see (2026-10-22: never taken as at their limit; tried again next trip).</summary>
    public IReadOnlyList<GatherTarget> NotSeen { get; init; } = [];

    /// <summary>Targets the walk did not reach because the helper's queue was full (the next run takes them).</summary>
    public IReadOnlyList<GatherTarget> Deferred { get; init; } = [];

    /// <summary>
    /// A press of this run ended Deferred (the engine's last fallback rung: unreachable from where the cursor rested, or
    /// its recovery ran out; FALLBACKS.md): what was not gathered can be tried again later.
    /// </summary>
    public bool EngineDeferred { get; init; }

    /// <summary>For "gather.deferred": the code the step would have given (gather.notStarted, gather.notOpened, ...).</summary>
    public string? StepCode { get; init; }

    /// <summary>
    /// The walk did not see the whole list (a page unreadable, the page cap, or the top never reached): unlearned targets are
    /// then <see cref="NotSeen"/>, never <see cref="GatherOutcome.NotFound"/> (audit R1.5, rule 2).
    /// </summary>
    public bool WalkIncomplete { get; init; }

    /// <summary>
    /// This run waited while a trip gathered and saw it complete, whatever came after (a Finish the user's mouse kept out
    /// of reach): the character did gather, which is progress for a job's "no progress" limit (incident 24).
    /// </summary>
    public bool TripCompleted { get; init; }
}

/// <summary>
/// The Gathering module (2026-10-15, live map in DISCOVERIES.md): the game's own Gathering Helper
/// (UI_LIFESKILL_AUTO_COLLECT on the Ability view's Gather tab) gathers up to 4 targets in order, travelling by itself;
/// this module only queues targets and claims them, through the data procedure <see cref="Procedure"/> (subsets of its
/// steps per run, like the Stock scan):
/// <list type="number">
/// <item>open the helper unless its list is already on screen (the grid reader <see cref="GridReader"/>); a complete
/// queue from before is claimed first (Finish), a running one is waited for;</item>
/// <item>per target: walk the list from the top; a tile whose picture is a learned helper item of ANOTHER id is skipped,
/// any other tile is pressed (the target picker opens) and its picture's item tip read (the log names the id: the only
/// truth; learned for the next walks, place "gather@list"); the picker of the wanted id gets the quantity on its keypad
/// and Confirm, every other picker is closed with Esc;</item>
/// <item>with the crafting switch on: Begin (the character travels to the sites), then the log's auto-collect status is
/// polled: 1 gathering, 2 complete (the game reopens the helper), 0 idle; Finish claims the queue.</item>
/// </list>
/// Fallbacks, never a halt: a material the list does not show is reported (NotFound) and the others are still gathered;
/// a status that never comes ends with a code and leaves the game gathering (the next run claims it). Shared by every
/// feature that needs materials (Crafting's goal loop first).
/// </summary>
internal static partial class Gathering
{
    /// <summary>Data procedure (crafting feature).</summary>
    internal const string Procedure = "crafting.gather";

    /// <summary>The picker's item-tip procedure.</summary>
    internal const string TipProcedure = "crafting.gatherTip";

    /// <summary>Reader of the helper's material list.</summary>
    internal const string GridReader = "crafting.gather.grid";

    /// <summary>Readers of the picker's "Stock Limit: a/b" line (the picker logged as the top UI, or silently over the helper).</summary>
    internal static readonly string[] SetReaders = ["crafting.gather.set", "crafting.gather.set.silent"];

    /// <summary>
    /// A trip's code when its time ran out while the character was still gathering (incident 23): neither a failure nor
    /// progress; the job pauses and the next walk waits for the trip and claims it.
    /// </summary>
    internal const string StillGathering = "gather.stillGathering";

    /// <summary>The picker's panel on screen, whatever the log's top (incident 22).</summary>
    internal const string PickerReader = "crafting.gather.set.any";

    /// <summary>The queue slots' "gathered/target" counters (what the game accepted for each queued target).</summary>
    internal const string QueueReader = "crafting.gather.queue";

    /// <summary>The helper's anchor (list presses and drags).</summary>
    internal const string Anchor = "gather.anchor";

    /// <summary>The picker's anchor (the tip procedure's shape).</summary>
    internal const string SetAnchor = "gather.set.anchor";

    /// <summary>Place of a learned helper picture.</summary>
    internal const string Place = "gather@list";

    /// <summary>The helper queue's length.</summary>
    internal const int MaxTargets = 4;

    /// <summary>Auto-collect status: idle (after Finish).</summary>
    internal const int StatusIdle = 0;

    /// <summary>Auto-collect status: gathering.</summary>
    internal const int StatusGathering = 1;

    /// <summary>Auto-collect status: complete, waiting for Finish.</summary>
    internal const int StatusComplete = 2;

    /// <summary>
    /// Auto-collect status: paused in the middle of a trip ("Gathering Paused", live 2026-10-16: the helper paused on the
    /// way to a cave spot and stayed there for 35 minutes). Its Continue button is the Begin button (the same picture at
    /// the same place, NCC 1.000 on gather_paused.png), so pressing Begin continues the trip.
    /// </summary>
    internal const int StatusPaused = 3;

    /// <summary>A trip that pauses again while waited for is left after this long (the next round tries again).</summary>
    internal static readonly TimeSpan PausedGiveUp = TimeSpan.FromMinutes(2);

    /// <summary>A helper picture this close to a learned one is that item (the same tile resampled by a scroll).</summary>
    internal const int SameKeyBits = 8;

    /// <summary>Another learned item must lie this much farther (else the tile is pressed).</summary>
    internal const int RivalBits = 3;

    /// <summary>Most list pages walked per target (a drag moves about 2 rows).</summary>
    internal const int MaxPages = 40;

    /// <summary>Scroll drags cut short in a row that are made again when the list then showed nothing new.</summary>
    internal const int MaxCutShortRetries = 2;

    /// <summary>Most drags back to the top.</summary>
    internal const int MaxTopDrags = 20;

    /// <summary>Poll interval of the auto-collect status.</summary>
    internal static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The gear durability Notice (2026-10-18): "&lt;gear&gt; Durability is insufficient ... pause and change Gear?",
    /// Ignore / Replace. The game does not log it and shows it again on every gathering tick, so it is looked for on the
    /// screen (on the HUD, and over the helper) and dismissed for the day: tick "Don't show again today", then Ignore.
    /// </summary>
    internal const string GearNoticeReader = "crafting.gear.notice";

    /// <summary>The data procedure that ticks the box and presses Ignore (Replace is never pressed).</summary>
    internal const string GearNoticeProcedure = "crafting.gearNotice";

    /// <summary>The reader that sees the gear notice with an EMPTY "Don't show again today" box (incident 20).</summary>
    internal const string GearBoxEmptyReader = "crafting.gear.emptybox";

    /// <summary>
    /// How often the wait loop looks for the gear notice (incident 20, 2026-10-24: 30 s let each notice stall gathering
    /// for up to half a minute; it came back every 72 s).
    /// </summary>
    internal static readonly TimeSpan GearNoticeEvery = TimeSpan.FromSeconds(10);

    /// <summary>The extra wait a trip gets per notice answered during it (the notice holds gathering), at most 30 minutes in all.</summary>
    internal static readonly TimeSpan NoticeGrace = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Looks for the gear durability notice and, when it is on screen, ticks "Don't show again today", presses Ignore,
    /// then tells the user what happened (journal "gear.notice" with the run's code). The run, or null when the notice is
    /// not shown. Shared by the gathering wait and the goal loop (live 2026-10-18: the notice covered the screen when a
    /// craft round opened the Ability view; the reset's Esc does not close it and 12 Esc presses latched a HALT).
    /// </summary>
    /// <remarks>
    /// Audit R1.9 (rules 1, 4 and 7): the message said "pressed Ignore" before anything was pressed, whatever the run did;
    /// it now follows the outcome. The box is a toggle and nothing reads whether it is ticked: after a run that clicked it
    /// and did not close the notice (a Stop, a pause, a deferral between the two clicks), the next run for the same notice
    /// presses only Ignore, or it would untick the box. A check that no longer sees the notice forgets that.
    /// </remarks>
    internal static ProcedureResult? DismissGearNotice(IJobContext context, CancellationToken ct)
    {
        if (context.ProcedureCatalog.Get(GearNoticeProcedure) is not { } dismiss)
        {
            return null;
        }

        var read = context.Screen.Read(GearNoticeReader, ct);
        JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "gear.check"), ("status", read.Status.ToString())));
        if (read.Status != ReadStatus.Ok)
        {
            if (read.Status == ReadStatus.ScreenMismatch)
            {
                _gearBoxTicked = false; // the notice is gone: a new one gets its box ticked
            }

            return null;
        }

        // incident 20 and the 2026-10-31 audit: "Don't show again today" never held (10-28: 26 notices in 8 minutes, the
        // box read ticked after 5 of the clicks and the notice still came back about 13 s later), so only Ignore is pressed
        var boxEmpty = !_gearBoxTicked && context.Screen.Read(GearBoxEmptyReader, ct).Status == ReadStatus.Ok;
        var boxSkipped = true;
        ProcedureResult run;
        if (boxSkipped)
        {
            run = context.Procedures.Run(Steps(dismiss, "ignore"), context.Run, ct);
        }
        else
        {
            // the tick and Ignore as two runs, with a look at the box in between (incident 20, 2026-10-24: the box read
            // empty on every one of the notices that kept coming back; is the click ticking it at all?)
            run = context.Procedures.Run(Steps(dismiss, "box"), context.Run, ct);
            if (run.Attempts.Any(a => a.StepId == "box" && a.Decision == RecoveryAction.Proceed))
            {
                _gearBoxTicked = true;
            }

            if (run.Status == ProcedureStatus.Succeeded)
            {
                var stillEmpty = context.Screen.Read(GearBoxEmptyReader, ct).Status == ReadStatus.Ok;
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "gear.boxAfterClick"), ("stillEmpty", stillEmpty)));
                run = context.Procedures.Run(Steps(dismiss, "ignore"), context.Run, ct);
            }
        }

        var dismissed = run.Status == ProcedureStatus.Succeeded;
        if (dismissed)
        {
            _gearBoxTicked = false; // Ignore closed it
            var now = context.Clock.Monotonic;
            _noticesAnswered.Enqueue(now);
            while (_noticesAnswered.Count > 0 && now - _noticesAnswered.Peek() > GearStormWindow)
            {
                _noticesAnswered.Dequeue();
            }
        }

        JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "gear.notice"), ("code", dismissed ? "dismissed" : "procedure." + run.Status),
            ("detail", run.EndDetail), ("boxSkipped", boxSkipped), ("boxEmpty", boxEmpty)));
        context.Notifier.Notify(dismissed
            ? new Notification(NotificationKind.NeedsUser, "gear.notice", GearNoticeText)
            : new Notification(NotificationKind.NeedsUser, "gear.notice.open", GearNoticeOpenText));
        return run;
    }

    /// <summary>An earlier run clicked the notice's box and the notice has stayed on screen since (see DismissGearNotice).</summary>
    private static bool _gearBoxTicked;

    /// <summary>When the gear notice was answered lately (monotonic).</summary>
    private static readonly Queue<TimeSpan> _noticesAnswered = new();

    /// <summary>The window and count of answered gear notices that make a storm: gathering stops (2026-10-31 audit).</summary>
    internal static readonly TimeSpan GearStormWindow = TimeSpan.FromMinutes(5);

    /// <summary>Gear notices answered within <see cref="GearStormWindow"/> that stop gathering.</summary>
    internal const int GearStormCount = 4;

    /// <summary>The user's message when the gear notice keeps coming back.</summary>
    internal const string GearStormText = "The gathering gear notice keeps coming back (4 times in 5 minutes): the gear needs repair or replacing. Gathering stops for this job; repair the gear, then start again.";

    /// <summary>The gear notice came back <see cref="GearStormCount"/> times within <see cref="GearStormWindow"/>.</summary>
    internal static bool GearNoticeStorm(TimeSpan now) => _noticesAnswered.Count(t => now - t <= GearStormWindow) >= GearStormCount;

    /// <summary>Test hook: forget the box state and the trip that is out between tests.</summary>
    internal static void ResetGearNoticeState()
    {
        _gearBoxTicked = false;
        _noticesAnswered.Clear();
        _tripDue = null;
    }

    /// <summary>The user's message after a dismiss that worked (tray and companion window).</summary>
    internal const string GearNoticeText = "Your gathering gear's durability ran out: its gathering bonus no longer works. The companion ticked 'Don't show again today' and pressed Ignore; gathering goes on. Replace the gear in the game when you can.";

    /// <summary>The user's message when the notice is still on screen after the companion's try.</summary>
    internal const string GearNoticeOpenText = "Your gathering gear's durability ran out and its notice is still on screen: the companion could not press Ignore this time. It tries again, or press Ignore in the game yourself. Replace the gear when you can.";

    /// <summary>
    /// How long gathering may take: 10 min plus 25 s per item, at most 4 h. Incident 23 (2026-10-24): 60 Wisdom Essence
    /// took 19 min 37 s (21:38:41 to 21:58:18 UTC, about 20 s each with the travel) against a limit of 13 min (3 s per
    /// item, from 2 mushrooms in 14 s); the goal called it a timeout twice and stopped gathering.
    /// </summary>
    internal static TimeSpan WaitLimit(int items) => TimeSpan.FromSeconds(Math.Min(4 * 3600, 600 + (25L * Math.Max(0, items))));

    /// <summary>
    /// When the trip begun last is due back (monotonic), so a later walk that finds it still out waits for the rest of its
    /// time, not only <see cref="WaitLimit"/>(0) (incident 23). Null when no trip is known to be out.
    /// </summary>
    private static TimeSpan? _tripDue;

    /// <summary>
    /// Gathers up to <see cref="MaxTargets"/> of <paramref name="targets"/>: the walk considers all of them and fills the
    /// queue with the first ones it finds (2026-10-16: taking the first 4 before the walk left slots empty when some of them
    /// were not in the list); targets the full queue left out are Deferred (the next run takes them).
    /// </summary>
    internal static GatherOutcome Run(IJobContext context, IReadOnlyList<GatherTarget> targets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(targets);
        var refusal = JobKit.RequireKnowledge(context) ?? JobKit.CheckProcedures(context, [Procedure, TipProcedure]);
        if (refusal is not null)
        {
            return new GatherOutcome(refusal, [], [], "gather.notOpened");
        }

        var data = context.ProcedureCatalog.Get(Procedure)!;
        var tipData = context.ProcedureCatalog.Get(TipProcedure)!;
        if (!ItemTip.IsExpectedShape(tipData, SetAnchor))
        {
            return new GatherOutcome(JobKit.Failed("procedure.invalid:" + TipProcedure), [], [], "gather.notOpened");
        }

        var walk = new Walk(context, data, tipData, ct);
        return Label(walk.Run([.. targets.Where(t => t.Qty > 0)]), walk.SawDeferred, walk.WalkIncomplete) with { TripCompleted = walk.SawCompletion };
    }

    /// <summary>
    /// Reads how full each target is without gathering (2026-10-23, the user: "does it top up lowest dry resources
    /// first? if not, make it do that"): one walk over the helper's list presses each target's tile, reads its picker's
    /// "Stock Limit: owned/limit" line, closes it, and queues nothing. A greyed tile is at its limit; a target the walk
    /// does not meet is "not seen" (or "not found" after a complete walk), as in a trip. Code "gather.surveyed".
    /// </summary>
    internal static GatherOutcome Survey(IJobContext context, IReadOnlyList<GatherTarget> targets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(targets);
        var refusal = JobKit.RequireKnowledge(context) ?? JobKit.CheckProcedures(context, [Procedure, TipProcedure]);
        if (refusal is not null)
        {
            return new GatherOutcome(refusal, [], [], "gather.notOpened");
        }

        var data = context.ProcedureCatalog.Get(Procedure)!;
        var tipData = context.ProcedureCatalog.Get(TipProcedure)!;
        if (!ItemTip.IsExpectedShape(tipData, SetAnchor))
        {
            return new GatherOutcome(JobKit.Failed("procedure.invalid:" + TipProcedure), [], [], "gather.notOpened");
        }

        var walk = new Walk(context, data, tipData, ct) { SurveyOnly = true };
        return Label(walk.Run([.. targets.Where(t => t.Qty > 0)]), walk.SawDeferred, walk.WalkIncomplete) with { TripCompleted = walk.SawCompletion };
    }

    /// <summary>
    /// The next trip's targets, driest first (2026-10-23, the user: "top up lowest dry resources first"): the lowest
    /// known fill (owned / limit) first; a material whose fill is unknown goes after every known one (incident 21,
    /// 2026-10-24: unknown counted as half full, so materials the walk could not see, likely greyed and full, went before
    /// Water at 539/999, and the run ended without trying the others); ties go to the one with fewer trips so far, then
    /// to the list order. At most <paramref name="count"/> (the helper's queue holds 4).
    /// </summary>
    internal static List<GatherTarget> Driest(IEnumerable<GatherTarget> candidates, IReadOnlyDictionary<long, StockLevel> levels, IReadOnlyDictionary<long, int> trips, int count) =>
        [.. candidates
            .Select((t, i) => (t, i))
            .OrderBy(x => levels.TryGetValue(x.t.ItemId, out var l) && l.Fill is { } f ? f : 2.0)
            .ThenBy(x => trips.GetValueOrDefault(x.t.ItemId))
            .ThenBy(x => x.i)
            .Select(x => x.t)
            .Take(count)];

    /// <summary>
    /// Marks how the trip went. Audit R1.4 (rule 4): a trip ended by an engine deferral was logged as notStarted,
    /// notOpened or queueFailed, and two "notStarted" in a row switched gathering off while the user was only playing.
    /// The deferral gets its own code; the cause stays in the Stop's code, the step's code in StepCode.
    /// </summary>
    internal static GatherOutcome Label(GatherOutcome outcome, bool sawDeferred, bool walkIncomplete)
    {
        outcome = outcome with { EngineDeferred = outcome.EngineDeferred || sawDeferred, WalkIncomplete = outcome.WalkIncomplete || walkIncomplete };
        if (outcome.Stop is { State: JobState.Failed } s && s.Code.Contains("deferred", StringComparison.Ordinal) && outcome.Code != "gather.deferred")
        {
            outcome = outcome with { Code = "gather.deferred", StepCode = outcome.Code };
        }

        return outcome;
    }

    /// <summary>
    /// A greyed tile cannot be pressed, so its id is never confirmed: only a strict picture match (the same icon) proves the
    /// material is at its limit; a looser one is unknown (audit R2.6, rule 2).
    /// </summary>
    internal static bool FadedProvesLimit(int distance) => distance <= StockIcons.SameIconMaxBits;

    /// <summary>
    /// The run of identical trip failures (two in a row end gathering for a goal). Audit R2.5 (rule 6): only "not started"
    /// had a budget; a timeout, a failed finish or a helper that did not open repeated every round. A deferred trip (the
    /// user was playing, a press out of reach) neither counts nor resets the run (audit R1.4); a trip that went through
    /// resets it.
    /// </summary>
    internal static (string? Code, int Count) NextFailure(string? last, int count, GatherOutcome outcome)
    {
        var failure = TripFailure(outcome);
        if (failure is null)
        {
            return (null, 0);
        }

        if (failure is "gather.deferred" or StillGathering)
        {
            return (last, count); // held, not failed: neither counts nor resets (audit R1.4, incident 23)
        }

        return failure == last ? (failure, count + 1) : (failure, 1);
    }

    /// <summary>
    /// The trip's failure code, or null when the trip went through: it completed (gather.done) or the walk settled every
    /// target without queueing any (enough, not found, at the limit, not seen).
    /// </summary>
    internal static string? TripFailure(GatherOutcome outcome) =>
        outcome.Stop is null && outcome.Code is "gather.done" or "gather.enough" or "gather.notFound" or "gather.atLimit" or "gather.notSeen"
            ? null
            : outcome.Code;

    /// <summary>
    /// The targets for the next trip of a multi-trip job (audit R1.7, rules 2 and 6): after a trip that went through, what
    /// the full queue left plus what the walk did not see; after a failed trip, everything it did not gather, rule out or
    /// find full. Nothing untried is dropped.
    /// </summary>
    internal static List<GatherTarget> NextTargets(IReadOnlyList<GatherTarget> remaining, GatherOutcome outcome)
    {
        if (TripFailure(outcome) is null)
        {
            return [.. outcome.Deferred, .. outcome.NotSeen.Where(t => outcome.Deferred.All(d => d.ItemId != t.ItemId))];
        }

        var settled = outcome.Gathered.Concat(outcome.NotFound).Concat(outcome.AtLimit).Select(t => t.ItemId).ToHashSet();
        return [.. remaining.Where(t => !settled.Contains(t.ItemId))];
    }

    /// <summary>
    /// What a trip that went through gathered without filling it to its stock limit (the game takes about 200 of a
    /// material per trip): the next top-up targets of a refill. A target whose room was unknown counts as not filled; the
    /// next trip's picker (no room) or the queue (cut to 1) then shows it full.
    /// </summary>
    internal static List<GatherTarget> NotFilled(GatherOutcome outcome) =>
        TripFailure(outcome) is null
            ? [.. outcome.Gathered.Where(g => outcome.Filled.All(f => f.ItemId != g.ItemId) && outcome.AtLimit.All(a => a.ItemId != g.ItemId))]
            : [];

    /// <summary>
    /// The materials a survey's complete walk did not meet: the helper does not show them now (greyed when full, or not
    /// offered here), and a trip's walk is the same walk (incident 21, 2026-10-24). None after an incomplete walk.
    /// </summary>
    internal static IReadOnlyList<GatherTarget> Unshown(GatherOutcome survey) =>
        survey.Code == "gather.surveyed" && !survey.WalkIncomplete ? survey.NotSeen : [];

    /// <summary>
    /// The picks of a trip that went through which it did not gather, counted in <paramref name="misses"/>; returned are
    /// the ones now left as not shown by the helper: after a complete walk at once, after an incomplete walk on the
    /// second miss (incident 21, 2026-10-24: the same 4 unseen picks were chosen again and the run ended).
    /// </summary>
    internal static List<GatherTarget> Unshown(IEnumerable<GatherTarget> picks, GatherOutcome outcome, Dictionary<long, int> misses)
    {
        var allowed = outcome.WalkIncomplete ? 2 : 1;
        var left = new List<GatherTarget>();
        foreach (var p in picks.Where(p => outcome.Gathered.All(g => g.ItemId != p.ItemId)))
        {
            if ((misses[p.ItemId] = misses.GetValueOrDefault(p.ItemId) + 1) >= allowed)
            {
                left.Add(p);
            }
        }

        return left;
    }

    /// <summary>
    /// The pending targets a walk did not queue: a learned material it did not meet is "not seen"; an unlearned one is
    /// "not found" only when the walk saw the whole list, else also "not seen" (audit R1.5, rule 2). Nothing is either
    /// when the queue filled up (the rest is deferred).
    /// </summary>
    internal static (List<GatherTarget> NotSeen, List<GatherTarget> NotFound) Unmet(
        IReadOnlyList<GatherTarget> pending, IReadOnlySet<long> learnedIds, bool queueFull, bool walkIncomplete)
    {
        if (queueFull)
        {
            return ([], []);
        }

        var notSeen = pending.Where(t => learnedIds.Contains(t.ItemId)).ToList();
        var unlearned = pending.Where(t => !learnedIds.Contains(t.ItemId)).ToList();
        if (walkIncomplete)
        {
            notSeen.AddRange(unlearned);
            return (notSeen, []);
        }

        return (notSeen, unlearned);
    }

    /// <summary>The run's steps, in order; a repeated id gets a suffix (the engine journals steps by id).</summary>
    internal static ProcedureDefinition Steps(ProcedureDefinition data, params string[] ids)
    {
        var steps = new List<StepDefinition>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var s = data.Steps.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("step missing: " + id);
            var n = seen[id] = seen.GetValueOrDefault(id) + 1;
            steps.Add(n == 1 ? s : s with { Id = id + "-" + n.ToString(CultureInfo.InvariantCulture) });
        }

        return data with { Steps = steps };
    }

    /// <summary>The keypad steps that type <paramref name="qty"/>: clear, its digits, OK, then the picker's Confirm.</summary>
    internal static string[] TypeSteps(int qty) =>
        ["qty", "qty.settle", "key.clear", .. qty.ToString(CultureInfo.InvariantCulture).Select(d => "key." + d), "key.ok", "set.confirm", "set.settle"];

    /// <summary>"Stock Limit: 353/500" = (353, 500); null unless one a/b pair with a &lt;= b and b &gt; 0.</summary>
    internal static (long Owned, long Limit)? ParseLimit(string text)
    {
        var m = LimitPattern().Match(text ?? string.Empty);
        if (!m.Success
            || !long.TryParse(m.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var owned)
            || !long.TryParse(m.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
            || limit <= 0 || owned > limit)
        {
            return null;
        }

        return (owned, limit);
    }

    /// <summary>The target of a queue slot's "gathered/target" ("0/416" = 416); null for anything else.</summary>
    internal static long? ParseSlot(string? text)
    {
        var t = (text ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal);
        var slash = t.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && slash < t.Length - 1 && t[..slash].All(char.IsAsciiDigit) && t[(slash + 1)..].All(char.IsAsciiDigit) && t.Length <= 11
            ? long.Parse(t[(slash + 1)..], CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>The quantity box's default ("147") as the free room; null unless 1-5 digits.</summary>
    internal static long? ParseRoom(string text)
    {
        var t = (text ?? string.Empty).Trim();
        return t.Length is >= 1 and <= 5 && t.All(char.IsAsciiDigit) ? long.Parse(t, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Where a list tile is pressed: on its picture, above the count line.</summary>
    internal static PointI PressPoint(RectI tile) => new(tile.Center.X, tile.Top + 40);

    [System.Text.RegularExpressions.GeneratedRegex(@"(\d{1,7})\s*/\s*(\d{1,7})", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex LimitPattern();

    /// <summary>One tile press: a stop, the logged id, whether a picker opened, its stock limit line.</summary>
    private sealed record ProbeResult(JobResult? Stop, long? Id, bool PickerOpen, PickerStock? Limit);

    /// <summary>
    /// What an open picker shows: the owned count and the limit when the "Stock Limit: a/b" line is visible (a long
    /// description pushes it out of its box, live 2026-10-16: Wisdom Essence), and the free room = limit - owned from
    /// that line only (the quantity box's default often equals it, 147 for 353/500, but showed 60 for 0/300).
    /// </summary>
    internal sealed record PickerStock(long? Owned, long? Limit, long? Room);

    private sealed class Walk(IJobContext context, ProcedureDefinition data, ProcedureDefinition tipData, CancellationToken ct)
    {
        private readonly ICharacterKnowledge _knowledge = context.Knowledge!;
        private readonly HashSet<string> _pressed = new(StringComparer.Ordinal);
        private readonly Dictionary<long, long> _owned = [];
        private readonly Dictionary<long, long> _rooms = [];
        private readonly Dictionary<long, StockLevel> _levels = [];
        private int _probes;

        /// <summary>Read every target's picker and queue nothing (<see cref="Survey"/>).</summary>
        public bool SurveyOnly { get; init; }

        public GatherOutcome Run(IReadOnlyList<GatherTarget> targets)
        {
            if (targets.Count == 0)
            {
                return new GatherOutcome(null, [], [], "gather.done");
            }

            // incident 18 (2026-10-23): a trip from before is still out; the engine allows no input while the character
            // gathers, so it is waited for before the Ability view is opened (it showed as a wait on "opening")
            if (context.UiState.AutoCollectStatus == StatusGathering && WaitComplete(0) is { } early)
            {
                return early;
            }

            // the notice can pop up on the trip's last tick and cover the helper: answered before anything is opened
            // (safety review 2026-10-23; Esc does not close it)
            if (DismissGearNotice() is { } noticeStop)
            {
                return noticeStop;
            }

            if (Open() is { } openStop)
            {
                return openStop;
            }

            // a queue from before: claim a complete one, wait for a running one (never disturbed)
            if (context.UiState.AutoCollectStatus == StatusGathering && WaitComplete(0) is { } busy)
            {
                return busy;
            }

            if (context.UiState.AutoCollectStatus == StatusComplete && Finish() is { } claimed)
            {
                return claimed;
            }

            // a paused trip from before: continue it (nothing gathered so far is wasted), wait, claim it, then walk
            if (context.UiState.AutoCollectStatus == StatusPaused)
            {
                if (SurveyOnly)
                {
                    return new GatherOutcome(null, [], [], "gather.busy"); // a survey only reads: the paused trip is left alone
                }

                if (JobKit.CheckCommitGates(context, AutomationSwitch.Crafting) is not null)
                {
                    return new GatherOutcome(null, [], [], "gather.switchOff"); // Begin restarts the travel: the switch decides
                }

                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "gather.resumePaused")));
                if (EnsureHelper() is { } hidden)
                {
                    return hidden;
                }

                var resume = Run(Steps(data, "begin"));
                if (resume.Status != ProcedureStatus.Succeeded)
                {
                    return new GatherOutcome(StopOf(resume), [], [], "gather.notResumed");
                }

                if (PollFor(StatusGathering, TimeSpan.FromSeconds(20)) && WaitComplete(0) is { } resumed)
                {
                    return resumed;
                }

                if (context.UiState.AutoCollectStatus == StatusComplete && Finish() is { } claimedResumed)
                {
                    return claimedResumed;
                }

                if (context.UiState.AutoCollectStatus != StatusIdle)
                {
                    return new GatherOutcome(null, [], [], "gather.notResumed");
                }
            }

            // one walk over the list queues every wanted material it meets (live 2026-10-15: a walk per material cost a
            // drag back to the top and the same pages again for each)
            var pending = targets.GroupBy(t => t.ItemId).Select(g => g.First()).ToList();
            var queued = new List<GatherTarget>();
            var atLimit = new List<GatherTarget>();
            if (WalkAll(pending, queued, atLimit) is { } walkStop)
            {
                return new GatherOutcome(walkStop, [], [], "gather.queueFailed") { Levels = _levels };
            }

            if (SurveyOnly)
            {
                // the survey's pickers were read and closed: nothing queued, nothing begun
                var known = Learned().Select(i => i.ItemId).ToHashSet();
                var (unseen, absent) = Unmet(pending, known, queueFull: false, _walkIncomplete);
                JobKit.Journal(context, JournalKind.Stock, Procedure, JobKit.Data(
                    ("event", "gather.surveyed"), ("levels", (long)_levels.Count), ("atLimit", (long)atLimit.Count), ("notSeen", (long)unseen.Count), ("notFound", (long)absent.Count)));
                return new GatherOutcome(null, [], absent, "gather.surveyed") { AtLimit = atLimit, NotSeen = unseen, Levels = _levels };
            }

            // a full queue stops the walk: what it did not reach is deferred, not "not found". A material the walk has
            // identified in this list before (its learned picture) that it did not meet now is "not seen", never "at its
            // limit" (2026-10-22: a walk cut short by failed page reads and drags left Scarlet Grape, 4 owned and in colour,
            // unmet; the old "unmet = greyed" rule capped the goal): it is tried again on the next trip, and a screenshot of
            // the helper is kept. "At limit" now comes only from a greyed tile recognised or a picker showing no room. The
            // others are not in the list at all.
            var deferred = queued.Count >= MaxTargets ? pending : [];
            var learnedIds = Learned().Select(i => i.ItemId).ToHashSet();
            // audit R1.5 (rule 2): only a walk that saw the whole list proves an unlearned material is not in it; after an
            // incomplete walk it is "not seen" (tried again next trip), never "not in the helper" for the whole job
            var (notSeen, notFound) = Unmet(pending, learnedIds, queued.Count >= MaxTargets, _walkIncomplete);
            foreach (var t in notSeen)
            {
                JobKit.Journal(context, JournalKind.Stock, Procedure, JobKit.Data(
                    ("event", "gather.notSeen"), ("itemId", t.ItemId), ("qty", (long)t.Qty), ("walk", _walkIncomplete ? "incomplete" : "complete")));
            }

            if (notSeen.Count > 0)
            {
                Snapshot();
            }

            foreach (var t in notFound)
            {
                Journal("gather.notFound", t);
            }

            if (queued.Count == 0)
            {
                var why = notFound.Count > 0 ? "gather.notFound" : atLimit.Count > 0 ? "gather.atLimit" : notSeen.Count > 0 ? "gather.notSeen" : "gather.enough";
                return new GatherOutcome(null, [], notFound, why) { AtLimit = atLimit, NotSeen = notSeen };
            }

            if (JobKit.CheckCommitGates(context, AutomationSwitch.Crafting) is not null)
            {
                return new GatherOutcome(null, [], notFound, "gather.switchOff");
            }

            // what the game accepted: a clamped target is credited at its real count, a target cut to 1 (no room: the
            // material is full) is taken out of the queue (live 2026-10-16: such a target made the helper pause)
            if (CheckQueue(queued, atLimit) is { } queueStop)
            {
                return queueStop with { NotFound = notFound };
            }

            if (queued.Count == 0)
            {
                return new GatherOutcome(null, [], notFound, "gather.atLimit") { AtLimit = atLimit };
            }

            if (EnsureHelper() is { } lost)
            {
                return lost with { NotFound = notFound };
            }

            var begin = Run(Steps(data, "begin"));
            if (begin.Status != ProcedureStatus.Succeeded)
            {
                return new GatherOutcome(StopOf(begin), [], notFound, "gather.notStarted");
            }

            if (!PollFor(StatusGathering, TimeSpan.FromSeconds(20)) && context.UiState.AutoCollectStatus != StatusComplete)
            {
                // the gear durability notice can pop up on Begin and hold the start until it is answered (2026-10-18)
                var notice = DismissGearNotice();
                if (notice is not null)
                {
                    return notice with { NotFound = notFound };
                }

                if (!SawGearNotice || (!PollFor(StatusGathering, TimeSpan.FromSeconds(20)) && context.UiState.AutoCollectStatus != StatusComplete))
                {
                    return new GatherOutcome(null, [], notFound, "gather.notStarted");
                }
            }

            if (WaitComplete(queued.Sum(t => t.Qty), queued) is { } waited)
            {
                return waited with { NotFound = notFound };
            }

            if (Finish() is { } finished)
            {
                return finished with { NotFound = notFound };
            }

            var filled = Credit(queued);
            return new GatherOutcome(null, queued, notFound, "gather.done") { AtLimit = atLimit, Filled = filled, Deferred = deferred, Levels = _levels };
        }

        /// <summary>Opens the helper unless its list is on screen; null when it is.</summary>
        private GatherOutcome? Open()
        {
            if (ListShown())
            {
                return null;
            }

            var opened = Run(Steps(data, "open", "announce.close", "tab", "tab.settle", "helper"));
            if (opened.Status != ProcedureStatus.Succeeded && !IsStop(opened))
            {
                // the Ability view may already be on the Gather tab (no unselected Gather tab to press)
                opened = Run(Steps(data, "helper"));
            }

            if (IsStop(opened))
            {
                return new GatherOutcome(StopOf(opened), [], [], "gather.notOpened");
            }

            return ListShown(settle: true) ? null : new GatherOutcome(null, [], [], "gather.notOpened");
        }

        /// <summary>
        /// False only when fresh captures prove the picker absent (ScreenMismatch, about 2 s); a capture that failed or a
        /// reader not ready decides nothing (true: the tip runs as before and its failure ends the walk).
        /// </summary>
        private bool PickerShown()
        {
            for (var i = 0; i < 4; i++)
            {
                var read = context.Screen.Read(PickerReader, ct);
                if (read.Status != ReadStatus.ScreenMismatch)
                {
                    return true;
                }

                context.Clock.Sleep(TimeSpan.FromMilliseconds(600), ct);
            }

            return false;
        }

        /// <summary>
        /// The helper's list is on screen; after an open (<paramref name="settle"/>) the read is retried for about 3 s
        /// (live 2026-10-15: a read 1 s after the show event still saw the opening animation).
        /// </summary>
        private bool ListShown(bool settle = false)
        {
            for (var i = 0; ; i++)
            {
                if (JobKit.Seen(context.Screen.Read(GridReader, ct)))
                {
                    return true; // a failed capture (the game minimized) is not "shown" (incident 22)
                }

                if (!settle || i >= 4)
                {
                    return false;
                }

                context.Clock.Sleep(TimeSpan.FromMilliseconds(700), ct);
            }
        }

        /// <summary>
        /// Walks the list from the top once and queues every target it meets: a greyed tile of a target is at its stock
        /// limit; any other tile that is a target or unknown is pressed and its picture's tip read (the log's id decides);
        /// the picker's "Stock Limit: a/b" gives the free room b - a, so a target is min(wanted, room) (none: at its
        /// limit) and the owned count a is recorded. Every probed material's owned count is recorded too. Null, or the
        /// stop of a procedure run.
        /// </summary>
        private JobResult? WalkAll(List<GatherTarget> pending, List<GatherTarget> queued, List<GatherTarget> atLimit)
        {
            // pass 1: loose picture matches (few presses); pass 2, only when a target the helper is known to list was not
            // met: strict matches only, so a picture that changed (live 2026-10-16: Wisdom Essence at 0 shows "Lv60"
            // without a count and loosely matched another blue bottle) is pressed and learned again
            var stop = WalkPass(pending, queued, atLimit, strict: false);
            var learnedIds = Learned().Select(i => i.ItemId).ToHashSet();
            if (stop is null && queued.Count < MaxTargets && pending.Any(t => learnedIds.Contains(t.ItemId)))
            {
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "gather.strictPass"), ("pending", (long)pending.Count)));
                stop = WalkPass(pending, queued, atLimit, strict: true);
            }

            return stop;
        }

        private JobResult? WalkPass(List<GatherTarget> pending, List<GatherTarget> queued, List<GatherTarget> atLimit, bool strict)
        {
            // each pass walks from the top: a later pass that reaches the end of the list makes the walk complete, whatever
            // an earlier pass hit (audit R2.11, incident 15: pass 1 "pageUnreadable", pass 2 to the end, still "incomplete")
            _walkIncomplete = false;
            if (ToTop() is { } topStop)
            {
                return topStop;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cutShort = false; // the last scroll drag was cut short: "nothing new" then proves nothing
            var retries = 0;
            for (var page = 0; page < MaxPages && pending.Count > 0 && queued.Count < MaxTargets; page++)
            {
                var (read, tries) = ReadPage();
                var pageTiles = Tiles(read);
                var recognised = new List<long>();
                if (pageTiles.Count == 0)
                {
                    // never the end of the list: the list always shows tiles; a read that finds none failed (2026-10-22)
                    _walkIncomplete = true;
                    JournalPage(page, strict, read, pageTiles, 0, tries, recognised);
                    JournalWalkEnd("pageUnreadable", page);
                    return null;
                }

                var anchor = read.Anchors is { } anchors && anchors.TryGetValue(Anchor, out var a) ? a : (PointI?)null;
                var fresh = 0;
                foreach (var (key, tile, faded) in pageTiles)
                {
                    if (pending.Count == 0 || queued.Count >= MaxTargets)
                    {
                        return null; // nothing left, or the helper's queue is full
                    }

                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    fresh++;
                    var match = StockIcons.Match([key], Learned(), SameKeyBits, RivalBits);
                    if (match.Item is { } recognisedItem)
                    {
                        recognised.Add(recognisedItem.ItemId);
                    }

                    var known = match.Item is { } k ? pending.FirstOrDefault(t => t.ItemId == k.ItemId) : null;
                    if (faded)
                    {
                        // a greyed tile is at its stock limit: the game ignores a press on it (live 2026-10-15). A greyed
                        // tile cannot be pressed, so its id is never confirmed: only a strict picture match concludes "at
                        // limit"; a loose one is unknown and the target stays pending (audit R2.6, rule 2). The distance
                        // is journaled either way, to tune the bound from real data
                        if (known is not null && FadedProvesLimit(match.Distance))
                        {
                            pending.Remove(known);
                            atLimit.Add(known);
                            JournalLimit(known, "fadedTile", page, tile, match.Distance, null, null);
                        }
                        else if (known is not null)
                        {
                            JobKit.Journal(context, JournalKind.Stock, Procedure, JobKit.Data(
                                ("event", "gather.fadedUnsure"), ("itemId", known.ItemId), ("page", (long)page), ("distance", (long?)match.Distance)));
                        }

                        continue;
                    }

                    var otherItem = match.Item is not null && known is null && (!strict || match.Distance <= StockIcons.SameIconMaxBits);
                    if (otherItem || ((match.Item is null || (strict && known is null)) && !_pressed.Add(key)))
                    {
                        continue; // another learned material, or pressed in this run already
                    }

                    var probe = Probe(key, tile, anchor);
                    if (probe.Stop is not null)
                    {
                        return probe.Stop;
                    }

                    if (!probe.PickerOpen)
                    {
                        continue; // the press opened no picker: nothing to close
                    }

                    var target = probe.Id is { } id ? pending.FirstOrDefault(t => t.ItemId == id) : null;
                    var room = probe.Limit?.Room;
                    if (SurveyOnly && target is not null && room is not <= 0)
                    {
                        // the survey: this target's level is read (in the probe); close the picker, queue nothing
                        pending.Remove(target);
                        var surveyed = Run(Steps(data, "set.close"));
                        if (surveyed.Status != ProcedureStatus.Succeeded)
                        {
                            return StopOf(surveyed) ?? JobKit.Failed("gather.pickerNotClosed");
                        }

                        continue;
                    }

                    if (target is { Need: { } need } && probe.Limit?.Owned is { } exactOwned && exactOwned >= need)
                    {
                        // the picker's exact count already covers the plan (the stored count was stale or unknown)
                        pending.Remove(target);
                        Journal("gather.enough", target);
                        var done = Run(Steps(data, "set.close"));
                        if (done.Status != ProcedureStatus.Succeeded)
                        {
                            return StopOf(done) ?? JobKit.Failed("gather.pickerNotClosed");
                        }

                        continue;
                    }

                    if (target is null || room <= 0)
                    {
                        if (target is not null)
                        {
                            pending.Remove(target);
                            atLimit.Add(target);
                            JournalLimit(target, "pickerNoRoom", page, tile, null, probe.Limit?.Owned, probe.Limit?.Limit);
                        }

                        var closed = Run(Steps(data, "set.close"));
                        if (closed.Status != ProcedureStatus.Succeeded)
                        {
                            return StopOf(closed) ?? JobKit.Failed("gather.pickerNotClosed");
                        }

                        continue;
                    }

                    var wanted = target.Need is { } total && probe.Limit?.Owned is { } have ? Math.Max(1, total - have) : target.Qty;
                    var qty = (int)Math.Min(Math.Min(wanted, 9_999), room ?? wanted);
                    var typed = Run(Steps(data, TypeSteps(qty)));
                    if (typed.Status != ProcedureStatus.Succeeded)
                    {
                        return StopOf(typed) ?? JobKit.Failed("gather.typeFailed");
                    }

                    pending.Remove(target);
                    var item = target with { Qty = qty };
                    queued.Add(item);
                    if (probe.Limit?.Owned is { } owned)
                    {
                        _owned[item.ItemId] = owned;
                    }

                    if (room is { } free)
                    {
                        _rooms[item.ItemId] = free;
                    }

                    JobKit.Journal(context, JournalKind.Stock, Procedure, JobKit.Data(
                        ("event", "gather.queued"), ("itemId", item.ItemId), ("qty", (long)qty), ("wanted", wanted),
                        ("owned", probe.Limit?.Owned), ("limit", probe.Limit?.Limit), ("room", room)));
                }

                JournalPage(page, strict, read, pageTiles, fresh, tries, recognised);
                var nothingNew = fresh == 0 && page > 0;
                if (nothingNew && cutShort && pending.Count > 0 && queued.Count < MaxTargets)
                {
                    // live 2026-10-31 15:07:28 UTC: the user's button cut a drag after 1 of its 12 steps, the list did not
                    // move, and "end of the list" hid the last page (the item was found only by the strict pass): drag again
                    if (++retries > MaxCutShortRetries)
                    {
                        _walkIncomplete = true; // what the walk did not meet is unknown, not absent
                        JournalWalkEnd("dragCutShort", page);
                        return null;
                    }

                    JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "gather.dragAgain"), ("page", (long)page), ("retries", (long)retries)));
                }
                else if (pending.Count == 0 || queued.Count >= MaxTargets || nothingNew)
                {
                    // all queued, or a whole drag showed nothing new: the end of the list
                    JournalWalkEnd(pending.Count == 0 ? "allQueued" : queued.Count >= MaxTargets ? "queueFull" : "endOfList", page);
                    return null;
                }
                else
                {
                    retries = 0;
                }

                var drag = Run(Steps(data, "scroll", "scroll.settle"));
                if (drag.Status != ProcedureStatus.Succeeded)
                {
                    _walkIncomplete = true;
                    JournalWalkEnd("scrollFailed", page);
                    return StopOf(drag) ?? JobKit.Failed("gather.scrollFailed");
                }

                cutShort = JobKit.DragCutShort(drag, "scroll");
            }

            _walkIncomplete = true;
            JournalWalkEnd("maxPages", MaxPages);
            return null;
        }

        /// <summary>The list read, retried twice when it finds no tile (a capture mid-animation or under a passing popup).</summary>
        private (ScreenReadResult Read, int Tries) ReadPage()
        {
            var read = context.Screen.Read(GridReader, ct);
            var tries = 1;
            while (Tiles(read).Count == 0 && tries < 3)
            {
                context.Clock.Sleep(TimeSpan.FromMilliseconds(500), ct);
                read = context.Screen.Read(GridReader, ct);
                tries++;
            }

            return (read, tries);
        }

        /// <summary>One walk page in the journal (2026-10-22, diagnostics): read status, tiles, greyed, new, recognised item ids.</summary>
        private void JournalPage(int page, bool strict, ScreenReadResult read, IReadOnlyList<(string Key, RectI Tile, bool Faded)> tiles, int fresh, int tries, List<long> recognised) =>
            JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(
                ("event", "gather.page"), ("page", (long)page), ("strict", strict), ("read", read.Status.ToString()), ("tries", (long)tries),
                ("tiles", (long)tiles.Count), ("greyed", (long)tiles.Count(t => t.Faded)), ("fresh", (long)fresh),
                ("recognised", string.Join("+", recognised.Select(id => id.ToString(CultureInfo.InvariantCulture))))));

        private void JournalWalkEnd(string reason, int page) =>
            JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "gather.walkEnd"), ("reason", reason), ("page", (long)page)));

        /// <summary>Why a material is taken as at its stock limit (2026-10-22): a recognised greyed tile, or a picker with no room.</summary>
        private void JournalLimit(GatherTarget t, string reason, int page, RectI tile, int? distance, long? owned, long? limit) =>
            JobKit.Journal(context, JournalKind.Stock, Procedure, JobKit.Data(
                ("event", "gather.atLimit"), ("itemId", t.ItemId), ("qty", (long)t.Qty), ("reason", reason), ("page", (long)page),
                ("tileX", (long)tile.Left), ("tileY", (long)tile.Top), ("distance", (long?)distance), ("owned", owned), ("limit", limit)));

        /// <summary>A screenshot of the helper for the diagnostics folder, once per run (a wanted material was not seen).</summary>
        private void Snapshot()
        {
            if (_snapshotTaken || !data.Steps.Any(s => s.Id == "snapshot"))
            {
                return;
            }

            _snapshotTaken = true;
            var shot = Run(Steps(data, "snapshot"));
            JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "gather.snapshot"), ("status", shot.Status.ToString())));
        }

        private bool _walkIncomplete;

        /// <summary>The walk did not see the whole list (<see cref="GatherOutcome.WalkIncomplete"/>).</summary>
        internal bool WalkIncomplete => _walkIncomplete;

        private bool _snapshotTaken;

        /// <summary>Drags the list down until a drag shows the same tiles (the top).</summary>
        private JobResult? ToTop()
        {
            var before = string.Join(",", Tiles(context.Screen.Read(GridReader, ct)).Select(t => t.Key));
            for (var i = 0; i < MaxTopDrags; i++)
            {
                var run = Run(Steps(data, "top", "scroll.settle"));
                if (run.Status != ProcedureStatus.Succeeded)
                {
                    return StopOf(run);
                }

                var after = string.Join(",", Tiles(context.Screen.Read(GridReader, ct)).Select(t => t.Key));
                // incident 31 (2026-10-28 13:06:45 UTC): a top drag the user's mouse cut short moved nothing, and the
                // unchanged tiles were taken for the top; only a whole drag that changes nothing proves it
                if (after == before && !JobKit.DragCutShort(run, "top"))
                {
                    // diagnostics (2026-10-22): an empty read on both sides also "matches"; the journal says which it was
                    JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(
                        ("event", "gather.top"), ("drags", (long)(i + 1)), ("emptyReads", after.Length == 0)));
                    if (after.Length == 0)
                    {
                        _walkIncomplete = true; // two empty reads "match" but prove nothing about the top (audit R1.5)
                    }

                    return null;
                }

                before = after;
            }

            // the top was never confirmed: what the walk does not meet is unknown, not absent (audit R1.5)
            _walkIncomplete = true;
            JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "gather.top"), ("drags", (long)MaxTopDrags), ("exhausted", true)));
            return null;
        }

        /// <summary>
        /// Presses a tile (its picker opens; the game logs the picker's show only the first time it builds it, 2026-10-15)
        /// and reads its picture's tip: the logged id, learned. PickerOpen is false when the tip run found no picker.
        /// </summary>
        private ProbeResult Probe(string key, RectI tile, PointI? anchor)
        {
            var at = anchor ?? new PointI(640, 476);
            var p = PressPoint(tile);
            var press = Steps(data, "tile");
            press = press with
            {
                Steps = [.. press.Steps.Select(s => s.Action is ClickAction c ? s with { Action = c with { Offset = new PointI(p.X - at.X, p.Y - at.Y) } } : s)],
            };
            var opened = Run(press);
            if (opened.Status != ProcedureStatus.Succeeded)
            {
                return new ProbeResult(StopOf(opened), null, false, null);
            }

            _probes++;
            var shown = PickerShown();
            if (!shown)
            {
                // incident 31 (2026-10-28 13:04:46 UTC): the user's mouse moved during the press and the game dropped it, so
                // a recognised target was never queued. The capture proved nothing open (incident 22's rule for the next
                // press), so the same tile is pressed once more; the engine waits for the mouse to rest
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "gather.noPicker"), ("tile", key), ("retry", true)));
                var again = Run(press);
                if (again.Status != ProcedureStatus.Succeeded)
                {
                    return new ProbeResult(StopOf(again), null, false, null);
                }

                shown = PickerShown();
            }

            if (!shown)
            {
                // incident 22 (2026-10-24): the press opened no picker (the user's mouse was busy); the tip, anchored on the
                // picker, would fail, and its engine reset closes the helper and spends a soft end. A capture that proves
                // the picker absent lets the walk go on: nothing is open for the next press to land on
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "gather.noPicker"), ("tile", key)));
                return new ProbeResult(null, null, false, null);
            }

            var tip = ItemTip.Read(context, tipData, ct);
            if (!tip.Succeeded)
            {
                // the tile press worked but its tip was not read: a picker may be open, and the next press would land on
                // it, so the walk ends here (safety review 2026-10-23)
                SawDeferred |= tip.Run.Status == ProcedureStatus.Deferred; // the tip runs outside Run()
                return new ProbeResult(StopOf(tip.Run) ?? JobKit.Failed("gather.tipFailed"), null, false, null);
            }

            var limit = ReadLimit();
            if (tip.Tip is not { ItemId: > 0 } t)
            {
                return new ProbeResult(null, null, true, limit);
            }

            var name = t.ItemName?.Trim() ?? string.Empty;
            try
            {
                _knowledge.LearnIconItem(new IconItem(key, t.ItemId, name, context.Clock.UtcNow) { Place = Place });
            }
            catch (ArgumentException)
            {
                // not a valid item name: the picture stays unlearned, the id still decides this press
            }

            if (limit?.Owned is { } seenOwned && ItemKey.FromDisplayName(name) is { Value.Length: > 0 } observed && context.UiState.AsOf is { } logAt)
            {
                // the picker shows the exact owned count: a free stock reading for every material the walk presses
                _knowledge.ApplyObservations([new StockObservation(observed, seenOwned, null, ObservationKind.StockTab, context.Clock.UtcNow, logAt, 0.95, "gather.picker", t.ItemId)]);
            }

            _levels[t.ItemId] = new StockLevel(limit?.Owned, limit?.Limit);
            JobKit.Journal(context, JournalKind.Stock, Procedure, JobKit.Data(
                ("event", "gather.probe"), ("itemId", t.ItemId), ("probes", (long)_probes), ("owned", limit?.Owned), ("limit", limit?.Limit), ("room", limit?.Room)));
            return new ProbeResult(null, t.ItemId, true, limit);
        }

        /// <summary>
        /// Reads the queue slots (<see cref="QueueReader"/>; the walk queued <paramref name="queued"/> in order into an
        /// empty queue) and compares each target with what the game accepted: a smaller count replaces the typed one
        /// (journal gather.clamped); a target of 1 for more is a full material: its "-" is pressed (last slot first, the
        /// slots after it shift left) and it moves to <paramref name="atLimit"/>. A slot that reads nothing changes
        /// nothing. Null to go on, else the walk's stop.
        /// </summary>
        private GatherOutcome? CheckQueue(List<GatherTarget> queued, List<GatherTarget> atLimit)
        {
            var read = context.Screen.Read(QueueReader, ct);
            if (read.Status == ReadStatus.ScreenMismatch)
            {
                return null;
            }

            var removed = false;
            for (var i = Math.Min(queued.Count, MaxTargets) - 1; i >= 0; i--)
            {
                var t = queued[i];
                var text = read.Values.FirstOrDefault(v => v.RegionId == "slot" + (i + 1).ToString(CultureInfo.InvariantCulture)) is { Status: ReadStatus.Ok, Text: { } x } ? x : null;
                if (ParseSlot(text) is not { } accepted || accepted >= t.Qty)
                {
                    continue;
                }

                JobKit.Journal(context, JournalKind.Stock, Procedure, JobKit.Data(("event", "gather.clamped"), ("itemId", t.ItemId), ("typed", (long)t.Qty), ("accepted", accepted)));
                if (accepted <= 1 && t.Qty > 1)
                {
                    var unqueue = Run(Steps(data, "unqueue." + (i + 1).ToString(CultureInfo.InvariantCulture), "unqueue.settle"));
                    if (IsStop(unqueue))
                    {
                        return new GatherOutcome(StopOf(unqueue), [], [], "gather.unqueueStopped");
                    }

                    queued.RemoveAt(i);
                    atLimit.Add(t);
                    removed = true;
                    Journal("gather.unqueued", t);
                    continue;
                }

                queued[i] = t with { Qty = (int)accepted };
            }

            if (removed)
            {
                // the removals are checked: a slot count that does not match the queue means a press went wrong
                var again = context.Screen.Read(QueueReader, ct);
                var shown = again.Values.Count(v => v.RegionId.StartsWith("slot", StringComparison.Ordinal) && v.Status == ReadStatus.Ok && ParseSlot(v.Text) is not null);
                if (JobKit.Seen(again) && shown != queued.Count)
                {
                    JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "gather.unqueueMismatch"), ("shown", (long)shown), ("queued", (long)queued.Count)));
                }
            }

            return null;
        }

        /// <summary>Polls the status until complete; null when it is, else an outcome (timeout, cancelled).</summary>
        private GatherOutcome? WaitComplete(int items, IReadOnlyList<GatherTarget>? queued = null)
        {
            // the status card says what the character is out gathering, and since when (2026-10-23, the user: "gather
            // everything doesn't do anything clear": during a trip the card only said "between steps")
            context.ReportActivity("step", "gather.trip", JobKit.Data(
                ("items", queued is { Count: > 0 } ? Codes.Safe(string.Join(", ", queued.Select(t => t.Name + " x" + t.Qty.ToString(CultureInfo.InvariantCulture)))) : null),
                ("sinceUtc", context.Clock.UtcNow)));
            try
            {
                return WaitTrip(items);
            }
            finally
            {
                context.ReportActivity("step", null);
            }
        }

        private GatherOutcome? WaitTrip(int items)
        {
            var limit = context.Clock.Monotonic + WaitLimit(items);
            if (items > 0)
            {
                _tripDue = limit;
            }
            else if (_tripDue is { } due && due > limit)
            {
                limit = due; // a trip from before: the rest of its own time (incident 23)
            }

            var grace = TimeSpan.Zero;
            var lastReport = context.Clock.Monotonic;
            var lastNoticeCheck = context.Clock.Monotonic;
            TimeSpan? pausedSince = null;
            while (context.Clock.Monotonic < limit)
            {
                if (context.Clock.Monotonic - lastNoticeCheck >= GearNoticeEvery)
                {
                    lastNoticeCheck = context.Clock.Monotonic;
                    var noticesBefore = NoticesAnswered;
                    if (DismissGearNotice() is { } stopped)
                    {
                        return stopped;
                    }

                    if (NoticesAnswered > noticesBefore && grace < TimeSpan.FromMinutes(30))
                    {
                        // the notice holds gathering while it is up: the trip gets that time back (incident 20: two trips
                        // outlived their limit while the notice came back every 72 s)
                        grace += NoticeGrace;
                        limit += NoticeGrace;
                        _tripDue = limit;
                    }
                }

                var status = context.UiState.AutoCollectStatus;
                if (status == StatusComplete)
                {
                    _tripDue = null;
                    SawCompletion = true;
                    return null;
                }

                // paused by the game on the way (status 3): not waited for until the long limit; the next round continues it
                pausedSince = status == StatusPaused ? pausedSince ?? context.Clock.Monotonic : null;
                if (pausedSince is { } since && context.Clock.Monotonic - since >= PausedGiveUp)
                {
                    return new GatherOutcome(null, [], [], "gather.paused");
                }

                if (status == StatusIdle)
                {
                    _tripDue = null;
                    return new GatherOutcome(null, [], [], "gather.cancelled"); // someone finished or stopped it
                }

                if (context.Clock.Monotonic - lastReport >= TimeSpan.FromMinutes(1))
                {
                    lastReport = context.Clock.Monotonic;
                    JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "gather.waiting"), ("status", (long)(status ?? -1))));
                }

                context.Clock.Sleep(PollEvery, ct);
            }

            // incident 23: the character still gathering at the limit is not a failed trip (the materials are coming); the
            // caller comes back for it later
            return new GatherOutcome(null, [], [], context.UiState.AutoCollectStatus == StatusGathering ? StillGathering : "gather.timeout");
        }

        /// <summary>
        /// Makes sure the helper's list is on screen before Begin or Finish (live 2026-10-16: the game's own team
        /// recommendation UI_TEAM_REOCMMEND_VIEW popped up over the helper right after a target was confirmed; the Begin
        /// press found no button and the engine's reset closed everything): a stray Esc-safe popup on top gets one Esc
        /// (at most twice), a closed helper is opened again (the game keeps its queue). Null when the list is shown.
        /// </summary>
        private GatherOutcome? EnsureHelper()
        {
            if (DismissGearNotice() is { } stopped)
            {
                return stopped;
            }

            for (var i = 0; i < 2 && !ListShown(settle: true); i++)
            {
                var top = context.UiState.AdvisoryTop;
                if (top is null || top is "UI_LIFESKILL_AUTO_COLLECT" || !UiCatalog.EscSafe.Contains(top))
                {
                    break;
                }

                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "gather.strayPopup"), ("ui", top)));
                var esc = Run(Steps(data, "esc"));
                if (IsStop(esc))
                {
                    return new GatherOutcome(StopOf(esc), [], [], "gather.notOpened");
                }
            }

            return ListShown(settle: true) ? null : Open();
        }

        /// <summary>
        /// Dismisses the gear durability notice for the day when it is on screen (see <see cref="GearNoticeReader"/>):
        /// journals "gear.notice", tells the user, ticks the box and presses Ignore. Null unless a stop ended the press.
        /// </summary>
        private GatherOutcome? DismissGearNotice()
        {
            var run = Gathering.DismissGearNotice(context, ct);
            SawDeferred |= run?.Status == ProcedureStatus.Deferred;
            SawGearNotice |= run?.Status == ProcedureStatus.Succeeded;
            NoticesAnswered += run?.Status == ProcedureStatus.Succeeded ? 1 : 0;
            if (run is not null && IsStop(run))
            {
                return new GatherOutcome(StopOf(run), [], [], "gather.gearNotice");
            }

            if (run?.Status == ProcedureStatus.Succeeded && GearNoticeStorm(context.Clock.Monotonic))
            {
                // 2026-10-31 audit (10-28: 26 notices, 52 clicks in 8 minutes): worn gear keeps holding the trip; gathering
                // stops for this job with one clear message (the goal crafts from stock)
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "gear.noticeStorm")));
                context.Notifier.Notify(new Notification(NotificationKind.NeedsUser, "gear.noticeStorm", GearStormText));
                return new GatherOutcome(null, [], [], "gather.gearNoticeStorm");
            }

            return null;
        }

        /// <summary>Claims a complete queue (Finish); null when the status went idle.</summary>
        private GatherOutcome? Finish()
        {
            if (EnsureHelper() is { } stop)
            {
                return stop with { Code = "gather.finishFailed" };
            }

            // incident 19 (2026-10-23 20:16): both Finish presses went out while the user's mouse moved (abort CursorMoved,
            // button down) and did not register; the step has no log line to verify, so the press read as done and the
            // queue stayed complete. A queue the log still reports complete after Finish gets one more press (the press is
            // aimed at the Finish button's picture, so it happens only while that button is on screen)
            for (var press = 1; press <= 2; press++)
            {
                var run = Run(Steps(data, "finish", "finish.settle"));
                if (run.Status != ProcedureStatus.Succeeded)
                {
                    return new GatherOutcome(StopOf(run), [], [], "gather.finishFailed");
                }

                if (PollFor(StatusIdle, TimeSpan.FromSeconds(10)))
                {
                    return null;
                }

                if (context.UiState.AutoCollectStatus != StatusComplete)
                {
                    break; // not complete either (paused, gathering again): not a missed press
                }

                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "gather.finishRetry"), ("press", (long)press)));
            }

            return new GatherOutcome(null, [], [], "gather.finishFailed");
        }

        private bool PollFor(int status, TimeSpan within)
        {
            var limit = context.Clock.Monotonic + within;
            while (context.UiState.AutoCollectStatus != status)
            {
                if (context.Clock.Monotonic >= limit)
                {
                    return false;
                }

                context.Clock.Sleep(TimeSpan.FromMilliseconds(500), ct);
            }

            return true;
        }

        /// <summary>
        /// The gathered amounts go into the known stock (at least the target was gathered; one pick can give more): a derived
        /// +qty on the item's last observation, or a first observation of qty when it was never counted, so the next plan
        /// does not gather it again. The Make press stays the final truth (a missing item is named in the log).
        /// </summary>
        private List<GatherTarget> Credit(IReadOnlyList<GatherTarget> gathered)
        {
            var filled = new List<GatherTarget>();
            if (context.UiState.AsOf is not { } at)
            {
                return filled;
            }

            foreach (var t in gathered)
            {
                var item = ItemKey.FromDisplayName(t.Name);
                if (item.Value.Length == 0)
                {
                    continue;
                }

                if (_rooms.TryGetValue(t.ItemId, out var room) && t.Qty >= room)
                {
                    filled.Add(t); // at its stock limit now: greyed in the list until crafting uses some
                }

                if (_owned.TryGetValue(t.ItemId, out var owned))
                {
                    // the picker's owned count plus the target (one pick can give 1-2 more: a lower bound)
                    _knowledge.ApplyObservations([new StockObservation(item, owned + t.Qty, null, ObservationKind.StockTab, context.Clock.UtcNow, at, 0.9, "gather.done", t.ItemId)]);
                    continue;
                }

                if (_knowledge.ApplyDerived(new DerivedStockDelta(item, t.Qty, at, "gather.done")))
                {
                    continue;
                }

                _knowledge.ApplyObservations([new StockObservation(item, t.Qty, null, ObservationKind.CraftViewPair, context.Clock.UtcNow, at, 0.5, "gather.done", t.ItemId)]);
            }

            return filled;
        }

        /// <summary>
        /// The open picker's "Stock Limit: a/b" (owned a, limit b), or null. The game logs the picker's show only the first
        /// time it builds it, so the line is read with the picker or the helper as the log's top UI.
        /// </summary>
        private PickerStock? ReadLimit()
        {
            foreach (var reader in SetReaders)
            {
                var read = context.Screen.Read(reader, ct);
                if (read.Status == ReadStatus.ScreenMismatch)
                {
                    continue;
                }

                var limit = read.Values.FirstOrDefault(v => v.RegionId == "limit") is { Status: ReadStatus.Ok, Text: { } text } ? ParseLimit(text) : null;
                var box = read.Values.FirstOrDefault(v => v.RegionId == "room") is { Status: ReadStatus.Ok, Text: { } digits } ? ParseRoom(digits) : null;
                // only the "Stock Limit" line gives the exact room; the quantity box's default is not always the room
                // (live 2026-10-16: 60 for Wisdom Essence at 0/300), so without the line the game clamps the typed target
                var room = limit is { } l ? l.Limit - l.Owned : (long?)null;
                return limit is null && box is null ? null : new PickerStock(limit?.Owned, limit?.Limit, room);
            }

            return null;
        }

        private List<IconItem> Learned() => [.. _knowledge.GetIconItems().Where(i => i.Place == Place)];

        /// <summary>A run of this walk ended Deferred (<see cref="GatherOutcome.EngineDeferred"/>).</summary>
        public bool SawDeferred { get; private set; }

        /// <summary>A trip this run waited for completed (<see cref="GatherOutcome.TripCompleted"/>).</summary>
        public bool SawCompletion { get; private set; }

        /// <summary>The gear notice was found and dismissed during this run.</summary>
        private bool SawGearNotice { get; set; }

        /// <summary>Gear notices this run answered (each one gives the trip wait more time).</summary>
        private int NoticesAnswered { get; set; }

        private ProcedureResult Run(ProcedureDefinition p)
        {
            var r = context.Procedures.Run(p, context.Run, ct);
            SawDeferred |= r.Status == ProcedureStatus.Deferred;
            return r;
        }

        private void Journal(string code, GatherTarget t) =>
            JobKit.Journal(context, JournalKind.Stock, Procedure, JobKit.Data(("event", code), ("itemId", t.ItemId), ("qty", (long)t.Qty)));

        /// <summary>
        /// A halt, stop, cancel or deferral ends the walk at once (2026-10-16 safety review: a walk that pressed on after a
        /// deferred press paid the unreachable limit per tile and could pile soft ends up into a HALT; the caller pauses
        /// and comes back).
        /// </summary>
        private static bool IsStop(ProcedureResult r) => r.Status is ProcedureStatus.Halted or ProcedureStatus.Stopped or ProcedureStatus.Cancelled or ProcedureStatus.Deferred;

        /// <summary>A halt, stop, cancel or deferral ends the walk (a deferral is a Failed "gather.deferred:..." for the caller, not a stop of its job); any other failure is a code only.</summary>
        private static JobResult? StopOf(ProcedureResult r) => IsStop(r) ? JobKit.FromProcedure(r, "gather.") : null;

        private static List<(string Key, RectI Tile, bool Faded)> Tiles(ScreenReadResult read)
        {
            var tiles = new List<(string, RectI, bool)>();
            foreach (var v in read.Values)
            {
                if (v.RegionId.StartsWith("icon.", StringComparison.Ordinal) && v is { Status: ReadStatus.Ok, Text: { } key, Rect: { } rect } && IconKeys.IsIconKey(key))
                {
                    tiles.Add((key, rect, v.DetailCode == "icon-faded"));
                }
            }

            return tiles;
        }
    }
}
