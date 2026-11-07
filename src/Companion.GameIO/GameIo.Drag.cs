using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.GameIO.Geometry;
using Companion.GameIO.Input;
using Companion.GameIO.Native;

namespace Companion.GameIO;

/// <summary>
/// Slide drag (SPEC 7.3a, port of proofs\p0_c14_drag_scroll.ps1: the recipe list scrolled about 300 px, the window came
/// back home and the game was never foreground). The game window slides behind the cover so the press point lies under
/// the resting cursor; the button goes down there; then, step by step, the WINDOW moves by the step (the real cursor never
/// moves, so the client point under it advances) and WM_MOUSEMOVE(MK_LBUTTON) is posted at the moved client point; the
/// pointer holds still (no fling), the button is released, the game is unfocused while slid and the window slides home.
/// Readiness, pacing (one batch, one slide, one focus pair), marker, watchdog, slid capture check and popup gate are the
/// slide click's (<see cref="SlideCore"/>); cursor movement, a user button or any incident before the release ends the
/// drag with the button released FIRST, then the slide back. Foreground mode (SPEC 7.3b): the game must stay under the
/// cursor at every step, and instead of the unfocus the game is drained before the window moves home.
/// </summary>
public sealed partial class GameIo
{
    /// <inheritdoc />
    public DragReport SlideDrag(DragRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var asClick = new ClickRequest(
            request.StepId, request.TemplateId, request.Start, FireOn.Up, request.LocatedAtLog, request.LocatedAtMonotonic, [],
            request.NearDenyZone, request.TemplateCenter, NavFixed: false, LocatedFrame: request.LocatedFrame, PatchVerify: request.PatchVerify);
        var plan = DragPlan.Create(request.Start, request.Delta);
        var clientRect = new RectI(0, 0, HardRules.ExpectedClientWidth, HardRules.ExpectedClientHeight);
        if (plan is null || !clientRect.Contains(request.TemplateCenter))
        {
            ClickReport refused;
            lock (_io)
            {
                refused = EarlyClick(asClick, InputDisposition.Refused, AbortReason.None, null, "drag.invalid", plan ?? new DragPlan(request.Start, request.Delta, 0));
            }

            return new DragReport(refused, plan?.Steps ?? 0, 0, request.Start);
        }

        var (report, run) = SlideCore(asClick, plan, ct);
        var done = run?.StepsDone ?? 0;
        return new DragReport(report, plan.Steps, done, report.UpPosted ? plan.PointAt(done) : request.Start);
    }

    /// <summary>
    /// The release point's readiness (passive): the slide to it must pass every click rule too, with the same cursor,
    /// cover and home, on the same slid monitor as the press (the game would otherwise re-target its frame rate in the
    /// middle of the drag, c12). Null when fine, otherwise a NotReadyReason name or a drag code.
    /// </summary>
    private string? DragEndRefusal(DragPlan drag, ClickGeometry start, bool nearDenyZone)
    {
        var end = EvaluateReadiness(InputKind.Click, drag.End, nearDenyZone, drag: true);
        if (!end.Report.Ready || end.Click is not { } g)
        {
            return end.Report.Reason.ToString();
        }

        if (g.Cursor != start.Cursor || g.CoverHwnd != start.CoverHwnd || g.Home != start.Home || g.Mode != start.Mode)
        {
            return "drag.sceneChanged";
        }

        if (!string.Equals(g.Plan.SlidMonitor, start.Plan.SlidMonitor, StringComparison.Ordinal))
        {
            return "drag.monitorChange";
        }

        return g.Plan.SlidRect == drag.SlidAt(start.Plan.SlidRect, drag.Steps) ? null : "drag.geometry";
    }

    /// <summary>
    /// P3→P7 of a drag on the engine thread, after the slid check and the popup gate. Returns early whenever an abort was
    /// claimed (by the watchdog or here). Window moves are never issued under the message gate (a hung game must not block
    /// the watchdog's release); the gate only orders the posts.
    /// </summary>
    private void RunDragTimeline(ClickRun run, DragPlan drag)
    {
        var s = run.Session;
        var choke = run.Choke;
        var hwnd = s.Game;
        var lpStart = SlideGeometry.MouseLParamChecked(drag.Start, HomeRules.ExpectedClient);

        // ---- MOVE at the press point, 40 ms, DOWN (atomic gate)
        lock (s.MsgGate)
        {
            if (s.MessagesClosed || s.AbortClaimed)
            {
                return;
            }

            choke.Post(hwnd, HardRules.WM_MOUSEMOVE, 0, lpStart, out _);
        }

        if (!Hold(run, HardRules.DragMoveToDown))
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
                downOk = choke.Post(hwnd, HardRules.WM_LBUTTONDOWN, HardRules.MK_LBUTTON, lpStart, out downErr);
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

        if (!Hold(run, HardRules.DragDownToFirstStep))
        {
            return;
        }

        // ---- the steps: move the window by the step, then MOUSEMOVE(MK_LBUTTON) at the moved client point
        s.Phase = InputPhase.P5DownToUp;
        for (var k = 1; k <= drag.Steps; k++)
        {
            if (!Continue(run))
            {
                return;
            }

            var target = drag.PointAt(k);
            var slid = drag.SlidAt(s.Slid, k);
            lock (s.MsgGate)
            {
                if (s.MessagesClosed || s.AbortClaimed)
                {
                    return;
                }

                s.BeginStep(slid, target);
            }

            var t0 = _clock.Monotonic;
            bool moved;
            try
            {
                moved = choke.SetWindowPos(hwnd, 0, slid.Left, slid.Top, 0, 0, HardRules.SwpSlide, out _);
            }
            finally
            {
                var call = _clock.Monotonic - t0;
                if (call > run.SlowestStepCall)
                {
                    run.SlowestStepCall = call;
                }

                if (s.AbortClaimed && s.SlideBackClaimed)
                {
                    // An abort slid back while this step's move was being issued: re-issue home so the final position is home.
                    choke.SetWindowPos(hwnd, 0, s.Home.Left, s.Home.Top, 0, 0, HardRules.SwpSlideAsync, out _);
                }
            }

            if (s.AbortClaimed)
            {
                return;
            }

            if (!moved || _win32.GetWindowRect(hwnd) != slid || _win32.IsZoomed(hwnd) != s.HomeZoomed)
            {
                ExecuteAbort(run, Trigger.SlideDidNotLand, fromWatchdog: false);
                return;
            }

            s.EndStep();
            var top = TopAt(s.Cursor);
            if (s.Foreground ? top != hwnd : top == hwnd)
            {
                ExecuteAbort(run, s.Foreground ? Trigger.CoverChanged : Trigger.GameUnderCursor, fromWatchdog: false);
                return;
            }

            lock (s.MsgGate)
            {
                if (s.MessagesClosed || s.AbortClaimed)
                {
                    return;
                }

                choke.Post(hwnd, HardRules.WM_MOUSEMOVE, HardRules.MK_LBUTTON, SlideGeometry.MouseLParamChecked(target, HomeRules.ExpectedClient), out _);
                run.StepsDone = k;
            }

            if (!Hold(run, HardRules.DragStepInterval))
            {
                return;
            }
        }

        // ---- hold still (no fling), then UP at the release point
        if (!Hold(run, HardRules.DragHoldBeforeUp))
        {
            return;
        }

        lock (s.MsgGate)
        {
            if (s.MessagesClosed || s.AbortClaimed)
            {
                return;
            }

            if (s.TryClaimUp())
            {
                choke.Post(hwnd, HardRules.WM_LBUTTONUP, 0, HardRules.MouseLParam(s.CurrentTarget), out _);
                s.MarkUpPosted(_clock.UtcNow);
            }
        }

        // ---- P6: wait, unfocus WHILE SLID (as the proof; foreground mode: the drain instead), then slide back home
        s.Phase = InputPhase.P6UpToUnfocus;
        if (!Hold(run, HardRules.DragAfterUp))
        {
            return;
        }

        if (s.Foreground)
        {
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
}
