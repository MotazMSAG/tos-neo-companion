using Companion.Contracts.Engine;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;

namespace Companion.Contracts.Navigation;

/// <summary>How much is known about a screen, button or reader.</summary>
public enum SurveyStatus
{
    /// <summary>Observed and measured at full scale.</summary>
    Known,
    /// <summary>Observed, but its template/positions are provisional (not clickable).</summary>
    Provisional,
    /// <summary>Not observed yet: explicit stub, no coordinates.</summary>
    NeedsSurvey,
}

/// <summary>Button safety (GATE-01).</summary>
public enum ButtonSafety
{
    /// <summary>Opens a screen.</summary>
    Navigation,
    /// <summary>HUD toggle.</summary>
    Toggle,
    /// <summary>Commits something (gated by its switch).</summary>
    Commit,
    /// <summary>Never pressed (buy, sell, delete, premium, gacha...).</summary>
    Forbidden,
}

/// <summary>A screen (ui-map.json).</summary>
/// <param name="Id">Screen id (e.g. "hud.collapsed", "bag").</param>
/// <param name="LogUiName">Top UI name in the log when this screen is open (null for HUD states).</param>
/// <param name="AnchorTemplateIds">Templates identifying it.</param>
/// <param name="IsHud">True for HUD states.</param>
/// <param name="Hud">HUD state for HUD screens.</param>
/// <param name="Status">Survey status.</param>
/// <param name="EscCloses">Esc closes it (left by Reset).</param>
/// <param name="Notes">Notes.</param>
public sealed record ScreenDefinition(
    string Id,
    string? LogUiName,
    IReadOnlyList<string> AnchorTemplateIds,
    bool IsHud,
    HudState? Hud,
    SurveyStatus Status,
    bool EscCloses,
    string? Notes);

/// <summary>A button (ui-map.json): either a template click or a whitelisted key. Edges go forward from a screen; going back is Reset.</summary>
/// <param name="Id">Button id.</param>
/// <param name="ScreenId">Screen it is on.</param>
/// <param name="TemplateId">Template (click), or null.</param>
/// <param name="Key">Key, or null.</param>
/// <param name="LeadsTo">Screen it opens, or null.</param>
/// <param name="ExpectedUi">Log UI name it opens (expectation), or null.</param>
/// <param name="Safety">Safety.</param>
/// <param name="Gate">Automation switch for Commit buttons.</param>
/// <param name="Status">Survey status.</param>
/// <param name="Notes">Notes.</param>
public sealed record ButtonDefinition(
    string Id,
    string ScreenId,
    string? TemplateId,
    GameKey? Key,
    string? LeadsTo,
    string? ExpectedUi,
    ButtonSafety Safety,
    AutomationSwitch Gate,
    SurveyStatus Status,
    string? Notes);

/// <summary>A deny zone: a client rect near which clicks need extra stillness and into which no click may land (shop entries, offers, chat panel).</summary>
/// <param name="Id">Zone id.</param>
/// <param name="ScreenId">Screen (HUD states).</param>
/// <param name="ClientRect">Client rect (approximate, guard only).</param>
/// <param name="Label">What it is.</param>
public sealed record DenyZone(string Id, string ScreenId, RectI ClientRect, string Label);

/// <summary>The UI map (core data\ui-map.json merged with feature fragments).</summary>
/// <param name="SchemaVersion">Schema version (1).</param>
/// <param name="Screens">Screens.</param>
/// <param name="Buttons">Buttons.</param>
/// <param name="DenyZones">Deny zones.</param>
public sealed record UiMap(int SchemaVersion, IReadOnlyList<ScreenDefinition> Screens, IReadOnlyList<ButtonDefinition> Buttons, IReadOnlyList<DenyZone> DenyZones);

/// <summary>Merged map access (unit "engine-nav", Companion.Navigation). Thread-safe after sealing.</summary>
public interface IUiMapCatalog
{
    /// <summary>The merged map.</summary>
    UiMap Map { get; }

    /// <summary>Screen by id.</summary>
    ScreenDefinition? GetScreen(string id);

    /// <summary>Button by id.</summary>
    ButtonDefinition? GetButton(string id);

    /// <summary>Buttons that click a template (for safety/gate lookups).</summary>
    IReadOnlyList<ButtonDefinition> ButtonsForTemplate(string templateId);

    /// <summary>Deny zones of a screen.</summary>
    IReadOnlyList<DenyZone> DenyZonesFor(string screenId);
}

/// <summary>Start-up merging of map fragments; sealed before the first job (duplicate ids = the later fragment is rejected and reported).</summary>
public interface IUiMapCatalogBuilder : IUiMapCatalog
{
    /// <summary>Adds a fragment. Throws after <see cref="Seal"/>.</summary>
    void AddFragment(UiMap fragment, string sourceCode);

    /// <summary>Freezes.</summary>
    void Seal();
}

/// <summary>Identification confidence.</summary>
public enum IdentificationConfidence
{
    /// <summary>Log top UI and anchors agree.</summary>
    Confirmed,
    /// <summary>Only the log says so (anchors needsSurvey).</summary>
    LogOnly,
    /// <summary>Only anchors (HUD states: log top must be nil/none).</summary>
    VisionOnly,
    /// <summary>Unidentified: Reset then re-identify.</summary>
    Unknown,
}

/// <summary>Current screen.</summary>
/// <param name="ScreenId">Screen id, or null.</param>
/// <param name="Confidence">Confidence.</param>
/// <param name="LogTop">Advisory top UI.</param>
/// <param name="Hud">HUD classification state.</param>
/// <param name="DetailCode">Short code.</param>
public sealed record ScreenIdentification(string? ScreenId, IdentificationConfidence Confidence, string? LogTop, HudState Hud, string DetailCode);

/// <summary>A path of buttons from one screen to another.</summary>
/// <param name="From">Start screen.</param>
/// <param name="To">Target screen.</param>
/// <param name="Hops">Buttons in order.</param>
public sealed record NavigationPath(string From, string To, IReadOnlyList<ButtonDefinition> Hops);

/// <summary>Navigation result status.</summary>
public enum NavigationStatus
{
    /// <summary>Arrived (identification confirmed).</summary>
    Arrived,
    /// <summary>Already there.</summary>
    AlreadyThere,
    /// <summary>No path in the map.</summary>
    NoPath,
    /// <summary>Path crosses a needsSurvey/provisional element.</summary>
    NeedsSurvey,
    /// <summary>Procedure failed (see result).</summary>
    Failed,
}

/// <summary>Navigation result.</summary>
/// <param name="Status">Status.</param>
/// <param name="ScreenId">Final screen.</param>
/// <param name="Procedure">Procedure result of the path run.</param>
public sealed record NavigationResult(NavigationStatus Status, string? ScreenId, ProcedureResult? Procedure);

/// <summary>Identifies the current screen from the log state + anchors (pure; unit "engine-nav").</summary>
public interface IScreenIdentifier
{
    /// <summary>Identifies.</summary>
    ScreenIdentification Identify(Frame frame, UiStateSnapshot ui);
}

/// <summary>BFS over the map (pure; unit "engine-nav"). Never routes through Forbidden or Commit buttons; NeedsSurvey/Provisional hops make the path non-executable.</summary>
public interface IPathPlanner
{
    /// <summary>Shortest path, or null.</summary>
    NavigationPath? FindPath(string fromScreenId, string toScreenId);

    /// <summary>Builds a procedure: Reset if needed, then one step per hop (pre = hop screen, expect = leadsTo UI/anchors).</summary>
    ProcedureDefinition BuildProcedure(NavigationPath path, bool startWithReset);
}

/// <summary>Navigator (unit "engine-nav"): identify, find a path, run it through the engine with recovery.</summary>
public interface INavigator
{
    /// <summary>Captures and identifies the current screen.</summary>
    ScreenIdentification IdentifyNow(CancellationToken ct);

    /// <summary>Navigates (unidentified screen = Reset with Esc, then re-identify).</summary>
    NavigationResult NavigateTo(string screenId, RunContext context, CancellationToken ct);
}
