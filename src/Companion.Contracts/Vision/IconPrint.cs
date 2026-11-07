namespace Companion.Contracts.Vision;

/// <summary>
/// The 256-bit perceptual fingerprint of an item icon's interior, shared by the readers (unit "vision-ocr": the Make
/// tab's material tiles, the Stock tab's grid) and the features' list scans (the Make tab's recipe cards, 2026-10-15),
/// so every module computes it the same way (moved here from Companion.Perception's IconFingerprint, unchanged). Four
/// 64-bit parts on an 8x8 grid of cell means (9x8 for the gradient) over the interior rect:
/// <list type="bullet">
/// <item><see cref="Compute"/>: luma above the interior mean, the luma gradient (dHash), then two chroma axes (R-B, G-R)
/// above their interior means (colour relative to the icon itself);</item>
/// <item><see cref="ComputeWithHue"/>: the same luma parts, then per cell R &gt; G and B &gt; R (the absolute hue: one
/// item family draws its icon in several colours).</item>
/// </list>
/// Lower-case hex, no prefix. Pure functions of the frame; thread-safe. The rect must lie inside the frame.
/// </summary>
public static class IconPrint
{
    /// <summary>Fingerprint size in bits (64 hex characters).</summary>
    public const int Bits = 256;

    /// <summary>The fingerprint with colour relative to the interior's own means (lower-case hex).</summary>
    public static string Compute(Frame frame, RectI interior)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var (lum8, rb8, gr8) = Grid(frame, interior, 8, 8);
        var (lum9, _, _) = Grid(frame, interior, 9, 8);
        var bits = new bool[Bits];
        var mLum = lum8.Average();
        var mRb = rb8.Average();
        var mGr = gr8.Average();
        for (var i = 0; i < 64; i++)
        {
            bits[i] = lum8[i] > mLum;
            bits[128 + i] = rb8[i] > mRb;
            bits[192 + i] = gr8[i] > mGr;
        }

        Gradient(lum9, bits);
        return Hex(bits);
    }

    /// <summary>The fingerprint that keeps the absolute hue (lower-case hex), see the class remarks.</summary>
    public static string ComputeWithHue(Frame frame, RectI interior)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var (lum8, rb8, gr8) = Grid(frame, interior, 8, 8);
        var (lum9, _, _) = Grid(frame, interior, 9, 8);
        var bits = new bool[Bits];
        var mLum = lum8.Average();
        for (var i = 0; i < 64; i++)
        {
            bits[i] = lum8[i] > mLum;
            bits[128 + i] = gr8[i] < 0; // R > G
            bits[192 + i] = rb8[i] < 0; // B > R
        }

        Gradient(lum9, bits);
        return Hex(bits);
    }

    /// <summary>Bits 64..127: the luma gradient between horizontal neighbours of the 9x8 grid.</summary>
    private static void Gradient(double[] lum9, bool[] bits)
    {
        for (var r = 0; r < 8; r++)
        {
            for (var c = 0; c < 8; c++)
            {
                bits[64 + (r * 8) + c] = lum9[(r * 9) + c] > lum9[(r * 9) + c + 1];
            }
        }
    }

    private static string Hex(bool[] bits)
    {
        var bytes = new byte[Bits / 8];
        for (var i = 0; i < Bits; i++)
        {
            if (bits[i])
            {
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Cell means (luma, R-B, G-R) over a cols x rows grid of the rect.</summary>
    private static (double[] Luma, double[] Rb, double[] Gr) Grid(Frame frame, RectI r, int cols, int rows)
    {
        var luma = new double[cols * rows];
        var rb = new double[cols * rows];
        var gr = new double[cols * rows];
        for (var cy = 0; cy < rows; cy++)
        {
            var y0 = r.Top + (cy * r.Height / rows);
            var y1 = r.Top + ((cy + 1) * r.Height / rows);
            for (var cx = 0; cx < cols; cx++)
            {
                var x0 = r.Left + (cx * r.Width / cols);
                var x1 = r.Left + ((cx + 1) * r.Width / cols);
                double sl = 0, srb = 0, sgr = 0;
                var n = 0;
                for (var y = y0; y < y1; y++)
                {
                    var row = y * frame.Stride;
                    for (var x = x0; x < x1; x++)
                    {
                        var o = row + (x * 4);
                        int b = frame.Bgra[o], g = frame.Bgra[o + 1], red = frame.Bgra[o + 2];
                        sl += ((b * 29) + (g * 150) + (red * 77) + 128) >> 8;
                        srb += red - b;
                        sgr += g - red;
                        n++;
                    }
                }

                var k = (cy * cols) + cx;
                if (n > 0)
                {
                    luma[k] = sl / n;
                    rb[k] = srb / n;
                    gr[k] = sgr / n;
                }
            }
        }

        return (luma, rb, gr);
    }
}
