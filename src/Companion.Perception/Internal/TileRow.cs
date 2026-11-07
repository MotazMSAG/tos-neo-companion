using Companion.Contracts;
using Companion.Contracts.Vision;

namespace Companion.Perception.Internal;

/// <summary>Result of detecting a row of item tiles.</summary>
/// <param name="Tiles">Tile boxes left to right (outline to outline, exclusive right/bottom), client coordinates.</param>
/// <param name="Code">"tiles", "no-tiles" (plain band), "tiles-not-found" (ink but no tile), "tiles-irregular", "band-outside-frame".</param>
internal readonly record struct TileRowResult(IReadOnlyList<RectI> Tiles, string Code)
{
    /// <summary>True when the band holds a consistent row (possibly empty).</summary>
    public bool Ok => Code is "tiles" or "no-tiles";
}

/// <summary>
/// Detects a row of item tiles in a horizontal band (<see cref="Contracts.Perception.ParseRule.TileRow"/>, added
/// 2026-10-14: the Make tab centres 3 or 4 material tiles, so their slots shift with the count). Thresholds are RELATIVE
/// to the band's background (its median column level), so a full-scale capture and an upscaled half-scale one behave
/// alike. A tile's left and right sides are a dark outline (at least <see cref="EdgeContrast"/> under the background) with
/// a white border just inside (at least <see cref="BorderLift"/> over it) and background just outside; its top outline
/// has background above it, its bottom outline the white border above it (a drop shadow lies under the tile). Measured on
/// proofs\out\live_ability_make_full.png: background 229-231, outline 117-136, side border 243-247; the half-scale
/// c14_after.png upscaled: outline 154-185, border 235-255. Each side is the darkest line of its cluster; icon content
/// inside may repeat the pattern, so only boxes about as tall as the band and about as wide as tall (87x89) count.
/// Accepted rows have equal tile sizes, are centred on the band (the game centres them: a row off centre is missing a
/// tile) and, from three tiles on, have equal spacing. Pure; thread-safe.
/// </summary>
internal static class TileRow
{
    /// <summary>The outline is at least this much darker than the band's background.</summary>
    internal const int EdgeContrast = 25;

    /// <summary>The white border just inside the outline is at least this much brighter than the background.</summary>
    internal const int BorderLift = 4;

    /// <summary>The line just outside an edge is background: at most this much darker...</summary>
    internal const int OutsideSlack = 14;

    /// <summary>...and less than this much brighter (an upscaled capture's resampling ring reaches +5; the white border +13 and more).</summary>
    internal const int OutsideLift = 9;

    /// <summary>How far inside an edge the white border is searched.</summary>
    internal const int BorderReach = 5;

    /// <summary>Distance of the outside probe from an edge.</summary>
    internal const int OutsideProbe = 3;

    /// <summary>Size tolerance (tile vs band, tiles vs each other).</summary>
    internal const int SizeTolerance = 4;

    /// <summary>Spacing tolerance between consecutive tiles.</summary>
    internal const int PitchTolerance = 3;

    /// <summary>How far above/below the band the top/bottom outline is searched.</summary>
    internal const int RowSearch = 3;

    /// <summary>Margin around the band read for the outside probes.</summary>
    private const int Margin = 8;

    /// <summary>A band with fewer dark pixels (40 under the background) than this fraction is plain: no tiles at all.</summary>
    internal const double PlainDarkFraction = 0.003;

    /// <summary>Detects the tiles of a band (client coordinates).</summary>
    public static TileRowResult Detect(Frame frame, RectI band)
    {
        var bounds = new RectI(0, 0, frame.Width, frame.Height);
        if (band.Height < 27 || band.Width < band.Height || bounds.Intersect(band) != band
            || frame.Bgra.Length < frame.Stride * frame.Height || frame.Stride < frame.Width * 4)
        {
            return new TileRowResult([], "band-outside-frame");
        }

        var window = bounds.Intersect(band.Inflate(Margin))!.Value;
        var luma = Luma(frame, window);
        int L(int x, int y) => luma[(y - window.Top) * window.Width + (x - window.Left)];

        // Column medians over the band's middle half (corners, badges and labels stay out).
        var midTop = band.Top + band.Height / 4;
        var midBottom = band.Bottom - band.Height / 4;
        var colMed = new int[window.Width];
        var buffer = new int[midBottom - midTop];
        for (var x = window.Left; x < window.Right; x++)
        {
            for (var y = midTop; y < midBottom; y++)
            {
                buffer[y - midTop] = L(x, y);
            }

            colMed[x - window.Left] = Median(buffer);
        }

        // The background is the band's upper-quartile column level: tiles (dark interiors) cover up to half of it.
        var bandCols = colMed.Skip(band.Left - window.Left).Take(band.Width).ToArray();
        var bg = Percentile(bandCols, 0.75);
        int C(int x) => x < window.Left || x >= window.Right ? bg : colMed[x - window.Left];
        bool Background(int v) => v >= bg - OutsideSlack && v < bg + OutsideLift;

        bool IsLeft(int x) => C(x) <= bg - EdgeContrast && Background(C(x - OutsideProbe))
            && Enumerable.Range(1, BorderReach).Any(d => C(x + d) >= bg + BorderLift);
        bool IsRight(int x) => C(x) <= bg - EdgeContrast && Background(C(x + OutsideProbe))
            && Enumerable.Range(1, BorderReach).Any(d => C(x - d) >= bg + BorderLift);

        var lefts = Darkest(Enumerable.Range(band.Left, band.Width).Where(IsLeft), C);
        var rights = Darkest(Enumerable.Range(band.Left, band.Width).Where(IsRight), C);
        var expectedWidth = band.Height - 2;
        var tiles = new List<RectI>();
        var after = int.MinValue;
        foreach (var left in lefts)
        {
            if (left <= after)
            {
                continue;
            }

            foreach (var right in rights)
            {
                if (right <= left || Math.Abs(right + 1 - left - expectedWidth) > SizeTolerance)
                {
                    continue;
                }

                if (Rows(L, window, band, bg, left, right) is { } rows)
                {
                    tiles.Add(new RectI(left, rows.Top, right + 1, rows.Bottom + 1));
                    after = right;
                }

                break;
            }
        }

        if (tiles.Count == 0)
        {
            var dark = 0;
            for (var y = band.Top; y < band.Bottom; y++)
            {
                for (var x = band.Left; x < band.Right; x++)
                {
                    dark += L(x, y) <= bg - 40 ? 1 : 0;
                }
            }

            return new TileRowResult([], dark < PlainDarkFraction * band.Area ? "no-tiles" : "tiles-not-found");
        }

        var w0 = tiles[0].Width;
        var h0 = tiles[0].Height;
        if (tiles.Any(t => Math.Abs(t.Width - w0) > PitchTolerance || Math.Abs(t.Height - h0) > PitchTolerance))
        {
            return new TileRowResult(tiles, "tiles-irregular");
        }

        // The game centres the row on the band's centre (3 and 4 materials alike): a row off centre misses a tile.
        var centre = (tiles[0].Left + tiles[^1].Right) / 2.0;
        if (Math.Abs(centre - ((band.Left + band.Right) / 2.0)) > expectedWidth / 4.0)
        {
            return new TileRowResult(tiles, "tiles-irregular");
        }

        if (tiles.Count >= 3)
        {
            var pitch = tiles[1].Left - tiles[0].Left;
            for (var k = 2; k < tiles.Count; k++)
            {
                if (Math.Abs(tiles[k].Left - tiles[k - 1].Left - pitch) > PitchTolerance)
                {
                    return new TileRowResult(tiles, "tiles-irregular");
                }
            }
        }

        return new TileRowResult(tiles, "tiles");
    }

    /// <summary>The top and bottom outline rows of a tile between two found edges, or null.</summary>
    private static (int Top, int Bottom)? Rows(Func<int, int, int> l, RectI window, RectI band, int bg, int left, int right)
    {
        var w = right + 1 - left;
        var x0 = left + w / 4;
        var x1 = right + 1 - w / 4;
        var buffer = new int[x1 - x0];
        int R(int y)
        {
            if (y < window.Top || y >= window.Bottom)
            {
                return bg;
            }

            for (var x = x0; x < x1; x++)
            {
                buffer[x - x0] = l(x, y);
            }

            return Median(buffer);
        }

        // The top border is parchment-coloured and a drop shadow lies under the tile (measured on both captures): the
        // top outline has background (or brighter) above it, the bottom outline the white border above it.
        bool IsTop(int y) => R(y) <= bg - EdgeContrast && R(y - OutsideProbe) >= bg - OutsideSlack;
        bool IsBottom(int y) => R(y) <= bg - EdgeContrast && Enumerable.Range(1, BorderReach).Any(d => R(y - d) >= bg + BorderLift);

        var tops = Darkest(Enumerable.Range(band.Top - RowSearch, (2 * RowSearch) + 1).Where(IsTop), R);
        var bottoms = Darkest(Enumerable.Range(band.Bottom - 1 - RowSearch, (2 * RowSearch) + 1).Where(IsBottom), R);
        int? top = tops.Count > 0 ? tops[0] : null;
        int? bottom = bottoms.Count > 0 ? bottoms[^1] : null;

        if (top is null || bottom is null || Math.Abs(bottom.Value + 1 - top.Value - band.Height) > SizeTolerance)
        {
            return null;
        }

        return (top.Value, bottom.Value);
    }

    /// <summary>
    /// Clusters adjacent candidates (within 2 px) and keeps the darkest line of each cluster (the outline itself, not its
    /// anti-aliased neighbour), so the box is the same as the fixed-slot reader's on a full-scale capture.
    /// </summary>
    private static List<int> Darkest(IEnumerable<int> candidates, Func<int, int> level)
    {
        var result = new List<int>();
        var cluster = new List<int>();
        void Flush()
        {
            if (cluster.Count > 0)
            {
                result.Add(cluster.OrderBy(level).ThenBy(v => v).First());
                cluster.Clear();
            }
        }

        foreach (var v in candidates)
        {
            if (cluster.Count > 0 && v - cluster[^1] > 2)
            {
                Flush();
            }

            cluster.Add(v);
        }

        Flush();
        return result;
    }

    private static int Percentile(int[] values, double q)
    {
        var copy = (int[])values.Clone();
        Array.Sort(copy);
        return copy.Length == 0 ? 0 : copy[Math.Clamp((int)(q * (copy.Length - 1)), 0, copy.Length - 1)];
    }

    private static int Median(int[] values)
    {
        var copy = (int[])values.Clone();
        Array.Sort(copy);
        return copy.Length == 0 ? 0 : copy[copy.Length / 2];
    }

    private static int[] Luma(Frame frame, RectI r)
    {
        var plane = new int[r.Width * r.Height];
        for (var y = 0; y < r.Height; y++)
        {
            var row = (r.Top + y) * frame.Stride;
            for (var x = 0; x < r.Width; x++)
            {
                var o = row + (r.Left + x) * 4;
                plane[y * r.Width + x] = (frame.Bgra[o] * 29 + frame.Bgra[o + 1] * 150 + frame.Bgra[o + 2] * 77 + 128) >> 8;
            }
        }

        return plane;
    }
}
