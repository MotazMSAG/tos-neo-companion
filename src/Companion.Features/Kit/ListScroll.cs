using Companion.Contracts;
using Companion.Contracts.Vision;

namespace Companion.Features.Kit;

/// <summary>How far the list content moved between two captures.</summary>
/// <param name="Known">The best vertical shift is clear (low error, well under any other shift's).</param>
/// <param name="Dy">Content shift in pixels (negative = moved up, as after dragging up).</param>
/// <param name="Error">Mean absolute row-signature difference at that shift.</param>
/// <param name="Next">The best error at least 30 px away from it.</param>
internal readonly record struct ListShift(bool Known, int Dy, double Error, double Next)
{
    /// <summary>The list did not move (end of the list, or a drag that did nothing).</summary>
    public bool Unchanged => Known && Math.Abs(Dy) <= 2;
}

/// <summary>
/// The shift measurement of a scrolling list, shared by the Make tab's recipe list (<see cref="RecipeList"/>) and the
/// Stock tab's grid (<see cref="StockGrid"/>): per row of the list's viewport a signature of band means (luma), compared
/// over the overlap (at least 120 rows) for every shift up to <see cref="MaxShift"/>. Known when the best error is at
/// most 6 and clearly under the best one 30+ px away. Pure functions of a frame; no I/O.
/// </summary>
internal static class ListScroll
{
    /// <summary>Largest shift searched between two captures.</summary>
    internal const int MaxShift = 450;

    /// <summary>
    /// The vertical shift of the content of <paramref name="viewport"/> from <paramref name="before"/> to
    /// <paramref name="after"/>, the row signature made of <paramref name="bands"/> band means across x
    /// <paramref name="x0"/>..<paramref name="x1"/>. Unknown for frames that are not 1920x1009.
    /// </summary>
    internal static ListShift Measure(Frame before, Frame after, RectI viewport, int x0, int x1, int bands)
    {
        if (!Valid(before) || !Valid(after) || bands < 1 || x1 - x0 < bands || viewport.Height < 121)
        {
            return new ListShift(false, 0, double.MaxValue, double.MaxValue);
        }

        var a = Signature(before, viewport, x0, x1, bands);
        var b = Signature(after, viewport, x0, x1, bands);
        var n = a.Length;
        var errors = new Dictionary<int, double>();
        for (var dy = -MaxShift; dy <= MaxShift; dy++)
        {
            // after[y] shows what before showed at y - dy
            var from = Math.Max(0, dy);
            var to = Math.Min(n, n + dy);
            if (to - from < 120)
            {
                continue;
            }

            double sum = 0;
            for (var y = from; y < to; y++)
            {
                var p = b[y];
                var q = a[y - dy];
                for (var k = 0; k < p.Length; k++)
                {
                    sum += Math.Abs(p[k] - q[k]);
                }
            }

            errors[dy] = sum / ((to - from) * a[0].Length);
        }

        if (errors.Count == 0)
        {
            return new ListShift(false, 0, double.MaxValue, double.MaxValue);
        }

        var best = errors.MinBy(e => e.Value);
        var next = errors.Where(e => Math.Abs(e.Key - best.Key) >= 30).Select(e => e.Value).DefaultIfEmpty(double.MaxValue).Min();
        var known = best.Value <= 6 && next >= (best.Value * 1.5) + 0.5;
        return new ListShift(known, best.Key, Math.Round(best.Value, 3), Math.Round(next, 3));
    }

    private static double[][] Signature(Frame f, RectI viewport, int x0, int x1, int bands)
    {
        var rows = new double[viewport.Height][];
        for (var y = 0; y < rows.Length; y++)
        {
            var row = new double[bands];
            for (var k = 0; k < bands; k++)
            {
                var from = x0 + (k * (x1 - x0) / bands);
                var to = x0 + ((k + 1) * (x1 - x0) / bands);
                double sum = 0;
                for (var x = from; x < to; x++)
                {
                    sum += Luma(f, x, viewport.Top + y);
                }

                row[k] = sum / (to - from);
            }

            rows[y] = row;
        }

        return rows;
    }

    private static bool Valid(Frame f) =>
        f.Width == 1920 && f.Height == 1009 && f.Stride >= f.Width * 4 && f.Bgra.Length >= f.Stride * f.Height;

    private static int Luma(Frame f, int x, int y)
    {
        var o = (y * f.Stride) + (x * 4);
        return ((f.Bgra[o] * 29) + (f.Bgra[o + 1] * 150) + (f.Bgra[o + 2] * 77) + 128) >> 8;
    }
}
