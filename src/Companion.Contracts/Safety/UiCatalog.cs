namespace Companion.Contracts.Safety;

/// <summary>How the engine treats a UI name seen in the log (SPEC 6.4). Precedence: see <see cref="UiCatalog.Precedence"/>.</summary>
public enum UiKind
{
    /// <summary>Never seen in any list and never in '[ui mgr]hide top ui'. Caused by us: one Esc + survey capture; a second one in the job = HALT.</summary>
    Unknown = 0,

    /// <summary>Opens/closes by itself, never Esc-closable (UI_CHANT, UI_MONEY, ...). Ignored by WRONG_UI and by gates.</summary>
    Overlay = 1,

    /// <summary>Esc-closable full screen (seen in '[ui mgr]hide top ui'). A stack UI other than the expected one = WRONG_UI.</summary>
    Stack = 2,

    /// <summary>Purchase / irreversible / confirm. No clicks and no letter keys while open; caused by us = one Esc then HALT; foreign = pause, no Esc.</summary>
    Forbidden = 3,

    /// <summary>Other players or matchmaking (invites, votes, card picks). Foreign = pause + notify, never Esc.</summary>
    Social = 4,

    /// <summary>Loading, cinema, scenario, login, revive, tutorial mask, PvP match: WAIT (no input at all).</summary>
    Busy = 5,

    /// <summary>Crafting station mini-UI (craft in progress): no key, no click, no Reset.</summary>
    Station = 6,
}

/// <summary>
/// Hard-coded UI lists (code, not json: UI-6). Built from the 2026-10-13/14 logs. The Logs unit classifies a name with
/// <see cref="Precedence"/>: Forbidden &gt; Social &gt; Busy &gt; Station &gt; Stack (seed or learned from 'hide top ui') &gt;
/// Overlay (seed) &gt; Unknown. A learned Stack overrides the Overlay seed.
/// </summary>
public static class UiCatalog
{
    /// <summary>Classification precedence, highest first.</summary>
    public static readonly IReadOnlyList<UiKind> Precedence =
        [UiKind.Forbidden, UiKind.Social, UiKind.Busy, UiKind.Station, UiKind.Stack, UiKind.Overlay, UiKind.Unknown];

    /// <summary>The in-world HUD root; 'open const ui UI_MAIN' marks entering the world (F14).</summary>
    public const string MainUi = "UI_MAIN";

    /// <summary>Forbidden: exact names (F09, UI-6, CR-02).</summary>
    public static readonly IReadOnlyList<string> ForbiddenExact =
    [
        "UI_RECHARGE", "UI_TRIGGER_GIFT", "UI_GOODS_BUY", "UI_TRADE_PURCHASE", "UI_TRADE_STOCKING",
        "UI_BATTLE_PASS_BUY_ADVANCE", "UI_BATTLE_PASS_PP_BUY", "UI_TALENT_BUY_POINT", "UI_MAIN_GACHA",
        "UI_HOME_TOWN_GACHA", "UI_CAT_GACHA_WISH_VIEW", "UI_MODEL_SINGLE_SHOP", "UI_REST_MONEY",
        "UI_HOME_SPEED_COIN_VIEW", "UI_TOKEN_EXCHANGE_VIEW", "UI_TIPS2_EXCHANGE", "UI_NEW_SERV_LOGIN",
        "UI_CHANGE_NAME_VIEW", "UI_EQUIP_INHERIT", "UI_COMMON_BATCH_APPLY", "UI_SHOP_DISCOUNT", "LIFE_CAREER_HELP_CONFIRM",
    ];

    /// <summary>Forbidden: name prefixes.</summary>
    public static readonly IReadOnlyList<string> ForbiddenPrefixes = ["UI_SHOP_", "UI_TIPS2_", "UI_TRADE_", "UI_BATTLE_PASS_BUY"];

    /// <summary>
    /// Exact names a Forbidden PREFIX does not cover (the exact list and the substrings still do). UI_TIPS2_OVERLAY: the
    /// tip layer of the talent/skill screens, 'open const ui' and never a close line, like UI_TIPS2 (live 2026-10-18:
    /// opened at 04:52 on the skill screen, it parked a Craft job as a foreign Forbidden window for good).
    /// </summary>
    public static readonly IReadOnlyList<string> ForbiddenPrefixExempt = ["UI_TIPS2_OVERLAY"];

    /// <summary>
    /// Overlay seeds a 'hide top ui' line never turns into a learned Stack (2026-10-18): an Esc while the short game
    /// tip UI_TIPS2 is shown logs 'hide top ui UI_TIPS2'; learned as a Stack, the tip shown on every gathering tick then
    /// tripped the popup gate of every click during gathering (the gear notice's box tick was aborted).
    /// </summary>
    public static readonly IReadOnlyList<string> NeverLearnedStack = ["UI_TIPS2", "UI_TIPS2_OVERLAY"];

    /// <summary>Forbidden: substrings (checked on the upper-case name).</summary>
    public static readonly IReadOnlyList<string> ForbiddenContains =
        ["CONFIRM", "GACHA", "RECHARGE", "PURCHASE", "_BUY", "_SHOP", "EXCHANGE", "_PAY"];

    /// <summary>Social / matchmaking: exact names.</summary>
    public static readonly IReadOnlyList<string> SocialExact =
    [
        "UI_TEAM_QUICK_ACCEPT_VIEW", "UI_TEAM_VOTE", "UI_TEAM_3V3_INVITE", "UI_KEY_REWARD_GET_CARD",
        "UI_MATCHING_STATUS_VIEW", "UI_AMUSEMENT_PARK_MATCH", "UI_FRIEND_ADD",
    ];

    /// <summary>Busy: exact names.</summary>
    public static readonly IReadOnlyList<string> BusyExact =
        ["UI_LOAD_PROGRESS", "UI_REVIVE", "UI_GUIDE_MASK", "UI_SERVER", "UI_SELECT_ROLE", "UI_SCENARIO_EXIT"];

    /// <summary>Busy: prefixes (cinema, scenario, 3v3 match).</summary>
    public static readonly IReadOnlyList<string> BusyPrefixes = ["UI_CINEMA_", "UI_SCENARIO", "UI_TEAM_3V3_"];

    /// <summary>Crafting station mini-UIs (CR-01).</summary>
    public static readonly IReadOnlyList<string> StationExact = ["UI_LIFE_ALCHEMIST_MAKE_POT", "UI_LIFE_ENCHANT_TABLE"];

    /// <summary>Crafting views (the recipe list); opened by the Ability menu button.</summary>
    public static readonly IReadOnlyList<string> CraftViews = ["UI_LIFESKILL_MAKE_VIEW", "UI_LIFESKILL_AUTO_COLLECT"];

    /// <summary>Popups expected right after a craft batch; never counted as failures (CR-01).</summary>
    public static readonly IReadOnlyList<string> PostCraftPopups =
        ["UI_LIFESKILL_PROMOTION", "UI_LIFE_PROMOTION_SUCCESS", "UI_LIFE_ENCHANT_SUCCESS"];

    /// <summary>Stack seed: names seen in '[ui mgr]hide top ui' or opened/closed as full screens in the 2026-10-13/14 logs.</summary>
    public static readonly IReadOnlyList<string> StackSeed =
    [
        "UI_BAG_MAIN", "UI_MAIL", "UI_CHAT", "UI_COMMON_SETTLE_VIEW", "UI_ITEM_REWARD", "UI_TEAM_MAIN", "UI_ITEM_TIPS",
        "UI_ITEM_TIPS_PASS_TOUCH", "UI_RANK_VIEW", "UI_NEW_PLAY_ENTRANCE", "UI_TALENT_MAIN", "UI_TALENT_THIRD_FAKE_MAIN",
        "UI_GUILD_MAIN", "UI_GUILD_FORGE", "UI_GUILD_FORGE_HELP_LUCK", "UI_OTHER_PLAYER_INFO_VIEW", "UI_OTHER_PLAYER_TIPS",
        "UI_ARENA_VIEW", "UI_HOME_TOWN_MAIN", "UI_HOME_TOWN_RIGHT_REWARD", "UI_TOWN_FACTORY_MAIN", "UI_HOME_ORDER",
        "UI_SELECT_BATTLE_FOOD_TIPS", "UI_KINGDOM_BP_VIEW", "UI_LIFEBUFF", "UI_CAVE_CENTER_TIPS", "UI_HOME_PET_VIEW",
        "UI_LIFESKILL_MAKE_VIEW", "UI_LIFESKILL_PROMOTION", "UI_LIFE_PROMOTION_SUCCESS", "UI_LIFE_ENCHANT_SUCCESS",
        "GUILD_WAR_GVE_BATTLE_RESULT_SUCC", "GUILD_WAR_GVE_SETTLEMENT_VIEW",
        // "New Ability Phase" announcement, sometimes shown with the Make view; closed by one Esc (live run 2026-10-14).
        "UI_LIFE_STAGE_PREVIEW",
        // What's On (2026-10-30): the events window that holds the Kitty Tea House board ('hide top ui' seen in the log),
        // and the Tea House's own windows: the round's end screen (shown after the press that fills the board, closed by
        // its Restart button), help, tasks and rankings.
        "UI_ACTIVITY_CENTER", "UI_TRIPLE_TOWN_SETTLE", "UI_TRIPLE_TOWN_HELP", "UI_TRIPLE_TOWN_TASK", "UI_TRIPLE_TOWN_RANK",
    ];

    /// <summary>Overlay seed: UIs that open by themselves (F04, PITFALL-06, CR-03).</summary>
    public static readonly IReadOnlyList<string> OverlaySeed =
    [
        "UI_CHANT", "UI_BOSS_CHANT", "UI_NORMAL_PROGRESS", "UI_ITEM_GET_TIP", "UI_MONEY", "UI_LOCK_ARROW", "UI_LOCK_TARGET",
        "UI_FLY_EFFECT", "UI_RED_BLUE_TIPS", "UI_SCROLL_TIPS_MIDDLE", "UI_BLOOD_BAR", "UI_REWARD_FALL", "UI_NPC_TIPS",
        "UI_NPC_TALK", "UI_ENTER_SCENE_TIPS", "UI_PLAYER_INFO_NOTIFY_VIEW", "UI_DIG_ALERT", "UI_MASK", "UI_FISH_TIPS",
        "UI_HOME_UPGRADE_BAR", "MAIN_TARGET_BUTTON", "MAIN_TARGET_BUTTON_BIG", "MAIN_PHONE_BUTTON", "GUILD_WAR_GVE_BTN_TIPS",
        // Game tip popup: shown at 16:29:28 and closed by itself at 16:29:33 ('add to close_list', no 'hide top ui');
        // as an unknown UI it made Reset park (live run 2026-10-14, c13).
        "UI_POP_TIPS_VIEW",
        // Short game tip: 74 shows and 69 'open const ui' in the 2026-10-13/15 logs and never a close line, so as a
        // Forbidden name (old 'UI_TIPS2' prefix) one tip blocked every job for good (live 2026-10-15). Its variants
        // (UI_TIPS2_EXCHANGE, other UI_TIPS2_*) stay Forbidden.
        "UI_TIPS2",
        // The same tip family's layer (2026-10-18): one show, 'open const ui', never a close (see ForbiddenPrefixExempt).
        "UI_TIPS2_OVERLAY",
        // Town HUD parts: shown on entering the home town and closed only on leaving it, so while in town the banner
        // (top left) made Reset park as a user window (live 2026-10-15).
        "UI_HOME_TOWN_INFO", "UI_HOME_TOWN_BUBBLE",
    ];

    /// <summary>
    /// Esc-safe: may be closed with Esc even when we did not open them (no user data inside). Anything else open at job
    /// start is the user's: the job parks and notifies once (F20), unless config policy.closeUserUis is on.
    /// </summary>
    public static readonly IReadOnlyList<string> EscSafe =
    [
        "UI_BAG_MAIN", "UI_HOME_PET_VIEW", "UI_LIFESKILL_MAKE_VIEW", "UI_ITEM_REWARD", "UI_COMMON_SETTLE_VIEW",
        "UI_ITEM_TIPS", "UI_ITEM_TIPS_PASS_TOUCH", "UI_SPECIAL_BUFF_TIPS", "UI_RANK_VIEW", "UI_NEW_PLAY_ENTRANCE", "UI_TALENT_MAIN",
        "UI_OTHER_PLAYER_TIPS", "UI_CAVE_CENTER_TIPS", "UI_LIFESKILL_PROMOTION", "UI_LIFE_PROMOTION_SUCCESS",
        "UI_LIFE_ENCHANT_SUCCESS",
        // Opened by the game on its own (team recommendations); closes with Esc (live run 2026-10-14).
        "UI_TEAM_REOCMMEND_VIEW",
        // Ability phase announcement (no user data); one Esc closes only it (live run 2026-10-14).
        "UI_LIFE_STAGE_PREVIEW",
        // Homestead farm-plot menu and plant info card: a press on the Make view can also reach a plot behind it when
        // the character stands at a field (live 2026-10-15); one Esc closes only it.
        "UI_HOME_PLANT_VIEW", "UI_HOME_BUILD_INFO",
        // Gathering Helper, its target picker, the picker's keypad and the gathering-duration card (live 2026-10-15): Esc
        // closes the view only, a queued gathering goes on; the helper reopens by itself when gathering completes, so a
        // reset must be able to close it instead of parking on it as a user window.
        "UI_LIFESKILL_AUTO_COLLECT", "UI_LIFESKILL_AUTO_COLLECT_SET", "UI_CALCULATOR", "UI_COLLECT_TIME_VIEW",
        // "Improved": a dish's appraisal level went up during a craft ("Chance of producing 2 dishes increased to 25%",
        // "Tap the blank space to close"; live 2026-10-16 it ended a batch and parked the next round as a user window)
        "UI_LIFE_FOOD_APPRAISE_UP",
    ];

    /// <summary>
    /// Shown within 1 s of our DOWN = the press went into the world (a target): WORLD_PRESS, HALT (PRESS-15). Not
    /// UI_NPC_TALK (2026-10-21): it is the speech bubble nearby NPCs show by themselves (405 shows in one day's log); a
    /// drag on the Gathering Helper's list (raycast 'Image_Bg1', a UI object) met one and latched a false HALT. A real
    /// press on an NPC opens an NPC window, which the unexpected-UI rules catch.
    /// </summary>
    public static readonly IReadOnlyList<string> WorldPressIndicators = ["UI_LOCK_TARGET", "UI_LOCK_ARROW"];

    /// <summary>Shown within <see cref="HardRules.CombatRecent"/>: clicks and commits wait (PRESS-15, CR-06).</summary>
    public static readonly IReadOnlyList<string> CombatIndicators = ["UI_BOSS_CHANT", "UI_BLOOD_BAR", "UI_LOCK_TARGET"];
}
