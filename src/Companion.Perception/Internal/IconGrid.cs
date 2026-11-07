using Companion.Contracts;
using Companion.Contracts.Vision;

namespace Companion.Perception.Internal;

/// <summary>The count text found at a tile's bottom right.</summary>
/// <param name="Image">A clean image of its glyphs only (ink 0 on 255, a 4 px margin), for the digit reader and OCR.</param>
/// <param name="Box">The glyphs' client box.</param>
/// <param name="Glyphs">Glyph count.</param>
internal sealed record CountWord(GrayImage Image, RectI Box, int Glyphs);

/// <summary>One whole tile of an icon grid.</summary>
/// <param name="Box">Outer box (outline to outline, exclusive right/bottom), client coordinates.</param>
/// <param name="Key">"icon:" + the fingerprint of the icon above the count line.</param>
/// <param name="Faded">The tile is drawn faded (washed out, no dark ink): the game shows owned-nothing items so.</param>
/// <param name="Count">The count text at its bottom right, or null when there is none.</param>
internal sealed record GridTile(RectI Box, string Key, bool Faded, CountWord? Count);

/// <summary>Result of reading an icon grid.</summary>
/// <param name="Tiles">Whole tiles, row by row, left to right.</param>
/// <param name="Code">"tiles", "no-tiles" (none whole in the region), "tiles-irregular", "region-outside-frame".</param>
/// <param name="EndVisible">Nothing is drawn below the last whole tile row (at least <see cref="IconGrid.EndBand"/> rows
/// of plain background down to the region's edge): the end of the list is on screen.</param>
internal sealed record IconGridResult(IReadOnlyList<GridTile> Tiles, string Code, bool EndVisible = false)
{
    /// <summary>True when the region holds a consistent grid (possibly with no whole tile).</summary>
    public bool Ok => Code is "tiles" or "no-tiles";
}

/// <summary>
/// The tile sizes of one icon grid (2026-10-15): the Stock tab's (<see cref="Stock"/>, 115x120 tiles) or the Gathering
/// Helper's material list (<see cref="Helper"/>, about 98x100: the same tile drawn at 0.83, measured on
/// proofs\out\gather_helper_full.png: outline luma about 123 on a 211-223 background). The helper's counts are drawn
/// in another font (white with a dark rim) and are not read (<see cref="ReadsCounts"/> false), and its keys use their own
/// salt so they never merge with Stock or Make keys.
/// </summary>
/// <param name="Name">Preset name.</param>
/// <param name="MinLine">Shortest outline line (a heart badge hides the top line's right end).</param>
/// <param name="MaxLine">Longest outline line.</param>
/// <param name="MinBottomLine">A bottom outline line spans at least this many pixels.</param>
/// <param name="MinTileRows">Fewest rows between a tile's top and bottom outline lines.</param>
/// <param name="MaxTileRows">Most rows between a tile's top and bottom outline lines.</param>
/// <param name="Inset">Interior inset (outline + white border + inner frame).</param>
/// <param name="CountLine">Height of the count line at the tile's bottom, left out of the fingerprint.</param>
/// <param name="ReadsCounts">The count words are read.</param>
/// <param name="SideCoverage">Share of the rows between the lines on which each side outline must be dark (the helper's
/// "Lv50" labels cover part of the left outline: 0.82 measured).</param>
/// <param name="Width">Outline-to-outline width when the bottom line cannot give it (the helper's count text cuts the
/// bottom outline's right end: 84 of 96 px), else null (the bottom line's extent).</param>
/// <param name="ColumnLefts">The fixed client x of every column's left outline, when the grid never moves sideways (the
/// helper's dialog is fixed): the found rows are completed column by column on their outlines (heart badges make the
/// line stage miss some tiles), and boxes off the columns or of another height are dropped. Null: as found.</param>
/// <param name="Salt">256-bit pattern XORed into every key of this grid (never change it: learned keys depend on it).</param>
internal sealed record IconGridGeometry(
    string Name, int MinLine, int MaxLine, int MinBottomLine, int MinTileRows, int MaxTileRows, int Inset, int CountLine, bool ReadsCounts, string Salt,
    double SideCoverage = IconGrid.SideCoverage, int? Width = null, IReadOnlyList<int>? ColumnLefts = null)
{
    /// <summary>The font style that selects <see cref="Helper"/> in a reader region.</summary>
    internal const string HelperFontStyle = "gather-count-light";

    /// <summary>The Stock tab's grid (the original constants of <see cref="IconGrid"/>).</summary>
    internal static IconGridGeometry Stock { get; } = new("stock", 80, 118, 106, 112, 122, 9, 36, true, IconGrid.DomainSalt);

    /// <summary>The Gathering Helper's material list (0.83 of the Stock tile).</summary>
    internal static IconGridGeometry Helper { get; } = new("helper", 60, 99, 78, 92, 103, 8, 30, false,
        "5a0c3e9f71d2b84c6e1a93f0d75b2c48e9a1f36b0c84d7e25f1b6a093c4e8d72", 0.75, 96, [344, 473, 601, 730, 858]);

    /// <summary>The geometry a reader region asks for (by its font style).</summary>
    internal static IconGridGeometry ForStyle(string? fontStyle) => fontStyle == HelperFontStyle ? Helper : Stock;
}

/// <summary>
/// The item grid of the Ability view's Stock tab (<see cref="Contracts.Perception.ParseRule.IconGrid"/>, 2026-10-15),
/// measured on the full-scale captures tests\TestData\images\stock_*_full.png (copies of proofs\out): parchment
/// background 228-229; tiles 115x120 (outline to outline) in 6 columns 211 px apart, rows 169 px apart inside a section,
/// section headers between the rows; the list is clipped at the region's edges. A tile is a rounded box with a dark
/// outline (luma 115-160) that wobbles by 1-2 px, a white border, a rarity-coloured interior and a drop shadow under it.
/// <list type="number">
/// <item>Tiles: horizontal outline lines (at least <see cref="DarkDelta"/> under the background in a 3-row window,
/// 80-118 px long; a favourite's heart badge shortens a top line) are paired into a top and a bottom line 112-122 rows
/// apart with the same left end, the bottom line 106-118 px long; both side outlines must be dark on 85 % of the rows in
/// between. Only tiles whose lines lie inside the region are read (a tile cut by the list's edge never is). Tiles that
/// differ in size by more than 4 px make the grid irregular (nothing is read).</item>
/// <item>Identity (<see cref="Key"/>): <see cref="IconFingerprint.ComputeWithHue"/> over the interior above the count
/// line (inset 9 px, the bottom 36 px left out, so the count and the level label "Lv50" never change it), XORed with
/// <see cref="DomainSalt"/>: Stock keys then lie about 128 bits away from every Make-tab material key, far beyond the
/// 24 bits within which the crafting scans take two keys for one icon, so the two screens' keys never merge. Distinct
/// Stock items measured 5-40 bits apart among themselves (one family draws an icon twice: two strawberry cones 5 bits
/// apart), a half-pixel resampling of the same tile up to 10 bits: the Stock module matches keys strictly and asks the
/// game (the item tip) when a match is not clear.</item>
/// <item>Faded: the same interior has a luma standard deviation of at most <see cref="FadedMaxStdDev"/> and its 5th
/// percentile at least <see cref="FadedMinP5"/> (faded tiles measured 9-22 and 154-191; every tile in colour 25-51 and
/// 56-134).</item>
/// <item>Count: the game draws it right-aligned at the tile's bottom right in dark brown digits (luma about 109) with a
/// thick white halo, 18-20 px tall for up to 3 digits and 15-16 px for 4-5 digits ("23550"); a level label ("Lv50",
/// left-aligned, same style) may stand left of it. Glyphs are the dark components of the bottom band whose ring 2 px
/// around them is white (an icon's own dark lines are not haloed), 13-22 px tall, ending 7-12 px above the tile's bottom
/// and 4 px or more inside its right edge; glyphs closer than 13 px form a word; the count is the last word when it ends
/// within 10 px of the right edge.</item>
/// <item>End of the list (<see cref="IsPlainBelow"/>): at least <see cref="EndBand"/> plain rows below the last whole
/// row; a list that goes on always draws something there (a section header, cut tiles, the "more" arrow).</item>
/// </list>
/// Pure; thread-safe.
/// </summary>
internal static class IconGrid
{
    /// <summary>Outline pixels are at least this much darker than the background.</summary>
    internal const int DarkDelta = 60;

    /// <summary>Shortest outline line (a heart badge hides the top line's right end).</summary>
    internal const int MinLine = 80;

    /// <summary>Longest outline line (two tiles never touch).</summary>
    internal const int MaxLine = 118;

    /// <summary>A bottom outline line spans at least this many pixels.</summary>
    internal const int MinBottomLine = 106;

    /// <summary>Rows between a tile's top and bottom outline lines.</summary>
    internal const int MinTileRows = 112;

    /// <summary>Rows between a tile's top and bottom outline lines.</summary>
    internal const int MaxTileRows = 122;

    /// <summary>Share of the rows between the lines on which each side outline must be dark.</summary>
    internal const double SideCoverage = 0.85;

    /// <summary>Size tolerance between the tiles of one grid.</summary>
    internal const int SizeTolerance = 4;

    /// <summary>Interior inset (outline + white border + inner frame).</summary>
    internal const int Inset = 9;

    /// <summary>Height of the count line at the tile's bottom, left out of the fingerprint and the faded test.</summary>
    internal const int CountLine = 36;

    /// <summary>A faded tile's interior luma standard deviation is at most this.</summary>
    internal const double FadedMaxStdDev = 22;

    /// <summary>A faded tile's interior 5th luma percentile is at least this (it has no dark ink).</summary>
    internal const int FadedMinP5 = 150;

    /// <summary>Glyph ink of the count: luma below this (the digits measure about 109, the halo 255).</summary>
    internal const int InkLuma = 160;

    /// <summary>The halo around a glyph: luma at least this.</summary>
    internal const int HaloLuma = 225;

    /// <summary>Share of a glyph's 2 px ring that must be halo.</summary>
    internal const double HaloShare = 0.9;

    /// <summary>Largest gap between two glyphs of one word (the "1"s of "114" stand 8 px apart, "Lv50 48" 21 px).</summary>
    internal const int WordGap = 12;

    /// <summary>
    /// Plain rows below the last tile row that show the end of the list (more than any gap between a row and the next
    /// section header or row: 49-60 px).
    /// </summary>
    internal const int EndBand = 150;

    /// <summary>A pixel is drawn (not background) when its luma differs from the background by more than this.</summary>
    internal const int InkDelta = 40;

    /// <summary>A band is plain when fewer than this share of its pixels are drawn.</summary>
    internal const double PlainShare = 0.003;

    /// <summary>
    /// A fixed random 256-bit pattern (2026-10-15) XORed into every Stock key: it keeps the Stock tab's keys and the Make
    /// tab's keys (another tile size and layout) about 128 bits apart. Never change it: learned Stock keys depend on it.
    /// </summary>
    internal const string DomainSalt = "bdbd1fe19b4c29182e0d2c2943a15b0db124015e69daddd87984325f2ec5114f";

    private const int Margin = 8;

    /// <summary>Reads the grid in <paramref name="region"/> (client coordinates).</summary>
    public static IconGridResult Detect(Frame frame, RectI region, IconGridGeometry? geometry = null)
    {
        var g = geometry ?? IconGridGeometry.Stock;
        var bounds = new RectI(0, 0, frame.Width, frame.Height);
        if (region.Width < 150 || region.Height < 150 || bounds.Intersect(region) != region
            || frame.Bgra.Length < frame.Stride * frame.Height || frame.Stride < frame.Width * 4)
        {
            return new IconGridResult([], "region-outside-frame");
        }

        var boxes = FindTiles(frame, region, g);
        if (boxes.Count == 0)
        {
            return new IconGridResult([], "no-tiles", IsPlainBelow(frame, region, region.Top));
        }

        var w = Median(boxes.Select(b => b.Width));
        var h = Median(boxes.Select(b => b.Height));
        if (boxes.Any(b => Math.Abs(b.Width - w) > SizeTolerance || Math.Abs(b.Height - h) > SizeTolerance))
        {
            return new IconGridResult([], "tiles-irregular");
        }

        var tiles = boxes.Select(b => new GridTile(b, Key(frame, b, g), IsFaded(frame, b, g), g.ReadsCounts ? FindCount(frame, b) : null)).ToList();
        return new IconGridResult(tiles, "tiles", IsPlainBelow(frame, region, boxes.Max(b => b.Bottom)));
    }

    /// <summary>
    /// True when the region below <paramref name="from"/> (plus the tiles' drop shadow) holds nothing but background over
    /// at least <see cref="EndBand"/> rows: fewer than <see cref="PlainShare"/> of its pixels differ from the region's
    /// median luma by more than <see cref="InkDelta"/>. On the captures the end of the list (Tailor, Hyperweather: 381
    /// plain rows) measured 0 %, every screen with more below 3-24 % (a section header, cut tiles, the "more" arrow at
    /// the list's bottom centre).
    /// </summary>
    internal static bool IsPlainBelow(Frame frame, RectI region, int from)
    {
        var band = new RectI(region.Left, Math.Max(region.Top, from + 10), region.Right, region.Bottom - 2);
        if (band.Height < EndBand)
        {
            return false;
        }

        var plane = Plane(frame, region);
        var bg = MedianLuma(plane, region, region);
        var ink = 0;
        for (var y = band.Top; y < band.Bottom; y++)
        {
            for (var x = band.Left; x < band.Right; x++)
            {
                ink += Math.Abs(plane[((y - region.Top) * region.Width) + (x - region.Left)] - bg) > InkDelta ? 1 : 0;
            }
        }

        return ink < PlainShare * band.Area;
    }

    /// <summary>The Stock key of a tile: "icon:" + its hue fingerprint XOR <see cref="DomainSalt"/> (see the class remarks).</summary>
    internal static string Key(Frame frame, RectI box, IconGridGeometry? geometry = null)
    {
        var g = geometry ?? IconGridGeometry.Stock;
        var print = Convert.FromHexString(IconFingerprint.ComputeWithHue(frame, Interior(box, g)));
        var salt = Convert.FromHexString(g.Salt);
        for (var i = 0; i < print.Length; i++)
        {
            print[i] ^= salt[i];
        }

        return IconFingerprint.Prefix + Convert.ToHexString(print).ToLowerInvariant();
    }

    /// <summary>The interior a tile's identity and fading are measured on (above the count line).</summary>
    internal static RectI Interior(RectI box, IconGridGeometry? geometry = null)
    {
        var g = geometry ?? IconGridGeometry.Stock;
        return new(box.Left + g.Inset, box.Top + g.Inset, box.Right - g.Inset, box.Bottom - g.CountLine);
    }

    /// <summary>The whole tiles of a region, row by row, left to right.</summary>
    internal static List<RectI> FindTiles(Frame frame, RectI region, IconGridGeometry? geometry = null)
    {
        var geo = geometry ?? IconGridGeometry.Stock;
        var bounds = new RectI(0, 0, frame.Width, frame.Height);
        var window = bounds.Intersect(region.Inflate(Margin))!.Value;
        var luma = Plane(frame, window);
        int L(int x, int y) => window.Contains(new PointI(x, y)) ? luma[(y - window.Top) * window.Width + (x - window.Left)] : 255;

        var bg = MedianLuma(luma, window, region);
        var thr = bg - DarkDelta;

        // 1. Outline lines: runs of dark pixels (min over 3 rows: the outline wobbles) with gaps of at most 2 px.
        var groups = new List<List<(int Y, int A, int C)>>();
        var dark = new bool[region.Width];
        for (var y = region.Top + 1; y < region.Bottom - 1; y++)
        {
            for (var x = region.Left; x < region.Right; x++)
            {
                dark[x - region.Left] = Math.Min(L(x, y - 1), Math.Min(L(x, y), L(x, y + 1))) < thr;
            }

            var i = 0;
            while (i < dark.Length)
            {
                if (!dark[i])
                {
                    i++;
                    continue;
                }

                var start = i;
                var last = i;
                while (i < dark.Length && (dark[i] || i - last <= 2))
                {
                    if (dark[i])
                    {
                        last = i;
                    }

                    i++;
                }

                var length = last - start + 1;
                if (length < geo.MinLine || length > geo.MaxLine)
                {
                    continue;
                }

                var (a, c) = (region.Left + start, region.Left + last);
                var group = groups.FirstOrDefault(g => y - g[^1].Y <= 1 && Math.Abs(a - g[0].A) <= 6 && Math.Abs(c - g[0].C) <= 6);
                if (group is null)
                {
                    groups.Add([(y, a, c)]);
                }
                else
                {
                    group.Add((y, a, c));
                }
            }
        }

        // the line's row is its darkest row; its extent the union of its rows
        var lines = groups.Select(g =>
        {
            var best = g.OrderBy(r => RowMean(L, r.Y, r.A, r.C)).ThenBy(r => r.Y).First();
            return (best.Y, A: g.Min(r => r.A), C: g.Max(r => r.C));
        }).ToList();

        // 2. Tiles: a bottom line, the widest top line above it with the same left end, and dark sides in between.
        var tiles = new List<RectI>();
        foreach (var bottom in lines.Where(l => l.C - l.A >= geo.MinBottomLine).OrderBy(l => l.Y).ThenBy(l => l.A))
        {
            var right = geo.Width is { } width ? bottom.A + width : bottom.C;
            var top = lines
                .Where(t => bottom.Y - t.Y >= geo.MinTileRows && bottom.Y - t.Y <= geo.MaxTileRows && Math.Abs(t.A - bottom.A) <= 4 && t.C <= Math.Max(bottom.C, right) + 4)
                .OrderByDescending(t => t.C - t.A)
                .Select(t => ((int Y, int A, int C)?)t)
                .FirstOrDefault();
            if (top is not { } t0 || t0.Y - 2 < region.Top || bottom.Y + 3 > region.Bottom)
            {
                continue;
            }

            if (Side(L, thr, t0.Y + 10, bottom.Y - 10, bottom.A - 5, bottom.A + 3) < geo.SideCoverage
                || Side(L, thr, t0.Y + 10, bottom.Y - 10, right - 3, right + 5) < geo.SideCoverage)
            {
                continue;
            }

            var box = new RectI(bottom.A - 1, t0.Y - 1, right + 2, bottom.Y + 2);
            if (bounds.Intersect(box) != box || tiles.Any(o => o.Intersect(box) is { } overlap && overlap.Area * 4 > box.Area))
            {
                continue;
            }

            tiles.Add(box);
        }

        if (geo.ColumnLefts is { } columns && geo.Width is { } tileWidth && tiles.Count > 0)
        {
            tiles = CompleteRows(tiles, columns, tileWidth, L, thr);
        }

        // row by row (a row's tops lie within 6 px of its first), left to right
        var rows = new List<List<RectI>>();
        foreach (var t in tiles.OrderBy(t => t.Top))
        {
            if (rows.Count > 0 && t.Top - rows[^1][0].Top <= 6)
            {
                rows[^1].Add(t);
            }
            else
            {
                rows.Add([t]);
            }
        }

        return [.. rows.SelectMany(r => r.OrderBy(t => t.Left))];
    }

    /// <summary>True when the tile is drawn faded (see the class remarks).</summary>
    internal static bool IsFaded(Frame frame, RectI box, IconGridGeometry? geometry = null)
    {
        var (std, p5) = InteriorStats(frame, box, geometry);
        return std <= FadedMaxStdDev && p5 >= FadedMinP5;
    }

    /// <summary>Luma standard deviation and 5th percentile of the interior above the count line.</summary>
    internal static (double StdDev, int P5) InteriorStats(Frame frame, RectI box, IconGridGeometry? geometry = null)
    {
        var r = Interior(box, geometry);
        var histogram = new int[256];
        double sum = 0, sq = 0;
        var n = 0;
        for (var y = r.Top; y < r.Bottom; y++)
        {
            for (var x = r.Left; x < r.Right; x++)
            {
                var v = LumaAt(frame, x, y);
                histogram[v]++;
                sum += v;
                sq += (double)v * v;
                n++;
            }
        }

        if (n == 0)
        {
            return (0, 0);
        }

        var mean = sum / n;
        var target = (int)(0.05 * (n - 1));
        int p5 = 0, seen = 0;
        for (var v = 0; v < 256; v++)
        {
            seen += histogram[v];
            if (seen > target)
            {
                p5 = v;
                break;
            }
        }

        return (Math.Sqrt(Math.Max(0, (sq / n) - (mean * mean))), p5);
    }

    /// <summary>The count word at the tile's bottom right, or null (see the class remarks).</summary>
    internal static CountWord? FindCount(Frame frame, RectI box)
    {
        var band = new RectI(box.Left + 3, box.Bottom - 34, box.Right, box.Bottom - 4);
        var bounds = new RectI(0, 0, frame.Width, frame.Height);
        if (bounds.Intersect(band) != band)
        {
            return null;
        }

        var w = band.Width;
        var h = band.Height;
        var luma = Plane(frame, band);
        var ink = new bool[w * h];
        for (var i = 0; i < ink.Length; i++)
        {
            ink[i] = luma[i] < InkLuma;
        }

        var glyphs = new List<Component>();
        foreach (var c in Components(ink, w, h))
        {
            var (x0, y0, x1, y1) = (band.Left + c.X0, band.Top + c.Y0, band.Left + c.X1, band.Top + c.Y1);
            var height = y1 - y0 + 1;
            if (height < 13 || height > 22 || y1 - box.Bottom < -12 || y1 - box.Bottom > -7 || x1 > box.Right - 4 || y0 < box.Bottom - 31)
            {
                continue;
            }

            if (HaloShareOf(c, ink, luma, w, h) >= HaloShare)
            {
                glyphs.Add(c);
            }
        }

        if (glyphs.Count == 0)
        {
            return null;
        }

        glyphs.Sort((p, q) => p.X0.CompareTo(q.X0));
        var word = new List<Component> { glyphs[0] };
        for (var k = 1; k < glyphs.Count; k++)
        {
            if (glyphs[k].X0 - word.Max(g => g.X1) - 1 > WordGap)
            {
                word = [];
            }

            word.Add(glyphs[k]);
        }

        var right = band.Left + word.Max(g => g.X1);
        if (right < box.Right - 10
            || word.Max(g => g.Y1 - g.Y0) - word.Min(g => g.Y1 - g.Y0) > 3
            || word.Max(g => g.Y1) - word.Min(g => g.Y1) > 2)
        {
            return null;
        }

        const int pad = 4;
        var wx0 = word.Min(g => g.X0);
        var wy0 = word.Min(g => g.Y0);
        var wx1 = word.Max(g => g.X1);
        var wy1 = word.Max(g => g.Y1);
        var iw = wx1 - wx0 + 1 + (2 * pad);
        var ih = wy1 - wy0 + 1 + (2 * pad);
        var px = new byte[iw * ih];
        Array.Fill(px, (byte)255);
        foreach (var g in word)
        {
            foreach (var p in g.Pixels)
            {
                var (x, y) = (p % w, p / w);
                px[((y - wy0 + pad) * iw) + (x - wx0 + pad)] = 0;
            }
        }

        return new CountWord(new GrayImage(iw, ih, px), new RectI(band.Left + wx0, band.Top + wy0, band.Left + wx1 + 1, band.Top + wy1 + 1), word.Count);
    }

    /// <summary>Share of halo (luma &gt;= <see cref="HaloLuma"/>) on the ring exactly 2 px around a component, other ink excluded.</summary>
    private static double HaloShareOf(Component c, bool[] ink, int[] luma, int w, int h)
    {
        var mine = new HashSet<int>(c.Pixels);
        var ring = new HashSet<int>();
        foreach (var p in c.Pixels)
        {
            var (x, y) = (p % w, p / w);
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != 2)
                    {
                        continue;
                    }

                    var (xx, yy) = (x + dx, y + dy);
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h)
                    {
                        continue;
                    }

                    var q = (yy * w) + xx;
                    if (!ink[q] && !mine.Contains(q) && !Near(mine, xx, yy, w, h))
                    {
                        ring.Add(q);
                    }
                }
            }
        }

        return ring.Count == 0 ? 0 : ring.Count(q => luma[q] >= HaloLuma) / (double)ring.Count;
    }

    /// <summary>True when (x, y) touches the component (Chebyshev distance 1): such pixels are its anti-aliased edge.</summary>
    private static bool Near(HashSet<int> mine, int x, int y, int w, int h)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var (xx, yy) = (x + dx, y + dy);
                if (xx >= 0 && yy >= 0 && xx < w && yy < h && mine.Contains((yy * w) + xx))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>8-connected components of the ink mask (pixel indices and bounding box).</summary>
    private static List<Component> Components(bool[] ink, int w, int h)
    {
        var label = new int[ink.Length];
        var result = new List<Component>();
        var queue = new Queue<int>();
        for (var start = 0; start < ink.Length; start++)
        {
            if (!ink[start] || label[start] != 0)
            {
                continue;
            }

            var id = result.Count + 1;
            var pixels = new List<int>();
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            label[start] = id;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                pixels.Add(p);
                var (x, y) = (p % w, p / w);
                x0 = Math.Min(x0, x);
                y0 = Math.Min(y0, y);
                x1 = Math.Max(x1, x);
                y1 = Math.Max(y1, y);
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var (xx, yy) = (x + dx, y + dy);
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h)
                        {
                            continue;
                        }

                        var q = (yy * w) + xx;
                        if (ink[q] && label[q] == 0)
                        {
                            label[q] = id;
                            queue.Enqueue(q);
                        }
                    }
                }
            }

            result.Add(new Component(pixels, x0, y0, x1, y1));
        }

        return result;
    }

    /// <summary>Share of rows in [y0, y1] on which some pixel of columns [x0, x1] is darker than <paramref name="thr"/>.</summary>
    /// <summary>
    /// The found tiles on fixed columns (<see cref="IconGridGeometry.ColumnLefts"/>): boxes off every column or of
    /// another height than the median are dropped; in every row of the rest, a column without a box gets one when its
    /// top and bottom outlines are dark at the row's lines (90 % of the bottom line's first 72 px, 90 % of the top line's
    /// first 56 px: the heart badge sits at the top right, the count text at the bottom right).
    /// </summary>
    private static List<RectI> CompleteRows(List<RectI> found, IReadOnlyList<int> columns, int width, Func<int, int, int> l, int thr)
    {
        var height = Median(found.Select(b => b.Height));
        var kept = found.Where(b => Math.Abs(b.Height - height) <= SizeTolerance && columns.Any(c => Math.Abs(b.Left + 1 - c) <= 3)).ToList();
        var result = new List<RectI>(kept);
        foreach (var row in kept.GroupBy(b => b.Top / 8).Select(g => g.ToList()))
        {
            var (top, bottom) = (row.Min(b => b.Top), row.Max(b => b.Bottom));
            var (topLine, bottomLine) = (top + 1, bottom - 2);
            foreach (var c in columns)
            {
                if (result.Any(b => Math.Abs(b.Left + 1 - c) <= 3 && Math.Abs(b.Top - top) <= 6))
                {
                    continue;
                }

                // 25 lighter than the line stage's threshold: a busy list lowers the region's median background
                if (LineShare(l, thr + 25, bottomLine, c + 4, c + 76) >= 0.9 && LineShare(l, thr + 25, topLine, c + 4, c + 60) >= 0.9)
                {
                    result.Add(new RectI(c - 1, top, c + width + 2, bottom));
                }
            }
        }

        return result;
    }

    /// <summary>Share of the columns x0..x1 that are dark on one of the rows y-1..y+1 (the outline wobbles).</summary>
    private static double LineShare(Func<int, int, int> l, int thr, int y, int x0, int x1)
    {
        var dark = 0;
        for (var x = x0; x <= x1; x++)
        {
            dark += Math.Min(l(x, y - 1), Math.Min(l(x, y), l(x, y + 1))) < thr ? 1 : 0;
        }

        return dark / (double)(x1 - x0 + 1);
    }

    private static double Side(Func<int, int, int> l, int thr, int y0, int y1, int x0, int x1)
    {
        if (y1 < y0)
        {
            return 0;
        }

        var dark = 0;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (l(x, y) < thr)
                {
                    dark++;
                    break;
                }
            }
        }

        return dark / (double)(y1 - y0 + 1);
    }

    private static double RowMean(Func<int, int, int> l, int y, int a, int c)
    {
        double sum = 0;
        for (var x = a; x <= c; x++)
        {
            sum += l(x, y);
        }

        return sum / (c - a + 1);
    }

    private static int MedianLuma(int[] plane, RectI window, RectI region)
    {
        var histogram = new int[256];
        for (var y = region.Top; y < region.Bottom; y++)
        {
            for (var x = region.Left; x < region.Right; x++)
            {
                histogram[plane[(y - window.Top) * window.Width + (x - window.Left)]]++;
            }
        }

        var half = region.Area / 2;
        long seen = 0;
        for (var v = 0; v < 256; v++)
        {
            seen += histogram[v];
            if (seen > half)
            {
                return v;
            }
        }

        return 255;
    }

    private static int Median(IEnumerable<int> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }

    private static int LumaAt(Frame f, int x, int y)
    {
        var o = (y * f.Stride) + (x * 4);
        return ((f.Bgra[o] * 29) + (f.Bgra[o + 1] * 150) + (f.Bgra[o + 2] * 77) + 128) >> 8;
    }

    private static int[] Plane(Frame frame, RectI r)
    {
        var plane = new int[r.Width * r.Height];
        for (var y = 0; y < r.Height; y++)
        {
            for (var x = 0; x < r.Width; x++)
            {
                plane[(y * r.Width) + x] = LumaAt(frame, r.Left + x, r.Top + y);
            }
        }

        return plane;
    }

    /// <summary>One connected ink component of the count band (band coordinates).</summary>
    private sealed record Component(List<int> Pixels, int X0, int Y0, int X1, int Y1);
}
