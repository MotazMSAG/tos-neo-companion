using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;

namespace Companion.CaptureProbe;

/// <summary>One sample of the window and cursor state while the window is moved (read-only calls).</summary>
/// <param name="Exists">The game window still exists.</param>
/// <param name="Foreground">The game process owns the foreground window.</param>
/// <param name="Minimized">The game window is minimized.</param>
/// <param name="Rect">Window rect, or null if unreadable.</param>
/// <param name="Cursor">Cursor position, or null if unavailable.</param>
/// <param name="ButtonDown">A mouse button is held.</param>
/// <param name="GameUnderCursor">The top-level window under the cursor is the game.</param>
internal sealed record ProbeSample(bool Exists, bool Foreground, bool Minimized, RectI? Rect, PointI? Cursor, bool ButtonDown, bool GameUnderCursor);

/// <summary>The probe's stop rules (pure; tested offline). Any reason means: move the window home now.</summary>
internal static class ProbeGuards
{
    /// <summary>Cursor margin inside the covering window (SPEC 7.5).</summary>
    public const int CoverMarginPx = 16;

    /// <summary>Null while it is fine to stay moved; otherwise the reason to move back at once.</summary>
    /// <param name="s">The sample.</param>
    /// <param name="cursor">The resting cursor recorded before the move.</param>
    /// <param name="expected">The rect the window should have now.</param>
    public static string? StopReason(ProbeSample s, PointI cursor, RectI expected)
    {
        if (!s.Exists)
        {
            return "game window gone";
        }

        if (s.Foreground)
        {
            return "game became foreground";
        }

        if (s.Minimized)
        {
            return "game minimized";
        }

        if (s.Rect != expected)
        {
            return "window rect changed (not by the probe)";
        }

        if (s.Cursor != cursor)
        {
            return "cursor moved";
        }

        if (s.ButtonDown)
        {
            return "mouse button pressed";
        }

        return s.GameUnderCursor ? "game under the cursor" : null;
    }

    /// <summary>
    /// Null when the move may start: the window under the cursor is another window above the game whose visible frame
    /// holds the cursor with the margin. Otherwise the refusal.
    /// </summary>
    /// <param name="cursor">Resting cursor.</param>
    /// <param name="top">Top-level window under the cursor (0 = none).</param>
    /// <param name="game">The game window.</param>
    /// <param name="aboveGame">The cursor window is above the game in z-order.</param>
    /// <param name="frame">Its DWM frame bounds.</param>
    public static string? CoverRefusal(PointI cursor, long top, long game, bool aboveGame, RectI? frame)
    {
        if (top == 0 || top == game)
        {
            return "no other window under the cursor";
        }

        if (!aboveGame)
        {
            return "the window under the cursor is not above the game";
        }

        return frame is { } f && f.ContainsWithMargin(cursor, CoverMarginPx) ? null : "cursor too close to the edge of the window under it";
    }

    /// <summary>
    /// Null when the moved window lands on the home monitor or on another monitor with the home monitor's DPI (a DPI
    /// change would make Windows resize the game; the user allowed same-DPI cross-monitor moves, EVIDENCE c12).
    /// </summary>
    /// <param name="home">Home monitor.</param>
    /// <param name="slid">Monitor with the moved window's largest overlap (pure computation), or null.</param>
    /// <param name="win32Slid">MonitorFromRect device name, or null.</param>
    /// <param name="monitors">All monitors.</param>
    public static string? MonitorRefusal(MonitorInfo? home, MonitorInfo? slid, string? win32Slid, IReadOnlyList<MonitorInfo> monitors)
    {
        if (home is null || slid is null)
        {
            return "monitor layout unknown";
        }

        if (home.Dpi != HardRules.ExpectedDpi)
        {
            return "home monitor DPI is not " + HardRules.ExpectedDpi;
        }

        bool SameDpi(string? device) =>
            string.Equals(device, home.DeviceName, StringComparison.Ordinal)
            || monitors.FirstOrDefault(m => string.Equals(m.DeviceName, device, StringComparison.Ordinal)) is { } m && m.Dpi == home.Dpi;

        return SameDpi(slid.DeviceName) && (win32Slid is null || SameDpi(win32Slid))
            ? null
            : "the moved window would land on a monitor with another DPI (Windows would resize the game)";
    }
}
