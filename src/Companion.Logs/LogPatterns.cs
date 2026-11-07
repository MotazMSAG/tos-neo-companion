using System.Text.RegularExpressions;

namespace Companion.Logs;

/// <summary>
/// Message patterns (SPEC 6.1), anchored on the message part after 'ts\tLevel\t'. Separators in the real log are 4
/// spaces (sometimes 5 when the game adds a trailing space) and are parsed as \s+. Every pattern was checked against
/// byte-exact lines of the real log (see tests\Companion.Logs.Tests\Fixtures); where the real line differs from the
/// SPEC text the pattern was widened minimally and the change is listed in the unit report.
/// </summary>
internal static partial class LogPatterns
{
    /// <summary>Timestamped first line of an entry: 'yyyy-MM-dd HH:mm:ss.fff\tLevel\t'.</summary>
    [GeneratedRegex(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}\t[A-Za-z]+\t", RegexOptions.CultureInvariant)]
    internal static partial Regex TimestampPrefix();

    /// <summary>ui_mgr UI verbs (SPEC table, unchanged).</summary>
    [GeneratedRegex(@"^\[LOG\] -> module/foundation/ui/ui_mgr\.lua:\d+\s+(<color=yellow>show_ui</color>|open const ui|\[ui mgr\]hide top ui|\[ui_mgr\]add to close_list|stack over -> remove|\[ui_mgr\]dispose_ui immediately|\[ui_mgr\] dispose_all_close|\[ui_mgr\]check_clean_unused - do dispose|check can open fail\d*|【to_hide】 hide_ui)\s+([A-Z][A-Z0-9_]*|nil|\d+)\s*$", RegexOptions.CultureInvariant)]
    internal static partial Regex UiVerb();

    /// <summary>popup_seq real show. The real line carries a trailing counter ('UI_ITEM_REWARD    1'), so the SPEC's '\s*$' became a boundary.</summary>
    [GeneratedRegex(@"^\[LOG\] -> \S*popup_seq_mgr\.lua:\d+\s+.*\[real show ui\]\s*tar_data\.ui_id\s+([A-Z][A-Z0-9_]*)(?=\s|$)", RegexOptions.CultureInvariant)]
    internal static partial Regex PopupRealShow();

    /// <summary>Guide ban. The real 'ban by guide mask' line comes from module/guide/guide_mgr.lua, so that file is accepted too.</summary>
    [GeneratedRegex(@"^\[LOG\] -> module/(?:foundation/ui/ui_mgr|guide/guide_mgr)\.lua:\d+\s+.*\bban by guide( mask)?\b", RegexOptions.CultureInvariant)]
    internal static partial Regex GuideBan();

    /// <summary>Focus (no '[LOG] ->' prefix).</summary>
    [GeneratedRegex(@"^\[StageMgr\] OnApplicationFocus (RecoverFocus|LostFocus)\s*$", RegexOptions.CultureInvariant)]
    internal static partial Regex Focus();

    /// <summary>Press block first line; bare 'UpdateModule LostFocuse ' = nothing under the pointer.</summary>
    [GeneratedRegex(@"^UpdateModule LostFocuse(?: Name: (.*) \(UnityEngine\.GameObject\))?\s*$", RegexOptions.CultureInvariant)]
    internal static partial Regex PressBlock();

    /// <summary>Press block continuation: '|\tmodule: Name: X (UnityEngine.GameObject)'.</summary>
    [GeneratedRegex(@"^\|\tmodule: Name: (.*) \(UnityEngine\.GameObject\)", RegexOptions.CultureInvariant)]
    internal static partial Regex PressModule();

    /// <summary>Press block continuation: '|\tscreenPosition: (x, y)'.</summary>
    [GeneratedRegex(@"^\|\tscreenPosition: \((-?[\d.]+), (-?[\d.]+)\)", RegexOptions.CultureInvariant)]
    internal static partial Regex PressScreenPosition();

    /// <summary>Resolution. The real 'Device resolution' line at start-up has no 'file.lua:N' part, so it is optional.</summary>
    [GeneratedRegex(@"^\[LOG\] -> (?:\S+\.lua:\d+\s+)?\[graphic\] (Current screen resolution|Device resolution)\s+(\d+)\s+(\d+)", RegexOptions.CultureInvariant)]
    internal static partial Regex Resolution();

    /// <summary>OnScreenSizeChange. Real: '[LOG] -> lss_start.lua:856    Unity Application Notify:    OnScreenSizeChange'.</summary>
    [GeneratedRegex(@"^(?:\[LOG\] -> (?:\S+:\d+\s+)?)?Unity Application Notify:\s+OnScreenSizeChange\b", RegexOptions.CultureInvariant)]
    internal static partial Regex ScreenSizeChange();

    /// <summary>Auto-collect status (a traceback follows on the same line and in '|' lines).</summary>
    [GeneratedRegex(@"^\[LOG\] -> \S+\.lua:\d+\s+\[collect_mgr\] set_auto_collect_status\s+(\d+)", RegexOptions.CultureInvariant)]
    internal static partial Regex AutoCollect();

    /// <summary>Scene load phases. A trailing boundary keeps 'load_scene end loading_empty' from reading as 'end load'.</summary>
    [GeneratedRegex(@"^\[LOG\] -> cave/cave_(?:cli|helper)\.lua:\d+\s+\[scene_load\]\s*(load_scene start load|before loading_empty|load_scene end load|time sample load scene need wait|on_cave_role_ready|hide_progress_now|load_scene hide_progress real|abort hiding progress)(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    internal static partial Regex SceneLoad();

    /// <summary>Craft tick.</summary>
    [GeneratedRegex(@"^\[LOG\] -> module/lifeskill/cook/lifeskill_cook_mgr\.lua:\d+\s+is_block\s+(-?[\d.]+)\s+(-?[\d.]+)", RegexOptions.CultureInvariant)]
    internal static partial Regex CraftTick();

    /// <summary>Jank (with or without the '[LOG] -> file:N' prefix).</summary>
    [GeneratedRegex(@"^(?:\[LOG\] -> \S+:\d+\s+)?\[jank_detector\] jank over\s*:\s*([\d.]+)", RegexOptions.CultureInvariant)]
    internal static partial Regex Jank();

    /// <summary>Session marker: first line of a generation.</summary>
    [GeneratedRegex(@"^--->>> add to unity log callback\s*$", RegexOptions.CultureInvariant)]
    internal static partial Regex SessionLogCallback();

    /// <summary>Session marker: quit.</summary>
    [GeneratedRegex(@"^\[StageMgr\] OnApplicationQuit\s*$", RegexOptions.CultureInvariant)]
    internal static partial Regex SessionQuit();

    /// <summary>Session marker: logout (real: '[LOG] -> vendor/ema/ema_vendor.lua:261    [emasdk]start logout').</summary>
    [GeneratedRegex(@"^\[LOG\] -> \S+\.lua:\d+\s+\[emasdk\]start logout\s*$", RegexOptions.CultureInvariant)]
    internal static partial Regex SessionLogout();

    /// <summary>Session marker: 5-minute ping (real: '[LOG] -> vendor/ema/ema_vendor.lua:838    [ema] dc_ping log trigger').</summary>
    [GeneratedRegex(@"^\[LOG\] -> \S+\.lua:\d+\s+\[ema\] dc_ping log trigger\s*$", RegexOptions.CultureInvariant)]
    internal static partial Regex SessionPing();

    /// <summary>Login role token (hashed at once, never kept).</summary>
    [GeneratedRegex(@"^\[LOG\] -> \S*select_role_view\.lua:\d+\s+\[login\] req_login_role\s+(\S+)", RegexOptions.CultureInvariant)]
    internal static partial Regex CharacterLogin();

    /// <summary>
    /// Throttle. The real line is '[LOG] -> module/foundation/base_network.lua:155    request too frequency :    x.y';
    /// the SPEC's '[base_network.lua] request too frequency' form is accepted too.
    /// </summary>
    [GeneratedRegex(@"^(?:\[LOG\] -> )?\S*base_network\.lua(?::\d+|\])\s+request too frequency", RegexOptions.CultureInvariant)]
    internal static partial Regex Throttle();

    /// <summary>pay_mgr line (the rest is checked against the two login exceptions).</summary>
    [GeneratedRegex(@"^\[LOG\] -> module/shop/pay_mgr\.lua:\d+\s+(.*)$", RegexOptions.CultureInvariant)]
    internal static partial Regex Pay();

    /// <summary>Auto-eat request. Real: '[lifeskill][autoeat] drug_mgr supply.eat_food req    N' (a space after ']').</summary>
    [GeneratedRegex(@"^\[LOG\] -> \S+\.lua:\d+\s+\[lifeskill\]\[autoeat\]\s*drug_mgr supply\.eat_food req\b", RegexOptions.CultureInvariant)]
    internal static partial Regex AutoEat();

    /// <summary>Kitty Tea House board press. Real: '[LOG] -> module/triple_town/chess_play_widget.lua:409    on_grid_click    5    3    14'.</summary>
    [GeneratedRegex(@"^\[LOG\] -> module/triple_town/chess_play_widget\.lua:\d+\s+on_grid_click\s+([1-6])\s+([1-6])\s+(\d{1,3})\s*$", RegexOptions.CultureInvariant)]
    internal static partial Regex GridClick();

    /// <summary>A chat message to the user arrived. Real: '[LOG] -> module/chat/chat_list.lua:1305    [chat_list]add wait show    1    &lt;n&gt;'.</summary>
    [GeneratedRegex(@"^\[LOG\] -> \S*chat_list\.lua:\d+\s+\[chat_list\]add wait show\s+(\d{1,3})", RegexOptions.CultureInvariant)]
    internal static partial Regex MessageArrived();

    /// <summary>
    /// Item tip (live 2026-10-15): 'module/item/item_tips_mgr.lua:200    Cat:item_tips_mgr print name, uuid, id :     White
    /// Toast        1051000'. Fields are 4 spaces apart (the empty uuid leaves 8), so a field break is 2+ spaces or a TAB
    /// while the name is words joined by single spaces; any uuid token is skipped and the id is the last integer.
    /// End-anchored and length-bounded: the privacy filter keeps exactly this shape.
    /// </summary>
    [GeneratedRegex(@"^\[LOG\] -> module/item/item_tips_mgr\.lua:\d+\s+Cat:item_tips_mgr print name, uuid, id\s*:\s+(\S{1,64}(?: \S{1,64}){0,15})(?:(?:\s{2,}|\t)\S{1,64})*?(?:\s{2,}|\t)(\d{1,18})\s*$", RegexOptions.CultureInvariant)]
    internal static partial Regex ItemTip();

    /// <summary>The request named by a throttle line: 'request too frequency :    town.task_info'.</summary>
    [GeneratedRegex(@"request too frequency\s*:\s*([a-z_0-9]{1,64}\.[a-z_0-9]{1,64})\s*$", RegexOptions.CultureInvariant)]
    internal static partial Regex ThrottleRequest();

    /// <summary>Server refusal: '[LOG] -> sproto/SprotoService.lua:309    errcode from:     arena.arena_sweep    2659'.</summary>
    [GeneratedRegex(@"^\[LOG\] -> sproto/SprotoService\.lua:\d+\s+errcode from:\s+([a-z_0-9]{1,64}\.[a-z_0-9]{1,64})\s+(-?\d{1,9})\s*$", RegexOptions.CultureInvariant)]
    internal static partial Regex ServerReply();

    /// <summary>Server push: '[LOG] -> module/convoy/convoy_mgr.lua:1566    Cat:convoy_mgr.lua[notify_convoy_commit_task] resp ...'.</summary>
    [GeneratedRegex(@"^\[LOG\] -> \S+\.lua:\d+\s+Cat:([a-z_0-9]{1,64})\.lua\[(notify_[a-z_0-9]{1,64})\]", RegexOptions.CultureInvariant)]
    internal static partial Regex ServerPush();

    /// <summary>The Honor War stage's win flag: 'prt_resp.is_win:    true'.</summary>
    [GeneratedRegex(@"is_win:\s+(true|false)\b", RegexOptions.CultureInvariant)]
    internal static partial Regex PushWin();

    /// <summary>Quest step: '[LOG] -> module/task/task_mgr.lua:1158    action state: 2, chain:70694, step:1, task_id:110014054'.</summary>
    [GeneratedRegex(@"^\[LOG\] -> module/task/task_mgr\.lua:\d+\s+action state:\s*\d+,\s*chain:(\d{1,9}),\s*step:(\d{1,4}),\s*task_id:(\d{1,18})\s*$", RegexOptions.CultureInvariant)]
    internal static partial Regex QuestStep();

    /// <summary>Quest hand-in: '[LOG] -> module/task/task_mgr.lua:1195    realy commit:110014054'.</summary>
    [GeneratedRegex(@"^\[LOG\] -> module/task/task_mgr\.lua:\d+\s+realy commit:(\d{1,18})\s*$", RegexOptions.CultureInvariant)]
    internal static partial Regex QuestCommit();

    /// <summary>Map loaded: '[LOG] -> cave/cave_helper.lua:138    [scene_lod] load_scene done    town'.</summary>
    [GeneratedRegex(@"^\[LOG\] -> cave/cave_helper\.lua:\d+\s+\[scene_lod\] load_scene done\s+([A-Za-z0-9_]{1,64})\s*$", RegexOptions.CultureInvariant)]
    internal static partial Regex MapEntered();

    /// <summary>Feature switch: '[LOG] -> module/gameplay/gameplay_network.lua:19    [notify_change_feature] instance:    1015    risk_war    0'.</summary>
    [GeneratedRegex(@"^\[LOG\] -> \S*gameplay_network\.lua:\d+\s+\[notify_change_feature\] instance:\s+(\d{1,9})\s+([A-Za-z0-9_]{1,80})\s+(0|1)\s*$", RegexOptions.CultureInvariant)]
    internal static partial Regex FeatureSwitch();

    /// <summary>Calendar or weekly reset: '[LOG] -> sproto/SprotoService.lua:432    unknown pack type!!!    REQUEST    notify.on_new_day    table: ...'.</summary>
    [GeneratedRegex(@"^\[LOG\] -> sproto/SprotoService\.lua:\d+\s+unknown pack type!!!\s+REQUEST\s+notify\.(on_new_day|weekly_update)\b", RegexOptions.CultureInvariant)]
    internal static partial Regex ResetRequest();

    /// <summary>Daily update: '[LOG] -> do_register_thing.lua:60    server_push.daily_update'.</summary>
    [GeneratedRegex(@"^\[LOG\] -> \S*do_register_thing\.lua:\d+\s+server_push\.daily_update\s*$", RegexOptions.CultureInvariant)]
    internal static partial Regex DailyUpdate();

    /// <summary>Guide step: '[LOG] -> module/guide/guide_mgr.lua:1624    Cat:guide_mgr.lua[finish_guide] guide.key    order_take3    nil'.</summary>
    [GeneratedRegex(@"^\[LOG\] -> \S*guide_mgr\.lua:\d+\s+Cat:guide_mgr\.lua\[finish_guide\] guide\.key\s+([A-Za-z0-9_]{1,64})(?:\s|$)", RegexOptions.CultureInvariant)]
    internal static partial Regex GuideKey();

    /// <summary>Shop refresh: '[LOG] -> module/shop/shop_mgr.lua:1090    [shop] shop_refresh    10028'.</summary>
    [GeneratedRegex(@"^\[LOG\] -> \S*shop_mgr\.lua:\d+\s+\[shop\] shop_refresh\s+(\d{1,9})\s*$", RegexOptions.CultureInvariant)]
    internal static partial Regex ShopRefresh();

    /// <summary>Helper slot taken: '[LOG] -> module/home/home_mgr_housekeeper.lua:182    home helper req_collect_housekeeper_products    3'.</summary>
    [GeneratedRegex(@"^\[LOG\] -> \S*home_mgr_housekeeper\.lua:\d+\s+home helper req_collect_housekeeper_products\s+(\d{1,2})\s*$", RegexOptions.CultureInvariant)]
    internal static partial Regex HelperCollect();

    /// <summary>Instance end: '[LOG] -> battle/room_cmd.lua:1967    room_finish------------->    30.0'.</summary>
    [GeneratedRegex(@"^\[LOG\] -> \S*room_cmd\.lua:\d+\s+room_finish-*>\s+(\d{1,5}(?:\.\d{1,3})?)\s*$", RegexOptions.CultureInvariant)]
    internal static partial Regex RoomFinish();
    /// <summary>Role-id-like tokens scrubbed from every kept line (SPEC 12).</summary>
    [GeneratedRegex(@"\b\d{4,5}-\d{4,6}\b", RegexOptions.CultureInvariant)]
    internal static partial Regex RoleIdLike();
}
