using System.Text.Json.Serialization;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;

namespace Companion.Contracts.Engine;

/// <summary>
/// State predicate checked before a step (conjunction of the non-null fields). A false predicate is
/// PRECONDITION_LOST. Busy, foreground, iconic, display mode and the other global gates are always checked first
/// and are not part of the predicate.
/// </summary>
/// <param name="HudClean">True: a fresh 'hide top ui nil' after our own Esc and no stack/forbidden/social UI shown since.</param>
/// <param name="TopUi">The advisory top UI must equal this name.</param>
/// <param name="Hud">HUD state (2 stable captures).</param>
/// <param name="Screen">ui-map screen id (navigator identification).</param>
/// <param name="TemplatesPresent">Templates that must be found in a fresh capture.</param>
public sealed record StatePredicate(
    bool? HudClean = null,
    string? TopUi = null,
    HudState? Hud = null,
    string? Screen = null,
    IReadOnlyList<string>? TemplatesPresent = null)
{
    /// <summary>No requirement.</summary>
    public static StatePredicate Any { get; } = new();
}

/// <summary>
/// What proves a step succeeded. A step succeeds when ANY of <see cref="UiEvents"/> is logged (exact verb + name,
/// attributed to our input) AND, if set, <see cref="Hud"/> holds on 2 stable captures AND <see cref="Screen"/> is
/// identified. Alternatives cover steps with two legal end states (CR-04).
/// </summary>
/// <param name="UiEvents">Expected log events (any of).</param>
/// <param name="Hud">Expected HUD state.</param>
/// <param name="Screen">Expected screen id.</param>
/// <param name="Alternatives">Alternative expectations (any of them also succeeds).</param>
public sealed record Expectation(
    IReadOnlyList<UiExpectation>? UiEvents = null,
    HudState? Hud = null,
    string? Screen = null,
    IReadOnlyList<Expectation>? Alternatives = null)
{
    /// <summary>No expectation (Wait/Capture steps).</summary>
    public static Expectation Nothing { get; } = new();
}

/// <summary>Step flags.</summary>
[Flags]
public enum StepFlags
{
    /// <summary>None.</summary>
    None = 0,
    /// <summary>On park/halt, Esc what this step opened (keys only, if Esc-safe).</summary>
    CompensateOnPark = 1,
    /// <summary>Commit step: requires the automation switch, character Allowed, idle period, and never retried blindly.</summary>
    Commit = 2,
    /// <summary>Step may be skipped when its expectation already holds (e.g. toggle already in target state).</summary>
    SkipIfSatisfied = 4,
    /// <summary>
    /// If the step cannot run because a target/reader is not clickable/ready (provisional, needsSurvey), record a
    /// PendingCompensation, notify once and continue (never allowed on Commit steps). Without this flag the
    /// procedure pre-flight ends NeedsSurvey before any input.
    /// </summary>
    Optional = 8,
    /// <summary>
    /// The click is MEANT to change the game's auto-collect status (2026-10-15: the Gathering Helper's Begin 0 to 1 and
    /// Finish 2 to 0): the engine's "auto-gathering changed while I was working" halt is not raised by this step's own
    /// press. Click steps only, never a commit step; every other step keeps the halt.
    /// </summary>
    ChangesAutoCollect = 16,

    /// <summary>
    /// The click may be pressed while the Gathering Helper is gathering (auto-collect status not idle; 2026-10-18: the
    /// gear durability Notice pops up only during gathering and must be answered then). Plain click steps only: never a
    /// commit, never one that changes the auto-collect status; every other gate still applies.
    /// </summary>
    DuringAutoCollect = 32,

    /// <summary>
    /// A selection click whose result the job verifies before any commit (2026-10-21: crafting.loop's favourites slots;
    /// CraftLoop reads the selected product's logged tip before every Make and refuses another product). Inside a commit
    /// procedure such a click may use the off-screen anchor patch rule, which commit procedures otherwise never use: a
    /// cursor resting low on the monitor slid the favourites anchor off the drawn screen and the strict check cancelled
    /// the slot click again and again. Plain click steps only, never the commit step itself.
    /// </summary>
    SelectionVerified = 64,

    /// <summary>
    /// A click that expects nothing whose job checks the effect itself (2026-10-31: the Tea House board squares; the job
    /// waits for the game's logged press): the engine reads the log for windows the click opened for
    /// <see cref="Safety.HardRules.QuickAttributionWindow"/> and returns; the rest of the full attribution window is read by
    /// the late check before the next input of any procedure (a Forbidden UI or pay line is the same HALT, a window the
    /// click opened that is on top ends that run before any input). Plain click steps with an empty expectation only, in
    /// a procedure with no gate, never a commit (step flag or template tag).
    /// </summary>
    QuickAttribution = 128,
}

/// <summary>
/// A step (data). Timeouts are raised to <see cref="HardRules.MinStepTimeout"/> and, for a UI not yet shown in this
/// game session, to <see cref="HardRules.FirstOpenStepTimeout"/>.
/// </summary>
/// <param name="Id">Step id (unique inside the procedure).</param>
/// <param name="Pre">Precondition.</param>
/// <param name="Action">Action.</param>
/// <param name="Expect">Expectation.</param>
/// <param name="TimeoutMs">Timeout in milliseconds.</param>
/// <param name="Flags">Flags.</param>
public sealed record StepDefinition(string Id, StatePredicate Pre, StepAction Action, Expectation Expect, int TimeoutMs = 3000, StepFlags Flags = StepFlags.None);

/// <summary>Step actions (polymorphic JSON by "type").</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(KeyAction), "key")]
[JsonDerivedType(typeof(ClickAction), "click")]
[JsonDerivedType(typeof(ToggleHudAction), "toggleHud")]
[JsonDerivedType(typeof(RestoreHudAction), "restoreHud")]
[JsonDerivedType(typeof(ResetAction), "reset")]
[JsonDerivedType(typeof(WaitAction), "wait")]
[JsonDerivedType(typeof(NavigateAction), "navigate")]
[JsonDerivedType(typeof(CaptureAction), "capture")]
[JsonDerivedType(typeof(ReadAction), "read")]
[JsonDerivedType(typeof(CallProcedureAction), "call")]
[JsonDerivedType(typeof(AwaitCraftAction), "awaitCraft")]
[JsonDerivedType(typeof(NeedsSurveyAction), "needsSurvey")]
[JsonDerivedType(typeof(DragAction), "drag")]
public abstract record StepAction;

/// <summary>Post a whitelisted key (Esc / B). Letter keys require Pre.HudClean (KEY-12).</summary>
/// <param name="Key">Key.</param>
public sealed record KeyAction(GameKey Key) : StepAction;

/// <summary>Slide-click a named template (located on a fresh capture right before the click).</summary>
/// <param name="TemplateId">Template id.</param>
/// <param name="Offset">Optional click point relative to the template's FOUND centre (a cell of a grid located through a
/// stable anchor, live run 2026-10-14). The template is still located twice and verified while slid at its own centre;
/// only the click lands at centre + offset. Null = click the centre.</param>
public sealed record ClickAction(string TemplateId, PointI? Offset = null) : StepAction;

/// <summary>
/// Slide-drag that starts on a located anchor template (proof p0_c14, 2026-10-14; used to scroll lists): the button is
/// pressed at the anchor's FOUND centre + <paramref name="Offset"/>, the pointer then moves by <paramref name="Delta"/>
/// in steps of at most <see cref="HardRules.DragStepPx"/> (the game window slides by each step behind the cover; the real
/// cursor never moves), holds still and releases. The anchor is located twice right before and verified while slid at its
/// own centre; deny zones apply to the start (and end) point. Never a commit; raw coordinates are never used. The step
/// expects nothing: the caller checks the effect on a capture.
/// </summary>
/// <param name="TemplateId">Anchor template (clickable, not commit).</param>
/// <param name="Offset">Press point relative to the anchor's found centre.</param>
/// <param name="Delta">Pointer movement in client pixels (|dx|, |dy| at most <see cref="HardRules.MaxDragDistancePx"/>, not zero); a negative dy drags the content up.</param>
public sealed record DragAction(string TemplateId, PointI Offset, PointI Delta) : StepAction;

/// <summary>Bring the HUD menu to a state: no click if already there (2 stable captures), else click the toggle template of the current state once.</summary>
/// <param name="Target">Expanded or Collapsed.</param>
public sealed record ToggleHudAction(HudState Target) : StepAction;

/// <summary>Restore the HUD state recorded at the start of the procedure (Calibrate); needs a Verified toggle template, else pending compensation + notify.</summary>
public sealed record RestoreHudAction : StepAction;

/// <summary>Reset: Esc while the top UI is Esc-safe and each Esc makes progress, until 'hide top ui nil' (max <see cref="HardRules.MaxEscPerReset"/>, &gt;= 1 s apart).</summary>
/// <param name="MaxEsc">Max Esc presses (capped by the hard rule).</param>
public sealed record ResetAction(int MaxEsc = 10) : StepAction;

/// <summary>Wait without input.</summary>
/// <param name="DurationMs">Duration.</param>
public sealed record WaitAction(int DurationMs) : StepAction;

/// <summary>Navigate to a ui-map screen (navigator path; unidentified screen = Reset then re-identify).</summary>
/// <param name="ScreenId">Target screen.</param>
public sealed record NavigateAction(string ScreenId) : StepAction;

/// <summary>What a capture step persists (PRIV-14).</summary>
public enum CaptureScope
{
    /// <summary>Right HUD strip (client x 1440-1919), full scale (Calibrate).</summary>
    HudStripRight,
    /// <summary>Template-sized crops of all templates of the current screen.</summary>
    TemplateCrops,
    /// <summary>Full client frame with the chat panel (x 660-1270, y 590-1009) blacked out; only in an explicit survey job with fullFrame=true.</summary>
    FullFrameMasked,

    /// <summary>
    /// The Gathering Helper as shown (2026-10-22, diagnostics): the client frame while the helper is the log's top UI (it
    /// covers the chat panel), saved as a diagnostics artifact when a gathering walk did not see a wanted material.
    /// Refused when the helper is not on top.
    /// </summary>
    GatherHelper,

    /// <summary>
    /// The Make view as shown (incident 26, 2026-10-26, diagnostics): the client frame while the Make view is open in the
    /// log's stack (a popup over it may be on top), chat panel blacked out, saved when a Make press started no craft.
    /// Refused when the Make view is not open.
    /// </summary>
    MakeView,
}

/// <summary>Capture the screen and persist per scope (artifact store).</summary>
/// <param name="Label">Label (sanitised to [a-z0-9_-]).</param>
/// <param name="Scope">Scope.</param>
public sealed record CaptureAction(string Label, CaptureScope Scope) : StepAction;

/// <summary>Apply a reader to a fresh capture; result stored in the procedure result under <paramref name="OutputKey"/>.</summary>
/// <param name="ReaderId">Reader id.</param>
/// <param name="OutputKey">Result key.</param>
public sealed record ReadAction(string ReaderId, string OutputKey) : StepAction;

/// <summary>Run another procedure as a sub-procedure (its limits count toward the caller's budget).</summary>
/// <param name="ProcedureId">Procedure id.</param>
public sealed record CallProcedureAction(string ProcedureId) : StepAction;

/// <summary>Wait for a craft session to end without any input (CR-01). Timeout = ticks x max observed cadence + margin.</summary>
/// <param name="RequestedTicks">Ticks requested at the commit.</param>
public sealed record AwaitCraftAction(int RequestedTicks) : StepAction;

/// <summary>Explicit stub for unobserved UI: the procedure ends NeedsSurvey without acting (no invented coordinates).</summary>
/// <param name="What">What must be surveyed (code).</param>
public sealed record NeedsSurveyAction(string What) : StepAction;

/// <summary>Per-procedure limits (data may lower them; ceilings in <see cref="HardRules"/>).</summary>
/// <param name="MaxCountedPerStep">Counted failures per step before stepping back (3).</param>
/// <param name="MaxUncountedPerStep">Uncounted retries per step before the next one is counted (4).</param>
/// <param name="MaxStepBacks">Step-backs per step (1).</param>
/// <param name="MaxRestarts">Full reset + restart of the procedure (2).</param>
/// <param name="MaxPreconditionLost">Non-transient PRECONDITION_LOST per procedure (2).</param>
/// <param name="MaxInputBatches">Input batches per procedure run (25).</param>
/// <param name="MaxClicks">Slide clicks per procedure run (6).</param>
/// <param name="MaxEsc">Esc presses per procedure run (8).</param>
/// <param name="PatienceMs">WAIT patience before parking (600 000).</param>
public sealed record ProcedureLimits(
    int MaxCountedPerStep = 3,
    int MaxUncountedPerStep = 4,
    int MaxStepBacks = 1,
    int MaxRestarts = 2,
    int MaxPreconditionLost = 2,
    int MaxInputBatches = 25,
    int MaxClicks = 6,
    int MaxEsc = 8,
    int PatienceMs = 600_000)
{
    /// <summary>Defaults.</summary>
    public static ProcedureLimits Default { get; } = new();
}

/// <summary>What a procedure does to the game (journal, gates).</summary>
public enum ProcedureKind
{
    /// <summary>Opens/closes screens and reads.</summary>
    Navigation,
    /// <summary>Contains commit steps (gated).</summary>
    Commit,
    /// <summary>Calibration / survey.</summary>
    Maintenance,
}

/// <summary>A procedure (data: data\procedures.json or a feature's procedures file).</summary>
/// <param name="Id">Procedure id.</param>
/// <param name="Description">One line.</param>
/// <param name="Steps">Steps in order.</param>
/// <param name="Limits">Limits.</param>
/// <param name="Kind">Kind.</param>
/// <param name="Gate">Automation switch required for any commit step.</param>
/// <param name="VerifiesSelection">
/// A read-only navigation run that shows what is selected (the product tip, 2026-10-16): when it succeeds, the commit
/// gate's user-activity window of the job starts at this run's start until the next commit run ends (the feature compares
/// what it read and presses nothing on a mismatch). Only allowed on navigation procedures without a gate.
/// </param>
public sealed record ProcedureDefinition(
    string Id,
    string Description,
    IReadOnlyList<StepDefinition> Steps,
    ProcedureLimits Limits,
    ProcedureKind Kind = ProcedureKind.Navigation,
    AutomationSwitch Gate = AutomationSwitch.None,
    bool VerifiesSelection = false);
