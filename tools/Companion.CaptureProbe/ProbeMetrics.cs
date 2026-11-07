using Companion.Contracts;

namespace Companion.CaptureProbe;

/// <summary>Pure image and geometry helpers of the capture probe (tested offline). Images are top-down BGRA.</summary>
internal static class ProbeMetrics
{
    /// <summary>The HUD toggle area (client) compared in c13.</summary>
    public static readonly RectI ToggleArea = new(1827, 290, 1883, 346);

    /// <summary>Chat panel blacked out in every PNG (the engine's HudTemplates.ChatPanel).</summary>
    public static readonly RectI ChatPanel = new(660, 590, 1270, 1009);

    /// <summary>
    /// Mean absolute difference per channel (0..255, B/G/R, alpha ignored) of two client images over a region (clipped
    /// to the image). NaN when the region is empty or a buffer is too short.
    /// </summary>
    /// <param name="a">First image (stride = width * 4).</param>
    /// <param name="b">Second image (same size).</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="region">Region.</param>
    public static double MeanAbsDiff(byte[] a, byte[] b, int width, int height, RectI region)
    {
        if (Clip(region, width, height) is not { } r || !Fits(a, width, height) || !Fits(b, width, height))
        {
            return double.NaN;
        }

        long sum = 0;
        for (var y = r.Top; y < r.Bottom; y++)
        {
            for (var x = r.Left; x < r.Right; x++)
            {
                var i = ((y * width) + x) * 4;
                sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
            }
        }

        return (double)sum / (r.Area * 3);
    }

    /// <summary>Share (0..1) of pure-black pixels (B = G = R = 0) in a region (clipped). NaN when empty or too short.</summary>
    /// <param name="bgra">Image (stride = width * 4).</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="region">Region.</param>
    public static double PureBlackFraction(byte[] bgra, int width, int height, RectI region)
    {
        if (Clip(region, width, height) is not { } r || !Fits(bgra, width, height))
        {
            return double.NaN;
        }

        long black = 0;
        for (var y = r.Top; y < r.Bottom; y++)
        {
            for (var x = r.Left; x < r.Right; x++)
            {
                var i = ((y * width) + x) * 4;
                if (bgra[i] == 0 && bgra[i + 1] == 0 && bgra[i + 2] == 0)
                {
                    black++;
                }
            }
        }

        return (double)black / r.Area;
    }

    /// <summary>The part of the client lying on a monitor, in client coordinates, or null if none.</summary>
    /// <param name="monitor">Monitor bounds (screen).</param>
    /// <param name="clientOrigin">Client origin on screen.</param>
    /// <param name="client">Client size.</param>
    public static RectI? ClientPartOn(RectI monitor, PointI clientOrigin, SizeI client) =>
        monitor.Offset(-clientOrigin.X, -clientOrigin.Y).Intersect(new RectI(0, 0, client.Width, client.Height));

    /// <summary>
    /// Where the client starts inside a Windows.Graphics.Capture frame of a window. The capture covers the window's
    /// visible bounds (DWM extended frame bounds); if the content size matches the window rect instead, that is used.
    /// </summary>
    /// <param name="content">Frame content size.</param>
    /// <param name="windowRect">Window rect at capture time.</param>
    /// <param name="frameBounds">DWM extended frame bounds at capture time.</param>
    /// <param name="clientOrigin">Client origin on screen at capture time.</param>
    public static (PointI Offset, string Basis) ClientOffsetInCapture(SizeI content, RectI windowRect, RectI? frameBounds, PointI clientOrigin)
    {
        if (frameBounds is { } fb && fb.Size == content)
        {
            return (new PointI(clientOrigin.X - fb.Left, clientOrigin.Y - fb.Top), "frameBounds");
        }

        if (windowRect.Size == content)
        {
            return (new PointI(clientOrigin.X - windowRect.Left, clientOrigin.Y - windowRect.Top), "windowRect");
        }

        return frameBounds is { } f
            ? (new PointI(clientOrigin.X - f.Left, clientOrigin.Y - f.Top), "frameBounds(sizeMismatch)")
            : (new PointI(clientOrigin.X - windowRect.Left, clientOrigin.Y - windowRect.Top), "windowRect(sizeMismatch)");
    }

    /// <summary>
    /// Copies the client out of a larger capture (alpha forced to 255); null when the client does not fit inside the
    /// source at <paramref name="offset"/> or the source buffer is too short.
    /// </summary>
    /// <param name="source">Source pixels (BGRA).</param>
    /// <param name="sourceWidth">Source width.</param>
    /// <param name="sourceHeight">Source height.</param>
    /// <param name="sourceStride">Source bytes per row.</param>
    /// <param name="offset">Client top-left inside the source.</param>
    /// <param name="client">Client size.</param>
    public static byte[]? CropClient(byte[] source, int sourceWidth, int sourceHeight, int sourceStride, PointI offset, SizeI client)
    {
        if (client.Width <= 0 || client.Height <= 0 || offset.X < 0 || offset.Y < 0
            || offset.X + client.Width > sourceWidth || offset.Y + client.Height > sourceHeight
            || sourceStride < sourceWidth * 4 || (long)sourceStride * sourceHeight > source.Length)
        {
            return null;
        }

        var row = client.Width * 4;
        var pixels = new byte[row * client.Height];
        for (var y = 0; y < client.Height; y++)
        {
            Array.Copy(source, ((offset.Y + y) * sourceStride) + (offset.X * 4), pixels, y * row, row);
        }

        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        return pixels;
    }

    private static RectI? Clip(RectI region, int width, int height) => region.Intersect(new RectI(0, 0, width, height));

    private static bool Fits(byte[] bgra, int width, int height) => width > 0 && height > 0 && bgra.Length >= (long)width * height * 4;
}
