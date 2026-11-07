using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.GameIO.Geometry;
using Companion.GameIO.Input;
using Companion.GameIO.Native;
using Companion.GameIO.Safety;

namespace Companion.GameIO;

/// <summary>Capture, home check, restore ladder and crash recovery (SPEC 7.6).</summary>
public sealed partial class GameIo
{
    /// <inheritdoc />
    public CaptureResult Capture(CancellationToken ct)
    {
        lock (_io)
        {
            var t0 = _clock.Monotonic;
            CaptureResult Fail(CaptureProblem p) => new(p, null, _clock.Monotonic - t0);

            var (id, choke) = Bound();
            if (id is null || choke is null || !ValidateIdentity(id))
            {
                return Fail(CaptureProblem.NotConnected);
            }

            ct.ThrowIfCancellationRequested();
            var h = id.Hwnd;
            if (_win32.IsIconic(h))
            {
                return Fail(CaptureProblem.Iconic);
            }

            if (_win32.IsCloaked(h))
            {
                return Fail(CaptureProblem.Cloaked);
            }

            var rect = _win32.GetWindowRect(h);
            var origin = _win32.GetClientOrigin(h);
            var client = _win32.GetClientSize(h);
            if (rect is null || origin is null || client != HomeRules.ExpectedClient)
            {
                return Fail(CaptureProblem.WrongSize);
            }

            var logAt = _logWatch.TryMark();
            var (bytes, timedOut) = PrintWithTimeout(choke, rect.Value, origin.Value, HomeRules.ExpectedClient);
            if (timedOut)
            {
                return Fail(CaptureProblem.Timeout);
            }

            if (bytes is null)
            {
                return Fail(CaptureProblem.PrintWindowFailed);
            }

            var frame = new Frame(
                HardRules.ExpectedClientWidth, HardRules.ExpectedClientHeight, HardRules.ExpectedClientWidth * 4, bytes,
                _clock.Monotonic, _clock.UtcNow, logAt, origin.Value, WhileSlid: false);
            var quality = _vision.Assess(frame);
            if (quality == FrameQuality.WrongSize)
            {
                return Fail(CaptureProblem.WrongSize);
            }

            if (quality != FrameQuality.Ok)
            {
                return Fail(CaptureProblem.InvalidImage);
            }

            _ownCaptures.AddOrUpdate(frame, CaptureMark);
            return new CaptureResult(CaptureProblem.None, frame, _clock.Monotonic - t0);
        }
    }

    /// <inheritdoc />
    public HomeCheck CheckHome()
    {
        var (id, _) = Bound();
        if (id is null || !_win32.IsWindow(id.Hwnd))
        {
            return new HomeCheck(false, default, default, DeviationOwner.None);
        }

        var h = id.Hwnd;
        var rect = _win32.GetWindowRect(h) ?? default;
        var zoomed = _win32.IsZoomed(h);
        var client = _win32.GetClientSize(h);
        var monitorName = _win32.MonitorFromWindow(h);
        var monitor = _win32.EnumMonitors().FirstOrDefault(m => string.Equals(m.DeviceName, monitorName, StringComparison.Ordinal));
        var expected = monitor is null ? default : SlideGeometry.ExpectedMaximizedRect(monitor.WorkArea, _win32.GetMaximizedFrameThickness());
        var atHome = monitor is not null && zoomed && rect == expected && client == HomeRules.ExpectedClient;
        if (atHome)
        {
            return new HomeCheck(true, rect, expected, DeviationOwner.None);
        }

        return new HomeCheck(false, rect, expected, IsDeviationOurs(id, rect) ? DeviationOwner.Ours : DeviationOwner.Others);
    }

    /// <inheritdoc />
    public RestoreReport RestoreHomeIfOurs()
    {
        lock (_io)
        {
            var (id, choke) = Bound();
            if (id is null || choke is null || !ValidateIdentity(id))
            {
                return new RestoreReport(false, false, RestoreRung.None, null);
            }

            var check = CheckHome();
            if (check.AtHome)
            {
                ClearDeviation(id);
                return new RestoreReport(false, true, RestoreRung.None, null);
            }

            if (check.Owner != DeviationOwner.Ours)
            {
                var others = new WindowIncident(IncidentKind.MovedByOthers, check.ExpectedMaximized, check.Current, false, RestoreRung.None, "deviation.others");
                Journal(JournalKind.Incident, null, ("kind", others.Kind.ToString()), ("detail", others.DetailCode));
                return new RestoreReport(false, false, RestoreRung.None, others);
            }

            RectI home;
            lock (_state)
            {
                home = _ourDeviation?.Home ?? default;
            }

            if (home == default)
            {
                var (m, _) = _marker.TryRead();
                home = m?.Home ?? check.ExpectedMaximized;
            }

            BeginOurInput();
            try
            {
                var report = RunLadder(choke, home, true, fromWatchdog: false, observed: check.Current);
                if (report.Success)
                {
                    ClearDeviation(id);
                }

                if (report.Incident is { } inc)
                {
                    Journal(JournalKind.Incident, null, ("kind", inc.Kind.ToString()), ("restored", inc.Restored), ("rung", inc.Rung.ToString()), ("detail", inc.DetailCode));
                }

                return report;
            }
            finally
            {
                EndOurInput();
            }
        }
    }

    /// <summary>Monotonic time the foreground/LostFocus mismatch was first seen (null = no mismatch).</summary>
    private TimeSpan? _focusMismatchSince;

    /// <summary>Monotonic time of the last focus resync.</summary>
    private TimeSpan? _lastFocusResync;

    /// <inheritdoc />
    public bool ResyncFocus()
    {
        if (!Monitor.TryEnter(_io))
        {
            return false; // an input runs: it owns the focus messages
        }

        try
        {
            var (id, choke) = Bound();
            if (id is null || choke is null || !choke.IsGameForeground() || _logWatch.State.Focus != Companion.Contracts.Logs.FocusState.Lost)
            {
                _focusMismatchSince = null;
                return false;
            }

            // the game is in front, Unity says it lost focus: a real switch logs RecoverFocus within milliseconds, so a
            // mismatch that lasts a second is a stale fake unfocus (the user's UI clicks are ignored until it is fixed)
            var now = _clock.Monotonic;
            _focusMismatchSince ??= now;
            if (now - _focusMismatchSince.Value < TimeSpan.FromSeconds(1)
                || (_lastFocusResync is { } last && now - last < TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            _lastFocusResync = now;
            _focusMismatchSince = null;
            foreach (var m in HardRules.FocusOnSequence)
            {
                choke.Send(choke.Game, m.Message, m.WParam, m.LParam);
            }

            Journal(JournalKind.Input, null, ("event", "focus.resync"));
            return true;
        }
        finally
        {
            Monitor.Exit(_io);
        }
    }

    /// <inheritdoc />
    public RestoreReport RecoverLeftoverDisplacement()
    {
        lock (_io)
        {
            var (marker, corrupt) = _marker.TryRead();
            if (marker is null && !corrupt)
            {
                return new RestoreReport(false, true, RestoreRung.None, null);
            }

            var exists = marker is not null && _win32.IsWindow(marker.Hwnd);
            var livePid = exists ? _win32.GetWindowProcessId(marker!.Hwnd) : 0;
            var liveStart = exists ? _win32.GetProcessStartUtc(livePid) : null;
            var classOk = exists && string.Equals(_win32.GetClassName(marker!.Hwnd), HardRules.GameWindowClass, StringComparison.Ordinal);
            var rect = exists ? _win32.GetWindowRect(marker!.Hwnd) : null;
            var iconic = exists && _win32.IsIconic(marker!.Hwnd);
            var decision = MarkerRecovery.Decide(marker, corrupt, exists, livePid, liveStart, classOk, rect, iconic);
            if (decision == MarkerDecision.RestoreMoveOnly)
            {
                var exe = Guards.QueryImagePath(_win32, livePid, StaticRules.OpenProcessAllowedAccess);
                if (exe is null || !string.Equals(Path.GetFullPath(exe), Path.GetFullPath(_paths.GameExePath), StringComparison.OrdinalIgnoreCase))
                {
                    decision = MarkerDecision.DeleteIdentityDiffers;
                }
            }

            Journal(JournalKind.Incident, null, ("kind", nameof(IncidentKind.LeftoverFromCrash)), ("decision", decision.ToString()));
            switch (decision)
            {
                case MarkerDecision.DeleteIdentityDiffers:
                case MarkerDecision.DeleteAtHome:
                    _marker.TryDelete();
                    return new RestoreReport(false, true, RestoreRung.None, null);

                case MarkerDecision.MarkerCorrupt:
                    return new RestoreReport(false, false, RestoreRung.None, new WindowIncident(IncidentKind.LeftoverFromCrash, null, null, false, RestoreRung.None, "marker.corrupt"));

                case MarkerDecision.WaitIconic:
                    return new RestoreReport(false, false, RestoreRung.None, new WindowIncident(IncidentKind.LeftoverFromCrash, marker!.Home, rect, false, RestoreRung.None, "marker.iconic"));

                case MarkerDecision.LeaveAloneNeither:
                    return new RestoreReport(false, false, RestoreRung.None, new WindowIncident(IncidentKind.MovedByOthers, marker!.Home, rect, false, RestoreRung.None, "marker.neither"));

                case MarkerDecision.RestoreMoveOnly:
                    var m = marker!;
                    var choke = new ChokePoint(_win32, m.Hwnd, m.Pid, HomeRules.ExpectedClient);
                    if (m.Foreground && !RecoveryDrained(choke))
                    {
                        // a foreground-mode input may have left our DOWN/UP unread: a move now would make the press land at
                        // home when the game reads it (SPEC 7.3b); the marker stays for the next recovery
                        var pending = new WindowIncident(IncidentKind.LeftoverFromCrash, m.Home, rect, false, RestoreRung.None, "marker.pressPending");
                        Journal(JournalKind.Incident, null, ("kind", pending.Kind.ToString()), ("restored", false), ("detail", pending.DetailCode));
                        return new RestoreReport(false, false, RestoreRung.None, pending);
                    }

                    BeginOurInput();
                    try
                    {
                        choke.SetWindowPos(m.Hwnd, 0, m.Home.Left, m.Home.Top, 0, 0, HardRules.SwpSlide, out _);
                        var ok = VerifyHome(m.Hwnd, m.Home, null, VerifyWindow);
                        if (ok)
                        {
                            _marker.TryDelete();
                        }

                        var inc = new WindowIncident(ok ? IncidentKind.LeftoverFromCrash : IncidentKind.RestoreFailed, m.Home, rect, ok, RestoreRung.MoveOnly, ok ? "marker.restored" : "marker.restoreFailed");
                        Journal(JournalKind.Incident, null, ("kind", inc.Kind.ToString()), ("restored", ok), ("rung", nameof(RestoreRung.MoveOnly)), ("detail", inc.DetailCode));
                        return new RestoreReport(true, ok, RestoreRung.MoveOnly, inc);
                    }
                    finally
                    {
                        EndOurInput();
                    }

                default:
                    return new RestoreReport(false, true, RestoreRung.None, null);
            }
        }
    }

    /// <summary>
    /// The drain for a foreground-mode marker (SPEC 7.3b): two WM_NULL round trips 50 ms apart within
    /// <see cref="HardRules.ForegroundRecoveryDrainMax"/>; only a timeout is waited out.
    /// </summary>
    private bool RecoveryDrained(ChokePoint choke)
    {
        var start = _clock.Monotonic;
        for (var trip = 0; trip < 2; trip++)
        {
            while (true)
            {
                var r = choke.Send(choke.Game, HardRules.WM_NULL, 0, 0);
                if (r == SendOutcome.Ok)
                {
                    break;
                }

                if (r != SendOutcome.Timeout || _clock.Monotonic - start >= HardRules.ForegroundRecoveryDrainMax)
                {
                    return false;
                }

                _clock.Sleep(LogPollSlice, CancellationToken.None);
            }

            if (trip == 0)
            {
                _clock.Sleep(HardRules.ForegroundDrainSettle, CancellationToken.None);
            }
        }

        return true;
    }

    /// <summary>
    /// Restore ladder (SPEC 7.6): move-only to home (0x0215, 0x4215 from the watchdog) verified within 200 ms; move+size
    /// to the live maximized rect (0x0214/0x4214, emergency) only if the size changed, at most once; one posted
    /// WM_SIZE(SIZE_MAXIMIZED, 1920x1009) verified by the resolution line and the rect (then a fake unfocus, since
    /// WM_SIZE logs RecoverFocus). No rung while iconic; never onto a vanished home monitor. All failed = RestoreFailed.
    /// </summary>
    private RestoreReport RunLadder(ChokePoint choke, RectI home, bool homeZoomed, bool fromWatchdog, RectI? observed)
    {
        var h = choke.Game;
        observed ??= _win32.GetWindowRect(h);
        var zoomedNow = _win32.IsZoomed(h);
        var kind = observed is { } ob ? HomeRules.DeviationKind(home, ob, homeZoomed, zoomedNow) : IncidentKind.NotHomeAfterInput;
        if (_win32.IsIconic(h))
        {
            return new RestoreReport(false, false, RestoreRung.None, new WindowIncident(kind, home, observed, false, RestoreRung.None, "ladder.iconic"));
        }

        var monitors = _win32.EnumMonitors();
        var homeMonitor = monitors.FirstOrDefault(m => m.Bounds.Contains(home.Center));
        if (homeMonitor is null)
        {
            return new RestoreReport(false, false, RestoreRung.None, new WindowIncident(IncidentKind.MonitorVanished, home, observed, false, RestoreRung.None, "ladder.monitorVanished"));
        }

        var sizeChanged = observed is { } o2 && SlideGeometry.SizeChanged(home, o2);
        var rungs = HomeRules.PlanLadder(false, true, sizeChanged, false);
        foreach (var rung in rungs)
        {
            bool ok;
            switch (rung)
            {
                case RestoreRung.MoveOnly:
                    choke.SetWindowPos(h, 0, home.Left, home.Top, 0, 0, fromWatchdog ? HardRules.SwpSlideAsync : HardRules.SwpSlide, out _);
                    ok = VerifyHome(h, home, homeZoomed, VerifyWindow);
                    break;

                case RestoreRung.MoveSize:
                    var target = SlideGeometry.ExpectedMaximizedRect(homeMonitor.WorkArea, _win32.GetMaximizedFrameThickness());
                    using (choke.EnterEmergency())
                    {
                        choke.SetWindowPos(h, 0, target.Left, target.Top, target.Width, target.Height, fromWatchdog ? HardRules.SwpEmergencyMoveSizeAsync : HardRules.SwpEmergencyMoveSize, out _);
                    }

                    ok = VerifyHome(h, home, homeZoomed, VerifyWindow);
                    break;

                case RestoreRung.PostedWmSize:
                    var mark = _logWatch.TryMark();
                    using (choke.EnterEmergency())
                    {
                        choke.Post(h, HardRules.WM_SIZE, HardRules.SIZE_MAXIMIZED, ((long)HardRules.ExpectedClientHeight << 16) | (long)HardRules.ExpectedClientWidth, out _);
                    }

                    ok = WaitResolutionAndHome(h, home, homeZoomed, mark);
                    if (!choke.InForegroundInput)
                    {
                        // foreground mode sends no focus message at all (SPEC 7.3b); the choke point would refuse it
                        SendFocusOff(choke);
                    }

                    break;

                default:
                    ok = false;
                    break;
            }

            if (ok)
            {
                return new RestoreReport(true, true, rung, new WindowIncident(kind, home, observed, true, rung, "ladder.ok"));
            }
        }

        return new RestoreReport(true, false, RestoreRung.Failed, new WindowIncident(IncidentKind.RestoreFailed, home, observed, false, RestoreRung.Failed, "ladder.failed"));
    }

    /// <summary>Polls (IClock) until the window is at <paramref name="home"/> (and zoomed as given) or the time is up.</summary>
    private bool VerifyHome(long hwnd, RectI home, bool? zoomed, TimeSpan within)
    {
        var end = _clock.Monotonic + within;
        while (true)
        {
            if (_win32.GetWindowRect(hwnd) == home && (zoomed is null || _win32.IsZoomed(hwnd) == zoomed))
            {
                return true;
            }

            if (_clock.Monotonic >= end)
            {
                return false;
            }

            _clock.Sleep(PollSlice, CancellationToken.None);
        }
    }

    private bool WaitResolutionAndHome(long hwnd, RectI home, bool homeZoomed, Contracts.Logs.LogPosition? mark)
    {
        var end = _clock.Monotonic + WmSizeWait;
        while (true)
        {
            var resolutionSeen = mark is not null && LogWatch.HasResolution(SafeSince(mark), HomeRules.ExpectedClient);
            if (resolutionSeen && _win32.GetWindowRect(hwnd) == home && _win32.IsZoomed(hwnd) == homeZoomed)
            {
                return true;
            }

            if (_clock.Monotonic >= end)
            {
                return false;
            }

            _clock.Sleep(LogPollSlice, CancellationToken.None);
        }
    }

    private IReadOnlyList<Contracts.Logs.LogEvent> SafeSince(Contracts.Logs.LogPosition from)
    {
        try
        {
            return _logWatch.Since(from);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [];
        }
    }

    private bool IsDeviationOurs(GameIdentity id, RectI current)
    {
        lock (_state)
        {
            if (_ourDeviation is { } d && d.Hwnd == id.Hwnd)
            {
                return true;
            }
        }

        var (m, _) = _marker.TryRead();
        return m is not null && m.Hwnd == id.Hwnd && m.Pid == id.Pid
            && Math.Abs((m.ProcessStartUtc - id.ProcessStartUtc).TotalMilliseconds) < 1
            && m.IsOurDisplacement(current);
    }

    private void ClearDeviation(GameIdentity id)
    {
        lock (_state)
        {
            _ourDeviation = null;
        }

        var (m, _) = _marker.TryRead();
        if (m is not null && m.Hwnd == id.Hwnd && m.Pid == id.Pid)
        {
            _marker.TryDelete();
        }
    }
}
