using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;
using Companion.GameIO.Native;

namespace Companion.GameIO.Geometry;

/// <summary>Facts about a candidate cover window (class name only, never the title).</summary>
/// <param name="Hwnd">Root window under the cursor (WindowFromPoint + GetAncestor(GA_ROOT)); 0 = none.</param>
/// <param name="ClassName">Class.</param>
/// <param name="Pid">Owning PID.</param>
/// <param name="Visible">IsWindowVisible.</param>
/// <param name="Iconic">IsIconic.</param>
/// <param name="Cloaked">DWMWA_CLOAKED.</param>
/// <param name="ExStyle">GWL_EXSTYLE.</param>
/// <param name="LayeredAlpha">Effective layered alpha (null = per-pixel or colour key).</param>
/// <param name="FrameBounds">DWM extended frame bounds.</param>
/// <param name="RootOwner">GetAncestor(GA_ROOTOWNER).</param>
/// <param name="ExePath">Executable of the owning PID (null if unknown).</param>
internal sealed record WindowFacts(
    long Hwnd,
    string ClassName,
    int Pid,
    bool Visible,
    bool Iconic,
    bool Cloaked,
    long ExStyle,
    byte? LayeredAlpha,
    RectI? FrameBounds,
    long RootOwner,
    string? ExePath);

/// <summary>Context for the cover decision.</summary>
/// <param name="GameHwnd">Game window.</param>
/// <param name="GamePid">Game PID.</param>
/// <param name="OwnPid">The companion's PID.</param>
/// <param name="GameDir">Game install folder (executables under it count as game children).</param>
/// <param name="ForegroundHwnd">Foreground window.</param>
/// <param name="ForegroundPid">Foreground PID.</param>
/// <param name="Cursor">Cursor (screen).</param>
/// <param name="ProbeRoots">Roots found at the 8 probe points.</param>
/// <param name="CoverStableFor">How long the cover HWND and frame rect have been unchanged.</param>
/// <param name="RequiredStillness">Stillness the slide needs (0.8 s, 1.5 s near a deny zone).</param>
/// <param name="AboveGameInSnapshot">Cover index &lt; game index in one EnumWindows snapshot.</param>
/// <param name="AboveGameByWalk">Cover found walking GW_HWNDPREV up from the game (cap 2048, proven c3).</param>
internal sealed record CoverContext(
    long GameHwnd,
    int GamePid,
    int OwnPid,
    string GameDir,
    long ForegroundHwnd,
    int ForegroundPid,
    PointI Cursor,
    IReadOnlyList<long> ProbeRoots,
    TimeSpan CoverStableFor,
    TimeSpan RequiredStillness,
    bool AboveGameInSnapshot,
    bool AboveGameByWalk);

/// <summary>Pure cover-window rules (SPEC 7.5, F11, WIN-4, PITFALL-04/11).</summary>
internal static class CoverRules
{
    /// <summary>Shell and transient classes that are never a valid cover.</summary>
    public static readonly IReadOnlyList<string> ShellClasses =
    [
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "ForegroundStaging", "tooltips_class32", "#32768", "Ghost",
    ];

    /// <summary>The game's crash handler executable name.</summary>
    public const string CrashHandlerExe = "TuanjieCrashHandler64.exe";

    /// <summary>Evaluates a cover candidate; returns <see cref="CoverRejectReason.None"/> when valid.</summary>
    public static CoverRejectReason Evaluate(WindowFacts f, CoverContext c)
    {
        if (f.Hwnd == c.GameHwnd)
        {
            return CoverRejectReason.IsGame;
        }

        if (!f.Visible || f.Iconic)
        {
            return CoverRejectReason.NotVisible;
        }

        if (f.Cloaked)
        {
            return CoverRejectReason.Cloaked;
        }

        if (ShellClasses.Contains(f.ClassName, StringComparer.Ordinal))
        {
            return CoverRejectReason.ShellOrTransient;
        }

        if (f.Pid == c.OwnPid)
        {
            return CoverRejectReason.OwnWindow;
        }

        if (IsGameOwned(f, c))
        {
            return CoverRejectReason.GameOwned;
        }

        if ((f.ExStyle & Win32Const.WS_EX_TOPMOST) != 0)
        {
            return CoverRejectReason.Topmost;
        }

        if ((f.ExStyle & (Win32Const.WS_EX_TOOLWINDOW | Win32Const.WS_EX_NOACTIVATE)) != 0)
        {
            return CoverRejectReason.ToolWindow;
        }

        if ((f.ExStyle & Win32Const.WS_EX_LAYERED) != 0 && f.LayeredAlpha is { } a && a < 255)
        {
            return CoverRejectReason.Translucent;
        }

        if ((f.ExStyle & Win32Const.WS_EX_TRANSPARENT) != 0)
        {
            return CoverRejectReason.Translucent;
        }

        if (f.Hwnd != c.ForegroundHwnd && (c.ForegroundPid == 0 || f.Pid != c.ForegroundPid))
        {
            return CoverRejectReason.NotForeground;
        }

        if (f.FrameBounds is not { } frame || !frame.ContainsWithMargin(c.Cursor, HardRules.CoverMarginPx))
        {
            return CoverRejectReason.CursorNearEdge;
        }

        if (c.ProbeRoots.Count != 8 || c.ProbeRoots.Any(r => r != f.Hwnd))
        {
            return CoverRejectReason.RootMismatch;
        }

        if (c.CoverStableFor < c.RequiredStillness)
        {
            return CoverRejectReason.Unstable;
        }

        if (!c.AboveGameInSnapshot || !c.AboveGameByWalk)
        {
            return CoverRejectReason.NotAboveGame;
        }

        return CoverRejectReason.None;
    }

    /// <summary>Owned by the game PID, rooted at the game, or an executable of the game (crash handler, anything under the game folder).</summary>
    public static bool IsGameOwned(WindowFacts f, CoverContext c)
    {
        if (f.Pid == c.GamePid || f.RootOwner == c.GameHwnd)
        {
            return true;
        }

        if (f.ExePath is { Length: > 0 } exe)
        {
            if (string.Equals(System.IO.Path.GetFileName(exe), CrashHandlerExe, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var dir = c.GameDir.TrimEnd('\\', '/') + "\\";
            if (exe.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True if a window above the game hides what is behind it, for the exposure rule: visible, not minimized, not
    /// cloaked, not click-through, and opaque (a layered window counts only with a readable constant alpha of 255).
    /// </summary>
    public static bool HidesWhatIsBehind(bool visible, bool iconic, bool cloaked, long exStyle, byte? layeredAlpha)
    {
        if (!visible || iconic || cloaked)
        {
            return false;
        }

        if ((exStyle & Win32Const.WS_EX_TRANSPARENT) != 0)
        {
            return false;
        }

        if ((exStyle & Win32Const.WS_EX_LAYERED) != 0 && layeredAlpha is not 255)
        {
            return false;
        }

        return true;
    }
}
