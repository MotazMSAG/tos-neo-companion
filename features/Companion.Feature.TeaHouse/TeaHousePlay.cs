using System.Globalization;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Features.Kit;

namespace Companion.Feature.TeaHouse;

/// <summary>
/// TeaHousePlay (2026-10-30): while the helper's auto-play is on, takes its presses from the <see cref="TeaHouseDesk"/>
/// one by one. A board press runs the single click step of that square (teahouse.board, "sq.NN": a slide click anchored
/// on the square's nearest static picture, What's On on top, character Allowed; teahouse.board.alt's second-nearest
/// picture when that one is hidden; never an Esc reset) and is confirmed only when the game logs on_grid_click for
/// that square after the click; a Restart press runs teahouse.restart while the end screen is on top. The job never
/// chooses a move. It ends when auto-play goes off, after <see cref="Idle"/> without an order, when the engine stops a
/// press (halt, stop, park, the game gone; auto-play goes off with it, so it never restarts by itself), or when a press
/// opened a window other than the end screen. Each press runs under its order's token: a press withdrawn by the helper
/// (auto-play off, the helper stopped waiting) is cancelled and never lands later (safety review 2026-10-30).
/// </summary>
internal sealed class TeaHousePlayHandler : IJobHandler
{
    /// <summary>The log's top UI while the Tea House board shows (What's On).</summary>
    internal const string BoardUi = "UI_ACTIVITY_CENTER";

    /// <summary>The round's end screen (shown after the press that fills the board).</summary>
    internal const string EndUi = "UI_TRIPLE_TOWN_SETTLE";

    /// <summary>The job ends after this long without an order.</summary>
    internal static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);

    /// <summary>How long a press may take to show in the log.</summary>
    internal static readonly TimeSpan LogWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A no-input run when auto-play stops after a press (2026-10-31): the engine's late check reads the last press's full
    /// attribution window (a Forbidden UI or pay line it caused is the same HALT as ever) even when no press follows.
    /// </summary>
    internal static readonly ProcedureDefinition Settle = new(
        "teahouse.settle", "Reads the last board press's full attribution window; no input.",
        [new StepDefinition("settle", StatePredicate.Any, new WaitAction(0), Expectation.Nothing)], ProcedureLimits.Default);

    private readonly TeaHouseDesk _desk;

    public TeaHousePlayHandler(TeaHouseDesk desk)
    {
        _desk = desk;
    }

    /// <summary>The board procedure's step for <paramref name="square"/> ("sq.00" is the tray).</summary>
    internal static string StepId(int square) => "sq." + square.ToString("00", CultureInfo.InvariantCulture);

    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        var refusal = JobKit.CheckProcedures(context, [TeaHouseFeature.BoardProcedure]);
        if (refusal is not null)
        {
            return refusal;
        }

        var board = context.ProcedureCatalog.Get(TeaHouseFeature.BoardProcedure)!;
        var presses = 0L;
        var failed = 0L;
        var settleDue = false;
        _desk.SetRunning(true);
        try
        {
            var idleSince = context.Clock.Monotonic;
            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    _desk.AutoPlay = false;
                    return JobKit.Result(JobState.Stopped, "teahouse.stopped", Counts(presses, failed));
                }

                if (!_desk.AutoPlay)
                {
                    SettleLast(context, ref settleDue);
                    return JobKit.Result(JobState.Succeeded, "teahouse.autoPlayOff", Counts(presses, failed));
                }

                if (_desk.Next(TimeSpan.FromSeconds(1), ct) is not { } order)
                {
                    // a second without an order after a press: the late check runs now, not at some later job's first input
                    SettleLast(context, ref settleDue);
                    if (context.Clock.Monotonic - idleSince > Idle)
                    {
                        return JobKit.Result(JobState.Succeeded, "teahouse.idle", Counts(presses, failed));
                    }

                    continue;
                }

                TeaPress press;
                JobResult? end;
                using (var one = CancellationTokenSource.CreateLinkedTokenSource(ct, order.Withdrawn))
                {
                    try
                    {
                        (press, end) = order.Square == TeaHouseDesk.Restart
                            ? PressRestart(context, one.Token)
                            : PressSquare(context, board, order.Square, one.Token);
                    }
                    catch (OperationCanceledException) when (order.Withdrawn.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        (press, end) = (new TeaPress(false, "press.withdrawn"), null);
                    }
                }

                // a press the helper withdrew (auto-play off, it stopped waiting) ends as withdrawn, never as an engine stop;
                // the engine's last reason stays with it (what the press was waiting for)
                if (order.Withdrawn.IsCancellationRequested && !ct.IsCancellationRequested && !press.Done)
                {
                    var at = press.Code.IndexOf('|', StringComparison.Ordinal);
                    (press, end) = (new TeaPress(false, "press.withdrawn" + (at >= 0 ? press.Code[at..] : string.Empty)), null);
                }

                _desk.Answer(order.Id, press);
                idleSince = context.Clock.Monotonic;
                settleDue = true;
                if (press.Done)
                {
                    presses++;
                }
                else
                {
                    failed++;
                }

                JobKit.Journal(context, JournalKind.Decision, TeaHouseFeature.BoardProcedure, JobKit.Data(
                    ("event", "teahouse.press"), ("square", (long)order.Square), ("done", press.Done), ("code", Codes.Safe(press.Code)),
                    ("piece", press.Piece is { } p ? (long)p : null)));
                if (end is not null)
                {
                    // the engine stopped (or a press opened a window): auto-play does not go on by itself afterwards
                    _desk.AutoPlay = false;
                    if (end.Code.StartsWith("teahouse.windowOpened", StringComparison.Ordinal))
                    {
                        SettleLast(context, ref settleDue);
                    }

                    var data = new Dictionary<string, object?>(end.Data ?? new Dictionary<string, object?>(), StringComparer.Ordinal);
                    foreach (var (k, v) in Counts(presses, failed))
                    {
                        data[k] = v;
                    }

                    return end with { Data = data };
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _desk.AutoPlay = false;
            return JobKit.Result(JobState.Stopped, "teahouse.stopped", Counts(presses, failed));
        }
        finally
        {
            _desk.SetRunning(false);
        }
    }

    /// <summary>
    /// One board press: What's On must be on top; the square's click step runs alone; the press counts only when the log
    /// shows on_grid_click for that square after the snapshot taken before the click. A procedure that did not succeed
    /// ends the job when the engine stopped it (halt, stop, park, game gone), else it is answered as not done. A step that
    /// expects nothing is judged before any window it opened, so afterwards the top window must be What's On or the end
    /// screen, else the job ends (safety review 2026-10-30). The engine reads only the first half second for windows
    /// (QuickAttribution); its late check reads the rest before the next input and ends that press's run when a window the
    /// last press opened is on top ("late.opened:&lt;ui&gt;"): the job ends then too (2026-10-31).
    /// </summary>
    internal static (TeaPress Press, JobResult? End) PressSquare(IJobContext context, ProcedureDefinition board, int square, CancellationToken ct)
    {
        var stepId = StepId(square);
        var step = board.Steps.FirstOrDefault(s => s.Id == stepId);
        if (step is null)
        {
            return (new TeaPress(false, "square.unknown"), null);
        }

        var before = context.UiState;
        if (!string.Equals(before.AdvisoryTop, BoardUi, StringComparison.Ordinal))
        {
            return (new TeaPress(false, "notOnBoard:" + Codes.Safe(before.AdvisoryTop ?? "none")), null);
        }

        var run = context.Procedures.Run(board with { Steps = [step] }, context.Run, ct);
        if (Hidden(run) && context.ProcedureCatalog.Get(TeaHouseFeature.AltBoardProcedure) is { } alt
            && alt.Steps.FirstOrDefault(s => s.Id == stepId) is { } altStep)
        {
            // the nearest picture was hidden (2026-10-30 live: the frame corner not found twice, and the engine's reset
            // pressed Esc, which closed the minigame; both board procedures now end such a run without a reset): the same
            // square once more, anchored on its second-nearest picture
            JobKit.Journal(context, JournalKind.Decision, TeaHouseFeature.BoardProcedure, JobKit.Data(
                ("event", "teahouse.altAnchor"), ("square", (long)square), ("hidden", Codes.Safe(Why(run).TrimStart('|')))));
            run = context.Procedures.Run(alt with { Steps = [altStep] }, context.Run, ct);
        }

        if (run.Status != ProcedureStatus.Succeeded)
        {
            if (run.EndDetail is { } late && late.StartsWith("late.opened:", StringComparison.Ordinal))
            {
                var ui = Codes.Safe(late["late.opened:".Length..]);
                return (new TeaPress(false, "press.openedWindow:" + ui), JobKit.Failed("teahouse.windowOpened:" + ui));
            }

            var result = JobKit.FromProcedure(run, "press.");
            return (new TeaPress(false, result.Code + Why(run)), EndsJob(run.Status) ? result : null);
        }

        var until = context.Clock.Monotonic + LogWait;
        TeaPress press;
        while (true)
        {
            if (context.UiState.LastGridClick is { } last && last.LoggedAfter(before.AsOf))
            {
                press = last.Square == square
                    ? new TeaPress(true, "pressed", last.Piece)
                    : new TeaPress(false, "press.otherSquare:" + last.Square.ToString(CultureInfo.InvariantCulture), last.Piece);
                break;
            }

            if (context.Clock.Monotonic >= until)
            {
                press = new TeaPress(false, "press.notLogged");
                break;
            }

            context.Clock.Sleep(TimeSpan.FromMilliseconds(100), ct);
        }

        // a window the press opened within the engine's first half second (the end screen about 0.15 s after the last
        // press); later ones meet the engine's late check before the next input
        var top = context.UiState.AdvisoryTop;
        if (top is not (BoardUi or EndUi))
        {
            var code = "press.openedWindow:" + Codes.Safe(top ?? "none");
            return (press with { Done = false, Code = code }, JobKit.Failed("teahouse.windowOpened:" + Codes.Safe(top ?? "none")));
        }

        return (press, null);
    }

    /// <summary>
    /// The end screen's Restart: the end screen must be on top and the restart procedure surveyed (needsSurvey until the
    /// button is cut from a capture of that screen); done when the end screen is no longer on top afterwards.
    /// </summary>
    internal static (TeaPress Press, JobResult? End) PressRestart(IJobContext context, CancellationToken ct)
    {
        var top = context.UiState.AdvisoryTop;
        if (!string.Equals(top, EndUi, StringComparison.Ordinal))
        {
            return (new TeaPress(false, "notOnEndScreen:" + Codes.Safe(top ?? "none")), null);
        }

        if (context.ProcedureCatalog.Get(TeaHouseFeature.RestartProcedure) is not { } restart)
        {
            return (new TeaPress(false, "restart.missing"), null);
        }

        if (JobKit.FindNeedsSurvey(context.ProcedureCatalog, restart).Count > 0)
        {
            return (new TeaPress(false, "restart.needsSurvey"), null);
        }

        var run = context.Procedures.Run(restart, context.Run, ct);
        if (run.Status != ProcedureStatus.Succeeded)
        {
            var result = JobKit.FromProcedure(run, "restart.");
            return (new TeaPress(false, result.Code), EndsJob(run.Status) ? result : null);
        }

        var until = context.Clock.Monotonic + LogWait;
        while (string.Equals(context.UiState.AdvisoryTop, EndUi, StringComparison.Ordinal))
        {
            if (context.Clock.Monotonic >= until)
            {
                return (new TeaPress(false, "restart.endScreenStays"), null);
            }

            context.Clock.Sleep(TimeSpan.FromMilliseconds(100), ct);
        }

        return (new TeaPress(true, "restarted"), null);
    }

    /// <summary>The engine's last reason in a run that did not succeed ("|ready.CoverRejected"), or empty.</summary>
    internal static string Why(ProcedureResult run) =>
        run.Attempts.LastOrDefault(a => !string.IsNullOrEmpty(a.DetailCode)) is { DetailCode: { } code } ? "|" + Codes.Safe(code) : string.Empty;

    /// <summary>A run that ended (not stopped) without a click because its anchor picture was not found on the screen.</summary>
    internal static bool Hidden(ProcedureResult run) =>
        run.Status == ProcedureStatus.Deferred && run.Clicks == 0
        && run.Attempts.Any(a => a.CauseStartsWith("template.notFound"));

    /// <summary>Runs <see cref="Settle"/> once after the last press (no input; a HALT it finds shows in the result).</summary>
    private static void SettleLast(IJobContext context, ref bool due)
    {
        if (!due)
        {
            return;
        }

        due = false;
        var run = context.Procedures.Run(Settle, context.Run, CancellationToken.None);
        if (run.Status != ProcedureStatus.Succeeded)
        {
            JobKit.Journal(context, JournalKind.Decision, TeaHouseFeature.BoardProcedure, JobKit.Data(
                ("event", "teahouse.settle"), ("status", run.Status.ToString()), ("detail", Codes.Safe(run.EndDetail ?? string.Empty))));
        }
    }

    /// <summary>The engine stopped the press for a reason the job must not press through.</summary>
    internal static bool EndsJob(ProcedureStatus status) =>
        status is ProcedureStatus.Halted or ProcedureStatus.Stopped or ProcedureStatus.Cancelled or ProcedureStatus.Parked or ProcedureStatus.GameExited;

    private static Dictionary<string, object?> Counts(long presses, long failed) => JobKit.Data(("presses", presses), ("notDone", failed));
}
