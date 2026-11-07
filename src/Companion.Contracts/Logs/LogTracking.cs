using Companion.Contracts.Knowledge;
using Companion.Contracts.Safety;

namespace Companion.Contracts.Logs;

/// <summary>Why the game cannot take input right now according to the log (F16, PITFALL-24).</summary>
public enum BusyKind
{
    /// <summary>Not busy.</summary>
    None,
    /// <summary>State unknown (just after start or a log rotation, before 'open const ui UI_MAIN' + scene end).</summary>
    Unknown,
    /// <summary>Scene load bracket open ('load_scene start load' .. 'hide_progress real') or UI_LOAD_PROGRESS.</summary>
    Loading,
    /// <summary>UI_CINEMA_* open.</summary>
    Cinema,
    /// <summary>UI_SCENARIO* open.</summary>
    Scenario,
    /// <summary>Login / role select / logout (UI_SERVER, UI_SELECT_ROLE, '[emasdk]start logout').</summary>
    Login,
    /// <summary>UI_REVIVE (character dead).</summary>
    Revive,
    /// <summary>UI_GUIDE_MASK or 'ban by guide mask' (tutorial).</summary>
    GuideMask,
    /// <summary>PvP / match UIs (UI_TEAM_3V3_*).</summary>
    Match,
}

/// <summary>Last focus state the game logged.</summary>
public enum FocusState
{
    /// <summary>Unknown.</summary>
    Unknown,
    /// <summary>'OnApplicationFocus RecoverFocus' last.</summary>
    Recovered,
    /// <summary>'OnApplicationFocus LostFocus' last.</summary>
    Lost,
}

/// <summary>Craft session as seen in the log (STK-01, CR-01, CR-04).</summary>
/// <param name="InProgress">True from the first is_block or station-UI show until its close/dispose + 2 s.</param>
/// <param name="StationUi">The station UI name (UI_LIFE_ALCHEMIST_MAKE_POT ...), if shown.</param>
/// <param name="StartedAt">Game time of the first is_block / station show.</param>
/// <param name="StartPosition">Log position of that first event.</param>
/// <param name="IsBlockCount">is_block lines in this session.</param>
/// <param name="LastTickAt">Game time of the last is_block.</param>
/// <param name="EndedAt">Game time the station UI closed, if ended.</param>
/// <param name="CraftViewReshown">True if UI_LIFESKILL_MAKE_VIEW was shown within 1 s after the station closed (CR-04).</param>
public sealed record CraftSessionInfo(
    bool InProgress,
    string? StationUi,
    DateTime? StartedAt,
    LogPosition? StartPosition,
    int IsBlockCount,
    DateTime? LastTickAt,
    DateTime? EndedAt,
    bool CraftViewReshown)
{
    /// <summary>"Item received" tips (UI_ITEM_GET_TIP) shown while this session ran.</summary>
    public int ItemGets { get; init; }

    /// <summary>
    /// Items made: is_block lines after the first, or the item-received tips if more (a one-item cooking craft logs a single
    /// is_block, then the pot, then one item tip; live 2026-10-15: Lime Water was made but counted 0), never more than the
    /// is_block lines: each craft logs one, and the game's periodic reward tip 1.2 s after the pot closed made 10 cooked
    /// sashimi count 11 (2026-10-31 audit; also the impossible "51-52 made" rounds).
    /// </summary>
    public int Ticks => IsBlockCount == 0 ? ItemGets : Math.Min(IsBlockCount, Math.Max(IsBlockCount - 1, ItemGets));

    /// <summary>No craft seen.</summary>
    public static CraftSessionInfo None { get; } = new(false, null, null, null, 0, null, null, false);
}

/// <summary>The latest item tip the game logged (<see cref="ItemTipLogEvent"/>, live 2026-10-15).</summary>
/// <param name="ItemName">Item name as logged.</param>
/// <param name="ItemId">Numeric item id.</param>
/// <param name="At">Game time of the line.</param>
/// <param name="Position">Log position of the line (its end offset).</param>
public sealed record ItemTipInfo(string ItemName, long ItemId, DateTime At, LogPosition Position)
{
    /// <summary>
    /// True if the tip was logged after <paramref name="from"/> (same generation, later offset). A null
    /// <paramref name="from"/> (nothing read yet when it was taken) accepts any tip.
    /// </summary>
    public bool LoggedAfter(LogPosition? from) => from is null || (Position.SameFile(from) && Position.Offset > from.Offset);
}

/// <summary>The latest Kitty Tea House board press the game logged (<see cref="GridClickLogEvent"/>).</summary>
/// <param name="Row">Row, 1..6 (1 = top).</param>
/// <param name="Column">Column, 1..6 (1 = left).</param>
/// <param name="Piece">The game's number of the piece in hand.</param>
/// <param name="At">Game time of the line.</param>
/// <param name="Position">Log position of the line (its end offset).</param>
public sealed record GridClickInfo(int Row, int Column, int Piece, DateTime At, LogPosition Position)
{
    /// <summary>The square, numbered row by row from 0 (the tray) to 35.</summary>
    public int Square => ((Row - 1) * 6) + (Column - 1);

    /// <summary>True if the press was logged after <paramref name="from"/> (same generation, later offset); null accepts any.</summary>
    public bool LoggedAfter(LogPosition? from) => from is null || (Position.SameFile(from) && Position.Offset > from.Offset);
}

/// <summary>
/// Read-only snapshot of the UI model built from the log (SPEC 5/6). The open set is ADVISORY (closes are not always
/// logged, PITFALL-06): decisions use <see cref="LastHideTopNil"/> from our own Esc and attribution windows, never the
/// open set alone.
/// </summary>
/// <param name="AsOf">Position the snapshot reflects.</param>
/// <param name="GameAvailable">False after a rotation/session marker until 'open const ui UI_MAIN' and scene end (F14).</param>
/// <param name="Busy">Busy state.</param>
/// <param name="AdvisoryStack">Stack/Forbidden/Social UIs shown since the last 'hide top ui nil' or scene load and not closed (last = top).</param>
/// <param name="LastHideTopNil">Position of the latest 'hide top ui nil'.</param>
/// <param name="Focus">Last logged focus state.</param>
/// <param name="LastFocusChange">Game time of the last focus line.</param>
/// <param name="AutoCollectStatus">Last set_auto_collect_status value, null = unknown.</param>
/// <param name="Character">Current character (salted hash), null = unknown.</param>
/// <param name="LastResolution">Last '[graphic] Current screen resolution'.</param>
/// <param name="LastJank">Game time of the last jank line.</param>
/// <param name="LastThrottle">Game time of the last 'request too frequency'.</param>
/// <param name="LastCombatIndicator">Game time of the last combat indicator show (<see cref="UiCatalog.CombatIndicators"/>).</param>
/// <param name="LastScreenSizeChange">Game time of the last OnScreenSizeChange / Device resolution line.</param>
/// <param name="LastGuideBan">Game time of the last 'ban by guide'.</param>
/// <param name="LastPayLine">Game time of the last non-login pay_mgr line.</param>
/// <param name="Craft">Craft session.</param>
/// <param name="ShownThisSession">UI names shown at least once in this log generation (first-open timeouts).</param>
public sealed record UiStateSnapshot(
    LogPosition? AsOf,
    bool GameAvailable,
    BusyKind Busy,
    IReadOnlyList<string> AdvisoryStack,
    LogPosition? LastHideTopNil,
    FocusState Focus,
    DateTime? LastFocusChange,
    int? AutoCollectStatus,
    CharacterKey? Character,
    SizeI? LastResolution,
    DateTime? LastJank,
    DateTime? LastThrottle,
    DateTime? LastCombatIndicator,
    DateTime? LastScreenSizeChange,
    DateTime? LastGuideBan,
    DateTime? LastPayLine,
    CraftSessionInfo Craft,
    IReadOnlyCollection<string> ShownThisSession)
{
    /// <summary>Top of the advisory stack, or null when nothing is believed open.</summary>
    public string? AdvisoryTop => AdvisoryStack.Count > 0 ? AdvisoryStack[^1] : null;

    /// <summary>
    /// The latest item tip logged in this session (name, id, game time, position), or null. A caller that needs the tip
    /// its own input opened takes a snapshot first and requires <see cref="ItemTipInfo.LoggedAfter"/> its
    /// <see cref="AsOf"/>.
    /// </summary>
    public ItemTipInfo? LastItemTip { get; init; }

    /// <summary>
    /// Game time of the latest auto-eat ("[lifeskill][autoeat] drug_mgr supply.eat_food req"), or null. The line names no
    /// food: rule mode re-checks its rules soon after one (roadmap B, "keep food at N").
    /// </summary>
    public DateTime? LastAutoEat { get; init; }

    /// <summary>
    /// The latest Kitty Tea House board press (square, piece in hand, position), or null. The Tea House job takes a
    /// snapshot before its click and requires <see cref="GridClickInfo.LoggedAfter"/> that snapshot's <see cref="AsOf"/>.
    /// </summary>
    public GridClickInfo? LastGridClick { get; init; }
}

/// <summary>Result of one incremental read.</summary>
/// <param name="Events">New events (in file order).</param>
/// <param name="End">Position after the last complete entry read.</param>
/// <param name="Rotated">True if the file generation changed since the previous poll (model was reset).</param>
/// <param name="Problem">Read problem, if any.</param>
public sealed record LogPollResult(IReadOnlyList<LogEvent> Events, LogPosition? End, bool Rotated, LogReadProblem Problem);

/// <summary>Log read problems.</summary>
public enum LogReadProblem
{
    /// <summary>No problem.</summary>
    None,
    /// <summary>log.txt missing (game restarting).</summary>
    FileMissing,
    /// <summary>Access denied / sharing violation.</summary>
    AccessDenied,
    /// <summary>Other I/O error.</summary>
    IoError,
}

/// <summary>Liveness of the log (LOG-01).</summary>
/// <param name="LastGrowthUtc">Wall time the file last grew.</param>
/// <param name="Length">Current length.</param>
/// <param name="Readable">Last poll succeeded.</param>
/// <param name="Rotations">Rotations seen since host start.</param>
/// <param name="LastPollUtc">Wall time of the last poll.</param>
public sealed record LogHealth(DateTimeOffset? LastGrowthUtc, long Length, bool Readable, int Rotations, DateTimeOffset? LastPollUtc);

/// <summary>Result of the start-up / post-rotation bootstrap scan (from byte 0 of the current generation).</summary>
/// <param name="Ok">True if the scan completed.</param>
/// <param name="File">File generation scanned.</param>
/// <param name="ScannedBytes">Bytes scanned.</param>
/// <param name="AutoCollectStatus">Last auto-collect status found, null if none.</param>
/// <param name="Character">Last login role (hashed), null if none.</param>
/// <param name="LastResolution">Last resolution line.</param>
/// <param name="InWorld">True if 'open const ui UI_MAIN' was seen after the last login/scene start and the scene finished loading.</param>
/// <param name="Elapsed">Scan duration.</param>
public sealed record LogBootstrapReport(
    bool Ok,
    LogFileIdentity? File,
    long ScannedBytes,
    int? AutoCollectStatus,
    CharacterKey? Character,
    SizeI? LastResolution,
    bool InWorld,
    TimeSpan Elapsed);

/// <summary>
/// Incremental reader + parser + UI model of D:\Games\TOS\Log\log.txt (unit "logs"). Thread-safe: GameIO polls it
/// during a click while the host polls it on a timer. Constructor has no side effects; nothing is read before
/// <see cref="Bootstrap"/> or <see cref="Poll"/>. Each poll opens the file with FileShare.ReadWrite|Delete, reads
/// from the stored offset to EOF, decodes only complete entries (a timestamped first line, '|'-continuation lines, a
/// blank separator), and closes the file immediately.
/// </summary>
public interface ILogTracker
{
    /// <summary>Scans the current generation from byte 0 for change-only state (auto-collect, login role, resolution, scene, UI_MAIN) and sets the read offset to the end.</summary>
    LogBootstrapReport Bootstrap(CancellationToken ct);

    /// <summary>Reads new complete entries. Never throws for I/O problems (reported in the result).</summary>
    LogPollResult Poll();

    /// <summary>Position after the last complete entry read so far; null before the first read.</summary>
    LogPosition? Position { get; }

    /// <summary>Events after <paramref name="from"/> held in the in-memory ring buffer (last 15 min or 50 000 events). Empty if <paramref name="from"/> belongs to another generation.</summary>
    IReadOnlyList<LogEvent> EventsSince(LogPosition from);

    /// <summary>Events with from &lt; end offset &lt;= to (same generation only).</summary>
    IReadOnlyList<LogEvent> EventsBetween(LogPosition from, LogPosition to);

    /// <summary>Current UI model snapshot.</summary>
    UiStateSnapshot State { get; }

    /// <summary>Liveness.</summary>
    LogHealth Health { get; }

    /// <summary>
    /// Raw lines between two positions after the privacy filter (<see cref="ILogPrivacyFilter"/>), for diagnostics,
    /// survey excerpts and fixtures. Never returns unfiltered text.
    /// </summary>
    IReadOnlyList<string> ReadFilteredExcerpt(LogPosition from, LogPosition to, int maxLines);
}

/// <summary>UI-name classification (unit "logs"): seeds from <see cref="UiCatalog"/> + Stack names learned from 'hide top ui'.</summary>
public interface IUiClassifier
{
    /// <summary>Kind of a UI name (precedence in <see cref="UiCatalog.Precedence"/>).</summary>
    UiKind Classify(string uiName);

    /// <summary>True if Esc may close it although we did not open it (<see cref="UiCatalog.EscSafe"/>).</summary>
    bool IsEscSafe(string uiName);

    /// <summary>True if the name was seen in a '[ui mgr]hide top ui' line in any generation since host start.</summary>
    bool IsLearnedStack(string uiName);
}

/// <summary>
/// Privacy filter for log text (unit "logs", PRIV-01/PRIV-14/F31). An allow-list of exact message patterns: ui_mgr UI
/// events, StageMgr focus, UpdateModule LostFocuse (+ its module/screenPosition lines only), [graphic] resolution,
/// collect_mgr, scene_load, lifeskill_cook_mgr is_block, jank_detector, item_tips_mgr item tips (item name and id: game
/// item data). Everything else (chat, ema_vendor analytics,
/// account/role/server/device info, server dumps, untimestamped dumps) is dropped; role-id-like patterns
/// ('\b\d{4,5}-\d{4,6}\b') are scrubbed from what remains.
/// </summary>
public interface ILogPrivacyFilter
{
    /// <summary>True if the raw line matches the allow-list.</summary>
    bool IsAllowed(string rawLine);

    /// <summary>Returns the scrubbed line if allowed, otherwise null.</summary>
    string? Filter(string rawLine);
}
