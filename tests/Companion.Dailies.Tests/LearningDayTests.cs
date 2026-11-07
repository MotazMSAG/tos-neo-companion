using Companion.Contracts.Logs;

namespace Companion.Dailies.Tests;

/// <summary>
/// The learning day's rules (DAILIES_MODULE.md entries 1-42) against the traces of Wednesday 2026-11-04's evening log
/// (times as logged, PC local time). The game day runs from Wednesday 07:00.
/// </summary>
[TestClass]
public sealed class LearningDayTests
{
    private static readonly DateTime Evening = new(2026, 11, 4, 20, 0, 0);

    private readonly Log _log = new();
    private DailyTracker _t = null!;

    [TestInitialize]
    public void Init() => _t = new DailyTracker(Data.Catalog());

    [TestMethod]
    public void TheList_FollowsTheUsersDecisions()
    {
        var ids = Data.Catalog().Items.Select(i => i.Id).ToHashSet();
        foreach (var gone in new[] { "mail", "activity", "teaTasks", "login", "monthly", "crib", "food", "plant", "halloween", "halfYear", "talent", "craft", "teaAuto", "gather", "lumi", "helper", "worship" })
        {
            Assert.IsFalse(ids.Contains(gone), gone + " was removed");
        }

        var cat = Data.Catalog();
        Assert.AreEqual("Horse guardian", cat.Items.Single(i => i.Id == "guildBoss").Name);
        Assert.AreEqual("Big raccoon", cat.Items.Single(i => i.Id == "bear").Name);
        Assert.AreEqual("Corridor", cat.Items.Single(i => i.Id == "tower").Name);
        Assert.IsEmpty(cat.Items.Single(i => i.Id == "tower").Rules, "the corridor is ticked by hand");
        Assert.AreEqual(3, cat.Items.Single(i => i.Id == "escort").PerDay, "the user corrected 5/5 to 3/3");
        Assert.AreEqual(4, cat.Items.Single(i => i.Id == "gearRift").PerDay);
    }

    [TestMethod]
    public void Forge_APopUpAndItsToastInTheSameSecond_AreOneForge_EightAreDone()
    {
        var t = new DateTime(2026, 11, 4, 21, 6, 19);
        Feed(_log.Show(t, "UI_GUILD_FORGE"));
        foreach (var s in new[] { 4, 6, 9, 11, 17, 19, 21, 24 })
        {
            Feed(_log.Popup(t.AddSeconds(s)), _log.Toast(t.AddSeconds(s + 0.2)));
        }

        Assert.AreEqual(8, Item("forge", t.AddMinutes(1)).Count);
        Assert.IsTrue(Item("forge", t.AddMinutes(1)).Done);
    }

    [TestMethod]
    public void Orders_EachHandInCounts_TowardTwelve_AndTheBlackMarketRefreshTicks()
    {
        var t = new DateTime(2026, 11, 4, 20, 52, 40);
        Feed(_log.Guide(t, "order_take3"), _log.Guide(t, "order_take4"), _log.Guide(t.AddSeconds(1), "order_take3"));
        Assert.AreEqual(2, Item("orders", t.AddMinutes(1)).Count, "order_take4 is the same hand-in");
        Feed(_log.ShopRefresh(t.AddMinutes(1), 10028), _log.ShopRefresh(t.AddMinutes(2), 999));
        Assert.IsTrue(Item("blackMarket", t.AddMinutes(3)).Done);
    }

    /// <summary>One click on the train's one-click fill: the dialog, its close, and (when it gave one) the reward.</summary>
    private void OneKeyFill(DateTime open, double closeAfter, bool rewarded = true) => Feed(Fill(open, closeAfter, rewarded));

    private LogEvent[] Fill(DateTime open, double closeAfter, bool rewarded = true)
    {
        var closed = open.AddSeconds(closeAfter);
        LogEvent[] dialog = [_log.Show(open, "UI_TRAIN_ONE_KEY_EQUIP_VIEW"), _log.Close(closed, "UI_TRAIN_ONE_KEY_EQUIP_VIEW")];
        return rewarded
            ? [.. dialog, _log.Toast(closed.AddMilliseconds(60)), _log.Toast(closed.AddMilliseconds(70)), _log.Popup(closed.AddMilliseconds(150))]
            : dialog;
    }

    [TestMethod]
    public void HugoTrain_Wednesday_FourRewardedOneClickFills_AreTheThreeRequests_AndTheSpecialTrainGoesToThree()
    {
        // 11-04 20:04-20:07 as logged: the train screen, the crate tips and "ask for help" screens (no longer counted), five
        // one-click fills (one closed without a reward), a stray toast while the dialog was open and one 20 s later
        var t = new DateTime(2026, 11, 4, 20, 4, 37);
        Feed(_log.Show(t, "UI_LITTLE_TRAIN_VIEW"));
        Feed(_log.Show(new DateTime(2026, 11, 4, 20, 6, 59), "UI_LITTLE_TRAIN_BOX_NEW_TIPS"), _log.Show(new DateTime(2026, 11, 4, 20, 7, 0), "UI_LITTLE_TRAIN_ASK_HELP_VIEW"),
            _log.Close(new DateTime(2026, 11, 4, 20, 7, 4, 471), "UI_LITTLE_TRAIN_ASK_HELP_VIEW"));
        OneKeyFill(new DateTime(2026, 11, 4, 20, 7, 6, 514), 1.738);
        Feed(_log.Show(new DateTime(2026, 11, 4, 20, 7, 9, 663), "UI_TRAIN_ONE_KEY_EQUIP_VIEW"), _log.Toast(new DateTime(2026, 11, 4, 20, 7, 9, 768)),
            _log.Close(new DateTime(2026, 11, 4, 20, 7, 10, 622), "UI_TRAIN_ONE_KEY_EQUIP_VIEW"));
        Feed(_log.Toast(new DateTime(2026, 11, 4, 20, 7, 10, 649)), _log.Popup(new DateTime(2026, 11, 4, 20, 7, 10, 713)));
        OneKeyFill(new DateTime(2026, 11, 4, 20, 7, 11, 894), 0.259, rewarded: false);
        OneKeyFill(new DateTime(2026, 11, 4, 20, 7, 13, 274), 1.020);
        Feed(_log.Toast(new DateTime(2026, 11, 4, 20, 7, 34, 278)));
        OneKeyFill(new DateTime(2026, 11, 4, 20, 7, 35, 855), 1.480);

        var train = Item("train", t.AddMinutes(5));
        Assert.AreEqual(3, train.Count);
        Assert.IsTrue(train.Done);
        Assert.AreEqual(0, train.Stack, "the user's 0/9 that night");
        Assert.AreEqual(3, Item("specialTrain", t.AddMinutes(5)).Stack, "the user's 3/6: the 4th fill (nothing stored) is no request");
        Assert.AreEqual(3, Item("train", t.AddHours(12)).Stack, "Thursday 07:00 adds three");
        Assert.AreEqual(9, Item("train", t.AddDays(4)).Stack, "never more than 9");

        Assert.IsTrue(_t.SetStack("train", 5, t.AddMinutes(6)));
        Assert.AreEqual(5, Item("train", t.AddMinutes(7)).Stack);
        Assert.IsFalse(_t.SetStack("arena", 1, t), "no stack");
    }

    [TestMethod]
    public void HugoTrain_Thursday_ThreeRequestsFillTheSpecialTrain_AndTheFourthFillIsTheSpecialTrain()
    {
        // 11-05 19:49 as logged: 3 waiting and 3 of 6 at the start; afterwards the game showed 0/9 and 0/6
        var t = new DateTime(2026, 11, 5, 19, 49, 12, 38);
        Assert.IsTrue(_t.SetStack("specialTrain", 3, t.AddMinutes(-10)));
        Feed(_log.Show(t, "UI_LITTLE_TRAIN_VIEW"));
        OneKeyFill(new DateTime(2026, 11, 5, 19, 49, 14, 816), 2.121);
        OneKeyFill(new DateTime(2026, 11, 5, 19, 49, 42, 727), 0.941);
        OneKeyFill(new DateTime(2026, 11, 5, 19, 49, 46, 607), 1.080);
        var ready = Item("specialTrain", new DateTime(2026, 11, 5, 19, 49, 50));
        Assert.AreEqual(6, ready.Stack);
        Assert.IsFalse(ready.Done, "ready to summon");
        OneKeyFill(new DateTime(2026, 11, 5, 19, 49, 55, 27), 2.900);

        var now = new DateTime(2026, 11, 5, 19, 55, 0);
        var train = Item("train", now);
        Assert.AreEqual(3, train.Count);
        Assert.IsTrue(train.Done);
        Assert.AreEqual(0, train.Stack, "0/9");
        var special = Item("specialTrain", now);
        Assert.AreEqual(0, special.Stack, "0/6: summoned and filled");
        Assert.IsNull(special.TimerUntil, "filled: no countdown");
        Assert.IsTrue(special.Done);
    }

    [TestMethod]
    public void SpecialTrain_ATickMeansSummoned_WithATwoHourTimer_AndFillingItEndsTheTimer()
    {
        var t = new DateTime(2026, 11, 4, 20, 7, 0);
        Assert.IsTrue(_t.SetStack("train", 0, t));
        Assert.IsTrue(_t.SetStack("specialTrain", 6, t));
        Assert.IsFalse(Item("specialTrain", t.AddMinutes(2)).Done, "ready to summon");
        Assert.IsTrue(_t.SetDone("specialTrain", true, t.AddMinutes(3)));
        var summoned = Item("specialTrain", t.AddMinutes(4));
        Assert.AreEqual(0, summoned.Stack);
        Assert.AreEqual(t.AddMinutes(123), summoned.TimerUntil);
        Assert.IsTrue(summoned.Done);
        Assert.IsNull(Item("specialTrain", t.AddHours(3)).TimerUntil, "it lapses after 2 hours");

        Feed(_log.Show(t.AddMinutes(10), "UI_LITTLE_TRAIN_VIEW"));
        OneKeyFill(t.AddMinutes(10).AddSeconds(3), 1.5);
        Assert.IsNull(Item("specialTrain", t.AddMinutes(12)).TimerUntil, "the summoned train was filled");
        Assert.AreEqual(0, Item("train", t.AddMinutes(12)).Count, "no regular request was waiting");
    }

    [TestMethod]
    public void Yggdrasil_ThreeSweeps_ThenNoAttemptsLeft_IsZero_AndItIsOnlyDueAtFourStored()
    {
        var t = new DateTime(2026, 11, 4, 21, 11, 25);
        _t.SetStack("ygg", 3, t.AddSeconds(-1));
        Feed(_log.Show(t, "UI_CHANGE_BEAD_TOWER"), _log.Settle(t.AddSeconds(4.7)), _log.Settle(t.AddSeconds(4.9)), _log.Settle(t.AddSeconds(5.6)),
            _log.Reply(t.AddSeconds(5.8), "devine_tree.devine_tree_sweep", 11));
        var ygg = Item("ygg", t.AddMinutes(1));
        Assert.AreEqual(0, ygg.Stack);
        Assert.IsTrue(ygg.Done, "nothing due");
        Assert.IsTrue(Item("ygg", t.AddDays(3)).Done, "3 stored: still quiet");
        Assert.IsFalse(Item("ygg", t.AddDays(4)).Done, "4 stored: due");
        Assert.AreEqual(5, Item("ygg", t.AddDays(9)).Stack);
    }

    [TestMethod]
    public void DemonTrial_OnlyOnItsDays_CountsRewardRuns_NotHelpingRuns()
    {
        var tue = new DateTime(2026, 11, 3, 18, 9, 39);
        Assert.IsFalse(Item("demon", Evening).Today, "Wednesday has no demon trial");
        Assert.IsTrue(Item("demon", tue).Today);

        // Tuesday 11-03 as logged: room_finish 30 (loot drops) on the 18:09, 18:58 and 19:13 runs, 3 (no drops) on the
        // 19:02, 19:06 and 19:08 helping runs; the 19:08 run met the periodic 10-minute toast (19:12:15)
        foreach (var (start, finish, end, seconds) in new[]
        {
            ("18:09:39", "18:12:07", "18:12:11", 30.0), ("18:58:33", "19:02:01", "19:02:04", 30.0), ("19:02:49", "19:06:07", "19:06:08", 3.0),
            ("19:06:57", "19:07:26", "19:07:26", 3.0), ("19:08:44", "19:12:25", "19:12:26", 3.0), ("19:13:32", "19:17:10", "19:17:13", 30.0),
        })
        {
            Feed(_log.Map(At(start), "lianyumoshen_0" + (start == "18:09:39" ? "1" : "2")));
            if (start == "19:08:44")
            {
                Feed(_log.Toast(At("19:12:15")));
            }

            Feed(_log.RoomFinish(At(finish), seconds), _log.Settle(At(end)), _log.Settle(At(end).AddSeconds(4)), _log.Map(At(end).AddSeconds(5), "haigangcheng"));
        }

        var demon = Item("demon", tue.AddHours(1.5));
        Assert.AreEqual(2, demon.Count, "two reward runs (one per visit, however many results screens), no helping runs");
        Assert.IsTrue(demon.Done);

        static DateTime At(string hms) => new DateTime(2026, 11, 3).Add(TimeSpan.Parse(hms, System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(0, demon.Stack, "two rewards used of the day's two");
        Assert.AreEqual(0, Item("demon", new DateTime(2026, 11, 4, 8, 0, 0)).Stack, "Wednesday adds nothing");
        Assert.AreEqual(2, Item("demon", new DateTime(2026, 11, 5, 8, 0, 0)).Stack, "Thursday adds two");
        Assert.AreEqual(4, Item("demon", new DateTime(2026, 11, 8, 8, 0, 0)).Stack, "skipping Thursday: Saturday stacks to 4, no more");
    }

    [TestMethod]
    public void DemonTrial_Thursday_TwoRewardRuns_TheSecondWithoutAnyToastInIt_AreTwoOfTwo()
    {
        // 11-05 as logged: both runs end with room_finish 30 and loot; the first met the periodic toast (23:45:00), the
        // second had no toast in the map at all (4.35 counted 1 of 2: the user's report)
        var d = new DateTime(2026, 11, 5);
        Feed(_log.Map(d.Add(new TimeSpan(23, 41, 58)), "lianyumoshen_01"), _log.Toast(d.Add(new TimeSpan(0, 23, 45, 0, 742))),
            _log.RoomFinish(d.Add(new TimeSpan(23, 45, 7)), 30), _log.Settle(d.Add(new TimeSpan(23, 45, 11))), _log.Map(d.Add(new TimeSpan(23, 45, 18)), "haigangcheng"),
            _log.Map(d.Add(new TimeSpan(23, 45, 36)), "lianyumoshen_01"), _log.RoomFinish(d.Add(new TimeSpan(23, 48, 42)), 30),
            _log.Settle(d.Add(new TimeSpan(23, 48, 46))), _log.Map(d.Add(new TimeSpan(23, 48, 50)), "haigangcheng"));
        var demon = Item("demon", d.Add(new TimeSpan(23, 50, 0)));
        Assert.AreEqual(2, demon.Count);
        Assert.IsTrue(demon.Done);
    }

    [TestMethod]
    public void Abyssal_NineFloors_OnItsDays_AndGearRiftFourRuns_AndShiftingSpace()
    {
        // in log order: shifting space 20:27, gear rift 20:30, abyssal 21:41
        var b = new DateTime(2026, 11, 4, 20, 27, 38);
        Feed(_log.Map(b, "bianshenzhu"));
        Assert.IsFalse(Item("shifting", b.AddMinutes(1)).Done, "entering is not finishing");
        Feed(_log.Settle(b.AddSeconds(97)));
        Assert.IsTrue(Item("shifting", b.AddMinutes(2)).Done);

        var g = new DateTime(2026, 11, 4, 20, 30, 27);
        Feed(_log.Map(g, "yjfuben"), _log.Settle(g.AddMinutes(2)), _log.Map(g.AddMinutes(3), "yiji"), _log.Map(g.AddMinutes(4), "yjfuben"), _log.Settle(g.AddMinutes(5)),
            _log.Map(g.AddMinutes(6), "hgcfuben"), _log.Settle(g.AddMinutes(7)), _log.Map(g.AddMinutes(8), "hgcfuben"), _log.Settle(g.AddMinutes(9)));
        Assert.IsTrue(Item("gearRift", g.AddMinutes(10)).Done);

        var t = new DateTime(2026, 11, 4, 21, 41, 0);
        for (var i = 0; i < 10; i++)
        {
            Feed(_log.Map(t.AddMinutes(i), "doujichang_0" + (i % 7 + 1)), _log.Settle(t.AddMinutes(i).AddSeconds(30)));
        }

        Assert.AreEqual(9, Item("abyssal", t.AddMinutes(20)).Count);
        Assert.IsTrue(Item("abyssal", t.AddMinutes(20)).Done);
        Assert.IsFalse(Item("abyssal", new DateTime(2026, 11, 3, 20, 0, 0)).Today, "not on Tuesday");
    }

    [TestMethod]
    public void Raccoons_FightsInTheGuildWhileOut_Count_AndMissingTheLastRoundIsMissed()
    {
        var t = new DateTime(2026, 11, 4, 19, 44, 39);
        Feed(_log.Switch(t, "guild_bear2", true), _log.Map(t.AddMinutes(2), "gonghuilingdi"));
        foreach (var m in new[] { 2.5, 3.5, 4.5, 6.0 })
        {
            Feed(_log.Settle(t.AddMinutes(m)));
        }

        Feed(_log.Switch(t.AddMinutes(20), "guild_bear2", false), _log.Settle(t.AddMinutes(21)));
        var raccoons = Item("raccoons", t.AddMinutes(25));
        Assert.AreEqual(4, raccoons.Count, "the fight after they left does not count");
        Assert.IsFalse(raccoons.Missed, "21:24 is still to come");
        Assert.IsTrue(Item("raccoons", new DateTime(2026, 11, 4, 21, 45, 0)).Missed, "the last round ended at 4/5");
        Assert.IsTrue(Item("raccoons", new DateTime(2026, 11, 4, 21, 30, 0)).Open);
    }

    [TestMethod]
    public void WorldBoss_TheFightInItsTime_AndTheBoxRightAfter_AreBothNeeded()
    {
        var t = new DateTime(2026, 11, 4, 20, 2, 36);
        Feed(_log.Map(t.AddMinutes(-9), "yiji"), _log.Settle(t));
        var boss = Item("wboss", t.AddSeconds(1));
        Assert.AreEqual(1, boss.Count);
        Assert.IsFalse(boss.Done, "the box is still to open");
        Feed(_log.Show(t.AddSeconds(8), "UI_NORMAL_PROGRESS"), _log.Popup(t.AddSeconds(9)));
        Assert.IsTrue(Item("wboss", t.AddSeconds(10)).Done);

        Init();
        var outside = new DateTime(2026, 11, 4, 20, 38, 21);
        Feed(_log.Map(outside.AddMinutes(-1), "hgcfuben"), _log.Settle(outside), _log.Map(outside.AddMinutes(1), "yiji"), _log.Settle(outside.AddMinutes(2)));
        Assert.AreEqual(0, Item("wboss", outside.AddMinutes(3)).Count, "a dungeon, then a results screen outside its times");
        var windows = Item("wboss", outside).Windows!;
        Assert.AreEqual(new DateTime(2026, 11, 5, 2, 0, 0), windows[^1].Start, "02:00 belongs to the night");
    }

    [TestMethod]
    public void GuildSpa_BeingInTheGuildWhileItRuns_IsDone_AndNotGoingIsMissed()
    {
        var t = new DateTime(2026, 11, 4, 21, 56, 40);
        Feed(_log.Map(t, "gonghuilingdi"));
        Assert.IsFalse(Item("spa", t.AddMinutes(1)).Done, "not open yet");
        Feed(_log.Switch(t.AddMinutes(3), "guild_spa_21_days_later", true));
        Assert.IsTrue(Item("spa", t.AddMinutes(4)).Done, "already there when it opened");

        Init();
        Feed(_log.Map(t, "yiji"), _log.Switch(t.AddMinutes(3), "guild_spa_21_days_later", true), _log.Show(t.AddMinutes(10), "UI_BAG_MAIN"));
        Assert.IsFalse(Item("spa", t.AddMinutes(11)).Done);
        Assert.IsTrue(Item("spa", t.AddMinutes(25)).Missed);
    }

    [TestMethod]
    public void EventDays_ComeFromTheCatalog_AndFromTheServersOwnSwitches()
    {
        Assert.IsFalse(Item("boat", Evening).Today, "the boat race is Friday's");
        Assert.IsFalse(Item("battle", Evening).Today);
        Feed(_log.Switch(new DateTime(2026, 11, 4, 22, 19, 39), "king_guild_battle_1", true));
        Assert.IsTrue(Item("battle", Evening).Today, "seen on a Wednesday now");
        var state = _t.Export();
        var again = new DailyTracker(Data.Catalog());
        again.Import(state);
        Assert.IsTrue(again.Snapshot(new DateTime(2026, 11, 11, 20, 0, 0)).Items.Single(i => i.Id == "battle").Today, "learned for every Wednesday");
    }

    [TestMethod]
    public void Lumi_QueueingStartsTwoHours_ReadyAfter_OpeningWithoutTakingRestarts_AllSixTakenIsEmpty()
    {
        var q = new DateTime(2026, 11, 4, 20, 53, 28);
        Assert.AreEqual(ProducerState.Empty, Card("lumiCard", q).State);
        Feed(_log.Show(q.AddSeconds(-7), "UI_HOME_PET_VIEW"));
        for (var i = 0; i < 6; i++)
        {
            Feed(_log.Show(q.AddSeconds(i * 2), "UI_HOME_PET_MAKE_VIEW"), _log.Close(q.AddSeconds(i * 2 + 1), "UI_HOME_PET_MAKE_VIEW"));
        }

        Feed(_log.Close(q.AddSeconds(20), "UI_HOME_PET_VIEW"));
        var card = Card("lumiCard", q.AddMinutes(1));
        Assert.AreEqual(ProducerState.Making, card.State, "queued while open: the timer runs");
        Assert.AreEqual(q.AddSeconds(10).AddMinutes(120), card.Until);
        var ready = q.AddHours(2.5);
        Assert.AreEqual(ProducerState.Ready, Card("lumiCard", ready).State);

        // opened at Ready, closed with nothing taken: probably nothing is done yet, a fresh 2 h
        Feed(_log.Show(ready, "UI_HOME_PET_VIEW"), _log.Close(ready.AddSeconds(10), "UI_HOME_PET_VIEW"));
        Assert.AreEqual(ProducerState.Making, Card("lumiCard", ready.AddMinutes(1)).State);
        Assert.AreEqual(ready.AddSeconds(10).AddHours(2), Card("lumiCard", ready.AddMinutes(1)).Until);

        // 20:52:00-01: six item toasts with the lumi open = all six taken
        var take = ready.AddHours(3);
        Feed(_log.Show(take, "UI_HOME_PET_VIEW"));
        for (var i = 0; i < 6; i++)
        {
            Feed(_log.Toast(take.AddSeconds(5 + i * 0.2)));
        }

        Feed(_log.Close(take.AddSeconds(15), "UI_HOME_PET_VIEW"));
        Assert.AreEqual(ProducerState.Empty, Card("lumiCard", take.AddMinutes(1)).State);
    }

    [TestMethod]
    public void Helper_AllSixSlotsTaken_IsEmpty_FewerKeepsTheTimer()
    {
        var t = new DateTime(2026, 11, 4, 20, 59, 32);
        Feed(_log.Show(t.AddSeconds(-40), "UI_HOME_HELPER_VIEW"), _log.Show(t, "UI_HOME_HELPER_ORDER_VIEW"), _log.Close(t.AddSeconds(19), "UI_HOME_HELPER_VIEW"));
        Assert.AreEqual(ProducerState.Making, Card("helperCard", t.AddMinutes(1)).State);

        var c = t.AddMinutes(30);
        Feed(_log.Show(c, "UI_HOME_HELPER_VIEW"), _log.Slot(c.AddSeconds(1), 3), _log.Slot(c.AddSeconds(2), 4), _log.Close(c.AddSeconds(4), "UI_HOME_HELPER_VIEW"));
        Assert.AreEqual(ProducerState.Making, Card("helperCard", c.AddMinutes(1)).State, "collecting never touches the countdown");
        Feed(_log.Show(c.AddMinutes(5), "UI_HOME_HELPER_VIEW"));
        var k = 0;
        foreach (var s in new[] { 6, 1, 1, 2, 5 })
        {
            Feed(_log.Slot(c.AddMinutes(5).AddSeconds(++k), s));
        }

        Feed(_log.Close(c.AddMinutes(6), "UI_HOME_HELPER_VIEW"));
        Assert.AreEqual(ProducerState.Empty, Card("helperCard", c.AddMinutes(7)).State, "slots 1-6 all taken since the queue");
    }

    [TestMethod]
    public void Alerts_AnEventOpening_AndAProducerReady_OnceEach_NotForDoneEvents()
    {
        var q = new DateTime(2026, 11, 4, 19, 30, 0);
        Feed(_log.Show(q, "UI_HOME_PET_MAKE_VIEW"));
        var alerts = _t.Alerts(q, new DateTime(2026, 11, 4, 19, 45, 0));
        var raccoons = alerts.Single(a => a.ItemId == "raccoons");
        Assert.AreEqual(DailyAlertKind.EventOpen, raccoons.Kind);
        Assert.AreEqual(5, raccoons.Left);
        Assert.IsFalse(raccoons.Last);
        Assert.IsEmpty(_t.Alerts(new DateTime(2026, 11, 4, 19, 45, 0), new DateTime(2026, 11, 4, 19, 50, 0)), "said once");
        Assert.AreEqual("lumiCard", _t.Alerts(new DateTime(2026, 11, 4, 21, 29, 0), new DateTime(2026, 11, 4, 21, 31, 0)).Single(a => a.Kind == DailyAlertKind.ProducerReady).ItemId);

        var w = new DateTime(2026, 11, 4, 20, 0, 0);
        _t.SetDone("wboss", true, w.AddMinutes(-1));
        Assert.IsFalse(_t.Alerts(w.AddMinutes(-1), w.AddMinutes(1)).Any(a => a.ItemId == "wboss"), "done already");
        var last = _t.Alerts(new DateTime(2026, 11, 4, 21, 23, 0), new DateTime(2026, 11, 4, 21, 25, 0)).Single(a => a.ItemId == "raccoons");
        Assert.IsTrue(last.Last, "21:24 is today's last round");
    }

    [TestMethod]
    public void TheNewState_StacksProducersAndParts_SurvivesARestart()
    {
        var t = new DateTime(2026, 11, 4, 20, 2, 36);
        Feed(_log.Map(t.AddMinutes(-9), "yiji"), _log.Settle(t), _log.Show(t.AddSeconds(30), "UI_HOME_PET_MAKE_VIEW"));
        _t.SetStack("train", 7, t.AddMinutes(1));
        var state = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_t.Export(), new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) },
        });
        var again = new DailyService(Data.Catalog(), state, () => t.AddMinutes(3));
        again.CompleteReplay([]);
        var snap = again.Snapshot();
        Assert.AreEqual(7, snap.Items.Single(i => i.Id == "train").Stack);
        Assert.AreEqual(1, snap.Items.Single(i => i.Id == "wboss").Count, "the fight part");
        Assert.AreEqual(ProducerState.Making, snap.Producers!.Single(p => p.Id == "lumiCard").State);
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(again.TodayFile()), "lumiCard");
    }

    [TestMethod]
    public void AStateCountedWithOtherRules_IsRecountedFromTodaysLog_KeepingTheUsersTicksAndOlderDays()
    {
        // Release 4.35's first start: 4.34's state.json said "counted up to 22:19" with its own rules, so the new rules
        // never saw tonight's log (Arena 1/5 instead of 5/5, the hand-only Corridor ticked at 20:10)
        var now = new DateTime(2026, 11, 4, 22, 19, 0);
        var arena = new DateTime(2026, 11, 4, 20, 51, 32);
        var old = new DailyState
        {
            Watermark = now,
            Catalog = null,
            Progress =
            [
                new DailyProgressDto { Item = "arena", Day = new DateOnly(2026, 11, 4), Count = 1, First = arena.AddMinutes(-6), Last = arena.AddMinutes(-6), Source = DailySource.Game },
                new DailyProgressDto { Item = "tower", Day = new DateOnly(2026, 11, 4), Count = 1, First = now.AddHours(-2), Last = now.AddHours(-2), Source = DailySource.Game },
                new DailyProgressDto { Item = "escort", Day = new DateOnly(2026, 10, 30), Count = 3, First = new DateTime(2026, 10, 30, 21, 36, 0), Last = new DateTime(2026, 10, 30, 21, 43, 0), Source = DailySource.Game },
            ],
            Overrides = [new DailyOverrideDto { Item = "advPass", Day = new DateOnly(2026, 11, 4), Done = true }],
        };
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(old, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        var svc = new DailyService(Data.Catalog(), bytes, () => now);
        Assert.IsTrue(svc.Recounted);
        // the log reaches back to Monday morning (the replay start), so the replay recounts everything since then
        svc.CompleteReplay([_log.Map(new DateTime(2026, 11, 2, 7, 5, 0), "zhucheng"), _log.Show(arena, "UI_ARENA_VIEW"), .. Enumerable.Range(8, 5).Select(s => _log.Settle(arena.AddSeconds(s)))]);
        var snap = svc.Snapshot();
        Assert.AreEqual(5, snap.Items.Single(i => i.Id == "arena").Count, "recounted with the new rule");
        Assert.IsFalse(snap.Items.Single(i => i.Id == "tower").Done, "the old automatic Corridor tick is gone");
        Assert.IsTrue(snap.Items.Single(i => i.Id == "advPass").Done, "the user's tick stays");

        var saved = svc.TakeStateToSave()!;
        var again = new DailyService(Data.Catalog(), saved, () => now.AddMinutes(1));
        Assert.IsFalse(again.Recounted, "the same rules: no recount");
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(saved), "2026-10-30", "days before the replay (Monday 07:00 here) are kept");
    }

    [TestMethod]
    public void HyperweatherDays_HaveNoGuildRaccoons()
    {
        // as logged: the 'spec_weather_*' switches went off on Wednesday 05:59-06:49 (raccoons all day) and on again on
        // Thursday 06:59-07:59 (no raccoon round; the user: "Today is hyperweather, so there's no raccoons")
        var wed = new DateTime(2026, 11, 4, 5, 59, 40);
        Feed(_log.Switch(wed.AddDays(-1).AddHours(11), "spec_weather_challenge_pve_3", true), _log.Switch(wed, "spec_weather_challenge_pve_3", false));
        var wedNoon = new DateTime(2026, 11, 4, 12, 0, 0);
        Assert.IsTrue(Item("raccoons", wedNoon).Today, "Wednesday: no hyperweather");
        Assert.IsTrue(Item("bear", wedNoon).Today, "Wednesday: the big raccoon is on");
        Assert.IsTrue(_t.Snapshot(wedNoon.AddMinutes(15)).Items.Single(i => i.Id == "raccoons").Windows!.Count == 5);

        Feed(_log.Switch(new DateTime(2026, 11, 5, 6, 59, 41), "spec_weather_challenge_pve_1", true),
            _log.Switch(new DateTime(2026, 11, 5, 7, 59, 39), "spec_weather_solo_pve_1", true));
        var thu = new DateTime(2026, 11, 5, 19, 50, 0);
        Assert.IsFalse(Item("raccoons", thu).Today, "Thursday is a hyperweather day");
        Assert.IsFalse(Item("bear", thu).Today, "no big raccoon either (user, 11-05; its switch still came on hyperweather Tuesday 21:44: watch)");
        Assert.IsFalse(_t.Alerts(new DateTime(2026, 11, 5, 19, 43, 0), new DateTime(2026, 11, 5, 19, 45, 0)).Any(a => a.ItemId == "raccoons"), "no notification");
    }

    /// <summary>Opening a box from its bag tip, as logged on 11-05: the tip, its close 0.7 s later, the results screen 0.15 s after.</summary>
    private LogEvent[] OpenBox(DateTime tip, long item) =>
    [
        _log.ItemTip(tip, item),
        _log.Close(tip.AddMilliseconds(720), "UI_ITEM_TIPS_PASS_TOUCH"),
        _log.Toast(tip.AddMilliseconds(864)),
        _log.Settle(tip.AddMilliseconds(869)),
        _log.Close(tip.AddMilliseconds(1000), "UI_COMMON_SETTLE_VIEW"),
    ];

    [TestMethod]
    public void HyperweatherBoxes_OpeningEachBox_TicksItsDaily_LookingDoesNot()
    {
        // 2026-11-05 20:28-20:30 as logged: the user looked at the boxes (tips, no results screen), then opened all 8
        var t = new DateTime(2026, 11, 5, 20, 28, 48);
        Feed(_log.Switch(new DateTime(2026, 11, 5, 6, 59, 41), "spec_weather_challenge_pve_1", true));
        foreach (var (s, id) in new[] { (0.0, 900015L), (1.2, 900015L), (2.3, 900014L), (3.3, 900014L), (44.8, 900004L), (46.3, 900003L), (47.6, 900002L), (48.2, 900001L) })
        {
            Feed(_log.ItemTip(t.AddSeconds(s), id), _log.Close(t.AddSeconds(s + 0.5), "UI_ITEM_TIPS_PASS_TOUCH"));
        }

        var looked = t.AddMinutes(1);
        Assert.AreEqual(0, Item("gearRift", looked).Count, "looking at a box is not opening it");
        Assert.IsFalse(Item("escort", looked).Done);

        var open = new DateTime(2026, 11, 5, 20, 30, 30, 553);
        var opened = 0;
        foreach (var (s, id) in new[] { (0.0, 900015L), (1.54, 900015L), (2.74, 900014L), (3.82, 900014L), (5.01, 900004L), (6.24, 900003L), (7.37, 900002L), (8.59, 900001L) })
        {
            Feed(OpenBox(open.AddSeconds(s), id));
            if (++opened <= 4)
            {
                Assert.AreEqual(opened, Item("gearRift", open.AddSeconds(s + 2)).Count, $"rift box {opened} at +{s}s");
            }
        }

        var now = new DateTime(2026, 11, 5, 20, 35, 0);
        Assert.AreEqual(4, Item("gearRift", now).Count, "4 rift boxes: 4/4");
        Assert.IsTrue(Item("gearRift", now).Done);
        Assert.IsTrue(Item("cargo", now).Done);
        Assert.IsTrue(Item("shifting", now).Done);
        Assert.AreEqual(3, Item("escort", now).Count, "one Escort Loot box is all 3 (the user)");
        var loot = Item("raccoonLoot", now);
        Assert.IsTrue(loot.Today, "a Hyperweather day");
        Assert.IsTrue(loot.Done);
        Assert.IsFalse(Item("raccoons", now).Today);
    }

    [TestMethod]
    public void TheRaccoonLootBox_IsOnlyOnHyperweatherDays_AndAResultsScreenWithoutATipIsNoBox()
    {
        var wed = new DateTime(2026, 11, 4, 12, 0, 0);
        Assert.IsFalse(Item("raccoonLoot", wed).Today, "no Hyperweather switch: no raccoon box card");
        Assert.IsTrue(Item("raccoons", wed).Today);

        // a results screen long after a tip closed (a dungeon's own results) is not a box
        Feed(_log.ItemTip(wed, 900015), _log.Close(wed.AddSeconds(1), "UI_ITEM_TIPS_PASS_TOUCH"), _log.Settle(wed.AddSeconds(30)));
        Assert.AreEqual(0, Item("gearRift", wed.AddMinutes(1)).Count);
    }

    [TestMethod]
    public void ARecount_KeepsTheSavedStoredCounts_AndOnlyAppliesWhatCameAfter()
    {
        // 2026-11-05: 4.35's state held the train at 0 and the special train at 3 (as of Wednesday 20:07:29, from the old
        // "ask for help" rule). The recount after the new train rule replays the log from Monday: Wednesday's fills are
        // already in the saved counts; Thursday's 3 requests fill the special train and the 4th fill is that train.
        var asOf = new DateTime(2026, 11, 4, 20, 7, 29, 217);
        var old = new DailyState
        {
            Watermark = new DateTime(2026, 11, 5, 19, 51, 0),
            Catalog = "4.35",
            // Tuesday night's Honor War, older than the oldest log file still there: kept
            Progress = [new DailyProgressDto { Item = "honor", Day = new DateOnly(2026, 11, 2), Count = 12, First = new DateTime(2026, 11, 3, 1, 20, 44), Last = new DateTime(2026, 11, 3, 1, 50, 43), Source = DailySource.Game }],
            Stacks =
            [
                new DailyStackDto { Item = "train", Value = 0, AsOf = asOf },
                new DailyStackDto { Item = "specialTrain", Value = 3, AsOf = asOf },
            ],
        };
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(old, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        var now = new DateTime(2026, 11, 5, 19, 58, 0);
        var svc = new DailyService(Data.Catalog(), bytes, () => now);
        Assert.IsTrue(svc.Recounted);
        svc.CompleteReplay(
        [
            _log.Show(new DateTime(2026, 11, 4, 20, 4, 37), "UI_LITTLE_TRAIN_VIEW"),
            .. Fill(new DateTime(2026, 11, 4, 20, 7, 6, 514), 1.738),
            .. Fill(new DateTime(2026, 11, 4, 20, 7, 13, 274), 1.020),
            .. Fill(new DateTime(2026, 11, 4, 20, 7, 35, 855), 1.480),
            _log.Close(new DateTime(2026, 11, 4, 20, 7, 40, 76), "UI_LITTLE_TRAIN_VIEW"),
            _log.Show(new DateTime(2026, 11, 5, 19, 49, 12, 38), "UI_LITTLE_TRAIN_VIEW"),
            .. Fill(new DateTime(2026, 11, 5, 19, 49, 14, 816), 2.121),
            .. Fill(new DateTime(2026, 11, 5, 19, 49, 42, 727), 0.941),
            .. Fill(new DateTime(2026, 11, 5, 19, 49, 46, 607), 1.080),
            .. Fill(new DateTime(2026, 11, 5, 19, 49, 55, 27), 2.900),
        ]);
        var snap = svc.Snapshot();
        var train = snap.Items.Single(i => i.Id == "train");
        Assert.AreEqual(3, train.Count);
        Assert.AreEqual(0, train.Stack, "the game's 0/9");
        Assert.AreEqual(0, snap.Items.Single(i => i.Id == "specialTrain").Stack, "the game's 0/6");
        Assert.AreEqual(12, snap.Items.Single(i => i.Id == "honor").Count, "counts older than the replayed log stay");
    }

    private void Feed(params LogEvent[] events)
    {
        foreach (var e in events)
        {
            _t.Feed(e);
        }
    }

    private DailyItemView Item(string id, DateTime now) => _t.Snapshot(now).Items.Single(i => i.Id == id);

    private DailyProducerView Card(string id, DateTime now) => _t.Snapshot(now).Producers!.Single(p => p.Id == id);
}
