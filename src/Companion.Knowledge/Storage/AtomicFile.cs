using Companion.Contracts;

namespace Companion.Knowledge.Storage;

/// <summary>How a file was read.</summary>
internal enum ReadSource
{
    /// <summary>Neither the file nor any backup exists (first run).</summary>
    Missing,
    /// <summary>The main file was valid.</summary>
    Main,
    /// <summary>The main file was missing or invalid; a backup was used.</summary>
    Backup,
    /// <summary>Files exist but none is valid.</summary>
    Unrecoverable,
}

/// <summary>Result of <see cref="AtomicFile.Read{T}"/>.</summary>
/// <param name="Value">Parsed value (null when Missing or Unrecoverable).</param>
/// <param name="Source">Where it came from.</param>
/// <param name="BackupGeneration">Backup generation used (1 = newest), or 0.</param>
/// <param name="MainInvalid">The main file exists but is invalid (it is moved aside on the next write).</param>
/// <param name="MainError">Error code of the main file, if invalid.</param>
internal sealed record ReadResult<T>(T? Value, ReadSource Source, int BackupGeneration, bool MainInvalid, string? MainError)
    where T : class;

/// <summary>
/// Atomic file writes and validated reads for runtime data (SPEC 11.2, SEC-2):
/// write a temp file in the same folder with <see cref="FileMode.CreateNew"/> and <c>Flush(true)</c>, read it back and
/// validate it, then <see cref="File.Replace(string, string, string?, bool)"/> with the previous file kept as backup
/// generation 1 (5 generations, oldest dropped), with bounded retries on sharing violations. Every folder from the
/// runtime root down to the target, and the target itself, is refused if it is a reparse point.
/// Reads fall back to the newest valid backup.
/// </summary>
internal sealed class AtomicFile
{
    /// <summary>Backup generations kept.</summary>
    public const int BackupGenerations = 5;

    /// <summary>Largest file read (knowledge files are small; anything larger is treated as corrupt).</summary>
    public const long MaxFileBytes = 16L * 1024 * 1024;

    private const int MaxAttempts = 5;

    private readonly string _root;
    private readonly IClock _clock;

    /// <summary>Creates the helper. No side effects.</summary>
    /// <param name="root">Runtime root (reparse checks start here; writes outside it are refused).</param>
    /// <param name="clock">Clock used for retry back-off.</param>
    public AtomicFile(string root, IClock clock)
    {
        _root = root;
        _clock = clock;
    }

    /// <summary>Backup file path for a generation (1 = newest).</summary>
    public static string BackupPath(string path, int generation) => $"{path}.bak{generation}";

    /// <summary>Where an invalid main file is moved before the next write (kept for diagnosis).</summary>
    public static string CorruptPath(string path) => path + ".corrupt";

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> atomically. Throws <see cref="IOException"/> or
    /// <see cref="InvalidDataException"/> on failure; the previous file (if any) is then unchanged or kept as backup 1.
    /// </summary>
    /// <param name="path">Target file.</param>
    /// <param name="content">UTF-8 JSON.</param>
    /// <param name="validate">Parses + schema-validates the bytes read back from the temp file; returns an error code or null.</param>
    /// <param name="moveAsideInvalidMain">Move the existing main file to <see cref="CorruptPath"/> first (it was invalid at load).</param>
    public void Write(string path, byte[] content, Func<byte[], string?> validate, bool moveAsideInvalidMain)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full) ?? throw new IOException("knowledge.path.noFolder");
        EnsureSafeDirectory(dir);
        RefuseReparse(full);

        var tmp = $"{full}.tmp-{Guid.NewGuid():N}";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(content, 0, content.Length);
                fs.Flush(true);
            }

            var back = ReadBounded(tmp);
            var error = validate(back);
            if (error is not null)
            {
                throw new InvalidDataException("knowledge.write.invalid:" + error);
            }

            if (moveAsideInvalidMain && File.Exists(full))
            {
                RefuseReparse(CorruptPath(full));
                Retry(() => File.Move(full, CorruptPath(full), overwrite: true));
            }

            if (File.Exists(full))
            {
                RotateBackups(full);
            }

            Retry(() =>
            {
                if (File.Exists(full))
                {
                    File.Replace(tmp, full, BackupPath(full, 1), ignoreMetadataErrors: true);
                }
                else
                {
                    // First write, or a previous Replace moved the target to the backup but could not move the
                    // replacement (ERROR_UNABLE_TO_MOVE_REPLACEMENT_2): finish with a plain move.
                    File.Move(tmp, full);
                }
            });
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    /// <summary>
    /// Reads the main file, falling back to backups 1..5 (newest first). The parser returns a value or an error code.
    /// </summary>
    public ReadResult<T> Read<T>(string path, Func<byte[], (T? Value, string? Error)> parse)
        where T : class
    {
        var full = Path.GetFullPath(path);
        var anyExists = false;
        var mainInvalid = false;
        string? mainError = null;

        for (var gen = 0; gen <= BackupGenerations; gen++)
        {
            var candidate = gen == 0 ? full : BackupPath(full, gen);
            if (!File.Exists(candidate))
            {
                continue;
            }
            anyExists = true;
            T? value;
            string? error;
            try
            {
                (value, error) = parse(ReadBounded(candidate));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                (value, error) = (null, "io.read");
            }

            if (value is not null)
            {
                return gen == 0
                    ? new ReadResult<T>(value, ReadSource.Main, 0, false, null)
                    : new ReadResult<T>(value, ReadSource.Backup, gen, mainInvalid, mainError);
            }

            if (gen == 0)
            {
                mainInvalid = true;
                mainError = error ?? "invalid";
            }
        }

        return anyExists
            ? new ReadResult<T>(null, ReadSource.Unrecoverable, 0, mainInvalid, mainError)
            : new ReadResult<T>(null, ReadSource.Missing, 0, false, null);
    }

    /// <summary>Reads a file of at most <see cref="MaxFileBytes"/> bytes.</summary>
    public static byte[] ReadBounded(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length > MaxFileBytes)
        {
            throw new IOException("knowledge.read.tooLarge");
        }
        var buffer = new byte[fs.Length];
        fs.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>
    /// Creates <paramref name="dir"/> (and missing parents below the root) and refuses any reparse point from the root
    /// down. The root itself is created when missing (the App normally creates it first with its protected ACL).
    /// </summary>
    public void EnsureSafeDirectory(string dir) => EnsureSafeDirectory(_root, dir);

    /// <summary>Static form of <see cref="EnsureSafeDirectory(string)"/> for a given runtime root.</summary>
    public static void EnsureSafeDirectory(string rootDir, string dir)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDir));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        var isRoot = full.Equals(root, StringComparison.OrdinalIgnoreCase);
        if (!isRoot && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("knowledge.path.outsideRoot");
        }

        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
        }
        RefuseReparse(root);

        var current = root;
        var rest = isRoot ? string.Empty : full[(root.Length + 1)..];
        foreach (var segment in rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current))
            {
                RefuseReparse(current);
            }
            else if (File.Exists(current))
            {
                throw new IOException("knowledge.path.notAFolder");
            }
            else
            {
                Directory.CreateDirectory(current);
                RefuseReparse(current);
            }
        }
    }

    /// <summary>Throws when <paramref name="path"/> exists and is a reparse point (junction, symlink...).</summary>
    public static void RefuseReparse(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("knowledge.path.reparsePoint");
        }
    }

    private void RotateBackups(string full)
    {
        var oldest = BackupPath(full, BackupGenerations);
        if (File.Exists(oldest))
        {
            RefuseReparse(oldest);
            Retry(() => File.Delete(oldest));
        }
        for (var gen = BackupGenerations - 1; gen >= 1; gen--)
        {
            var from = BackupPath(full, gen);
            if (File.Exists(from))
            {
                var to = BackupPath(full, gen + 1);
                RefuseReparse(from);
                Retry(() => File.Move(from, to, overwrite: true));
            }
        }
    }

    private void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex))
            {
                _clock.Sleep(TimeSpan.FromMilliseconds(20 << attempt), CancellationToken.None);
            }
        }
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
        {
            return true; // an anti-virus scanner briefly holding the file
        }
        if (ex is IOException io and not FileNotFoundException and not DirectoryNotFoundException)
        {
            var code = io.HResult & 0xFFFF;
            // 32 sharing violation, 33 lock violation, 1175-1177 ReplaceFile partial failures.
            return code is 32 or 33 or 1175 or 1176 or 1177;
        }
        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is harmless; it never matches the main or backup names.
        }
    }
}
