using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Logs;
using Companion.Contracts.Navigation;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;

namespace Companion.Navigation;

/// <summary>
/// Navigator (SPEC 8.2): identify the current screen, find a path and run it through the procedure runner (which owns
/// every gate, budget and recovery rule). An unidentified screen is Reset (Esc until nil) and identified again; an
/// escCloses screen that has no forward path to the target is left through Reset as well. A path through a
/// Provisional or NeedsSurvey hop returns NeedsSurvey without any input. Arrival is confirmed by identification.
/// Runs on the engine thread (not re-entrant).
/// </summary>
public sealed class Navigator : INavigator
{
    private static readonly ProcedureDefinition FallbackReset = new(
        "Reset",
        "Esc until 'hide top ui nil'",
        [new StepDefinition("reset", StatePredicate.Any, new ResetAction(HardRules.MaxEscPerReset), new Expectation([UiExpectation.HiddenTop(null)]))],
        new ProcedureLimits(MaxEsc: HardRules.MaxEscPerReset));

    private readonly IScreenService _screen;
    private readonly IScreenIdentifier _identifier;
    private readonly IPathPlanner _planner;
    private readonly IProcedureRunner _runner;
    private readonly IProcedureCatalog _procedures;
    private readonly IUiMapCatalog _map;
    private readonly ITemplateCatalog _templates;
    private readonly ILogTracker _log;

    /// <summary>Creates the navigator (no side effects).</summary>
    public Navigator(
        IScreenService screen,
        IScreenIdentifier identifier,
        IPathPlanner planner,
        IProcedureRunner runner,
        IProcedureCatalog procedures,
        IUiMapCatalog map,
        ITemplateCatalog templates,
        ILogTracker log)
    {
        _screen = screen;
        _identifier = identifier;
        _planner = planner;
        _runner = runner;
        _procedures = procedures;
        _map = map;
        _templates = templates;
        _log = log;
    }

    /// <inheritdoc />
    public ScreenIdentification IdentifyNow(CancellationToken ct)
    {
        var cap = _screen.Capture(ct);
        var ui = _log.State;
        if (!cap.Ok)
        {
            return new ScreenIdentification(null, IdentificationConfidence.Unknown, ui.AdvisoryTop, HudState.Unknown, "capture." + cap.Problem);
        }
        return _identifier.Identify(cap.Frame!, ui);
    }

    /// <inheritdoc />
    public NavigationResult NavigateTo(string screenId, RunContext context, CancellationToken ct)
    {
        var target = screenId is null ? null : _map.GetScreen(screenId);
        if (target is null)
        {
            return new NavigationResult(NavigationStatus.NoPath, null, null);
        }
        var id = IdentifyNow(ct);
        ProcedureResult? last = null;
        if (id.Confidence == IdentificationConfidence.Unknown || id.ScreenId is null)
        {
            last = RunReset(context, ct);
            if (last.Status != ProcedureStatus.Succeeded)
            {
                return Failed(last, null);
            }
            id = IdentifyNow(ct);
            if (id.Confidence == IdentificationConfidence.Unknown || id.ScreenId is null)
            {
                return Failed(last with { FinalOutcome = StepOutcome.PreconditionLost }, null);
            }
        }
        if (string.Equals(id.ScreenId, target.Id, StringComparison.Ordinal))
        {
            return new NavigationResult(NavigationStatus.AlreadyThere, target.Id, last);
        }
        var path = _planner.FindPath(id.ScreenId, target.Id);
        if (path is null && _map.GetScreen(id.ScreenId) is { IsHud: false })
        {
            last = RunReset(context, ct);
            if (last.Status != ProcedureStatus.Succeeded)
            {
                return Failed(last, id.ScreenId);
            }
            id = IdentifyNow(ct);
            if (id.ScreenId is null)
            {
                return Failed(last with { FinalOutcome = StepOutcome.PreconditionLost }, null);
            }
            if (string.Equals(id.ScreenId, target.Id, StringComparison.Ordinal))
            {
                return new NavigationResult(NavigationStatus.Arrived, target.Id, last);
            }
            path = _planner.FindPath(id.ScreenId, target.Id);
        }
        if (path is null)
        {
            return new NavigationResult(NavigationStatus.NoPath, id.ScreenId, last);
        }
        if (path.Hops.Any(h => !Executable(h)))
        {
            return new NavigationResult(NavigationStatus.NeedsSurvey, id.ScreenId, last);
        }
        var startWithReset = _map.GetScreen(path.From) is { IsHud: true };
        var proc = _planner.BuildProcedure(path, startWithReset);
        var result = _runner.Run(proc, context, ct);
        if (result.Status == ProcedureStatus.NeedsSurvey)
        {
            return new NavigationResult(NavigationStatus.NeedsSurvey, id.ScreenId, result);
        }
        if (result.Status != ProcedureStatus.Succeeded)
        {
            return Failed(result, null);
        }
        var arrived = IdentifyNow(ct);
        return string.Equals(arrived.ScreenId, target.Id, StringComparison.Ordinal)
            ? new NavigationResult(NavigationStatus.Arrived, target.Id, result)
            : Failed(result with { FinalOutcome = StepOutcome.PreconditionLost }, arrived.ScreenId);
    }

    private ProcedureResult RunReset(RunContext context, CancellationToken ct) =>
        _runner.Run(_procedures.Get("Reset") ?? FallbackReset, context, ct);

    private static NavigationResult Failed(ProcedureResult result, string? screen) =>
        new(NavigationStatus.Failed, screen, result);

    private bool Executable(ButtonDefinition b) =>
        b.Status == SurveyStatus.Known
        && b.Safety is ButtonSafety.Navigation or ButtonSafety.Toggle
        && (b.TemplateId is { } t ? _templates.Get(t) is not null && _templates.IsClickable(t) : b.Key is not null);
}
