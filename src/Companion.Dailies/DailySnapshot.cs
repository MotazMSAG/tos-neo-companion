namespace Companion.Dailies;

/// <summary>Where an item's tick came from.</summary>
public enum DailySource
{
    /// <summary>Nothing yet.</summary>
    None,

    /// <summary>Seen in the game log.</summary>
    Game,

    /// <summary>The companion's own work (journal).</summary>
    Companion,

    /// <summary>The user's click (wins until the reset).</summary>
    You,
}

/// <summary>One item as the list shows it now.</summary>
/// <param name="Id">Item id.</param>
/// <param name="Name">Default name (the UI applies the user's rename).</param>
/// <param name="Section">Section id.</param>
/// <param name="Kind">Task or event.</param>
/// <param name="Count">Count in the current day.</param>
/// <param name="PerDay">Target per day (0 = counter).</param>
/// <param name="Done">Done, the user's choice included.</param>
/// <param name="AutoDone">Done by the log or the companion alone.</param>
/// <param name="Source">Who ticked it.</param>
/// <param name="FirstAt">First hit of the day (local).</param>
/// <param name="LastAt">Last hit of the day (local).</param>
/// <param name="How">How it is detected.</param>
/// <param name="Learning">Still being confirmed.</param>
/// <param name="Shown">On the list by default.</param>
/// <param name="Open">Events: switched on now (null = unknown).</param>
/// <param name="Usual">Events: usual hours.</param>
/// <param name="Today">It exists on today's game day (its 'days' allow today).</param>
/// <param name="Windows">Events: today's open times (local), in order.</param>
/// <param name="Missed">Events: today's last window ended and it is not done.</param>
/// <param name="Stack">Stored count now (null = no stack).</param>
/// <param name="StackCap">Most the stack holds.</param>
/// <param name="StackFill">The stack fills with use ('fill' mode: the special train).</param>
/// <param name="TimerUntil">'fill' stacks: the summoned thing lasts until then.</param>
public sealed record DailyItemView(
    string Id, string Name, string Section, DailyKind Kind, int Count, int PerDay, bool Done, bool AutoDone, DailySource Source,
    DateTime? FirstAt, DateTime? LastAt, string How, bool Learning, bool Shown, bool? Open, string? Usual,
    bool Today = true, IReadOnlyList<DailyTimeSpan>? Windows = null, bool Missed = false, int? Stack = null, int StackCap = 0,
    bool StackFill = false, DateTime? TimerUntil = null);

/// <summary>A start and end (local).</summary>
/// <param name="Start">Start.</param>
/// <param name="End">End.</param>
public readonly record struct DailyTimeSpan(DateTime Start, DateTime End);

/// <summary>A producer card's state.</summary>
public enum ProducerState
{
    /// <summary>Nothing being made (red dot: put items in).</summary>
    Empty,

    /// <summary>Items queued; the "go and check" countdown runs.</summary>
    Making,

    /// <summary>The countdown ended (red dot: go and collect).</summary>
    Ready,
}

/// <summary>A producer card as the page shows it.</summary>
/// <param name="Id">Item id.</param>
/// <param name="Name">Default name.</param>
/// <param name="State">State now.</param>
/// <param name="Until">Making: when the countdown ends; Ready: when it ended.</param>
/// <param name="How">How it is followed.</param>
public sealed record DailyProducerView(string Id, string Name, ProducerState State, DateTime? Until, string How);

/// <summary>The checklist at one moment.</summary>
/// <param name="AsOf">The moment (local).</param>
/// <param name="GameDay">The daily update's day ('2026-11-03' runs from 07:00 on the 3rd).</param>
/// <param name="NextDailyReset">When the daily list resets next.</param>
/// <param name="NextWeeklyReset">When the weekly items reset next.</param>
/// <param name="Sections">Sections in display order.</param>
/// <param name="Items">Every catalog item.</param>
/// <param name="LastEventAt">Newest game-log event fed (null before any).</param>
/// <param name="Version">Changes with every state change.</param>
/// <param name="Producers">Producer cards (Lumi's Hut, the helper).</param>
public sealed record DailySnapshot(
    DateTime AsOf, DateOnly GameDay, DateTime NextDailyReset, DateTime NextWeeklyReset, IReadOnlyList<DailySection> Sections,
    IReadOnlyList<DailyItemView> Items, DateTime? LastEventAt, long Version, IReadOnlyList<DailyProducerView>? Producers = null);
