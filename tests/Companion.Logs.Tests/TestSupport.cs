using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Companion.Contracts;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;

namespace Companion.Logs.Tests;

/// <summary>Fake clock: time only moves when a test says so.</summary>
internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);

    public TimeSpan Monotonic { get; set; } = TimeSpan.FromHours(1);

    public void Sleep(TimeSpan duration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Advance(duration);
    }

    public void Advance(TimeSpan d)
    {
        Monotonic += d;
        UtcNow += d;
    }

    /// <summary>Sets wall time so that UtcNow.LocalDateTime equals <paramref name="local"/> (game timestamps are local).</summary>
    public void SetLocal(DateTime local) =>
        UtcNow = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToUniversalTime();
}

/// <summary>Deterministic salted hash (stand-in for the knowledge unit's hasher). Counts calls, keeps nothing.</summary>
internal sealed class FakeHasher : ICharacterKeyHasher
{
    public int Calls { get; private set; }

    public CharacterKey Hash(string rawRoleToken)
    {
        Calls++;
        return Of(rawRoleToken);
    }

    public static CharacterKey Of(string raw) =>
        new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("test-salt:" + raw)))[..16]);
}

/// <summary>
/// Byte-exact fixtures copied from the real game log (read-only, 2026-10-14). Only whole ENTRIES of these categories
/// were copied: ui_mgr UI lines, popup_seq real show, StageMgr focus, UpdateModule LostFocuse press blocks,
/// '[graphic]' resolution, OnScreenSizeChange, collect_mgr auto-collect status, lifeskill_cook_mgr is_block. No chat,
/// analytics, login, pay, team/vendor or player-name lines. Entries in between were skipped, so offsets differ.
/// <list type="bullet">
/// <item>real_c3_run3_bag.log: log.txt 6745275..6772000 (c3 run 3, 2026-10-13 23:03): show_ui UI_BAG_MAIN, press block Label_Text / Canvas_Bag at (1748,694), block BEFORE the LostFocus line.</item>
/// <item>real_c3_run1_homestead.log: log.txt 5747696..5768500 (c3 run 1, 22:59): UI_HOME_PET_VIEW, block AFTER LostFocus, a bare 'UpdateModule LostFocuse ', OnScreenSizeChange + Device/Current resolution, a foreign UI_MAIN_GACHA show.</item>
/// <item>real_craft_session.log: log.txt 5331456..5402480 (22:51): 14 is_block lines, station UI_LIFE_ALCHEMIST_MAKE_POT show and close.</item>
/// <item>real_ui_verbs.log: single entries of every ui_mgr verb (nil and numeric ids), popup real show, the ui_mgr guide ban (log.bak1), cinema/scenario closes by dispose, non-verb ui_mgr lines.</item>
/// <item>real_bootstrap_lines.log: auto-collect status with its traceback block, Current and Device resolution (with and without 'file.lua:N'), OnScreenSizeChange.</item>
/// </list>
/// </summary>
internal static class Fixtures
{
    public const string C3Run3 = "real_c3_run3_bag.log";
    public const string C3Run1 = "real_c3_run1_homestead.log";
    public const string Craft = "real_craft_session.log";
    public const string UiVerbs = "real_ui_verbs.log";
    public const string BootstrapLines = "real_bootstrap_lines.log";

    public static readonly string[] All = [C3Run3, C3Run1, Craft, UiVerbs, BootstrapLines];

    public static byte[] Load(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

/// <summary>Synthetic entries with the real byte shape (CRLF, TAB after timestamp and level, 4-space separators).</summary>
internal static class Synthetic
{
    public static readonly DateTime T0 = new(2026, 10, 14, 1, 0, 0, 0);

    public static string Ts(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>One entry: the timestamped line, optional '|' lines (closed by an empty '|' line), the blank terminator.</summary>
    public static string Entry(DateTime t, string message, params string[] pipeLines)
    {
        var sb = new StringBuilder();
        sb.Append(Ts(t)).Append("\tLog\t").Append(message).Append("\r\n");
        if (pipeLines.Length > 0)
        {
            foreach (var p in pipeLines)
            {
                sb.Append("|\t").Append(p).Append("\r\n");
            }

            sb.Append("|\t\r\n");
        }

        sb.Append("\r\n");
        return sb.ToString();
    }

    public static string Header(DateTime t) => Entry(t, "--->>> add to unity log callback",
        "UnityEngine.Logger:Log(LogType, Object)", "lss.lib.Log:CreateLogFile()", "lss.lib.Log:.cctor()", "lss.ErrorHandler:Awake()");

    public static string Ui(string verb, string value, int line) => $"[LOG] -> module/foundation/ui/ui_mgr.lua:{line}    {verb}    {value}";

    public static string Show(string name) => Ui("<color=yellow>show_ui</color>", name, 1077);

    public static string OpenConst(string name) => Ui("open const ui", name, 1715);

    public static string HideTop(string? name) => Ui("[ui mgr]hide top ui", name ?? "nil", 1688);

    public static string AddToClose(string name) => Ui("[ui_mgr]add to close_list", name, 1724);

    public static string StackOver(string name) => Ui("stack over -> remove", name, 547);

    public static string DisposeNow(string name) => Ui("[ui_mgr]dispose_ui immediately", name, 582);

    public static string DisposeAll(string name) => Ui("[ui_mgr] dispose_all_close", name, 1777);

    public static string CheckClean(string name) => Ui("[ui_mgr]check_clean_unused - do dispose", name, 1801);

    public static string ToHide(string name) => Ui("【to_hide】 hide_ui", name, 556);

    public static string Scene(string phaseText, string file = "cave_helper.lua", int line = 258) =>
        $"[LOG] -> cave/{file}:{line}    [scene_load] {phaseText}";

    public const string StartLoad = "[LOG] -> cave/cave_helper.lua:258    [scene_load] load_scene start load     town    1791925793    9557.6220703125";
    public const string BeforeLoadingEmpty = "[LOG] -> cave/cave_helper.lua:232    [scene_load] before loading_empty    town    nil    town    nil    9595.81";
    public const string EndLoadingEmpty = "[LOG] -> cave/cave_helper.lua:251    [scene_load] load_scene end loading_empty    town    1791925793    9557.6220703125";
    public const string EndLoad = "[LOG] -> cave/cave_helper.lua:262    [scene_load] load_scene end load     town    1791925794    9558.33203125";
    public const string NeedWait = "[LOG] -> cave/cave_helper.lua:294    [scene_load] time sample load scene need wait    town";
    public const string RoleReady = "[LOG] -> cave/cave_cli.lua:1106    [scene_load] on_cave_role_ready";
    public const string HideProgressNow = "[LOG] -> cave/cave_cli.lua:1112    [scene_load] hide_progress_now";
    public const string HideProgressReal = "[LOG] -> cave/cave_helper.lua:467    [scene_load]load_scene hide_progress real    1791925795    9559.1    1000";
    public const string AbortHiding = "[LOG] -> cave/cave_helper.lua:543    [scene_load] abort hiding progress because locks    today_first_into_town";

    public const string FocusRecover = "[StageMgr] OnApplicationFocus RecoverFocus";
    public const string FocusLost = "[StageMgr] OnApplicationFocus LostFocus";
    public static readonly string[] FocusPipes = ["UnityEngine.Logger:Log(LogType, Object)", "lss.StageMgr:OnApplicationFocus(Boolean)"];

    public const string Quit = "[StageMgr] OnApplicationQuit";
    public const string Logout = "[LOG] -> vendor/ema/ema_vendor.lua:261    [emasdk]start logout";
    public const string Ping = "[LOG] -> vendor/ema/ema_vendor.lua:838    [ema] dc_ping log trigger";
    public const string Jank = "[LOG] -> lss_start.lua:381    [jank_detector] jank over :    1.5071393";
    public const string JankBare = "[jank_detector] jank over :    0.75";
    public const string Throttle = "[LOG] -> module/foundation/base_network.lua:155    request too frequency :    town.task_info";
    public const string ThrottleSpecForm = "[LOG] -> [base_network.lua] request too frequency";
    public const string PayForbidden = "[LOG] -> module/shop/pay_mgr.lua:321    [pay mgr]get_info";
    public const string PayLoginReady = "[LOG] -> module/shop/pay_mgr.lua:88    [pay mgr]on_cave_role_ready";
    public const string PayLoginShow = "[LOG] -> module/shop/pay_mgr.lua:412    [pay mgr]req get pay show";
    public const string AutoEatReq = "[LOG] -> module/drug/drug_mgr.lua:575    [lifeskill][autoeat] drug_mgr supply.eat_food req    7654321";
    public const string AutoEatResp = "[LOG] -> module/drug/drug_network.lua:73    [lifeskill][autoeat] drug_mgr supply.eat_food resp    7654321";
    public const string GuideMaskBan = "[LOG] -> module/guide/guide_mgr.lua:1514    guild mgr is ban by guide mask by guide_key:    test_guide_key    11111";
    public const string CollectStatus0 = "[LOG] -> module/lifeskill/lifeskill_auto_collect_mgr.lua:210    [collect_mgr] set_auto_collect_status    0    stack traceback:";
    public const string CurrentRes = "[LOG] -> graphic_cfg.lua:284    [graphic] Current screen resolution    1920    1009";
    public const string IsBlock = "[LOG] -> module/lifeskill/cook/lifeskill_cook_mgr.lua:907    is_block     1.5    -2.25";

    /// <summary>
    /// The live item-tip line (2026-10-15, byte-exact apart from the CRLF terminators): the product icon on the Make tab
    /// opened UI_ITEM_TIPS_PASS_TOUCH. The uuid is empty, so 8 spaces separate the name and the id.
    /// </summary>
    public const string LiveItemTipLine =
        "2026-10-15 01:44:58.553\tLog\t[LOG] -> module/item/item_tips_mgr.lua:200    Cat:item_tips_mgr print name, uuid, id :     White Toast        1051000";

    /// <summary>The live Kitty Tea House board press (2026-10-29 10:22:23, byte-exact apart from the terminator): row 3, column 4, a Cookie.</summary>
    public const string LiveGridClickLine =
        "2026-10-29 10:22:23.389\tLog\t[LOG] -> module/triple_town/chess_play_widget.lua:409    on_grid_click    3    4    1    ";

    /// <summary>A board-press message (the part after 'ts\tLevel\t').</summary>
    public static string GridClick(int row, int column, int piece) =>
        $"[LOG] -> module/triple_town/chess_play_widget.lua:409    on_grid_click    {row}    {column}    {piece}";

    /// <summary>An item-tip message (the part after 'ts\tLevel\t'), as the live material tip ('Water', 20330037) logged it.</summary>
    public static string ItemTip(string name, string tail) =>
        $"[LOG] -> module/item/item_tips_mgr.lua:200    Cat:item_tips_mgr print name, uuid, id :     {name}    {tail}";

    /// <summary>Made-up login token: the test asserts it never appears in any output.</summary>
    public const string FakeRoleToken = "98765-432109";

    public static string Login(string token = FakeRoleToken) =>
        $"[LOG] -> module/create_role/view/select_role_view.lua:522    [login] req_login_role    {token}";

    public static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
}

/// <summary>Parses bytes with the real scanner and parser, without any file.</summary>
internal static class Parsing
{
    public static readonly LogFileIdentity FixtureFile = new(new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc), 42, "fixture");

    public static List<LogEvent> All(byte[] bytes, ICharacterKeyHasher? hasher = null) => All(bytes, 0, bytes.Length, hasher);

    public static List<LogEvent> All(byte[] bytes, int start, int end, ICharacterKeyHasher? hasher = null)
    {
        var slice = bytes[start..end];
        var scan = EntryScanner.Scan(slice, slice.Length, start, capped: false);
        var parser = new LogEntryParser(hasher ?? new FakeHasher());
        return scan.Entries.Select(e => parser.Parse(e, FixtureFile)).OfType<LogEvent>().ToList();
    }

    public static UiModel Model(IEnumerable<LogEvent> events, IUiClassifier? classifier = null)
    {
        var model = new UiModel(classifier ?? new UiClassifier());
        foreach (var e in events)
        {
            model.Apply(e);
        }

        return model;
    }
}

/// <summary>A temporary log file under this test's output folder (never anywhere else).</summary>
internal sealed class TempLog : IDisposable
{
    public TempLog()
    {
        Dir = Path.Combine(AppContext.BaseDirectory, "tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        LogPath = Path.Combine(Dir, "log.txt");
        Paths = new CompanionPaths(Dir, Dir, Dir, Dir, Dir, Dir, LogPath, Path.Combine(Dir, "TOS.exe"));
    }

    public string Dir { get; }

    public string LogPath { get; }

    public CompanionPaths Paths { get; }

    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    public void Write(byte[] bytes)
    {
        using var fs = new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.Write(bytes);
    }

    public void Append(string text) => Append(Encoding.UTF8.GetBytes(text));

    public void Append(byte[] bytes) => Append(bytes, 0, bytes.Length);

    public void Append(byte[] bytes, int offset, int count)
    {
        using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.Write(bytes, offset, count);
    }

    public void Truncate(long length)
    {
        using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.SetLength(length);
    }

    /// <summary>What the game does at start-up: log.txt becomes log.bak1 (or <paramref name="name"/>; mid-session: log.bak).</summary>
    public string RotateAway(string name = "log.bak1")
    {
        var bak = Path.Combine(Dir, name);
        File.Move(LogPath, bak, overwrite: true);
        return bak;
    }

    public LogTracker Tracker(FakeClock clock, UiClassifier? classifier = null, FakeHasher? hasher = null, LogTrackerOptions? options = null) =>
        new(Paths, clock, hasher ?? new FakeHasher(), classifier ?? new UiClassifier(), new LogPrivacyFilter(), options ?? LogTrackerOptions.Default);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
