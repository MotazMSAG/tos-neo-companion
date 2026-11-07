using Companion.Contracts.Logs;
using static Companion.Logs.Tests.Synthetic;

namespace Companion.Logs.Tests;

/// <summary>The lines the daily checklist reads (2026-11-04), copied from the real game log (system lines only).</summary>
[TestClass]
public sealed class DailyLinesTests
{
    [TestMethod]
    public void TheChecklistLines_ParseIntoTheirEvents_KeepingNamesIdsAndCodesOnly()
    {
        var text = string.Concat(
            Entry(T0, "[LOG] -> sproto/SprotoService.lua:309    errcode from:     arena.arena_sweep    2659"),
            Entry(T0, "[LOG] -> module/foundation/base_network.lua:155    request too frequency :    town.task_info"),
            Entry(T0, "[LOG] -> module/convoy/convoy_mgr.lua:1566    Cat:convoy_mgr.lua[notify_convoy_commit_task] resp    table: 00000003415CEF10"),
            Entry(T0, "[LOG] -> module/honor_war/honor_war_network.lua:232    Cat:honor_war_network.lua[notify_honor_war_stage] prt_resp.is_win:    true    nil_rewards_info    nil_total_rewards"),
            Entry(T0, "[LOG] -> module/task/task_mgr.lua:1158    action state: 2, chain:70694, step:1, task_id:110014054"),
            Entry(T0, "[LOG] -> module/task/task_mgr.lua:1195    realy commit:110014054"),
            Entry(T0, "[LOG] -> cave/cave_helper.lua:138    [scene_lod] load_scene done    town"),
            Entry(T0, "[LOG] -> module/gameplay/gameplay_network.lua:19        [notify_change_feature] instance:    1015    risk_war    0"),
            Entry(T0, "[LOG] -> module/gameplay/gameplay_network.lua:16    [notify_change_feature]    10137    1"),
            Entry(T0, "[LOG] -> sproto/SprotoService.lua:432    unknown pack type!!!    REQUEST    notify.on_new_day    table: 00000002E8474030"),
            Entry(T0, "[LOG] -> do_register_thing.lua:60    server_push.daily_update"),
            Entry(T0, "[LOG] -> sproto/SprotoService.lua:432    unknown pack type!!!    REQUEST    notify.weekly_update    table: 0000000341264400"),
            Entry(T0, "[LOG] -> module/popup_seq/popup_seq_mgr.lua:191    Cat:popup_seq_mgr.lua[real show ui] tar_data.ui_id    UI_ITEM_REWARD    323"));
        var events = Parsing.All(Bytes(text));

        var reply = events.OfType<ServerReplyLogEvent>().Single();
        Assert.AreEqual(("arena.arena_sweep", 2659), (reply.Request, reply.Code));
        Assert.AreEqual("town.task_info", events.OfType<ThrottleLogEvent>().Single().Request);
        var pushes = events.OfType<ServerPushLogEvent>().ToList();
        CollectionAssert.AreEqual(
            new[] { "convoy_mgr.notify_convoy_commit_task", "honor_war_network.notify_honor_war_stage" },
            pushes.Select(p => p.FullName).ToArray());
        Assert.IsNull(pushes[0].Win);
        Assert.IsTrue(pushes[1].Win);
        var quests = events.OfType<QuestLogEvent>().ToList();
        Assert.AreEqual((70694, 1, 110014054L, false), (quests[0].Chain, quests[0].Step, quests[0].TaskId, quests[0].Committed));
        Assert.AreEqual((110014054L, true), (quests[1].TaskId, quests[1].Committed));
        Assert.AreEqual("town", events.OfType<MapEnteredLogEvent>().Single().Map);
        var sw = events.OfType<FeatureSwitchLogEvent>().Single();
        Assert.AreEqual((1015, "risk_war", false), (sw.GameplayId, sw.Instance, sw.Open));
        CollectionAssert.AreEqual(
            new[] { ResetKind.NewDay, ResetKind.DailyUpdate, ResetKind.WeeklyUpdate },
            events.OfType<ResetLogEvent>().Select(r => r.Kind).ToArray());
        Assert.AreEqual(UiVerb.PopupRealShow, events.OfType<UiLogEvent>().Single().Verb);
        Assert.HasCount(12, events, "the feature-switch header line gives nothing");
    }

    [TestMethod]
    public void TheLearningDayLines_OrdersShopRefreshAndHelperSlots_Parse()
    {
        var text = string.Concat(
            Entry(T0, "[LOG] -> module/guide/guide_mgr.lua:1624    Cat:guide_mgr.lua[finish_guide] guide.key    order_take3    nil"),
            Entry(T0, "[LOG] -> module/shop/shop_mgr.lua:1090    [shop] shop_refresh    10028"),
            Entry(T0, "[LOG] -> module/home/home_mgr_housekeeper.lua:182    home helper req_collect_housekeeper_products    3"),
            Entry(T0, "[LOG] -> module/guide/guide_mgr.lua:991    Cat:guide_mgr.lua[on_cave_event] ename, param    big_bear_dead    nil"));
        var events = Parsing.All(Bytes(text));

        Assert.AreEqual("order_take3", events.OfType<GuideKeyLogEvent>().Single().Key);
        Assert.AreEqual(10028, events.OfType<ShopRefreshLogEvent>().Single().ShopId);
        Assert.AreEqual(3, events.OfType<HelperCollectLogEvent>().Single().Slot);
        Assert.HasCount(3, events, "a cave event line gives nothing");
    }

    [TestMethod]
    public void RoomFinishLines_GiveTheSeconds()
    {
        var text = string.Concat(
            Entry(T0, "[LOG] -> battle/room_cmd.lua:1967    room_finish------------->    30.0"),
            Entry(T0, "[LOG] -> battle/room_cmd.lua:1967    room_finish------------->    3.0"),
            Entry(T0, "[LOG] -> battle/room_cmd.lua:1967    room_finish------------->    0.0"));
        var seconds = Parsing.All(Bytes(text)).OfType<RoomFinishLogEvent>().Select(e => e.Seconds).ToList();
        CollectionAssert.AreEqual(new[] { 30.0, 3.0, 0.0 }, seconds);
    }

    [TestMethod]
    public void ItemTipLines_GiveTheItemId_WithOrWithoutAUuid()
    {
        // 2026-11-05 as logged (4-space fields: name, uuid, id; the Weather Key has no uuid)
        var text = string.Concat(
            Entry(T0, "[LOG] -> module/item/item_tips_mgr.lua:200    Cat:item_tips_mgr print name, uuid, id :     Big Cat Cargo Loot    FAKE_UID_1    900003"),
            Entry(T0, "[LOG] -> module/item/item_tips_mgr.lua:200    Cat:item_tips_mgr print name, uuid, id :     Weather Key        900000"),
            Entry(T0, "[LOG] -> module/item/item_tips_mgr.lua:200    Cat:item_tips_mgr print name, uuid, id :     Vitality Gloves    FAKE_UID_2    2004030641"));
        var ids = Parsing.All(Bytes(text)).OfType<ItemTipLogEvent>().Select(e => e.ItemId).ToList();
        CollectionAssert.AreEqual(new long[] { 900003, 900000, 2004030641 }, ids);
    }

    [TestMethod]
    public void AThrottleLineWithoutARequestName_StillThrottles()
    {
        var events = Parsing.All(Bytes(Entry(T0, ThrottleSpecForm)));
        Assert.IsNull(events.OfType<ThrottleLogEvent>().Single().Request);
    }

    [TestMethod]
    public void TheReplay_ReadsAWholeFileFromAMoment_AndNothingBefore()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "replay_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, string.Concat(
            Header(T0),
            Entry(T0.AddMinutes(1), "[LOG] -> cave/cave_helper.lua:138    [scene_lod] load_scene done    vc"),
            Entry(T0.AddMinutes(5), "[LOG] -> cave/cave_helper.lua:138    [scene_lod] load_scene done    town"),
            Entry(T0.AddMinutes(6), "[LOG] -> sproto/SprotoService.lua:309    errcode from:     room.quit_room    99"),
            Entry(T0.AddMinutes(7), "[StageMgr] OnApplicationFocus LostFocus")));
        try
        {
            var events = LogReplay.Read(path, T0.AddMinutes(2));
            Assert.AreEqual("town", events.OfType<MapEnteredLogEvent>().Single().Map, "the map before the start moment is skipped");
            Assert.AreEqual(99, events.OfType<ServerReplyLogEvent>().Single().Code);
            Assert.IsEmpty(LogReplay.Read(path + ".missing", T0));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
