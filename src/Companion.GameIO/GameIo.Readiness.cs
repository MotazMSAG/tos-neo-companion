using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.GameIO.Geometry;
using Companion.GameIO.Native;
using Companion.GameIO.Safety;

namespace Companion.GameIO;

/// <summary>Readiness (passive: nothing is sent to the game).</summary>
public sealed partial class GameIo
{
    /// <inheritdoc />
    public ReadinessReport CheckReadiness(ReadinessQuery query) =>
        EvaluateReadiness(query.Kind, query.TargetClientPoint, query.NearDenyZone, query.CrossMonitorAllowed, query.LetterKey, query.Drag).Report;

    /// <summary>
    /// The passive readiness checks, in order (first failing reason wins): stop switch; identity; extra game window;
    /// iconic; foreground: the game's PID in front is not ready unless foreground mode is on and the game's own window is
    /// the foreground window (SPEC 7.3b); cooldowns (cover mode: 5 s since foreground; 5 s since a foreign RecoverFocus or
    /// OnScreenSizeChange; foreground mode: in front for 5 s without a break and not logged unfocused); interactive
    /// session; hung; display mode (maximized, 1920x1009 client, caption, not cloaked, prefs windowed); home monitor DPI.
    /// Drags in foreground mode need the user's foreground-drag switch (else GameForeground, as before).
    /// Keys in foreground mode add: no mouse button down, no input for 3 s, for a letter key no open chat. Clicks add: no
    /// mouse button down; cursor still 0.8 s (1.5 s near a deny zone; 2 s in foreground mode); system idle (foreground
    /// mode 2 s); valid home; a valid cover, or in foreground mode the game itself under and around the cursor well inside
    /// its client; slide plan (distance, monitor, DPI; exposure journaled); no jank / open scene load.
    /// </summary>
    private ReadinessEval EvaluateReadiness(
        InputKind kind, PointI? target, bool nearDenyZone, bool crossMonitorAllowed = true, bool letterKey = false, bool drag = false)
    {
        SamplerTick();
        ScanForeignFocus();
        var now = _clock.Monotonic;
        var foregroundOn = ForegroundModeOn();
        TimeSpan sinceFg, sinceUse, stillFor, coverStable, homeStable;
        TimeSpan? foregroundFor;
        lock (_state)
        {
            sinceFg = _lastForegroundMono is { } lf ? now - lf : TimeSpan.Zero;
            sinceUse = foregroundOn ? now - _lastGameUseMono : sinceFg;
            foregroundFor = _foregroundSinceMono is { } fs ? now - fs : null;
            stillFor = _trackCursor is null ? TimeSpan.Zero : now - _trackCursorSince;
            coverStable = _trackCover is null ? TimeSpan.Zero : now - _trackCoverSince;
            homeStable = _trackHome is null ? TimeSpan.Zero : now - _trackHomeSince;
        }

        var cursorValid = _win32.TryGetCursorPos(out var cursor);
        var inputTick = _win32.GetLastInputTick(); // before the idle read: input after it shows as a changed tick later
        var idle = _win32.GetSystemIdle() ?? TimeSpan.Zero;
        CoverInfo? coverInfo = null;
        SlidePlan? plan = null;
        var mode = InputMode.Cover;

        ReadinessEval Fail(NotReadyReason reason) =>
            new(new ReadinessReport(false, reason, cursor, cursorValid, stillFor, idle, coverInfo, plan, sinceFg, mode, sinceUse), null);

        if (_stop.IsStopRequested)
        {
            return Fail(NotReadyReason.Stopped);
        }

        var (id, choke) = Bound();
        if (id is null || choke is null || !ValidateIdentity(id))
        {
            return Fail(NotReadyReason.GameNotConnected);
        }

        var h = id.Hwnd;
        var zorder = _win32.EnumTopLevelWindows();
        if (CountExtraGameWindows(id, zorder) > 0)
        {
            return Fail(NotReadyReason.ExtraGameWindow);
        }

        if (_win32.IsIconic(h))
        {
            return Fail(NotReadyReason.GameIconic);
        }

        if (IsPidForeground(id.Pid, h))
        {
            // Foreground mode (SPEC 7.3b): only with the user's switch on and the game's own window in front (a game
            // dialog or another window of the game PID in front is not ready); a drag also needs its own switch.
            if (!foregroundOn || _win32.GetForegroundWindow() != h || (drag && !ForegroundDragOn()))
            {
                return Fail(NotReadyReason.GameForeground);
            }

            mode = InputMode.Foreground;
        }

        if (mode == InputMode.Cover && (sinceFg < HardRules.CooldownAfterGameForeground || ForeignFocusWithin(HardRules.CooldownAfterForeignFocus)))
        {
            return Fail(NotReadyReason.Cooldown);
        }

        // Foreground mode: the user's own switch-in settles first (a foreign RecoverFocus, 5 s in front without a break),
        // and posted buttons need the game to consider itself focused (a stale fake unfocus would make it ignore them).
        if (mode == InputMode.Foreground
            && (ForeignFocusWithin(HardRules.CooldownAfterForeignFocus) || foregroundFor is not { } settled || settled < HardRules.ForegroundSettle
                || _log.State.Focus == FocusState.Lost))
        {
            return Fail(NotReadyReason.ForegroundSettling);
        }

        if (!cursorValid || !_win32.IsInputDesktopAvailable())
        {
            return Fail(NotReadyReason.SessionNotInteractive);
        }

        if (_win32.IsHungAppWindow(h))
        {
            return Fail(NotReadyReason.GameBusy);
        }

        var zoomed = _win32.IsZoomed(h);
        var caption = (_win32.GetStyle(h) & Win32Const.WS_CAPTION) == Win32Const.WS_CAPTION;
        GameWindowPrefs? prefs;
        lock (_state)
        {
            prefs = _prefsBaseline;
        }

        if (_win32.GetWindowRect(h) is not { } rect || _win32.GetClientSize(h) is not { } client
            || client != HomeRules.ExpectedClient || !zoomed || !caption || _win32.IsCloaked(h)
            || (prefs?.FullscreenMode is { } fm && fm != HardRules.ExpectedFullscreenMode))
        {
            return Fail(NotReadyReason.DisplayModeWrong);
        }

        var monitors = _win32.EnumMonitors();
        var homeMonitorName = _win32.MonitorFromWindow(h);
        var homeMonitor = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, homeMonitorName, StringComparison.Ordinal));
        if (homeMonitor is null)
        {
            return Fail(NotReadyReason.DisplayModeWrong);
        }

        if (homeMonitor.Dpi != HardRules.ExpectedDpi)
        {
            return Fail(NotReadyReason.DpiUnsupported);
        }

        if (kind == InputKind.Key)
        {
            if (mode == InputMode.Foreground)
            {
                // The keyboard goes to the game now: never while the user types or holds a button, and never a letter
                // into an open chat (SPEC 7.3b; the engine's clean-HUD rule for letters holds too).
                if (Guards.AnyMouseButtonDown(_win32))
                {
                    return Fail(NotReadyReason.UserButtonDown);
                }

                if (inputTick is null || idle < HardRules.ForegroundKeyIdle)
                {
                    return Fail(NotReadyReason.UserInputRecent);
                }

                if (letterKey && _log.State.AdvisoryStack.Contains(ChatUi, StringComparer.Ordinal))
                {
                    return Fail(NotReadyReason.ChatOpen);
                }
            }

            return new ReadinessEval(new ReadinessReport(true, NotReadyReason.None, cursor, cursorValid, stillFor, idle, null, null, sinceFg, mode, sinceUse), null, inputTick);
        }

        if (Guards.AnyMouseButtonDown(_win32))
        {
            return Fail(NotReadyReason.UserButtonDown);
        }

        var required = mode == InputMode.Foreground ? HardRules.ForegroundCursorStillness
            : nearDenyZone ? HardRules.CursorStillnessNearDenyZone : HardRules.CursorStillness;
        if (stillFor < required)
        {
            return Fail(NotReadyReason.CursorMoving);
        }

        if (mode == InputMode.Foreground && (inputTick is null || idle < HardRules.ForegroundSystemIdle))
        {
            return Fail(NotReadyReason.UserInputRecent);
        }

        if (idle < HardRules.SystemIdleForSlide)
        {
            return Fail(NotReadyReason.SystemNotIdle);
        }

        var expectedMax = SlideGeometry.ExpectedMaximizedRect(homeMonitor.WorkArea, _win32.GetMaximizedFrameThickness());
        var state = _log.State;
        if (HomeRules.Validate(new HomeFacts(rect, zoomed, client, caption, state.LastResolution, expectedMax, homeStable)) is not null)
        {
            return Fail(NotReadyReason.WindowNotAtHome);
        }

        var top = TopAt(cursor);
        var probes = SlideGeometry.ProbePoints(cursor).Select(TopAt).ToList();
        var gameIndex = IndexOf(zorder, h);
        WindowFacts? facts = null;
        if (mode == InputMode.Foreground)
        {
            // no cover: the game itself must be under and around the resting cursor, well inside its client (SPEC 7.3b)
            if (ForegroundRules.Evaluate(h, top, probes, cursor, _win32.GetClientOrigin(h), client) != ForegroundReject.None)
            {
                return Fail(NotReadyReason.CursorNotOnGame);
            }
        }
        else
        {
            if (top == 0)
            {
                return Fail(NotReadyReason.NoCover);
            }

            facts = ReadFacts(top);
            var fgHwnd = _win32.GetForegroundWindow();
            var fgPid = fgHwnd == 0 ? 0 : _win32.GetWindowProcessId(fgHwnd);
            var coverIndex = IndexOf(zorder, top);
            var ctx = new CoverContext(
                h, id.Pid, _win32.CurrentProcessId, _paths.GameDir, fgHwnd, fgPid, cursor, probes, coverStable, required,
                coverIndex >= 0 && gameIndex >= 0 && coverIndex < gameIndex,
                IsAboveByWalk(top, h));
            var reject = CoverRules.Evaluate(facts, ctx);
            coverInfo = new CoverInfo(top, facts.ClassName, facts.Pid, facts.FrameBounds ?? default, reject);
            if (reject != CoverRejectReason.None)
            {
                return Fail(NotReadyReason.CoverRejected);
            }
        }

        ClickGeometry? geometry = null;
        if (target is { } t)
        {
            if (!new RectI(0, 0, HardRules.ExpectedClientWidth, HardRules.ExpectedClientHeight).Contains(t))
            {
                return Fail(NotReadyReason.MonitorRule);
            }

            if (_win32.GetClientOrigin(h) is not { } origin)
            {
                return Fail(NotReadyReason.DisplayModeWrong);
            }

            var covers = new List<RectI>();
            for (var i = 0; i < gameIndex; i++)
            {
                var w = zorder[i];
                if (!_win32.IsWindowVisible(w))
                {
                    continue;
                }

                if (CoverRules.HidesWhatIsBehind(true, _win32.IsIconic(w), _win32.IsCloaked(w), _win32.GetExStyle(w), _win32.GetLayeredAlpha(w))
                    && _win32.GetFrameBounds(w) is { IsEmpty: false } fb)
                {
                    covers.Add(fb);
                }
            }

            plan = SlideGeometry.BuildPlan(rect, origin, cursor, t, monitors, homeMonitor, covers);
            var cursorMonitor = SlideGeometry.MonitorAt(cursor, monitors);
            var planReason = SlideGeometry.CheckPlan(plan, homeMonitor, cursorMonitor, _win32.MonitorFromRect(plan.SlidRect), monitors);
            if (planReason != NotReadyReason.None)
            {
                return Fail(planReason);
            }

            if (!crossMonitorAllowed && SlidVerifyRules.IsCrossMonitor(plan))
            {
                return Fail(NotReadyReason.CrossMonitor); // unverifiable while slid: wait for the cursor (2026-10-16)
            }

            var slidMonitorBounds = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, plan.SlidMonitor, StringComparison.Ordinal))?.Bounds;
            geometry = facts is null
                ? new ClickGeometry(cursor, 0, HardRules.GameWindowClass, default, rect, zoomed, plan, homeMonitor.Bounds, slidMonitorBounds, mode, inputTick)
                : new ClickGeometry(cursor, top, facts.ClassName, facts.FrameBounds ?? default, rect, zoomed, plan, homeMonitor.Bounds, slidMonitorBounds, mode, inputTick);
        }

        var nowLocal = _clock.UtcNow.LocalDateTime;
        if (state.Busy == BusyKind.Loading || (state.LastJank is { } jank && nowLocal - jank < HardRules.JankQuiet))
        {
            return Fail(NotReadyReason.RecentJank);
        }

        return new ReadinessEval(new ReadinessReport(true, NotReadyReason.None, cursor, cursorValid, stillFor, idle, coverInfo, plan, sinceFg, mode, sinceUse), geometry, inputTick);
    }

    private WindowFacts ReadFacts(long hwnd)
    {
        var pid = _win32.GetWindowProcessId(hwnd);
        string? exe = null;
        if (pid > 0)
        {
            exe = Guards.QueryImagePath(_win32, pid, StaticRules.OpenProcessAllowedAccess);
        }

        return new WindowFacts(
            hwnd,
            _win32.GetClassName(hwnd),
            pid,
            _win32.IsWindowVisible(hwnd),
            _win32.IsIconic(hwnd),
            _win32.IsCloaked(hwnd),
            _win32.GetExStyle(hwnd),
            _win32.GetLayeredAlpha(hwnd),
            _win32.GetFrameBounds(hwnd),
            _win32.GetAncestor(hwnd, Win32Const.GA_ROOTOWNER),
            exe);
    }

    private static int IndexOf(IReadOnlyList<long> list, long value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Incrementally scans new log events for RecoverFocus lines that were not caused by our inputs.</summary>
    private void ScanForeignFocus()
    {
        try
        {
            var pos = _log.Position;
            if (pos is null)
            {
                return;
            }

            LogPosition? from;
            lock (_state)
            {
                from = _focusScanFrom;
                if (from is null || !from.SameFile(pos))
                {
                    _focusScanFrom = pos;
                    return;
                }
            }

            var events = _log.EventsSince(from);
            DateTime? latest = null;
            foreach (var e in events)
            {
                if (e is FocusLogEvent { Recovered: true } f && !IsOurs(f.Timestamp) && (latest is null || f.Timestamp > latest))
                {
                    latest = f.Timestamp;
                }
            }

            lock (_state)
            {
                if (events.Count > 0)
                {
                    _focusScanFrom = events[^1].Position;
                }

                if (latest is { } l && (_lastForeignFocusLocal is null || l > _lastForeignFocusLocal))
                {
                    _lastForeignFocusLocal = l;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Passive bookkeeping; a failure here only means less information (the engine also gates on the log).
        }
    }

    private bool ForeignFocusWithin(TimeSpan window)
    {
        var nowLocal = _clock.UtcNow.LocalDateTime;
        DateTime? last;
        lock (_state)
        {
            last = _lastForeignFocusLocal;
        }

        if (last is { } t && nowLocal - t < window)
        {
            return true;
        }

        var sc = _log.State.LastScreenSizeChange;
        return sc is { } s && nowLocal - s < window && !IsOurs(s);
    }

    /// <summary>
    /// Geometry captured by a passing click readiness check. Foreground mode (SPEC 7.3b): no cover (CoverHwnd 0, the game's
    /// class), and the GetLastInputInfo tick at readiness, against which the section detects the user's input.
    /// </summary>
    private sealed record ClickGeometry(
        PointI Cursor, long CoverHwnd, string CoverClass, RectI CoverFrame, RectI Home, bool HomeZoomed, SlidePlan Plan, RectI HomeMonitorBounds, RectI? SlidMonitorBounds,
        InputMode Mode = InputMode.Cover, uint? InputTick = null);

    /// <summary>Readiness result plus the click geometry when ready for a click, and the input tick read with it.</summary>
    private sealed record ReadinessEval(ReadinessReport Report, ClickGeometry? Click, uint? InputTick = null);

    /// <summary>The game's chat (Enter shows it, EVIDENCE c): a letter key would type into it.</summary>
    private const string ChatUi = "UI_CHAT";
}
