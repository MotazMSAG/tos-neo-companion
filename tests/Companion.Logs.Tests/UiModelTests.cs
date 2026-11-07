using Companion.Contracts;
using Companion.Contracts.Logs;
using static Companion.Logs.Tests.Synthetic;

namespace Companion.Logs.Tests;

/// <summary>Every UI-state transition of SPEC 6.2: advisory stack, busy brackets, availability, craft session, facts.</summary>
[TestClass]
public sealed class UiModelTests
{
    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    private static UiModel Build(params (double Seconds, string Message)[] lines) => Build(new UiClassifier(), lines);

    private static UiModel Build(UiClassifier classifier, params (double Seconds, string Message)[] lines)
    {
        var text = string.Concat(lines.Select(l => Entry(At(l.Seconds), l.Message)));
        return Parsing.Model(Parsing.All(Bytes(text)), classifier);
    }

    private static UiStateSnapshot Snap(UiModel m, double seconds) => m.Snapshot(null, At(seconds));

    /// <summary>A model that is in the world: UI_MAIN opened and a scene finished loading.</summary>
    private static (double, string)[] InWorld() => [(0, StartLoad), (0.5, OpenConst("UI_MAIN")), (1, HideProgressReal)];

    [TestMethod]
    public void Stack_ShowAndOpenConstPush_IdempotentMoveToTop_OverlayBusyStationNotPushed()
    {
        var m = Build([.. InWorld(),
            (2, Show("UI_BAG_MAIN")), (2.1, OpenConst("UI_BAG_MAIN")), (3, Show("UI_MAIL")), (4, Show("UI_BAG_MAIN")),
            (5, Show("UI_CHANT")), (5.1, OpenConst("UI_MONEY")), (6, Show("UI_REVIVE")), (6.5, Show("UI_LIFE_ALCHEMIST_MAKE_POT")),
            (7, Show("UI_RECHARGE")), (8, Show("UI_TEAM_VOTE")), (9, OpenConst("UI_SOMETHING_NEW"))]);
        CollectionAssert.AreEqual(
            new[] { "UI_MAIL", "UI_BAG_MAIN", "UI_RECHARGE", "UI_TEAM_VOTE", "UI_SOMETHING_NEW" },
            Snap(m, 10).AdvisoryStack.ToArray());
        Assert.AreEqual("UI_SOMETHING_NEW", Snap(m, 10).AdvisoryTop);
    }

    [TestMethod]
    public void Stack_EveryCloseVerbRemoves_DisposeImmediatelyNumericAndNilAreIgnored()
    {
        string[] names = ["UI_A", "UI_B", "UI_C", "UI_D", "UI_E", "UI_F", "UI_G"];
        var lines = InWorld().ToList();
        lines.AddRange(names.Select((n, i) => (2.0 + (i * 0.1), Show(n))));
        lines.AddRange(
        [
            (3, HideTop("UI_A")), (3.1, AddToClose("UI_B")), (3.2, StackOver("UI_C")), (3.3, DisposeAll("UI_D")),
            (3.4, CheckClean("UI_E")), (3.5, ToHide("UI_F")),
            (3.6, DisposeNow("UI_G")), (3.7, Ui("[ui_mgr]add to close_list", "826", 1724)), (3.8, Ui("[ui_mgr]add to close_list", "nil", 1724)),
            (3.9, Ui("check can open fail2", "UI_G", 1127)),
        ]);
        var m = Build([.. lines]);
        CollectionAssert.AreEqual(new[] { "UI_G" }, Snap(m, 4).AdvisoryStack.ToArray());
        Assert.IsNull(Snap(m, 4).LastHideTopNil, "a numeric or nil add-to-close is not 'hide top ui nil'");
    }

    // incident 25 (2026-10-25, game log 23:46:24-23:46:32): the Forbidden exchange popup never logs a close
    [TestMethod]
    public void ASilentPopup_IsDropped_WhenAWindowBelowItClosesLater_NotByTheGamesOwnCloseAtOnce()
    {
        var m = Build([.. InWorld(),
            (2, Show("UI_HENSHIN_BEAD_VIEW")), (3, Show("UI_ITEM_TIPS_PASS_TOUCH")),
            (10, Show("UI_TIPS2_EXCHANGE")), (10.001, OpenConst("UI_TIPS2_EXCHANGE")), (10.025, AddToClose("UI_ITEM_TIPS_PASS_TOUCH")),
            (11, CheckClean("UI_HENSHIN_BEAD_VIEW"))]);
        CollectionAssert.Contains(Snap(m, 12).AdvisoryStack.ToArray(), "UI_TIPS2_EXCHANGE", "the game's own close 25 ms later and a cache dispose prove nothing");

        var answered = Build([.. InWorld(),
            (2, Show("UI_HENSHIN_BEAD_VIEW")), (3, Show("UI_ITEM_TIPS_PASS_TOUCH")),
            (10, Show("UI_TIPS2_EXCHANGE")), (10.001, OpenConst("UI_TIPS2_EXCHANGE")), (10.025, AddToClose("UI_ITEM_TIPS_PASS_TOUCH")),
            (18, AddToClose("UI_HENSHIN_BEAD_VIEW"))]);
        Assert.HasCount(0, Snap(answered, 19).AdvisoryStack, "the user closed the window under it 8 s later: the popup was gone");
    }

    [TestMethod]
    public void OnlySilentPopups_AreDroppedThatWay()
    {
        var m = Build([.. InWorld(), (2, Show("UI_BAG_MAIN")), (3, Show("UI_RECHARGE")), (10, AddToClose("UI_BAG_MAIN"))]);
        CollectionAssert.AreEqual(new[] { "UI_RECHARGE" }, Snap(m, 11).AdvisoryStack.ToArray(), "a window that logs its close stays until it does");
    }

    [TestMethod]
    public void Stack_PhantomOpenPersists_UntilHideTopNil_OrSceneStart()
    {
        var m = Build([.. InWorld(), (2, Show("UI_BAG_MAIN")), (3, Show("UI_MAIL"))]);
        CollectionAssert.AreEqual(new[] { "UI_BAG_MAIN", "UI_MAIL" }, Snap(m, 60).AdvisoryStack.ToArray());

        var text = string.Concat(InWorld().Select(l => Entry(At(l.Item1), l.Item2)))
            + Entry(At(2), Show("UI_BAG_MAIN")) + Entry(At(3), HideTop(null));
        var events = Parsing.All(Bytes(text));
        var nilEvent = events.OfType<UiLogEvent>().Single(e => e.IsHideTopNil);
        var m2 = Parsing.Model(events);
        Assert.HasCount(0, Snap(m2, 4).AdvisoryStack);
        Assert.AreEqual(nilEvent.Position, Snap(m2, 4).LastHideTopNil);

        var m3 = Build([.. InWorld(), (2, Show("UI_BAG_MAIN")), (3, BeforeLoadingEmpty)]);
        Assert.HasCount(0, Snap(m3, 4).AdvisoryStack);
        Assert.AreEqual(BusyKind.Loading, Snap(m3, 4).Busy);
    }

    [TestMethod]
    public void Stack_UiMainIsNeverPushed()
    {
        var m = Build([.. InWorld(), (5, OpenConst("UI_MAIN"))]);
        Assert.HasCount(0, Snap(m, 6).AdvisoryStack);
    }

    [TestMethod]
    public void HideTop_TeachesTheClassifier_LearnedStackOverridesOverlaySeed()
    {
        var classifier = new UiClassifier();
        Assert.AreEqual(Contracts.Safety.UiKind.Overlay, classifier.Classify("UI_NPC_TALK"));
        Build(classifier, (1, HideTop("UI_NPC_TALK")), (2, HideTop("UI_BRAND_NEW")));
        Assert.IsTrue(classifier.IsLearnedStack("UI_NPC_TALK"));
        Assert.AreEqual(Contracts.Safety.UiKind.Stack, classifier.Classify("UI_NPC_TALK"));
        Assert.AreEqual(Contracts.Safety.UiKind.Stack, classifier.Classify("UI_BRAND_NEW"));
    }

    [TestMethod]
    public void GameAvailable_NeedsUiMainAndHideProgressReal_SessionMarkersReset()
    {
        var empty = Build();
        Assert.IsFalse(Snap(empty, 0).GameAvailable);
        Assert.AreEqual(BusyKind.Unknown, Snap(empty, 0).Busy);

        var mainOnly = Build((1, OpenConst("UI_MAIN")));
        Assert.IsFalse(Snap(mainOnly, 2).GameAvailable);
        Assert.AreEqual(BusyKind.Unknown, Snap(mainOnly, 2).Busy);

        var showMain = Build((1, Show("UI_MAIN")), (2, HideProgressReal));
        Assert.IsFalse(Snap(showMain, 3).GameAvailable, "only 'open const ui UI_MAIN' counts");

        var world = Build(InWorld());
        Assert.IsTrue(Snap(world, 2).GameAvailable);
        Assert.AreEqual(BusyKind.None, Snap(world, 2).Busy);
        Assert.IsTrue(world.InWorld);

        foreach (var marker in new[] { Quit, Logout })
        {
            var m = Build([.. InWorld(), (2, Show("UI_BAG_MAIN")), (3, marker)]);
            var s = Snap(m, 4);
            Assert.IsFalse(s.GameAvailable, marker);
            Assert.HasCount(0, s.AdvisoryStack, marker);
        }

        var header = Parsing.Model(Parsing.All(Bytes(string.Concat(InWorld().Select(l => Entry(At(l.Item1), l.Item2))) + Header(At(3)))));
        Assert.IsFalse(Snap(header, 4).GameAvailable);

        var ping = Build([.. InWorld(), (3, Ping)]);
        Assert.IsTrue(Snap(ping, 4).GameAvailable, "the 5-minute ping is not a reset");
    }

    [TestMethod]
    public void Busy_Loading_FromStartLoadOrLoadProgress_EndsOnlyAtHideProgressReal()
    {
        var m = Build([.. InWorld(), (2, StartLoad), (3, EndLoad), (3.5, AbortHiding), (4, HideProgressNow)]);
        Assert.AreEqual(BusyKind.Loading, Snap(m, 5).Busy);
        m = Build([.. InWorld(), (2, StartLoad), (3, EndLoad), (4, HideProgressReal)]);
        Assert.AreEqual(BusyKind.None, Snap(m, 5).Busy);

        m = Build([.. InWorld(), (2, Show("UI_LOAD_PROGRESS"))]);
        Assert.AreEqual(BusyKind.Loading, Snap(m, 3).Busy);
        m = Build([.. InWorld(), (2, OpenConst("UI_LOAD_PROGRESS")), (3, HideProgressReal)]);
        Assert.AreEqual(BusyKind.None, Snap(m, 4).Busy);
    }

    [TestMethod]
    public void Busy_Login_FromSelectRoleServerOrLogout_EndsAtOpenConstUiMain()
    {
        foreach (var start in new[] { Show("UI_SELECT_ROLE"), Show("UI_SERVER"), Logout })
        {
            var m = Build([.. InWorld(), (2, start)]);
            Assert.AreEqual(BusyKind.Login, Snap(m, 3).Busy, start);
            m = Build([.. InWorld(), (2, start), (3, OpenConst("UI_MAIN")), (4, HideProgressReal)]);
            Assert.AreEqual(BusyKind.None, Snap(m, 5).Busy, start);
            Assert.IsTrue(Snap(m, 5).GameAvailable, start);
        }
    }

    [TestMethod]
    public void Busy_CinemaAndScenario_EndOnAnyCloseVerbOrAfter120s()
    {
        var m = Build([.. InWorld(), (2, Show("UI_CINEMA_BLACK_VIEW"))]);
        Assert.AreEqual(BusyKind.Cinema, Snap(m, 3).Busy);
        Assert.AreEqual(BusyKind.Cinema, Snap(m, 121).Busy);
        Assert.AreEqual(BusyKind.None, Snap(m, 122.5).Busy, "120 s without a related event");
        m = Build([.. InWorld(), (2, Show("UI_CINEMA_BLACK_VIEW")), (8, DisposeNow("UI_CINEMA_BLACK_VIEW"))]);
        Assert.AreEqual(BusyKind.None, Snap(m, 9).Busy);

        m = Build([.. InWorld(), (2, Show("UI_SCENARIO_BG")), (2.1, Show("UI_SCENARIO_EXIT"))]);
        Assert.AreEqual(BusyKind.Scenario, Snap(m, 3).Busy);
        m = Build([.. InWorld(), (2, Show("UI_SCENARIO_BG")), (2.1, Show("UI_SCENARIO_EXIT")), (5, DisposeNow("UI_SCENARIO_EXIT"))]);
        Assert.AreEqual(BusyKind.Scenario, Snap(m, 6).Busy, "UI_SCENARIO_BG is still open");
        m = Build([.. InWorld(), (2, Show("UI_SCENARIO_BG")), (2.1, Show("UI_SCENARIO_EXIT")), (5, DisposeNow("UI_SCENARIO_EXIT")), (5, AddToClose("UI_SCENARIO_BG"))]);
        Assert.AreEqual(BusyKind.None, Snap(m, 6).Busy);
    }

    [TestMethod]
    public void Busy_Revive_UntilClosedOr60s()
    {
        var m = Build([.. InWorld(), (2, Show("UI_REVIVE"))]);
        Assert.AreEqual(BusyKind.Revive, Snap(m, 30).Busy);
        Assert.AreEqual(BusyKind.None, Snap(m, 63).Busy);
        m = Build([.. InWorld(), (2, Show("UI_REVIVE")), (5, AddToClose("UI_REVIVE"))]);
        Assert.AreEqual(BusyKind.None, Snap(m, 6).Busy);
    }

    [TestMethod]
    public void Busy_GuideMask_BanWithin2s_OrMaskUiOpen()
    {
        var m = Build([.. InWorld(), (2, GuideMaskBan)]);
        Assert.AreEqual(BusyKind.GuideMask, Snap(m, 3.5).Busy);
        Assert.AreEqual(BusyKind.None, Snap(m, 4.5).Busy);
        Assert.AreEqual(At(2), Snap(m, 4.5).LastGuideBan);

        var plainBan = Build([.. InWorld(), (2, "[LOG] -> module/foundation/ui/ui_mgr.lua:1104    ui_mgr:true_show_ui ban by guide, ui_id    UI_X")]);
        Assert.AreEqual(BusyKind.None, Snap(plainBan, 2.5).Busy, "only the 'mask' variant makes the game busy");
        Assert.AreEqual(At(2), Snap(plainBan, 2.5).LastGuideBan);

        m = Build([.. InWorld(), (2, Show("UI_GUIDE_MASK"))]);
        Assert.AreEqual(BusyKind.GuideMask, Snap(m, 100).Busy);
        m = Build([.. InWorld(), (2, Show("UI_GUIDE_MASK")), (3, AddToClose("UI_GUIDE_MASK"))]);
        Assert.AreEqual(BusyKind.None, Snap(m, 4).Busy);
    }

    [TestMethod]
    public void Busy_Match_WhileOpenOrShownWithin120s()
    {
        var m = Build([.. InWorld(), (2, Show("UI_TEAM_3V3_MAIN"))]);
        Assert.AreEqual(BusyKind.Match, Snap(m, 500).Busy, "open");
        m = Build([.. InWorld(), (2, Show("UI_TEAM_3V3_MAIN")), (5, HideTop("UI_TEAM_3V3_MAIN"))]);
        Assert.AreEqual(BusyKind.Match, Snap(m, 100).Busy, "shown within 120 s");
        Assert.AreEqual(BusyKind.None, Snap(m, 123).Busy);
    }

    [TestMethod]
    public void Craft_RealSession_14IsBlock_13Ticks_EndsTwoSecondsAfterStationClose()
    {
        var events = Parsing.All(Fixtures.Load(Fixtures.Craft));
        var m = Parsing.Model(events);
        var close = new DateTime(2026, 10, 13, 22, 51, 52, 112);
        var c = m.Snapshot(null, close.AddSeconds(1)).Craft;
        Assert.IsTrue(c.InProgress);
        Assert.AreEqual(14, c.IsBlockCount);
        Assert.AreEqual(13, c.Ticks);
        Assert.AreEqual("UI_LIFE_ALCHEMIST_MAKE_POT", c.StationUi);
        Assert.AreEqual(new DateTime(2026, 10, 13, 22, 51, 13, 926), c.StartedAt);
        Assert.AreEqual(events.OfType<CraftTickLogEvent>().First().Position, c.StartPosition);
        Assert.AreEqual(new DateTime(2026, 10, 13, 22, 51, 49, 811), c.LastTickAt);
        Assert.AreEqual(close, c.EndedAt);
        Assert.IsFalse(c.CraftViewReshown);
        Assert.IsFalse(m.Snapshot(null, close.AddSeconds(2.5)).Craft.InProgress);

        // During the session (before the close) it is in progress.
        var during = Parsing.Model(events.Where(e => e.Timestamp < new DateTime(2026, 10, 13, 22, 51, 30)));
        Assert.IsTrue(during.Snapshot(null, new DateTime(2026, 10, 13, 22, 51, 30)).Craft.InProgress);
        Assert.AreEqual(5, during.Snapshot(null, new DateTime(2026, 10, 13, 22, 51, 30)).Craft.Ticks);
    }

    [TestMethod]
    public void Craft_OneItemCooking_CountsTheItemReceivedTip()
    {
        // live 2026-10-15 (Lime Water): one is_block, the pot, the Make view back, one item tip, then the pot closes
        var m = Build([.. InWorld(), (2, IsBlock), (2.3, Show("UI_LIFE_ALCHEMIST_MAKE_POT")), (3.5, Show("UI_LIFESKILL_MAKE_VIEW")),
            (3.55, Show("UI_ITEM_GET_TIP")), (3.7, AddToClose("UI_LIFE_ALCHEMIST_MAKE_POT"))]);
        var c = Snap(m, 4).Craft;
        Assert.AreEqual((1, 1, 1), (c.IsBlockCount, c.ItemGets, c.Ticks));

        var noTip = Build([.. InWorld(), (2, IsBlock), (2.3, Show("UI_LIFE_ALCHEMIST_MAKE_POT")), (3.7, AddToClose("UI_LIFE_ALCHEMIST_MAKE_POT"))]);
        Assert.AreEqual(0, Snap(noTip, 4).Craft.Ticks, "no tip and no second is_block: nothing proven made");

        var outside = Build([.. InWorld(), (1, Show("UI_ITEM_GET_TIP")), (2, IsBlock), (2.3, Show("UI_LIFE_ALCHEMIST_MAKE_POT")), (3.7, AddToClose("UI_LIFE_ALCHEMIST_MAKE_POT"))]);
        Assert.AreEqual(0, Snap(outside, 4).Craft.ItemGets, "a tip before the craft is not counted");
    }

    [TestMethod]
    public void Craft_ReshownWithin1s_NewSessionAfterEnd_IdleWithoutStationEnds()
    {
        var m = Build([.. InWorld(), (2, IsBlock), (2.3, Show("UI_LIFE_ALCHEMIST_MAKE_POT")), (5, IsBlock),
            (6, AddToClose("UI_LIFE_ALCHEMIST_MAKE_POT")), (6.6, Show("UI_LIFESKILL_MAKE_VIEW"))]);
        var c = Snap(m, 7).Craft;
        Assert.IsTrue(c.CraftViewReshown);
        Assert.AreEqual(1, c.Ticks);
        Assert.IsTrue(c.InProgress);
        Assert.IsFalse(Snap(m, 8.1).Craft.InProgress);

        var late = Build([.. InWorld(), (2, IsBlock), (2.3, Show("UI_LIFE_ALCHEMIST_MAKE_POT")), (6, AddToClose("UI_LIFE_ALCHEMIST_MAKE_POT")), (7.5, Show("UI_LIFESKILL_MAKE_VIEW"))]);
        Assert.IsFalse(Snap(late, 8).Craft.CraftViewReshown);

        var again = Build([.. InWorld(), (2, IsBlock), (2.3, Show("UI_LIFE_ALCHEMIST_MAKE_POT")), (6, AddToClose("UI_LIFE_ALCHEMIST_MAKE_POT")),
            (20, IsBlock), (20.2, Show("UI_LIFE_ENCHANT_TABLE"))]);
        var c2 = Snap(again, 21).Craft;
        Assert.AreEqual(1, c2.IsBlockCount);
        Assert.AreEqual(0, c2.Ticks);
        Assert.AreEqual("UI_LIFE_ENCHANT_TABLE", c2.StationUi);
        Assert.AreEqual(At(20), c2.StartedAt);
        Assert.IsTrue(c2.InProgress);

        var blocked = Build([.. InWorld(), (2, IsBlock)]);
        Assert.IsTrue(Snap(blocked, 5).Craft.InProgress);
        Assert.IsFalse(Snap(blocked, 12.5).Craft.InProgress, "no station UI: the session ends after 10 s without is_block");

        var stationFirst = Build([.. InWorld(), (2, Show("UI_LIFE_ALCHEMIST_MAKE_POT"))]);
        Assert.IsTrue(Snap(stationFirst, 3).Craft.InProgress);
        Assert.AreEqual(0, Snap(stationFirst, 3).Craft.IsBlockCount);
        Assert.AreSame(CraftSessionInfo.None, Snap(Build(InWorld()), 3).Craft);
    }

    [TestMethod]
    public void Facts_FocusCollectResolutionJankThrottleCombatScreenSizePayShown()
    {
        var hasher = new FakeHasher();
        var text = string.Concat(
            Entry(At(1), FocusRecover, FocusPipes),
            Entry(At(2), FocusLost, FocusPipes),
            Entry(At(3), CollectStatus0),
            Entry(At(4), Login()),
            Entry(At(5), CurrentRes),
            Entry(At(6), Jank),
            Entry(At(7), Throttle),
            Entry(At(8), OpenConst("UI_BLOOD_BAR")),
            Entry(At(9), "[LOG] -> lss_start.lua:856    Unity Application Notify:    OnScreenSizeChange"),
            Entry(At(10), PayForbidden),
            Entry(At(11), Show("UI_BAG_MAIN")));
        var m = Parsing.Model(Parsing.All(Bytes(text), hasher));
        var s = Snap(m, 12);
        Assert.AreEqual(FocusState.Lost, s.Focus);
        Assert.AreEqual(At(2), s.LastFocusChange);
        Assert.AreEqual(0, s.AutoCollectStatus);
        Assert.AreEqual(FakeHasher.Of(FakeRoleToken), s.Character);
        Assert.AreEqual(new SizeI(1920, 1009), s.LastResolution);
        Assert.AreEqual(At(6), s.LastJank);
        Assert.AreEqual(At(7), s.LastThrottle);
        Assert.AreEqual(At(8), s.LastCombatIndicator);
        Assert.AreEqual(At(9), s.LastScreenSizeChange);
        Assert.AreEqual(At(10), s.LastPayLine);
        CollectionAssert.AreEquivalent(new[] { "UI_BLOOD_BAR", "UI_BAG_MAIN" }, s.ShownThisSession.ToArray());

        // A device-resolution line also counts as a screen-size change.
        var dev = Build((1, "[LOG] -> graphic_cfg.lua:242    [graphic] Device resolution    1902    991"));
        Assert.AreEqual(At(1), Snap(dev, 2).LastScreenSizeChange);
        Assert.IsNull(Snap(dev, 2).LastResolution);

        // A session marker resets the UI model but keeps change-only facts and the shown-this-generation set.
        var after = Parsing.Model(Parsing.All(Bytes(text + Entry(At(13), Quit)), hasher));
        var s2 = Snap(after, 14);
        Assert.AreEqual(FocusState.Unknown, s2.Focus);
        Assert.HasCount(0, s2.AdvisoryStack);
        Assert.AreEqual(0, s2.AutoCollectStatus);
        Assert.AreEqual(FakeHasher.Of(FakeRoleToken), s2.Character);
        Assert.HasCount(2, s2.ShownThisSession);
    }

    [TestMethod]
    public void ItemTip_TheLatestIsInTheSnapshot_WithItsPosition_AndASessionMarkerForgetsIt()
    {
        var text = string.Concat(InWorld().Select(l => Entry(At(l.Item1), l.Item2)))
            + Entry(At(2), ItemTip("Water", "    20330037"))
            + Entry(At(3), Show("UI_ITEM_TIPS_PASS_TOUCH"))
            + Entry(At(4), HideTop("UI_ITEM_TIPS_PASS_TOUCH"))
            + Entry(At(5), ItemTip("White Toast", "    1051000"));
        var events = Parsing.All(Bytes(text));
        var tips = events.OfType<ItemTipLogEvent>().ToList();
        var s = Snap(Parsing.Model(events), 6);
        Assert.AreEqual(new ItemTipInfo("White Toast", 1051000, At(5), tips[1].Position), s.LastItemTip);

        // "a tip logged after position X": the second tip is after the first one's position, the first is not after itself
        Assert.IsTrue(s.LastItemTip!.LoggedAfter(tips[0].Position));
        Assert.IsFalse(s.LastItemTip.LoggedAfter(tips[1].Position));
        Assert.IsFalse(s.LastItemTip.LoggedAfter(tips[1].Position with { File = tips[1].Position.File with { FileIndex = 99 } }), "another generation");
        Assert.IsTrue(s.LastItemTip.LoggedAfter(null));

        Assert.IsNull(Snap(Build(InWorld()), 3).LastItemTip);
        var quit = Parsing.Model(Parsing.All(Bytes(text + Entry(At(6), Quit))));
        Assert.IsNull(Snap(quit, 7).LastItemTip, "a session marker resets the UI model");
    }

    [TestMethod]
    public void GridClick_TheLatestIsInTheSnapshot_WithItsSquare_AndASessionMarkerForgetsIt()
    {
        var text = string.Concat(InWorld().Select(l => Entry(At(l.Item1), l.Item2)))
            + Entry(At(2), Show("UI_ACTIVITY_CENTER"))
            + Entry(At(3), GridClick(3, 4, 1))
            + Entry(At(4), GridClick(1, 1, 14));
        var events = Parsing.All(Bytes(text));
        var presses = events.OfType<GridClickLogEvent>().ToList();
        var s = Snap(Parsing.Model(events), 5);
        Assert.AreEqual(new GridClickInfo(1, 1, 14, At(4), presses[1].Position), s.LastGridClick);
        Assert.AreEqual(0, s.LastGridClick!.Square, "the tray");
        Assert.AreEqual(15, new GridClickInfo(3, 4, 1, At(3), presses[0].Position).Square, "row by row: (3-1)*6 + (4-1)");
        Assert.AreEqual(35, new GridClickInfo(6, 6, 1, At(3), presses[0].Position).Square);
        Assert.IsTrue(s.LastGridClick.LoggedAfter(presses[0].Position));
        Assert.IsFalse(s.LastGridClick.LoggedAfter(presses[1].Position));

        var quit = Parsing.Model(Parsing.All(Bytes(text + Entry(At(6), Quit))));
        Assert.IsNull(Snap(quit, 7).LastGridClick, "a session marker resets the UI model");
    }

    [TestMethod]
    public void ResetAll_ClearsEverything()
    {
        var m = Build([.. InWorld(), (2, Show("UI_BAG_MAIN")), (3, CollectStatus0), (4, IsBlock), (4.5, ItemTip("Water", "    20330037"))]);
        Assert.IsNotNull(Snap(m, 5).LastItemTip);
        m.ResetAll();
        var s = Snap(m, 5);
        Assert.IsNull(s.LastItemTip);
        Assert.IsFalse(s.GameAvailable);
        Assert.AreEqual(BusyKind.Unknown, s.Busy);
        Assert.HasCount(0, s.AdvisoryStack);
        Assert.IsNull(s.AutoCollectStatus);
        Assert.HasCount(0, s.ShownThisSession);
        Assert.AreSame(CraftSessionInfo.None, s.Craft);
    }
}

[TestClass]
public sealed class CraftCountTests
{
    [TestMethod]
    public void ACraftNeverCountsMoreItemsThanItsTicks()
    {
        // 2026-10-31 audit: the game's periodic reward tip 1.2 s after the pot closed made 10 cooked sashimi count 11
        var s = new CraftSessionInfo(false, "UI_LIFE_ALCHEMIST_MAKE_POT", null, null, 10, null, null, false) { ItemGets = 11 };
        Assert.AreEqual(10, s.Ticks);
        Assert.AreEqual(1, (s with { IsBlockCount = 1, ItemGets = 1 }).Ticks, "a one-item craft: one is_block, one tip");
        Assert.AreEqual(9, (s with { ItemGets = 0 }).Ticks, "no tips: the is_block lines after the first");
        Assert.AreEqual(2, (s with { IsBlockCount = 0, ItemGets = 2 }).Ticks, "no is_block read: the tips");
    }
}
