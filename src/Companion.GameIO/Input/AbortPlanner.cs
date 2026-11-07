using Companion.Contracts.GameIo;

namespace Companion.GameIO.Input;

/// <summary>What ended (or threatens) an input section. The first one wins (one owner performs the abort).</summary>
internal enum Trigger
{
    /// <summary>No trigger.</summary>
    None,

    // ---- watchdog triggers (SPEC 7.4)
    /// <summary>Cursor moved before DOWN.</summary>
    CursorMoved,
    /// <summary>A mouse button went down before DOWN.</summary>
    UserButton,
    /// <summary>GetCursorPos failed (desktop switch / lock).</summary>
    CursorLost,
    /// <summary>The window under the (still) cursor changed, or its frame rect changed.</summary>
    CoverChanged,
    /// <summary>TopAt(cursor) is the game.</summary>
    GameUnderCursor,
    /// <summary>Kill switch.</summary>
    Stop,
    /// <summary>The game PID became really foreground.</summary>
    Foreground,
    /// <summary>The game was minimized.</summary>
    Iconic,
    /// <summary>Window rect / maximized flag changed, not by us.</summary>
    WindowChanged,
    /// <summary>ClipCursor rect changed while we held fake focus (F27); pointer visibility alone is evidence only.</summary>
    ClipChanged,
    /// <summary>Dead-man deadline (displaced 1.5 s, or the whole section too long).</summary>
    DeadMan,
    /// <summary>A SendMessageTimeout timed out (game hung).</summary>
    SendTimeout,
    /// <summary>FireOn.Up target: cursor moved after DOWN but before UP (forced order).</summary>
    MovedBeforeUp,
    /// <summary>Foreground mode: the game is no longer the foreground window (the user switched away).</summary>
    ForegroundLost,
    /// <summary>Foreground mode: keyboard, wheel or button input (GetLastInputInfo) with the cursor still: it reached the game.</summary>
    UserInput,

    // ---- main-thread aborts
    /// <summary>No RecoverFocus logged.</summary>
    FocusFailed,
    /// <summary>ERROR_ACCESS_DENIED (UIPI).</summary>
    IntegrityMismatch,
    /// <summary>Slide did not land on the slid rect.</summary>
    SlideDidNotLand,
    /// <summary>Slid capture failed or timed out.</summary>
    SlidCaptureFailed,
    /// <summary>Template not verified at the target in the slid capture.</summary>
    SlidTemplateMismatch,
    /// <summary>Template provisional / suspect / missing (refused by a hard rule).</summary>
    NotClickable,
    /// <summary>A popup or non-overlay UI was logged since the locating capture.</summary>
    LogGatePopup,
    /// <summary>Game identity changed.</summary>
    IdentityChanged,
    /// <summary>The caller cancelled.</summary>
    Cancelled,
    /// <summary>Unexpected exception (bug or fake failure).</summary>
    Exception,
    /// <summary>A runtime hard-rule assert refused an action of our own code.</summary>
    HardRule,
}

/// <summary>One step of an abort sequence.</summary>
internal enum AbortStep
{
    /// <summary>Post WM_LBUTTONUP (only if DOWN was posted and UP is pending).</summary>
    PostUp,
    /// <summary>Wait 20 ms so the UP is processed at the slid position (PAIR-17).</summary>
    Wait20,
    /// <summary>
    /// Foreground mode: a WM_NULL round trip (bounded by <c>HardRules.ForegroundDrainMax</c>) and one frame, so the game
    /// reads our queued button messages while the window is still slid.
    /// </summary>
    Drain,
    /// <summary>Slide back move-only: 0x0215 from the engine thread, 0x4215 from the watchdog thread.</summary>
    SlideBack,
    /// <summary>Slide back move-only, always asynchronous (0x4215).</summary>
    SlideBackAsync,
    /// <summary>Fake unfocus via SendMessageTimeout (skipped while the game PID is really foreground).</summary>
    Unfocus,
    /// <summary>Fake unfocus via SendNotifyMessage (nonqueued: a pending posted DOWN is ignored, c6).</summary>
    UnfocusNotify,
    /// <summary>Restore ladder (7.6).</summary>
    RestoreLadder,
}

/// <summary>
/// Pure abort orders (SPEC 7.4 table). Inputs: the trigger, whether DOWN was posted (the DOWN gate decides this
/// atomically), whether UP is still pending, whether a slide happened, whether focus messages were sent, and whether the
/// pending button must be released before anything else (a drag, SPEC 7.3a: every order then posts UP before the
/// window moves back).
/// </summary>
internal static class AbortPlanner
{
    /// <summary>Plans the ordered abort steps (<paramref name="foreground"/>: a foreground-mode section, SPEC 7.3b).</summary>
    public static IReadOnlyList<AbortStep> Plan(Trigger trigger, bool downPosted, bool upPending, bool slid, bool focusSent, bool releaseFirst = false, bool foreground = false)
    {
        if (foreground)
        {
            return PlanForeground(trigger, downPosted, upPending, slid, releaseFirst);
        }

        var steps = new List<AbortStep>();
        var up = downPosted && upPending;
        switch (trigger)
        {
            case Trigger.None:
                break;

            case Trigger.DeadMan:
            case Trigger.SendTimeout:
            case Trigger.MovedBeforeUp:
                // Forced order: nonqueued unfocus first (a pending DOWN is then ignored), UP if pending, async slide back.
                if (focusSent)
                {
                    steps.Add(AbortStep.UnfocusNotify);
                }

                if (up)
                {
                    steps.Add(AbortStep.PostUp);
                }

                if (slid)
                {
                    steps.Add(AbortStep.SlideBackAsync);
                }

                break;

            case Trigger.Foreground:
                // The user took over: slide back at once, UP if pending, never a fake unfocus. A drag releases its
                // held button first (one non-blocking post), then slides back.
                if (up && releaseFirst)
                {
                    steps.Add(AbortStep.PostUp);
                }

                if (slid)
                {
                    steps.Add(AbortStep.SlideBackAsync);
                }

                if (up && !releaseFirst)
                {
                    steps.Add(AbortStep.PostUp);
                }

                break;

            case Trigger.Iconic:
                // Minimized: no SetWindowPos, no unfocus; only a pending UP (restore later via RestoreHomeIfOurs).
                if (up)
                {
                    steps.Add(AbortStep.PostUp);
                }

                break;

            case Trigger.WindowChanged:
                if (up)
                {
                    steps.Add(AbortStep.PostUp);
                }

                if (focusSent)
                {
                    steps.Add(AbortStep.Unfocus);
                }

                steps.Add(AbortStep.RestoreLadder);
                break;

            case Trigger.ClipChanged:
                if (up)
                {
                    steps.Add(AbortStep.PostUp);
                }

                if (focusSent)
                {
                    steps.Add(AbortStep.Unfocus);
                }

                if (slid)
                {
                    steps.Add(AbortStep.SlideBack);
                }

                break;

            default:
                // Cursor moved / button / cover changed / game under cursor / stop / every main-thread abort.
                // Movement after DOWN never triggers (recorded only); it reaches this branch only in the race where
                // DOWN was claimed at the same instant, and then the conservative after-DOWN order applies.
                if (downPosted)
                {
                    if (up)
                    {
                        steps.Add(AbortStep.PostUp);
                        steps.Add(AbortStep.Wait20);
                    }

                    if (slid)
                    {
                        steps.Add(AbortStep.SlideBack);
                    }

                    if (focusSent)
                    {
                        steps.Add(AbortStep.Unfocus);
                    }
                }
                else
                {
                    if (slid)
                    {
                        steps.Add(AbortStep.SlideBack);
                    }

                    if (focusSent)
                    {
                        steps.Add(AbortStep.Unfocus);
                    }
                }

                break;
        }

        return steps;
    }

    /// <summary>
    /// Foreground-mode orders (SPEC 7.3b, 7.4): never a focus message (the game has real focus, R2). There is no fake unfocus
    /// to make a button message the game has not read yet harmless, and one read in a frame after the slide-back hit-tests
    /// at home (EVIDENCE c6). So after DOWN, whatever the trigger, the pending UP is posted, the game is drained (two WM_NULL
    /// round trips and a frame), and only then does the window move home; also when the user switched away (the game may
    /// read our DOWN before the deactivation, safety review 2026-11-02). Before DOWN the window goes home at once. A
    /// minimized game is not moved at all.
    /// </summary>
    private static IReadOnlyList<AbortStep> PlanForeground(Trigger trigger, bool downPosted, bool upPending, bool slid, bool releaseFirst)
    {
        var steps = new List<AbortStep>();
        var up = downPosted && upPending;
        switch (trigger)
        {
            case Trigger.None:
                break;

            case Trigger.ForegroundLost:
            case Trigger.Foreground:
                if (up)
                {
                    steps.Add(AbortStep.PostUp);
                }

                if (downPosted)
                {
                    steps.Add(AbortStep.Drain);
                }

                if (slid)
                {
                    steps.Add(AbortStep.SlideBackAsync);
                }

                break;

            case Trigger.Iconic:
                if (up)
                {
                    steps.Add(AbortStep.PostUp);
                }

                break;

            case Trigger.WindowChanged:
                if (up)
                {
                    steps.Add(AbortStep.PostUp);
                }

                if (downPosted)
                {
                    steps.Add(AbortStep.Drain);
                }

                steps.Add(AbortStep.RestoreLadder);
                break;

            default:
                // The user's input, cursor moved, cover changed, dead-man, stop and every main-thread abort.
                if (up)
                {
                    steps.Add(AbortStep.PostUp);
                }

                if (downPosted)
                {
                    steps.Add(AbortStep.Drain);
                }

                if (slid)
                {
                    steps.Add(trigger is Trigger.DeadMan or Trigger.SendTimeout or Trigger.MovedBeforeUp ? AbortStep.SlideBackAsync : AbortStep.SlideBack);
                }

                break;
        }

        return steps;
    }

    /// <summary>Detailed abort reason for a trigger.</summary>
    public static AbortReason ReasonOf(Trigger t) => t switch
    {
        Trigger.None => AbortReason.None,
        Trigger.CursorMoved or Trigger.MovedBeforeUp => AbortReason.CursorMoved,
        Trigger.UserButton => AbortReason.UserButton,
        Trigger.CursorLost => AbortReason.NotReady,
        Trigger.CoverChanged => AbortReason.CoverChanged,
        Trigger.GameUnderCursor => AbortReason.GameUnderCursor,
        Trigger.Stop or Trigger.Cancelled => AbortReason.StopRequested,
        Trigger.Foreground => AbortReason.GameForeground,
        Trigger.Iconic => AbortReason.GameIconic,
        Trigger.WindowChanged => AbortReason.WindowChanged,
        Trigger.ClipChanged => AbortReason.CursorClipChanged,
        Trigger.DeadMan => AbortReason.DeadMan,
        Trigger.SendTimeout => AbortReason.SendTimeout,
        Trigger.FocusFailed => AbortReason.None,
        Trigger.IntegrityMismatch => AbortReason.None,
        Trigger.SlideDidNotLand => AbortReason.SlideDidNotLand,
        Trigger.SlidCaptureFailed => AbortReason.SlidCaptureFailed,
        Trigger.SlidTemplateMismatch or Trigger.NotClickable => AbortReason.SlidTemplateMismatch,
        Trigger.LogGatePopup => AbortReason.LogGatePopup,
        Trigger.IdentityChanged => AbortReason.IdentityChanged,
        Trigger.ForegroundLost => AbortReason.ForegroundLost,
        Trigger.UserInput => AbortReason.UserInput,
        _ => AbortReason.None,
    };

    /// <summary>
    /// Disposition of a click (or key) section that ended with a trigger. <paramref name="downPosted"/>: the input
    /// itself was delivered; a forced or urgent end after DOWN is reported as Sent (the engine classifies it Uncertain
    /// from <c>ForcedSlideBack</c> / cursor evidence). <paramref name="touched"/>: the game was touched before DOWN (focus
    /// messages sent, or in foreground mode the window slid).
    /// </summary>
    public static InputDisposition DispositionOf(Trigger t, bool downPosted, bool touched) => t switch
    {
        Trigger.None => downPosted ? InputDisposition.Sent : (touched ? InputDisposition.AbortedAfterFocus : InputDisposition.NotSent),
        Trigger.Stop or Trigger.Cancelled => InputDisposition.Stopped,
        Trigger.Foreground or Trigger.Iconic or Trigger.ForegroundLost => InputDisposition.UserTookOver,
        Trigger.WindowChanged or Trigger.ClipChanged => InputDisposition.Incident,
        Trigger.FocusFailed => InputDisposition.FocusFailed,
        Trigger.IntegrityMismatch => InputDisposition.IntegrityMismatch,
        Trigger.NotClickable or Trigger.HardRule => InputDisposition.Refused,
        Trigger.IdentityChanged => InputDisposition.GameGone,
        _ => downPosted ? InputDisposition.Sent : (touched ? InputDisposition.AbortedAfterFocus : InputDisposition.NotSent),
    };
}
