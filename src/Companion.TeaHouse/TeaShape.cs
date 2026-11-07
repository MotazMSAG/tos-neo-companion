namespace Companion.TeaHouse;

/// <summary>
/// One pass over a board that reads its shape, shared by the expert features (rules 5, 6, 7 and 9 of the 2026-10-29 rule
/// design): the touching groups of each kind, how many empty squares touch each square, how big a join a piece placed on
/// an empty square would make (split pairs included), the teas and Sundaes around each empty square, and how penned in
/// each White Cat group is. Pure.
/// </summary>
internal sealed class TeaShape
{
    /// <summary>The largest pen counted (empty squares a White Cat group can still reach).</summary>
    public const int PenCap = 9;

    private TeaShape()
    {
    }

    /// <summary>Empty play squares.</summary>
    public int Empties { get; private init; }

    /// <summary>Per square: its group number (-1 for empty or the tray).</summary>
    public int[] GroupOf { get; } = new int[TeaBoard.Squares];

    /// <summary>Per group: size, kind, empty squares touching it.</summary>
    public List<(int Size, TeaKind Kind, int Openings)> Groups { get; } = [];

    /// <summary>Per square: empty neighbours.</summary>
    public int[] Free { get; } = new int[TeaBoard.Squares];

    /// <summary>Orange Cats on the board.</summary>
    public int OrangeCats { get; private init; }

    /// <summary>The shape of <paramref name="board"/>.</summary>
    public static TeaShape Of(TeaBoard board)
    {
        var shape = new TeaShape { Empties = board.EmptyCount, OrangeCats = Enumerable.Range(1, TeaBoard.Squares - 1).Count(i => board[i] == TeaKind.OrangeCat) };
        Array.Fill(shape.GroupOf, -1);
        Span<int> group = stackalloc int[TeaBoard.Squares];
        Span<bool> open = stackalloc bool[TeaBoard.Squares];
        for (var i = 1; i < TeaBoard.Squares; i++)
        {
            foreach (var nb in TeaBoard.Neighbours[i])
            {
                shape.Free[i] += board[nb] == TeaKind.Empty ? 1 : 0;
            }

            var k = board[i];
            if (k == TeaKind.Empty || shape.GroupOf[i] >= 0)
            {
                continue;
            }

            var n = TeaRules.Group(board, i, k, group);
            open.Clear();
            var openings = 0;
            for (var g = 0; g < n; g++)
            {
                shape.GroupOf[group[g]] = shape.Groups.Count;
                foreach (var nb in TeaBoard.Neighbours[group[g]])
                {
                    if (board[nb] == TeaKind.Empty && !open[nb])
                    {
                        open[nb] = true;
                        openings++;
                    }
                }
            }

            shape.Groups.Add((n, k, openings));
        }

        return shape;
    }

    /// <summary>Pieces of <paramref name="kind"/> in the distinct groups touching the empty square <paramref name="s"/>.</summary>
    public int Touching(TeaBoard board, int s, TeaKind kind)
    {
        Span<int> seen = stackalloc int[4];
        var count = 0;
        var total = 0;
        foreach (var nb in TeaBoard.Neighbours[s])
        {
            if (board[nb] != kind)
            {
                continue;
            }

            var g = GroupOf[nb];
            var dup = false;
            for (var j = 0; j < count; j++)
            {
                dup |= seen[j] == g;
            }

            if (!dup)
            {
                seen[count++] = g;
                total += Groups[g].Size;
            }
        }

        return total;
    }

    /// <summary>The size of the join a <paramref name="kind"/> placed on the empty <paramref name="s"/> would make (1 + touching).</summary>
    public int JoinSize(TeaBoard board, int s, TeaKind kind) => 1 + Touching(board, s, kind);

    /// <summary>
    /// The levels a <paramref name="kind"/> placed on <paramref name="s"/> would climb (0 when it does not join): each join
    /// leaves the next piece on <paramref name="s"/>, which joins again when that kind's groups there total two or more.
    /// </summary>
    public int ChainLength(TeaBoard board, int s, TeaKind kind)
    {
        var n = 0;
        for (var k = kind; TeaKinds.Joins(k) && JoinSize(board, s, k) >= 3; k = TeaKinds.Next(k))
        {
            n++;
        }

        return n;
    }

    /// <summary>Empty squares a White Cat group can still reach (over empty squares), up to <see cref="PenCap"/>.</summary>
    public int PenRoom(TeaBoard board, int groupIndex)
    {
        Span<bool> seen = stackalloc bool[TeaBoard.Squares];
        Span<int> queue = stackalloc int[TeaBoard.Squares];
        int head = 0, tail = 0;
        for (var i = 1; i < TeaBoard.Squares; i++)
        {
            if (GroupOf[i] != groupIndex)
            {
                continue;
            }

            foreach (var nb in TeaBoard.Neighbours[i])
            {
                if (board[nb] == TeaKind.Empty && !seen[nb])
                {
                    seen[nb] = true;
                    queue[tail++] = nb;
                }
            }
        }

        while (head < tail && tail < PenCap)
        {
            var s = queue[head++];
            foreach (var nb in TeaBoard.Neighbours[s])
            {
                if (board[nb] == TeaKind.Empty && !seen[nb] && tail < PenCap)
                {
                    seen[nb] = true;
                    queue[tail++] = nb;
                }
            }
        }

        return tail;
    }
}

/// <summary>
/// The expert features the learned brain sees besides its windows (rules 5, 6, 7 and 9 of the rule design, turned into
/// inputs whose values self-play learns instead of hand-set shares). Each is a small index into its own table of
/// <see cref="TeaNet"/>.
/// </summary>
internal static class TeaFeatures
{
    /// <summary>Table sizes, in the order of <see cref="Indices"/>.</summary>
    public static readonly int[] Sizes =
    [
        5 * 4 * 8, // tea sockets: best socket (none, open, one exit, sealed, Sundae socket) x count band x room band
        5 * 3 * 3, // pens (per White Cat group): room band x group size x tea alignment
        9 * 4, // lifeline: level of the best one-exit pair x danger band
        4 * 9, // chains: longest chain ready x level it would reach
        11 * 9, // endgame coverage: share of next pieces that can still join (tenths) x empty squares (0..8; 8 = more)
        3 * 10 * 8, // joker readiness: tray joker (none, Oven, Cutlery) x best reach x room band
    ];

    /// <summary>Feature tables (rules 5, 6, 7, 9; the pens add one index per White Cat group).</summary>
    public const int Tables = 6;

    /// <summary>
    /// The table indices for <paramref name="board"/>: one per table, except the pens, which add one per White Cat group
    /// (written after the others, table 1); returns how many were written into <paramref name="into"/>.
    /// </summary>
    public static int Indices(TeaBoard board, Span<(int Table, int Index)> into)
    {
        var shape = TeaShape.Of(board);
        var room = Math.Min(7, shape.Empties / 5);
        var n = 0;

        // rule 5: the best tea socket (an empty square touching teas totalling two or more)
        var bestSocket = 0;
        var sockets = 0;
        for (var s = 1; s < TeaBoard.Squares; s++)
        {
            if (board[s] != TeaKind.Empty)
            {
                continue;
            }

            var teas = shape.Touching(board, s, TeaKind.Tea);
            var sundaes = shape.Touching(board, s, TeaKind.Sundae);
            if (teas < 2 && sundaes < 2)
            {
                continue;
            }

            sockets++;
            var code = sundaes >= 2 && teas >= 2 ? 4 : shape.Free[s] == 0 ? 3 : shape.Free[s] == 1 ? 2 : 1;
            bestSocket = Math.Max(bestSocket, code);
        }

        into[n++] = (0, (((bestSocket * 4) + Math.Min(3, sockets)) * 8) + room);

        // rule 6: the highest pair with one empty neighbour left (a lifeline), and how threatened it is
        var lifeline = 0;
        var danger = 0;
        foreach (var g in shape.Groups)
        {
            if (g.Size >= 2 && g.Openings == 1 && g.Kind <= TeaKind.WholeCake && TeaKinds.Level(g.Kind) >= 3 && TeaKinds.Level(g.Kind) > lifeline)
            {
                lifeline = TeaKinds.Level(g.Kind);
            }
        }

        if (lifeline > 0)
        {
            danger = Math.Min(3, shape.OrangeCats + (shape.Empties < 8 ? 1 : 0));
        }

        into[n++] = (2, (lifeline * 4) + danger);

        // rule 6: the longest chain ready on one square, and the level it reaches
        var chain = 0;
        var reach = 0;
        for (var s = 1; s < TeaBoard.Squares; s++)
        {
            if (board[s] != TeaKind.Empty)
            {
                continue;
            }

            // only the cake kinds touching the square can start a chain there
            foreach (var nb in TeaBoard.Neighbours[s])
            {
                var k = board[nb];
                if (k < TeaKind.Cookie || k > TeaKind.WholeCake)
                {
                    continue;
                }

                var len = shape.ChainLength(board, s, k);
                if (len > chain || (len == chain && len > 0 && (int)k + len > reach))
                {
                    chain = len;
                    reach = Math.Min(8, (int)k + len);
                }
            }
        }

        into[n++] = (3, (Math.Min(3, chain) * 9) + reach);

        // rule 9: near the end, the share of next pieces that could still join somewhere
        var coverage = 0;
        if (shape.Empties <= 8)
        {
            var share = 0.0;
            foreach (var (k, p) in Typical)
            {
                var can = k switch
                {
                    TeaKind.Oven => Enumerable.Range(1, TeaBoard.Squares - 1).Any(s => board[s] == TeaKind.Empty && Enumerable.Range(1, 8).Any(l => shape.JoinSize(board, s, (TeaKind)l) >= 3)),
                    TeaKind.Cutlery => true,
                    TeaKind.WhiteCat => Enumerable.Range(1, TeaBoard.Squares - 1).Any(s => board[s] == TeaKind.Empty && shape.Free[s] == 0 && shape.Touching(board, s, TeaKind.Tea) >= 2),
                    TeaKind.OrangeCat => false,
                    _ => Enumerable.Range(1, TeaBoard.Squares - 1).Any(s => board[s] == TeaKind.Empty && shape.JoinSize(board, s, k) >= 3),
                };
                share += can ? p : 0;
            }

            coverage = (int)Math.Round(share * 10);
        }

        into[n++] = (4, (coverage * 9) + Math.Min(8, shape.Empties));

        // rule 7: what a joker in the tray could finish
        var tray = board.TrayPiece switch { TeaKind.Oven => 1, TeaKind.Cutlery => 2, _ => 0 };
        var best = 0;
        if (tray == 1)
        {
            for (var s = 1; s < TeaBoard.Squares; s++)
            {
                if (board[s] != TeaKind.Empty)
                {
                    continue;
                }

                for (var k = TeaKind.Cookie; k <= TeaKind.Sundae; k++)
                {
                    if (TeaKinds.Joins(k) && shape.JoinSize(board, s, k) >= 3)
                    {
                        best = Math.Max(best, Math.Min(9, TeaKinds.Level(TeaKinds.Next(k)) + (k >= TeaKind.Tea ? 5 : 0) + shape.ChainLength(board, s, k) - 1));
                    }
                }
            }
        }
        else if (tray == 2)
        {
            for (var s = 1; s < TeaBoard.Squares; s++)
            {
                var target = board[s];
                var code = target switch
                {
                    TeaKind.OrangeCat => 6,
                    TeaKind.WhiteCat => shape.Touching(board, s, TeaKind.Tea) >= 2 ? 8 : 4,
                    TeaKind.BlackTea or TeaKind.Unknown => 5,
                    _ => 0,
                };
                best = Math.Max(best, code);
            }
        }

        into[n++] = (5, (((tray * 10) + best) * 8) + room);

        // rule 5: each White Cat group's pen
        for (var gi = 0; gi < shape.Groups.Count; gi++)
        {
            var g = shape.Groups[gi];
            if (g.Kind != TeaKind.WhiteCat)
            {
                continue;
            }

            var r = shape.PenRoom(board, gi);
            var roomBand = r <= 1 ? 0 : r == 2 ? 1 : r == 3 ? 2 : r <= 7 ? 3 : 4;
            var align = 0;
            if (r <= 3)
            {
                var teas = 0;
                for (var s = 1; s < TeaBoard.Squares; s++)
                {
                    if (board[s] == TeaKind.Empty && TeaBoard.Neighbours[s].Any(nb => shape.GroupOf[nb] == gi))
                    {
                        teas = Math.Max(teas, shape.Touching(board, s, TeaKind.Tea));
                    }
                }

                align = g.Size + teas >= 3 ? 2 : g.Size + teas == 2 ? 1 : 0;
            }

            into[n++] = (1, (((roomBand * 3) + Math.Min(2, g.Size - 1)) * 3) + align);
        }

        return n;
    }

    /// <summary>The usual next pieces and their chances (the user's rounds), for the endgame coverage.</summary>
    private static readonly (TeaKind Kind, double Chance)[] Typical =
    [
        (TeaKind.Cookie, 0.58), (TeaKind.Sponge, 0.15), (TeaKind.WhiteCat, 0.12), (TeaKind.Oven, 0.054), (TeaKind.OrangeCat, 0.05),
        (TeaKind.Cutlery, 0.025), (TeaKind.Pudding, 0.01), (TeaKind.Choco, 0.01),
    ];
}
