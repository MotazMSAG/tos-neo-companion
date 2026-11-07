namespace Companion.TeaHouse;

/// <summary>
/// How much the move finder values each trait of a board, in points (scaled to the game's points as measured live on
/// 2026-10-29: Cookie 5, Sponge Cake 40, Pudding 200; tuned by self-play, see <see cref="TeaSelfPlay"/>).
/// </summary>
public sealed record TeaWeights
{
    /// <summary>Each empty play square.</summary>
    public double Empty { get; init; } = 96;

    /// <summary>Extra cost per missing square below <see cref="CrowdedBelow"/>, squared (a nearly full board is close to the end).</summary>
    public double Crowded { get; init; } = 120;

    /// <summary>Empty squares below which the board counts as crowded.</summary>
    public int CrowdedBelow { get; init; } = 8;

    /// <summary>A full board (the round is over).</summary>
    public double Full { get; init; } = -32000;

    /// <summary>Share of a join's points credited to a pair (or larger group) that has an empty square next to it.</summary>
    public double Pair { get; init; } = 0.45;

    /// <summary>Share credited to a pair with no empty square next to it (it can only join after something clears).</summary>
    public double DeadPair { get; init; } = 0.05;

    /// <summary>Share credited to a single joinable piece with an empty square next to it.</summary>
    public double Single { get; init; } = 0.08;

    /// <summary>Share of the next level's join credited when one empty square touches pairs of two consecutive levels (a chain).</summary>
    public double Chain { get; init; } = 0.25;

    /// <summary>Cost per step towards the middle for each piece of cake level 3 or more, per level.</summary>
    public double Central { get; init; } = 20;

    /// <summary>Cost per step towards the middle of a Black Tea, a top piece or an unknown picture (they never leave).</summary>
    public double BlockerCentral { get; init; } = 80;

    /// <summary>Each White Cat still free to walk.</summary>
    public double Cat { get; init; } = 200;

    /// <summary>Bonus for a White Cat with one way out left (about to become a tea).</summary>
    public double CatNearlyBoxed { get; init; } = 64;

    /// <summary>Each Orange Cat (only the Cutlery or a full board removes it).</summary>
    public double OrangeCat { get; init; } = 480;

    /// <summary>Each Black Tea.</summary>
    public double BlackTea { get; init; } = 240;

    /// <summary>Each unknown picture (treated as a blocker).</summary>
    public double Unknown { get; init; } = 240;

    /// <summary>The tray holding a useful piece (an Oven or Cutlery) instead of nothing.</summary>
    public double TrayJoker { get; init; } = 320;
}

/// <summary>Scores a board in points: what its pieces and space promise for the rest of the round (not the points already paid).</summary>
public static class TeaEvaluator
{
    /// <summary>Steps from the nearest corner along the edges (corners 0, the middle squares 4).</summary>
    internal static readonly int[] Centrality = BuildCentrality();

    /// <summary>The board's promise under <paramref name="w"/>.</summary>
    public static double Evaluate(TeaBoard board, TeaModel model, TeaWeights w)
    {
        var empties = board.EmptyCount;
        if (empties == 0)
        {
            return w.Full;
        }

        var h = w.Empty * empties;
        if (empties < w.CrowdedBelow)
        {
            var missing = w.CrowdedBelow - empties;
            h -= w.Crowded * missing * missing;
        }

        Span<int> group = stackalloc int[TeaBoard.Squares];
        Span<bool> done = stackalloc bool[TeaBoard.Squares];
        Span<bool> open = stackalloc bool[TeaBoard.Squares];
        // per empty square: the biggest pair level touching it, by ladder (for the chain bonus)
        Span<int> pairMask = stackalloc int[TeaBoard.Squares];
        for (var i = 1; i < TeaBoard.Squares; i++)
        {
            var k = board[i];
            if (k == TeaKind.Empty || done[i])
            {
                continue;
            }

            if (TeaKinds.Joins(k))
            {
                var n = TeaRules.Group(board, i, k, group);
                open.Clear();
                var openings = 0;
                for (var g = 0; g < n; g++)
                {
                    done[group[g]] = true;
                    foreach (var nb in TeaBoard.Neighbours[group[g]])
                    {
                        if (board[nb] == TeaKind.Empty && !open[nb])
                        {
                            open[nb] = true;
                            openings++;
                            if (n >= 2)
                            {
                                pairMask[nb] |= 1 << (int)k;
                            }
                        }
                    }

                    var level = TeaKinds.Level(k);
                    if (k <= TeaKind.Luxury && level >= 3)
                    {
                        h -= w.Central * level * Centrality[group[g]];
                    }
                }

                var value = model.JoinPoints[(int)TeaKinds.Next(k)];
                h += value * (n >= 2 ? (openings > 0 ? w.Pair : w.DeadPair) : (openings > 0 ? w.Single : 0));
                continue;
            }

            done[i] = true;
            switch (k)
            {
                case TeaKind.WhiteCat:
                    var free = 0;
                    foreach (var nb in TeaBoard.Neighbours[i])
                    {
                        free += board[nb] == TeaKind.Empty ? 1 : 0;
                    }

                    h -= w.Cat;
                    if (free == 1)
                    {
                        h += w.CatNearlyBoxed;
                    }

                    break;
                case TeaKind.OrangeCat:
                    h -= w.OrangeCat;
                    break;
                case TeaKind.BlackTea:
                    h -= w.BlackTea + (w.BlockerCentral * Centrality[i]);
                    break;
                case TeaKind.Unknown:
                    h -= w.Unknown + (w.BlockerCentral * Centrality[i]);
                    break;
                case TeaKind.Luxury or TeaKind.TeaHouse:
                    h -= w.BlockerCentral * Centrality[i];
                    break;
            }
        }

        // chains: one empty square touching pairs of two consecutive levels (placing the lower third there joins twice)
        for (var s = 1; s < TeaBoard.Squares; s++)
        {
            var mask = pairMask[s];
            if (mask == 0)
            {
                continue;
            }

            for (var k = TeaKind.Cookie; k <= TeaKind.Sundae; k++)
            {
                var next = TeaKinds.Next(k);
                if (next != TeaKind.Empty && (mask & (1 << (int)k)) != 0 && (mask & (1 << (int)next)) != 0)
                {
                    h += w.Chain * model.JoinPoints[(int)TeaKinds.Next(next)];
                }
            }
        }

        if (board.TrayPiece is TeaKind.Oven or TeaKind.Cutlery)
        {
            h += w.TrayJoker;
        }

        return h;
    }

    private static int[] BuildCentrality()
    {
        var c = new int[TeaBoard.Squares];
        for (var i = 0; i < TeaBoard.Squares; i++)
        {
            var r = TeaBoard.Row(i);
            var col = TeaBoard.Column(i);
            c[i] = Math.Min(r, TeaBoard.Side - 1 - r) + Math.Min(col, TeaBoard.Side - 1 - col);
        }

        return c;
    }
}
