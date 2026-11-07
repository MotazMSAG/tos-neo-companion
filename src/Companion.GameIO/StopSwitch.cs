using Companion.Contracts;
using Companion.Contracts.Safety;

namespace Companion.GameIO;

/// <summary>
/// Kill switch (KILL-21, SPEC 6.8). Once requested, GameIO refuses every new input at once and the slide watchdog ends
/// an in-flight slide within one sample. Stays set until <see cref="Clear"/> (the tray, after an explicit user action).
/// The first request wins for <see cref="Current"/>. Thread-safe; the constructor has no side effects.
/// </summary>
public sealed class StopSwitch : IStopSwitch
{
    private readonly IClock _clock;
    private readonly ManualResetEvent _handle = new(false);
    private StopRequest? _current;

    /// <summary>Creates a cleared switch.</summary>
    /// <param name="clock">Clock for the request time.</param>
    public StopSwitch(IClock clock)
    {
        _clock = clock;
    }

    /// <inheritdoc />
    public bool IsStopRequested => Volatile.Read(ref _current) is not null;

    /// <inheritdoc />
    public StopRequest? Current => Volatile.Read(ref _current);

    /// <inheritdoc />
    public WaitHandle Handle => _handle;

    /// <inheritdoc />
    public void RequestStop(StopSource source, string reasonCode)
    {
        var req = new StopRequest(source, string.IsNullOrWhiteSpace(reasonCode) ? "stop" : reasonCode, _clock.UtcNow);
        Interlocked.CompareExchange(ref _current, req, null);
        _handle.Set();
    }

    /// <inheritdoc />
    public void Clear()
    {
        Volatile.Write(ref _current, null);
        _handle.Reset();
    }
}
