using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;
using Companion.GameIO.Geometry;
using Companion.GameIO.Native;
using Companion.GameIO.Safety;

namespace Companion.GameIO.Diagnostics;

/// <summary>
/// Window access for the operator-run capture probe (tools\Companion.CaptureProbe, EVIDENCE c13 follow-up). Never used
/// by the host. Finds the game exactly like <see cref="GameIo.Connect"/> (<see cref="GameWindowFinder"/>), reads window,
/// cursor and monitor state (read-only calls), moves the window move-only with the slide flags 0x0215 through the
/// <see cref="ChokePoint"/> (size, z-order and activation untouched) and captures the client with
/// PrintWindow(PW_RENDERFULLCONTENT). It sends no window message of any kind: no focus, mouse or key messages.
/// </summary>
internal sealed class ProbeWindow
{
    private readonly IWin32 _win32;
    private readonly ChokePoint _choke;

    private ProbeWindow(IWin32 win32, long hwnd, int pid)
    {
        _win32 = win32;
        Hwnd = hwnd;
        Pid = pid;
        _choke = new ChokePoint(win32, hwnd, pid, HomeRules.ExpectedClient);
    }

    /// <summary>The game window.</summary>
    public long Hwnd { get; }

    /// <summary>The game process id.</summary>
    public int Pid { get; }

    /// <summary>True while the window exists and still belongs to the game process.</summary>
    public bool Exists => _win32.IsWindow(Hwnd) && _win32.GetWindowProcessId(Hwnd) == Pid;

    /// <summary>The foreground window belongs to the game process (the user is playing).</summary>
    public bool IsForeground => _choke.IsGameForeground();

    /// <summary>IsIconic.</summary>
    public bool IsMinimized => _win32.IsIconic(Hwnd);

    /// <summary>IsZoomed.</summary>
    public bool IsMaximized => _win32.IsZoomed(Hwnd);

    /// <summary>Window rect (screen).</summary>
    public RectI? Rect => _win32.GetWindowRect(Hwnd);

    /// <summary>Client origin on screen.</summary>
    public PointI? ClientOrigin => _win32.GetClientOrigin(Hwnd);

    /// <summary>Client size.</summary>
    public SizeI? ClientSize => _win32.GetClientSize(Hwnd);

    /// <summary>DWM extended frame bounds (screen).</summary>
    public RectI? FrameBounds => _win32.GetFrameBounds(Hwnd);

    /// <summary>
    /// Opens the game window with the production Win32 layer, or returns null with a problem code (not64Bit,
    /// notElevated, or a <see cref="ConnectProblem"/> name).
    /// </summary>
    /// <param name="gameExePath">Expected game executable.</param>
    /// <param name="problem">"none" or the reason.</param>
    public static ProbeWindow? Open(string gameExePath, out string problem) => Open(new Win32Api(), gameExePath, out problem);

    /// <summary>Opens the game window through <paramref name="win32"/> (tests pass a fake).</summary>
    internal static ProbeWindow? Open(IWin32 win32, string gameExePath, out string problem)
    {
        if (!win32.Is64BitProcess)
        {
            problem = "not64Bit";
            return null;
        }

        if (!win32.IsCurrentProcessElevated())
        {
            problem = "notElevated";
            return null;
        }

        var found = GameWindowFinder.Find(win32, gameExePath);
        if (found.Problem != ConnectProblem.None)
        {
            problem = found.Problem.ToString();
            return null;
        }

        problem = "none";
        return new ProbeWindow(win32, found.Hwnd, found.Pid);
    }

    /// <summary>All monitors (EnumDisplayMonitors).</summary>
    public IReadOnlyList<MonitorInfo> Monitors() => _win32.EnumMonitors();

    /// <summary>The monitor with the largest overlap (pure computation, MonitorFromRect NEAREST semantics).</summary>
    public static MonitorInfo? LargestOverlap(RectI rect, IReadOnlyList<MonitorInfo> monitors) => SlideGeometry.NearestMonitor(rect, monitors);

    /// <summary>MonitorFromRect(rect, NEAREST) device name from Win32.</summary>
    public string? MonitorOf(RectI rect) => _win32.MonitorFromRect(rect);

    /// <summary>The real cursor position (read-only), or null when unavailable.</summary>
    public PointI? Cursor() => _win32.TryGetCursorPos(out var p) ? p : null;

    /// <summary>True if a mouse button is held (VK 1, 2, 4 only).</summary>
    public bool AnyMouseButtonDown() => Guards.AnyMouseButtonDown(_win32);

    /// <summary>Top-level window under a screen point (WindowFromPoint, then GA_ROOT), 0 if none.</summary>
    public long TopWindowAt(PointI p)
    {
        var h = _win32.WindowFromPoint(p);
        return h == 0 ? 0 : _win32.GetAncestor(h, Win32Const.GA_ROOT);
    }

    /// <summary>True if <paramref name="hwnd"/> is above the game in one EnumWindows z-order snapshot.</summary>
    public bool IsAboveGame(long hwnd)
    {
        var z = _win32.EnumTopLevelWindows();
        var a = -1;
        var g = -1;
        for (var i = 0; i < z.Count; i++)
        {
            if (z[i] == hwnd && a < 0)
            {
                a = i;
            }

            if (z[i] == Hwnd && g < 0)
            {
                g = i;
            }
        }

        return a >= 0 && g >= 0 && a < g;
    }

    /// <summary>DWM extended frame bounds of any window.</summary>
    public RectI? FrameOf(long hwnd) => _win32.GetFrameBounds(hwnd);

    /// <summary>Move-only to (x, y) with flags 0x0215 through the choke point (hWndInsertAfter 0, no size change).</summary>
    public bool MoveTo(int x, int y) => _choke.SetWindowPos(Hwnd, 0, x, y, 0, 0, HardRules.SwpSlide, out _);

    /// <summary>PrintWindow(PW_RENDERFULLCONTENT) of the client as top-down BGRA (alpha 255), or null on failure.</summary>
    public byte[]? CaptureClient(RectI windowRect, PointI clientOrigin, SizeI clientSize) =>
        _choke.PrintClient(Hwnd, HardRules.PW_RENDERFULLCONTENT, windowRect, clientOrigin, clientSize);
}
