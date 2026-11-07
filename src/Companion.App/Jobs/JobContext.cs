using Companion.App.Hosting;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Host;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Navigation;
using Companion.Contracts.Planning;

namespace Companion.App.Jobs;

/// <summary>
/// What a job handler may use (FR-03): engine contracts only. IGameIo and ILogTracker are not reachable; the UI state is
/// a read-only snapshot taken on each access.
/// </summary>
internal sealed class JobContext : IJobContext
{
    private readonly ILogTracker _log;
    private readonly JobActivityBoard? _activity;

    /// <summary>Creates the context for one job run.</summary>
    public JobContext(
        RunContext run,
        IProcedureRunner procedures,
        IProcedureCatalog procedureCatalog,
        INavigator navigator,
        IScreenService screen,
        ILogTracker log,
        ICharacterKnowledge? knowledge,
        IPlanner planner,
        IAutomationSwitches switches,
        IArtifactStore artifacts,
        IJournal journal,
        INotifier notifier,
        IClock clock,
        CompanionConfig config,
        JobActivityBoard? activity = null)
    {
        _activity = activity;
        Run = run;
        Procedures = procedures;
        ProcedureCatalog = procedureCatalog;
        Navigator = navigator;
        Screen = screen;
        _log = log;
        Knowledge = knowledge;
        Planner = planner;
        Switches = switches;
        Artifacts = artifacts;
        Journal = journal;
        Notifier = notifier;
        Clock = clock;
        Config = config;
    }

    /// <inheritdoc />
    public RunContext Run { get; }

    /// <inheritdoc />
    public IReadOnlyList<TimeSpan> EngineSoftEnds => Procedures is Companion.Engine.ProcedureRunner runner ? runner.RecentSoftEnds() : [];

    /// <inheritdoc />
    public IProcedureRunner Procedures { get; }

    /// <inheritdoc />
    public IProcedureCatalog ProcedureCatalog { get; }

    /// <inheritdoc />
    public INavigator Navigator { get; }

    /// <inheritdoc />
    public IScreenService Screen { get; }

    /// <inheritdoc />
    public UiStateSnapshot UiState => _log.State;

    /// <inheritdoc />
    public ICharacterKnowledge? Knowledge { get; }

    /// <inheritdoc />
    public IPlanner Planner { get; }

    /// <inheritdoc />
    public IAutomationSwitches Switches { get; }

    /// <inheritdoc />
    public IArtifactStore Artifacts { get; }

    /// <inheritdoc />
    public IJournal Journal { get; }

    /// <inheritdoc />
    public INotifier Notifier { get; }

    /// <inheritdoc />
    public IClock Clock { get; }

    /// <inheritdoc />
    public CompanionConfig Config { get; }

    /// <inheritdoc />
    public void ReportActivity(string slot, string? code, IReadOnlyDictionary<string, object?>? data = null) =>
        _activity?.Set(Run.JobId, slot, code, data, Clock.UtcNow);
}

/// <summary>Builds job contexts from the composed host services (config snapshot taken per job).</summary>
internal sealed class HostJobContextFactory : IJobContextFactory
{
    private readonly HostServices _s;

    /// <summary>Creates the factory.</summary>
    public HostJobContextFactory(HostServices services)
    {
        _s = services;
    }

    /// <inheritdoc />
    public IJobContext Create(RunContext run, ICharacterKnowledge? knowledge) =>
        new JobContext(
            run, _s.Runner, _s.Procedures, _s.Navigator, _s.Screen, _s.Log, knowledge, _s.Planner, _s.Config,
            _s.Artifacts, _s.Journal, _s.Notifier, _s.Clock, _s.Config.Current, _s.Activity);
}
