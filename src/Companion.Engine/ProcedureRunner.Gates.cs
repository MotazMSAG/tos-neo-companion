using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Navigation;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.Engine.Internal;

namespace Companion.Engine;

public sealed partial class ProcedureRunner
{
    private static readonly TimeSpan ClockJumpTolerance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HudAnimation = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PopupRecentWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InlinePacingMax = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Waits (no input) until the global gates of SPEC 6.3 clear. Returns null when clear; otherwise an attempt for a
    /// HALT/terminal outcome, a patience overrun (park), or a resume after user activity.
    /// </summary>
    private Attempt? AwaitGates(RunState rs, ProcFrame f, StepDefinition step, GateScope scope)
    {
        StepOutcome? current = null;
        var hadUserActive = false;
        var hadBusy = false;
        while (true)
        {
            rs.Ct.ThrowIfCancellationRequested();
            if (_pause.IsPaused)
            {
                WaitWhilePaused(rs, f);
                continue;
            }
            // No quiet-log gate before acting: an idle game can go a long time without a log line (live run 2026-10-14
            // waited here forever). Liveness is checked after we act, when a response is expected.
            var g = EvaluateGates(rs, f, step, scope, checkLogQuiet: false, out var detail);
            if (g is null && hadBusy && scope is GateScope.Key or GateScope.Click or GateScope.Commit && !HudSettled(rs))
            {
                g = StepOutcome.Busy;
                detail = "busy.settle";
            }
            if (g is null)
            {
                if (current is not null)
                {
                    Write(rs, JournalKind.Wait, rs.CurrentStep, ("state", "clear"), ("reason", current));
                    SetStatus(EngineState.Running, rs, null);
                }
                return hadUserActive ? new Attempt(StepOutcome.UserActive, "userActive.resume", Resume: true) : null;
            }
            var o = g.Value;
            if (_late is not null)
            {
                // a gate is closed while the last quick click's late check is due: read it first (a window that click
                // opened is then the job's own, or the same HALT as ever), then look at the gates again
                SettleLate(rs, step);
                continue;
            }
            switch (OutcomePolicy.ClassOf(o))
            {
                case OutcomeClass.Halt:
                    return new Attempt(o, detail, Subject: step.Id);
                case OutcomeClass.Terminal:
                    return new Attempt(o, detail);
            }
            if (o == StepOutcome.UserActive && IsCommitJob(rs, f))
            {
                return new Attempt(StepOutcome.Deferred, "commit.userActive"); // not a stop: the plan pauses and comes back (FALLBACKS.md)
            }
            if (o != current)
            {
                Write(rs, JournalKind.Wait, rs.CurrentStep, ("state", current is null ? "start" : "change"), ("reason", o), ("detail", detail));
                current = o;
                SetStatus(EngineState.Waiting, rs, o.ToString());
                NotifyFor(rs, o, detail);
            }
            hadUserActive |= o == StepOutcome.UserActive;
            hadBusy |= o == StepOutcome.Busy;
            f.WaitSince ??= Mono;
            if (Mono - f.WaitSince.Value >= TimeSpan.FromMilliseconds(f.Limits.PatienceMs))
            {
                return new Attempt(o, detail, PatienceExceeded: true);
            }
            if (o == StepOutcome.ClockJump)
            {
                Sleep(rs, HardRules.UncertainSettle);
                f.WaitSince = Mono;
                continue;
            }
            Sleep(rs, o switch
            {
                StepOutcome.Throttled => ThrottleWait(),
                StepOutcome.RateLimited => Clamp(SafePacing(), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(60)),
                _ => PollInterval,
            });
        }
    }

    private static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max) => v < min ? min : v > max ? max : v;

    private void WaitWhilePaused(RunState rs, ProcFrame f)
    {
        SetStatus(EngineState.Paused, rs, "paused");
        Write(rs, JournalKind.Wait, rs.CurrentStep, ("state", "start"), ("reason", "paused"));
        while (_pause.IsPaused)
        {
            if (_stop.IsStopRequested)
            {
                throw new EndSignal(StepOutcome.Stopped, "stop");
            }
            Sleep(rs, PollInterval);
        }
        Write(rs, JournalKind.Wait, rs.CurrentStep, ("state", "clear"), ("reason", "paused"));
        SetStatus(EngineState.Running, rs, null);
        f.WaitSince = null;
        f.UnreachableSince = null; // a resume, pause, step back or settle starts the unreachable limit again (2026-10-16 review)
    }

    private void NotifyFor(RunState rs, StepOutcome o, string? detail)
    {
        var code = o switch
        {
            StepOutcome.DisplayModeWrong => "wait.displayMode",
            StepOutcome.ForeignBlockingUi => "wait.foreignBlockingUi",
            // a gathering trip that is out (status 1) is a known state, not the user's to fix (incident 18: a trip from
            // before was still out and the user was told "NEEDS YOU", pressed Stop and started again); an unknown or
            // other status may need the user's confirmation in the tray
            StepOutcome.AutoCollectNotIdle when detail == "autoCollect.1" => null,
            StepOutcome.AutoCollectNotIdle => "wait.autoCollect",
            _ => null,
        };
        if (code is not null)
        {
            Notify(rs, Messages.Note(code), oncePerJob: true);
        }
    }

    /// <summary>Busy just ended (loading): the HUD must also be seen on 2 stable captures (SPEC 6.2).</summary>
    private bool HudSettled(RunState rs)
    {
        var hc = _screen.ClassifyHudStable(rs.Ct);
        return hc.State is HudState.Collapsed or HudState.Expanded or HudState.Covered;
    }

    /// <summary>The gates of SPEC 6.3 in order (no waiting). Null when every gate in scope is clear.</summary>
    private StepOutcome? EvaluateGates(RunState rs, ProcFrame? f, StepDefinition step, GateScope scope, bool checkLogQuiet, out string detail)
    {
        detail = string.Empty;
        // 1. stop / halt latch (pause is handled by the caller)
        if (_stop.IsStopRequested)
        {
            detail = "stop";
            return StepOutcome.Stopped;
        }
        if (_haltLatch.IsHalted)
        {
            detail = "halt.latched";
            return StepOutcome.Refused;
        }
        // 2. trusted build
        if (!_fileTrust.BuildTrusted)
        {
            detail = "build.untrusted";
            return StepOutcome.TamperedFiles;
        }
        // 3. game identity
        var st = _gameIo.GetState();
        if (st.Identity is null)
        {
            detail = "game.gone";
            return StepOutcome.GameExited;
        }
        if (rs.Identity is not null && st.Identity != rs.Identity)
        {
            detail = "game.identity";
            return StepOutcome.IdentityChanged;
        }
        if (!st.Exists)
        {
            detail = "game.window";
            return StepOutcome.GameExited;
        }
        // 4. log generation and liveness
        var poll = _log.Poll();
        if (poll.Rotated || (rs.LogFile is not null && _log.Position is { } pos && pos.File != rs.LogFile))
        {
            detail = "log.rotated";
            return StepOutcome.GameExited;
        }
        if (checkLogQuiet)
        {
            var health = _log.Health;
            var quiet = health.LastGrowthUtc is { } g ? _clock.UtcNow - g : TimeSpan.MaxValue;
            if (quiet > HardRules.LogHeartbeatMax)
            {
                rs.LogQuietSince ??= Mono;
                if (Mono - rs.LogQuietSince.Value >= HardRules.LogStallHalt - HardRules.LogHeartbeatMax)
                {
                    detail = "log.stalled";
                    return StepOutcome.LogStalled;
                }
                detail = "log.quiet";
                return StepOutcome.LogQuiet;
            }
            rs.LogQuietSince = null;
        }
        if (scope == GateScope.Passive)
        {
            return null;
        }
        // 5. game available, not busy
        var ui = _log.State;
        if (!ui.GameAvailable || ui.Busy != BusyKind.None)
        {
            detail = "busy." + (ui.GameAvailable ? ui.Busy.ToString() : "notAvailable");
            return StepOutcome.Busy;
        }
        if (scope == GateScope.Await)
        {
            return null;
        }
        // 6. display mode, extra game window
        if (st.ExtraVisibleGameWindows > 0)
        {
            detail = "game.extraWindow";
            return StepOutcome.ExtraGameWindow;
        }
        var prefs = _gameIo.ReadWindowPrefs();
        if (prefs.FullscreenMode != HardRules.ExpectedFullscreenMode || !st.Maximized || !st.HasCaption
            || st.ClientSize != new SizeI(HardRules.ExpectedClientWidth, HardRules.ExpectedClientHeight)
            || st.Monitor is null || st.Monitor.Dpi != HardRules.ExpectedDpi)
        {
            detail = "display.mode";
            return StepOutcome.DisplayModeWrong;
        }
        // 7. not foreground (foreground mode excepted, SPEC 7.3b: the game in front while the user rests on it), not
        // iconic, cooldowns; in foreground mode the user's input or a fresh switch-in is the "user active" signal
        var r = _gameIo.CheckReadiness(new ReadinessQuery(InputKind.Key, null, false));
        if (st.Iconic || (st.Foreground && r.Mode != InputMode.Foreground))
        {
            detail = st.Iconic ? "game.iconic" : "game.foreground";
            return StepOutcome.UserActive;
        }
        switch (r.Reason)
        {
            case NotReadyReason.GameForeground or NotReadyReason.GameIconic or NotReadyReason.Cooldown
                or NotReadyReason.ForegroundSettling or NotReadyReason.UserInputRecent or NotReadyReason.UserButtonDown or NotReadyReason.ChatOpen:
                detail = "ready." + r.Reason;
                return StepOutcome.UserActive;
            case NotReadyReason.SessionNotInteractive:
                detail = "session";
                return StepOutcome.SessionUnavailable;
            case NotReadyReason.ExtraGameWindow:
                detail = "game.extraWindow";
                return StepOutcome.ExtraGameWindow;
            case NotReadyReason.DisplayModeWrong:
                detail = "display.mode";
                return StepOutcome.DisplayModeWrong;
            case NotReadyReason.Stopped:
                detail = "stop";
                return StepOutcome.Stopped;
            case NotReadyReason.GameBusy or NotReadyReason.RecentJank:
                detail = "ready." + r.Reason;
                return StepOutcome.Busy;
        }
        // 8. session interactive (above), clock jump
        var wall = _clock.UtcNow;
        var mono = Mono;
        if (rs.LastWall is { } lw && rs.LastMono is { } lm)
        {
            rs.LastWall = wall;
            rs.LastMono = mono;
            if (((wall - lw) - (mono - lm)).Duration() > ClockJumpTolerance)
            {
                detail = "clock.jump";
                return StepOutcome.ClockJump;
            }
        }
        else
        {
            rs.LastWall = wall;
            rs.LastMono = mono;
        }
        // 9. auto-collect idle
        if (!AutoCollectIdle(ui.AutoCollectStatus) && !(scope == GateScope.Click && step.Flags.HasFlag(StepFlags.DuringAutoCollect)))
        {
            detail = ui.AutoCollectStatus is { } acs ? "autoCollect." + acs : "autoCollect.unknown";
            return StepOutcome.AutoCollectNotIdle;
        }
        // 10. combat (clicks and commits)
        if (scope is GateScope.Click or GateScope.Commit && ui.LastCombatIndicator is { } combat && GameNow - combat < HardRules.CombatRecent)
        {
            detail = "combat.recent";
            return StepOutcome.CombatRecent;
        }
        // 11. throttle, pacing token
        // Only throttle lines that follow one of OUR inputs count: the game throttles its own background requests all
        // day (town refreshes, friends going offline), which must not pause us (live run 2026-10-14).
        if (ui.LastThrottle is { } thr && GameNow - thr < HardRules.ThrottleBackoff
            && rs.LastOwnInputLocal is { } own && thr >= own && thr - own <= TimeSpan.FromSeconds(10))
        {
            detail = "throttle";
            return StepOutcome.Throttled;
        }
        var next = SafePacing();
        if (next > InlinePacingMax)
        {
            detail = "pacing";
            return StepOutcome.RateLimited;
        }
        // 12. foreign forbidden/social UI, craft in progress
        foreach (var name in ui.AdvisoryStack)
        {
            var kind = _classifier.Classify(name);
            if (kind is UiKind.Forbidden or UiKind.Social && !rs.Job.OpenedUis.Contains(name))
            {
                detail = "foreign." + kind;
                return StepOutcome.ForeignBlockingUi;
            }
            if (kind == UiKind.Station)
            {
                detail = "craft.station";
                return StepOutcome.CraftInProgress;
            }
        }
        if (ui.Craft.InProgress)
        {
            detail = "craft.inProgress";
            return StepOutcome.CraftInProgress;
        }
        // 13. commit gates, slide switch
        if (scope == GateScope.Commit)
        {
            var def = step.Action is ClickAction c ? _templates.Get(c.TemplateId) : null;
            var sw = CommitSwitchOf(f?.Def.Gate ?? rs.Root.Gate, def, rs.Ctx.Switch);
            if (sw == AutomationSwitch.None || !SwitchOn(sw))
            {
                detail = "switch.off";
                return StepOutcome.Refused;
            }
            // the user used the game since the job started, or since the selection was last verified (a read-only
            // selection-verifying run that succeeded, cleared by the next commit run's end): not now (FALLBACKS.md: the
            // plan pauses and comes back; a goal job verifies its recipe again before the next Make). Without a fresh
            // verification the window is the whole job, as before (2026-10-16 review: a per-run window let a favourite
            // slot click select a recipe the user had moved while away).
            var since = rs.Job.VerifiedMono is { } verified && verified > rs.Job.StartedMono ? verified : rs.Job.StartedMono;
            if (r.SinceUserInGame < Mono - since)
            {
                detail = "commit.userActivity";
                return StepOutcome.Deferred;
            }
        }
        if (scope is GateScope.Click or GateScope.Commit && step.Action is ClickAction or ToggleHudAction or DragAction && !SafeSlideSwitch())
        {
            detail = "slideClick.off";
            return StepOutcome.Refused;
        }
        return null;
    }

    private bool AutoCollectIdle(int? status)
    {
        // No status line since the game started: auto-collect was never switched this session, so it is off. The game
        // only logs the status when it changes (live run 2026-10-14: an unknown status blocked every job).
        if (status is null)
        {
            return true;
        }
        if (status.Value == 0)
        {
            return true;
        }
        try
        {
            return _config.Current.Policy.TreatAutoCollectStatusAsIdle?.Contains(status.Value) == true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ HudClean (engine-side, SPEC 6.2)

    private static bool IsOpenVerb(UiVerb v) => v is UiVerb.Show or UiVerb.OpenConst or UiVerb.PopupRealShow;

    private void RecordEsc(RunState rs, InputMarks marks)
    {
        if (marks.ActionPosted is not { } pos)
        {
            return;
        }
        _escMarks.Add((pos, Mono));
        if (_escMarks.Count > 64)
        {
            _escMarks.RemoveRange(0, _escMarks.Count - 64);
        }
        rs.LastHudInputMono = Mono;
    }

    /// <summary>
    /// A 'hide top ui nil' after one of OUR Esc KEYDOWN marks, with no stack/forbidden/social/unknown show, scene
    /// load, rotation or game-foreground interval since.
    /// </summary>
    private bool HudClean(RunState rs)
    {
        var ui = _log.State;
        var nil = ui.LastHideTopNil;
        if (nil is null)
        {
            return false;
        }
        if (rs.LogFile is not null && nil.File != rs.LogFile)
        {
            return false;
        }
        (LogPosition Pos, TimeSpan Mono)? mark = null;
        for (var k = _escMarks.Count - 1; k >= 0; k--)
        {
            var m = _escMarks[k];
            if (m.Pos.SameFile(nil) && m.Pos.Offset < nil.Offset)
            {
                mark = m;
                break;
            }
        }
        if (mark is null)
        {
            return false;
        }
        foreach (var e in _log.EventsSince(nil))
        {
            switch (e)
            {
                case UiLogEvent u when IsOpenVerb(u.Verb) && u.UiName is { } name:
                    if (_classifier.Classify(name) != UiKind.Overlay)
                    {
                        return false;
                    }
                    break;
                case SceneLoadLogEvent s when s.Phase == ScenePhase.StartLoad:
                    return false;
                case SessionLogEvent s when s.Marker is SessionMarker.LogCallbackAdded or SessionMarker.ApplicationQuit or SessionMarker.LogoutStart:
                    return false;
            }
        }
        try
        {
            var st = _gameIo.GetState();
            var r = _gameIo.CheckReadiness(new ReadinessQuery(InputKind.Key, null, false));
            if (st.Iconic || (st.Foreground && r.Mode != InputMode.Foreground))
            {
                return false;
            }
            if (r.Reason is NotReadyReason.GameForeground or NotReadyReason.GameIconic)
            {
                return false;
            }
            // the user has not used the game since our Esc (foreground mode: no input while it was in front)
            var sinceEsc = Mono - mark.Value.Mono;
            if (r.SinceUserInGame < sinceEsc)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }
        return true;
    }

    private bool PopupRecent(RunState rs)
    {
        if (rs.StartLog is not { } from)
        {
            return false;
        }
        var now = GameNow;
        var events = _log.EventsSince(from);
        for (var k = events.Count - 1; k >= 0; k--)
        {
            if (events[k] is not UiLogEvent u)
            {
                continue;
            }
            var age = now - u.Timestamp;
            if (age > PopupRecentWindow)
            {
                break;
            }
            if (IsOpenVerb(u.Verb) && u.UiName is { } n && _classifier.Classify(n) != UiKind.Overlay)
            {
                return true;
            }
        }
        return false;
    }

    private bool RecentHudInput(RunState rs) => rs.LastHudInputMono is { } t && Mono - t < HudAnimation;

    // ------------------------------------------------------------------ preconditions

    private PreResult EvaluatePre(RunState rs, StepDefinition step)
    {
        var pre = step.Pre ?? StatePredicate.Any;
        var ui = _log.State;
        var transientUi = ui.Busy != BusyKind.None || PopupRecent(rs);
        if (pre.HudClean == true && !HudClean(rs))
        {
            return new PreResult(false, transientUi, "pre.hudClean");
        }
        if (pre.TopUi is not null && !string.Equals(ui.AdvisoryTop, pre.TopUi, StringComparison.Ordinal))
        {
            return new PreResult(false, transientUi, "pre.topUi");
        }
        if (pre.Hud is { } hud)
        {
            var hc = _screen.ClassifyHudStable(rs.Ct);
            if (hc.State != hud)
            {
                var transient = transientUi || (hc.State == HudState.Unknown && RecentHudInput(rs));
                return new PreResult(false, transient, "pre.hud." + hc.State);
            }
        }
        if (pre.Screen is not null)
        {
            var id = IdentifyNow(rs);
            if (!string.Equals(id.ScreenId, pre.Screen, StringComparison.Ordinal))
            {
                var transient = transientUi || (id.Confidence == IdentificationConfidence.Unknown && RecentHudInput(rs));
                return new PreResult(false, transient, "pre.screen");
            }
        }
        if (pre.TemplatesPresent is { Count: > 0 } templates)
        {
            var misses = 0;
            for (var k = 0; k < 2; k++)
            {
                var cap = _screen.Capture(rs.Ct);
                if (!cap.Ok)
                {
                    return new PreResult(false, true, "pre.capture." + cap.Problem);
                }
                rs.LastFrame = cap.Frame;
                var missing = false;
                foreach (var t in templates)
                {
                    var m = _vision.Locate(cap.Frame!, t);
                    if (m.Found)
                    {
                        continue;
                    }
                    if (m.Failure == MatchFailure.OutsideRadius && !PopupRecent(rs))
                    {
                        throw new HaltSignal(HaltCode.LayoutChanged, t, rs.CurrentStep, StepOutcome.LayoutChanged); // a window just opened may still be sliding in (2026-10-16)
                    }
                    missing = true;
                }
                if (missing)
                {
                    misses++;
                }
                if (k == 0)
                {
                    Sleep(rs, HardRules.StableCaptureGap);
                }
            }
            if (misses > 0)
            {
                return new PreResult(false, misses < 2 || transientUi, "pre.templates");
            }
        }
        return PreResult.Ok;
    }

    /// <summary>For SkipIfSatisfied: does the expectation already hold without any input?</summary>
    private bool ExpectationHoldsNow(RunState rs, Expectation? e, int depth = 0)
    {
        if (e is null || depth > 2)
        {
            return false;
        }
        var specified = false;
        var ok = true;
        if (e.UiEvents is { Count: > 0 } events)
        {
            specified = true;
            var ui = _log.State;
            ok &= events.Any(x => x.Verb switch
            {
                UiVerb.Show or UiVerb.OpenConst => string.Equals(ui.AdvisoryTop, x.UiName, StringComparison.Ordinal),
                UiVerb.HideTop when x.UiName is null => HudClean(rs),
                UiVerb.HideTop => !ui.AdvisoryStack.Contains(x.UiName!, StringComparer.Ordinal),
                _ => false,
            });
        }
        if (ok && e.Hud is { } hud)
        {
            specified = true;
            ok &= _screen.ClassifyHudStable(rs.Ct).State == hud;
        }
        if (ok && e.Screen is { } screen)
        {
            specified = true;
            ok &= string.Equals(IdentifyNow(rs).ScreenId, screen, StringComparison.Ordinal);
        }
        if (specified && ok)
        {
            return true;
        }
        return e.Alternatives?.Any(a => ExpectationHoldsNow(rs, a, depth + 1)) == true;
    }

    private ScreenIdentification IdentifyNow(RunState rs)
    {
        var cap = _screen.Capture(rs.Ct);
        var ui = _log.State;
        if (!cap.Ok)
        {
            return new ScreenIdentification(null, IdentificationConfidence.Unknown, ui.AdvisoryTop, HudState.Unknown, "capture." + cap.Problem);
        }
        rs.LastFrame = cap.Frame;
        return _identifier.Identify(cap.Frame!, ui);
    }
}
