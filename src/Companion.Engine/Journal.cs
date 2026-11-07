using System.Globalization;
using System.Text;
using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Engine.Internal;

namespace Companion.Engine;

/// <summary>
/// JSONL journal (SPEC 6.10): one file per UTC day in RuntimeDir\journal. <see cref="Write"/> only buffers (no disk I/O,
/// so it is safe while the window is displaced); <see cref="Flush"/> appends. Field values are linted: anything that is
/// not a bool, a number or a code string is written as "&lt;redacted&gt;". Never throws into control flow; when the folder
/// is refused (reparse point, missing RuntimeDir) or a cap is hit, records are dropped and counted. Thread-safe.
/// </summary>
public sealed class Journal : IJournal
{
    internal const int MaxBuffered = 20_000;
    internal const long MaxFileBytes = 32L * 1024 * 1024;
    internal const int MaxDayFiles = 60;

    private readonly CompanionPaths _paths;
    private readonly IClock _clock;
    private readonly object _gate = new();
    // held around a whole flush (snapshot and append) so flushes from the heartbeat, the engine and Stop stay in order;
    // Write only takes _gate and never waits on the disk
    private readonly object _flushGate = new();
    private readonly Queue<JournalRecord> _buffer = new();
    private long _dropped;
    private long _redacted;

    /// <summary>Creates the journal (no I/O).</summary>
    public Journal(CompanionPaths paths, IClock clock)
    {
        _paths = paths;
        _clock = clock;
    }

    /// <summary>Records dropped so far (buffer overflow, refused folder, size cap).</summary>
    internal long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Values replaced by the redaction marker so far.</summary>
    internal long RedactedValues => Interlocked.Read(ref _redacted);

    /// <inheritdoc />
    public void Write(JournalRecord record)
    {
        try
        {
            if (record is null)
            {
                return;
            }
            lock (_gate)
            {
                if (_buffer.Count >= MaxBuffered)
                {
                    _buffer.Dequeue();
                    Interlocked.Increment(ref _dropped);
                }
                _buffer.Enqueue(record);
            }
        }
        catch
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <inheritdoc />
    public void Flush()
    {
        lock (_flushGate)
        {
            FlushCore();
        }
    }

    private void FlushCore()
    {
        JournalRecord[] batch;
        long droppedBefore;
        lock (_gate)
        {
            batch = _buffer.ToArray();
            _buffer.Clear();
            droppedBefore = Interlocked.Read(ref _dropped);
        }
        try
        {
            if (batch.Length == 0 && droppedBefore == 0)
            {
                return;
            }
            var dir = _paths.JournalDir;
            if (!SafeFiles.EnsureDirectory(_paths.RuntimeDir, dir))
            {
                Interlocked.Add(ref _dropped, batch.Length);
                return;
            }
            var lines = new List<(string Day, string Line)>(batch.Length + 1);
            foreach (var r in batch)
            {
                lines.Add((DayOf(r.Utc), Serialize(r, out var red)));
                if (red > 0)
                {
                    Interlocked.Add(ref _redacted, red);
                }
            }
            if (droppedBefore > 0)
            {
                var now = _clock.UtcNow;
                var rec = new JournalRecord(now, JournalKind.Log, null, null, null,
                    new Dictionary<string, object?> { ["code"] = "journal.dropped", ["count"] = droppedBefore });
                lines.Add((DayOf(now), Serialize(rec, out _)));
                Interlocked.Add(ref _dropped, -droppedBefore);
            }
            foreach (var group in lines.GroupBy(l => l.Day, StringComparer.Ordinal))
            {
                AppendDay(dir, group.Key, group.Select(g => g.Line).ToList());
            }
            PruneDays(dir);
        }
        catch
        {
            Interlocked.Add(ref _dropped, batch.Length);
        }
    }

    private void AppendDay(string dir, string day, List<string> lines)
    {
        var path = Path.Combine(dir, day + ".jsonl");
        if (SafeFiles.IsReparsePoint(path) || SafeFiles.IsReparsePoint(dir))
        {
            Interlocked.Add(ref _dropped, lines.Count);
            return;
        }
        long existing = File.Exists(path) ? new FileInfo(path).Length : 0;
        var sb = new StringBuilder();
        var written = 0;
        foreach (var line in lines)
        {
            var bytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (existing + bytes > MaxFileBytes)
            {
                Interlocked.Add(ref _dropped, lines.Count - written);
                break;
            }
            existing += bytes;
            sb.Append(line).Append('\n');
            written++;
        }
        if (sb.Length == 0)
        {
            return;
        }
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var data = Encoding.UTF8.GetBytes(sb.ToString());
        fs.Write(data, 0, data.Length);
        fs.Flush(true);
    }

    private static void PruneDays(string dir)
    {
        try
        {
            var files = Directory.EnumerateFiles(dir, "*.jsonl")
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToList();
            for (var i = 0; i < files.Count - MaxDayFiles; i++)
            {
                if (!SafeFiles.IsReparsePoint(files[i]))
                {
                    File.Delete(files[i]);
                }
            }
        }
        catch
        {
            // best effort
        }
    }

    private static string DayOf(DateTimeOffset utc) => utc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>One JSONL line (camelCase, codes only); <paramref name="redacted"/> counts replaced values.</summary>
    internal static string Serialize(JournalRecord record, out int redacted)
    {
        redacted = 0;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteString("utc", record.Utc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            w.WriteString("kind", JsonNamingPolicy.CamelCase.ConvertName(record.Kind.ToString()));
            WriteCode(w, "jobId", record.JobId, ref redacted);
            WriteCode(w, "procedureId", record.ProcedureId, ref redacted);
            WriteCode(w, "stepId", record.StepId, ref redacted);
            w.WriteStartObject("fields");
            if (record.Fields is not null)
            {
                foreach (var (key, value) in record.Fields)
                {
                    if (!JournalSchema.IsKey(key))
                    {
                        redacted++;
                        continue;
                    }
                    var v = JournalSchema.Sanitize(value, out var red);
                    if (red)
                    {
                        redacted++;
                    }
                    switch (v)
                    {
                        case null:
                            w.WriteNull(key);
                            break;
                        case bool b:
                            w.WriteBoolean(key, b);
                            break;
                        case long l:
                            w.WriteNumber(key, l);
                            break;
                        case double d:
                            w.WriteNumber(key, d);
                            break;
                        default:
                            w.WriteString(key, v.ToString());
                            break;
                    }
                }
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteCode(Utf8JsonWriter w, string name, string? value, ref int redacted)
    {
        if (value is null)
        {
            return;
        }
        if (JournalSchema.IsCode(value))
        {
            w.WriteString(name, value);
        }
        else
        {
            redacted++;
            w.WriteString(name, JournalSchema.Redacted);
        }
    }
}
