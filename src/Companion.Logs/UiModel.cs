using Companion.Contracts;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;

namespace Companion.Logs;

/// <summary>
/// The UI-state model built from log events (SPEC 6.2). Not thread-safe: the tracker serialises access. Time windows
/// (cinema 120 s, revive 60 s, guide mask 2 s, match 120 s, craft end + 2 s) are evaluated against the "now" passed to
/// <see cref="Snapshot"/> (local wall time, the same clock the game stamps its lines with).
/// </summary>
internal sealed class UiModel
{
    internal static readonly TimeSpan CinemaTimeout = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan ReviveTimeout = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan GuideMaskWindow = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan MatchWindow = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan CraftEndGrace = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan CraftReshowWindow = TimeSpan.FromSeconds(1);

    /// <summary>A craft session with no station UI shown ends this long after its last is_block (blocked spot).</summary>
    internal static readonly TimeSpan CraftIdleWithoutStation = TimeSpan.FromSeconds(10);

    private const string LoadProgressUi = "UI_LOAD_PROGRESS";
    private const string ReviveUi = "UI_REVIVE";
    private const string GuideMaskUi = "UI_GUIDE_MASK";
    private const string CraftViewUi = "UI_LIFESKILL_MAKE_VIEW";
    private const string ItemGetTipUi = "UI_ITEM_GET_TIP";
    private static readonly HashSet<string> LoginUis = new(["UI_SERVER", "UI_SELECT_ROLE"], StringComparer.Ordinal);
    private static readonly HashSet<string> CombatIndicators = new(UiCatalog.CombatIndicators, StringComparer.Ordinal);

    private readonly IUiClassifier _classifier;
    private readonly UiClassifier? _learner;

    // ---- generation facts (reset by a rotation only; the bootstrap recovers them from byte 0)
    private int? _autoCollect;
    private CharacterKey? _character;
    private SizeI? _resolution;
    private DateTime? _lastJank, _lastThrottle, _lastCombat, _lastScreenSize, _lastGuideBan, _lastGuideBanMask, _lastPay;
    private readonly HashSet<string> _shown = new(StringComparer.Ordinal);

    // ---- session state (reset by a rotation and by session markers)
    private readonly List<string> _stack = [];

    /// <summary>
    /// When each open popup of the TIPS2 family was shown: they log "open const ui" and never a close line (incident 25).
    /// </summary>
    private readonly Dictionary<string, DateTime> _silentOpened = new(StringComparer.Ordinal);

    /// <summary>
    /// A window below a silent popup closing at least this long after the popup was shown proves the popup is gone (the
    /// game's own close of a window under it comes within milliseconds of the show: 25 ms in the 2026-10-25 log).
    /// </summary>
    internal static readonly TimeSpan SilentPopupMargin = TimeSpan.FromSeconds(3);
    private LogPosition? _lastHideTopNil;
    private FocusState _focus;
    private DateTime? _lastFocusChange;
    private bool _sawMain, _sawHideReal;
    private bool _sceneLoading, _loadingUi, _login;
    private readonly HashSet<string> _cinemaOpen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _scenarioOpen = new(StringComparer.Ordinal);
    private DateTime? _cinemaLast, _scenarioLast, _reviveAt, _matchLastShown;
    private bool _guideMaskOpen;
    private readonly HashSet<string> _matchOpen = new(StringComparer.Ordinal);
    private CraftState? _craft;
    private ItemTipInfo? _lastItemTip;
    private DateTime? _lastAutoEat;
    private GridClickInfo? _lastGridClick;

    internal UiModel(IUiClassifier classifier)
    {
        _classifier = classifier;
        _learner = classifier as UiClassifier;
    }

    /// <summary>Position the model reflects (set by the tracker after every read).</summary>
    internal LogPosition? AsOf { get; set; }

    internal int? AutoCollectStatus => _autoCollect;

    internal CharacterKey? Character => _character;

    internal SizeI? LastResolution => _resolution;

    /// <summary>GameAvailable: 'open const ui UI_MAIN' and 'hide_progress real' both seen since the last reset.</summary>
    internal bool GameAvailable => _sawMain && _sawHideReal;

    /// <summary>In the world: available, not loading, not logging in.</summary>
    internal bool InWorld => GameAvailable && !_sceneLoading && !_loadingUi && !_login;

    /// <summary>Full reset (rotation): everything back to Unknown, learned Stack names kept by the classifier.</summary>
    internal void ResetAll()
    {
        _autoCollect = null;
        _character = null;
        _resolution = null;
        _lastJank = _lastThrottle = _lastCombat = _lastScreenSize = _lastGuideBan = _lastGuideBanMask = _lastPay = null;
        _shown.Clear();
        AsOf = null;
        ResetSession();
    }

    /// <summary>Session reset (OnApplicationQuit, logout, new log callback): UI model back to Unknown.</summary>
    internal void ResetSession()
    {
        _stack.Clear();
        _silentOpened.Clear();
        _lastHideTopNil = null;
        _focus = FocusState.Unknown;
        _lastFocusChange = null;
        _sawMain = _sawHideReal = false;
        _sceneLoading = _loadingUi = _login = false;
        _cinemaOpen.Clear();
        _scenarioOpen.Clear();
        _cinemaLast = _scenarioLast = _reviveAt = _matchLastShown = null;
        _guideMaskOpen = false;
        _matchOpen.Clear();
        _craft = null;
        _lastItemTip = null;
        _lastGridClick = null;
    }

    /// <summary>
    /// Forgets the log positions the model holds (after a log rotation the model is kept, but a position in the old file
    /// proves nothing about the new one: the last 'hide top ui nil' is cleared, so a clean HUD must be shown anew).
    /// </summary>
    internal void ForgetPositions() => _lastHideTopNil = null;

    /// <summary>Applies one event (in file order).</summary>
    internal void Apply(LogEvent e)
    {
        switch (e)
        {
            case UiLogEvent u:
                ApplyUi(u);
                break;
            case SceneLoadLogEvent s:
                ApplyScene(s);
                break;
            case FocusLogEvent f:
                _focus = f.Recovered ? FocusState.Recovered : FocusState.Lost;
                _lastFocusChange = f.Timestamp;
                break;
            case ResolutionLogEvent r:
                if (r.Source == ResolutionSource.CurrentScreen)
                {
                    _resolution = r.Size;
                }
                else
                {
                    _lastScreenSize = r.Timestamp;
                }

                break;
            case AutoCollectLogEvent a:
                _autoCollect = a.Status;
                break;
            case CraftTickLogEvent c:
                ApplyCraftTick(c);
                break;
            case JankLogEvent j:
                _lastJank = j.Timestamp;
                break;
            case SessionLogEvent m:
                ApplySession(m);
                break;
            case CharacterLoginLogEvent l:
                _character = l.Character;
                break;
            case ThrottleLogEvent t:
                _lastThrottle = t.Timestamp;
                break;
            case PayLogEvent p:
                _lastPay = p.Timestamp;
                break;
            case GuideBanLogEvent g:
                _lastGuideBan = g.Timestamp;
                if (g.Mask)
                {
                    _lastGuideBanMask = g.Timestamp;
                }

                break;
            case ItemTipLogEvent t:
                _lastItemTip = new ItemTipInfo(t.ItemName, t.ItemId, t.Timestamp, t.Position);
                break;
            case AutoEatLogEvent a:
                _lastAutoEat = a.Timestamp;
                break;
            case GridClickLogEvent g:
                _lastGridClick = new GridClickInfo(g.Row, g.Column, g.Piece, g.Timestamp, g.Position);
                break;
        }
    }

    /// <summary>Builds the immutable snapshot at local wall time <paramref name="now"/>.</summary>
    internal UiStateSnapshot Snapshot(LogPosition? asOf, DateTime now) => new(
        asOf,
        GameAvailable,
        BusyAt(now),
        _stack.ToArray(),
        _lastHideTopNil,
        _focus,
        _lastFocusChange,
        _autoCollect,
        _character,
        _resolution,
        _lastJank,
        _lastThrottle,
        _lastCombat,
        _lastScreenSize,
        _lastGuideBan,
        _lastPay,
        CraftAt(now),
        _shown.ToArray()) { LastItemTip = _lastItemTip, LastAutoEat = _lastAutoEat, LastGridClick = _lastGridClick };

    /// <summary>Busy state at <paramref name="now"/> (first match wins).</summary>
    internal BusyKind BusyAt(DateTime now)
    {
        if (_sceneLoading || _loadingUi)
        {
            return BusyKind.Loading;
        }

        if (_login)
        {
            return BusyKind.Login;
        }

        if (_cinemaOpen.Count > 0 && Within(_cinemaLast, now, CinemaTimeout))
        {
            return BusyKind.Cinema;
        }

        if (_scenarioOpen.Count > 0 && Within(_scenarioLast, now, CinemaTimeout))
        {
            return BusyKind.Scenario;
        }

        if (Within(_reviveAt, now, ReviveTimeout))
        {
            return BusyKind.Revive;
        }

        if (_guideMaskOpen || Within(_lastGuideBanMask, now, GuideMaskWindow))
        {
            return BusyKind.GuideMask;
        }

        if (_matchOpen.Count > 0 || Within(_matchLastShown, now, MatchWindow))
        {
            return BusyKind.Match;
        }

        return GameAvailable ? BusyKind.None : BusyKind.Unknown;
    }

    private CraftSessionInfo CraftAt(DateTime now) => _craft is null
        ? CraftSessionInfo.None
        : new CraftSessionInfo(
            !_craft.IsOver(now),
            _craft.StationUi,
            _craft.StartedAt,
            _craft.StartPosition,
            _craft.IsBlockCount,
            _craft.LastTickAt,
            _craft.EndedAt,
            _craft.Reshown) { ItemGets = _craft.ItemGets };

    private void ApplyUi(UiLogEvent u)
    {
        var name = u.UiName;
        var ts = u.Timestamp;
        switch (u.Verb)
        {
            case UiVerb.Show:
            case UiVerb.OpenConst:
                if (name is not null)
                {
                    ApplyOpen(u.Verb, name, u);
                }

                break;

            case UiVerb.HideTop:
                if (u.IsHideTopNil)
                {
                    _stack.Clear();
                    _silentOpened.Clear();
                    _lastHideTopNil = u.Position;
                    break;
                }

                if (name is not null)
                {
                    _learner?.LearnStack(name);
                    DropSilentAbove(name, ts);
                    ApplyClose(name, ts, stack: true);
                }

                break;

            case UiVerb.AddToCloseList:
            case UiVerb.ToHide:
                if (name is not null)
                {
                    DropSilentAbove(name, ts);
                    ApplyClose(name, ts, stack: true);
                }

                break;

            case UiVerb.StackOverRemove:
            case UiVerb.DisposeAllClose:
            case UiVerb.CheckCleanDispose:
                if (name is not null)
                {
                    ApplyClose(name, ts, stack: true);
                }

                break;

            case UiVerb.DisposeImmediately:
                // Cache eviction: ignored by the advisory stack, but it is how cinema/scenario UIs close in the real log.
                if (name is not null)
                {
                    ApplyClose(name, ts, stack: false);
                }

                break;

            case UiVerb.CheckCanOpenFail:
            case UiVerb.PopupRealShow:
                break;
        }
    }

    private void ApplyOpen(UiVerb verb, string name, UiLogEvent u)
    {
        var ts = u.Timestamp;
        _shown.Add(name);
        var kind = _classifier.Classify(name);

        // UI_MAIN is the HUD root itself (opened once per scene entry, never closed): never a screen above the HUD.
        if ((kind is UiKind.Stack or UiKind.Forbidden or UiKind.Social or UiKind.Unknown) && name != UiCatalog.MainUi)
        {
            _stack.Remove(name);
            _stack.Add(name);
            if (ClosesSilently(name))
            {
                _silentOpened[name] = ts;
            }
        }

        if (CombatIndicators.Contains(name))
        {
            _lastCombat = ts;
        }

        if (name == LoadProgressUi)
        {
            _loadingUi = true;
        }
        else if (LoginUis.Contains(name))
        {
            _login = true;
        }
        else if (name.StartsWith("UI_CINEMA_", StringComparison.Ordinal))
        {
            _cinemaOpen.Add(name);
            _cinemaLast = ts;
        }
        else if (name.StartsWith("UI_SCENARIO", StringComparison.Ordinal))
        {
            _scenarioOpen.Add(name);
            _scenarioLast = ts;
        }
        else if (name == ReviveUi)
        {
            _reviveAt = ts;
        }
        else if (name == GuideMaskUi)
        {
            _guideMaskOpen = true;
        }
        else if (name.StartsWith("UI_TEAM_3V3_", StringComparison.Ordinal))
        {
            _matchOpen.Add(name);
            _matchLastShown = ts;
        }
        else if (name == UiCatalog.MainUi && verb == UiVerb.OpenConst)
        {
            _login = false;
            _sawMain = true;
        }

        if (kind == UiKind.Station)
        {
            if (_craft is null || _craft.IsOver(ts))
            {
                _craft = new CraftState(ts, u.Position) { StationUi = name };
            }
            else
            {
                _craft.StationUi ??= name;
            }
        }

        if (name == ItemGetTipUi && verb == UiVerb.Show && _craft is { StationUi: not null } running && !running.IsOver(ts))
        {
            running.ItemGets++;
        }

        if (name == CraftViewUi && verb == UiVerb.Show && _craft?.EndedAt is { } ended && ts >= ended && ts - ended <= CraftReshowWindow)
        {
            _craft.Reshown = true;
        }
    }

    /// <summary>
    /// Incident 25 (2026-10-25): UI_TIPS2_EXCHANGE (Forbidden) was shown at 23:46:24 and, like every TIPS2 popup, never
    /// logged a close; it parked a job for good although the user had answered it and closed the window under it 8 s
    /// later. A window below a silent popup that the user (or the game) asks to close, at least
    /// <see cref="SilentPopupMargin"/> after the popup was shown, proves the popup is gone: it is dropped. Only the close
    /// requests (hide top, add to close list, to hide) count, never a cache dispose.
    /// </summary>
    private void DropSilentAbove(string name, DateTime ts)
    {
        var at = _stack.IndexOf(name);
        if (at < 0)
        {
            return;
        }

        for (var i = _stack.Count - 1; i > at; i--)
        {
            if (_silentOpened.TryGetValue(_stack[i], out var shown) && ts - shown >= SilentPopupMargin)
            {
                _silentOpened.Remove(_stack[i]);
                _stack.RemoveAt(i);
            }
        }
    }

    /// <summary>A popup that never logs its close (the TIPS2 family, incident 25).</summary>
    internal static bool ClosesSilently(string name) => name.StartsWith("UI_TIPS2", StringComparison.Ordinal);

    private void ApplyClose(string name, DateTime ts, bool stack)
    {
        if (stack)
        {
            _stack.Remove(name);
        }

        if (_cinemaOpen.Remove(name) || name.StartsWith("UI_CINEMA_", StringComparison.Ordinal))
        {
            _cinemaLast = ts;
        }

        if (_scenarioOpen.Remove(name) || name.StartsWith("UI_SCENARIO", StringComparison.Ordinal))
        {
            _scenarioLast = ts;
        }

        if (name == ReviveUi)
        {
            _reviveAt = null;
        }
        else if (name == GuideMaskUi)
        {
            _guideMaskOpen = false;
        }
        else if (name.StartsWith("UI_TEAM_3V3_", StringComparison.Ordinal))
        {
            _matchOpen.Remove(name);
        }

        if (_craft is { EndedAt: null } c && c.StationUi == name)
        {
            c.EndedAt = ts;
        }
    }

    private void ApplyScene(SceneLoadLogEvent s)
    {
        switch (s.Phase)
        {
            case ScenePhase.StartLoad:
                _stack.Clear();
                _silentOpened.Clear();
                _sceneLoading = true;
                break;
            case ScenePhase.HideProgressReal:
                _sceneLoading = false;
                _loadingUi = false;
                _sawHideReal = true;
                break;
        }
    }

    private void ApplyCraftTick(CraftTickLogEvent c)
    {
        if (_craft is null || _craft.IsOver(c.Timestamp))
        {
            _craft = new CraftState(c.Timestamp, c.Position);
        }

        _craft.IsBlockCount++;
        _craft.LastTickAt = c.Timestamp;
    }

    private void ApplySession(SessionLogEvent m)
    {
        switch (m.Marker)
        {
            case SessionMarker.LogCallbackAdded:
            case SessionMarker.ApplicationQuit:
                ResetSession();
                break;
            case SessionMarker.LogoutStart:
                ResetSession();
                _login = true;
                break;
            case SessionMarker.ScreenSizeChange:
                _lastScreenSize = m.Timestamp;
                break;
            case SessionMarker.PingTrigger:
                break;
        }
    }

    private static bool Within(DateTime? at, DateTime now, TimeSpan window) => at is { } t && now - t <= window;

    private sealed class CraftState(DateTime startedAt, LogPosition startPosition)
    {
        internal DateTime StartedAt { get; } = startedAt;

        internal LogPosition StartPosition { get; } = startPosition;

        internal string? StationUi { get; set; }

        internal int IsBlockCount { get; set; }

        internal DateTime? LastTickAt { get; set; }

        internal DateTime? EndedAt { get; set; }

        internal bool Reshown { get; set; }

        internal int ItemGets { get; set; }

        /// <summary>True once the session has ended at time <paramref name="t"/>.</summary>
        internal bool IsOver(DateTime t)
        {
            if (EndedAt is { } ended)
            {
                return t - ended >= CraftEndGrace;
            }

            return StationUi is null && LastTickAt is { } last && t - last > CraftIdleWithoutStation;
        }
    }
}
