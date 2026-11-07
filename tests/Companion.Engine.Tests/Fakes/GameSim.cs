using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;

namespace Companion.EngineNav.Testing;

/// <summary>
/// A simulated game behind the IGameIo contract (no window is ever touched). Keys and clicks are recorded in
/// <see cref="Calls"/> and, by default, behave like the proofs: Esc closes the top UI or logs 'hide top ui nil', B opens
/// the bag on a clean HUD, hud.bag opens the bag, the toggles flip the HUD, menu.ability opens the crafting view. Tests
/// script deviations with <see cref="KeyScript"/> / <see cref="ClickScript"/> (return null for the default behaviour).
/// </summary>
public sealed class GameSim : IGameIo
{
    private readonly FakeClock _clock;
    private readonly FakeLog _log;
    private TimeSpan _lastBatch = TimeSpan.MinValue;

    public GameSim(FakeClock clock, FakeLog log)
    {
        _clock = clock;
        _log = log;
        Id = new GameIdentity(0x10A2C, 4242, FakeClock.Epoch.AddHours(-1), @"D:\Games\TOS\TOS.exe");
        CurrentIdentity = Id;
        LastForegroundMono = clock.Monotonic - TimeSpan.FromHours(1);
    }

    public List<string> Calls { get; } = [];

    /// <summary>Each input call with the simulated elapsed time at which it was made.</summary>
    public List<(string Call, TimeSpan At)> Timeline { get; } = [];

    public List<KeyRequest> KeyRequests { get; } = [];

    public List<ClickRequest> ClickRequests { get; } = [];

    public List<DragRequest> DragRequests { get; } = [];

    /// <summary>Scripted drags (dequeued per call; return null for the default: a completed drag).</summary>
    public Queue<Func<DragRequest, DragReport?>> DragScript { get; } = new();

    /// <summary>Extra effect of a default drag (e.g. the list moved).</summary>
    public Action<DragRequest>? OnDrag { get; set; }

    public int Captures { get; private set; }

    public int Connects { get; private set; }

    public GameIdentity Id { get; }

    public GameIdentity? CurrentIdentity { get; set; }

    public bool Connected { get; set; } = true;

    public HudState Hud { get; set; } = HudState.Collapsed;

    public bool Foreground { get; set; }

    /// <summary>
    /// Foreground mode (SPEC 7.3b): with the game in front, readiness is Ready in that mode (unless
    /// <see cref="ForegroundNotReady"/>) and the default inputs send no focus messages and have no press block.
    /// </summary>
    public bool ForegroundMode { get; set; }

    /// <summary>Foreground mode: the reason readiness reports instead of Ready (e.g. the user's recent input).</summary>
    public NotReadyReason ForegroundNotReady { get; set; }

    /// <summary>The user's last use of the game (the SinceGameUse clock), set when the user switches to it.</summary>
    public TimeSpan LastGameUseMono { get; set; }

    /// <summary>Foreground mode: the default inputs report the user's own input during them.</summary>
    public bool UserInputDuringInputs { get; set; }

    private bool InFront => ForegroundMode && Foreground && !Iconic;

    public bool Iconic { get; set; }

    public bool Exists { get; set; } = true;

    public int ExtraWindows { get; set; }

    public GameWindowPrefs Prefs { get; set; } = new(3, 0, 0, 0, 1920, 1009, 1, 1);

    public TimeSpan LastForegroundMono { get; set; }

    public Func<ReadinessQuery, ReadinessReport?>? ReadinessOverride { get; set; }

    public Queue<Func<KeyRequest, KeyReport?>> KeyScript { get; } = new();

    public Queue<Func<ClickRequest, ClickReport?>> ClickScript { get; } = new();

    /// <summary>When false, B does nothing (press registered, no UI).</summary>
    public bool BOpensBag { get; set; } = true;

    /// <summary>Extra effect after the default Esc behaviour.</summary>
    public Action? AfterEsc { get; set; }

    public int Keys(GameKey key) => Calls.Count(c => c == "key:" + key);

    public int ClickCount => Calls.Count(c => c.StartsWith("click:", StringComparison.Ordinal));

    public int DragCount => Calls.Count(c => c.StartsWith("drag:", StringComparison.Ordinal));

    public int Inputs => Calls.Count;

    /// <summary>Makes the game foreground now (the user switched to it).</summary>
    public void UserActivates()
    {
        Foreground = true;
        LastForegroundMono = _clock.Monotonic;
        LastGameUseMono = _clock.Monotonic;
    }

    /// <summary>The user switches away (cooldown starts now).</summary>
    public void UserLeaves()
    {
        Foreground = false;
        LastForegroundMono = _clock.Monotonic;
        LastGameUseMono = _clock.Monotonic; // leaving the game is the last moment it could have been used
    }

    /// <summary>The game was opened again: the next Connect finds its new window (incident 28).</summary>
    public bool NewWindowOnConnect { get; set; }

    public ConnectResult Connect(CancellationToken ct)
    {
        Connects++;
        Connected = true;
        if (NewWindowOnConnect)
        {
            Exists = true;
        }

        return new ConnectResult(ConnectProblem.None, Id, Prefs, null);
    }

    public GameIdentity? Identity => Connected ? Id : null;

    public GameWindowState GetState()
    {
        var mon = new MonitorInfo(@"\\.\DISPLAY2", new RectI(0, 0, 1920, 1080), new RectI(0, 0, 1920, 1040), true, 96);
        var rect = new RectI(-8, -8, 1928, 1040);
        return new GameWindowState(Exists && CurrentIdentity == Id, CurrentIdentity, rect, new PointI(0, 23), new SizeI(1920, 1009), true, Iconic,
            Foreground, false, false, true, mon, [mon], ExtraWindows, 0x409, new RectI(0, 0, 1920, 1080), true);
    }

    public GameWindowPrefs ReadWindowPrefs() => Prefs;

    public ReadinessReport CheckReadiness(ReadinessQuery query)
    {
        if (ReadinessOverride?.Invoke(query) is { } r)
        {
            return r;
        }
        var since = Foreground ? TimeSpan.Zero : _clock.Monotonic - LastForegroundMono;
        if (InFront)
        {
            return Report(ForegroundNotReady, since) with { Mode = InputMode.Foreground, SinceGameUse = _clock.Monotonic - LastGameUseMono };
        }

        var reason = Iconic ? NotReadyReason.GameIconic
            : Foreground ? NotReadyReason.GameForeground
            : since < HardRules.CooldownAfterGameForeground ? NotReadyReason.Cooldown
            : NotReadyReason.None;
        return Report(reason, since);
    }

    public ReadinessReport Report(NotReadyReason reason, TimeSpan? sinceForeground = null) =>
        new(reason == NotReadyReason.None, reason, new PointI(918, 672), true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), null, null,
            sinceForeground ?? (_clock.Monotonic - LastForegroundMono));

    public ResponsivenessReport Probe() => new(true, false, TimeSpan.FromMilliseconds(5), true, 0);

    public KeyReport PressKey(KeyRequest request, CancellationToken ct)
    {
        Calls.Add("key:" + request.Key);
        Timeline.Add(("key:" + request.Key, _clock.Elapsed));
        KeyRequests.Add(request);
        _lastBatch = _clock.Monotonic;
        if (KeyScript.Count > 0 && KeyScript.Dequeue()(request) is { } scripted)
        {
            return scripted;
        }
        return SentKey(request, () => DefaultKeyEffect(request.Key));
    }

    public void DefaultKeyEffect(GameKey key)
    {
        if (key == GameKey.Escape)
        {
            _log.HideTop(_log.State.AdvisoryTop);
            AfterEsc?.Invoke();
        }
        else if (key == GameKey.B && BOpensBag && _log.State.AdvisoryTop is null)
        {
            _log.Show("UI_BAG_MAIN");
        }
    }

    /// <summary>A delivered key batch: focus, KEYDOWN, effect, KEYUP, unfocus.</summary>
    public KeyReport SentKey(KeyRequest request, Action? effect, bool focusLogged = true)
    {
        var focusPos = _log.Position;
        var focusUtc = _clock.UtcNow;
        _clock.Advance(TimeSpan.FromMilliseconds(350));
        var actionPos = _log.Position;
        var actionUtc = _clock.UtcNow;
        _clock.Advance(TimeSpan.FromMilliseconds(80));
        effect?.Invoke();
        _clock.Advance(TimeSpan.FromMilliseconds(300));
        var end = _log.Position;
        if (InFront)
        {
            var fgMarks = new InputMarks(focusPos, focusUtc, actionPos, actionUtc, actionUtc.AddMilliseconds(70), null, null, null, end);
            return new KeyReport(InputDisposition.Sent, AbortReason.None, 0, false, false, fgMarks, true, InputMode.Foreground, UserInputDuringInputs);
        }

        var marks = new InputMarks(focusPos, focusUtc, actionPos, actionUtc, actionUtc.AddMilliseconds(70), end, _clock.UtcNow, null, end);
        return new KeyReport(InputDisposition.Sent, AbortReason.None, 4, focusLogged, false, marks, true);
    }

    /// <summary>A key batch that did not deliver.</summary>
    public KeyReport NotDeliveredKey(InputDisposition d, AbortReason abort, int focusMessages)
    {
        var pos = _log.Position;
        var marks = new InputMarks(focusMessages > 0 ? pos : null, focusMessages > 0 ? _clock.UtcNow : null, null, null, null, null, null, null, pos);
        _clock.Advance(TimeSpan.FromMilliseconds(focusMessages > 0 ? 400 : 1));
        return new KeyReport(d, abort, focusMessages, false, false, marks, false);
    }

    public ClickReport SlideClick(ClickRequest request, CancellationToken ct)
    {
        Calls.Add("click:" + request.TemplateId);
        Timeline.Add(("click:" + request.TemplateId, _clock.Elapsed));
        ClickRequests.Add(request);
        _lastBatch = _clock.Monotonic;
        if (ClickScript.Count > 0 && ClickScript.Dequeue()(request) is { } scripted)
        {
            return scripted;
        }
        return SentClick(request, () => DefaultClickEffect(request.TemplateId));
    }

    public void DefaultClickEffect(string templateId)
    {
        switch (templateId)
        {
            case "hud.bag":
                _log.Show("UI_BAG_MAIN");
                break;
            case "hud.toggle.collapsed":
                Hud = HudState.Expanded;
                break;
            case "hud.toggle.expanded":
                Hud = HudState.Collapsed;
                break;
            case "menu.ability":
                _log.Show("UI_LIFESKILL_MAKE_VIEW");
                break;
        }
    }

    /// <summary>A delivered click (DOWN/UP posted, unfocus while slid, press block at the aim unless told otherwise).</summary>
    public ClickReport SentClick(ClickRequest request, Action? effect, bool pressBlock = true, string objectName = "Label_Text",
        bool movedInP4 = false, bool forcedSlideBack = false, PointI? raycastAt = null, WindowIncident? incident = null,
        InputDisposition disposition = InputDisposition.Sent, bool focusLogged = true)
    {
        var focusPos = _log.Position;
        var focusUtc = _clock.UtcNow;
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        var actionPos = _log.Position;
        var actionUtc = _clock.UtcNow;
        _clock.Advance(TimeSpan.FromMilliseconds(100));
        effect?.Invoke();
        _clock.Advance(TimeSpan.FromMilliseconds(300));
        PressBlockLogEvent? block = null;
        if (pressBlock && !InFront)
        {
            block = _log.PressBlock(objectName, raycastAt ?? request.ClientPoint);
        }
        _clock.Advance(TimeSpan.FromMilliseconds(100));
        var end = _log.Position;
        var marks = new InputMarks(focusPos, focusUtc, actionPos, actionUtc, actionUtc.AddMilliseconds(70), actionPos, actionUtc.AddMilliseconds(400), _clock.UtcNow, end);
        var cursor = new CursorEvidence(new PointI(918, 672), false, movedInP4, false, false, false);
        var plan = new SlidePlan(new RectI(-8, -8, 1928, 1040), new RectI(-838, 327, 1098, 1375), -830, 335, @"\\.\DISPLAY2", @"\\.\DISPLAY2", true, 0);
        var slide = new SlideEvidence(plan, "Chrome_WidgetWin_1", TimeSpan.FromMilliseconds(840), TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(3),
            forcedSlideBack, true, new WatchdogStats(400, 1.0, 1.9, 2.5));
        var verification = new MatchResult(request.TemplateId, true, request.ClientPoint, 0.95, 0.5, 0, MatchFailure.None);
        if (InFront)
        {
            // foreground mode: no focus messages, no unfocus, no press block; the user's input is reported (SPEC 7.3b)
            var fgMarks = marks with { UnfocusSent = null, UnfocusSentUtc = null };
            var fgCursor = cursor with { UserInput = UserInputDuringInputs };
            return new ClickReport(disposition, AbortReason.None, InputPhase.Completed, 0, false, true, true, false, false, fgMarks, fgCursor,
                slide with { CoverClass = "UnityWndClass" }, verification, false, null, incident, InputMode.Foreground);
        }

        return new ClickReport(disposition, AbortReason.None, InputPhase.Completed, 4, focusLogged, true, true, true, false, marks, cursor, slide,
            verification, false, block, incident);
    }

    public DragReport SlideDrag(DragRequest request, CancellationToken ct)
    {
        Calls.Add("drag:" + request.TemplateId);
        Timeline.Add(("drag:" + request.TemplateId, _clock.Elapsed));
        DragRequests.Add(request);
        _lastBatch = _clock.Monotonic;
        if (DragScript.Count > 0 && DragScript.Dequeue()(request) is { } scripted)
        {
            return scripted;
        }
        return SentDrag(request, stepsDone: null);
    }

    /// <summary>A delivered drag: DOWN at the start, the steps, UP at the release point, unfocus while slid, slide back.</summary>
    public DragReport SentDrag(DragRequest request, int? stepsDone, bool movedAfterDown = false, bool forced = false)
    {
        var steps = (Math.Max(Math.Abs(request.Delta.X), Math.Abs(request.Delta.Y)) + HardRules.DragStepPx - 1) / HardRules.DragStepPx;
        var done = Math.Clamp(stepsDone ?? steps, 0, steps);
        var focusPos = _log.Position;
        var focusUtc = _clock.UtcNow;
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        var actionPos = _log.Position;
        var actionUtc = _clock.UtcNow;
        _clock.Advance(TimeSpan.FromMilliseconds(120 + (45 * done) + 250));
        OnDrag?.Invoke(request);
        _clock.Advance(TimeSpan.FromMilliseconds(300));
        var end = _log.Position;
        var marks = new InputMarks(focusPos, focusUtc, actionPos, actionUtc, _clock.UtcNow, actionPos, _clock.UtcNow, _clock.UtcNow, end);
        var cursor = new CursorEvidence(new PointI(918, 672), false, false, movedAfterDown, false, false);
        var plan = new SlidePlan(new RectI(-8, -8, 1928, 1040), new RectI(-838, 327, 1098, 1375), -830, 335, @"\\.\DISPLAY2", @"\\.\DISPLAY2", true, 0);
        var slide = new SlideEvidence(plan, "Chrome_WidgetWin_1", TimeSpan.FromMilliseconds(1600), TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(3),
            forced, true, new WatchdogStats(800, 1.0, 1.9, 2.5));
        var verification = new MatchResult(request.TemplateId, true, request.TemplateCenter, 0.95, 0.5, 0, MatchFailure.None);
        var abort = done == steps && !movedAfterDown && !forced ? AbortReason.None : AbortReason.CursorMoved;
        var press = new ClickReport(InputDisposition.Sent, abort, abort == AbortReason.None ? InputPhase.Completed : InputPhase.P5DownToUp, 4, true, true, true,
            abort == AbortReason.None, false, marks, cursor, slide, verification, false, null, null);
        var release = new PointI(request.Start.X + (int)((long)request.Delta.X * done / Math.Max(1, steps)), request.Start.Y + (int)((long)request.Delta.Y * done / Math.Max(1, steps)));
        return new DragReport(press, steps, done, release);
    }

    /// <summary>A drag that did not deliver (no DOWN).</summary>
    public DragReport NotDeliveredDrag(DragRequest request, InputDisposition d, AbortReason abort, int focusMessages) =>
        new(NotDeliveredClick(d, abort, InputPhase.P0Ready, focusMessages), 1, 0, request.Start);

    /// <summary>A click that did not deliver (no DOWN).</summary>
    public ClickReport NotDeliveredClick(InputDisposition d, AbortReason abort, InputPhase phase, int focusMessages, WindowIncident? incident = null)
    {
        var pos = _log.Position;
        var marks = new InputMarks(focusMessages > 0 ? pos : null, focusMessages > 0 ? _clock.UtcNow : null, null, null, null, null, null, null, pos);
        _clock.Advance(TimeSpan.FromMilliseconds(focusMessages > 0 ? 500 : 1));
        var cursor = new CursorEvidence(new PointI(918, 672), abort == AbortReason.CursorMoved, false, false, false, false);
        return new ClickReport(d, abort, phase, focusMessages, focusMessages > 0, false, false, false, false, marks, cursor, null, null, false, null, incident);
    }

    public CaptureResult Capture(CancellationToken ct)
    {
        Captures++;
        if (Iconic)
        {
            return new CaptureResult(CaptureProblem.Iconic, null, TimeSpan.FromMilliseconds(1));
        }
        _clock.Advance(TimeSpan.FromMilliseconds(30));
        var frame = new Frame(1920, 1009, 1920 * 4, [], _clock.Monotonic, _clock.UtcNow, _log.Position, new PointI(0, 23), false);
        return new CaptureResult(CaptureProblem.None, frame, TimeSpan.FromMilliseconds(30));
    }

    public HomeCheck CheckHome() => new(true, new RectI(-8, -8, 1928, 1040), new RectI(-8, -8, 1928, 1040), DeviationOwner.None);

    public RestoreReport RecoverLeftoverDisplacement() => new(false, true, RestoreRung.None, null);

    public RestoreReport RestoreHomeIfOurs() => new(false, true, RestoreRung.None, null);

    public PacingStatus Pacing
    {
        get
        {
            var next = _lastBatch == TimeSpan.MinValue ? TimeSpan.Zero : _lastBatch + HardRules.MinGapBetweenInputBatches - _clock.Monotonic;
            return new PacingStatus(next < TimeSpan.Zero ? TimeSpan.Zero : next, 0, 0, 0, 0);
        }
    }
}

/// <summary>Vision fake: HUD state from the simulation, templates found at their expected centre unless scripted.</summary>
public sealed class FakeVision(GameSim sim, FakeLog log, FakeTemplates templates, FakeClassifier classifier) : IVision
{
    /// <summary>Per-template scripted results (dequeued per Locate call); empty = found at the expected centre.</summary>
    public Dictionary<string, Queue<MatchResult>> LocateScript { get; } = new(StringComparer.Ordinal);

    /// <summary>Persistent per-template failure (until removed).</summary>
    public Dictionary<string, MatchFailure> LocateFailure { get; } = new(StringComparer.Ordinal);

    public Queue<HudState> HudScript { get; } = new();

    public HudState? HudOverride { get; set; }

    public int Locates { get; private set; }

    public int Encodes { get; private set; }

    public MatchResult Locate(Frame frame, string templateId)
    {
        Locates++;
        if (LocateScript.TryGetValue(templateId, out var q) && q.Count > 0)
        {
            return q.Dequeue();
        }
        var def = templates.Get(templateId);
        if (def is null)
        {
            return new MatchResult(templateId, false, default, 0, 0, 0, MatchFailure.TemplateMissing);
        }
        if (LocateFailure.TryGetValue(templateId, out var f))
        {
            return new MatchResult(templateId, false, f == MatchFailure.OutsideRadius ? def.ExpectedCenter.Offset(60, 0) : default, 0.4, 0.3, f == MatchFailure.OutsideRadius ? 60 : 0, f);
        }
        return new MatchResult(templateId, true, def.ExpectedCenter, 0.95, 0.5, 0, MatchFailure.None);
    }

    public MatchResult VerifyAt(Frame frame, string templateId, PointI expectedCenter, int tolerancePx) =>
        new(templateId, true, expectedCenter, 0.95, 0.5, 0, MatchFailure.None);

    public HudClassification ClassifyHud(Frame frame)
    {
        HudState state;
        if (HudScript.Count > 0)
        {
            state = HudScript.Dequeue();
        }
        else if (HudOverride is { } o)
        {
            state = o;
        }
        else if (log.State.AdvisoryStack.Any(n => classifier.Classify(n) == Contracts.Safety.UiKind.Stack))
        {
            state = HudState.Covered;
        }
        else
        {
            state = sim.Hud;
        }
        var sig = state is HudState.Collapsed or HudState.Expanded;
        return new HudClassification(state, state == HudState.Collapsed ? 0.95 : 0.2, state == HudState.Expanded ? 0.93 : 0.2, sig, sig, "fake." + state);
    }

    public FrameQuality Assess(Frame frame) => FrameQuality.Ok;

    public double RegionChange(Frame a, Frame b, RectI region) => 0;

    public GrayImage Crop(Frame frame, RectI region, double scale = 1) => new(1, 1, [0]);

    public byte[] EncodePng(Frame frame, RectI region, IReadOnlyList<RectI> blackout)
    {
        Encodes++;
        return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 1, 2, 3, 4];
    }
}
