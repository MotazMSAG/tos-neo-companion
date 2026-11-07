using Companion.Contracts;
using Companion.Contracts.Features;
using Companion.Contracts.GameIo;
using Companion.Contracts.Host;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Perception;
using Companion.Contracts.Planning;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.Engine;
using Companion.Feature.Crafting;
using Companion.Feature.Homestead;
using Companion.Feature.Inventory;
using Companion.Feature.Recipes;
using Companion.Feature.Survey;
using Companion.Feature.TeaHouse;
using Companion.Features;
using Companion.GameIO;
using Companion.Knowledge;
using Companion.Knowledge.Planning;
using Companion.Logs;
using Companion.Navigation;
using Companion.Perception;
using Companion.Vision;

namespace Companion.App.Hosting;

/// <summary>
/// Every object of the host, constructed once (SPEC 4 step 6). Constructors only store their dependencies: building
/// this graph touches no window, file, registry, thread or timer.
/// </summary>
internal sealed class HostServices
{
    public required CompanionPaths Paths { get; init; }

    /// <summary>What the running jobs report they are doing (the status card).</summary>
    public Jobs.JobActivityBoard Activity { get; } = new();

    /// <summary>The Kitty Tea House helper's presses for the TeaHousePlay job (auto-play, 2026-10-30).</summary>
    public TeaHouseDesk TeaHouse { get; } = new();

    public required IClock Clock { get; init; }

    public required IStopSwitch Stop { get; init; }

    public required Journal Journal { get; init; }

    public required NotificationGate NotificationGate { get; init; }

    public required HostNotifier Notifier { get; init; }

    public required ConfigStore Config { get; init; }

    public required HaltLatch Halt { get; init; }

    public required IFileTrust FileTrust { get; init; }

    public required CharacterKeyHasher Hasher { get; init; }

    public required KnowledgeStore Knowledge { get; init; }

    public required UiClassifier Classifier { get; init; }

    public required LogPrivacyFilter PrivacyFilter { get; init; }

    public required LogTracker Log { get; init; }

    public required TemplateCatalog Templates { get; init; }

    public required IVision Vision { get; init; }

    public required IOcr Ocr { get; init; }

    public required IDigitReader Digits { get; init; }

    public required IScreenReader ScreenReader { get; init; }

    public required ReaderCatalog Readers { get; init; }

    public required UiMapCatalog UiMap { get; init; }

    public required ProcedureCatalog Procedures { get; init; }

    public required IGameIo GameIo { get; init; }

    public required IPlanner Planner { get; init; }

    public required ScreenIdentifier Identifier { get; init; }

    public required PathPlanner PathPlanner { get; init; }

    public required FeatureHost Features { get; init; }

    public required PauseControl Pause { get; init; }

    public required ArtifactStore Artifacts { get; init; }

    public required ScreenService Screen { get; init; }

    public required ProcedureRunner Runner { get; init; }

    public required Navigator Navigator { get; init; }
}

/// <summary>The composition root (SPEC 4 step 6, 5). The only place that names implementations.</summary>
internal static class Composition
{
    /// <summary>De-duplication window for non-HALT notifications.</summary>
    public static readonly TimeSpan NotificationWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The features compiled into this build (no runtime assembly loading, SEC-1). Core is added by the feature host.
    /// <paramref name="teaHouse"/> is the desk the Tea House helper shares with its job (<see cref="HostServices.TeaHouse"/>).
    /// </summary>
    public static IReadOnlyList<IFeature> CompiledFeatures(TeaHouseDesk? teaHouse = null) =>
        [new InventoryFeature(), new RecipesFeature(), new CraftingFeature(), new SurveyFeature(), new HomesteadFeature(), new TeaHouseFeature(teaHouse ?? new TeaHouseDesk())];

    /// <summary>
    /// Constructs the whole graph. <paramref name="fileTrust"/> replaces the real trust checker (tests);
    /// <paramref name="currentGameBuild"/> is the game build fingerprint code from file metadata (null if unknown).
    /// </summary>
    public static HostServices Create(CompanionPaths paths, IClock clock, string? currentGameBuild, IFileTrust? fileTrust = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);

        var stop = new StopSwitch(clock);
        var journal = new Journal(paths, clock);
        var gate = new NotificationGate(clock, NotificationWindow);
        var notifier = new HostNotifier(gate, journal, clock);
        var config = new ConfigStore(paths, journal, clock);
        var halt = new HaltLatch(paths, notifier, journal, clock);
        var trust = fileTrust ?? new FileTrust(paths, clock);
        var hasher = new CharacterKeyHasher(paths);
        var knowledge = new KnowledgeStore(paths, clock);

        // The SAME classifier instance goes to the log tracker and the engine (learned 'hide top ui' names are shared).
        var classifier = new UiClassifier();
        var privacy = new LogPrivacyFilter();
        var log = new LogTracker(paths, clock, hasher, classifier, privacy);

        // VisionEngine requires the concrete TemplateCatalog of the same unit.
        var templates = new TemplateCatalog();
        var vision = new VisionEngine(templates);
        var ocr = new WinRtOcr();
        var digits = new DigitTemplateReader();
        var screenReader = new ScreenReader(vision, ocr, digits);
        var readers = new ReaderCatalog();
        var uiMap = new UiMapCatalog(templates, journal, clock);
        var procedures = new ProcedureCatalog(templates, readers, uiMap, journal, clock);

        var gameIo = new GameIo(
            clock, stop, log, classifier, vision, templates, journal, paths, () => config.Current.Input.ForegroundSlide, () => config.Current.Input.ForegroundDrag);
        var planner = new Planner();
        var identifier = new ScreenIdentifier(uiMap, vision, templates);
        var pathPlanner = new PathPlanner(uiMap, templates);
        var features = new FeatureHost(clock, currentGameBuild);
        var pause = new PauseControl(journal, clock);
        var artifacts = new ArtifactStore(paths, clock, privacy);
        var screen = new ScreenService(gameIo, vision, screenReader, readers, log, clock);
        var runner = new ProcedureRunner(
            gameIo, log, classifier, vision, templates, readers, screen, procedures, uiMap, identifier, pathPlanner,
            journal, artifacts, halt, notifier, config, config, trust, stop, pause, clock, paths);
        var navigator = new Navigator(screen, identifier, pathPlanner, runner, procedures, uiMap, templates, log);

        return new HostServices
        {
            Paths = paths,
            Clock = clock,
            Stop = stop,
            Journal = journal,
            NotificationGate = gate,
            Notifier = notifier,
            Config = config,
            Halt = halt,
            FileTrust = trust,
            Hasher = hasher,
            Knowledge = knowledge,
            Classifier = classifier,
            PrivacyFilter = privacy,
            Log = log,
            Templates = templates,
            Vision = vision,
            Ocr = ocr,
            Digits = digits,
            ScreenReader = screenReader,
            Readers = readers,
            UiMap = uiMap,
            Procedures = procedures,
            GameIo = gameIo,
            Planner = planner,
            Identifier = identifier,
            PathPlanner = pathPlanner,
            Features = features,
            Pause = pause,
            Artifacts = artifacts,
            Screen = screen,
            Runner = runner,
            Navigator = navigator,
        };
    }

    /// <summary>
    /// Game build fingerprint code from file METADATA only (size + last write time of TOS.exe and GameAssembly.dll, the
    /// same rule as GameIO; contents are never read, FR-04). Null if the game exe is not there.
    /// </summary>
    public static string? GameBuildCode(CompanionPaths paths)
    {
        try
        {
            var exe = new FileInfo(paths.GameExePath);
            if (!exe.Exists)
            {
                return null;
            }

            var asm = new FileInfo(Path.Combine(paths.GameDir, "GameAssembly.dll"));
            return new GameBuildFingerprint(exe.Length, exe.LastWriteTimeUtc, asm.Exists ? asm.Length : null, asm.Exists ? asm.LastWriteTimeUtc : null).Code;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
