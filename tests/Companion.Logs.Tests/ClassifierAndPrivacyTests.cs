using System.Text;
using Companion.Contracts.Safety;
using static Companion.Logs.Tests.Synthetic;

namespace Companion.Logs.Tests;

/// <summary>UI classifier precedence (SPEC 6.2).</summary>
[TestClass]
public sealed class ClassifierTests
{
    [TestMethod]
    [DataRow("UI_RECHARGE", UiKind.Forbidden)]
    [DataRow("UI_SHOP_ANYTHING", UiKind.Forbidden)]
    [DataRow("UI_TIPS2_EXCHANGE", UiKind.Forbidden)]
    [DataRow("UI_TIPS2_ANY", UiKind.Forbidden)]
    [DataRow("UI_MAIN_GACHA", UiKind.Forbidden)]
    [DataRow("LIFE_CAREER_HELP_CONFIRM", UiKind.Forbidden)]
    [DataRow("UI_SOMETHING_PURCHASE_VIEW", UiKind.Forbidden)]
    [DataRow("UI_SINGLE_SHOP", UiKind.Forbidden)] // the Kitty Tea House shop (2026-10-30 log): no press while it is open
    [DataRow("UI_ACTIVITY_CENTER", UiKind.Stack)] // What's On, the Tea House board's window
    [DataRow("UI_TRIPLE_TOWN_SETTLE", UiKind.Stack)] // the Tea House round's end screen
    [DataRow("UI_TEAM_3V3_INVITE", UiKind.Social)]
    [DataRow("UI_TEAM_VOTE", UiKind.Social)]
    [DataRow("UI_TEAM_3V3_MAIN", UiKind.Busy)]
    [DataRow("UI_LOAD_PROGRESS", UiKind.Busy)]
    [DataRow("UI_CINEMA_BLACK_VIEW", UiKind.Busy)]
    [DataRow("UI_SCENARIO_BG", UiKind.Busy)]
    [DataRow("UI_LIFE_ALCHEMIST_MAKE_POT", UiKind.Station)]
    [DataRow("UI_BAG_MAIN", UiKind.Stack)]
    [DataRow("GUILD_WAR_GVE_BATTLE_RESULT_SUCC", UiKind.Stack)]
    [DataRow("UI_CHANT", UiKind.Overlay)]
    [DataRow("UI_TIPS2", UiKind.Overlay)]
    [DataRow("UI_TIPS2_OVERLAY", UiKind.Overlay)]
    [DataRow("UI_HOME_TOWN_INFO", UiKind.Overlay)] // the town banner, open for the whole stay in town // a self-closing tip that never logs its close (live 2026-10-15)
    [DataRow("MAIN_TARGET_BUTTON", UiKind.Overlay)]
    [DataRow("UI_NEVER_SEEN", UiKind.Unknown)]
    [DataRow("BAG_MAIN", UiKind.Unknown)]
    public void Precedence(string name, UiKind expected) => Assert.AreEqual(expected, new UiClassifier().Classify(name));

    [TestMethod]
    public void LearnedStack_OverridesOverlay_ButNeverForbiddenOrBusy()
    {
        var c = new UiClassifier();
        c.LearnStack("UI_CHANT");
        c.LearnStack("UI_RECHARGE");
        c.LearnStack("UI_LOAD_PROGRESS");
        c.LearnStack("UI_FRESH");
        Assert.AreEqual(UiKind.Stack, c.Classify("UI_CHANT"));
        Assert.AreEqual(UiKind.Forbidden, c.Classify("UI_RECHARGE"));
        Assert.AreEqual(UiKind.Busy, c.Classify("UI_LOAD_PROGRESS"));
        Assert.AreEqual(UiKind.Stack, c.Classify("UI_FRESH"));
        Assert.IsTrue(c.IsLearnedStack("UI_FRESH"));
        Assert.IsFalse(c.IsLearnedStack("UI_BAG_MAIN"), "a seed is not a learned name");
        Assert.IsFalse(new UiClassifier().IsLearnedStack("UI_FRESH"), "learning is per instance");

        // an Esc while the short tip is shown logs 'hide top ui UI_TIPS2': it stays an overlay (2026-10-18)
        c.LearnStack("UI_TIPS2");
        c.LearnStack("UI_TIPS2_OVERLAY");
        Assert.AreEqual((UiKind.Overlay, UiKind.Overlay), (c.Classify("UI_TIPS2"), c.Classify("UI_TIPS2_OVERLAY")));
        Assert.IsFalse(c.IsLearnedStack("UI_TIPS2"));
    }

    [TestMethod]
    public void EscSafe_IsTheCatalogList()
    {
        var c = new UiClassifier();
        foreach (var n in UiCatalog.EscSafe)
        {
            Assert.IsTrue(c.IsEscSafe(n), n);
        }

        Assert.IsFalse(c.IsEscSafe("UI_MAIL"));
        Assert.IsFalse(c.IsEscSafe("UI_RECHARGE"));
    }
}

/// <summary>Privacy filter (SPEC 12): allow-list only, zero hits on a synthetic sensitive fixture, role-id scrub.</summary>
[TestClass]
public sealed class PrivacyFilterTests
{
    private static readonly LogPrivacyFilter Filter = new();

    private static IEnumerable<string> Lines(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);

    [TestMethod]
    public void RealFixtures_AllowedCategoriesPass_OtherLinesDrop()
    {
        var kept = new List<string>();
        var dropped = new List<string>();
        foreach (var name in Fixtures.All)
        {
            foreach (var line in Lines(Fixtures.Load(name)))
            {
                (Filter.IsAllowed(line) ? kept : dropped).Add(line);
            }
        }

        // Every ui_mgr verb, focus, press, resolution, collect and is_block first line is kept.
        Assert.IsTrue(kept.Any(l => l.Contains("show_ui</color>    UI_BAG_MAIN", StringComparison.Ordinal)));
        Assert.IsTrue(kept.Any(l => l.Contains("hide top ui    nil", StringComparison.Ordinal)));
        Assert.IsTrue(kept.Any(l => l.Contains("【to_hide】", StringComparison.Ordinal)));
        Assert.IsTrue(kept.Any(l => l.Contains("ui_mgr:true_show_ui ban by guide", StringComparison.Ordinal)));
        Assert.IsTrue(kept.Any(l => l.EndsWith("OnApplicationFocus RecoverFocus", StringComparison.Ordinal)));
        Assert.IsTrue(kept.Any(l => l.EndsWith("UpdateModule LostFocuse ", StringComparison.Ordinal)));
        Assert.IsTrue(kept.Any(l => l.Contains("UpdateModule LostFocuse Name: Label_Text", StringComparison.Ordinal)));
        Assert.IsTrue(kept.Contains("|\tmodule: Name: Canvas_Bag (UnityEngine.GameObject)"));
        Assert.IsTrue(kept.Contains("|\tscreenPosition: (1748.00, 694.00)"));
        Assert.IsTrue(kept.Any(l => l.Contains("[graphic] Device resolution    1920    1009", StringComparison.Ordinal)));
        Assert.IsTrue(kept.Any(l => l.Contains("set_auto_collect_status    2    stack traceback:", StringComparison.Ordinal)));
        Assert.AreEqual(14, kept.Count(l => l.Contains("is_block", StringComparison.Ordinal)));

        // Dropped: stack traces, other RaycastResult fields, popup_seq, non-verb ui_mgr lines, OnScreenSizeChange.
        Assert.IsTrue(dropped.Any(l => l.StartsWith("|\tUnityEngine.Logger", StringComparison.Ordinal)));
        Assert.IsTrue(dropped.Contains("|\teventCamera: UICamera (UnityEngine.Camera)"));
        Assert.IsTrue(dropped.Any(l => l.StartsWith("|\t\tLuaScript/", StringComparison.Ordinal)));
        Assert.IsTrue(dropped.Any(l => l.Contains("popup_seq_mgr", StringComparison.Ordinal)));
        Assert.IsTrue(dropped.Any(l => l.Contains("load cache ui", StringComparison.Ordinal)));
        Assert.IsTrue(dropped.Any(l => l.Contains("OnScreenSizeChange", StringComparison.Ordinal)));
        Assert.IsTrue(kept.All(l => Filter.Filter(l) == l), "real kept lines carry nothing to scrub");
    }

    [TestMethod]
    public void SyntheticSensitiveFixture_ZeroHits()
    {
        // Made-up chat, analytics, login/role dumps, team/vendor and mount lines (no real data).
        var text = string.Concat(
            Entry(T0, "[LOG] -> module/chat/chat_mgr.lua:300    recv msg    FakePlayer: meet at town 12345-678901"),
            Entry(T0, "[LOG] -> module/chat/chat_util.lua:88    parse    <color=yellow>show_ui</color>    UI_BAG_MAIN"),
            Entry(T0, "[LOG] -> vendor/ejoy/chat_helper.lua:41    send    hello [StageMgr] OnApplicationFocus RecoverFocus"),
            Entry(T0, "[LOG] -> vendor/ema/ema_vendor.lua:954    baLogEvent upload:     login    account=0f0e0d0c0b0a    role_id=12345-678901    server=9001    device=FakeDevice"),
            Entry(T0, Login()),
            Entry(T0, $"[LOG] -> login_mgr.lua:228    Cat:login_mgr.lua[req_login_role] role, server    {FakeRoleToken}    extra"),
            Entry(T0, "[LOG] -> module/create_role/role_mgr.lua:10    resp client.load_role    {name=FakeName, guild_name=FakeGuild}"),
            Entry(T0, "[LOG] -> module/team/team_mgr.lua:77    member    12345-678901    FakeMate"),
            Entry(T0, "[LOG] -> module/vendor/vendor_mgr.lua:5    vendor player    54321-123456"),
            Entry(T0, "[LOG] -> module/mount/mount_mgr.lua:9    up_mount    FakeMount"),
            Entry(T0, PayForbidden),
            Entry(T0, Logout),
            Entry(T0, Throttle),
            Entry(T0, AutoEatReq),
            "    {name = 'FakeName', role = '12345-678901'}\r\n",
            "role_id: 12345-678901\r\n",
            "|\tFakePlayer said something\r\n",
            "|\tmodule: Name: FakePlayer said (UnityEngine.GameObject) and more text\r\n",
            Entry(T0, "[LOG] -> module/foundation/ui/ui_mgr.lua:1077    <color=yellow>show_ui</color>    UI_BAG_MAIN    FakePlayer"),
            Entry(T0, "[LOG] -> lss_start.lua:381    [jank_detector] jank over :    1.5    FakePlayer"),
            Entry(T0, "[LOG] -> module/lifeskill/cook/lifeskill_cook_mgr.lua:907    is_block     1.0    2.0    FakePlayer"));
        var hits = Lines(Bytes(text)).Where(Filter.IsAllowed).ToList();
        Assert.HasCount(0, hits, string.Join(" | ", hits));
        Assert.IsTrue(Lines(Bytes(text)).All(l => Filter.Filter(l) is null));
    }

    [TestMethod]
    public void GridClickLines_AreKept_LookAlikesDrop()
    {
        // a Kitty Tea House square and a piece number are game data (2026-10-30)
        Assert.IsTrue(Filter.IsAllowed(LiveGridClickLine));
        Assert.AreEqual(LiveGridClickLine, Filter.Filter(LiveGridClickLine + "\n"));
        Assert.IsFalse(Filter.IsAllowed(LiveGridClickLine + "FakePlayer"));
        Assert.IsFalse(Filter.IsAllowed($"{Ts(T0)}\tLog\t[LOG] -> module/chat/chat_mgr.lua:300    recv    on_grid_click    3    4    1"));
    }

    [TestMethod]
    public void ItemTipLines_AreKeptExactly_LookAlikesDrop()
    {
        // item names and ids are game data, not personal data (live 2026-10-15)
        Assert.IsTrue(Filter.IsAllowed(LiveItemTipLine));
        Assert.AreEqual(LiveItemTipLine, Filter.Filter(LiveItemTipLine + "\r\n"));
        var material = $"{Ts(T0)}\tLog\t{ItemTip("Water", "    20330037")}";
        Assert.AreEqual(material, Filter.Filter(material));
        Assert.IsTrue(Filter.IsAllowed($"{Ts(T0)}\tLog\t{ItemTip("White Toast", "7f3a-19bc    1051000")}"));

        string[] dropped =
        [
            LiveItemTipLine + "    FakePlayer said hi", // anything after the id
            LiveItemTipLine[..LiveItemTipLine.LastIndexOf(' ')], // no id
            $"{Ts(T0)}\tLog\t{ItemTip(new string('x', 120), "    1051000")}", // longer than any item name
            $"{Ts(T0)}\tLog\t[LOG] -> module/chat/chat_mgr.lua:300    recv    Cat:item_tips_mgr print name, uuid, id :     White Toast        1051000",
            $"{Ts(T0)}\tLog\t[LOG] -> module/item/item_tips_mgr.lua:88    Cat:item_tips_mgr show tips    FakePlayer    1051000",
            "Cat:item_tips_mgr print name, uuid, id :     White Toast        1051000",
        ];
        foreach (var line in dropped)
        {
            Assert.IsFalse(Filter.IsAllowed(line), line);
        }
    }

    [TestMethod]
    public void RoleIdLikeTokens_AreScrubbedFromKeptLines()
    {
        var line = $"{Ts(T0)}\tLog\t[LOG] -> cave/cave_helper.lua:232    [scene_load] before loading_empty    12345-678901    9999-1234";
        Assert.IsTrue(Filter.IsAllowed(line));
        var kept = Filter.Filter(line + "\r\n");
        Assert.AreEqual($"{Ts(T0)}\tLog\t[LOG] -> cave/cave_helper.lua:232    [scene_load] before loading_empty    <id>    <id>", kept);
        Assert.IsNull(Filter.Filter("no timestamp at all"));
        Assert.IsFalse(Filter.IsAllowed(string.Empty));
    }
}
