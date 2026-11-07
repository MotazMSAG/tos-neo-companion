using Companion.Contracts.Logs;

namespace Companion.Dailies.Tests;

/// <summary>The checklist's rule engine against the traces of the 2026-11-04 log scans and live capture.</summary>
[TestClass]
public sealed class TrackerTests
{
    // Wednesday 2026-11-04 03:48, in the game day that began on Tuesday 07:00.
    private static readonly DateTime Wed = new(2026, 11, 4, 3, 48, 0);

    private readonly Log _log = new();
    private DailyTracker _t = null!;

    [TestInitialize]
    public void Init() => _t = new DailyTracker(Data.Catalog());

    [TestMethod]
    public void TheRealDataFile_Loads_WithEveryItemInAKnownSection()
    {
        var c = Data.Catalog();
        Assert.IsTrue(c.Items.Count > 30);
        Assert.AreEqual(3, c.Items.Single(i => i.Id == "kanu").PerDay);
        Assert.AreEqual(TimeSpan.FromHours(7), c.Resets.Daily);
        Assert.AreEqual(DayOfWeek.Monday, c.Resets.WeeklyDay);
    }

    [TestMethod]
    public void KanusWish_TheLiveCapture_CountsTheTwoQuestHandIns_AndTheWorshipRewardBeforeThemIsWorship()
    {
        Feed(_log.Map(Wed.AddSeconds(21), "town"),
            // 03:48:45: the worship screen is open again; a talk to the statue gives the worship reward
            _log.Show(Wed.AddSeconds(39.663), "UI_HOME_TOWN_WORSHIP2_MAIN"),
            _log.Show(Wed.AddSeconds(45.062), "UI_NPC_TALK"),
            _log.Show(Wed.AddSeconds(45.527), "UI_ITEM_GET_TIP"),
            _log.Throttle(Wed.AddSeconds(45.538), "town.task_info"),
            _log.Popup(Wed.AddSeconds(45.642)),
            _log.Throttle(Wed.AddSeconds(45.662), "town.task_info"),
            // the corn errand: Kanu's window, a trip to the shop in another map, back to town
            _log.Show(Wed.AddSeconds(62.343), "UI_KAKA_MAIN_VIEW"),
            _log.Map(Wed.AddSeconds(78), "leiguang"),
            _log.Show(Wed.AddSeconds(110), "UI_GOODS_BUY"),
            _log.Show(Wed.AddSeconds(120.519), "UI_ITEM_GET_TIP"),
            _log.Close(Wed.AddSeconds(125), "UI_GOODS_BUY"),
            _log.Map(Wed.AddSeconds(130.974), "town"),
            _log.Commit(Wed.AddSeconds(161.204), 110014054),
            _log.Commit(Wed.AddSeconds(171.665), 110014056),
            _log.Show(Wed.AddSeconds(173.565), "UI_KAKA_MAIN_VIEW"),
            _log.Throttle(Wed.AddSeconds(175.488), "town.task_info"),
            _log.Popup(Wed.AddSeconds(175.544)),
            _log.Throttle(Wed.AddSeconds(177.348), "town.task_info"),
            _log.Popup(Wed.AddSeconds(177.355)),
            _log.Throttle(Wed.AddSeconds(177.545), "town.task_info"));

        var kanu = Item("kanu", Wed.AddMinutes(5));
        Assert.AreEqual(2, kanu.Count);
        Assert.IsFalse(kanu.Done);
        Assert.AreEqual(DailySource.Game, kanu.Source);
    }

    [TestMethod]
    public void KanusWish_QuestIdsWithAnotherFront_StillCount()
    {
        // 2026-11-06 03:47 as logged: Kanu's quests 3310014006/008 (not 1100140xx), then three rewards in town, each with
        // the town refresh burst (town.task_info); 4.36 before this fix counted 0 of the user's 3
        var t = new DateTime(2026, 11, 6, 3, 46, 57);
        Feed(_log.Map(t, "town"),
            _log.Commit(t.AddSeconds(14.408), 3310014006),
            _log.Commit(t.AddSeconds(18.238), 3310014008));
        foreach (var s in new[] { 30.654, 32.759, 34.859 })
        {
            Feed(_log.Throttle(t.AddSeconds(s - 0.062), "town.task_info"), _log.Popup(t.AddSeconds(s)), _log.Throttle(t.AddSeconds(s + 0.156), "town.task_info"));
        }

        var kanu = Item("kanu", t.AddMinutes(2));
        Assert.AreEqual(3, kanu.Count);
        Assert.IsTrue(kanu.Done);
    }

    [TestMethod]
    public void KanusWish_AnotherQuestsCommit_IsNoKanuHandIn()
    {
        // the cave errands of 11-04 (2100131xx) and 11-05's 210008554 / 1010008020 are not Kanu's
        var t = new DateTime(2026, 11, 6, 3, 46, 57);
        Feed(_log.Map(t, "town"), _log.Commit(t.AddSeconds(5), 1010008020),
            _log.Throttle(t.AddSeconds(9.9), "town.task_info"), _log.Popup(t.AddSeconds(10)));
        Assert.AreEqual(0, Item("kanu", t.AddMinutes(1)).Count);
    }

    [TestMethod]
    public void KanusWish_AShopPurchaseInTown_IsNotAHandIn_EvenRightAfterAKanuQuest()
    {
        Feed(_log.Map(Wed, "town"),
            _log.Commit(Wed.AddSeconds(10), 110014054),
            _log.Show(Wed.AddSeconds(10.5), "UI_GOODS_BUY"),
            _log.Close(Wed.AddSeconds(11.5), "UI_GOODS_BUY"),
            _log.Popup(Wed.AddSeconds(11.6)),
            _log.Throttle(Wed.AddSeconds(11.7), "town.task_info"));
        Assert.AreEqual(0, Item("kanu", Wed.AddMinutes(1)).Count);
    }

    [TestMethod]
    public void KanusWish_Monday_ALateCleanUpOfAClosedGachaWindow_DoesNotHideAHandIn()
    {
        var mon = new DateTime(2026, 11, 2, 20, 53, 30);
        Feed(_log.Map(mon, "town"),
            _log.Show(mon.AddSeconds(7.919), "UI_HOME_TOWN_GACHA"),
            _log.Close(mon.AddSeconds(22.2), "UI_HOME_TOWN_GACHA"),
            _log.Commit(mon.AddSeconds(30.239), 110014050),
            _log.Throttle(mon.AddSeconds(34.610), "town.task_info"),
            _log.Popup(mon.AddSeconds(34.809)),
            _log.Throttle(mon.AddSeconds(35.315), "town.task_info"),
            _log.Dispose(mon.AddSeconds(35.893), "UI_HOME_TOWN_GACHA"),
            _log.Popup(mon.AddSeconds(36.008)),
            _log.Throttle(mon.AddSeconds(38.963), "town.task_info"),
            _log.Popup(mon.AddSeconds(39.008)));
        Assert.AreEqual(3, Item("kanu", mon.AddMinutes(1)).Count);
    }

    [TestMethod]
    public void KanusWish_ATownRewardWithoutATalkOrKanuQuest_IsNotAHandIn()
    {
        // building upgrades: rewards with the town refresh, but nobody was talked to
        Feed(_log.Map(Wed, "town"),
            _log.Commit(Wed.AddSeconds(5), 210013104),
            _log.Popup(Wed.AddSeconds(20)),
            _log.Throttle(Wed.AddSeconds(20.1), "town.task_info"));
        Assert.AreEqual(0, Item("kanu", Wed.AddMinutes(1)).Count);
    }

    [TestMethod]
    public void KanusWish_WithoutTheTownRefresh_TheRewardIsDropped()
    {
        Feed(_log.Map(Wed, "town"),
            _log.Commit(Wed.AddSeconds(10), 110014054),
            _log.Popup(Wed.AddSeconds(10.5)),
            _log.Show(Wed.AddSeconds(14), "UI_MAIL"));
        Assert.AreEqual(0, Item("kanu", Wed.AddMinutes(1)).Count);
    }

    [TestMethod]
    public void Arena_ARewardClaimBeforeTheRuns_DoesNotTick_AndFiveSweepsAreFiveOfFive()
    {
        // 2026-11-04 20:45:56: the arena screen and its reward claim, six minutes before any run (learning day, entry 22)
        Feed(_log.Show(Wed, "UI_ARENA_VIEW"), _log.Show(Wed.AddSeconds(0.1), "UI_ARENA_REWARD_VIEW"), _log.Show(Wed.AddSeconds(0.4), "UI_ITEM_GET_TIP"),
            _log.Close(Wed.AddSeconds(3), "UI_ARENA_VIEW"));
        Assert.AreEqual(0, Item("arena", Wed.AddMinutes(1)).Count, "a reward claim is not a run");

        // 20:51:32 the arena screen, 20:51:40-43 five sweeps (and a mid-burst 2659 reply that is not "all done")
        var t = Wed.AddMinutes(6);
        Feed(_log.Show(t, "UI_ARENA_VIEW"), _log.Show(t.AddSeconds(8), "UI_COMMON_SETTLE_VIEW"), _log.Reply(t.AddSeconds(8.5), "arena.arena_sweep", 2659));
        Assert.AreEqual(1, Item("arena", t.AddMinutes(1)).Count);
        for (var i = 1; i < 5; i++)
        {
            Feed(_log.Show(t.AddSeconds(8 + i), "UI_COMMON_SETTLE_VIEW"));
        }

        Assert.IsTrue(Item("arena", t.AddMinutes(1)).Done);
        Assert.AreEqual(5, Item("arena", t.AddMinutes(1)).Count);
    }

    [TestMethod]
    public void ARewardLongAfterTheScreenOpened_OrAfterItClosed_CountsForNothing()
    {
        Feed(_log.Show(Wed, "UI_GUILD_DONATE"), _log.Show(Wed.AddMinutes(4), "UI_ITEM_GET_TIP"));
        Assert.IsFalse(Item("donate", Wed.AddMinutes(5)).Done, "the 180 s context ran out");

        Feed(_log.Show(Wed.AddMinutes(6), "UI_GUILD_DONATE"), _log.Close(Wed.AddMinutes(6.5), "UI_GUILD_DONATE"),
            _log.Show(Wed.AddMinutes(7), "UI_ITEM_GET_TIP"));
        Assert.IsFalse(Item("donate", Wed.AddMinutes(8)).Done, "closed before the reward");
    }

    [TestMethod]
    public void Escort_CountsEachDelivery_UpToThree()
    {
        for (var i = 0; i < 4; i++)
        {
            Feed(_log.Push(Wed.AddMinutes(i), "convoy_mgr", "notify_convoy_commit_task"));
            Assert.AreEqual(Math.Min(i + 1, 3), Item("escort", Wed.AddMinutes(10)).Count);
        }

        Assert.IsTrue(Item("escort", Wed.AddMinutes(10)).Done);
    }

    [TestMethod]
    public void TheDailyReset_Is07_00_AndTheListEmptiesByItself_WithNoGameRunning()
    {
        var beforeReset = new DateTime(2026, 11, 4, 6, 59, 0);
        Feed(_log.Show(beforeReset, "UI_GUILD_DONATE"), _log.Show(beforeReset.AddSeconds(5), "UI_ITEM_GET_TIP"));
        Assert.IsTrue(Item("donate", beforeReset.AddSeconds(30)).Done);
        Assert.IsFalse(Item("donate", beforeReset.AddMinutes(2)).Done, "07:01: a new game day, though no event arrived");
        Assert.AreEqual(new DateOnly(2026, 11, 3), _t.Snapshot(beforeReset).GameDay);
        Assert.AreEqual(new DateTime(2026, 11, 4, 7, 0, 0), _t.Snapshot(beforeReset).NextDailyReset);
    }

    [TestMethod]
    public void HonorWar_IsAWeeklyCounter_FromMonday07_00()
    {
        var sunday = new DateTime(2026, 11, 1, 23, 55, 0);
        Feed(_log.Push(sunday, "honor_war_network", "notify_honor_war_stage", true),
            _log.Push(sunday.AddHours(8), "honor_war_network", "notify_honor_war_stage", false),
            _log.Push(sunday.AddHours(9), "honor_war_network", "notify_honor_war_stage", true));
        var tuesday = new DateTime(2026, 11, 3, 20, 0, 0);
        Assert.AreEqual(2, Item("honor", tuesday).Count, "Sunday's round belongs to last week");
        Assert.AreEqual(new DateTime(2026, 11, 9, 7, 0, 0), _t.Snapshot(tuesday).NextWeeklyReset);
    }

    [TestMethod]
    public void TheUsersTick_WinsUntilTheReset_AndChoosingWhatTheLogSaysClearsIt()
    {
        Assert.IsTrue(_t.SetDone("tower", true, Wed));
        var corridor = Item("tower", Wed.AddMinutes(1));
        Assert.IsTrue(corridor.Done);
        Assert.AreEqual(DailySource.You, corridor.Source);
        Assert.IsFalse(Item("tower", Wed.AddHours(4)).Done, "after 07:00 the tick is gone");

        Feed(_log.Show(Wed, "UI_GUILD_DONATE"), _log.Show(Wed.AddSeconds(5), "UI_ITEM_GET_TIP"));
        _t.SetDone("donate", false, Wed.AddMinutes(1));
        Assert.IsFalse(Item("donate", Wed.AddMinutes(2)).Done);
        Assert.IsTrue(Item("donate", Wed.AddMinutes(2)).AutoDone);
        _t.SetDone("donate", true, Wed.AddMinutes(3));
        Assert.AreEqual(DailySource.Game, Item("donate", Wed.AddMinutes(4)).Source, "back to what the log says");
        Assert.IsFalse(_t.SetDone("nope", true, Wed));
    }

    [TestMethod]
    public void CompanionJobs_TickAJobItem_AndTheRealListHasNoneSinceTheLearningDay()
    {
        Assert.IsFalse(_t.OnJob("Craft", "Succeeded", Wed), "crafting, gathering and Tea House auto-play left the list (entries 31/33)");
        var t = new DailyTracker(DailyCatalog.Parse(System.Text.Encoding.UTF8.GetBytes(
            "{ \"schemaVersion\": 1, \"sections\": [ { \"id\": \"comp\", \"title\": \"The companion\" } ], "
            + "\"items\": [ { \"id\": \"craft\", \"name\": \"Crafting\", \"section\": \"comp\", \"rules\": [ { \"on\": \"job\", \"jobs\": [ \"Craft\" ] } ] } ] }")));
        t.OnJob("Craft", "Cancelled", Wed);
        Assert.IsFalse(t.Snapshot(Wed).Items.Single().Done);
        t.OnJob("Craft", "Succeeded", Wed);
        Assert.AreEqual(DailySource.Companion, t.Snapshot(Wed).Items.Single().Source);
    }

    [TestMethod]
    public void EventSwitches_AndWindows_TellWhetherAnEventIsOpen()
    {
        Assert.IsFalse(Item("spa", Wed).Open, "03:48 is outside 21:59-22:19");
        Feed(_log.Switch(Wed, "guild_spa_21_days_later", true));
        Assert.IsTrue(Item("spa", Wed).Open, "the server says it is on");
        Feed(_log.Switch(Wed.AddMinutes(20), "guild_spa_21_days_later", false));
        Assert.IsFalse(Item("spa", Wed.AddMinutes(21)).Open);
        var evening = new DateTime(2026, 11, 4, 22, 5, 0);
        Assert.IsTrue(Item("spa", evening).Open, "inside its window with no switch seen");
    }

    [TestMethod]
    public void TheSavedState_RestoresCounts_AndTheReplayOfAlreadyCountedLinesAddsNothing()
    {
        var events = new[] { _log.Push(Wed, "convoy_mgr", "notify_convoy_commit_task"), _log.Push(Wed.AddMinutes(1), "convoy_mgr", "notify_convoy_commit_task") };
        Feed(events);
        _t.SetDone("tower", true, Wed);
        var state = _t.Export();

        var restored = new DailyTracker(Data.Catalog());
        restored.Import(state);
        foreach (var e in events)
        {
            restored.Feed(e); // the start-up replay reads the same lines again
        }

        restored.Feed(_log.Push(Wed.AddMinutes(2), "convoy_mgr", "notify_convoy_commit_task"));
        var snap = restored.Snapshot(Wed.AddMinutes(3));
        Assert.AreEqual(3, snap.Items.Single(i => i.Id == "escort").Count);
        Assert.IsTrue(snap.Items.Single(i => i.Id == "tower").Done);
    }

    [TestMethod]
    public void TheService_HoldsLiveEventsUntilTheReplayIsDone_AndCountsEachLineOnce()
    {
        var svc = new DailyService(Data.Catalog(), null, () => Wed.AddMinutes(10));
        var a = _log.Push(Wed, "convoy_mgr", "notify_convoy_commit_task");
        var b = _log.Push(Wed.AddMinutes(1), "convoy_mgr", "notify_convoy_commit_task");
        svc.Feed([b]);
        Assert.AreEqual(0, svc.Snapshot().Items.Single(i => i.Id == "escort").Count, "held while replaying");
        svc.CompleteReplay([a, b]);
        Assert.AreEqual(2, svc.Snapshot().Items.Single(i => i.Id == "escort").Count);

        var bytes = svc.TakeStateToSave();
        Assert.IsNotNull(bytes);
        Assert.IsNull(svc.TakeStateToSave(), "nothing changed since");
        var again = new DailyService(Data.Catalog(), bytes, () => Wed.AddMinutes(10));
        again.CompleteReplay([a, b]);
        Assert.AreEqual(2, again.Snapshot().Items.Single(i => i.Id == "escort").Count);
        Assert.AreEqual("dailies.stateUnreadable", new DailyService(Data.Catalog(), "{oops"u8.ToArray(), () => Wed).StateProblem);
    }

    [TestMethod]
    public void TheUsersOwnItems_AndListChoices_AreSaved_AndComeBack()
    {
        var svc = new DailyService(Data.Catalog(), null, () => Wed);
        svc.CompleteReplay([]);
        Assert.IsNull(svc.TakePrefsToSave(), "nothing chosen yet");
        svc.SetPrefs(new DailyPrefs
        {
            Shown = new Dictionary<string, bool> { ["arena"] = false },
            Names = new Dictionary<string, string> { ["donate"] = "Donation" },
            Custom = [new DailyCustomItem { Id = "own1", Name = "Daily bounty" }, new DailyCustomItem { Id = "donate", Name = "clash" }],
        });
        Assert.IsTrue(svc.SetDone("own1", true));
        var snap = svc.Snapshot();
        Assert.AreEqual("own", snap.Items.Single(i => i.Id == "own1").Section);
        Assert.AreEqual(DailyTracker.OwnSection, snap.Sections[^1].Id);
        Assert.AreEqual(1, snap.Items.Count(i => i.Id == "donate"), "an own item may not take a catalog id");

        var prefs = svc.TakePrefsToSave();
        var state = svc.TakeStateToSave();
        Assert.IsNotNull(prefs);
        Assert.IsNotNull(state);
        var again = new DailyService(Data.Catalog(), state, () => Wed.AddMinutes(1), prefs);
        again.CompleteReplay([]);
        Assert.IsTrue(again.Snapshot().Items.Single(i => i.Id == "own1").Done, "the tick on an own item survives a restart");
        Assert.IsFalse(again.Prefs.IsShown(again.Snapshot().Items.Single(i => i.Id == "arena")));
        Assert.AreEqual("Donation", again.Prefs.NameOf(again.Snapshot().Items.Single(i => i.Id == "donate")));
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(again.TodayFile()), "Daily bounty");
    }

    private void Feed(params LogEvent[] events)
    {
        foreach (var e in events)
        {
            _t.Feed(e);
        }
    }

    private DailyItemView Item(string id, DateTime now) => _t.Snapshot(now).Items.Single(i => i.Id == id);
}
