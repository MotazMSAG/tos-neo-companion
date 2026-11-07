using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;
using Companion.GameIO.Safety;
using Microsoft.Win32;

namespace Companion.GameIO.Native;

/// <summary>
/// Production <see cref="IWin32"/>. Thin wrappers over <see cref="NativeMethods"/> plus BCL process/registry/file
/// metadata reads. Never used by tests. Acting members are reached only through <see cref="ChokePoint"/>.
/// </summary>
internal sealed class Win32Api : IWin32
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint DesktopReadObjects = 0x0001;
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;

    public bool Is64BitProcess => Environment.Is64BitProcess;

    public int CurrentProcessId => Environment.ProcessId;

    public IReadOnlyList<long> EnumTopLevelWindows()
    {
        var list = new List<long>(512);
        NativeMethods.EnumWindowsProc cb = (h, _) =>
        {
            list.Add(h.ToInt64());
            return list.Count < 8192;
        };
        NativeMethods.EnumWindows(cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return list;
    }

    public int GetWindowProcessId(long hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(new IntPtr(hwnd), out var pid);
        return (int)pid;
    }

    public int GetWindowThreadId(long hwnd) => (int)NativeMethods.GetWindowThreadProcessId(new IntPtr(hwnd), out _);

    public bool IsWindow(long hwnd) => hwnd != 0 && NativeMethods.IsWindow(new IntPtr(hwnd));

    public bool IsWindowVisible(long hwnd) => NativeMethods.IsWindowVisible(new IntPtr(hwnd));

    public bool IsIconic(long hwnd) => NativeMethods.IsIconic(new IntPtr(hwnd));

    public bool IsZoomed(long hwnd) => NativeMethods.IsZoomed(new IntPtr(hwnd));

    public bool IsHungAppWindow(long hwnd) => NativeMethods.IsHungAppWindow(new IntPtr(hwnd));

    public long GetForegroundWindow() => NativeMethods.GetForegroundWindow().ToInt64();

    public string GetClassName(long hwnd)
    {
        var buf = new char[256];
        var n = NativeMethods.GetClassNameW(new IntPtr(hwnd), buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : string.Empty;
    }

    public RectI? GetWindowRect(long hwnd) =>
        NativeMethods.GetWindowRect(new IntPtr(hwnd), out var r) ? ToRect(r) : null;

    public SizeI? GetClientSize(long hwnd) =>
        NativeMethods.GetClientRect(new IntPtr(hwnd), out var r) ? new SizeI(r.Right - r.Left, r.Bottom - r.Top) : null;

    public PointI? GetClientOrigin(long hwnd)
    {
        var p = new NativeMethods.POINT();
        return NativeMethods.ClientToScreen(new IntPtr(hwnd), ref p) ? new PointI(p.X, p.Y) : null;
    }

    public long GetStyle(long hwnd) => NativeMethods.GetWindowLongPtrW(new IntPtr(hwnd), Win32Const.GWL_STYLE).ToInt64();

    public long GetExStyle(long hwnd) => NativeMethods.GetWindowLongPtrW(new IntPtr(hwnd), Win32Const.GWL_EXSTYLE).ToInt64();

    public byte? GetLayeredAlpha(long hwnd)
    {
        if (!NativeMethods.GetLayeredWindowAttributes(new IntPtr(hwnd), out _, out var alpha, out var flags))
        {
            return null;
        }

        if ((flags & Win32Const.LWA_COLORKEY) != 0)
        {
            return null;
        }

        return (flags & Win32Const.LWA_ALPHA) != 0 ? alpha : (byte)255;
    }

    public long WindowFromPoint(PointI screen) =>
        NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = screen.X, Y = screen.Y }).ToInt64();

    public long GetAncestor(long hwnd, uint flags) => NativeMethods.GetAncestor(new IntPtr(hwnd), flags).ToInt64();

    public long GetWindow(long hwnd, uint command) => NativeMethods.GetWindow(new IntPtr(hwnd), command).ToInt64();

    public bool IsCloaked(long hwnd) =>
        NativeMethods.DwmGetWindowAttributeInt(new IntPtr(hwnd), Win32Const.DWMWA_CLOAKED, out var v, sizeof(int)) == 0 && v != 0;

    public RectI? GetFrameBounds(long hwnd) =>
        NativeMethods.DwmGetWindowAttributeRect(new IntPtr(hwnd), Win32Const.DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<NativeMethods.RECT>()) == 0
            ? ToRect(r)
            : null;

    public long GetKeyboardLayout(int threadId) => NativeMethods.GetKeyboardLayout((uint)threadId).ToInt64();

    public bool TryGetCursorPos(out PointI screen)
    {
        if (NativeMethods.GetCursorPos(out var p))
        {
            screen = new PointI(p.X, p.Y);
            return true;
        }

        screen = default;
        return false;
    }

    public bool TryGetCursorShowing(out bool showing)
    {
        var ci = new NativeMethods.CURSORINFO { CbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
        if (NativeMethods.GetCursorInfo(ref ci))
        {
            showing = (ci.Flags & Win32Const.CURSOR_SHOWING) != 0;
            return true;
        }

        showing = false;
        return false;
    }

    public RectI? GetClipCursor() => NativeMethods.GetClipCursor(out var r) ? ToRect(r) : null;

    public short GetAsyncKeyState(int virtualKey)
    {
        if (!StaticRules.GetAsyncKeyStateAllowedKeys.Contains(virtualKey))
        {
            throw new HardRuleViolationException("getAsyncKeyState.vk");
        }

        return NativeMethods.GetAsyncKeyState(virtualKey);
    }

    public TimeSpan? GetSystemIdle()
    {
        var li = new NativeMethods.LASTINPUTINFO { CbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        if (!NativeMethods.GetLastInputInfo(ref li))
        {
            return null;
        }

        var now = unchecked((uint)Environment.TickCount);
        return TimeSpan.FromMilliseconds(unchecked(now - li.DwTime));
    }

    public uint? GetLastInputTick()
    {
        var li = new NativeMethods.LASTINPUTINFO { CbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        return NativeMethods.GetLastInputInfo(ref li) ? li.DwTime : null;
    }

    public bool IsInputDesktopAvailable()
    {
        var desk = NativeMethods.OpenInputDesktop(0, false, DesktopReadObjects);
        if (desk == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.CloseDesktop(desk);
        return true;
    }

    public IReadOnlyList<MonitorInfo> EnumMonitors()
    {
        var list = new List<MonitorInfo>();
        NativeMethods.MonitorEnumProc cb = (IntPtr hmon, IntPtr _, ref NativeMethods.RECT _, IntPtr _) =>
        {
            var info = ReadMonitor(hmon);
            if (info is not null)
            {
                list.Add(info);
            }

            return list.Count < 32;
        };
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return list;
    }

    public string? MonitorFromRect(RectI rect)
    {
        var r = new NativeMethods.RECT { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };
        return ReadMonitor(NativeMethods.MonitorFromRect(ref r, Win32Const.MONITOR_DEFAULTTONEAREST))?.DeviceName;
    }

    public string? MonitorFromWindow(long hwnd) =>
        ReadMonitor(NativeMethods.MonitorFromWindow(new IntPtr(hwnd), Win32Const.MONITOR_DEFAULTTONEAREST))?.DeviceName;

    public string? MonitorFromPoint(PointI screen) =>
        ReadMonitor(NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = screen.X, Y = screen.Y }, Win32Const.MONITOR_DEFAULTTONEAREST))?.DeviceName;

    public int GetMaximizedFrameThickness() =>
        NativeMethods.GetSystemMetrics(Win32Const.SM_CXSIZEFRAME) + NativeMethods.GetSystemMetrics(Win32Const.SM_CXPADDEDBORDER);

    public IReadOnlyList<int> GetProcessIdsByName(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        try
        {
            return procs.Select(p => p.Id).ToArray();
        }
        finally
        {
            foreach (var p in procs)
            {
                p.Dispose();
            }
        }
    }

    public DateTimeOffset? GetProcessStartUtc(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return new DateTimeOffset(proc.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    public string? QueryProcessImagePath(int pid, uint access)
    {
        if (access != ProcessQueryLimitedInformation)
        {
            throw new HardRuleViolationException("openProcess.access");
        }

        var h = NativeMethods.OpenProcess(access, false, (uint)pid);
        if (h == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buf = new char[1024];
            var size = (uint)buf.Length;
            return NativeMethods.QueryFullProcessImageNameW(h, 0, buf, ref size) ? new string(buf, 0, (int)size) : null;
        }
        finally
        {
            NativeMethods.CloseHandle(h);
        }
    }

    public bool IsCurrentProcessElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public (long Size, DateTime WriteUtc)? GetFileMetadata(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public object? ReadGameRegistryValue(string valueName)
    {
        if (!GamePrefsReader.IsWhitelisted(valueName))
        {
            throw new HardRuleViolationException("registry.notWhitelisted");
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StaticRules.GameRegistryKey, false);
            return key?.GetValue(valueName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public bool PostMessage(long hwnd, uint message, long wParam, long lParam, out int lastError)
    {
        var ok = NativeMethods.PostMessageW(new IntPtr(hwnd), message, new IntPtr(wParam), new IntPtr(lParam));
        lastError = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }

    public bool SendMessageTimeout(long hwnd, uint message, long wParam, long lParam, uint flags, uint timeoutMs, out int lastError)
    {
        var r = NativeMethods.SendMessageTimeoutW(new IntPtr(hwnd), message, new IntPtr(wParam), new IntPtr(lParam), flags, timeoutMs, out _);
        var ok = r != IntPtr.Zero;
        lastError = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }

    public bool SendNotifyMessage(long hwnd, uint message, long wParam, long lParam, out int lastError)
    {
        var ok = NativeMethods.SendNotifyMessageW(new IntPtr(hwnd), message, new IntPtr(wParam), new IntPtr(lParam));
        lastError = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }

    public bool SetWindowPos(long hwnd, long insertAfter, int x, int y, int cx, int cy, uint flags, out int lastError)
    {
        var ok = NativeMethods.SetWindowPos(new IntPtr(hwnd), new IntPtr(insertAfter), x, y, cx, cy, flags);
        lastError = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }

    public byte[]? PrintWindowClient(long hwnd, uint flags, RectI windowRect, PointI clientOrigin, SizeI clientSize)
    {
        if (windowRect.Width <= 0 || windowRect.Height <= 0 || clientSize.Width <= 0 || clientSize.Height <= 0)
        {
            return null;
        }

        using var bmp = new Bitmap(windowRect.Width, windowRect.Height, PixelFormat.Format32bppArgb);
        bool ok;
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            try
            {
                ok = NativeMethods.PrintWindow(new IntPtr(hwnd), hdc, flags);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        if (!ok)
        {
            return null;
        }

        var ox = clientOrigin.X - windowRect.Left;
        var oy = clientOrigin.Y - windowRect.Top;
        if (ox < 0 || oy < 0 || ox + clientSize.Width > bmp.Width || oy + clientSize.Height > bmp.Height)
        {
            return null;
        }

        var rowBytes = clientSize.Width * 4;
        var pixels = new byte[rowBytes * clientSize.Height];
        var data = bmp.LockBits(new Rectangle(ox, oy, clientSize.Width, clientSize.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < clientSize.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        return pixels;
    }

    public IPreciseWaiter CreatePreciseWaiter() => new WaitableTimerWaiter();

    private static RectI ToRect(NativeMethods.RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);

    private static MonitorInfo? ReadMonitor(IntPtr hmon)
    {
        if (hmon == IntPtr.Zero)
        {
            return null;
        }

        var mi = new NativeMethods.MONITORINFOEX { CbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(), SzDevice = string.Empty };
        if (!NativeMethods.GetMonitorInfoW(hmon, ref mi))
        {
            return null;
        }

        var dpi = NativeMethods.GetDpiForMonitor(hmon, 0, out var dx, out _) == 0 ? (int)dx : 0;
        return new MonitorInfo(mi.SzDevice ?? string.Empty, ToRect(mi.RcMonitor), ToRect(mi.RcWork), (mi.DwFlags & 1) != 0, dpi);
    }

    /// <summary>High-resolution waitable timer (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION); falls back to a 1 ms timer period.</summary>
    private sealed class WaitableTimerWaiter : IPreciseWaiter
    {
        private readonly IntPtr _timer;
        private readonly bool _raisedPeriod;
        private int _disposed;

        public WaitableTimerWaiter()
        {
            _timer = NativeMethods.CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            if (_timer == IntPtr.Zero)
            {
                _raisedPeriod = NativeMethods.timeBeginPeriod(1) == 0;
                _timer = NativeMethods.CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);
            }
        }

        public void Wait(TimeSpan duration)
        {
            if (_timer == IntPtr.Zero)
            {
                Thread.Sleep(Math.Max(1, (int)duration.TotalMilliseconds));
                return;
            }

            var due = -Math.Max(1L, duration.Ticks);
            if (!NativeMethods.SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                Thread.Sleep(1);
                return;
            }

            NativeMethods.WaitForSingleObject(_timer, Math.Max(20u, (uint)duration.TotalMilliseconds * 4));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_timer != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_timer);
            }

            if (_raisedPeriod)
            {
                NativeMethods.timeEndPeriod(1);
            }
        }
    }
}
