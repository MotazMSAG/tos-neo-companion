using System.Security.Cryptography;
using Companion.Contracts.Logs;

namespace Companion.Logs;

/// <summary>
/// Read-only access to the game log (SPEC 6.1). The file is opened per operation with FileShare.ReadWrite|Delete (so
/// the game can keep writing and can rename the file at start-up while we hold it) and closed immediately after.
/// </summary>
internal static class LogFileAccess
{
    /// <summary>Longest first line hashed for the identity; a longer first line is hashed on this prefix.</summary>
    internal const int MaxHeadBytes = 64 * 1024;

    /// <summary>Opens the log for reading without buffering and without blocking the writer, renames or deletes.</summary>
    internal static FileStream OpenShared(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
        BufferSize = 0,
        Options = FileOptions.None,
    });

    /// <summary>Creation time (UTC) and volume file index of the open handle (GetFileInformationByHandle).</summary>
    internal static (DateTime CreationUtc, ulong FileIndex) GetHandleInfo(FileStream fs)
    {
        if (NativeMethods.GetFileInformationByHandle(fs.SafeFileHandle, out var info))
        {
            return (DateTime.FromFileTimeUtc(((long)info.CreationTimeHigh << 32) | info.CreationTimeLow),
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        }

        // Should not happen on NTFS; the head fingerprint and the length check still detect a new generation.
        return (DateTime.MinValue, 0);
    }

    /// <summary>
    /// Hex SHA-256 of the first line (without CR/LF), or null while the first line is not complete yet (a brand-new
    /// or empty file). A first line longer than <see cref="MaxHeadBytes"/> is hashed on that prefix.
    /// </summary>
    internal static string? TryHeadFingerprint(FileStream fs, long length)
    {
        // The first line is ~60 bytes: read a small head first, the full 64 KB only if no LF shows up in it.
        var headLength = (int)Math.Min(length, 4096);
        if (headLength <= 0)
        {
            return null;
        }

        var head = new byte[headLength];
        var n = ReadAt(fs, 0, head, headLength);
        var lf = Array.IndexOf(head, (byte)'\n', 0, n);
        if (lf < 0 && length > n && n < MaxHeadBytes)
        {
            headLength = (int)Math.Min(length, MaxHeadBytes);
            head = new byte[headLength];
            n = ReadAt(fs, 0, head, headLength);
            lf = Array.IndexOf(head, (byte)'\n', 0, n);
        }

        int lineLength;
        if (lf >= 0)
        {
            lineLength = lf;
        }
        else if (n >= MaxHeadBytes)
        {
            lineLength = n;
        }
        else
        {
            return null;
        }

        if (lineLength > 0 && head[lineLength - 1] == (byte)'\r')
        {
            lineLength--;
        }

        return Convert.ToHexStringLower(SHA256.HashData(head.AsSpan(0, lineLength)));
    }

    /// <summary>Full identity of the open file, or null while its first line is incomplete.</summary>
    internal static LogFileIdentity? TryGetIdentity(FileStream fs, long length)
    {
        var fp = TryHeadFingerprint(fs, length);
        if (fp is null)
        {
            return null;
        }

        var (created, index) = GetHandleInfo(fs);
        return new LogFileIdentity(created, index, fp);
    }

    /// <summary>Reads up to <paramref name="count"/> bytes at <paramref name="offset"/>; returns the number read (short at EOF).</summary>
    internal static int ReadAt(FileStream fs, long offset, byte[] buffer, int count) => ReadAt(fs, offset, buffer.AsSpan(0, count));

    /// <summary>Fills <paramref name="target"/> from <paramref name="offset"/>; returns the number of bytes read (short at EOF).</summary>
    internal static int ReadAt(FileStream fs, long offset, Span<byte> target)
    {
        var total = 0;
        while (total < target.Length)
        {
            var r = RandomAccess.Read(fs.SafeFileHandle, target[total..], offset + total);
            if (r <= 0)
            {
                break;
            }

            total += r;
        }

        return total;
    }

    /// <summary>Maps an I/O exception to the contract's problem code.</summary>
    internal static LogReadProblem ProblemOf(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => LogReadProblem.FileMissing,
        UnauthorizedAccessException => LogReadProblem.AccessDenied,
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 or 5 => LogReadProblem.AccessDenied,
        _ => LogReadProblem.IoError,
    };
}
