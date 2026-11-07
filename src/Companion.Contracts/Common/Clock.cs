namespace Companion.Contracts;

/// <summary>
/// Time source used by every unit that waits or measures durations. Production uses Stopwatch for
/// <see cref="Monotonic"/>; tests use a fake that advances instantly in <see cref="Sleep"/>.
/// All timeouts, stillness checks and pacing MUST use <see cref="Monotonic"/>, never wall time
/// (wall time jumps on sleep/resume and clock adjustment, F19/PITFALL-12).
/// </summary>
public interface IClock
{
    /// <summary>Wall-clock time (UTC). Only for journaling and for comparing with log timestamps.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Monotonic elapsed time since an arbitrary origin. Never jumps.</summary>
    TimeSpan Monotonic { get; }

    /// <summary>
    /// Blocks for <paramref name="duration"/> (or advances fake time). Throws <see cref="OperationCanceledException"/>
    /// when <paramref name="ct"/> is cancelled.
    /// </summary>
    void Sleep(TimeSpan duration, CancellationToken ct);
}

/// <summary>User on/off switches that gate live automation (config.json "automation"). Off by default.</summary>
public enum AutomationSwitch
{
    /// <summary>Not gated (read-only scans, navigation, maintenance).</summary>
    None = 0,

    /// <summary>Gated by config automation.crafting (any commit inside crafting screens or a crafting station).</summary>
    Crafting = 1,

    /// <summary>Gated by config automation.homestead (any commit inside homestead screens).</summary>
    Homestead = 2,
}

/// <summary>Version information for the contracts. Features declare the API version they were built against.</summary>
public static class ContractsInfo
{
    /// <summary>Contracts API version. Bumped only by a spec change (never by builders).</summary>
    public const int ApiVersion = 1;
}
