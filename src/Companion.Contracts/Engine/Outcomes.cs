namespace Companion.Contracts.Engine;

/// <summary>Outcome of one step attempt (SPEC 6.3). Its class is fixed by <see cref="OutcomePolicy.ClassOf"/>.</summary>
public enum StepOutcome
{
    // ----- Ok
    /// <summary>Expectation met.</summary>
    Ok,
    /// <summary>Expectation met, logged after the timeout but found by the settle read (F06). Never retried.</summary>
    LateSuccess,

    // ----- Wait (uncounted; no input while waiting; patience -> Park)
    /// <summary>Zero messages sent: cursor not usable, cover invalid, game busy, rate limit... (F02).</summary>
    NotSent,
    /// <summary>Focus sent, aborted before DOWN/KEYDOWN. Back-off 2-60 s; focus pairs capped per minute.</summary>
    AbortedAfterFocus,
    /// <summary>Cursor moved in P4, forced slide-back, jank, or press location unverifiable: settle 2 s, decide from the log.</summary>
    Uncertain,
    /// <summary>No LostFocuse block: the press was not registered (F07). Retry allowed.</summary>
    PressNotRegistered,
    /// <summary>The game became foreground / minimized, or the user was active recently (R4, F10, F29).</summary>
    UserActive,
    /// <summary>A stack UI with no input of ours, or user actions during the step. Commit jobs abort instead (CR-07).</summary>
    UserInterference,
    /// <summary>A popup (queued real-show, stack UI) appeared between locate and DOWN; nothing pressed (F08).</summary>
    Popup,
    /// <summary>A foreign Forbidden/Social UI is open: pause + notify, no Esc (UI-6).</summary>
    ForeignBlockingUi,
    /// <summary>Loading, cinema, scenario, login, revive, guide mask, match (F16).</summary>
    Busy,
    /// <summary>First FOCUS_FAILED (no RecoverFocus): one uncounted retry after 5 s (F23).</summary>
    FocusFailedOnce,
    /// <summary>Locked session, secure desktop, GetCursorPos failing (F19).</summary>
    SessionUnavailable,
    /// <summary>Wall/monotonic clock jump (sleep/resume): restart timers, settle the log (F19).</summary>
    ClockJump,
    /// <summary>Pacing token not available.</summary>
    RateLimited,
    /// <summary>'request too frequency' within the back-off window.</summary>
    Throttled,
    /// <summary>Auto-collect status not confirmed idle (1, or an unverified value) (F15).</summary>
    AutoCollectNotIdle,
    /// <summary>Combat indicators recent (clicks and commits only).</summary>
    CombatRecent,
    /// <summary>Not windowed-maximized 1920x1009 / prefs differ: wait + notify once (F13, GAME-18).</summary>
    DisplayModeWrong,
    /// <summary>Log quiet for more than 5 s but less than 30 s.</summary>
    LogQuiet,
    /// <summary>Window incident restored by the ladder: pause 30 s and re-evaluate (max 2 per hour).</summary>
    WindowIncidentRestored,
    /// <summary>Precondition false for a transient cause (loading, popup, user active, HUD 'unknown' while animating).</summary>
    PreconditionTransient,
    /// <summary>Another stack UI opened but the aim was not verified (cursor moved / jank): Esc if Esc-safe, uncounted.</summary>
    WrongUiUnverified,
    /// <summary>An unknown UI opened after our input: one Esc + survey capture; a second in the job is a HALT.</summary>
    UnknownUi,
    /// <summary>A craft session is in progress: no input (CR-01).</summary>
    CraftInProgress,
    /// <summary>A non-Esc-safe UI of the user is open at job start: park + notify once (F20).</summary>
    UserUiOpen,

    // ----- Counted
    /// <summary>Press registered (LostFocuse block), aim verified, log settled, no jank, and nothing happened.</summary>
    None,
    /// <summary>Precondition false for a non-transient cause (template missing in 2 stable captures, HUD unknown outside loading).</summary>
    PreconditionLost,
    /// <summary>Another stack UI opened with the aim verified: close, mark template suspect, ONE retry if unique; a second one = HALT RecalibrateTemplate (F05).</summary>
    WrongUiVerifiedAim,

    // ----- Halt (no retry)
    /// <summary>A forbidden UI or pay line after our input: one Esc (verified), then HALT (F09).</summary>
    ForbiddenUi,
    /// <summary>An Esc opened a UI (F20).</summary>
    EscOpenedUi,
    /// <summary>'ban by guide' after our input (F16).</summary>
    GuideBan,
    /// <summary>Extra visible TOS.exe window (security/webview dialog).</summary>
    ExtraGameWindow,
    /// <summary>ClipCursor / cursor visibility changed while we held fake focus (F27).</summary>
    CursorClipChanged,
    /// <summary>Restore ladder failed.</summary>
    RestoreFailed,
    /// <summary>The game resized its own window during our step.</summary>
    GameSelfResize,
    /// <summary>Template found outside its radius: layout changed (F25).</summary>
    LayoutChanged,
    /// <summary>The press went into the world (empty raycast at the aim, UI_LOCK_*, UI_NPC_TALK) (PRESS-15).</summary>
    WorldPress,
    /// <summary>Second verified-aim wrong UI on the same template.</summary>
    RecalibrateTemplate,
    /// <summary>Foreground mode: the game stopped responding with our press unread and its window was moved home anyway (HALT).</summary>
    GameStoppedMidPress,
    /// <summary>Second FOCUS_FAILED: 'game not responding to focus (UIPI?)'.</summary>
    FocusFailed,
    /// <summary>ERROR_ACCESS_DENIED on post/send.</summary>
    IntegrityMismatch,
    /// <summary>HWND/PID/start time/class changed mid-job.</summary>
    IdentityChanged,
    /// <summary>No log growth for 30 s while waiting (LOG-01).</summary>
    LogStalled,
    /// <summary>Same (step, state hash) reached too often.</summary>
    RepeatedState,
    /// <summary>Per-procedure or hourly budget exhausted.</summary>
    BudgetExceeded,
    /// <summary>Two early-stopped crafts on the same recipe (CR-05).</summary>
    CraftStoppedEarlyTwice,
    /// <summary>is_block without a station UI (CR-06).</summary>
    BlockedCraftSpot,
    /// <summary>Auto-collect status changed inside our step window (F15).</summary>
    AutoCollectChanged,
    /// <summary>Data files differ from the trusted build (SEC-1).</summary>
    TamperedFiles,
    /// <summary>Unhandled exception in the engine or a feature handler.</summary>
    InternalError,

    // ----- Terminal (job ends, not a HALT)
    /// <summary>The game exited / log rotated: no restore attempts (F23).</summary>
    GameExited,
    /// <summary>Kill switch.</summary>
    Stopped,
    /// <summary>Refused: automation switch off, character not Allowed, slide clicks disabled, provisional target, job not whitelisted.</summary>
    Refused,
    /// <summary>A needsSurvey stub or reader was reached.</summary>
    NeedsSurvey,
    /// <summary>Cancelled by the user or parked too long.</summary>
    Cancelled,
    /// <summary>
    /// The fallback ladder's last rung (FALLBACKS.md): the step cannot be done now (unreachable from where the cursor
    /// rests, or its retries, step-backs and restarts ran out) and nothing unsafe happened. The job ends without a HALT;
    /// the caller does other work and tries again later (2026-10-16: the user, "nothing stops us").
    /// </summary>
    Deferred,
}

/// <summary>Outcome classes (F00).</summary>
public enum OutcomeClass
{
    /// <summary>Success.</summary>
    Ok,
    /// <summary>Never counted; no input while waiting; patience then Park.</summary>
    Wait,
    /// <summary>Counted toward the step / procedure limits.</summary>
    Counted,
    /// <summary>Immediate HALT (latched, diagnostics, notification).</summary>
    Halt,
    /// <summary>Job ends without HALT.</summary>
    Terminal,
}

/// <summary>Decision taken after an attempt (journaled with the class).</summary>
public enum RecoveryAction
{
    /// <summary>Next step.</summary>
    Proceed,
    /// <summary>Retry the same step without counting.</summary>
    RetryUncounted,
    /// <summary>Wait for the gate to clear (passive polling / back-off), then re-check and retry.</summary>
    WaitThenRetry,
    /// <summary>Close what we opened (Esc, log-verified), then retry.</summary>
    CloseThenRetry,
    /// <summary>Retry, counted.</summary>
    RetryCounted,
    /// <summary>Go back to the latest earlier step whose precondition holds.</summary>
    StepBack,
    /// <summary>Reset (Esc until nil) and restart the procedure.</summary>
    ResetAndRestart,
    /// <summary>Park the job (keys-only compensation), resume automatically when ready.</summary>
    Park,
    /// <summary>HALT.</summary>
    Halt,
    /// <summary>End the job (terminal outcome).</summary>
    End,
}

/// <summary>The fixed outcome-to-class table (SPEC 6.3). Builders implement the actions; the classes are not negotiable.</summary>
public static class OutcomePolicy
{
    /// <summary>Class of an outcome.</summary>
    public static OutcomeClass ClassOf(StepOutcome outcome) => outcome switch
    {
        StepOutcome.Ok or StepOutcome.LateSuccess => OutcomeClass.Ok,

        StepOutcome.NotSent or StepOutcome.AbortedAfterFocus or StepOutcome.Uncertain or StepOutcome.PressNotRegistered
            or StepOutcome.UserActive or StepOutcome.UserInterference or StepOutcome.Popup or StepOutcome.ForeignBlockingUi
            or StepOutcome.Busy or StepOutcome.FocusFailedOnce or StepOutcome.SessionUnavailable or StepOutcome.ClockJump
            or StepOutcome.RateLimited or StepOutcome.Throttled or StepOutcome.AutoCollectNotIdle or StepOutcome.CombatRecent
            or StepOutcome.DisplayModeWrong or StepOutcome.LogQuiet or StepOutcome.WindowIncidentRestored
            or StepOutcome.PreconditionTransient or StepOutcome.WrongUiUnverified or StepOutcome.UnknownUi
            or StepOutcome.CraftInProgress or StepOutcome.UserUiOpen => OutcomeClass.Wait,

        StepOutcome.None or StepOutcome.PreconditionLost or StepOutcome.WrongUiVerifiedAim => OutcomeClass.Counted,

        StepOutcome.GameExited or StepOutcome.Stopped or StepOutcome.Refused or StepOutcome.NeedsSurvey
            or StepOutcome.Cancelled or StepOutcome.Deferred or StepOutcome.IdentityChanged => OutcomeClass.Terminal,

        _ => OutcomeClass.Halt,
    };
}

/// <summary>HALT codes (each has a fixed user message in SPEC 6.6). <see cref="StepOutcome"/> halts map 1:1; the rest are ladder exhaustion.</summary>
public enum HaltCode
{
    /// <summary>Forbidden / purchase UI opened after our input.</summary>
    ForbiddenUi,
    /// <summary>Esc opened a UI.</summary>
    EscOpenedUi,
    /// <summary>Tutorial guide blocks the UI.</summary>
    GuideBan,
    /// <summary>Extra game window (security dialog).</summary>
    ExtraGameWindow,
    /// <summary>Cursor clip changed.</summary>
    CursorClipChanged,
    /// <summary>Window could not be restored.</summary>
    RestoreFailed,
    /// <summary>Game resized its window.</summary>
    GameSelfResize,
    /// <summary>Layout changed (template far from expected).</summary>
    LayoutChanged,
    /// <summary>Press went into the world.</summary>
    WorldPress,
    /// <summary>Template needs recalibration.</summary>
    RecalibrateTemplate,
    /// <summary>Foreground mode: the game stopped responding in the middle of a press; its window was moved home after the wait.</summary>
    GameStoppedMidPress,
    /// <summary>Game does not react to focus.</summary>
    FocusFailed,
    /// <summary>UIPI / integrity mismatch.</summary>
    IntegrityMismatch,
    /// <summary>Game identity changed.</summary>
    IdentityChanged,
    /// <summary>Log not growing.</summary>
    LogStalled,
    /// <summary>Same state reached repeatedly.</summary>
    RepeatedState,
    /// <summary>Budget exhausted.</summary>
    BudgetExceeded,
    /// <summary>3 counted failures + step back + 2 restarts exhausted.</summary>
    CountedFailuresExhausted,
    /// <summary>Non-transient precondition lost too often.</summary>
    PreconditionLostExhausted,
    /// <summary>Two early-stopped crafts.</summary>
    CraftStoppedEarlyTwice,
    /// <summary>Craft spot blocked.</summary>
    BlockedCraftSpot,
    /// <summary>Auto-collect changed during our step.</summary>
    AutoCollectChanged,
    /// <summary>Two unknown UIs opened in one job.</summary>
    UnknownUiTwice,
    /// <summary>Too many restored window incidents in an hour.</summary>
    TooManyWindowIncidents,
    /// <summary>Files differ from the trusted build.</summary>
    TamperedFiles,
    /// <summary>Internal error.</summary>
    InternalError,
}
