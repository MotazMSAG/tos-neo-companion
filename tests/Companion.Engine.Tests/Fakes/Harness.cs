using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Navigation;
using Companion.Contracts.Safety;
using Companion.Engine;
using Companion.Navigation;

namespace Companion.EngineNav.Testing;

/// <summary>Builds the real engine + navigation classes over the fakes and the real data files.</summary>
public sealed class Harness
{
    public Harness()
    {
        Clock = new FakeClock();
        Classifier = new FakeClassifier();
        Log = new FakeLog(Clock, Classifier);
        Game = new GameSim(Clock, Log);
        Templates = new FakeTemplates();
        Vision = new FakeVision(Game, Log, Templates, Classifier);
        Readers = new FakeReaders();
        ScreenReader = new FakeScreenReader(Clock);
        Journal = new FakeJournal();
        Artifacts = new FakeArtifacts();
        Halt = new FakeHaltLatch();
        Notifier = new FakeNotifier();
        Switches = new FakeSwitches();
        Config = new FakeConfig();
        Trust = new FakeTrust();
        Stop = new FakeStop();
        Pause = new PauseControl(Journal, Clock);
        Paths = TestPaths.ForTest("harness-unused");
        Screen = new ScreenService(Game, Vision, ScreenReader, Readers, Log, Clock);
        Map = new UiMapCatalog(Templates, Journal, Clock);
        Map.AddFragment(LoadUiMap(), "core");
        Map.Seal();
        Procedures = new ProcedureCatalog(Templates, Readers, Map, Journal, Clock);
        foreach (var p in LoadProcedures())
        {
            Procedures.Add(p, "core");
        }
        Procedures.Seal();
        Identifier = new ScreenIdentifier(Map, Vision, Templates);
        Planner = new PathPlanner(Map, Templates);
        Runner = new ProcedureRunner(Game, Log, Classifier, Vision, Templates, Readers, Screen, Procedures, Map, Identifier, Planner,
            Journal, Artifacts, Halt, Notifier, Switches, Config, Trust, Stop, Pause, Clock, Paths);
    }

    public FakeClock Clock { get; }
    public FakeClassifier Classifier { get; }
    public FakeLog Log { get; }
    public GameSim Game { get; }
    public FakeTemplates Templates { get; }
    public FakeVision Vision { get; }
    public FakeReaders Readers { get; }
    public FakeScreenReader ScreenReader { get; }
    public FakeJournal Journal { get; }
    public FakeArtifacts Artifacts { get; }
    public FakeHaltLatch Halt { get; }
    public FakeNotifier Notifier { get; }
    public FakeSwitches Switches { get; }
    public FakeConfig Config { get; }
    public FakeTrust Trust { get; }
    public FakeStop Stop { get; }
    public PauseControl Pause { get; }
    public CompanionPaths Paths { get; }
    public ScreenService Screen { get; }
    public UiMapCatalog Map { get; }
    public ProcedureCatalog Procedures { get; }
    public ScreenIdentifier Identifier { get; }
    public PathPlanner Planner { get; }
    public ProcedureRunner Runner { get; }

    public static string DataFile(string name) => Path.Combine(AppContext.BaseDirectory, "data", name);

    public static UiMap LoadUiMap() =>
        JsonSerializer.Deserialize<UiMap>(File.ReadAllBytes(DataFile("ui-map.json")), ContractJson.Options)
        ?? throw new InvalidDataException("ui-map.json");

    public static ProcedureDefinition[] LoadProcedures() =>
        JsonSerializer.Deserialize<ProcedureDefinition[]>(File.ReadAllBytes(DataFile("procedures.json")), ContractJson.Options)
        ?? throw new InvalidDataException("procedures.json");

    public RunContext Context(JobKind kind = JobKind.Navigation, AutomationSwitch sw = AutomationSwitch.None, string? jobId = null) =>
        new(jobId ?? Guid.NewGuid().ToString("N"), "TestJob", kind, sw);

    public ProcedureResult Run(string procedureId, JobKind kind = JobKind.Navigation, CancellationToken ct = default) =>
        Runner.Run(procedureId, Context(kind), ct);

    public ProcedureResult Run(ProcedureDefinition procedure, JobKind kind = JobKind.Navigation, AutomationSwitch sw = AutomationSwitch.None, CancellationToken ct = default) =>
        Runner.Run(procedure, Context(kind, sw), ct);

    public int Keys(GameKey key) => Game.Keys(key);

    public IEnumerable<StepOutcome> Outcomes(ProcedureResult r) => r.Attempts.Select(a => a.Outcome);

    public int Count(ProcedureResult r, StepOutcome o) => r.Attempts.Count(a => a.Outcome == o);

    /// <summary>Journal schema lint (SPEC 13 #30): every record written in the scenario must be in journal form.</summary>
    public void AssertJournalClean()
    {
        foreach (var rec in Journal.Records)
        {
            var ok = Companion.Engine.Internal.JournalSchema.IsClean(rec, out var problem);
            Assert.IsTrue(ok, $"journal record {rec.Kind} not clean: {problem}");
        }
    }
}

/// <summary>Runtime folders for tests, always under the test output folder (never outside companion\).</summary>
public static class TestPaths
{
    public static CompanionPaths ForTest(string name)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-runtime", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        var p = CompanionPaths.Default(AppContext.BaseDirectory) with { RuntimeDir = root };
        return p;
    }
}
