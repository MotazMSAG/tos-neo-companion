using Companion.Contracts;
using Companion.Contracts.Vision;

namespace Companion.Perception.Internal;

/// <summary>
/// Cuts digit glyphs out of a full-scale text crop whose text is known (font building and its provenance test): the
/// crop is segmented exactly like the digit reader does, and every glyph is kept as its ink mask (rows of '#' ink and
/// '.' background, tight box). Pure.
/// </summary>
internal static class GlyphCutter
{
    /// <summary>One mask per non-space character of <paramref name="text"/>, or null when the segmentation does not match it.</summary>
    public static IReadOnlyList<(char Char, string Mask)>? Cut(GrayImage region, string text)
    {
        var chars = text.Where(c => !char.IsWhiteSpace(c)).ToArray();
        var ink = GlyphSegmenter.InkMask(region);
        var boxes = GlyphSegmenter.Boxes(region, ink);
        if (boxes.Count != chars.Length)
        {
            return null;
        }

        var result = new List<(char, string)>(chars.Length);
        for (var i = 0; i < chars.Length; i++)
        {
            result.Add((chars[i], MaskRows(ink, region.Width, boxes[i].Box)));
        }

        return result;
    }

    /// <summary>The ink rows of a box ('#' ink, '.' background, rows joined by '|').</summary>
    public static string MaskRows(bool[] ink, int stride, RectI box)
    {
        var rows = new string[box.Height];
        for (var y = 0; y < box.Height; y++)
        {
            var chars = new char[box.Width];
            for (var x = 0; x < box.Width; x++)
            {
                chars[x] = ink[(box.Top + y) * stride + box.Left + x] ? '#' : '.';
            }

            rows[y] = new string(chars);
        }

        return string.Join('|', rows);
    }

    /// <summary>
    /// A glyph image from mask rows: ink 0, background 255, with a background margin so that the reader's Otsu split
    /// keeps the ink as the minority class.
    /// </summary>
    public static GrayImage ToImage(string mask, int margin = 3)
    {
        var rows = mask.Split('|');
        var h = rows.Length;
        var w = rows.Max(r => r.Length);
        if (h == 0 || w == 0 || rows.Any(r => r.Length != w))
        {
            throw new ArgumentException("glyph mask rows must be non-empty and of equal length");
        }

        var fullW = w + 2 * margin;
        var fullH = h + 2 * margin;
        var px = new byte[fullW * fullH];
        Array.Fill(px, (byte)255);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (rows[y][x] == '#')
                {
                    px[(y + margin) * fullW + x + margin] = 0;
                }
            }
        }

        return new GrayImage(fullW, fullH, px);
    }
}
