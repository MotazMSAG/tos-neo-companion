namespace Companion.TeaHouse;

/// <summary>
/// Simulated rounds for tuning the move finder's weights offline: pieces drawn from the model's odds, a random start
/// board, the move finder's best option every turn, until the board is full. Uses only the rules as the helper knows
/// them, so it tunes against that model of the game (live play remains the real test).
/// </summary>
public static class TeaSelfPlay
{
    /// <summary>One simulated round's result.</summary>
    /// <param name="Score">Points earned.</param>
    /// <param name="Turns">Pieces placed.</param>
    /// <param name="Top">The highest piece made.</param>
    public readonly record struct Round(double Score, int Turns, TeaKind Top);

    /// <summary>One decision of a simulated round, shown to a watcher before it is played.</summary>
    /// <param name="Number">Pieces placed so far.</param>
    /// <param name="Score">Points earned so far.</param>
    /// <param name="Best">The chosen option (its Value is the points it pays now plus the search's value of what follows).</param>
    /// <param name="Board">The board before the move (do not keep it: the round goes on changing it).</param>
    /// <param name="Current">The piece in hand.</param>
    public readonly record struct Turn(int Number, double Score, TeaAdvice Best, TeaBoard Board, TeaKind Current);

    /// <summary>Plays one round with <paramref name="options"/> (seeded: the same seed draws the same pieces).</summary>
    public static Round Play(TeaModel model, TeaSolverOptions options, int seed, int maxTurns = 1500, Action<Turn>? watch = null, TeaBoard? from = null)
    {
        // the pieces and the cats draw from separate streams, the cats' keyed by the turn: two strategies on one seed get
        // the same pieces and the same cat luck turn by turn, so a comparison is truly paired (the rule designers found
        // that one shared stream let two arms drift apart after their first different choice); a given board
        // (<paramref name="from"/>, e.g. a real one) replaces the random start
        var rng = new Random(seed);
        var board = from?.Clone() ?? StartBoard(rng);
        double score = 0;
        var top = TeaKind.Empty;
        var current = Draw(model, rng);
        var turns = 0;
        while (turns < maxTurns && board.EmptyCount > 0)
        {
            var advice = TeaSolver.Advise(board, current, model, options);
            if (advice.Count == 0)
            {
                break;
            }

            var a = advice[0];
            watch?.Invoke(new Turn(turns, score, a, board, current));
            if (a.Action == TeaAction.Store)
            {
                board.TrayPiece = current;
                current = Draw(model, rng);
                continue;
            }

            if (a.Action == TeaAction.Swap)
            {
                board.TrayPiece = current;
            }

            var played = TeaRules.Play(board, a.Piece, a.Square, model, new Random(unchecked((seed * 1000003) + turns)));
            score += played.Points;
            if (TeaKinds.Level(played.Top) > TeaKinds.Level(top) || (top == TeaKind.Empty && played.Top != TeaKind.Empty))
            {
                top = played.Top;
            }

            turns++;
            current = Draw(model, rng);
        }

        return new Round(score, turns, top);
    }

    /// <summary>The average score of <paramref name="rounds"/> rounds (seeds 1..rounds).</summary>
    public static double Average(TeaModel model, TeaSolverOptions options, int rounds, int firstSeed = 1)
    {
        var sum = 0.0;
        for (var s = 0; s < rounds; s++)
        {
            sum += Play(model, options, firstSeed + s).Score;
        }

        return sum / rounds;
    }

    /// <summary>A piece drawn from the model's odds.</summary>
    public static TeaKind Draw(TeaModel model, Random rng)
    {
        var roll = rng.NextDouble();
        var acc = 0.0;
        foreach (var k in TeaKinds.Spawnable)
        {
            acc += model.Odds(k);
            if (roll < acc)
            {
                return k;
            }
        }

        return TeaKind.Cookie;
    }

    /// <summary>A start board like the game's: a few low pieces scattered (no joins).</summary>
    internal static TeaBoard StartBoard(Random rng)
    {
        var board = new TeaBoard();
        var n = 4 + rng.Next(4);
        Span<int> group = stackalloc int[TeaBoard.Squares];
        for (var placed = 0; placed < n;)
        {
            var s = 1 + rng.Next(TeaBoard.Squares - 1);
            var k = rng.NextDouble() < 0.7 ? TeaKind.Cookie : TeaKind.Sponge;
            if (board[s] != TeaKind.Empty)
            {
                continue;
            }

            board[s] = k;
            if (TeaRules.Group(board, s, k, group) >= 3)
            {
                board[s] = TeaKind.Empty;
                continue;
            }

            placed++;
        }

        return board;
    }
}
