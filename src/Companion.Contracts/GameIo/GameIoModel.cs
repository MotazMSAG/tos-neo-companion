using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;

namespace Companion.Contracts.GameIo;

/// <summary>Validated identity of the game window (re-checked before every input, F23/GAME-18).</summary>
/// <param name="Hwnd">Window handle as int64.</param>
/// <param name="Pid">Process id.</param>
/// <param name="ProcessStartUtc">Process start time (Process.StartTime; PROCESS_QUERY_LIMITED_INFORMATION only).</param>
/// <param name="ExePath">QueryFullProcessImageName result (must equal D:\Games\TOS\TOS.exe).</param>
public sealed record GameIdentity(long Hwnd, int Pid, DateTimeOffset ProcessStartUtc, string ExePath);

/// <summary>
/// Game build fingerprint from file metadata only (size + last write time of TOS.exe and GameAssembly.dll). File
/// contents and version resources are never read (ToS: no reading game files, FR-04).
/// </summary>
/// <param name="ExeSize">TOS.exe size.</param>
/// <param name="ExeWriteUtc">TOS.exe last write.</param>
/// <param name="AssemblySize">GameAssembly.dll size, if present.</param>
/// <param name="AssemblyWriteUtc">GameAssembly.dll last write, if present.</param>
public sealed record GameBuildFingerprint(long ExeSize, DateTime ExeWriteUtc, long? AssemblySize, DateTime? AssemblyWriteUtc)
{
    /// <summary>Compact code for journals and templates ("exeSize-exeTicks-asmSize-asmTicks").</summary>
    public string Code => $"{ExeSize:x}-{ExeWriteUtc.Ticks:x}-{AssemblySize ?? 0:x}-{AssemblyWriteUtc?.Ticks ?? 0:x}";
}

/// <summary>A monitor (EnumDisplayMonitors, re-read before every slide, F12).</summary>
/// <param name="DeviceName">Device name (\\.\DISPLAY1).</param>
/// <param name="Bounds">Monitor rect (screen).</param>
/// <param name="WorkArea">Work area (screen).</param>
/// <param name="Primary">Primary monitor.</param>
/// <param name="Dpi">Effective DPI.</param>
public sealed record MonitorInfo(string DeviceName, RectI Bounds, RectI WorkArea, bool Primary, int Dpi);

/// <summary>Read-only registry prefs of the game (HKCU\Software\TOS\TOS, whitelist only; never written, F13).</summary>
/// <param name="FullscreenMode">'Screenmanager Fullscreen mode*' (3 = windowed).</param>
/// <param name="WindowPositionX">'Screenmanager Window Position X*'.</param>
/// <param name="WindowPositionY">'Screenmanager Window Position Y*'.</param>
/// <param name="SelectMonitor">'UnitySelectMonitor*'.</param>
/// <param name="ResolutionWidth">'Resolution Window Width' / 'Screenmanager Resolution Width*'.</param>
/// <param name="ResolutionHeight">'Resolution Window Height' / 'Screenmanager Resolution Height*'.</param>
/// <param name="LogOpen">'neo_log_save_openLog*' (1 = log enabled, LOG-01).</param>
/// <param name="LogWrite">'neo_log_save_openWrite*'.</param>
public sealed record GameWindowPrefs(
    int? FullscreenMode,
    int? WindowPositionX,
    int? WindowPositionY,
    int? SelectMonitor,
    int? ResolutionWidth,
    int? ResolutionHeight,
    int? LogOpen,
    int? LogWrite);

/// <summary>Live state of the game window (all read-only calls).</summary>
/// <param name="Exists">IsWindow and identity unchanged.</param>
/// <param name="Identity">Identity.</param>
/// <param name="WindowRect">GetWindowRect (screen).</param>
/// <param name="ClientOrigin">ClientToScreen(0,0).</param>
/// <param name="ClientSize">GetClientRect size.</param>
/// <param name="Maximized">IsZoomed.</param>
/// <param name="Iconic">IsIconic.</param>
/// <param name="Foreground">Foreground window's PID == game PID (not HWND-only, FOC-8).</param>
/// <param name="Hung">IsHungAppWindow.</param>
/// <param name="Cloaked">DWMWA_CLOAKED != 0.</param>
/// <param name="HasCaption">WS_CAPTION present (windowed).</param>
/// <param name="Monitor">Monitor the window is on (MonitorFromWindow).</param>
/// <param name="Monitors">All monitors.</param>
/// <param name="ExtraVisibleGameWindows">Other visible top-level windows of the game PID (security dialog, webview: stop).</param>
/// <param name="KeyboardLayout">GetKeyboardLayout(game thread) (F30).</param>
/// <param name="ClipCursor">GetClipCursor.</param>
/// <param name="CursorShowing">GetCursorInfo CURSOR_SHOWING.</param>
public sealed record GameWindowState(
    bool Exists,
    GameIdentity? Identity,
    RectI WindowRect,
    PointI ClientOrigin,
    SizeI ClientSize,
    bool Maximized,
    bool Iconic,
    bool Foreground,
    bool Hung,
    bool Cloaked,
    bool HasCaption,
    MonitorInfo? Monitor,
    IReadOnlyList<MonitorInfo> Monitors,
    int ExtraVisibleGameWindows,
    long KeyboardLayout,
    RectI ClipCursor,
    bool CursorShowing);

/// <summary>Connect problems.</summary>
public enum ConnectProblem
{
    /// <summary>Connected.</summary>
    None,
    /// <summary>No TOS process.</summary>
    NotRunning,
    /// <summary>Several TOS processes with a visible UnityWndClass window.</summary>
    Ambiguous,
    /// <summary>Process found but no visible UnityWndClass window.</summary>
    WindowNotFound,
    /// <summary>Executable path is not the expected one.</summary>
    ExePathMismatch,
    /// <summary>Host not elevated (UIPI would drop our messages).</summary>
    NotElevated,
    /// <summary>Not a 64-bit process.</summary>
    Not64Bit,
    /// <summary>PostMessage(WM_NULL) failed with ERROR_ACCESS_DENIED (integrity mismatch, PITFALL-07).</summary>
    IntegrityMismatch,
}

/// <summary>Connect result (no input sent; only window enumeration, process query, registry read and a WM_NULL probe).</summary>
/// <param name="Problem">Problem.</param>
/// <param name="Game">Identity when connected.</param>
/// <param name="Prefs">Registry prefs baseline.</param>
/// <param name="Build">Build fingerprint.</param>
public sealed record ConnectResult(ConnectProblem Problem, GameIdentity? Game, GameWindowPrefs? Prefs, GameBuildFingerprint? Build)
{
    /// <summary>True when connected.</summary>
    public bool Ok => Problem == ConnectProblem.None && Game is not null;
}

/// <summary>What the readiness check is for.</summary>
public enum InputKind
{
    /// <summary>A key batch (no cursor/cover requirements).</summary>
    Key,
    /// <summary>A slide click.</summary>
    Click,
}

/// <summary>
/// How an input reaches the game, chosen by the readiness check and fixed for the whole input (SPEC 7.3b).
/// </summary>
public enum InputMode
{
    /// <summary>
    /// The game lies behind another window (the cover) that the cursor rests on: fake focus, the hidden slide, the fake
    /// unfocus while slid (its press block is the aim evidence).
    /// </summary>
    Cover,

    /// <summary>
    /// The game is the foreground window and the cursor rests on it (the user's decision 2026-11-02, EVIDENCE c15): no
    /// focus message (the game has real focus, R2), the window slides visibly under the cursor and back, and the user's
    /// own clicks or keys during that time reach the game. No press block, so no aim evidence.
    /// </summary>
    Foreground,
}

/// <summary>Why the companion may not act right now. Passive checks only: nothing is sent to the game.</summary>
public enum NotReadyReason
{
    /// <summary>Ready.</summary>
    None,
    /// <summary>Not connected / identity changed.</summary>
    GameNotConnected,
    /// <summary>Game minimized.</summary>
    GameIconic,
    /// <summary>Game (its PID) is foreground: the user is playing.</summary>
    GameForeground,
    /// <summary>Game was foreground less than the cooldown ago, or a foreign RecoverFocus was logged recently.</summary>
    Cooldown,
    /// <summary>GetCursorPos failed / no input desktop / locked session (F19).</summary>
    SessionNotInteractive,
    /// <summary>Cursor moved within the stillness window.</summary>
    CursorMoving,
    /// <summary>A mouse button is held (GetAsyncKeyState high bit).</summary>
    UserButtonDown,
    /// <summary>System idle below the requirement.</summary>
    SystemNotIdle,
    /// <summary>No window above the game under the cursor.</summary>
    NoCover,
    /// <summary>The window under the cursor is not a valid cover (<see cref="CoverRejectReason"/>).</summary>
    CoverRejected,
    /// <summary>Slide plan breaks the monitor rule (largest overlap on another monitor whose DPI is not 96, or target outside the client).</summary>
    MonitorRule,
    /// <summary>Slid window would be visible somewhere (exposed area &gt; 0).</summary>
    ExposedArea,
    /// <summary>Slide distance above the cap.</summary>
    SlideTooFar,
    /// <summary>Game window not at its validated home (maximized, 1920x1009 client).</summary>
    WindowNotAtHome,
    /// <summary>Display mode wrong (not windowed maximized 1920x1009, prefs differ).</summary>
    DisplayModeWrong,
    /// <summary>Game hung or WM_NULL probe slow.</summary>
    GameBusy,
    /// <summary>A jank line or an open scene_load bracket within the quiet window.</summary>
    RecentJank,
    /// <summary>Monitor DPI not 96.</summary>
    DpiUnsupported,
    /// <summary>Extra visible game window (security dialog / webview).</summary>
    ExtraGameWindow,
    /// <summary>Kill switch set.</summary>
    Stopped,
    /// <summary>
    /// The slide would carry the game onto another monitor than its home (the cursor rests on another monitor), where the
    /// slid capture cannot show the target (c13), and the target is not one of the fixed HUD buttons the cross-monitor
    /// rule allows: wait for the cursor to come back instead of trying (2026-10-16: 57 refused slid checks ended in a halt).
    /// </summary>
    CrossMonitor,

    /// <summary>
    /// Foreground mode: the cursor does not rest on the game's client area (another window under it or around it, the
    /// caption, or closer than <see cref="Safety.HardRules.CoverMarginPx"/> to the client edge).
    /// </summary>
    CursorNotOnGame,

    /// <summary>
    /// Foreground mode: the game has been the foreground window for less than <see cref="Safety.HardRules.ForegroundSettle"/>
    /// (the user just switched to it), or the game's log says it is unfocused.
    /// </summary>
    ForegroundSettling,

    /// <summary>
    /// Foreground mode: keyboard or mouse input (GetLastInputInfo) more recently than the idle requirement: the user is
    /// using the game.
    /// </summary>
    UserInputRecent,

    /// <summary>Foreground mode, a letter key: the game's chat is open, so the key would type into it.</summary>
    ChatOpen,
}

/// <summary>Why a candidate cover window is rejected (F11, WIN-4, PITFALL-11).</summary>
public enum CoverRejectReason
{
    /// <summary>Valid.</summary>
    None,
    /// <summary>It is the game.</summary>
    IsGame,
    /// <summary>Owned by the game PID or a child process (crash handler, webview), or its root owner is the game.</summary>
    GameOwned,
    /// <summary>Shell/transient class (Shell_TrayWnd, Shell_SecondaryTrayWnd, NotifyIconOverflowWindow, Windows.UI.Core.CoreWindow, XamlExplorerHostIslandWindow, MultitaskingViewFrame, ForegroundStaging, tooltips_class32, #32768, Ghost).</summary>
    ShellOrTransient,
    /// <summary>WS_EX_TOPMOST.</summary>
    Topmost,
    /// <summary>WS_EX_TOOLWINDOW or WS_EX_NOACTIVATE.</summary>
    ToolWindow,
    /// <summary>Layered with alpha &lt; 255.</summary>
    Translucent,
    /// <summary>DWM-cloaked.</summary>
    Cloaked,
    /// <summary>Minimized or invisible.</summary>
    NotVisible,
    /// <summary>Not the foreground window and not owned by the foreground process.</summary>
    NotForeground,
    /// <summary>Cursor within the margin of its DWM extended frame.</summary>
    CursorNearEdge,
    /// <summary>Different root at one of the 8 probe points around the cursor.</summary>
    RootMismatch,
    /// <summary>Its rect changed during the stillness period.</summary>
    Unstable,
    /// <summary>One of the companion's own windows.</summary>
    OwnWindow,
    /// <summary>Not above the game in z-order.</summary>
    NotAboveGame,
}

/// <summary>A candidate cover window. Class name only, never the title (PRIV-14).</summary>
/// <param name="Hwnd">Handle as int64.</param>
/// <param name="ClassName">Window class.</param>
/// <param name="Pid">Owning PID.</param>
/// <param name="FrameBounds">DWM extended frame bounds (screen).</param>
/// <param name="Reject">Reject reason, None if valid.</param>
public sealed record CoverInfo(long Hwnd, string ClassName, int Pid, RectI FrameBounds, CoverRejectReason Reject);

/// <summary>Slide geometry for a target (window moved so the target client point lies under the resting cursor).</summary>
/// <param name="HomeRect">Home window rect (sampled now; validated maximized 1920x1009).</param>
/// <param name="SlidRect">Window rect after the slide.</param>
/// <param name="Dx">Horizontal move.</param>
/// <param name="Dy">Vertical move.</param>
/// <param name="HomeMonitor">Home monitor device.</param>
/// <param name="SlidMonitor">MonitorFromRect(slid, NEAREST) device.</param>
/// <param name="CenterInsideHomeMonitor">Slid window centre inside the home monitor (informational; not a refusal rule since live run 2026-10-14).</param>
/// <param name="ExposedAreaPx">Slid rect on any monitor minus windows above the game (must be 0).</param>
public sealed record SlidePlan(RectI HomeRect, RectI SlidRect, int Dx, int Dy, string HomeMonitor, string SlidMonitor, bool CenterInsideHomeMonitor, long ExposedAreaPx);

/// <summary>Readiness query.</summary>
/// <param name="Kind">Key or click.</param>
/// <param name="TargetClientPoint">For clicks: client point to bring under the cursor (enables the slide plan checks).</param>
/// <param name="NearDenyZone">Target within the deny-zone guard distance (longer stillness).</param>
/// <param name="CrossMonitorAllowed">False: a slide onto another monitor than the game's home is not ready
/// (<see cref="NotReadyReason.CrossMonitor"/>); the engine allows it only for fixed HUD navigation targets (2026-10-16).</param>
/// <param name="LetterKey">A key query for a letter key (not Esc): in foreground mode not ready while the chat is open.</param>
/// <param name="Drag">A click query for a slide drag: in foreground mode ready only with the user's foreground-drag switch
/// (SPEC 7.3b: drags with the game in front wait for their own live proof).</param>
public sealed record ReadinessQuery(InputKind Kind, PointI? TargetClientPoint, bool NearDenyZone, bool CrossMonitorAllowed = true, bool LetterKey = false, bool Drag = false);

/// <summary>Readiness (passive; nothing sent to the game).</summary>
/// <param name="Ready">True if every check passes.</param>
/// <param name="Reason">First failing reason.</param>
/// <param name="Cursor">Cursor (screen), valid only if <paramref name="CursorValid"/>.</param>
/// <param name="CursorValid">GetCursorPos succeeded on the input desktop.</param>
/// <param name="CursorStill">How long the cursor has been still.</param>
/// <param name="SystemIdle">GetLastInputInfo idle time.</param>
/// <param name="Cover">Window under the cursor, if any.</param>
/// <param name="Plan">Slide plan, for click queries.</param>
/// <param name="SinceGameForeground">Time since the game was last seen foreground (sampled by GameIO's background monitor).</param>
/// <param name="Mode">The input mode the checks ran for: <see cref="InputMode.Foreground"/> as soon as the game is the
/// foreground window and foreground mode is on (also when a later check fails).</param>
/// <param name="SinceGameUse">Time since the user could last have used the game: the game was foreground and there was
/// keyboard or mouse input (or it became foreground, or stopped being). Foreground mode keeps this growing while the user
/// rests, where <paramref name="SinceGameForeground"/> stays zero. Null from a reporter that does not track it (use
/// <paramref name="SinceGameForeground"/>).</param>
public sealed record ReadinessReport(
    bool Ready,
    NotReadyReason Reason,
    PointI Cursor,
    bool CursorValid,
    TimeSpan CursorStill,
    TimeSpan SystemIdle,
    CoverInfo? Cover,
    SlidePlan? Plan,
    TimeSpan SinceGameForeground,
    InputMode Mode = InputMode.Cover,
    TimeSpan? SinceGameUse = null)
{
    /// <summary>The user-activity clock: <see cref="SinceGameUse"/> when reported, else <see cref="SinceGameForeground"/>.</summary>
    public TimeSpan SinceUserInGame => SinceGameUse ?? SinceGameForeground;
}

/// <summary>What happened to an input request (the engine maps it to a StepOutcome).</summary>
public enum InputDisposition
{
    /// <summary>Zero messages sent (F02). Uncounted wait.</summary>
    NotSent,
    /// <summary>Focus messages sent, then aborted before DOWN/KEYDOWN. Uncounted, capped.</summary>
    AbortedAfterFocus,
    /// <summary>Input delivered (DOWN/UP or KEYDOWN/KEYUP posted) and the window is back home.</summary>
    Sent,
    /// <summary>The game became really foreground or iconic mid-input: no fake unfocus, slid back, USER_ACTIVE.</summary>
    UserTookOver,
    /// <summary>Kill switch.</summary>
    Stopped,
    /// <summary>Pacing token not available (no message sent).</summary>
    RateLimited,
    /// <summary>No RecoverFocus logged after the focus messages.</summary>
    FocusFailed,
    /// <summary>Game window gone / process exited.</summary>
    GameGone,
    /// <summary>PostMessage/SendMessageTimeout failed with ERROR_ACCESS_DENIED (UIPI).</summary>
    IntegrityMismatch,
    /// <summary>Refused by a hard rule (key not whitelisted, identity mismatch, provisional target, bad flags).</summary>
    Refused,
    /// <summary>A window incident happened (see <see cref="WindowIncident"/>).</summary>
    Incident,
}

/// <summary>Detailed abort reason.</summary>
public enum AbortReason
{
    /// <summary>No abort.</summary>
    None,
    /// <summary>A readiness check failed (see <see cref="NotReadyReason"/>).</summary>
    NotReady,
    /// <summary>Cursor moved.</summary>
    CursorMoved,
    /// <summary>User pressed a mouse button.</summary>
    UserButton,
    /// <summary>Window under the cursor changed.</summary>
    CoverChanged,
    /// <summary>The game would show / showed under the cursor.</summary>
    GameUnderCursor,
    /// <summary>Slide did not land where asked.</summary>
    SlideDidNotLand,
    /// <summary>Window rect/maximized changed during the input (not by us).</summary>
    WindowChanged,
    /// <summary>PrintWindow while slid failed or timed out.</summary>
    SlidCaptureFailed,
    /// <summary>Template not verified at the target point in the slid capture.</summary>
    SlidTemplateMismatch,
    /// <summary>A non-overlay UI (stack/forbidden/social/busy/popup real-show) was logged since the locating capture.</summary>
    LogGatePopup,
    /// <summary>Locating capture too old.</summary>
    LocateTooOld,
    /// <summary>A SendMessageTimeout timed out (game hung).</summary>
    SendTimeout,
    /// <summary>Dead-man deadline reached.</summary>
    DeadMan,
    /// <summary>Clip cursor changed or cursor hidden while we held fake focus (F27/CUR-19).</summary>
    CursorClipChanged,
    /// <summary>Game identity changed.</summary>
    IdentityChanged,
    /// <summary>Keyboard layout differs from the baseline (F30).</summary>
    KeyboardLayoutChanged,
    /// <summary>Kill switch.</summary>
    StopRequested,
    /// <summary>Game became foreground.</summary>
    GameForeground,
    /// <summary>Game minimized.</summary>
    GameIconic,
    /// <summary>Foreground mode: the game stopped being the foreground window (the user switched away).</summary>
    ForegroundLost,
    /// <summary>Foreground mode: keyboard, wheel or mouse-button input (GetLastInputInfo) during the input: it reached the game.</summary>
    UserInput,
}

/// <summary>Phases of a slide click (F18). HUD buttons fire on DOWN, so only P4 decides where the click landed.</summary>
public enum InputPhase
{
    /// <summary>Readiness, before any message.</summary>
    P0Ready,
    /// <summary>Focus messages being sent.</summary>
    P1Focus,
    /// <summary>Focus sent, before the slide.</summary>
    P2FocusToSlide,
    /// <summary>Slid, before DOWN.</summary>
    P3SlideToDown,
    /// <summary>DOWN posted until consumed (approx. the expected show_ui, ~110 ms).</summary>
    P4DownConsumption,
    /// <summary>DOWN to UP.</summary>
    P5DownToUp,
    /// <summary>UP to unfocus.</summary>
    P6UpToUnfocus,
    /// <summary>After slide-back.</summary>
    P7AfterSlideBack,
    /// <summary>Finished.</summary>
    Completed,
}

/// <summary>Log positions and wall times of the input milestones (for attribution by the engine).</summary>
/// <param name="FocusSent">Log position read just before the first focus message.</param>
/// <param name="FocusSentUtc">Time of the first focus message.</param>
/// <param name="ActionPosted">Log position just before DOWN / KEYDOWN.</param>
/// <param name="ActionPostedUtc">Time of DOWN / KEYDOWN.</param>
/// <param name="ReleasePostedUtc">Time of UP / KEYUP.</param>
/// <param name="UnfocusSent">Log position just before the first unfocus message.</param>
/// <param name="UnfocusSentUtc">Time of the first unfocus message.</param>
/// <param name="SlideBackUtc">Time of the slide-back call.</param>
/// <param name="End">Log position at the end of the call.</param>
public sealed record InputMarks(
    LogPosition? FocusSent,
    DateTimeOffset? FocusSentUtc,
    LogPosition? ActionPosted,
    DateTimeOffset? ActionPostedUtc,
    DateTimeOffset? ReleasePostedUtc,
    LogPosition? UnfocusSent,
    DateTimeOffset? UnfocusSentUtc,
    DateTimeOffset? SlideBackUtc,
    LogPosition? End);

/// <summary>Watchdog sampling statistics for one displaced section (WD-20).</summary>
/// <param name="Samples">Number of samples.</param>
/// <param name="P50Ms">Median sample period.</param>
/// <param name="P99Ms">99th percentile period (target &lt;= 2 ms).</param>
/// <param name="MaxGapMs">Largest gap.</param>
public sealed record WatchdogStats(int Samples, double P50Ms, double P99Ms, double MaxGapMs);

/// <summary>Cursor evidence from the watchdog (sampled every ~1-2 ms from focus to slide-back).</summary>
/// <param name="Start">Cursor at readiness (screen).</param>
/// <param name="MovedBeforeDown">Moved in P1-P3 (then no DOWN was posted).</param>
/// <param name="MovedInP4">Moved between DOWN and its consumption: UNCERTAIN.</param>
/// <param name="MovedAfterP4">Moved in P5-P6 (irrelevant for DOWN-firing targets).</param>
/// <param name="UserButton">A mouse button went down during the input.</param>
/// <param name="ClipChanged">The ClipCursor rect changed while we held focus (pointer visibility alone does not count).</param>
/// <param name="UserInput">Foreground mode: keyboard, wheel or button input (GetLastInputInfo) during the input, with the
/// cursor still; it reached the game, so a UI seen afterwards may be the user's.</param>
public sealed record CursorEvidence(PointI Start, bool MovedBeforeDown, bool MovedInP4, bool MovedAfterP4, bool UserButton, bool ClipChanged, bool UserInput = false);

/// <summary>Slide evidence.</summary>
/// <param name="Plan">Geometry used.</param>
/// <param name="CoverClass">Cover window class.</param>
/// <param name="Displaced">Time from slide call to verified slide-back.</param>
/// <param name="SlideCall">Duration of the slide SetWindowPos call.</param>
/// <param name="SlideBackCall">Duration of the slide-back call.</param>
/// <param name="ForcedSlideBack">Slide-back done by the watchdog (trigger or dead-man).</param>
/// <param name="BackAtHome">Window verified at home afterwards.</param>
/// <param name="Watchdog">Sampling statistics.</param>
public sealed record SlideEvidence(SlidePlan Plan, string CoverClass, TimeSpan Displaced, TimeSpan SlideCall, TimeSpan SlideBackCall, bool ForcedSlideBack, bool BackAtHome, WatchdogStats Watchdog);

/// <summary>Kinds of window incident.</summary>
public enum IncidentKind
{
    /// <summary>Window not at home after an input (restored by the ladder).</summary>
    NotHomeAfterInput,
    /// <summary>The game resized its own window during our input (c9/c10 class): HALT.</summary>
    SelfResize,
    /// <summary>Maximized flag lost.</summary>
    MaximizedLost,
    /// <summary>Deviation that started outside our displacement (user/game moved it): pause, never fight the user (WIN-9).</summary>
    MovedByOthers,
    /// <summary>Home monitor vanished (re-baseline, never restore onto it).</summary>
    MonitorVanished,
    /// <summary>The whole restore ladder failed: HALT with an instruction.</summary>
    RestoreFailed,
    /// <summary>SetWindowPos slower than the threshold.</summary>
    SlowSetWindowPos,
    /// <summary>The ClipCursor rect changed while we held fake focus: HALT.</summary>
    CursorClip,
    /// <summary>Registry window prefs changed after a slide (notify; never written by us).</summary>
    PrefsChanged,
    /// <summary>Leftover displacement found at start (crash recovery from the slide marker).</summary>
    LeftoverFromCrash,
    /// <summary>
    /// Foreground mode: the game did not take our DOWN/UP within <see cref="Safety.HardRules.ForegroundDrainHangMax"/> (it
    /// stopped responding mid-press), and the window was moved home anyway (R3), so the press may land at home when the
    /// game wakes up: HALT, never a retry.
    /// </summary>
    UndrainedSlideBack,
}

/// <summary>Restore ladder rungs (F24/WIN-9).</summary>
public enum RestoreRung
{
    /// <summary>No restore needed.</summary>
    None,
    /// <summary>SetWindowPos move-only (0x0215 / 0x4215).</summary>
    MoveOnly,
    /// <summary>SetWindowPos move+size to the live maximized rect (0x0214), only if the size changed during our displacement.</summary>
    MoveSize,
    /// <summary>One posted WM_SIZE(SIZE_MAXIMIZED, 1920x1009), verified by '[graphic] Current screen resolution 1920 1009'.</summary>
    PostedWmSize,
    /// <summary>All rungs failed.</summary>
    Failed,
}

/// <summary>A window incident.</summary>
/// <param name="Kind">Kind.</param>
/// <param name="Expected">Expected rect.</param>
/// <param name="Observed">Observed rect.</param>
/// <param name="Restored">Restored successfully.</param>
/// <param name="Rung">Last rung used.</param>
/// <param name="DetailCode">Short code.</param>
public sealed record WindowIncident(IncidentKind Kind, RectI? Expected, RectI? Observed, bool Restored, RestoreRung Rung, string DetailCode);

/// <summary>A key request (Esc or B only).</summary>
/// <param name="StepId">Step (journal).</param>
/// <param name="Key">Key.</param>
/// <param name="ExpectedUi">Lets GameIO end the post-KEYUP wait early when one is logged.</param>
public sealed record KeyRequest(string StepId, GameKey Key, IReadOnlyList<UiExpectation> ExpectedUi);

/// <summary>Result of a key batch.</summary>
/// <param name="Disposition">Disposition.</param>
/// <param name="Abort">Abort reason.</param>
/// <param name="FocusMessagesSent">Focus messages sent (0 = nothing touched the game).</param>
/// <param name="FocusLogged">RecoverFocus was logged after our focus messages.</param>
/// <param name="UnfocusSkipped">Unfocus skipped because the game was really foreground.</param>
/// <param name="Marks">Milestones.</param>
/// <param name="ExpectedSeen">An expected UI event was seen before unfocus.</param>
/// <param name="Mode">Foreground mode: no focus message was sent (FocusLogged stays false, the game had real focus).</param>
/// <param name="UserInputDuring">Foreground mode: keyboard or mouse input between KEYDOWN and the end of the wait; a UI
/// seen afterwards may be the user's.</param>
public sealed record KeyReport(
    InputDisposition Disposition, AbortReason Abort, int FocusMessagesSent, bool FocusLogged, bool UnfocusSkipped, InputMarks Marks, bool ExpectedSeen,
    InputMode Mode = InputMode.Cover, bool UserInputDuring = false);

/// <summary>A slide-click request. The engine located the template on a fresh capture right before calling.</summary>
/// <param name="StepId">Step (journal).</param>
/// <param name="TemplateId">Template to verify in the slid capture (must be clickable).</param>
/// <param name="ClientPoint">Found centre (client coordinates) to bring under the cursor.</param>
/// <param name="FireOn">Down or Up.</param>
/// <param name="LocatedAtLog">Log position of the locating capture (popup gate reference).</param>
/// <param name="LocatedAtMonotonic">Monotonic time of the locating capture (max age 1.5 s).</param>
/// <param name="ExpectedUi">Ends the post-UP wait early.</param>
/// <param name="NearDenyZone">A deny zone lies within the guard distance (1.5 s stillness, abort on any movement).</param>
/// <param name="TemplateCenter">The button's found centre when ClientPoint is an aim point inside the button (off-centre
/// aim, live run 2026-10-14); the slid verification checks the template there. Null = ClientPoint is the centre.</param>
/// <param name="NavFixed">The engine's opt-in for the cross-monitor rule (SPEC 7.3, EVIDENCE c13): a clickable,
/// non-commit, ungated <see cref="TargetTags.NavFixed"/> template pressed at (or inside) its own centre, no step offset,
/// not a commit step. GameIO re-checks the template catalog itself; false (default) keeps the strict slid check.</param>
/// <param name="LocatedFrame">The capture the engine located <paramref name="TemplateCenter"/> in (its CapturedAtMonotonic
/// and LogAt are <paramref name="LocatedAtMonotonic"/> and <paramref name="LocatedAtLog"/>): the reference image of the
/// off-screen anchor patch rule (SPEC 7.3). In memory only, never serialized or journaled (PRIV-14).</param>
/// <param name="PatchVerify">The engine's opt-in for the off-screen anchor patch rule (SPEC 7.3, live run 2026-10-14): a
/// clickable, non-commit, ungated <see cref="TargetTags.ScreenAnchor"/> pressed at a step offset (not an aim), not near a
/// deny zone, outside commit and gated procedures. GameIO re-checks the catalog, the frame and the geometry itself, and
/// sends nothing (NotSent, <see cref="AbortReason.LocateTooOld"/>) when the anchor will be off the drawn monitor and the
/// located capture would be over 1.5 s old at the slid capture; false (default) keeps the strict slid check.</param>
/// <param name="EndsOnGridClick">The post-UP wait also ends when the game logs a Kitty Tea House board press (on_grid_click,
/// 2026-10-31): the game has handled the click at the slid position, so the window may slide home. The engine sets it for
/// QuickAttribution clicks only.</param>
public sealed record ClickRequest(
    string StepId,
    string TemplateId,
    PointI ClientPoint,
    FireOn FireOn,
    LogPosition? LocatedAtLog,
    TimeSpan LocatedAtMonotonic,
    IReadOnlyList<UiExpectation> ExpectedUi,
    bool NearDenyZone,
    PointI? TemplateCenter = null,
    bool NavFixed = false,
    Frame? LocatedFrame = null,
    bool PatchVerify = false,
    bool EndsOnGridClick = false);

/// <summary>Result of a slide click.</summary>
/// <param name="Disposition">Disposition.</param>
/// <param name="Abort">Abort reason.</param>
/// <param name="Phase">Phase reached (abort phase or Completed).</param>
/// <param name="FocusMessagesSent">Focus messages sent.</param>
/// <param name="FocusLogged">RecoverFocus logged.</param>
/// <param name="DownPosted">LBUTTONDOWN posted.</param>
/// <param name="UpPosted">LBUTTONUP posted (always true if DOWN was posted).</param>
/// <param name="UnfocusSentWhileSlid">Unfocus sent while still slid (raycast taken at the slid position).</param>
/// <param name="UnfocusSkipped">Unfocus skipped because the game was really foreground.</param>
/// <param name="Marks">Milestones.</param>
/// <param name="Cursor">Cursor evidence.</param>
/// <param name="Slide">Slide evidence (null if no slide happened).</param>
/// <param name="SlidVerification">Template check in the slid capture.</param>
/// <param name="ExpectedSeenWhileSlid">An expected UI event was logged before slide-back.</param>
/// <param name="PressBlock">The LostFocuse block seen before slide-back, if any (never in foreground mode).</param>
/// <param name="Incident">Window incident, if any.</param>
/// <param name="Mode">Foreground mode: no focus message was sent (FocusLogged stays false), no unfocus, no press block;
/// the section touched the game when the window slid.</param>
public sealed record ClickReport(
    InputDisposition Disposition,
    AbortReason Abort,
    InputPhase Phase,
    int FocusMessagesSent,
    bool FocusLogged,
    bool DownPosted,
    bool UpPosted,
    bool UnfocusSentWhileSlid,
    bool UnfocusSkipped,
    InputMarks Marks,
    CursorEvidence Cursor,
    SlideEvidence? Slide,
    MatchResult? SlidVerification,
    bool ExpectedSeenWhileSlid,
    PressBlockLogEvent? PressBlock,
    WindowIncident? Incident,
    InputMode Mode = InputMode.Cover);

/// <summary>
/// A slide-drag request (SPEC 7.3a, proof p0_c14). The engine located the anchor template on a fresh capture right
/// before calling; the press point is the anchor's found centre + the step's offset (never a raw point).
/// </summary>
/// <param name="StepId">Step (journal).</param>
/// <param name="TemplateId">Anchor template, verified in the slid capture at <paramref name="TemplateCenter"/> (must be clickable).</param>
/// <param name="Start">Client point pressed (under the resting cursor after the first slide).</param>
/// <param name="TemplateCenter">The anchor's found centre (slid check).</param>
/// <param name="Delta">Pointer movement in client pixels (the window moves the opposite way): not zero, |dx| and |dy| at
/// most <see cref="Safety.HardRules.MaxDragDistancePx"/>; the release point must lie inside the client.</param>
/// <param name="LocatedAtLog">Log position of the locating capture (popup gate reference).</param>
/// <param name="LocatedAtMonotonic">Monotonic time of the locating capture (max age 1.5 s).</param>
/// <param name="NearDenyZone">A deny zone lies within the guard distance of the start or release point (1.5 s stillness).</param>
/// <param name="LocatedFrame">The capture the anchor was located in (see <see cref="ClickRequest.LocatedFrame"/>); in
/// memory only (PRIV-14).</param>
/// <param name="PatchVerify">The engine's opt-in for the off-screen anchor patch rule at the press point (see
/// <see cref="ClickRequest.PatchVerify"/>); false (default) keeps the strict slid check.</param>
public sealed record DragRequest(
    string StepId,
    string TemplateId,
    PointI Start,
    PointI TemplateCenter,
    PointI Delta,
    LogPosition? LocatedAtLog,
    TimeSpan LocatedAtMonotonic,
    bool NearDenyZone,
    Frame? LocatedFrame = null,
    bool PatchVerify = false);

/// <summary>Result of a slide drag.</summary>
/// <param name="Press">The input as a click report: disposition, abort, phase, focus, DOWN/UP, unfocus, marks, cursor
/// evidence, slide evidence (plan of the start), slid verification, incident.</param>
/// <param name="StepsPlanned">Steps planned (each at most <see cref="Safety.HardRules.DragStepPx"/>).</param>
/// <param name="StepsDone">Steps whose window move landed and whose MOUSEMOVE was posted before the release.</param>
/// <param name="Release">Client point of the LBUTTONUP (the start moved by the steps done).</param>
public sealed record DragReport(ClickReport Press, int StepsPlanned, int StepsDone, PointI Release)
{
    /// <summary>Every step ran and the button was released normally (no abort).</summary>
    public bool Completed => Press.Disposition == InputDisposition.Sent && Press.Abort == AbortReason.None
        && Press.UpPosted && StepsPlanned > 0 && StepsDone == StepsPlanned;
}

/// <summary>Capture problems.</summary>
public enum CaptureProblem
{
    /// <summary>OK.</summary>
    None,
    /// <summary>Not connected.</summary>
    NotConnected,
    /// <summary>Minimized.</summary>
    Iconic,
    /// <summary>Cloaked (other virtual desktop).</summary>
    Cloaked,
    /// <summary>Client not 1920x1009.</summary>
    WrongSize,
    /// <summary>PrintWindow returned FALSE.</summary>
    PrintWindowFailed,
    /// <summary>Took longer than the timeout (busy game).</summary>
    Timeout,
    /// <summary>Black or uniform image.</summary>
    InvalidImage,
}

/// <summary>Capture result.</summary>
/// <param name="Problem">Problem.</param>
/// <param name="Frame">Frame if OK.</param>
/// <param name="Elapsed">Duration.</param>
public sealed record CaptureResult(CaptureProblem Problem, Frame? Frame, TimeSpan Elapsed)
{
    /// <summary>True when a valid frame was captured.</summary>
    public bool Ok => Problem == CaptureProblem.None && Frame is not null;
}

/// <summary>Who caused a home-rect deviation (WIN-9).</summary>
public enum DeviationOwner
{
    /// <summary>No deviation.</summary>
    None,
    /// <summary>Started inside our displacement (or a matching slide marker): ours to restore.</summary>
    Ours,
    /// <summary>User or game: pause, re-validate, adopt as new home only after validation.</summary>
    Others,
}

/// <summary>Home check.</summary>
/// <param name="AtHome">Window maximized at the monitor's maximized rect with a 1920x1009 client.</param>
/// <param name="Current">Current rect.</param>
/// <param name="ExpectedMaximized">Live maximized rect of the window's monitor (work area + frame).</param>
/// <param name="Owner">Deviation owner.</param>
public sealed record HomeCheck(bool AtHome, RectI Current, RectI ExpectedMaximized, DeviationOwner Owner);

/// <summary>Restore result.</summary>
/// <param name="Attempted">A restore was attempted.</param>
/// <param name="Success">Window verified at home.</param>
/// <param name="Rung">Rung that succeeded or the last tried.</param>
/// <param name="Incident">Incident record.</param>
public sealed record RestoreReport(bool Attempted, bool Success, RestoreRung Rung, WindowIncident? Incident);

/// <summary>Responsiveness probe (WM_NULL via SendMessageTimeout + IsHungAppWindow + PostMessage(WM_NULL) for UIPI).</summary>
/// <param name="Responsive">Round trip under the limit and not hung.</param>
/// <param name="Hung">IsHungAppWindow.</param>
/// <param name="RoundTrip">WM_NULL round trip.</param>
/// <param name="PostAllowed">PostMessage(WM_NULL) succeeded.</param>
/// <param name="LastError">Win32 error of a failed call (5 = UIPI).</param>
public sealed record ResponsivenessReport(bool Responsive, bool Hung, TimeSpan RoundTrip, bool PostAllowed, int LastError);

/// <summary>Pacing state of the choke point.</summary>
/// <param name="NextAllowedIn">Time until the next batch may start.</param>
/// <param name="BatchesLastMinute">Batches in the last minute.</param>
/// <param name="SlidesLastMinute">Slides in the last minute.</param>
/// <param name="BatchesLastHour">Batches in the last hour.</param>
/// <param name="FocusPairsLastMinute">Focus pairs in the last minute.</param>
public sealed record PacingStatus(TimeSpan NextAllowedIn, int BatchesLastMinute, int SlidesLastMinute, int BatchesLastHour, int FocusPairsLastMinute);
