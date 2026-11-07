namespace Companion.Contracts;

/// <summary>
/// Data file names. Every data file is read ONCE at host start through IFileTrust.ReadTrusted and deserialized with
/// <see cref="ContractJson.Options"/>: templates.json = TemplateDefinition[], ui-map.json = UiMap,
/// procedures.json = ProcedureDefinition[], readers.json = ReaderDefinition[].
/// </summary>
public static class DataFileNames
{
    /// <summary>Templates (in TemplatesDir or a feature's templates folder).</summary>
    public const string Templates = "templates.json";

    /// <summary>UI map (in DataDir or a feature folder).</summary>
    public const string UiMap = "ui-map.json";

    /// <summary>Procedures (in DataDir or a feature folder).</summary>
    public const string Procedures = "procedures.json";

    /// <summary>Readers (in DataDir or a feature folder).</summary>
    public const string Readers = "readers.json";
}

/// <summary>
/// Every path the companion uses. Install-side folders are read once at host start (code and data are never re-read
/// while running). Runtime data lives in <see cref="RuntimeDir"/> (%ProgramData%\TOS-companion), which the elevated
/// host creates with an ACL of Administrators + SYSTEM full control and read-only for the interactive user, and checks
/// for reparse points before every write (SEC-2).
/// </summary>
/// <param name="InstallDir">Folder of Companion.App.exe (build output or dist).</param>
/// <param name="DataDir">Core data (ui-map.json, procedures.json, readers.json): InstallDir\data.</param>
/// <param name="TemplatesDir">Templates (templates.json + PNG): InstallDir\templates.</param>
/// <param name="FeaturesDataDir">Feature data folders: InstallDir\features\&lt;featureId&gt;.</param>
/// <param name="RuntimeDir">Runtime data root (%ProgramData%\TOS-companion).</param>
/// <param name="GameDir">Game install folder (read-only for us).</param>
/// <param name="GameLogPath">The game's log file (read-only, FileShare ReadWrite|Delete).</param>
/// <param name="GameExePath">The game's executable (identity check only; content never read).</param>
public sealed record CompanionPaths(
    string InstallDir,
    string DataDir,
    string TemplatesDir,
    string FeaturesDataDir,
    string RuntimeDir,
    string GameDir,
    string GameLogPath,
    string GameExePath)
{
    /// <summary>Game folder used when nothing else names one (TOS_GAME_DIR unset, nothing saved, nothing found).</summary>
    public const string DefaultGameDir = @"D:\Games\TOS";

    /// <summary>Environment variable naming the game folder (the one that holds TOS.exe and Log\log.txt). Optional.</summary>
    public const string GameDirVariable = "TOS_GAME_DIR";

    /// <summary>The game's executable file name.</summary>
    public const string GameExeName = "TOS.exe";

    /// <summary>The game's Unity runtime next to the executable (its presence tells a real game folder from a stray TOS.exe).</summary>
    public const string GameAssemblyName = "GameAssembly.dll";

    /// <summary>File in <see cref="RuntimeDir"/> that remembers the game folder found or chosen at an earlier start.</summary>
    public const string GameFolderFileName = "game-folder.json";

    /// <summary>The runtime data root every host uses (%ProgramData%\TOS-companion).</summary>
    public static string DefaultRuntimeDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TOS-companion");

    /// <summary>
    /// Default layout for an install folder: runtime under %ProgramData%\TOS-companion, game under TOS_GAME_DIR when that
    /// names an existing folder with TOS.exe in it, else D:\Games\TOS. The host and the guardian resolve the game folder
    /// more thoroughly (saved choice, running game, usual places) and use <see cref="ForGame"/>.
    /// </summary>
    public static CompanionPaths Default(string installDir) => ForGame(installDir, ResolveGameDir(Environment.GetEnvironmentVariable(GameDirVariable)));

    /// <summary>The layout for an install folder and a game folder.</summary>
    public static CompanionPaths ForGame(string installDir, string gameDir) => new(
        installDir,
        Path.Combine(installDir, "data"),
        Path.Combine(installDir, "templates"),
        Path.Combine(installDir, "features"),
        DefaultRuntimeDir,
        gameDir,
        Path.Combine(gameDir, "Log", "log.txt"),
        Path.Combine(gameDir, GameExeName));

    /// <summary>The configured game folder when it is a full path that holds TOS.exe; otherwise the default.</summary>
    public static string ResolveGameDir(string? configured) => NormalizeGameDir(configured) ?? DefaultGameDir;

    /// <summary>
    /// The full path of <paramref name="configured"/> (quotes, spaces and trailing slashes removed) when it is a fully
    /// qualified folder that holds TOS.exe; otherwise null. Existence checks only; nothing is opened.
    /// </summary>
    public static string? NormalizeGameDir(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        try
        {
            var dir = configured.Trim().Trim('"').Trim().TrimEnd('\\', '/');
            if (dir.Length == 2 && dir[1] == ':')
            {
                dir += "\\"; // a drive root such as "E:" means "E:\", not the current folder of drive E
            }

            return Path.IsPathFullyQualified(dir) && File.Exists(Path.Combine(dir, GameExeName)) ? Path.GetFullPath(dir) : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="dir"/> looks like a complete game install: TOS.exe and GameAssembly.dll are both there.
    /// Used for folders the companion finds by itself (a folder the user names needs TOS.exe only).
    /// </summary>
    public static bool IsCompleteGameDir(string? dir)
    {
        var full = NormalizeGameDir(dir);
        if (full is null)
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(full, GameAssemblyName));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The saved game folder (see <see cref="GameFolderFileName"/>).</summary>
    public string GameFolderPath => Path.Combine(RuntimeDir, GameFolderFileName);

    /// <summary>config.json (automation switches etc.); written only by the tray UI of the elevated host.</summary>
    public string ConfigPath => Path.Combine(RuntimeDir, "config.json");

    /// <summary>Persisted HALT latch (survives host restarts until the user clears it).</summary>
    public string HaltLatchPath => Path.Combine(RuntimeDir, "halt.json");

    /// <summary>SHA-256 list of the build the user trusted (code + data files).</summary>
    public string TrustedBuildPath => Path.Combine(RuntimeDir, "trusted-build.json");

    /// <summary>Folder for slide markers and the host lock/heartbeat.</summary>
    public string RunDir => Path.Combine(RuntimeDir, "run");

    /// <summary>Slide marker written (and flushed) before every slide, deleted after a verified slide-back (PITFALL-02).</summary>
    public string SlideMarkerPath => Path.Combine(RunDir, "slide.json");

    /// <summary>JSONL journals (one file per day).</summary>
    public string JournalDir => Path.Combine(RuntimeDir, "journal");

    /// <summary>HALT diagnostics bundles (masked crops + filtered log excerpt + journal tail).</summary>
    public string DiagnosticsDir => Path.Combine(RuntimeDir, "diagnostics");

    /// <summary>Calibration captures (right HUD strip, full scale) for template cutting.</summary>
    public string CalibrationDir => Path.Combine(RuntimeDir, "calibration");

    /// <summary>Survey captures and filtered log excerpts.</summary>
    public string SurveyDir => Path.Combine(RuntimeDir, "survey");

    /// <summary>Per-character knowledge stores (folder per character key) and the local salt.</summary>
    public string KnowledgeDir => Path.Combine(RuntimeDir, "knowledge");
}
