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

/// <summary>
/// Slide click (SPEC 7.3 timeline, port of proofs\p0_c3_window_under_cursor.ps1 plus the adopted findings): the game
/// window slides BEHIND the cover window until the target lies under the resting cursor, one click is posted, the
/// window slides back. The real cursor is never moved; z-order, size and activation are never touched. Foreground mode
/// (SPEC 7.3b, proofs\p0_c15_foreground_click.ps1): the game is the foreground window and the cursor rests on it; no
/// focus message is sent, the window slides visibly, and before it moves home the game is drained so it reads DOWN and UP
/// at the slid position.
/// </summary>
public sealed partial class GameIo
{
    /// <summary>Refusal code of the cross-monitor rule for a same-monitor slide (not journaled).</summary>
    private const string NotCrossMonitor = "notCrossMonitor";

    /// <inheritdoc />
    public ClickReport SlideClick(ClickRequest request, CancellationToken ct) => SlideCore(request, null, ct).Report;

    /// <summary>
    /// A press on a commit target keeps the commit pace (<see cref="HardRules.MaxCommitSlidesPerMinute"/>, 2026-10-15):
    /// a template tagged <see cref="TargetTags.Commit"/> or gated by an automation switch (the Make button and its max
    /// button), as the catalog defines it. An unknown template counts as a commit target (the stricter pace).
    /// </summary>
    private bool IsCommitTarget(string? templateId)
    {
        var definition = templateId is null ? null : _templates.Get(templateId);
        return definition is null || definition.Tags.HasFlag(TargetTags.Commit) || definition.Gate != AutomationSwitch.None;
    }

    /// <summary>
    /// The slide section shared by <see cref="SlideClick"/> and <see cref="SlideDrag"/> (SPEC 7.3, 7.3a): early checks,
    /// readiness (a drag also for its release point, on the same monitor, cursor and cover), pacing (one batch, one slide,
    /// one focus pair), probe, marker (a drag records its path), watchdog, the timeline and the finish. Returns the report
    /// and the run (null when nothing was sent).
    /// </summary>
    private (ClickReport Report, ClickRun? Run) SlideCore(ClickRequest request, DragPlan? drag, CancellationToken ct)
    {
        lock (_io)
        {
            ClickReport Early(InputDisposition d, AbortReason a, PointI? cursor, string? detail, InputMode mode = InputMode.Cover) =>
                EarlyClick(request, d, a, cursor, detail, drag, mode);

            if (_stop.IsStopRequested || ct.IsCancellationRequested)
            {
                return (Early(InputDisposition.Stopped, AbortReason.StopRequested, null, "stop"), null);
            }

            var (id, choke) = Bound();
            if (id is null || choke is null)
            {
                return (Early(InputDisposition.NotSent, AbortReason.NotReady, null, "notConnected"), null);
            }

            if (!ValidateIdentity(id))
            {
                return (Early(ProcessGone(id) ? InputDisposition.GameGone : InputDisposition.Refused, AbortReason.IdentityChanged, null, null), null);
            }

            var clientRect = new RectI(0, 0, HardRules.ExpectedClientWidth, HardRules.ExpectedClientHeight);
            if (string.IsNullOrWhiteSpace(request.TemplateId) || !clientRect.Contains(request.ClientPoint)
                || (drag is not null && (!clientRect.Contains(drag.End) || drag.Start != request.ClientPoint)))
            {
                return (Early(InputDisposition.Refused, AbortReason.None, null, "request.invalid"), null);
            }

            var age = _clock.Monotonic - request.LocatedAtMonotonic;
            if (age < TimeSpan.Zero || age > HardRules.MaxLocateAge)
            {
                return (Early(InputDisposition.NotSent, AbortReason.LocateTooOld, null, null), null);
            }

            ReadinessEval eval;
            ResponsivenessReport probe;
            LogPosition? mark0;
            try
            {
                eval = EvaluateReadiness(InputKind.Click, request.ClientPoint, request.NearDenyZone, drag: drag is not null);
                if (!eval.Report.Ready || eval.Click is null)
                {
                    var d = eval.Report.Reason == NotReadyReason.Stopped ? InputDisposition.Stopped : InputDisposition.NotSent;
                    return (Early(d, AbortReason.NotReady, eval.Report.Cursor, eval.Report.Reason.ToString(), eval.Report.Mode), null);
                }

                if (drag is not null && DragEndRefusal(drag, eval.Click, request.NearDenyZone) is { } endRefusal)
                {
                    var d = endRefusal == nameof(NotReadyReason.Stopped) ? InputDisposition.Stopped : InputDisposition.NotSent;
                    return (Early(d, AbortReason.NotReady, eval.Report.Cursor, endRefusal), null);
                }

                // Off-screen anchor patch rule (SPEC 7.3): when only that rule could let this press go ahead and the located
                // capture would already be too old at the slid capture, send nothing (no focus pair, slide or pacing slot);
                // the engine locates afresh on its retry.
                if (request.PatchVerify && request.TemplateCenter is { } anchor
                    && SlidVerifyRules.PatchLocateTooOld(
                        anchor, request.ClientPoint, eval.Click.Cursor, eval.Click.SlidMonitorBounds, _clock.Monotonic - request.LocatedAtMonotonic, HardRules.MaxLocateAge,
                        eval.Click.Mode))
                {
                    return (Early(InputDisposition.NotSent, AbortReason.LocateTooOld, eval.Report.Cursor, "patch.locatedTooOld"), null);
                }

                if (_pacing.NextAllowedIn(slide: true, focusPair: eval.Click.Mode == InputMode.Cover, commit: IsCommitTarget(request.TemplateId)) > TimeSpan.Zero)
                {
                    return (Early(InputDisposition.RateLimited, AbortReason.None, eval.Report.Cursor, null), null);
                }

                probe = ProbeCore(choke);
                if (probe.LastError == Win32Const.ERROR_ACCESS_DENIED)
                {
                    return (Early(InputDisposition.IntegrityMismatch, AbortReason.None, eval.Report.Cursor, "probe.accessDenied"), null);
                }

                if (!probe.Responsive)
                {
                    return (Early(InputDisposition.NotSent, AbortReason.NotReady, eval.Report.Cursor, nameof(NotReadyReason.GameBusy)), null);
                }

                mark0 = _logWatch.Mark();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return (Early(ex is HardRuleViolationException ? InputDisposition.Refused : InputDisposition.NotSent, AbortReason.NotReady, null, "p0.exception"), null);
            }

            if (mark0 is null)
            {
                return (Early(InputDisposition.NotSent, AbortReason.NotReady, eval.Report.Cursor, "log.noPosition"), null);
            }

            if (!_pacing.TryAcquire(slide: true, focusPair: eval.Click.Mode == InputMode.Cover, commit: IsCommitTarget(request.TemplateId)))
            {
                return (Early(InputDisposition.RateLimited, AbortReason.None, eval.Report.Cursor, null), null);
            }

            // Off-screen anchor patch rule (SPEC 7.3): re-prove the engine's located frame now, at home, so the displaced
            // time does not grow. It never refuses the input; only a failed slid check may use it.
            var locatedRefusal = request.PatchVerify ? LocatedFrameRefusal(request) : null;
            var g = eval.Click;
            bool? showing = _win32.TryGetCursorShowing(out var sh) ? sh : null;
            var session = new InputSession(
                id.Hwnd, id.Pid, g.Cursor, g.CoverHwnd, g.CoverFrame, g.Home, g.HomeZoomed, g.Plan.SlidRect,
                _win32.GetClipCursor(), showing, request.FireOn, request.ClientPoint, _clock.Monotonic,
                isDrag: drag is not null, deadMan: drag is null ? null : HardRules.DragDeadManDeadline(drag.Steps), mode: g.Mode, inputTickBaseline: g.InputTick);

            // P0: durable marker BEFORE anything moves (crash recovery / guardian); a drag records its whole path.
            RectI? pathEnd = drag is null ? null : drag.SlidAt(g.Plan.SlidRect, drag.Steps);
            if (!_marker.TryWrite(new SlideMarkerData(id.Pid, id.Hwnd, id.ProcessStartUtc, g.Home, g.Plan.SlidRect, _clock.UtcNow, pathEnd, g.Mode == InputMode.Foreground)))
            {
                return (Early(InputDisposition.NotSent, AbortReason.NotReady, g.Cursor, "marker.writeFailed"), null);
            }

            var run = new ClickRun(request, id, choke, session, g, mark0, ct) { Drag = drag, LocatedRefusal = locatedRefusal };
            var watchdog = new SlideWatchdog(_win32, _clock, _stop, session, t => ExecuteAbort(run, t, fromWatchdog: true), _options.ManualWatchdog);
            Volatile.Write(ref _activeWatchdog, watchdog);

            // Cover mode marks the section as ours (its focus lines are not the user's). Foreground mode sends no focus
            // message, so a focus line now is the user's own switch, and the choke point refuses any focus message.
            var foregroundScope = session.Foreground ? choke.EnterForegroundInput() : null;
            if (!session.Foreground)
            {
                BeginOurInput();
            }

            ClickReport report;
            WatchdogStats stats = new(0, 0, 0, 0);
            try
            {
                watchdog.Start();
                RunClickTimeline(run);
            }
            catch (HardRuleViolationException ex)
            {
                run.Detail = ex.Code;
                ExecuteAbort(run, Trigger.HardRule, fromWatchdog: false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                run.Detail = "exception." + ex.GetType().Name;
                ExecuteAbort(run, Trigger.Exception, fromWatchdog: false);
            }
            finally
            {
                if (!session.CompletedNormally)
                {
                    ExecuteAbort(run, Trigger.Exception, fromWatchdog: false);
                }

                try
                {
                    stats = watchdog.Stop();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    run.Detail ??= "watchdog.stop";
                }

                Volatile.Write(ref _activeWatchdog, null);
                watchdog.Dispose();
                report = FinishClick(run, stats);
                if (foregroundScope is null)
                {
                    EndOurInput();
                }

                foregroundScope?.Dispose();
            }

            return (report, run);
        }
    }

    /// <summary>P1-P7 on the engine thread. Returns early whenever an abort was claimed (by the watchdog or here).</summary>
    private void RunClickTimeline(ClickRun run)
    {
        var s = run.Session;
        var choke = run.Choke;
        var hwnd = s.Game;
        var lp = SlideGeometry.MouseLParamChecked(s.Target, HomeRules.ExpectedClient);

        // ---- P1-P2: fake focus and RecoverFocus (cover mode). Foreground mode sends nothing (the game has real focus, R2);
        // its FocusSent mark only opens the engine's event window (SPEC 7.3b).
        s.Phase = InputPhase.P1Focus;
        run.Marks.FocusSent = _logWatch.Mark() ?? run.Mark0;
        run.Marks.FocusSentUtc = _clock.UtcNow;
        if (!s.Foreground && !FocusHandshake(run))
        {
            return;
        }

        // ---- P2: re-check cursor, cover (foreground mode: still in front, the game under the cursor, no user input), iconic
        s.Phase = InputPhase.P2FocusToSlide;
        if (!Continue(run))
        {
            return;
        }

        var recheck = RecheckBeforeSlide(s);
        if (recheck != Trigger.None)
        {
            ExecuteAbort(run, recheck, fromWatchdog: false);
            return;
        }

        // ---- P3: slide (0x0215), verify it landed and the game is not under the cursor
        s.Phase = InputPhase.P3SlideToDown;
        if (!s.TryClaimSlide())
        {
            return;
        }

        s.Expect = WindowExpect.HomeOrSlid;
        var t0 = _clock.Monotonic;
        s.MarkSlideStart(t0);
        bool slideOk;
        try
        {
            slideOk = choke.SetWindowPos(hwnd, 0, s.Slid.Left, s.Slid.Top, 0, 0, HardRules.SwpSlide, out _);
        }
        finally
        {
            s.MarkSlideCall(_clock.Monotonic - t0);
            s.SlideCallReturned = true;
            if (s.AbortClaimed && s.SlideBackClaimed)
            {
                // An abort slid back while our call was being issued: re-issue home so the final position is home.
                choke.SetWindowPos(hwnd, 0, s.Home.Left, s.Home.Top, 0, 0, HardRules.SwpSlideAsync, out _);
            }
        }

        if (s.SlideCall > HardRules.SlowSetWindowPos)
        {
            run.SlowSlideCall = true;
        }

        if (s.AbortClaimed)
        {
            return;
        }

        var landed = _win32.GetWindowRect(hwnd);
        if (!slideOk || landed != s.Slid || _win32.IsZoomed(hwnd) != s.HomeZoomed)
        {
            ExecuteAbort(run, Trigger.SlideDidNotLand, fromWatchdog: false);
            return;
        }

        s.Expect = WindowExpect.Slid;
        var topNow = TopAt(s.Cursor);
        if (s.Foreground ? topNow != hwnd : topNow == hwnd)
        {
            ExecuteAbort(run, s.Foreground ? Trigger.CoverChanged : Trigger.GameUnderCursor, fromWatchdog: false);
            return;
        }

        if (!Hold(run, HardRules.SlideSettle))
        {
            return;
        }

        // ---- P3: slid capture verification (worker, <= 250 ms) and the log popup gate
        var frame = CaptureSlid(run);
        if (s.AbortClaimed)
        {
            return;
        }

        if (frame is null)
        {
            run.VerifyCode = "captureFailed";
            ExecuteAbort(run, Trigger.SlidCaptureFailed, fromWatchdog: false);
            return;
        }

        var verifyCenter = run.Request.TemplateCenter ?? s.Target;
        var tolerance = _templates.Get(run.Request.TemplateId)?.EffectiveMotionTolerancePx ?? HardRules.AimTolerancePx;
        var verify = _vision.VerifyAt(frame, run.Request.TemplateId, verifyCenter, tolerance);
        run.SlidVerification = verify;
        if (verify.Found)
        {
            run.VerifyCode = "ok";
            if (run.Request.PatchVerify)
            {
                // Shadow evaluation of the off-screen anchor patch rule: journaled to calibrate it, never decides.
                run.Patch = PressPatchCheck(run, frame, verify, null);
            }
        }
        else if (verify.Failure is MatchFailure.NotClickable or MatchFailure.TemplateMissing)
        {
            run.VerifyCode = "notClickable";
            ExecuteAbort(run, Trigger.NotClickable, fromWatchdog: false);
            return;
        }
        else
        {
            // Cross-monitor slide (EVIDENCE c13): the capture cannot show the target. Only a fixed HUD navigation target
            // with the exact c13 signature may go ahead; every other check (located twice at home, popup gate, cursor and
            // cover, window watchdog, release and unfocus) still runs. An anchor + offset press whose anchor slid off the
            // drawn monitor may go ahead on its press patch (off-screen anchor patch rule, engine opt-in only), with every
            // other check unchanged too. Everything else aborts as before.
            var refusal = CrossMonitorSkipRefusal(run, frame, verifyCenter);
            if (refusal is not null)
            {
                run.NavFixedCode = refusal == NotCrossMonitor ? null : refusal;
                if (run.Request.PatchVerify)
                {
                    run.Patch = PressPatchCheck(run, frame, verify, verifyCenter);
                }

                if (run.Patch is not { Allowed: true })
                {
                    run.VerifyCode = "mismatch";
                    ExecuteAbort(run, Trigger.SlidTemplateMismatch, fromWatchdog: false);
                    return;
                }

                run.VerifyCode = SlidVerifyRules.PatchCode;
            }
            else
            {
                run.VerifyCode = SlidVerifyRules.SkippedCode;
                run.NavFixedCode = "allowed";
            }
        }

        if (LogWatch.PopupGateTripped(_logWatch.Since(run.Request.LocatedAtLog ?? run.Mark0), _ui))
        {
            ExecuteAbort(run, Trigger.LogGatePopup, fromWatchdog: false);
            return;
        }

        if (!Continue(run))
        {
            return;
        }

        if (run.Drag is { } drag)
        {
            RunDragTimeline(run, drag);
            return;
        }

        // ---- P3 -> P4: MOVE, 50 ms, watchdog check, DOWN (atomic gate)
        lock (s.MsgGate)
        {
            if (s.MessagesClosed || s.AbortClaimed)
            {
                return;
            }

            choke.Post(hwnd, HardRules.WM_MOUSEMOVE, 0, lp, out _);
        }

        if (!Hold(run, HardRules.MoveToDown))
        {
            return;
        }

        var actionMark = _logWatch.Mark();
        bool downOk;
        int downErr;
        lock (s.MsgGate)
        {
            if (s.MessagesClosed || s.AbortClaimed || !s.TryClaimDown())
            {
                return;
            }

            try
            {
                downOk = choke.Post(hwnd, HardRules.WM_LBUTTONDOWN, HardRules.MK_LBUTTON, lp, out downErr);
            }
            finally
            {
                s.MarkDownDone(_clock.Monotonic, _clock.UtcNow);
            }
        }

        run.Marks.ActionPosted = actionMark;
        run.DownOk = downOk;
        s.Phase = InputPhase.P4DownConsumption;
        if (!downOk)
        {
            ExecuteAbort(run, downErr == Win32Const.ERROR_ACCESS_DENIED ? Trigger.IntegrityMismatch : Trigger.Exception, fromWatchdog: false);
            return;
        }

        // ---- P4/P5: 70 ms, UP
        if (!Hold(run, HardRules.DownToUp))
        {
            return;
        }

        s.Phase = InputPhase.P5DownToUp;
        lock (s.MsgGate)
        {
            if (s.TryClaimUp())
            {
                choke.Post(hwnd, HardRules.WM_LBUTTONUP, 0, lp, out _);
                s.MarkUpPosted(_clock.UtcNow);
            }
        }

        // ---- P6: wait <= 400 ms (early exit on the expected UI, or the logged board press), unfocus WHILE SLID, wait <= 300 ms for LostFocuse
        s.Phase = InputPhase.P6UpToUnfocus;
        var since = actionMark ?? run.Marks.FocusSent;
        var upStart = _clock.Monotonic;
        while (_clock.Monotonic - upStart < HardRules.AfterUpWait)
        {
            var seen = _logWatch.Since(since);
            if (LogWatch.HasExpected(seen, run.Request.ExpectedUi))
            {
                run.ExpectedSeen = true;
                break;
            }

            if (run.Request.EndsOnGridClick && seen.OfType<GridClickLogEvent>().Any())
            {
                break;
            }

            if (!Hold(run, LogPollSlice))
            {
                return;
            }
        }

        if (!Continue(run))
        {
            return;
        }

        if (s.Foreground)
        {
            // no unfocus (R2), so no press block: the game reads DOWN and UP at the slid position before the window moves
            if (!DrainBeforeSlideBack(run))
            {
                return;
            }
        }
        else
        {
            run.Marks.UnfocusSent = _logWatch.Mark();
            lock (s.MsgGate)
            {
                if (s.MessagesClosed || s.AbortClaimed)
                {
                    return;
                }

                s.MarkUnfocus(_clock.UtcNow);
                var (skipped, _) = SendFocusOff(choke);
                s.UnfocusSkipped = skipped;
                s.UnfocusSentWhileSlid = !skipped;
                s.UnfocusDone = true;
            }
        }

        if (!s.Foreground && !s.UnfocusSkipped)
        {
            var pbStart = _clock.Monotonic;
            while (_clock.Monotonic - pbStart < HardRules.PressBlockWait)
            {
                var pb = LogWatch.LastPressBlock(_logWatch.Since(run.Marks.UnfocusSent));
                if (pb is not null)
                {
                    run.PressBlock = pb;
                    break;
                }

                if (!Hold(run, LogPollSlice))
                {
                    return;
                }
            }
        }

        if (!run.ExpectedSeen)
        {
            run.ExpectedSeen = LogWatch.HasExpected(_logWatch.Since(since), run.Request.ExpectedUi);
        }

        // ---- P7: slide back (0x0215); verification, marker and journal happen in FinishClick
        if (s.AbortClaimed)
        {
            return;
        }

        if (s.TryClaimSlideBack())
        {
            s.Expect = WindowExpect.SlidOrHome;
            var tb = _clock.Monotonic;
            choke.SetWindowPos(hwnd, 0, s.Home.Left, s.Home.Top, 0, 0, HardRules.SwpSlide, out _);
            s.MarkSlideBack(_clock.Monotonic - tb, _clock.Monotonic, _clock.UtcNow);
        }

        s.Finished = true;
        s.Phase = InputPhase.P7AfterSlideBack;
        s.CompletedNormally = true;
    }

    /// <summary>
    /// Null when the cross-monitor NavFixed rule (SPEC 7.3, EVIDENCE c13) lets the press go ahead after a failed slid
    /// check: the slide is cross-monitor, the engine opted in, the catalog confirms a fixed navigation target (verified,
    /// not commit, not gated, no anchor, no grid offset) and the slid capture shows the c13 gap at the target. Otherwise
    /// the refusal code (<see cref="NotCrossMonitor"/> for a same-monitor slide).
    /// </summary>
    private string? CrossMonitorSkipRefusal(ClickRun run, Frame frame, PointI verifyCenter)
    {
        if (!SlidVerifyRules.IsCrossMonitor(run.Geometry.Plan))
        {
            return NotCrossMonitor;
        }

        try
        {
            var id = run.Request.TemplateId;
            var refusal = SlidVerifyRules.NavFixedRefusal(run.Request, _templates.Get(id), _templates.IsClickable(id));
            return refusal ?? SlidVerifyRules.CaptureGapRefusal(
                frame.Bgra, HomeRules.ExpectedClient, verifyCenter, frame.ClientOriginOnScreen, run.Geometry.HomeMonitorBounds, run.Geometry.SlidMonitorBounds);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "rule.exception";
        }
    }

    /// <summary>
    /// P0 part of the off-screen anchor patch rule (SPEC 7.3): null when the engine's evidence holds up; otherwise the
    /// refusal code. The request must qualify (<see cref="SlidVerifyRules.PatchRequestRefusal"/>, GameIO's own catalog
    /// check); the located frame must be one of GameIO's own home captures, the one the request names
    /// (LocatedAtMonotonic, LocatedAtLog), and show the anchor at TemplateCenter. Runs before anything is sent.
    /// </summary>
    private string? LocatedFrameRefusal(ClickRequest request)
    {
        try
        {
            var id = request.TemplateId;
            var definition = _templates.Get(id);
            var refusal = SlidVerifyRules.PatchRequestRefusal(request, definition, _templates.IsClickable(id));
            if (refusal is not null)
            {
                return refusal;
            }

            var located = request.LocatedFrame!;
            if (!_ownCaptures.TryGetValue(located, out _))
            {
                return "located.notOurs";
            }

            if (located.WhileSlid)
            {
                return "located.slid";
            }

            if (located.CapturedAtMonotonic != request.LocatedAtMonotonic || !Equals(located.LogAt, request.LocatedAtLog))
            {
                return "located.mismatch";
            }

            // The very frame the engine located the anchor in: it must be found at TemplateCenter itself (aim tolerance).
            var found = _vision.VerifyAt(located, id, request.TemplateCenter!.Value, HardRules.AimTolerancePx);
            return found.Found ? null : "located.anchorMissing";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "rule.exception";
        }
    }

    /// <summary>
    /// The off-screen anchor patch rule on the slid capture (SPEC 7.3), first failure wins: the P0 located-frame result;
    /// then, for a failed slid check (<paramref name="verifyCenter"/> given), the failure kind (only a template that is
    /// not visible may fall back) and the anchor hidden off the drawn monitor; then the press patch, slid against located.
    /// With <paramref name="verifyCenter"/> null it is the shadow evaluation of a verified press.
    /// </summary>
    private PatchVerdict PressPatchCheck(ClickRun run, Frame slid, MatchResult verify, PointI? verifyCenter)
    {
        try
        {
            if (run.LocatedRefusal is { } locatedCode)
            {
                return PatchVerdict.Refused(locatedCode);
            }

            var drawn = run.Geometry.SlidMonitorBounds;
            if (verifyCenter is { } center)
            {
                var failure = verify.Failure switch
                {
                    MatchFailure.BelowThreshold or MatchFailure.FrameInvalid => null,
                    MatchFailure.OutsideRadius => "verify.outsideRadius",
                    MatchFailure.LowMargin => "verify.lowMargin",
                    _ => "verify.unexpected",
                };
                var hidden = failure ?? SlidVerifyRules.AnchorHiddenRefusal(slid.Bgra, HomeRules.ExpectedClient, center, slid.ClientOriginOnScreen, drawn, _templates.SizeOf(run.Request.TemplateId));
                if (hidden is not null)
                {
                    return PatchVerdict.Refused(hidden);
                }
            }

            var plan = run.Geometry.Plan;
            return SlidVerifyRules.PressPatchVerdict(
                slid, run.Request.LocatedFrame!, run.Session.Target, run.Session.Cursor, drawn, new PointI(plan.Dx, plan.Dy), HardRules.MaxLocateAge,
                run.Drag is null ? SlidVerifyRules.PatchSide : SlidVerifyRules.DragPatchSide);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return PatchVerdict.Refused("rule.exception");
        }
    }

    /// <summary>
    /// P2 re-check: cursor unchanged, cover (same HWND and frame), not foreground, not iconic, no stop. Foreground mode
    /// (SPEC 7.3b): the game still the foreground window and under the cursor, and no user input since readiness.
    /// </summary>
    private Trigger RecheckBeforeSlide(InputSession s)
    {
        if (!_win32.TryGetCursorPos(out var c))
        {
            return Trigger.CursorLost;
        }

        if (c != s.Cursor)
        {
            s.MovedBeforeDown = true;
            return Trigger.CursorMoved;
        }

        if (s.Foreground)
        {
            if (_win32.GetForegroundWindow() != s.Game)
            {
                return Trigger.ForegroundLost;
            }

            if (s.InputTickBaseline is not { } baseline || _win32.GetLastInputTick() != baseline)
            {
                s.UserInputSeen = true;
                return Trigger.UserInput;
            }
        }
        else if (IsPidForeground(s.GamePid, s.Game))
        {
            return Trigger.Foreground;
        }

        if (_win32.IsIconic(s.Game))
        {
            return Trigger.Iconic;
        }

        if (s.Foreground ? TopAt(c) != s.Game : TopAt(c) != s.CoverHwnd || _win32.GetFrameBounds(s.CoverHwnd) != s.CoverFrame)
        {
            return Trigger.CoverChanged;
        }

        if (_stop.IsStopRequested)
        {
            return Trigger.Stop;
        }

        return Trigger.None;
    }

    /// <summary>
    /// Cover mode P1-P2: the fake focus (each send checked), then RecoverFocus logged within 1 s with at least 300 ms
    /// elapsed. False when the run ended (an abort was claimed).
    /// </summary>
    private bool FocusHandshake(ClickRun run)
    {
        var s = run.Session;
        var focusStart = _clock.Monotonic;
        foreach (var m in HardRules.FocusOnSequence)
        {
            SendOutcome r;
            lock (s.MsgGate)
            {
                if (s.MessagesClosed || s.AbortClaimed)
                {
                    return false;
                }

                s.IncFocusSent();
                r = run.Choke.Send(s.Game, m.Message, m.WParam, m.LParam);
            }

            if (r == SendOutcome.AccessDenied)
            {
                ExecuteAbort(run, Trigger.IntegrityMismatch, fromWatchdog: false);
                return false;
            }

            if (r != SendOutcome.Ok)
            {
                ExecuteAbort(run, Trigger.SendTimeout, fromWatchdog: false);
                return false;
            }
        }

        s.Phase = InputPhase.P2FocusToSlide;
        while (true)
        {
            if (!Continue(run))
            {
                return false;
            }

            var elapsed = _clock.Monotonic - focusStart;
            if (!run.FocusLogged)
            {
                run.FocusLogged = LogWatch.HasRecoverFocus(_logWatch.Since(run.Marks.FocusSent));
            }

            if ((run.FocusLogged && elapsed >= HardRules.FocusSettle) || elapsed >= HardRules.RecoverFocusLogWait)
            {
                break;
            }

            if (!Hold(run, LogPollSlice))
            {
                return false;
            }
        }

        if (!run.FocusLogged)
        {
            ExecuteAbort(run, Trigger.FocusFailed, fromWatchdog: false);
            return false;
        }

        return true;
    }

    /// <summary>
    /// The drain (SPEC 7.3b): a WM_NULL round trip (the game's pump has begun: a thread is handed its sent messages before
    /// its posted ones, so our earlier DOWN and UP are read in this pump), <see cref="HardRules.ForegroundDrainSettle"/>, and
    /// a second round trip, answered only at a later pump, after the frame that read DOWN and UP ran its input at the slid
    /// position (a button and the slide-back read in one frame hit-test at home, EVIDENCE c6). Sets
    /// <see cref="InputSession.Drained"/>. Only a timeout (a game that does not pump) is waited out, up to
    /// <paramref name="limit"/> or until <paramref name="wait"/> says stop; a window that is gone ends it at once (its queued
    /// messages died with it), and any other failure gives up at once.
    /// </summary>
    private DrainResult DrainCore(ClickRun run, TimeSpan limit, Func<TimeSpan, bool> wait)
    {
        var start = _clock.Monotonic;
        for (var trip = 0; trip < 2; trip++)
        {
            while (true)
            {
                var r = run.Choke.Send(run.Session.Game, HardRules.WM_NULL, 0, 0);
                if (r == SendOutcome.Ok)
                {
                    break;
                }

                if (!_win32.IsWindow(run.Session.Game))
                {
                    return DrainResult.Gone;
                }

                if (r != SendOutcome.Timeout || _clock.Monotonic - start >= limit || !wait(LogPollSlice))
                {
                    return DrainResult.GaveUp;
                }
            }

            if (trip == 0 && !wait(HardRules.ForegroundDrainSettle))
            {
                return DrainResult.GaveUp;
            }
        }

        run.Session.Drained = true;
        return DrainResult.Drained;
    }

    /// <summary>
    /// Foreground mode, before the window moves home: the drain (<see cref="DrainCore"/>), bounded by
    /// <see cref="HardRules.ForegroundDrainMax"/>. A game that does not answer in time is handed to the abort's own drain,
    /// which waits much longer (the watchdog's dead-man normally gets there first). False when the run ended.
    /// </summary>
    private bool DrainBeforeSlideBack(ClickRun run)
    {
        var result = DrainCore(run, HardRules.ForegroundDrainMax, d => Hold(run, d));
        if (result == DrainResult.Drained)
        {
            return Continue(run);
        }

        if (Continue(run))
        {
            run.Detail ??= result == DrainResult.Gone ? "drain.gameGone" : "drain.slow";
            ExecuteAbort(run, result == DrainResult.Gone ? Trigger.IdentityChanged : Trigger.DeadMan, fromWatchdog: false);
        }

        return false;
    }

    /// <summary>
    /// The foreground-mode abort's drain (<see cref="AbortStep.Drain"/>, and FinishClick's safety net): never a window move
    /// while our DOWN/UP may be unread. Waits up to <see cref="HardRules.ForegroundDrainHangMax"/> for a game that stopped
    /// responding; past it the window may go home (R3) and the input ends as an incident (HALT).
    /// </summary>
    private void DrainForAbort(ClickRun run)
    {
        var s = run.Session;
        if (s.Drained || s.DrainGaveUp)
        {
            return;
        }

        var result = DrainCore(run, HardRules.ForegroundDrainHangMax, d =>
        {
            _clock.Sleep(d, CancellationToken.None);
            return true;
        });
        if (result == DrainResult.Gone)
        {
            s.DrainGone = true;
            run.Detail ??= "drain.gameGone";
        }
        else if (result == DrainResult.GaveUp)
        {
            s.DrainGaveUp = true;
            run.Detail ??= "drain.timeout";
        }
    }

    /// <summary>How a drain ended.</summary>
    private enum DrainResult
    {
        /// <summary>Both round trips answered: the game read our button messages at the slid position.</summary>
        Drained,
        /// <summary>The limit passed, the run ended, or a send failed otherwise: the messages may still be unread.</summary>
        GaveUp,
        /// <summary>The game window is gone: its queue died with it, nothing can be pressed.</summary>
        Gone,
    }

    /// <summary>True to continue: no abort claimed and not cancelled (a cancellation claims a Stop-like abort).</summary>
    private bool Continue(ClickRun run)
    {
        if (run.Session.AbortClaimed)
        {
            return false;
        }

        if (run.Ct.IsCancellationRequested)
        {
            ExecuteAbort(run, Trigger.Cancelled, fromWatchdog: false);
            return false;
        }

        return true;
    }

    /// <summary>Sleeps in slices (IClock), returning false as soon as an abort was claimed or the call was cancelled.</summary>
    private bool Hold(ClickRun run, TimeSpan duration)
    {
        var end = _clock.Monotonic + duration;
        while (true)
        {
            if (!Continue(run))
            {
                return false;
            }

            var left = end - _clock.Monotonic;
            if (left <= TimeSpan.Zero)
            {
                return true;
            }

            _clock.Sleep(left < PollSlice ? left : PollSlice, CancellationToken.None);
        }
    }

    /// <summary>PrintWindow while slid on a worker thread with the 250 ms budget; null on failure or timeout.</summary>
    private Frame? CaptureSlid(ClickRun run)
    {
        var s = run.Session;
        var origin = _win32.GetClientOrigin(s.Game);
        var client = _win32.GetClientSize(s.Game);
        if (origin is null || client != HomeRules.ExpectedClient)
        {
            return null;
        }

        var (bytes, _) = PrintWithTimeout(run.Choke, s.Slid, origin.Value, HomeRules.ExpectedClient);
        if (bytes is null)
        {
            return null;
        }

        return new Frame(HardRules.ExpectedClientWidth, HardRules.ExpectedClientHeight, HardRules.ExpectedClientWidth * 4, bytes, _clock.Monotonic, _clock.UtcNow, null, origin.Value, WhileSlid: true);
    }

    /// <summary>Runs PrintWindow on a worker with <see cref="HardRules.PrintWindowTimeout"/>.</summary>
    private static (byte[]? Bytes, bool TimedOut) PrintWithTimeout(ChokePoint choke, RectI windowRect, PointI clientOrigin, SizeI client)
    {
        var task = Task.Run(() => choke.PrintClient(choke.Game, HardRules.PW_RENDERFULLCONTENT, windowRect, clientOrigin, client));
        try
        {
            if (!task.Wait(HardRules.PrintWindowTimeout))
            {
                return (null, true);
            }

            return (task.Result, false);
        }
        catch (AggregateException)
        {
            return (null, false);
        }
    }

    /// <summary>
    /// Runs the abort for <paramref name="trigger"/> exactly once (first claimer wins). The DOWN and slide gates are
    /// closed atomically first, so the plan knows for sure whether DOWN was posted and whether the window moved.
    /// </summary>
    private void ExecuteAbort(ClickRun run, Trigger trigger, bool fromWatchdog)
    {
        var s = run.Session;
        if (!s.TryClaimAbort(trigger))
        {
            return;
        }

        var downPosted = s.CloseDownGate();
        if (downPosted)
        {
            s.WaitDownDone(TimeSpan.FromMilliseconds(50));
        }

        var slid = s.CloseSlideGate();
        var plan = AbortPlanner.Plan(trigger, downPosted, !s.UpClaimed, slid, s.FocusMessagesSent > 0, releaseFirst: s.IsDrag, foreground: s.Foreground);
        s.SetAbortPlan(plan);
        foreach (var step in plan)
        {
            try
            {
                RunAbortStep(run, step, trigger, fromWatchdog);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Never stop an abort sequence half-way: the next steps (slide back, unfocus) still run.
                run.AbortStepFailures++;
            }
        }
    }

    private void RunAbortStep(ClickRun run, AbortStep step, Trigger trigger, bool fromWatchdog)
    {
        var s = run.Session;
        var choke = run.Choke;
        switch (step)
        {
            case AbortStep.PostUp:
                PostPendingUp(s, choke);
                break;

            case AbortStep.Wait20:
                _clock.Sleep(TimeSpan.FromMilliseconds(20), CancellationToken.None);
                break;

            case AbortStep.Drain:
                DrainForAbort(run);
                break;

            case AbortStep.SlideBack:
            case AbortStep.SlideBackAsync:
                if (s.Foreground)
                {
                    if (s.MoveBlocked)
                    {
                        DrainForAbort(run); // a Drain step that failed must not let the window move early
                    }

                    if (s.MoveBlocked || s.DrainGone || _win32.IsIconic(s.Game))
                    {
                        break; // still unread, gone, or minimized (a move would land on its minimized placement)
                    }
                }

                if (s.SlideAttempted && s.TryClaimSlideBack())
                {
                    var flags = step == AbortStep.SlideBackAsync || fromWatchdog ? HardRules.SwpSlideAsync : HardRules.SwpSlide;
                    s.Expect = WindowExpect.SlidOrHome;
                    var t0 = _clock.Monotonic;
                    choke.SetWindowPos(s.Game, 0, s.Home.Left, s.Home.Top, 0, 0, flags, out _);
                    s.MarkSlideBack(_clock.Monotonic - t0, _clock.Monotonic, _clock.UtcNow);
                    s.ForcedSlideBack = fromWatchdog || trigger is Trigger.DeadMan or Trigger.SendTimeout or Trigger.MovedBeforeUp;
                }

                break;

            case AbortStep.Unfocus:
                lock (s.MsgGate)
                {
                    s.MessagesClosed = true;
                    if (s.FocusMessagesSent > 0 && !s.UnfocusDone)
                    {
                        var whileSlid = s.SlideAttempted && !s.SlideBackClaimed;
                        s.MarkUnfocus(_clock.UtcNow);
                        s.UnfocusDone = true;
                        var (skipped, _) = SendFocusOff(choke);
                        s.UnfocusSkipped |= skipped;
                        s.UnfocusSentWhileSlid |= whileSlid && !skipped;
                    }
                }

                break;

            case AbortStep.UnfocusNotify:
                s.MessagesClosed = true;
                if (s.FocusMessagesSent > 0 && !s.UnfocusDone)
                {
                    var whileSlid = s.SlideAttempted && !s.SlideBackClaimed;
                    s.MarkUnfocus(_clock.UtcNow);
                    s.UnfocusDone = true;
                    var skipped = SendFocusOffNotify(choke);
                    s.UnfocusSkipped |= skipped;
                    s.UnfocusSentWhileSlid |= whileSlid && !skipped;
                }

                break;

            case AbortStep.RestoreLadder:
                if (s.Foreground && (s.MoveBlocked || s.DrainGone))
                {
                    break; // our press may still be unread (the drain failed), or the game is gone: no move
                }

                s.Expect = WindowExpect.Any;
                s.SetAbortRestore(RunLadder(choke, s.Home, s.HomeZoomed, fromWatchdog, null));
                break;
        }
    }

    /// <summary>
    /// Posts the pending LBUTTONUP once (claim), at the client point under the cursor NOW (a drag's current step). Taken
    /// under the message gate when it is free within 50 ms, so a drag step's MOUSEMOVE never follows the release; the
    /// engine thread holds that gate only around non-blocking posts while UP is pending.
    /// </summary>
    private void PostPendingUp(InputSession s, ChokePoint choke)
    {
        if (s.UpClaimed)
        {
            return;
        }

        var locked = Monitor.TryEnter(s.MsgGate, TimeSpan.FromMilliseconds(50));
        try
        {
            if (s.TryClaimUp())
            {
                choke.Post(s.Game, HardRules.WM_LBUTTONUP, 0, HardRules.MouseLParam(s.CurrentTarget), out _);
                s.MarkUpPosted(_clock.UtcNow);
            }
        }
        finally
        {
            if (locked)
            {
                Monitor.Exit(s.MsgGate);
            }
        }
    }

    /// <summary>
    /// Ends the section on the engine thread: safety nets (UP after DOWN, slide-back, unfocus), home verification and
    /// the restore ladder, marker deletion, prefs comparison, then (window back home) the journal records.
    /// </summary>
    private ClickReport FinishClick(ClickRun run, WatchdogStats stats)
    {
        var s = run.Session;
        var choke = run.Choke;
        var trigger = s.Trigger;
        bool iconic;
        try
        {
            iconic = trigger == Trigger.Iconic || _win32.IsIconic(s.Game);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            iconic = trigger == Trigger.Iconic;
        }

        // Safety nets: none of these should be needed after a correct abort, but a pending one is always closed.
        try
        {
            if (s.DownDone && s.TryClaimUp())
            {
                choke.Post(s.Game, HardRules.WM_LBUTTONUP, 0, HardRules.MouseLParam(s.CurrentTarget), out _);
                s.MarkUpPosted(_clock.UtcNow);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            run.Detail ??= "finish.up";
        }

        try
        {
            if (!iconic && s.SlideAttempted && s.MoveBlocked)
            {
                // foreground mode: our DOWN/UP may be unread; the window moves only after the drain (SPEC 7.3b)
                DrainForAbort(run);
            }

            if (!iconic && !s.DrainGone && !s.MoveBlocked && s.SlideAttempted && s.TryClaimSlideBack())
            {
                s.Expect = WindowExpect.SlidOrHome;
                var t0 = _clock.Monotonic;
                choke.SetWindowPos(s.Game, 0, s.Home.Left, s.Home.Top, 0, 0, HardRules.SwpSlide, out _);
                s.MarkSlideBack(_clock.Monotonic - t0, _clock.Monotonic, _clock.UtcNow);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            run.Detail ??= "finish.slideBack";
        }

        try
        {
            if (s.FocusMessagesSent > 0 && !s.UnfocusDone && trigger is not (Trigger.Foreground or Trigger.Iconic))
            {
                lock (s.MsgGate)
                {
                    s.MessagesClosed = true;
                    s.MarkUnfocus(_clock.UtcNow);
                    s.UnfocusDone = true;
                    var (skipped, _) = SendFocusOff(choke);
                    s.UnfocusSkipped |= skipped;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            run.Detail ??= "finish.unfocus";
        }

        // Home verification and the ladder.
        var backAtHome = false;
        var ladder = s.AbortRestore;
        try
        {
            if (!iconic && !s.DrainGone)
            {
                backAtHome = VerifyHome(s.Game, s.Home, s.HomeZoomed, VerifyWindow);
                if (!backAtHome && !s.MoveBlocked && (ladder is null || ladder.Success))
                {
                    ladder = RunLadder(choke, s.Home, s.HomeZoomed, fromWatchdog: false, observed: null);
                    backAtHome = ladder.Success;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            run.Detail ??= "finish.verify";
        }

        var now = _clock.Monotonic;
        var displaced = s.SlideStart is { } st && s.SlideAttempted ? now - st : TimeSpan.Zero;
        if (!s.SlideAttempted || backAtHome || s.DrainGone)
        {
            _marker.TryDelete();
            lock (_state)
            {
                _ourDeviation = null;
            }
        }
        else
        {
            var observed = SafeRect(s.Game);
            lock (_state)
            {
                _ourDeviation = new OurDeviation(s.Game, s.Home, s.Slid, observed is { } o && SlideGeometry.SizeChanged(s.Home, o));
            }
        }

        // Disposition: the game was touched by the focus messages, or in foreground mode by the slide.
        var downPosted = s.DownClaimed;
        var touched = s.FocusMessagesSent > 0 || (s.Foreground && s.SlideAttempted);
        var disposition = s.CompletedNormally && trigger == Trigger.None
            ? InputDisposition.Sent
            : AbortPlanner.DispositionOf(trigger, downPosted, touched);
        WindowIncident? incident = null;
        if (trigger == Trigger.ClipChanged)
        {
            incident = new WindowIncident(IncidentKind.CursorClip, s.Home, SafeRect(s.Game), backAtHome, ladder?.Rung ?? RestoreRung.None, "clip.changed");
            disposition = InputDisposition.Incident;
        }

        if (ladder?.Incident is { } li)
        {
            incident = li;
            disposition = InputDisposition.Incident;
        }
        else if (!iconic && s.SlideAttempted && !backAtHome)
        {
            incident = new WindowIncident(IncidentKind.RestoreFailed, s.Home, SafeRect(s.Game), false, RestoreRung.Failed, "home.notVerified");
            disposition = InputDisposition.Incident;
        }

        if (s.DrainGaveUp)
        {
            // foreground mode: the game stopped responding with our press unread; the window went home anyway (R3), so the
            // press may land at home when it wakes up: never a retry
            incident = new WindowIncident(IncidentKind.UndrainedSlideBack, s.Home, SafeRect(s.Game), backAtHome, ladder?.Rung ?? RestoreRung.None, "drain.timeout");
            disposition = InputDisposition.Incident;
        }

        if (s.DrainGone)
        {
            // foreground mode: the game window went away during the drain (a crash): nothing to restore or press
            incident = null;
            disposition = InputDisposition.GameGone;
        }

        // Prefs after every slide (never written by us).
        string? prefsDiff = null;
        try
        {
            GameWindowPrefs? baseline;
            lock (_state)
            {
                baseline = _prefsBaseline;
            }

            if (s.SlideAttempted && baseline is not null)
            {
                prefsDiff = GamePrefsReader.Difference(baseline, GamePrefsReader.Read(_win32));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            prefsDiff = null;
        }

        run.Marks.End = _logWatch.TryMark();
        var phase = s.CompletedNormally ? InputPhase.Completed : s.Phase;
        var abort = s.CompletedNormally && trigger == Trigger.None ? AbortReason.None : AbortPlanner.ReasonOf(trigger);
        var marks = new InputMarks(
            run.Marks.FocusSent,
            run.Marks.FocusSentUtc,
            run.Marks.ActionPosted,
            s.ActionPostedUtc,
            s.ReleasePostedUtc,
            run.Marks.UnfocusSent,
            s.UnfocusSentUtc,
            s.SlideBackUtc,
            run.Marks.End);
        var cursor = new CursorEvidence(s.Cursor, s.MovedBeforeDown, s.MovedInP4, s.MovedAfterP4, s.UserButtonSeen, s.ClipChangedSeen, s.UserInputSeen);
        SlideEvidence? slide = s.SlideAttempted
            ? new SlideEvidence(run.Geometry.Plan, run.Geometry.CoverClass, displaced, s.SlideCall, s.SlideBackCall, s.ForcedSlideBack, backAtHome, stats)
            : null;

        // Journal only now: the window is back home (or the incident is final). Nothing was written while displaced.
        var drag = run.Drag;
        Journal(
            JournalKind.Input,
            run.Request.StepId,
            ("input", drag is null ? "click" : "drag"),
            ("mode", s.Mode.ToString()),
            ("template", run.Request.TemplateId),
            ("dragSteps", drag?.Steps),
            ("dragStepsDone", drag is null ? null : run.StepsDone),
            ("dragDx", drag?.Delta.X),
            ("dragDy", drag?.Delta.Y),
            ("disposition", disposition.ToString()),
            ("abort", abort.ToString()),
            ("trigger", trigger.ToString()),
            ("phase", phase.ToString()),
            ("focusSent", s.FocusMessagesSent),
            ("focusLogged", run.FocusLogged),
            ("down", run.DownOk),
            ("up", s.UpClaimed),
            ("unfocusSlid", s.UnfocusSentWhileSlid),
            ("unfocusSkipped", s.UnfocusSkipped),
            ("expectedSeen", run.ExpectedSeen),
            ("pressBlock", run.PressBlock is not null),
            ("drained", s.Foreground ? s.Drained : (bool?)null),
            ("userInput", s.Foreground ? s.UserInputSeen : (bool?)null),
            ("userButton", s.UserButtonSeen),
            ("verify", run.VerifyCode),
            ("navFixed", run.NavFixedCode),
            ("patch", run.Patch?.Code ?? run.LocatedRefusal),
            ("patchScore", Rounded(run.Patch?.Score, 3)),
            ("patchStd", Rounded(run.Patch?.MinStd, 1)),
            ("patchDelta", Rounded(run.Patch?.MeanDelta, 1)),
            ("patchAgeMs", run.Patch?.AgeMs),
            ("clip", s.ClipDetail),
            ("abortPlan", string.Join("+", s.AbortPlan)),
            ("detail", run.Detail));
        if (slide is not null)
        {
            Journal(
                JournalKind.Slide,
                run.Request.StepId,
                ("mode", s.Mode.ToString()),
                ("dx", slide.Plan.Dx),
                ("dy", slide.Plan.Dy),
                ("coverClass", slide.CoverClass),
                ("exposedPx", slide.Plan.ExposedAreaPx),
                ("crossMonitor", !string.Equals(slide.Plan.SlidMonitor, slide.Plan.HomeMonitor, StringComparison.Ordinal)),
                ("slidMonitor", slide.Plan.SlidMonitor),
                ("displacedMs", slide.Displaced),
                ("overBudget", slide.Displaced > HardRules.DisplacementBudget + (s.DeadMan - HardRules.DeadManDeadline)),
                ("slideCallMs", slide.SlideCall),
                ("slideBackCallMs", slide.SlideBackCall),
                ("forced", slide.ForcedSlideBack),
                ("backAtHome", slide.BackAtHome),
                ("wdSamples", stats.Samples),
                ("wdP50Ms", stats.P50Ms),
                ("wdP99Ms", stats.P99Ms),
                ("wdMaxGapMs", stats.MaxGapMs));
        }

        if (run.SlowSlideCall)
        {
            Journal(JournalKind.Incident, run.Request.StepId, ("kind", nameof(IncidentKind.SlowSetWindowPos)), ("callMs", s.SlideCall));
        }

        if (run.SlowestStepCall > HardRules.SlowSetWindowPos)
        {
            Journal(JournalKind.Incident, run.Request.StepId, ("kind", nameof(IncidentKind.SlowSetWindowPos)), ("stepCallMs", run.SlowestStepCall));
        }

        if (incident is not null)
        {
            Journal(JournalKind.Incident, run.Request.StepId, ("kind", incident.Kind.ToString()), ("restored", incident.Restored), ("rung", incident.Rung.ToString()), ("detail", incident.DetailCode));
        }

        if (prefsDiff is not null)
        {
            Journal(JournalKind.Incident, run.Request.StepId, ("kind", nameof(IncidentKind.PrefsChanged)), ("detail", prefsDiff));
        }

        return new ClickReport(
            disposition,
            abort,
            phase,
            s.FocusMessagesSent,
            run.FocusLogged,
            run.DownOk,
            s.UpClaimed,
            s.UnfocusSentWhileSlid,
            s.UnfocusSkipped,
            marks,
            cursor,
            slide,
            run.SlidVerification,
            run.ExpectedSeen,
            run.PressBlock,
            incident,
            s.Mode);
    }

    /// <summary>A measured journal number rounded to <paramref name="digits"/>, or null when it was not measured.</summary>
    private static double? Rounded(double? value, int digits) => value is { } v ? Math.Round(v, digits) : null;

    private RectI? SafeRect(long hwnd)
    {
        try
        {
            return _win32.GetWindowRect(hwnd);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>A click (or drag) that sent nothing (zero messages): journaled and reported.</summary>
    private ClickReport EarlyClick(
        ClickRequest request, InputDisposition disposition, AbortReason abort, PointI? cursor, string? detail, DragPlan? drag = null, InputMode mode = InputMode.Cover)
    {
        Journal(
            JournalKind.Input,
            request.StepId,
            ("input", drag is null ? "click" : "drag"),
            ("mode", mode.ToString()),
            ("template", request.TemplateId),
            ("disposition", disposition.ToString()),
            ("abort", abort.ToString()),
            ("phase", nameof(InputPhase.P0Ready)),
            ("focusSent", 0),
            ("detail", detail));
        var marks = new InputMarks(null, null, null, null, null, null, null, null, null);
        var ev = new CursorEvidence(cursor ?? default, false, false, false, false, false);
        return new ClickReport(disposition, abort, InputPhase.P0Ready, 0, false, false, false, false, false, marks, ev, null, null, false, null, null, mode);
    }

    /// <summary>Mutable state of one slide click on the engine thread.</summary>
    private sealed class ClickRun(
        ClickRequest request,
        GameIdentity identity,
        ChokePoint choke,
        InputSession session,
        ClickGeometry geometry,
        LogPosition mark0,
        CancellationToken ct)
    {
        public ClickRequest Request { get; } = request;

        public GameIdentity Identity { get; } = identity;

        public ChokePoint Choke { get; } = choke;

        public InputSession Session { get; } = session;

        public ClickGeometry Geometry { get; } = geometry;

        public LogPosition Mark0 { get; } = mark0;

        public CancellationToken Ct { get; } = ct;

        public MarkSet Marks { get; } = new();

        public bool FocusLogged { get; set; }

        public bool DownOk { get; set; }

        public bool ExpectedSeen { get; set; }

        public bool SlowSlideCall { get; set; }

        public MatchResult? SlidVerification { get; set; }

        /// <summary>
        /// Slid check outcome for the journal: ok, skipped.crossMonitor.navFixed, patch.anchorOffscreen, mismatch,
        /// notClickable, captureFailed.
        /// </summary>
        public string? VerifyCode { get; set; }

        /// <summary>Cross-monitor NavFixed rule: allowed, or why it did not apply to a cross-monitor mismatch.</summary>
        public string? NavFixedCode { get; set; }

        /// <summary>
        /// Off-screen anchor patch rule, P0 part: why the engine's located frame does not qualify (null = it does). Journaled
        /// as <c>patch</c> also when the run ends before the slid check.
        /// </summary>
        public string? LocatedRefusal { get; init; }

        /// <summary>
        /// Off-screen anchor patch rule verdict (engine opt-in only): the decision after a failed slid check, or the shadow
        /// evaluation of a verified press (journal only). Null when not evaluated.
        /// </summary>
        public PatchVerdict? Patch { get; set; }

        public PressBlockLogEvent? PressBlock { get; set; }

        public string? Detail { get; set; }

        public int AbortStepFailures { get; set; }

        /// <summary>The drag plan (SlideDrag), or null for a click.</summary>
        public DragPlan? Drag { get; init; }

        /// <summary>Drag steps whose move landed and whose MOUSEMOVE was posted.</summary>
        public int StepsDone { get; set; }

        /// <summary>Slowest drag step SetWindowPos call (a slow one is an incident record, F26).</summary>
        public TimeSpan SlowestStepCall { get; set; }
    }
}
