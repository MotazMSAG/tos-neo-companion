using Companion.App.Jobs;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Host;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Safety;

namespace Companion.App.Tests;

[TestClass]
public sealed class AssemblySetup
{
    /// <summary>Removes temp folders of earlier runs (only under this test's output folder).</summary>
    [AssemblyInitialize]
    public static void Init(TestContext _)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "tmp");
        if (Directory.Exists(root))
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // a previous run still holds a file: harmless, every test uses its own folder
            }
        }
    }
}

internal static class TestDirs
{
    /// <summary>A fresh, NOT yet created folder under bin\...\tmp.</summary>
    public static string NewPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "tmp", name + "-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Paths whose runtime and install folders are under the test output (game paths point to nothing).</summary>
    public static CompanionPaths Paths(string? installDir = null, string? runtimeDir = null)
    {
        var install = installDir ?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var runtime = runtimeDir ?? NewPath("runtime");
        var game = NewPath("nogame");
        return new CompanionPaths(
            install,
            Path.Combine(install, "data"),
            Path.Combine(install, "templates"),
            Path.Combine(install, "features"),
            runtime,
            game,
            Path.Combine(game, "Log", "log.txt"),
            Path.Combine(game, "TOS.exe"));
    }
}

internal sealed class FakeClock : IClock
{
    private readonly object _gate = new();
    private TimeSpan _mono = TimeSpan.FromHours(1);
    private DateTimeOffset _utc = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _utc;
            }
        }
    }

    public TimeSpan Monotonic
    {
        get
        {
            lock (_gate)
            {
                return _mono;
            }
        }
    }

    public void Advance(TimeSpan d)
    {
        lock (_gate)
        {
            _mono += d;
            _utc += d;
        }
    }

    public void Sleep(TimeSpan duration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Advance(duration);
    }
}

internal sealed class FakeJournal : IJournal
{
    private readonly List<JournalRecord> _records = [];

    public IReadOnlyList<JournalRecord> Records
    {
        get
        {
            lock (_records)
            {
                return _records.ToArray();
            }
        }
    }

    public int Flushes { get; private set; }

    public void Write(JournalRecord record)
    {
        lock (_records)
        {
            _records.Add(record);
        }
    }

    public void Flush() => Flushes++;
}

internal sealed class FakeNotifier : INotifier
{
    public List<Notification> Shown { get; } = [];

    public void Notify(Notification notification)
    {
        lock (Shown)
        {
            Shown.Add(notification);
        }
    }
}

internal sealed class FakeStop : IStopSwitch
{
    private readonly ManualResetEvent _handle = new(false);

    public bool IsStopRequested => Current is not null;

    public StopRequest? Current { get; private set; }

    public WaitHandle Handle => _handle;

    public void RequestStop(StopSource source, string reasonCode)
    {
        Current ??= new StopRequest(source, reasonCode, DateTimeOffset.UtcNow);
        _handle.Set();
    }

    public void Clear()
    {
        Current = null;
        _handle.Reset();
    }
}

internal sealed class FakePause : IPauseControl
{
    public bool IsPaused { get; private set; }

    public void Pause(string sourceCode) => IsPaused = true;

    public void Resume(string sourceCode) => IsPaused = false;
}

internal sealed class FakeHalt : IHaltLatch
{
    public bool IsHalted => Current is not null;

    public HaltReport? Current { get; private set; }

    public int HaltCalls { get; private set; }

    public void Halt(HaltReport report)
    {
        HaltCalls++;
        Current ??= report;
    }

    public void Clear(string byCode) => Current = null;
}

internal sealed class FakeEngine : IEngineStatusSource
{
    public EngineStatus Status { get; set; } = new(EngineState.Idle, null, null, null, null, DateTimeOffset.MinValue);
}

internal sealed class FakeTrust : IFileTrust
{
    public bool BuildTrusted { get; set; } = true;

    public Func<string, byte[]?> Reader { get; set; } = _ => null;

    public List<string> Reads { get; } = [];

    public byte[]? ReadTrusted(string fullPath)
    {
        lock (Reads)
        {
            Reads.Add(fullPath);
        }

        return Reader(fullPath);
    }
}

internal sealed class FakeSwitches : IAutomationSwitches
{
    public bool Crafting { get; set; }

    public bool Homestead { get; set; }

    public bool SlideClickEnabled { get; set; } = true;

    public bool IsOn(AutomationSwitch automationSwitch) => automationSwitch switch
    {
        AutomationSwitch.None => true,
        AutomationSwitch.Crafting => Crafting,
        AutomationSwitch.Homestead => Homestead,
        _ => false,
    };
}

/// <summary>The store's one knowledge profile (4.33: one profile for every character, whoever is logged in).</summary>
internal sealed class FakeKnowledgeStore : IKnowledgeStore
{
    public FakeCharacterKnowledge ProfileFake { get; } = new(new CharacterKey("0000000000000000"));

    /// <summary>When set, the store cannot open the profile (reading it throws).</summary>
    public bool Broken { get; set; }

    public int Opens { get; private set; }

    public KnowledgeLoadReport Load() => new(true, 1, []);

    public ICharacterKnowledge Profile
    {
        get
        {
            Opens++;
            return Broken ? throw new IOException("knowledge.test.broken") : ProfileFake;
        }
    }
}

internal sealed class FakeCharacterKnowledge(CharacterKey character) : ICharacterKnowledge
{
    public CharacterKey Character { get; } = character;

    public int Saves { get; private set; }

    public StockView GetStock() => new(Character, new Dictionary<ItemKey, StockEntry>(), null, 0, null);

    public void ApplyObservations(IReadOnlyList<StockObservation> observations)
    {
    }

    public bool ApplyDerived(DerivedStockDelta delta) => false;

    public void MarkStale(StaleScope scope, ItemKey? item, string reasonCode, DateTimeOffset atUtc)
    {
    }

    public IReadOnlyList<Recipe> GetRecipes() => [];

    public void UpsertRecipe(Recipe recipe)
    {
    }

    public bool RemoveRecipe(string recipeId) => false;

    public IReadOnlyList<IconItem> GetIconItems() => [];

    public void LearnIconItem(IconItem item)
    {
    }

    public ForgetRecipesReport ForgetRecipes(IReadOnlyCollection<string> coverages) => new("knowledge.json.forget-test", 0, 0);

    public IReadOnlyList<KeepStockRule> GetKeepStockRules() => [];

    public void SetKeepStockRules(IReadOnlyList<KeepStockRule> rules)
    {
    }

    public void Save() => Saves++;
}

/// <summary>A scripted handler: returns <see cref="Result"/>, optionally blocking until released.</summary>
internal sealed class FakeHandler : IJobHandler
{
    public JobResult Result { get; set; } = new(JobState.Succeeded, "ok");

    public ManualResetEventSlim? Gate { get; set; }

    public ManualResetEventSlim Entered { get; } = new(false);

    public int Calls { get; private set; }

    public Exception? Throw { get; set; }

    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        Calls++;
        Entered.Set();
        Gate?.Wait(ct);
        if (Throw is not null)
        {
            throw Throw;
        }

        return Result;
    }
}

/// <summary>A small whitelist: Reset (navigation), Craft (commit, crafting, qty 1..999), Scan (read-only).</summary>
internal sealed class FakeCatalog : IJobCatalog
{
    private readonly Dictionary<string, (JobDefinition Def, IJobHandler Handler)> _jobs = new(StringComparer.Ordinal);

    public FakeCatalog(FakeHandler handler)
    {
        Add(new JobDefinition("Reset", "core", "reset", [], AutomationSwitch.None, JobKind.Navigation, 60), handler); // per-job cap at the total limit (2026-10-18)
        Add(new JobDefinition("Craft", "crafting", "craft", [new JobParamSpec("qty", JobParamType.Int, true, 1, 999)], AutomationSwitch.Crafting, JobKind.Commit, 10), handler);
        Add(new JobDefinition("Scan", "inventory", "scan", [], AutomationSwitch.None, JobKind.ReadOnlyScan, 2), handler);
    }

    public IReadOnlyList<JobDefinition> All => _jobs.Values.Select(v => v.Def).ToArray();

    public void Add(JobDefinition def, IJobHandler handler) => _jobs[def.Name] = (def, handler);

    public bool TryGet(string name, out JobDefinition? definition, out IJobHandler? handler)
    {
        if (_jobs.TryGetValue(name, out var v))
        {
            definition = v.Def;
            handler = v.Handler;
            return true;
        }

        definition = null;
        handler = null;
        return false;
    }

    public JobParameters? Validate(JobDefinition definition, IReadOnlyDictionary<string, string> raw, ICharacterKnowledge? knowledge, out string? reasonCode)
    {
        reasonCode = null;
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var key in raw.Keys)
        {
            if (!definition.Params.Any(p => p.Name == key))
            {
                reasonCode = "param.unknown";
                return null;
            }
        }

        foreach (var spec in definition.Params)
        {
            if (!raw.TryGetValue(spec.Name, out var text))
            {
                if (spec.Required)
                {
                    reasonCode = "param.missing:" + spec.Name;
                    return null;
                }

                continue;
            }

            if (spec.Type == JobParamType.ItemKey)
            {
                values[spec.Name] = text; // an item name (the real catalog maps it to a known item key)
                continue;
            }

            if (spec.Type == JobParamType.Enum)
            {
                if (spec.Allowed is not { } allowed || !allowed.Contains(text))
                {
                    reasonCode = "param.enum:" + spec.Name;
                    return null;
                }

                values[spec.Name] = text;
                continue;
            }

            if (!long.TryParse(text, out var n) || n < spec.Min || n > spec.Max)
            {
                reasonCode = "param.range:" + spec.Name;
                return null;
            }

            values[spec.Name] = n;
        }

        return new JobParameters(values);
    }
}

internal sealed class FakeContexts : IJobContextFactory
{
    public List<RunContext> Runs { get; } = [];

    /// <summary>The knowledge each run was given (same order as <see cref="Runs"/>).</summary>
    public List<ICharacterKnowledge?> Knowledge { get; } = [];

    public IJobContext Create(RunContext run, ICharacterKnowledge? knowledge)
    {
        lock (Runs)
        {
            Runs.Add(run);
            Knowledge.Add(knowledge);
        }

        return null!; // handlers in these tests never touch the context
    }
}

/// <summary>A JobHost wired to fakes.</summary>
internal sealed class JobRig : IDisposable
{
    public JobRig()
    {
        Catalog = new FakeCatalog(Handler);
        Host = new JobHost(new JobHostDeps(Catalog, Clock, Stop, Pause, Halt, Engine, Journal, Trust, Knowledge, Switches,
            () => GameConnected, Contexts));
        Host.MarkStarted(null);
    }

    public FakeClock Clock { get; } = new();

    public FakeHandler Handler { get; } = new();

    public FakeCatalog Catalog { get; }

    public FakeStop Stop { get; } = new();

    public FakePause Pause { get; } = new();

    public FakeHalt Halt { get; } = new();

    public FakeEngine Engine { get; } = new();

    public FakeJournal Journal { get; } = new();

    public FakeTrust Trust { get; } = new();

    public FakeKnowledgeStore Knowledge { get; } = new();

    public FakeSwitches Switches { get; } = new();

    public FakeContexts Contexts { get; } = new();

    /// <summary>What the host's GameConnected callback answers (false: no game window held).</summary>
    public bool GameConnected { get; set; }

    public JobHost Host { get; }

    public static string NewId() => Guid.NewGuid().ToString("N");

    public PipeResponse Submit(string name, IReadOnlyDictionary<string, string>? p = null, string? id = null) =>
        Host.Handle(new PipeRequest(PipeOp.Submit, id ?? NewId(), name, p));

    public void Dispose() => Host.Dispose();
}
