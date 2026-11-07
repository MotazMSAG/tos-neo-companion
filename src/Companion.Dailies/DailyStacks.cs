namespace Companion.Dailies;

/// <summary>
/// Stored counts (learning day, entries 10/21/29): each daily reset of an allowed day adds <see cref="DailyStackDef.PerReset"/>
/// up to the cap; a hit takes one ('use') or adds one ('fill'); an "empty" reply sets 0; the user can set it. With no
/// saved value, an item starts its first game day full for that day's allowance (the best guess; the user corrects it).
/// Not thread-safe (owned by <see cref="DailyTracker"/>).
/// </summary>
internal sealed class DailyStacks
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<DateTime, DateOnly> _gameDay;
    private readonly TimeSpan _reset;

    public DailyStacks(Func<DateTime, DateOnly> gameDay, TimeSpan dailyReset)
    {
        _gameDay = gameDay;
        _reset = dailyReset;
    }

    /// <summary>Applies a hit at <paramref name="t"/>.</summary>
    public void Hit(DailyItemDef item, bool empties, DateTime t)
    {
        var s = item.Stack!;
        var e = At(item, t);
        e.Value = empties ? 0 : s.Mode == "fill" ? Math.Min(s.Cap, e.Value + 1) : Math.Max(0, e.Value - 1);
    }

    /// <summary>The user set the stored count.</summary>
    public void Set(DailyItemDef item, int value, DateTime t)
    {
        var e = At(item, t);
        e.Value = Math.Clamp(value, 0, item.Stack!.Cap);
    }

    /// <summary>'fill' mode: the user summoned it (the stack empties; the timer starts).</summary>
    public void Summon(DailyItemDef item, DateTime t)
    {
        var e = At(item, t);
        e.Value = 0;
        e.SummonedAt = t;
    }

    /// <summary>
    /// True when the stored value already includes a hit at <paramref name="t"/> (it is at or before the value's time):
    /// a recount replays the log over the saved values, and applying those hits again would count them twice.
    /// </summary>
    public bool Includes(DailyItemDef item, DateTime t) => _entries.TryGetValue(item.Id, out var e) && t <= e.AsOf;

    /// <summary>'fill' mode: the summoned thing was filled (the stack empties; no timer runs).</summary>
    public void Finish(DailyItemDef item, DateTime t)
    {
        var e = At(item, t);
        e.Value = 0;
        e.SummonedAt = null;
    }

    /// <summary>The value at <paramref name="t"/> (does not change the stored state) and the summon moment.</summary>
    public (int Value, DateTime? SummonedAt) Peek(DailyItemDef item, DateTime t)
    {
        if (!_entries.TryGetValue(item.Id, out var e))
        {
            var fresh = Fresh(item, t);
            return (Advance(item.Stack!, fresh.Value, fresh.AsOf, t), null);
        }

        return (Advance(item.Stack!, e.Value, e.AsOf, t), e.SummonedAt);
    }

    public IEnumerable<DailyStackDto> Export() =>
        _entries.Select(kv => new DailyStackDto { Item = kv.Key, Value = kv.Value.Value, AsOf = kv.Value.AsOf, SummonedAt = kv.Value.SummonedAt });

    public void Import(DailyStackDto dto) =>
        _entries[dto.Item] = new Entry { Value = dto.Value, AsOf = dto.AsOf, SummonedAt = dto.SummonedAt };

    private Entry At(DailyItemDef item, DateTime t)
    {
        if (!_entries.TryGetValue(item.Id, out var e))
        {
            e = Fresh(item, t);
            _entries[item.Id] = e;
        }

        if (t > e.AsOf)
        {
            e.Value = Advance(item.Stack!, e.Value, e.AsOf, t);
            e.AsOf = t;
        }

        return e;
    }

    private Entry Fresh(DailyItemDef item, DateTime t)
    {
        var s = item.Stack!;
        var day = _gameDay(t);
        var start = day.ToDateTime(TimeOnly.MinValue) + _reset;
        return new Entry { Value = s.Mode == "fill" ? 0 : Adds(s, day) ? Math.Min(s.Cap, s.PerReset) : 0, AsOf = start };
    }

    private int Advance(DailyStackDef s, int value, DateTime from, DateTime to)
    {
        if (to <= from || s.PerReset == 0)
        {
            return value;
        }

        for (var day = _gameDay(from).AddDays(1); day <= _gameDay(to); day = day.AddDays(1))
        {
            var reset = day.ToDateTime(TimeOnly.MinValue) + _reset;
            if (reset > from && reset <= to && Adds(s, day))
            {
                value = Math.Min(s.Cap, value + s.PerReset);
            }
        }

        return value;
    }

    private static bool Adds(DailyStackDef s, DateOnly day) =>
        s.ResetDays.Count == 0 || s.ResetDays.Contains(DailyCatalog.DayName(day.DayOfWeek), StringComparer.Ordinal);

    private sealed class Entry
    {
        public int Value { get; set; }

        public DateTime AsOf { get; set; }

        public DateTime? SummonedAt { get; set; }
    }
}

/// <summary>One saved stack.</summary>
public sealed record DailyStackDto
{
    /// <summary>Item id.</summary>
    public string Item { get; init; } = "";

    /// <summary>Stored count at <see cref="AsOf"/>.</summary>
    public int Value { get; init; }

    /// <summary>When the value was true.</summary>
    public DateTime AsOf { get; init; }

    /// <summary>'fill' mode: when the user summoned it last.</summary>
    public DateTime? SummonedAt { get; init; }
}
