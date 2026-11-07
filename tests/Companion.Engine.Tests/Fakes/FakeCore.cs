using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Host;
using Companion.Contracts.Logs;
using Companion.Contracts.Perception;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;

namespace Companion.EngineNav.Testing;

/// <summary>Fake clock: Sleep advances time instantly; timers fire as time passes; a hard limit stops runaway loops.</summary>
public sealed class FakeClock : IClock
{
    public static readonly DateTimeOffset Epoch = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);
    private readonly List<(TimeSpan At, int Seq, Action Act)> _timers = [];
    private readonly TimeSpan _start = TimeSpan.FromSeconds(1000);
    private int _seq;

    public FakeClock()
    {
        Monotonic = _start;
        UtcNow = Epoch;
    }

    public DateTimeOffset UtcNow { get; private set; }

    public TimeSpan Monotonic { get; private set; }

    /// <summary>Simulated time limit (a runaway engine loop ends with an exception, seen as InternalError).</summary>
    public TimeSpan Limit { get; set; } = TimeSpan.FromHours(6);

    public TimeSpan Elapsed => Monotonic - _start;

    public int Sleeps { get; private set; }

    public DateTime GameNow => UtcNow.ToLocalTime().DateTime;

    public void Sleep(TimeSpan duration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Sleeps++;
        Advance(duration < TimeSpan.Zero ? TimeSpan.Zero : duration);
        ct.ThrowIfCancellationRequested();
    }

    public void Advance(TimeSpan d)
    {
        Monotonic += d;
        UtcNow += d;
        if (Elapsed > Limit)
        {
            throw new InvalidOperationException("fake clock limit reached");
        }
        RunTimers();
    }

    /// <summary>Wall clock jump (sleep/resume) without monotonic change.</summary>
    public void JumpWall(TimeSpan d) => UtcNow += d;

    /// <summary>Runs <paramref name="action"/> once simulated time passes now + <paramref name="fromNow"/>.</summary>
    public void At(TimeSpan fromNow, Action action) => _timers.Add((Monotonic + fromNow, _seq++, action));

    private void RunTimers()
    {
        while (true)
        {
            var due = _timers.Where(t => t.At <= Monotonic).OrderBy(t => t.At).ThenBy(t => t.Seq).FirstOrDefault();
            if (due.Act is null)
            {
                return;
            }
            _timers.Remove(due);
            due.Act();
        }
    }
}

/// <summary>UI classification from the hard-coded catalog (precedence of SPEC 6.2).</summary>
public sealed class FakeClassifier : IUiClassifier
{
    public HashSet<string> LearnedStack { get; } = new(StringComparer.Ordinal);

    public UiKind Classify(string uiName)
    {
        var n = uiName.ToUpperInvariant();
        if (UiCatalog.ForbiddenExact.Contains(n) || UiCatalog.ForbiddenPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal))
            || UiCatalog.ForbiddenContains.Any(c => n.Contains(c, StringComparison.Ordinal)))
        {
            return UiKind.Forbidden;
        }
        if (UiCatalog.SocialExact.Contains(n))
        {
            return UiKind.Social;
        }
        if (UiCatalog.BusyExact.Contains(n) || UiCatalog.BusyPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
        {
            return UiKind.Busy;
        }
        if (UiCatalog.StationExact.Contains(n))
        {
            return UiKind.Station;
        }
        if (UiCatalog.StackSeed.Contains(n) || LearnedStack.Contains(n))
        {
            return UiKind.Stack;
        }
        if (UiCatalog.OverlaySeed.Contains(n))
        {
            return UiKind.Overlay;
        }
        return UiKind.Unknown;
    }

    public bool IsEscSafe(string uiName) => UiCatalog.EscSafe.Contains(uiName);

    public bool IsLearnedStack(string uiName) => LearnedStack.Contains(uiName);
}

/// <summary>Scripted log: events appended by the game simulation or by the test; UI model per SPEC 6.2 (simplified).</summary>
public sealed class FakeLog : ILogTracker
{
    private readonly FakeClock _clock;
    private readonly IUiClassifier _classifier;
    private readonly List<LogEvent> _events = [];
    private long _offset = 1000;
    private bool _rotatedPending;

    public FakeLog(FakeClock clock, IUiClassifier classifier)
    {
        _clock = clock;
        _classifier = classifier;
        File = new LogFileIdentity(DateTime.UnixEpoch, 1, "head-1");
    }

    public LogFileIdentity File { get; private set; }

    public BusyKind Busy { get; set; } = BusyKind.None;

    public bool GameAvailable { get; set; } = true;

    public int? AutoCollect { get; set; } = 0;

    public DateTime? LastThrottle { get; set; }

    public DateTime? LastCombatOverride { get; set; }

    public CraftSessionInfo Craft { get; set; } = CraftSessionInfo.None;

    public HashSet<string> Shown { get; } = new(StringComparer.Ordinal) { "UI_BAG_MAIN", "UI_LIFESKILL_MAKE_VIEW", "UI_HOME_PET_VIEW", "UI_MAIL" };

    /// <summary>When set, the log stopped growing at this wall time.</summary>
    public DateTimeOffset? QuietSince { get; set; }

    public int Polls { get; private set; }

    public IReadOnlyList<LogEvent> All => _events;

    private DateTime Now => _clock.GameNow;

    private LogPosition Next(out long start)
    {
        start = _offset;
        _offset += 100;
        return new LogPosition(File, _offset);
    }

    public UiLogEvent Ui(UiVerb verb, string? name)
    {
        var pos = Next(out var start);
        var e = new UiLogEvent(pos, start, Now, verb, name, null);
        _events.Add(e);
        if (name is not null && verb is UiVerb.Show or UiVerb.OpenConst)
        {
            Shown.Add(name);
        }
        return e;
    }

    public void Show(string name) => Ui(UiVerb.Show, name);

    public void HideTop(string? name) => Ui(UiVerb.HideTop, name);

    public PressBlockLogEvent PressBlock(string objectName, PointI client)
    {
        var pos = Next(out var start);
        var e = new PressBlockLogEvent(pos, start, Now, objectName, "Canvas", new PointD(client.X, HardRules.ExpectedClientHeight - 1 - client.Y));
        _events.Add(e);
        return e;
    }

    public void Focus(bool recovered)
    {
        var pos = Next(out var start);
        _events.Add(new FocusLogEvent(pos, start, Now, recovered));
    }

    public void Jank(double seconds)
    {
        var pos = Next(out var start);
        _events.Add(new JankLogEvent(pos, start, Now, seconds));
    }

    public void Pay()
    {
        var pos = Next(out var start);
        _events.Add(new PayLogEvent(pos, start, Now));
    }

    public void GuideBan()
    {
        var pos = Next(out var start);
        _events.Add(new GuideBanLogEvent(pos, start, Now, false));
    }

    public void Scene(ScenePhase phase)
    {
        var pos = Next(out var start);
        _events.Add(new SceneLoadLogEvent(pos, start, Now, phase));
    }

    /// <summary>New log generation (game restarted): model reset, Poll reports Rotated once.</summary>
    public void Rotate()
    {
        File = new LogFileIdentity(DateTime.UnixEpoch.AddDays(1), 2, "head-2");
        _offset = 0;
        _events.Clear();
        _rotatedPending = true;
    }

    public LogBootstrapReport Bootstrap(CancellationToken ct) => new(true, File, _offset, AutoCollect, null, new SizeI(1920, 1009), true, TimeSpan.Zero);

    public LogPollResult Poll()
    {
        Polls++;
        var rotated = _rotatedPending;
        _rotatedPending = false;
        return new LogPollResult([], Position, rotated, LogReadProblem.None);
    }

    public LogPosition? Position => new(File, _offset);

    public IReadOnlyList<LogEvent> EventsSince(LogPosition from) =>
        from.File != File ? [] : _events.Where(e => e.Position.Offset > from.Offset).ToList();

    public IReadOnlyList<LogEvent> EventsBetween(LogPosition from, LogPosition to) =>
        from.File != File || to.File != File ? [] : _events.Where(e => e.Position.Offset > from.Offset && e.Position.Offset <= to.Offset).ToList();

    public UiStateSnapshot State
    {
        get
        {
            var stack = new List<string>();
            LogPosition? lastNil = null;
            DateTime? combat = LastCombatOverride;
            DateTime? guide = null;
            DateTime? pay = null;
            foreach (var e in _events)
            {
                switch (e)
                {
                    case UiLogEvent u when u.Verb is UiVerb.Show or UiVerb.OpenConst && u.UiName is { } n:
                        var kind = _classifier.Classify(n);
                        if (kind is UiKind.Stack or UiKind.Forbidden or UiKind.Social or UiKind.Unknown or UiKind.Station)
                        {
                            stack.Remove(n);
                            stack.Add(n);
                        }
                        if (UiCatalog.CombatIndicators.Contains(n))
                        {
                            combat = u.Timestamp;
                        }
                        break;
                    case UiLogEvent u when u.Verb == UiVerb.HideTop && u.UiName is null && u.NumericId is null:
                        stack.Clear();
                        lastNil = u.Position;
                        break;
                    case UiLogEvent u when u.UiName is { } n2 && u.Verb is UiVerb.HideTop or UiVerb.AddToCloseList or UiVerb.StackOverRemove
                        or UiVerb.DisposeAllClose or UiVerb.CheckCleanDispose or UiVerb.ToHide:
                        stack.Remove(n2);
                        break;
                    case SceneLoadLogEvent s when s.Phase == ScenePhase.StartLoad:
                        stack.Clear();
                        break;
                    case GuideBanLogEvent g:
                        guide = g.Timestamp;
                        break;
                    case PayLogEvent p:
                        pay = p.Timestamp;
                        break;
                }
            }
            return new UiStateSnapshot(Position, GameAvailable, Busy, stack, lastNil, FocusState.Unknown, null, AutoCollect, null,
                new SizeI(1920, 1009), null, LastThrottle, combat, null, guide, pay, Craft, Shown.ToList());
        }
    }

    public LogHealth Health => new(QuietSince ?? _clock.UtcNow, _offset, true, 0, _clock.UtcNow);

    public IReadOnlyList<string> ReadFilteredExcerpt(LogPosition from, LogPosition to, int maxLines) =>
        ["2026-10-14 10:00:00.000\tLog\t[LOG] -> module/foundation/ui/ui_mgr.lua:1    [ui mgr]hide top ui    nil"];
}

/// <summary>Templates of SPEC 14 (two verified, two provisional) plus extra test templates.</summary>
public sealed class FakeTemplates : ITemplateCatalog
{
    private readonly Dictionary<string, TemplateDefinition> _defs = new(StringComparer.Ordinal);

    public FakeTemplates()
    {
        Add(Def("hud.bag", 1748, 312, TemplateStatus.Verified, 1, null, TargetTags.Nav | TargetTags.HudAnchor | TargetTags.NavFixed));
        Add(Def("hud.toggle.collapsed", 1855, 318, TemplateStatus.Verified, 1, HudState.Collapsed, TargetTags.Toggle | TargetTags.HudAnchor | TargetTags.NavFixed));
        Add(Def("hud.toggle.expanded", 1855, 320, TemplateStatus.Provisional, 2, HudState.Expanded, TargetTags.Toggle | TargetTags.HudAnchor | TargetTags.NavFixed));
        Add(Def("menu.ability", 1734, 448, TemplateStatus.Provisional, 2, HudState.Expanded, TargetTags.Nav | TargetTags.NavFixed));
    }

    public HashSet<string> Suspect { get; } = new(StringComparer.Ordinal);

    public List<string> SuspectCalls { get; } = [];

    public static TemplateDefinition Def(string id, int x, int y, TemplateStatus status, int scale, HudState? hud, TargetTags tags, AutomationSwitch gate = AutomationSwitch.None) =>
        new(id, id + ".png", null, null, new PointI(x, y), 24, 0.9, 0.1, status, scale, hud, tags, FireOn.Down, gate, null, null, "test", null);

    public void Add(TemplateDefinition d) => _defs[d.Id] = d;

    public void Verify(string id) => _defs[id] = _defs[id] with { Status = TemplateStatus.Verified, SourceScale = 1 };

    public TemplateDefinition? Get(string id) => id is not null && _defs.TryGetValue(id, out var d) ? d : null;

    public IReadOnlyCollection<TemplateDefinition> All => _defs.Values.ToList();

    public bool IsClickable(string id) =>
        Get(id) is { Status: TemplateStatus.Verified, SourceScale: 1 } d && !Suspect.Contains(id) && !d.Tags.HasFlag(TargetTags.Forbidden);

    public void MarkSuspect(string id, string reasonCode)
    {
        Suspect.Add(id);
        SuspectCalls.Add(id);
    }

    public bool IsSuspect(string id) => Suspect.Contains(id);
}

/// <summary>Seed readers (all NeedsSurvey) plus one ready test reader.</summary>
public sealed class FakeReaders : IReaderCatalog
{
    private readonly Dictionary<string, ReaderDefinition> _defs = new(StringComparer.Ordinal);

    public FakeReaders()
    {
        foreach (var (id, ui) in new[] { ("bag.grid", "UI_BAG_MAIN"), ("craft.pairs", "UI_LIFESKILL_MAKE_VIEW"), ("craft.quantity", "UI_LIFESKILL_MAKE_VIEW"), ("lumi.products", "UI_HOME_PET_VIEW") })
        {
            _defs[id] = new ReaderDefinition(id, new ScreenIdentity(ui, []), [], ReaderStatus.NeedsSurvey, "needsSurvey");
        }
        _defs["test.ready"] = new ReaderDefinition("test.ready", new ScreenIdentity("UI_BAG_MAIN", []), [], ReaderStatus.Ready, null);
    }

    public ReaderDefinition? Get(string id) => id is not null && _defs.TryGetValue(id, out var d) ? d : null;

    public IReadOnlyCollection<ReaderDefinition> All => _defs.Values.ToList();

    /// <summary>Adds or replaces a reader definition as given.</summary>
    public void Put(ReaderDefinition d) => _defs[d.Id] = d;

    /// <summary>Adds or replaces a Ready reader (a surveyed one, e.g. the Make-tab readers of 2026-10-14).</summary>
    public void SetReady(string id, string logUi) => _defs[id] = new ReaderDefinition(id, new ScreenIdentity(logUi, []), [], ReaderStatus.Ready, null);
}

public sealed class FakeScreenReader(FakeClock clock) : IScreenReader
{
    public ReadStatus Next { get; set; } = ReadStatus.Ok;

    /// <summary>Values to return instead of the single fake value (null = the default).</summary>
    public IReadOnlyList<ReadValue>? Values { get; set; }

    public int Calls { get; private set; }

    public ScreenReadResult Read(ReaderDefinition reader, Frame frame, UiStateSnapshot ui)
    {
        Calls++;
        return new ScreenReadResult(reader.Id, Next, Values ?? [new ReadValue("r1", Next, 5, null, null, null, 0.9, "fake")], frame.LogAt, clock.UtcNow);
    }
}

/// <summary>Records journal records and lints them (every value must be in journal form).</summary>
public sealed class FakeJournal : IJournal
{
    public List<JournalRecord> Records { get; } = [];

    public int Flushes { get; private set; }

    public void Write(JournalRecord record)
    {
        lock (Records)
        {
            Records.Add(record);
        }
    }

    public void Flush() => Flushes++;

    public IEnumerable<JournalRecord> OfKind(JournalKind kind) => Records.Where(r => r.Kind == kind);

    public static string? Str(JournalRecord r, string key) => r.Fields.TryGetValue(key, out var v) ? v?.ToString() : null;
}

public sealed class FakeArtifacts : IArtifactStore
{
    public List<(ArtifactCategory Category, string Label, int Bytes)> Images { get; } = [];

    public List<(ArtifactCategory Category, string Label, int Lines)> Excerpts { get; } = [];

    public string? SaveImage(ArtifactCategory category, string label, byte[] png)
    {
        Images.Add((category, label, png.Length));
        return @"C:\fake\" + category + @"\" + label.Replace('/', '\\') + ".png";
    }

    public string? SaveLogExcerpt(ArtifactCategory category, string label, IReadOnlyList<string> filteredLines)
    {
        Excerpts.Add((category, label, filteredLines.Count));
        return @"C:\fake\" + category + @"\" + label.Replace('/', '\\') + ".log";
    }
}

public sealed class FakeHaltLatch : IHaltLatch
{
    public List<HaltReport> Reports { get; } = [];

    public bool IsHalted => Current is not null;

    public HaltReport? Current { get; private set; }

    public void Halt(HaltReport report)
    {
        Reports.Add(report);
        Current = report;
    }

    public void Clear(string byCode) => Current = null;
}

public sealed class FakeNotifier : INotifier
{
    public List<Notification> Sent { get; } = [];

    public void Notify(Notification notification) => Sent.Add(notification);
}

public sealed class FakeSwitches : IAutomationSwitches
{
    public bool Crafting { get; set; }

    public bool Homestead { get; set; }

    public bool SlideClickEnabled { get; set; } = true;

    public bool IsOn(AutomationSwitch automationSwitch) => automationSwitch switch
    {
        AutomationSwitch.None => true,
        AutomationSwitch.Crafting => Crafting,
        AutomationSwitch.Homestead => Homestead,
        _ => false,
    };
}

public sealed class FakeConfig : IConfigProvider
{
    public CompanionConfig Current { get; set; } = CompanionConfig.Default;

    public void SetPolicy(PolicyConfig policy) => Current = Current with { Policy = policy };
}

public sealed class FakeTrust : IFileTrust
{
    public bool BuildTrusted { get; set; } = true;

    public byte[]? ReadTrusted(string fullPath) => null;
}

public sealed class FakeStop : IStopSwitch
{
    private readonly ManualResetEvent _handle = new(false);

    public bool IsStopRequested => Current is not null;

    public StopRequest? Current { get; private set; }

    public WaitHandle Handle => _handle;

    public void RequestStop(StopSource source, string reasonCode)
    {
        Current ??= new StopRequest(source, reasonCode, FakeClock.Epoch);
        _handle.Set();
    }

    public void Clear()
    {
        Current = null;
        _handle.Reset();
    }
}
