using Companion.Contracts.Features;

namespace Companion.Features.Kit;

/// <summary>
/// A job that runs one catalogued procedure through the engine (all gates, limits and recovery live there).
/// The procedure is checked first: missing = Failed, reachable needsSurvey stub = NeedsSurvey, click steps while
/// slide clicks are off = Refused; in all three cases nothing runs.
/// </summary>
internal sealed class ProcedureJobHandler : IJobHandler
{
    private readonly string _procedureId;

    public ProcedureJobHandler(string procedureId) => _procedureId = procedureId;

    public string ProcedureId => _procedureId;

    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        var refusal = JobKit.CheckProcedures(context, [_procedureId]);
        if (refusal is not null)
        {
            return refusal;
        }

        ct.ThrowIfCancellationRequested();
        return JobKit.FromProcedure(context.Procedures.Run(_procedureId, context.Run, ct));
    }
}
