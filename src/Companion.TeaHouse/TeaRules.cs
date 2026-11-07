namespace Companion.TeaHouse;

/// <summary>What one placement did.</summary>
/// <param name="Legal">False when the piece may not go there (nothing changed).</param>
/// <param name="Points">Points the joins paid (placing points included).</param>
/// <param name="Joins">How many joins happened (a chain counts each step).</param>
/// <param name="Top">The highest piece a join created, or <see cref="TeaKind.Empty"/>.</param>
/// <param name="Became">What the placed piece became on the board (an Oven's choice, Black Tea, a cat's tea...).</param>
public readonly record struct TeaPlacement(bool Legal, double Points, int Joins, TeaKind Top, TeaKind Became);

/// <summary>
/// The game's rules as the user described them (2026-10-29) and the tips show:
/// <list type="bullet">
/// <item>a piece goes on an empty play square (the Cutlery on an occupied one); the tray square is never a place;</item>
/// <item>three or more touching pieces of a kind (up, down, left, right; never diagonal) join into the next kind on the
/// square just placed, and that may join again straight away; a group of four pays the same as three;</item>
/// <item>the Oven becomes whichever touching kind makes the best join (assumed: the most points; the user does not know),
/// else Black Tea; an Oven that joins pays only its joins (the log, 2026-10-30);</item>
/// <item>the Cutlery removes a piece; a cat it meets becomes a tea (which may join);</item>
/// <item>after the placement, cats with no way out become teas (a boxed-in group of three White Cats becomes a Sundae),
/// then each other White Cat walks one square up, down, left or right to an empty square and each Orange Cat jumps to any
/// empty square (where they go is random as far as anyone knows); an Orange Cat is boxed in only when no square is empty.</item>
/// </list>
/// Squares are numbered as in <see cref="TeaBoard"/>. Not thread-safe per board; the methods share no state.
/// </summary>
public static class TeaRules
{
    /// <summary>
    /// A cat placed this turn walks or jumps away in the same turn (the user, 2026-10-29: "it always moves from the place
    /// you put it on"; the log agrees: in 29 of 32 placements the cat had left its square by the next settled board). The
    /// rules first assumed it stayed put for a turn. A cat boxed in where it is placed still becomes a tea at once.
    /// </summary>
    public const bool CatMovesAtOnce = true;

    /// <summary>
    /// The share of Orange Cat jumps that are a short hop to an empty neighbour; the rest land on any empty square. The log
    /// of 2026-10-29 (93 jumps): 17 % landed next to the old square where purely random jumps would give 8 %, i.e. about 1
    /// jump in 10 is a hop. Provisional: re-measure as rounds come in.
    /// </summary>
    public const double OrangeHop = 0.10;

    /// <summary>The order the cats move in: every Orange Cat first, then the White Cats.</summary>
    private static readonly TeaKind[] OrangesFirst = [TeaKind.OrangeCat, TeaKind.WhiteCat];

    /// <summary>True when <paramref name="piece"/> may be placed on <paramref name="square"/>.</summary>
    public static bool CanPlace(TeaBoard board, TeaKind piece, int square)
    {
        if (square <= TeaBoard.Tray || square >= TeaBoard.Squares)
        {
            return false;
        }

        return piece == TeaKind.Cutlery ? board[square] != TeaKind.Empty : board[square] == TeaKind.Empty;
    }

    /// <summary>The full turn: the placement, then the cats (random moves from <paramref name="rng"/>).</summary>
    public static TeaPlacement Play(TeaBoard board, TeaKind piece, int square, TeaModel model, Random rng)
    {
        var placed = Place(board, piece, square, model);
        if (!placed.Legal)
        {
            return placed;
        }

        var (points, joins, top) = Cats(board, model, rng, TeaKinds.IsCat(piece) ? square : -1);
        return placed with { Points = placed.Points + points, Joins = placed.Joins + joins, Top = Higher(placed.Top, top) };
    }

    /// <summary>The placement and its joins only (no cat moves).</summary>
    public static TeaPlacement Place(TeaBoard board, TeaKind piece, int square, TeaModel model)
    {
        if (!CanPlace(board, piece, square))
        {
            return default;
        }

        double points = model.PlacePoints[(int)piece];
        var joins = 0;
        var top = TeaKind.Empty;
        switch (piece)
        {
            case TeaKind.Cutlery:
                if (TeaKinds.IsCat(board[square]))
                {
                    Settle(board, square, TeaKind.Tea, model, ref points, ref joins, ref top);
                }
                else
                {
                    board[square] = TeaKind.Empty;
                }

                return new TeaPlacement(true, points, joins, top, board[square]);

            case TeaKind.Oven:
                var best = OvenChoice(board, square, model);
                if (best == TeaKind.Empty)
                {
                    board[square] = TeaKind.BlackTea;
                    return new TeaPlacement(true, points, 0, TeaKind.Empty, TeaKind.BlackTea);
                }

                // an Oven that joins pays only its joins (2026-10-30 log: 9 of 9 Oven joins paid exactly their join points)
                points = 0;
                Settle(board, square, best, model, ref points, ref joins, ref top);
                return new TeaPlacement(true, points, joins, top, best);

            default:
                Settle(board, square, piece, model, ref points, ref joins, ref top);
                return new TeaPlacement(true, points, joins, top, piece);
        }
    }

    /// <summary>
    /// The kind an Oven placed on <paramref name="square"/> becomes: of the joinable kinds touching it, the one whose
    /// join pays the most (a tie: the higher kind); <see cref="TeaKind.Empty"/> when none would join (it becomes Black Tea).
    /// </summary>
    public static TeaKind OvenChoice(TeaBoard board, int square, TeaModel model)
    {
        var best = TeaKind.Empty;
        var bestPoints = -1.0;
        var trial = new TeaBoard();
        foreach (var n in TeaBoard.Neighbours[square])
        {
            var k = board[n];
            if (!TeaKinds.Joins(k) || k == best)
            {
                continue;
            }

            board.CopyTo(trial);
            double points = 0;
            var joins = 0;
            var top = TeaKind.Empty;
            Settle(trial, square, k, model, ref points, ref joins, ref top);
            if (joins > 0 && (points > bestPoints || (points == bestPoints && k > best)))
            {
                best = k;
                bestPoints = points;
            }
        }

        return best;
    }

    /// <summary>
    /// The cats' turn: boxed-in cats become teas (and join), then the others move. <paramref name="justPlaced"/> is a cat
    /// placed this turn, or -1: it may be boxed in at once, and it moves at once too (<see cref="CatMovesAtOnce"/>).
    /// </summary>
    public static (double Points, int Joins, TeaKind Top) Cats(TeaBoard board, TeaModel model, Random? rng, int justPlaced = -1)
    {
        double points = 0;
        var joins = 0;
        var top = TeaKind.Empty;
        Trap(board, model, ref points, ref joins, ref top, justPlaced);
        if (rng is not null)
        {
            Move(board, rng, justPlaced);
        }

        return (points, joins, top);
    }

    /// <summary>The square's group: touching squares of the same kind (written into <paramref name="group"/>); its size.</summary>
    internal static int Group(TeaBoard board, int start, TeaKind kind, Span<int> group)
    {
        Span<bool> seen = stackalloc bool[TeaBoard.Squares];
        var n = 0;
        group[n++] = start;
        seen[start] = true;
        for (var i = 0; i < n; i++)
        {
            foreach (var nb in TeaBoard.Neighbours[group[i]])
            {
                if (!seen[nb] && board[nb] == kind)
                {
                    seen[nb] = true;
                    group[n++] = nb;
                }
            }
        }

        return n;
    }

    /// <summary>Puts <paramref name="kind"/> on <paramref name="square"/> and joins while a group of three or more forms.</summary>
    private static void Settle(TeaBoard board, int square, TeaKind kind, TeaModel model, ref double points, ref int joins, ref TeaKind top)
    {
        Span<int> group = stackalloc int[TeaBoard.Squares];
        board[square] = kind;
        while (true)
        {
            var next = TeaKinds.Next(kind);
            if (next == TeaKind.Empty)
            {
                return;
            }

            var n = Group(board, square, kind, group);
            if (n < 3)
            {
                return;
            }

            for (var i = 0; i < n; i++)
            {
                board[group[i]] = TeaKind.Empty;
            }

            board[square] = next;
            points += model.JoinPoints[(int)next];
            joins++;
            top = Higher(top, next);
            kind = next;
        }
    }

    /// <summary>
    /// White Cats in a touching group where none has an empty square or an Orange Cat next to it become teas, as do Orange
    /// Cats when no square is empty; the new teas join (with each other and with teas already there), in reading order.
    /// An Orange Cat is no wall for a White Cat: it is about to jump away (2026-10-30, the user's idea, measured on 586 real
    /// moves with White Cats: the White Cats left afterwards matched 570 times this way, 534 times when Orange Cats counted
    /// as walls). On a full board the Orange Cats cannot move, so they are walls then.
    /// </summary>
    private static void Trap(TeaBoard board, TeaModel model, ref double points, ref int joins, ref TeaKind top, int justPlaced)
    {
        Span<int> group = stackalloc int[TeaBoard.Squares];
        Span<bool> done = stackalloc bool[TeaBoard.Squares];
        Span<int> trapped = stackalloc int[TeaBoard.Squares];
        var t = 0;
        var noRoom = board.EmptyCount == 0;
        for (var i = 1; i < TeaBoard.Squares; i++)
        {
            if (done[i])
            {
                continue;
            }

            if (board[i] == TeaKind.OrangeCat && noRoom)
            {
                trapped[t++] = i;
                continue;
            }

            if (board[i] != TeaKind.WhiteCat)
            {
                continue;
            }

            var n = Group(board, i, TeaKind.WhiteCat, group);
            var free = false;
            for (var g = 0; g < n; g++)
            {
                done[group[g]] = true;
                foreach (var nb in TeaBoard.Neighbours[group[g]])
                {
                    free |= board[nb] == TeaKind.Empty || (board[nb] == TeaKind.OrangeCat && !noRoom);
                }
            }

            if (!free)
            {
                for (var g = 0; g < n; g++)
                {
                    trapped[t++] = group[g];
                }
            }
        }

        if (t == 0)
        {
            return;
        }

        // where the teas join: on the cat just placed when it is among them, else on the highest-numbered square (the log
        // of 2026-10-29: the Sundae appeared on the placed cat's square 2 of 2 times, else on the highest-numbered boxed-in
        // cat 3 of 4 times; the rules first used the lowest)
        var cells = trapped[..t];
        cells.Sort();
        cells.Reverse();
        var placedIndex = cells.IndexOf(justPlaced);
        if (placedIndex > 0)
        {
            (cells[0], cells[placedIndex]) = (cells[placedIndex], cells[0]);
        }

        foreach (var c in cells)
        {
            board[c] = TeaKind.Tea;
        }

        foreach (var c in cells)
        {
            if (board[c] == TeaKind.Tea)
            {
                Settle(board, c, TeaKind.Tea, model, ref points, ref joins, ref top);
            }
        }
    }

    /// <summary>
    /// Every way the cats can walk after the boxed-in ones became teas, with its chance (each White Cat, in reading order,
    /// picks one of its empty neighbours evenly; one with none stays). Null when there are more than
    /// <paramref name="limit"/> outcomes, or when an Orange Cat would jump (any empty square: too many to list); the
    /// caller then samples random moves instead.
    /// </summary>
    public static List<(TeaBoard Board, double Chance)>? CatOutcomes(TeaBoard board, int justPlaced, int limit, bool includeOrange = false)
    {
        // Orange Cats jump first, then the White Cats walk (OrangesFirst)
        var whites = new List<int>();
        var cats = new List<int>();
        for (var i = 1; i < TeaBoard.Squares; i++)
        {
            if (i == justPlaced && !CatMovesAtOnce)
            {
                continue;
            }

            if (board[i] == TeaKind.OrangeCat)
            {
                // rule 8 of the rule design: near the end (few empty squares) every jump is listed too
                if (!includeOrange)
                {
                    return null;
                }

                cats.Add(i);
            }

            if (board[i] == TeaKind.WhiteCat)
            {
                whites.Add(i);
            }
        }

        cats.AddRange(whites);
        var outcomes = new List<(TeaBoard, double)>();
        return Walk(board.Clone(), cats, 0, 1.0, outcomes, limit) ? outcomes : null;
    }

    /// <summary>The cats' moves at random (each White Cat one step, each Orange Cat a jump), the boxed-in ones already handled.</summary>
    public static void MoveCats(TeaBoard board, Random rng, int justPlaced = -1) => Move(board, rng, justPlaced);

    private static bool Walk(TeaBoard b, List<int> cats, int index, double chance, List<(TeaBoard, double)> outcomes, int limit)
    {
        if (index == cats.Count)
        {
            outcomes.Add((b, chance));
            return outcomes.Count <= limit;
        }

        var from = cats[index];
        var kind = b[from];

        Span<int> options = stackalloc int[TeaBoard.Squares];
        var n = 0;
        if (kind == TeaKind.OrangeCat)
        {
            for (var s = 1; s < TeaBoard.Squares; s++)
            {
                if (b[s] == TeaKind.Empty)
                {
                    options[n++] = s;
                }
            }
        }
        else
        {
            foreach (var nb in TeaBoard.Neighbours[from])
            {
                if (b[nb] == TeaKind.Empty)
                {
                    options[n++] = nb;
                }
            }
        }

        if (n == 0)
        {
            return Walk(b, cats, index + 1, chance, outcomes, limit);
        }

        var hops = 0;
        if (kind == TeaKind.OrangeCat)
        {
            foreach (var nb in TeaBoard.Neighbours[from])
            {
                hops += b[nb] == TeaKind.Empty ? 1 : 0;
            }
        }

        var targets = options[..n].ToArray();
        foreach (var to in targets)
        {
            var next = b.Clone();
            next[to] = kind;
            next[from] = TeaKind.Empty;
            var p = kind == TeaKind.OrangeCat && hops > 0
                ? ((1 - OrangeHop) / n) + (TeaBoard.Neighbours[from].Contains(to) ? OrangeHop / hops : 0)
                : 1.0 / n;
            if (!Walk(next, cats, index + 1, chance * p, outcomes, limit))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Each Orange Cat jumps to a random empty square, then each White Cat (reading order, as they stood) walks to a random
    /// empty neighbour; a White Cat whose free squares the Orange Cats took stays where it is (no tea: it was not boxed in
    /// when the turn began). The order is the user's idea (2026-10-30), seen in the real moves: 38 White Cats stayed with
    /// room because every free square next to them had been taken by an Orange Cat.
    /// </summary>
    private static void Move(TeaBoard board, Random rng, int justPlaced)
    {
        Span<int> cats = stackalloc int[TeaBoard.Squares];
        Span<int> options = stackalloc int[TeaBoard.Squares];
        var n = 0;
        foreach (var kind in OrangesFirst)
        {
            for (var i = 1; i < TeaBoard.Squares; i++)
            {
                if (board[i] == kind && (i != justPlaced || CatMovesAtOnce))
                {
                    cats[n++] = i;
                }
            }
        }

        for (var i = 0; i < n; i++)
        {
            var from = cats[i];
            var kind = board[from];
            var o = 0;
            if (kind == TeaKind.WhiteCat)
            {
                foreach (var nb in TeaBoard.Neighbours[from])
                {
                    if (board[nb] == TeaKind.Empty)
                    {
                        options[o++] = nb;
                    }
                }
            }
            else if (kind == TeaKind.OrangeCat)
            {
                // mostly anywhere; now and then a short hop to a neighbour (OrangeHop)
                if (rng.NextDouble() < OrangeHop)
                {
                    foreach (var nb in TeaBoard.Neighbours[from])
                    {
                        if (board[nb] == TeaKind.Empty)
                        {
                            options[o++] = nb;
                        }
                    }
                }

                if (o == 0)
                {
                    for (var s = 1; s < TeaBoard.Squares; s++)
                    {
                        if (board[s] == TeaKind.Empty)
                        {
                            options[o++] = s;
                        }
                    }
                }
            }

            if (o == 0)
            {
                continue;
            }

            var to = options[rng.Next(o)];
            board[to] = kind;
            board[from] = TeaKind.Empty;
        }
    }

    private static TeaKind Higher(TeaKind a, TeaKind b) => TeaKinds.Level(b) > TeaKinds.Level(a) || (a == TeaKind.Empty && b != TeaKind.Empty) ? b : a;
}
