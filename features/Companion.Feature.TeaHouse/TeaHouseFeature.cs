using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;

namespace Companion.Feature.TeaHouse;

/// <summary>
/// Feature "teahouse" (2026-10-30): TeaHousePlay, the Kitty Tea House helper's hands. The helper (tray app) decides every
/// move and posts the presses on the <see cref="TeaHouseDesk"/>; the job presses each square through the engine (a slide
/// click anchored on a static picture of the page, one click step per square in data\procedures.json, with a second
/// anchor per square as the fallback and never an Esc reset, which would close the minigame), confirms the press from the
/// game log (on_grid_click) and answers. The end screen's Restart button is a needsSurvey stub until a capture of that
/// screen is cut.
/// </summary>
public sealed class TeaHouseFeature : IFeature
{
    /// <summary>Feature id.</summary>
    internal const string Id = "teahouse";

    /// <summary>The board presses (one click step per square, "sq.00" the tray .. "sq.35").</summary>
    internal const string BoardProcedure = "teahouse.board";

    /// <summary>The same presses anchored on each square's second-nearest picture (used when the nearest one is hidden).</summary>
    internal const string AltBoardProcedure = "teahouse.board.alt";

    /// <summary>The end screen's Restart press.</summary>
    internal const string RestartProcedure = "teahouse.restart";

    /// <summary>The auto-play job's name.</summary>
    public const string PlayJob = "TeaHousePlay";

    private readonly TeaHouseDesk _desk;

    /// <summary>Creates the feature (no side effects); <paramref name="desk"/> is shared with the helper.</summary>
    public TeaHouseFeature(TeaHouseDesk desk)
    {
        _desk = desk ?? throw new ArgumentNullException(nameof(desk));
    }

    /// <inheritdoc />
    public FeatureManifest Manifest { get; } = new(
        Id,
        "1.0.0",
        ContractsInfo.ApiVersion,
        [],
        new FeatureDataFiles(UiMap: DataFileNames.UiMap, Templates: DataFileNames.Templates, Procedures: DataFileNames.Procedures),
        AutomationSwitch.None,
        null,
        "Kitty Tea House auto-play: presses the helper's moves on the board and confirms them from the game log");

    /// <inheritdoc />
    public void Register(IFeatureRegistrar registrar)
    {
        registrar.AddJob(
            new JobDefinition(PlayJob, Id, "Press the Kitty Tea House helper's moves while its auto-play is on", [], AutomationSwitch.None, JobKind.Navigation, MaxPerHour: 30),
            new TeaHousePlayHandler(_desk));
    }
}
