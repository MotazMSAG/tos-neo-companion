using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;
using Companion.GameIO.Safety;

namespace Companion.GameIO.Native;

/// <summary>
/// The game window search (read-only): processes named TOS, their visible top-level UnityWndClass windows (exactly one),
/// and the executable path (OpenProcess 0x1000 only). Shared by <see cref="GameIo.Connect"/>, the operator-run
/// capture probe (tools\Companion.CaptureProbe) and <see cref="GameFolderLocator"/>, so all of them find the same window.
/// </summary>
internal static class GameWindowFinder
{
    /// <summary>Finds the game window; Problem is None only with a single candidate whose exe path matches.</summary>
    /// <param name="win32">Win32 layer.</param>
    /// <param name="gameExePath">Expected executable (for example D:\Games\TOS\TOS.exe).</param>
    public static (ConnectProblem Problem, long Hwnd, int Pid, string? ExePath) Find(IWin32 win32, string gameExePath)
    {
        var found = FindAnyPath(win32);
        if (found.Problem != ConnectProblem.None)
        {
            return found;
        }

        var exe = found.ExePath;
        if (exe is null || !string.Equals(Path.GetFullPath(exe), Path.GetFullPath(gameExePath), StringComparison.OrdinalIgnoreCase))
        {
            return (ConnectProblem.ExePathMismatch, found.Hwnd, found.Pid, exe);
        }

        return found;
    }

    /// <summary>
    /// The same search WITHOUT the exe path comparison: Problem is None with a single candidate window; its executable
    /// path is read (OpenProcess 0x1000 only) and may be null when Windows does not tell it.
    /// </summary>
    /// <param name="win32">Win32 layer.</param>
    public static (ConnectProblem Problem, long Hwnd, int Pid, string? ExePath) FindAnyPath(IWin32 win32)
    {
        var pids = win32.GetProcessIdsByName(HardRules.GameProcessName);
        if (pids.Count == 0)
        {
            return (ConnectProblem.NotRunning, 0, 0, null);
        }

        var candidates = win32.EnumTopLevelWindows()
            .Where(h => win32.IsWindowVisible(h)
                && string.Equals(win32.GetClassName(h), HardRules.GameWindowClass, StringComparison.Ordinal)
                && pids.Contains(win32.GetWindowProcessId(h)))
            .ToList();
        if (candidates.Count == 0)
        {
            return (ConnectProblem.WindowNotFound, 0, 0, null);
        }

        if (candidates.Count > 1)
        {
            return (ConnectProblem.Ambiguous, 0, 0, null);
        }

        var hwnd = candidates[0];
        var pid = win32.GetWindowProcessId(hwnd);
        var exe = Guards.QueryImagePath(win32, pid, StaticRules.OpenProcessAllowedAccess);
        return (ConnectProblem.None, hwnd, pid, exe);
    }
}
