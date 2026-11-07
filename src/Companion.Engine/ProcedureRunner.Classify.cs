using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.Engine.Internal;

namespace Companion.Engine;

public sealed partial class ProcedureRunner
{
    private static readonly TimeSpan CollectPoll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan AttributionSkew = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan WorldPressWindow = TimeSpan.FromSeconds(1);

    // ------------------------------------------------------------------ log collection after an input

    /// <summary>
    /// Reads the log after an input (SPEC 6.5 settle): until the expectation is met (early exit), an early-exit
    /// condition (forbidden UI / pay line), or the step timeout plus the settle read. A late match is LateSuccess.
    /// Expectations without UI events read for the attribution window only (the caller checks vision; a QuickAttribution
    /// click for the quick window).
    /// </summary>
    private EventWindow Collect(RunState rs, InputMarks marks, Expectation? expect, TimeSpan timeout, Func<IReadOnlyList<LogEvent>, bool>? early, bool settle = true, bool quick = false)
    {
        var from = marks.FocusSent ?? marks.ActionPosted ?? rs.StartLog;
        var uiExp = StepValidation.AllUiEvents(expect);
        var visionOnly = uiExp.Count == 0;
        var start = Mono;
        var deadline = start + timeout;
        var window = quick ? HardRules.QuickAttributionWindow : HardRules.AttributionWindow;
        var end = visionOnly ? start + (timeout < window ? timeout : window)
            : settle ? deadline + HardRules.SettleAfterTimeout : deadline;
        while (true)
        {
            _log.Poll();
            IReadOnlyList<LogEvent> events = from is null ? [] : _log.EventsSince(from);
            if (!visionOnly && MatchExpectation(expect, events, marks.ActionPosted, 0) is { } matched)
            {
                return new EventWindow(events, true, Mono > deadline, matched);
            }
            if (early?.Invoke(events) == true)
            {
                return new EventWindow(events, false, false, null);
            }
            if (Mono >= end)
            {
                return new EventWindow(events, visionOnly, false, visionOnly ? expect : null);
            }
            Sleep(rs, CollectPoll);
        }
    }

    private static Expectation? MatchExpectation(Expectation? e, IReadOnlyList<LogEvent> events, LogPosition? after, int depth)
    {
        if (e is null || depth > 3)
        {
            return null;
        }
        if (e.UiEvents is { Count: > 0 } list
            && events.OfType<UiLogEvent>().Any(ev => After(ev, after) && list.Any(x => x is not null && x.Matches(ev))))
        {
            return e;
        }
        foreach (var alt in e.Alternatives ?? [])
        {
            if (MatchExpectation(alt, events, after, depth + 1) is { } m)
            {
                return m;
            }
        }
        return null;
    }

    private static bool After(LogEvent e, LogPosition? pos) =>
        pos is null || (e.Position.SameFile(pos) && e.Position.Offset > pos.Offset);

    private Func<IReadOnlyList<LogEvent>, bool> ForbiddenEarly(InputMarks marks) => events =>
    {
        var ours = Ours(events, marks);
        return ours.OfType<PayLogEvent>().Any()
            || OpenedEvents(ours).Any(e => _classifier.Classify(e.UiName!) == UiKind.Forbidden);
    };

    /// <summary>Events attributed to our input: after the action mark and within 1.5 s of DOWN/KEYDOWN (UI-6).</summary>
    private static List<LogEvent> Ours(IReadOnlyList<LogEvent> events, InputMarks marks)
    {
        var result = new List<LogEvent>();
        if (marks.ActionPosted is not { } action)
        {
            return result;
        }
        DateTime? t0 = marks.ActionPostedUtc is { } u ? GameTime(u) : null;
        foreach (var e in events)
        {
            if (!After(e, action))
            {
                continue;
            }
            if (t0 is { } t && (e.Timestamp - t < -AttributionSkew || e.Timestamp - t > HardRules.AttributionWindow))
            {
                continue;
            }
            result.Add(e);
        }
        return result;
    }

    /// <summary>
    /// The game was already showing this indicator by itself, in a rhythm, before our DOWN: at least 3 separate shows in
    /// the 12 s before it, evenly spaced (at least 1 s apart), and the show at our press falls on the same beat (within
    /// 0.5 s). 2026-11-05: UI_LOCK_ARROW re-showed every 3 s for hours; one repeat fell 50 ms before a gathering drag
    /// ended and latched a false WORLD_PRESS. Two taps of the user's own, or a show off the beat, still halt (safety review).
    /// </summary>
    private bool ShownByItselfBefore(RunState rs, UiLogEvent atPress, DateTime down)
    {
        if (rs.StartLog is not { } from)
        {
            return false;
        }

        // one game show logs 'show_ui' and 'open const ui' together: lines closer than RepeatSame are one show
        var shows = new List<DateTime>();
        foreach (var t in OpenedEvents(_log.EventsSince(from))
            .Where(e => e.UiName == atPress.UiName && e.Timestamp < down - RepeatGuard && down - e.Timestamp <= RepeatLookBack)
            .Select(e => e.Timestamp).Order())
        {
            if (shows.Count == 0 || t - shows[^1] >= RepeatSame)
            {
                shows.Add(t);
            }
        }

        if (shows.Count < 3)
        {
            return false;
        }

        var beat = shows[^1] - shows[^2];
        var even = beat >= RepeatMinBeat && (shows[^2] - shows[^3] - beat).Duration() <= RepeatBeatTolerance;
        return even && (atPress.Timestamp - shows[^1] - beat).Duration() <= RepeatBeatTolerance;
    }

    private static readonly TimeSpan RepeatLookBack = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan RepeatSame = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RepeatMinBeat = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RepeatBeatTolerance = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RepeatGuard = TimeSpan.FromMilliseconds(200);

    private static IEnumerable<UiLogEvent> OpenedEvents(IEnumerable<LogEvent> events) =>
        events.OfType<UiLogEvent>().Where(e => IsOpenVerb(e.Verb) && e.UiName is not null);

    private TimeSpan StepTimeout(StepDefinition step)
    {
        var t = TimeSpan.FromMilliseconds(step.TimeoutMs);
        if (t < HardRules.MinStepTimeout)
        {
            t = HardRules.MinStepTimeout;
        }
        var shown = _log.State.ShownThisSession;
        var firstOpen = StepValidation.AllUiEvents(step.Expect)
            .Any(x => x.UiName is not null && x.Verb is UiVerb.Show or UiVerb.OpenConst && !shown.Contains(x.UiName));
        if (firstOpen && t < HardRules.FirstOpenStepTimeout)
        {
            t = HardRules.FirstOpenStepTimeout;
        }
        return t;
    }

    private void RecordOpened(RunState rs, StepDefinition step, IEnumerable<UiLogEvent> opened, Expectation? matched)
    {
        foreach (var e in opened)
        {
            var name = e.UiName!;
            if (_classifier.Classify(name) == UiKind.Overlay)
            {
                continue;
            }
            rs.Job.OpenedUis.Add(name);
            if (step.Flags.HasFlag(StepFlags.CompensateOnPark) && IsExpected(matched ?? step.Expect, e))
            {
                rs.Compensable.Add(name);
            }
        }
    }

    private static bool IsExpected(Expectation? e, UiLogEvent ev) =>
        StepValidation.AllUiEvents(e).Any(x => x.Matches(ev));

    private bool VisionExpectOk(RunState rs, Expectation? e)
    {
        if (e is null)
        {
            return true;
        }
        if (e.Hud is { } hud)
        {
            var hc = _screen.ClassifyHudStable(rs.Ct);
            var until = Mono + HardRules.SettleAfterTimeout;
            while (hc.State != hud && Mono < until)
            {
                Sleep(rs, TimeSpan.FromMilliseconds(500));
                hc = _screen.ClassifyHudStable(rs.Ct);
            }
            if (hc.State != hud)
            {
                return false;
            }
        }
        if (e.Screen is { } screen)
        {
            var id = IdentifyNow(rs);
            if (!string.Equals(id.ScreenId, screen, StringComparison.Ordinal))
            {
                Sleep(rs, TimeSpan.FromMilliseconds(500));
                id = IdentifyNow(rs);
            }
            return string.Equals(id.ScreenId, screen, StringComparison.Ordinal);
        }
        return true;
    }

    // ------------------------------------------------------------------ key classification (SPEC 6.5 Key)

    private Attempt ClassifyKey(RunState rs, StepDefinition step, GameKey key, KeyReport rep, EventWindow win)
    {
        var ours = Ours(win.Events, rep.Marks);
        var opened = OpenedEvents(ours).ToList();
        var subject = "key." + key;
        if (rep.Mode == InputMode.Foreground && UserInGame(rep.UserInputDuring, rep.Marks))
        {
            return UserInputOutcome(rs, step, step.Expect, win, ours, opened, uiOnly: false, null, subject);
        }
        RecordOpened(rs, step, opened, win.Matched);
        var forbidden = opened.FirstOrDefault(e => _classifier.Classify(e.UiName!) == UiKind.Forbidden);
        if (forbidden is not null || ours.OfType<PayLogEvent>().Any())
        {
            return new Attempt(StepOutcome.ForbiddenUi, "forbidden", UiSeen: forbidden?.UiName ?? "pay", Delivered: true, Subject: forbidden?.UiName ?? subject, OursUi: true);
        }
        if (ours.OfType<GuideBanLogEvent>().Any())
        {
            return new Attempt(StepOutcome.GuideBan, "guideBan", Delivered: true, Subject: subject);
        }
        if (win.Met && VisionExpectOk(rs, win.Matched ?? step.Expect))
        {
            return new Attempt(win.Late ? StepOutcome.LateSuccess : StepOutcome.Ok, win.Late ? "late" : "expected", Delivered: true);
        }
        var unexpected = opened.Where(e => !IsExpected(step.Expect, e) && _classifier.Classify(e.UiName!) != UiKind.Overlay).ToList();
        if (key == GameKey.Escape && unexpected.Count > 0)
        {
            return new Attempt(StepOutcome.EscOpenedUi, "esc.opened", UiSeen: unexpected[0].UiName, Delivered: true, Subject: unexpected[0].UiName, OursUi: true);
        }
        if (UnexpectedKind(unexpected) is { } bad)
        {
            return bad.Kind switch
            {
                UiKind.Stack => new Attempt(StepOutcome.WrongUiVerifiedAim, "key.wrongUi", UiSeen: bad.Name, AimVerified: true, Delivered: true, Subject: subject, OursUi: true),
                UiKind.Social => new Attempt(StepOutcome.ForeignBlockingUi, "key.social", UiSeen: bad.Name, Delivered: true, OursUi: true),
                UiKind.Busy => new Attempt(StepOutcome.Busy, "key.busy", UiSeen: bad.Name, Delivered: true, OursUi: true),
                UiKind.Station => new Attempt(StepOutcome.CraftInProgress, "key.station", UiSeen: bad.Name, Delivered: true, OursUi: true),
                _ => new Attempt(StepOutcome.UnknownUi, "key.unknownUi", UiSeen: bad.Name, Delivered: true, OursUi: true),
            };
        }
        if (ForeignStackUi(win, rep.Marks, ours, step.Expect) is { } foreign)
        {
            return new Attempt(StepOutcome.UserInterference, "foreign.ui", UiSeen: foreign, Delivered: true, Subject: subject);
        }
        return new Attempt(StepOutcome.None, win.Met ? "key.visionMismatch" : "key.nothing", Delivered: true, Subject: subject);
    }

    /// <summary>
    /// A stack/unknown UI shown during our step that is NOT attributed to our input (outside the 1.5 s window) and not
    /// expected: someone else is using the game (UserInterference).
    /// </summary>
    private string? ForeignStackUi(EventWindow win, InputMarks marks, List<LogEvent> ours, Expectation? expect)
    {
        foreach (var e in OpenedEvents(win.Events.Where(ev => After(ev, marks.ActionPosted) && !ours.Contains(ev))))
        {
            if (IsExpected(expect, e))
            {
                continue;
            }
            var kind = _classifier.Classify(e.UiName!);
            if (kind is UiKind.Stack or UiKind.Unknown)
            {
                return e.UiName;
            }
        }
        return null;
    }

    private (UiKind Kind, string Name)? UnexpectedKind(List<UiLogEvent> unexpected)
    {
        foreach (var k in new[] { UiKind.Stack, UiKind.Social, UiKind.Busy, UiKind.Station, UiKind.Unknown })
        {
            var e = unexpected.FirstOrDefault(x => _classifier.Classify(x.UiName!) == k);
            if (e is not null)
            {
                return (k, e.UiName!);
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ late check of a QuickAttribution click

    /// <summary>A QuickAttribution click whose full attribution window has not been read yet.</summary>
    private sealed record LateCheck(InputMarks Marks, DateTimeOffset Deadline, JobScope Job, string StepId, string TemplateId, bool Foreground = false);

    private LateCheck? _late;

    /// <summary>
    /// The late check (safety review 2026-10-31): before the next input of any procedure (and at the start of every step
    /// that is not itself a QuickAttribution click), waits until the last QuickAttribution click's full attribution window
    /// has passed and reads every log event that window attributes to it, as the click's own full read would have: a
    /// Forbidden UI or a pay line is the ForbiddenUi HALT (with the Esc compensation of a Forbidden UI), a guide ban the
    /// GuideBan HALT, every opened UI is the job's own; an opened UI that is now on top (not the overlay kind, not the one
    /// the step needs on top) ends the run before any input ("late.opened:&lt;ui&gt;"), so no stray-popup Esc follows.
    /// </summary>
    private void SettleLate(RunState rs, StepDefinition? step)
    {
        if (_late is not { } late)
        {
            return;
        }
        _late = null;
        var left = late.Deadline - _clock.UtcNow;
        var waited = left > TimeSpan.Zero ? left : TimeSpan.Zero;
        Sleep(rs, waited);
        _log.Poll();
        var from = late.Marks.FocusSent ?? late.Marks.ActionPosted;
        IReadOnlyList<LogEvent> events = from is null ? [] : _log.EventsSince(from);
        var ours = Ours(events, late.Marks);
        var opened = OpenedEvents(ours).Select(e => e.UiName!).Where(n => _classifier.Classify(n) != UiKind.Overlay).Distinct(StringComparer.Ordinal).ToList();
        // foreground mode: the user's own input reached the game in the window (SPEC 7.3b): its UIs are not taken for ours
        var userIn = late.Foreground && UserInGame(false, late.Marks);
        foreach (var name in opened.Where(_ => !userIn))
        {
            // ours in the click's job and in the run that reads it (another job may run next: its Esc compensation needs it)
            late.Job.OpenedUis.Add(name);
            rs.Job.OpenedUis.Add(name);
        }
        var forbidden = opened.FirstOrDefault(n => _classifier.Classify(n) == UiKind.Forbidden);
        var pay = ours.OfType<PayLogEvent>().Any();
        var guideBan = ours.OfType<GuideBanLogEvent>().Any();
        var top = _log.State.AdvisoryTop;
        var openTop = top is not null && opened.Contains(top) && !string.Equals(top, step?.Pre?.TopUi, StringComparison.Ordinal) ? top : null;
        Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "late.check"), ("of", late.StepId), ("waitedMs", (long)waited.TotalMilliseconds),
            ("opened", (long)opened.Count), ("top", openTop), ("forbidden", forbidden), ("pay", pay), ("guideBan", guideBan), ("userInput", userIn));
        if (pay || (forbidden is not null && !userIn))
        {
            // the user's own forbidden window is foreign (the gates wait on it, no Esc); a pay line stops either way
            throw new HaltSignal(HaltCode.ForbiddenUi, forbidden ?? late.TemplateId, late.StepId, StepOutcome.ForbiddenUi,
                escUi: userIn ? null : forbidden, escForbidden: !userIn);
        }
        if (guideBan)
        {
            throw new HaltSignal(HaltCode.GuideBan, late.TemplateId, late.StepId, StepOutcome.GuideBan);
        }
        if (openTop is not null)
        {
            throw new EndSignal(StepOutcome.Refused, "late.opened:" + openTop);
        }
    }

    // ------------------------------------------------------------------ click classification (SPEC 6.5 Click)

    private Attempt ClassifyClick(RunState rs, StepDefinition step, string templateId, ClickReport rep, PointI aim, Expectation expect)
    {
        // never on a click the engine treats as a commit (a commit-tagged template), whatever the step says
        var quick = step.Flags.HasFlag(StepFlags.QuickAttribution) && !step.Flags.HasFlag(StepFlags.Commit)
            && !(_templates.Get(templateId) is { } def && IsCommitTemplate(def));
        var win = Collect(rs, rep.Marks, expect, StepTimeout(step), ForbiddenEarly(rep.Marks), quick: quick);
        if (quick && rep.Marks.ActionPosted is not null)
        {
            // the rest of the full attribution window is read before the next input (SettleLate)
            _late = new LateCheck(rep.Marks, (rep.Marks.ActionPostedUtc ?? _clock.UtcNow) + HardRules.AttributionWindow + HardRules.LateCheckMargin,
                rs.Job, step.Id, templateId, rep.Mode == InputMode.Foreground);
        }
        var a = EvaluateClick(rs, step, templateId, rep, aim, expect, win, settled: false, uiOnly: false)!;
        if (a.Outcome != StepOutcome.Uncertain)
        {
            return a;
        }
        Sleep(rs, HardRules.UncertainSettle);
        _log.Poll();
        var from = rep.Marks.FocusSent ?? rep.Marks.ActionPosted;
        var events = from is null ? (IReadOnlyList<LogEvent>)[] : _log.EventsSince(from);
        var matched = MatchExpectation(expect, events, rep.Marks.ActionPosted, 0);
        var win2 = new EventWindow(events, matched is not null, matched is not null, matched);
        var b = EvaluateClick(rs, step, templateId, rep, aim, expect, win2, settled: true, uiOnly: false)!;
        return b.Outcome == StepOutcome.Uncertain ? b with { Detail = "uncertain.settled" } : b;
    }

    /// <summary>The ordered check list of SPEC 6.5 (click). With <paramref name="uiOnly"/> only the log-decisive checks run (toggles).</summary>
    private Attempt? EvaluateClick(RunState rs, StepDefinition step, string templateId, ClickReport rep, PointI aim, Expectation expect, EventWindow win, bool settled, bool uiOnly)
    {
        var marks = rep.Marks;
        var phase = rep.Phase;
        var ours = Ours(win.Events, marks);
        var opened = OpenedEvents(ours).ToList();
        if (rep.Mode == InputMode.Foreground && UserInGame(rep.Cursor.UserInput || rep.Cursor.UserButton, marks))
        {
            return UserInputOutcome(rs, step, expect, win, ours, opened, uiOnly, phase, templateId);
        }
        RecordOpened(rs, step, opened, win.Matched);
        // 1. pay line or forbidden UI
        var forbidden = opened.FirstOrDefault(e => _classifier.Classify(e.UiName!) == UiKind.Forbidden);
        if (forbidden is not null || ours.OfType<PayLogEvent>().Any())
        {
            return new Attempt(StepOutcome.ForbiddenUi, "forbidden", UiSeen: forbidden?.UiName ?? "pay", Phase: phase, Delivered: true,
                Subject: forbidden?.UiName ?? templateId, OursUi: true);
        }
        // 2. guide ban
        if (win.Events.OfType<GuideBanLogEvent>().Any(e => After(e, marks.ActionPosted)))
        {
            return new Attempt(StepOutcome.GuideBan, "guideBan", Phase: phase, Delivered: true, Subject: templateId);
        }
        // 3. expected
        if (!uiOnly && win.Met && VisionExpectOk(rs, win.Matched ?? expect))
        {
            return new Attempt(win.Late ? StepOutcome.LateSuccess : StepOutcome.Ok, win.Late ? "late" : "expected", Phase: phase, Delivered: true, Subject: templateId);
        }
        // 4. world press indicators within 1 s of DOWN, unless the game's own raycast names a UI object at our aim (the
        // press hit the interface; an indicator in the same second came from elsewhere, 2026-10-21)
        DateTime? down = marks.ActionPostedUtc is { } du ? GameTime(du) : null;
        var pressBlock = PressBlockOf(rep, win);
        var uiAtAim = pressBlock is { ObjectName.Length: > 0 }
            && pressBlock.ToClient(HardRules.ExpectedClientHeight) is { } hit && hit.ChebyshevDistanceTo(aim) <= HardRules.AimTolerancePx;
        var world = opened.FirstOrDefault(e => UiCatalog.WorldPressIndicators.Contains(e.UiName!)
            && (down is null || e.Timestamp - down.Value <= WorldPressWindow)
            && !(down is { } d && ShownByItselfBefore(rs, e, d)));
        if (world is not null && !uiAtAim)
        {
            return new Attempt(StepOutcome.WorldPress, "worldPress.ui", UiSeen: world.UiName, Phase: phase, Delivered: true, Subject: templateId);
        }
        // 5. aim verification
        var cur = rep.Cursor;
        var forced = rep.Slide?.ForcedSlideBack ?? false;
        var aimVerified = !cur.MovedBeforeDown && !cur.MovedInP4 && pressBlock is not null
            && pressBlock.ToClient(HardRules.ExpectedClientHeight) is { } p && p.ChebyshevDistanceTo(aim) <= HardRules.AimTolerancePx
            && rep.UnfocusSentWhileSlid && !forced;
        // 6-7. other UIs
        var unexpected = opened.Where(e => !IsExpected(expect, e) && _classifier.Classify(e.UiName!) != UiKind.Overlay).ToList();
        if (UnexpectedKind(unexpected) is { } bad)
        {
            return bad.Kind switch
            {
                UiKind.Stack => aimVerified
                    ? new Attempt(StepOutcome.WrongUiVerifiedAim, "wrongUi.verified", UiSeen: bad.Name, AimVerified: true, Phase: phase, Delivered: true, Subject: templateId, OursUi: true)
                    : new Attempt(StepOutcome.WrongUiUnverified, "wrongUi.unverified", UiSeen: bad.Name, Phase: phase, Delivered: true, Subject: templateId, OursUi: true),
                UiKind.Social => new Attempt(StepOutcome.ForeignBlockingUi, "click.social", UiSeen: bad.Name, AimVerified: aimVerified, Phase: phase, Delivered: true, OursUi: true),
                UiKind.Busy => new Attempt(StepOutcome.Busy, "click.busy", UiSeen: bad.Name, AimVerified: aimVerified, Phase: phase, Delivered: true, OursUi: true),
                UiKind.Station => new Attempt(StepOutcome.CraftInProgress, "click.station", UiSeen: bad.Name, AimVerified: aimVerified, Phase: phase, Delivered: true, OursUi: true),
                _ => new Attempt(StepOutcome.UnknownUi, "click.unknownUi", UiSeen: bad.Name, AimVerified: aimVerified, Phase: phase, Delivered: true, Subject: templateId, OursUi: true),
            };
        }
        if (ForeignStackUi(win, marks, ours, expect) is { } foreign)
        {
            return new Attempt(StepOutcome.UserInterference, "foreign.ui", UiSeen: foreign, AimVerified: aimVerified, Phase: phase, Delivered: true, Subject: templateId);
        }
        if (uiOnly)
        {
            return null;
        }
        // 8. press not registered (cover mode: its unfocus makes the game log the press; foreground mode has no unfocus, so
        // a delivered press without a result goes on as "nothing happened", never as an uncounted blind repeat)
        if (pressBlock is null && rep.Mode == InputMode.Cover)
        {
            return new Attempt(StepOutcome.PressNotRegistered, "noPressBlock", Phase: phase, Delivered: true, Subject: templateId);
        }
        // 9. verified aim, nothing under the pointer
        if (aimVerified && pressBlock is not null && string.IsNullOrEmpty(pressBlock.ObjectName))
        {
            return new Attempt(StepOutcome.WorldPress, "worldPress.emptyRaycast", AimVerified: true, Phase: phase, Delivered: true, Subject: templateId);
        }
        // 10. uncertain
        var jank = win.Events.OfType<JankLogEvent>().Any();
        if (jank || cur.MovedInP4 || forced)
        {
            return new Attempt(StepOutcome.Uncertain, settled ? "uncertain.settled" : jank ? "uncertain.jank" : cur.MovedInP4 ? "uncertain.cursorP4" : "uncertain.forcedSlideBack",
                AimVerified: aimVerified, Phase: phase, Delivered: true, Subject: templateId);
        }
        // 11. nothing happened
        return new Attempt(StepOutcome.None, "click.nothing", AimVerified: aimVerified, Phase: phase, Delivered: true, Subject: templateId);
    }

    /// <summary>
    /// Foreground mode (SPEC 7.3b): the user's own keyboard or mouse input reached the game during our input (GameIO saw it),
    /// or after it within the attribution window (the game-use clock moved since DOWN / KEYDOWN, or since the section began).
    /// </summary>
    private bool UserInGame(bool reported, InputMarks marks)
    {
        if (reported)
        {
            return true;
        }
        if ((marks.ActionPostedUtc ?? marks.FocusSentUtc) is not { } since)
        {
            return false;
        }
        try
        {
            var r = _gameIo.CheckReadiness(new ReadinessQuery(InputKind.Key, null, false));
            return r.SinceGameUse is { } use && use < _clock.UtcNow - since;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// An input the user's own input overlapped (foreground mode, SPEC 7.3b, safety review 2026-11-02): what opened may be
    /// theirs, so nothing is recorded as the job's and no Esc compensates. A pay line still stops (HALT, no Esc); a forbidden
    /// UI waits as a foreign one; the expected UI is success; anything else is the user's activity: wait, then resume from
    /// the latest step whose precondition holds (the user's game use invalidates a clean HUD), a commit job defers. Never a
    /// blind repeat of the press.
    /// </summary>
    private Attempt UserInputOutcome(
        RunState rs, StepDefinition step, Expectation expect, EventWindow win, List<LogEvent> ours, List<UiLogEvent> opened, bool uiOnly, InputPhase? phase, string subject)
    {
        if (ours.OfType<PayLogEvent>().Any())
        {
            return new Attempt(StepOutcome.ForbiddenUi, "foreground.userInput.pay", UiSeen: "pay", Phase: phase, Delivered: true, Subject: subject);
        }
        if (opened.FirstOrDefault(e => _classifier.Classify(e.UiName!) == UiKind.Forbidden) is { } forbidden)
        {
            return new Attempt(StepOutcome.ForeignBlockingUi, "foreground.userInput.forbidden", UiSeen: forbidden.UiName, Phase: phase, Delivered: true);
        }
        if (!uiOnly && win.Met && VisionExpectOk(rs, win.Matched ?? expect))
        {
            RecordOpened(rs, step, opened.Where(e => IsExpected(win.Matched ?? expect, e)), win.Matched);
            return new Attempt(win.Late ? StepOutcome.LateSuccess : StepOutcome.Ok, win.Late ? "late" : "expected", Phase: phase, Delivered: true, Subject: subject);
        }
        return new Attempt(StepOutcome.UserActive, "foreground.userInput", Phase: phase, Delivered: true, Subject: subject);
    }

    /// <summary>
    /// The press block of our unfocus. Never in foreground mode (SPEC 7.3b): no unfocus is sent, so a LostFocuse block
    /// there comes from a real focus change (the user switching away), not from our press.
    /// </summary>
    private static PressBlockLogEvent? PressBlockOf(ClickReport rep, EventWindow win) =>
        rep.Mode == InputMode.Foreground ? null
        : rep.PressBlock ?? win.Events.OfType<PressBlockLogEvent>().LastOrDefault(e => After(e, rep.Marks.FocusSent));

    /// <summary>A fresh full-scale capture finds the template (unique within its radius: vision accepts only with margin).</summary>
    private bool UniqueNow(RunState rs, string templateId)
    {
        try
        {
            var cap = _screen.Capture(rs.Ct);
            if (!cap.Ok)
            {
                return false;
            }
            rs.LastFrame = cap.Frame;
            var m = _vision.Locate(cap.Frame!, templateId);
            return m.Found && m.Failure == MatchFailure.None;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
