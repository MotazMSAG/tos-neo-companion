using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.GameIO.Native;
using Companion.GameIO.Safety;

namespace Companion.GameIO.Tests.Fakes;

/// <summary>
/// The user's layout as a synthetic scene (SPEC 13): DISPLAY1 (-1920,0) and DISPLAY2 (0,0) primary, 1920x1080 each,
/// 96 DPI, taskbars at the bottom. The game is maximized on DISPLAY2 at (-8,-8)-(1928,1040) with a 1920x1009 client at
/// offset (8,31); a maximized Chrome window covers it and is foreground; the cursor rests at (918,672) (c3 run 3).
/// The scripted game logs RecoverFocus on our focus messages, opens UI_BAG_MAIN on DOWN / KEYDOWN B and logs the
/// LostFocuse block on our unfocus. GameIO runs with a manual watchdog sampled on every fake Sleep.
/// </summary>
internal sealed class Rig : IDisposable
{
    public const long Game = 0x1000;
    public const long Cover = 0x2000;
    public const long Taskbar = 0x3000;
    public const long CoverD1 = 0x4000;
    public const long TaskbarD1 = 0x5000;
    public const int GamePid = 4242;
    public const string GameExe = @"D:\Games\TOS\TOS.exe";
    public static readonly RectI HomeRect = new(-8, -8, 1928, 1040);
    public static readonly PointI RestingCursor = new(918, 672);
    public static readonly PointI BagTarget = new(1748, 314);
    public static readonly RectI SlidRect = new(-838, 327, 1098, 1375);
    public static readonly DateTimeOffset GameStart = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    public Rig(bool gameReacts = true)
    {
        Clock = new FakeClock();
        Win32 = new FakeWin32(Clock);
        Log = new FakeLogTracker(Clock);
        Vision = new FakeVision();
        Templates = new FakeTemplates();
        Ui = new FakeUiClassifier();
        Journal = new FakeJournal();
        Stop = new StopSwitch(Clock);
        RuntimeDir = Path.Combine(AppContext.BaseDirectory, "rt-" + Guid.NewGuid().ToString("N"));
        Paths = new CompanionPaths(
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "data"),
            Path.Combine(AppContext.BaseDirectory, "templates"),
            Path.Combine(AppContext.BaseDirectory, "features"),
            RuntimeDir,
            @"D:\Games\TOS",
            @"D:\Games\TOS\Log\log.txt",
            GameExe);

        Win32.Monitors.Add(new MonitorInfo(@"\\.\DISPLAY1", new RectI(-1920, 0, 0, 1080), new RectI(-1920, 0, 0, 1032), false, 96));
        Win32.Monitors.Add(new MonitorInfo(@"\\.\DISPLAY2", new RectI(0, 0, 1920, 1080), new RectI(0, 0, 1920, 1032), true, 96));
        Win32.Add(new FakeWindow
        {
            Hwnd = Game, Pid = GamePid, ThreadId = 11, Class = HardRules.GameWindowClass, Zoomed = true, Rect = HomeRect,
            Client = new SizeI(1920, 1009), ClientOffset = new PointI(8, 31), Style = Win32Const.WS_CAPTION,
        });
        Win32.Add(new FakeWindow { Hwnd = Cover, Pid = 777, Class = "Chrome_WidgetWin_1", Zoomed = true, Rect = HomeRect, Frame = new RectI(0, 0, 1920, 1032) });
        Win32.Add(new FakeWindow { Hwnd = Taskbar, Pid = 100, Class = "Shell_TrayWnd", Rect = new RectI(0, 1032, 1920, 1080), ExStyle = Win32Const.WS_EX_TOPMOST });
        Win32.Add(new FakeWindow { Hwnd = CoverD1, Pid = 778, Class = "Notepad", Zoomed = true, Rect = new RectI(-1928, -8, 8, 1040), Frame = new RectI(-1920, 0, 0, 1032) });
        Win32.Add(new FakeWindow { Hwnd = TaskbarD1, Pid = 100, Class = "Shell_SecondaryTrayWnd", Rect = new RectI(-1920, 1032, 0, 1080), ExStyle = Win32Const.WS_EX_TOPMOST });
        Win32.ZOrder.AddRange([Taskbar, TaskbarD1, Cover, CoverD1, Game]);
        Win32.Foreground = Cover;
        Win32.Cursor = RestingCursor;
        Win32.Processes[GamePid] = new FakeProcess("TOS", GameExe, GameStart);
        Win32.Processes[777] = new FakeProcess("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", GameStart.AddHours(-1));
        Win32.Processes[778] = new FakeProcess("notepad", @"C:\Windows\notepad.exe", GameStart.AddHours(-1));
        Win32.Processes[100] = new FakeProcess("explorer", @"C:\Windows\explorer.exe", GameStart.AddHours(-2));
        Win32.Registry[GamePrefsReader.UnityValueName("Screenmanager Fullscreen mode")] = 3;
        Win32.Registry[GamePrefsReader.UnityValueName("Screenmanager Resolution Width")] = 1920;
        Win32.Registry[GamePrefsReader.UnityValueName("Screenmanager Resolution Height")] = 1009;
        Win32.Registry[GamePrefsReader.UnityValueName("UnitySelectMonitor")] = 0;

        Log.StateValue = Log.StateValue with { LastFocusChange = Clock.UtcNow.LocalDateTime.AddMinutes(-10) };

        if (gameReacts)
        {
            Win32.OnCall = ReactLikeTheGame;
        }

        Io = new GameIo(
            Clock, Stop, Log, Ui, Vision, Templates, Journal, Paths, Win32,
            new GameIoOptions(StartSampler: false, ManualWatchdog: true, ForegroundSlide: () => ForegroundSlideOn, ForegroundDrag: () => ForegroundDragOn));
        Clock.OnSleep = () =>
        {
            BeforeSample?.Invoke();
            Io.ActiveWatchdog?.SampleOnce();
        };
    }

    public FakeClock Clock { get; }

    public FakeWin32 Win32 { get; }

    public FakeLogTracker Log { get; }

    public FakeVision Vision { get; }

    public FakeTemplates Templates { get; }

    public FakeUiClassifier Ui { get; }

    public FakeJournal Journal { get; }

    public StopSwitch Stop { get; }

    public CompanionPaths Paths { get; }

    public string RuntimeDir { get; }

    public GameIo Io { get; }

    /// <summary>Extra scripted action run on every fake Sleep before the watchdog sample.</summary>
    public Action? BeforeSample { get; set; }

    /// <summary>Extra scripted action run after the game's own reaction to every acting call.</summary>
    public Action<Call>? OnCall { get; set; }

    public FakeWindow GameWindow => Win32.Windows[Game];

    /// <summary>The user's foreground-mode switch (SPEC 7.3b; off unless a test turns it on).</summary>
    public bool ForegroundSlideOn { get; set; }

    /// <summary>The user's switch for drags in foreground mode (off unless a test turns it on).</summary>
    public bool ForegroundDragOn { get; set; }

    /// <summary>
    /// Foreground mode scene (SPEC 7.3b): the user clicked the game (foreground, above the covers below the topmost
    /// taskbars, RecoverFocus logged) and rests the cursor on it; then <paramref name="settle"/> passes (default 6 s, past
    /// the switch-in settle).
    /// </summary>
    public void GameInFront(TimeSpan? settle = null)
    {
        ForegroundSlideOn = true;
        Win32.ZOrder.Remove(Game);
        Win32.ZOrder.Insert(2, Game);
        Win32.Foreground = Game;
        Log.Focus(recovered: true);
        Log.StateValue = Log.StateValue with { Focus = FocusState.Recovered, LastFocusChange = Clock.UtcNow.LocalDateTime };
        Settle(settle ?? TimeSpan.FromSeconds(6));
    }

    /// <summary>Connects and lets 3 s pass (cursor still, home stable, cover stable).</summary>
    public void ConnectAndSettle()
    {
        var r = Io.Connect(CancellationToken.None);
        Assert.IsTrue(r.Ok, r.Problem.ToString());
        Settle(TimeSpan.FromSeconds(3));
    }

    /// <summary>Observes the current scene, lets <paramref name="d"/> pass, observes again (stillness/stability trackers).</summary>
    public void Settle(TimeSpan d)
    {
        Io.SamplerTick();
        Clock.Advance(d);
        Io.SamplerTick();
    }

    public ClickRequest BagClick(FireOn fireOn = FireOn.Down) =>
        new("open", "hud.bag", BagTarget, fireOn, Log.Position, Clock.Monotonic, UiExpectation.Opened("UI_BAG_MAIN"), false);

    /// <summary>Acting calls made after Connect (the connect WM_NULL probe is excluded by <see cref="ConnectAndSettle"/> callers clearing).</summary>
    public IReadOnlyList<Call> Acting => Win32.Calls.Where(c => !(c.Msg == HardRules.WM_NULL && c.Kind is "send" or "post")).ToList();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RuntimeDir))
            {
                Directory.Delete(RuntimeDir, true);
            }
        }
        catch (IOException)
        {
        }
    }

    private void ReactLikeTheGame(Call c)
    {
        if (c.Kind == "send" && c.Msg == HardRules.WM_ACTIVATE && c.WParam == 1)
        {
            Log.Focus(recovered: true);
        }
        else if (c.Kind == "post" && c.Msg == HardRules.WM_LBUTTONDOWN)
        {
            Log.Show("UI_BAG_MAIN");
        }
        else if (c.Kind == "post" && c.Msg == HardRules.WM_KEYDOWN && c.WParam == 0x42)
        {
            Log.Show("UI_BAG_MAIN");
        }
        else if (c.Kind is "send" or "notify" && c.Msg == HardRules.WM_KILLFOCUS)
        {
            Log.PressBlock("Label_Text", new PointD(1748.0, 694.0));
            Log.Focus(recovered: false);
        }

        OnCall?.Invoke(c);
    }
}
