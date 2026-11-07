using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.GameIo;
using Companion.Contracts.Host;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Navigation;
using Companion.Contracts.Perception;
using Companion.Contracts.Planning;
using Companion.Contracts.Vision;
using Companion.Feature.Crafting;
using Companion.Feature.Homestead;
using Companion.Feature.Inventory;
using Companion.Feature.Recipes;
using Companion.Feature.Survey;
using Companion.Feature.TeaHouse;
using Companion.Features;

namespace Companion.Features.Tests;

// Offline fakes for every contract the features unit touches. None of them reaches a window, the game or the log.

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    public TimeSpan Monotonic { get; set; } = TimeSpan.FromHours(1);

    public void Sleep(TimeSpan duration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Monotonic += duration;
        UtcNow += duration;
    }
}

internal sealed class FakeJournal : IJournal
{
    private readonly object _gate = new();

    public List<JournalRecord> Records { get; } = [];

    public void Write(JournalRecord record)
    {
        lock (_gate)
        {
            Records.Add(record);
        }
    }

    public void Flush()
    {
    }
}

internal sealed class FakeFileTrust : IFileTrust
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Untrusted { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Reads { get; } = [];

    /// <summary>Real feature data copied next to the test assembly (offline, read-only).</summary>
    public bool ReadFromDisk { get; set; }

    public bool BuildTrusted => true;

    public byte[]? ReadTrusted(string fullPath)
    {
        lock (Reads)
        {
            Reads.Add(fullPath);
        }

        if (Untrusted.Contains(fullPath) || Untrusted.Contains(Path.GetFileName(fullPath)))
        {
            return null;
        }

        if (Files.TryGetValue(fullPath, out var bytes))
        {
            return bytes;
        }

        return ReadFromDisk && File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
    }
}

internal sealed class FakeTemplateBuilder : ITemplateCatalogBuilder
{
    private readonly Dictionary<string, TemplateDefinition> _items = new(StringComparer.Ordinal);
    private bool _sealed;

    public List<string> Sources { get; } = [];

    public IReadOnlyCollection<TemplateDefinition> All => _items.Values;

    public TemplateDefinition? Get(string id) => _items.GetValueOrDefault(id);

    public bool IsClickable(string id) => _items.TryGetValue(id, out var t) && t.Status == TemplateStatus.Verified;

    public void MarkSuspect(string id, string reasonCode)
    {
    }

    public bool IsSuspect(string id) => false;

    public void Add(TemplateDefinition definition, byte[] png, byte[]? maskPng, string sourceCode)
    {
        if (_sealed || !_items.TryAdd(definition.Id, definition))
        {
            throw new InvalidOperationException("rejected");
        }

        Sources.Add(sourceCode);
    }

    public void Seal() => _sealed = true;
}

internal sealed class FakeUiMapBuilder : IUiMapCatalogBuilder
{
    private readonly List<UiMap> _fragments = [];

    public UiMap Map => new(1, _fragments.SelectMany(f => f.Screens).ToList(), _fragments.SelectMany(f => f.Buttons).ToList(), _fragments.SelectMany(f => f.DenyZones).ToList());

    public ScreenDefinition? GetScreen(string id) => Map.Screens.FirstOrDefault(s => s.Id == id);

    public ButtonDefinition? GetButton(string id) => Map.Buttons.FirstOrDefault(b => b.Id == id);

    public IReadOnlyList<ButtonDefinition> ButtonsForTemplate(string templateId) => Map.Buttons.Where(b => b.TemplateId == templateId).ToList();

    public IReadOnlyList<DenyZone> DenyZonesFor(string screenId) => Map.DenyZones.Where(z => z.ScreenId == screenId).ToList();

    public void AddFragment(UiMap fragment, string sourceCode) => _fragments.Add(fragment);

    public void Seal()
    {
    }
}

internal sealed class FakeProcedureBuilder : IProcedureCatalogBuilder
{
    private readonly Dictionary<string, ProcedureDefinition> _items = new(StringComparer.Ordinal);

    public Dictionary<string, string> Sources { get; } = new(StringComparer.Ordinal);

    /// <summary>When set and true for a procedure, Add throws (a validating builder rejecting it).</summary>
    public Func<ProcedureDefinition, bool>? Reject { get; set; }

    public IReadOnlyCollection<ProcedureDefinition> All => _items.Values;

    public ProcedureDefinition? Get(string id) => _items.GetValueOrDefault(id);

    public void Add(ProcedureDefinition procedure, string sourceCode)
    {
        if (Reject?.Invoke(procedure) == true || !_items.TryAdd(procedure.Id, procedure))
        {
            throw new InvalidOperationException("rejected");
        }

        Sources[procedure.Id] = sourceCode;
    }

    public void Seal()
    {
    }

    /// <summary>Replaces or adds (tests only).</summary>
    public void Put(ProcedureDefinition procedure) => _items[procedure.Id] = procedure;
}

internal sealed class FakeReaderBuilder : IReaderCatalogBuilder
{
    private readonly Dictionary<string, ReaderDefinition> _items = new(StringComparer.Ordinal);

    public IReadOnlyCollection<ReaderDefinition> All => _items.Values;

    public ReaderDefinition? Get(string id) => _items.GetValueOrDefault(id);

    public void Add(ReaderDefinition reader, string sourceCode)
    {
        if (!_items.TryAdd(reader.Id, reader))
        {
            throw new InvalidOperationException("rejected");
        }
    }

    public void Seal()
    {
    }
}

/// <summary>Records every procedure the engine would have run. Zero runs = zero inputs.</summary>
internal sealed class FakeRunner : IProcedureRunner
{
    private readonly Func<IProcedureCatalog> _catalog;

    public FakeRunner(Func<IProcedureCatalog> catalog) => _catalog = catalog;

    public List<ProcedureDefinition> Runs { get; } = [];

    public Func<ProcedureDefinition, ProcedureResult>? Script { get; set; }

    public ProcedureResult Run(string procedureId, RunContext context, CancellationToken ct) =>
        Run(_catalog().Get(procedureId) ?? throw new InvalidOperationException("unknown procedure " + procedureId), context, ct);

    public ProcedureResult Run(ProcedureDefinition procedure, RunContext context, CancellationToken ct)
    {
        Runs.Add(procedure);
        return Script?.Invoke(procedure) ?? T.Result(procedure.Id, ProcedureStatus.Succeeded);
    }

    public IEnumerable<string> RunIds => Runs.Select(r => r.Id);
}

internal sealed class FakeNavigator : INavigator
{
    public int IdentifyCalls { get; private set; }

    public List<string> NavigateCalls { get; } = [];

    public ScreenIdentification Identification { get; set; } = new("hud.collapsed", IdentificationConfidence.VisionOnly, null, HudState.Collapsed, "vision.hud");

    public ScreenIdentification IdentifyNow(CancellationToken ct)
    {
        IdentifyCalls++;
        return Identification;
    }

    public NavigationResult NavigateTo(string screenId, RunContext context, CancellationToken ct)
    {
        NavigateCalls.Add(screenId);
        return new NavigationResult(NavigationStatus.Failed, null, null);
    }
}

internal sealed class FakeScreen : IScreenService
{
    public int Calls { get; private set; }

    public CaptureResult Capture(CancellationToken ct)
    {
        Calls++;
        return new CaptureResult(CaptureProblem.NotConnected, null, TimeSpan.Zero);
    }

    public HudClassification ClassifyHudStable(CancellationToken ct)
    {
        Calls++;
        return new HudClassification(HudState.Unknown, 0, 0, false, false, "fake");
    }

    public ScreenReadResult Read(string readerId, CancellationToken ct)
    {
        Calls++;
        return new ScreenReadResult(readerId, ReadStatus.NeedsSurvey, [], null, DateTimeOffset.UnixEpoch);
    }
}

internal sealed class FakeKnowledge : ICharacterKnowledge
{
    public CharacterKey Character { get; } = new("0123456789abcdef");

    public Dictionary<ItemKey, StockEntry> Items { get; } = [];

    public DateTimeOffset? StaleSince { get; set; }

    public List<Recipe> Recipes { get; } = [];

    public List<KeepStockRule> Rules { get; } = [];

    public List<StockObservation> Observations { get; } = [];

    public List<DerivedStockDelta> Derived { get; } = [];

    public List<(StaleScope Scope, ItemKey? Item, string Reason)> StaleMarks { get; } = [];

    public int Saves { get; private set; }

    public StockView GetStock() => new(Character, new Dictionary<ItemKey, StockEntry>(Items), StaleSince, 0, null);

    public void ApplyObservations(IReadOnlyList<StockObservation> observations)
    {
        foreach (var o in observations)
        {
            Observations.Add(o);
            Items[o.Item] = new StockEntry(o.Item, o.Count, o.Range, StockSource.Observed, o.AtUtc, o.AtLog, o.Confidence, o.Coverage, false, o.ItemId);
        }
    }

    public bool ApplyDerived(DerivedStockDelta delta)
    {
        if (!Items.TryGetValue(delta.Item, out var e) || e.Count is not { } count)
        {
            return false;
        }

        if (e.ObservedAtLog is { } at && delta.EventPosition.Offset <= at.Offset)
        {
            return false;
        }

        Items[delta.Item] = e with { Count = count + delta.Delta, Source = StockSource.Derived };
        Derived.Add(delta);
        return true;
    }

    public void MarkStale(StaleScope scope, ItemKey? item, string reasonCode, DateTimeOffset atUtc)
    {
        StaleMarks.Add((scope, item, reasonCode));
        if (scope == StaleScope.Item && item is { } i && Items.TryGetValue(i, out var e))
        {
            Items[i] = e with { Stale = true };
        }
        else if (scope == StaleScope.AllStock)
        {
            StaleSince = atUtc;
        }
    }

    public IReadOnlyList<Recipe> GetRecipes() => Recipes.ToArray();

    public void UpsertRecipe(Recipe recipe)
    {
        // like the real store: a recipe with a product item id replaces any recipe with that id
        Recipes.RemoveAll(r => r.Id == recipe.Id || (recipe.ProductItemId is { } id && r.ProductItemId == id));
        Recipes.Add(recipe);
    }

    public bool RemoveRecipe(string recipeId) => Recipes.RemoveAll(r => r.Id == recipeId) > 0;

    public List<IconItem> IconItems { get; } = [];

    public IReadOnlyList<IconItem> GetIconItems() => IconItems.ToArray();

    public void LearnIconItem(IconItem item)
    {
        // like the real store: remember the icon, move the icon-keyed stock and materials to the item
        IconItems.RemoveAll(i => i.IconKey == item.IconKey);
        IconItems.Add(item);
        var icon = new ItemKey(item.IconKey);
        if (Items.Remove(icon, out var e))
        {
            Items[item.Item] = e with { Item = item.Item, ItemId = item.ItemId };
        }

        for (var i = 0; i < Recipes.Count; i++)
        {
            var r = Recipes[i];
            if (r.Materials.Any(m => m.Item == icon))
            {
                Recipes[i] = r with { Materials = [.. r.Materials.Select(m => m.Item == icon ? m with { Item = item.Item, ItemId = item.ItemId } : m)] };
            }
        }
    }

    public List<IReadOnlyCollection<string>> Forgets { get; } = [];

    public ForgetRecipesReport ForgetRecipes(IReadOnlyCollection<string> coverages)
    {
        Forgets.Add(coverages);
        var recipes = Recipes.Count;
        Recipes.Clear();
        var stock = Items.Values.Where(e => coverages.Contains(e.Coverage)).Select(e => e.Item).ToList();
        foreach (var key in stock)
        {
            Items.Remove(key);
        }

        Saves++;
        return new ForgetRecipesReport("knowledge.json.forget-20261014T120000Z", recipes, stock.Count);
    }

    public IReadOnlyList<KeepStockRule> GetKeepStockRules() => Rules.ToArray();

    public void SetKeepStockRules(IReadOnlyList<KeepStockRule> rules)
    {
        Rules.Clear();
        Rules.AddRange(rules);
    }

    public void Save() => Saves++;
}

internal sealed class FakePlanner : IPlanner
{
    public List<PlanRequest> Requests { get; } = [];

    public Func<PlanRequest, CraftPlan>? Script { get; set; }

    public CraftPlan Plan(PlanRequest request)
    {
        Requests.Add(request);
        return Script?.Invoke(request) ?? new CraftPlan([], [], [], new TimeRange(TimeSpan.Zero, TimeSpan.Zero), true);
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

internal sealed class FakeArtifacts : IArtifactStore
{
    public int Saves { get; private set; }

    public string? SaveImage(ArtifactCategory category, string label, byte[] png)
    {
        Saves++;
        return null;
    }

    public string? SaveLogExcerpt(ArtifactCategory category, string label, IReadOnlyList<string> filteredLines)
    {
        Saves++;
        return null;
    }
}

internal sealed class FakeNotifier : INotifier
{
    public List<Notification> Sent { get; } = [];

    public void Notify(Notification notification) => Sent.Add(notification);
}

internal sealed class FakeJobContext : IJobContext
{
    public FakeJobContext(IProcedureCatalog catalog)
    {
        ProcedureCatalog = catalog;
        Runner = new FakeRunner(() => ProcedureCatalog);
    }

    public RunContext Run { get; set; } = new("0123456789abcdef0123456789abcdef", "Test", JobKind.Navigation, AutomationSwitch.None);

    public FakeRunner Runner { get; }

    public IProcedureRunner Procedures => Runner;

    public IProcedureCatalog ProcedureCatalog { get; set; }

    public FakeNavigator Nav { get; } = new();

    public INavigator Navigator => Nav;

    public FakeScreen ScreenFake { get; } = new();

    /// <summary>A scripted screen (e.g. a simulated list) instead of <see cref="ScreenFake"/>.</summary>
    public IScreenService? ScreenOverride { get; set; }

    public IScreenService Screen => ScreenOverride ?? ScreenFake;

    public UiStateSnapshot Ui { get; set; } = T.Snapshot(1000);

    public Func<UiStateSnapshot>? UiProvider { get; set; }

    public UiStateSnapshot UiState => UiProvider?.Invoke() ?? Ui;

    public FakeKnowledge? KnowledgeFake { get; set; } = new();

    public ICharacterKnowledge? Knowledge => KnowledgeFake;

    public FakePlanner PlannerFake { get; } = new();

    public IPlanner Planner => PlannerFake;

    public FakeSwitches SwitchesFake { get; } = new();

    public IAutomationSwitches Switches => SwitchesFake;

    public FakeArtifacts ArtifactsFake { get; } = new();

    public IArtifactStore Artifacts => ArtifactsFake;

    public FakeJournal JournalFake { get; } = new();

    public IJournal Journal => JournalFake;

    public FakeNotifier NotifierFake { get; } = new();

    public INotifier Notifier => NotifierFake;

    public FakeClock ClockFake { get; } = new();

    public IClock Clock => ClockFake;

    public CompanionConfig Config { get; set; } = CompanionConfig.Default;
}

/// <summary>A synthetic feature for loader tests.</summary>
internal sealed class TestFeature : IFeature
{
    private readonly Action<IFeatureRegistrar>? _register;
    private readonly FeatureManifest? _manifest;
    private readonly bool _manifestThrows;

    public TestFeature(
        string id,
        string version = "1.0.0",
        int api = ContractsInfo.ApiVersion,
        FeatureDependency[]? requires = null,
        FeatureDataFiles? data = null,
        AutomationSwitch automationSwitch = AutomationSwitch.None,
        string? validatedBuild = null,
        Action<IFeatureRegistrar>? register = null,
        bool manifestThrows = false)
    {
        _manifest = new FeatureManifest(id, version, api, requires ?? [], data ?? new FeatureDataFiles(), automationSwitch, validatedBuild, "test feature");
        _register = register;
        _manifestThrows = manifestThrows;
    }

    public int RegisterCalls { get; private set; }

    public FeatureManifest Manifest => _manifestThrows ? throw new InvalidOperationException("boom") : _manifest!;

    public void Register(IFeatureRegistrar registrar)
    {
        RegisterCalls++;
        if (_register is not null)
        {
            _register(registrar);
            return;
        }

        registrar.AddJob(new JobDefinition(T.JobName(_manifest!.Id), _manifest.Id, "test job", [], AutomationSwitch.None, JobKind.Navigation), new OkHandler());
    }
}

internal sealed class OkHandler : IJobHandler
{
    public int Calls { get; private set; }

    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        Calls++;
        return new JobResult(JobState.Succeeded, "ok");
    }
}

internal sealed class ThrowingHandler : IJobHandler
{
    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct) =>
        throw new InvalidOperationException("handler failed with some free text: player Bob");
}

/// <summary>Test environment: builders, trust, journal and a host.</summary>
internal sealed class TestEnv
{
    public FakeClock Clock { get; } = new();

    public FakeJournal Journal { get; } = new();

    public FakeFileTrust Trust { get; } = new();

    public FakeTemplateBuilder Templates { get; } = new();

    public FakeUiMapBuilder UiMap { get; } = new();

    public FakeProcedureBuilder Procedures { get; } = new();

    public FakeReaderBuilder Readers { get; } = new();

    public CompanionConfig Config { get; set; } = CompanionConfig.Default;

    public string InstallDir { get; set; } = Path.Combine(Path.GetTempPath(), "tos-companion-features-tests-unused");

    public CompanionPaths Paths => new(
        InstallDir,
        Path.Combine(InstallDir, "data"),
        Path.Combine(InstallDir, "templates"),
        Path.Combine(InstallDir, "features"),
        Path.Combine(InstallDir, "runtime-unused"),
        @"D:\Games\TOS",
        @"D:\Games\TOS\Log\log.txt",
        @"D:\Games\TOS\TOS.exe");

    public FeatureLoadContext Context => new(Config, Paths, Templates, UiMap, Procedures, Readers, Trust, Journal);

    /// <summary>The features' real data files, as copied to the test output (features\&lt;id&gt;\procedures.json).</summary>
    public static TestEnv WithRealData()
    {
        var env = new TestEnv { InstallDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar) };
        env.Trust.ReadFromDisk = true;
        return env;
    }

    public string FeatureFile(string featureId, string file) => Path.Combine(Paths.FeaturesDataDir, featureId, file);
}

/// <summary>Shared test helpers.</summary>
internal static class T
{
    public static readonly LogFileIdentity LogFile = new(DateTime.UnixEpoch, 7, "head");

    public static IReadOnlyList<IFeature> RealFeatures() =>
        [new InventoryFeature(), new RecipesFeature(), new CraftingFeature(), new SurveyFeature(), new HomesteadFeature(), new TeaHouseFeature(new TeaHouseDesk())];

    public static readonly string[] ExpectedJobs =
    [
        "Reset", "OpenBagKey", "OpenBagClick", "ExpandMenu", "CollapseMenu", "OpenAbility", "Calibrate",
        "ScanBag", "ScanStock", "ScanRecipes", "Craft", "KeepStock", "CraftLoop", "ScanFavorites", "ScanRecipeList", "Gather", "GatherAll", "KeepStockLoop", "LearnRecipe", "ForgetRecipes", "SurveyScreen", "HomesteadStatus", "HomesteadCollect", "TeaHousePlay",
    ];

    public static LogPosition Pos(long offset) => new(LogFile, offset);

    public static string JobName(string featureId) =>
        "Job" + new string(featureId.Where(char.IsLetterOrDigit).ToArray());

    public static UiStateSnapshot Snapshot(long asOf, CraftSessionInfo? craft = null, BusyKind busy = BusyKind.None) => new(
        Pos(asOf),
        true,
        busy,
        [],
        null,
        FocusState.Lost,
        null,
        0,
        new CharacterKey("0123456789abcdef"),
        new SizeI(1920, 1009),
        null,
        null,
        null,
        null,
        null,
        null,
        craft ?? CraftSessionInfo.None,
        []);

    public static ProcedureResult Result(string id, ProcedureStatus status, IReadOnlyDictionary<string, ScreenReadResult>? reads = null, int inputs = 0, HaltReport? halt = null, IReadOnlyList<StepAttemptRecord>? attempts = null) =>
        new(id, status, status == ProcedureStatus.Succeeded ? StepOutcome.Ok : null, halt, inputs, 0, 0, attempts ?? [], reads ?? new Dictionary<string, ScreenReadResult>(), [], []);

    public static ReadValue Text(string region, string text) => new(region, ReadStatus.Ok, null, null, text, null, 0.9, "ok");

    public static ReadValue Int(string region, long value) => new(region, ReadStatus.Ok, value, null, null, null, 0.9, "ok");

    public static ReadValue Pair(string region, long first, long second) => new(region, ReadStatus.Ok, first, second, null, null, 0.9, "ok");

    public static ReadValue Unknown(string region) => new(region, ReadStatus.Unknown, null, null, null, null, 0, "ocr-digits-disagree");

    public static ScreenReadResult Read(string reader, long logAt, params ReadValue[] values) =>
        new(reader, values.Any(v => v.Status != ReadStatus.Ok) ? ReadStatus.Unknown : ReadStatus.Ok, values, Pos(logAt), new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Loads the real features (real data files) into a fresh host; returns host and env.</summary>
    public static (FeatureHost Host, TestEnv Env, IReadOnlyList<FeatureLoadReport> Reports) LoadReal(Action<TestEnv>? configure = null)
    {
        var env = TestEnv.WithRealData();
        configure?.Invoke(env);
        var host = new FeatureHost(env.Clock);
        var reports = host.LoadAll(RealFeatures(), env.Context);
        return (host, env, reports);
    }

    public static IJobHandler Handler(FeatureHost host, string job)
    {
        Assert.IsTrue(host.Jobs.TryGet(job, out _, out var handler), job);
        return handler!;
    }

    public static JobDefinition Definition(FeatureHost host, string job)
    {
        Assert.IsTrue(host.Jobs.TryGet(job, out var def, out _), job);
        return def!;
    }

    /// <summary>
    /// What a procedure looks like once surveyed: needsSurvey stubs removed, and a stubbed commit step replaced by a
    /// click on a (future) verified template.
    /// </summary>
    public static ProcedureDefinition Surveyed(ProcedureDefinition p) => p with
    {
        Steps = p.Steps
            .Where(s => s.Action is not NeedsSurveyAction || s.Flags.HasFlag(StepFlags.Commit))
            .Select(s => s.Action is NeedsSurveyAction ? s with { Action = new ClickAction("craft.make") } : s)
            .ToArray(),
    };

    /// <summary>A catalog with the surveyed versions of every procedure in <paramref name="source"/>.</summary>
    public static FakeProcedureBuilder SurveyedCatalog(IProcedureCatalog source)
    {
        var catalog = new FakeProcedureBuilder();
        foreach (var p in source.All)
        {
            catalog.Put(Surveyed(p));
        }

        return catalog;
    }
}
