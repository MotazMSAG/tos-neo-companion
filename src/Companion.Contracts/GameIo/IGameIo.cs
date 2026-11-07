namespace Companion.Contracts.GameIo;

/// <summary>
/// The ONLY path to the game window (unit "core-io", Companion.GameIO). Implements R1-R7 in code: every message goes
/// through one private choke point that asserts the target HWND, the message id (<c>HardRules.AllowedMessages</c>),
/// the lParam range, the SetWindowPos flags, the pacing token bucket and the stop switch. The safety gate (cursor,
/// cover, foreground, iconic, home rect, monitors) runs INSIDE <see cref="PressKey"/>, <see cref="SlideClick"/> and
/// <see cref="SlideDrag"/>; it is never injectable (PITFALL-18).
/// <para>Constructor: no side effects (no window lookup, no threads, no files). Dependencies (constructor-injected
/// contracts): IClock, IStopSwitch, ILogTracker (RecoverFocus / expected UI / LostFocuse waits, popup gate, jank
/// gate), IUiClassifier (overlay vs stack for the popup gate), IVision (slid capture verification), IJournal,
/// CompanionPaths (slide marker). <see cref="Connect"/> also starts a passive 250 ms sampler of the foreground PID
/// (read-only) that feeds <see cref="ReadinessReport.SinceGameForeground"/>.</para>
/// <para>Calls are serialized internally (one input at a time); thread-safe.</para>
/// </summary>
public interface IGameIo
{
    /// <summary>
    /// Resolves the game: EnumWindows for a visible UnityWndClass window whose PID is a TOS process with exe path
    /// D:\Games\TOS\TOS.exe (0 or several matches = problem), checks elevation/64-bit, reads the registry baseline,
    /// probes PostMessage(WM_NULL) for UIPI. Sends no input.
    /// </summary>
    ConnectResult Connect(CancellationToken ct);

    /// <summary>Current identity, or null if not connected.</summary>
    GameIdentity? Identity { get; }

    /// <summary>
    /// How long ago the game was last the foreground window (the passive sampler; read-only, no window call, cheap from any
    /// thread), or null when not connected. Used for the "while you were away" summary (roadmap A).
    /// </summary>
    TimeSpan? SinceGameForeground => null;

    /// <summary>Live read-only state (re-validates identity: IsWindow, PID, start time, class).</summary>
    GameWindowState GetState();

    /// <summary>
    /// True when the game's window is there, for the status only (4.33, incident 39): the held window still lives, or else
    /// exactly one game window of the right exe exists. Read-only: nothing is sent and nothing is bound (a job binds the
    /// window at its own start, incident 28).
    /// </summary>
    bool GameWindowPresent() => Identity is not null && GetState().Exists;

    /// <summary>Reads the whitelisted registry prefs (read-only).</summary>
    GameWindowPrefs ReadWindowPrefs();

    /// <summary>Passive readiness check (nothing is sent to the game).</summary>
    ReadinessReport CheckReadiness(ReadinessQuery query);

    /// <summary>WM_NULL responsiveness / UIPI probe (WM_NULL is the only message sent).</summary>
    ResponsivenessReport Probe();

    /// <summary>
    /// One key batch: readiness, pacing token, fake focus (SendMessageTimeout), wait for RecoverFocus in the log,
    /// re-check, KEYDOWN, 70 ms, KEYUP, wait (early exit on expected UI), fake unfocus unless the game is really
    /// foreground. KEYUP is always posted for a posted KEYDOWN.
    /// </summary>
    KeyReport PressKey(KeyRequest request, CancellationToken ct);

    /// <summary>
    /// One slide click (SPEC 7.3 timeline): readiness (cursor still, idle, valid cover, monitor/exposure/DPI rules,
    /// responsive, no jank), pacing, slide marker, watchdog, focus, re-check, slide (0x0215), settle, slid capture
    /// verification (the template; only on the engine's opt-in, the cross-monitor NavFixed rule or, for an anchor slid
    /// off the drawn monitor, the off-screen anchor patch rule), log popup gate, MOVE, DOWN, UP, wait for expected UI,
    /// unfocus while slid, wait for the LostFocuse block, slide back, verify home. The watchdog thread ends the section
    /// itself on any trigger or at the dead-man deadline. Never throws for game-side problems (reported in the result).
    /// </summary>
    ClickReport SlideClick(ClickRequest request, CancellationToken ct);

    /// <summary>
    /// One slide drag (SPEC 7.3a, proof p0_c14): the same readiness (start and release point), pacing (one input batch,
    /// one slide, one focus pair), marker, watchdog and slid capture check as <see cref="SlideClick"/> (the off-screen
    /// anchor patch rule compares the press point only; the list moves by design after it); then MOVE, DOWN,
    /// and per step: move the window by the step (SetWindowPos 0x0215) and post WM_MOUSEMOVE(MK_LBUTTON) at the moved
    /// client point; hold still, UP, unfocus while slid, slide back home. Cursor movement, a user button or any incident
    /// before the release ends it with the button released FIRST, then the slide back. Never throws for game-side problems.
    /// </summary>
    DragReport SlideDrag(DragRequest request, CancellationToken ct);

    /// <summary>PrintWindow capture of the client (validated 1920x1009, not black/uniform; works while covered).</summary>
    CaptureResult Capture(CancellationToken ct);

    /// <summary>Checks the window against its live maximized rect.</summary>
    HomeCheck CheckHome();

    /// <summary>
    /// Crash recovery (host start, guardian): if the slide marker exists and PID/HWND match and the window is at the
    /// marker's slid rect, restore its home move-only; delete the marker. Otherwise only delete a stale marker.
    /// </summary>
    RestoreReport RecoverLeftoverDisplacement();

    /// <summary>
    /// Focus resync (2026-10-18): when the game is really the foreground window but its last logged focus line is
    /// LostFocus for over a second (a fake unfocus delivered after the user switched to the game: Unity then ignores the
    /// user's UI clicks until a minimise/restore), sends the fake focus sequence once (the truth; allowed while
    /// foreground). Never during an input. Returns true when it was sent.
    /// </summary>
    bool ResyncFocus() => false;

    /// <summary>Restores a deviation that is ours (ladder). Refuses deviations owned by others (WIN-9).</summary>
    RestoreReport RestoreHomeIfOurs();

    /// <summary>Pacing state.</summary>
    PacingStatus Pacing { get; }
}
