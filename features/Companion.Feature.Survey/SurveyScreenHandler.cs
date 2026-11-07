using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Navigation;
using Companion.Features.Kit;

namespace Companion.Feature.Survey;

/// <summary>
/// SurveyScreen. Uses only passive calls: the navigator's identification (a capture) and a procedure that is verified
/// to be capture-only BEFORE it runs (no key, click, toggle, reset, navigate, read, call or await step). Never calls
/// NavigateTo. The summary contains codes only (UI names, states, counts).
/// </summary>
internal sealed class SurveyScreenHandler : IJobHandler
{
    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        var fullFrame = parameters.GetBool("fullFrame");
        var procedureId = fullFrame ? SurveyFeature.FullFrameProcedure : SurveyFeature.CropsProcedure;
        var procedure = context.ProcedureCatalog.Get(procedureId);
        if (procedure is null)
        {
            return JobKit.Failed("procedure.missing:" + procedureId);
        }

        // Passive guarantee: refuse a procedure that could send input, whatever its data says.
        if (!JobKit.IsPassiveCaptureOnly(procedure))
        {
            return JobKit.Refused("survey.notPassive:" + procedureId);
        }

        var expectedScope = fullFrame ? CaptureScope.FullFrameMasked : CaptureScope.TemplateCrops;
        if (procedure.Steps.Any(s => s.Action is CaptureAction c && c.Scope != expectedScope))
        {
            return JobKit.Refused("survey.scopeMismatch:" + procedureId);
        }

        ct.ThrowIfCancellationRequested();
        var identification = context.Navigator.IdentifyNow(ct);
        var ui = context.UiState;
        var run = context.Procedures.Run(procedure, context.Run, ct);

        var data = JobKit.ProcedureData(run);
        data["fullFrame"] = fullFrame;
        data["screen"] = identification.ScreenId is null ? "unknown" : Codes.Safe(identification.ScreenId);
        data["confidence"] = identification.Confidence.ToString();
        data["identify"] = Codes.Safe(identification.DetailCode);
        data["hud"] = identification.Hud.ToString();
        data["logTop"] = Codes.Safe(ui.AdvisoryTop ?? "nil");
        data["stackDepth"] = (long)ui.AdvisoryStack.Count;
        data["busy"] = ui.Busy.ToString();
        data["gameAvailable"] = ui.GameAvailable;
        data["focus"] = ui.Focus.ToString();
        data["autoCollect"] = ui.AutoCollectStatus is { } ac ? ac : null;
        data["craftInProgress"] = ui.Craft.InProgress;
        data["shownThisSession"] = (long)ui.ShownThisSession.Count;

        // The filtered log excerpt (SPEC 11.4) is not reachable through IJobContext; reported as a code.
        data["logExcerpt"] = "unavailable:contract";

        if (run.Status != ProcedureStatus.Succeeded)
        {
            var failed = JobKit.FromProcedure(run);
            return failed with { Data = data };
        }

        var known = identification.Confidence != IdentificationConfidence.Unknown;
        return JobKit.Result(JobState.Succeeded, known ? "survey.saved" : "survey.saved.unknownScreen", data);
    }
}
