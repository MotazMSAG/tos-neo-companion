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
/// Time zones (2026-11-08): the data file's times are written for one UTC offset ('timesUtcOffset'). The tracker runs
/// on a copy shifted to the PC's clock (<see cref="DailyCatalog.Shifted"/>): by the shift learned from the game log's
/// daily update when one was seen (it also covers other server regions), else by the PC's UTC offset minus the file's.
/// A new shift re-derives the copy and recounts through the same path as a changed data file.
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
    private readonly DailyCatalog _file;
    private readonly Func<DateTime> _nowLocal;
    private readonly Func<DateTime, TimeSpan>? _pcUtcOffset;

    /// <summary>
    /// The log fed since the start-up replay began, in order (dropped once older than a day before the earliest current
    /// period): what a new shift is counted again from. The replay already held this much in memory.
    /// </summary>
    private readonly List<LogEvent> _kept = [];

    private DailyTracker _tracker;
    private List<LogEvent>? _buffer = [];
    private long _savedVersion;
    private DailyPrefs _prefs = new();
    private bool _prefsDirty;
    private (DateTime At, long Offset) _keptLast = (DateTime.MinValue, -1);

    /// <summary>The shift the tracker's catalog was made with.</summary>
    private TimeSpan _shift;

    /// <summary>The shift learned from the log, and the PC's UTC offset at that moment.</summary>
    private (TimeSpan Shift, TimeSpan PcOffset)? _learned;

    /// <summary>A new learned shift waits to be saved.</summary>
    private bool _learnedDirty;

    /// <summary>
    /// Creates the service. <paramref name="saved"/> is the state bytes from disk and <paramref name="prefs"/> the
    /// "Edit my list" choices (null or damaged = start empty / with the defaults). <paramref name="pcUtcOffset"/> gives
    /// the PC's UTC offset at a local time (the host passes <see cref="PcUtcOffset"/>); null means the PC keeps the data
    /// file's own offset (tests), and a shift learned from the log still applies.
    /// </summary>
    public DailyService(DailyCatalog catalog, byte[]? saved, Func<DateTime> nowLocal, byte[]? prefs = null, Func<DateTime, TimeSpan>? pcUtcOffset = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _file = catalog;
        _nowLocal = nowLocal;
        _pcUtcOffset = pcUtcOffset;
        if (prefs is not null && DeserializePrefs(prefs) is { } p)
        {
            _prefs = p;
        }

        var state = saved is null ? null : Deserialize(saved);
        StateProblem = saved is not null && state is null ? "dailies.stateUnreadable" : null;
        if (state is { LearnedShiftMinutes: { } shift, LearnedPcOffsetMinutes: { } pc })
        {
            _learned = (TimeSpan.FromMinutes(shift), TimeSpan.FromMinutes(pc));
        }

        var now = nowLocal();
        _tracker = Build(ShiftAt(now), state, now);
        _savedVersion = _tracker.Version;
    }

    /// <summary>
    /// The production source of the PC's UTC offset at a local time (the Windows time zone, daylight saving included):
    /// the only place in this module that reads the PC's time zone.
    /// </summary>
    public static TimeSpan PcUtcOffset(DateTime local) => TimeZoneInfo.Local.GetUtcOffset(local);

    /// <summary>'dailies.stateUnreadable' when the saved state could not be read (the list started empty).</summary>
    public string? StateProblem { get; }

    /// <summary>
    /// True when the saved counts were made with another data file or another time shift, and today is counted again
    /// from the log.
    /// </summary>
    public bool Recounted => Recounts > 0;

    /// <summary>How many times the counts were re-derived (a changed data file or a new time shift).</summary>
    public int Recounts { get; private set; }

    /// <summary>How far the data file's times are moved to reach the PC's clock.</summary>
    public TimeSpan Shift
    {
        get
        {
            lock (_gate)
            {
                return _shift;
            }
        }
    }

    /// <summary>The shift learned from the game log's daily update (null until one was seen).</summary>
    public TimeSpan? LearnedShift
    {
        get
        {
            lock (_gate)
            {
                return _learned?.Shift;
            }
        }
    }

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

    /// <summary>
    /// Feeds the replayed events (log order), then everything that arrived live meanwhile. The newest daily update among
    /// them sets the learned shift first, so the replay is counted once, with the right times.
    /// </summary>
    public void CompleteReplay(IEnumerable<LogEvent> replayed)
    {
        ArgumentNullException.ThrowIfNull(replayed);
        var events = replayed as IReadOnlyList<LogEvent> ?? [.. replayed];
        lock (_gate)
        {
            var now = _nowLocal();
            List<LogEvent> all = [.. events, .. _buffer ?? []];
            _buffer = null;
            foreach (var r in all.OfType<ResetLogEvent>())
            {
                Learn(r);
            }

            var shift = ShiftAt(now);
            if (shift != _shift)
            {
                _tracker = Build(shift, _tracker.Export(), now);
            }

            Recount(events);
            foreach (var e in all)
            {
                if (Keep(e))
                {
                    _tracker.Feed(e);
                }
            }

            Trim(now);
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

            var now = _nowLocal();
            Follow(now); // the PC's offset may have changed (daylight saving)
            foreach (var e in events)
            {
                if (!Keep(e))
                {
                    continue;
                }

                if (e is ResetLogEvent r && Learn(r) && Follow(now))
                {
                    continue; // the kept log, this event included, was counted again with the new shift
                }

                _tracker.Feed(e);
            }

            Trim(now);
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

    /// <summary>The PC's UTC offset at <paramref name="t"/>, in whole minutes (the file's own without a source).</summary>
    private TimeSpan PcOffsetAt(DateTime t) =>
        _pcUtcOffset is null ? _file.TimesOffset : TimeSpan.FromMinutes(Math.Round(_pcUtcOffset(t).TotalMinutes));

    /// <summary>
    /// The shift to use at <paramref name="now"/>: the learned one, moved along when the PC's offset changed since
    /// (daylight saving; the server's reset stays put in UTC, and the next daily update corrects it if not), else the
    /// PC's offset minus the file's.
    /// </summary>
    private TimeSpan ShiftAt(DateTime now)
    {
        var pc = PcOffsetAt(now);
        return _learned is { } l ? l.Shift + (pc - l.PcOffset) : pc - _file.TimesOffset;
    }

    /// <summary>
    /// Learns the shift from a daily update logged at local time <paramref name="r"/>: its time of day minus the file's
    /// daily reset, rounded to 15 minutes. A time of day does not tell the date, so of the candidates a whole day apart
    /// the one within 12 hours of the PC-offset guess is taken, which keeps each game day's weekday name. One more than
    /// 2 minutes off a quarter hour is ignored. True when the learned value changed.
    /// </summary>
    private bool Learn(ResetLogEvent r)
    {
        if (r.Kind != ResetKind.DailyUpdate)
        {
            return false;
        }

        var pc = PcOffsetAt(r.Timestamp);
        var guess = pc - _file.TimesOffset;
        var seen = r.Timestamp.TimeOfDay - _file.Resets.Daily;
        var shift = TimeSpan.FromMinutes(Math.Round(seen.TotalMinutes / 15, MidpointRounding.AwayFromZero) * 15);
        if ((seen - shift).Duration() > TimeSpan.FromMinutes(2))
        {
            return false; // the server pushes it seconds after a quarter hour; anything else is not a reset moment
        }

        while (shift - guess > TimeSpan.FromHours(12))
        {
            shift -= TimeSpan.FromDays(1);
        }

        while (shift - guess <= TimeSpan.FromHours(-12))
        {
            shift += TimeSpan.FromDays(1);
        }

        if (_learned is { } old && old.PcOffset - old.Shift == pc - shift)
        {
            return false; // the same server time, seen under the same or another PC offset
        }

        _learned = (shift, pc);
        _learnedDirty = true;
        return true;
    }

    /// <summary>
    /// A tracker for <paramref name="shift"/> loaded with <paramref name="state"/>. A state counted with another data file
    /// or another shift (the fingerprint names both) is recounted: keep the history before today's replay and the user's
    /// ticks, and let the replay count today again with the new rules. Stored counts stay: each knows when it was last
    /// right, and the replay only applies what came after (2026-11-05: dropping them restarted the special train at 0 and
    /// showed 3/6 for the game's 0/6). Cards rebuild from the replay. Counts are forgotten in <see cref="Recount"/>, and
    /// only from where the replayed log begins: older log files are gone (Honor War's 12 rounds of Tuesday 01:20-01:50
    /// lived only in log.bak2 by Thursday).
    /// </summary>
    private DailyTracker Build(TimeSpan shift, DailyState? state, DateTime now)
    {
        var tracker = new DailyTracker(_file.Shifted(shift));
        if (_tracker is not null)
        {
            tracker.Version = _tracker.Version + 1;
        }

        tracker.SetCustomItems(_prefs.Custom);
        _shift = shift;
        _recountFrom = null;
        if (state is not null)
        {
            if (state.Catalog != tracker.Catalog.Fingerprint)
            {
                _recountFrom = tracker.ReplayFrom(now);
                state = state with { Watermark = DateTime.MinValue, Producers = null };
                Recounts++;
            }

            tracker.Import(state);
        }

        return tracker;
    }

    /// <summary>
    /// After <see cref="Build"/> asked for a recount: forgets the counts from where <paramref name="log"/> begins (or the
    /// earliest current period, if later); events before that only rebuild the context. No log keeps every saved count.
    /// </summary>
    private void Recount(IReadOnlyList<LogEvent> log)
    {
        if (_recountFrom is not { } recount)
        {
            return;
        }

        _recountFrom = null;
        var logBegins = log.Count > 0 ? log.Min(e => e.Timestamp) : DateTime.MaxValue;
        var from = logBegins > recount ? logBegins : recount;
        _tracker.ForgetCountsFrom(from);
        if (from != DateTime.MaxValue)
        {
            _tracker.CountFrom = from.AddTicks(-1);
        }
    }

    /// <summary>
    /// Re-derives the catalog when the shift for <paramref name="now"/> is not the one in use, and counts the kept log
    /// again with it. True when it did.
    /// </summary>
    private bool Follow(DateTime now)
    {
        var shift = ShiftAt(now);
        if (shift == _shift)
        {
            return false;
        }

        _tracker = Build(shift, _tracker.Export(), now);
        Recount(_kept);
        foreach (var e in _kept)
        {
            _tracker.Feed(e);
        }

        return true;
    }

    /// <summary>Keeps <paramref name="e"/> for a later recount; false for a duplicate (the tracker's own rule).</summary>
    private bool Keep(LogEvent e)
    {
        if (e.Timestamp < _keptLast.At || (e.Timestamp == _keptLast.At && e.StartOffset <= _keptLast.Offset))
        {
            return false;
        }

        _keptLast = (e.Timestamp, e.StartOffset);
        _kept.Add(e);
        return true;
    }

    /// <summary>Drops kept events older than a day before the earliest current period (no new shift reaches further back).</summary>
    private void Trim(DateTime now)
    {
        var keepFrom = _tracker.ReplayFrom(now).AddDays(-1);
        if (_kept.Count == 0 || _kept[0].Timestamp >= keepFrom)
        {
            return;
        }

        var first = _kept.FindIndex(e => e.Timestamp >= keepFrom);
        _kept.RemoveRange(0, first < 0 ? _kept.Count : first);
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
    /// the next reset, the time shift in use and every shown item with its name, count and state. Names and counts only.
    /// </summary>
    public byte[] TodayFile()
    {
        DailySnapshot s;
        DailyPrefs p;
        TimeSpan shift;
        bool learned;
        lock (_gate)
        {
            s = _tracker.Snapshot(_nowLocal());
            p = _prefs;
            shift = _shift;
            learned = _learned is not null;
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
            shiftMinutes = (int)shift.TotalMinutes,
            shiftLearned = learned,
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
            if ((_tracker.Version == _savedVersion && !_learnedDirty) || _buffer is not null)
            {
                return null;
            }

            _savedVersion = _tracker.Version;
            _learnedDirty = false;
            var state = _tracker.Export();
            if (_learned is { } l)
            {
                state = state with { LearnedShiftMinutes = (int)l.Shift.TotalMinutes, LearnedPcOffsetMinutes = (int)l.PcOffset.TotalMinutes };
            }

            return JsonSerializer.SerializeToUtf8Bytes(state, Json);
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
