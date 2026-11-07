using Companion.Contracts.Features;
using Companion.Features.Kit;

namespace Companion.Features;

/// <summary>
/// Wraps a feature's handler (FR-02): an exception fails the job as InternalError (counted by the host; 3 fault the
/// feature), cancellation ends it Cancelled, and the result is sanitised to codes and scalars before it leaves the
/// feature (PRIV-14: no free text reaches the journal or the pipe).
/// </summary>
internal sealed class GuardedJobHandler : IJobHandler
{
    private readonly string _featureId;
    private readonly IJobHandler _inner;
    private readonly Action<string, Exception> _onThrow;

    public GuardedJobHandler(string featureId, IJobHandler inner, Action<string, Exception> onThrow)
    {
        _featureId = featureId;
        _inner = inner;
        _onThrow = onThrow;
    }

    public IJobHandler Inner => _inner;

    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        JobResult? result;
        try
        {
            result = _inner.Execute(context, parameters, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new JobResult(JobState.Cancelled, "cancelled");
        }
        catch (Exception ex)
        {
            _onThrow(_featureId, ex);
            return new JobResult(
                JobState.Failed,
                "InternalError",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["feature"] = _featureId, ["exception"] = Codes.Safe(ex.GetType().Name) });
        }

        if (result is null)
        {
            return new JobResult(JobState.Failed, "InternalError", new Dictionary<string, object?>(StringComparer.Ordinal) { ["feature"] = _featureId, ["reason"] = "result.null" });
        }

        return Sanitise(result);
    }

    /// <summary>Codes and scalars only: other strings and non-scalar values are replaced.</summary>
    internal static JobResult Sanitise(JobResult r)
    {
        var code = Codes.IsCode(r.Code) && r.Code.Length > 0 ? r.Code : "code.invalid";
        if (r.Data is null)
        {
            return r with { Code = code };
        }

        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in r.Data.Take(32))
        {
            if (!Codes.IsCode(k) || k.Length == 0)
            {
                continue;
            }

            data[k] = v switch
            {
                null => null,
                bool or byte or short or int or long or double or float or decimal => v,
                string s when Codes.IsCode(s) => s,
                Enum e => e.ToString(),
                _ => "redacted",
            };
        }

        return r with { Code = code, Data = data };
    }
}
