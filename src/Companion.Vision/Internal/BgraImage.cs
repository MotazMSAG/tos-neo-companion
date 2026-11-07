using Companion.Contracts;
using Companion.Contracts.Vision;

namespace Companion.Vision.Internal;

/// <summary>A plain 32bpp BGRA image (top-down, straight alpha). Internal working type.</summary>
internal sealed record BgraImage(int Width, int Height, int Stride, byte[] Data)
{
    /// <summary>Creates a zeroed image.</summary>
    public static BgraImage Create(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "image size must be positive");
        }

        return new BgraImage(width, height, width * 4, new byte[checked(width * height * 4)]);
    }

    /// <summary>Wraps a frame's pixels (no copy).</summary>
    public static BgraImage FromFrame(Frame frame) => new(frame.Width, frame.Height, frame.Stride, frame.Bgra);

    /// <summary>True if the buffer is large enough for the declared geometry.</summary>
    public bool IsConsistent => Width > 0 && Height > 0 && Stride >= Width * 4 && Data.Length >= (long)Stride * Height;

    /// <summary>Full bounds.</summary>
    public RectI Bounds => new(0, 0, Width, Height);

    /// <summary>Fills the whole image with one colour.</summary>
    public void Fill(byte b, byte g, byte r, byte a = 255)
    {
        for (var y = 0; y < Height; y++)
        {
            var row = Data.AsSpan(y * Stride, Width * 4);
            for (var x = 0; x < row.Length; x += 4)
            {
                row[x] = b;
                row[x + 1] = g;
                row[x + 2] = r;
                row[x + 3] = a;
            }
        }
    }

    /// <summary>Copies <paramref name="src"/> into this image with its top-left at (x, y), clipped.</summary>
    public void Paste(BgraImage src, int x, int y)
    {
        var dst = Bounds.Intersect(RectI.FromXywh(x, y, src.Width, src.Height));
        if (dst is not { } r)
        {
            return;
        }

        for (var row = r.Top; row < r.Bottom; row++)
        {
            var s = src.Data.AsSpan((row - y) * src.Stride + (r.Left - x) * 4, r.Width * 4);
            s.CopyTo(Data.AsSpan(row * Stride + r.Left * 4, r.Width * 4));
        }
    }

    /// <summary>Returns a copy of a region (must lie inside the image).</summary>
    public BgraImage CropCopy(RectI region)
    {
        if (region.IsEmpty || region.Left < 0 || region.Top < 0 || region.Right > Width || region.Bottom > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "region must lie inside the image");
        }

        var dst = Create(region.Width, region.Height);
        for (var row = 0; row < region.Height; row++)
        {
            Data.AsSpan((region.Top + row) * Stride + region.Left * 4, region.Width * 4)
                .CopyTo(dst.Data.AsSpan(row * dst.Stride, region.Width * 4));
        }

        return dst;
    }

    /// <summary>Sets the colour channels of a region (clipped) to black, alpha unchanged.</summary>
    public void Blackout(RectI region)
    {
        if (Bounds.Intersect(region) is not { } r)
        {
            return;
        }

        for (var row = r.Top; row < r.Bottom; row++)
        {
            var span = Data.AsSpan(row * Stride + r.Left * 4, r.Width * 4);
            for (var i = 0; i < span.Length; i += 4)
            {
                span[i] = 0;
                span[i + 1] = 0;
                span[i + 2] = 0;
            }
        }
    }

    /// <summary>Builds a frame-shaped record around this image (offline sources, tests, tools).</summary>
    public Frame ToFrame() =>
        new(Width, Height, Stride, Data, TimeSpan.Zero, DateTimeOffset.UnixEpoch, null, new PointI(0, 0), false);
}
