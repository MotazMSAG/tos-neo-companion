namespace Companion.App.Hosting;

/// <summary>
/// Runtime-file writes of the host (SEC-2, SPEC 10.1): the target folder and file must not be reparse points, the
/// folder must lie inside RuntimeDir, and the content is written to a CreateNew temp file in the same folder, flushed to
/// disk, then renamed over the target (atomic on the same volume).
/// </summary>
internal static class SafeFile
{
    /// <summary>True if the path exists and is a reparse point (junction, symlink, mount point).</summary>
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
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>True if <paramref name="path"/> is <paramref name="root"/> or lies under it (after normalisation).</summary>
    public static bool IsUnder(string root, string path)
    {
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks every existing folder from <paramref name="root"/> down to <paramref name="dir"/> for reparse points and
    /// creates the missing ones plainly (RuntimeDir itself is created by <see cref="RuntimeDirectory"/> with its ACL;
    /// children inherit it). Returns false if any level is a reparse point or the folder is outside the root.
    /// </summary>
    public static bool EnsureSafeDirectory(string root, string dir)
    {
        if (!IsUnder(root, dir))
        {
            return false;
        }

        var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var chain = new List<string>();
        for (var d = full; d is not null && d.Length >= rootFull.Length; d = Path.GetDirectoryName(d))
        {
            chain.Add(d);
            if (string.Equals(d, rootFull, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        chain.Reverse();
        foreach (var d in chain)
        {
            if (IsReparsePoint(d))
            {
                return false;
            }

            if (!Directory.Exists(d))
            {
                Directory.CreateDirectory(d);
            }
        }

        return true;
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="target"/> atomically. Throws <see cref="IOException"/> when the
    /// folder or the target is a reparse point or lies outside <paramref name="root"/>.
    /// </summary>
    public static void WriteAtomic(string root, string target, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(target)) ?? throw new IOException("write.noFolder");
        if (!EnsureSafeDirectory(root, dir))
        {
            throw new IOException("write.unsafeFolder");
        }

        if (IsReparsePoint(target))
        {
            throw new IOException("write.reparseTarget");
        }

        var temp = Path.Combine(dir, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
                // best effort cleanup of our own temp file
            }
            catch (UnauthorizedAccessException)
            {
                // best effort cleanup of our own temp file
            }
        }
    }

    /// <summary>Reads a runtime file (at most <paramref name="maxBytes"/>); null if missing, a reparse point or too large.</summary>
    public static byte[]? ReadSmall(string path, int maxBytes)
    {
        try
        {
            if (!File.Exists(path) || IsReparsePoint(path))
            {
                return null;
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length > maxBytes)
            {
                return null;
            }

            var buffer = new byte[fs.Length];
            fs.ReadExactly(buffer);
            return buffer;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
