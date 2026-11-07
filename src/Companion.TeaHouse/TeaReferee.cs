namespace Companion.TeaHouse;

/// <summary>
/// Rule 4 of the rule design (2026-10-29), with the user's own rule as its first key: when the leading options are closer
/// than the learned values' noise, expert tie-breaks decide instead of the noise. Only options looked at to the same
/// depth are compared; outside the band the move finder's order stands, so nothing is banned.
/// <list type="number">
/// <item>K0, the user's rule ("there's no way it wants me to release the orange cat from its prison"): within
/// <see cref="PrisonerBand"/>, an option that keeps an Orange Cat in the tray beats one that places it on the board.</item>
/// <item>K1 cash the top pair: a joker spent on a high use (an Oven making a Chocolate Cake or better, or the level above the
/// best open pair; a Cutlery whose cat's tea joins; a Cutlery on a blocker or an Orange Cat late in the round).</item>
/// <item>K2 keep on low stakes: when the leader spends a joker on a low use, keeping it goes first (not in the last 4
/// squares, where a joker still held is worth nothing).</item>
/// <item>K3 protect the lifeline: not filling the last empty neighbour of the best open Pudding-or-better pair without
/// joining it.</item>
/// <item>K4 sure points: more joins now, then the higher piece created now.</item>
/// <item>K5 the move finder's value.</item>
/// </list>
/// Pure; thread-safe.
/// </summary>
public static class TeaReferee
{
    /// <summary>Expected points within which two options count as equal (the learned values' noise).</summary>
    public const double NoiseBand = 300;

    /// <summary>The band with an Orange Cat on the board (more luck in every value).</summary>
    public const double OrangeBand = 600;

    /// <summary>The band for keeping an Orange Cat in the tray (the user is sure of this one).</summary>
    public const double PrisonerBand = 1000;

    /// <summary>The options reordered by the tie-breaks (the first one carries the reason when the referee changed it).</summary>
    public static IReadOnlyList<TeaAdvice> Apply(IReadOnlyList<TeaAdvice> ranked, TeaBoard board, TeaKind current, bool keepPrisoner = true)
    {
        if (ranked.Count < 2)
        {
            return ranked;
        }

        var depth = ranked[0].Depth;
        var same = ranked.Where(o => o.Depth == depth).ToList();
        var leader = same[0];
        string? reason = null;

        // K0: the Orange Cat stays in the tray on a close call
        if (keepPrisoner && Releases(leader, board) && same.Where(o => !Releases(o, board) && o.Value >= leader.Value - PrisonerBand).MaxBy(o => o.Value) is { } kept)
        {
            leader = kept;
            reason = "close call: kept the Orange Cat in the tray";
        }

        var band = Enumerable.Range(1, TeaBoard.Squares - 1).Any(i => board[i] == TeaKind.OrangeCat) ? OrangeBand : NoiseBand;
        var block = same.Where(o => o.Value >= leader.Value - band && (o == leader || !keepPrisoner || !Releases(o, board) || Releases(leader, board))).ToList();
        if (block.Count >= 2)
        {
            var top = BestOpenPair(board);
            var empties = board.EmptyCount;
            var leaderSpendsLow = Spends(leader) && !HighUse(leader, board, top, empties);
            var chosen = block
                .OrderBy(o => Spends(o) && HighUse(o, board, top, empties) ? 0 : 1)
                .ThenBy(o => leaderSpendsLow && empties > 4 && Keeps(o, board, current) ? 0 : 1)
                .ThenBy(o => FillsLifeline(o, board) ? 1 : 0)
                .ThenByDescending(o => o.Joins)
                .ThenByDescending(o => TeaKinds.Level(o.Top))
                .ThenByDescending(o => o.Value)
                .First();
            if (chosen != leader)
            {
                reason = chosen.Action == TeaAction.Store || Keeps(chosen, board, current)
                    ? "close call: kept the joker for a bigger use"
                    : Spends(chosen) ? "close call: used the joker where it pays most" : "close call: the surer points";
                leader = chosen;
            }
        }

        if (leader == ranked[0])
        {
            return ranked;
        }

        var result = new List<TeaAdvice>(ranked.Count) { leader with { Reason = reason } };
        result.AddRange(ranked.Where(o => o != leader));
        return result;
    }

    /// <summary>True when the option puts the tray's Orange Cat on the board.</summary>
    internal static bool Releases(TeaAdvice o, TeaBoard board) =>
        o.Action == TeaAction.Swap && o.Piece == TeaKind.OrangeCat && board.TrayPiece == TeaKind.OrangeCat;

    private static bool Spends(TeaAdvice o) => TeaSolver.IsJoker(o.Piece) && o.Action != TeaAction.Store;

    /// <summary>Keeps the joker (rule 2's definition).</summary>
    private static bool Keeps(TeaAdvice o, TeaBoard board, TeaKind current) =>
        (o.Action == TeaAction.Store && TeaSolver.IsJoker(current))
        || (o.Action == TeaAction.Swap && TeaSolver.IsJoker(current) && !TeaSolver.IsJoker(o.Piece))
        || (o.Action == TeaAction.Place && !TeaSolver.IsJoker(current) && TeaSolver.IsJoker(board.TrayPiece));

    /// <summary>A joker spent where it pays most (K1).</summary>
    private static bool HighUse(TeaAdvice o, TeaBoard board, TeaKind topPair, int empties)
    {
        if (o.Piece == TeaKind.Oven)
        {
            var needed = Math.Max(TeaKinds.Level(TeaKind.Choco), topPair == TeaKind.Empty ? 0 : TeaKinds.Level(TeaKinds.Next(topPair)));
            return TeaKinds.Level(o.Top) >= needed || o.Top is TeaKind.Sundae or TeaKind.TeaHouse;
        }

        if (o.Piece != TeaKind.Cutlery || o.Square < 0)
        {
            return false;
        }

        var target = board[o.Square];
        return (TeaKinds.IsCat(target) && o.Joins >= 1)
            || (target is TeaKind.BlackTea or TeaKind.Unknown && empties <= 10)
            || (target == TeaKind.OrangeCat && empties <= 12)
            || empties <= 2;
    }

    /// <summary>The highest cake or tea with an open pair (two or more touching, an empty square next to them).</summary>
    internal static TeaKind BestOpenPair(TeaBoard board)
    {
        Span<int> group = stackalloc int[TeaBoard.Squares];
        var best = TeaKind.Empty;
        for (var i = 1; i < TeaBoard.Squares; i++)
        {
            var k = board[i];
            if (!TeaKinds.Joins(k) || TeaKinds.Level(k) <= TeaKinds.Level(best))
            {
                continue;
            }

            var n = TeaRules.Group(board, i, k, group);
            if (n < 2)
            {
                continue;
            }

            for (var g = 0; g < n; g++)
            {
                if (TeaBoard.Neighbours[group[g]].Any(nb => board[nb] == TeaKind.Empty))
                {
                    best = k;
                    break;
                }
            }
        }

        return best;
    }

    /// <summary>The option fills the last empty neighbour of the best open Pudding-or-better pair without joining it (K3).</summary>
    private static bool FillsLifeline(TeaAdvice o, TeaBoard board)
    {
        if (o.Square < 0 || o.Joins > 0 || board[o.Square] != TeaKind.Empty)
        {
            return false;
        }

        var top = BestOpenPair(board);
        if (TeaKinds.Level(top) < TeaKinds.Level(TeaKind.Pudding) || top > TeaKind.Luxury)
        {
            return false;
        }

        Span<int> group = stackalloc int[TeaBoard.Squares];
        for (var i = 1; i < TeaBoard.Squares; i++)
        {
            if (board[i] != top)
            {
                continue;
            }

            var n = TeaRules.Group(board, i, top, group);
            if (n < 2)
            {
                continue;
            }

            var openings = new HashSet<int>();
            for (var g = 0; g < n; g++)
            {
                foreach (var nb in TeaBoard.Neighbours[group[g]])
                {
                    if (board[nb] == TeaKind.Empty)
                    {
                        openings.Add(nb);
                    }
                }
            }

            if (openings.Count == 1 && openings.Contains(o.Square))
            {
                return true;
            }
        }

        return false;
    }
}
