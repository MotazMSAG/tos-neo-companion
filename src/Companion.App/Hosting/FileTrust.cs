using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Companion.Contracts;
using Companion.Contracts.Host;

namespace Companion.App.Hosting;

/// <summary>One install-side file and its SHA-256 (lower-case hex).</summary>
/// <param name="RelativePath">Path relative to InstallDir, '\' separated.</param>
/// <param name="Sha256">Hash.</param>
internal sealed record BuildFile(string RelativePath, string Sha256);

/// <summary>The hash list of a build (trusted-build.json when the user trusted it).</summary>
/// <param name="SchemaVersion">1.</param>
/// <param name="BuildHash">SHA-256 over the sorted "path\thash\n" lines.</param>
/// <param name="TrustedUtc">When the user trusted it (null for a scan).</param>
/// <param name="Files">Files.</param>
internal sealed record BuildManifest(int SchemaVersion, string BuildHash, DateTimeOffset? TrustedUtc, IReadOnlyList<BuildFile> Files)
{
    /// <summary>Short form for the tray (first 16 hex chars).</summary>
    [JsonIgnore]
    public string ShortHash => BuildHash.Length >= 16 ? BuildHash[..16] : BuildHash;
}

/// <summary>Result of comparing the scanned build with the trusted list.</summary>
/// <param name="Trusted">Every file matches.</param>
/// <param name="Missing">Trusted files that are gone.</param>
/// <param name="Changed">Files whose hash differs.</param>
/// <param name="Extra">Files not in the trusted list.</param>
/// <param name="NoTrustList">trusted-build.json missing or unreadable.</param>
internal sealed record TrustComparison(bool Trusted, int Missing, int Changed, int Extra, bool NoTrustList)
{
    /// <summary>Code for the tray and the journal.</summary>
    public string Code => Trusted ? "trusted" : NoTrustList ? "untrusted.noList" : $"untrusted.missing{Missing}.changed{Changed}.extra{Extra}";
}

/// <summary>
/// Integrity of install-side files (SEC-1/FR-01, SPEC 4 step 5, 10.1). At start the host hashes every install-side file
/// (code: *.dll/*.exe under InstallDir; data: data\, templates\, features\) into a snapshot and compares it with
/// trusted-build.json (admin-only, written only when the user clicks "Trust this build..."). Data files are read through
/// <see cref="ReadTrusted"/>: the bytes must match the start snapshot (and the trusted list when there is one), so the
/// data that is loaded is exactly the build the user trusts in the dialog. Untrusted builds load nothing new later and
/// run no jobs. Thread-safe.
/// </summary>
internal sealed class FileTrust : IFileTrust
{
    /// <summary>Largest trusted-build.json accepted.</summary>
    public const int MaxListBytes = 16 * 1024 * 1024;

    /// <summary>Largest single data file read through <see cref="ReadTrusted"/>.</summary>
    public const int MaxDataFileBytes = 32 * 1024 * 1024;

    private static readonly string[] DataFolders = ["data", "templates", "features"];

    private readonly CompanionPaths _paths;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private Dictionary<string, string>? _snapshot;
    private Dictionary<string, string>? _trustedList;
    private BuildManifest? _scanned;
    private bool _trusted;

    /// <summary>Creates the trust checker (no side effects).</summary>
    public FileTrust(CompanionPaths paths, IClock clock)
    {
        _paths = paths;
        _clock = clock;
    }

    /// <inheritdoc />
    public bool BuildTrusted
    {
        get
        {
            lock (_gate)
            {
                return _trusted;
            }
        }
    }

    /// <summary>The start snapshot (null before <see cref="ScanAndCompare"/>).</summary>
    public BuildManifest? Scanned
    {
        get
        {
            lock (_gate)
            {
                return _scanned;
            }
        }
    }

    /// <summary>Last comparison.</summary>
    public TrustComparison? Comparison { get; private set; }

    /// <summary>Hashes the install-side files of <paramref name="installDir"/> (deterministic order).</summary>
    public static BuildManifest Scan(string installDir)
    {
        var root = Path.GetFullPath(installDir);
        var files = new List<BuildFile>();
        foreach (var path in EnumerateInstallFiles(root))
        {
            files.Add(new BuildFile(Relative(root, path), HashFile(path)));
        }

        files.Sort((a, b) => string.CompareOrdinal(a.RelativePath.ToUpperInvariant(), b.RelativePath.ToUpperInvariant()));
        return new BuildManifest(1, BuildHashOf(files), null, files);
    }

    /// <summary>SHA-256 over the sorted "path\thash\n" lines (lower-case hex).</summary>
    public static string BuildHashOf(IReadOnlyList<BuildFile> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files.OrderBy(f => f.RelativePath.ToUpperInvariant(), StringComparer.Ordinal))
        {
            sb.Append(f.RelativePath.ToUpperInvariant()).Append('\t').Append(f.Sha256).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Compares a scan with a trusted list.</summary>
    public static TrustComparison Compare(BuildManifest scanned, BuildManifest? trusted)
    {
        if (trusted is null)
        {
            return new TrustComparison(false, 0, 0, scanned.Files.Count, true);
        }

        var t = trusted.Files.ToDictionary(f => f.RelativePath, f => f.Sha256, StringComparer.OrdinalIgnoreCase);
        var s = scanned.Files.ToDictionary(f => f.RelativePath, f => f.Sha256, StringComparer.OrdinalIgnoreCase);
        var missing = t.Keys.Count(k => !s.ContainsKey(k));
        var extra = s.Keys.Count(k => !t.ContainsKey(k));
        var changed = s.Count(kv => t.TryGetValue(kv.Key, out var h) && !string.Equals(h, kv.Value, StringComparison.OrdinalIgnoreCase));
        return new TrustComparison(missing == 0 && extra == 0 && changed == 0, missing, changed, extra, false);
    }

    /// <summary>Host start: scans the install folder and compares it with trusted-build.json.</summary>
    public TrustComparison ScanAndCompare()
    {
        var scanned = Scan(_paths.InstallDir);
        var trusted = LoadTrustList();
        var cmp = Compare(scanned, trusted);
        lock (_gate)
        {
            _scanned = scanned;
            _snapshot = scanned.Files.ToDictionary(f => f.RelativePath, f => f.Sha256, StringComparer.OrdinalIgnoreCase);
            _trustedList = trusted?.Files.ToDictionary(f => f.RelativePath, f => f.Sha256, StringComparer.OrdinalIgnoreCase);
            _trusted = cmp.Trusted;
            Comparison = cmp;
        }

        return cmp;
    }

    /// <inheritdoc />
    public byte[]? ReadTrusted(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return null;
        }

        string rel;
        string? expected;
        string? trustedHash = null;
        bool trusted;
        lock (_gate)
        {
            if (_snapshot is null)
            {
                return null;
            }

            var root = Path.GetFullPath(_paths.InstallDir);
            var full = Path.GetFullPath(fullPath);
            if (!SafeFile.IsUnder(root, full) || string.Equals(root.TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            rel = Relative(root, full);
            if (!_snapshot.TryGetValue(rel, out expected))
            {
                return null;
            }

            trusted = _trusted;
            if (_trustedList is not null)
            {
                _trustedList.TryGetValue(rel, out trustedHash);
            }
        }

        byte[] bytes;
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length > MaxDataFileBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            bytes = File.ReadAllBytes(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (trusted && !string.Equals(hash, trustedHash, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return bytes;
    }

    /// <summary>
    /// "Trust this build..." (tray, explicit user action): re-scans, and only if nothing changed since the start
    /// snapshot (the data that was loaded) writes trusted-build.json and marks the build trusted. Returns a code.
    /// </summary>
    public string TrustCurrentBuild()
    {
        BuildManifest? start;
        lock (_gate)
        {
            start = _scanned;
        }

        if (start is null)
        {
            return "trust.notScanned";
        }

        var now = Scan(_paths.InstallDir);
        if (!string.Equals(now.BuildHash, start.BuildHash, StringComparison.Ordinal))
        {
            return "trust.changedSinceStart";
        }

        var list = start with { TrustedUtc = _clock.UtcNow };
        try
        {
            SafeFile.WriteAtomic(_paths.RuntimeDir, _paths.TrustedBuildPath, JsonSerializer.SerializeToUtf8Bytes(list, ContractJson.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "trust.writeFailed";
        }

        lock (_gate)
        {
            _trustedList = list.Files.ToDictionary(f => f.RelativePath, f => f.Sha256, StringComparer.OrdinalIgnoreCase);
            _trusted = true;
            Comparison = new TrustComparison(true, 0, 0, 0, false);
        }

        return "trust.ok";
    }

    private BuildManifest? LoadTrustList()
    {
        var bytes = SafeFile.ReadSmall(_paths.TrustedBuildPath, MaxListBytes);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            var list = JsonSerializer.Deserialize<BuildManifest>(bytes, ContractJson.Options);
            if (list is null || list.SchemaVersion != 1 || list.Files is null || list.Files.Any(f => f is null || f.RelativePath is null || f.Sha256 is null))
            {
                return null;
            }

            // The stored build hash must match its own file list (a hand-edited list is not trusted).
            return string.Equals(BuildHashOf(list.Files), list.BuildHash, StringComparison.OrdinalIgnoreCase) ? list : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateInstallFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        var dataRoots = DataFolders.Select(d => Path.Combine(root, d) + Path.DirectorySeparatorChar).ToArray();
        foreach (var pattern in new[] { "*.dll", "*.exe" })
        {
            foreach (var f in Directory.EnumerateFiles(root, pattern, options))
            {
                if (!dataRoots.Any(d => f.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return f;
                }
            }
        }

        foreach (var d in DataFolders)
        {
            var dir = Path.Combine(root, d);
            if (!Directory.Exists(dir) || SafeFile.IsReparsePoint(dir))
            {
                continue;
            }

            foreach (var f in Directory.EnumerateFiles(dir, "*", options))
            {
                yield return f;
            }
        }
    }

    private static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('/', '\\');
}
