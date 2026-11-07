using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;

namespace Companion.Contracts.Host;

/// <summary>Automation switches section of config.json (off by default).</summary>
/// <param name="Crafting">Live crafting automation.</param>
/// <param name="Homestead">Live homestead automation.</param>
public sealed record AutomationConfig(bool Crafting = false, bool Homestead = false);

/// <summary>Input section.</summary>
/// <param name="SlideClick">Slide clicks enabled (user decision 2026-10-14; tray switch, journaled). Off = key-only procedures.</param>
/// <param name="ForegroundSlide">Foreground mode (SPEC 7.3b, the user's decision 2026-11-02): inputs also while the game is
/// the foreground window and the cursor rests on it; the window jumps visibly for about half a second per press. Off = wait
/// until the game is in the background, as before.</param>
/// <param name="ForegroundDrag">Slide drags (list scrolls) also in foreground mode. Off until their own live proof (p0_c16)
/// passes: with the game in front, Windows sends the game its own mouse moves with the real (released) button state while
/// the window steps under the still cursor, and whether the game then ends the drag as a click is not proven.</param>
public sealed record InputConfig(bool SlideClick = true, bool ForegroundSlide = true, bool ForegroundDrag = false);

/// <summary>Policies.</summary>
/// <param name="CloseUserUisAtJobStart">Allow Reset to close non-Esc-safe UIs the user left open (default off: park + notify).</param>
/// <param name="TreatAutoCollectStatusAsIdle">Auto-collect status values the user confirmed as "not gathering" (0 always idle).</param>
/// <param name="ServerUtcOffsetHours">Server day boundary for daily counters (HUD shows UTC+1, PL-01).</param>
public sealed record PolicyConfig(bool CloseUserUisAtJobStart = false, IReadOnlyList<int>? TreatAutoCollectStatusAsIdle = null, int ServerUtcOffsetHours = 1);

/// <summary>Keep-stock caps (TOS-01).</summary>
/// <param name="Enabled">Keep-stock evaluation enabled.</param>
/// <param name="SessionStartLocal">Optional session window start (HH:mm local).</param>
/// <param name="SessionEndLocal">Optional session window end (HH:mm local).</param>
/// <param name="MaxCraftsPerHour">Cap.</param>
/// <param name="MaxSessionMinutes">Cap.</param>
/// <param name="MaxDailyCrafts">Cap (server day).</param>
public sealed record KeepStockConfig(bool Enabled = false, string? SessionStartLocal = null, string? SessionEndLocal = null, int MaxCraftsPerHour = 6, int MaxSessionMinutes = 60, int MaxDailyCrafts = 30);

/// <summary>
/// config.json in RuntimeDir (admin-write only). Changed ONLY through the elevated tray UI by an explicit user action,
/// never through the job pipe and never by features (GATE-01).
/// </summary>
/// <param name="SchemaVersion">1.</param>
/// <param name="Automation">Switches.</param>
/// <param name="Input">Input.</param>
/// <param name="Features">Feature id to enabled.</param>
/// <param name="Policy">Policies.</param>
/// <param name="KeepStock">Keep-stock caps.</param>
public sealed record CompanionConfig(
    int SchemaVersion,
    AutomationConfig Automation,
    InputConfig Input,
    IReadOnlyDictionary<string, bool> Features,
    PolicyConfig Policy,
    KeepStockConfig KeepStock)
{
    /// <summary>Defaults: automation off, slide clicks on, all features enabled.</summary>
    public static CompanionConfig Default { get; } = new(
        1,
        new AutomationConfig(),
        new InputConfig(),
        new Dictionary<string, bool> { ["core"] = true, ["inventory"] = true, ["recipes"] = true, ["crafting"] = true, ["survey"] = true, ["homestead"] = true },
        new PolicyConfig(),
        new KeepStockConfig());
}

/// <summary>Config access (unit "app-host").</summary>
public interface IConfigProvider
{
    /// <summary>Current config (immutable snapshot).</summary>
    CompanionConfig Current { get; }
}

/// <summary>Read-only switch view handed to the engine and features.</summary>
public interface IAutomationSwitches
{
    /// <summary>True if the switch is on (None is always on).</summary>
    bool IsOn(AutomationSwitch automationSwitch);

    /// <summary>Slide clicks enabled.</summary>
    bool SlideClickEnabled { get; }
}

/// <summary>Notification kinds.</summary>
public enum NotificationKind
{
    /// <summary>Info.</summary>
    Info,
    /// <summary>Warning.</summary>
    Warning,
    /// <summary>The user should do something (park, untrusted build).</summary>
    NeedsUser,
    /// <summary>HALT.</summary>
    Halt,
}

/// <summary>A notification. Text is a fixed template per code (no game text, no chat).</summary>
/// <param name="Kind">Kind.</param>
/// <param name="Code">Code.</param>
/// <param name="Text">Fixed text.</param>
public sealed record Notification(NotificationKind Kind, string Code, string Text);

/// <summary>Notifier (unit "app-host"): non-activating tray balloon; never a dialog that takes focus (KILL-21). De-duplicates by code.</summary>
public interface INotifier
{
    /// <summary>Shows a notification.</summary>
    void Notify(Notification notification);
}

/// <summary>Integrity of install-side files against the user-trusted build (SEC-1/FR-01).</summary>
public interface IFileTrust
{
    /// <summary>True if the build was trusted by the user and every file matches.</summary>
    bool BuildTrusted { get; }

    /// <summary>Reads a data file once, verifying its SHA-256 against the trusted list; null if missing or mismatched.</summary>
    byte[]? ReadTrusted(string fullPath);
}

/// <summary>Pipe operations.</summary>
public enum PipeOp
{
    /// <summary>Submit a job.</summary>
    Submit,
    /// <summary>Host status.</summary>
    Status,
    /// <summary>Job result.</summary>
    Result,
    /// <summary>Cancel a queued/parked job.</summary>
    Cancel,
    /// <summary>Kill switch (anyone may stop: fail-safe).</summary>
    Stop,
    /// <summary>Pause.</summary>
    Pause,
    /// <summary>Resume after pause (not after Stop/HALT: tray only).</summary>
    Resume,
    /// <summary>Opens the companion window (JobName = the tab to show, optional; 2026-10-17: lets the window be checked
    /// without a click on the tray).</summary>
    Show,
}

/// <summary>Named-pipe job channel constants (SEC-2). JSON messages, one per line, &lt;= 1024 bytes.</summary>
public static class PipeProtocol
{
    /// <summary>Pipe name.</summary>
    public const string PipeName = "TOS-companion.jobs.v1";

    /// <summary>Max request/response size in bytes.</summary>
    public const int MaxMessageBytes = 1024;

    /// <summary>Client connect timeout (fail fast: host not running).</summary>
    public const int ConnectTimeoutMs = 2000;

    /// <summary>Heartbeat older than this = host considered dead.</summary>
    public const int HeartbeatStaleMs = 5000;

    /// <summary>Job id pattern.</summary>
    public const string JobIdPattern = "^[0-9a-f]{32}$";

    /// <summary>
    /// Max queued jobs (plus one running). 10 since 2026-10-31: the tray's Queue tab sends every goal at once, and with 1
    /// queued, 5 of the user's 7 goals were refused "queue full" in the same millisecond. The hourly job limits still apply.
    /// </summary>
    public const int MaxQueued = 10;
}

/// <summary>A pipe request. Every field outside the op's needs is rejected.</summary>
/// <param name="Op">Operation.</param>
/// <param name="JobId">Job id (Submit/Result/Cancel).</param>
/// <param name="JobName">Job name (Submit).</param>
/// <param name="Params">Raw scalar params (Submit).</param>
public sealed record PipeRequest(PipeOp Op, string? JobId = null, string? JobName = null, IReadOnlyDictionary<string, string>? Params = null);

/// <summary>Host state.</summary>
public enum HostState
{
    /// <summary>Starting.</summary>
    Starting,
    /// <summary>Ready, idle.</summary>
    Idle,
    /// <summary>Running a job.</summary>
    Running,
    /// <summary>Paused.</summary>
    Paused,
    /// <summary>A job is parked.</summary>
    Parked,
    /// <summary>HALT latched.</summary>
    Halted,
    /// <summary>Kill switch set.</summary>
    Stopped,
    /// <summary>Build not trusted: no jobs.</summary>
    Untrusted,
}

/// <summary>Status (pipe Status op, heartbeat).</summary>
/// <param name="State">Host state.</param>
/// <param name="HeartbeatUtc">Heartbeat (updated by a timer independent of jobs).</param>
/// <param name="Engine">Engine status.</param>
/// <param name="Halt">Latched HALT.</param>
/// <param name="GameConnected">Game connected.</param>
/// <param name="Jobs">Whitelisted job names.</param>
public sealed record HostStatus(
    HostState State,
    DateTimeOffset HeartbeatUtc,
    EngineStatus? Engine,
    HaltReport? Halt,
    bool GameConnected,
    IReadOnlyList<string> Jobs);

/// <summary>Job status (pipe Result op).</summary>
/// <param name="JobId">Job.</param>
/// <param name="JobName">Name.</param>
/// <param name="State">State.</param>
/// <param name="Code">Code.</param>
/// <param name="CreatedUtc">Created.</param>
/// <param name="FinishedUtc">Finished.</param>
/// <param name="Data">Scalar summary.</param>
public sealed record JobStatusInfo(string JobId, string JobName, JobState State, string Code, DateTimeOffset CreatedUtc, DateTimeOffset? FinishedUtc, IReadOnlyDictionary<string, object?>? Data);

/// <summary>A pipe response.</summary>
/// <param name="Ok">Accepted.</param>
/// <param name="Code">Code ("accepted", "refused.whitelist", "refused.rate", ...).</param>
/// <param name="Status">Host status (Status op).</param>
/// <param name="Job">Job status (Submit/Result/Cancel).</param>
public sealed record PipeResponse(bool Ok, string Code, HostStatus? Status = null, JobStatusInfo? Job = null);
