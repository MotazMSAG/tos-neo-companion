using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;

namespace Companion.Feature.Survey;

/// <summary>
/// Feature "survey": SurveyScreen {fullFrame: Bool} (JobKind Survey). Passive: it sends NO input of any kind, so it
/// only ever captures screens the user opened (TOS-01). It identifies the current screen, persists template crops (or,
/// with fullFrame=true, the full client frame with the chat panel blacked out) through a capture-only procedure, and
/// returns a UI-state summary as codes.
/// </summary>
public sealed class SurveyFeature : IFeature
{
    /// <summary>Feature id.</summary>
    internal const string Id = "survey";

    /// <summary>Capture-only procedure: template-sized crops of the current screen.</summary>
    internal const string CropsProcedure = "survey.captureCrops";

    /// <summary>Capture-only procedure: full frame, chat panel masked (only with fullFrame=true).</summary>
    internal const string FullFrameProcedure = "survey.captureFull";

    /// <summary>Creates the feature (no side effects).</summary>
    public SurveyFeature()
    {
    }

    /// <inheritdoc />
    public FeatureManifest Manifest { get; } = new(
        Id,
        "1.0.0",
        ContractsInfo.ApiVersion,
        [],
        new FeatureDataFiles(Procedures: DataFileNames.Procedures),
        AutomationSwitch.None,
        null,
        "Passive survey of the screen the user opened: crops or masked frame, UI-state summary (no input)");

    /// <inheritdoc />
    public void Register(IFeatureRegistrar registrar) =>
        registrar.AddJob(
            new JobDefinition(
                "SurveyScreen",
                Id,
                "Identify the current screen and save template crops (or the masked full frame); sends no input",
                [new JobParamSpec("fullFrame", JobParamType.Bool, false, MaxLength: 5)],
                AutomationSwitch.None,
                JobKind.Survey),
            new SurveyScreenHandler());
}
