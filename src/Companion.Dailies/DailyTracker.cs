using Companion.Contracts.Logs;

namespace Companion.Dailies;

/// <summary>
/// The checklist's rule engine (DAILIES_MODULE.md). Fed game-log events in log order, finished companion jobs and the
/// user's ticks; answers with a <see cref="DailySnapshot"/> for any moment. Every count belongs to the day (period) of
/// its own timestamp, so a day that passes while the game is off simply shows empty: no reset step is needed.
/// Not thread-safe: <see cref="DailyService"/> serialises the calls. Keeps no log text, only counts and times.
/// </summary>
public sealed class DailyTracker
{
    private static readonly HashSet<UiVerb> CloseVerbs =
        [UiVerb.AddToCloseList, UiVerb.StackOverRemove, UiVerb.DisposeImmediately, UiVerb.CheckCleanDispose, UiVerb.DisposeAllClose];

    /// <summary>Section of the user's own items (ticked by hand).</summary>
    public const string OwnSection = "own";

    private readonly DailyCatalog _cat;
    private readonly List<DailyItemDef> _items;
    private readonly HashSet<string> _rewardScreens;
    private readonly HashSet<string> _passive;
    private readonly Dictionary<(string Item, DateOnly Day), Progress> _progress = [];
    private readonly Dictionary<string, (DateOnly Day, bool Done)> _overrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (bool Open, DateTime At)> _switches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _closed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _throttled = new(StringComparer.Ordinal);
    private readonly List<(DateTime At, DailyItemDef Item, DailyRule Rule)> _pending = [];
    private readonly DailyStacks _stacks;
    private readonly DailyProducers _producers;
    private readonly Dictionary<string, HashSet<string>> _seenDays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastHit = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Item, string Part), DateTime> _partAt = [];
    private readonly HashSet<string> _visitHits = new(StringComparer.Ordinal);
    private DateTime? _npcTalk;
    private (DateTime At, long Task)? _commit;
    private string? _map;

    /// <summary>The current map visit's room_finish seconds (null until the run ends).</summary>
    private double? _visitFinish;
    private DateTime? _progressAt;

    /// <summary>The item whose bag tip opened last, and when; and when the tip closed (openItem rules).</summary>
    private (long Item, DateTime At)? _tip;

    private DateTime? _tipClosed;

    private const string ItemTipScreen = "UI_ITEM_TIPS_PASS_TOUCH";
    private DateTime _lastTs = DateTime.MinValue;
    private long _lastOffset = -1;
    private DateTime _countFrom = DateTime.MinValue;

    /// <summary>Screen of the results after a run, a fight or a sweep.</summary>
    public const string SettleScreen = "UI_COMMON_SETTLE_VIEW";

    private const string ToastScreen = "UI_ITEM_GET_TIP";
    private const string ProgressScreen = "UI_NORMAL_PROGRESS";

    /// <summary>Creates an empty tracker for <paramref name="catalog"/>.</summary>
    public DailyTracker(DailyCatalog catalog)
    {
        _cat = catalog;
        _items = [.. catalog.Items];
        _rewardScreens = catalog.RewardScreens.ToHashSet(StringComparer.Ordinal);
        _passive = catalog.PassiveScreens.ToHashSet(StringComparer.Ordinal);
        _stacks = new DailyStacks(t => DayOf(DailyReset.Daily, t), catalog.Resets.Daily);
        _producers = new DailyProducers(catalog.Items.Where(i => i.Kind == DailyKind.Producer));
    }

    /// <summary>
    /// Changes with every state change (the UI repaints on a new value). <see cref="DailyService"/> starts a rebuilt
    /// tracker above the old one's value, so the number only grows.
    /// </summary>
    public long Version { get; internal set; }

    /// <summary>Newest log event fed (log time).</summary>
    public DateTime? LastEventAt => _lastTs == DateTime.MinValue ? null : _lastTs;

    /// <summary>The catalog.</summary>
    public DailyCatalog Catalog => _cat;

    /// <summary>
    /// Events at or before this moment only rebuild the context (open screens, map, last talk) and count nothing: they
    /// were counted before the companion restarted (the saved watermark).
    /// </summary>
    public DateTime CountFrom
    {
        get => _countFrom;
        set => _countFrom = value;
    }

    /// <summary>
    /// Feeds one game-log event. Events must come in log order; an event at or before the newest one already fed (same
    /// time and an earlier or equal offset) is a duplicate (replay overlapping the live feed) and is ignored.
    /// Returns true when a count changed.
    /// </summary>
    public bool Feed(LogEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Timestamp < _lastTs || (e.Timestamp == _lastTs && e.StartOffset <= _lastOffset))
        {
            return false;
        }

        _lastTs = e.Timestamp;
        _lastOffset = e.StartOffset;
        var before = Version;
        ExpirePending(e.Timestamp);
        switch (e)
        {
            case UiLogEvent u:
                OnUi(u);
                break;
            case ServerReplyLogEvent r:
                foreach (var (item, rule) in Rules("reply"))
                {
                    if (rule.Request == r.Request && (rule.Code is null || rule.Code == r.Code))
                    {
                        Hit(item, rule, r.Timestamp, DailySource.Game);
                    }
                }

                break;
            case GuideKeyLogEvent g:
                foreach (var (item, rule) in Rules("guide"))
                {
                    if (rule.Key == g.Key)
                    {
                        Hit(item, rule, g.Timestamp, DailySource.Game);
                    }
                }

                break;
            case ShopRefreshLogEvent s:
                foreach (var (item, rule) in Rules("shopRefresh"))
                {
                    if (rule.Shop == s.ShopId)
                    {
                        Hit(item, rule, s.Timestamp, DailySource.Game);
                    }
                }

                break;
            case RoomFinishLogEvent rf:
                _visitFinish = rf.Seconds;
                break;
            case ItemTipLogEvent it:
                _tip = (it.ItemId, it.Timestamp);
                break;
            case HelperCollectLogEvent h when Counts(h.Timestamp):
                _producers.OnSlot(h.Slot);
                break;
            case ServerPushLogEvent p:
                foreach (var (item, rule) in Rules("push"))
                {
                    if (rule.Name == p.FullName && (rule.Win is null || rule.Win == p.Win))
                    {
                        Hit(item, rule, p.Timestamp, DailySource.Game);
                    }
                }

                break;
            case QuestLogEvent { Committed: true } q:
                _commit = (q.Timestamp, q.TaskId);
                break;
            case MapEnteredLogEvent m:
                _map = m.Map;
                _visitFinish = null;
                _visitHits.Clear();
                foreach (var (item, rule) in Rules("map"))
                {
                    if (m.Map.StartsWith(rule.Prefix!, StringComparison.Ordinal))
                    {
                        Hit(item, rule, m.Timestamp, DailySource.Game);
                    }
                }

                break;
            case FeatureSwitchLogEvent f:
                if (!_switches.TryGetValue(f.Instance, out var old) || old.Open != f.Open)
                {
                    _switches[f.Instance] = (f.Open, f.Timestamp);
                    Version++;
                }

                if (f.Open && Instanced.Contains(f.Instance))
                {
                    // learn the event's weekdays from the server's own switches (entry 17)
                    var day = DailyCatalog.DayName(DayOf(DailyReset.Daily, f.Timestamp).DayOfWeek);
                    if (!_seenDays.TryGetValue(f.Instance, out var days))
                    {
                        days = new HashSet<string>(StringComparer.Ordinal);
                        _seenDays[f.Instance] = days;
                    }

                    if (days.Add(day))
                    {
                        Version++;
                    }
                }

                break;
            case ThrottleLogEvent { Request: { } req } t:
                _throttled[req] = t.Timestamp;
                for (var i = _pending.Count - 1; i >= 0; i--)
                {
                    var (at, item, rule) = _pending[i];
                    if (rule.ConfirmRequest == req && (t.Timestamp - at).TotalSeconds <= rule.ConfirmSeconds)
                    {
                        _pending.RemoveAt(i);
                        Hit(item, rule, at, DailySource.Game);
                    }
                }

                break;
        }

        CheckPresence(e.Timestamp);
        return Version != before;
    }

    /// <summary>
    /// The user set a stack's stored count (Hugo train requests, demon trial rewards, Yggdrasil attempts...); false for
    /// an item without a stack.
    /// </summary>
    public bool SetStack(string itemId, int value, DateTime nowLocal)
    {
        var item = _items.FirstOrDefault(i => i.Id == itemId);
        if (item?.Stack is null)
        {
            return false;
        }

        _stacks.Set(item, value, nowLocal);
        Version++;
        return true;
    }

    /// <summary>
    /// Events whose window opened, and producers whose countdown ended, in (<paramref name="from"/>, <paramref name="to"/>]:
    /// what the tray tells the user (entries 3/4/24). Done events and items not on today are skipped.
    /// </summary>
    public IReadOnlyList<DailyAlert> Alerts(DateTime from, DateTime to)
    {
        var list = new List<DailyAlert>();
        if (to <= from)
        {
            return list;
        }

        var snap = Snapshot(to);
        foreach (var v in snap.Items.Where(i => i.Kind == DailyKind.Event && i.Today && !i.Done && i.Windows is { Count: > 0 }))
        {
            var windows = v.Windows!;
            for (var k = 0; k < windows.Count; k++)
            {
                var w = windows[k];
                if (w.Start > from && w.Start <= to && to < w.End)
                {
                    var left = v.PerDay > 1 && v.Count < v.PerDay ? v.PerDay - v.Count : 0;
                    list.Add(new DailyAlert(v.Id, DailyAlertKind.EventOpen, w.End, k == windows.Count - 1, left));
                }
            }
        }

        foreach (var p in _producers.BecameReady(from, to))
        {
            list.Add(new DailyAlert(p.Id, DailyAlertKind.ProducerReady, null, false, 0));
        }

        return list;
    }

    private HashSet<string> Instanced => _instanced ??= _items.Where(i => i.Kind == DailyKind.Event).SelectMany(i => i.Instances).ToHashSet(StringComparer.Ordinal);

    private HashSet<string>? _instanced;

    private bool Counts(DateTime t) => t > _countFrom;

    private void CheckPresence(DateTime t)
    {
        foreach (var (item, rule) in Rules("presence"))
        {
            if (rule.Map == _map && OpenAt(item, t))
            {
                var p = Get(item.Id, DayOf(item.Reset, t));
                if (p is null || p.Count < Math.Max(1, item.PerDay))
                {
                    Hit(item, rule, t, DailySource.Game);
                }
            }
        }
    }

    /// <summary>An event is open at <paramref name="t"/>: inside one of its windows, or one of its switches is on (switched on that game day).</summary>
    private bool OpenAt(DailyItemDef item, DateTime t)
    {
        var day = DayOf(DailyReset.Daily, t);
        if (DailySchedule.WindowAt(DailySchedule.WindowsOn(item, day, _cat.Resets.Daily), t) is not null)
        {
            return true;
        }

        foreach (var inst in item.Instances)
        {
            if (_switches.TryGetValue(inst, out var s) && s.Open && DayOf(DailyReset.Daily, s.At) == day && t >= s.At)
            {
                return true;
            }
        }

        return false;
    }

    private void OnSettle(DateTime t)
    {
        foreach (var (item, rule) in Rules("settle"))
        {
            if (!SettleMatches(item, rule, t))
            {
                continue;
            }

            if (rule.FinishAtLeast is not { } least)
            {
                Hit(item, rule, t, DailySource.Game);
            }
            else if (_visitFinish >= least && _visitHits.Add(item.Id))
            {
                Hit(item, rule, t, DailySource.Game); // one reward run per map visit, however many results screens
            }
        }
    }

    private bool SettleMatches(DailyItemDef item, DailyRule rule, DateTime t)
    {
        var map = _map ?? string.Empty;
        if (rule.Maps.Count > 0 && !rule.Maps.Any(m => map.StartsWith(m, StringComparison.Ordinal)))
        {
            return false;
        }

        if (rule.MapsContain.Count > 0 && !rule.MapsContain.Any(m => map.Contains(m, StringComparison.Ordinal)))
        {
            return false;
        }

        if (rule.NotMaps.Any(m => map.StartsWith(m, StringComparison.Ordinal)) || rule.NotMapsContain.Any(m => map.Contains(m, StringComparison.Ordinal)))
        {
            return false;
        }

        if (rule.ScreensOpen.Count > 0 && !rule.ScreensOpen.Any(s => IsOpen(s, t)))
        {
            return false;
        }

        if (rule.NotScreensOpen.Any(s => IsOpen(s, t)))
        {
            return false;
        }

        if (rule.WhileOn.Count > 0 && !rule.WhileOn.Any(i => _switches.TryGetValue(i, out var s) && s.Open))
        {
            return false;
        }

        return !rule.InWindow || DailySchedule.WindowAt(DailySchedule.WindowsOn(item, DayOf(DailyReset.Daily, t), _cat.Resets.Daily), t) is not null;
    }

    private bool IsOpen(string ui, DateTime t) => _open.TryGetValue(ui, out var at) && (t - at).TotalSeconds <= _cat.ContextSeconds;

    private void OnToast(DateTime t)
    {
        if (Counts(t))
        {
            _producers.OnToast();
        }
    }

    /// <summary>
    /// A results screen right after an item's bag tip closed: that item was opened (Hyperweather loot boxes,
    /// 2026-11-05: tip 20:30:30.553, tip closed 31.273, results screen 31.422). Looking at a tip without opening shows no
    /// results screen. Each tip counts once.
    /// </summary>
    private void OnOpenItem(DateTime t)
    {
        if (_tip is not { } tip || _tipClosed is not { } closed || closed < tip.At || t < closed
            || (t - closed).TotalSeconds > 1 || (closed - tip.At).TotalMinutes > 5)
        {
            return;
        }

        _tip = null;
        foreach (var (item, rule) in Rules("openItem"))
        {
            if (rule.TakesItem(tip.Item))
            {
                Hit(item, rule, t, DailySource.Game);
            }
        }
    }

    private void OnBox(DateTime t)
    {
        if (_progressAt is not { } bar || (t - bar).TotalSeconds > 5)
        {
            return;
        }

        foreach (var (item, rule) in Rules("box"))
        {
            if (_partAt.TryGetValue((item.Id, rule.AfterPart!), out var after) && (t - after).TotalSeconds <= rule.AfterPartSeconds
                && DayOf(item.Reset, after) == DayOf(item.Reset, t)
                && (!rule.InWindow || DailySchedule.WindowAt(DailySchedule.WindowsOn(item, DayOf(DailyReset.Daily, t), _cat.Resets.Daily), t) is not null))
            {
                Hit(item, rule, t, DailySource.Game);
            }
        }
    }

    /// <summary>Replaces the user's own items (hand-ticked only; ids must not clash with the catalog's).</summary>
    public void SetCustomItems(IEnumerable<DailyCustomItem> custom)
    {
        ArgumentNullException.ThrowIfNull(custom);
        _items.RemoveAll(i => i.Section == OwnSection);
        var known = _items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var c in custom.Where(c => !string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.Name)))
        {
            if (known.Add(c.Id))
            {
                _items.Add(new DailyItemDef { Id = c.Id, Name = c.Name.Trim(), Section = OwnSection, How = "Ticked by hand" });
            }
        }

        Version++;
    }

    /// <summary>A companion job finished (journal 'jobFinished'): <paramref name="job"/> name and end state.</summary>
    public bool OnJob(string job, string state, DateTime atLocal)
    {
        var before = Version;
        foreach (var (item, rule) in Rules("job"))
        {
            var states = rule.States.Count == 0 ? ["Succeeded"] : rule.States;
            if (rule.Jobs.Contains(job, StringComparer.Ordinal) && states.Contains(state, StringComparer.Ordinal))
            {
                Hit(item, rule, atLocal, DailySource.Companion, ignoreWatermark: true);
            }
        }

        return Version != before;
    }

    /// <summary>
    /// The user ticked (<paramref name="done"/> true) or unticked an item at <paramref name="nowLocal"/>. Their choice
    /// wins until the item's next reset; choosing what the log already says clears it. False for an unknown item.
    /// </summary>
    public bool SetDone(string itemId, bool done, DateTime nowLocal)
    {
        var item = _items.FirstOrDefault(i => i.Id == itemId);
        if (item is null || item.Kind == DailyKind.Producer)
        {
            return false;
        }

        if (item.Stack is { Mode: "fill" })
        {
            // the special train: a tick means "summoned" (the stack empties and its timer starts); there is no untick
            if (done)
            {
                _stacks.Summon(item, nowLocal);
                Version++;
            }

            return true;
        }

        var day = DayOf(item.Reset, nowLocal);
        var auto = AutoDoneNow(item, Get(item.Id, day), nowLocal);
        if (done == auto)
        {
            _overrides.Remove(itemId);
        }
        else
        {
            _overrides[itemId] = (day, done);
        }

        Version++;
        return true;
    }

    /// <summary>The list at <paramref name="nowLocal"/>.</summary>
    public DailySnapshot Snapshot(DateTime nowLocal)
    {
        var items = new List<DailyItemView>(_items.Count);
        var r = _cat.Resets;
        var gameDay = DayOf(DailyReset.Daily, nowLocal);
        foreach (var item in _items)
        {
            var day = DayOf(item.Reset, nowLocal);
            var p = Get(item.Id, day);
            var auto = AutoDoneNow(item, p, nowLocal);
            var mine = _overrides.TryGetValue(item.Id, out var o) && o.Day == day;
            var windows = DailySchedule.WindowsOn(item, gameDay, r.Daily);
            bool? open = null;
            if (item.Kind == DailyKind.Event)
            {
                open = windows.Count > 0 || item.Instances.Any(i => _switches.ContainsKey(i)) ? OpenAt(item, nowLocal) : null;
            }

            var done = mine ? o.Done : auto;
            int? stack = null;
            DateTime? timer = null;
            if (item.Stack is { } sd)
            {
                var (value, summoned) = _stacks.Peek(item, nowLocal);
                stack = value;
                if (summoned is { } at && sd.TimerMinutes > 0 && nowLocal < at.AddMinutes(sd.TimerMinutes))
                {
                    timer = at.AddMinutes(sd.TimerMinutes);
                }
            }

            items.Add(new DailyItemView(
                item.Id, item.Name, item.Section, item.Kind, p?.Count ?? 0, PerDayOf(item), done, auto,
                mine ? DailySource.You : p?.Source ?? DailySource.None, p?.First, p?.Last, item.How, item.Learning, item.Shown, open, item.Usual,
                DailySchedule.OnDay(item, gameDay, _seenDays) && !SkippedNow(item, nowLocal), windows,
                Missed: item.Kind == DailyKind.Event && !done && windows.Count > 0 && nowLocal >= windows[^1].End,
                Stack: stack, StackCap: item.Stack?.Cap ?? 0, StackFill: item.Stack?.Mode == "fill", TimerUntil: timer));
        }

        var week = DayOf(DailyReset.Weekly, nowLocal);
        return new DailySnapshot(
            nowLocal, gameDay, gameDay.ToDateTime(TimeOnly.MinValue).AddDays(1) + r.Daily, week.ToDateTime(TimeOnly.MinValue).AddDays(7) + r.Weekly,
            _items.Any(i => i.Section == OwnSection) ? [.. _cat.Sections, new DailySection(OwnSection, "Your own")] : _cat.Sections,
            items, LastEventAt, Version, _producers.Views(nowLocal));
    }

    /// <summary>
    /// Not on today's list now: one of its <see cref="DailyItemDef.SkipWhileOn"/> switches is on, or none of its
    /// <see cref="DailyItemDef.OnlyWhileOn"/> switches is (on = switched on within 30 hours).
    /// </summary>
    private bool SkippedNow(DailyItemDef item, DateTime nowLocal) =>
        (item.SkipWhileOn.Count > 0 && AnySwitchOn(item.SkipWhileOn, nowLocal))
        || (item.OnlyWhileOn.Count > 0 && !AnySwitchOn(item.OnlyWhileOn, nowLocal));

    private bool AnySwitchOn(IReadOnlyList<string> prefixes, DateTime nowLocal) =>
        _switches.Any(kv => kv.Value.Open && (nowLocal - kv.Value.At).TotalHours < 30
            && prefixes.Any(prefix => kv.Key.StartsWith(prefix, StringComparison.Ordinal)));

    /// <summary>The day (period) an item of reset class <paramref name="reset"/> counts <paramref name="t"/> in.</summary>
    public DateOnly DayOf(DailyReset reset, DateTime t)
    {
        var r = _cat.Resets;
        switch (reset)
        {
            case DailyReset.Calendar:
                return DateOnly.FromDateTime(t - r.Calendar);
            case DailyReset.Weekly:
                var d = DateOnly.FromDateTime(t - r.Weekly);
                return d.AddDays(-(((int)d.DayOfWeek - (int)r.WeeklyDay + 7) % 7));
            default:
                return DateOnly.FromDateTime(t - r.Daily);
        }
    }

    /// <summary>The earliest moment any item's current day began (where a start-up replay of the log must begin).</summary>
    public DateTime ReplayFrom(DateTime nowLocal)
    {
        var r = _cat.Resets;
        var starts = new[]
        {
            DayOf(DailyReset.Daily, nowLocal).ToDateTime(TimeOnly.MinValue) + r.Daily,
            DayOf(DailyReset.Calendar, nowLocal).ToDateTime(TimeOnly.MinValue) + r.Calendar,
            DayOf(DailyReset.Weekly, nowLocal).ToDateTime(TimeOnly.MinValue) + r.Weekly,
        };
        return starts.Min();
    }

    /// <summary>A recount: forgets the counts last seen at or after <paramref name="from"/> (the replay counts them again).</summary>
    public void ForgetCountsFrom(DateTime from)
    {
        foreach (var key in _progress.Where(kv => (kv.Value.Last ?? kv.Value.First ?? DateTime.MinValue) >= from).Select(kv => kv.Key).ToList())
        {
            _progress.Remove(key);
        }

        Version++;
    }

    /// <summary>The saved form: counts of the last 60 days, the user's ticks and the watermark.</summary>
    public DailyState Export()
    {
        var newest = _progress.Keys.Select(k => k.Day).DefaultIfEmpty(DateOnly.MinValue).Max();
        return new DailyState
        {
            SchemaVersion = 1,
            Catalog = _cat.Fingerprint,
            Watermark = LastEventAt ?? _countFrom,
            Progress = [.. _progress
                .Where(kv => kv.Key.Day >= newest.AddDays(-60))
                .Select(kv => new DailyProgressDto
                {
                    Item = kv.Key.Item, Day = kv.Key.Day, Count = kv.Value.Count, First = kv.Value.First, Last = kv.Value.Last, Source = kv.Value.Source,
                    Parts = kv.Value.Parts.Count > 0 ? [.. kv.Value.Parts] : null,
                })],
            Overrides = [.. _overrides.Select(kv => new DailyOverrideDto { Item = kv.Key, Day = kv.Value.Day, Done = kv.Value.Done })],
            Stacks = [.. _stacks.Export()],
            Producers = [.. _producers.Export()],
            SeenDays = _seenDays.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)[.. kv.Value.Order(StringComparer.Ordinal)], StringComparer.Ordinal),
        };
    }

    /// <summary>Loads a saved form (unknown items are dropped) and sets <see cref="CountFrom"/> to its watermark.</summary>
    public void Import(DailyState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var known = _items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var p in state.Progress.Where(p => known.Contains(p.Item)))
        {
            var progress = new Progress { Count = p.Count, First = p.First, Last = p.Last, Source = p.Source };
            progress.Parts.UnionWith(p.Parts ?? []);
            _progress[(p.Item, p.Day)] = progress;
        }

        foreach (var o in state.Overrides.Where(o => known.Contains(o.Item)))
        {
            _overrides[o.Item] = (o.Day, o.Done);
        }

        foreach (var s in (state.Stacks ?? []).Where(s => _items.Any(i => i.Id == s.Item && i.Stack is not null)))
        {
            _stacks.Import(s);
        }

        foreach (var p in state.Producers ?? [])
        {
            _producers.Import(p);
        }

        foreach (var (inst, days) in state.SeenDays ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            _seenDays[inst] = days.Where(d => DailyCatalog.DayNames.Contains(d)).ToHashSet(StringComparer.Ordinal);
        }

        _countFrom = state.Watermark;
        Version++;
    }

    private void OnUi(UiLogEvent u)
    {
        if (u.UiName is not { } ui)
        {
            return;
        }

        var t = u.Timestamp;
        if (u.Verb == UiVerb.PopupRealShow)
        {
            if (_rewardScreens.Contains(ui))
            {
                OnReward(t, popup: true);
                OnBox(t);
                OnToast(t);
            }

            return;
        }

        if (CloseVerbs.Contains(u.Verb))
        {
            if (ui == ItemTipScreen && u.Verb == UiVerb.AddToCloseList)
            {
                _tipClosed = t;
            }

            // Only the close of an open screen counts: the game disposes a closed screen again seconds later (memory
            // clean-up), and that must not look like "a shop just closed" (Monday 20:54, a Kanu hand-in was lost).
            if (_open.Remove(ui))
            {
                _closed[ui] = t;
                if (Counts(t) && _producers.OnClose(ui, t))
                {
                    Version++;
                }
            }

            return;
        }

        if (u.Verb != UiVerb.Show)
        {
            return;
        }

        if (Counts(t) && _producers.OnShow(ui, t))
        {
            Version++;
        }

        if (ui == SettleScreen)
        {
            OnOpenItem(t);
            OnSettle(t);
        }
        else if (ui == ToastScreen)
        {
            OnToast(t);
        }
        else if (ui == ProgressScreen)
        {
            _progressAt = t;
        }

        foreach (var (item, rule) in Rules("shown"))
        {
            if (rule.Screens.Contains(ui, StringComparer.Ordinal))
            {
                Hit(item, rule, t, DailySource.Game);
            }
        }

        if (ui == "UI_NPC_TALK")
        {
            _npcTalk = t;
        }
        else if (_rewardScreens.Contains(ui))
        {
            OnReward(t, popup: false);
        }
        else if (!IsPassive(ui))
        {
            _open[ui] = t;
        }
    }

    private void OnReward(DateTime t, bool popup)
    {
        string? context = null;
        var newest = DateTime.MinValue;
        foreach (var (ui, at) in _open)
        {
            if ((t - at).TotalSeconds <= _cat.ContextSeconds && at > newest)
            {
                newest = at;
                context = ui;
            }
        }

        if (context is not null)
        {
            foreach (var (item, rule) in Rules("reward"))
            {
                if (rule.Screens.Contains(context, StringComparer.Ordinal)
                    && (rule.AfterClose.Count == 0 || rule.AfterClose.Any(s => _closed.TryGetValue(s, out var closed)
                        && t >= closed && (t - closed).TotalSeconds <= rule.AfterCloseSeconds)))
                {
                    Hit(item, rule, t, DailySource.Game);
                }
            }
        }

        if (!popup)
        {
            return;
        }

        foreach (var (item, rule) in Rules("handIn"))
        {
            if (!HandInContext(rule, t))
            {
                continue;
            }

            if (rule.ConfirmRequest is null
                || (_throttled.TryGetValue(rule.ConfirmRequest, out var seen) && (t - seen).TotalSeconds <= rule.ConfirmSeconds))
            {
                Hit(item, rule, t, DailySource.Game);
            }
            else
            {
                _pending.Add((t, item, rule));
            }
        }
    }

    private bool HandInContext(DailyRule rule, DateTime t)
    {
        if (rule.Map is not null && rule.Map != _map)
        {
            return false;
        }

        var byQuest = rule.QuestContains is not null && _commit is { } c
            && c.Task.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(rule.QuestContains, StringComparison.Ordinal)
            && (t - c.At).TotalSeconds <= rule.QuestSeconds;
        var byTalk = rule.NpcTalkSeconds > 0 && _npcTalk is { } n && (t - n).TotalSeconds <= rule.NpcTalkSeconds;
        if (!byQuest && !byTalk)
        {
            return false;
        }

        foreach (var shop in rule.NotAfterScreens)
        {
            if (_open.ContainsKey(shop) || (_closed.TryGetValue(shop, out var closed) && (t - closed).TotalSeconds <= rule.NotAfterSeconds))
            {
                return false;
            }
        }

        return true;
    }

    private void ExpirePending(DateTime now) =>
        _pending.RemoveAll(p => (now - p.At).TotalSeconds > p.Rule.ConfirmSeconds);

    private bool IsPassive(string ui) =>
        _passive.Contains(ui) || _cat.PassiveContains.Any(part => ui.Contains(part, StringComparison.Ordinal));

    private IEnumerable<(DailyItemDef Item, DailyRule Rule)> Rules(string on)
    {
        foreach (var item in _items)
        {
            foreach (var rule in item.Rules)
            {
                if (rule.On == on)
                {
                    yield return (item, rule);
                }
            }
        }
    }

    private void Hit(DailyItemDef item, DailyRule rule, DateTime t, DailySource source, bool ignoreWatermark = false)
    {
        if (!ignoreWatermark && t <= _countFrom)
        {
            return;
        }

        if (rule.GapSeconds > 0 && _lastHit.TryGetValue(item.Id, out var lastHit) && (t - lastHit).TotalSeconds < rule.GapSeconds)
        {
            return; // the same moment seen twice (a pop-up and its toast)
        }

        _lastHit[item.Id] = t;
        if (item.Stack is not null && _stacks.Includes(item, t))
        {
            // already in the saved stored count (a recount replaying the log over saved values)
            if (rule.Empties)
            {
                return;
            }
        }
        else if (item.Stack is { } stack)
        {
            var fed = stack.Feeds is { } f ? _items.FirstOrDefault(i => i.Id == f && i.Stack is not null) : null;
            if (stack.Gate && !rule.Empties && stack.Mode == "use" && _stacks.Peek(item, t).Value <= 0)
            {
                // Nothing stored: not a regular request. With the special train ready or summoned, it is that train.
                if (fed is { Stack: { } fs } && !_stacks.Includes(fed, t) && _stacks.Peek(fed, t) is var (value, summoned)
                    && ((fs.DueAt > 0 && value >= fs.DueAt) || (summoned is { } at && fs.TimerMinutes > 0 && t < at.AddMinutes(fs.TimerMinutes))))
                {
                    _stacks.Finish(fed, t);
                    Version++;
                }

                return;
            }

            _stacks.Hit(item, rule.Empties, t);
            if (fed is not null && !rule.Empties && !_stacks.Includes(fed, t))
            {
                _stacks.Hit(fed, false, t);
            }

            Version++;
            if (rule.Empties)
            {
                return;
            }
        }

        var day = DayOf(item.Reset, t);
        if (!_progress.TryGetValue((item.Id, day), out var p))
        {
            p = new Progress();
            _progress[(item.Id, day)] = p;
        }

        if (rule.Part is { } part)
        {
            _partAt[(item.Id, part)] = t;
            if (!p.Parts.Add(part))
            {
                return;
            }

            p.Count = p.Parts.Count;
        }
        else
        {
            var target = PerDayOf(item);
            if (target > 0 && p.Count >= target)
            {
                return;
            }

            p.Count = rule.Full && target > 0 ? target : p.Count + 1;
        }

        p.First ??= t;
        p.Last = t;
        p.Source = source;
        Version++;
    }

    private Progress? Get(string item, DateOnly day) => _progress.GetValueOrDefault((item, day));

    /// <summary>Daily target: the number of parts, or 'perDay'.</summary>
    private static int PerDayOf(DailyItemDef item) => item.Parts.Count > 0 ? item.Parts.Count : item.PerDay;

    private static bool AutoDone(DailyItemDef item, Progress? p) =>
        p is not null && (PerDayOf(item) > 0 ? p.Count >= PerDayOf(item) : p.Count > 0);

    /// <summary>
    /// Done by the log alone at <paramref name="now"/>. A stack with 'dueAt' is only due when it holds that many: the
    /// Yggdrasil temple at 4 of 5, the special train at 6 of 6 (ready to summon).
    /// </summary>
    private bool AutoDoneNow(DailyItemDef item, Progress? p, DateTime now) =>
        item.Stack is { DueAt: > 0 } s ? _stacks.Peek(item, now).Value < s.DueAt : AutoDone(item, p);

    private sealed class Progress
    {
        public int Count { get; set; }

        public DateTime? First { get; set; }

        public DateTime? Last { get; set; }

        public DailySource Source { get; set; }

        public HashSet<string> Parts { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>The checklist's saved state (RuntimeDir\dailies\state.json).</summary>
public sealed record DailyState
{
    /// <summary>Schema version (1).</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Newest log event already counted (log time).</summary>
    public DateTime Watermark { get; init; }

    /// <summary>The data file's fingerprint the counts were made with (null before Release 4.35).</summary>
    public string? Catalog { get; init; }

    /// <summary>Counts per item and day.</summary>
    public IReadOnlyList<DailyProgressDto> Progress { get; init; } = [];

    /// <summary>The user's ticks.</summary>
    public IReadOnlyList<DailyOverrideDto> Overrides { get; init; } = [];

    /// <summary>Stored counts (null in files from before 2026-11-05).</summary>
    public IReadOnlyList<DailyStackDto>? Stacks { get; init; }

    /// <summary>Producer cards (null in files from before 2026-11-05).</summary>
    public IReadOnlyList<DailyProducerDto>? Producers { get; init; }

    /// <summary>Weekdays each event switch was seen opening on (learned schedule).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? SeenDays { get; init; }

    /// <summary>
    /// The server's daily update as minutes after 00:00 UTC, learned from the game log (null until one was seen, and in
    /// files from before 2026-11-08). It names no data file or PC offset, so it stays right when either changes; the
    /// shift of the data file's times is derived from it (see <see cref="DailyService"/>).
    /// </summary>
    public int? LearnedResetUtcMinutes { get; init; }
}

/// <summary>What the tray says (entries 3/4/24).</summary>
public enum DailyAlertKind
{
    /// <summary>An event's window opened.</summary>
    EventOpen,

    /// <summary>A producer's countdown ended.</summary>
    ProducerReady,
}

/// <summary>One thing to tell the user.</summary>
/// <param name="ItemId">Item id.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Until">Events: when the window closes.</param>
/// <param name="Last">Events: today's last window.</param>
/// <param name="Left">Events with a daily count: how many are still to do.</param>
public sealed record DailyAlert(string ItemId, DailyAlertKind Kind, DateTime? Until, bool Last, int Left);

/// <summary>One saved count.</summary>
public sealed record DailyProgressDto
{
    /// <summary>Item id.</summary>
    public string Item { get; init; } = "";

    /// <summary>Its day.</summary>
    public DateOnly Day { get; init; }

    /// <summary>Count.</summary>
    public int Count { get; init; }

    /// <summary>First hit.</summary>
    public DateTime? First { get; init; }

    /// <summary>Last hit.</summary>
    public DateTime? Last { get; init; }

    /// <summary>Who ticked it.</summary>
    public DailySource Source { get; init; }

    /// <summary>Parts seen (items with parts).</summary>
    public IReadOnlyList<string>? Parts { get; init; }
}

/// <summary>One saved user tick.</summary>
public sealed record DailyOverrideDto
{
    /// <summary>Item id.</summary>
    public string Item { get; init; } = "";

    /// <summary>The day it applies to.</summary>
    public DateOnly Day { get; init; }

    /// <summary>Done or not done.</summary>
    public bool Done { get; init; }
}
