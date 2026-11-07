using Companion.Contracts;
using Companion.Contracts.Logs;
using static Companion.Logs.Tests.Synthetic;

namespace Companion.Logs.Tests;

/// <summary>Every message pattern of SPEC 6.1 against byte-exact real fixtures and synthetic edge cases.</summary>
[TestClass]
public sealed class PatternTests
{
    [TestMethod]
    public void RealUiVerbs_EveryVerbNilAndNumericIdsParse()
    {
        var events = Parsing.All(Fixtures.Load(Fixtures.UiVerbs));
        var ui = events.OfType<UiLogEvent>().Select(e => (e.Verb, e.UiName, e.NumericId)).ToList();
        (UiVerb, string?, int?)[] expected =
        [
            (UiVerb.Show, "UI_BAG_MAIN", null),
            (UiVerb.OpenConst, "UI_BAG_MAIN", null),
            (UiVerb.HideTop, "UI_BAG_MAIN", null),
            (UiVerb.HideTop, null, null),
            (UiVerb.HideTop, "GUILD_WAR_GVE_BATTLE_RESULT_SUCC", null),
            (UiVerb.AddToCloseList, "UI_MAIL", null),
            (UiVerb.AddToCloseList, null, null),
            (UiVerb.AddToCloseList, null, 826),
            (UiVerb.StackOverRemove, "UI_GUILD_FORGE", null),
            (UiVerb.DisposeImmediately, "UI_GUILD_FORGE", null),
            (UiVerb.DisposeAllClose, "UI_ANNOUNCE", null),
            (UiVerb.CheckCleanDispose, "UI_TEAM_MAIN", null),
            (UiVerb.CheckCanOpenFail, "UI_TIPS2", null),
            (UiVerb.ToHide, "UI_GAMEPLAY_COUNT_DOWN", null),
            (UiVerb.PopupRealShow, "UI_ITEM_REWARD", null),
            (UiVerb.StackOverRemove, "LIFE_CAREER_HELP_CONFIRM", null),
            (UiVerb.Show, "UI_CINEMA_BLACK_VIEW", null),
            (UiVerb.DisposeImmediately, "UI_CINEMA_BLACK_VIEW", null),
            (UiVerb.Show, "UI_SCENARIO_BG", null),
            (UiVerb.Show, "UI_SCENARIO_EXIT", null),
            (UiVerb.DisposeImmediately, "UI_SCENARIO_EXIT", null),
            (UiVerb.DisposeImmediately, "UI_SCENARIO_BG", null),
            (UiVerb.OpenConst, "UI_MAIN", null),
            (UiVerb.OpenConst, "UI_TIPS2", null),
        ];
        CollectionAssert.AreEqual(expected, ui);

        // The four non-verb ui_mgr lines (load cache ui, clean_unused begin, add to delay hide fail, init close stack) give nothing.
        Assert.AreEqual(expected.Length + 1, events.Count);
        var ban = events.OfType<GuideBanLogEvent>().Single();
        Assert.IsFalse(ban.Mask);
        Assert.AreEqual(new DateTime(2026, 10, 13, 7, 30, 12, 870), ban.Timestamp);

        var nil = events.OfType<UiLogEvent>().Single(e => e.Verb == UiVerb.HideTop && e.UiName is null);
        Assert.IsTrue(nil.IsHideTopNil);
        Assert.IsTrue(UiExpectation.HiddenTop(null).Matches(nil));
        var numeric = events.OfType<UiLogEvent>().Single(e => e.NumericId == 826);
        Assert.IsFalse(UiExpectation.HiddenTop(null).Matches(numeric));
        Assert.IsTrue(UiExpectation.Opened("UI_BAG_MAIN").Any(x => x.Matches(events.OfType<UiLogEvent>().First())));
    }

    [TestMethod]
    public void RealUiVerbs_OffsetsAndTimestampsAreByteExact()
    {
        var bytes = Fixtures.Load(Fixtures.UiVerbs);
        var events = Parsing.All(bytes);
        var first = events[0];
        Assert.AreEqual(0L, first.StartOffset);
        var firstEntryLength = System.Text.Encoding.UTF8.GetByteCount(
            "2026-10-13 23:03:42.314\tLog\t[LOG] -> module/foundation/ui/ui_mgr.lua:1077    <color=yellow>show_ui</color>    UI_BAG_MAIN\r\n\r\n");
        Assert.AreEqual((long)firstEntryLength, first.Position.Offset);
        Assert.AreEqual(first.Position.Offset, events[1].StartOffset);
        Assert.AreEqual(new DateTime(2026, 10, 13, 23, 3, 42, 314), first.Timestamp);
        Assert.AreEqual((long)bytes.Length, events[^1].Position.Offset);

        // Offsets strictly increase and each event ends where the next begins or before it.
        for (var i = 1; i < events.Count; i++)
        {
            Assert.IsTrue(events[i].StartOffset >= events[i - 1].Position.Offset);
        }
    }

    [TestMethod]
    public void RealC3Run3_PressBlockBeforeLostFocus_MapsToTheProvenClientPoint()
    {
        var events = Parsing.All(Fixtures.Load(Fixtures.C3Run3));
        var press = events.OfType<PressBlockLogEvent>().Single();
        Assert.AreEqual("Label_Text", press.ObjectName);
        Assert.AreEqual("Canvas_Bag", press.ModuleName);
        Assert.AreEqual(new PointD(1748, 694), press.ScreenPosition);
        Assert.AreEqual(new PointI(1748, 314), press.ToClient(1009));

        var i = events.IndexOf(press);
        Assert.IsInstanceOfType<FocusLogEvent>(events[i + 1]);
        Assert.IsFalse(((FocusLogEvent)events[i + 1]).Recovered);

        var bagShow = events.OfType<UiLogEvent>().First(e => e.UiName == "UI_BAG_MAIN");
        Assert.AreEqual(UiVerb.Show, bagShow.Verb);
        Assert.IsTrue(bagShow.StartOffset < press.StartOffset);
        var focus = events.OfType<FocusLogEvent>().Select(f => f.Recovered).ToArray();
        CollectionAssert.AreEqual(new[] { true, false, true, false, true, false, true }, focus);
    }

    [TestMethod]
    public void RealC3Run1_BareBlock_BlockAfterLostFocus_ResolutionAndScreenSizeLines()
    {
        var events = Parsing.All(Fixtures.Load(Fixtures.C3Run1));
        var presses = events.OfType<PressBlockLogEvent>().ToList();
        Assert.HasCount(2, presses);
        Assert.AreEqual(string.Empty, presses[0].ObjectName);
        Assert.IsNull(presses[0].ModuleName);
        Assert.IsNull(presses[0].ScreenPosition);
        Assert.IsNull(presses[0].ToClient(1009));

        Assert.AreEqual("Image_sharedAnchor", presses[1].ObjectName);
        Assert.AreEqual("Canvas_HomePetMain", presses[1].ModuleName);
        Assert.AreEqual(new PointI(1732, 444), presses[1].ToClient(1009));
        var before = events[events.IndexOf(presses[1]) - 1];
        Assert.IsInstanceOfType<FocusLogEvent>(before);
        Assert.IsFalse(((FocusLogEvent)before).Recovered);

        var sizes = events.OfType<SessionLogEvent>().Where(s => s.Marker == SessionMarker.ScreenSizeChange).ToList();
        Assert.HasCount(2, sizes);
        var res = events.OfType<ResolutionLogEvent>().Select(r => (r.Source, r.Size)).ToList();
        CollectionAssert.AreEqual(
            new[] { (ResolutionSource.Device, new SizeI(1902, 991)), (ResolutionSource.Device, new SizeI(1920, 1009)), (ResolutionSource.CurrentScreen, new SizeI(1920, 1009)) },
            res);
        Assert.IsTrue(events.OfType<UiLogEvent>().Any(e => e.UiName == "UI_MAIN_GACHA" && e.Verb == UiVerb.Show));
    }

    [TestMethod]
    public void RealBootstrapLines_CollectWithTraceback_ResolutionWithAndWithoutLuaPrefix()
    {
        var events = Parsing.All(Fixtures.Load(Fixtures.BootstrapLines));
        Assert.HasCount(5, events);
        var collect = (AutoCollectLogEvent)events[0];
        Assert.AreEqual(2, collect.Status);
        Assert.AreEqual(collect.Position.Offset, events[1].StartOffset, "the traceback block belongs to the collect entry");
        var current = (ResolutionLogEvent)events[1];
        Assert.AreEqual(ResolutionSource.CurrentScreen, current.Source);
        Assert.AreEqual(new SizeI(1920, 1009), current.Size);
        var device = (ResolutionLogEvent)events[2];
        Assert.AreEqual(ResolutionSource.Device, device.Source);
        Assert.AreEqual(new SizeI(1920, 1009), device.Size);
        Assert.AreEqual(SessionMarker.ScreenSizeChange, ((SessionLogEvent)events[3]).Marker);
        Assert.AreEqual(new SizeI(1902, 991), ((ResolutionLogEvent)events[4]).Size);
    }

    [TestMethod]
    public void RealCraftSession_FourteenIsBlockLines()
    {
        var ticks = Parsing.All(Fixtures.Load(Fixtures.Craft)).OfType<CraftTickLogEvent>().ToList();
        Assert.HasCount(14, ticks);
        Assert.IsTrue(ticks.All(t => t.X == 10.437787465143 && t.Y == -51.478912430509));
        Assert.AreEqual(new DateTime(2026, 10, 13, 22, 51, 13, 926), ticks[0].Timestamp);
    }

    [TestMethod]
    public void SceneLoadPhases_AllEight_AndEndLoadingEmptyIsNotEndLoad()
    {
        var text = string.Concat(
            Entry(T0, BeforeLoadingEmpty),
            Entry(T0, EndLoadingEmpty),
            Entry(T0, StartLoad),
            Entry(T0, EndLoad),
            Entry(T0, NeedWait),
            Entry(T0, RoleReady),
            Entry(T0, AbortHiding),
            Entry(T0, HideProgressNow),
            Entry(T0, HideProgressReal),
            Entry(T0, "[LOG] -> cave/cave_helper.lua:247    [scene_load] abort cave loading cave ~= get_cur_cave"),
            Entry(T0, "[LOG] -> module/common/util/log/log_event_mgr.lua:261    post_scene_load_time    40301    1.2    1.2    1.3"));
        var phases = Parsing.All(Bytes(text)).OfType<SceneLoadLogEvent>().Select(s => s.Phase).ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                ScenePhase.StartLoad, ScenePhase.StartLoad, ScenePhase.EndLoad, ScenePhase.NeedWait, ScenePhase.RoleReady,
                ScenePhase.AbortHidingProgress, ScenePhase.HideProgressNow, ScenePhase.HideProgressReal,
            },
            phases);
    }

    [TestMethod]
    public void SessionMarkers_JankThrottlePayAutoEatGuideMask()
    {
        var text = string.Concat(
            Header(T0),
            Entry(T0, Quit, "UnityEngine.Logger:Log(LogType, Object)", "lss.StageMgr:OnApplicationQuit()"),
            Entry(T0, Logout),
            Entry(T0, "[LOG] -> vendor/vendor_ejoy.lua:1016    start logout sdk    nil"),
            Entry(T0, Ping),
            Entry(T0, "[LOG] -> vendor/ema/ema_vendor.lua:954    baLogEvent upload:     dc_ping    dc_ping    0"),
            Entry(T0, Jank),
            Entry(T0, JankBare),
            Entry(T0, Throttle),
            Entry(T0, ThrottleSpecForm),
            Entry(T0, PayForbidden),
            Entry(T0, PayLoginReady),
            Entry(T0, PayLoginShow),
            Entry(T0, AutoEatReq),
            Entry(T0, AutoEatResp),
            Entry(T0, GuideMaskBan));
        var events = Parsing.All(Bytes(text));
        CollectionAssert.AreEqual(
            new[] { SessionMarker.LogCallbackAdded, SessionMarker.ApplicationQuit, SessionMarker.LogoutStart, SessionMarker.PingTrigger },
            events.OfType<SessionLogEvent>().Select(s => s.Marker).ToArray());
        CollectionAssert.AreEqual(new[] { 1.5071393, 0.75 }, events.OfType<JankLogEvent>().Select(j => j.Seconds).ToArray());
        Assert.HasCount(2, events.OfType<ThrottleLogEvent>().ToList());
        Assert.HasCount(1, events.OfType<PayLogEvent>().ToList());
        Assert.HasCount(1, events.OfType<AutoEatLogEvent>().ToList());
        Assert.IsTrue(events.OfType<GuideBanLogEvent>().Single().Mask);
        Assert.HasCount(4 + 2 + 2 + 1 + 1 + 1, events);
    }

    [TestMethod]
    [Ignore("Whisper alert, unfinished (2026-10-20): the log privacy allow-list does not admit the chat line, and which channels it covers is unverified. Not part of Release 1; see RELEASES.md.")]
    public void AChatMessageArriving_IsAnEventWithTheChannelOnly()
    {
        // live 2026-10-18 22:11-22:18: one line per whisper received; the number after the channel is not kept
        var text = string.Concat(
            Entry(T0, "[LOG] -> module/chat/chat_list.lua:1305    [chat_list]add wait show    1    1760814711"),
            Entry(T0, "[LOG] -> module/chat/chat_list.lua:1313    [chat_list]on_msg_show    0"),
            Entry(T0, "[LOG] -> module/chat/chat_util.lua:585    transfer_notice_to_recv mention_notice    0    false"));
        var events = Parsing.All(Bytes(text));
        Assert.HasCount(1, events.OfType<MessageArrivedLogEvent>().ToList(), "events: " + string.Join(",", events.Select(e => e.GetType().Name)) + " entries: " + EntryScanner.Scan(Bytes(text), Bytes(text).Length, 0, capped: false).Entries.Count);
        Assert.AreEqual(1, events.OfType<MessageArrivedLogEvent>().Single().Channel);
        Assert.HasCount(1, events);
    }

    [TestMethod]
    public void Login_IsHashedAtOnce_RawTokenNeverInTheEvent()
    {
        var hasher = new FakeHasher();
        var text = string.Concat(
            Entry(T0, Login()),
            Entry(T0, $"[LOG] -> login_mgr.lua:228    Cat:login_mgr.lua[req_login_role] role, server.id_and_more    {FakeRoleToken}    extra"));
        var events = Parsing.All(Bytes(text), hasher);
        var login = events.OfType<CharacterLoginLogEvent>().Single();
        Assert.AreEqual(FakeHasher.Of(FakeRoleToken), login.Character);
        Assert.AreEqual(1, hasher.Calls);
        Assert.HasCount(1, events);
        Assert.DoesNotContain(FakeRoleToken, login.ToString());
        Assert.DoesNotContain("98765", login.ToString());
    }

    [TestMethod]
    public void Separators_FourFiveOrOneSpace_AndWarningLevel_AndNoDigitFailVerb()
    {
        var text = string.Concat(
            Entry(T0, "[LOG] -> module/foundation/ui/ui_mgr.lua:1077     <color=yellow>show_ui</color>     UI_BAG_MAIN"),
            Entry(T0, "[LOG] -> module/foundation/ui/ui_mgr.lua:1077 <color=yellow>show_ui</color> UI_MAIL    "),
            Entry(T0, "[LOG] -> module/foundation/ui/ui_mgr.lua:1127    check can open fail    UI_RANK_VIEW"),
            "2026-10-14 01:00:00.000\tWarning\t[StageMgr] OnApplicationFocus LostFocus\r\n\r\n");
        var events = Parsing.All(Bytes(text));
        Assert.HasCount(4, events);
        Assert.AreEqual("UI_BAG_MAIN", ((UiLogEvent)events[0]).UiName);
        Assert.AreEqual("UI_MAIL", ((UiLogEvent)events[1]).UiName);
        Assert.AreEqual(UiVerb.CheckCanOpenFail, ((UiLogEvent)events[2]).Verb);
        Assert.IsFalse(((FocusLogEvent)events[3]).Recovered);
    }

    [TestMethod]
    public void ItemTip_TheLiveLine_GivesTheNameWithItsSpacesAndTheId()
    {
        // live 2026-10-15: a press on the Make tab's product icon opened UI_ITEM_TIPS_PASS_TOUCH and logged this line
        var bytes = Bytes(LiveItemTipLine + "\r\n\r\n");
        var tip = (ItemTipLogEvent)Parsing.All(bytes).Single();
        Assert.AreEqual(("White Toast", 1051000L), (tip.ItemName, tip.ItemId));
        Assert.AreEqual(new DateTime(2026, 10, 15, 1, 44, 58, 553), tip.Timestamp);
        Assert.AreEqual((0L, (long)bytes.Length), (tip.StartOffset, tip.Position.Offset));

        // the material tip of the same session, and the line followed directly by the next entry (no blank line)
        var text = Entry(T0, ItemTip("Water", "    20330037")).Replace("\r\n\r\n", "\r\n", StringComparison.Ordinal) + Entry(T0, Show("UI_ITEM_TIPS_PASS_TOUCH"));
        var events = Parsing.All(Bytes(text));
        Assert.AreEqual(("Water", 20330037L), (((ItemTipLogEvent)events[0]).ItemName, ((ItemTipLogEvent)events[0]).ItemId));
        Assert.AreEqual("UI_ITEM_TIPS_PASS_TOUCH", ((UiLogEvent)events[1]).UiName);
    }

    [TestMethod]
    public void GridClick_TheLiveLine_GivesRowColumnAndPiece_LookAlikesGiveNothing()
    {
        // live 2026-10-29: every press on a Kitty Tea House square logs this line (row, column, piece in hand)
        var bytes = Bytes(LiveGridClickLine + "\n\n");
        var press = (GridClickLogEvent)Parsing.All(bytes).Single();
        Assert.AreEqual((3, 4, 1), (press.Row, press.Column, press.Piece));
        Assert.AreEqual(new DateTime(2026, 10, 29, 10, 22, 23, 389), press.Timestamp);

        var tray = (GridClickLogEvent)Parsing.All(Bytes(Entry(T0, GridClick(1, 1, 17)))).Single();
        Assert.AreEqual((1, 1, 17), (tray.Row, tray.Column, tray.Piece), "the tray is row 1, column 1 (an Oven stored)");

        var text = string.Concat(
            Entry(T0, GridClick(0, 4, 1)), // rows count from 1
            Entry(T0, GridClick(3, 7, 1)), // six columns
            Entry(T0, GridClick(3, 4, 1) + "    FakePlayer"), // anything after the piece
            Entry(T0, "[LOG] -> module/chat/chat_mgr.lua:300    recv    on_grid_click    3    4    1"),
            Entry(T0, "[LOG] -> module/triple_town/chess_play_widget.lua:409    on_grid_click    3    4"));
        Assert.IsEmpty(Parsing.All(Bytes(text)));
    }

    [TestMethod]
    public void ItemTip_AnExtraUuidToken_TheIdIsTheLastInteger_ElseNothing()
    {
        (string Tail, string Name, long Id)[] cases =
        [
            ("    1051000", "White Toast", 1051000),
            ("1051000", "White Toast", 1051000), // no uuid field at all: 4 spaces
            ("7f3a-19bc    1051000", "White Toast", 1051000), // a uuid token is skipped
            ("987654321    1051000", "White Toast", 1051000), // a numeric uuid: the id is the LAST integer
            ("    1051000    ", "White Toast", 1051000), // trailing spaces
        ];
        foreach (var (tail, name, id) in cases)
        {
            var tip = (ItemTipLogEvent)Parsing.All(Bytes(Entry(T0, ItemTip("White Toast", tail)))).Single();
            Assert.AreEqual((name, id), (tip.ItemName, tip.ItemId), tail);
        }

        var multi = (ItemTipLogEvent)Parsing.All(Bytes(Entry(T0, ItemTip("Grilled Fish with Lemon", "    20330099")))).Single();
        Assert.AreEqual("Grilled Fish with Lemon", multi.ItemName, "single spaces stay inside the name");

        var text = string.Concat(
            Entry(T0, ItemTip("White Toast", "    ")), // no id
            Entry(T0, ItemTip("White Toast", "    abc")), // the last token is not an integer
            Entry(T0, "[LOG] -> module/item/item_tips_mgr.lua:200    Cat:item_tips_mgr print name, uuid, id :     1051000"), // no name
            Entry(T0, "[LOG] -> module/item/item_tips_mgr.lua:88    Cat:item_tips_mgr show tips    1051000"), // another line of the file
            Entry(T0, "[LOG] -> module/chat/chat_mgr.lua:300    recv    Cat:item_tips_mgr print name, uuid, id :     White Toast        1051000"));
        Assert.HasCount(0, Parsing.All(Bytes(text)));
    }

    [TestMethod]
    public void NonMatchingLines_AreDropped()
    {
        var text = string.Concat(
            // Text that merely contains a pattern (other file, other position) is never an event.
            Entry(T0, "[LOG] -> module/chat/chat_mgr.lua:120    recv    open const ui    UI_BAG_MAIN"),
            Entry(T0, "[LOG] -> module/chat/chat_mgr.lua:121    [StageMgr] OnApplicationFocus RecoverFocus"),
            Entry(T0, "[LOG] -> module/main_ui_mgr.lua:319    Cat:main_ui_mgr.lua[hide by cave setting] is_hide_left, is_hide_right    false    false"),
            Entry(T0, "[LOG] -> module/foundation/ui/ui_mgr.lua:1077    <color=yellow>show_ui</color>    ui_lower_case"),
            Entry(T0, "[LOG] -> graphic_cfg.lua:569    [graphic] Upgrade quality    4    98.7"),
            Entry(T0, "[LOG] -> graphic_cfg.lua:163    [graphic] SetTargetFrameRate    60    1"),
            Entry(T0, "[LOG] -> lss_lua_notify    100    OnScreenSizeChange"),
            Entry(T0, "[LOG] -> module/lifeskill/lifeskill_mgr.lua:35    [lifeskill]    [lifeskill][autoeat] check_autoeat_startup end-task    -- x"),
            "|\t[StageMgr] OnApplicationFocus RecoverFocus\r\n",
            "open const ui    UI_BAG_MAIN\r\n\r\n");
        Assert.HasCount(0, Parsing.All(Bytes(text)));
    }
}
