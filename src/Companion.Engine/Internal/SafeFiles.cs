using System.Text.RegularExpressions;

namespace Companion.Engine.Internal;

/// <summary>
/// Runtime-file helpers (SEC-2): every folder from RuntimeDir down to the target is checked for reparse points before a
/// write, new files are written with FileMode.CreateNew to a temp name and renamed, and pruning never follows links.
/// Nothing here throws into control flow except programming errors in the callers' arguments.
/// </summary>
internal static partial class SafeFiles
{
    [GeneratedRegex("[^a-z0-9_-]", RegexOptions.CultureInvariant)]
    private static partial Regex BadNameChars();

    /// <summary>Sanitises a name component to [a-z0-9_-], at most 64 characters, never empty.</summary>
    public static string SanitizeName(string? name)
    {
        var s = BadNameChars().Replace((name ?? string.Empty).Trim().ToLowerInvariant(), "-");
        if (s.Length > 64)
        {
            s = s[..64];
        }
        return s.Length == 0 ? "x" : s;
    }

    /// <summary>True if the path exists and is a reparse point (symlink, junction, mount point).</summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true; // unknown = refuse
        }
    }

    /// <summary>
    /// Ensures <paramref name="dir"/> exists below the existing <paramref name="root"/>, refusing reparse points on the
    /// root and every component. The root itself is never created here (the App creates it with its ACL).
    /// </summary>
    public static bool EnsureDirectory(string root, string dir)
    {
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
            if (!Directory.Exists(fullRoot) || IsReparsePoint(fullRoot))
            {
                return false;
            }
            if (string.Equals(fullRoot, fullDir, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!fullDir.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var rel = fullDir[(fullRoot.Length + 1)..];
            var current = fullRoot;
            foreach (var part in rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (File.Exists(current))
                {
                    return false;
                }
                if (Directory.Exists(current))
                {
                    if (IsReparsePoint(current))
                    {
                        return false;
                    }
                }
                else
                {
                    Directory.CreateDirectory(current);
                    if (IsReparsePoint(current))
                    {
                        return false;
                    }
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Writes a new file (CreateNew temp + rename, Flush(true)); returns the final path or null.</summary>
    public static string? WriteNew(string dir, string fileName, ReadOnlySpan<byte> bytes)
    {
        string? temp = null;
        try
        {
            if (IsReparsePoint(dir))
            {
                return null;
            }
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            var tempPath = Path.Combine(dir, "." + stem + "." + Guid.NewGuid().ToString("N") + ".tmp");
            temp = tempPath;
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(true);
            }
            for (var n = 0; n < 1000; n++)
            {
                var candidate = Path.Combine(dir, n == 0 ? fileName : $"{stem}-{n}{ext}");
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    continue;
                }
                try
                {
                    File.Move(tempPath, candidate, overwrite: false);
                    temp = null;
                    return candidate;
                }
                catch (IOException)
                {
                    // raced: try the next name
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // best effort
                }
            }
        }
    }

    /// <summary>Total size of the regular files of an entry (one level of folders; links are not followed).</summary>
    public static long EntrySize(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return IsReparsePoint(path) ? 0 : new FileInfo(path).Length;
            }
            if (Directory.Exists(path) && !IsReparsePoint(path))
            {
                long total = 0;
                foreach (var f in Directory.EnumerateFiles(path))
                {
                    if (!IsReparsePoint(f))
                    {
                        total += new FileInfo(f).Length;
                    }
                }
                return total;
            }
        }
        catch
        {
            // ignore
        }
        return 0;
    }

    /// <summary>
    /// Deletes the oldest top-level entries of <paramref name="categoryDir"/> (by name: names start with a UTC stamp)
    /// until at most <paramref name="maxEntries"/> remain and they total at most <paramref name="maxBytes"/>. The
    /// entry named <paramref name="keep"/> is never deleted. Reparse points are skipped, never followed.
    /// </summary>
    public static void Prune(string categoryDir, int maxEntries, long maxBytes, string? keep)
    {
        try
        {
            if (!Directory.Exists(categoryDir) || IsReparsePoint(categoryDir))
            {
                return;
            }
            var entries = Directory.EnumerateFileSystemEntries(categoryDir)
                .Where(e => !Path.GetFileName(e).StartsWith('.'))
                .OrderBy(e => Path.GetFileName(e), StringComparer.Ordinal)
                .Select(e => (Path: e, Size: EntrySize(e)))
                .ToList();
            var count = entries.Count;
            var bytes = entries.Sum(e => e.Size);
            foreach (var (path, size) in entries)
            {
                if (count <= maxEntries && bytes <= maxBytes)
                {
                    break;
                }
                if (keep is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (IsReparsePoint(path))
                {
                    continue;
                }
                if (DeleteEntry(path))
                {
                    count--;
                    bytes -= size;
                }
            }
        }
        catch
        {
            // pruning is best effort
        }
    }

    private static bool DeleteEntry(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
            foreach (var f in Directory.EnumerateFiles(path))
            {
                if (!IsReparsePoint(f))
                {
                    File.Delete(f);
                }
            }
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, recursive: false);
                return true;
            }
        }
        catch
        {
            // ignore
        }
        return false;
    }
}
