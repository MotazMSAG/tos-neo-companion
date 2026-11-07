using System.Text;

namespace Companion.Logs;

/// <summary>A line inside a scan buffer: start index and length without the CR/LF terminator.</summary>
/// <param name="Start">Index of the first byte in the buffer.</param>
/// <param name="Length">Length without '\r\n' / '\n'.</param>
internal readonly record struct LineRange(int Start, int Length);

/// <summary>
/// One complete log entry (SPEC 6.1): a timestamped first line, the untimestamped lines that follow it, ended by a
/// blank line (included) or by the next timestamped line (excluded). Holds a reference to the scan buffer; decode
/// before the buffer is reused.
/// </summary>
internal sealed class RawEntry
{
    private readonly byte[] _buffer;
    private readonly List<LineRange>? _continuations;

    internal RawEntry(byte[] buffer, long start, long end, LineRange first, int messageStart, List<LineRange>? continuations)
    {
        _buffer = buffer;
        Start = start;
        End = end;
        First = first;
        MessageStart = messageStart;
        _continuations = continuations;
    }

    /// <summary>Absolute byte offset of the entry's first byte.</summary>
    internal long Start { get; }

    /// <summary>Absolute byte offset just after the entry (after its blank terminator, or at the next timestamped line).</summary>
    internal long End { get; }

    /// <summary>The timestamped first line.</summary>
    internal LineRange First { get; }

    /// <summary>Index (inside <see cref="First"/>) where the message part starts, after 'ts\tLevel\t'.</summary>
    internal int MessageStart { get; }

    /// <summary>Number of untimestamped lines inside the entry.</summary>
    internal int ContinuationCount => _continuations?.Count ?? 0;

    /// <summary>The game timestamp (local time as written), or null when the digits do not form a valid date.</summary>
    internal DateTime? Timestamp => EntryScanner.ParseTimestamp(_buffer.AsSpan(First.Start, First.Length));

    /// <summary>Decoded first line (UTF-8, no CR/LF).</summary>
    internal string FirstLine => Encoding.UTF8.GetString(_buffer, First.Start, First.Length);

    /// <summary>Decoded message part of the first line.</summary>
    internal string Message => Encoding.UTF8.GetString(_buffer, First.Start + MessageStart, First.Length - MessageStart);

    /// <summary>True if the message part starts with the ASCII <paramref name="prefix"/> (no decoding).</summary>
    internal bool MessageStartsWith(ReadOnlySpan<byte> prefix) =>
        _buffer.AsSpan(First.Start + MessageStart, First.Length - MessageStart).StartsWith(prefix);

    /// <summary>Decoded untimestamped lines that start with '|' + TAB (the only continuation lines ever used).</summary>
    internal IEnumerable<string> PipeLines()
    {
        if (_continuations is null)
        {
            yield break;
        }

        foreach (var c in _continuations)
        {
            if (c.Length >= 2 && _buffer[c.Start] == (byte)'|' && _buffer[c.Start + 1] == (byte)'\t')
            {
                yield return Encoding.UTF8.GetString(_buffer, c.Start, c.Length);
            }
        }
    }
}

/// <summary>Result of scanning a buffer.</summary>
/// <param name="Entries">Complete entries in file order.</param>
/// <param name="NextOffset">Absolute offset where the next read must start (just after the last complete entry, or past orphan lines).</param>
internal sealed record ScanResult(List<RawEntry> Entries, long NextOffset);

/// <summary>
/// Splits raw log bytes into complete entries without decoding anything but what is asked for later. Only lines ending
/// in LF are considered, so a multi-byte UTF-8 sequence or a CRLF pair is never split. Untimestamped lines outside an
/// entry (and blank lines) are consumed and dropped. An entry still open at the end of the buffer is incomplete: the
/// next read restarts at its first byte.
/// </summary>
internal static class EntryScanner
{
    /// <summary>
    /// Scans <paramref name="buffer"/>[0..<paramref name="count"/>) that starts at absolute offset <paramref name="baseOffset"/>.
    /// When <paramref name="capped"/> is true (the read hit the per-poll byte cap) and no progress at all was possible,
    /// the scanner skips to the last complete line so a giant multi-line dump can never stall the reader.
    /// </summary>
    internal static ScanResult Scan(byte[] buffer, int count, long baseOffset, bool capped)
    {
        var entries = new List<RawEntry>();
        var pos = 0;
        var consumed = 0;
        var lastLineEnd = 0;
        var open = false;
        var openStart = 0;
        var openFirst = default(LineRange);
        var openMsg = 0;
        List<LineRange>? openCont = null;

        while (pos < count)
        {
            var lf = Array.IndexOf(buffer, (byte)'\n', pos, count - pos);
            if (lf < 0)
            {
                break;
            }

            var lineStart = pos;
            var next = lf + 1;
            var contentEnd = lf;
            if (contentEnd > lineStart && buffer[contentEnd - 1] == (byte)'\r')
            {
                contentEnd--;
            }

            var line = new LineRange(lineStart, contentEnd - lineStart);
            var msgStart = TimestampedMessageStart(buffer.AsSpan(lineStart, line.Length));
            if (msgStart > 0)
            {
                if (open)
                {
                    entries.Add(new RawEntry(buffer, baseOffset + openStart, baseOffset + lineStart, openFirst, openMsg, openCont));
                }

                open = true;
                openStart = lineStart;
                openFirst = line;
                openMsg = msgStart;
                openCont = null;
                consumed = lineStart;
            }
            else if (IsBlank(buffer.AsSpan(lineStart, line.Length)))
            {
                if (open)
                {
                    entries.Add(new RawEntry(buffer, baseOffset + openStart, baseOffset + next, openFirst, openMsg, openCont));
                    open = false;
                    openCont = null;
                }

                consumed = next;
            }
            else if (open)
            {
                (openCont ??= []).Add(line);
            }
            else
            {
                consumed = next;
            }

            lastLineEnd = next;
            pos = next;
        }

        if (!open)
        {
            consumed = Math.Max(consumed, lastLineEnd);
        }

        if (capped && entries.Count == 0 && consumed == 0)
        {
            // Nothing completed inside a full-size read: drop the giant entry's bytes up to the last complete line.
            consumed = lastLineEnd > 0 ? lastLineEnd : count;
        }

        return new ScanResult(entries, baseOffset + consumed);
    }

    /// <summary>
    /// Returns the index of the message part if the line starts with 'yyyy-MM-dd HH:mm:ss.fff\t[A-Za-z]+\t', else 0.
    /// </summary>
    internal static int TimestampedMessageStart(ReadOnlySpan<byte> line)
    {
        // 0123456789012345678901234
        // 2026-10-13 23:03:42.715\tLog\t
        if (line.Length < 26)
        {
            return 0;
        }

        for (var i = 0; i < 23; i++)
        {
            var b = line[i];
            var ok = i switch
            {
                4 or 7 => b == (byte)'-',
                10 => b == (byte)' ',
                13 or 16 => b == (byte)':',
                19 => b == (byte)'.',
                _ => b is >= (byte)'0' and <= (byte)'9',
            };
            if (!ok)
            {
                return 0;
            }
        }

        if (line[23] != (byte)'\t')
        {
            return 0;
        }

        var j = 24;
        while (j < line.Length && line[j] is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z'))
        {
            j++;
        }

        if (j == 24 || j >= line.Length || line[j] != (byte)'\t')
        {
            return 0;
        }

        return j + 1;
    }

    /// <summary>Parses the 23-byte timestamp at the start of a timestamped line; null if the values are out of range.</summary>
    internal static DateTime? ParseTimestamp(ReadOnlySpan<byte> line)
    {
        if (line.Length < 23)
        {
            return null;
        }

        static int D(ReadOnlySpan<byte> s, int start, int len)
        {
            var v = 0;
            for (var i = 0; i < len; i++)
            {
                v = (v * 10) + (s[start + i] - (byte)'0');
            }

            return v;
        }

        int year = D(line, 0, 4), month = D(line, 5, 2), day = D(line, 8, 2);
        int hour = D(line, 11, 2), minute = D(line, 14, 2), second = D(line, 17, 2), ms = D(line, 20, 3);
        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        return new DateTime(year, month, day, hour, minute, second, ms, DateTimeKind.Unspecified);
    }

    private static bool IsBlank(ReadOnlySpan<byte> line)
    {
        foreach (var b in line)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r'))
            {
                return false;
            }
        }

        return true;
    }
}
