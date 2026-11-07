using Companion.Contracts.Logs;
using Companion.Contracts.Navigation;
using Companion.Contracts.Vision;

namespace Companion.Navigation;

/// <summary>
/// Identifies the current screen from the log UI model and the captured frame (SPEC 8.2). Pure: no capture, no input.
/// <list type="bullet">
/// <item>Advisory top equals a screen's log UI name and its (verified) anchors are found in the frame: Confirmed.</item>
/// <item>Same, but the screen's anchors are not surveyed (none, provisional, or the screen is NeedsSurvey): LogOnly.</item>
/// <item>Nothing on top and the HUD is classified Collapsed/Expanded with its signature: VisionOnly (hud.*).</item>
/// <item>Anything else (busy, unknown top UI, anchors not found, HUD unknown/covered/absent): Unknown.</item>
/// </list>
/// Thread-safe.
/// </summary>
public sealed class ScreenIdentifier : IScreenIdentifier
{
    private readonly IUiMapCatalog _map;
    private readonly IVision _vision;
    private readonly ITemplateCatalog _templates;

    /// <summary>Creates the identifier (no side effects).</summary>
    public ScreenIdentifier(IUiMapCatalog map, IVision vision, ITemplateCatalog templates)
    {
        _map = map;
        _vision = vision;
        _templates = templates;
    }

    /// <inheritdoc />
    public ScreenIdentification Identify(Frame frame, UiStateSnapshot ui)
    {
        var top = ui.AdvisoryTop;
        HudClassification hc;
        try
        {
            hc = _vision.ClassifyHud(frame);
        }
        catch
        {
            hc = new HudClassification(HudState.Unknown, 0, 0, false, false, "hud.error");
        }
        var hud = hc.State;
        if (!ui.GameAvailable || ui.Busy != BusyKind.None)
        {
            return new ScreenIdentification(null, IdentificationConfidence.Unknown, top, hud, "busy");
        }
        var screens = _map.Map.Screens;
        if (top is not null)
        {
            var screen = screens.FirstOrDefault(s => !s.IsHud && string.Equals(s.LogUiName, top, StringComparison.Ordinal));
            if (screen is null)
            {
                return new ScreenIdentification(null, IdentificationConfidence.Unknown, top, hud, "top.notInMap");
            }
            var verified = screen.AnchorTemplateIds
                .Where(a => _templates.Get(a) is { Status: TemplateStatus.Verified, SourceScale: 1 })
                .ToList();
            if (screen.Status == SurveyStatus.NeedsSurvey || verified.Count == 0)
            {
                return new ScreenIdentification(screen.Id, IdentificationConfidence.LogOnly, top, hud, "logOnly");
            }
            var allFound = verified.All(a =>
            {
                try
                {
                    return _vision.Locate(frame, a).Found;
                }
                catch
                {
                    return false;
                }
            });
            return allFound
                ? new ScreenIdentification(screen.Id, IdentificationConfidence.Confirmed, top, hud, "confirmed")
                : new ScreenIdentification(null, IdentificationConfidence.Unknown, top, hud, "anchors.notFound");
        }
        if (hc.SignatureOk && hud is HudState.Collapsed or HudState.Expanded)
        {
            var hudScreen = screens.FirstOrDefault(s => s.IsHud && s.Hud == hud);
            if (hudScreen is not null)
            {
                return new ScreenIdentification(hudScreen.Id, IdentificationConfidence.VisionOnly, null, hud, "hud." + hud);
            }
        }
        return new ScreenIdentification(null, IdentificationConfidence.Unknown, null, hud, "hud." + hud);
    }
}
