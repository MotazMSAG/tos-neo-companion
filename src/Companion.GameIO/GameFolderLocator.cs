using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.GameIO.Native;

namespace Companion.GameIO;

/// <summary>
/// Finds the folder of the RUNNING game, so a player never has to type it (read-only). It uses the same search as
/// <see cref="GameIo.Connect"/> (processes named TOS, exactly one visible UnityWndClass window, the executable path through
/// OpenProcess 0x1000 only) but without the exe path comparison, and returns the executable's folder when the file is
/// named TOS.exe and GameAssembly.dll lies next to it. Nothing is sent to the game and nothing is bound; the exe path
/// identity check of <see cref="GameIo.Connect"/> is unchanged.
/// </summary>
public static class GameFolderLocator
{
    /// <summary>The folder of the single running game window's TOS.exe, or null (not running, several windows, other exe).</summary>
    public static string? FindRunningGameFolder() => FindRunningGameFolder(new Win32Api());

    /// <summary>The same with a given Win32 layer (tests use the fake).</summary>
    internal static string? FindRunningGameFolder(IWin32 win32)
    {
        try
        {
            var found = GameWindowFinder.FindAnyPath(win32);
            if (found.Problem != ConnectProblem.None || string.IsNullOrWhiteSpace(found.ExePath))
            {
                return null;
            }

            var exe = Path.GetFullPath(found.ExePath);
            if (!string.Equals(Path.GetFileName(exe), CompanionPaths.GameExeName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var dir = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(dir) || win32.GetFileMetadata(Path.Combine(dir, CompanionPaths.GameAssemblyName)) is null)
            {
                return null;
            }

            return dir;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}
