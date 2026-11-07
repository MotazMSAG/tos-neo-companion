using Companion.Contracts;
using Companion.Contracts.Vision;

namespace Companion.TeaHouse;

/// <summary>
/// Where things sit on the Kitty Tea House view, in game client pixels (1920x1009), measured on the captures of
/// 2026-10-29: the board's squares 130.8 x 130.5 px apart from (1023, 155.5), the side panel's "Current" box, the Score
/// and the view's title.
/// </summary>
public static class TeaGeometry
{
    /// <summary>Left edge of the board's first column.</summary>
    public const double Left = 1023;

    /// <summary>Top edge of the board's first row.</summary>
    public const double Top = 155.5;

    /// <summary>Column pitch.</summary>
    public const double PitchX = 130.8;

    /// <summary>Row pitch.</summary>
    public const double PitchY = 130.5;

    /// <summary>A square's drawn size.</summary>
    public const int Size = 127;

    /// <summary>
    /// Margin left out around a square's picture: the frame and the pulsing preview's spill (it is drawn up to ~25 %
    /// larger than its square; 2026-10-29 it made the square below look occupied at a margin of 12).
    /// </summary>
    public const int Inset = 18;

    /// <summary>The tray's margin (its plate fills the square).</summary>
    public const int TrayInset = 12;

    /// <summary>The panel's current-piece picture (inside its frame).</summary>
    public static readonly RectI CurrentIcon = RectI.FromXywh(582, 675, 78, 78);

    /// <summary>The Score number (room for six digits).</summary>
    public static readonly RectI Score = RectI.FromXywh(812, 532, 150, 42);

    /// <summary>The "Kitty Tea House" title banner.</summary>
    public static readonly RectI Title = RectI.FromXywh(590, 145, 350, 50);

    /// <summary>A square's whole rect.</summary>
    public static RectI Square(int square)
    {
        var x = (int)Math.Round(Left + (PitchX * TeaBoard.Column(square)));
        var y = (int)Math.Round(Top + (PitchY * TeaBoard.Row(square)));
        return RectI.FromXywh(x, y, Size, Size);
    }

    /// <summary>A square's picture (its rect without the margin).</summary>
    public static RectI Interior(int square)
    {
        var r = Square(square);
        var inset = square == TeaBoard.Tray ? TrayInset : Inset;
        return RectI.FromXywh(r.Left + inset, r.Top + inset, Size - (2 * inset), Size - (2 * inset));
    }
}

/// <summary>One square as read.</summary>
/// <param name="Square">Its number.</param>
/// <param name="Empty">True when the square is flat (nothing on it).</param>
/// <param name="Print">The picture's fingerprint (null when empty).</param>
/// <param name="Kind">Its kind (<see cref="TeaKind.Unknown"/> for a picture not known yet).</param>
/// <param name="Distance">Bits to the nearest known picture.</param>
/// <param name="Change">How much it moved between the frames (share of pixels, the largest of the frame pairs).</param>
/// <param name="Prints">Its fingerprint in each frame (empty when the square is empty): a cat that moved between frames
/// gives the learner more of its pictures.</param>
public sealed record TeaSquare(int Square, bool Empty, string? Print, TeaKind Kind, int Distance, double Change, IReadOnlyList<string> Prints)
{
    /// <summary>True when the kind was told by movement and colour (a cat in a pose not known yet), not by a known picture.</summary>
    public bool ByColour { get; init; }

    /// <summary>How much the square's edge band moved (the largest of the frame pairs): only the enlarged preview sweeps it.</summary>
    public double Ring { get; init; }

    /// <summary>
    /// True when the square shows the pink tile placed cakes and teas sit on (its bottom-right corner is pinker); empty
    /// squares, cats and the preview sit on the plain cream square.
    /// </summary>
    public bool OnTile { get; init; } = true;

    /// <summary>How much the square looks like the pulsing preview: the middle's movement plus twice the edge band's.</summary>
    public double PreviewScore => Change + (2 * Ring);
}

/// <summary>What one look at the screen showed.</summary>
/// <param name="OnScreen">True when the Kitty Tea House view is open.</param>
/// <param name="TitleDistance">Bits between the title banner and the known one.</param>
/// <param name="Squares">The 36 squares (square 0: the tray).</param>
/// <param name="CurrentPrint">The panel's current-piece fingerprint.</param>
/// <param name="Current">The current piece (<see cref="TeaKind.Unknown"/> when not known yet).</param>
/// <param name="CurrentDistance">Bits to the nearest known panel picture.</param>
/// <param name="Preview">The square where the game shows the current piece pulsing (its suggestion), or -1.</param>
/// <param name="Board">The board for the move finder: the preview square as it really is (empty, or for the Cutlery the
/// piece under it as last seen), unknown pictures as <see cref="TeaKind.Unknown"/>.</param>
public sealed record TeaReading(bool OnScreen, int TitleDistance, IReadOnlyList<TeaSquare> Squares, string? CurrentPrint, TeaKind Current, int CurrentDistance, int Preview, TeaBoard Board)
{
    /// <summary>Squares (tray included) whose picture is not known.</summary>
    public int UnknownCount => Squares.Count(s => s.Kind == TeaKind.Unknown && s.Square != Preview);
}

/// <summary>
/// Reads the Kitty Tea House view from a few captures taken a moment apart (look only: the host captures, nothing is
/// pressed). A flat square is empty; any other square is matched by its fingerprint against the known pictures
/// (<see cref="TeaLabels"/>; the best of the frames counts, since the cats move a little while idle). The game shows
/// the current piece pulsing on its suggested square: that square changes most between the frames, so it is taken
/// out (it is empty underneath, except for the Cutlery). Pure; thread-safe.
/// </summary>
public static class TeaReader
{
    /// <summary>Luminance spread below which a square is empty (empty squares measured 2, pieces 22 and more).</summary>
    public const double EmptySpread = 12;

    /// <summary>
    /// <see cref="TeaSquare.PreviewScore"/> from which a square can be the pulsing preview. Measured 2026-10-29: a cat's
    /// preview sweeps the edge band (0.5-0.6) and scores 1.6-1.8; the small Cookie preview stays inside its square but its
    /// middle moves 0.50 (0.53); idle cats score 0.24-0.36 (middle 0.16-0.32, band 0.02-0.04). Before, the middle alone
    /// let a lively Orange Cat pass for the preview while the Cookie preview, pulsing and orange-ish, was taken for a cat.
    /// </summary>
    public const double PreviewChange = 0.42;

    /// <summary>A square changing less than this is still (a placed piece; cats and the preview move more).</summary>
    public const double StillChange = 0.08;

    /// <summary>The preview must change this many times more than any other square.</summary>
    public const double PreviewLead = 1.5;

    /// <summary>Largest bit distance between the title banner and <see cref="TeaSeed.Title"/> (another view: ~128).</summary>
    public const int TitleMaxDistance = 40;

    /// <summary>
    /// Mean title brightness below which the page is veiled (2026-10-31: the game's "Reconnecting..." veil darkens it to
    /// 57, the end screen to 122; the bright title measures 204). Under the veil light cakes read as empty squares, while
    /// the title's fingerprint, relative to its own mean, still matched.
    /// </summary>
    public const double VeilTitleLuma = 130;

    /// <summary>
    /// Reads the view. <paramref name="frames"/>: one or more captures a moment apart (the first is the main one; the
    /// preview needs two or more). <paramref name="remembered"/>: the board of the last reading (what lies under a Cutlery
    /// preview), or null. <paramref name="lastPreview"/>: the preview square of the last reading: when the pulse is caught
    /// at a slow moment, that square, still unknown and not still, stays the preview.
    /// </summary>
    public static TeaReading Read(IReadOnlyList<Frame> frames, TeaLabels labels, TeaBoard? remembered = null, int lastPreview = -1, TeaKind previousCurrent = TeaKind.Unknown)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(labels);
        if (frames.Count == 0)
        {
            throw new ArgumentException("at least one frame", nameof(frames));
        }

        var main = frames[0];
        var title = TeaLabels.Distance(IconPrint.Compute(main, TeaGeometry.Title), TeaSeed.Title);
        if (title > TitleMaxDistance || MeanLuma(main, TeaGeometry.Title) < VeilTitleLuma)
        {
            return new TeaReading(false, title, [], null, TeaKind.Unknown, 0, -1, new TeaBoard());
        }

        var squares = new TeaSquare[TeaBoard.Squares];
        for (var i = 0; i < TeaBoard.Squares; i++)
        {
            squares[i] = ReadSquare(frames, labels, i);
        }

        var currentPrint = IconPrint.Compute(main, TeaGeometry.CurrentIcon);
        var (current, currentDistance, _) = labels.Classify(currentPrint, TeaPictureSource.Panel);
        var preview = FindPreview(squares, current, LikenessToCurrent(squares, labels, current));
        if (preview < 0 && lastPreview > 0 && squares[lastPreview].Kind == TeaKind.Unknown && squares[lastPreview].Change >= StillChange)
        {
            preview = lastPreview;
        }

        // a square next to the preview still unknown: its far side shows whether anything is there (the spill covers the
        // near side); a piece that cannot move keeps what the last settled board saw
        if (preview > 0)
        {
            foreach (var nb in Around(preview))
            {
                if (squares[nb].Kind != TeaKind.Unknown)
                {
                    continue;
                }

                if (frames.Min(f => Spread(f, FarSide(nb, preview))) < EmptySpread)
                {
                    squares[nb] = squares[nb] with { Empty = true, Kind = TeaKind.Empty, Print = null, Prints = [] };
                }
                else if (remembered is not null && remembered[nb] is not (TeaKind.Empty or TeaKind.Unknown) && !TeaKinds.IsCat(remembered[nb]))
                {
                    squares[nb] = squares[nb] with { Kind = remembered[nb] };
                }
            }
        }

        // only the cats (and the preview) move while idle: an unknown picture that moves is a cat, its colour says which
        // (2026-10-29 live: the Orange Cat's many poses kept coming back as unknown, however often the user named them)
        for (var i = 0; i < TeaBoard.Squares; i++)
        {
            if (i != preview && squares[i].Kind == TeaKind.Unknown && squares[i].Change >= StillChange)
            {
                var cat = OrangeShare(main, TeaGeometry.Interior(i)) >= OrangeCatShare ? TeaKind.OrangeCat : TeaKind.WhiteCat;
                squares[i] = squares[i] with { Kind = cat, ByColour = true };
            }
        }

        var board = new TeaBoard();
        for (var i = 0; i < TeaBoard.Squares; i++)
        {
            board[i] = i == preview
                ? (current == TeaKind.Cutlery ? UnderCutlery(i, squares, remembered, previousCurrent) : TeaKind.Empty)
                : squares[i].Kind;
        }

        return new TeaReading(true, title, squares, currentPrint, current, currentDistance, preview, board);
    }

    /// <summary>
    /// What lies under the Cutlery's preview: the game suggests it on a piece (2026-10-29 live: usually an Orange Cat, which
    /// had jumped there after the last settled board, so the remembered square was empty and the cat vanished from the
    /// helper's board). The remembered piece when there was one; else the cat that is missing from the board now; else
    /// unknown (still a piece, never empty).
    /// </summary>
    internal static TeaKind UnderCutlery(int square, IReadOnlyList<TeaSquare> squares, TeaBoard? remembered, TeaKind previousCurrent = TeaKind.Unknown)
    {
        if (remembered is not null && remembered[square] is not (TeaKind.Empty or TeaKind.Unknown))
        {
            return remembered[square];
        }

        // on a tile and empty before the last move: the piece just placed there, or what it joined into (2026-10-29 live:
        // the Cutlery's preview sat on the Cookie the user had just placed; read as an Orange Cat, the Cookie was to be cut)
        if (remembered is not null && squares[square].OnTile && TeaSolver.Placeable(previousCurrent) && !TeaKinds.IsCat(previousCurrent))
        {
            var trial = remembered.Clone();
            var placed = TeaRules.Place(trial, previousCurrent, square, TeaModel.Default());
            if (placed.Legal && trial[square] is not (TeaKind.Empty or TeaKind.Unknown))
            {
                return trial[square];
            }
        }

        // off its tile only a cat stands; the cat just placed counts among those before (2026-10-31: the hint hid the Orange
        // Cat just placed, no colour was short, and the square stayed unknown: one cat short on the helper's board)
        if (remembered is not null && !squares[square].OnTile)
        {
            foreach (var cat in new[] { TeaKind.OrangeCat, TeaKind.WhiteCat })
            {
                var before = previousCurrent == cat ? 1 : 0;
                var now = 0;
                for (var i = 1; i < TeaBoard.Squares; i++)
                {
                    before += remembered[i] == cat ? 1 : 0;
                    now += i != square && squares[i].Kind == cat ? 1 : 0;
                }

                if (now < before)
                {
                    return cat;
                }
            }

            if (TeaKinds.IsCat(previousCurrent))
            {
                return previousCurrent;
            }
        }

        return TeaKind.Unknown;
    }

    /// <summary>
    /// The pink tile's shaded bottom-right edge: at least two of three patches (the corner, the middle of the bottom
    /// edge, the middle of the right edge) with red over green by 25 or more (measured: tile 27-34, plain cream 15-16):
    /// placed cakes and teas only.
    /// </summary>
    internal static bool TileCorner(Frame f, RectI square)
    {
        var mid = square.Width / 2;
        var hits = (Pinkish(f, square.Right - 16, square.Bottom - 16) ? 1 : 0)
            + (Pinkish(f, square.Left + mid - 5, square.Bottom - 14) ? 1 : 0)
            + (Pinkish(f, square.Right - 14, square.Top + mid - 5) ? 1 : 0);
        return hits >= 2;
    }

    private static bool Pinkish(Frame f, int left, int top)
    {
        long r = 0;
        long g = 0;
        var n = 0;
        for (var y = top; y < top + 10; y++)
        {
            for (var x = left; x < left + 10; x++)
            {
                var p = (y * f.Stride) + (x * 4);
                g += f.Bgra[p + 1];
                r += f.Bgra[p + 2];
                n++;
            }
        }

        return (r - g) / (double)n >= 25;
    }

    /// <summary>Bits within which an off-tile picture counts as the current piece's preview (preview Cookie 34, cats 79+).</summary>
    public const int PreviewLikeness = 48;

    /// <summary>
    /// Per square, the bit distance from its pictures (all frames) to the known board pictures of the current piece (a cake
    /// or a tea); int.MaxValue when the current piece is not one or nothing is known of it.
    /// </summary>
    internal static int[] LikenessToCurrent(IReadOnlyList<TeaSquare> squares, TeaLabels labels, TeaKind current)
    {
        var result = Enumerable.Repeat(int.MaxValue, squares.Count).ToArray();
        if (TeaKinds.Level(current) == 0)
        {
            return result;
        }

        var known = labels.Entries.Where(e => e.Kind == current && e.Source == TeaPictureSource.Board).Select(e => e.Print).ToList();
        if (known.Count == 0)
        {
            return result;
        }

        for (var i = 1; i < squares.Count; i++)
        {
            foreach (var p in squares[i].Prints)
            {
                foreach (var k in known)
                {
                    result[i] = Math.Min(result[i], TeaLabels.Distance(p, k));
                }
            }
        }

        return result;
    }

    /// <summary>The play squares around a square, diagonals included (the preview's spill reaches them).</summary>
    internal static IEnumerable<int> Around(int square)
    {
        var r = TeaBoard.Row(square);
        var c = TeaBoard.Column(square);
        for (var dr = -1; dr <= 1; dr++)
        {
            for (var dc = -1; dc <= 1; dc++)
            {
                var rr = r + dr;
                var cc = c + dc;
                if ((dr != 0 || dc != 0) && rr is >= 0 and < TeaBoard.Side && cc is >= 0 and < TeaBoard.Side && (rr * TeaBoard.Side) + cc != TeaBoard.Tray)
                {
                    yield return (rr * TeaBoard.Side) + cc;
                }
            }
        }
    }

    /// <summary>The part of a square's picture away from the preview (the 55 % farthest from it, per axis it lies on).</summary>
    internal static RectI FarSide(int square, int preview)
    {
        var r = TeaGeometry.Interior(square);
        var dr = TeaBoard.Row(preview) - TeaBoard.Row(square);
        var dc = TeaBoard.Column(preview) - TeaBoard.Column(square);
        var keepH = (int)(r.Height * 0.55);
        var keepW = (int)(r.Width * 0.55);
        var top = dr > 0 ? r.Top : dr < 0 ? r.Bottom - keepH : r.Top;
        var height = dr == 0 ? r.Height : keepH;
        var left = dc > 0 ? r.Left : dc < 0 ? r.Right - keepW : r.Left;
        var width = dc == 0 ? r.Width : keepW;
        return RectI.FromXywh(left, top, width, height);
    }

    /// <summary>Share of orange-fur pixels from which a moving unknown picture is an Orange Cat (measured: Orange Cat 0.12, White Cat 0.03).</summary>
    public const double OrangeCatShare = 0.06;

    /// <summary>Share of the rect's pixels that are the Orange Cat's fur colour.</summary>
    internal static double OrangeShare(Frame f, RectI r)
    {
        var hits = 0;
        var n = 0;
        for (var y = r.Top; y < r.Bottom; y += 2)
        {
            for (var x = r.Left; x < r.Right; x += 2)
            {
                var p = (y * f.Stride) + (x * 4);
                int b = f.Bgra[p], g = f.Bgra[p + 1], red = f.Bgra[p + 2];
                hits += red > 200 && g is > 110 and < 185 && b < 120 && red - b > 110 ? 1 : 0;
                n++;
            }
        }

        return (double)hits / n;
    }

    /// <summary>
    /// The square that pulses: the most changing square at or above <see cref="PreviewChange"/>. The idle cats move too,
    /// but less; unless the current piece is itself a cat, a square read as a cat is never the preview (2026-10-29 live:
    /// at a slow moment of its pulse the Cookie preview read as a placed Cookie, so the lead over the cats alone failed);
    /// with a cat in hand the preview must lead the other squares clearly.
    /// </summary>
    internal static int FindPreview(IReadOnlyList<TeaSquare> squares, TeaKind current = TeaKind.Unknown, IReadOnlyList<int>? toCurrent = null)
    {
        // nothing moves on a tile but the Cutlery's preview (cakes and teas sit still, cats never stand on tiles): with the
        // Cutlery in hand, the moving tile is the preview (2026-10-29 live: its preview over a Cookie read as an Orange Cat)
        if (current == TeaKind.Cutlery)
        {
            var moving = -1;
            for (var i = 1; i < squares.Count; i++)
            {
                if (squares[i].OnTile && !squares[i].Empty && squares[i].Change >= StillChange && (moving < 0 || squares[i].Change > squares[moving].Change))
                {
                    moving = i;
                }
            }

            if (moving > 0)
            {
                return moving;
            }
        }

        // a cake or a tea that is not on its tile is not placed: it is the preview, whatever its pulse (2026-10-29 live: the
        // Cookie preview, caught at a slow moment, read as a placed Cookie and the Cutlery was advised onto it)
        var offTile = -1;
        for (var i = 1; i < squares.Count; i++)
        {
            var k = squares[i].Kind;
            if (!squares[i].OnTile && (TeaKinds.Level(k) > 0 || k == TeaKind.BlackTea) && (offTile < 0 || squares[i].PreviewScore > squares[offTile].PreviewScore))
            {
                offTile = i;
            }
        }

        if (offTile > 0)
        {
            return offTile;
        }

        // the preview shows the current piece: off its tile, the square that looks most like it (the log audit of
        // 2026-10-30: in about 50 moves a lively Orange Cat passed for the Cookie preview and the preview for a cat; the
        // preview Cookie measured 34 bits from the known Cookies, Orange Cats 79-84)
        if (toCurrent is not null)
        {
            var like = -1;
            for (var i = 1; i < squares.Count; i++)
            {
                if (!squares[i].OnTile && !squares[i].Empty && toCurrent[i] <= PreviewLikeness && (like < 0 || toCurrent[i] < toCurrent[like]))
                {
                    like = i;
                }
            }

            if (like > 0)
            {
                return like;
            }
        }

        var catInHand = TeaKinds.IsCat(current);
        var best = -1;
        var bestChange = 0.0;
        var second = 0.0;
        for (var i = 1; i < squares.Count; i++)
        {
            var c = squares[i].PreviewScore;
            if (!catInHand && TeaKinds.IsCat(squares[i].Kind))
            {
                continue;
            }

            if (c > bestChange)
            {
                second = bestChange;
                bestChange = c;
                best = i;
            }
            else if (c > second)
            {
                second = c;
            }
        }

        if (bestChange < PreviewChange)
        {
            return -1;
        }

        return !catInHand || bestChange >= second * PreviewLead ? best : -1;
    }

    private static TeaSquare ReadSquare(IReadOnlyList<Frame> frames, TeaLabels labels, int square)
    {
        var rect = TeaGeometry.Interior(square);
        var change = Change(frames, rect);
        var ring = RingChange(frames, TeaGeometry.Square(square));
        // most frames must show the tile: the pulsing preview may cover a corner in one of them (2026-10-29 live: the Cookie
        // preview's orange passed for the tile in a frame, and the Cutlery was advised onto it again)
        var onTile = square == TeaBoard.Tray || frames.Count(f => TileCorner(f, TeaGeometry.Square(square))) * 2 > frames.Count;
        var source = square == TeaBoard.Tray ? TeaPictureSource.Tray : TeaPictureSource.Board;
        // flat in any frame is empty: a piece is there in every frame, the pulsing preview's spill comes and goes
        // (2026-10-29 live: an Orange Cat preview's hat reached into the empty square above in 2 of 4 frames, the square
        // read as unknown and the advice stopped)
        if (square != TeaBoard.Tray && frames.Min(f => Spread(f, rect)) < EmptySpread)
        {
            return new TeaSquare(square, true, null, TeaKind.Empty, 0, change, []) { Ring = ring };
        }

        string? bestPrint = null;
        var bestKind = TeaKind.Unknown;
        var bestDistance = int.MaxValue;
        var prints = new List<string>(frames.Count);
        foreach (var f in frames)
        {
            var print = IconPrint.Compute(f, rect);
            prints.Add(print);
            var (kind, distance, _) = labels.Classify(print, source);
            if (bestPrint is null || (kind != TeaKind.Unknown && (bestKind == TeaKind.Unknown || distance < bestDistance)) || (bestKind == TeaKind.Unknown && distance < bestDistance))
            {
                bestPrint = print;
                bestKind = kind;
                bestDistance = distance;
            }
        }

        var empty = bestKind == TeaKind.Empty;
        return new TeaSquare(square, empty, empty ? null : bestPrint, bestKind, bestDistance, change, empty ? [] : prints) { Ring = ring, OnTile = onTile };
    }

    /// <summary>Mean luminance over a sample grid of the rect.</summary>
    internal static double MeanLuma(Frame f, RectI r)
    {
        double sum = 0;
        var n = 0;
        for (var y = r.Top; y < r.Bottom; y += 2)
        {
            var row = y * f.Stride;
            for (var x = r.Left; x < r.Right; x += 2)
            {
                var p = row + (x * 4);
                sum += (0.114 * f.Bgra[p]) + (0.587 * f.Bgra[p + 1]) + (0.299 * f.Bgra[p + 2]);
                n++;
            }
        }

        return n == 0 ? 0 : sum / n;
    }

    /// <summary>Spread (standard deviation) of the luminance over a sample grid of the rect.</summary>
    internal static double Spread(Frame f, RectI r)
    {
        double sum = 0;
        double sq = 0;
        var n = 0;
        for (var y = r.Top; y < r.Bottom; y += 2)
        {
            var row = y * f.Stride;
            for (var x = r.Left; x < r.Right; x += 2)
            {
                var p = row + (x * 4);
                var lum = (0.114 * f.Bgra[p]) + (0.587 * f.Bgra[p + 1]) + (0.299 * f.Bgra[p + 2]);
                sum += lum;
                sq += lum * lum;
                n++;
            }
        }

        var mean = sum / n;
        return Math.Sqrt(Math.Max(0, (sq / n) - (mean * mean)));
    }

    /// <summary>The largest change between consecutive frames in the square's edge band (2 to 16 px in from its edge).</summary>
    internal static double RingChange(IReadOnlyList<Frame> frames, RectI r)
    {
        if (frames.Count < 2)
        {
            return 0;
        }

        var most = 0.0;
        for (var i = 1; i < frames.Count; i++)
        {
            var a = frames[i - 1];
            var b = frames[i];
            var changed = 0;
            var n = 0;
            for (var y = r.Top + 2; y < r.Bottom - 2; y += 2)
            {
                for (var x = r.Left + 2; x < r.Right - 2; x += 2)
                {
                    var edge = Math.Min(Math.Min(x - r.Left, r.Right - 1 - x), Math.Min(y - r.Top, r.Bottom - 1 - y));
                    if (edge >= 16)
                    {
                        continue;
                    }

                    var pa = (y * a.Stride) + (x * 4);
                    var pb = (y * b.Stride) + (x * 4);
                    var d = Math.Abs(a.Bgra[pa] - b.Bgra[pb]) + Math.Abs(a.Bgra[pa + 1] - b.Bgra[pb + 1]) + Math.Abs(a.Bgra[pa + 2] - b.Bgra[pb + 2]);
                    changed += d > 60 ? 1 : 0;
                    n++;
                }
            }

            most = Math.Max(most, (double)changed / n);
        }

        return most;
    }

    /// <summary>The largest share of sampled pixels that changed (|dB|+|dG|+|dR| over 60) between consecutive frames.</summary>
    internal static double Change(IReadOnlyList<Frame> frames, RectI r)
    {
        if (frames.Count < 2)
        {
            return 0;
        }

        var most = 0.0;
        for (var i = 1; i < frames.Count; i++)
        {
            var a = frames[i - 1];
            var b = frames[i];
            var changed = 0;
            var n = 0;
            for (var y = r.Top; y < r.Bottom; y += 3)
            {
                for (var x = r.Left; x < r.Right; x += 3)
                {
                    var pa = (y * a.Stride) + (x * 4);
                    var pb = (y * b.Stride) + (x * 4);
                    var d = Math.Abs(a.Bgra[pa] - b.Bgra[pb]) + Math.Abs(a.Bgra[pa + 1] - b.Bgra[pb + 1]) + Math.Abs(a.Bgra[pa + 2] - b.Bgra[pb + 2]);
                    changed += d > 60 ? 1 : 0;
                    n++;
                }
            }

            most = Math.Max(most, (double)changed / n);
        }

        return most;
    }
}
