using Companion.Contracts.Vision;

namespace Companion.Perception.Internal;

/// <summary>OCR preprocessing on grayscale crops (upscale, polarity + contrast stretch, padding) and the Otsu threshold the digit reader uses.</summary>
internal static class OcrImageOps
{
    /// <summary>Bilinear resize (pixel-centre aligned).</summary>
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

    /// <summary>Upscales by a factor (1 = copy).</summary>
    public static GrayImage Upscale(GrayImage src, double factor)
    {
        var f = double.IsFinite(factor) ? Math.Clamp(factor, 1.0, 8.0) : 1.0;
        var w = Math.Max(1, (int)Math.Round(src.Width * f));
        var h = Math.Max(1, (int)Math.Round(src.Height * f));
        return Resize(src, w, h);
    }

    /// <summary>Otsu threshold (pixels &lt;= t are the dark class).</summary>
    public static int Otsu(GrayImage img)
    {
        var hist = new long[256];
        foreach (var p in img.Pixels)
        {
            hist[p]++;
        }

        long total = img.Pixels.Length;
        if (total == 0)
        {
            return 127;
        }

        double sumAll = 0;
        for (var i = 0; i < 256; i++)
        {
            sumAll += i * (double)hist[i];
        }

        double sumB = 0, best = -1;
        long wB = 0;
        var t = 127;
        for (var i = 0; i < 256; i++)
        {
            wB += hist[i];
            if (wB == 0)
            {
                continue;
            }

            var wF = total - wB;
            if (wF == 0)
            {
                break;
            }

            sumB += i * (double)hist[i];
            var mB = sumB / wB;
            var mF = (sumAll - sumB) / wF;
            var between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best)
            {
                best = between;
                t = i;
            }
        }

        return t;
    }

    /// <summary>
    /// Linear contrast stretch between the 2nd and 98th percentiles, optionally inverted so that text ends up dark on
    /// light. Keeps anti-aliasing (Windows OCR reads soft edges better than hard binary ones).
    /// </summary>
    public static GrayImage Stretch(GrayImage img, bool invert)
    {
        if (img.Pixels.Length == 0)
        {
            return img;
        }

        var sorted = (byte[])img.Pixels.Clone();
        Array.Sort(sorted);
        int lo = sorted[(int)(sorted.Length * 0.02)];
        int hi = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.98))];
        if (hi - lo < 8)
        {
            lo = Math.Max(0, lo - 4);
            hi = Math.Min(255, hi + 4);
        }

        var dst = new byte[img.Pixels.Length];
        for (var i = 0; i < dst.Length; i++)
        {
            var v = Math.Clamp((img.Pixels[i] - lo) * 255 / (hi - lo), 0, 255);
            dst[i] = (byte)(invert ? 255 - v : v);
        }

        return new GrayImage(img.Width, img.Height, dst);
    }

    /// <summary>Pads on every side with a constant value.</summary>
    public static GrayImage Pad(GrayImage img, int pad, byte value)
    {
        var w = img.Width + 2 * pad;
        var h = img.Height + 2 * pad;
        var dst = new byte[w * h];
        Array.Fill(dst, value);
        for (var y = 0; y < img.Height; y++)
        {
            Array.Copy(img.Pixels, y * img.Width, dst, (y + pad) * w + pad, img.Width);
        }

        return new GrayImage(w, h, dst);
    }

    /// <summary>Median of the border pixels (background estimate for unbinarised padding).</summary>
    public static byte BorderMedian(GrayImage img)
    {
        if (img.Width == 0 || img.Height == 0)
        {
            return 255;
        }

        var values = new List<byte>();
        for (var x = 0; x < img.Width; x++)
        {
            values.Add(img.Pixels[x]);
            values.Add(img.Pixels[(img.Height - 1) * img.Width + x]);
        }

        for (var y = 0; y < img.Height; y++)
        {
            values.Add(img.Pixels[y * img.Width]);
            values.Add(img.Pixels[y * img.Width + img.Width - 1]);
        }

        values.Sort();
        return values[values.Count / 2];
    }
}
