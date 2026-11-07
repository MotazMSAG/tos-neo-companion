using System.Globalization;
using Companion.Contracts;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;

namespace Companion.Features.Kit;

/// <summary>A recipe card found in the list, fully visible.</summary>
/// <param name="Column">Column 0..4.</param>
/// <param name="Center">Client point of the card's centre (the click point).</param>
/// <param name="Top">First row of the card's tile edges.</param>
/// <param name="Bottom">Last row of the card's tile edges.</param>
internal readonly record struct ListCard(int Column, PointI Center, int Top, int Bottom);

/// <summary>
/// Geometry of the Make tab's recipe list (left page) and the two image checks the list scan needs, measured on the
/// full-scale proofs\out\live_ability_make_full.png and the half-scale c14_before/c14_after pair of proof p0_c14 (a 300 px
/// drag moved the list 284 px). Everything is placed from the "Make" page title (template craft.list.anchor, expected at
/// (485,123); it does not move while the list scrolls): the scan clicks a card and drags the list only as that anchor +
/// an offset, so the engine locates the anchor right before every input. Pure functions of a frame; no I/O.
/// <list type="bullet">
/// <item>The list is drawn in client (296..886, 385..903). Cards sit in 5 columns centred at x 364, 477, 590, 703, 816
/// (113 px apart); a card's tile is about 98x100 (left outline at the column centre - 50, right outline at + 47), rows
/// are 140 px apart inside a section; section headers ("My Favorites", "Recent", dish types) lie between the rows.</item>
/// <item>A card is found by its tile's side outlines (dark line, white border inside, section background outside),
/// relative to the background level of the gaps between the columns; rows are the ones at least one column shows as a
/// whole tile (85+ rows of edges); a column has a card in such a row when its edges cover 40 % of it (a badge or the
/// selection frame hides parts of an edge; an empty cell shows none).</item>
/// <item>A card's identity without a click (2026-10-15, <see cref="CardKeys"/>): the hue-aware fingerprint of its product
/// picture (<see cref="PictureRect"/>: inside the side outlines, below the badges the game hangs on the top corners and
/// changes over time, down to the level label), XORed with <see cref="CardKeySalt"/>. Measured on the full-scale captures:
/// the same card in five captures with another card selected 0 bits apart; the list 31 px further (a sub-pixel scroll
/// phase) up to 15 bits at the found rows, up to 7 bits at the best of the rows -3..+3; the same product in My Favorites
/// and Recent 9 bits; different products 30 bits or more (a pink and an orange drink in one glass 34). A card matches a
/// learned product strictly (<see cref="MatchCard"/>): within <see cref="CardSameMaxBits"/> at the best alignment and
/// every other product more than <see cref="CardRivalMarginBits"/> farther; anything else is selected and read.</item>
/// <item>The selected card (<see cref="IsSelected"/>): the game draws a mint frame around it, seen in the strips just
/// outside both side outlines (96-154 mint pixels per side on the captures; a neighbour shows at most 48 on its one side
/// next to the selected card, and none on the other).</item>
/// <item>Where a card was seen (2026-10-16, <see cref="PlaceOf"/>): its column and its list row measured from the list
/// top ("listY", the client y the card's centre has while the list sits at its top), kept on the card's learned picture
/// (<see cref="IconItem.Place"/>, "craft.list@&lt;column&gt;,&lt;listY&gt;"). A remembered place is a hint, never the truth: it
/// goes stale when the user learns recipes (the sections below grow) or crafts (the Recent row changes). The find walk
/// jumps there with long drags (<see cref="JumpDragPx"/>) and checks the card by its picture as always.</item>
/// </list>
/// </summary>
internal static class RecipeList
{
    /// <summary>A list find's "not found": given only by a walk that reached the list's end (audit R1.6).</summary>
    internal const string FindNotFound = "list.notFound";

    /// <summary>A find result proves the card is absent only with <see cref="FindNotFound"/> (a capped or failed walk does not, rule 2).</summary>
    internal static bool ProvesAbsent(string code) => code == FindNotFound;

    /// <summary>The list's anchor template: the "Make" title of the list page.</summary>
    internal const string AnchorTemplate = "craft.list.anchor";

    /// <summary>The anchor's expected centre (templates.json); offsets are relative to it.</summary>
    internal static readonly PointI AnchorAt = new(485, 123);

    /// <summary>Client rect where the list draws its content.</summary>
    internal static readonly RectI Viewport = new(296, 385, 886, 903);

    /// <summary>Card column centres (client x).</summary>
    internal static readonly int[] Columns = [364, 477, 590, 703, 816];

    /// <summary>Distance between rows of cards inside a section.</summary>
    internal const int RowPitch = 140;

    /// <summary>A click lands at least this far inside the viewport.</summary>
    internal const int ClickMargin = 20;

    /// <summary>Rows of tile edges that make a whole card.</summary>
    internal const int MinCardRows = 85;

    /// <summary>Share of a card row a column's edges must cover for a card to be there.</summary>
    internal const double MinCoverage = 0.4;

    /// <summary>Largest shift searched between two captures.</summary>
    internal const int MaxShift = ListScroll.MaxShift;

    /// <summary>
    /// A fixed random 256-bit pattern (2026-10-15) XORed into every card key: the list's keys then lie about 128 bits away
    /// from the Make tab's material keys and the Stock tab's keys (other tiles, other salts), so the three never merge among
    /// the store's icon items. Never change it: the learned card keys depend on it.
    /// </summary>
    internal const string CardKeySalt = "2df540cdd036d187e0115596a49c6f07f5a1b3bb3fe77a629ec50fbb5aa8da2f";

    /// <summary>A card key is compared at the rows this far above and below the found card (sub-pixel scroll phases).</summary>
    internal const int CardKeySearchPx = 3;

    /// <summary>Largest distance (at the best alignment) at which a learned card is the same card.</summary>
    internal const int CardSameMaxBits = 12;

    /// <summary>A learned card of another product must lie more than this many bits farther than the nearest.</summary>
    internal const int CardRivalMarginBits = 8;

    /// <summary>Mint pixels each side strip must show for the card to be the selected one.</summary>
    internal const int SelectedMinPixels = 70;

    /// <summary>Prefix of a list card's place (<see cref="IconItem.Place"/>); never a Stock sub-tab's or the Gathering Helper's.</summary>
    internal const string PlacePrefix = "craft.list@";

    /// <summary>
    /// What a drag loses before the list follows: live 2026-10-15, a 280 px drag moved the list 263..266 px (13 walks); the
    /// simulated list loses 14.
    /// </summary>
    internal const int DragLossPx = 16;

    /// <summary>
    /// A drag towards the top that moved the list this much less than it would move a free list reached the top (live
    /// 2026-10-15: every walk's first top drag after the open moved 31 px, the second one 0: the second drag was only a
    /// confirmation, ~7 s each walk).
    /// </summary>
    internal const int TopShortMovePx = 40;

    /// <summary>Client row a jump aims the remembered card's centre at (the middle of the list).</summary>
    internal static readonly int JumpAimY = (Viewport.Top + Viewport.Bottom) / 2;

    /// <summary>A jump ends when the remembered card's centre is this close to <see cref="JumpAimY"/> (the card stays whole and clickable).</summary>
    internal const int JumpTolerancePx = 120;

    /// <summary>Shortest jump drag (two steps of the engine's drag).</summary>
    internal const int MinJumpDragPx = 40;

    /// <summary>
    /// Longest jump drag (the engine allows <see cref="HardRules.MaxDragDistancePx"/>): the list then moves ~344 px and the
    /// frames before and after still share 174 rows for <see cref="MeasureShift"/> (it needs 120; the live scroll drags
    /// shared 254 and always measured clearly). A drag of 400 px would leave 134.
    /// </summary>
    internal const int MaxJumpDragPx = 360;

    /// <summary>A card's centre this far inside the list's top and bottom: the whole card is shown (tiles are ~100 rows).</summary>
    internal const int WholeCardMarginPx = 55;

    private const int LeftOutline = -50;
    private const int RightOutline = 47;
    private const int EdgeSearch = 4;
    private const int PictureInset = 9;
    private const int PictureTop = 26;
    private const int PictureHeight = 63;
    private const int SelectionStrip = 11;
    private const int SelectionTop = 30;
    private const int SelectionBelow = 6;

    /// <summary>The offset (from the anchor's expected centre) that lands on <paramref name="clientPoint"/>.</summary>
    internal static PointI OffsetOf(PointI clientPoint) => new(clientPoint.X - AnchorAt.X, clientPoint.Y - AnchorAt.Y);

    /// <summary>True when a click at <paramref name="p"/> stays well inside the list (never outside the panel).</summary>
    internal static bool Clickable(PointI p) => Viewport.Inflate(-ClickMargin).Contains(p);

    /// <summary>The whole cards in a capture, row by row, left to right (empty when the frame is not 1920x1009).</summary>
    internal static IReadOnlyList<ListCard> FindCards(Frame frame)
    {
        if (!Valid(frame))
        {
            return [];
        }

        var top = Viewport.Top;
        var bottom = Viewport.Bottom;
        var gaps = new List<int>();
        for (var c = 0; c + 1 < Columns.Length; c++)
        {
            var gx = (Columns[c] + Columns[c + 1]) / 2;
            for (var y = top; y < bottom; y++)
            {
                gaps.Add(Luma(frame, gx, y));
            }
        }

        gaps.Sort();
        var bg = gaps[gaps.Count / 2];
        var flags = Columns.Select(x => EdgeRows(frame, bg, x)).ToArray();

        // Whole tiles in any column give the rows.
        var whole = new List<(int Start, int End)>();
        foreach (var f in flags)
        {
            whole.AddRange(Runs(f, top).Where(r => r.End - r.Start + 1 >= MinCardRows));
        }

        // Runs of one row overlap (a badge may shorten a column's run); rows are 140 px apart.
        whole.Sort();
        var rows = new List<List<(int Start, int End)>>();
        foreach (var r in whole)
        {
            var last = rows.Count > 0 ? rows[^1] : null;
            if (last is not null && Math.Min(r.End, last.Max(g => g.End)) - r.Start + 1 >= (r.End - r.Start + 1) / 2)
            {
                rows[^1].Add(r);
            }
            else
            {
                rows.Add([r]);
            }
        }

        var cards = new List<ListCard>();
        foreach (var group in rows)
        {
            var a = MedianOf(group.Select(g => g.Start));
            var b = MedianOf(group.Select(g => g.End));
            for (var c = 0; c < Columns.Length; c++)
            {
                var covered = 0;
                for (var y = a; y <= b; y++)
                {
                    covered += flags[c][y - top] ? 1 : 0;
                }

                if (covered >= MinCoverage * (b - a + 1))
                {
                    cards.Add(new ListCard(c, new PointI(Columns[c], (a + b + 1) / 2), a, b));
                }
            }
        }

        return cards;
    }

    /// <summary>
    /// The vertical shift of the list content from <paramref name="before"/> to <paramref name="after"/>
    /// (<see cref="ListScroll.Measure"/> over the list's viewport, 5 bands across x 300..880).
    /// </summary>
    internal static ListShift MeasureShift(Frame before, Frame after) => ListScroll.Measure(before, after, Viewport, 300, 880, 5);

    /// <summary>
    /// The card's product picture at <paramref name="dy"/> rows from the found card: 9 px inside the side outlines, 63 rows
    /// from 26 px under the top (below the corner badges; down to 6 px above the bottom outline of a whole 96-row card, so
    /// the level label is part of it). Placed from the top only: a card the list cuts at its bottom keeps its picture.
    /// </summary>
    internal static RectI PictureRect(ListCard card, int dy = 0)
    {
        var x = Columns[card.Column];
        var top = card.Top + PictureTop + dy;
        return new RectI(x + LeftOutline + PictureInset, top, x + RightOutline - PictureInset + 1, top + PictureHeight);
    }

    /// <summary>
    /// The card's key ("icon:" + its hue-aware picture fingerprint XOR <see cref="CardKeySalt"/>) at <paramref name="dy"/>
    /// rows from the found card; null when the frame is not 1920x1009 or the picture is not wholly inside the list (a card
    /// cut by the list's edge is selected and read as before, never matched on a part of its picture).
    /// </summary>
    internal static string? CardKey(Frame frame, ListCard card, int dy = 0)
    {
        var rect = PictureRect(card, dy);
        if (!Valid(frame) || Viewport.Intersect(rect) != rect)
        {
            return null;
        }

        var print = Convert.FromHexString(IconPrint.ComputeWithHue(frame, rect));
        var salt = Convert.FromHexString(CardKeySalt);
        for (var i = 0; i < print.Length; i++)
        {
            print[i] ^= salt[i];
        }

        return IconKeys.Prefix + Convert.ToHexString(print).ToLowerInvariant();
    }

    /// <summary>
    /// The card's keys at every row of the search (-<see cref="CardKeySearchPx"/>..+<see cref="CardKeySearchPx"/>), the key
    /// at the found rows (the one stored when the card is learned) first; empty when the card has no key.
    /// </summary>
    internal static IReadOnlyList<string> CardKeys(Frame frame, ListCard card)
    {
        if (CardKey(frame, card) is not { } own)
        {
            return [];
        }

        var keys = new List<string> { own };
        for (var dy = -CardKeySearchPx; dy <= CardKeySearchPx; dy++)
        {
            if (dy != 0 && CardKey(frame, card, dy) is { } k)
            {
                keys.Add(k);
            }
        }

        return keys;
    }

    /// <summary>
    /// The learned product a card shows, matched strictly (the rule of <see cref="StockIcons"/> with
    /// <see cref="CardSameMaxBits"/> and <see cref="CardRivalMarginBits"/>, the best of the card's alignments): "known",
    /// "unknown" or "ambiguous".
    /// </summary>
    internal static StockIconMatch MatchCard(IReadOnlyList<string> keys, IReadOnlyList<IconItem> learned) =>
        StockIcons.Match(keys, learned, CardSameMaxBits, CardRivalMarginBits);

    /// <summary>True when the game shows <paramref name="card"/> selected (its mint frame on both sides, see the class remarks).</summary>
    internal static bool IsSelected(Frame frame, ListCard card)
    {
        if (!Valid(frame))
        {
            return false;
        }

        var x = Columns[card.Column];
        var y0 = card.Top + SelectionTop;
        var y1 = Math.Min(frame.Height - 1, card.Bottom + SelectionBelow);
        return Mint(frame, x + RightOutline + 1, x + RightOutline + SelectionStrip, y0, y1) >= SelectedMinPixels
            && Mint(frame, x + LeftOutline - SelectionStrip, x + LeftOutline - 1, y0, y1) >= SelectedMinPixels;
    }

    /// <summary>
    /// The place of a card (column 0..4, <paramref name="listY"/> = the client y of its centre while the list sits at its
    /// top): "craft.list@&lt;column&gt;,&lt;listY&gt;".
    /// </summary>
    internal static string PlaceOf(int column, int listY) =>
        string.Create(CultureInfo.InvariantCulture, $"{PlacePrefix}{column},{listY}");

    /// <summary>The column and list row of a list card's place; null for any other place (a Stock tile, the Gathering Helper, none).</summary>
    internal static (int Column, int ListY)? ParsePlace(string? place)
    {
        if (place is null || !place.StartsWith(PlacePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = place[PlacePrefix.Length..].Split(',');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var column) && column < Columns.Length
            && int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var listY)
            ? (column, listY)
            : null;
    }

    /// <summary>True when <paramref name="place"/> is a list place of this column within <paramref name="tolerancePx"/> rows.</summary>
    internal static bool SamePlace(string? place, int column, int listY, int tolerancePx) =>
        ParsePlace(place) is { } p && p.Column == column && Math.Abs(p.ListY - listY) <= tolerancePx;

    /// <summary>
    /// The remembered places of the learned card pictures that <paramref name="wanted"/> accepts, shallowest first;
    /// places of one column less than half a row apart count once (the same card seen at two scroll phases).
    /// </summary>
    internal static IReadOnlyList<(int Column, int ListY)> PlaceHints(IEnumerable<IconItem> learned, Func<IconItem, bool> wanted)
    {
        var places = new List<(int Column, int ListY)>();
        foreach (var p in learned.Where(wanted).Select(i => ParsePlace(i.Place)).OfType<(int Column, int ListY)>().OrderBy(p => p.ListY).ThenBy(p => p.Column))
        {
            if (!places.Any(q => q.Column == p.Column && Math.Abs(q.ListY - p.ListY) < RowPitch / 2))
            {
                places.Add(p);
            }
        }

        return places;
    }

    /// <summary>True when a card whose centre is at list row <paramref name="listY"/> is whole inside the list at <paramref name="offset"/>.</summary>
    internal static bool ShownWhole(int listY, int offset) =>
        listY + offset >= Viewport.Top + WholeCardMarginPx && listY + offset <= Viewport.Bottom - WholeCardMarginPx;

    /// <summary>
    /// A drag towards the top reached it (2026-10-16): the list did not move, or moved clearly less than a drag of
    /// <paramref name="dragPx"/> moves a list with room (a free list follows all but <see cref="DragLossPx"/>).
    /// </summary>
    internal static bool TopReached(ListShift shift, int dragPx) =>
        shift.Known && (shift.Unchanged || (shift.Dy > 0 && shift.Dy < dragPx - DragLossPx - TopShortMovePx));

    /// <summary>
    /// The longest jump drag up from <paramref name="start"/> (in whole drag steps) whose release stays well inside the
    /// list: at most <see cref="MaxJumpDragPx"/> and the engine's <see cref="HardRules.MaxDragDistancePx"/>.
    /// </summary>
    internal static int MaxUpDragPx(PointI start)
    {
        var room = Math.Min(Math.Min(MaxJumpDragPx, HardRules.MaxDragDistancePx), start.Y - (Viewport.Top + ClickMargin));
        return Math.Max(0, room / HardRules.DragStepPx * HardRules.DragStepPx);
    }

    /// <summary>
    /// The next jump drag towards a card remembered at list row <paramref name="listY"/> while the list is at
    /// <paramref name="offset"/> (content moved up by -offset): the pointer distance up (positive) that brings the card's
    /// centre to <see cref="JumpAimY"/>, in whole drag steps, between <see cref="MinJumpDragPx"/> and
    /// <paramref name="maxPx"/>. Null when the card is already within <see cref="JumpTolerancePx"/> of the aim or above it
    /// (a jump never drags back towards the top).
    /// </summary>
    internal static int? JumpDragPx(int listY, int offset, int maxPx)
    {
        var need = listY + offset - JumpAimY;
        if (need <= JumpTolerancePx || maxPx < MinJumpDragPx)
        {
            return null;
        }

        var steps = (need + DragLossPx + HardRules.DragStepPx - 1) / HardRules.DragStepPx;
        return Math.Clamp(steps * HardRules.DragStepPx, MinJumpDragPx, maxPx);
    }

    /// <summary>Pixels of the selection frame's mint (green and blue well above red) in a client rect (inclusive).</summary>
    private static int Mint(Frame f, int x0, int x1, int y0, int y1)
    {
        var n = 0;
        for (var y = Math.Max(0, y0); y <= y1; y++)
        {
            for (var x = Math.Max(0, x0); x <= Math.Min(f.Width - 1, x1); x++)
            {
                var o = (y * f.Stride) + (x * 4);
                int b = f.Bgra[o], g = f.Bgra[o + 1], r = f.Bgra[o + 2];
                if (g - r >= 25 && b - r >= 12 && g >= 140)
                {
                    n++;
                }
            }
        }

        return n;
    }

    /// <summary>Per viewport row: a tile side outline at the column's left or right edge (searched within 4 px).</summary>
    private static bool[] EdgeRows(Frame f, int bg, int column)
    {
        var flags = new bool[Viewport.Height];
        for (var y = Viewport.Top; y < Viewport.Bottom; y++)
        {
            flags[y - Viewport.Top] = Edge(f, bg, column + LeftOutline, y, +1) || Edge(f, bg, column + RightOutline, y, -1);
        }

        return flags;
    }

    private static bool Edge(Frame f, int bg, int x, int y, int inward)
    {
        for (var xx = x - EdgeSearch; xx <= x + EdgeSearch; xx++)
        {
            var v = Luma(f, xx, y);
            // the outside is background (gaps, section boxes or the panel: within 14), never the white border (+29)
            var outside = Luma(f, xx - (3 * inward), y);
            if (v > bg - 25 || outside < bg - 14 || outside >= bg + 14)
            {
                continue;
            }

            for (var d = 1; d <= 5; d++)
            {
                if (Luma(f, xx + (inward * d), y) >= bg + 4)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static List<(int Start, int End)> Runs(bool[] flags, int top)
    {
        var runs = new List<(int Start, int End)>();
        int? start = null;
        var last = 0;
        for (var i = 0; i < flags.Length; i++)
        {
            if (!flags[i])
            {
                continue;
            }

            if (start is null)
            {
                start = i;
            }
            else if (i - last > 4)
            {
                runs.Add((top + start.Value, top + last));
                start = i;
            }

            last = i;
        }

        if (start is { } s)
        {
            runs.Add((top + s, top + last));
        }

        return runs;
    }

    private static int MedianOf(IEnumerable<int> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }

    private static bool Valid(Frame f) =>
        f.Width == 1920 && f.Height == 1009 && f.Stride >= f.Width * 4 && f.Bgra.Length >= f.Stride * f.Height;

    private static int Luma(Frame f, int x, int y)
    {
        var o = (y * f.Stride) + (x * 4);
        return ((f.Bgra[o] * 29) + (f.Bgra[o + 1] * 150) + (f.Bgra[o + 2] * 77) + 128) >> 8;
    }
}
