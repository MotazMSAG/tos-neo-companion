using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Navigation;
using Companion.Contracts.Perception;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.Engine.Internal;

namespace Companion.Engine;

public sealed partial class ProcedureRunner
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    /// <summary>Consecutive "target keeps moving" waits on one step before it is handled as a lost precondition.</summary>
    private const int MaxMovingStreak = 10;
    /// <summary>Consecutive slid-capture mismatches on one step before it is handled as a lost precondition.</summary>
    private const int MaxMismatchStreak = 5;
    /// <summary>Pauses (each after <see cref="MaxMismatchStreak"/> mismatches) before the step is handled as lost.</summary>
    private const int MaxMismatchPauses = 3;
    private static readonly TimeSpan MismatchPause = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ParkPoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FocusFailedRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IncidentPause = TimeSpan.FromSeconds(30);

    private const string KeyboardLayoutDetail = "notSent.keyboardLayout";

    private static readonly ProcedureDefinition InternalReset = new(
        "Reset.internal",
        "Internal reset before a restart",
        [new StepDefinition("reset", StatePredicate.Any, new ResetAction(HardRules.MaxEscPerReset), new Expectation([UiExpectation.HiddenTop(null)]))],
        new ProcedureLimits(MaxEsc: HardRules.MaxEscPerReset));

    // ------------------------------------------------------------------ main loop

    private void RunFrame(RunState rs, ProcFrame f)
    {
        var savedProcedure = rs.CurrentProcedure;
        rs.CurrentProcedure = f.Def.Id;
        try
        {
            var steps = f.Def.Steps;
            var i = 0;
            while (i < steps.Count)
            {
                rs.Ct.ThrowIfCancellationRequested();
                var step = steps[i];
                rs.CurrentStep = f.StepKey(i);
                f.AttemptNo[i]++;
                SetStatus(EngineState.Running, rs, null);
                Write(rs, JournalKind.StepStart, rs.CurrentStep, ("attempt", f.AttemptNo[i]), ("action", ActionCode(step.Action)), ("target", TargetCode(step.Action)));
                var att = AttemptStep(rs, f, i);
                var d = Decide(rs, f, i, att);
                d = CycleCheck(rs, f, i, att, d);
                RecordAttempt(rs, f, i, att, d);
                rs.LastOutcome = att.Outcome;
                if (d.Action is RecoveryAction.Proceed or RecoveryAction.Park or RecoveryAction.Halt or RecoveryAction.End)
                {
                    FlushJournal(); // between inputs only: the window is at home here
                }
                i = Apply(rs, f, i, att, d);
            }
        }
        finally
        {
            rs.CurrentProcedure = savedProcedure;
        }
    }

    private Attempt AttemptStep(RunState rs, ProcFrame f, int i)
    {
        var step = f.Def.Steps[i];
        if (f.Skipped.Contains(i))
        {
            return new Attempt(StepOutcome.Ok, "optional.skipped");
        }
        // a QuickAttribution click settles the last one after its own locate (before its input), every other step first
        if (!(step.Action is ClickAction && step.Flags.HasFlag(StepFlags.QuickAttribution)))
        {
            SettleLate(rs, step);
        }
        var optional = step.Flags.HasFlag(StepFlags.Optional);
        if (optional && OptionalBlocked(step) is { } why)
        {
            SkipOptional(rs, f, i, why);
            return new Attempt(StepOutcome.Ok, "optional.skipped");
        }
        var scope = ScopeOf(rs, f, step);
        if (AwaitGates(rs, f, step, scope) is { } gated)
        {
            return gated;
        }
        var pre = EvaluatePre(rs, step);
        if (!pre.Holds && _late is not null)
        {
            // the precondition failed while the last quick click's late check is due (a window it opened late?): read it
            // first, before any stray-popup Esc
            SettleLate(rs, step);
            pre = EvaluatePre(rs, step);
        }
        if (!pre.Holds)
        {
            // A stray Esc-safe popup right above the screen this step needs (live 2026-10-15: in the homestead a press
            // on a material tile also opened the farm-plot menu behind the Make view): close just that popup and retry,
            // instead of treating the screen as lost (which resets and reopens everything).
            if (pre.Detail == "pre.topUi" && step.Pre?.TopUi is { } wanted && StrayPopupAbove(wanted) is { } stray)
            {
                return new Attempt(StepOutcome.Popup, "pre.strayPopup", UiSeen: stray, Subject: step.Id);
            }
            if (optional && !pre.Transient)
            {
                SkipOptional(rs, f, i, pre.Detail);
                return new Attempt(StepOutcome.Ok, "optional.skipped");
            }
            return new Attempt(pre.Transient ? StepOutcome.PreconditionTransient : StepOutcome.PreconditionLost, pre.Detail, Subject: step.Id);
        }
        if (step.Flags.HasFlag(StepFlags.SkipIfSatisfied) && ExpectationHoldsNow(rs, step.Expect))
        {
            Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "skip.satisfied"));
            return new Attempt(StepOutcome.Ok, "skip.satisfied");
        }
        // The screen this step would open is already there under a stray Esc-safe popup (live 2026-10-15: a team
        // recommendation popup over the Ability view made the open step reset and reopen it on another tab): close just
        // the popup; the retry then skips.
        if (step.Flags.HasFlag(StepFlags.SkipIfSatisfied)
            && step.Expect.UiEvents?.Where(x => x.Verb is UiVerb.Show or UiVerb.OpenConst && x.UiName is not null)
                .Select(x => StrayPopupAbove(x.UiName!)).FirstOrDefault(s => s is not null) is { } strayAbove)
        {
            return new Attempt(StepOutcome.Popup, "pre.strayPopup", UiSeen: strayAbove, Subject: step.Id);
        }
        rs.AutoCollectAtGate = _log.State.AutoCollectStatus;
        var att = step.Action switch
        {
            KeyAction k => DoKey(rs, f, step, k),
            ClickAction c => DoClick(rs, f, step, c.TemplateId, step.Expect, c.Offset),
            DragAction d => DoDrag(rs, f, step, d),
            ToggleHudAction t => DoToggle(rs, f, step, t.Target),
            RestoreHudAction => DoRestore(rs, f, step),
            ResetAction r => DoReset(rs, f, step, r),
            WaitAction w => DoWait(rs, w),
            NavigateAction n => DoNavigate(rs, f, n),
            CaptureAction c => DoCapture(rs, f, c),
            ReadAction r => DoRead(rs, r),
            CallProcedureAction c => DoCall(rs, f, c),
            AwaitCraftAction a => DoAwaitCraft(rs, f, step, a),
            NeedsSurveyAction n => new Attempt(StepOutcome.NeedsSurvey, "needsSurvey:" + n.What),
            _ => new Attempt(StepOutcome.InternalError, "action.unknown"),
        };
        var inputScope = scope is GateScope.Key or GateScope.Click or GateScope.Commit;
        if (inputScope && att.Delivered && OutcomePolicy.ClassOf(att.Outcome) is not (OutcomeClass.Halt or OutcomeClass.Terminal)
            && !step.Flags.HasFlag(StepFlags.ChangesAutoCollect))
        {
            _log.Poll();
            if (_log.State.AutoCollectStatus != rs.AutoCollectAtGate)
            {
                return new Attempt(StepOutcome.AutoCollectChanged, "autoCollect.changed", Subject: step.Id, Delivered: true);
            }
        }
        return att;
    }

    private Decision Decide(RunState rs, ProcFrame f, int i, Attempt att)
    {
        var step = f.Def.Steps[i];
        // aborted presses no longer use the input budget (R1.1), so they get their own bound of the same size. It follows
        // the counter, whatever the attempt's detail (safety review 2026-10-23: a user takeover aborts with "io.*"), and
        // only for wait or counted outcomes, so a HALT or a stop keeps its class. A soft end in the engine's ledger
        if (OutcomePolicy.ClassOf(att.Outcome) is OutcomeClass.Wait or OutcomeClass.Counted && rs.Aborted > rs.AbortedChecked)
        {
            rs.AbortedChecked = rs.Aborted;
            if (rs.Aborted >= rs.Limits.MaxInputBatches)
            {
                return SoftOrHaltDetail(i, HaltCode.CountedFailuresExhausted, step.Id, "aborted");
            }
        }

        switch (OutcomePolicy.ClassOf(att.Outcome))
        {
            case OutcomeClass.Ok:
                return new Decision(RecoveryAction.Proceed, i + 1);
            case OutcomeClass.Halt:
                return new Decision(RecoveryAction.Halt, i, Halt: HaltCodeOf(att.Outcome), HaltSubject: att.Subject ?? att.UiSeen ?? step.Id, Detail: att.Detail);
            case OutcomeClass.Terminal:
                return new Decision(RecoveryAction.End, i, End: att.Outcome, Detail: att.Detail);
            case OutcomeClass.Counted:
                return DecideCounted(rs, f, i, att);
            default:
                return DecideWait(rs, f, i, att);
        }
    }

    private Decision DecideCounted(RunState rs, ProcFrame f, int i, Attempt att)
    {
        var step = f.Def.Steps[i];
        var limits = f.Limits;
        switch (att.Outcome)
        {
            case StepOutcome.PreconditionLost:
                {
                    f.PreconditionLost++;
                    if (f.PreconditionLost > limits.MaxPreconditionLost)
                    {
                        return SoftOrHalt(i, HaltCode.PreconditionLostExhausted, step.Id);
                    }
                    // A popup broke the clean HUD: only a Reset step can restore it, so step back to the latest one
                    // (live run 2026-10-14: a game popup made the engine step back to a capture step three times and halt).
                    var j = att.Detail == "pre.hudClean" ? LatestEarlierReset(f, i) : -1;
                    if (j < 0)
                    {
                        j = LatestEarlierPre(rs, f, i);
                    }
                    if (j >= 0)
                    {
                        return new Decision(RecoveryAction.StepBack, j, Detail: "preLost.stepBack");
                    }
                    if (NoResetFromReset(f, i, att.Detail, step.Id) is { } stop)
                    {
                        return stop;
                    }
                    if (f.Restarts >= limits.MaxRestarts)
                    {
                        return SoftOrHalt(i, HaltCode.PreconditionLostExhausted, step.Id);
                    }
                    f.Restarts++;
                    return new Decision(RecoveryAction.ResetAndRestart, 0, Detail: "preLost.restart");
                }
            case StepOutcome.WrongUiVerifiedAim:
                return DecideWrongVerified(rs, f, i, att);
            default:
                if (IsCommitStep(rs, f, step))
                {
                    return new Decision(RecoveryAction.End, i, End: StepOutcome.Cancelled, Detail: "commit.noEffect");
                }
                return CountedLadder(rs, f, i, null);
        }
    }

    private static int LatestEarlierReset(ProcFrame f, int i)
    {
        for (var k = i - 1; k >= 0; k--)
        {
            if (f.Def.Steps[k].Action is ResetAction)
            {
                return k;
            }
        }
        return -1;
    }

    private Decision CountedLadder(RunState rs, ProcFrame f, int i, string? detail)
    {
        var step = f.Def.Steps[i];
        var limits = f.Limits;
        f.Counted[i]++;
        if (f.Counted[i] < limits.MaxCountedPerStep)
        {
            return new Decision(RecoveryAction.RetryCounted, i, Detail: detail ?? "ladder.retry");
        }
        if (f.StepBacks[i] < limits.MaxStepBacks)
        {
            var j = LatestEarlierPre(rs, f, i);
            if (j >= 0)
            {
                f.StepBacks[i]++;
                f.Counted[i] = 0;
                return new Decision(RecoveryAction.StepBack, j, Detail: detail ?? "ladder.stepBack");
            }
        }
        if (NoResetFromReset(f, i, detail, step.Id) is { } stop)
        {
            return stop;
        }
        if (f.Restarts < limits.MaxRestarts)
        {
            f.Restarts++;
            return new Decision(RecoveryAction.ResetAndRestart, 0, Detail: detail ?? "ladder.restart");
        }
        return SoftOrHalt(i, HaltCode.CountedFailuresExhausted, step.Id);
    }

    /// <summary>
    /// A failing reset step never restarts through another reset (incident 5, audit R1.2: each nested reset pressed 2 more
    /// Escs at a dialog Esc cannot close, until the Esc budget latched a BudgetExceeded HALT). The procedure ends Deferred
    /// with the reset's own reason, as a soft end in the engine's ledger: a reset that fails again and again across runs
    /// (rule mode comes back) becomes the HALT after <see cref="HardRules.SoftEndsBeforeHalt"/> in 30 minutes (safety
    /// review 2026-10-23). Null for any other step.
    /// </summary>
    private Decision? NoResetFromReset(ProcFrame f, int i, string? detail, string subject) =>
        f.Def.Steps[i].Action is ResetAction || f.Def.Id == InternalReset.Id
            ? SoftOrHaltDetail(i, HaltCode.PreconditionLostExhausted, subject,
                detail is { } d && d.StartsWith("reset.", StringComparison.Ordinal) ? d : "reset.failed")
            : null;

    /// <summary>
    /// <see cref="SoftOrHalt"/> with the soft end's own reason: Deferred "exhausted.&lt;detail&gt;" (the goal loop counts it
    /// with the other exhausted ends, audit R1.10), or the HALT when the ledger is full.
    /// </summary>
    private Decision SoftOrHaltDetail(int i, HaltCode code, string? subject, string detail)
    {
        var d = SoftOrHalt(i, code, subject);
        return d.Action == RecoveryAction.End ? d with { Detail = "exhausted." + detail } : d;
    }

    private Decision DecideWrongVerified(RunState rs, ProcFrame f, int i, Attempt att)
    {
        var step = f.Def.Steps[i];
        var subject = att.Subject ?? step.Id;
        rs.Job.WrongVerified.TryGetValue(subject, out var n);
        rs.Job.WrongVerified[subject] = ++n;
        f.Counted[i]++;
        var templateId = TemplateOfStep(step, subject);
        if (n >= 2)
        {
            if (templateId is not null)
            {
                SafeMarkSuspect(templateId, "wrongUi.verified.twice");
            }
            return HaltDecision(i, HaltCode.RecalibrateTemplate, subject);
        }
        // first one: close it (log-verified Esc), then one retry only if the template is unique on a fresh capture
        if (att.UiSeen is not null)
        {
            CloseEsc(rs, att.UiSeen, compensation: false);
        }
        if (templateId is not null && !UniqueNow(rs, templateId))
        {
            SafeMarkSuspect(templateId, "wrongUi.verified.notUnique");
            return HaltDecision(i, HaltCode.RecalibrateTemplate, subject);
        }
        return new Decision(RecoveryAction.RetryCounted, i, Detail: "wrongUi.closed");
    }

    private Decision DecideWait(RunState rs, ProcFrame f, int i, Attempt att)
    {
        var step = f.Def.Steps[i];
        var limits = f.Limits;
        var o = att.Outcome;
        if (att.Resume)
        {
            return new Decision(RecoveryAction.WaitThenRetry, i, Resume: true, Detail: "resume");
        }
        if (att.PatienceExceeded)
        {
            return new Decision(RecoveryAction.Park, i, Detail: "patience");
        }
        f.WaitSince ??= Mono;
        if (Mono - f.WaitSince.Value >= TimeSpan.FromMilliseconds(limits.PatienceMs))
        {
            return new Decision(RecoveryAction.Park, i, Detail: "patience");
        }
        var commitJob = IsCommitJob(rs, f);
        switch (o)
        {
            case StepOutcome.Uncertain or StepOutcome.PressNotRegistered:
                f.Uncounted[i]++;
                if (f.Uncounted[i] > limits.MaxUncountedPerStep)
                {
                    f.Uncounted[i] = 0;
                    if (IsCommitStep(rs, f, step))
                    {
                        return new Decision(RecoveryAction.End, i, End: StepOutcome.Cancelled, Detail: "commit.unverified");
                    }
                    return CountedLadder(rs, f, i, "uncounted.exhausted");
                }
                if (o == StepOutcome.Uncertain && IsCommitStep(rs, f, step))
                {
                    return new Decision(RecoveryAction.End, i, End: StepOutcome.Cancelled, Detail: "commit.uncertain");
                }
                return new Decision(RecoveryAction.RetryUncounted, i, Sleep: o == StepOutcome.PressNotRegistered ? HardRules.UncertainSettle : TimeSpan.Zero);
            case StepOutcome.NotSent:
                if (att.Detail == KeyboardLayoutDetail)
                {
                    // a refused letter key does not fix itself by waiting (incident 10: 1,084 refusals in 18 min); the
                    // same bound as an unreachable press, then Deferred with a notice to switch the layout back
                    f.UnreachableSince ??= Mono;
                    if (Mono - f.UnreachableSince.Value >= HardRules.UnreachableLimit)
                    {
                        Notify(rs, Messages.Note("wait.keyboardLayout"), oncePerJob: true);
                        return new Decision(RecoveryAction.End, i, End: StepOutcome.Deferred, Detail: "keyboardLayout");
                    }
                    return new Decision(RecoveryAction.WaitThenRetry, i, Sleep: PollInterval);
                }
                if (Unreachable(att.Detail) is { } reason)
                {
                    // fallback ladder (FALLBACKS.md): the press cannot happen from where the cursor rests; the alternative
                    // aim points were tried; after a short limit the procedure ends Deferred (the caller does other work
                    // and comes back) instead of waiting out the patience and parking for hours
                    f.UnreachableSince ??= Mono;
                    if (Mono - f.UnreachableSince.Value >= HardRules.UnreachableLimit)
                    {
                        return new Decision(RecoveryAction.End, i, End: StepOutcome.Deferred, Detail: "unreachable." + reason);
                    }
                }
                return new Decision(RecoveryAction.WaitThenRetry, i, Sleep: PollInterval);
            case StepOutcome.AbortedAfterFocus:
                {
                    f.AbortStreak[i]++;
                    var seconds = Math.Min(HardRules.BackoffMax.TotalSeconds, HardRules.BackoffStart.TotalSeconds * Math.Pow(2, f.AbortStreak[i] - 1));
                    return new Decision(RecoveryAction.WaitThenRetry, i, Sleep: TimeSpan.FromSeconds(seconds), Detail: "backoff");
                }
            case StepOutcome.UserActive:
                if (commitJob)
                {
                    return new Decision(RecoveryAction.End, i, End: StepOutcome.Deferred, Detail: "commit.userActive");
                }
                return new Decision(RecoveryAction.WaitThenRetry, i, Sleep: PollInterval, Resume: true);
            case StepOutcome.UserInterference:
                if (commitJob)
                {
                    return new Decision(RecoveryAction.End, i, End: StepOutcome.Deferred, Detail: "commit.userInterference");
                }
                return new Decision(RecoveryAction.Park, i,
                    EscFor: att.UiSeen is { } iu && att.OursUi && _classifier.IsEscSafe(iu) ? iu : null, Detail: "userInterference");
            case StepOutcome.Popup:
                {
                    var ui = att.UiSeen;
                    if (ui is null)
                    {
                        // A slid capture that keeps disagreeing does not fix itself by waiting (live 2026-10-14: 30
                        // SlidTemplateMismatch aborts in a row used up the input budget): after a few, step back.
                        if (att.Detail == "aborted.SlidTemplateMismatch" && ++f.MovingStreak[i] >= MaxMismatchStreak)
                        {
                            f.MovingStreak[i] = 0;
                            // Mostly the user's resting cursor makes the slid capture unusable (live 2026-10-15: the user
                            // worked on the PC, the window slid partly off the drawn monitor): wait for it to move first,
                            // and only after a few such pauses treat the screen as lost.
                            if (++f.MismatchPauses[i] <= MaxMismatchPauses)
                            {
                                return new Decision(RecoveryAction.WaitThenRetry, i, Sleep: MismatchPause, Detail: "mismatch.pause");
                            }
                            f.MismatchPauses[i] = 0;
                            return DecideCounted(rs, f, i, att with { Outcome = StepOutcome.PreconditionLost });
                        }
                        return new Decision(RecoveryAction.WaitThenRetry, i, Sleep: PollInterval, Detail: "popup.noUi");
                    }
                    var kind = _classifier.Classify(ui);
                    if (kind is UiKind.Forbidden or UiKind.Social)
                    {
                        return new Decision(RecoveryAction.WaitThenRetry, i, Sleep: PollInterval, Detail: "popup.blocking");
                    }
                    if (_classifier.IsEscSafe(ui))
                    {
                        return new Decision(RecoveryAction.CloseThenRetry, i, EscFor: ui, Detail: "popup.esc");
                    }
                    return new Decision(RecoveryAction.Park, i, Detail: "popup.userUi");
                }
            case StepOutcome.FocusFailedOnce:
                return new Decision(RecoveryAction.WaitThenRetry, i, Sleep: FocusFailedRetry);
            case StepOutcome.ClockJump:
                f.WaitSince = null;
                f.UnreachableSince = null; // a resume, pause, step back or settle starts the unreachable limit again (2026-10-16 review)
                return new Decision(RecoveryAction.RetryUncounted, i, Sleep: HardRules.UncertainSettle, Detail: "clock.settle");
            case StepOutcome.RateLimited:
                {
                    var next = SafePacing();
                    return new Decision(RecoveryAction.WaitThenRetry, i, Sleep: next < PollInterval ? PollInterval : next);
                }
            case StepOutcome.Throttled:
                return new Decision(RecoveryAction.WaitThenRetry, i, Sleep: ThrottleWait());
            case StepOutcome.WindowIncidentRestored:
                {
                    var now = Mono;
                    _incidents.RemoveAll(t => now - t > TimeSpan.FromHours(1));
                    _incidents.Add(now);
                    if (_incidents.Count > HardRules.MaxRestoredIncidentsPerHour)
                    {
                        return HaltDecision(i, HaltCode.TooManyWindowIncidents, att.Subject ?? step.Id);
                    }
                    return new Decision(RecoveryAction.WaitThenRetry, i, Sleep: IncidentPause, Resume: true, CheckHome: true, Detail: "incident.pause");
                }
            case StepOutcome.PreconditionTransient:
                // A target that keeps moving between captures does not settle by waiting (live 2026-10-14: the list
                // anchor read "template.moving" ~230 times until the budget ran out): after a few tries step back.
                if (att.Detail is { } md && (md.StartsWith("template.moving:", StringComparison.Ordinal) || md.StartsWith("relocate.moved:", StringComparison.Ordinal))
                    && ++f.MovingStreak[i] >= MaxMovingStreak)
                {
                    f.MovingStreak[i] = 0;
                    return DecideCounted(rs, f, i, att with { Outcome = StepOutcome.PreconditionLost });
                }
                return new Decision(RecoveryAction.WaitThenRetry, i, Sleep: PollInterval);
            case StepOutcome.WrongUiUnverified:
                f.Uncounted[i]++;
                if (f.Uncounted[i] > limits.MaxUncountedPerStep)
                {
                    f.Uncounted[i] = 0;
                    return CountedLadder(rs, f, i, "uncounted.exhausted");
                }
                if (att.UiSeen is { } wu && att.OursUi && _classifier.IsEscSafe(wu))
                {
                    return new Decision(RecoveryAction.CloseThenRetry, i, EscFor: wu, Detail: "wrongUi.esc");
                }
                return new Decision(RecoveryAction.RetryUncounted, i, Sleep: PollInterval);
            case StepOutcome.UnknownUi:
                if (JobScope.Recent(rs.Job.UnknownUis, Mono) >= 2)
                {
                    return HaltDecision(i, HaltCode.UnknownUiTwice, att.UiSeen ?? step.Id);
                }
                return new Decision(RecoveryAction.CloseThenRetry, i, EscFor: att.UiSeen, EscForNil: att.UiSeen is null, SurveyCapture: true, Detail: "unknownUi.esc");
            case StepOutcome.UserUiOpen:
                return new Decision(RecoveryAction.Park, i, Detail: "userUiOpen");
            default:
                return new Decision(RecoveryAction.WaitThenRetry, i, Sleep: PollInterval);
        }
    }

    private static Decision HaltDecision(int i, HaltCode code, string? subject) =>
        new(RecoveryAction.Halt, i, Halt: code, HaltSubject: subject);

    /// <summary>
    /// The not-ready reasons that depend on where the cursor rests, not on the user being busy (FALLBACKS.md): waiting
    /// for them may never end while the user works on another monitor, so they are bounded by
    /// <see cref="HardRules.UnreachableLimit"/>. Null for a detail that is not one of them.
    /// </summary>
    internal static string? Unreachable(string? detail) =>
        detail is { } d && d.StartsWith("ready.", StringComparison.Ordinal)
            && Enum.TryParse<NotReadyReason>(d["ready.".Length..], out var r)
            && r is NotReadyReason.CrossMonitor or NotReadyReason.MonitorRule or NotReadyReason.SlideTooFar
                or NotReadyReason.NoCover or NotReadyReason.CoverRejected or NotReadyReason.ExposedArea or NotReadyReason.CursorNotOnGame
            ? r.ToString()
            : null;

    /// <summary>
    /// The last rung of an exhausted recovery ladder (FALLBACKS.md): end Deferred, no latched HALT, while fewer than
    /// <see cref="HardRules.SoftEndsBeforeHalt"/> soft ends happened in <see cref="HardRules.SoftEndWindow"/>; one more is
    /// the HALT (something keeps going wrong).
    /// </summary>
    private Decision SoftOrHalt(int i, HaltCode code, string? subject)
    {
        lock (_softEnds)
        {
            while (_softEnds.Count > 0 && Mono - _softEnds.Peek() > HardRules.SoftEndWindow)
            {
                _softEnds.Dequeue();
            }

            if (_softEnds.Count >= HardRules.SoftEndsBeforeHalt)
            {
                return HaltDecision(i, code, subject);
            }

            _softEnds.Enqueue(Mono);
        }

        return new Decision(RecoveryAction.End, i, End: StepOutcome.Deferred, Detail: "exhausted." + code);
    }

    private Decision CycleCheck(RunState rs, ProcFrame f, int i, Attempt att, Decision d)
    {
        var cls = OutcomePolicy.ClassOf(att.Outcome);
        if (cls is OutcomeClass.Ok or OutcomeClass.Halt or OutcomeClass.Terminal || d.Action is RecoveryAction.Halt or RecoveryAction.End)
        {
            return d;
        }
        if (cls == OutcomeClass.Wait && !att.Delivered)
        {
            return d;
        }
        var key = f.Def.Id + "/" + f.Def.Steps[i].Id + "|" + att.Outcome + "|" + UiStateHash(f, i);
        rs.Job.Cycle.TryGetValue(key, out var n);
        rs.Job.Cycle[key] = ++n;
        if (n <= CycleLimit)
        {
            return d;
        }
        var soft = SoftOrHalt(i, HaltCode.RepeatedState, f.Def.Steps[i].Id);
        if (soft.Action == RecoveryAction.End)
        {
            // a soft end gives a later retry in the same job a fresh count (2026-10-16 safety review: the job-wide counts
            // ended every retry at its first recurrence and filled the soft-end ledger towards the HALT)
            foreach (var stale in rs.Job.Cycle.Keys.Where(k => k.StartsWith(f.Def.Id + "/", StringComparison.Ordinal)).ToList())
            {
                rs.Job.Cycle.Remove(stale);
            }
        }
        return soft;
    }

    private void RecordAttempt(RunState rs, ProcFrame f, int i, Attempt att, Decision d)
    {
        var cls = OutcomePolicy.ClassOf(att.Outcome);
        var rec = new StepAttemptRecord(f.StepKey(i), f.AttemptNo[i], att.Outcome, cls, d.Action, att.Phase, att.AimVerified,
            JournalSchema.IsCode(att.UiSeen) ? att.UiSeen : JournalSchema.Redacted, JournalSchema.IsCode(d.Detail ?? att.Detail) ? d.Detail ?? att.Detail : JournalSchema.Redacted, _clock.UtcNow,
            att.Detail is null ? null : JournalSchema.IsCode(att.Detail) ? att.Detail : JournalSchema.Redacted);
        if (rs.Attempts.Count < 2000)
        {
            rs.Attempts.Add(rec);
        }
        Write(rs, JournalKind.Decision, rec.StepId,
            ("attempt", rec.Attempt), ("outcome", att.Outcome), ("class", cls), ("action", d.Action),
            ("detail", att.Detail), ("decisionDetail", d.Detail), ("uiSeen", att.UiSeen), ("aim", att.AimVerified), ("phase", att.Phase),
            ("delivered", att.Delivered), ("counted", f.Counted[i]), ("uncounted", f.Uncounted[i]), ("stepBacks", f.StepBacks[i]),
            ("restarts", f.Restarts), ("preLost", f.PreconditionLost), ("inputs", rs.Inputs), ("aborted", rs.Aborted), ("clicks", rs.Clicks), ("escs", rs.Escs),
            ("stateHash", UiStateHash(f, i)), ("halt", d.Halt), ("haltSubject", d.HaltSubject));
    }

    private int Apply(RunState rs, ProcFrame f, int i, Attempt att, Decision d)
    {
        switch (d.Action)
        {
            case RecoveryAction.Halt:
                throw new HaltSignal(d.Halt ?? HaltCode.InternalError, d.HaltSubject, rs.CurrentStep, att.Outcome,
                    escUi: att.UiSeen == "pay" ? null : att.UiSeen, escForbidden: att.Outcome == StepOutcome.ForbiddenUi && att.OursUi);
            case RecoveryAction.End:
                throw new EndSignal(d.End ?? att.Outcome, d.Detail ?? att.Detail);
            case RecoveryAction.Proceed:
                f.ResetStep(i);
                // only recurrences with no step proceeding in between are a cycle (audit R2.8: recurrences hours apart
                // in a long job added up towards the RepeatedState limit)
                var proceeded = f.Def.Id + "/" + f.Def.Steps[i].Id + "|";
                foreach (var key in rs.Job.Cycle.Keys.Where(k => k.StartsWith(proceeded, StringComparison.Ordinal)).ToList())
                {
                    rs.Job.Cycle.Remove(key);
                }

                if (f.Def.Steps[i].Action is ResetAction && rs.InitialHudPending)
                {
                    RecordInitialHud(rs, beforeReset: false);
                }
                return d.Next;
            case RecoveryAction.StepBack:
                f.WaitSince = null;
                f.UnreachableSince = null; // a resume, pause, step back or settle starts the unreachable limit again (2026-10-16 review)
                return d.Next;
            case RecoveryAction.ResetAndRestart:
                RunSub(rs, f, InternalReset);
                f.ResetAfterRestart();
                return 0;
            case RecoveryAction.Park:
                {
                    // audit R1.3 (incident 10): a park whose resume changed nothing parked again for the same reason,
                    // forever (patience, park, a 5-s resume, patience again); the second one ends the procedure
                    var why = att.Detail ?? d.Detail ?? att.Outcome.ToString();
                    if (!f.Parks.Add(f.StepKey(i) + "|" + att.Outcome + "|" + why))
                    {
                        Write(rs, JournalKind.Park, rs.CurrentStep, ("event", "repeated"), ("reason", att.Outcome), ("detail", why));
                        // what our steps left open is recorded, as a park would (safety review 2026-10-23); no press here
                        foreach (var ui in rs.Compensable.Distinct(StringComparer.Ordinal).Where(u => _log.State.AdvisoryStack.Contains(u, StringComparer.Ordinal)))
                        {
                            AddPending(rs, "ui.open." + ui, "park.repeated");
                        }

                        if (rs.HudToggled && rs.InitialHud is { } hud)
                        {
                            AddPending(rs, "hud.restore." + hud.ToString().ToLowerInvariant(), "park.repeated");
                        }

                        // a soft end in the engine's ledger (safety review 2026-10-23): one that keeps coming back is the HALT
                        var soft = SoftOrHaltDetail(i, HaltCode.RepeatedState, f.Def.Steps[i].Id, "park.repeated:" + why);
                        if (soft.Action == RecoveryAction.Halt)
                        {
                            throw new HaltSignal(soft.Halt ?? HaltCode.RepeatedState, soft.HaltSubject, rs.CurrentStep, att.Outcome);
                        }

                        throw new EndSignal(StepOutcome.Deferred, soft.Detail ?? "exhausted.park.repeated");
                    }
                }
                if (d.EscFor is not null)
                {
                    CloseEsc(rs, d.EscFor, compensation: true);
                }
                Park(rs, f, i, att);
                return ResumeIndex(rs, f, i);
            default:
                if (d.SurveyCapture)
                {
                    SurveyCapture(rs, att.UiSeen);
                }
                if (d.EscFor is not null || d.EscForNil)
                {
                    CloseEsc(rs, d.EscFor, compensation: false);
                }
                if (d.CheckHome)
                {
                    try
                    {
                        var home = _gameIo.CheckHome();
                        Write(rs, JournalKind.Incident, rs.CurrentStep, ("event", "home.check"), ("atHome", home.AtHome), ("owner", home.Owner));
                    }
                    catch
                    {
                        // informational
                    }
                }
                Sleep(rs, d.Sleep);
                return d.Resume ? ResumeIndex(rs, f, i) : d.Next;
        }
    }

    private static HaltCode HaltCodeOf(StepOutcome o) => o switch
    {
        StepOutcome.ForbiddenUi => HaltCode.ForbiddenUi,
        StepOutcome.EscOpenedUi => HaltCode.EscOpenedUi,
        StepOutcome.GuideBan => HaltCode.GuideBan,
        StepOutcome.ExtraGameWindow => HaltCode.ExtraGameWindow,
        StepOutcome.CursorClipChanged => HaltCode.CursorClipChanged,
        StepOutcome.RestoreFailed => HaltCode.RestoreFailed,
        StepOutcome.GameSelfResize => HaltCode.GameSelfResize,
        StepOutcome.LayoutChanged => HaltCode.LayoutChanged,
        StepOutcome.WorldPress => HaltCode.WorldPress,
        StepOutcome.RecalibrateTemplate => HaltCode.RecalibrateTemplate,
        StepOutcome.FocusFailed => HaltCode.FocusFailed,
        StepOutcome.IntegrityMismatch => HaltCode.IntegrityMismatch,
        StepOutcome.IdentityChanged => HaltCode.IdentityChanged,
        StepOutcome.LogStalled => HaltCode.LogStalled,
        StepOutcome.RepeatedState => HaltCode.RepeatedState,
        StepOutcome.BudgetExceeded => HaltCode.BudgetExceeded,
        StepOutcome.CraftStoppedEarlyTwice => HaltCode.CraftStoppedEarlyTwice,
        StepOutcome.BlockedCraftSpot => HaltCode.BlockedCraftSpot,
        StepOutcome.AutoCollectChanged => HaltCode.AutoCollectChanged,
        StepOutcome.TamperedFiles => HaltCode.TamperedFiles,
        StepOutcome.GameStoppedMidPress => HaltCode.GameStoppedMidPress,
        _ => HaltCode.InternalError,
    };

    // ------------------------------------------------------------------ step-back / resume / sub-runs

    private int LatestEarlierPre(RunState rs, ProcFrame f, int i)
    {
        for (var j = i - 1; j >= 0; j--)
        {
            if (f.Skipped.Contains(j))
            {
                continue;
            }
            if (EvaluatePre(rs, f.Def.Steps[j]).Holds)
            {
                return j;
            }
        }
        return -1;
    }

    private int ResumeIndex(RunState rs, ProcFrame f, int i)
    {
        f.WaitSince = null;
        f.UnreachableSince = null; // a resume, pause, step back or settle starts the unreachable limit again (2026-10-16 review)
        for (var j = Math.Min(i, f.Def.Steps.Count - 1); j >= 0; j--)
        {
            if (f.Skipped.Contains(j))
            {
                continue;
            }
            if (EvaluatePre(rs, f.Def.Steps[j]).Holds)
            {
                Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "resume"), ("toStep", f.Def.Steps[j].Id));
                return j;
            }
        }
        RunSub(rs, f, InternalReset);
        return 0;
    }

    private void RunSub(RunState rs, ProcFrame parent, ProcedureDefinition proc)
    {
        if (proc.Id == InternalReset.Id && parent.Def.Id == InternalReset.Id)
        {
            // the backstop for R1.2: whatever path asks for it, a reset never runs inside a reset
            Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "invariant.resetDepth"), ("depth", parent.Depth + 1));
            throw new EndSignal(StepOutcome.Deferred, "reset.recursion");
        }
        var problems = StepValidation.Validate(proc);
        if (problems.Count > 0)
        {
            throw new EndSignal(StepOutcome.Refused, "procedure.invalid:" + proc.Id);
        }
        var saved = rs.CurrentStep;
        RunFrame(rs, new ProcFrame(proc, StepValidation.Clamp(proc.Limits), parent.Depth + 1));
        rs.CurrentStep = saved;
    }

    // ------------------------------------------------------------------ park

    private void Park(RunState rs, ProcFrame f, int i, Attempt att)
    {
        var step = f.Def.Steps[i];
        SetStatus(EngineState.Parked, rs, att.Outcome.ToString());
        Write(rs, JournalKind.Park, rs.CurrentStep, ("event", "start"), ("reason", att.Outcome), ("detail", att.Detail));
        if (att.Outcome == StepOutcome.UserUiOpen || att.Outcome == StepOutcome.Popup)
        {
            Notify(rs, Messages.Note("park.userUiOpen"), oncePerJob: true);
        }
        CompensateOnPark(rs);
        if (rs.HudToggled && rs.InitialHud is { } h)
        {
            AddPending(rs, "hud.restore." + h.ToString().ToLowerInvariant(), "park");
        }
        var start = Mono;
        var notifiedLong = false;
        TimeSpan? unreachableSince = null;
        TimeSpan? pausedAt = null;
        while (true)
        {
            rs.Ct.ThrowIfCancellationRequested();
            // a pause holds the park like WaitWhilePaused does: its time counts neither towards the expiry nor towards
            // the 30-minute notice (audit R2.8, #49)
            var elapsed = Mono - start - (pausedAt is { } since ? Mono - since : TimeSpan.Zero);
            if (elapsed >= HardRules.ParkExpiry)
            {
                Notify(rs, Messages.Note("park.expired"), oncePerJob: false);
                Write(rs, JournalKind.Park, rs.CurrentStep, ("event", "expire"));
                throw new EndSignal(StepOutcome.Cancelled, "park.expired");
            }
            if (!notifiedLong && elapsed >= HardRules.NotifyAfterParked)
            {
                notifiedLong = true;
                Notify(rs, Messages.Note("park.long"), oncePerJob: false);
            }
            Sleep(rs, ParkPoll);
            if (_pause.IsPaused)
            {
                if (_stop.IsStopRequested)
                {
                    throw new EndSignal(StepOutcome.Stopped, "stop");
                }

                pausedAt ??= Mono;
                continue;
            }

            if (pausedAt is { } resumed)
            {
                start += Mono - resumed;
                pausedAt = null;
            }
            var g = EvaluateGates(rs, f, step, ScopeOf(rs, f, step), checkLogQuiet: false, out var detail);
            if (g is { } o)
            {
                switch (OutcomePolicy.ClassOf(o))
                {
                    case OutcomeClass.Halt:
                        throw new HaltSignal(HaltCodeOf(o), step.Id, rs.CurrentStep, o);
                    case OutcomeClass.Terminal:
                        throw new EndSignal(o, detail);
                }
                if (o == StepOutcome.UserActive && IsCommitJob(rs, f))
                {
                    throw new EndSignal(StepOutcome.Deferred, "commit.userActive");
                }
                continue;
            }
            if (!ResumeProbe(rs, step, att, out var why))
            {
                // the same bound as an unreachable step (2026-10-16 review: a parked click step whose cursor rests where no
                // press reaches waited up to ParkExpiry)
                if (Unreachable("ready." + why) is { } far)
                {
                    unreachableSince ??= Mono;
                    if (Mono - unreachableSince.Value >= HardRules.UnreachableLimit)
                    {
                        Write(rs, JournalKind.Park, rs.CurrentStep, ("event", "unreachable"), ("reason", far));
                        throw new EndSignal(StepOutcome.Deferred, "unreachable." + far);
                    }
                }
                else
                {
                    unreachableSince = null;
                }

                continue;
            }
            Write(rs, JournalKind.Park, rs.CurrentStep, ("event", "resume"), ("parkedMs", Mono - start));
            SetStatus(EngineState.Running, rs, null);
            f.WaitSince = null;
            f.UnreachableSince = null; // a resume, pause, step back or settle starts the unreachable limit again (2026-10-16 review)
            return;
        }
    }

    private bool ResumeProbe(RunState rs, StepDefinition step, Attempt att, out NotReadyReason why)
    {
        why = NotReadyReason.None;
        try
        {
            if (att.Outcome is StepOutcome.UserUiOpen or StepOutcome.Popup && att.UiSeen is { } ui)
            {
                return !_log.State.AdvisoryStack.Contains(ui, StringComparer.Ordinal);
            }
            // a click step resumes only from a valid cursor spot (idle, still cursor over a valid cover)
            if (ClickTemplateOf(step) is { } tid && ExpectedClickPoint(step, tid) is { } point)
            {
                var r = _gameIo.CheckReadiness(new ReadinessQuery(InputKind.Click, point, false));
                why = r.Reason;
                return r.Ready;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void CompensateOnPark(RunState rs)
    {
        foreach (var ui in rs.Compensable.AsEnumerable().Reverse().Distinct(StringComparer.Ordinal).ToList())
        {
            var stack = _log.State.AdvisoryStack;
            if (!stack.Contains(ui, StringComparer.Ordinal))
            {
                continue;
            }
            if (!_classifier.IsEscSafe(ui) || !string.Equals(stack[^1], ui, StringComparison.Ordinal))
            {
                AddPending(rs, "compensate." + ui, "notClosable");
                continue;
            }
            CloseEsc(rs, ui, compensation: true);
        }
        rs.Compensable.Clear();
    }

    // ------------------------------------------------------------------ optional steps / pending compensation

    private string? OptionalBlocked(StepDefinition step)
    {
        switch (step.Action)
        {
            case ClickAction c:
                if (_templates.Get(c.TemplateId) is null || !_templates.IsClickable(c.TemplateId))
                {
                    return "template.notClickable:" + c.TemplateId;
                }
                return SafeSlideSwitch() ? null : "slideClick.off";
            case DragAction d:
                if (_templates.Get(d.TemplateId) is null || !_templates.IsClickable(d.TemplateId))
                {
                    return "template.notClickable:" + d.TemplateId;
                }
                return SafeSlideSwitch() ? null : "slideClick.off";
            case ToggleHudAction t:
                {
                    var id = HudTemplates.ToggleToReach(t.Target);
                    if (id is null || _templates.Get(id) is null || !_templates.IsClickable(id))
                    {
                        return "template.notClickable:" + id;
                    }
                    return SafeSlideSwitch() ? null : "slideClick.off";
                }
            case ReadAction r:
                return _readers.Get(r.ReaderId) is { Status: ReaderStatus.Ready } ? null : "reader.needsSurvey:" + r.ReaderId;
            case NeedsSurveyAction n:
                return "needsSurvey:" + n.What;
            default:
                return null;
        }
    }

    private void SkipOptional(RunState rs, ProcFrame f, int i, string why)
    {
        f.Skipped.Add(i);
        AddPending(rs, "skipped." + f.Def.Steps[i].Id, why);
        Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "optional.skipped"), ("detail", why));
        NotifyPendingOnce(rs);
    }

    private void AddPending(RunState rs, string code, string detail)
    {
        var c = JournalSchema.IsCode(code) ? code : "pending";
        var d = JournalSchema.IsCode(detail) ? detail : JournalSchema.Redacted;
        if (!rs.Pending.Any(p => p.Code == c))
        {
            rs.Pending.Add(new PendingCompensation(c, d));
            Write(rs, JournalKind.Decision, rs.CurrentStep, ("event", "pending"), ("code", c), ("detail", d));
        }
    }

    private void NotifyPendingOnce(RunState rs)
    {
        if (rs.PendingNotified)
        {
            return;
        }
        rs.PendingNotified = true;
        Notify(rs, Messages.Note("procedure.pending"), oncePerJob: false);
    }

    // ------------------------------------------------------------------ pre-flight (zero inputs)

    private void Preflight(RunState rs, ProcedureDefinition p, int depth, HashSet<string> visiting)
    {
        if (StepValidation.Validate(p).Count > 0)
        {
            throw new EndSignal(StepOutcome.Refused, "procedure.invalid:" + p.Id);
        }
        if (depth > MaxCallDepth)
        {
            throw new EndSignal(StepOutcome.Refused, "call.depth");
        }
        if (!visiting.Add(p.Id))
        {
            throw new EndSignal(StepOutcome.Refused, "call.cycle:" + p.Id);
        }
        if (p.Kind == ProcedureKind.Commit && !SwitchOn(p.Gate == AutomationSwitch.None ? rs.Ctx.Switch : p.Gate))
        {
            throw new EndSignal(StepOutcome.Refused, "switch.off");
        }
        foreach (var step in p.Steps)
        {
            var optional = step.Flags.HasFlag(StepFlags.Optional);
            if (step.Flags.HasFlag(StepFlags.Commit))
            {
                CommitPreflight(rs, p.Gate);
            }
            switch (step.Action)
            {
                case ClickAction c:
                    PreflightTemplate(rs, p, c.TemplateId, optional, isToggle: false);
                    break;
                case DragAction d:
                    if (_templates.Get(d.TemplateId) is { } dragDef && IsCommitTemplate(dragDef))
                    {
                        // a drag scrolls; it never lands on a commit target
                        throw new EndSignal(StepOutcome.Refused, "drag.commitTemplate:" + d.TemplateId);
                    }
                    PreflightTemplate(rs, p, d.TemplateId, optional, isToggle: false);
                    break;
                case ToggleHudAction t:
                    if (HudTemplates.ToggleToReach(t.Target) is { } tid)
                    {
                        PreflightTemplate(rs, p, tid, optional, isToggle: true);
                    }
                    break;
                case ReadAction r:
                    if (!optional && _readers.Get(r.ReaderId) is not { Status: ReaderStatus.Ready })
                    {
                        throw new EndSignal(StepOutcome.NeedsSurvey, "reader.needsSurvey:" + r.ReaderId);
                    }
                    break;
                case NeedsSurveyAction n:
                    if (!optional)
                    {
                        throw new EndSignal(StepOutcome.NeedsSurvey, "needsSurvey:" + n.What);
                    }
                    break;
                case CallProcedureAction call:
                    {
                        var callee = _procedures.Get(call.ProcedureId) ?? throw new EndSignal(StepOutcome.Refused, "procedure.missing:" + call.ProcedureId);
                        Preflight(rs, callee, depth + 1, visiting);
                        break;
                    }
                case NavigateAction n:
                    if (!NavigationReachable(n.ScreenId))
                    {
                        throw new EndSignal(StepOutcome.NeedsSurvey, "nav.needsSurvey:" + n.ScreenId);
                    }
                    break;
                case CaptureAction cap:
                    if (cap.Scope == CaptureScope.FullFrameMasked && rs.Ctx.Kind != JobKind.Survey)
                    {
                        throw new EndSignal(StepOutcome.Refused, "capture.fullFrame.notSurvey");
                    }
                    break;
            }
        }
        visiting.Remove(p.Id);
    }

    private void PreflightTemplate(RunState rs, ProcedureDefinition p, string templateId, bool optional, bool isToggle)
    {
        var def = _templates.Get(templateId);
        if (def is null)
        {
            if (optional)
            {
                return;
            }
            throw new EndSignal(StepOutcome.NeedsSurvey, "template.missing:" + templateId);
        }
        if (def.Tags.HasFlag(TargetTags.Forbidden) || SafeButtons(templateId).Any(b => b.Safety == ButtonSafety.Forbidden))
        {
            throw new EndSignal(StepOutcome.Refused, "target.forbidden:" + templateId);
        }
        if (!optional && !_templates.IsClickable(templateId))
        {
            throw new EndSignal(StepOutcome.NeedsSurvey, (_templates.IsSuspect(templateId) ? "template.suspect:" : "template.provisional:") + templateId);
        }
        if (!optional && !SafeSlideSwitch())
        {
            throw new EndSignal(StepOutcome.Refused, "slideClick.off");
        }
        if (!isToggle && IsCommitTemplate(def))
        {
            CommitPreflight(rs, CommitSwitchOf(p.Gate, def, rs.Ctx.Switch));
        }
    }

    private void CommitPreflight(RunState rs, AutomationSwitch gate)
    {
        if (gate == AutomationSwitch.None || !SwitchOn(gate))
        {
            throw new EndSignal(StepOutcome.Refused, "switch.off:" + gate);
        }
    }

    private bool SwitchOn(AutomationSwitch s)
    {
        try
        {
            return s == AutomationSwitch.None || _switches.IsOn(s);
        }
        catch
        {
            return false;
        }
    }

    private bool NavigationReachable(string target)
    {
        var screen = _uiMap.GetScreen(target);
        if (screen is null)
        {
            return false;
        }
        foreach (var from in _uiMap.Map.Screens.Where(s => s.IsHud))
        {
            if (string.Equals(from.Id, target, StringComparison.Ordinal))
            {
                return true;
            }
            var path = _planner.FindPath(from.Id, target);
            if (path is not null && path.Hops.All(HopExecutable))
            {
                return true;
            }
        }
        return false;
    }

    private bool HopExecutable(ButtonDefinition hop) =>
        hop.Status == SurveyStatus.Known
        && hop.Safety is ButtonSafety.Navigation or ButtonSafety.Toggle
        && (hop.TemplateId is { } t ? _templates.Get(t) is not null && _templates.IsClickable(t) : hop.Key is not null);

    // ------------------------------------------------------------------ classification helpers for steps

    private GateScope ScopeOf(RunState rs, ProcFrame f, StepDefinition step) => step.Action switch
    {
        KeyAction => IsCommitStep(rs, f, step) ? GateScope.Commit : GateScope.Key,
        ResetAction => GateScope.Key,
        ClickAction or ToggleHudAction or RestoreHudAction => IsCommitStep(rs, f, step) ? GateScope.Commit : GateScope.Click,
        DragAction => GateScope.Click,
        AwaitCraftAction => GateScope.Await,
        _ => GateScope.Passive,
    };

    private bool IsCommitStep(RunState rs, ProcFrame f, StepDefinition step)
    {
        if (step.Flags.HasFlag(StepFlags.Commit))
        {
            return true;
        }
        return step.Action is ClickAction c && _templates.Get(c.TemplateId) is { } def && IsCommitTemplate(def);
    }

    private bool IsCommitTemplate(TemplateDefinition def) =>
        def.Tags.HasFlag(TargetTags.Commit) || SafeButtons(def.Id).Any(b => b.Safety == ButtonSafety.Commit);

    private AutomationSwitch CommitSwitchOf(AutomationSwitch procedureGate, TemplateDefinition? def, AutomationSwitch jobSwitch)
    {
        if (procedureGate != AutomationSwitch.None)
        {
            return procedureGate;
        }
        if (def is not null)
        {
            if (def.Gate != AutomationSwitch.None)
            {
                return def.Gate;
            }
            var b = SafeButtons(def.Id).FirstOrDefault(x => x.Safety == ButtonSafety.Commit && x.Gate != AutomationSwitch.None);
            if (b is not null)
            {
                return b.Gate;
            }
        }
        return jobSwitch;
    }

    private IReadOnlyList<ButtonDefinition> SafeButtons(string templateId)
    {
        try
        {
            return _uiMap.ButtonsForTemplate(templateId);
        }
        catch
        {
            return [];
        }
    }

    private static string? ClickTemplateOf(StepDefinition step) => step.Action switch
    {
        ClickAction c => c.TemplateId,
        DragAction d => d.TemplateId,
        ToggleHudAction t => HudTemplates.ToggleToReach(t.Target),
        _ => null,
    };

    private static string? TemplateOfStep(StepDefinition step, string subject) => step.Action switch
    {
        ClickAction c => c.TemplateId,
        ToggleHudAction or RestoreHudAction => subject.StartsWith("hud.toggle.", StringComparison.Ordinal) ? subject : null,
        _ => null,
    };

    private void SafeMarkSuspect(string templateId, string reason)
    {
        try
        {
            _templates.MarkSuspect(templateId, reason);
        }
        catch
        {
            // the HALT follows anyway
        }
    }

    private static string ActionCode(StepAction a) => a switch
    {
        KeyAction => "key",
        ClickAction => "click",
        DragAction => "drag",
        ToggleHudAction => "toggleHud",
        RestoreHudAction => "restoreHud",
        ResetAction => "reset",
        WaitAction => "wait",
        NavigateAction => "navigate",
        CaptureAction => "capture",
        ReadAction => "read",
        CallProcedureAction => "call",
        AwaitCraftAction => "awaitCraft",
        NeedsSurveyAction => "needsSurvey",
        _ => "unknown",
    };

    private static string? TargetCode(StepAction a) => a switch
    {
        KeyAction k => "key." + k.Key,
        ClickAction c => c.TemplateId,
        DragAction d => d.TemplateId,
        ToggleHudAction t => "hud." + t.Target,
        NavigateAction n => n.ScreenId,
        CaptureAction c => c.Label,
        ReadAction r => r.ReaderId,
        CallProcedureAction c => c.ProcedureId,
        NeedsSurveyAction n => n.What,
        _ => null,
    };

    private TimeSpan SafePacing()
    {
        try
        {
            return _gameIo.Pacing.NextAllowedIn;
        }
        catch
        {
            return PollInterval;
        }
    }

    private TimeSpan ThrottleWait()
    {
        var last = _log.State.LastThrottle;
        if (last is null)
        {
            return PollInterval;
        }
        var left = HardRules.ThrottleBackoff - (GameNow - last.Value);
        if (left < PollInterval)
        {
            return PollInterval;
        }
        return left > HardRules.ThrottleBackoff ? HardRules.ThrottleBackoff : left;
    }

    /// <summary>The Esc-safe UI on top when the one just below it is <paramref name="wanted"/>; otherwise null.</summary>
    private string? StrayPopupAbove(string wanted)
    {
        var stack = _log.State.AdvisoryStack;
        return stack.Count >= 2 && string.Equals(stack[^2], wanted, StringComparison.Ordinal) && _classifier.IsEscSafe(stack[^1])
            ? stack[^1]
            : null;
    }
}
