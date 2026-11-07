using Companion.Contracts;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;

namespace Companion.GameIO.Input;

/// <summary>
/// Log helpers for the input timelines: marks (positions), RecoverFocus / expected-UI / LostFocuse waits, the popup
/// gate and the resolution check. ILogTracker is thread-safe; GameIO polls it during an input while the host polls it
/// on its timer.
/// </summary>
internal sealed class LogWatch(ILogTracker log)
{
    /// <summary>Polls, then returns the current position (null before the first successful read).</summary>
    public LogPosition? Mark()
    {
        log.Poll();
        return log.Position;
    }

    /// <summary><see cref="Mark"/> that never throws (cleanup paths).</summary>
    public LogPosition? TryMark()
    {
        try
        {
            return Mark();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Polls, then returns the events after <paramref name="from"/> (empty for null or another generation).</summary>
    public IReadOnlyList<LogEvent> Since(LogPosition? from)
    {
        if (from is null)
        {
            return [];
        }

        log.Poll();
        return log.EventsSince(from);
    }

    /// <summary>Current snapshot.</summary>
    public UiStateSnapshot State => log.State;

    /// <summary>True if '[StageMgr] OnApplicationFocus RecoverFocus' is among the events.</summary>
    public static bool HasRecoverFocus(IReadOnlyList<LogEvent> events) =>
        events.OfType<FocusLogEvent>().Any(e => e.Recovered);

    /// <summary>True if one of the expectations matches exactly.</summary>
    public static bool HasExpected(IReadOnlyList<LogEvent> events, IReadOnlyList<UiExpectation> expected) =>
        expected.Count > 0 && events.OfType<UiLogEvent>().Any(e => expected.Any(x => x.Matches(e)));

    /// <summary>The last LostFocuse block, if any.</summary>
    public static PressBlockLogEvent? LastPressBlock(IReadOnlyList<LogEvent> events) =>
        events.OfType<PressBlockLogEvent>().LastOrDefault();

    /// <summary>True if '[graphic] Current screen resolution W H' was logged.</summary>
    public static bool HasResolution(IReadOnlyList<LogEvent> events, SizeI size) =>
        events.OfType<ResolutionLogEvent>().Any(e => e.Source == ResolutionSource.CurrentScreen && e.Size == size);

    /// <summary>
    /// Popup gate (SPEC 7.3 P3): any non-overlay show/open-const (Stack, Forbidden, Social, Busy, Station, Unknown), a
    /// popup real-show or a Pay line since the locating capture. Overlays (UI_CHANT ...) are ignored.
    /// </summary>
    public static bool PopupGateTripped(IReadOnlyList<LogEvent> events, IUiClassifier classifier)
    {
        foreach (var e in events)
        {
            switch (e)
            {
                case PayLogEvent:
                    return true;
                case UiLogEvent { Verb: UiVerb.PopupRealShow }:
                    return true;
                case UiLogEvent { Verb: UiVerb.Show or UiVerb.OpenConst, UiName: { } name }:
                    if (classifier.Classify(name) != UiKind.Overlay)
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }
}
