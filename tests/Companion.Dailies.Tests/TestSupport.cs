using Companion.Contracts.Logs;

namespace Companion.Dailies.Tests;

/// <summary>The real data file (copied next to the tests).</summary>
internal static class Data
{
    public static DailyCatalog Catalog() =>
        DailyCatalog.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "dailies.json")));
}

/// <summary>Builds log events in file order (each one a later byte offset), as the parser would.</summary>
internal sealed class Log
{
    private static readonly LogFileIdentity File = new(new DateTime(2026, 11, 3, 14, 21, 0, DateTimeKind.Utc), 7, "test");
    private long _offset;

    private (LogPosition, long) Next()
    {
        _offset += 100;
        return (new LogPosition(File, _offset + 99), _offset);
    }

    public LogEvent Show(DateTime t, string ui)
    {
        var (p, s) = Next();
        return new UiLogEvent(p, s, t, UiVerb.Show, ui, null);
    }

    public LogEvent Close(DateTime t, string ui)
    {
        var (p, s) = Next();
        return new UiLogEvent(p, s, t, UiVerb.AddToCloseList, ui, null);
    }

    public LogEvent Dispose(DateTime t, string ui)
    {
        var (p, s) = Next();
        return new UiLogEvent(p, s, t, UiVerb.DisposeImmediately, ui, null);
    }

    public LogEvent Popup(DateTime t)
    {
        var (p, s) = Next();
        return new UiLogEvent(p, s, t, UiVerb.PopupRealShow, "UI_ITEM_REWARD", null);
    }

    public LogEvent Throttle(DateTime t, string request)
    {
        var (p, s) = Next();
        return new ThrottleLogEvent(p, s, t) { Request = request };
    }

    public LogEvent Map(DateTime t, string map)
    {
        var (p, s) = Next();
        return new MapEnteredLogEvent(p, s, t, map);
    }

    public LogEvent Commit(DateTime t, long task)
    {
        var (p, s) = Next();
        return new QuestLogEvent(p, s, t, 0, 0, task, true);
    }

    public LogEvent Reply(DateTime t, string request, int code)
    {
        var (p, s) = Next();
        return new ServerReplyLogEvent(p, s, t, request, code);
    }

    public LogEvent Push(DateTime t, string module, string name, bool? win = null)
    {
        var (p, s) = Next();
        return new ServerPushLogEvent(p, s, t, module, name, win);
    }

    public LogEvent Switch(DateTime t, string instance, bool open)
    {
        var (p, s) = Next();
        return new FeatureSwitchLogEvent(p, s, t, 1, instance, open);
    }

    public LogEvent Settle(DateTime t) => Show(t, "UI_COMMON_SETTLE_VIEW");

    public LogEvent Toast(DateTime t) => Show(t, "UI_ITEM_GET_TIP");

    public LogEvent Guide(DateTime t, string key)
    {
        var (p, s) = Next();
        return new GuideKeyLogEvent(p, s, t, key);
    }

    public LogEvent ShopRefresh(DateTime t, int shop)
    {
        var (p, s) = Next();
        return new ShopRefreshLogEvent(p, s, t, shop);
    }

    public LogEvent Slot(DateTime t, int slot)
    {
        var (p, s) = Next();
        return new HelperCollectLogEvent(p, s, t, slot);
    }

    public LogEvent RoomFinish(DateTime t, double seconds)
    {
        var (p, s) = Next();
        return new RoomFinishLogEvent(p, s, t, seconds);
    }

    public LogEvent ItemTip(DateTime t, long item)
    {
        var (p, s) = Next();
        return new ItemTipLogEvent(p, s, t, "Loot", item);
    }

    public LogEvent Reset(DateTime t, ResetKind kind = ResetKind.DailyUpdate)
    {
        var (p, s) = Next();
        return new ResetLogEvent(p, s, t, kind);
    }
}
