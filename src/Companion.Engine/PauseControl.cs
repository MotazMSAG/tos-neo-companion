using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Engine.Internal;

namespace Companion.Engine;

/// <summary>
/// Pause control (SPEC 6.8): the runner checks <see cref="IsPaused"/> at every gate and waits there (no input) until
/// resumed. An input already in progress completes or aborts through its own guards. Thread-safe.
/// </summary>
public sealed class PauseControl : IPauseControl
{
    private readonly IJournal _journal;
    private readonly IClock _clock;
    private volatile bool _paused;

    /// <summary>Creates the control (not paused, no side effects).</summary>
    public PauseControl(IJournal journal, IClock clock)
    {
        _journal = journal;
        _clock = clock;
    }

    /// <inheritdoc />
    public bool IsPaused => _paused;

    /// <inheritdoc />
    public void Pause(string sourceCode)
    {
        _paused = true;
        Record("pause", sourceCode);
    }

    /// <inheritdoc />
    public void Resume(string sourceCode)
    {
        _paused = false;
        Record("resume", sourceCode);
    }

    private void Record(string what, string sourceCode)
    {
        try
        {
            _journal.Write(new JournalRecord(_clock.UtcNow, JournalKind.Wait, null, null, null,
                new Dictionary<string, object?> { ["event"] = what, ["source"] = JournalSchema.Code(sourceCode) }));
        }
        catch
        {
            // never throws
        }
    }
}
