using System.Text.RegularExpressions;

namespace Companion.App.Jobs;

/// <summary>
/// What each running job says it is doing (<see cref="Contracts.Features.IJobContext.ReportActivity"/>), for the status
/// card (2026-10-23, the user: "gather everything doesn't do anything clear"). Two slots per job: "job" (its own progress,
/// e.g. Gather everything's trip and what is left) and "step" (the current wait, e.g. a gathering trip running). Codes and
/// plain values only; the window chooses the words. Thread-safe: the engine thread writes, the UI thread reads.
/// </summary>
internal sealed class JobActivityBoard
{
    /// <summary>The slots a job may report.</summary>
    internal static readonly IReadOnlySet<string> Slots = new HashSet<string>(StringComparer.Ordinal) { "job", "step" };

    // the feature code set (Kit Codes): what may be shown is a code, never free text
    private static readonly Regex CodePattern = new(@"^[A-Za-z0-9_.:,()\[\]/+\- ]{0,120}$", RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, (string Code, IReadOnlyDictionary<string, object?>? Data, DateTimeOffset AtUtc)>> _jobs = new(StringComparer.Ordinal);

    /// <summary>Sets (or with a null code clears) one slot of a job. Unknown slots and non-code values are ignored.</summary>
    public void Set(string jobId, string slot, string? code, IReadOnlyDictionary<string, object?>? data, DateTimeOffset atUtc)
    {
        if (!Slots.Contains(slot))
        {
            return;
        }

        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var slots))
            {
                slots = new(StringComparer.Ordinal);
                _jobs[jobId] = slots;
            }

            if (code is null || !CodePattern.IsMatch(code))
            {
                slots.Remove(slot);
                return;
            }

            // values are codes, numbers or times; any other string is dropped
            var safe = data?.ToDictionary(kv => kv.Key, kv => kv.Value is string s && !CodePattern.IsMatch(s) ? null : kv.Value, StringComparer.Ordinal);
            slots[slot] = (code, safe, atUtc);
        }
    }

    /// <summary>
    /// A job's activity as flat values ("activity.&lt;slot&gt;" = code, "activity.&lt;slot&gt;.&lt;key&gt;" = value,
    /// "activity.&lt;slot&gt;.atUtc"), or null when it reported nothing.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Snapshot(string jobId)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var slots) || slots.Count == 0)
            {
                return null;
            }

            var flat = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (slot, (code, data, at)) in slots)
            {
                flat["activity." + slot] = code;
                flat["activity." + slot + ".atUtc"] = at;
                foreach (var (k, v) in data ?? new Dictionary<string, object?>())
                {
                    flat["activity." + slot + "." + k] = v;
                }
            }

            return flat;
        }
    }

    /// <summary>Forgets a job (it ended).</summary>
    public void Clear(string jobId)
    {
        lock (_gate)
        {
            _jobs.Remove(jobId);
        }
    }
}
