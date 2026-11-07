using Companion.App.Tray.Today;
using Companion.Dailies;

namespace Companion.App.Tests;

/// <summary>The Today page's texts and order (DAILIES_MODULE.md, UX), from snapshots built by hand.</summary>
[TestClass]
public sealed class TodayModelTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 21, 47, 0);

    private static readonly DailySection[] Sections =
        [new("day", "Every day"), new("home", "Homestead and Norton Town"), new("week", "This week"), new("clock", "Coming up"), new("comp", "The companion")];

    [TestMethod]
    public void WhatIsLeftComesFirstBySection_TheDoneOnesInOneGroup_WithPlainTexts()
    {
        var v = TodayModel.Build(Snap(
            Item("mail", "Mail rewards", "day", done: true, at: Now.AddMinutes(-80)),
            Item("arena", "Arena", "day"),
            Item("escort", "Escort missions", "day", count: 2, perDay: 3, at: Now.AddMinutes(-7)),
            Item("kanu", "Kanu's Wish", "home", count: 1, perDay: 3, learning: true),
            Item("crib", "Crib", "home", done: true, source: DailySource.You),
            Item("craft", "Crafting", "comp", done: true, source: DailySource.Companion, at: Now.AddMinutes(-17))), new DailyPrefs(), replaying: false);

        Assert.AreEqual("Today · 3 left", v.TabText);
        Assert.AreEqual("3 left", v.LeftText);
        Assert.AreEqual("3 of 6 done today", v.DoneText);
        Assert.AreEqual("Resets at 07:00 · in 9 h 13 min", v.ResetText);
        CollectionAssert.AreEqual(new[] { "day", "home", "comp" }, v.Sections.Select(s => s.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "Arena", "Escort missions" }, v.Sections[0].Todo.Select(r => r.Name).ToArray());
        Assert.AreEqual(0, v.Sections[2].Todo.Count, "the companion's section is all done");

        var escort = v.Sections[0].Todo[1];
        Assert.AreEqual("2 of 3 · last at 21:40", escort.Detail);
        Assert.AreEqual("2/3", escort.Chip);
        Assert.AreEqual(2d / 3, escort.Fill, 1e-9);
        Assert.AreEqual("Not seen yet today", v.Sections[0].Todo[0].Detail);
        Assert.IsTrue(v.Sections[1].Todo[0].Learning);
        Assert.IsNull(v.Sections[0].Todo[0].Chip);

        CollectionAssert.AreEqual(new[] { "Mail rewards", "Crafting", "Crib" }, v.Done.Select(r => r.Name).ToArray(), "in the order they were done");
        Assert.AreEqual("20:27 · seen in the game", v.Done[0].Detail);
        Assert.AreEqual("21:30 · done by the companion", v.Done[1].Detail);
        Assert.AreEqual("Ticked by you", v.Done[2].Detail);
    }

    [TestMethod]
    public void HiddenItemsAndRenames_FollowTheUsersChoices()
    {
        var prefs = new DailyPrefs
        {
            Shown = new Dictionary<string, bool> { ["arena"] = false, ["gacha"] = true },
            Names = new Dictionary<string, string> { ["mail"] = "  Mailbox " },
        };
        var v = TodayModel.Build(Snap(
            Item("mail", "Mail rewards", "day"),
            Item("arena", "Arena", "day"),
            Item("gacha", "Gacha draw", "day", shown: false)), prefs, replaying: true);

        CollectionAssert.AreEqual(new[] { "Mailbox", "Gacha draw" }, v.Sections[0].Todo.Select(r => r.Name).ToArray());
        Assert.IsNotNull(v.Banner, "while the log is read at start");
    }

    [TestMethod]
    public void TheUsersUntick_OfSomethingTheLogSaw_SaysSo_AndAnAllDoneDay_SaysSo()
    {
        var v = TodayModel.Build(Snap(Item("mail", "Mail rewards", "day", done: false, autoDone: true)), new DailyPrefs(), false);
        Assert.AreEqual("You marked it not done", v.Sections[0].Todo[0].Detail);

        var all = TodayModel.Build(Snap(Item("mail", "Mail rewards", "day", done: true, at: Now)), new DailyPrefs(), false);
        Assert.AreEqual("Today · all done", all.TabText);
        Assert.AreEqual("All done for today", all.LeftText);
        Assert.AreEqual(1d, all.Progress);
    }

    [TestMethod]
    public void EventsAndTheWeek_GoToTheSide_OpenEventsFirst()
    {
        var v = TodayModel.Build(Snap(
            Item("spa", "Guild Spa", "clock", kind: DailyKind.Event, usual: "21:59–22:19", open: false),
            Item("bear", "Guild Bear boss", "clock", kind: DailyKind.Event, usual: "21:44–21:59", open: true),
            Item("honor", "Honor War", "week", count: 23, perDay: 0)), new DailyPrefs(), false);

        Assert.AreEqual(0, v.Sections.Count);
        CollectionAssert.AreEqual(new[] { "Guild Bear boss", "Guild Spa" }, v.Events.Select(e => e.Name).ToArray());
        Assert.AreEqual("Open now", v.Events[0].When);
        Assert.AreEqual("Usually 21:59–22:19", v.Events[1].When);
        Assert.AreEqual(("Honor War", "23 rounds this week"), v.Week.Single());
        Assert.AreEqual("Nothing on your list", v.LeftText);
        Assert.AreEqual("4 d 9 h", TodayModel.Span(TimeSpan.FromHours(105.2)));
        Assert.AreEqual("1 min", TodayModel.Span(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public void ComingUp_ShowsOnlyTodaysEventsWithACountdown_AndDoneOrMissedOnesMoveToDoneToday()
    {
        DailyTimeSpan W(int h, int m, int len) => new(Now.Date.AddHours(h).AddMinutes(m), Now.Date.AddHours(h).AddMinutes(m + len));
        var v = TodayModel.Build(Snap(
            Item("raccoons", "Guild raccoons", "clock", kind: DailyKind.Event, count: 2, perDay: 5, open: true) with { Windows = [W(19, 44, 20), W(21, 44, 20)] },
            Item("spa", "Guild spa", "clock", kind: DailyKind.Event, open: false) with { Windows = [W(21, 59, 20)] },
            Item("boat", "Guild Boat race", "clock", kind: DailyKind.Event, open: false) with { Windows = [W(22, 19, 25)], Today = false },
            Item("wboss", "World boss", "clock", kind: DailyKind.Event, done: true, at: Now.AddMinutes(-100), open: false) with { Windows = [W(20, 0, 25)] },
            Item("bear", "Big raccoon", "clock", kind: DailyKind.Event, open: false) with { Windows = [W(21, 30, 15)], Missed = true }),
            new DailyPrefs(), false);

        CollectionAssert.AreEqual(new[] { "Guild raccoons", "Guild spa" }, v.Events.Select(e => e.Name).ToArray(), "not Friday's boat race; done and missed ones leave");
        Assert.AreEqual("Open now · 17 min left · last one today", v.Events[0].When, "21:47 inside 21:44-22:04, the last round");
        Assert.AreEqual("2/5", v.Events[0].Progress);
        Assert.AreEqual("21:59 · in 12 min", v.Events[1].When);
        CollectionAssert.AreEqual(new[] { "World boss", "Big raccoon" }, v.Done.Select(r => r.Name).ToArray());
        Assert.AreEqual("20:07 · done", v.Done[0].Detail);
        Assert.IsTrue(v.Done[1].Missed);
        Assert.AreEqual("Missed", v.Done[1].Detail);
        Assert.AreEqual(0, v.Left, "events are not counted in what is left");
    }

    [TestMethod]
    public void ProducerCards_AndStoredCounts_SayWhatToDo()
    {
        var s = Snap(
            Item("train", "Hugo train", "day", count: 3, perDay: 3, done: true, at: Now) with { Stack = 0, StackCap = 9 },
            Item("ygg", "Yggdrasil temple", "day", perDay: 0, done: true) with { Stack = 1, StackCap = 5 },
            Item("special", "Special train", "day", perDay: 0) with { Stack = 6, StackCap = 6, StackFill = true },
            Item("demon", "Demon trial", "day", perDay: 2) with { Today = false })
            with
            {
                Producers =
                [
                    new DailyProducerView("lumiCard", "Lumi's Hut", ProducerState.Making, Now.AddMinutes(66), "how"),
                    new DailyProducerView("helperCard", "Homestead helper", ProducerState.Ready, Now.AddMinutes(-5), "how"),
                    new DailyProducerView("x", "Empty one", ProducerState.Empty, null, "how"),
                ],
            };
        var v = TodayModel.Build(s, new DailyPrefs(), false);

        Assert.AreEqual("Check in 1 h 6 min (22:53)", v.Cards![0].Text);
        Assert.IsFalse(v.Cards[0].Dot);
        Assert.AreEqual("Ready since 21:42 · go and collect", v.Cards[1].Text);
        Assert.IsTrue(v.Cards[1].Dot);
        Assert.AreEqual("Empty · put items in", v.Cards[2].Text);
        Assert.IsTrue(v.Cards[2].Dot);

        var special = v.Sections.Single().Todo.Single();
        Assert.AreEqual("Ready to summon · tick it when you do", special.Detail);
        Assert.AreEqual(6, special.StackCap);
        StringAssert.Contains(v.Done.Single(r => r.Id == "ygg").Detail, "1 of 5 stored · nothing due");
        StringAssert.Contains(v.Done.Single(r => r.Id == "train").Detail, "0 of 9 stored");
        Assert.IsFalse(v.Done.Concat(v.Sections.SelectMany(x => x.Todo)).Any(r => r.Id == "demon"), "not today");
    }

    private static DailySnapshot Snap(params DailyItemView[] items) =>
        new(Now, new DateOnly(2026, 11, 4), new DateTime(2026, 11, 5, 7, 0, 0), new DateTime(2026, 11, 9, 7, 0, 0), Sections, items, Now, 1);

    private static DailyItemView Item(
        string id, string name, string section, bool done = false, int count = 0, int perDay = 1, DateTime? at = null, bool learning = false,
        DailySource source = DailySource.Game, bool shown = true, DailyKind kind = DailyKind.Task, string? usual = null, bool? open = null, bool? autoDone = null) =>
        new(id, name, section, kind, done && perDay > 0 && count == 0 ? perDay : count, perDay, done, autoDone ?? done, done || count > 0 ? source : DailySource.None,
            at, at, "how " + id, learning, shown, open, usual);
}
