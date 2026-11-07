using System.Text.Json;
using Companion.Contracts;
using Companion.GameIO;

namespace Companion.App.Hosting;

/// <summary>Where the game folder of this start came from.</summary>
internal enum GameFolderSource
{
    /// <summary>The TOS_GAME_DIR environment variable (for power users).</summary>
    Variable,

    /// <summary>The folder saved in the runtime folder at an earlier start or from the tray.</summary>
    Saved,

    /// <summary>The folder of the running game's TOS.exe.</summary>
    RunningGame,

    /// <summary>One of the usual install places.</summary>
    UsualPlace,

    /// <summary>The folder the user chose in the start dialog.</summary>
    UserChoice,
}

/// <summary>A resolved game folder and where it came from.</summary>
/// <param name="Folder">Full path of the folder that holds TOS.exe.</param>
/// <param name="Source">Where it came from.</param>
internal sealed record GameFolderChoice(string Folder, GameFolderSource Source)
{
    /// <summary>True when the folder should be saved so the next start is instant (found or chosen, not configured).</summary>
    public bool ShouldSave => Source is GameFolderSource.RunningGame or GameFolderSource.UsualPlace or GameFolderSource.UserChoice;
}

/// <summary>
/// Finds the game folder without the player editing anything (host start, SPEC 4). Order: TOS_GAME_DIR when it names a
/// folder with TOS.exe; the saved folder when it still holds TOS.exe; the running game's folder (read-only, through
/// <see cref="GameFolderLocator"/>); the usual install places (existence checks only); otherwise the start dialog asks.
/// The guardian uses the first two steps and the default, never a dialog. Folders found by the companion itself must
/// hold TOS.exe AND GameAssembly.dll; a folder the user names needs TOS.exe only.
/// </summary>
internal static class GameFolderResolver
{
    /// <summary>How deep below a chosen parent folder a TOS\TOS.exe is looked for.</summary>
    public const int MaxParentSearchDepth = 3;

    /// <summary>Most folders looked at below a chosen parent (a whole drive must not take long).</summary>
    public const int MaxParentSearchFolders = 5000;

    /// <summary>Steps 1 and 2: the environment variable, then the saved folder; null when neither names a game folder.</summary>
    /// <param name="variable">Value of TOS_GAME_DIR (may be null).</param>
    /// <param name="saved">The saved folder as read from the file (may be null; validated here).</param>
    public static GameFolderChoice? FromSettings(string? variable, string? saved)
    {
        if (CompanionPaths.NormalizeGameDir(variable) is { } fromVariable)
        {
            return new GameFolderChoice(fromVariable, GameFolderSource.Variable);
        }

        return CompanionPaths.NormalizeGameDir(saved) is { } fromFile ? new GameFolderChoice(fromFile, GameFolderSource.Saved) : null;
    }

    /// <summary>Steps 3 and 4: the running game's folder, then the first usual place that holds a complete game.</summary>
    /// <param name="runningGame">The running game's folder, or null (see <see cref="FindRunningGame"/>).</param>
    /// <param name="usualPlaces">Candidate folders in order (see <see cref="UsualPlaces"/>).</param>
    /// <param name="isCompleteGame">True when a folder holds TOS.exe and GameAssembly.dll.</param>
    public static GameFolderChoice? Detect(Func<string?> runningGame, IEnumerable<string> usualPlaces, Func<string, bool> isCompleteGame)
    {
        ArgumentNullException.ThrowIfNull(runningGame);
        ArgumentNullException.ThrowIfNull(usualPlaces);
        ArgumentNullException.ThrowIfNull(isCompleteGame);
        if (runningGame() is { } running && isCompleteGame(running))
        {
            return new GameFolderChoice(Path.GetFullPath(running), GameFolderSource.RunningGame);
        }

        foreach (var place in usualPlaces)
        {
            if (isCompleteGame(place))
            {
                return new GameFolderChoice(Path.GetFullPath(place), GameFolderSource.UsualPlace);
            }
        }

        return null;
    }

    /// <summary>
    /// The whole order. <paramref name="ask"/> shows the start dialog (it gets <paramref name="detect"/> for its Try again
    /// button) and returns the chosen folder or null for Exit. Null means: no game folder, do not start.
    /// </summary>
    public static GameFolderChoice? Resolve(string? variable, string? saved, Func<GameFolderChoice?> detect, Func<Func<GameFolderChoice?>, string?> ask)
    {
        ArgumentNullException.ThrowIfNull(detect);
        ArgumentNullException.ThrowIfNull(ask);
        if (FromSettings(variable, saved) is { } known)
        {
            return known;
        }

        if (detect() is { } found)
        {
            return found;
        }

        return CompanionPaths.NormalizeGameDir(ask(detect)) is { } chosen ? new GameFolderChoice(chosen, GameFolderSource.UserChoice) : null;
    }

    /// <summary>The guardian's folder (no UI, nothing searched): the variable, then the saved folder, then the default.</summary>
    public static string ForGuardian(string? variable, string? saved) => FromSettings(variable, saved)?.Folder ?? CompanionPaths.DefaultGameDir;

    /// <summary>
    /// The usual install places in order: D:\Games\TOS first, then for every given drive root X:\Games\TOS, X:\TOS,
    /// X:\Program Files\TOS and X:\Program Files (x86)\TOS (no duplicates).
    /// </summary>
    public static IReadOnlyList<string> UsualPlaces(IEnumerable<string> fixedDriveRoots)
    {
        ArgumentNullException.ThrowIfNull(fixedDriveRoots);
        var list = new List<string> { CompanionPaths.DefaultGameDir };
        foreach (var root in fixedDriveRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var r = root.TrimEnd('\\', '/') + "\\";
            foreach (var sub in (string[])[@"Games\TOS", "TOS", @"Program Files\TOS", @"Program Files (x86)\TOS"])
            {
                var place = r + sub;
                if (!list.Contains(place, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(place);
                }
            }
        }

        return list;
    }

    /// <summary>Root folders of the fixed drives that are ready (C:\, D:\, ...); empty when Windows cannot list them.</summary>
    public static IReadOnlyList<string> FixedDriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The running game's folder (read-only window and process query; never throws).</summary>
    public static string? FindRunningGame()
    {
        try
        {
            return GameFolderLocator.FindRunningGameFolder();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Steps 3 and 4 on this PC.</summary>
    public static GameFolderChoice? DetectOnThisPc() => Detect(FindRunningGame, UsualPlaces(FixedDriveRoots()), CompanionPaths.IsCompleteGameDir);

    /// <summary>
    /// A folder the user chose: accepted when it holds TOS.exe, or when it is a parent with exactly one TOS\TOS.exe below
    /// it (up to <see cref="MaxParentSearchDepth"/> levels; links and system folders are skipped). Null otherwise.
    /// </summary>
    public static string? AcceptChosen(string? chosen)
    {
        if (CompanionPaths.NormalizeGameDir(chosen) is { } direct)
        {
            return direct;
        }

        string root;
        try
        {
            if (string.IsNullOrWhiteSpace(chosen))
            {
                return null;
            }

            root = chosen.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            {
                return null;
            }

            root = Path.GetFullPath(root);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden,
        };
        var hits = new List<string>();
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        while (queue.Count > 0 && visited < MaxParentSearchFolders)
        {
            var (dir, depth) = queue.Dequeue();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(dir, "*", options).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (++visited > MaxParentSearchFolders)
                {
                    break;
                }

                if (string.Equals(Path.GetFileName(child), "TOS", StringComparison.OrdinalIgnoreCase)
                    && CompanionPaths.NormalizeGameDir(child) is { } game)
                {
                    hits.Add(game);
                    if (hits.Count > 1)
                    {
                        return null; // several games below: the user must pick one
                    }

                    continue;
                }

                if (depth + 1 < MaxParentSearchDepth)
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>
    /// True when the running game's folder is a game folder other than the one in use (the tray then suggests
    /// Advanced > Game folder...; the companion never switches by itself).
    /// </summary>
    public static bool IsOtherGameFolder(string? runningGame, string current) =>
        runningGame is not null && !SamePath(runningGame, current);

    /// <summary>Case-insensitive comparison of two full paths (trailing slashes ignored); false when either is not a path.</summary>
    public static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }
}

/// <summary>
/// The saved game folder (<see cref="CompanionPaths.GameFolderFileName"/> in the runtime folder). Written only by the
/// elevated host (start or tray) with the SEC-2 atomic write (no reparse points, inside the runtime folder); read by the
/// host and the guardian. The folder is validated by every reader (it must still hold TOS.exe).
/// </summary>
internal sealed class GameFolderStore
{
    /// <summary>Largest file accepted.</summary>
    public const int MaxBytes = 8 * 1024;

    private readonly string _runtimeDir;

    /// <summary>Creates the store for a runtime folder (no side effects).</summary>
    public GameFolderStore(string runtimeDir)
    {
        _runtimeDir = runtimeDir;
    }

    /// <summary>The file.</summary>
    public string FilePath => Path.Combine(_runtimeDir, CompanionPaths.GameFolderFileName);

    /// <summary>The saved folder when the file is there, readable, and the folder still holds TOS.exe; otherwise null.</summary>
    public string? Read() => CompanionPaths.NormalizeGameDir(ReadRaw());

    /// <summary>The folder text in the file, unvalidated; null when the file is missing, a reparse point or invalid.</summary>
    public string? ReadRaw()
    {
        var bytes = SafeFile.ReadSmall(FilePath, MaxBytes);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<SavedGameFolder>(bytes, ContractJson.Options);
            return saved is { SchemaVersion: 1 } ? saved.GameDir : null;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Saves <paramref name="folder"/> atomically; false when the folder is not a game folder or the write failed.</summary>
    public bool Save(string folder)
    {
        if (CompanionPaths.NormalizeGameDir(folder) is not { } full)
        {
            return false;
        }

        try
        {
            SafeFile.WriteAtomic(_runtimeDir, FilePath, JsonSerializer.SerializeToUtf8Bytes(new SavedGameFolder(1, full), ContractJson.Options));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>File shape.</summary>
    /// <param name="SchemaVersion">Always 1.</param>
    /// <param name="GameDir">The game folder (holds TOS.exe).</param>
    internal sealed record SavedGameFolder(int SchemaVersion, string? GameDir);
}
