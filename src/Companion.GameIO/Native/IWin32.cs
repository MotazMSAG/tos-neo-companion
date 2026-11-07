using Companion.Contracts;
using Companion.Contracts.GameIo;

namespace Companion.GameIO.Native;

/// <summary>
/// Internal test seam over every Win32 call GameIO makes (SPEC 5: "GameIO has an internal seam"). Not a contract.
/// Production uses <see cref="Win32Api"/>; tests use a fake message sink + fake window table + fake cursor samples, so
/// no test ever touches a real window. Handles are int64. Acting members (PostMessage, SendMessageTimeout,
/// SendNotifyMessage, SetWindowPos, PrintWindowClient) are called ONLY by <see cref="Safety.ChokePoint"/>.
/// </summary>
internal interface IWin32
{
    // ---------------------------------------------------------------- windows (read-only)
    /// <summary>Top-level windows in z-order, topmost first (EnumWindows).</summary>
    IReadOnlyList<long> EnumTopLevelWindows();

    /// <summary>Owning process id (0 if unknown).</summary>
    int GetWindowProcessId(long hwnd);

    /// <summary>Owning thread id (0 if unknown).</summary>
    int GetWindowThreadId(long hwnd);

    bool IsWindow(long hwnd);

    bool IsWindowVisible(long hwnd);

    bool IsIconic(long hwnd);

    bool IsZoomed(long hwnd);

    bool IsHungAppWindow(long hwnd);

    long GetForegroundWindow();

    /// <summary>Class name ("" if unknown). Window titles are never read (PRIV-14).</summary>
    string GetClassName(long hwnd);

    RectI? GetWindowRect(long hwnd);

    SizeI? GetClientSize(long hwnd);

    /// <summary>ClientToScreen(0,0).</summary>
    PointI? GetClientOrigin(long hwnd);

    /// <summary>GWL_STYLE.</summary>
    long GetStyle(long hwnd);

    /// <summary>GWL_EXSTYLE.</summary>
    long GetExStyle(long hwnd);

    /// <summary>
    /// Effective constant alpha of a layered window: the LWA_ALPHA value, 255 when only opaque attributes are set,
    /// null when the attributes cannot be read (per-pixel alpha) or a colour key makes parts transparent.
    /// </summary>
    byte? GetLayeredAlpha(long hwnd);

    long WindowFromPoint(PointI screen);

    long GetAncestor(long hwnd, uint flags);

    long GetWindow(long hwnd, uint command);

    /// <summary>DWMWA_CLOAKED != 0.</summary>
    bool IsCloaked(long hwnd);

    /// <summary>DWMWA_EXTENDED_FRAME_BOUNDS.</summary>
    RectI? GetFrameBounds(long hwnd);

    long GetKeyboardLayout(int threadId);

    // ---------------------------------------------------------------- cursor / input state (read-only, R1)
    bool TryGetCursorPos(out PointI screen);

    /// <summary>GetCursorInfo: CURSOR_SHOWING.</summary>
    bool TryGetCursorShowing(out bool showing);

    RectI? GetClipCursor();

    /// <summary>Raw GetAsyncKeyState. Callers go through <see cref="Safety.Guards.IsMouseButtonDown"/> (VK 1, 2, 4 only).</summary>
    short GetAsyncKeyState(int virtualKey);

    /// <summary>GetLastInputInfo idle time, or null if unavailable.</summary>
    TimeSpan? GetSystemIdle();

    /// <summary>
    /// GetLastInputInfo dwTime (tick of the last keyboard or mouse input), or null if unavailable. Foreground mode compares
    /// it with the value at readiness: any change is the user's input (our window messages and window moves are not input).
    /// </summary>
    uint? GetLastInputTick();

    /// <summary>OpenInputDesktop succeeds (false on a locked or secure desktop).</summary>
    bool IsInputDesktopAvailable();

    // ---------------------------------------------------------------- monitors (read-only)
    IReadOnlyList<MonitorInfo> EnumMonitors();

    /// <summary>MonitorFromRect(r, NEAREST) device name.</summary>
    string? MonitorFromRect(RectI rect);

    /// <summary>MonitorFromWindow(h, NEAREST) device name.</summary>
    string? MonitorFromWindow(long hwnd);

    /// <summary>MonitorFromPoint(p, NEAREST) device name.</summary>
    string? MonitorFromPoint(PointI screen);

    /// <summary>Hidden frame thickness of a maximized sizable window (SM_CXSIZEFRAME + SM_CXPADDEDBORDER).</summary>
    int GetMaximizedFrameThickness();

    // ---------------------------------------------------------------- process / environment (read-only)
    IReadOnlyList<int> GetProcessIdsByName(string processName);

    DateTimeOffset? GetProcessStartUtc(int pid);

    /// <summary>OpenProcess(access) + QueryFullProcessImageNameW. Callers go through <see cref="Safety.Guards.QueryImagePath"/> (0x1000 only).</summary>
    string? QueryProcessImagePath(int pid, uint access);

    bool IsCurrentProcessElevated();

    bool Is64BitProcess { get; }

    int CurrentProcessId { get; }

    /// <summary>File size and last write time (metadata only; contents are never read).</summary>
    (long Size, DateTime WriteUtc)? GetFileMetadata(string path);

    /// <summary>Reads one whitelisted value of HKCU\Software\TOS\TOS (read-only; asserts the whitelist).</summary>
    object? ReadGameRegistryValue(string valueName);

    // ---------------------------------------------------------------- acting (ChokePoint only)
    bool PostMessage(long hwnd, uint message, long wParam, long lParam, out int lastError);

    /// <summary>SendMessageTimeout; true when the call returned in time.</summary>
    bool SendMessageTimeout(long hwnd, uint message, long wParam, long lParam, uint flags, uint timeoutMs, out int lastError);

    bool SendNotifyMessage(long hwnd, uint message, long wParam, long lParam, out int lastError);

    bool SetWindowPos(long hwnd, long insertAfter, int x, int y, int cx, int cy, uint flags, out int lastError);

    /// <summary>PrintWindow into a window-sized bitmap, then the client area as top-down BGRA (alpha forced to 255); null if PrintWindow failed.</summary>
    byte[]? PrintWindowClient(long hwnd, uint flags, RectI windowRect, PointI clientOrigin, SizeI clientSize);

    // ---------------------------------------------------------------- timing
    /// <summary>High-resolution waiter for the watchdog (waitable timer; timeBeginPeriod fallback).</summary>
    IPreciseWaiter CreatePreciseWaiter();
}

/// <summary>Sleeps with ~1 ms resolution on the watchdog thread.</summary>
internal interface IPreciseWaiter : IDisposable
{
    void Wait(TimeSpan duration);
}

/// <summary>Win32 constants used by GameIO (values only; the APIs they belong to are in <see cref="NativeMethods"/>).</summary>
internal static class Win32Const
{
    public const uint GA_ROOT = 2;
    public const uint GA_ROOTOWNER = 3;
    public const uint GW_HWNDPREV = 3;
    public const uint GW_OWNER = 4;
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const long WS_CAPTION = 0x00C00000L;
    public const long WS_EX_TOPMOST = 0x00000008L;
    public const long WS_EX_TRANSPARENT = 0x00000020L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_LAYERED = 0x00080000L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;
    public const uint SMTO_BLOCK = 0x0001;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_TIMEOUT = 1460;
    public const int CURSOR_SHOWING = 0x00000001;
    public const int SM_CXSIZEFRAME = 32;
    public const int SM_CXPADDEDBORDER = 92;
    public const uint LWA_COLORKEY = 0x1;
    public const uint LWA_ALPHA = 0x2;
    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;
    public const int VK_MBUTTON = 0x04;
}
