using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.GameIO.Input;
using Companion.GameIO.Safety;

namespace Companion.GameIO;

/// <summary>
/// Key batch (SPEC 7.2, port of lib.ps1 Send-GameKey with the proven 300/70/700 ms timings). Foreground mode (SPEC 7.3b,
/// EVIDENCE c15): the game has real focus, so the key is posted without any focus message.
/// </summary>
public sealed partial class GameIo
{
    /// <inheritdoc />
    public KeyReport PressKey(KeyRequest request, CancellationToken ct)
    {
        lock (_io)
        {
            var run = new KeyRun(request);
            try
            {
                return PressKeyCore(run, ct);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Bug or failing dependency: close the batch safely (KEYUP after KEYDOWN, then unfocus) and report.
                SafeCloseKey(run);
                var d = run.KeyDownPosted ? InputDisposition.Sent
                    : run.FocusSent > 0 ? InputDisposition.AbortedAfterFocus
                    : InputDisposition.NotSent;
                if (ex is HardRuleViolationException && !run.KeyDownPosted)
                {
                    d = InputDisposition.Refused;
                }

                return KeyReportOf(run, d, AbortReason.None, "key.exception");
            }
            finally
            {
                if (run.OurInput)
                {
                    EndOurInput();
                }
            }
        }
    }

    private KeyReport PressKeyCore(KeyRun run, CancellationToken ct)
    {
        var request = run.Request;
        if (_stop.IsStopRequested || ct.IsCancellationRequested)
        {
            return KeyReportOf(run, InputDisposition.Stopped, AbortReason.StopRequested, null);
        }

        if (!HardRules.AllowedKeys.TryGetValue(request.Key, out var key) || !Guards.IsKeyAllowed(key))
        {
            return KeyReportOf(run, InputDisposition.Refused, AbortReason.None, "key.notWhitelisted");
        }

        var (id, choke) = Bound();
        if (id is null || choke is null)
        {
            return KeyReportOf(run, InputDisposition.NotSent, AbortReason.NotReady, "notConnected");
        }

        if (!ValidateIdentity(id))
        {
            return KeyReportOf(run, ProcessGone(id) ? InputDisposition.GameGone : InputDisposition.Refused, AbortReason.IdentityChanged, null);
        }

        var readiness = EvaluateReadiness(InputKind.Key, null, false, letterKey: request.Key != GameKey.Escape);
        var ready = readiness.Report;
        run.Mode = ready.Mode;
        if (!ready.Ready)
        {
            return KeyReportOf(run, ready.Reason == NotReadyReason.Stopped ? InputDisposition.Stopped : InputDisposition.NotSent, AbortReason.NotReady, ready.Reason.ToString());
        }

        long baseline;
        lock (_state)
        {
            baseline = _keyboardLayoutBaseline;
        }

        // Esc (VK 0x1B, scan 0x01) is the same key in every layout: only a letter key can change meaning when the user
        // switches the input language (2026-10-22: a switched layout refused the Helper tip's Esc 1,084 times in 18 min)
        if (run.Request.Key != GameKey.Escape && _win32.GetKeyboardLayout(_win32.GetWindowThreadId(id.Hwnd)) != baseline)
        {
            return KeyReportOf(run, InputDisposition.NotSent, AbortReason.KeyboardLayoutChanged, null);
        }

        var mark = _logWatch.Mark();
        if (mark is null)
        {
            return KeyReportOf(run, InputDisposition.NotSent, AbortReason.NotReady, "log.noPosition");
        }

        if (!_pacing.TryAcquire(slide: false, focusPair: run.Mode == InputMode.Cover))
        {
            return KeyReportOf(run, InputDisposition.RateLimited, AbortReason.None, null);
        }

        run.Choke = choke;
        run.Began = true;
        if (run.Mode == InputMode.Foreground)
        {
            return PressKeyForeground(run, id, choke, key, mark, readiness.InputTick, ct);
        }

        run.ClipBaseline = _win32.GetClipCursor();
        run.ShowingBaseline = _win32.TryGetCursorShowing(out var showing) ? showing : null;
        BeginOurInput();
        run.OurInput = true;
        var hwnd = id.Hwnd;

        // ---- step 1: fake focus (SendMessageTimeout, each result checked)
        run.Marks.FocusSent = mark;
        run.Marks.FocusSentUtc = _clock.UtcNow;
        var focusStart = _clock.Monotonic;
        foreach (var m in HardRules.FocusOnSequence)
        {
            var r = choke.Send(hwnd, m.Message, m.WParam, m.LParam);
            run.FocusSent++;
            if (r == SendOutcome.AccessDenied)
            {
                return KeyReportOf(run, InputDisposition.IntegrityMismatch, AbortReason.None, null);
            }

            if (r != SendOutcome.Ok)
            {
                KeyUnfocusForced(run);
                return KeyReportOf(run, InputDisposition.AbortedAfterFocus, AbortReason.SendTimeout, null);
            }
        }

        // ---- step 2: RecoverFocus logged after the mark (<= 1000 ms) and >= 300 ms elapsed
        while (true)
        {
            if (_stop.IsStopRequested || ct.IsCancellationRequested)
            {
                KeyUnfocus(run);
                return KeyReportOf(run, InputDisposition.Stopped, AbortReason.StopRequested, null);
            }

            if (IsPidForeground(id.Pid, hwnd))
            {
                return KeyReportOf(run, InputDisposition.UserTookOver, AbortReason.GameForeground, null);
            }

            if (_win32.IsIconic(hwnd))
            {
                return KeyReportOf(run, InputDisposition.UserTookOver, AbortReason.GameIconic, null);
            }

            if (ClipChanged(run))
            {
                KeyUnfocus(run);
                return KeyReportOf(run, InputDisposition.Incident, AbortReason.CursorClipChanged, "clip.changed");
            }

            var elapsed = _clock.Monotonic - focusStart;
            if (!run.FocusLogged)
            {
                run.FocusLogged = LogWatch.HasRecoverFocus(_logWatch.Since(mark));
            }

            if ((run.FocusLogged && elapsed >= HardRules.FocusSettle) || elapsed >= HardRules.RecoverFocusLogWait)
            {
                break;
            }

            _clock.Sleep(LogPollSlice, CancellationToken.None);
        }

        if (!run.FocusLogged)
        {
            KeyUnfocus(run);
            return KeyReportOf(run, InputDisposition.FocusFailed, AbortReason.None, null);
        }

        // ---- step 3: re-check (user took over = no unfocus)
        if (IsPidForeground(id.Pid, hwnd))
        {
            return KeyReportOf(run, InputDisposition.UserTookOver, AbortReason.GameForeground, null);
        }

        if (_win32.IsIconic(hwnd))
        {
            return KeyReportOf(run, InputDisposition.UserTookOver, AbortReason.GameIconic, null);
        }

        if (_stop.IsStopRequested || ct.IsCancellationRequested)
        {
            KeyUnfocus(run);
            return KeyReportOf(run, InputDisposition.Stopped, AbortReason.StopRequested, null);
        }

        // ---- step 4: KEYDOWN, 70 ms, KEYUP (always posted once KEYDOWN was)
        run.Marks.ActionPosted = _logWatch.Mark();
        run.Key = key;
        var downOk = choke.Post(hwnd, HardRules.WM_KEYDOWN, key.VirtualKey, HardRules.KeyDownLParam(key), out var downErr);
        run.ActionPostedUtc = _clock.UtcNow;
        if (!downOk)
        {
            KeyUnfocus(run);
            return KeyReportOf(run, downErr == Native.Win32Const.ERROR_ACCESS_DENIED ? InputDisposition.IntegrityMismatch : InputDisposition.AbortedAfterFocus, AbortReason.None, "keydown.failed");
        }

        run.KeyDownPosted = true;
        try
        {
            _clock.Sleep(HardRules.KeyHold, CancellationToken.None);
        }
        finally
        {
            PostKeyUp(run);
        }

        // ---- step 5: wait <= 700 ms, early exit on an expected UI
        var upStart = _clock.Monotonic;
        var tookOver = false;
        while (_clock.Monotonic - upStart < HardRules.KeyAfterUp)
        {
            if (LogWatch.HasExpected(_logWatch.Since(run.Marks.ActionPosted ?? mark), request.ExpectedUi))
            {
                run.ExpectedSeen = true;
                break;
            }

            if (IsPidForeground(id.Pid, hwnd) || _win32.IsIconic(hwnd))
            {
                tookOver = true;
                break;
            }

            if (_stop.IsStopRequested || ct.IsCancellationRequested)
            {
                break;
            }

            _clock.Sleep(LogPollSlice, CancellationToken.None);
        }

        if (tookOver)
        {
            return KeyReportOf(run, InputDisposition.UserTookOver, IsPidForeground(id.Pid, hwnd) ? AbortReason.GameForeground : AbortReason.GameIconic, null);
        }

        // ---- step 6: unfocus unless the game PID is really foreground (the choke point skips it then)
        var clipChanged = ClipChanged(run);
        run.Marks.UnfocusSent = _logWatch.Mark();
        KeyUnfocus(run);
        run.Marks.End = _logWatch.TryMark();
        return clipChanged
            ? KeyReportOf(run, InputDisposition.Incident, AbortReason.CursorClipChanged, "clip.changed")
            : KeyReportOf(run, InputDisposition.Sent, AbortReason.None, null);
    }

    /// <summary>
    /// Foreground-mode key (SPEC 7.3b): no focus message and no unfocus (the game has real focus, R2; the choke point refuses
    /// any focus message meanwhile). KEYDOWN goes only while the game is still the foreground window and nothing was typed
    /// or clicked since readiness (checked last, right before it); input during the key or its wait is reported, since a UI
    /// seen then may be the user's. Keys never move the window, and no fake focus is held, so the clip is not watched.
    /// </summary>
    private KeyReport PressKeyForeground(KeyRun run, GameIdentity id, ChokePoint choke, KeyCode key, LogPosition mark, uint? inputTick, CancellationToken ct)
    {
        var hwnd = id.Hwnd;
        using var scope = choke.EnterForegroundInput();
        run.Marks.FocusSent = mark; // the engine's event window starts here; nothing is sent before KEYDOWN
        run.Marks.FocusSentUtc = _clock.UtcNow;
        if (_stop.IsStopRequested || ct.IsCancellationRequested)
        {
            return KeyReportOf(run, InputDisposition.Stopped, AbortReason.StopRequested, null);
        }

        if (_win32.IsIconic(hwnd))
        {
            return KeyReportOf(run, InputDisposition.NotSent, AbortReason.GameIconic, null);
        }

        if (_win32.GetForegroundWindow() != hwnd)
        {
            return KeyReportOf(run, InputDisposition.NotSent, AbortReason.ForegroundLost, null);
        }

        if (inputTick is not { } baseline || _win32.GetLastInputTick() != baseline)
        {
            run.UserInputDuring = true;
            return KeyReportOf(run, InputDisposition.NotSent, AbortReason.UserInput, null);
        }

        // ---- KEYDOWN, 70 ms, KEYUP (always posted once KEYDOWN was)
        run.Marks.ActionPosted = _logWatch.Mark();
        run.Key = key;
        var downOk = choke.Post(hwnd, HardRules.WM_KEYDOWN, key.VirtualKey, HardRules.KeyDownLParam(key), out var downErr);
        run.ActionPostedUtc = _clock.UtcNow;
        if (!downOk)
        {
            return KeyReportOf(run, downErr == Native.Win32Const.ERROR_ACCESS_DENIED ? InputDisposition.IntegrityMismatch : InputDisposition.NotSent, AbortReason.None, "keydown.failed");
        }

        run.KeyDownPosted = true;
        try
        {
            _clock.Sleep(HardRules.KeyHold, CancellationToken.None);
        }
        finally
        {
            PostKeyUp(run);
        }

        // ---- wait <= 700 ms, early exit on an expected UI; the user switching away or minimizing ends it
        var upStart = _clock.Monotonic;
        var tookOver = false;
        while (_clock.Monotonic - upStart < HardRules.KeyAfterUp)
        {
            if (LogWatch.HasExpected(_logWatch.Since(run.Marks.ActionPosted ?? mark), run.Request.ExpectedUi))
            {
                run.ExpectedSeen = true;
                break;
            }

            if (_win32.GetForegroundWindow() != hwnd || _win32.IsIconic(hwnd))
            {
                tookOver = true;
                break;
            }

            if (_stop.IsStopRequested || ct.IsCancellationRequested)
            {
                break;
            }

            _clock.Sleep(LogPollSlice, CancellationToken.None);
        }

        run.UserInputDuring = _win32.GetLastInputTick() != baseline;
        run.Marks.End = _logWatch.TryMark();
        return tookOver
            ? KeyReportOf(run, InputDisposition.UserTookOver, _win32.IsIconic(hwnd) ? AbortReason.GameIconic : AbortReason.ForegroundLost, null)
            : KeyReportOf(run, InputDisposition.Sent, AbortReason.None, null);
    }

    /// <summary>
    /// R1/F27: the ClipCursor rectangle changed while we hold fake focus. Pointer visibility alone is not checked: other
    /// apps hide the pointer (a video player, typing; live 2026-10-15), which says nothing about the game.
    /// </summary>
    private bool ClipChanged(KeyRun run)
    {
        var clip = _win32.GetClipCursor();
        return run.ClipBaseline is { } cb && clip is { } c && c != cb;
    }

    private void PostKeyUp(KeyRun run)
    {
        if (run.KeyUpPosted || !run.KeyDownPosted || run.Choke is null || run.Key is not { } key)
        {
            return;
        }

        run.KeyUpPosted = true;
        run.Choke.Post(run.Choke.Game, HardRules.WM_KEYUP, key.VirtualKey, HardRules.KeyUpLParam(key), out _);
        run.ReleasePostedUtc = _clock.UtcNow;
    }

    private void KeyUnfocus(KeyRun run)
    {
        if (run.Choke is null || run.FocusSent == 0 || run.UnfocusDone)
        {
            return;
        }

        run.UnfocusDone = true;
        run.UnfocusSentUtc ??= _clock.UtcNow;
        var (skipped, _) = SendFocusOff(run.Choke);
        run.UnfocusSkipped |= skipped;
    }

    private void KeyUnfocusForced(KeyRun run)
    {
        if (run.Choke is null || run.FocusSent == 0 || run.UnfocusDone)
        {
            return;
        }

        run.UnfocusDone = true;
        run.UnfocusSentUtc ??= _clock.UtcNow;
        run.UnfocusSkipped |= SendFocusOffNotify(run.Choke);
    }

    private void SafeCloseKey(KeyRun run)
    {
        try
        {
            PostKeyUp(run);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // continue with the unfocus
        }

        try
        {
            KeyUnfocus(run);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // nothing more to do
        }
    }

    /// <summary>Fake unfocus (FocusOffSequence) via SendMessageTimeout; after a timeout the rest goes nonqueued.</summary>
    private static (bool Skipped, bool TimedOut) SendFocusOff(ChokePoint choke)
    {
        var skipped = false;
        var timedOut = false;
        foreach (var m in HardRules.FocusOffSequence)
        {
            if (timedOut)
            {
                skipped |= choke.Notify(choke.Game, m.Message, m.WParam, m.LParam) == SendOutcome.SkippedForeground;
                continue;
            }

            var r = choke.Send(choke.Game, m.Message, m.WParam, m.LParam);
            if (r == SendOutcome.SkippedForeground)
            {
                skipped = true;
            }
            else if (r == SendOutcome.Timeout)
            {
                timedOut = true;
            }
        }

        return (skipped, timedOut);
    }

    /// <summary>Fake unfocus via SendNotifyMessage (forced order); returns true if skipped because the game was foreground.</summary>
    private static bool SendFocusOffNotify(ChokePoint choke)
    {
        var skipped = false;
        foreach (var m in HardRules.FocusOffSequence)
        {
            skipped |= choke.Notify(choke.Game, m.Message, m.WParam, m.LParam) == SendOutcome.SkippedForeground;
        }

        return skipped;
    }

    private KeyReport KeyReportOf(KeyRun run, InputDisposition disposition, AbortReason abort, string? detail)
    {
        var marks = new InputMarks(
            run.Marks.FocusSent,
            run.Marks.FocusSentUtc,
            run.Marks.ActionPosted,
            run.ActionPostedUtc,
            run.ReleasePostedUtc,
            run.Marks.UnfocusSent,
            run.UnfocusSentUtc,
            null,
            run.Marks.End);
        Journal(
            JournalKind.Input,
            run.Request.StepId,
            ("input", "key"),
            ("mode", run.Mode.ToString()),
            ("key", run.Request.Key.ToString()),
            ("disposition", disposition.ToString()),
            ("abort", abort.ToString()),
            ("focusSent", run.FocusSent),
            ("focusLogged", run.FocusLogged),
            ("keyDown", run.KeyDownPosted),
            ("keyUp", run.KeyUpPosted),
            ("unfocusSkipped", run.UnfocusSkipped),
            ("expectedSeen", run.ExpectedSeen),
            ("userInput", run.Mode == InputMode.Foreground ? run.UserInputDuring : (bool?)null),
            ("detail", detail));
        return new KeyReport(disposition, abort, run.FocusSent, run.FocusLogged, run.UnfocusSkipped, marks, run.ExpectedSeen, run.Mode, run.UserInputDuring);
    }

    /// <summary>Mutable state of one key batch.</summary>
    private sealed class KeyRun(KeyRequest request)
    {
        public KeyRequest Request { get; } = request;

        public MarkSet Marks { get; } = new();

        public ChokePoint? Choke { get; set; }

        public KeyCode? Key { get; set; }

        public RectI? ClipBaseline { get; set; }

        public bool? ShowingBaseline { get; set; }

        public bool Began { get; set; }

        /// <summary>Cover mode: the batch is marked as ours (its focus lines are not the user's).</summary>
        public bool OurInput { get; set; }

        /// <summary>The input mode from readiness (SPEC 7.3b).</summary>
        public InputMode Mode { get; set; }

        /// <summary>Foreground mode: keyboard or mouse input since readiness.</summary>
        public bool UserInputDuring { get; set; }

        public int FocusSent { get; set; }

        public bool FocusLogged { get; set; }

        public bool KeyDownPosted { get; set; }

        public bool KeyUpPosted { get; set; }

        public bool UnfocusDone { get; set; }

        public bool UnfocusSkipped { get; set; }

        public bool ExpectedSeen { get; set; }

        public DateTimeOffset? ActionPostedUtc { get; set; }

        public DateTimeOffset? ReleasePostedUtc { get; set; }

        public DateTimeOffset? UnfocusSentUtc { get; set; }
    }
}
