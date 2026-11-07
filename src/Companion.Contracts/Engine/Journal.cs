namespace Companion.Contracts.Engine;

/// <summary>Journal record kinds.</summary>
public enum JournalKind
{
    /// <summary>Host started (versions, trusted build, features).</summary>
    HostStart,
    /// <summary>Host stopping.</summary>
    HostStop,
    /// <summary>Feature load report.</summary>
    FeatureLoad,
    /// <summary>Config change (tray).</summary>
    ConfigChange,
    /// <summary>Job accepted.</summary>
    JobAccepted,
    /// <summary>Job refused.</summary>
    JobRefused,
    /// <summary>Job finished.</summary>
    JobFinished,
    /// <summary>Step started.</summary>
    StepStart,
    /// <summary>Attempt outcome + class + decision + counters.</summary>
    Decision,
    /// <summary>Gate wait started/changed/cleared.</summary>
    Wait,
    /// <summary>Input batch summary (key or click) after the fact.</summary>
    Input,
    /// <summary>Slide begin/end marks (written after the slide-back; no I/O while displaced).</summary>
    Slide,
    /// <summary>Window incident.</summary>
    Incident,
    /// <summary>Park / resume / expire.</summary>
    Park,
    /// <summary>HALT.</summary>
    Halt,
    /// <summary>Stop switch.</summary>
    Stop,
    /// <summary>Character change / binding.</summary>
    Character,
    /// <summary>Stock update.</summary>
    Stock,
    /// <summary>Plan created.</summary>
    Plan,
    /// <summary>Notification sent.</summary>
    Notification,
    /// <summary>Log rotation / bootstrap.</summary>
    Log,
    /// <summary>Auto-collect status before/after a procedure.</summary>
    AutoCollect,
}

/// <summary>
/// A journal record (JSONL). Field values: bool, integer, double, or a CODE string matching
/// ^[A-Za-z0-9_.:,()\[\]/+\- ]{0,120}$ (UI names, template ids, class names, outcome names). No free text, no raw
/// log lines, no chat, no window titles, no role ids (PRIV-14). The writer replaces any other value with "&lt;redacted&gt;".
/// </summary>
/// <param name="Utc">Time.</param>
/// <param name="Kind">Kind.</param>
/// <param name="JobId">Job.</param>
/// <param name="ProcedureId">Procedure.</param>
/// <param name="StepId">Step.</param>
/// <param name="Fields">Fields.</param>
public sealed record JournalRecord(
    DateTimeOffset Utc,
    JournalKind Kind,
    string? JobId,
    string? ProcedureId,
    string? StepId,
    IReadOnlyDictionary<string, object?> Fields);

/// <summary>
/// Journal (unit "engine-nav"): buffered JSONL writer under RuntimeDir\journal; never throws into control flow
/// (retries, then drops with a counter). Never read back as input for actions (WIN-9). Thread-safe.
/// </summary>
public interface IJournal
{
    /// <summary>Appends a record (validated, buffered).</summary>
    void Write(JournalRecord record);

    /// <summary>Flushes to disk (not called while the window is displaced).</summary>
    void Flush();
}

/// <summary>Artifact categories.</summary>
public enum ArtifactCategory
{
    /// <summary>Calibration captures.</summary>
    Calibration,
    /// <summary>Survey captures / excerpts.</summary>
    Survey,
    /// <summary>Diagnostics bundles.</summary>
    Diagnostics,
}

/// <summary>
/// Artifact store (unit "engine-nav"): the only way captures and log excerpts are persisted. Images must come from
/// IVision.EncodePng (masked); text lines are re-filtered with ILogPrivacyFilter. Caps count and size per category.
/// </summary>
public interface IArtifactStore
{
    /// <summary>Saves a PNG; returns the path or null if refused/capped.</summary>
    string? SaveImage(ArtifactCategory category, string label, byte[] png);

    /// <summary>Saves filtered log lines; returns the path or null.</summary>
    string? SaveLogExcerpt(ArtifactCategory category, string label, IReadOnlyList<string> filteredLines);
}
