using System.Globalization;
using System.Text.RegularExpressions;
using Companion.Contracts;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;

namespace Companion.Logs;

/// <summary>
/// Turns a complete <see cref="RawEntry"/> into at most one <see cref="LogEvent"/> (SPEC 6.1). Everything that matches
/// no pattern is dropped and never buffered. The login role token is hashed with <see cref="ICharacterKeyHasher"/>
/// immediately and the raw token is never stored or reported.
/// </summary>
internal sealed class LogEntryParser
{
    private static readonly byte[] LogPrefix = "[LOG] -> "u8.ToArray();
    private static readonly byte[] StageMgrPrefix = "[StageMgr] "u8.ToArray();
    private static readonly byte[] PressPrefix = "UpdateModule LostFocuse"u8.ToArray();
    private static readonly byte[] CallbackPrefix = "--->>> add to unity log callback"u8.ToArray();
    private static readonly byte[] JankToken = "[jank_detector]"u8.ToArray();
    private static readonly byte[] NotifyToken = "Unity Application Notify"u8.ToArray();
    private const string ItemTipsPrefix = "[LOG] -> module/item/item_tips_mgr.lua:";

    private readonly ICharacterKeyHasher _hasher;

    internal LogEntryParser(ICharacterKeyHasher hasher) => _hasher = hasher;

    /// <summary>Parses one entry of generation <paramref name="file"/>; null when the entry is not an event we keep.</summary>
    internal LogEvent? Parse(RawEntry entry, LogFileIdentity file)
    {
        if (entry.Timestamp is not { } ts)
        {
            return null;
        }

        var pos = new LogPosition(file, entry.End);
        var start = entry.Start;

        if (entry.MessageStartsWith(LogPrefix))
        {
            return ParseLogLine(entry.Message, pos, start, ts);
        }

        if (entry.MessageStartsWith(StageMgrPrefix))
        {
            var msg = entry.Message;
            var f = LogPatterns.Focus().Match(msg);
            if (f.Success)
            {
                return new FocusLogEvent(pos, start, ts, f.Groups[1].ValueSpan.SequenceEqual("RecoverFocus"));
            }

            return LogPatterns.SessionQuit().IsMatch(msg) ? new SessionLogEvent(pos, start, ts, SessionMarker.ApplicationQuit) : null;
        }

        if (entry.MessageStartsWith(PressPrefix))
        {
            return ParsePress(entry, pos, start, ts);
        }

        if (entry.MessageStartsWith(CallbackPrefix))
        {
            return LogPatterns.SessionLogCallback().IsMatch(entry.Message)
                ? new SessionLogEvent(pos, start, ts, SessionMarker.LogCallbackAdded)
                : null;
        }

        if (entry.MessageStartsWith(JankToken))
        {
            return ParseJank(entry.Message, pos, start, ts);
        }

        if (entry.MessageStartsWith(NotifyToken))
        {
            return LogPatterns.ScreenSizeChange().IsMatch(entry.Message)
                ? new SessionLogEvent(pos, start, ts, SessionMarker.ScreenSizeChange)
                : null;
        }

        return null;
    }

    private LogEvent? ParseLogLine(string msg, LogPosition pos, long start, DateTime ts)
    {
        // First: an item name is free game text and may contain any other branch's token.
        if (msg.StartsWith(ItemTipsPrefix, StringComparison.Ordinal))
        {
            var t = LogPatterns.ItemTip().Match(msg);
            return t.Success && long.TryParse(t.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? new ItemTipLogEvent(pos, start, ts, t.Groups[1].Value, id)
                : null;
        }

        if (msg.Contains("ui_mgr.lua:", StringComparison.Ordinal))
        {
            var m = LogPatterns.UiVerb().Match(msg);
            if (m.Success)
            {
                return ParseUi(m, pos, start, ts);
            }

            var g = LogPatterns.GuideBan().Match(msg);
            return g.Success ? new GuideBanLogEvent(pos, start, ts, g.Groups[1].Success) : null;
        }

        if (msg.Contains("guide_mgr.lua:", StringComparison.Ordinal))
        {
            if (msg.Contains("[finish_guide] guide.key", StringComparison.Ordinal))
            {
                var k = LogPatterns.GuideKey().Match(msg);
                return k.Success ? new GuideKeyLogEvent(pos, start, ts, k.Groups[1].Value) : null;
            }

            var g = LogPatterns.GuideBan().Match(msg);
            return g.Success ? new GuideBanLogEvent(pos, start, ts, g.Groups[1].Success) : null;
        }

        if (msg.Contains("popup_seq_mgr.lua:", StringComparison.Ordinal))
        {
            var p = LogPatterns.PopupRealShow().Match(msg);
            return p.Success ? new UiLogEvent(pos, start, ts, UiVerb.PopupRealShow, p.Groups[1].Value, null) : null;
        }

        if (msg.Contains("[graphic] ", StringComparison.Ordinal))
        {
            var r = LogPatterns.Resolution().Match(msg);
            if (r.Success
                && int.TryParse(r.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var w)
                && int.TryParse(r.Groups[3].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var h))
            {
                var source = r.Groups[1].ValueSpan.SequenceEqual("Current screen resolution") ? ResolutionSource.CurrentScreen : ResolutionSource.Device;
                return new ResolutionLogEvent(pos, start, ts, source, new SizeI(w, h));
            }

            return null;
        }

        if (msg.Contains("OnScreenSizeChange", StringComparison.Ordinal))
        {
            return LogPatterns.ScreenSizeChange().IsMatch(msg) ? new SessionLogEvent(pos, start, ts, SessionMarker.ScreenSizeChange) : null;
        }

        if (msg.Contains("[collect_mgr]", StringComparison.Ordinal))
        {
            var c = LogPatterns.AutoCollect().Match(msg);
            return c.Success && int.TryParse(c.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var status)
                ? new AutoCollectLogEvent(pos, start, ts, status)
                : null;
        }

        if (msg.Contains("[scene_load]", StringComparison.Ordinal))
        {
            var s = LogPatterns.SceneLoad().Match(msg);
            return s.Success ? new SceneLoadLogEvent(pos, start, ts, PhaseOf(s.Groups[1].Value)) : null;
        }

        if (msg.Contains("lifeskill_cook_mgr.lua:", StringComparison.Ordinal))
        {
            var t = LogPatterns.CraftTick().Match(msg);
            return t.Success && TryDouble(t.Groups[1].Value, out var x) && TryDouble(t.Groups[2].Value, out var y)
                ? new CraftTickLogEvent(pos, start, ts, x, y)
                : null;
        }

        if (msg.Contains("[jank_detector]", StringComparison.Ordinal))
        {
            return ParseJank(msg, pos, start, ts);
        }

        if (msg.Contains("dc_ping log trigger", StringComparison.Ordinal))
        {
            return LogPatterns.SessionPing().IsMatch(msg) ? new SessionLogEvent(pos, start, ts, SessionMarker.PingTrigger) : null;
        }

        if (msg.Contains("[emasdk]start logout", StringComparison.Ordinal))
        {
            return LogPatterns.SessionLogout().IsMatch(msg) ? new SessionLogEvent(pos, start, ts, SessionMarker.LogoutStart) : null;
        }

        if (msg.Contains("req_login_role", StringComparison.Ordinal))
        {
            return ParseLogin(msg, pos, start, ts);
        }

        if (msg.Contains("request too frequency", StringComparison.Ordinal))
        {
            if (!LogPatterns.Throttle().IsMatch(msg))
            {
                return null;
            }

            var r = LogPatterns.ThrottleRequest().Match(msg);
            return new ThrottleLogEvent(pos, start, ts) { Request = r.Success ? r.Groups[1].Value : null };
        }

        if (msg.Contains("errcode from:", StringComparison.Ordinal))
        {
            var e = LogPatterns.ServerReply().Match(msg);
            return e.Success && int.TryParse(e.Groups[2].ValueSpan, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code)
                ? new ServerReplyLogEvent(pos, start, ts, e.Groups[1].Value, code)
                : null;
        }

        if (msg.Contains("[notify_change_feature] instance:", StringComparison.Ordinal))
        {
            var f = LogPatterns.FeatureSwitch().Match(msg);
            return f.Success && int.TryParse(f.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var gid)
                ? new FeatureSwitchLogEvent(pos, start, ts, gid, f.Groups[2].Value, f.Groups[3].ValueSpan[0] == '1')
                : null;
        }

        if (msg.Contains("[notify_", StringComparison.Ordinal))
        {
            var p = LogPatterns.ServerPush().Match(msg);
            if (!p.Success)
            {
                return null;
            }

            var w = LogPatterns.PushWin().Match(msg);
            return new ServerPushLogEvent(pos, start, ts, p.Groups[1].Value, p.Groups[2].Value, w.Success ? w.Groups[1].ValueSpan.SequenceEqual("true") : null);
        }

        if (msg.Contains("task_mgr.lua:", StringComparison.Ordinal))
        {
            var q = LogPatterns.QuestStep().Match(msg);
            if (q.Success)
            {
                return int.TryParse(q.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var chain)
                    && int.TryParse(q.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var step)
                    && long.TryParse(q.Groups[3].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var task)
                    ? new QuestLogEvent(pos, start, ts, chain, step, task, false)
                    : null;
            }

            var c = LogPatterns.QuestCommit().Match(msg);
            return c.Success && long.TryParse(c.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var done)
                ? new QuestLogEvent(pos, start, ts, 0, 0, done, true)
                : null;
        }

        if (msg.Contains("[shop] shop_refresh", StringComparison.Ordinal))
        {
            var r = LogPatterns.ShopRefresh().Match(msg);
            return r.Success && int.TryParse(r.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var shop)
                ? new ShopRefreshLogEvent(pos, start, ts, shop)
                : null;
        }

        if (msg.Contains("req_collect_housekeeper_products", StringComparison.Ordinal))
        {
            var h = LogPatterns.HelperCollect().Match(msg);
            return h.Success && int.TryParse(h.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var slot)
                ? new HelperCollectLogEvent(pos, start, ts, slot)
                : null;
        }

        if (msg.Contains("room_finish-", StringComparison.Ordinal))
        {
            var f = LogPatterns.RoomFinish().Match(msg);
            return f.Success && double.TryParse(f.Groups[1].ValueSpan, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
                ? new RoomFinishLogEvent(pos, start, ts, seconds)
                : null;
        }

        if (msg.Contains("[scene_lod] load_scene done", StringComparison.Ordinal))
        {
            var m = LogPatterns.MapEntered().Match(msg);
            return m.Success ? new MapEnteredLogEvent(pos, start, ts, m.Groups[1].Value) : null;
        }

        if (msg.Contains("unknown pack type!!!", StringComparison.Ordinal))
        {
            var r = LogPatterns.ResetRequest().Match(msg);
            return r.Success
                ? new ResetLogEvent(pos, start, ts, r.Groups[1].ValueSpan.SequenceEqual("on_new_day") ? ResetKind.NewDay : ResetKind.WeeklyUpdate)
                : null;
        }

        if (msg.Contains("server_push.daily_update", StringComparison.Ordinal))
        {
            return LogPatterns.DailyUpdate().IsMatch(msg) ? new ResetLogEvent(pos, start, ts, ResetKind.DailyUpdate) : null;
        }

        if (msg.Contains("pay_mgr.lua:", StringComparison.Ordinal))
        {
            var p = LogPatterns.Pay().Match(msg);
            if (!p.Success)
            {
                return null;
            }

            var rest = p.Groups[1].Value;
            var loginPair = rest.Contains("[pay mgr]on_cave_role_ready", StringComparison.Ordinal)
                || rest.Contains("req get pay show", StringComparison.Ordinal);
            return loginPair ? null : new PayLogEvent(pos, start, ts);
        }

        if (msg.Contains("[autoeat]", StringComparison.Ordinal))
        {
            return LogPatterns.AutoEat().IsMatch(msg) ? new AutoEatLogEvent(pos, start, ts) : null;
        }

        if (msg.Contains("on_grid_click", StringComparison.Ordinal))
        {
            var g = LogPatterns.GridClick().Match(msg);
            return g.Success
                ? new GridClickLogEvent(pos, start, ts, g.Groups[1].ValueSpan[0] - '0', g.Groups[2].ValueSpan[0] - '0', int.Parse(g.Groups[3].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture))
                : null;
        }

        if (msg.Contains("[chat_list]add wait show", StringComparison.Ordinal))
        {
            var c = LogPatterns.MessageArrived().Match(msg);
            return c.Success && int.TryParse(c.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var channel)
                ? new MessageArrivedLogEvent(pos, start, ts, channel)
                : null;
        }

        return null;
    }

    private static UiLogEvent? ParseUi(Match m, LogPosition pos, long start, DateTime ts)
    {
        var verb = VerbOf(m.Groups[1].Value);
        var value = m.Groups[2].Value;
        if (value == "nil")
        {
            return new UiLogEvent(pos, start, ts, verb, null, null);
        }

        if (value[0] is >= '0' and <= '9')
        {
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? new UiLogEvent(pos, start, ts, verb, null, id)
                : null;
        }

        return new UiLogEvent(pos, start, ts, verb, value, null);
    }

    private static UiVerb VerbOf(string text) => text switch
    {
        "<color=yellow>show_ui</color>" => UiVerb.Show,
        "open const ui" => UiVerb.OpenConst,
        "[ui mgr]hide top ui" => UiVerb.HideTop,
        "[ui_mgr]add to close_list" => UiVerb.AddToCloseList,
        "stack over -> remove" => UiVerb.StackOverRemove,
        "[ui_mgr]dispose_ui immediately" => UiVerb.DisposeImmediately,
        "[ui_mgr] dispose_all_close" => UiVerb.DisposeAllClose,
        "[ui_mgr]check_clean_unused - do dispose" => UiVerb.CheckCleanDispose,
        "【to_hide】 hide_ui" => UiVerb.ToHide,
        _ => UiVerb.CheckCanOpenFail, // 'check can open fail<digits>'
    };

    private static ScenePhase PhaseOf(string text) => text switch
    {
        "load_scene start load" or "before loading_empty" => ScenePhase.StartLoad,
        "load_scene end load" => ScenePhase.EndLoad,
        "time sample load scene need wait" => ScenePhase.NeedWait,
        "on_cave_role_ready" => ScenePhase.RoleReady,
        "hide_progress_now" => ScenePhase.HideProgressNow,
        "load_scene hide_progress real" => ScenePhase.HideProgressReal,
        _ => ScenePhase.AbortHidingProgress,
    };

    private static LogEvent? ParseJank(string msg, LogPosition pos, long start, DateTime ts)
    {
        var j = LogPatterns.Jank().Match(msg);
        return j.Success && TryDouble(j.Groups[1].Value, out var s) ? new JankLogEvent(pos, start, ts, s) : null;
    }

    private static PressBlockLogEvent? ParsePress(RawEntry entry, LogPosition pos, long start, DateTime ts)
    {
        var m = LogPatterns.PressBlock().Match(entry.Message);
        if (!m.Success)
        {
            return null;
        }

        var objectName = m.Groups[1].Success ? m.Groups[1].Value : string.Empty;
        string? module = null;
        PointD? screen = null;
        foreach (var line in entry.PipeLines())
        {
            if (module is null)
            {
                var mm = LogPatterns.PressModule().Match(line);
                if (mm.Success)
                {
                    module = mm.Groups[1].Value;
                    continue;
                }
            }

            if (screen is null)
            {
                var sp = LogPatterns.PressScreenPosition().Match(line);
                if (sp.Success && TryDouble(sp.Groups[1].Value, out var x) && TryDouble(sp.Groups[2].Value, out var y))
                {
                    screen = new PointD(x, y);
                }
            }
        }

        return new PressBlockLogEvent(pos, start, ts, objectName, module, screen);
    }

    private CharacterLoginLogEvent? ParseLogin(string msg, LogPosition pos, long start, DateTime ts)
    {
        var m = LogPatterns.CharacterLogin().Match(msg);
        if (!m.Success)
        {
            return null;
        }

        CharacterKey key;
        try
        {
            key = _hasher.Hash(m.Groups[1].Value);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The hasher failed (no salt yet?). Drop the event; the raw token never leaves this method.
            return null;
        }

        return new CharacterLoginLogEvent(pos, start, ts, key);
    }

    private static bool TryDouble(string s, out double value) =>
        double.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
}
