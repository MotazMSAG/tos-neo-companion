using System.Security.Cryptography;
using System.Text;
using Companion.Contracts;
using Companion.Contracts.Knowledge;
using Companion.Knowledge.Storage;

namespace Companion.Knowledge;

/// <summary>
/// Salted character key (KS-01): HMAC-SHA256 of the trimmed login role token with a 32-byte local salt, truncated to
/// 8 bytes and written as 16 lower-case hex chars. The salt lives in RuntimeDir\knowledge\salt.bin (admin-only folder;
/// a copy in salt.bin.bak1). The raw token is never stored, logged or kept in a field.
/// The constructor has no side effects; the salt is read (or created once) on the first <see cref="Hash"/>.
/// Thread-safe (the log tracker's poll thread and the engine thread may both call it).
/// </summary>
public sealed class CharacterKeyHasher : ICharacterKeyHasher
{
    internal const string SaltFileName = "salt.bin";
    internal const int SaltBytes = 32;

    private readonly CompanionPaths _paths;
    private readonly object _gate = new();
    private byte[]? _salt;

    /// <summary>Stores the paths only.</summary>
    /// <param name="paths">Companion paths (the salt lives in <see cref="CompanionPaths.KnowledgeDir"/>).</param>
    public CharacterKeyHasher(CompanionPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc />
    public CharacterKey Hash(string rawRoleToken)
    {
        if (string.IsNullOrWhiteSpace(rawRoleToken))
        {
            throw new ArgumentException("knowledge.hash.emptyToken", nameof(rawRoleToken));
        }
        var salt = GetSalt();
        var data = Encoding.UTF8.GetBytes(rawRoleToken.Trim());
        try
        {
            var mac = HMACSHA256.HashData(salt, data);
            return new CharacterKey(Convert.ToHexStringLower(mac, 0, 8));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    private byte[] GetSalt()
    {
        lock (_gate)
        {
            return _salt ??= LoadOrCreateSalt();
        }
    }

    private byte[] LoadOrCreateSalt()
    {
        var path = Path.Combine(_paths.KnowledgeDir, SaltFileName);
        var backup = AtomicFile.BackupPath(path, 1);
        var main = TryReadSalt(path);
        if (main is not null)
        {
            return main;
        }
        var fromBackup = TryReadSalt(backup);
        if (fromBackup is not null)
        {
            return fromBackup;
        }

        // No valid salt anywhere: create one. Characters hashed with a lost salt get new keys (their old knowledge
        // stays on disk, unbound); nothing raw is ever needed to recover.
        AtomicFile.EnsureSafeDirectory(_paths.RuntimeDir, _paths.KnowledgeDir);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        if (File.Exists(path))
        {
            AtomicFile.RefuseReparse(path);
            File.Move(path, AtomicFile.CorruptPath(path), overwrite: true);
        }
        WriteNew(path, salt);
        AtomicFile.RefuseReparse(backup);
        WriteNew(backup + ".new", salt);
        File.Move(backup + ".new", backup, overwrite: true);
        return salt;
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        AtomicFile.RefuseReparse(path);
        var tmp = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    private static byte[]? TryReadSalt(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var bytes = AtomicFile.ReadBounded(path);
            return bytes.Length == SaltBytes && bytes.Any(b => b != 0) ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
