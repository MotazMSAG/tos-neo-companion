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
    /// <summary>Game folder used when TOS_GAME_DIR is not set.</summary>
    public const string DefaultGameDir = @"D:\Games\TOS";

    /// <summary>Environment variable naming the game folder (the one that holds TOS.exe and Log\log.txt).</summary>
    public const string GameDirVariable = "TOS_GAME_DIR";

    /// <summary>
    /// Default layout for an install folder: runtime under %ProgramData%\TOS-companion, game under TOS_GAME_DIR when that
    /// names an existing folder with TOS.exe in it, else D:\Games\TOS.
    /// </summary>
    public static CompanionPaths Default(string installDir) => ForGame(installDir, ResolveGameDir(Environment.GetEnvironmentVariable(GameDirVariable)));

    /// <summary>The layout for an install folder and a game folder.</summary>
    public static CompanionPaths ForGame(string installDir, string gameDir) => new(
        installDir,
        Path.Combine(installDir, "data"),
        Path.Combine(installDir, "templates"),
        Path.Combine(installDir, "features"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TOS-companion"),
        gameDir,
        Path.Combine(gameDir, "Log", "log.txt"),
        Path.Combine(gameDir, "TOS.exe"));

    /// <summary>The configured game folder when it is a full path that holds TOS.exe; otherwise the default.</summary>
    public static string ResolveGameDir(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultGameDir;
        }

        try
        {
            var dir = configured.Trim().Trim('"').TrimEnd('\\', '/');
            return Path.IsPathFullyQualified(dir) && File.Exists(Path.Combine(dir, "TOS.exe")) ? Path.GetFullPath(dir) : DefaultGameDir;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return DefaultGameDir;
        }
    }

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
