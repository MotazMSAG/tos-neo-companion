using System.Buffers;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;

namespace Companion.Logs;

/// <summary>
/// Reads a whole game log file once, through the same scanner and parser as <see cref="LogTracker"/>, and returns its
/// events (2026-11-04, the daily checklist: today's part of the log is replayed when the companion starts, because the
/// tracker's bootstrap parses only the last 2 MB). The file is opened shared (the game keeps writing and may rename it);
/// login lines are dropped (no hasher). Read-only; nothing is kept but the returned events.
/// </summary>
public static class LogReplay
{
    private const int ChunkBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The events of <paramref name="path"/> whose timestamp is at or after <paramref name="fromLocal"/> (log time),
    /// in file order. Missing or unreadable files give an empty list.
    /// </summary>
    public static IReadOnlyList<LogEvent> Read(string path, DateTime fromLocal, CancellationToken ct = default)
    {
        var events = new List<LogEvent>();
        FileStream fs;
        try
        {
            fs = LogFileAccess.OpenShared(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return events;
        }

        using (fs)
        {
            var length = fs.Length;
            var info = LogFileAccess.GetHandleInfo(fs);
            var id = new LogFileIdentity(info.CreationUtc, info.FileIndex, "replay");
            var parser = new LogEntryParser(NoHasher.Instance);
            var offset = 0L;
            while (offset < length)
            {
                ct.ThrowIfCancellationRequested();
                var want = (int)Math.Min(ChunkBytes, length - offset);
                var buffer = ArrayPool<byte>.Shared.Rent(want);
                try
                {
                    var n = LogFileAccess.ReadAt(fs, offset, buffer, want);
                    if (n <= 0)
                    {
                        break;
                    }

                    var scan = EntryScanner.Scan(buffer, n, offset, capped: n == ChunkBytes);
                    foreach (var entry in scan.Entries)
                    {
                        if (entry.Timestamp is { } t && t >= fromLocal && parser.Parse(entry, id) is { } e)
                        {
                            events.Add(e);
                        }
                    }

                    if (scan.NextOffset <= offset)
                    {
                        break;
                    }

                    offset = scan.NextOffset;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        return events;
    }

    private sealed class NoHasher : ICharacterKeyHasher
    {
        internal static readonly NoHasher Instance = new();

        public CharacterKey Hash(string rawRoleToken) => throw new InvalidOperationException("replay keeps no logins");
    }
}
