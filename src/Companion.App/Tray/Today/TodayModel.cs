using System.Globalization;
using Companion.Dailies;

namespace Companion.App.Tray.Today;

/// <summary>What the Today page needs from the host (the daily checklist service).</summary>
internal interface ITodayBackend
{
    /// <summary>The list now.</summary>
    DailySnapshot Snapshot();

    /// <summary>The user's list choices.</summary>
    DailyPrefs Prefs { get; }

    /// <summary>True while today's game log is still being read at start-up.</summary>
    bool Replaying { get; }

    /// <summary>Applies new list choices ("Edit my list").</summary>
    void SetPrefs(DailyPrefs prefs);

    /// <summary>The user ticked or unticked an item.</summary>
    bool SetDone(string itemId, bool done);

    /// <summary>The user set a stored count (Hugo train requests, demon trial rewards, Yggdrasil attempts).</summary>
    bool SetStack(string itemId, int value);
}

/// <summary>The host's checklist service as an <see cref="ITodayBackend"/>.</summary>
internal sealed class HostTodayBackend(DailyService service) : ITodayBackend
{
    public DailySnapshot Snapshot() => service.Snapshot();

    public DailyPrefs Prefs => service.Prefs;

    public bool Replaying => service.Replaying;

    public void SetPrefs(DailyPrefs prefs) => service.SetPrefs(prefs);

    public bool SetDone(string itemId, bool done) => service.SetDone(itemId, done);

    public bool SetStack(string itemId, int value) => service.SetStack(itemId, value);
}

/// <summary>One row of the list.</summary>
/// <param name="Id">Item id.</param>
/// <param name="Name">Shown name (the user's rename applied).</param>
/// <param name="Detail">Second line ("2 of 3 · last at 21:40", "20:34 · seen in the game", "Ticked by you").</param>
/// <param name="Done">Done (the user's choice included).</param>
/// <param name="Fill">Progress 0..1 for items with several per day (0 when done or single).</param>
/// <param name="Chip">Count chip ("2/3") for unfinished items with several per day.</param>
/// <param name="Learning">Still being confirmed.</param>
/// <param name="How">How it is detected (tooltip).</param>
/// <param name="StackCap">Items with a stored count: its cap (the row's menu sets the count); 0 = none.</param>
/// <param name="Missed">A timed event that ended without the user (shown in Done today as "Missed").</param>
internal sealed record TodayRow(string Id, string Name, string Detail, bool Done, double Fill, string? Chip, bool Learning, string How, int StackCap = 0, bool Missed = false);

/// <summary>A section of what is left.</summary>
/// <param name="Id">Section id.</param>
/// <param name="Title">Title.</param>
/// <param name="Todo">Rows still to do.</param>
/// <param name="Total">Rows in the section, done ones included.</param>
internal sealed record TodaySection(string Id, string Title, IReadOnlyList<TodayRow> Todo, int Total);

/// <summary>An event in "Coming up today".</summary>
/// <param name="Name">Name.</param>
/// <param name="When">"Open now · 14 min left", "21:44 · in 1 h 12 min".</param>
/// <param name="Open">Open now.</param>
/// <param name="Joined">The user took part today (a partial count, e.g. 3/5 fights).</param>
/// <param name="Id">Item id (a click ticks it).</param>
/// <param name="Progress">"3/5" for events counted per day; null otherwise.</param>
/// <param name="How">How it is detected (tooltip).</param>
internal sealed record TodayEvent(string Name, string When, bool Open, bool Joined, string Id = "", string? Progress = null, string How = "");

/// <summary>A homestead producer card (Lumi's Hut, the helper).</summary>
/// <param name="Id">Item id.</param>
/// <param name="Name">Name.</param>
/// <param name="Text">"Empty · put items in", "Check in 1 h 12 min (22:53)", "Ready since 22:53 · go and collect".</param>
/// <param name="Dot">Show the red dot (empty or ready).</param>
/// <param name="How">How it is followed (tooltip).</param>
internal sealed record TodayCard(string Id, string Name, string Text, bool Dot, string How);

/// <summary>Everything the Today page shows.</summary>
internal sealed record TodayView(
    string TabText, string LeftText, string DoneText, string ResetText, double Progress, IReadOnlyList<TodaySection> Sections,
    IReadOnlyList<TodayRow> Done, IReadOnlyList<TodayEvent> Events, IReadOnlyList<(string Name, string Text)> Week,
    string WeekReset, string? Banner, int Left, IReadOnlyList<TodayCard>? Cards = null);

/// <summary>
/// Pure presentation of the daily checklist (DAILIES_MODULE.md, UX and the learning-day entries 16/38-41): what is left
/// first by section, the done ones in one group; anything with a set time only in "Coming up today" (today's ones only,
/// with a countdown) until it is done or missed, then in "Done today"; producers as cards. No WinForms here, so it is
/// unit-tested.
/// </summary>
internal static class TodayModel
{
    private static readonly HashSet<string> SideSections = ["clock", "week"];

    /// <summary>Builds the page from a snapshot and the user's choices.</summary>
    public static TodayView Build(DailySnapshot s, DailyPrefs p, bool replaying)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(p);
        var shown = s.Items.Where(i => p.IsShown(i) && i.Today).ToList();
        var list = shown.Where(i => i.Kind == DailyKind.Task && !SideSections.Contains(i.Section)).ToList();
        var rows = list.ToDictionary(i => i.Id, i => Row(i, p, s.AsOf));

        var sections = s.Sections
            .Where(sec => !SideSections.Contains(sec.Id))
            .Select(sec =>
            {
                var all = list.Where(i => i.Section == sec.Id).ToList();
                return new TodaySection(sec.Id, sec.Title, [.. all.Where(i => !i.Done).Select(i => rows[i.Id])], all.Count);
            })
            .Where(sec => sec.Total > 0)
            .ToList();

        var events = shown.Where(i => i.Kind == DailyKind.Event).ToList();
        var eventsDone = events.Where(i => i.Done || i.Missed).ToList();
        var done = list.Where(i => i.Done).Select(i => (At: i.LastAt ?? DateTime.MaxValue, Row: rows[i.Id]))
            .Concat(eventsDone.Select(i => (At: i.Missed && !i.Done ? i.Windows![^1].End : i.LastAt ?? DateTime.MaxValue, Row: EventDoneRow(i, p))))
            .OrderBy(x => x.At)
            .Select(x => x.Row)
            .ToList();
        var left = list.Count - list.Count(i => i.Done);

        var coming = events.Where(i => !i.Done && !i.Missed)
            .Select(i => (Item: i, Next: NextStart(i, s.AsOf)))
            .Where(x => x.Item.Open == true || x.Next is not null || (x.Item.Windows is not { Count: > 0 } && x.Item.Usual is not null))
            .OrderByDescending(x => x.Item.Open == true)
            .ThenBy(x => x.Next ?? DateTime.MaxValue)
            .Select(x => Event(x.Item, p, s.AsOf))
            .ToList();
        var week = shown.Where(i => i.Section == "week")
            .Select(i => (p.NameOf(i), i.Count == 0 ? "No rounds yet this week" : i.Count + (i.Count == 1 ? " round" : " rounds") + " this week"))
            .ToList();
        var cards = (s.Producers ?? []).Where(c => p.Shown.GetValueOrDefault(c.Id, true))
            .Select(c => Card(c, p, s.AsOf))
            .ToList();

        return new TodayView(
            left == 0 ? "Today · all done" : "Today · " + left + " left",
            list.Count == 0 ? "Nothing on your list" : left == 0 ? "All done for today" : left + " left",
            (list.Count - left) + " of " + list.Count + " done today",
            "Resets at " + s.NextDailyReset.ToString("HH:mm", CultureInfo.InvariantCulture) + " · in " + Span(s.NextDailyReset - s.AsOf),
            list.Count == 0 ? 0 : (double)(list.Count - left) / list.Count,
            sections,
            done,
            coming,
            week,
            "Weekly reset: " + s.NextWeeklyReset.ToString("dddd HH:mm", CultureInfo.InvariantCulture) + " (in " + Span(s.NextWeeklyReset - s.AsOf) + ")",
            replaying ? "Reading today's game log… the list fills in a moment." : null,
            left,
            cards);
    }

    /// <summary>"9 h 13 min", "45 min", "4 d 10 h".</summary>
    public static string Span(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }

        return t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h"
            : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
            : $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))} min";
    }

    private static string Hm(DateTime t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static DateTime? NextStart(DailyItemView i, DateTime now) =>
        i.Windows?.Where(w => w.Start > now).Select(w => (DateTime?)w.Start).FirstOrDefault();

    private static TodayEvent Event(DailyItemView i, DailyPrefs p, DateTime now)
    {
        string when;
        var current = i.Windows is { } ws ? DailySchedule.WindowAt(ws, now) : null;
        if (i.Open == true)
        {
            when = current is { } w ? "Open now · " + Span(w.End - now) + " left" : "Open now";
        }
        else if (NextStart(i, now) is { } next)
        {
            when = Hm(next) + " · in " + Span(next - now);
        }
        else
        {
            when = "Usually " + (i.Usual ?? "at its time");
        }

        var counted = i.PerDay > 1;
        if (counted && i.Windows is { Count: > 0 } all && i.Windows.Count(w => w.End > now) == 1 && i.Count < i.PerDay)
        {
            when += " · last one today";
        }

        return new TodayEvent(p.NameOf(i), when, i.Open == true, i.Count > 0, i.Id, counted ? i.Count + "/" + i.PerDay : null, i.How);
    }

    private static TodayRow EventDoneRow(DailyItemView i, DailyPrefs p)
    {
        var detail = i.Done
            ? i.Source == DailySource.You ? "Ticked by you" : (i.LastAt is { } at ? Hm(at) + " · " : string.Empty) + (i.PerDay > 1 ? $"{i.Count} of {i.PerDay}" : "done")
            : i.Count > 0 ? $"Missed · {i.Count} of {i.PerDay}" : "Missed";
        return new TodayRow(i.Id, p.NameOf(i), detail, true, 0, null, i.Learning, i.How, Missed: !i.Done);
    }

    private static TodayCard Card(DailyProducerView c, DailyPrefs p, DateTime now)
    {
        var name = p.Names.GetValueOrDefault(c.Id)?.Trim() is { Length: > 0 } n ? n : c.Name;
        return c.State switch
        {
            ProducerState.Making when c.Until is { } u => new TodayCard(c.Id, name, "Check in " + Span(u - now) + " (" + Hm(u) + ")", false, c.How),
            ProducerState.Ready => new TodayCard(c.Id, name, (c.Until is { } r ? "Ready since " + Hm(r) + " · " : "Ready · ") + "go and collect", true, c.How),
            _ => new TodayCard(c.Id, name, "Empty · put items in", true, c.How),
        };
    }

    private static TodayRow Row(DailyItemView i, DailyPrefs p, DateTime now)
    {
        var at = (i.LastAt ?? i.FirstAt)?.ToString("HH:mm", CultureInfo.InvariantCulture);
        string detail;
        if (i.Stack is { } stack && i.StackFill)
        {
            detail = i.TimerUntil is { } until ? "Summoned · leaves about " + Hm(until) + " (" + Span(until - now) + ")"
                : stack >= i.StackCap ? "Ready to summon · tick it when you do"
                : $"{stack} of {i.StackCap} filled";
            return new TodayRow(i.Id, p.NameOf(i), detail, i.Done, 0, null, i.Learning, i.How, i.StackCap);
        }

        if (i.Stack is { } dueStack && i.PerDay == 0)
        {
            // stored attempts (Yggdrasil): only due when nearly full
            detail = $"{dueStack} of {i.StackCap} stored" + (dueStack >= i.StackCap ? " · full: the next reset wastes one"
                : i.Done ? " · nothing due" : " · use them before it's full");
            return new TodayRow(i.Id, p.NameOf(i), detail, i.Done, 0, null, i.Learning, i.How, i.StackCap);
        }

        if (i.Done)
        {
            detail = i.Source switch
            {
                DailySource.You => "Ticked by you",
                DailySource.Companion => at + " · done by the companion",
                _ => i.PerDay > 1 ? $"{at} · {i.Count} of {i.PerDay} seen in the game" : at + " · seen in the game",
            };
        }
        else if (i.AutoDone)
        {
            detail = "You marked it not done";
        }
        else if (i.PerDay > 1)
        {
            detail = i.Count + " of " + i.PerDay + (i.Count > 0 && at is not null ? " · last at " + at : string.Empty);
        }
        else
        {
            detail = i.Learning || i.Source != DailySource.None || i.How != "Ticked by hand" ? "Not seen yet today" : "Tick it when you've done it";
        }

        if (i.Stack is { } waiting)
        {
            detail += $" · {waiting} of {i.StackCap} stored" + (waiting >= i.StackCap ? " (full: new ones are lost)" : string.Empty);
        }

        var partial = !i.Done && i.PerDay > 1 && i.Count > 0;
        return new TodayRow(
            i.Id, p.NameOf(i), detail, i.Done, partial ? Math.Min(1, (double)i.Count / i.PerDay) : 0,
            partial ? i.Count + "/" + i.PerDay : null, i.Learning, i.How, i.Stack is null ? 0 : i.StackCap);
    }
}
