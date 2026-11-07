using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace Companion.CaptureProbe;

/// <summary>One Windows.Graphics.Capture frame as BGRA bytes.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Stride">Bytes per row.</param>
/// <param name="Bgra">Pixels, top-down.</param>
/// <param name="LagMs">Time from frame composition to the copy.</param>
/// <param name="Fresh">The frame is time-stamped after the grab started.</param>
internal sealed record WgcImage(int Width, int Height, int Stride, byte[] Bgra, double LagMs, bool Fresh);

/// <summary>
/// A Windows.Graphics.Capture session on one window (GraphicsCaptureItem via IGraphicsCaptureItemInterop.CreateForWindow,
/// a free-threaded Direct3D11CaptureFramePool, cursor capture off). The capture border is removed only when the system
/// accepts it; otherwise the border stays. Read-only towards the game: it never sends anything to the window.
/// </summary>
internal sealed class WgcCapture : IDisposable
{
    private const int D3D11DriverTypeHardware = 1;
    private const int D3D11DriverTypeWarp = 5;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11SdkVersion = 7;

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceIid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid CaptureSession3Iid = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");

    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private int _disposed;

    private WgcCapture(IDirect3DDevice device, Direct3D11CaptureFramePool pool, GraphicsCaptureSession session, int width, int height, string border)
    {
        _device = device;
        _pool = pool;
        _session = session;
        Width = width;
        Height = height;
        Border = border;
    }

    /// <summary>Item width at start.</summary>
    public int Width { get; }

    /// <summary>Item height at start.</summary>
    public int Height { get; }

    /// <summary>Capture border: "removed", or "shown (reason)".</summary>
    public string Border { get; }

    /// <summary>Starts a capture session on <paramref name="hwnd"/>. Throws when the system does not support it.</summary>
    /// <param name="hwnd">The window.</param>
    public static WgcCapture Start(long hwnd)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new NotSupportedException("Windows.Graphics.Capture is not supported on this system");
        }

        var device = CreateDevice();
        var item = CreateItem(hwnd);
        var size = item.Size;
        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        var session = pool.CreateCaptureSession(item);
        try
        {
            session.IsCursorCaptureEnabled = false;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            // Older systems: the cursor is not drawn into window captures of a covered window anyway.
        }

        var border = TryRemoveBorder(session);
        session.StartCapture();
        return new WgcCapture(device, pool, session, size.Width, size.Height, border);
    }

    /// <summary>
    /// The first frame composed after this call (older pooled frames are dropped), as BGRA; null when none arrives
    /// within <paramref name="timeout"/>. If only frames time-stamped before the call arrive for half the timeout, the
    /// next one is taken anyway and marked not fresh (the time base is then unverified).
    /// </summary>
    /// <param name="timeout">Wait limit.</param>
    public WgcImage? Grab(TimeSpan timeout)
    {
        var start = QpcNow();
        Direct3D11CaptureFrame? old;
        while ((old = _pool.TryGetNextFrame()) is not null)
        {
            old.Dispose();
        }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            using var frame = _pool.TryGetNextFrame();
            if (frame is null)
            {
                Thread.Sleep(2);
                continue;
            }

            var fresh = frame.SystemRelativeTime >= start;
            if (!fresh && sw.Elapsed < timeout / 2)
            {
                continue;
            }

            var lag = (QpcNow() - frame.SystemRelativeTime).TotalMilliseconds;
            return Copy(frame, timeout - sw.Elapsed, lag, fresh);
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _session.Dispose();
        _pool.Dispose();
        _device.Dispose();
    }

    private static WgcImage? Copy(Direct3D11CaptureFrame frame, TimeSpan budget, double lagMs, bool fresh)
    {
        var task = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied).AsTask();
        if (!task.Wait(budget > TimeSpan.FromMilliseconds(100) ? budget : TimeSpan.FromMilliseconds(100)))
        {
            return null;
        }

        var bitmap = task.Result;
        try
        {
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            {
                var converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                bitmap.Dispose();
                bitmap = converted;
            }

            // CopyToBuffer writes tightly packed rows (the counterpart of CreateCopyFromBuffer, which takes no stride).
            var width = Math.Min(bitmap.PixelWidth, frame.ContentSize.Width);
            var height = Math.Min(bitmap.PixelHeight, frame.ContentSize.Height);
            var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyToBuffer(bytes.AsBuffer());
            return new WgcImage(width, height, bitmap.PixelWidth * 4, bytes, lagMs, fresh);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    /// <summary>System-relative time in the same base as <see cref="Direct3D11CaptureFrame.SystemRelativeTime"/> (QPC).</summary>
    private static TimeSpan QpcNow() => TimeSpan.FromTicks((long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency)));

    private static string TryRemoveBorder(GraphicsCaptureSession session)
    {
        if (!Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
        {
            return "shown (IsBorderRequired not available on this system)";
        }

        try
        {
            var native = ((IWinRTObject)session).NativeObject.ThisPtr;
            var iid = CaptureSession3Iid;
            var hr = Marshal.QueryInterface(native, in iid, out var ptr);
            if (hr < 0 || ptr == IntPtr.Zero)
            {
                return "shown (IGraphicsCaptureSession3 unavailable)";
            }

            try
            {
                var session3 = (IGraphicsCaptureSession3)Marshal.GetObjectForIUnknown(ptr);
                session3.PutIsBorderRequired(false);
                return session3.GetIsBorderRequired() ? "shown (system kept it)" : "removed";
            }
            finally
            {
                Marshal.Release(ptr);
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException)
        {
            return "shown (not permitted: 0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")";
        }
    }

    private static GraphicsCaptureItem CreateItem(long hwnd)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        var ptr = interop.CreateForWindow(new IntPtr(hwnd), ref iid);
        try
        {
            return GraphicsCaptureItem.FromAbi(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    private static IDirect3DDevice CreateDevice()
    {
        var hr = D3D11CreateDevice(IntPtr.Zero, D3D11DriverTypeHardware, IntPtr.Zero, D3D11CreateDeviceBgraSupport, IntPtr.Zero, 0, D3D11SdkVersion, out var d3d, out _, out var context);
        if (hr < 0)
        {
            hr = D3D11CreateDevice(IntPtr.Zero, D3D11DriverTypeWarp, IntPtr.Zero, D3D11CreateDeviceBgraSupport, IntPtr.Zero, 0, D3D11SdkVersion, out d3d, out _, out context);
        }

        Marshal.ThrowExceptionForHR(hr);
        try
        {
            var iid = DxgiDeviceIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in iid, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgi);
            }
        }
        finally
        {
            Marshal.Release(d3d);
            if (context != IntPtr.Zero)
            {
                Marshal.Release(context);
            }
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}

/// <summary>Windows.Graphics.Capture.Interop (windows.graphics.capture.interop.h).</summary>
[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    /// <summary>Creates a capture item for a window.</summary>
    IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);

    /// <summary>Creates a capture item for a monitor (unused).</summary>
    IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
}

/// <summary>
/// IGraphicsCaptureSession3 (IsBorderRequired, Windows 10 2104+; not in the 19041 projection). Declared as IUnknown with
/// the three IInspectable slots first so the property accessors land on the right vtable entries.
/// </summary>
[ComImport]
[Guid("F2CDD966-22AE-5EA1-9596-3A289344C3BE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureSession3
{
    /// <summary>IInspectable slot.</summary>
    void GetIids(out int iidCount, out IntPtr iids);

    /// <summary>IInspectable slot.</summary>
    void GetRuntimeClassName(out IntPtr className);

    /// <summary>IInspectable slot.</summary>
    void GetTrustLevel(out int trustLevel);

    /// <summary>get_IsBorderRequired.</summary>
    [return: MarshalAs(UnmanagedType.U1)]
    bool GetIsBorderRequired();

    /// <summary>put_IsBorderRequired.</summary>
    void PutIsBorderRequired([MarshalAs(UnmanagedType.U1)] bool value);
}
