using Companion.Contracts.GameIo;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Perception;
using Companion.Contracts.Vision;

namespace Companion.Contracts.Engine;

/// <summary>Kinds of job (informational + gates; the engine still gates every action by tags, GATE-01).</summary>
public enum JobKind
{
    /// <summary>Reads only (keys + captures).</summary>
    ReadOnlyScan,
    /// <summary>Opens/closes screens.</summary>
    Navigation,
    /// <summary>Commits (crafting, homestead).</summary>
    Commit,
    /// <summary>Passive survey.</summary>
    Survey,
    /// <summary>Calibration.</summary>
    Maintenance,
}

/// <summary>Context of a procedure run.</summary>
/// <param name="JobId">Job id (32 hex).</param>
/// <param name="JobName">Job name.</param>
/// <param name="Kind">Job kind.</param>
/// <param name="Switch">Automation switch of the job.</param>
public sealed record RunContext(string JobId, string JobName, JobKind Kind, AutomationSwitch Switch);

/// <summary>Final status of a procedure run.</summary>
public enum ProcedureStatus
{
    /// <summary>All steps OK.</summary>
    Succeeded,
    /// <summary>Parked (waiting; resumes automatically).</summary>
    Parked,
    /// <summary>HALT (latched).</summary>
    Halted,
    /// <summary>Kill switch.</summary>
    Stopped,
    /// <summary>Cancelled.</summary>
    Cancelled,
    /// <summary>Deferred: cannot be done now, nothing unsafe happened; try again later (<see cref="StepOutcome.Deferred"/>).</summary>
    Deferred,
    /// <summary>Game exited.</summary>
    GameExited,
    /// <summary>Refused by a gate.</summary>
    Refused,
    /// <summary>Reached a needsSurvey stub.</summary>
    NeedsSurvey,
}

/// <summary>One journaled attempt.</summary>
/// <param name="StepId">Step.</param>
/// <param name="Attempt">Attempt number (1-based) within the step.</param>
/// <param name="Outcome">Outcome.</param>
/// <param name="Class">Class.</param>
/// <param name="Decision">Decision.</param>
/// <param name="Phase">Input phase reached (clicks).</param>
/// <param name="AimVerified">LostFocuse screenPosition within 2 px of the aim and no P1-P4 cursor movement.</param>
/// <param name="UiSeen">Attributed UI name, if any.</param>
/// <param name="DetailCode">Short code: the decision's detail when it has one (e.g. "preLost.stepBack",
/// "exhausted.PreconditionLostExhausted"), else the attempt's own.</param>
/// <param name="AtUtc">Time.</param>
/// <param name="Cause">The attempt's own detail (e.g. "template.notFound:craft.fav.anchor"), kept also when
/// <paramref name="DetailCode"/> holds the decision's (2026-10-31: the favourites scroll-back and the Tea House second anchor
/// looked for "template.notFound" in DetailCode, which a step-back or an exhausted end had overwritten).</param>
public sealed record StepAttemptRecord(
    string StepId,
    int Attempt,
    StepOutcome Outcome,
    OutcomeClass Class,
    RecoveryAction Decision,
    InputPhase? Phase,
    bool AimVerified,
    string? UiSeen,
    string? DetailCode,
    DateTimeOffset AtUtc,
    string? Cause = null)
{
    /// <summary>True when the attempt's own detail (or, for older records, its DetailCode) starts with <paramref name="prefix"/>.</summary>
    public bool CauseStartsWith(string prefix) =>
        (Cause ?? DetailCode)?.StartsWith(prefix, StringComparison.Ordinal) == true || DetailCode?.StartsWith(prefix, StringComparison.Ordinal) == true;
}

/// <summary>A compensation still owed to the game (e.g. a HUD toggle to undo after Calibrate).</summary>
/// <param name="Code">Code (e.g. "hud.restore.collapsed").</param>
/// <param name="DetailCode">Detail.</param>
public sealed record PendingCompensation(string Code, string DetailCode);

/// <summary>Result of a procedure run.</summary>
/// <param name="ProcedureId">Procedure.</param>
/// <param name="Status">Status.</param>
/// <param name="FinalOutcome">Last outcome.</param>
/// <param name="Halt">HALT report if halted.</param>
/// <param name="InputBatches">Input batches used.</param>
/// <param name="Clicks">Clicks used.</param>
/// <param name="EscPresses">Esc presses used.</param>
/// <param name="Attempts">Attempts.</param>
/// <param name="Reads">Read results by output key.</param>
/// <param name="Artifacts">Artifact paths written (captures).</param>
/// <param name="Pending">Compensations still owed.</param>
/// <param name="EndDetail">Why the run ended early (the engine's detail code, e.g. "unreachable.CrossMonitor"); null when it succeeded.</param>
public sealed record ProcedureResult(
    string ProcedureId,
    ProcedureStatus Status,
    StepOutcome? FinalOutcome,
    HaltReport? Halt,
    int InputBatches,
    int Clicks,
    int EscPresses,
    IReadOnlyList<StepAttemptRecord> Attempts,
    IReadOnlyDictionary<string, ScreenReadResult> Reads,
    IReadOnlyList<string> Artifacts,
    IReadOnlyList<PendingCompensation> Pending,
    string? EndDetail = null);

/// <summary>A HALT. User message text is fixed per code (SPEC 6.6); no free text from the game.</summary>
/// <param name="Code">Code.</param>
/// <param name="Subject">Template id / UI name / step id concerned.</param>
/// <param name="JobId">Job.</param>
/// <param name="ProcedureId">Procedure.</param>
/// <param name="StepId">Step.</param>
/// <param name="AtUtc">Time.</param>
/// <param name="DiagnosticsPath">Diagnostics bundle folder.</param>
public sealed record HaltReport(HaltCode Code, string? Subject, string JobId, string ProcedureId, string? StepId, DateTimeOffset AtUtc, string? DiagnosticsPath);

/// <summary>
/// Runs procedures with the recovery state machine (unit "engine-nav", Companion.Engine). All game I/O of the host
/// goes through one runner on one engine thread (F28). Constructor has no side effects.
/// </summary>
public interface IProcedureRunner
{
    /// <summary>Runs a catalogued procedure.</summary>
    ProcedureResult Run(string procedureId, RunContext context, CancellationToken ct);

    /// <summary>Runs an ad-hoc procedure (navigator paths, feature-built procedures). Same gates and limits.</summary>
    ProcedureResult Run(ProcedureDefinition procedure, RunContext context, CancellationToken ct);
}

/// <summary>Procedures loaded at start (core data + features).</summary>
public interface IProcedureCatalog
{
    /// <summary>Procedure by id, or null.</summary>
    ProcedureDefinition? Get(string id);

    /// <summary>All procedures.</summary>
    IReadOnlyCollection<ProcedureDefinition> All { get; }
}

/// <summary>Start-up loading of procedures; sealed before the first job.</summary>
public interface IProcedureCatalogBuilder : IProcedureCatalog
{
    /// <summary>Adds (validates ids, limits &lt;= ceilings, referenced templates/readers/screens exist). Throws after <see cref="Seal"/>.</summary>
    void Add(ProcedureDefinition procedure, string sourceCode);

    /// <summary>Freezes the catalog.</summary>
    void Seal();
}

/// <summary>
/// Capture/read service for features and the navigator (unit "engine-nav"): captures through IGameIo on the engine
/// thread (rate-limited to 2/s), validates frames, applies readers with screen-identity checks.
/// </summary>
public interface IScreenService
{
    /// <summary>Fresh validated capture.</summary>
    CaptureResult Capture(CancellationToken ct);

    /// <summary>HUD state from 2 agreeing captures &gt;= 300 ms apart; Unknown if they disagree (F07).</summary>
    HudClassification ClassifyHudStable(CancellationToken ct);

    /// <summary>Capture + reader (identity confirmed by log AND anchors in the same capture).</summary>
    ScreenReadResult Read(string readerId, CancellationToken ct);
}

/// <summary>Pause control (unit "engine-nav"): the engine stops at the next gate, between inputs.</summary>
public interface IPauseControl
{
    /// <summary>True while paused.</summary>
    bool IsPaused { get; }

    /// <summary>Pauses.</summary>
    void Pause(string sourceCode);

    /// <summary>Resumes.</summary>
    void Resume(string sourceCode);
}

/// <summary>Persisted HALT latch (unit "app-host"): survives restarts until the user clears it in the tray (KILL-21).</summary>
public interface IHaltLatch
{
    /// <summary>True while latched.</summary>
    bool IsHalted { get; }

    /// <summary>The latched report.</summary>
    HaltReport? Current { get; }

    /// <summary>Latches (persists atomically, notifies).</summary>
    void Halt(HaltReport report);

    /// <summary>Clears (tray only).</summary>
    void Clear(string byCode);
}

/// <summary>Engine state for status/heartbeat.</summary>
public enum EngineState
{
    /// <summary>No job.</summary>
    Idle,
    /// <summary>Running a step.</summary>
    Running,
    /// <summary>Waiting on a gate.</summary>
    Waiting,
    /// <summary>Paused by the user.</summary>
    Paused,
    /// <summary>A job is parked.</summary>
    Parked,
    /// <summary>Halted.</summary>
    Halted,
    /// <summary>Stopped by the kill switch.</summary>
    Stopped,
}

/// <summary>Engine status snapshot.</summary>
/// <param name="State">State.</param>
/// <param name="JobId">Current job.</param>
/// <param name="ProcedureId">Current procedure.</param>
/// <param name="StepId">Current step.</param>
/// <param name="WaitingFor">Wait reason (StepOutcome name or gate code).</param>
/// <param name="SinceUtc">Since when in this state.</param>
public sealed record EngineStatus(EngineState State, string? JobId, string? ProcedureId, string? StepId, string? WaitingFor, DateTimeOffset SinceUtc);

/// <summary>Status source for the host.</summary>
public interface IEngineStatusSource
{
    /// <summary>Current status.</summary>
    EngineStatus Status { get; }
}
