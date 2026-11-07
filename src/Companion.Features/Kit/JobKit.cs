using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Host;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Perception;
using Companion.Contracts.Safety;

namespace Companion.Features.Kit;

/// <summary>
/// Shared job helpers for handlers: result mapping, the job-level gates (automation switch, character permission,
/// knowledge) and the needsSurvey stub check that refuses BEFORE any procedure runs (zero inputs). The engine applies
/// the same gates again at the action level (GATE-01); these are the feature's own, earlier refusals.
/// </summary>
internal static class JobKit
{
    /// <summary>
    /// A plan's identity for the no-progress rule (audit R2.3, "same plan, no progress, twice"): its crafts in order with
    /// their batches, its shortfalls with their amounts, and its issue kinds.
    /// </summary>
    internal static string PlanFingerprint(Companion.Contracts.Planning.CraftPlan plan) =>
        string.Join(";", plan.Crafts.OrderBy(c => c.Order).Select(c => c.Product.Value + "x" + c.Batches.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        + "|" + string.Join(";", plan.Shortfalls.Select(s => s.Item.Value + "x" + s.Missing.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        + "|" + string.Join(",", plan.Issues.Select(i => i.Kind.ToString()).Distinct());

    /// <summary>
    /// The back-off of a rule-mode rule whose goal made nothing for a reason other than a deferral (audit R2.9: a blocked
    /// rule was walked, read and planned every 10 minutes with nothing journaled about why): 10, 20, 40, then 60 minutes.
    /// </summary>
    internal static TimeSpan RuleBackoff(int times) => TimeSpan.FromMinutes(Math.Min(60, 10 * Math.Pow(2, Math.Max(0, times - 1))));

    /// <summary>
    /// A goal loop's retries must stay inside the engine's soft-end allowance (<see cref="HardRules.SoftEndsBeforeHalt"/>
    /// exhausted recoveries per <see cref="HardRules.SoftEndWindow"/>, then a HALT): true at the second exhausted deferral
    /// inside the window, when the loop ends with its cause instead of retrying (audit R1.10, rule 6).
    /// </summary>
    internal static bool SoftEndsNearLimit(IReadOnlyList<TimeSpan> exhausted, TimeSpan now) =>
        exhausted.Count(t => now - t <= HardRules.SoftEndWindow) >= 2;

    /// <summary>
    /// The soft ends a job paces by: the engine's (every job's, <see cref="IJobContext.EngineSoftEnds"/>) when they are more
    /// inside the window than the job's own list, else its own (2026-10-31 audit: a goal right after a goal that ended
    /// "exhausted" believed it had the whole allowance).
    /// </summary>
    internal static IReadOnlyList<TimeSpan> Ledger(IJobContext context, IReadOnlyList<TimeSpan> own, TimeSpan now)
    {
        var engine = context.EngineSoftEnds;
        return engine.Count(t => now - t <= HardRules.SoftEndWindow) > own.Count(t => now - t <= HardRules.SoftEndWindow) ? engine : own;
    }

    /// <summary>The user's message when a job pauses because the mouse rests on another monitor (incident 24).</summary>
    internal const string OutOfReachText = "The companion is waiting: it cannot click the game while your mouse rests on another monitor. Leave the mouse anywhere on the game's monitor and it carries on.";

    /// <summary>
    /// The user's message for every other out-of-reach pause (incident 27, 2026-10-27: "CoverRejected" for 40 minutes, and the
    /// notice spoke of another monitor). The companion never moves the mouse: it slides the game, hidden, under the
    /// window the mouse rests on, and that window must be the active one, in front of the game, with the mouse well inside it.
    /// </summary>
    internal const string OutOfCoverText = "The companion is waiting: it cannot click the game from where your mouse rests. Leave the mouse in the middle of the window you last clicked, in front of the game on the game's monitor (a browser window, for example), not on the game itself, a window edge or the taskbar, and it carries on.";

    /// <summary>
    /// How long a job keeps waiting, without a trip or a craft, when only the mouse's resting place keeps it from clicking
    /// (incident 27: a goal with all its materials gathered gave up after 8 pauses, 40 minutes, while the user was away).
    /// These ends never count toward the engine's soft-end allowance, so a long wait cannot become a HALT.
    /// </summary>
    internal static readonly TimeSpan OutOfReachWindow = TimeSpan.FromHours(3);

    /// <summary>The cause is the mouse resting where no press reaches the game (the engine's "unreachable." ends).</summary>
    internal static bool OutOfReach(string? cause) => cause is not null && cause.Contains("unreachable", StringComparison.Ordinal);

    /// <summary>
    /// Tells the user once per job why it pauses when the cause is the mouse resting out of the game's reach (incident 24,
    /// 2026-10-25: Finish and Make were out of reach for 11 minutes and nothing said so). True when it told.
    /// </summary>
    internal static bool TellOutOfReach(IJobContext context, string? cause, bool toldBefore)
    {
        if (toldBefore || !OutOfReach(cause))
        {
            return false;
        }

        var text = cause!.Contains("CrossMonitor", StringComparison.Ordinal) ? OutOfReachText : OutOfCoverText;
        context.Notifier.Notify(new Notification(NotificationKind.NeedsUser, "job.outOfReach", text));
        return true;
    }

    /// <summary>
    /// A loop's <paramref name="pause"/> before it tries a deferred step again, lengthened when it is near the engine's
    /// soft-end allowance (<see cref="SoftEndsNearLimit"/>): until all but the newest of its exhausted recoveries are out of
    /// the 30-minute window, so the retry can never be the engine's 4th soft end, a HALT (incident 22, 2026-10-24: Gather
    /// everything ended instead of waiting).
    /// </summary>
    internal static TimeSpan ClearOfSoftEnds(TimeSpan pause, IReadOnlyList<TimeSpan> exhausted, TimeSpan now)
    {
        var inWindow = exhausted.Where(t => now - t <= HardRules.SoftEndWindow).Order().ToList();
        if (inWindow.Count < 2)
        {
            return pause;
        }

        var clear = inWindow[^2] + HardRules.SoftEndWindow + TimeSpan.FromSeconds(10) - now;
        return clear > pause ? clear : pause;
    }

    /// <summary>
    /// The stored exact count of <paramref name="item"/> when it is fresh: observed after the latest stale mark and not past
    /// its fresh window. Null when there is no entry, only a range, or the count is outdated: unknown, never 0 (design rules
    /// 2 and 3, audit R1.8).
    /// </summary>
    internal static long? FreshCount(IJobContext context, ItemKey item)
    {
        var stock = context.Knowledge?.GetStock();
        if (stock is null || !stock.Items.TryGetValue(item, out var e) || e.Count is not { } c)
        {
            return null;
        }

        return e.IsStaleAt(context.Clock.UtcNow) || (stock.StaleSince is { } since && e.ObservedAtUtc <= since) ? null : c;
    }

    /// <summary>Max CallProcedure nesting followed by the stub check (engine max depth is 3).</summary>
    private const int MaxCallDepth = 3;

    /// <summary>A result with a code and optional scalar data.</summary>
    public static JobResult Result(JobState state, string code, IReadOnlyDictionary<string, object?>? data = null) =>
        new(state, Codes.Safe(code), data);

    /// <summary>Refused (whitelist, params, switch, character, rate).</summary>
    public static JobResult Refused(string code, IReadOnlyDictionary<string, object?>? data = null) =>
        Result(JobState.Refused, code, data);

    /// <summary>
    /// The reader's screen was confirmed in a real capture: Ok, or Unknown with values or anchors (shown, a value unclear).
    /// A failed capture reads Unknown with nothing in it: not seen, and no proof of absence either (only ScreenMismatch is).
    /// Incident 22 (2026-10-24): with the game minimized, "not ScreenMismatch" was taken as "the Gathering Helper is open",
    /// the walk's first drag waited 3.5 hours for the window and then found no helper.
    /// </summary>
    internal static bool Seen(ScreenReadResult read) =>
        read.Status == ReadStatus.Ok || (read.Status == ReadStatus.Unknown && (read.Values.Count > 0 || read.Anchors is { Count: > 0 }));

    /// <summary>Failed without HALT.</summary>
    public static JobResult Failed(string code, IReadOnlyDictionary<string, object?>? data = null) =>
        Result(JobState.Failed, code, data);

    /// <summary>
    /// A HALT decided by feature logic (e.g. CraftStoppedEarlyTwice, BlockedCraftSpot). The code is the
    /// <see cref="HaltCode"/> name, so the host can latch it (features cannot reach the HALT latch).
    /// </summary>
    public static JobResult Halted(HaltCode code, IReadOnlyDictionary<string, object?>? data = null) =>
        Result(JobState.Halted, code.ToString(), data);

    /// <summary>NeedsSurvey listing what must be surveyed (codes).</summary>
    public static JobResult NeedsSurvey(IReadOnlyList<string> whats, IReadOnlyDictionary<string, object?>? extra = null)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal) { ["inputs"] = 0L, ["stubs"] = (long)whats.Count };
        if (extra is not null)
        {
            foreach (var (k, v) in extra)
            {
                data[k] = v;
            }
        }

        return Result(JobState.NeedsSurvey, Codes.Join("needsSurvey:", whats), data);
    }

    /// <summary>Maps a procedure result to a job result (codes and counts only).</summary>
    public static JobResult FromProcedure(ProcedureResult r, string? codePrefix = null)
    {
        var (state, code) = r.Status switch
        {
            ProcedureStatus.Succeeded => (JobState.Succeeded, "ok"),
            ProcedureStatus.Parked => (JobState.Parked, "parked"),
            ProcedureStatus.Halted => (JobState.Halted, r.Halt?.Code.ToString() ?? "halted"),
            ProcedureStatus.Stopped => (JobState.Stopped, "stopped"),
            // the engine's own end keeps its reason (audit R3.2 #32: park.expired, commit.uncertain ... all read "cancelled",
            // shown as "Stopped at your request")
            ProcedureStatus.Cancelled => (JobState.Cancelled, string.IsNullOrEmpty(r.EndDetail) || r.EndDetail == "cancelled" ? "cancelled" : "cancelled:" + Codes.Safe(r.EndDetail)),
            // the engine's last fallback rung: not a stop; the caller does other work and tries again later
            ProcedureStatus.Deferred => (JobState.Failed, string.IsNullOrEmpty(r.EndDetail) ? "deferred" : "deferred:" + Codes.Safe(r.EndDetail)),
            ProcedureStatus.GameExited => (JobState.GameExited, "gameExited"),
            ProcedureStatus.Refused => (JobState.Refused, WithDetail("refused", r)),
            ProcedureStatus.NeedsSurvey => (JobState.NeedsSurvey, WithDetail("needsSurvey", r)),
            _ => (JobState.Failed, "procedure.status.unknown"),
        };

        if (codePrefix is not null && state != JobState.Halted)
        {
            code = codePrefix + code;
        }

        return Result(state, code, ProcedureData(r));
    }

    /// <summary>Scalar summary of a procedure run.</summary>
    public static Dictionary<string, object?> ProcedureData(ProcedureResult r)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["procedure"] = Codes.Safe(r.ProcedureId),
            ["status"] = r.Status.ToString(),
            ["inputs"] = (long)r.InputBatches,
            ["clicks"] = (long)r.Clicks,
            ["esc"] = (long)r.EscPresses,
            ["attempts"] = (long)r.Attempts.Count,
            ["artifacts"] = (long)r.Artifacts.Count,
            ["pending"] = (long)r.Pending.Count,
        };
        if (r.FinalOutcome is { } o)
        {
            data["outcome"] = o.ToString();
        }

        if (r.Halt is { } h)
        {
            data["halt"] = h.Code.ToString();
        }

        return data;
    }

    /// <summary>
    /// Commit-job gate (R10): the automation switch must be on (since 4.33 no per-character permission: one profile for
    /// every character). Returns null when it passes, else a Refused result. Checked before anything else, so a
    /// switched-off job runs nothing.
    /// </summary>
    public static JobResult? CheckCommitGates(IJobContext ctx, AutomationSwitch automationSwitch)
    {
        if (automationSwitch == AutomationSwitch.None)
        {
            return Refused("gate.commitWithoutSwitch");
        }

        if (!ctx.Switches.IsOn(automationSwitch))
        {
            return Refused("switch.off:" + automationSwitch.ToString().ToLowerInvariant());
        }

        return null;
    }

    /// <summary>Jobs that read or write the store need it opened (null only when the store failed to open).</summary>
    public static JobResult? RequireKnowledge(IJobContext ctx) =>
        ctx.Knowledge is null ? Refused("knowledge.unavailable") : null;

    /// <summary>
    /// Checks the procedures a job will run, before any of them runs: a missing procedure fails the job; a reachable
    /// <see cref="NeedsSurveyAction"/> (non-Optional step, following CallProcedure up to depth 3) ends it NeedsSurvey;
    /// a click step while slide clicks are off refuses it. Returns null when every procedure may run.
    /// </summary>
    public static JobResult? CheckProcedures(IJobContext ctx, IReadOnlyList<string> procedureIds)
    {
        var missing = new List<string>();
        var stubs = new List<string>();
        var clicks = false;
        foreach (var id in procedureIds)
        {
            var p = ctx.ProcedureCatalog.Get(id);
            if (p is null)
            {
                missing.Add(id);
                continue;
            }

            Walk(ctx.ProcedureCatalog, p, 0, stubs, missing, ref clicks, new HashSet<string>(StringComparer.Ordinal));
        }

        if (missing.Count > 0)
        {
            return Failed(Codes.Join("procedure.missing:", missing.Distinct(StringComparer.Ordinal)));
        }

        if (stubs.Count > 0)
        {
            return NeedsSurvey(stubs.Distinct(StringComparer.Ordinal).ToList());
        }

        if (clicks && !ctx.Switches.SlideClickEnabled)
        {
            return Refused("slideClick.off");
        }

        return null;
    }

    /// <summary>needsSurvey codes reachable in a procedure (non-Optional steps; CallProcedure followed).</summary>
    public static IReadOnlyList<string> FindNeedsSurvey(IProcedureCatalog catalog, ProcedureDefinition procedure)
    {
        var stubs = new List<string>();
        var missing = new List<string>();
        var clicks = false;
        Walk(catalog, procedure, 0, stubs, missing, ref clicks, new HashSet<string>(StringComparer.Ordinal));
        return stubs;
    }

    /// <summary>True if every step only captures (no key, click, toggle, reset, navigate, read, call, await).</summary>
    public static bool IsPassiveCaptureOnly(ProcedureDefinition procedure) =>
        procedure.Steps.Count > 0
        && procedure.Kind != ProcedureKind.Commit
        && procedure.Steps.All(s => s.Action is CaptureAction or WaitAction && !s.Flags.HasFlag(StepFlags.Commit));

    /// <summary>
    /// The read stored under <paramref name="key"/> by a succeeded procedure. Missing = Failed; NeedsSurvey reader =
    /// NeedsSurvey; screen identity not confirmed = Failed (never a HALT, SPEC 6.4 ReadAction). Ok and Unknown pass
    /// (Unknown values are skipped value by value).
    /// </summary>
    public static JobResult? CheckRead(ProcedureResult run, string key, out ScreenReadResult? read)
    {
        read = null;
        if (!run.Reads.TryGetValue(key, out var r) || r is null)
        {
            return Failed("read.missing:" + key, ProcedureData(run));
        }

        read = r;
        return r.Status switch
        {
            ReadStatus.NeedsSurvey => NeedsSurvey([r.ReaderId], ProcedureData(run)),
            ReadStatus.ScreenMismatch => Failed("read.screenMismatch:" + r.ReaderId, ProcedureData(run)),
            _ => null,
        };
    }

    /// <summary>Journals a record with code fields (never throws into control flow).</summary>
    public static void Journal(IJobContext ctx, JournalKind kind, string? procedureId, IReadOnlyDictionary<string, object?> fields)
    {
        try
        {
            ctx.Journal.Write(new JournalRecord(ctx.Clock.UtcNow, kind, ctx.Run.JobId, procedureId, null, fields));

            // a job writes between procedures (no slide in progress): to disk at once (2026-10-16: during a gathering trip
            // or a pause no procedure ended, so the buffered records reached the file only later; the file looked silent
            // for 15+ minutes and the supervisor cancelled healthy jobs)
            ctx.Journal.Flush();
        }
        catch (Exception)
        {
            // The journal never throws by contract; a broken fake or writer must not fail the job.
        }
    }

    /// <summary>Data dictionary helper.</summary>
    public static Dictionary<string, object?> Data(params (string Key, object? Value)[] items)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in items)
        {
            d[k] = v;
        }

        return d;
    }

    private static void Walk(IProcedureCatalog catalog, ProcedureDefinition p, int depth, List<string> stubs, List<string> missing, ref bool clicks, HashSet<string> seen)
    {
        if (!seen.Add(p.Id))
        {
            return;
        }

        foreach (var step in p.Steps)
        {
            if (step.Flags.HasFlag(StepFlags.Optional))
            {
                continue;
            }

            switch (step.Action)
            {
                case NeedsSurveyAction n:
                    stubs.Add(string.IsNullOrWhiteSpace(n.What) ? step.Id : n.What);
                    break;
                case ClickAction or ToggleHudAction or DragAction:
                    clicks = true;
                    break;
                case CallProcedureAction call when depth < MaxCallDepth:
                    var sub = catalog.Get(call.ProcedureId);
                    if (sub is null)
                    {
                        missing.Add(call.ProcedureId);
                    }
                    else
                    {
                        Walk(catalog, sub, depth + 1, stubs, missing, ref clicks, seen);
                    }

                    break;
            }
        }
    }

    private static string WithDetail(string head, ProcedureResult r)
    {
        var detail = r.Attempts.Count > 0 ? r.Attempts[^1].DetailCode : null;
        return string.IsNullOrEmpty(detail) ? head : head + ":" + Codes.Safe(detail);
    }

    /// <summary>
    /// The drag of step <paramref name="stepId"/> was cut short (the user's mouse or button: the engine's "drag.partial"):
    /// a list that did not move after it is at neither its end nor its top (incident 31; the rule ScanRecipeList uses).
    /// </summary>
    internal static bool DragCutShort(ProcedureResult r, string stepId) =>
        r.Attempts.Any(a => a.StepId == stepId && a.DetailCode == "drag.partial");
}
