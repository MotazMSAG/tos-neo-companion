using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;
using Companion.GameIO.Native;
using Companion.GameIO.Safety;

namespace Companion.GameIO.Input;

/// <summary>
/// The slide watchdog (SPEC 7.4, WD-20). One instance per input section: from the start of focus until slide-back it
/// samples every ~1-2 ms (dedicated high-priority thread, high-resolution waitable timer) the cursor (return value
/// checked), the mouse buttons (VK 1, 2, 4), the window under the cursor and the cover rect, the foreground PID,
/// IsIconic, the window rect and IsZoomed, GetClipCursor / GetCursorInfo, the stop switch and the dead-man deadline.
/// Foreground mode (SPEC 7.3b) inverts the window checks: the game must stay the foreground window and the window under
/// the cursor, and the user's own input (GetLastInputInfo, mouse buttons) ends the section at any phase, because it
/// reaches the slid game (the cover that absorbs it in cover mode does not exist).
/// On a trigger it calls the abort callback ITSELF (it does not wait for the engine thread), so a blocked engine thread
/// can never leave the window displaced. In manual mode (tests) there is no thread: the test calls
/// <see cref="SampleOnce"/>.
/// </summary>
internal sealed class SlideWatchdog : IDisposable
{
    /// <summary>Whole-section deadline (focus to slide-back) after which the forced order runs even if never slid (a drag adds its extra dead-man time).</summary>
    public static readonly TimeSpan SectionDeadline = TimeSpan.FromSeconds(5);

    /// <summary>Movement within this long after DOWN counts as P4 (DOWN consumption, ~110 ms observed).</summary>
    public static readonly TimeSpan P4Window = TimeSpan.FromMilliseconds(150);

    /// <summary>How long <see cref="Stop"/> waits for an abort running on this thread: a foreground-mode abort may drain a frozen game that long (SPEC 7.3b).</summary>
    private static readonly TimeSpan JoinLimit = TimeSpan.FromSeconds(5) + HardRules.ForegroundDrainHangMax;

    private readonly IWin32 _win32;
    private readonly IClock _clock;
    private readonly IStopSwitch _stop;
    private readonly InputSession _s;
    private readonly Action<Trigger> _abort;
    private readonly bool _manual;
    private readonly object _statsLock = new();
    private readonly List<double> _periods = new(2048);
    private TimeSpan? _lastSample;
    private PointI _lastCursor;
    private Thread? _thread;
    private volatile bool _stopLoop;
    private int _sampling;
    private int _samples;

    public SlideWatchdog(IWin32 win32, IClock clock, IStopSwitch stop, InputSession session, Action<Trigger> abort, bool manual)
    {
        _win32 = win32;
        _clock = clock;
        _stop = stop;
        _s = session;
        _abort = abort;
        _manual = manual;
        _lastCursor = session.Cursor;
    }

    /// <summary>Starts the sampling thread (no-op in manual mode).</summary>
    public void Start()
    {
        if (_manual || _thread is not null)
        {
            return;
        }

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "GameIO.Watchdog",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
    }

    /// <summary>Stops sampling (joins the thread) and returns the sampling statistics.</summary>
    public WatchdogStats Stop()
    {
        _stopLoop = true;
        if (_thread is not null && Thread.CurrentThread != _thread)
        {
            _thread.Join(JoinLimit);
        }

        lock (_statsLock)
        {
            if (_periods.Count == 0)
            {
                return new WatchdogStats(_samples, 0, 0, 0);
            }

            var sorted = _periods.OrderBy(p => p).ToArray();
            return new WatchdogStats(
                _samples,
                Math.Round(sorted[sorted.Length / 2], 3),
                Math.Round(sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.99) - 1)], 3),
                Math.Round(sorted[^1], 3));
        }
    }

    /// <summary>One sample; triggers the abort callback on the first trigger. Re-entrant calls are ignored.</summary>
    public void SampleOnce()
    {
        if (Interlocked.Exchange(ref _sampling, 1) == 1)
        {
            return;
        }

        try
        {
            var now = _clock.Monotonic;
            lock (_statsLock)
            {
                if (_lastSample is { } last)
                {
                    _periods.Add((now - last).TotalMilliseconds);
                }

                _lastSample = now;
                _samples++;
            }

            if (_s.AbortClaimed || _s.Finished)
            {
                return;
            }

            var t = Evaluate(now);
            if (t != Trigger.None)
            {
                _abort(t);
            }
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }

    public void Dispose()
    {
        _stopLoop = true;
    }

    /// <summary>Evaluates the trigger table (highest priority first). Movement after DOWN is recorded, not triggered.</summary>
    internal Trigger Evaluate(TimeSpan now)
    {
        var fg = _win32.GetForegroundWindow();
        if (_s.Foreground)
        {
            // the user switched away (or a system window took over, fg 0): the game is unfocused now
            if (fg != _s.Game)
            {
                return Trigger.ForegroundLost;
            }
        }
        else if (fg != 0 && (fg == _s.Game || _win32.GetWindowProcessId(fg) == _s.GamePid))
        {
            return Trigger.Foreground;
        }

        if (_win32.IsIconic(_s.Game))
        {
            return Trigger.Iconic;
        }

        var clip = _win32.GetClipCursor();
        var showingOk = _win32.TryGetCursorShowing(out var showing);
        var clipChanged = _s.ClipBaseline is { } cb && clip is { } c && c != cb;
        var showingChanged = _s.CursorShowingBaseline is { } sb && showingOk && showing != sb;
        if (clipChanged)
        {
            // overwrite an earlier evidence-only "part=showing" so a clip halt journals the clip
            _s.ClipDetail = ClipDetailOf(clipChanged, showingChanged, _s.ClipBaseline, clip, _s.CursorShowingBaseline, showingOk ? showing : null);
            _s.ClipChangedSeen = true;
            return Trigger.ClipChanged;
        }

        if (showingChanged)
        {
            // Pointer hidden/shown with the clip unchanged: evidence only, never an abort. Other apps hide the pointer
            // (live 2026-10-15: "part=showing" 1->0, clip unchanged, while the user watched a video; typing does it too),
            // and the game hit-tests at the cursor position, which the movement checks below still guard.
            _s.ClipDetail ??= ClipDetailOf(false, true, _s.ClipBaseline, clip, _s.CursorShowingBaseline, showing);
        }

        // Foreground mode: a key, the wheel or a button reached the slid game. Checked before the window rect, so a click
        // on the slid caption ends the section with a slide-back instead of the restore ladder fighting the user's move.
        if (_s.Foreground && ForegroundUserInput() is var input && input != Trigger.None)
        {
            return input;
        }

        var rect = _win32.GetWindowRect(_s.Game);
        if (rect is null || !_s.IsAcceptable(rect.Value, _win32.IsZoomed(_s.Game)))
        {
            return Trigger.WindowChanged;
        }

        if ((_s.SlideStart is { } st && now - st >= _s.DeadMan) || now - _s.StartMono >= _s.SectionLimit)
        {
            return Trigger.DeadMan;
        }

        if (_stop.IsStopRequested)
        {
            return Trigger.Stop;
        }

        if (!_win32.TryGetCursorPos(out var p))
        {
            return Trigger.CursorLost;
        }

        var downPosted = _s.DownClaimed;
        var top = TopAt(p);
        var moved = p != _s.Cursor;
        if (_s.Foreground)
        {
            // the game must stay under the cursor: anything else (a toast, a topmost window, the cursor off the game)
            // would take the next real click
            if (top != _s.Game)
            {
                return Trigger.CoverChanged;
            }
        }
        else if (top == _s.Game)
        {
            return Trigger.GameUnderCursor;
        }
        else if (!moved && (top != _s.CoverHwnd || _win32.GetFrameBounds(_s.CoverHwnd) != _s.CoverFrame))
        {
            return Trigger.CoverChanged;
        }

        if (p != _lastCursor)
        {
            _lastCursor = p;
            if (!downPosted)
            {
                _s.MovedBeforeDown = true;
            }
            else if (_s.DownMono is { } dm && now - dm < P4Window)
            {
                _s.MovedInP4 = true;
            }
            else
            {
                _s.MovedAfterP4 = true;
            }
        }

        if (moved && !downPosted)
        {
            return Trigger.CursorMoved;
        }

        // A drag holds the button across many window moves: any movement before the release ends it (button released
        // first, then the slide back; SPEC 7.3a).
        if (moved && _s.IsDrag && !_s.UpClaimed)
        {
            return Trigger.CursorMoved;
        }

        if (moved && _s.FireOn == Contracts.Vision.FireOn.Up && !_s.UpClaimed)
        {
            return Trigger.MovedBeforeUp;
        }

        if (Guards.AnyMouseButtonDown(_win32))
        {
            _s.UserButtonSeen = true;
            if (_s.Foreground || !downPosted || (_s.IsDrag && !_s.UpClaimed))
            {
                return Trigger.UserButton;
            }
        }

        return Trigger.None;
    }

    /// <summary>
    /// Foreground mode: any change of GetLastInputInfo since readiness is the user's input reaching the game (our own window
    /// messages and moves are not input), recorded whatever it was, so a UI seen afterwards is not taken for ours. With the
    /// cursor where it was (a key, the wheel, a button) it ends the section now (<see cref="Trigger.UserInput"/>); a movement
    /// keeps its own rules in the table. A failed read counts as input.
    /// </summary>
    private Trigger ForegroundUserInput()
    {
        if (_s.InputTickBaseline is { } baseline && _win32.GetLastInputTick() is { } tick && tick == baseline)
        {
            return Trigger.None;
        }

        _s.UserInputSeen = true;
        return _win32.TryGetCursorPos(out var p) && p != _s.Cursor ? Trigger.None : Trigger.UserInput;
    }

    private long TopAt(PointI p)
    {
        var h = _win32.WindowFromPoint(p);
        return h == 0 ? 0 : _win32.GetAncestor(h, Win32Const.GA_ROOT);
    }

    private void Loop()
    {
        IPreciseWaiter? waiter = null;
        try
        {
            waiter = _win32.CreatePreciseWaiter();
            while (!_stopLoop)
            {
                try
                {
                    SampleOnce();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A failing sample must not kill the safety thread; the dead-man still applies via the abort.
                    try
                    {
                        if (!_s.AbortClaimed && !_s.Finished)
                        {
                            _abort(Trigger.DeadMan);
                        }
                    }
                    catch (Exception inner) when (inner is not OutOfMemoryException)
                    {
                        // Nothing more can be done on this thread; the engine thread's finally restores home.
                    }
                }

                waiter.Wait(TimeSpan.FromMilliseconds(1));
            }
        }
        finally
        {
            waiter?.Dispose();
        }
    }

    /// <summary>"part=clip|showing|both base=L,T,R,B seen=L,T,R,B showBase=.. showSeen=..": numbers only.</summary>
    internal static string ClipDetailOf(bool clipChanged, bool showingChanged, RectI? clipBase, RectI? clipSeen, bool? showBase, bool? showSeen)
    {
        static string R(RectI? r) => r is { } v ? $"{v.Left},{v.Top},{v.Right},{v.Bottom}" : "-";
        static string B(bool? b) => b is { } v ? (v ? "1" : "0") : "-";
        var part = clipChanged && showingChanged ? "both" : clipChanged ? "clip" : "showing";
        return $"part={part} base={R(clipBase)} seen={R(clipSeen)} showBase={B(showBase)} showSeen={B(showSeen)}";
    }
}
