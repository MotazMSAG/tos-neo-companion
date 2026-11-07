using Companion.Contracts.Engine;
using Companion.Contracts.Host;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Navigation;
using Companion.Contracts.Perception;
using Companion.Contracts.Planning;

namespace Companion.Contracts.Features;

/// <summary>A dependency on another feature.</summary>
/// <param name="FeatureId">Feature id.</param>
/// <param name="MinVersion">Minimum version (semver "1.0.0").</param>
public sealed record FeatureDependency(string FeatureId, string MinVersion);

/// <summary>
/// Data files of a feature, relative to InstallDir\features\&lt;id&gt;\. Loaded ONCE by the feature host at start
/// (after the trusted-build hash check) and handed to the catalog builders; features do no file I/O.
/// </summary>
/// <param name="UiMap">ui-map fragment (UiMap JSON).</param>
/// <param name="Templates">templates.json (TemplateDefinition[]; PNGs next to it).</param>
/// <param name="Procedures">procedures.json (ProcedureDefinition[]).</param>
/// <param name="Readers">readers.json (ReaderDefinition[]).</param>
public sealed record FeatureDataFiles(string? UiMap = null, string? Templates = null, string? Procedures = null, string? Readers = null);

/// <summary>Feature manifest (in code: <see cref="IFeature.Manifest"/>).</summary>
/// <param name="Id">Feature id ("inventory", "recipes", "crafting", "survey", "homestead", "core").</param>
/// <param name="Version">Semver.</param>
/// <param name="CoreApiVersion">Must equal <see cref="ContractsInfo.ApiVersion"/>.</param>
/// <param name="Requires">Dependencies (topological load order; a disabled dependency disables dependents).</param>
/// <param name="Data">Data files.</param>
/// <param name="Switch">Automation switch gating its commit jobs.</param>
/// <param name="ValidatedGameBuild">Game build fingerprint code its templates were validated against (null = any; mismatch flips its templates to provisional).</param>
/// <param name="Description">One line.</param>
public sealed record FeatureManifest(
    string Id,
    string Version,
    int CoreApiVersion,
    IReadOnlyList<FeatureDependency> Requires,
    FeatureDataFiles Data,
    AutomationSwitch Switch,
    string? ValidatedGameBuild,
    string Description);

/// <summary>Job parameter types (validated by the host before a handler runs; never interpolated into code, paths or regexes).</summary>
public enum JobParamType
{
    /// <summary>Integer within [Min, Max].</summary>
    Int,
    /// <summary>true/false.</summary>
    Bool,
    /// <summary>A known item key of the current character's store, or the game's item id of a known item (2026-10-15).</summary>
    ItemKey,
    /// <summary>One of <see cref="JobParamSpec.Allowed"/>.</summary>
    Enum,
}

/// <summary>A job parameter declaration.</summary>
/// <param name="Name">Name ([a-zA-Z][a-zA-Z0-9]{0,31}).</param>
/// <param name="Type">Type.</param>
/// <param name="Required">Required.</param>
/// <param name="Min">Min (Int).</param>
/// <param name="Max">Max (Int).</param>
/// <param name="Allowed">Allowed values (Enum).</param>
/// <param name="MaxLength">Max raw length (&lt;= 64).</param>
public sealed record JobParamSpec(string Name, JobParamType Type, bool Required, long? Min = null, long? Max = null, IReadOnlyList<string>? Allowed = null, int MaxLength = 64);

/// <summary>A whitelisted job.</summary>
/// <param name="Name">Job name ([A-Za-z][A-Za-z0-9]{0,39}, unique across features).</param>
/// <param name="FeatureId">Owning feature.</param>
/// <param name="Description">One line.</param>
/// <param name="Params">Parameters.</param>
/// <param name="Switch">Automation switch (jobs with Commit kind are refused while it is off).</param>
/// <param name="Kind">Kind.</param>
/// <param name="MaxPerHour">Per-job hourly cap (&lt;= host cap).</param>
public sealed record JobDefinition(string Name, string FeatureId, string Description, IReadOnlyList<JobParamSpec> Params, AutomationSwitch Switch, JobKind Kind, int MaxPerHour = 10);

/// <summary>Validated parameters.</summary>
/// <param name="Values">Values: long, bool, ItemKey or string (enum).</param>
public sealed record JobParameters(IReadOnlyDictionary<string, object> Values)
{
    /// <summary>Integer parameter.</summary>
    public long GetInt(string name) => (long)Values[name];

    /// <summary>Boolean parameter (false if absent).</summary>
    public bool GetBool(string name) => Values.TryGetValue(name, out var v) && (bool)v;

    /// <summary>Item parameter.</summary>
    public ItemKey GetItem(string name) => (ItemKey)Values[name];

    /// <summary>Enum parameter.</summary>
    public string GetEnum(string name) => (string)Values[name];

    /// <summary>Empty.</summary>
    public static JobParameters Empty { get; } = new(new Dictionary<string, object>());
}

/// <summary>Job result states.</summary>
public enum JobState
{
    /// <summary>Queued.</summary>
    Queued,
    /// <summary>Running.</summary>
    Running,
    /// <summary>Succeeded.</summary>
    Succeeded,
    /// <summary>Failed without HALT (e.g. reader Unknown).</summary>
    Failed,
    /// <summary>HALT.</summary>
    Halted,
    /// <summary>Parked (will resume).</summary>
    Parked,
    /// <summary>Refused (whitelist, params, switch, character, rate).</summary>
    Refused,
    /// <summary>Reached a needsSurvey stub.</summary>
    NeedsSurvey,
    /// <summary>Cancelled / expired.</summary>
    Cancelled,
    /// <summary>Game exited.</summary>
    GameExited,
    /// <summary>Kill switch.</summary>
    Stopped,
}

/// <summary>Result of a job handler.</summary>
/// <param name="State">State.</param>
/// <param name="Code">Short code.</param>
/// <param name="Data">Scalar summary data (counts, codes; no free text).</param>
public sealed record JobResult(JobState State, string Code, IReadOnlyDictionary<string, object?>? Data = null);

/// <summary>
/// Everything a job handler may use (built by the host per job). Features reach the game ONLY through these engine
/// contracts; IGameIo / ILogTracker are not reachable (FR-03).
/// </summary>
public interface IJobContext
{
    /// <summary>Run context (job id, kind, switch).</summary>
    RunContext Run { get; }

    /// <summary>
    /// The engine's soft ends (exhausted recoveries) inside its 30-minute window, every job's together (monotonic times):
    /// a job paces its retries by these (2026-10-31 audit: each goal paced by its own count while the previous goal's had
    /// brought the host near its HALT; on 10-28 that HALT cost 73 minutes). Empty when the host does not give them.
    /// </summary>
    IReadOnlyList<TimeSpan> EngineSoftEnds => [];

    /// <summary>Procedure runner.</summary>
    IProcedureRunner Procedures { get; }

    /// <summary>Procedure catalog.</summary>
    IProcedureCatalog ProcedureCatalog { get; }

    /// <summary>Navigator.</summary>
    INavigator Navigator { get; }

    /// <summary>Capture / read service.</summary>
    IScreenService Screen { get; }

    /// <summary>Latest UI model snapshot.</summary>
    UiStateSnapshot UiState { get; }

    /// <summary>The companion's knowledge (the one profile; null only when the store could not open it: jobs that need it must refuse).</summary>
    ICharacterKnowledge? Knowledge { get; }

    /// <summary>Planner.</summary>
    IPlanner Planner { get; }

    /// <summary>Automation switches (read-only).</summary>
    IAutomationSwitches Switches { get; }

    /// <summary>Artifact store.</summary>
    IArtifactStore Artifacts { get; }

    /// <summary>Journal.</summary>
    IJournal Journal { get; }

    /// <summary>Notifier.</summary>
    INotifier Notifier { get; }

    /// <summary>Clock.</summary>
    IClock Clock { get; }

    /// <summary>Config snapshot.</summary>
    CompanionConfig Config { get; }

    /// <summary>
    /// Reports what the job is doing, for the status card (2026-10-23, the user: "gather everything doesn't do anything
    /// clear"): <paramref name="slot"/> "job" (the job's own progress) or "step" (the current wait, e.g. a gathering trip
    /// running); a null <paramref name="code"/> clears the slot. Codes, numbers and times only; the host chooses the words.
    /// </summary>
    void ReportActivity(string slot, string? code, IReadOnlyDictionary<string, object?>? data = null)
    {
    }
}

/// <summary>Executes one job. Runs on the engine thread; must honour <c>ct</c>; exceptions are caught by the host (job fails InternalError; 3 exceptions disable the feature).</summary>
public interface IJobHandler
{
    /// <summary>Executes.</summary>
    JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct);
}

/// <summary>Registration surface handed to <see cref="IFeature.Register"/>. Valid only during the call.</summary>
public interface IFeatureRegistrar
{
    /// <summary>The registering feature id.</summary>
    string FeatureId { get; }

    /// <summary>Adds a job (name must be unique).</summary>
    void AddJob(JobDefinition definition, IJobHandler handler);

    /// <summary>Adds a code-defined procedure (data-defined ones come from the manifest's files).</summary>
    void AddProcedure(ProcedureDefinition procedure);

    /// <summary>Adds a code-defined reader.</summary>
    void AddReader(ReaderDefinition reader);
}

/// <summary>
/// A feature plug-in (features\Companion.Feature.*; references Companion.Contracts only). Compiled into the build
/// (no runtime assembly loading); enabled/disabled by config. <see cref="Register"/> must be fast (budget 5 s),
/// side-effect free apart from registrations, and may throw: the feature is then disabled and reported.
/// </summary>
public interface IFeature
{
    /// <summary>Manifest.</summary>
    FeatureManifest Manifest { get; }

    /// <summary>Registers jobs, procedures and readers.</summary>
    void Register(IFeatureRegistrar registrar);
}

/// <summary>Feature load status.</summary>
public enum FeatureLoadStatus
{
    /// <summary>Enabled.</summary>
    Enabled,
    /// <summary>Disabled in config.</summary>
    DisabledByConfig,
    /// <summary>Register threw or data invalid.</summary>
    Failed,
    /// <summary>A dependency is missing/disabled/too old.</summary>
    DependencyUnavailable,
    /// <summary>CoreApiVersion mismatch.</summary>
    ApiMismatch,
    /// <summary>Register exceeded its budget.</summary>
    Timeout,
    /// <summary>Data file hash not in the trusted build.</summary>
    Untrusted,
    /// <summary>Disabled at runtime after repeated handler exceptions.</summary>
    Faulted,
}

/// <summary>Load report of one feature.</summary>
/// <param name="FeatureId">Feature.</param>
/// <param name="Version">Version.</param>
/// <param name="Status">Status.</param>
/// <param name="ReasonCode">Reason code.</param>
public sealed record FeatureLoadReport(string FeatureId, string Version, FeatureLoadStatus Status, string? ReasonCode);

/// <summary>The job whitelist built from enabled features.</summary>
public interface IJobCatalog
{
    /// <summary>Jobs of enabled features.</summary>
    IReadOnlyList<JobDefinition> All { get; }

    /// <summary>Looks up a job and its handler.</summary>
    bool TryGet(string name, out JobDefinition? definition, out IJobHandler? handler);

    /// <summary>Validates raw string parameters against the spec; returns null and a reason code on failure.</summary>
    JobParameters? Validate(JobDefinition definition, IReadOnlyDictionary<string, string> raw, ICharacterKnowledge? knowledge, out string? reasonCode);
}

/// <summary>
/// Feature host (unit "features", Companion.Features): validates manifests, orders by dependencies, loads data files
/// once into the catalog builders, calls Register with isolation (try/catch + timeout), builds the job catalog.
/// Includes the built-in "core" feature exposing the core procedures as jobs.
/// </summary>
public interface IFeatureHost
{
    /// <summary>Loads all features once (host start).</summary>
    IReadOnlyList<FeatureLoadReport> LoadAll(IReadOnlyList<IFeature> features, FeatureLoadContext context);

    /// <summary>Job whitelist.</summary>
    IJobCatalog Jobs { get; }

    /// <summary>Marks a feature faulted (its jobs leave the whitelist).</summary>
    void Fault(string featureId, string reasonCode);

    /// <summary>Current reports.</summary>
    IReadOnlyList<FeatureLoadReport> Reports { get; }
}

/// <summary>What the feature host needs at load time.</summary>
/// <param name="Config">Config (enabled features).</param>
/// <param name="Paths">Paths.</param>
/// <param name="Templates">Template catalog builder.</param>
/// <param name="UiMap">UI map builder.</param>
/// <param name="Procedures">Procedure catalog builder.</param>
/// <param name="Readers">Reader catalog builder.</param>
/// <param name="FileTrust">Integrity check for data files.</param>
/// <param name="Journal">Journal.</param>
public sealed record FeatureLoadContext(
    CompanionConfig Config,
    CompanionPaths Paths,
    Vision.ITemplateCatalogBuilder Templates,
    IUiMapCatalogBuilder UiMap,
    IProcedureCatalogBuilder Procedures,
    IReaderCatalogBuilder Readers,
    IFileTrust FileTrust,
    IJournal Journal);
