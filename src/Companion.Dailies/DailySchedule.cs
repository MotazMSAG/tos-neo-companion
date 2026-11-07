using System.Globalization;

namespace Companion.Dailies;

/// <summary>
/// Days and open times of the checklist's items (learning day, entries 16/17/40). A game day starts at the daily reset
/// (07:00 in the data file), so a window at 00:00 or 02:00 belongs to the night after that day's evening. Pure functions.
/// </summary>
public static class DailySchedule
{
    /// <summary>Today's windows of <paramref name="item"/> on game day <paramref name="gameDay"/>, in order.</summary>
    public static IReadOnlyList<DailyTimeSpan> WindowsOn(DailyItemDef item, DateOnly gameDay, TimeSpan dailyReset)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Windows.Count == 0)
        {
            return [];
        }

        var list = new List<DailyTimeSpan>(item.Windows.Count);
        foreach (var w in item.Windows)
        {
            if (!TimeOnly.TryParseExact(w.At, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            {
                continue;
            }

            // the window's place in the game day: how long after the reset it opens (also right for a shifted reset that
            // lies before 00:00 or after 24:00, see DailyResets)
            var afterReset = (at.ToTimeSpan() - dailyReset).Ticks % TimeSpan.TicksPerDay;
            if (afterReset < 0)
            {
                afterReset += TimeSpan.TicksPerDay;
            }

            var start = gameDay.ToDateTime(TimeOnly.MinValue) + dailyReset + TimeSpan.FromTicks(afterReset);
            list.Add(new DailyTimeSpan(start, start.AddMinutes(w.Minutes)));
        }

        list.Sort((a, b) => a.Start.CompareTo(b.Start));
        return list;
    }

    /// <summary>
    /// True when <paramref name="item"/> exists on <paramref name="gameDay"/>: no 'days' set, today's name listed, or one
    /// of its switches was seen opening on that weekday before (<paramref name="learned"/>, per instance).
    /// </summary>
    public static bool OnDay(DailyItemDef item, DateOnly gameDay, IReadOnlyDictionary<string, HashSet<string>>? learned)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Days.Count == 0)
        {
            return true;
        }

        var name = DailyCatalog.DayName(gameDay.DayOfWeek);
        if (item.Days.Contains(name, StringComparer.Ordinal))
        {
            return true;
        }

        return learned is not null && item.Instances.Any(i => learned.TryGetValue(i, out var days) && days.Contains(name));
    }

    /// <summary>The window containing <paramref name="t"/>, if any.</summary>
    public static DailyTimeSpan? WindowAt(IReadOnlyList<DailyTimeSpan> windows, DateTime t)
    {
        ArgumentNullException.ThrowIfNull(windows);
        foreach (var w in windows)
        {
            if (t >= w.Start && t < w.End)
            {
                return w;
            }
        }

        return null;
    }
}
