using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.GameIO.Geometry;
using Companion.GameIO.Native;
using Companion.GameIO.Safety;

namespace Companion.GameIO.Tests.Fakes;

/// <summary>Fake clock: Sleep advances time instantly and runs the hook (the manual watchdog samples there).</summary>
internal sealed class FakeClock : IClock
{
    private readonly object _lock = new();
    private TimeSpan _mono = TimeSpan.FromSeconds(1000);
    private DateTimeOffset _utc = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    public Action? OnSleep { get; set; }

    public Exception? ThrowOnNextSleep { get; set; }

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_lock)
            {
                return _utc;
            }
        }
    }

    public TimeSpan Monotonic
    {
        get
        {
            lock (_lock)
            {
                return _mono;
            }
        }
    }

    public void Sleep(TimeSpan duration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ThrowOnNextSleep is { } ex)
        {
            ThrowOnNextSleep = null;
            throw ex;
        }

        Advance(duration);
        OnSleep?.Invoke();
    }

    public void Advance(TimeSpan d)
    {
        if (d < TimeSpan.Zero)
        {
            d = TimeSpan.Zero;
        }

        lock (_lock)
        {
            _mono += d;
            _utc += d;
        }
    }
}

/// <summary>One recorded acting call that reached the (fake) Win32 layer.</summary>
internal sealed record Call(string Kind, long Hwnd, uint Msg, long WParam, long LParam, int X, int Y, int Cx, int Cy, uint Flags, TimeSpan At)
{
    public bool IsFocusOff => Kind is "send" or "notify" && ChokePoint.IsFocusOffMessage(Msg, WParam, LParam);

    public bool IsFocusOn => Kind == "send" && HardRules.FocusOnSequence.Contains(new FocusMessage(Msg, WParam, LParam));

    public override string ToString() => Kind switch
    {
        "swp" => $"swp({X},{Y},0x{Flags:X4})",
        "print" => "print",
        _ => $"{Kind}(0x{Msg:X},{WParam},{LParam})",
    };
}

internal sealed class FakeWindow
{
    public long Hwnd { get; init; }

    public int Pid { get; set; }

    public int ThreadId { get; set; } = 1;

    public string Class { get; set; } = "";

    public bool Visible { get; set; } = true;

    public bool Iconic { get; set; }

    public bool Zoomed { get; set; }

    public bool Hung { get; set; }

    public bool Cloaked { get; set; }

    public RectI Rect { get; set; }

    public SizeI Client { get; set; }

    public PointI ClientOffset { get; set; }

    public long Style { get; set; }

    public long ExStyle { get; set; }

    public byte? LayeredAlpha { get; set; } = 255;

    public RectI? Frame { get; set; }

    public long RootOwner { get; set; }

    /// <summary>Visible frame: explicit, or the rect (moves with the rect when not explicit).</summary>
    public RectI FrameBounds => Frame ?? Rect;
}

internal sealed record FakeProcess(string Name, string ExePath, DateTimeOffset StartUtc);

/// <summary>Fake Win32: window table, z-order, cursor, monitors, processes; records every acting call.</summary>
internal sealed class FakeWin32(FakeClock clock) : IWin32
{
    private readonly object _lock = new();
    private readonly List<Call> _calls = [];

    public Dictionary<long, FakeWindow> Windows { get; } = [];

    /// <summary>Top-level z-order, topmost first.</summary>
    public List<long> ZOrder { get; } = [];

    public long Foreground { get; set; }

    public PointI Cursor { get; set; }

    public bool CursorOk { get; set; } = true;

    public bool CursorShowing { get; set; } = true;

    public RectI? Clip { get; set; } = new RectI(-1920, 0, 1920, 1080);

    public HashSet<int> ButtonsDown { get; } = [];

    public TimeSpan Idle { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>GetLastInputInfo dwTime: a test simulates the user's keyboard or mouse input by changing it.</summary>
    public uint LastInputTick { get; set; } = 1000;

    public bool InputDesktop { get; set; } = true;

    public List<MonitorInfo> Monitors { get; } = [];

    public Dictionary<int, FakeProcess> Processes { get; } = [];

    public Dictionary<string, object> Registry { get; } = [];

    public List<string> RegistryReads { get; } = [];

    public bool Elevated { get; set; } = true;

    public bool PrintFails { get; set; }

    /// <summary>Pixels returned by PrintWindowClient (window rect, client origin, client size); null keeps the 16-byte stub.</summary>
    public Func<RectI, PointI, SizeI, byte[]?>? PrintPixels { get; set; }

    /// <summary>When set, SetWindowPos records the call but does not move the window.</summary>
    public bool FreezeWindows { get; set; }

    /// <summary>Per-message SendMessageTimeout behaviour (ok, lastError).</summary>
    public Func<uint, (bool Ok, int Err)>? SendBehavior { get; set; }

    public Func<uint, (bool Ok, int Err)>? PostBehavior { get; set; }

    /// <summary>Runs after every acting call (game reactions, scripted user actions).</summary>
    public Action<Call>? OnCall { get; set; }

    public long KeyboardLayout { get; set; } = 0x04090409;

    public bool Is64BitProcess => true;

    public int CurrentProcessId => 9999;

    public IReadOnlyList<Call> Calls
    {
        get
        {
            lock (_lock)
            {
                return _calls.ToList();
            }
        }
    }

    public void ClearCalls()
    {
        lock (_lock)
        {
            _calls.Clear();
        }
    }

    public FakeWindow Add(FakeWindow w)
    {
        Windows[w.Hwnd] = w;
        return w;
    }

    private FakeWindow? W(long h) => Windows.GetValueOrDefault(h);

    public IReadOnlyList<long> EnumTopLevelWindows() => ZOrder.ToList();

    public int GetWindowProcessId(long hwnd) => W(hwnd)?.Pid ?? 0;

    public int GetWindowThreadId(long hwnd) => W(hwnd)?.ThreadId ?? 0;

    public bool IsWindow(long hwnd) => W(hwnd) is not null;

    public bool IsWindowVisible(long hwnd) => W(hwnd)?.Visible ?? false;

    public bool IsIconic(long hwnd) => W(hwnd)?.Iconic ?? false;

    public bool IsZoomed(long hwnd) => W(hwnd)?.Zoomed ?? false;

    public bool IsHungAppWindow(long hwnd) => W(hwnd)?.Hung ?? false;

    public long GetForegroundWindow() => Foreground;

    public string GetClassName(long hwnd) => W(hwnd)?.Class ?? "";

    public RectI? GetWindowRect(long hwnd) => W(hwnd)?.Rect;

    public SizeI? GetClientSize(long hwnd) => W(hwnd)?.Client;

    public PointI? GetClientOrigin(long hwnd) => W(hwnd) is { } w ? new PointI(w.Rect.Left + w.ClientOffset.X, w.Rect.Top + w.ClientOffset.Y) : null;

    public long GetStyle(long hwnd) => W(hwnd)?.Style ?? 0;

    public long GetExStyle(long hwnd) => W(hwnd)?.ExStyle ?? 0;

    public byte? GetLayeredAlpha(long hwnd) => W(hwnd)?.LayeredAlpha;

    public long WindowFromPoint(PointI screen)
    {
        foreach (var h in ZOrder)
        {
            var w = W(h);
            if (w is { Visible: true, Iconic: false, Cloaked: false } && w.FrameBounds.Contains(screen))
            {
                return h;
            }
        }

        return 0;
    }

    public long GetAncestor(long hwnd, uint flags) =>
        flags == Win32Const.GA_ROOTOWNER && W(hwnd) is { RootOwner: not 0 } w ? w.RootOwner : hwnd;

    public long GetWindow(long hwnd, uint command)
    {
        var i = ZOrder.IndexOf(hwnd);
        if (command == Win32Const.GW_HWNDPREV)
        {
            return i > 0 ? ZOrder[i - 1] : 0;
        }

        return 0;
    }

    public bool IsCloaked(long hwnd) => W(hwnd)?.Cloaked ?? false;

    public RectI? GetFrameBounds(long hwnd) => W(hwnd)?.FrameBounds;

    public long GetKeyboardLayout(int threadId) => KeyboardLayout;

    public bool TryGetCursorPos(out PointI screen)
    {
        screen = Cursor;
        return CursorOk;
    }

    public bool TryGetCursorShowing(out bool showing)
    {
        showing = CursorShowing;
        return true;
    }

    public RectI? GetClipCursor() => Clip;

    public short GetAsyncKeyState(int virtualKey)
    {
        if (!StaticRules.GetAsyncKeyStateAllowedKeys.Contains(virtualKey))
        {
            throw new InvalidOperationException("fake: GetAsyncKeyState reached with a forbidden VK");
        }

        return ButtonsDown.Contains(virtualKey) ? unchecked((short)0x8000) : (short)0;
    }

    public TimeSpan? GetSystemIdle() => Idle;

    public uint? GetLastInputTick() => LastInputTick;

    public bool IsInputDesktopAvailable() => InputDesktop;

    public IReadOnlyList<MonitorInfo> EnumMonitors() => Monitors.ToList();

    public string? MonitorFromRect(RectI rect) => SlideGeometry.NearestMonitor(rect, Monitors)?.DeviceName;

    public string? MonitorFromWindow(long hwnd) => W(hwnd) is { } w ? SlideGeometry.NearestMonitor(w.Rect, Monitors)?.DeviceName : null;

    public string? MonitorFromPoint(PointI screen) => SlideGeometry.NearestMonitor(new RectI(screen.X, screen.Y, screen.X + 1, screen.Y + 1), Monitors)?.DeviceName;

    public int GetMaximizedFrameThickness() => 8;

    public IReadOnlyList<int> GetProcessIdsByName(string processName) =>
        Processes.Where(p => string.Equals(p.Value.Name, processName, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList();

    public DateTimeOffset? GetProcessStartUtc(int pid) => Processes.TryGetValue(pid, out var p) ? p.StartUtc : null;

    public string? QueryProcessImagePath(int pid, uint access)
    {
        if (access != StaticRules.OpenProcessAllowedAccess)
        {
            throw new InvalidOperationException("fake: OpenProcess reached with a forbidden access mask");
        }

        return Processes.TryGetValue(pid, out var p) ? p.ExePath : null;
    }

    public bool IsCurrentProcessElevated() => Elevated;

    public (long Size, DateTime WriteUtc)? GetFileMetadata(string path) => (1234, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

    public object? ReadGameRegistryValue(string valueName)
    {
        if (!GamePrefsReader.IsWhitelisted(valueName))
        {
            throw new InvalidOperationException("fake: non-whitelisted registry read");
        }

        RegistryReads.Add(valueName);
        return Registry.GetValueOrDefault(valueName);
    }

    public bool PostMessage(long hwnd, uint message, long wParam, long lParam, out int lastError)
    {
        Record(new Call("post", hwnd, message, wParam, lParam, 0, 0, 0, 0, 0, clock.Monotonic));
        var (ok, err) = PostBehavior?.Invoke(message) ?? (true, 0);
        lastError = ok ? 0 : err;
        return ok;
    }

    public bool SendMessageTimeout(long hwnd, uint message, long wParam, long lParam, uint flags, uint timeoutMs, out int lastError)
    {
        Record(new Call("send", hwnd, message, wParam, lParam, 0, 0, 0, 0, flags, clock.Monotonic));
        var (ok, err) = SendBehavior?.Invoke(message) ?? (true, 0);
        lastError = ok ? 0 : err;
        return ok;
    }

    public bool SendNotifyMessage(long hwnd, uint message, long wParam, long lParam, out int lastError)
    {
        Record(new Call("notify", hwnd, message, wParam, lParam, 0, 0, 0, 0, 0, clock.Monotonic));
        lastError = 0;
        return true;
    }

    public bool SetWindowPos(long hwnd, long insertAfter, int x, int y, int cx, int cy, uint flags, out int lastError)
    {
        if (!FreezeWindows && W(hwnd) is { } w)
        {
            w.Rect = (flags & HardRules.SWP_NOSIZE) != 0
                ? new RectI(x, y, x + w.Rect.Width, y + w.Rect.Height)
                : new RectI(x, y, x + cx, y + cy);
        }

        Record(new Call("swp", hwnd, 0, 0, 0, x, y, cx, cy, flags, clock.Monotonic));
        lastError = 0;
        return true;
    }

    public byte[]? PrintWindowClient(long hwnd, uint flags, RectI windowRect, PointI clientOrigin, SizeI clientSize)
    {
        Record(new Call("print", hwnd, 0, 0, 0, 0, 0, 0, 0, flags, clock.Monotonic));
        return PrintFails ? null : PrintPixels?.Invoke(windowRect, clientOrigin, clientSize) ?? new byte[16];
    }

    public IPreciseWaiter CreatePreciseWaiter() => new NoWait();

    private void Record(Call c)
    {
        lock (_lock)
        {
            _calls.Add(c);
        }

        OnCall?.Invoke(c);
    }

    private sealed class NoWait : IPreciseWaiter
    {
        public void Wait(TimeSpan duration)
        {
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>Fake log tracker: events appended by the scripted game; positions advance by 100 bytes per event.</summary>
internal sealed class FakeLogTracker(FakeClock clock) : ILogTracker
{
    private readonly object _lock = new();
    private readonly List<LogEvent> _events = [];
    private long _offset = 5000;

    public LogFileIdentity File { get; } = new(new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc), 7, "head");

    public Exception? ThrowOnPoll { get; set; }

    public UiStateSnapshot StateValue { get; set; } = new(
        null, true, BusyKind.None, [], null, FocusState.Lost, null, 0, null, new SizeI(1920, 1009), null, null, null, null, null, null,
        CraftSessionInfo.None, []);

    public int Polls { get; private set; }

    public LogPosition? Position
    {
        get
        {
            lock (_lock)
            {
                return new LogPosition(File, _offset);
            }
        }
    }

    public UiStateSnapshot State => StateValue;

    public LogHealth Health => new(clock.UtcNow, _offset, true, 0, clock.UtcNow);

    public LogBootstrapReport Bootstrap(CancellationToken ct) => new(true, File, _offset, 0, null, new SizeI(1920, 1009), true, TimeSpan.Zero);

    public LogPollResult Poll()
    {
        if (ThrowOnPoll is { } ex)
        {
            throw ex;
        }

        Polls++;
        return new LogPollResult([], Position, false, LogReadProblem.None);
    }

    public IReadOnlyList<LogEvent> EventsSince(LogPosition from)
    {
        lock (_lock)
        {
            return from.File != File ? [] : _events.Where(e => e.Position.Offset > from.Offset).ToList();
        }
    }

    public IReadOnlyList<LogEvent> EventsBetween(LogPosition from, LogPosition to)
    {
        lock (_lock)
        {
            return _events.Where(e => e.Position.Offset > from.Offset && e.Position.Offset <= to.Offset).ToList();
        }
    }

    public IReadOnlyList<string> ReadFilteredExcerpt(LogPosition from, LogPosition to, int maxLines) => [];

    public void Append(Func<LogPosition, long, DateTime, LogEvent> make)
    {
        lock (_lock)
        {
            var start = _offset;
            _offset += 100;
            _events.Add(make(new LogPosition(File, _offset), start, clock.UtcNow.LocalDateTime));
        }
    }

    public void Show(string ui) => Append((p, s, t) => new UiLogEvent(p, s, t, UiVerb.Show, ui, null));

    public void Focus(bool recovered) => Append((p, s, t) => new FocusLogEvent(p, s, t, recovered));

    public void PressBlock(string name, PointD screen) => Append((p, s, t) => new PressBlockLogEvent(p, s, t, name, "Canvas_Main", screen));

    public void Resolution(int w, int h) => Append((p, s, t) => new ResolutionLogEvent(p, s, t, ResolutionSource.CurrentScreen, new SizeI(w, h)));
}

internal sealed class FakeVision : IVision
{
    public MatchResult? VerifyResult { get; set; }

    public FrameQuality Quality { get; set; } = FrameQuality.Ok;

    /// <summary>Checked first by VerifyAt (frame, template, centre); null falls through to <see cref="VerifyResult"/> or found.</summary>
    public Func<Frame, string, PointI, MatchResult?>? VerifyHook { get; set; }

    public int VerifyCalls { get; private set; }

    /// <summary>Every VerifyAt call: while slid or not, the centre and the tolerance.</summary>
    public List<(bool WhileSlid, PointI Center, int Tolerance)> Verifies { get; } = [];

    public MatchResult Locate(Frame frame, string templateId) => new(templateId, true, new PointI(1748, 314), 0.97, 0.5, 0, MatchFailure.None);

    public MatchResult VerifyAt(Frame frame, string templateId, PointI expectedCenter, int tolerancePx)
    {
        VerifyCalls++;
        Verifies.Add((frame.WhileSlid, expectedCenter, tolerancePx));
        return VerifyHook?.Invoke(frame, templateId, expectedCenter)
            ?? VerifyResult
            ?? new MatchResult(templateId, true, expectedCenter, 0.97, 0.5, 0, MatchFailure.None);
    }

    public HudClassification ClassifyHud(Frame frame) => new(HudState.Collapsed, 0.95, 0.1, true, true, "ok");

    public FrameQuality Assess(Frame frame) => Quality;

    public double RegionChange(Frame a, Frame b, RectI region) => 0;

    public GrayImage Crop(Frame frame, RectI region, double scale = 1) => new(0, 0, []);

    public byte[] EncodePng(Frame frame, RectI region, IReadOnlyList<RectI> blackout) => [];
}

internal sealed class FakeUiClassifier : IUiClassifier
{
    public UiKind Classify(string uiName) =>
        UiCatalog.OverlaySeed.Contains(uiName) ? UiKind.Overlay
        : UiCatalog.ForbiddenExact.Contains(uiName) ? UiKind.Forbidden
        : UiCatalog.StackSeed.Contains(uiName) ? UiKind.Stack
        : UiKind.Unknown;

    public bool IsEscSafe(string uiName) => UiCatalog.EscSafe.Contains(uiName);

    public bool IsLearnedStack(string uiName) => false;
}

internal sealed class FakeJournal : IJournal
{
    private readonly object _lock = new();

    public List<JournalRecord> Records { get; } = [];

    public void Write(JournalRecord record)
    {
        lock (_lock)
        {
            Records.Add(record);
        }
    }

    public void Flush()
    {
    }
}

/// <summary>Fake template catalog mirroring templates\templates.json tags (the four HUD navigation templates are NavFixed).</summary>
internal sealed class FakeTemplates : ITemplateCatalog
{
    private readonly Dictionary<string, TemplateDefinition> _defs = new(StringComparer.Ordinal);

    public FakeTemplates()
    {
        Add(Def("hud.bag", 1748, 312, TargetTags.Nav | TargetTags.HudAnchor | TargetTags.NavFixed));
        Add(Def("hud.toggle.collapsed", 1855, 318, TargetTags.Toggle | TargetTags.HudAnchor | TargetTags.NavFixed));
        Add(Def("hud.toggle.expanded", 1855, 320, TargetTags.Toggle | TargetTags.HudAnchor | TargetTags.NavFixed));
        Add(Def("menu.ability", 1734, 452, TargetTags.Nav | TargetTags.NavFixed));
        Add(Def("craft.make", 1472, 870, TargetTags.Commit, AutomationSwitch.Crafting));
        Add(Def("craft.qty.max", 1264, 868, TargetTags.None, AutomationSwitch.Crafting));
        Add(Def("craft.fav.anchor", 591, 756, TargetTags.ScreenAnchor));
        Add(Def("craft.list.anchor", 485, 123, TargetTags.ScreenAnchor) with { FireOn = FireOn.Up, SearchRadius = 16, MotionTolerancePx = 8 });
    }

    public HashSet<string> Suspect { get; } = new(StringComparer.Ordinal);

    public IReadOnlyCollection<TemplateDefinition> All => _defs.Values.ToList();

    public static TemplateDefinition Def(string id, int x, int y, TargetTags tags, AutomationSwitch gate = AutomationSwitch.None) =>
        new(id, id + ".png", null, null, new PointI(x, y), 24, 0.9, 0.1, TemplateStatus.Verified, 1, null, tags, FireOn.Down, gate, null, null, "test", null);

    public void Add(TemplateDefinition d) => _defs[d.Id] = d;

    public TemplateDefinition? Get(string id) => id is not null && _defs.TryGetValue(id, out var d) ? d : null;

    public bool IsClickable(string id) =>
        Get(id) is { Status: TemplateStatus.Verified, SourceScale: 1 } d && !Suspect.Contains(id) && !d.Tags.HasFlag(TargetTags.Forbidden);

    public void MarkSuspect(string id, string reasonCode) => Suspect.Add(id);

    public bool IsSuspect(string id) => Suspect.Contains(id);
}
