using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Logs;
using Companion.Contracts.Vision;

namespace Companion.GameIO.Input;

/// <summary>Where the game window is expected to be (the watchdog flags any other rect as WindowChanged).</summary>
internal enum WindowExpect
{
    /// <summary>At home.</summary>
    Home,
    /// <summary>Our slide call is in flight: home or slid.</summary>
    HomeOrSlid,
    /// <summary>Slid.</summary>
    Slid,
    /// <summary>Our slide-back call is in flight: slid (the current or, during a drag step, the previous rect) or home.</summary>
    SlidOrHome,
    /// <summary>A drag step's window move is in flight: the previous or the new slid rect.</summary>
    Stepping,
    /// <summary>The restore ladder owns the window: not checked.</summary>
    Any,
}

/// <summary>Where the slid window and the pointer are now: a drag moves both step by step; a click keeps the first.</summary>
/// <param name="Slid">Slid window rect now.</param>
/// <param name="Previous">The rect before the step in flight (drag), or null.</param>
/// <param name="Target">Client point under the resting cursor now (the point the release is posted at).</param>
internal sealed record SlidSpot(RectI Slid, RectI? Previous, PointI Target);

/// <summary>
/// Shared state of ONE input section between the engine thread and the watchdog thread. Exactly-once claims use
/// <see cref="Interlocked.CompareExchange(ref int, int, int)"/>: the abort (first trigger wins), the DOWN gate (main
/// claims it, an abort closes it; whoever is first decides whether DOWN is ever posted), UP, and the slide-back.
/// Message sends of the engine thread and the abort's unfocus are serialized by <see cref="MsgGate"/>.
/// </summary>
internal sealed class InputSession
{
    private readonly object _ev = new();
    private int _expect;
    private long _slideStartTicks = -1;
    private int _slide;
    private int _down;
    private int _downDone;
    private int _up;
    private int _slideBack;
    private int _abort;
    private int _trigger;
    private int _focusSent;
    private int _phase;
    private SlidSpot _spot;

    public InputSession(
        long game,
        int gamePid,
        PointI cursor,
        long coverHwnd,
        RectI coverFrame,
        RectI home,
        bool homeZoomed,
        RectI slid,
        RectI? clipBaseline,
        bool? cursorShowingBaseline,
        FireOn fireOn,
        PointI target,
        TimeSpan startMono,
        bool isDrag = false,
        TimeSpan? deadMan = null,
        InputMode mode = InputMode.Cover,
        uint? inputTickBaseline = null)
    {
        Game = game;
        GamePid = gamePid;
        Cursor = cursor;
        CoverHwnd = coverHwnd;
        CoverFrame = coverFrame;
        Home = home;
        HomeZoomed = homeZoomed;
        Slid = slid;
        ClipBaseline = clipBaseline;
        CursorShowingBaseline = cursorShowingBaseline;
        FireOn = fireOn;
        Target = target;
        StartMono = startMono;
        IsDrag = isDrag;
        DeadMan = deadMan ?? Contracts.Safety.HardRules.DeadManDeadline;
        Mode = mode;
        InputTickBaseline = inputTickBaseline;
        _spot = new SlidSpot(slid, null, target);
    }

    /// <summary>The input mode, fixed at readiness for the whole section (SPEC 7.3b).</summary>
    public InputMode Mode { get; }

    /// <summary>Foreground mode: the game has real focus, no focus message is sent, the cover is the game itself.</summary>
    public bool Foreground => Mode == InputMode.Foreground;

    /// <summary>Foreground mode: GetLastInputInfo dwTime read at readiness (any later change is the user's input).</summary>
    public uint? InputTickBaseline { get; }

    public long Game { get; }

    public int GamePid { get; }

    /// <summary>Resting cursor (screen) at readiness.</summary>
    public PointI Cursor { get; }

    public long CoverHwnd { get; }

    public RectI CoverFrame { get; }

    public RectI Home { get; }

    public bool HomeZoomed { get; }

    public RectI Slid { get; }

    public RectI? ClipBaseline { get; }

    public bool? CursorShowingBaseline { get; }

    public FireOn FireOn { get; }

    /// <summary>Target client point (a drag's press point).</summary>
    public PointI Target { get; }

    public TimeSpan StartMono { get; }

    /// <summary>A slide drag (SPEC 7.3a): movement or a user button before the release ends it, button released first.</summary>
    public bool IsDrag { get; }

    /// <summary>Displacement dead-man of this section (a drag adds its proven timeline, <c>HardRules.DragDeadManDeadline</c>).</summary>
    public TimeSpan DeadMan { get; }

    /// <summary>Whole-section deadline (focus to slide-back): the base limit plus a drag's extra dead-man time.</summary>
    public TimeSpan SectionLimit => SlideWatchdog.SectionDeadline + (DeadMan - Contracts.Safety.HardRules.DeadManDeadline);

    /// <summary>Current slid rect and pointer target (see <see cref="SlidSpot"/>).</summary>
    public SlidSpot Spot => Volatile.Read(ref _spot);

    /// <summary>Client point under the resting cursor now (release point).</summary>
    public PointI CurrentTarget => Spot.Target;

    /// <summary>Drag: the next step's window move is about to be issued (the watchdog accepts the old or the new rect).</summary>
    public void BeginStep(RectI slid, PointI target)
    {
        // Stepping first: whichever spot the watchdog reads next, the rect the window is at now stays acceptable.
        Expect = WindowExpect.Stepping;
        var current = Spot;
        Volatile.Write(ref _spot, new SlidSpot(slid, current.Slid, target));
    }

    /// <summary>Drag: the step landed (verified); only the new rect is acceptable again.</summary>
    public void EndStep() => Expect = WindowExpect.Slid;

    /// <summary>Serializes the engine thread's message sends with the abort's unfocus.</summary>
    public object MsgGate { get; } = new();

    /// <summary>Set by an abort: the engine thread sends nothing more.</summary>
    public volatile bool MessagesClosed;

    /// <summary>The engine thread finished normally (slid back): the watchdog stops triggering.</summary>
    public volatile bool Finished;

    /// <summary>The engine thread's slide SetWindowPos returned.</summary>
    public volatile bool SlideCallReturned;

    /// <summary>Fake unfocus sent (or deliberately skipped).</summary>
    public volatile bool UnfocusDone;

    /// <summary>Fake unfocus skipped because the game PID was really foreground.</summary>
    public volatile bool UnfocusSkipped;

    /// <summary>Unfocus sent while still slid.</summary>
    public volatile bool UnfocusSentWhileSlid;

    /// <summary>Slide-back performed by the watchdog thread.</summary>
    public volatile bool ForcedSlideBack;

    /// <summary>The engine thread completed P7 normally.</summary>
    public volatile bool CompletedNormally;

    // ---- cursor evidence
    public volatile bool MovedBeforeDown;
    public volatile bool MovedInP4;
    public volatile bool MovedAfterP4;
    public volatile bool UserButtonSeen;

    /// <summary>Foreground mode: keyboard or mouse input (GetLastInputInfo) during the section: it reached the game.</summary>
    public volatile bool UserInputSeen;

    /// <summary>Foreground mode: the drain (two WM_NULL round trips) succeeded after the last button message was posted.</summary>
    public volatile bool Drained;

    /// <summary>Foreground mode: the abort's drain waited the longest allowed for a game that stopped responding, and gave up.</summary>
    public volatile bool DrainGaveUp;

    /// <summary>Foreground mode: the drain found the game window gone (its queued messages died with it): nothing to move.</summary>
    public volatile bool DrainGone;

    /// <summary>
    /// Foreground mode: our DOWN (and UP) may still be unread by the game, so the window must not move yet (a button read
    /// with the window home hit-tests at home, EVIDENCE c6): DOWN was claimed and no drain has succeeded or given up.
    /// </summary>
    public bool MoveBlocked => Foreground && DownClaimed && !Drained && !DrainGaveUp && !DrainGone;
    public volatile bool ClipChangedSeen;

    /// <summary>
    /// What the clip check saw when it fired (numbers only, journaled as "clip"): which part changed and the baseline vs.
    /// seen values. The live CursorClipChanged halt of 2026-10-14 recorded only a boolean, so its cause stayed unknown.
    /// </summary>
    public volatile string? ClipDetail;

    public WindowExpect Expect
    {
        get => (WindowExpect)Volatile.Read(ref _expect);
        set => Volatile.Write(ref _expect, (int)value);
    }

    /// <summary>Monotonic time the slide call started, or null.</summary>
    public TimeSpan? SlideStart
    {
        get
        {
            var t = Interlocked.Read(ref _slideStartTicks);
            return t < 0 ? null : TimeSpan.FromTicks(t);
        }
    }

    public InputPhase Phase
    {
        get => (InputPhase)Volatile.Read(ref _phase);
        set => Volatile.Write(ref _phase, (int)value);
    }

    public Trigger Trigger => (Trigger)Volatile.Read(ref _trigger);

    public bool AbortClaimed => Volatile.Read(ref _abort) != 0;

    /// <summary>The engine thread claimed the slide (the SetWindowPos is or will be called).</summary>
    public bool SlideAttempted => Volatile.Read(ref _slide) == 1;

    /// <summary>DOWN was claimed by the engine thread (it is or will be posted).</summary>
    public bool DownClaimed => Volatile.Read(ref _down) == 1;

    /// <summary>The DOWN PostMessage returned.</summary>
    public bool DownDone => Volatile.Read(ref _downDone) != 0;

    public bool UpClaimed => Volatile.Read(ref _up) != 0;

    public bool SlideBackClaimed => Volatile.Read(ref _slideBack) != 0;

    public int FocusMessagesSent => Volatile.Read(ref _focusSent);

    // ---- milestones (written by either thread)
    public TimeSpan? DownMono { get; private set; }

    public DateTimeOffset? ActionPostedUtc { get; private set; }

    public DateTimeOffset? ReleasePostedUtc { get; private set; }

    public DateTimeOffset? UnfocusSentUtc { get; private set; }

    public DateTimeOffset? SlideBackUtc { get; private set; }

    public TimeSpan SlideCall { get; private set; }

    public TimeSpan SlideBackCall { get; private set; }

    public TimeSpan? SlideBackMono { get; private set; }

    public RestoreReport? AbortRestore { get; private set; }

    public IReadOnlyList<AbortStep> AbortPlan { get; private set; } = [];

    /// <summary>Claims the abort for <paramref name="trigger"/>; false if another owner already claimed it.</summary>
    public bool TryClaimAbort(Trigger trigger)
    {
        if (Interlocked.CompareExchange(ref _abort, 1, 0) != 0)
        {
            return false;
        }

        Volatile.Write(ref _trigger, (int)trigger);
        return true;
    }

    /// <summary>Engine thread: claims the slide gate (fails once an abort closed it).</summary>
    public bool TryClaimSlide() => Interlocked.CompareExchange(ref _slide, 1, 0) == 0;

    /// <summary>Abort: closes the slide gate. Returns true if the slide had already been claimed.</summary>
    public bool CloseSlideGate() => Interlocked.CompareExchange(ref _slide, 2, 0) == 1;

    /// <summary>Engine thread: claims the DOWN gate (fails once an abort closed it).</summary>
    public bool TryClaimDown() => Interlocked.CompareExchange(ref _down, 1, 0) == 0;

    /// <summary>Abort: closes the DOWN gate. Returns true if DOWN had already been claimed (after-DOWN order).</summary>
    public bool CloseDownGate() => Interlocked.CompareExchange(ref _down, 2, 0) == 1;

    public void MarkDownDone(TimeSpan mono, DateTimeOffset utc)
    {
        lock (_ev)
        {
            DownMono = mono;
            ActionPostedUtc = utc;
        }

        Volatile.Write(ref _downDone, 1);
    }

    /// <summary>Waits (bounded spin) until the claimed DOWN post returned, so a slide-back never overtakes it.</summary>
    public void WaitDownDone(TimeSpan max)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var spin = new SpinWait();
        while (!DownDone && sw.Elapsed < max)
        {
            spin.SpinOnce();
        }
    }

    public bool TryClaimUp() => Interlocked.CompareExchange(ref _up, 1, 0) == 0;

    public void MarkUpPosted(DateTimeOffset utc)
    {
        lock (_ev)
        {
            ReleasePostedUtc = utc;
        }
    }

    public bool TryClaimSlideBack() => Interlocked.CompareExchange(ref _slideBack, 1, 0) == 0;

    public void IncFocusSent() => Interlocked.Increment(ref _focusSent);

    public void MarkSlideStart(TimeSpan mono) => Interlocked.Exchange(ref _slideStartTicks, mono.Ticks);

    public void MarkSlideCall(TimeSpan duration)
    {
        lock (_ev)
        {
            SlideCall = duration;
        }
    }

    public void MarkSlideBack(TimeSpan duration, TimeSpan mono, DateTimeOffset utc)
    {
        lock (_ev)
        {
            SlideBackCall = duration;
            SlideBackMono = mono;
            SlideBackUtc = utc;
        }
    }

    public void MarkUnfocus(DateTimeOffset utc)
    {
        lock (_ev)
        {
            UnfocusSentUtc ??= utc;
        }
    }

    public void SetAbortPlan(IReadOnlyList<AbortStep> plan)
    {
        lock (_ev)
        {
            AbortPlan = plan;
        }
    }

    public void SetAbortRestore(RestoreReport report)
    {
        lock (_ev)
        {
            AbortRestore = report;
        }
    }

    /// <summary>True if the rect/zoom is acceptable for the current expectation.</summary>
    public bool IsAcceptable(RectI rect, bool zoomed)
    {
        var expect = Expect;
        if (expect == WindowExpect.Any)
        {
            return true;
        }

        if (zoomed != HomeZoomed || rect.Width != Home.Width || rect.Height != Home.Height)
        {
            return false;
        }

        var spot = Spot;
        var previous = spot.Previous is { } p && rect == p;
        return expect switch
        {
            WindowExpect.Home => rect == Home,
            WindowExpect.Slid => rect == spot.Slid,
            WindowExpect.Stepping => rect == spot.Slid || previous,
            _ => rect == Home || rect == spot.Slid || previous,
        };
    }
}

/// <summary>Log positions captured along an input (InputMarks builder).</summary>
internal sealed class MarkSet
{
    public LogPosition? FocusSent { get; set; }

    public DateTimeOffset? FocusSentUtc { get; set; }

    public LogPosition? ActionPosted { get; set; }

    public LogPosition? UnfocusSent { get; set; }

    public LogPosition? End { get; set; }
}
