using Companion.Contracts.Knowledge;

namespace Companion.Contracts.Logs;

/// <summary>
/// Identity of one log file generation. The game renames log.txt to log.bak1 at every start (F14, PITFALL-05), so a
/// byte offset is meaningful only together with this identity. Any field changing = rotation.
/// </summary>
/// <param name="CreationUtc">File creation time (UTC).</param>
/// <param name="FileIndex">Volume file index from GetFileInformationByHandle.</param>
/// <param name="HeadFingerprint">Hex SHA-256 of the first line (the '--->>> add to unity log callback' line).</param>
public sealed record LogFileIdentity(DateTime CreationUtc, ulong FileIndex, string HeadFingerprint);

/// <summary>
/// A byte position in a specific log file generation. Events are ordered and bracketed by offset, never by time
/// (PITFALL-09). Offsets always point just after a complete entry (after its terminating LF).
/// </summary>
/// <param name="File">Log file generation.</param>
/// <param name="Offset">Byte offset in that file.</param>
public sealed record LogPosition(LogFileIdentity File, long Offset)
{
    /// <summary>True when both positions belong to the same file generation.</summary>
    public bool SameFile(LogPosition other) => File == other.File;
}

/// <summary>UI verbs parsed from module/foundation/ui/ui_mgr.lua lines (and the popup_seq real-show line).</summary>
public enum UiVerb
{
    /// <summary>'&lt;color=yellow&gt;show_ui&lt;/color&gt;    UI_X'.</summary>
    Show,
    /// <summary>'open const ui    UI_X'.</summary>
    OpenConst,
    /// <summary>'[ui mgr]hide top ui    UI_X|nil' (Esc / back closed the top; 'nil' = nothing was open).</summary>
    HideTop,
    /// <summary>'[ui_mgr]add to close_list    UI_X|&lt;number&gt;|nil'.</summary>
    AddToCloseList,
    /// <summary>'stack over -> remove    UI_X'.</summary>
    StackOverRemove,
    /// <summary>'[ui_mgr]dispose_ui immediately    UI_X' (cache eviction; ignored for state).</summary>
    DisposeImmediately,
    /// <summary>'[ui_mgr] dispose_all_close    UI_X'.</summary>
    DisposeAllClose,
    /// <summary>'[ui_mgr]check_clean_unused - do dispose    UI_X'.</summary>
    CheckCleanDispose,
    /// <summary>'check can open fail2    UI_X' (the game refused to open it).</summary>
    CheckCanOpenFail,
    /// <summary>'【to_hide】 hide_ui    UI_X'.</summary>
    ToHide,
    /// <summary>popup_seq_mgr.lua '[real show ui] tar_data.ui_id UI_X' (a queued popup is being released, F08).</summary>
    PopupRealShow,
}

/// <summary>Scene-load phases (cave_cli.lua / cave_helper.lua '[scene_load] ...').</summary>
public enum ScenePhase
{
    /// <summary>'load_scene start load' (or 'before loading_empty'): busy starts.</summary>
    StartLoad,
    /// <summary>'load_scene end load'.</summary>
    EndLoad,
    /// <summary>'time sample load scene need wait'.</summary>
    NeedWait,
    /// <summary>'on_cave_role_ready'.</summary>
    RoleReady,
    /// <summary>'hide_progress_now'.</summary>
    HideProgressNow,
    /// <summary>'load_scene hide_progress real': busy ends (+2 stable HUD captures).</summary>
    HideProgressReal,
    /// <summary>'abort hiding progress because locks ...': still busy.</summary>
    AbortHidingProgress,
}

/// <summary>Session-level markers.</summary>
public enum SessionMarker
{
    /// <summary>'--->>> add to unity log callback' (first line of a new log generation).</summary>
    LogCallbackAdded,
    /// <summary>'[StageMgr] OnApplicationQuit'.</summary>
    ApplicationQuit,
    /// <summary>'[emasdk]start logout'.</summary>
    LogoutStart,
    /// <summary>'[ema] dc_ping log trigger' (every 5 min while connected; absent &gt; 6 min = stalled/disconnected).</summary>
    PingTrigger,
    /// <summary>'Unity Application Notify: OnScreenSizeChange' (real activation re-applies resolution, F10).</summary>
    ScreenSizeChange,
}

/// <summary>Where a resolution line came from.</summary>
public enum ResolutionSource
{
    /// <summary>'[graphic] Current screen resolution    W    H'.</summary>
    CurrentScreen,
    /// <summary>'[graphic] Device resolution    W    H'.</summary>
    Device,
}

/// <summary>
/// One parsed log entry the companion cares about. Everything else (chat, analytics, server dumps) is dropped by the
/// reader and never buffered, journaled or copied (PRIV-01, LOG-11).
/// </summary>
/// <param name="Position">Position just after this entry (its end offset in its file generation).</param>
/// <param name="StartOffset">Byte offset where the entry starts.</param>
/// <param name="Timestamp">Game timestamp (local time, as written by the game).</param>
public abstract record LogEvent(LogPosition Position, long StartOffset, DateTime Timestamp);

/// <summary>A ui_mgr event.</summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="Verb">The verb.</param>
/// <param name="UiName">UI name ('[A-Z][A-Z0-9_]*', no UI_ prefix required); null for 'nil' or a numeric id.</param>
/// <param name="NumericId">Numeric id when the game logged a number instead of a name (ignored for state).</param>
public sealed record UiLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, UiVerb Verb, string? UiName, int? NumericId)
    : LogEvent(Position, StartOffset, Timestamp)
{
    /// <summary>True for 'hide top ui nil'.</summary>
    public bool IsHideTopNil => Verb == UiVerb.HideTop && UiName is null && NumericId is null;
}

/// <summary>'[StageMgr] OnApplicationFocus RecoverFocus|LostFocus'.</summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="Recovered">True for RecoverFocus, false for LostFocus.</param>
public sealed record FocusLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, bool Recovered)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>
/// 'UpdateModule LostFocuse [Name: X (UnityEngine.GameObject)]' plus its '|&lt;TAB&gt;' RaycastResult block. Logged at
/// focus LOSS after a registered press: its presence proves a press was registered since focus-on; the name/module are
/// what lies under the pointer at unfocus time (NOT necessarily the pressed object, F03/PITFALL-10).
/// </summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="ObjectName">GameObject name; empty when nothing was under the pointer.</param>
/// <param name="ModuleName">'|	module: Name: X' canvas name, or null.</param>
/// <param name="ScreenPosition">'|	screenPosition: (x, y)' (Unity bottom-left origin), or null.</param>
public sealed record PressBlockLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, string ObjectName, string? ModuleName, PointD? ScreenPosition)
    : LogEvent(Position, StartOffset, Timestamp)
{
    /// <summary>Client point of the raycast: (x, clientHeight - 1 - y). Proven: run 3 (1748,694) to (1748,314).</summary>
    public PointI? ToClient(int clientHeight) => ScreenPosition is { } p
        ? new PointI((int)Math.Round(p.X), clientHeight - 1 - (int)Math.Round(p.Y))
        : null;
}

/// <summary>'[graphic] Current screen resolution|Device resolution    W    H'.</summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="Source">Which line.</param>
/// <param name="Size">Width and height.</param>
public sealed record ResolutionLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, ResolutionSource Source, SizeI Size)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>'[collect_mgr] set_auto_collect_status N' (logged only on change; bootstrap from byte 0, F15).</summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="Status">Raw status value (0, 1, 2, 3 seen).</param>
public sealed record AutoCollectLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, int Status)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>'[scene_load] ...' phases.</summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="Phase">Phase.</param>
public sealed record SceneLoadLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, ScenePhase Phase)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>'module/lifeskill/cook/lifeskill_cook_mgr.lua:NNN is_block x y' (one per craft tick; ticks = count - 1, STK-01).</summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="X">x argument.</param>
/// <param name="Y">y argument.</param>
public sealed record CraftTickLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, double X, double Y)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>'[jank_detector] jank over : S' (a game stall of S seconds).</summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="Seconds">Stall length.</param>
public sealed record JankLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, double Seconds)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>A session marker.</summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="Marker">Marker.</param>
public sealed record SessionLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, SessionMarker Marker)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>
/// 'select_role_view.lua [login] req_login_role &lt;server&gt;-&lt;role&gt;'. The raw token never leaves the Logs unit: it is
/// hashed with <see cref="ICharacterKeyHasher"/> while parsing (KS-01).
/// </summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="Character">Salted hash of the role token.</param>
public sealed record CharacterLoginLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, CharacterKey Character)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>'[base_network.lua] request too frequency' (back off, F21).</summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
public sealed record ThrottleLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp)
    : LogEvent(Position, StartOffset, Timestamp)
{
    /// <summary>The throttled request ('town.task_info'), when the line names one (2026-11-04, the daily checklist).</summary>
    public string? Request { get; init; }
}

/// <summary>
/// A server refusal (2026-11-04, the daily checklist): 'sproto/SprotoService.lua:N    errcode from:     arena.arena_sweep    2659'.
/// Successful requests are never logged; some refusals mean "already done today" (arena 2659 = no tries left).
/// </summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="Request">Protocol request name, 'module.call'.</param>
/// <param name="Code">The server's error code.</param>
public sealed record ServerReplyLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, string Request, int Code)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>
/// A server push handled by a game module (2026-11-04): 'Cat:convoy_mgr.lua[notify_convoy_commit_task] resp ...'. Only the
/// module and push names are kept, plus the Honor War stage's win flag ('prt_resp.is_win: true'); no payload.
/// </summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="Module">Lua module name ('convoy_mgr').</param>
/// <param name="Name">Push name ('notify_convoy_commit_task').</param>
/// <param name="Win">The 'is_win' flag when the line has one.</param>
public sealed record ServerPushLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, string Module, string Name, bool? Win)
    : LogEvent(Position, StartOffset, Timestamp)
{
    /// <summary>'module.name', the form the checklist data uses.</summary>
    public string FullName => Module + "." + Name;
}

/// <summary>
/// A quest step (2026-11-04): 'task_mgr.lua:N    action state: 2, chain:70694, step:1, task_id:110014054', or its hand-in
/// 'realy commit:110014054' (<see cref="Committed"/>, chain and step 0). Kanu's Wish quests are 1100140xx.
/// </summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="Chain">Quest chain id (0 on a commit line).</param>
/// <param name="Step">Step in the chain (0 on a commit line).</param>
/// <param name="TaskId">Quest id.</param>
/// <param name="Committed">True for the hand-in line.</param>
public sealed record QuestLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, int Chain, int Step, long TaskId, bool Committed)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>A map finished loading (2026-11-04): 'cave/cave_helper.lua:N    [scene_lod] load_scene done    town'.</summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="Map">The map's internal name ('town' is Norton Town, 'nvwushen_06' a Goddess Tower floor).</param>
public sealed record MapEnteredLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, string Map)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>
/// The server switched a timed feature on or off (2026-11-04): 'gameplay_network.lua:N    [notify_change_feature]
/// instance:    1015    risk_war    0'. At login the whole table arrives in one burst (a snapshot, not switches).
/// </summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="GameplayId">The feature's number.</param>
/// <param name="Instance">The instance name ('guild_spa_21_days_later').</param>
/// <param name="Open">True when switched on.</param>
public sealed record FeatureSwitchLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, int GameplayId, string Instance, bool Open)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>Which server reset a <see cref="ResetLogEvent"/> announces.</summary>
public enum ResetKind
{
    /// <summary>'REQUEST notify.on_new_day': the calendar day (02:00 local in 2026-11).</summary>
    NewDay,

    /// <summary>'server_push.daily_update': the daily update (07:00 local in 2026-11).</summary>
    DailyUpdate,

    /// <summary>'REQUEST notify.weekly_update': Monday's daily update.</summary>
    WeeklyUpdate,
}

/// <summary>A server reset announcement (2026-11-04), seen only while the game runs at that moment.</summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="Kind">Which reset.</param>
public sealed record ResetLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, ResetKind Kind)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>Any 'module/shop/pay_mgr.lua' line other than the login pair ('[pay mgr]on_cave_role_ready', 'req get pay show') = forbidden trigger (F09).</summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
public sealed record PayLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>'[lifeskill][autoeat]drug_mgr supply.eat_food req ...' (food eaten by the game; item id dropped, STK-03).</summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
public sealed record AutoEatLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>
/// A press on the Kitty Tea House board (2026-10-30): 'module/triple_town/chess_play_widget.lua:N    on_grid_click    R
/// C    P', logged for every press on a square, the user's or ours. Row and column count from 1 at the top left (row 1,
/// column 1 is the tray); the piece is the one in hand (1 Cookie, 2 Sponge, 3 Pudding, 4 Choco, 14 White Cat, 15 Orange
/// Cat, 16 Cutlery, 17 Oven, decoded against the helper's journal).
/// </summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="Row">Row, 1..6.</param>
/// <param name="Column">Column, 1..6.</param>
/// <param name="Piece">The game's number of the piece in hand.</param>
public sealed record GridClickLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, int Row, int Column, int Piece)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>
/// A chat message to the user arrived (2026-10-18/20): '[chat_list]add wait show &lt;channel&gt; &lt;n&gt;' (seen for received
/// whispers, channel 1). Only the channel is kept: the line has no sender and no text, and nothing else is read.
/// </summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="Channel">The chat list's channel number (1 for the whispers seen so far).</param>
public sealed record MessageArrivedLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, int Channel)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>'ui_mgr:true_show_ui ban by guide' / 'ban by guide mask' (tutorial guide blocks UI opens, F16).</summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="Mask">True for the 'ban by guide mask' variant.</param>
public sealed record GuideBanLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, bool Mask)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>
/// 'module/item/item_tips_mgr.lua:N    Cat:item_tips_mgr print name, uuid, id :    &lt;name&gt;    [&lt;uuid&gt;]    &lt;id&gt;'
/// (live 2026-10-15): logged whenever an item tip opens (UI_ITEM_TIPS_PASS_TOUCH), e.g. after a press on the Make tab's
/// product icon. Gives the item's exact name and numeric id (game item data, not personal data).
/// </summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="ItemName">Item name as logged (may contain spaces).</param>
/// <param name="ItemId">Item id: the last integer of the line.</param>
public sealed record ItemTipLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, string ItemName, long ItemId)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>
/// A finished guide step (2026-11-04): 'module/guide/guide_mgr.lua:1624    Cat:guide_mgr.lua[finish_guide] guide.key
/// order_take3    nil'. One 'order_take3' (+ 'order_take4') per homestead order handed in.
/// </summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="Key">The guide key ('order_take3').</param>
public sealed record GuideKeyLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, string Key)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>A shop refresh (2026-11-04): 'module/shop/shop_mgr.lua:1090    [shop] shop_refresh    10028' (10028 = the black market).</summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="ShopId">The shop's id.</param>
public sealed record ShopRefreshLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, int ShopId)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>
/// One slot taken from the homestead helper (2026-11-04): 'module/home/home_mgr_housekeeper.lua:182    home helper
/// req_collect_housekeeper_products    3'.
/// </summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="Slot">The slot, 1..6.</param>
public sealed record HelperCollectLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, int Slot)
    : LogEvent(Position, StartOffset, Timestamp);

/// <summary>
/// An instance run ended (2026-11-06): 'battle/room_cmd.lua:1967    room_finish------------->    30.0'. The number is
/// how long the room stays before it closes. Demon trial: 30 = a reward run (loot drops), 3 = a helping run (none).
/// </summary>
/// <param name="Position">See <see cref="LogEvent"/>.</param>
/// <param name="StartOffset">See <see cref="LogEvent"/>.</param>
/// <param name="Timestamp">See <see cref="LogEvent"/>.</param>
/// <param name="Seconds">The logged number (30.0, 10.0, 3.0, ...).</param>
public sealed record RoomFinishLogEvent(LogPosition Position, long StartOffset, DateTime Timestamp, double Seconds)
    : LogEvent(Position, StartOffset, Timestamp);