using System.Runtime.CompilerServices;
using System.Text;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.GameIO.Geometry;
using Companion.GameIO.Input;
using Companion.GameIO.Native;
using Companion.GameIO.Safety;

namespace Companion.GameIO;

/// <summary>Internal construction options (tests drive the sampler and the watchdog by hand).</summary>
/// <param name="StartSampler">Start the passive 250 ms sampler timer on Connect.</param>
/// <param name="ManualWatchdog">No watchdog thread: tests call <see cref="SlideWatchdog.SampleOnce"/>.</param>
/// <param name="ForegroundSlide">The user's foreground-mode switch (config input.foregroundSlide, SPEC 7.3b), read at every
/// readiness check; null or false keeps the game-in-front wait.</param>
/// <param name="ForegroundDrag">The user's foreground-drag switch (config input.foregroundDrag): drags in foreground mode.</param>
internal sealed record GameIoOptions(bool StartSampler, bool ManualWatchdog, Func<bool>? ForegroundSlide = null, Func<bool>? ForegroundDrag = null)
{
    public static GameIoOptions Production { get; } = new(true, false);
}

/// <summary>
/// The only path to the game window (unit "core-io", SPEC 1, 7). Every message, SetWindowPos and PrintWindow goes
/// through one runtime-asserting choke point; the safety gate (readiness, cover, monitors, exposure, home, pacing,
/// stop switch) runs inside <see cref="PressKey"/> and <see cref="SlideClick"/> and cannot be injected (PITFALL-18).
/// The constructor only stores its dependencies; nothing touches the game before <see cref="Connect"/>, and inputs
/// only happen in <see cref="PressKey"/>, <see cref="SlideClick"/> and the restore paths. Calls are serialized.
/// </summary>
public sealed partial class GameIo : IGameIo
{
    private static readonly TimeSpan SamplerPeriod = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PollSlice = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan LogPollSlice = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan VerifyWindow = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan WmSizeWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OurFocusSlackBefore = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan OurFocusSlackAfter = TimeSpan.FromSeconds(2);

    /// <summary>Value stored for each frame in <see cref="_ownCaptures"/> (only the key matters).</summary>
    private static readonly object CaptureMark = new();

    private readonly IClock _clock;
    private readonly IStopSwitch _stop;
    private readonly ILogTracker _log;
    private readonly IUiClassifier _ui;
    private readonly IVision _vision;
    private readonly ITemplateCatalog _templates;
    private readonly IJournal _journal;
    private readonly CompanionPaths _paths;
    private readonly IWin32 _win32;
    private readonly GameIoOptions _options;
    private readonly LogWatch _logWatch;
    private readonly PacingBucket _pacing;
    private readonly SlideMarkerStore _marker;

    /// <summary>
    /// The frames <see cref="Capture"/> returned (reference identity, without keeping them alive): the off-screen anchor
    /// patch rule accepts only a located frame GameIO captured itself at home (SPEC 7.3).
    /// </summary>
    private readonly ConditionalWeakTable<Frame, object> _ownCaptures = new();

    /// <summary>Serializes every acting call (one input at a time).</summary>
    private readonly object _io = new();

    /// <summary>Guards identity, baselines, trackers and attribution state.</summary>
    private readonly object _state = new();

    private readonly List<(DateTime Start, DateTime? End)> _ourInputs = [];

    private GameIdentity? _identity;
    private ChokePoint? _choke;

    /// <summary>Since when the game's own window has been the foreground window without a break (null: not foreground).</summary>
    private TimeSpan? _foregroundSinceMono;

    /// <summary>When the user could last have used the game (see <see cref="TrackGameUse"/>).</summary>
    private TimeSpan _lastGameUseMono;

    private bool _wasGameForeground;
    private uint? _lastInputTickSeen;
    private GameWindowPrefs? _prefsBaseline;
    private long _keyboardLayoutBaseline;
    private Timer? _samplerTimer;
    private TimeSpan? _lastForegroundMono;
    private PointI? _trackCursor;
    private TimeSpan _trackCursorSince;
    private (long Hwnd, RectI? Frame)? _trackCover;
    private TimeSpan _trackCoverSince;
    private (RectI Rect, bool Zoomed)? _trackHome;
    private TimeSpan _trackHomeSince;
    private LogPosition? _focusScanFrom;
    private DateTime? _lastForeignFocusLocal;
    private OurDeviation? _ourDeviation;
    private SlideWatchdog? _activeWatchdog;

    /// <summary>Creates the GameIO unit. No side effects: no window lookups, threads, files, timers or registry access.</summary>
    /// <param name="clock">Clock (monotonic for every timeout).</param>
    /// <param name="stop">Kill switch.</param>
    /// <param name="log">Log tracker (RecoverFocus, expected UI, LostFocuse, popup and jank gates).</param>
    /// <param name="ui">UI classifier (overlay vs stack for the popup gate).</param>
    /// <param name="vision">Vision (slid capture verification, frame validity).</param>
    /// <param name="templates">Template catalog (GameIO's own check of the cross-monitor NavFixed rule and the off-screen
    /// anchor patch rule, SPEC 7.3).</param>
    /// <param name="journal">Journal (written only after the slide-back).</param>
    /// <param name="paths">Paths (slide marker, game exe).</param>
    /// <param name="foregroundSlide">The user's foreground-mode switch (SPEC 7.3b), read at every readiness check; null = off.</param>
    /// <param name="foregroundDrag">The user's switch for drags in foreground mode; null = off.</param>
    public GameIo(
        IClock clock, IStopSwitch stop, ILogTracker log, IUiClassifier ui, IVision vision, ITemplateCatalog templates, IJournal journal, CompanionPaths paths,
        Func<bool>? foregroundSlide = null, Func<bool>? foregroundDrag = null)
        : this(clock, stop, log, ui, vision, templates, journal, paths, new Win32Api(),
            GameIoOptions.Production with { ForegroundSlide = foregroundSlide, ForegroundDrag = foregroundDrag })
    {
    }

    internal GameIo(
        IClock clock,
        IStopSwitch stop,
        ILogTracker log,
        IUiClassifier ui,
        IVision vision,
        ITemplateCatalog templates,
        IJournal journal,
        CompanionPaths paths,
        IWin32 win32,
        GameIoOptions options)
    {
        _clock = clock;
        _stop = stop;
        _log = log;
        _ui = ui;
        _vision = vision;
        _templates = templates;
        _journal = journal;
        _paths = paths;
        _win32 = win32;
        _options = options;
        _logWatch = new LogWatch(log);
        _pacing = new PacingBucket(clock);
        _marker = new SlideMarkerStore(paths.RuntimeDir, paths.RunDir, paths.SlideMarkerPath);
    }

    /// <inheritdoc />
    public GameIdentity? Identity
    {
        get
        {
            lock (_state)
            {
                return _identity;
            }
        }
    }

    /// <inheritdoc />
    public PacingStatus Pacing => _pacing.Status();

    /// <inheritdoc />
    public bool GameWindowPresent() =>
        (Identity is { } id && ValidateIdentity(id)) || GameWindowFinder.Find(_win32, _paths.GameExePath).Problem == ConnectProblem.None;

    /// <summary>Test hook: the watchdog of the input in progress (manual mode).</summary>
    internal SlideWatchdog? ActiveWatchdog => Volatile.Read(ref _activeWatchdog);

    /// <inheritdoc />
    public ConnectResult Connect(CancellationToken ct)
    {
        lock (_io)
        {
            ct.ThrowIfCancellationRequested();
            if (!_win32.Is64BitProcess)
            {
                return ConnectFail(ConnectProblem.Not64Bit);
            }

            if (!_win32.IsCurrentProcessElevated())
            {
                return ConnectFail(ConnectProblem.NotElevated);
            }

            var found = GameWindowFinder.Find(_win32, _paths.GameExePath);
            if (found.Problem != ConnectProblem.None || found.ExePath is not { } exe)
            {
                return ConnectFail(found.Problem == ConnectProblem.None ? ConnectProblem.ExePathMismatch : found.Problem);
            }

            var hwnd = found.Hwnd;
            var pid = found.Pid;
            var start = _win32.GetProcessStartUtc(pid);
            if (start is null)
            {
                return ConnectFail(ConnectProblem.NotRunning);
            }

            var identity = new GameIdentity(hwnd, pid, start.Value, exe);
            var choke = new ChokePoint(_win32, hwnd, pid, HomeRules.ExpectedClient);
            if (!choke.Post(hwnd, HardRules.WM_NULL, 0, 0, out var err) && err == Win32Const.ERROR_ACCESS_DENIED)
            {
                Journal(JournalKind.Log, null, ("event", "connect"), ("problem", nameof(ConnectProblem.IntegrityMismatch)));
                return ConnectFail(ConnectProblem.IntegrityMismatch);
            }

            var prefs = GamePrefsReader.Read(_win32);
            var build = Fingerprint();
            var layout = _win32.GetKeyboardLayout(_win32.GetWindowThreadId(hwnd));
            var now = _clock.Monotonic;
            lock (_state)
            {
                _identity = identity;
                _choke = choke;
                _prefsBaseline = prefs;
                _keyboardLayoutBaseline = layout;
                _trackCursor = null;
                _trackCover = null;
                _trackHome = null;
                InitForegroundHistory(now);
                _focusScanFrom = _log.Position;
            }

            SamplerTick();
            if (_options.StartSampler)
            {
                _samplerTimer ??= new Timer(_ => SafeSamplerTick(), null, SamplerPeriod, SamplerPeriod);
            }

            Journal(JournalKind.Log, null, ("event", "connect"), ("problem", nameof(ConnectProblem.None)), ("build", build?.Code), ("fullscreenMode", prefs.FullscreenMode));
            return new ConnectResult(ConnectProblem.None, identity, prefs, build);
        }
    }

    /// <inheritdoc />
    public TimeSpan? SinceGameForeground
    {
        get
        {
            if (Identity is null)
            {
                return null;
            }

            lock (_state)
            {
                return _lastForegroundMono is { } lf ? _clock.Monotonic - lf : null;
            }
        }
    }

    /// <inheritdoc />
    public GameWindowState GetState()
    {
        var id = Identity;
        var monitors = _win32.EnumMonitors();
        var clip = _win32.GetClipCursor() ?? default;
        var showing = _win32.TryGetCursorShowing(out var sh) && sh;
        if (id is null || !ValidateIdentity(id))
        {
            return new GameWindowState(false, id, default, default, default, false, false, false, false, false, false, null, monitors, 0, 0, clip, showing);
        }

        var h = id.Hwnd;
        var rect = _win32.GetWindowRect(h) ?? default;
        var origin = _win32.GetClientOrigin(h) ?? default;
        var client = _win32.GetClientSize(h) ?? default;
        var monitorName = _win32.MonitorFromWindow(h);
        var monitor = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, monitorName, StringComparison.Ordinal));
        return new GameWindowState(
            true,
            id,
            rect,
            origin,
            client,
            _win32.IsZoomed(h),
            _win32.IsIconic(h),
            IsPidForeground(id.Pid, h),
            _win32.IsHungAppWindow(h),
            _win32.IsCloaked(h),
            (_win32.GetStyle(h) & Win32Const.WS_CAPTION) == Win32Const.WS_CAPTION,
            monitor,
            monitors,
            CountExtraGameWindows(id, _win32.EnumTopLevelWindows()),
            _win32.GetKeyboardLayout(_win32.GetWindowThreadId(h)),
            clip,
            showing);
    }

    /// <inheritdoc />
    public GameWindowPrefs ReadWindowPrefs() => GamePrefsReader.Read(_win32);

    /// <inheritdoc />
    public ResponsivenessReport Probe()
    {
        lock (_io)
        {
            var (id, choke) = Bound();
            if (id is null || choke is null || !ValidateIdentity(id))
            {
                return new ResponsivenessReport(false, false, TimeSpan.Zero, false, 0);
            }

            return ProbeCore(choke);
        }
    }

    // ------------------------------------------------------------------ helpers shared by the partial files

    private ResponsivenessReport ProbeCore(ChokePoint choke)
    {
        var hung = _win32.IsHungAppWindow(choke.Game);
        var t0 = _clock.Monotonic;
        var sent = choke.Send(choke.Game, HardRules.WM_NULL, 0, 0);
        var rt = _clock.Monotonic - t0;
        var posted = choke.Post(choke.Game, HardRules.WM_NULL, 0, 0, out var err);
        var lastError = sent == SendOutcome.AccessDenied ? Win32Const.ERROR_ACCESS_DENIED : (posted ? 0 : err);
        var responsive = !hung && sent == SendOutcome.Ok && rt <= HardRules.ResponsivenessProbeMax;
        return new ResponsivenessReport(responsive, hung, rt, posted, lastError);
    }

    private (GameIdentity? Id, ChokePoint? Choke) Bound()
    {
        lock (_state)
        {
            return (_identity, _choke);
        }
    }

    private ConnectResult ConnectFail(ConnectProblem problem)
    {
        Journal(JournalKind.Log, null, ("event", "connect"), ("problem", problem.ToString()));
        return new ConnectResult(problem, null, null, null);
    }

    private GameBuildFingerprint? Fingerprint()
    {
        var exe = _win32.GetFileMetadata(_paths.GameExePath);
        if (exe is null)
        {
            return null;
        }

        var asm = _win32.GetFileMetadata(Path.Combine(_paths.GameDir, "GameAssembly.dll"));
        return new GameBuildFingerprint(exe.Value.Size, exe.Value.WriteUtc, asm?.Size, asm?.WriteUtc);
    }

    /// <summary>IsWindow, same PID, UnityWndClass, same process start time.</summary>
    private bool ValidateIdentity(GameIdentity id)
    {
        if (!_win32.IsWindow(id.Hwnd) || _win32.GetWindowProcessId(id.Hwnd) != id.Pid)
        {
            return false;
        }

        if (!string.Equals(_win32.GetClassName(id.Hwnd), HardRules.GameWindowClass, StringComparison.Ordinal))
        {
            return false;
        }

        var start = _win32.GetProcessStartUtc(id.Pid);
        return start is { } s && Math.Abs((s - id.ProcessStartUtc).TotalMilliseconds) < 1;
    }

    /// <summary>True if the process of the identity is gone (vs. identity changed).</summary>
    private bool ProcessGone(GameIdentity id) => _win32.GetProcessStartUtc(id.Pid) is null || !_win32.IsWindow(id.Hwnd);

    private bool IsPidForeground(int pid, long hwnd)
    {
        var fg = _win32.GetForegroundWindow();
        return fg != 0 && (fg == hwnd || _win32.GetWindowProcessId(fg) == pid);
    }

    private int CountExtraGameWindows(GameIdentity id, IReadOnlyList<long> zorder) =>
        zorder.Count(h => h != id.Hwnd
            && _win32.GetWindowProcessId(h) == id.Pid
            && _win32.IsWindowVisible(h)
            && !_win32.IsCloaked(h)
            && _win32.GetWindowRect(h) is { IsEmpty: false });

    private long TopAt(PointI p)
    {
        var h = _win32.WindowFromPoint(p);
        return h == 0 ? 0 : _win32.GetAncestor(h, Win32Const.GA_ROOT);
    }

    /// <summary>Cover above the game by walking GW_HWNDPREV up from the game (proven c3), capped at 2048 steps.</summary>
    private bool IsAboveByWalk(long cover, long game)
    {
        var h = _win32.GetWindow(game, Win32Const.GW_HWNDPREV);
        for (var i = 0; h != 0 && i < HardRules.MaxZOrderWalk; i++)
        {
            if (h == cover)
            {
                return true;
            }

            h = _win32.GetWindow(h, Win32Const.GW_HWNDPREV);
        }

        return false;
    }

    private void InitForegroundHistory(TimeSpan now)
    {
        // Conservative start (R4): the game counts as "just foreground" (and just used) unless the log proves it lost
        // focus long ago.
        _foregroundSinceMono = null;
        _wasGameForeground = false;
        _lastInputTickSeen = null;
        _lastForegroundMono = now;
        _lastGameUseMono = now;
        try
        {
            var st = _log.State;
            if (st.Focus == FocusState.Lost && st.LastFocusChange is { } t)
            {
                var ago = _clock.UtcNow.LocalDateTime - t;
                if (ago > TimeSpan.Zero)
                {
                    _lastForegroundMono = now - ago;
                    _lastGameUseMono = now - ago;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _lastForegroundMono = now;
            _lastGameUseMono = now;
        }
    }

    private void SafeSamplerTick()
    {
        try
        {
            SamplerTick();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The passive sampler never throws into the timer thread.
        }
    }

    /// <summary>
    /// Passive sample (read-only): foreground PID, cursor, cover under the cursor and the game rect, for the stillness,
    /// cover-stability, home-stability and foreground-cooldown rules. Called by the 250 ms timer and every readiness check.
    /// </summary>
    internal void SamplerTick()
    {
        var id = Identity;
        if (id is null)
        {
            return;
        }

        var now = _clock.Monotonic;
        var fgHwnd = _win32.GetForegroundWindow();
        var fg = fgHwnd != 0 && (fgHwnd == id.Hwnd || _win32.GetWindowProcessId(fgHwnd) == id.Pid);
        var inputTick = _win32.GetLastInputTick();
        var idle = _win32.GetSystemIdle();
        var cursorOk = _win32.TryGetCursorPos(out var cursor);
        long cover = 0;
        RectI? coverFrame = null;
        if (cursorOk)
        {
            cover = TopAt(cursor);
            coverFrame = cover == 0 ? null : _win32.GetFrameBounds(cover);
        }

        var rect = _win32.GetWindowRect(id.Hwnd);
        var zoomed = _win32.IsZoomed(id.Hwnd);
        lock (_state)
        {
            if (fg)
            {
                _lastForegroundMono = now;
            }

            TrackGameUse(now, fg, fgHwnd == id.Hwnd, inputTick, idle);
            if (!cursorOk || _trackCursor != cursor)
            {
                _trackCursor = cursorOk ? cursor : null;
                _trackCursorSince = now;
            }

            var coverKey = (cover, coverFrame);
            if (_trackCover != coverKey)
            {
                _trackCover = coverKey;
                _trackCoverSince = now;
            }

            if (rect is { } r)
            {
                var homeKey = (r, zoomed);
                if (_trackHome != homeKey)
                {
                    _trackHome = homeKey;
                    _trackHomeSince = now;
                }
            }
        }
    }

    /// <summary>
    /// The user-activity clock behind <see cref="ReadinessReport.SinceGameUse"/> and the foreground settle (the caller holds
    /// <see cref="_state"/>). The game counts as used when it becomes or stops being foreground, and at the time of any
    /// keyboard or mouse input while it is foreground. Our own inputs are window messages and window moves, never input, so
    /// a user resting while the companion plays in foreground mode keeps this clock growing (SPEC 7.3b).
    /// </summary>
    private void TrackGameUse(TimeSpan now, bool fg, bool fgExact, uint? inputTick, TimeSpan? idle)
    {
        if (fg != _wasGameForeground)
        {
            _lastGameUseMono = now;
        }
        else if (fg && (inputTick is null || (_lastInputTickSeen is { } seen && inputTick != seen)))
        {
            var at = idle is { } i ? now - i : now;
            if (at > _lastGameUseMono)
            {
                _lastGameUseMono = at;
            }
        }

        _foregroundSinceMono = fgExact ? _foregroundSinceMono ?? now : null;
        _wasGameForeground = fg;
        _lastInputTickSeen = inputTick;
    }

    /// <summary>The user's foreground-mode switch (SPEC 7.3b); false when unset or when reading it fails.</summary>
    private bool ForegroundModeOn() => SwitchOn(_options.ForegroundSlide);

    /// <summary>The user's switch for drags in foreground mode; false when unset or when reading it fails.</summary>
    private bool ForegroundDragOn() => SwitchOn(_options.ForegroundDrag);

    private static bool SwitchOn(Func<bool>? read)
    {
        try
        {
            return read?.Invoke() == true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private void BeginOurInput()
    {
        lock (_state)
        {
            _ourInputs.Add((_clock.UtcNow.LocalDateTime, null));
            if (_ourInputs.Count > 32)
            {
                _ourInputs.RemoveAt(0);
            }
        }
    }

    private void EndOurInput()
    {
        lock (_state)
        {
            if (_ourInputs.Count > 0 && _ourInputs[^1].End is null)
            {
                _ourInputs[^1] = (_ourInputs[^1].Start, _clock.UtcNow.LocalDateTime);
            }
        }
    }

    /// <summary>True if a game timestamp falls inside one of our recent input windows (attribution of focus lines).</summary>
    private bool IsOurs(DateTime gameLocal)
    {
        var nowLocal = _clock.UtcNow.LocalDateTime;
        lock (_state)
        {
            return _ourInputs.Any(w => gameLocal >= w.Start - OurFocusSlackBefore && gameLocal <= (w.End ?? nowLocal) + OurFocusSlackAfter);
        }
    }

    // ------------------------------------------------------------------ journal (codes only, never throws)

    private void Journal(JournalKind kind, string? stepId, params (string Key, object? Value)[] fields)
    {
        try
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (k, v) in fields)
            {
                dict[k] = v switch
                {
                    null => null,
                    string s => Code(s),
                    bool or int or long or double => v,
                    TimeSpan ts => Math.Round(ts.TotalMilliseconds, 1),
                    Enum e => Code(e.ToString()),
                    _ => Code(v.ToString() ?? string.Empty),
                };
            }

            _journal.Write(new JournalRecord(_clock.UtcNow, kind, null, null, stepId is null ? null : Code(stepId), dict));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The journal never throws into control flow.
        }
    }

    /// <summary>Restricts a value to the journal code alphabet ^[A-Za-z0-9_.:,()\[\]/+\- ]{0,120}$.</summary>
    internal static string Code(string value)
    {
        var sb = new StringBuilder(Math.Min(value.Length, 120));
        foreach (var c in value)
        {
            if (sb.Length >= 120)
            {
                break;
            }

            var ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '_' or '.' or ':' or ',' or '(' or ')' or '[' or ']' or '/' or '+' or '-' or ' ';
            sb.Append(ok ? c : '_');
        }

        return sb.ToString();
    }

    private sealed record OurDeviation(long Hwnd, RectI Home, RectI Slid, bool SizeChanged);
}
