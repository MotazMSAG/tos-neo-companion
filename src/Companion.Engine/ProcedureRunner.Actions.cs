using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Logs;
using Companion.Contracts.Navigation;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.Engine.Internal;

namespace Companion.Engine;

public sealed partial class ProcedureRunner
{
    private static readonly TimeSpan ReadinessWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReadinessPoll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan EscWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CraftCadence = TimeSpan.FromSeconds(3.5);
    private static readonly TimeSpan CraftMargin = TimeSpan.FromSeconds(15);

    private static readonly StepDefinition EscStep = new("esc", StatePredicate.Any, new KeyAction(GameKey.Escape), Expectation.Nothing);

    // ------------------------------------------------------------------ budgets / accounting

    private void BudgetCheck(RunState rs, int clicks, int escs, string? subject)
    {
        var l = rs.Limits;
        if (rs.Inputs + 1 > l.MaxInputBatches || rs.Clicks + clicks > l.MaxClicks || rs.Escs + escs > l.MaxEsc)
        {
            throw new HaltSignal(HaltCode.BudgetExceeded, subject ?? rs.CurrentStep, rs.CurrentStep, StepOutcome.BudgetExceeded);
        }
    }

    private void WaitPacing(RunState rs)
    {
        var n = SafePacing();
        if (n > TimeSpan.Zero && n <= InlinePacingMax)
        {
            Sleep(rs, n);
        }
    }

    // Only a press that reached the game counts toward the input budget (incident 5, audit R1.1: aborts after the focus
    // messages, with no key or button down, used up the budget and latched BudgetExceeded HALTs with 0 clicks). Aborts
    // are counted apart and bounded in Decide (aborted.exhausted, Deferred).
    private void AccountKey(RunState rs, KeyReport rep, GameKey key)
    {
        // the game was touched: focus messages, or in foreground mode (no focus messages, SPEC 7.3b) the key itself
        if (rep.FocusMessagesSent > 0 || (rep.Mode == InputMode.Foreground && rep.Marks.ActionPostedUtc is not null))
        {
            rs.LastOwnInputLocal = GameNow;
            if (rep.Marks.ActionPostedUtc is not null)
            {
                rs.Inputs++;
            }
            else
            {
                rs.Aborted++;
            }
        }
        if (key == GameKey.Escape && rep.Marks.ActionPostedUtc is not null)
        {
            rs.Escs++;
        }
    }

    private void AccountClick(RunState rs, ClickReport rep)
    {
        // the game was touched: focus messages, or in foreground mode (no focus messages, SPEC 7.3b) the slide
        if (rep.FocusMessagesSent > 0 || (rep.Mode == InputMode.Foreground && rep.Slide is not null))
        {
            rs.LastOwnInputLocal = GameNow;
            if (rep.DownPosted)
            {
                rs.Inputs++;
            }
            else
            {
                rs.Aborted++;
            }
        }
        if (rep.DownPosted)
        {
            rs.Clicks++;
        }
    }

    private void JournalKey(RunState rs, KeyReport rep, GameKey key, string purpose) =>
        Write(rs, JournalKind.Input, rs.CurrentStep, ("kind", "key"), ("mode", rep.Mode), ("key", key), ("purpose", purpose), ("disposition", rep.Disposition),
            ("abort", rep.Abort), ("focusMsgs", rep.FocusMessagesSent), ("focusLogged", rep.FocusLogged), ("unfocusSkipped", rep.UnfocusSkipped),
            ("expectedSeen", rep.ExpectedSeen), ("userInput", rep.UserInputDuring), ("inputs", rs.Inputs), ("aborted", rs.Aborted), ("escs", rs.Escs));

    private void JournalClick(RunState rs, string templateId, ClickReport rep, bool near, bool navFixed, bool patchVerify, string? patchWhy = null, StepFlags flags = StepFlags.None)
    {
        Write(rs, JournalKind.Input, rs.CurrentStep, ("kind", "click"), ("mode", rep.Mode), ("template", templateId), ("disposition", rep.Disposition),
            ("abort", rep.Abort), ("phase", rep.Phase), ("focusMsgs", rep.FocusMessagesSent), ("focusLogged", rep.FocusLogged),
            ("down", rep.DownPosted), ("up", rep.UpPosted), ("unfocusSlid", rep.UnfocusSentWhileSlid), ("unfocusSkipped", rep.UnfocusSkipped),
            ("expectedSeenSlid", rep.ExpectedSeenWhileSlid), ("userInput", rep.Cursor.UserInput), ("nearDenyZone", near), ("navFixed", navFixed), ("patchVerify", patchVerify),
            ("patchWhy", patchWhy), ("stepFlags", flags.ToString().Replace(", ", "+", StringComparison.Ordinal)),
            ("inputs", rs.Inputs), ("aborted", rs.Aborted), ("clicks", rs.Clicks));
        JournalSlideAndIncident(rs, rep);
    }

    private void JournalSlideAndIncident(RunState rs, ClickReport rep)
    {
        if (rep.Slide is { } s)
        {
            Write(rs, JournalKind.Slide, rs.CurrentStep, ("mode", rep.Mode), ("displacedMs", s.Displaced), ("slideCallMs", s.SlideCall), ("slideBackCallMs", s.SlideBackCall),
                ("forced", s.ForcedSlideBack), ("backAtHome", s.BackAtHome), ("coverClass", s.CoverClass), ("dx", s.Plan.Dx), ("dy", s.Plan.Dy),
                ("exposedPx", s.Plan.ExposedAreaPx), ("wdSamples", s.Watchdog.Samples), ("wdP50Ms", s.Watchdog.P50Ms), ("wdP99Ms", s.Watchdog.P99Ms),
                ("wdMaxGapMs", s.Watchdog.MaxGapMs), ("overBudget", s.Displaced > HardRules.DisplacementBudget));
        }
        if (rep.Incident is { } inc)
        {
            Write(rs, JournalKind.Incident, rs.CurrentStep, ("kind", inc.Kind), ("restored", inc.Restored), ("rung", inc.Rung), ("detail", inc.DetailCode));
        }
    }

    // ------------------------------------------------------------------ keys

    private Attempt DoKey(RunState rs, ProcFrame f, StepDefinition step, KeyAction k)
    {
        var isEsc = k.Key == GameKey.Escape;
        if (!isEsc && !HudClean(rs))
        {
            return new Attempt(PopupRecent(rs) ? StepOutcome.PreconditionTransient : StepOutcome.PreconditionLost, "key.hudNotClean", Subject: step.Id);
        }
        BudgetCheck(rs, 0, isEsc ? 1 : 0, step.Id);
        SettleLate(rs, step);
        WaitPacing(rs);
        var expected = StepValidation.AllUiEvents(step.Expect);
        var rep = _gameIo.PressKey(new KeyRequest(step.Id, k.Key, expected), rs.Ct);
        AccountKey(rs, rep, k.Key);
        JournalKey(rs, rep, k.Key, "step");
        if (isEsc)
        {
            RecordEsc(rs, rep.Marks);
        }
        if (rep.Disposition == InputDisposition.UserTookOver && rep.Mode == InputMode.Foreground && rep.Marks.ActionPostedUtc is not null)
        {
            // foreground mode: the user switched away after our KEYDOWN; the key was delivered, so the log decides whether
            // it worked before it is pressed again (SPEC 7.3b, safety review 2026-11-02)
            var delivered = Collect(rs, rep.Marks, step.Expect, StepTimeout(step), ForbiddenEarly(rep.Marks));
            return ClassifyKey(rs, step, k.Key, rep, delivered);
        }
        if (rep.Disposition != InputDisposition.Sent)
        {
            return MapKeyDisposition(rs, rep, step);
        }
        if (!rep.FocusLogged && rep.Mode == InputMode.Cover)
        {
            return FocusFailure(rs, step, delivered: true);
        }
        var win = Collect(rs, rep.Marks, step.Expect, StepTimeout(step), ForbiddenEarly(rep.Marks));
        return ClassifyKey(rs, step, k.Key, rep, win);
    }

    private Attempt MapKeyDisposition(RunState rs, KeyReport rep, StepDefinition step)
    {
        return rep.Disposition switch
        {
            InputDisposition.NotSent => MapNotSent(rs, rep.Abort,
                new ReadinessQuery(InputKind.Key, null, false, LetterKey: step.Action is KeyAction { Key: not GameKey.Escape })),
            InputDisposition.RateLimited => new Attempt(StepOutcome.RateLimited, "io.rateLimited"),
            InputDisposition.Stopped => new Attempt(StepOutcome.Stopped, "io.stopped"),
            InputDisposition.GameGone => new Attempt(StepOutcome.GameExited, "io.gameGone"),
            InputDisposition.IntegrityMismatch => new Attempt(StepOutcome.IntegrityMismatch, "io.integrity", Subject: step.Id),
            InputDisposition.Refused => new Attempt(StepOutcome.Refused, "io.refused." + rep.Abort),
            InputDisposition.FocusFailed => FocusFailure(rs, step, delivered: false),
            InputDisposition.UserTookOver => new Attempt(StepOutcome.UserActive, "io.userTookOver"),
            InputDisposition.AbortedAfterFocus => MapAborted(rep.Abort, null),
            InputDisposition.Incident => new Attempt(StepOutcome.RestoreFailed, "io.keyIncident", Subject: step.Id),
            _ => new Attempt(StepOutcome.NotSent, "io." + rep.Disposition),
        };
    }

    private Attempt FocusFailure(RunState rs, StepDefinition step, bool delivered)
    {
        return JobScope.Recent(rs.Job.FocusFailures, Mono) >= 2
            ? new Attempt(StepOutcome.FocusFailed, "focus.failed.twice", Subject: step.Id, Delivered: delivered)
            : new Attempt(StepOutcome.FocusFailedOnce, "focus.failed.once", Delivered: delivered);
    }

    private Attempt MapNotSent(RunState rs, AbortReason abort, ReadinessQuery? query)
    {
        switch (abort)
        {
            case AbortReason.GameForeground or AbortReason.GameIconic or AbortReason.ForegroundLost or AbortReason.UserInput:
                return new Attempt(StepOutcome.UserActive, "notSent." + abort);
            case AbortReason.StopRequested:
                return new Attempt(StepOutcome.Stopped, "notSent.stop");
            case AbortReason.IdentityChanged:
                return new Attempt(StepOutcome.IdentityChanged, "notSent.identity");
            case AbortReason.KeyboardLayoutChanged:
                // the game is ready; only the layout differs from the one at connect (bounded in DecideWait, audit R1.3)
                return new Attempt(StepOutcome.NotSent, KeyboardLayoutDetail);
        }
        try
        {
            var r = _gameIo.CheckReadiness(query ?? new ReadinessQuery(InputKind.Key, null, false));
            if (!r.Ready)
            {
                return new Attempt(MapReadiness(r.Reason), "notSent." + r.Reason);
            }
        }
        catch
        {
            // fall through
        }
        return new Attempt(StepOutcome.NotSent, "notSent." + abort);
    }

    private static StepOutcome MapReadiness(NotReadyReason reason) => reason switch
    {
        NotReadyReason.GameForeground or NotReadyReason.GameIconic or NotReadyReason.Cooldown
            or NotReadyReason.ForegroundSettling or NotReadyReason.UserInputRecent => StepOutcome.UserActive,
        NotReadyReason.SessionNotInteractive => StepOutcome.SessionUnavailable,
        NotReadyReason.ExtraGameWindow => StepOutcome.ExtraGameWindow,
        NotReadyReason.DisplayModeWrong => StepOutcome.DisplayModeWrong,
        NotReadyReason.Stopped => StepOutcome.Stopped,
        _ => StepOutcome.NotSent,
    };

    private static Attempt MapAborted(AbortReason abort, string? popupUi) => abort switch
    {
        AbortReason.LogGatePopup or AbortReason.SlidTemplateMismatch => new Attempt(StepOutcome.Popup, "aborted." + abort, UiSeen: popupUi),
        AbortReason.CursorClipChanged => new Attempt(StepOutcome.CursorClipChanged, "aborted.clip"),
        AbortReason.IdentityChanged => new Attempt(StepOutcome.IdentityChanged, "aborted.identity"),
        AbortReason.StopRequested => new Attempt(StepOutcome.Stopped, "aborted.stop"),
        AbortReason.GameForeground or AbortReason.GameIconic or AbortReason.ForegroundLost or AbortReason.UserInput => new Attempt(StepOutcome.UserActive, "aborted." + abort),
        _ => new Attempt(StepOutcome.AbortedAfterFocus, "aborted." + abort),
    };

    // ------------------------------------------------------------------ clicks

    private Attempt DoClick(RunState rs, ProcFrame f, StepDefinition step, string templateId, Expectation expect, PointI? offset)
    {
        var core = ClickCore(rs, f, step, templateId, StepValidation.AllUiEvents(expect), offset);
        if (core.Early is { } early)
        {
            return early;
        }
        var rep = core.Report!;
        if (rep.Disposition == InputDisposition.UserTookOver && rep.Mode == InputMode.Foreground && rep.DownPosted)
        {
            // foreground mode: the user switched away after our DOWN; the press was delivered, so the log decides whether
            // it worked before anything is pressed again (SPEC 7.3b, safety review 2026-11-02)
            return ClassifyClick(rs, step, templateId, rep, core.Center, expect);
        }
        if (rep.Disposition != InputDisposition.Sent)
        {
            return MapClickDisposition(rs, step, templateId, rep, core.Frame);
        }
        if (!rep.FocusLogged && rep.Mode == InputMode.Cover)
        {
            return FocusFailure(rs, step, delivered: rep.DownPosted);
        }
        return ClassifyClick(rs, step, templateId, rep, core.Center, expect);
    }

    /// <summary>
    /// Locate, guard and slide-click. With <paramref name="offset"/> the click lands at the found centre + offset (a grid
    /// cell placed from a located anchor); deny zones and readiness use that point, the slid check verifies the template
    /// at its own centre. The returned Center is the intended click point (classification aim).
    /// </summary>
    private (Attempt? Early, ClickReport? Report, PointI Center, Frame? Frame) ClickCore(RunState rs, ProcFrame f, StepDefinition step, string templateId, IReadOnlyList<UiExpectation> expectedUi, PointI? offset = null)
    {
        var def = _templates.Get(templateId);
        if (def is null)
        {
            return (new Attempt(StepOutcome.NeedsSurvey, "template.missing:" + templateId, Subject: templateId), null, default, null);
        }
        if (!_templates.IsClickable(templateId))
        {
            var code = (_templates.IsSuspect(templateId) ? "template.suspect:" : "template.provisional:") + templateId;
            return (new Attempt(StepOutcome.NeedsSurvey, code, Subject: templateId), null, default, null);
        }
        if (def.Tags.HasFlag(TargetTags.Forbidden))
        {
            return (new Attempt(StepOutcome.Refused, "target.forbidden:" + templateId), null, default, null);
        }
        if (!SafeSlideSwitch())
        {
            return (new Attempt(StepOutcome.Refused, "slideClick.off"), null, default, null);
        }
        if (offset is { } requested && requested != default && IsCommitTemplate(def))
        {
            // a commit always lands on its own verified button
            return (new Attempt(StepOutcome.Refused, "click.commitOffset", Subject: templateId), null, default, null);
        }
        rs.TemplatesUsed.Add(templateId);
        var hudTarget = def.Hud is not null || (def.Tags & (TargetTags.HudAnchor | TargetTags.Toggle)) != 0;
        var loc = LocateTwice(rs, def, hudTarget);
        if (loc.Early is { } e)
        {
            return (e, null, default, null);
        }
        var center = loc.Center;
        var frame = loc.Frame!;
        var shift = offset ?? default;
        var point = center.Offset(shift.X, shift.Y);
        if (!HudTemplates.Client.Contains(point))
        {
            return (new Attempt(StepOutcome.Refused, "click.offsetOutsideClient", Subject: templateId), null, default, null);
        }
        var zones = ZonesFor(hudTarget);
        foreach (var z in zones)
        {
            if (z.ClientRect.Inflate(HardRules.DenyZoneRefusePx).Contains(point))
            {
                return (new Attempt(StepOutcome.LayoutChanged, "denyZone:" + z.Id, Subject: templateId), null, default, null);
            }
        }
        var near = zones.Any(z => Distance(point, z.ClientRect) <= HardRules.DenyZoneGuardPx);
        var notReady = WaitClickReadiness(rs, def, ref center, ref frame, near, shift, out var templateCenter);
        if (notReady is not null)
        {
            return (notReady, null, default, null);
        }
        if (shift != default)
        {
            Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "click.offset"), ("template", def.Id), ("dx", shift.X), ("dy", shift.Y));
        }
        BudgetCheck(rs, 1, 0, step.Id);
        SettleLate(rs, step);
        WaitPacing(rs);
        // center = click point (an offset cell or an aim inside the button); templateCenter = the template's found centre;
        // frame = the capture that centre was located in (after any re-locate), GameIO's reference for the patch rule.
        var patchWhy = PatchRefusal(rs, f, def, step, shift, near);
        var patch = patchWhy is null;
        var req = new ClickRequest(step.Id, templateId, center, def.FireOn, frame.LogAt, frame.CapturedAtMonotonic, expectedUi, near,
            templateCenter == center ? null : templateCenter, NavFixedClick(def, step, shift), patch ? frame : null, patch,
            EndsOnGridClick: step.Flags.HasFlag(StepFlags.QuickAttribution) && !step.Flags.HasFlag(StepFlags.Commit) && !IsCommitTemplate(def));
        var rep = _gameIo.SlideClick(req, rs.Ct);
        AccountClick(rs, rep);
        JournalClick(rs, templateId, rep, near, req.NavFixed, req.PatchVerify, patchWhy, step.Flags);
        return (null, rep, templateCenter.Offset(shift.X, shift.Y), frame);
    }

    /// <summary>
    /// The engine's opt-in for GameIO's cross-monitor rule (SPEC 7.3, EVIDENCE c13): a clickable template tagged NavFixed,
    /// neither Commit (by tag or ui-map safety) nor Forbidden, no gate, no anchor, pressed at its own centre (an aim
    /// inside the button is fine, a step offset such as a favourites slot is not), and not a commit step. GameIO checks
    /// the catalog again; everything else keeps the strict slid verification.
    /// </summary>
    private bool NavFixedClick(TemplateDefinition def, StepDefinition step, PointI shift) =>
        def.Tags.HasFlag(TargetTags.NavFixed)
        && (def.Tags & (TargetTags.Commit | TargetTags.Forbidden)) == 0
        && def.Gate == AutomationSwitch.None
        && def.AnchorId is null
        && def.OffsetFromAnchor is null
        && shift == default
        && !step.Flags.HasFlag(StepFlags.Commit)
        && !IsCommitTemplate(def)
        && _templates.IsClickable(def.Id);

    /// <summary>
    /// The engine's opt-in for GameIO's off-screen anchor patch rule (SPEC 7.3, live run 2026-10-14: ScanRecipeList's
    /// 2nd-row cards slid the list anchor above the screen): a clickable template tagged ScreenAnchor, neither Commit (by
    /// tag or ui-map safety) nor Forbidden, no gate, no anchor of its own, pressed at a step offset larger than an aim (a
    /// list card or grid cell), not near a deny zone, not a commit step, and only while neither the job, the root nor the
    /// current procedure commits or is gated. CraftLoop's favourites cells therefore keep the strict slid check (they pick
    /// what the Make commit crafts). GameIO checks the catalog, the located frame and the geometry again.
    /// </summary>
    private bool PatchClick(RunState rs, ProcFrame f, TemplateDefinition def, StepDefinition step, PointI shift, bool near) =>
        PatchRefusal(rs, f, def, step, shift, near) is null;

    /// <summary>
    /// Why the off-screen anchor patch rule is off for this click (the first failing condition, a code), or null when it
    /// is on. Journaled with every click (2026-10-21: the live slot clicks reported no patch although every condition
    /// held in the tests).
    /// </summary>
    private string? PatchRefusal(RunState rs, ProcFrame f, TemplateDefinition def, StepDefinition step, PointI shift, bool near)
    {
        if (!def.Tags.HasFlag(TargetTags.ScreenAnchor))
        {
            return "notScreenAnchor";
        }
        if ((def.Tags & (TargetTags.Commit | TargetTags.Forbidden)) != 0 || IsCommitTemplate(def))
        {
            return "commitTemplate";
        }
        if (def.Gate != AutomationSwitch.None)
        {
            return "templateGated";
        }
        if (def.AnchorId is not null || def.OffsetFromAnchor is not null)
        {
            return "derivedTemplate";
        }
        if (Math.Abs(shift.X) <= HardRules.MaxAimOffsetXPx && Math.Abs(shift.Y) <= HardRules.MaxAimOffsetYPx)
        {
            return "smallOffset";
        }
        if (near)
        {
            return "nearDenyZone";
        }
        if (step.Flags.HasFlag(StepFlags.Commit))
        {
            return "commitStep";
        }
        // the procedures decide, not the job (2026-10-15: a Craft job's read-only list walk was refused 29 times when the
        // slide hid the list anchor; the walk ends only on the card whose log tip names the wanted item, and the commit
        // procedure crafting.loop, gated and Commit, keeps the strict check), except a selection the job verifies before
        // its commit (2026-10-21)
        if (!step.Flags.HasFlag(StepFlags.SelectionVerified)
            && (rs.Root.Kind == ProcedureKind.Commit || f.Def.Kind == ProcedureKind.Commit
                || rs.Root.Gate != AutomationSwitch.None || f.Def.Gate != AutomationSwitch.None))
        {
            return "commitProcedure";
        }
        return _templates.IsClickable(def.Id) ? null : "notClickable";
    }

    private (Attempt? Early, PointI Center, Frame? Frame) LocateTwice(RunState rs, TemplateDefinition def, bool hudTarget)
    {
        var centers = new PointI?[2];
        Frame? last = null;
        var misses = 0;
        for (var k = 0; k < 2; k++)
        {
            if (k == 1 && last is not null)
            {
                Sleep(rs, HardRules.StableCaptureGap - (Mono - last.CapturedAtMonotonic));
            }
            // the pair's second capture waits only the F07 gap slept above, not the 2/s throttle (2026-10-31: ~0.59 s a locate)
            var cap = k == 1 && _screen is ScreenService pair ? pair.CapturePairSecond(rs.Ct) : _screen.Capture(rs.Ct);
            if (!cap.Ok)
            {
                return (CaptureProblemAttempt(cap.Problem), default, null);
            }
            var frame = cap.Frame!;
            rs.LastFrame = frame;
            last = frame;
            if (hudTarget)
            {
                if (!HudClean(rs))
                {
                    return (new Attempt(PopupRecent(rs) ? StepOutcome.PreconditionTransient : StepOutcome.PreconditionLost, "hud.notClean", Subject: def.Id), default, null);
                }
                var hc = _vision.ClassifyHud(frame);
                if (hc.State is HudState.Covered or HudState.Absent)
                {
                    return (new Attempt(StepOutcome.PreconditionLost, "hud." + hc.State, Subject: def.Id), default, null);
                }
                if (!hc.SignatureOk || hc.State == HudState.Unknown)
                {
                    return (new Attempt(RecentHudInput(rs) ? StepOutcome.PreconditionTransient : StepOutcome.PreconditionLost, "hud.signature", Subject: def.Id), default, null);
                }
            }
            var m = _vision.Locate(frame, def.Id);
            if (m.Found)
            {
                centers[k] = m.Center;
                continue;
            }
            switch (m.Failure)
            {
                case MatchFailure.OutsideRadius:
                    // a window that opened moments ago is still sliding in (live 2026-10-16: the Gather tab, found
                    // shifted 0.1 s after the Ability view's show, halted LayoutChanged): wait for it; still displaced
                    // once settled is a real layout change
                    return (PopupRecent(rs)
                        ? new Attempt(StepOutcome.PreconditionTransient, "template.animating:" + def.Id, Subject: def.Id)
                        : new Attempt(StepOutcome.LayoutChanged, "template.outsideRadius", Subject: def.Id), default, null);
                case MatchFailure.NotClickable:
                    return (new Attempt(StepOutcome.NeedsSurvey, "template.notClickable:" + def.Id, Subject: def.Id), default, null);
                case MatchFailure.TemplateMissing:
                    return (new Attempt(StepOutcome.NeedsSurvey, "template.missing:" + def.Id, Subject: def.Id), default, null);
                case MatchFailure.FrameInvalid:
                    return (new Attempt(StepOutcome.PreconditionTransient, "frame.invalid", Subject: def.Id), default, null);
                default:
                    misses++;
                    break;
            }
        }
        if (misses == 2)
        {
            return (new Attempt(StepOutcome.PreconditionLost, "template.notFound:" + def.Id, Subject: def.Id), default, null);
        }
        if (misses == 1)
        {
            return (new Attempt(StepOutcome.PreconditionTransient, "template.flicker:" + def.Id, Subject: def.Id), default, null);
        }
        if (centers[0]!.Value.ChebyshevDistanceTo(centers[1]!.Value) > def.EffectiveMotionTolerancePx)
        {
            return (new Attempt(StepOutcome.PreconditionTransient, "template.moving:" + def.Id, Subject: def.Id), default, null);
        }
        return (null, centers[1]!.Value, last);
    }

    private static Attempt CaptureProblemAttempt(CaptureProblem p) => p switch
    {
        CaptureProblem.Iconic or CaptureProblem.Cloaked => new Attempt(StepOutcome.UserActive, "capture." + p),
        CaptureProblem.NotConnected => new Attempt(StepOutcome.GameExited, "capture." + p),
        CaptureProblem.WrongSize => new Attempt(StepOutcome.DisplayModeWrong, "capture." + p),
        _ => new Attempt(StepOutcome.PreconditionTransient, "capture." + p),
    };

    private List<DenyZone> ZonesFor(bool hudTarget)
    {
        var result = new List<DenyZone>();
        try
        {
            var map = _uiMap.Map;
            var top = _log.State.AdvisoryTop;
            var hudScreens = map.Screens.Where(s => s.IsHud).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var topScreens = top is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : map.Screens.Where(s => string.Equals(s.LogUiName, top, StringComparison.Ordinal)).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var z in map.DenyZones)
            {
                if ((hudTarget && hudScreens.Contains(z.ScreenId)) || topScreens.Contains(z.ScreenId))
                {
                    result.Add(z);
                }
            }
        }
        catch
        {
            // no map: no zones (templates still have radius and slid verification)
        }
        return result;
    }

    private static double Distance(PointI p, RectI r)
    {
        var dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - (r.Right - 1));
        var dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - (r.Bottom - 1));
        return Math.Sqrt(((double)dx * dx) + ((double)dy * dy));
    }

    /// <summary>
    /// Aim points inside a button, nearest first (x up to 18 px, y up to 10 px: well inside the ~55 px HUD icons, and
    /// within <see cref="HardRules.MaxAimOffsetXPx"/> / <see cref="HardRules.MaxAimOffsetYPx"/>, GameIO's aim limit). Used
    /// only when the centre itself cannot be slid under the cursor (live run 2026-10-14: the menu toggle at the right edge
    /// was 14 px out of reach of the monitor rule).
    /// </summary>
    private static readonly (int X, int Y)[] AimOffsets =
    [
        (-6, 0), (6, 0), (0, -6), (0, 6), (-12, 0), (12, 0), (0, -10), (0, 10), (-12, -8), (12, -8), (-12, 8), (12, 8),
        (-18, 0), (18, 0), (-18, -10), (18, -10), (-18, 10), (18, 10),
    ];

    private Attempt? WaitClickReadiness(RunState rs, TemplateDefinition def, ref PointI center, ref Frame frame, bool near, PointI shift, out PointI templateCenter)
    {
        templateCenter = center;
        var start = Mono;
        var relocated = false;
        var aim = (X: 0, Y: 0);
        NotReadyReason? lastWait = null;
        while (true)
        {
            rs.Ct.ThrowIfCancellationRequested();
            var point = center.Offset(shift.X, shift.Y);
            // only a fixed HUD navigation target may slide onto another monitor (the cross-monitor NavFixed rule); any
            // other press waits for the cursor to rest on the game's monitor instead of failing its slid check
            var crossOk = def.Tags.HasFlag(TargetTags.NavFixed) && shift == default;
            var r = _gameIo.CheckReadiness(new ReadinessQuery(InputKind.Click, point.Offset(aim.X, aim.Y), near, crossOk));
            if (!r.Ready && r.Reason is NotReadyReason.MonitorRule or NotReadyReason.SlideTooFar)
            {
                foreach (var off in AimOffsets)
                {
                    var alt = _gameIo.CheckReadiness(new ReadinessQuery(InputKind.Click, point.Offset(off.X, off.Y), near, crossOk));
                    if (alt.Ready)
                    {
                        if (off != aim)
                        {
                            Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "aim.offset"), ("template", def.Id), ("dx", off.X), ("dy", off.Y));
                        }
                        aim = off;
                        r = alt;
                        break;
                    }
                }
            }
            if (r.Ready)
            {
                if (Mono - frame.CapturedAtMonotonic <= HardRules.MaxLocateAge)
                {
                    templateCenter = center;
                    center = point.Offset(aim.X, aim.Y);
                    return null;
                }
                if (relocated && Mono - frame.CapturedAtMonotonic > HardRules.MaxLocateAge * 2)
                {
                    return new Attempt(StepOutcome.NotSent, "locate.tooOld");
                }
                var cap = _screen.Capture(rs.Ct);
                if (!cap.Ok)
                {
                    return CaptureProblemAttempt(cap.Problem);
                }
                rs.LastFrame = cap.Frame;
                var m = _vision.Locate(cap.Frame!, def.Id);
                if (!m.Found)
                {
                    return m.Failure == MatchFailure.OutsideRadius
                        ? (PopupRecent(rs) ? new Attempt(StepOutcome.PreconditionTransient, "template.animating:" + def.Id, Subject: def.Id) : new Attempt(StepOutcome.LayoutChanged, "template.outsideRadius", Subject: def.Id))
                        : new Attempt(StepOutcome.PreconditionTransient, "relocate.notFound:" + def.Id, Subject: def.Id);
                }
                if (m.Center.ChebyshevDistanceTo(center) > def.EffectiveMotionTolerancePx)
                {
                    return new Attempt(StepOutcome.PreconditionTransient, "relocate.moved:" + def.Id, Subject: def.Id);
                }
                frame = cap.Frame!;
                center = m.Center;
                relocated = true;
                continue;
            }
            var o = MapReadiness(r.Reason);
            if (lastWait != r.Reason)
            {
                // Make readiness waits visible (live run 2026-10-14: silent waits looked like hangs).
                // incident 27 (2026-10-27): 40 minutes of "CoverRejected" with no record of which cover rule refused
                Write(rs, JournalKind.Wait, rs.CurrentStep, ("state", "start"), ("reason", "ready." + r.Reason), ("template", def.Id),
                    ("coverClass", r.Cover?.ClassName), ("coverReject", r.Cover is { Reject: not CoverRejectReason.None } c ? c.Reject.ToString() : null));
                lastWait = r.Reason;
            }
            if (OutcomePolicy.ClassOf(o) != OutcomeClass.Wait || Mono - start >= ReadinessWindow)
            {
                return new Attempt(o, "ready." + r.Reason, Subject: def.Id);
            }
            Sleep(rs, ReadinessPoll);
        }
    }

    // ------------------------------------------------------------------ drags (SPEC 7.3a)

    /// <summary>
    /// Locate the anchor twice, place the press point (found centre + offset) and the release point (+ delta) inside the
    /// client and outside every deny zone, wait until both are reachable, then <see cref="IGameIo.SlideDrag"/> (one input
    /// batch, one click of the budget). A delivered drag is never retried here: its effect (how far a list moved) is
    /// checked by the caller on a capture, so it ends Ok with "drag.done" or, when it was cut short (cursor moved, forced
    /// slide-back), "drag.partial". A UI our release opened is classified like a click's (forbidden, unknown, ...).
    /// </summary>
    private Attempt DoDrag(RunState rs, ProcFrame f, StepDefinition step, DragAction a)
    {
        var def = _templates.Get(a.TemplateId);
        if (def is null)
        {
            return new Attempt(StepOutcome.NeedsSurvey, "template.missing:" + a.TemplateId, Subject: a.TemplateId);
        }
        if (!_templates.IsClickable(a.TemplateId))
        {
            var code = (_templates.IsSuspect(a.TemplateId) ? "template.suspect:" : "template.provisional:") + a.TemplateId;
            return new Attempt(StepOutcome.NeedsSurvey, code, Subject: a.TemplateId);
        }
        if (def.Tags.HasFlag(TargetTags.Forbidden))
        {
            return new Attempt(StepOutcome.Refused, "target.forbidden:" + a.TemplateId);
        }
        if (IsCommitTemplate(def))
        {
            return new Attempt(StepOutcome.Refused, "drag.commitTemplate", Subject: a.TemplateId);
        }
        if (!SafeSlideSwitch())
        {
            return new Attempt(StepOutcome.Refused, "slideClick.off");
        }
        rs.TemplatesUsed.Add(def.Id);
        var hudTarget = def.Hud is not null || (def.Tags & (TargetTags.HudAnchor | TargetTags.Toggle)) != 0;
        var loc = LocateTwice(rs, def, hudTarget);
        if (loc.Early is { } e)
        {
            return e;
        }
        var center = loc.Center;
        var frame = loc.Frame!;
        var start = center.Offset(a.Offset.X, a.Offset.Y);
        var end = start.Offset(a.Delta.X, a.Delta.Y);
        if (!HudTemplates.Client.Contains(start) || !HudTemplates.Client.Contains(end))
        {
            return new Attempt(StepOutcome.Refused, "drag.outsideClient", Subject: def.Id);
        }
        var zones = ZonesFor(hudTarget);
        foreach (var z in zones)
        {
            var inflated = z.ClientRect.Inflate(HardRules.DenyZoneRefusePx);
            if (inflated.Contains(start) || inflated.Contains(end))
            {
                return new Attempt(StepOutcome.LayoutChanged, "denyZone:" + z.Id, Subject: def.Id);
            }
        }
        var near = zones.Any(z => Distance(start, z.ClientRect) <= HardRules.DenyZoneGuardPx || Distance(end, z.ClientRect) <= HardRules.DenyZoneGuardPx);
        var notReady = WaitDragReadiness(rs, def, ref center, ref frame, near, a);
        if (notReady is not null)
        {
            return notReady;
        }
        BudgetCheck(rs, 1, 0, step.Id);
        SettleLate(rs, step);
        WaitPacing(rs);
        var patch = PatchClick(rs, f, def, step, a.Offset, near);
        var req = new DragRequest(step.Id, def.Id, center.Offset(a.Offset.X, a.Offset.Y), center, a.Delta, frame.LogAt, frame.CapturedAtMonotonic, near,
            patch ? frame : null, patch);
        var rep = _gameIo.SlideDrag(req, rs.Ct);
        var press = rep.Press;
        AccountClick(rs, press);
        JournalDrag(rs, def.Id, rep, a.Delta, near, req.PatchVerify);
        if (press.Disposition != InputDisposition.Sent)
        {
            return MapClickDisposition(rs, step, def.Id, press, frame);
        }
        if (!press.FocusLogged && press.Mode == InputMode.Cover)
        {
            return FocusFailure(rs, step, delivered: press.DownPosted);
        }
        var win = Collect(rs, press.Marks, Expectation.Nothing, HardRules.AttributionWindow, ForbiddenEarly(press.Marks));
        var decisive = EvaluateClick(rs, step, def.Id, press, rep.Release, Expectation.Nothing, win, settled: false, uiOnly: true);
        if (decisive is not null)
        {
            return decisive;
        }
        return new Attempt(StepOutcome.Ok, rep.Completed ? "drag.done" : "drag.partial", Phase: press.Phase, Delivered: true, Subject: def.Id);
    }

    /// <summary>
    /// Passive readiness for both ends of a drag (no aim shifts: the press point is exact). Re-locates the anchor when the
    /// locating capture got older than 1.5 s, like a click.
    /// </summary>
    private Attempt? WaitDragReadiness(RunState rs, TemplateDefinition def, ref PointI center, ref Frame frame, bool near, DragAction a)
    {
        var start = Mono;
        var relocated = false;
        NotReadyReason? lastWait = null;
        while (true)
        {
            rs.Ct.ThrowIfCancellationRequested();
            var from = center.Offset(a.Offset.X, a.Offset.Y);
            var r = _gameIo.CheckReadiness(new ReadinessQuery(InputKind.Click, from, near, CrossMonitorAllowed: false, Drag: true));
            if (r.Ready)
            {
                r = _gameIo.CheckReadiness(new ReadinessQuery(InputKind.Click, from.Offset(a.Delta.X, a.Delta.Y), near, CrossMonitorAllowed: false, Drag: true));
            }
            if (r.Ready)
            {
                if (Mono - frame.CapturedAtMonotonic <= HardRules.MaxLocateAge)
                {
                    return null;
                }
                if (relocated && Mono - frame.CapturedAtMonotonic > HardRules.MaxLocateAge * 2)
                {
                    return new Attempt(StepOutcome.NotSent, "locate.tooOld");
                }
                var cap = _screen.Capture(rs.Ct);
                if (!cap.Ok)
                {
                    return CaptureProblemAttempt(cap.Problem);
                }
                rs.LastFrame = cap.Frame;
                var m = _vision.Locate(cap.Frame!, def.Id);
                if (!m.Found)
                {
                    return m.Failure == MatchFailure.OutsideRadius
                        ? (PopupRecent(rs) ? new Attempt(StepOutcome.PreconditionTransient, "template.animating:" + def.Id, Subject: def.Id) : new Attempt(StepOutcome.LayoutChanged, "template.outsideRadius", Subject: def.Id))
                        : new Attempt(StepOutcome.PreconditionTransient, "relocate.notFound:" + def.Id, Subject: def.Id);
                }
                if (m.Center.ChebyshevDistanceTo(center) > def.EffectiveMotionTolerancePx)
                {
                    return new Attempt(StepOutcome.PreconditionTransient, "relocate.moved:" + def.Id, Subject: def.Id);
                }
                frame = cap.Frame!;
                center = m.Center;
                relocated = true;
                continue;
            }
            var o = MapReadiness(r.Reason);
            if (lastWait != r.Reason)
            {
                Write(rs, JournalKind.Wait, rs.CurrentStep, ("state", "start"), ("reason", "ready." + r.Reason), ("template", def.Id), ("input", "drag"),
                    ("coverClass", r.Cover?.ClassName), ("coverReject", r.Cover is { Reject: not CoverRejectReason.None } c ? c.Reject.ToString() : null));
                lastWait = r.Reason;
            }
            if (OutcomePolicy.ClassOf(o) != OutcomeClass.Wait || Mono - start >= ReadinessWindow)
            {
                return new Attempt(o, "ready." + r.Reason, Subject: def.Id);
            }
            Sleep(rs, ReadinessPoll);
        }
    }

    private void JournalDrag(RunState rs, string templateId, DragReport rep, PointI delta, bool near, bool patchVerify)
    {
        var p = rep.Press;
        Write(rs, JournalKind.Input, rs.CurrentStep, ("kind", "drag"), ("mode", p.Mode), ("template", templateId), ("disposition", p.Disposition),
            ("abort", p.Abort), ("phase", p.Phase), ("focusMsgs", p.FocusMessagesSent), ("focusLogged", p.FocusLogged),
            ("down", p.DownPosted), ("up", p.UpPosted), ("steps", rep.StepsPlanned), ("stepsDone", rep.StepsDone),
            ("dragDx", delta.X), ("dragDy", delta.Y), ("unfocusSlid", p.UnfocusSentWhileSlid), ("unfocusSkipped", p.UnfocusSkipped),
            ("nearDenyZone", near), ("patchVerify", patchVerify), ("completed", rep.Completed), ("inputs", rs.Inputs), ("clicks", rs.Clicks));
        JournalSlideAndIncident(rs, p);
    }

    private Attempt MapClickDisposition(RunState rs, StepDefinition step, string templateId, ClickReport rep, Frame? frame)
    {
        // foreground mode: the user's own input reached the slid game (a click there hits our target, which the slide put
        // under the cursor): that is the user's activity, resumed from the latest step whose precondition holds, never a
        // failed press repeated as such (SPEC 7.3b, safety review 2026-11-02)
        if (rep.Mode == InputMode.Foreground && (rep.Cursor.UserInput || rep.Cursor.UserButton)
            && rep.Disposition is InputDisposition.AbortedAfterFocus or InputDisposition.NotSent or InputDisposition.UserTookOver)
        {
            return new Attempt(StepOutcome.UserActive, "aborted.userInputInGame", Phase: rep.Phase, Subject: templateId, Delivered: rep.DownPosted);
        }
        var att = rep.Disposition switch
        {
            InputDisposition.NotSent => MapNotSent(rs, rep.Abort,
                new ReadinessQuery(InputKind.Click, ExpectedClickPoint(step, templateId), false, Drag: step.Action is DragAction)),
            InputDisposition.RateLimited => new Attempt(StepOutcome.RateLimited, "io.rateLimited"),
            InputDisposition.Stopped => new Attempt(StepOutcome.Stopped, "io.stopped"),
            InputDisposition.GameGone => new Attempt(StepOutcome.GameExited, "io.gameGone"),
            InputDisposition.IntegrityMismatch => new Attempt(StepOutcome.IntegrityMismatch, "io.integrity", Subject: templateId),
            InputDisposition.Refused => new Attempt(StepOutcome.Refused, "io.refused." + rep.Abort),
            InputDisposition.FocusFailed => FocusFailure(rs, step, delivered: false),
            InputDisposition.UserTookOver => new Attempt(StepOutcome.UserActive, "io.userTookOver"),
            InputDisposition.AbortedAfterFocus => MapAborted(rep.Abort, PopupUiSince(frame?.LogAt)),
            InputDisposition.Incident => MapIncident(rep.Incident, templateId),
            _ => new Attempt(StepOutcome.NotSent, "io." + rep.Disposition),
        };
        return att with { Phase = rep.Phase, Subject = att.Subject ?? templateId, Delivered = rep.DownPosted };
    }

    /// <summary>Expected client point of a click step: the template's expected centre plus the step's click offset.</summary>
    private PointI? ExpectedClickPoint(StepDefinition step, string templateId)
    {
        if (_templates.Get(templateId) is not { } def)
        {
            return null;
        }
        var shift = step.Action switch
        {
            ClickAction { Offset: { } o } => o,
            DragAction d => d.Offset,
            _ => default,
        };
        return def.ExpectedCenter.Offset(shift.X, shift.Y);
    }

    private static Attempt MapIncident(WindowIncident? inc, string templateId)
    {
        if (inc is null)
        {
            return new Attempt(StepOutcome.RestoreFailed, "incident.missing", Subject: templateId);
        }
        return inc.Kind switch
        {
            IncidentKind.SelfResize => new Attempt(StepOutcome.GameSelfResize, "incident." + inc.Kind, Subject: templateId),
            IncidentKind.RestoreFailed => new Attempt(StepOutcome.RestoreFailed, "incident." + inc.Kind, Subject: templateId),
            IncidentKind.CursorClip => new Attempt(StepOutcome.CursorClipChanged, "incident." + inc.Kind, Subject: templateId),
            IncidentKind.UndrainedSlideBack => new Attempt(StepOutcome.GameStoppedMidPress, "incident." + inc.Kind, Subject: templateId),
            IncidentKind.MovedByOthers or IncidentKind.MonitorVanished or IncidentKind.PrefsChanged =>
                new Attempt(StepOutcome.DisplayModeWrong, "incident." + inc.Kind, Subject: templateId),
            _ => inc.Restored
                ? new Attempt(StepOutcome.WindowIncidentRestored, "incident." + inc.Kind, Subject: templateId)
                : new Attempt(StepOutcome.RestoreFailed, "incident." + inc.Kind, Subject: templateId),
        };
    }

    /// <summary>The non-overlay UI logged since the locating capture (popup gate), if any.</summary>
    private string? PopupUiSince(LogPosition? from)
    {
        if (from is null)
        {
            return null;
        }
        string? name = null;
        foreach (var e in _log.EventsSince(from).OfType<UiLogEvent>())
        {
            if (IsOpenVerb(e.Verb) && e.UiName is { } n && _classifier.Classify(n) != UiKind.Overlay)
            {
                name = n;
            }
        }
        return name;
    }

    // ------------------------------------------------------------------ HUD toggles

    private Attempt DoToggle(RunState rs, ProcFrame f, StepDefinition step, HudState target)
    {
        var hc = _screen.ClassifyHudStable(rs.Ct);
        switch (hc.State)
        {
            case HudState.Unknown:
                return new Attempt(StepOutcome.PreconditionTransient, "hud.unknown");
            case HudState.Covered or HudState.Absent:
                return new Attempt(StepOutcome.PreconditionLost, "hud." + hc.State, Subject: step.Id);
        }
        if (hc.State == target)
        {
            Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "toggle.already"), ("hud", hc.State));
            return new Attempt(StepOutcome.Ok, "toggle.already");
        }
        var templateId = HudTemplates.ToggleOf(hc.State)!;
        if (_templates.Get(templateId) is null || !_templates.IsClickable(templateId))
        {
            if (step.Flags.HasFlag(StepFlags.Optional))
            {
                var index = Enumerable.Range(0, f.Def.Steps.Count).FirstOrDefault(k => ReferenceEquals(f.Def.Steps[k], step));
                SkipOptional(rs, f, index, "template.notClickable:" + templateId);
                return new Attempt(StepOutcome.Ok, "optional.skipped");
            }
            return new Attempt(StepOutcome.NeedsSurvey, "template.provisional:" + templateId, Subject: templateId);
        }
        var core = ClickCore(rs, f, step, templateId, []);
        if (core.Early is { } early)
        {
            return early;
        }
        var rep = core.Report!;
        rs.LastHudInputMono = Mono;
        if (rep.Disposition != InputDisposition.Sent)
        {
            return MapClickDisposition(rs, step, templateId, rep, core.Frame);
        }
        rs.HudToggled = true;
        if (!rep.FocusLogged && rep.Mode == InputMode.Cover)
        {
            return FocusFailure(rs, step, delivered: rep.DownPosted);
        }
        return ClassifyToggle(rs, step, templateId, rep, core.Center, target);
    }

    private Attempt ClassifyToggle(RunState rs, StepDefinition step, string templateId, ClickReport rep, PointI aim, HudState target)
    {
        var clickedAt = Mono;
        var win = Collect(rs, rep.Marks, Expectation.Nothing, HardRules.AttributionWindow, ForbiddenEarly(rep.Marks));
        var decisive = EvaluateClick(rs, step, templateId, rep, aim, Expectation.Nothing, win, settled: false, uiOnly: true);
        if (decisive is not null)
        {
            return decisive;
        }
        var hc = _screen.ClassifyHudStable(rs.Ct);
        while (hc.State == HudState.Unknown && Mono - clickedAt < HudAnimation)
        {
            Sleep(rs, TimeSpan.FromMilliseconds(500));
            hc = _screen.ClassifyHudStable(rs.Ct);
        }
        var phase = rep.Phase;
        if (hc.State == target)
        {
            return new Attempt(StepOutcome.Ok, "toggle.reached", Phase: phase, Delivered: true, Subject: templateId);
        }
        var pressBlock = PressBlockOf(rep, win);
        if (hc.State == HudState.Unknown)
        {
            return new Attempt(StepOutcome.Uncertain, "toggle.hudUnknown", Phase: phase, Delivered: true, Subject: templateId);
        }
        // foreground mode never has a press block (no unfocus, SPEC 7.3b): the HUD read decides, a press is not repeated blind
        return pressBlock is not null || rep.Mode == InputMode.Foreground
            ? new Attempt(StepOutcome.None, "toggle.unchanged", Phase: phase, Delivered: true, Subject: templateId)
            : new Attempt(StepOutcome.PressNotRegistered, "toggle.noPressBlock", Phase: phase, Delivered: true, Subject: templateId);
    }

    private Attempt DoRestore(RunState rs, ProcFrame f, StepDefinition step)
    {
        if (rs.InitialHud is not { } target)
        {
            Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "restore.noInitial"));
            return new Attempt(StepOutcome.Ok, "restore.noInitial");
        }
        var hc = _screen.ClassifyHudStable(rs.Ct);
        if (hc.State == target)
        {
            return new Attempt(StepOutcome.Ok, "restore.already");
        }
        if (hc.State is not (HudState.Collapsed or HudState.Expanded))
        {
            return hc.State == HudState.Unknown
                ? new Attempt(StepOutcome.PreconditionTransient, "hud.unknown")
                : new Attempt(StepOutcome.PreconditionLost, "hud." + hc.State, Subject: step.Id);
        }
        var templateId = HudTemplates.ToggleOf(hc.State)!;
        if (_templates.Get(templateId) is null || !_templates.IsClickable(templateId) || !SafeSlideSwitch())
        {
            AddPending(rs, "hud.restore." + target.ToString().ToLowerInvariant(), "template.notClickable:" + templateId);
            NotifyPendingOnce(rs);
            return new Attempt(StepOutcome.Ok, "restore.pending");
        }
        return DoToggle(rs, f, step, target);
    }

    private void RecordInitialHud(RunState rs, bool beforeReset)
    {
        try
        {
            var hc = _screen.ClassifyHudStable(rs.Ct);
            if (hc.SignatureOk && hc.State is HudState.Collapsed or HudState.Expanded)
            {
                rs.InitialHud = hc.State;
                rs.InitialHudPending = false;
                Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "hud.initial"), ("hud", hc.State), ("beforeReset", beforeReset));
            }
            else
            {
                rs.InitialHudPending = beforeReset;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            rs.InitialHudPending = beforeReset;
        }
    }

    private void JournalHud(RunState rs, string when)
    {
        try
        {
            var cap = _screen.Capture(rs.Ct);
            if (!cap.Ok)
            {
                Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "hud." + when), ("hud", "capture." + cap.Problem));
                return;
            }
            rs.LastFrame = cap.Frame;
            var hc = _vision.ClassifyHud(cap.Frame!);
            Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "hud." + when), ("hud", hc.State), ("signature", hc.SignatureOk));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // informational only
        }
    }

    // ------------------------------------------------------------------ reset (F20)

    private Attempt DoReset(RunState rs, ProcFrame f, StepDefinition step, ResetAction r)
    {
        var maxEsc = Math.Clamp(r.MaxEsc, 1, HardRules.MaxEscPerReset);
        JournalHud(rs, "reset.before");
        var noProgress = 0;
        var pressed = 0;
        var unloggedChecks = 0;
        TimeSpan? lastEsc = null;
        while (true)
        {
            rs.Ct.ThrowIfCancellationRequested();
            _log.Poll();
            string? unlogged = null;
            if (HudClean(rs))
            {
                // the log says clean, but a window opened before the companion started is not in the log (live
                // 2026-10-17: the Make view stayed open over a restart; the reset said clean and every click step found
                // the HUD covered, three times, then the procedure ended): the screen decides. An Esc-safe window
                // identified by its anchors gets an Esc like a logged one; anything else is the user's to close.
                unlogged = unloggedChecks++ < 2 && _log.State.AdvisoryTop is null ? UnloggedWindow(rs) : null;
                if (unlogged is null)
                {
                    JournalHud(rs, "reset.after");
                    return new Attempt(StepOutcome.Ok, pressed == 0 ? "reset.alreadyClean" : "reset.clean", Delivered: pressed > 0);
                }

                if (unlogged.Length == 0)
                {
                    return new Attempt(StepOutcome.UserUiOpen, "reset.unloggedWindow", Delivered: pressed > 0);
                }
            }
            var top = unlogged ?? _log.State.AdvisoryTop;
            if (top is not null)
            {
                var kind = _classifier.Classify(top);
                var ours = rs.Job.OpenedUis.Contains(top);
                switch (kind)
                {
                    case UiKind.Forbidden or UiKind.Social when !ours:
                        return new Attempt(StepOutcome.ForeignBlockingUi, "reset.foreign." + kind, UiSeen: top, Delivered: pressed > 0);
                    case UiKind.Busy:
                        return new Attempt(StepOutcome.Busy, "reset.busy", UiSeen: top, Delivered: pressed > 0);
                    case UiKind.Station:
                        return new Attempt(StepOutcome.CraftInProgress, "reset.station", UiSeen: top, Delivered: pressed > 0);
                }
                if (!_classifier.IsEscSafe(top) && !ours && !CloseUserUis())
                {
                    return new Attempt(StepOutcome.UserUiOpen, "reset.userUi", UiSeen: top, Delivered: pressed > 0);
                }
            }
            if (pressed >= maxEsc)
            {
                return new Attempt(StepOutcome.PreconditionLost, "reset.escExhausted", Delivered: true, Subject: step.Id);
            }
            if (pressed > 0)
            {
                var g = EvaluateGates(rs, f, step, GateScope.Key, checkLogQuiet: true, out var gd);
                if (g is { } go)
                {
                    return new Attempt(go, gd, Delivered: true, Subject: step.Id);
                }
            }
            if (lastEsc is { } le && Mono - le < TimeSpan.FromSeconds(1))
            {
                Sleep(rs, TimeSpan.FromSeconds(1) - (Mono - le));
            }
            BudgetCheck(rs, 0, 1, step.Id);
            SettleLate(rs, step);
            WaitPacing(rs);
            IReadOnlyList<UiExpectation> expected = top is null
                ? [UiExpectation.HiddenTop(null)]
                : [UiExpectation.HiddenTop(top), UiExpectation.HiddenTop(null)];
            var rep = _gameIo.PressKey(new KeyRequest(step.Id, GameKey.Escape, expected), rs.Ct);
            AccountKey(rs, rep, GameKey.Escape);
            JournalKey(rs, rep, GameKey.Escape, "reset");
            lastEsc = Mono;
            if (rep.Disposition != InputDisposition.Sent)
            {
                return MapKeyDisposition(rs, rep, step) with { Delivered = pressed > 0 };
            }
            if (!rep.FocusLogged && rep.Mode == InputMode.Cover)
            {
                return FocusFailure(rs, step, delivered: true);
            }
            pressed++;
            RecordEsc(rs, rep.Marks);
            var win = CollectEsc(rs, rep.Marks, expected);
            var ours2 = Ours(win.Events, rep.Marks);
            var opened = OpenedEvents(ours2).Where(e => _classifier.Classify(e.UiName!) != UiKind.Overlay).ToList();
            var hides = win.Events.OfType<UiLogEvent>().Where(e => e.Verb == UiVerb.HideTop && After(e, rep.Marks.ActionPosted)).ToList();
            // a window shown right after this Esc closed one, that is Esc-safe, was revealed, not opened (live 2026-10-16:
            // closing a dish's "Improved" popup re-showed the Make view under it): the reset goes on and closes it too
            var revealed = hides.Count > 0 && opened.Count > 0 && opened.All(e => UiCatalog.EscSafe.Contains(e.UiName!));
            if (opened.Count > 0 && !revealed)
            {
                if (rep.Mode == InputMode.Foreground && UserInGame(rep.UserInputDuring, rep.Marks))
                {
                    // foreground mode: the user's own input reached the game around our Esc; the window may be theirs
                    return new Attempt(StepOutcome.UserActive, "reset.userInput", UiSeen: opened[0].UiName, Delivered: true, Subject: step.Id);
                }
                return new Attempt(StepOutcome.EscOpenedUi, "reset.escOpened", UiSeen: opened[0].UiName, Delivered: true, Subject: opened[0].UiName);
            }
            if (revealed)
            {
                Write(rs, JournalKind.Decision, step.Id, ("event", "reset.revealed"), ("ui", opened[0].UiName));
            }
            if (hides.Count > 0)
            {
                noProgress = 0;
                continue;
            }
            noProgress++;
            if (noProgress >= 2)
            {
                return new Attempt(StepOutcome.PreconditionLost, "reset.noProgress", Delivered: true, Subject: step.Id);
            }
        }
    }

    /// <summary>
    /// When the log has no open window but the screen shows the HUD covered: the Esc-safe window whose screen anchors
    /// (from the readers' screen identities: the Make list title, the Gathering Helper's heading, ...) are all found on a
    /// fresh capture, by its log name; "" when the HUD is covered by something not identified; null when the screen
    /// agrees that the HUD is clean (or cannot tell: the log is trusted as before).
    /// </summary>
    private string? UnloggedWindow(RunState rs)
    {
        try
        {
            var hc = _screen.ClassifyHudStable(rs.Ct);
            if (hc.State is not (HudState.Covered or HudState.Absent))
            {
                return null;
            }

            var cap = _screen.Capture(rs.Ct);
            if (!cap.Ok)
            {
                return null;
            }

            rs.LastFrame = cap.Frame;
            var candidates = _readers.All
                .Select(r => r.Screen)
                .Where(sc => sc.LogUiName is { } n && _classifier.IsEscSafe(n) && sc.AnchorTemplateIds.Count > 0 && sc.AnchorTemplateIds.All(a => _templates.Get(a) is not null))
                .GroupBy(sc => sc.LogUiName + "|" + string.Join(",", sc.AnchorTemplateIds), StringComparer.Ordinal)
                .Select(g => g.First());
            foreach (var screen in candidates)
            {
                if (screen.AnchorTemplateIds.All(a => _vision.Locate(cap.Frame!, a).Found))
                {
                    Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "reset.unlogged"), ("ui", screen.LogUiName), ("hud", hc.State));
                    return screen.LogUiName;
                }
            }

            Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "reset.unloggedUnknown"), ("hud", hc.State));
            return string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not EndSignal and not HaltSignal)
        {
            return null;
        }
    }

    private bool CloseUserUis()
    {
        try
        {
            return _config.Current.Policy.CloseUserUisAtJobStart;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Esc used to close a UI (CloseThenRetry, popup, wrong UI, compensation). Returns true when a matching
    /// 'hide top ui' was logged. An Esc that opens a UI is a HALT (EscOpenedUi) unless this is compensation.
    /// </summary>
    private bool CloseEsc(RunState rs, string? ui, bool compensation)
    {
        if (_stop.IsStopRequested)
        {
            return false;
        }
        var blocked = EvaluateGates(rs, null, EscStep, GateScope.Key, checkLogQuiet: false, out var why);
        if (blocked is not null)
        {
            // an Esc is an input batch like any other: never sent past a closed gate (busy, user active, ...)
            if (compensation)
            {
                AddPending(rs, "compensate.esc", "gate." + blocked);
            }
            Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "esc.skipped"), ("reason", blocked), ("detail", why));
            return false;
        }
        if (!compensation)
        {
            BudgetCheck(rs, 0, 1, rs.CurrentStep);
            SettleLate(rs, null);
        }
        WaitPacing(rs);
        IReadOnlyList<UiExpectation> expected = ui is null
            ? [UiExpectation.HiddenTop(null)]
            : [UiExpectation.HiddenTop(ui), UiExpectation.HiddenTop(null)];
        var rep = _gameIo.PressKey(new KeyRequest(rs.CurrentStep ?? "esc", GameKey.Escape, expected), rs.Ct);
        AccountKey(rs, rep, GameKey.Escape);
        JournalKey(rs, rep, GameKey.Escape, compensation ? "compensate" : "close");
        if (rep.Disposition != InputDisposition.Sent)
        {
            return false;
        }
        RecordEsc(rs, rep.Marks);
        var win = CollectEsc(rs, rep.Marks, expected);
        var opened = OpenedEvents(Ours(win.Events, rep.Marks)).Where(e => _classifier.Classify(e.UiName!) != UiKind.Overlay).ToList();
        if (opened.Count > 0 && rep.Mode == InputMode.Foreground && UserInGame(rep.UserInputDuring, rep.Marks))
        {
            // foreground mode: the user's own input reached the game around our Esc; the window may be theirs (no HALT)
            Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "esc.userInput"), ("ui", opened[0].UiName));
            return false;
        }
        if (opened.Count > 0)
        {
            if (!compensation)
            {
                throw new HaltSignal(HaltCode.EscOpenedUi, opened[0].UiName, rs.CurrentStep, StepOutcome.EscOpenedUi);
            }
            Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "compensate.escOpenedUi"), ("ui", opened[0].UiName));
            return false;
        }
        return win.Met;
    }

    private EventWindow CollectEsc(RunState rs, InputMarks marks, IReadOnlyList<UiExpectation> expected)
    {
        var win = Collect(rs, marks, new Expectation(expected), EscWait, null, settle: false);
        if (win.Met)
        {
            Sleep(rs, TimeSpan.FromMilliseconds(300));
            _log.Poll();
            var from = marks.FocusSent ?? marks.ActionPosted;
            if (from is not null)
            {
                win = win with { Events = _log.EventsSince(from) };
            }
        }
        return win;
    }

    // ------------------------------------------------------------------ passive actions

    private Attempt DoWait(RunState rs, WaitAction w)
    {
        Sleep(rs, TimeSpan.FromMilliseconds(w.DurationMs));
        return new Attempt(StepOutcome.Ok, "wait.done");
    }

    private Attempt DoCapture(RunState rs, ProcFrame f, CaptureAction a)
    {
        var cap = _screen.Capture(rs.Ct);
        if (!cap.Ok)
        {
            return CaptureProblemAttempt(cap.Problem);
        }
        var frame = cap.Frame!;
        rs.LastFrame = frame;
        var category = rs.Root.Kind == ProcedureKind.Maintenance || f.Def.Kind == ProcedureKind.Maintenance
            ? ArtifactCategory.Calibration
            : ArtifactCategory.Survey;
        switch (a.Scope)
        {
            case CaptureScope.HudStripRight:
                SaveImage(rs, category, a.Label, frame, HudTemplates.HudStripRight);
                break;
            case CaptureScope.TemplateCrops:
                foreach (var id in CropTemplateIds(rs, frame))
                {
                    if (_templates.Get(id) is { } def && CropRect(def) is { } rect)
                    {
                        SaveImage(rs, category, a.Label + "-" + id, frame, rect);
                    }
                }
                break;
            case CaptureScope.FullFrameMasked:
                if (rs.Ctx.Kind != JobKind.Survey)
                {
                    return new Attempt(StepOutcome.Refused, "capture.fullFrame.notSurvey");
                }
                SaveImage(rs, ArtifactCategory.Survey, a.Label, frame, HudTemplates.Client);
                break;
            case CaptureScope.MakeView:
                if (!_log.State.AdvisoryStack.Contains("UI_LIFESKILL_MAKE_VIEW"))
                {
                    return new Attempt(StepOutcome.Refused, "capture.makeView.notOpen");
                }
                SaveImage(rs, ArtifactCategory.Diagnostics, a.Label, frame, HudTemplates.Client);
                break;
            case CaptureScope.GatherHelper:
                if (_log.State.AdvisoryTop != "UI_LIFESKILL_AUTO_COLLECT")
                {
                    return new Attempt(StepOutcome.Refused, "capture.helper.notOnTop");
                }
                SaveImage(rs, ArtifactCategory.Diagnostics, a.Label, frame, HudTemplates.Client);
                break;
        }
        return new Attempt(StepOutcome.Ok, "capture." + a.Scope);
    }

    private void SaveImage(RunState rs, ArtifactCategory category, string label, Frame frame, RectI rect)
    {
        var blackout = HudTemplates.ChatPanel.Intersect(rect) is { } chat ? new List<RectI> { chat } : [];
        var png = _vision.EncodePng(frame, rect, blackout);
        var path = _artifacts.SaveImage(category, rs.ArtifactGroup + "/" + SafeFiles.SanitizeName(label), png);
        Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "artifact"), ("category", category), ("saved", path is not null));
        if (path is not null)
        {
            rs.Artifacts.Add(path);
        }
    }

    private IReadOnlyList<string> CropTemplateIds(RunState rs, Frame frame)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            var id = _identifier.Identify(frame, _log.State);
            if (id.ScreenId is { } sid && _uiMap.GetScreen(sid) is { } screen)
            {
                foreach (var a in screen.AnchorTemplateIds)
                {
                    ids.Add(a);
                }
                foreach (var b in _uiMap.Map.Buttons.Where(b => b.ScreenId == sid && b.TemplateId is not null))
                {
                    ids.Add(b.TemplateId!);
                }
            }
        }
        catch
        {
            // fall back to the HUD templates
        }
        if (ids.Count == 0)
        {
            foreach (var t in _templates.All.Where(t => t.Hud is not null || (t.Tags & (TargetTags.HudAnchor | TargetTags.Toggle)) != 0))
            {
                ids.Add(t.Id);
            }
        }
        return ids.Where(i => _templates.Get(i) is not null).Take(16).ToList();
    }

    private void SurveyCapture(RunState rs, string? ui)
    {
        try
        {
            var cap = _screen.Capture(rs.Ct);
            if (!cap.Ok)
            {
                return;
            }
            rs.LastFrame = cap.Frame;
            var label = "unknown-" + SafeFiles.SanitizeName(ui ?? "ui");
            foreach (var id in CropTemplateIds(rs, cap.Frame!))
            {
                if (_templates.Get(id) is { } def && CropRect(def) is { } rect)
                {
                    SaveImage(rs, ArtifactCategory.Survey, label + "-" + id, cap.Frame!, rect);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // survey capture is best effort
        }
    }

    private Attempt DoRead(RunState rs, ReadAction a)
    {
        var res = _screen.Read(a.ReaderId, rs.Ct);
        rs.Reads[a.OutputKey] = res;
        // per-region codes, never the read text (item names): says why a value stayed Unknown (live 2026-10-14: whole
        // recipe panels read Unknown with no way to tell which region or which check failed)
        // one short code field per region ("r.<region>" = "<status>:<detail>:c<confidence>[:g<glyph score x1000>]") so
        // each stays a journal code; the glyph score (2026-10-15) is the weakest digit glyph of a number's template read
        var fields = new List<(string, object?)> { ("event", "read"), ("reader", a.ReaderId), ("status", res.Status), ("values", res.Values.Count) };
        foreach (var v in res.Values.Take(16))
        {
            var key = "r." + new string(v.RegionId.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.' ? ch : '_').ToArray());
            fields.Add((key.Length <= 41 ? key : key[..41], RegionCode(v)));
        }
        Write(rs, JournalKind.Decision, rs.CurrentStep, [.. fields]);
        return new Attempt(StepOutcome.Ok, "read." + res.Status);
    }

    /// <summary>
    /// The journal code of one read value: "&lt;status&gt;:&lt;detail&gt;:c&lt;confidence %&gt;", plus ":g&lt;glyph score x1000&gt;" when a digit
    /// template ran (e.g. "Unknown:digit-low-score:c0:g741"). Codes only: never the read text (item names).
    /// </summary>
    internal static string RegionCode(Contracts.Perception.ReadValue v)
    {
        var code = $"{v.Status}:{v.DetailCode}:c{Math.Round(v.Confidence * 100)}";
        return v.GlyphScore is { } g && double.IsFinite(g)
            ? code + ":g" + ((int)Math.Round(Math.Clamp(g, 0, 1) * 1000)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : code;
    }

    private Attempt DoCall(RunState rs, ProcFrame f, CallProcedureAction a)
    {
        var callee = _procedures.Get(a.ProcedureId);
        if (callee is null)
        {
            return new Attempt(StepOutcome.Refused, "procedure.missing:" + a.ProcedureId);
        }
        if (f.Depth + 1 > MaxCallDepth)
        {
            return new Attempt(StepOutcome.Refused, "call.depth");
        }
        RunSub(rs, f, callee);
        return new Attempt(StepOutcome.Ok, "call.done");
    }

    private Attempt DoNavigate(RunState rs, ProcFrame f, NavigateAction a)
    {
        var target = _uiMap.GetScreen(a.ScreenId);
        if (target is null)
        {
            return new Attempt(StepOutcome.NeedsSurvey, "nav.unknownScreen:" + a.ScreenId);
        }
        var id = IdentifyNow(rs);
        if (id.Confidence == IdentificationConfidence.Unknown || id.ScreenId is null)
        {
            RunSub(rs, f, InternalReset);
            id = IdentifyNow(rs);
            if (id.Confidence == IdentificationConfidence.Unknown || id.ScreenId is null)
            {
                return new Attempt(StepOutcome.PreconditionLost, "nav.unidentified", Subject: a.ScreenId);
            }
        }
        if (string.Equals(id.ScreenId, target.Id, StringComparison.Ordinal))
        {
            return new Attempt(StepOutcome.Ok, "nav.alreadyThere");
        }
        var path = _planner.FindPath(id.ScreenId, target.Id);
        if (path is null && _uiMap.GetScreen(id.ScreenId) is { IsHud: false })
        {
            RunSub(rs, f, InternalReset);
            id = IdentifyNow(rs);
            if (id.ScreenId is null)
            {
                return new Attempt(StepOutcome.PreconditionLost, "nav.unidentified", Subject: a.ScreenId);
            }
            if (string.Equals(id.ScreenId, target.Id, StringComparison.Ordinal))
            {
                return new Attempt(StepOutcome.Ok, "nav.alreadyThere");
            }
            path = _planner.FindPath(id.ScreenId, target.Id);
        }
        if (path is null)
        {
            return new Attempt(StepOutcome.NeedsSurvey, "nav.noPath:" + target.Id);
        }
        if (path.Hops.FirstOrDefault(h => !HopExecutable(h)) is { } bad)
        {
            return new Attempt(StepOutcome.NeedsSurvey, "nav.needsSurvey:" + bad.Id);
        }
        var startWithReset = _uiMap.GetScreen(path.From) is { IsHud: true };
        var proc = _planner.BuildProcedure(path, startWithReset);
        RunSub(rs, f, proc);
        var arrived = IdentifyNow(rs);
        return string.Equals(arrived.ScreenId, target.Id, StringComparison.Ordinal)
            ? new Attempt(StepOutcome.Ok, "nav.arrived")
            : new Attempt(StepOutcome.PreconditionLost, "nav.notArrived", Subject: target.Id);
    }

    private Attempt DoAwaitCraft(RunState rs, ProcFrame f, StepDefinition step, AwaitCraftAction a)
    {
        var timeout = TimeSpan.FromTicks(CraftCadence.Ticks * a.RequestedTicks) + CraftMargin;
        var start = Mono;
        while (true)
        {
            rs.Ct.ThrowIfCancellationRequested();
            var g = EvaluateGates(rs, f, step, GateScope.Passive, checkLogQuiet: false, out var gd);
            if (g is { } go && OutcomePolicy.ClassOf(go) is OutcomeClass.Halt or OutcomeClass.Terminal)
            {
                return new Attempt(go, gd, Subject: step.Id);
            }
            var craft = _log.State.Craft;
            if (craft.InProgress && craft.IsBlockCount > 0 && craft.StationUi is null && craft.StartedAt is { } s && GameNow - s > TimeSpan.FromSeconds(1))
            {
                return new Attempt(StepOutcome.BlockedCraftSpot, "craft.noStation", Subject: step.Id);
            }
            if (!craft.InProgress)
            {
                Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "craft.end"), ("ticks", craft.Ticks), ("requested", a.RequestedTicks), ("reshown", craft.CraftViewReshown));
                return new Attempt(StepOutcome.Ok, craft.IsBlockCount > 0 ? "craft.ended" : "craft.notRunning");
            }
            if (Mono - start >= timeout)
            {
                return new Attempt(StepOutcome.CraftInProgress, "craft.timeout");
            }
            Sleep(rs, PollInterval);
        }
    }
}
