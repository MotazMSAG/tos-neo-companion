using Companion.Contracts;
using Companion.Contracts.Vision;

namespace Companion.Vision.Internal;

/// <summary>Grayscale helpers (Rec.601 luma, integer arithmetic).</summary>
internal static class GrayOps
{
    /// <summary>Luma of one BGRA pixel.</summary>
    public static byte Luma(byte b, byte g, byte r) => (byte)((b * 29 + g * 150 + r * 77 + 128) >> 8);

    /// <summary>Gray copy of a region (must lie inside the image).</summary>
    public static GrayImage ToGray(BgraImage img, RectI region)
    {
        var px = new byte[region.Width * region.Height];
        for (var y = 0; y < region.Height; y++)
        {
            var row = img.Data.AsSpan((region.Top + y) * img.Stride + region.Left * 4, region.Width * 4);
            var o = y * region.Width;
            for (var x = 0; x < region.Width; x++)
            {
                px[o + x] = Luma(row[x * 4], row[x * 4 + 1], row[x * 4 + 2]);
            }
        }

        return new GrayImage(region.Width, region.Height, px);
    }

    /// <summary>Gray float plane of a region (must lie inside the image).</summary>
    public static float[] ToGrayFloat(BgraImage img, RectI region)
    {
        var px = new float[region.Width * region.Height];
        for (var y = 0; y < region.Height; y++)
        {
            var row = img.Data.AsSpan((region.Top + y) * img.Stride + region.Left * 4, region.Width * 4);
            var o = y * region.Width;
            for (var x = 0; x < region.Width; x++)
            {
                px[o + x] = Luma(row[x * 4], row[x * 4 + 1], row[x * 4 + 2]);
            }
        }

        return px;
    }

    /// <summary>Bilinear resize (pixel-centre aligned). Same size returns a copy.</summary>
    public static GrayImage Resize(GrayImage src, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "target size must be positive");
        }

        if (src.Width == 0 || src.Height == 0)
        {
            return new GrayImage(0, 0, []);
        }

        if (width == src.Width && height == src.Height)
        {
            return new GrayImage(width, height, (byte[])src.Pixels.Clone());
        }

        var dst = new byte[width * height];
        var sx = (double)src.Width / width;
        var sy = (double)src.Height / height;
        for (var y = 0; y < height; y++)
        {
            var fy = Math.Clamp((y + 0.5) * sy - 0.5, 0, src.Height - 1);
            var y0 = (int)fy;
            var y1 = Math.Min(y0 + 1, src.Height - 1);
            var wy = fy - y0;
            for (var x = 0; x < width; x++)
            {
                var fx = Math.Clamp((x + 0.5) * sx - 0.5, 0, src.Width - 1);
                var x0 = (int)fx;
                var x1 = Math.Min(x0 + 1, src.Width - 1);
                var wx = fx - x0;
                var top = src.Pixels[y0 * src.Width + x0] * (1 - wx) + src.Pixels[y0 * src.Width + x1] * wx;
                var bottom = src.Pixels[y1 * src.Width + x0] * (1 - wx) + src.Pixels[y1 * src.Width + x1] * wx;
                dst[y * width + x] = (byte)Math.Clamp((int)Math.Round(top * (1 - wy) + bottom * wy), 0, 255);
            }
        }

        return new GrayImage(width, height, dst);
    }
}
