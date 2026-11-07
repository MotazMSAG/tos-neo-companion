using System.Globalization;
using System.Text;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Logs;
using Companion.Engine.Internal;

namespace Companion.Engine;

/// <summary>
/// The only way captures and log excerpts are persisted (SPEC 10.4, 12). Images must be PNGs produced by
/// IVision.EncodePng (the signature is checked); text lines are re-filtered with <see cref="ILogPrivacyFilter"/>.
/// Labels are "name" or "group/name"; both parts are sanitised to [a-z0-9_-] (a group becomes a folder, e.g. one
/// calibration run or one diagnostics bundle). Caps per category (entries and bytes); the oldest entries are pruned.
/// Reparse points anywhere below RuntimeDir make the save fail (null). Never throws. Thread-safe.
/// </summary>
public sealed class ArtifactStore : IArtifactStore
{
    internal const int MaxExcerptLines = 500;
    internal const long MaxImageBytes = 16L * 1024 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly CompanionPaths _paths;
    private readonly IClock _clock;
    private readonly ILogPrivacyFilter _privacy;
    private readonly object _gate = new();

    /// <summary>Creates the store (no I/O).</summary>
    public ArtifactStore(CompanionPaths paths, IClock clock, ILogPrivacyFilter privacy)
    {
        _paths = paths;
        _clock = clock;
        _privacy = privacy;
    }

    /// <summary>Entry and byte caps of a category (diagnostics: 20 bundles / 50 MB, SPEC 10.4).</summary>
    internal static (int MaxEntries, long MaxBytes) Caps(ArtifactCategory category) => category switch
    {
        ArtifactCategory.Diagnostics => (20, 50L * 1024 * 1024),
        ArtifactCategory.Calibration => (50, 200L * 1024 * 1024),
        _ => (200, 200L * 1024 * 1024),
    };

    /// <summary>Folder of a category.</summary>
    internal static string CategoryDir(CompanionPaths paths, ArtifactCategory category) => category switch
    {
        ArtifactCategory.Diagnostics => paths.DiagnosticsDir,
        ArtifactCategory.Calibration => paths.CalibrationDir,
        _ => paths.SurveyDir,
    };

    /// <inheritdoc />
    public string? SaveImage(ArtifactCategory category, string label, byte[] png)
    {
        try
        {
            if (png is null || png.Length < 16 || png.Length > MaxImageBytes || !png.AsSpan(0, 8).SequenceEqual(PngSignature))
            {
                return null;
            }
            var (group, name) = SplitLabel(label);
            return Save(category, group, name + ".png", png);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public string? SaveLogExcerpt(ArtifactCategory category, string label, IReadOnlyList<string> filteredLines)
    {
        try
        {
            var sb = new StringBuilder();
            var n = 0;
            foreach (var raw in filteredLines ?? [])
            {
                if (n >= MaxExcerptLines)
                {
                    break;
                }
                if (raw is null)
                {
                    continue;
                }
                var line = _privacy.Filter(raw);
                if (line is null)
                {
                    continue;
                }
                sb.Append(line.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)).Append('\n');
                n++;
            }
            var (group, name) = SplitLabel(label);
            return Save(category, group, name + ".log", Encoding.UTF8.GetBytes(sb.ToString()));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Splits "group/name" into sanitised parts (group null when absent).</summary>
    internal static (string? Group, string Name) SplitLabel(string? label)
    {
        var l = label ?? string.Empty;
        var slash = l.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return (null, SafeFiles.SanitizeName(l));
        }
        return (SafeFiles.SanitizeName(l[..slash]), SafeFiles.SanitizeName(l[(slash + 1)..].Replace('/', '-')));
    }

    private string? Save(ArtifactCategory category, string? group, string fileName, byte[] bytes)
    {
        lock (_gate)
        {
            var root = _paths.RuntimeDir;
            var catDir = CategoryDir(_paths, category);
            var (maxEntries, maxBytes) = Caps(category);
            if (bytes.LongLength > maxBytes)
            {
                return null;
            }
            if (!SafeFiles.EnsureDirectory(root, catDir))
            {
                return null;
            }
            var target = group is null ? catDir : Path.Combine(catDir, group);
            var newEntry = group is null || !Directory.Exists(target);
            SafeFiles.Prune(catDir, newEntry ? maxEntries - 1 : maxEntries, maxBytes - bytes.LongLength, group is null ? null : target);
            if (!SafeFiles.EnsureDirectory(root, target))
            {
                return null;
            }
            if (group is null)
            {
                fileName = _clock.UtcNow.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture).ToLowerInvariant() + "-" + fileName;
            }
            return SafeFiles.WriteNew(target, fileName, bytes);
        }
    }
}
