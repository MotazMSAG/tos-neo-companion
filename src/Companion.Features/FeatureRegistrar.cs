using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Perception;

namespace Companion.Features;

/// <summary>
/// Staging registrar handed to one <see cref="IFeature.Register"/> call. It only records; the host validates and
/// commits afterwards, so a feature that throws, times out or registers something invalid leaves nothing behind.
/// Closed after the call (or at the timeout): later calls from a still-running worker throw.
/// </summary>
internal sealed class FeatureRegistrar : IFeatureRegistrar
{
    private readonly object _gate = new();
    private readonly List<(JobDefinition Definition, IJobHandler Handler)> _jobs = [];
    private readonly List<ProcedureDefinition> _procedures = [];
    private readonly List<ReaderDefinition> _readers = [];
    private readonly List<string> _errors = [];
    private bool _closed;

    public FeatureRegistrar(string featureId) => FeatureId = featureId;

    public string FeatureId { get; }

    public void AddJob(JobDefinition definition, IJobHandler handler)
    {
        lock (_gate)
        {
            ThrowIfClosed();
            if (definition is null || handler is null)
            {
                _errors.Add("job.null");
                return;
            }

            _jobs.Add((definition, handler));
        }
    }

    public void AddProcedure(ProcedureDefinition procedure)
    {
        lock (_gate)
        {
            ThrowIfClosed();
            if (procedure is null)
            {
                _errors.Add("procedure.null");
                return;
            }

            _procedures.Add(procedure);
        }
    }

    public void AddReader(ReaderDefinition reader)
    {
        lock (_gate)
        {
            ThrowIfClosed();
            if (reader is null)
            {
                _errors.Add("reader.null");
                return;
            }

            _readers.Add(reader);
        }
    }

    /// <summary>Ends the registration window.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
        }
    }

    /// <summary>Snapshot of what was registered (call after <see cref="Close"/>).</summary>
    public (IReadOnlyList<(JobDefinition Definition, IJobHandler Handler)> Jobs, IReadOnlyList<ProcedureDefinition> Procedures, IReadOnlyList<ReaderDefinition> Readers, IReadOnlyList<string> Errors) Snapshot()
    {
        lock (_gate)
        {
            return (_jobs.ToArray(), _procedures.ToArray(), _readers.ToArray(), _errors.ToArray());
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed)
        {
            throw new InvalidOperationException("The registrar is only valid during Register.");
        }
    }
}
