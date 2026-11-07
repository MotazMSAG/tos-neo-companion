using System.Text.Json;
using System.Text.Json.Serialization;
using Companion.Contracts.Logs;

namespace Companion.Dailies;

/// <summary>
/// Thread-safe front of <see cref="DailyTracker"/> for the host (DAILIES_MODULE.md, host wiring). The host:
/// <list type="number">
/// <item>creates it with the catalog and the saved state (if any);</item>
/// <item>replays today's part of the game log (<see cref="ReplayFrom"/>) on a background thread and hands the events to
/// <see cref="CompleteReplay"/>; live events fed meanwhile (<see cref="Feed"/>) wait in a buffer, so nothing is counted
/// out of order or twice;</item>
/// <item>feeds every poll's events, finished jobs and the user's ticks;</item>
/// <item>saves <see cref="TakeStateToSave"/> when it returns bytes (at most once a second).</item>
/// </list>
/// </summary>
public sealed class DailyService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object _gate = new();
    private readonly DailyTracker _tracker;
    private readonly Func<DateTime> _nowLocal;
    private List<LogEvent>? _buffer = [];
    private long _savedVersion;
    private DailyPrefs _prefs = new();
    private bool _prefsDirty;

    /// <summary>
    /// Creates the service. <paramref name="saved"/> is the state bytes from disk and <paramref name="prefs"/> the
    /// "Edit my list" choices (null or damaged = start empty / with the defaults).
    /// </summary>
    public DailyService(DailyCatalog catalog, byte[]? saved, Func<DateTime> nowLocal, byte[]? prefs = null)
    {
        _tracker = new DailyTracker(catalog);
        _nowLocal = nowLocal;
        if (prefs is not null && DeserializePrefs(prefs) is { } p)
        {
            _prefs = p;
            _tracker.SetCustomItems(p.Custom);
        }

        if (saved is not null && Deserialize(saved) is { } state)
        {
            if (state.Catalog != catalog.Fingerprint)
            {
                // Counted with other rules: keep the history before today's replay and the user's ticks, and let the
                // replay count today again with the new rules. Stored counts stay: each knows when it was last right, and
                // the replay only applies what came after (2026-11-05: dropping them restarted the special train at 0
                // and showed 3/6 for the game's 0/6). Cards rebuild from the replay. Counts are forgotten in CompleteReplay,
                // and only from where the replayed log begins: older log files are gone (Honor War's 12 rounds of
                // Tuesday 01:20-01:50 lived only in log.bak2 by Thursday).
                _recountFrom = _tracker.ReplayFrom(nowLocal());
                state = state with { Watermark = DateTime.MinValue, Producers = null };
                Recounted = true;
            }

            _tracker.Import(state);
            StateProblem = null;
        }
        else
        {
            StateProblem = saved is null ? null : "dailies.stateUnreadable";
        }

        _savedVersion = _tracker.Version;
    }

    /// <summary>'dailies.stateUnreadable' when the saved state could not be read (the list started empty).</summary>
    public string? StateProblem { get; }

    /// <summary>True when the saved counts were made with another data file and today is counted again from the log.</summary>
    public bool Recounted { get; }

    /// <summary>True until <see cref="CompleteReplay"/> ran.</summary>
    public bool Replaying
    {
        get
        {
            lock (_gate)
            {
                return _buffer is not null;
            }
        }
    }

    /// <summary>Where the start-up replay of the log begins (log time).</summary>
    public DateTime ReplayFrom
    {
        get
        {
            lock (_gate)
            {
                return _tracker.ReplayFrom(_nowLocal());
            }
        }
    }

    /// <summary>Feeds the replayed events (log order), then everything that arrived live meanwhile.</summary>
    public void CompleteReplay(IEnumerable<LogEvent> replayed)
    {
        ArgumentNullException.ThrowIfNull(replayed);
        var events = replayed as IReadOnlyList<LogEvent> ?? [.. replayed];
        lock (_gate)
        {
            if (_recountFrom is { } recount)
            {
                // nothing replayed (no log): keep every saved count
                var logBegins = events.Count > 0 ? events.Min(e => e.Timestamp) : DateTime.MaxValue;
                _tracker.ForgetCountsFrom(logBegins > recount ? logBegins : recount);
                _recountFrom = null;
            }

            foreach (var e in events)
            {
                _tracker.Feed(e);
            }

            foreach (var e in _buffer ?? [])
            {
                _tracker.Feed(e);
            }

            _buffer = null;
        }
    }

    /// <summary>Feeds one poll's events (buffered while the replay runs).</summary>
    public void Feed(IReadOnlyList<LogEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        lock (_gate)
        {
            if (_buffer is not null)
            {
                _buffer.AddRange(events);
                return;
            }

            foreach (var e in events)
            {
                _tracker.Feed(e);
            }
        }
    }

    /// <summary>A companion job finished.</summary>
    public void OnJob(string job, string state)
    {
        lock (_gate)
        {
            _tracker.OnJob(job, state, _nowLocal());
        }
    }

    /// <summary>The user ticked or unticked an item; false for an unknown item.</summary>
    public bool SetDone(string itemId, bool done)
    {
        lock (_gate)
        {
            return _tracker.SetDone(itemId, done, _nowLocal());
        }
    }

    /// <summary>The user set a stack's stored count; false for an item without one.</summary>
    public bool SetStack(string itemId, int value)
    {
        lock (_gate)
        {
            return _tracker.SetStack(itemId, value, _nowLocal());
        }
    }

    /// <summary>
    /// What to tell the user since the last call (an event opening, a producer ready), with the user's names applied.
    /// The first call only starts the clock: nothing that happened before the companion started is announced.
    /// </summary>
    public IReadOnlyList<(DailyAlert Alert, string Text)> TakeAlerts()
    {
        lock (_gate)
        {
            var now = _nowLocal();
            if (_buffer is not null || _alertedUpTo is not { } from)
            {
                _alertedUpTo ??= now;
                return [];
            }

            _alertedUpTo = now;
            var snap = _tracker.Snapshot(now);
            return [.. _tracker.Alerts(from, now).Select(a => (a, AlertText(a, snap)))];
        }
    }

    private DateTime? _alertedUpTo;

    /// <summary>Set when the saved counts were made with other rules: where the replay starts counting again.</summary>
    private DateTime? _recountFrom;

    private string AlertText(DailyAlert a, DailySnapshot snap)
    {
        var name = snap.Items.FirstOrDefault(i => i.Id == a.ItemId) is { } v ? _prefs.NameOf(v)
            : snap.Producers?.FirstOrDefault(p => p.Id == a.ItemId)?.Name ?? a.ItemId;
        if (a.Kind == DailyAlertKind.ProducerReady)
        {
            return name + ": your items should be ready.";
        }

        var minutes = a.Until is { } u ? Math.Max(1, (int)Math.Round((u - snap.AsOf).TotalMinutes)) : 0;
        var text = name + " is open now" + (minutes > 0 ? $" ({minutes} min)." : ".");
        if (a.Last)
        {
            text += a.Left > 0 ? $" Last one today: {a.Left} left." : " Last one today.";
        }

        return text;
    }

    /// <summary>The list now.</summary>
    public DailySnapshot Snapshot()
    {
        lock (_gate)
        {
            return _tracker.Snapshot(_nowLocal());
        }
    }

    /// <summary>The user's list choices.</summary>
    public DailyPrefs Prefs
    {
        get
        {
            lock (_gate)
            {
                return _prefs;
            }
        }
    }

    /// <summary>Applies new list choices ("Edit my list" Save); they are written at the next save.</summary>
    public void SetPrefs(DailyPrefs prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        lock (_gate)
        {
            _prefs = prefs;
            _prefsDirty = true;
            _tracker.SetCustomItems(prefs.Custom);
        }
    }

    /// <summary>The prefs bytes to write when they changed since the last call; null otherwise.</summary>
    public byte[]? TakePrefsToSave()
    {
        lock (_gate)
        {
            if (!_prefsDirty)
            {
                return null;
            }

            _prefsDirty = false;
            return JsonSerializer.SerializeToUtf8Bytes(_prefs, Json);
        }
    }

    /// <summary>Reads saved prefs bytes; null when they are not version-1 prefs.</summary>
    public static DailyPrefs? DeserializePrefs(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            var p = JsonSerializer.Deserialize<DailyPrefs>(bytes, Json);
            return p is { SchemaVersion: 1 } ? p : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A small readable copy of the list now (RuntimeDir\dailies\today.json, for "Companion.Cli dailies"): the game day,
    /// the next reset and every shown item with its name, count and state. Names and counts only.
    /// </summary>
    public byte[] TodayFile()
    {
        DailySnapshot s;
        DailyPrefs p;
        lock (_gate)
        {
            s = _tracker.Snapshot(_nowLocal());
            p = _prefs;
        }

        var items = s.Items.Where(i => p.IsShown(i) && i.Today && i.Kind != DailyKind.Producer).Select(i => new
        {
            id = i.Id,
            name = p.NameOf(i),
            section = i.Section,
            kind = i.Kind == DailyKind.Event ? "event" : "task",
            count = i.Count,
            perDay = i.PerDay,
            done = i.Done,
            missed = i.Missed,
            stack = i.Stack,
            source = i.Source.ToString().ToLowerInvariant(),
            at = i.LastAt?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            open = i.Open,
        });
        var cards = (s.Producers ?? []).Select(c => new
        {
            id = c.Id,
            name = c.Name,
            state = c.State.ToString().ToLowerInvariant(),
            until = c.Until?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        });
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            asOf = s.AsOf.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            gameDay = s.GameDay.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            nextReset = s.NextDailyReset.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            replaying = _buffer is not null,
            items,
            cards,
        });
    }

    /// <summary>The state bytes to write when something changed since the last call; null otherwise.</summary>
    public byte[]? TakeStateToSave()
    {
        lock (_gate)
        {
            if (_tracker.Version == _savedVersion || _buffer is not null)
            {
                return null;
            }

            _savedVersion = _tracker.Version;
            return JsonSerializer.SerializeToUtf8Bytes(_tracker.Export(), Json);
        }
    }

    /// <summary>Reads saved state bytes; null when they are not a version-1 state.</summary>
    public static DailyState? Deserialize(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            var s = JsonSerializer.Deserialize<DailyState>(bytes, Json);
            return s is { SchemaVersion: 1 } ? s : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
