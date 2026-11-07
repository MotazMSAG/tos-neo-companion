using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Logs;
using Companion.Contracts.Perception;
using Companion.Contracts.Vision;

namespace Companion.Engine.Internal;

/// <summary>Counters that live for a whole job (several Run calls with the same job id).</summary>
internal sealed class JobScope(string jobId, TimeSpan startedMono, DateTimeOffset startedUtc)
{
    public string JobId { get; } = jobId;
    public TimeSpan StartedMono { get; } = startedMono;
    public DateTimeOffset StartedUtc { get; } = startedUtc;
    /// <summary>
    /// When focus failures happened (monotonic). Audit R2.8 (rule 6): the counts lived for the whole job, and a rule-mode
    /// job runs for days, so two failures a day apart latched a HALT; only the ones inside <see cref="FailureWindow"/> count.
    /// </summary>
    public List<TimeSpan> FocusFailures { get; } = [];

    /// <summary>When unknown windows were met (monotonic), counted inside <see cref="FailureWindow"/> like <see cref="FocusFailures"/>.</summary>
    public List<TimeSpan> UnknownUis { get; } = [];

    /// <summary>The window of the job-wide failure counts (the same as the engine's soft-end window).</summary>
    public static readonly TimeSpan FailureWindow = Companion.Contracts.Safety.HardRules.SoftEndWindow;

    /// <summary>Adds one occurrence at <paramref name="now"/> and returns how many fall inside <see cref="FailureWindow"/>.</summary>
    public static int Recent(List<TimeSpan> times, TimeSpan now)
    {
        times.RemoveAll(t => now - t > FailureWindow);
        times.Add(now);
        return times.Count;
    }
    public Dictionary<string, int> Cycle { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> WrongVerified { get; } = new(StringComparer.Ordinal);
    public HashSet<string> OpenedUis { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Notified { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The start of the latest successful selection-verifying run (<see cref="ProcedureDefinition.VerifiesSelection"/>),
    /// cleared when a commit run ends: the commit gate's user-activity window starts there instead of at the job's start.
    /// </summary>
    public TimeSpan? VerifiedMono { get; set; }
}

/// <summary>State of one root Run call (budgets are shared with sub-procedures and navigation hops).</summary>
internal sealed class RunState
{
    public required JobScope Job { get; init; }
    public required RunContext Ctx { get; init; }
    public required ProcedureDefinition Root { get; init; }
    public required ProcedureLimits Limits { get; init; }
    public required CancellationToken Ct { get; init; }
    public required string ArtifactGroup { get; init; }
    /// <summary>When this Run call started (a selection-verifying run's mark, <see cref="JobScope.VerifiedMono"/>).</summary>
    public TimeSpan StartedMono { get; init; }

    public int Inputs { get; set; }
    /// <summary>Presses aborted after the focus messages, before any key or button went down (not in <see cref="Inputs"/>).</summary>
    public int Aborted { get; set; }
    /// <summary>The <see cref="Aborted"/> count the abort bound last looked at.</summary>
    public int AbortedChecked { get; set; }
    public int Clicks { get; set; }
    public int Escs { get; set; }

    public List<StepAttemptRecord> Attempts { get; } = [];
    public Dictionary<string, ScreenReadResult> Reads { get; } = new(StringComparer.Ordinal);
    public List<string> Artifacts { get; } = [];
    public List<PendingCompensation> Pending { get; } = [];
    public List<JournalRecord> JournalTail { get; } = [];
    public HashSet<string> TemplatesUsed { get; } = new(StringComparer.Ordinal);
    public List<string> Compensable { get; } = [];

    public GameIdentity? Identity { get; set; }
    public LogFileIdentity? LogFile { get; set; }
    public LogPosition? StartLog { get; set; }
    public HudState? InitialHud { get; set; }
    public bool InitialHudPending { get; set; }
    public bool HudToggled { get; set; }
    public bool PendingNotified { get; set; }
    public Frame? LastFrame { get; set; }
    public StepOutcome? LastOutcome { get; set; }
    public string? CurrentStep { get; set; }
    public string CurrentProcedure { get; set; } = string.Empty;
    public TimeSpan? LogQuietSince { get; set; }
    /// <summary>Game-local time of our last delivered input (throttle attribution, live run 2026-10-14).</summary>
    public DateTime? LastOwnInputLocal { get; set; }
    public DateTimeOffset? LastWall { get; set; }
    public TimeSpan? LastMono { get; set; }
    public TimeSpan? LastHudInputMono { get; set; }
    public int? AutoCollectAtGate { get; set; }
}

/// <summary>One procedure being executed (root, called procedure, navigation path or internal reset).</summary>
internal sealed class ProcFrame
{
    public ProcFrame(ProcedureDefinition def, ProcedureLimits limits, int depth)
    {
        Def = def;
        Limits = limits;
        Depth = depth;
        var n = def.Steps.Count;
        Counted = new int[n];
        Uncounted = new int[n];
        StepBacks = new int[n];
        AbortStreak = new int[n];
        AttemptNo = new int[n];
        MovingStreak = new int[n];
        MismatchPauses = new int[n];
    }

    public ProcedureDefinition Def { get; }
    public ProcedureLimits Limits { get; }
    public int Depth { get; }
    public int[] Counted { get; }
    public int[] Uncounted { get; }
    public int[] StepBacks { get; }
    public int[] AbortStreak { get; }
    public int[] AttemptNo { get; }
    /// <summary>Consecutive waits on a target that will not settle (moving, or mismatched in the slid capture) per step; capped, then handled as a lost precondition.</summary>
    public int[] MovingStreak { get; }
    /// <summary>Pauses taken per step after streaks of slid-capture mismatches.</summary>
    public int[] MismatchPauses { get; }
    public int Restarts { get; set; }
    public int PreconditionLost { get; set; }
    public TimeSpan? WaitSince { get; set; }
    /// <summary>Since when the current step's press has been unreachable from where the cursor rests (cleared when it proceeds).</summary>
    public TimeSpan? UnreachableSince { get; set; }
    public HashSet<int> Skipped { get; } = [];
    /// <summary>Parks taken since a step last proceeded, by step, outcome and detail (a repeat ends the procedure).</summary>
    public HashSet<string> Parks { get; } = new(StringComparer.Ordinal);

    public string StepKey(int i) => Depth == 0 ? Def.Steps[i].Id : Def.Id + "/" + Def.Steps[i].Id;

    public void ResetStep(int i)
    {
        Counted[i] = 0;
        Uncounted[i] = 0;
        AbortStreak[i] = 0;
        MovingStreak[i] = 0;
        MismatchPauses[i] = 0;
        WaitSince = null;
        UnreachableSince = null;
        // only this step's parks (safety review 2026-10-23: a step back through earlier steps wiped every record, and a
        // step could park again for the same reason without end)
        var mine = StepKey(i) + "|";
        Parks.RemoveWhere(k => k.StartsWith(mine, StringComparison.Ordinal));
    }

    public void ResetAfterRestart()
    {
        Array.Clear(Counted);
        Array.Clear(Uncounted);
        Array.Clear(AbortStreak);
        Array.Clear(MovingStreak);
        Array.Clear(MismatchPauses);
        WaitSince = null;
        UnreachableSince = null;
    }
}

/// <summary>Outcome of one attempt at a step.</summary>
internal sealed record Attempt(
    StepOutcome Outcome,
    string? Detail = null,
    string? UiSeen = null,
    bool AimVerified = false,
    InputPhase? Phase = null,
    bool Delivered = false,
    bool PatienceExceeded = false,
    string? Subject = null,
    bool Resume = false,
    bool OursUi = false);

/// <summary>Decision with its side effects.</summary>
internal sealed record Decision(
    RecoveryAction Action,
    int Next,
    TimeSpan Sleep = default,
    string? EscFor = null,
    bool EscForNil = false,
    bool Resume = false,
    bool SurveyCapture = false,
    HaltCode? Halt = null,
    string? HaltSubject = null,
    StepOutcome? End = null,
    string? Detail = null,
    bool CheckHome = false);

/// <summary>Unwinds the run to the HALT path.</summary>
internal sealed class HaltSignal(HaltCode code, string? subject, string? stepId, StepOutcome outcome, string? escUi = null, bool escForbidden = false)
    : Exception("halt." + code)
{
    public HaltCode Code { get; } = code;
    public string? Subject { get; } = subject;
    public string? StepId { get; } = stepId;
    public StepOutcome Outcome { get; } = outcome;
    public string? EscUi { get; } = escUi;
    public bool EscForbidden { get; } = escForbidden;
}

/// <summary>Unwinds the run to a terminal end (not a HALT).</summary>
internal sealed class EndSignal(StepOutcome outcome, string? detail) : Exception("end." + outcome)
{
    public StepOutcome Outcome { get; } = outcome;
    public string? Detail { get; } = detail;
}

/// <summary>Log events collected after an input.</summary>
internal sealed record EventWindow(IReadOnlyList<LogEvent> Events, bool Met, bool Late, Expectation? Matched);

/// <summary>Scope of the gates checked before a step.</summary>
internal enum GateScope
{
    /// <summary>Stop, trust, identity, log generation only (captures, reads, waits).</summary>
    Passive,
    /// <summary>Passive plus busy (await craft: no input).</summary>
    Await,
    /// <summary>All gates for a key batch.</summary>
    Key,
    /// <summary>All gates for a click (combat, slide switch).</summary>
    Click,
    /// <summary>All gates for a commit (switch, character, idle since job start).</summary>
    Commit,
}

/// <summary>Result of evaluating a step precondition.</summary>
internal readonly record struct PreResult(bool Holds, bool Transient, string Detail)
{
    public static PreResult Ok { get; } = new(true, false, "pre.ok");
}
