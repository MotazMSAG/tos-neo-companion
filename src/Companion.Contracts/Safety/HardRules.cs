namespace Companion.Contracts.Safety;

/// <summary>A whitelisted key the companion may post to the game (SPEC 1, KEY-12).</summary>
public enum GameKey
{
    /// <summary>Esc (VK 0x1B, scan 0x01): closes the top UI; logs 'hide top ui nil' when nothing is open.</summary>
    Escape = 0,

    /// <summary>B (VK 0x42, scan 0x30): opens UI_BAG_MAIN (proof b, key map).</summary>
    B = 1,
}

/// <summary>Virtual-key code plus hardware scan code, exactly as posted in WM_KEYDOWN/WM_KEYUP.</summary>
/// <param name="VirtualKey">VK code (wParam).</param>
/// <param name="ScanCode">Scan code (lParam bits 16-23).</param>
public readonly record struct KeyCode(ushort VirtualKey, ushort ScanCode);

/// <summary>One focus/unfocus message as sent by lib.ps1 Set-GameFocus.</summary>
/// <param name="Message">Window message id.</param>
/// <param name="WParam">wParam value.</param>
/// <param name="LParam">lParam value.</param>
public readonly record struct FocusMessage(uint Message, long WParam, long LParam);

/// <summary>
/// Hard rules as code constants (SPEC section 1). The GameIO choke point enforces them at runtime; the static
/// rules test (tests\Companion.GameIO.Tests) checks the source tree. Values come from the proofs (EVIDENCE.md,
/// lib.ps1, p0_c3) and the adopted critique findings.
/// </summary>
public static class HardRules
{
    // ---------------------------------------------------------------- game identity / display
    /// <summary>Game process name (TOS.exe).</summary>
    public const string GameProcessName = "TOS";

    /// <summary>Game top-level window class.</summary>
    public const string GameWindowClass = "UnityWndClass";

    /// <summary>Required client width (windowed, maximized).</summary>
    public const int ExpectedClientWidth = 1920;

    /// <summary>Required client height (windowed, maximized).</summary>
    public const int ExpectedClientHeight = 1009;

    /// <summary>Required "Screenmanager Fullscreen mode" registry value (3 = windowed).</summary>
    public const int ExpectedFullscreenMode = 3;

    /// <summary>Only 96 DPI (100 %) monitors are supported for slides until proven otherwise (F12).</summary>
    public const int ExpectedDpi = 96;

    // ---------------------------------------------------------------- window messages
    /// <summary>WM_NULL (responsiveness / UIPI probe).</summary>
    public const uint WM_NULL = 0x0000;
    /// <summary>WM_SIZE: emergency restore ladder only (SIZE_MAXIMIZED, proven c9 06:50:32).</summary>
    public const uint WM_SIZE = 0x0005;
    /// <summary>WM_ACTIVATE.</summary>
    public const uint WM_ACTIVATE = 0x0006;
    /// <summary>WM_SETFOCUS.</summary>
    public const uint WM_SETFOCUS = 0x0007;
    /// <summary>WM_KILLFOCUS.</summary>
    public const uint WM_KILLFOCUS = 0x0008;
    /// <summary>WM_ACTIVATEAPP.</summary>
    public const uint WM_ACTIVATEAPP = 0x001C;
    /// <summary>WM_NCACTIVATE.</summary>
    public const uint WM_NCACTIVATE = 0x0086;
    /// <summary>WM_KEYDOWN.</summary>
    public const uint WM_KEYDOWN = 0x0100;
    /// <summary>WM_KEYUP.</summary>
    public const uint WM_KEYUP = 0x0101;
    /// <summary>WM_MOUSEMOVE.</summary>
    public const uint WM_MOUSEMOVE = 0x0200;
    /// <summary>WM_LBUTTONDOWN.</summary>
    public const uint WM_LBUTTONDOWN = 0x0201;
    /// <summary>WM_LBUTTONUP.</summary>
    public const uint WM_LBUTTONUP = 0x0202;
    /// <summary>SIZE_MAXIMIZED wParam for the emergency WM_SIZE.</summary>
    public const int SIZE_MAXIMIZED = 2;
    /// <summary>MK_LBUTTON wParam for WM_LBUTTONDOWN.</summary>
    public const int MK_LBUTTON = 0x0001;

    /// <summary>Every message the companion may send or post to the game window (runtime assert in GameIO).</summary>
    public static readonly IReadOnlyList<uint> AllowedMessages =
    [
        WM_NULL, WM_ACTIVATE, WM_SETFOCUS, WM_KILLFOCUS, WM_ACTIVATEAPP, WM_NCACTIVATE,
        WM_KEYDOWN, WM_KEYUP, WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_LBUTTONUP,
    ];

    /// <summary>Allowed only inside the emergency restore ladder (one posted WM_SIZE(SIZE_MAXIMIZED, client size)).</summary>
    public static readonly IReadOnlyList<uint> EmergencyOnlyMessages = [WM_SIZE];

    /// <summary>
    /// Messages that must never reach the game (documentation + test data): WM_CHAR 0x102, WM_SYSKEYDOWN 0x104,
    /// WM_SYSKEYUP 0x105, WM_SYSCHAR 0x106, WM_NCLBUTTONDOWN 0xA1 (move loop), WM_SYSCOMMAND 0x112, WM_CLOSE 0x10,
    /// WM_SHOWWINDOW 0x18, WM_MOVE 0x3, WM_WINDOWPOSCHANGED 0x47, WM_TOUCH 0x240, WM_POINTERDOWN 0x246, WM_QUIT 0x12,
    /// WM_DESTROY 0x2, WM_MOUSEWHEEL 0x20A (unproven).
    /// </summary>
    public static readonly IReadOnlyList<uint> ForbiddenMessages =
        [0x0102, 0x0104, 0x0105, 0x0106, 0x00A1, 0x0112, 0x0010, 0x0018, 0x0003, 0x0047, 0x0240, 0x0246, 0x0012, 0x0002, 0x020A];

    /// <summary>Fake focus, in this order (lib.ps1 Set-GameFocus $true). Each via SendMessageTimeout.</summary>
    public static readonly IReadOnlyList<FocusMessage> FocusOnSequence =
    [
        new(WM_ACTIVATEAPP, 1, 0), new(WM_NCACTIVATE, 1, 0), new(WM_ACTIVATE, 1, 0), new(WM_SETFOCUS, 0, 0),
    ];

    /// <summary>Fake unfocus, in this order (lib.ps1 Set-GameFocus $false). Never sent while the game is really foreground.</summary>
    public static readonly IReadOnlyList<FocusMessage> FocusOffSequence =
    [
        new(WM_KILLFOCUS, 0, 0), new(WM_ACTIVATE, 0, 0), new(WM_NCACTIVATE, 0, 0), new(WM_ACTIVATEAPP, 0, 0),
    ];

    // ---------------------------------------------------------------- SetWindowPos
    /// <summary>SWP_NOSIZE.</summary>
    public const uint SWP_NOSIZE = 0x0001;
    /// <summary>SWP_NOZORDER.</summary>
    public const uint SWP_NOZORDER = 0x0004;
    /// <summary>SWP_NOACTIVATE.</summary>
    public const uint SWP_NOACTIVATE = 0x0010;
    /// <summary>SWP_NOOWNERZORDER.</summary>
    public const uint SWP_NOOWNERZORDER = 0x0200;
    /// <summary>SWP_ASYNCWINDOWPOS (watchdog / forced slide-back only; cannot block on a hung game).</summary>
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;

    /// <summary>Slide and slide-back: NOSIZE|NOZORDER|NOACTIVATE|NOOWNERZORDER (proven c3).</summary>
    public const uint SwpSlide = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER; // 0x0215

    /// <summary>Forced slide-back from the watchdog thread: <see cref="SwpSlide"/> | ASYNC.</summary>
    public const uint SwpSlideAsync = SwpSlide | SWP_ASYNCWINDOWPOS; // 0x4215

    /// <summary>Emergency restore rung 2 (move + size): NOZORDER|NOACTIVATE|NOOWNERZORDER.</summary>
    public const uint SwpEmergencyMoveSize = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER; // 0x0214

    /// <summary>Emergency restore rung 2 from the watchdog thread.</summary>
    public const uint SwpEmergencyMoveSizeAsync = SwpEmergencyMoveSize | SWP_ASYNCWINDOWPOS; // 0x4214

    /// <summary>SetWindowPos flag sets allowed on the normal path (hWndInsertAfter must be 0).</summary>
    public static readonly IReadOnlyList<uint> AllowedSetWindowPosFlags = [SwpSlide, SwpSlideAsync];

    /// <summary>SetWindowPos flag sets allowed only inside the emergency restore ladder.</summary>
    public static readonly IReadOnlyList<uint> EmergencySetWindowPosFlags = [SwpEmergencyMoveSize, SwpEmergencyMoveSizeAsync];

    /// <summary>PrintWindow flag PW_RENDERFULLCONTENT (proof a).</summary>
    public const uint PW_RENDERFULLCONTENT = 0x0002;

    // ---------------------------------------------------------------- keys
    /// <summary>Exact (VK, scan) pairs the companion may post. Nothing else, ever, until a proof adds one.</summary>
    public static readonly IReadOnlyDictionary<GameKey, KeyCode> AllowedKeys = new Dictionary<GameKey, KeyCode>
    {
        [GameKey.Escape] = new(0x1B, 0x01),
        [GameKey.B] = new(0x42, 0x30),
    };

    /// <summary>Hard-denied even if the whitelist is edited: Enter (chat), Y (Recharge = purchase), I (server login).</summary>
    public static readonly IReadOnlyList<KeyCode> DeniedKeys = [new(0x0D, 0x1C), new(0x59, 0x15), new(0x49, 0x17)];

    /// <summary>KEYDOWN lParam: repeat 1 | scan &lt;&lt; 16.</summary>
    public static long KeyDownLParam(KeyCode k) => 1L | ((long)k.ScanCode << 16);

    /// <summary>KEYUP lParam: repeat 1 | scan &lt;&lt; 16 | previous-state | transition (0xC0000000).</summary>
    public static long KeyUpLParam(KeyCode k) => (1L | ((long)k.ScanCode << 16)) | 0xC0000000L;

    /// <summary>Mouse lParam from CLIENT coordinates: (y &lt;&lt; 16) | (x &amp; 0xFFFF).</summary>
    public static long MouseLParam(PointI client) => ((long)(client.Y & 0xFFFF) << 16) | (long)(client.X & 0xFFFF);

    // ---------------------------------------------------------------- proven timings (lib.ps1 / p0_c3)
    /// <summary>Minimum wait after the focus messages before acting (proven 300 ms).</summary>
    public static readonly TimeSpan FocusSettle = TimeSpan.FromMilliseconds(300);
    /// <summary>Max wait for '[StageMgr] OnApplicationFocus RecoverFocus' after our focus messages (else FOCUS_FAILED).</summary>
    public static readonly TimeSpan RecoverFocusLogWait = TimeSpan.FromMilliseconds(1000);
    /// <summary>KEYDOWN to KEYUP (proven 70 ms).</summary>
    public static readonly TimeSpan KeyHold = TimeSpan.FromMilliseconds(70);
    /// <summary>KEYUP to unfocus (proven 700 ms; may end early when the expected UI is logged).</summary>
    public static readonly TimeSpan KeyAfterUp = TimeSpan.FromMilliseconds(700);
    /// <summary>Slide landed to MOUSEMOVE: settle so the game reads the cursor through the new position (proven 150 ms).</summary>
    public static readonly TimeSpan SlideSettle = TimeSpan.FromMilliseconds(150);
    /// <summary>MOUSEMOVE to LBUTTONDOWN (proven 50 ms).</summary>
    public static readonly TimeSpan MoveToDown = TimeSpan.FromMilliseconds(50);
    /// <summary>LBUTTONDOWN to LBUTTONUP (proven 70 ms).</summary>
    public static readonly TimeSpan DownToUp = TimeSpan.FromMilliseconds(70);
    /// <summary>Max wait after UP for the expected UI while still slid (proven 400 ms; early exit).</summary>
    public static readonly TimeSpan AfterUpWait = TimeSpan.FromMilliseconds(400);
    /// <summary>Max wait after the slid unfocus for the 'UpdateModule LostFocuse' block (early exit).</summary>
    public static readonly TimeSpan PressBlockWait = TimeSpan.FromMilliseconds(300);

    // ---------------------------------------------------------------- slide drag (proof p0_c14, live 2026-10-14)
    /// <summary>Largest pointer move per drag step; the window moves by the same amount (proven c14: 20 px).</summary>
    public const int DragStepPx = 20;
    /// <summary>Most steps of one drag.</summary>
    public const int MaxDragSteps = 20;
    /// <summary>Largest |dx| or |dy| of one drag (<see cref="MaxDragSteps"/> x <see cref="DragStepPx"/>).</summary>
    public const int MaxDragDistancePx = MaxDragSteps * DragStepPx;
    /// <summary>MOUSEMOVE to LBUTTONDOWN at the drag start (proven c14: 40 ms).</summary>
    public static readonly TimeSpan DragMoveToDown = TimeSpan.FromMilliseconds(40);
    /// <summary>LBUTTONDOWN to the first drag step (proven c14: 120 ms).</summary>
    public static readonly TimeSpan DragDownToFirstStep = TimeSpan.FromMilliseconds(120);
    /// <summary>Time per drag step: window move, MOUSEMOVE(MK_LBUTTON), wait (proven c14: 45 ms).</summary>
    public static readonly TimeSpan DragStepInterval = TimeSpan.FromMilliseconds(45);
    /// <summary>Hold still after the last step before LBUTTONUP so the list does not fling (proven c14: 250 ms).</summary>
    public static readonly TimeSpan DragHoldBeforeUp = TimeSpan.FromMilliseconds(250);
    /// <summary>LBUTTONUP to the fake unfocus (proven c14: 250 ms).</summary>
    public static readonly TimeSpan DragAfterUp = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Dead-man of a drag of <paramref name="steps"/> steps: the click's <see cref="DeadManDeadline"/> plus the drag's own
    /// proven timeline (down to first step, the steps, the hold). 15 steps (300 px) = 2.545 s; the most (20) = 2.77 s.
    /// </summary>
    public static TimeSpan DragDeadManDeadline(int steps) =>
        DeadManDeadline + DragDownToFirstStep + (DragStepInterval * Math.Clamp(steps, 0, MaxDragSteps)) + DragHoldBeforeUp;

    // ---------------------------------------------------------------- slide safety
    /// <summary>
    /// Cursor must be still this long before a slide (proven 0.8 s). The user's rule (2026-10-16): try whenever the mouse
    /// rests, abort and retry later when it moves during the action (the slide watchdog), so the companion works while the
    /// user uses the PC; no longer pre-wait than this.
    /// </summary>
    public static readonly TimeSpan CursorStillness = TimeSpan.FromMilliseconds(800);
    /// <summary>Stillness required when a deny zone lies within <see cref="DenyZoneGuardPx"/> of the target (CLK-5).</summary>
    public static readonly TimeSpan CursorStillnessNearDenyZone = TimeSpan.FromMilliseconds(1500);
    /// <summary>
    /// System idle (GetLastInputInfo) required before a slide (F29): zero by the user's decision (2026-10-16): keyboard
    /// input elsewhere never blocks the companion; the mouse is covered by <see cref="CursorStillness"/> before the action
    /// and the slide watchdog (abort on any cursor move or button) during it.
    /// </summary>
    public static readonly TimeSpan SystemIdleForSlide = TimeSpan.Zero;
    /// <summary>No input of any kind until the game has not been foreground for this long (F29/FOC-8).</summary>
    public static readonly TimeSpan CooldownAfterGameForeground = TimeSpan.FromSeconds(5); // was 60 (the user, 2026-10-20); cursor stillness and the slide watchdog still guard every press
    /// <summary>No input until no foreign RecoverFocus / OnScreenSizeChange was logged for this long (FOC-8).</summary>
    public static readonly TimeSpan CooldownAfterForeignFocus = TimeSpan.FromSeconds(5); // was 30 (the user, 2026-10-20)

    // ---------------------------------------------------------------- foreground mode (SPEC 7.3b, the user 2026-11-02, EVIDENCE c15)
    /// <summary>Foreground mode: the game must have been the foreground window continuously this long (the user's own switch-in settles).</summary>
    public static readonly TimeSpan ForegroundSettle = TimeSpan.FromSeconds(5);
    /// <summary>Foreground mode: the cursor must rest on the game this long before a slide (the user is watching the game).</summary>
    public static readonly TimeSpan ForegroundCursorStillness = TimeSpan.FromSeconds(2);
    /// <summary>Foreground mode: no keyboard or mouse input (GetLastInputInfo) for this long before a slide.</summary>
    public static readonly TimeSpan ForegroundSystemIdle = TimeSpan.FromSeconds(2);
    /// <summary>Foreground mode: no keyboard or mouse input for this long before a key (the user may be typing in the game).</summary>
    public static readonly TimeSpan ForegroundKeyIdle = TimeSpan.FromSeconds(3);
    /// <summary>
    /// Foreground mode, the drain before the window moves home: a WM_NULL round trip (the game's pump has begun; a thread is
    /// handed its sent messages before its posted ones), this wait, and a second round trip, which the game answers only at
    /// a later pump, after the frame that read our DOWN/UP ran its input at the slid position (the game reads the cursor
    /// per frame: a button and the slide-back read in one frame hit-test at home, EVIDENCE c6). There is no fake unfocus to
    /// make a late button harmless, as in cover mode.
    /// </summary>
    public static readonly TimeSpan ForegroundDrainSettle = TimeSpan.FromMilliseconds(50);
    /// <summary>Foreground mode, the normal path's drain limit; past it the abort's drain takes over (the dead-man fires first).</summary>
    public static readonly TimeSpan ForegroundDrainMax = TimeSpan.FromSeconds(2);
    /// <summary>
    /// Foreground mode abort: the longest wait for a game that stopped responding with our DOWN/UP unread. A window moved
    /// home before the game reads them makes the press land at home when it wakes up, so the window waits slid (the game is
    /// frozen anyway); past this it goes home (R3) and the input is a HALT (<c>GameStoppedMidPress</c>).
    /// </summary>
    public static readonly TimeSpan ForegroundDrainHangMax = TimeSpan.FromSeconds(30);
    /// <summary>
    /// Foreground mode, crash recovery: a slide marker of a foreground-mode input is restored only after the drain succeeds
    /// within this long (the dead section may have left our DOWN/UP unread); otherwise the window stays and the marker is
    /// kept for the next recovery (the guardian, the next start).
    /// </summary>
    public static readonly TimeSpan ForegroundRecoveryDrainMax = TimeSpan.FromSeconds(10);
    /// <summary>Dead-man: the watchdog thread restores home (async, move-only) once displaced this long (WIN-3).</summary>
    public static readonly TimeSpan DeadManDeadline = TimeSpan.FromMilliseconds(1500);
    /// <summary>Normal displacement budget; exceeding it is journaled (c3 PASS was 0.84 s).</summary>
    public static readonly TimeSpan DisplacementBudget = TimeSpan.FromMilliseconds(1200);
    /// <summary>SendMessageTimeout timeout for every sent message (SMTO_ABORTIFHUNG|SMTO_BLOCK).</summary>
    public static readonly TimeSpan SendMessageTimeout = TimeSpan.FromMilliseconds(250);
    /// <summary>WM_NULL round trip above this means "game busy": no slide.</summary>
    public static readonly TimeSpan ResponsivenessProbeMax = TimeSpan.FromMilliseconds(200);
    /// <summary>A SetWindowPos call slower than this is a WINDOW_INCIDENT record (F26).</summary>
    public static readonly TimeSpan SlowSetWindowPos = TimeSpan.FromMilliseconds(200);
    /// <summary>PrintWindow budget (runs on a worker thread with this timeout).</summary>
    public static readonly TimeSpan PrintWindowTimeout = TimeSpan.FromMilliseconds(250);
    /// <summary>No slide within this long after a '[jank_detector] jank over' line or an open scene_load bracket.</summary>
    public static readonly TimeSpan JankQuiet = TimeSpan.FromSeconds(2);
    /// <summary>Max age of the locating capture when SlideClick is called (CAP-16/PITFALL-15).</summary>
    public static readonly TimeSpan MaxLocateAge = TimeSpan.FromMilliseconds(1500);
    /// <summary>Watchdog target sample period while displaced (high-resolution waitable timer, WD-20).</summary>
    public static readonly TimeSpan WatchdogTargetPeriod = TimeSpan.FromMilliseconds(2);
    /// <summary>Aim tolerance: LostFocuse screenPosition and slid template verification (F03).</summary>
    public const int AimTolerancePx = 2;
    /// <summary>Largest horizontal off-centre aim inside a button (engine aim offsets); a farther press is a step offset.</summary>
    public const int MaxAimOffsetXPx = 18;
    /// <summary>Largest vertical off-centre aim inside a button (engine aim offsets); a farther press is a step offset.</summary>
    public const int MaxAimOffsetYPx = 10;
    /// <summary>Upper bound for a screen anchor's declared drift between captures (TemplateDefinition.MotionTolerancePx).</summary>
    public const int MaxAnchorMotionPx = 8;
    /// <summary>Cursor must be at least this far inside the cover window's DWM extended frame (PITFALL-11).</summary>
    public const int CoverMarginPx = 16;
    /// <summary>Cover must be the same root window at 8 points this far around the cursor (PITFALL-11).</summary>
    public const int CoverProbeOffsetPx = 12;
    /// <summary>Deny zones within this distance of a click target require <see cref="CursorStillnessNearDenyZone"/>.</summary>
    public const int DenyZoneGuardPx = 120;
    /// <summary>A click point (found centre) inside a deny zone inflated by this many pixels is refused (HALT LayoutChanged).</summary>
    public const int DenyZoneRefusePx = 8;
    /// <summary>Max |dx| or |dy| of a slide (WIN-10).</summary>
    public const int MaxSlideDistancePx = 2000;
    /// <summary>Max z-order walk steps (PITFALL-11).</summary>
    public const int MaxZOrderWalk = 2048;

    // ---------------------------------------------------------------- vision floors (code, not json: SEC-1)
    /// <summary>NCC threshold floor; a templates.json value below it is raised to it.</summary>
    public const double MinNccThreshold = 0.85;
    /// <summary>Best-minus-second-best floor (F25).</summary>
    public const double MinMatchMargin = 0.05;
    /// <summary>Search radius ceiling in pixels.</summary>
    public const int MaxSearchRadiusPx = 40;
    /// <summary>Two HUD captures used for a state decision must be at least this far apart (F07).</summary>
    public static readonly TimeSpan StableCaptureGap = TimeSpan.FromMilliseconds(300);

    // ---------------------------------------------------------------- pacing at the GameIO choke point (R7, PACE-7)
    /// <summary>Minimum gap between two input batches (a batch = one PressKey, one SlideClick or one SlideDrag).</summary>
    public static readonly TimeSpan MinGapBetweenInputBatches = TimeSpan.FromSeconds(1);
    /// <summary>Max input batches per rolling minute (user 2026-10-15: human browsing speed; was 20).</summary>
    public const int MaxInputBatchesPerMinute = 40;
    /// <summary>Max slides per rolling minute (user 2026-10-15: about one click every 1.5 s; was 20, before 2026-10-14 3).</summary>
    public const int MaxSlidesPerMinute = 40;
    /// <summary>Max input batches per rolling hour (2026-10-15; was 1200).</summary>
    public const int MaxInputBatchesPerHour = 2400;
    /// <summary>Max focus/unfocus pairs per rolling minute (ABORTED_AFTER_FOCUS cap, F02; 2026-10-15, was 20).</summary>
    public const int MaxFocusPairsPerMinute = 40;

    // The commit pace (2026-10-15): a commit input (a click on a Commit-tagged or gated target, e.g. Make and its max
    // button) keeps the 2026-10-14 pace. It starts only while ALL inputs of the rolling window stay under these caps, so
    // raising the browsing pace above never makes the Make button faster.

    /// <summary>A commit input starts only while fewer input batches than this fell in the rolling minute.</summary>
    public const int MaxCommitInputBatchesPerMinute = 20;
    /// <summary>A commit input starts only while fewer slides than this fell in the rolling minute (one every 3 s).</summary>
    public const int MaxCommitSlidesPerMinute = 20;
    /// <summary>A commit input starts only while fewer input batches than this fell in the rolling hour.</summary>
    public const int MaxCommitInputBatchesPerHour = 1200;
    /// <summary>A commit input starts only while fewer focus pairs than this fell in the rolling minute.</summary>
    public const int MaxCommitFocusPairsPerMinute = 20;
    /// <summary>Max jobs accepted per rolling hour (SEC-2).</summary>
    public const int MaxJobsPerHour = 60; // was 10 (2026-10-18: the queue, rule mode and the user's own starts all count; the input pacing keeps the human pace)

    // ---------------------------------------------------------------- per-procedure ceilings (data may lower, never raise)
    /// <summary>Ceiling for <c>ProcedureLimits.MaxInputBatches</c>.</summary>
    public const int CeilingInputBatchesPerProcedure = 60;
    /// <summary>Ceiling for <c>ProcedureLimits.MaxClicks</c>.</summary>
    public const int CeilingClicksPerProcedure = 20;
    /// <summary>Ceiling for <c>ProcedureLimits.MaxEsc</c>.</summary>
    public const int CeilingEscPerProcedure = 12;
    /// <summary>Max Esc presses inside one Reset (each must make progress, F20).</summary>
    public const int MaxEscPerReset = 10;

    // ---------------------------------------------------------------- engine timing
    /// <summary>Minimum step timeout (F06).</summary>
    public static readonly TimeSpan MinStepTimeout = TimeSpan.FromSeconds(3);
    /// <summary>Minimum step timeout when the expected UI has not been shown yet this game session (asset load).</summary>
    public static readonly TimeSpan FirstOpenStepTimeout = TimeSpan.FromSeconds(6);
    /// <summary>Settle read after a timeout before any retry (late success, F06).</summary>
    public static readonly TimeSpan SettleAfterTimeout = TimeSpan.FromMilliseconds(1500);
    /// <summary>Settle after an UNCERTAIN attempt before classifying or retrying (F17).</summary>
    public static readonly TimeSpan UncertainSettle = TimeSpan.FromSeconds(2);
    /// <summary>A UI shown within this window after our DOWN/KEYDOWN is attributed to us (UI-6).</summary>
    public static readonly TimeSpan AttributionWindow = TimeSpan.FromMilliseconds(1500);
    /// <summary>
    /// The shorter read of a <see cref="Engine.StepFlags.QuickAttribution"/> click that expects nothing (2026-10-31, the Tea
    /// House board: its job confirms each press from the log and checks the top window itself afterwards).
    /// </summary>
    public static readonly TimeSpan QuickAttributionWindow = TimeSpan.FromMilliseconds(500);
    /// <summary>
    /// The log's margin after a QuickAttribution click's full <see cref="AttributionWindow"/> before its late check reads
    /// the events the window attributes to it (by the game's timestamps; 2026-10-31 safety review). 700 ms since the
    /// 2026-10-31 speed audit: game log lines became readable up to ~0.4 s after their own timestamps, and the full read
    /// before 4.25 ended ~0.5-0.8 s after the window; the next board's read and locate take longer anyway.
    /// </summary>
    public static readonly TimeSpan LateCheckMargin = TimeSpan.FromMilliseconds(700);
    /// <summary>A UI shown with no input of ours in this preceding window is foreign (UI-6).</summary>
    public static readonly TimeSpan ForeignQuiet = TimeSpan.FromSeconds(2);
    /// <summary>The log must have grown within this window before every action (LOG-01).</summary>
    public static readonly TimeSpan LogHeartbeatMax = TimeSpan.FromSeconds(5);
    /// <summary>No log growth for this long while waiting = HALT LogStalled.</summary>
    public static readonly TimeSpan LogStallHalt = TimeSpan.FromSeconds(30);
    /// <summary>Back-off after '[base_network.lua] request too frequency' (F21).</summary>
    public static readonly TimeSpan ThrottleBackoff = TimeSpan.FromSeconds(60);
    /// <summary>Combat indicators (UI_BOSS_CHANT, UI_BLOOD_BAR, UI_LOCK_TARGET shows) within this window pause clicks/commits.</summary>
    public static readonly TimeSpan CombatRecent = TimeSpan.FromSeconds(10);
    /// <summary>Default patience of a WAIT before the job is parked (F22).</summary>
    public static readonly TimeSpan ParkPatience = TimeSpan.FromMinutes(10);
    /// <summary>Notify the user once when a job has been parked this long (F22).</summary>
    public static readonly TimeSpan NotifyAfterParked = TimeSpan.FromMinutes(30);
    /// <summary>A parked job expires (Cancelled, notified) after this long.</summary>
    public static readonly TimeSpan ParkExpiry = TimeSpan.FromHours(4);
    /// <summary>
    /// A press that stays unreachable from where the cursor rests (another monitor, no cover, too far to slide) this long
    /// ends its procedure Deferred instead of waiting out <see cref="ParkPatience"/> and parking (live 2026-10-16: the
    /// Make tab waited with the cursor on the other monitor; the caller does other work and comes back).
    /// </summary>
    public static readonly TimeSpan UnreachableLimit = TimeSpan.FromSeconds(45);
    /// <summary>
    /// Exhausted recovery ladders (retries, step-backs, restarts) end Deferred, not in a latched HALT, this many times per
    /// <see cref="SoftEndWindow"/>; one more is a HALT (a runaway backstop: something keeps going wrong).
    /// </summary>
    public const int SoftEndsBeforeHalt = 3;
    /// <summary>The window of <see cref="SoftEndsBeforeHalt"/>.</summary>
    public static readonly TimeSpan SoftEndWindow = TimeSpan.FromMinutes(30);
    /// <summary>ABORTED_AFTER_FOCUS back-off start (doubles up to <see cref="BackoffMax"/>).</summary>
    public static readonly TimeSpan BackoffStart = TimeSpan.FromSeconds(2);
    /// <summary>ABORTED_AFTER_FOCUS back-off ceiling.</summary>
    public static readonly TimeSpan BackoffMax = TimeSpan.FromSeconds(60);
    /// <summary>Restored window incidents allowed per hour before HALT.</summary>
    public const int MaxRestoredIncidentsPerHour = 2;
}
