using Companion.Contracts;
using Companion.Contracts.Vision;

namespace Companion.Perception.Internal;

/// <summary>
/// Glyph segmentation shared by the digit reader and the glyph cutter: Otsu binarisation (the minority class is ink),
/// split at empty columns, specks dropped. Pure.
/// </summary>
internal static class GlyphSegmenter
{
    /// <summary>Ink mask of an image (true = ink).</summary>
    public static bool[] InkMask(GrayImage img)
    {
        var t = OcrImageOps.Otsu(img);
        var dark = 0;
        foreach (var p in img.Pixels)
        {
            if (p <= t)
            {
                dark++;
            }
        }

        var inkIsDark = dark * 2 <= img.Pixels.Length;
        var mask = new bool[img.Pixels.Length];
        for (var i = 0; i < mask.Length; i++)
        {
            mask[i] = inkIsDark ? img.Pixels[i] <= t : img.Pixels[i] > t;
        }

        return mask;
    }

    /// <summary>Ink boxes of the glyphs, left to right (exclusive right/bottom), with their ink pixel counts.</summary>
    public static List<(RectI Box, int Ink)> Boxes(GrayImage img, bool[] ink)
    {
        var cols = new bool[img.Width];
        for (var x = 0; x < img.Width; x++)
        {
            for (var y = 0; y < img.Height; y++)
            {
                if (ink[y * img.Width + x])
                {
                    cols[x] = true;
                    break;
                }
            }
        }

        var boxes = new List<(RectI Box, int Ink)>();
        for (var x = 0; x < img.Width; x++)
        {
            if (!cols[x])
            {
                continue;
            }

            var start = x;
            while (x < img.Width && cols[x])
            {
                x++;
            }

            int y0 = int.MaxValue, y1 = -1, count = 0;
            for (var y = 0; y < img.Height; y++)
            {
                for (var xx = start; xx < x; xx++)
                {
                    if (!ink[y * img.Width + xx])
                    {
                        continue;
                    }

                    count++;
                    y0 = Math.Min(y0, y);
                    y1 = Math.Max(y1, y);
                }
            }

            if (y1 >= 0)
            {
                boxes.Add((new RectI(start, y0, x, y1 + 1), count));
            }
        }

        if (boxes.Count == 0)
        {
            return boxes;
        }

        // drop specks: much shorter than the tallest glyph and tiny
        var tallest = boxes.Max(b => b.Box.Height);
        return boxes.Where(b => b.Box.Height * 3 >= tallest || b.Ink >= 6).ToList();
    }
}
