using Companion.Contracts;
using Companion.Contracts.Knowledge;
using Companion.Knowledge.Storage;

namespace Companion.Knowledge;

/// <summary>
/// The companion's knowledge under RuntimeDir\knowledge (SPEC 11.2): ONE profile, <c>&lt;key&gt;\knowledge.json</c>,
/// holds the stock, recipes and keep-stock rules for every character, whoever is logged in (4.33; the user: the
/// per-character binding "is ruining the tool so often ... I'm the only one using the PC"). The profile's folder is
/// chosen once and pinned in <c>profile.json</c>: the folder the old <c>characters.json</c> allowed when it holds
/// knowledge (so everything learned before 4.33 is kept as it was), else the largest knowledge folder, else
/// <see cref="DefaultProfile"/>. <c>characters.json</c> is never written. Every file carries <c>schemaVersion</c>, is
/// written atomically with 5 backup generations and is read with fallback to the newest valid backup (reported in
/// <see cref="KnowledgeLoadReport.RecoveredFromBackup"/>). The constructor has no side effects; <see cref="Load"/> reads,
/// and pins the profile when nothing is pinned yet. Only the host process writes. Thread-safe (one lock).
/// </summary>
public sealed class KnowledgeStore : IKnowledgeStore
{
    /// <summary>The profile folder of a fresh install (no knowledge from before 4.33).</summary>
    public static readonly CharacterKey DefaultProfile = new("0000000000000000");

    private readonly CompanionPaths _paths;
    private readonly IClock _clock;
    private readonly AtomicFile _files;
    private readonly object _gate = new();
    private CharacterKnowledge? _profile;
    private KnowledgeLoadReport? _report;
    private bool _pinMainInvalid;

    /// <summary>Stores dependencies only (no file access).</summary>
    /// <param name="paths">Companion paths (<see cref="CompanionPaths.KnowledgeDir"/> is used).</param>
    /// <param name="clock">Clock (file timestamps, retry back-off).</param>
    public KnowledgeStore(CompanionPaths paths, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);
        _paths = paths;
        _clock = clock;
        _files = new AtomicFile(paths.RuntimeDir, clock);
    }

    private string BindingsPath => Path.Combine(_paths.KnowledgeDir, KnowledgeSchema.BindingsFileName);

    private string PinPath => Path.Combine(_paths.KnowledgeDir, KnowledgeSchema.ProfileFileName);

    /// <inheritdoc />
    public ICharacterKnowledge Profile
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _profile!;
            }
        }
    }

    /// <inheritdoc />
    public KnowledgeLoadReport Load()
    {
        lock (_gate)
        {
            if (_report is not null)
            {
                return _report;
            }

            var recovered = new List<string>();
            var folders = ProfileFolders();
            var pinned = Pinned(recovered);
            var key = pinned ?? AllowedBefore() ?? Largest(folders) ?? DefaultProfile;
            if (pinned is null)
            {
                Pin(key);
            }

            var (knowledge, code) = LoadProfile(key);
            _profile = knowledge;
            if (code is not null)
            {
                recovered.Add(code);
            }

            _report = new KnowledgeLoadReport(recovered.Count == 0, Math.Max(1, folders.Count), recovered);
            return _report;
        }
    }

    private void EnsureLoaded()
    {
        if (_report is null)
        {
            Load();
        }
    }

    /// <summary>The pinned profile folder, or null when none is pinned yet (or the pin and its backups are unreadable).</summary>
    private CharacterKey? Pinned(List<string> recovered)
    {
        var read = _files.Read(PinPath, KnowledgeSchema.ParseProfile);
        switch (read.Source)
        {
            case ReadSource.Backup:
                recovered.Add($"profile:bak{read.BackupGeneration}");
                break;
            case ReadSource.Unrecoverable:
                recovered.Add("profile:unrecoverable");
                break;
        }

        _pinMainInvalid = read.MainInvalid;
        return read.Value?.Profile is { } k ? new CharacterKey(k) : null;
    }

    /// <summary>Pins the chosen folder (best effort: without the pin, the same files give the same choice next start).</summary>
    private void Pin(CharacterKey key)
    {
        var dto = new ProfileFileDto { SchemaVersion = KnowledgeSchema.CurrentVersion, SavedUtc = _clock.UtcNow, Profile = key.Value };
        try
        {
            _files.Write(PinPath, KnowledgeSchema.Serialize(dto), b => KnowledgeSchema.ParseProfile(b).Error, _pinMainInvalid);
            _pinMainInvalid = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // not pinned: chosen again from the same files at the next start
        }
    }

    /// <summary>
    /// The first character the old <c>characters.json</c> allowed, when its folder holds knowledge (the file or one of its
    /// backups), or null. Read only while nothing is pinned; a damaged old file is not reported (the store no longer
    /// owns it).
    /// </summary>
    private CharacterKey? AllowedBefore()
    {
        var read = _files.Read(BindingsPath, KnowledgeSchema.ParseBindings);
        var allowed = read.Value?.Characters?.FirstOrDefault(b => string.Equals(b.Permission, "allowed", StringComparison.OrdinalIgnoreCase));
        return allowed?.Character is { } k && HoldsKnowledge(k) ? new CharacterKey(k) : null;
    }

    private bool HoldsKnowledge(string key)
    {
        try
        {
            var dir = Path.Combine(_paths.KnowledgeDir, key);
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, KnowledgeSchema.CharacterFileName + "*").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The knowledge folders (valid key names holding the knowledge file or one of its backups) with the size of their
    /// largest such file (a folder that lost its main file still counts: the read falls back to the backup).
    /// </summary>
    private List<(CharacterKey Key, long Size)> ProfileFolders()
    {
        var found = new List<(CharacterKey, long)>();
        try
        {
            if (!Directory.Exists(_paths.KnowledgeDir))
            {
                return found;
            }

            foreach (var dir in Directory.EnumerateDirectories(_paths.KnowledgeDir))
            {
                var key = new CharacterKey(Path.GetFileName(dir));
                if (!KnowledgeSchema.IsValidCharacterKey(key))
                {
                    continue;
                }

                var sizes = new DirectoryInfo(dir).EnumerateFiles(KnowledgeSchema.CharacterFileName + "*")
                    .Where(f => f.Name.Length == KnowledgeSchema.CharacterFileName.Length || f.Name.StartsWith(KnowledgeSchema.CharacterFileName + ".bak", StringComparison.Ordinal))
                    .Select(f => f.Length)
                    .ToList();
                if (sizes.Count > 0)
                {
                    found.Add((key, sizes.Max()));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // no folder list: the pinned, allowed or default profile is used
        }

        return found;
    }

    private static CharacterKey? Largest(List<(CharacterKey Key, long Size)> folders) =>
        folders.Count == 0 ? null : folders.OrderByDescending(f => f.Size).ThenBy(f => f.Key.Value, StringComparer.Ordinal).First().Key;

    private (CharacterKnowledge Knowledge, string? RecoveryCode) LoadProfile(CharacterKey key)
    {
        var path = Path.Combine(_paths.KnowledgeDir, key.Value, KnowledgeSchema.CharacterFileName);
        var gameDir = _paths.GameDir;
        var read = _files.Read(path, bytes => KnowledgeSchema.ParseCharacter(bytes, key, gameDir));
        string? code = read.Source switch
        {
            ReadSource.Backup => $"character.{key.Short}:bak{read.BackupGeneration}",
            ReadSource.Unrecoverable => $"character.{key.Short}:unrecoverable",
            _ => null,
        };
        var repair = read.Source is ReadSource.Backup or ReadSource.Unrecoverable;
        var knowledge = new CharacterKnowledge(key, path, _files, _clock, gameDir, read.Value, repair, read.MainInvalid);
        return (knowledge, code);
    }
}
