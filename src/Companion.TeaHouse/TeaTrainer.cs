namespace Companion.TeaHouse;

/// <summary>
/// Teaches a <see cref="TeaNet"/> by self-play (temporal difference learning on afterstates, as for 2048): each move picks
/// the option with the most points now plus the network's value of the board right after it (the placement and its
/// joins, the boxed-in cats; before the cats walk and the next piece comes), then nudges the value of the previous such
/// board toward what this move paid plus this board's value. The game's chance (the cats, the next piece) does the
/// exploring. Runs on the rules and the model (points, piece odds) the helper learned from the user's rounds.
/// </summary>
public static class TeaTrainer
{
    /// <summary>Plays one round, learning after every move; its points.</summary>
    public static double Round(TeaNet net, TeaModel model, Random rng, double rate, int maxTurns = 4000)
    {
        var board = TeaSelfPlay.StartBoard(rng);
        var current = TeaSelfPlay.Draw(model, rng);
        TeaBoard? previous = null;
        var trial = new TeaBoard();
        var chosen = new TeaBoard();
        double score = 0;
        for (var turns = 0; turns < maxTurns && board.EmptyCount > 0;)
        {
            var bestValue = double.NegativeInfinity;
            var bestReward = 0.0;
            var bestAction = TeaAction.Place;
            var bestSquare = -1;
            var bestPiece = current;
            foreach (var o in TeaSolver.Options(board, current))
            {
                board.CopyTo(trial);
                double reward = 0;
                if (o.Action == TeaAction.Store)
                {
                    trial.TrayPiece = current;
                }
                else
                {
                    if (o.Action == TeaAction.Swap)
                    {
                        trial.TrayPiece = current;
                    }

                    var placed = TeaRules.Place(trial, o.Piece, o.Square, model);
                    var (catPoints, _, _) = TeaRules.Cats(trial, model, rng: null, TeaKinds.IsCat(o.Piece) ? o.Square : -1);
                    reward = placed.Points + catPoints;
                }

                var value = reward + (trial.EmptyCount == 0 ? 0 : net.Value(trial));
                if (value > bestValue)
                {
                    bestValue = value;
                    bestReward = reward;
                    bestAction = o.Action;
                    bestSquare = o.Square;
                    bestPiece = o.Piece;
                    trial.CopyTo(chosen);
                }
            }

            if (double.IsNegativeInfinity(bestValue))
            {
                break;
            }

            if (previous is not null)
            {
                net.Learn(previous, bestValue, rate);
            }

            previous ??= new TeaBoard();
            chosen.CopyTo(previous);
            chosen.CopyTo(board);
            score += bestReward;
            if (bestAction != TeaAction.Store)
            {
                TeaRules.MoveCats(board, rng, TeaKinds.IsCat(bestPiece) ? bestSquare : -1);
                turns++;
            }

            current = TeaSelfPlay.Draw(model, rng);
        }

        if (previous is not null)
        {
            net.Learn(previous, 0, rate); // the round is over: nothing more to come
        }

        net.Trained++;
        return score;
    }
}
