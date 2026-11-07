using System.Globalization;
using System.Text.Json;

namespace Companion.App.Tray;

/// <summary>
/// What the companion did in a time span, counted from its own journal (codes and numbers only; no chat, no names of
/// people). Shared by the "while you were away" summary (roadmap A) and the stats page (roadmap C), so both count the
/// same things the same way.
/// </summary>
internal sealed class JournalStats
{
    /// <summary>Crafts per item id (the game's craft ticks).</summary>
    public Dictionary<long, long> Made { get; } = [];

    /// <summary>Gathering trips that gathered something.</summary>
    public int Trips { get; set; }

    /// <summary>Items queued on gathering trips per item id (the count the game accepted).</summary>
    public Dictionary<long, long> Gathered { get; } = [];

    /// <summary>Pauses of a goal (a press could not happen now; plan.deferred).</summary>
    public int Pauses { get; set; }

    /// <summary>Safety stops (HALT).</summary>
    public int SafetyStops { get; set; }

    /// <summary>When the gear notice was seen (local time).</summary>
    public List<DateTime> GearNotices { get; } = [];

    /// <summary>Jobs that ended: (job name, end code, end state).</summary>
    public List<(string Job, string Code, string State)> JobsEnded { get; } = [];

    /// <summary>Jobs that ended Succeeded.</summary>
    public int JobsSucceeded => JobsEnded.Count(j => j.State == "Succeeded");

    private readonly Dictionary<long, long> _pendingAccepted = [];
    private DateTimeOffset? _lastRound;

    /// <summary>Time spent in craft sessions (from the first to the last round of each run of rounds).</summary>
    public TimeSpan CraftingTime { get; set; }

    /// <summary>Everything counted is zero.</summary>
    public bool IsEmpty => Made.Count == 0 && Trips == 0 && Pauses == 0 && SafetyStops == 0 && GearNotices.Count == 0 && JobsEnded.Count == 0;

    /// <summary>Crafts in all.</summary>
    public long MadeTotal => Made.Values.Sum();

    /// <summary>
    /// Counts the journal records from <paramref name="fromUtc"/> (inclusive) to <paramref name="toUtc"/> (exclusive).
    /// Lines that are not JSON records are skipped.
    /// </summary>
    public static JournalStats From(IEnumerable<string> lines, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var s = new JournalStats();
        foreach (var (at, r) in Records(lines))
        {
            if (at >= fromUtc && at < toUtc)
            {
                s.Add(at, r);
            }
        }

        return s;
    }

    /// <summary>The records of <paramref name="lines"/> counted per local day (roadmap C, the stats page), in one pass.</summary>
    public static SortedDictionary<DateTime, JournalStats> ByDay(IEnumerable<string> lines, DateTime firstLocalDay)
    {
        var days = new SortedDictionary<DateTime, JournalStats>();
        foreach (var (at, r) in Records(lines))
        {
            var day = at.ToLocalTime().Date;
            if (day < firstLocalDay)
            {
                continue;
            }

            if (!days.TryGetValue(day, out var s))
            {
                days[day] = s = new JournalStats();
            }

            s.Add(at, r);
        }

        return days;
    }

    /// <summary>The parsed records (time and root element); lines that are not JSON records are skipped.</summary>
    private static IEnumerable<(DateTimeOffset At, JsonElement Record)> Records(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(line);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue; // a torn or foreign line
            }

            if (root.TryGetProperty("utc", out var u) && u.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(u.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                && root.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object)
            {
                yield return (at, root);
            }
        }
    }

    private void Add(DateTimeOffset at, JsonElement r)
    {
        var f = r.GetProperty("fields");
        var kind = r.TryGetProperty("kind", out var k) ? k.GetString() : null;
        var ev = f.TryGetProperty("event", out var e) ? e.GetString() : null;
        if (kind == "halt")
        {
            SafetyStops++;
            return;
        }

        if (kind == "jobFinished")
        {
            JobsEnded.Add((Str(f, "job") ?? "?", Str(f, "code") ?? "?", Str(f, "state") ?? "?"));
            return;
        }

        switch (ev)
        {
            case "craftLoop.round" when Num(f, "produced") is > 0 and var produced && Num(f, "itemId") is { } item:
                Made[item] = Made.GetValueOrDefault(item) + produced;
                if (_lastRound is { } previous && at - previous < TimeSpan.FromMinutes(10))
                {
                    CraftingTime += at - previous;
                }

                _lastRound = at;
                break;
            case "craftLoop.round":
                _lastRound = at;
                break;
            case "gather.queued" when Num(f, "itemId") is { } item && Num(f, "qty") is { } qty:
                _pendingAccepted[item] = qty;
                break;
            case "gather.clamped" when Num(f, "itemId") is { } item && Num(f, "accepted") is { } accepted:
                _pendingAccepted[item] = accepted; // the game took this many of what was typed
                break;
            case "plan.gather" or "gatherAll.trip" when Num(f, "gathered") > 0:
                Trips++;
                foreach (var (item, qty) in _pendingAccepted)
                {
                    Gathered[item] = Gathered.GetValueOrDefault(item) + qty;
                }

                _pendingAccepted.Clear();
                break;
            case "plan.gather" or "gatherAll.trip":
                _pendingAccepted.Clear();
                break;
            case "plan.deferred":
                Pauses++;
                break;
            case "gear.notice":
                GearNotices.Add(at.ToLocalTime().DateTime);
                break;
        }
    }

    /// <summary>The journal lines of the local days touching [from, to): the day files that can hold them.</summary>
    public static IEnumerable<string> Lines(string journalDir, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (!Directory.Exists(journalDir))
        {
            yield break;
        }

        var first = fromUtc.UtcDateTime.Date.AddDays(-1);
        var last = toUtc.UtcDateTime.Date.AddDays(1);
        foreach (var file in new DirectoryInfo(journalDir).GetFiles("*.jsonl").OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                || day < first || day > last)
            {
                continue;
            }

            List<string> content;
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                content = [];
                while (reader.ReadLine() is { } line)
                {
                    content.Add(line);
                }
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var line in content)
            {
                yield return line;
            }
        }
    }

    /// <summary>
    /// One plain paragraph (roadmap A, e.g. "Made 120 Sauteed Clams, 3 gathering trips (thyme 200, toothclam 121), 2
    /// pauses, gear notice at 19:10"); null when nothing happened.
    /// </summary>
    public string? Summary(Func<long, string?> names)
    {
        if (IsEmpty)
        {
            return null;
        }

        var parts = new List<string>();
        if (Made.Count > 0)
        {
            parts.Add("made " + string.Join(", ", Made.OrderByDescending(kv => kv.Value).Select(kv => kv.Value.ToString(CultureInfo.InvariantCulture) + " " + (names(kv.Key) ?? "items"))));
        }

        if (Trips > 0)
        {
            var top = Gathered.OrderByDescending(kv => kv.Value).Take(4).Select(kv => (names(kv.Key) ?? "a material") + " " + kv.Value.ToString(CultureInfo.InvariantCulture)).ToList();
            parts.Add(Trips.ToString(CultureInfo.InvariantCulture) + " gathering trip" + (Trips == 1 ? string.Empty : "s")
                + (top.Count > 0 ? " (" + string.Join(", ", top) + (Gathered.Count > 4 ? ", ..." : string.Empty) + ")" : string.Empty));
        }

        if (Pauses > 0)
        {
            parts.Add(Pauses.ToString(CultureInfo.InvariantCulture) + " pause" + (Pauses == 1 ? string.Empty : "s"));
        }

        if (GearNotices.Count > 0)
        {
            parts.Add("gear worn out at " + string.Join(", ", GearNotices.Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture))));
        }

        if (SafetyStops > 0)
        {
            parts.Add(SafetyStops.ToString(CultureInfo.InvariantCulture) + " safety stop" + (SafetyStops == 1 ? string.Empty : "s"));
        }

        if (JobsEnded.Count > 0)
        {
            var last = JobsEnded[^1];
            parts.Add("last job: " + last.Job + " (" + last.Code + ")");
        }

        var text = string.Join(", ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..] + ".";
    }

    private static string? Str(JsonElement f, string key) => f.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Num(JsonElement f, string key) => f.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
