namespace Companion.Contracts.Safety;

/// <summary>Who requested a stop.</summary>
public enum StopSource
{
    /// <summary>Global hotkey Ctrl+Alt+Pause (App, RegisterHotKey).</summary>
    Hotkey,
    /// <summary>Tray menu "Stop now".</summary>
    Tray,
    /// <summary>Job pipe 'stop' op (anyone with pipe access may stop: fail-safe).</summary>
    Pipe,
    /// <summary>Windows session ending / logoff / shutdown.</summary>
    SessionEnding,
    /// <summary>The engine itself (HALT path).</summary>
    Engine,
}

/// <summary>A stop request.</summary>
/// <param name="Source">Who requested it.</param>
/// <param name="ReasonCode">Short code (no free text).</param>
/// <param name="AtUtc">When.</param>
public sealed record StopRequest(StopSource Source, string ReasonCode, DateTimeOffset AtUtc);

/// <summary>
/// Kill switch (KILL-21). Implemented in Companion.GameIO (the input choke point and the slide watchdog read it on
/// every sample). Once set: GameIO refuses every new input at once, an in-flight slide is ended by the watchdog
/// (UP if DOWN was posted, unfocus unless the game is really foreground, slide back), and the engine ends the job as
/// Stopped. Stays set until the user clears it in the tray. Thread-safe.
/// </summary>
public interface IStopSwitch
{
    /// <summary>True once a stop was requested and not yet cleared.</summary>
    bool IsStopRequested { get; }

    /// <summary>The active request, if any.</summary>
    StopRequest? Current { get; }

    /// <summary>Signalled while a stop is requested (for waits).</summary>
    WaitHandle Handle { get; }

    /// <summary>Requests a stop. Idempotent; the first request wins for <see cref="Current"/>.</summary>
    void RequestStop(StopSource source, string reasonCode);

    /// <summary>Clears the stop. Called only by the App after an explicit user action in the tray.</summary>
    void Clear();
}
