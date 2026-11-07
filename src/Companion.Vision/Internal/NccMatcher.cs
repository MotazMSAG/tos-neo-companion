using System.Numerics;
using Companion.Contracts;
using Companion.Contracts.Vision;

namespace Companion.Vision.Internal;

/// <summary>Scores for every candidate template centre in a rectangle of centres.</summary>
/// <param name="Centers">Centre positions covered (exclusive right/bottom), in frame coordinates.</param>
/// <param name="Scores">Row-major NCC scores in [-1, 1].</param>
internal sealed record ScoreMap(RectI Centers, float[] Scores)
{
    /// <summary>Score at a centre inside <see cref="Centers"/>.</summary>
    public float At(int cx, int cy) => Scores[(cy - Centers.Top) * Centers.Width + (cx - Centers.Left)];
}

/// <summary>A scored candidate.</summary>
internal readonly record struct Peak(PointI Center, double Score);

/// <summary>
/// Masked normalised cross-correlation on grayscale. For each centre:
/// score = sum(m*t'*g) / sqrt(sum(t'^2) * (sum(m*g^2) - sum(m*g)^2 / n)), with t' the template minus its masked mean.
/// Row dot products run on <see cref="Vector{T}"/> in float, rows accumulate in double. Windows whose variance is
/// below <see cref="MinWindowStd"/> score 0 (zero-variance guard: a flat area never matches).
/// </summary>
internal static class NccMatcher
{
    /// <summary>Minimum image-window standard deviation (gray levels) for a non-zero score.</summary>
    public const double MinWindowStd = 2.0;

    /// <summary>
    /// Scores every centre in <paramref name="wanted"/> (clipped so the template stays inside the image).
    /// Returns null when no centre fits.
    /// </summary>
    public static ScoreMap? Compute(BgraImage image, TemplateImage t, RectI wanted)
    {
        var tw = t.Width;
        var th = t.Height;
        var hx = tw / 2;
        var hy = th / 2;

        // centres whose template rect lies fully inside the image
        var valid = new RectI(hx, hy, image.Width - tw + hx + 1, image.Height - th + hy + 1);
        if (valid.Intersect(wanted) is not { } centers)
        {
            return null;
        }

        var area = new RectI(centers.Left - hx, centers.Top - hy, centers.Right - 1 - hx + tw, centers.Bottom - 1 - hy + th);
        var g = GrayOps.ToGrayFloat(image, area);
        var g2 = new float[g.Length];
        for (var i = 0; i < g.Length; i++)
        {
            g2[i] = g[i] * g[i];
        }

        var aw = area.Width;
        var n = (double)t.ActiveCount;
        var minVar = n * MinWindowStd * MinWindowStd;
        var scores = new float[centers.Width * centers.Height];
        var zm = t.ZeroMean;
        var mask = t.Mask;

        for (var cy = 0; cy < centers.Height; cy++)
        {
            for (var cx = 0; cx < centers.Width; cx++)
            {
                double c = 0, s1 = 0, s2 = 0;
                for (var r = 0; r < th; r++)
                {
                    var o = (cy + r) * aw + cx;
                    var gs = new ReadOnlySpan<float>(g, o, tw);
                    var g2s = new ReadOnlySpan<float>(g2, o, tw);
                    var ts = new ReadOnlySpan<float>(zm, r * tw, tw);
                    var ms = new ReadOnlySpan<float>(mask, r * tw, tw);
                    Dot3(ts, ms, gs, g2s, out var dc, out var d1, out var d2);
                    c += dc;
                    s1 += d1;
                    s2 += d2;
                }

                var variance = s2 - s1 * s1 / n;
                double score = 0;
                if (variance > minVar)
                {
                    score = c / Math.Sqrt(t.Energy * variance);
                    score = Math.Clamp(score, -1.0, 1.0);
                }

                scores[cy * centers.Width + cx] = (float)score;
            }
        }

        return new ScoreMap(centers, scores);
    }

    /// <summary>Best score whose centre lies within <paramref name="radius"/> (Chebyshev) of <paramref name="around"/>.</summary>
    public static Peak? Best(ScoreMap map, PointI around, int radius)
    {
        Peak? best = null;
        var c = map.Centers;
        for (var y = Math.Max(c.Top, around.Y - radius); y < Math.Min(c.Bottom, around.Y + radius + 1); y++)
        {
            for (var x = Math.Max(c.Left, around.X - radius); x < Math.Min(c.Right, around.X + radius + 1); x++)
            {
                var s = map.At(x, y);
                if (best is null || s > best.Value.Score || (s == best.Value.Score && Closer(new PointI(x, y), best.Value.Center, around)))
                {
                    best = new Peak(new PointI(x, y), s);
                }
            }
        }

        return best;
    }

    /// <summary>Best score anywhere in the map.</summary>
    public static Peak? BestAnywhere(ScoreMap map, PointI around) => Best(map, around, 1 << 20);

    /// <summary>
    /// Highest local maximum (8-neighbourhood) farther than <paramref name="nmsRadius"/> from <paramref name="peak"/>;
    /// 0 when there is none. Shoulders of the main peak are not local maxima, so they do not count.
    /// </summary>
    public static double SecondBest(ScoreMap map, PointI peak, int nmsRadius)
    {
        double second = 0;
        var c = map.Centers;
        for (var y = c.Top; y < c.Bottom; y++)
        {
            for (var x = c.Left; x < c.Right; x++)
            {
                var p = new PointI(x, y);
                if (p.ChebyshevDistanceTo(peak) <= nmsRadius)
                {
                    continue;
                }

                var s = map.At(x, y);
                if (s <= second || !IsLocalMax(map, x, y, s))
                {
                    continue;
                }

                second = s;
            }
        }

        return second;
    }

    private static bool IsLocalMax(ScoreMap map, int x, int y, float s)
    {
        var c = map.Centers;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                var nx = x + dx;
                var ny = y + dy;
                if (nx < c.Left || ny < c.Top || nx >= c.Right || ny >= c.Bottom)
                {
                    continue;
                }

                if (map.At(nx, ny) > s)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool Closer(PointI a, PointI b, PointI to) => a.ChebyshevDistanceTo(to) < b.ChebyshevDistanceTo(to);

    private static void Dot3(ReadOnlySpan<float> t, ReadOnlySpan<float> m, ReadOnlySpan<float> g, ReadOnlySpan<float> g2, out float c, out float s1, out float s2)
    {
        var i = 0;
        var n = t.Length;
        float rc = 0, r1 = 0, r2 = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var vc = Vector<float>.Zero;
            var v1 = Vector<float>.Zero;
            var v2 = Vector<float>.Zero;
            var w = Vector<float>.Count;
            for (; i <= n - w; i += w)
            {
                var vg = new Vector<float>(g.Slice(i, w));
                var vm = new Vector<float>(m.Slice(i, w));
                vc += new Vector<float>(t.Slice(i, w)) * vg;
                v1 += vm * vg;
                v2 += vm * new Vector<float>(g2.Slice(i, w));
            }

            rc = Vector.Sum(vc);
            r1 = Vector.Sum(v1);
            r2 = Vector.Sum(v2);
        }

        for (; i < n; i++)
        {
            rc += t[i] * g[i];
            r1 += m[i] * g[i];
            r2 += m[i] * g2[i];
        }

        c = rc;
        s1 = r1;
        s2 = r2;
    }
}
