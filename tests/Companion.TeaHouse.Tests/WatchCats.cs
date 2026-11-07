using System.Text;
using Companion.TeaHouse;

namespace Companion.TeaHouse.Tests;

/// <summary>
/// Watches self-play rounds for Orange Cat moments (2026-10-30, the user: "see what happens to trapped or trapping orange
/// cats"): every Orange Cat placement (square, boxed in or not, how much the square matters to the search), what the cats
/// do afterwards, and every Orange Cat that becomes a tea, with the rule checks: a cat that stays put or becomes a tea
/// while a square is empty is flagged. TEA_NET brain, TEA_DEPTH (2), TEA_ROUNDS (40) rounds, the first TEA_TELL (2) told
/// move by move. Writes TEA_WATCH (a text report). Category probe.
/// </summary>
[TestClass]
public sealed class WatchCatsProbe
{
    private static readonly int[] Checkpoints = [25, 50, 100, 150, 200, 250, 300];

    private sealed class Stats
    {
        public int Placements;
        public int BoxedChosen;
        public double BoxedShareOfChoices;
        public double SpreadSum;
        public double GapSum;
        public int Trapped;
        public int TrappedFull;
        public int CutleryOnOrange;
        public int Stayed;
        public int PlacedStayed;
        public int Moves;
        public readonly StringBuilder Story = new();

        /// <summary>Cats and empty squares on the board at <see cref="Checkpoints"/> (white, orange, empty), -1 when the round had ended.</summary>
        public readonly (int White, int Orange, int Empty)[] AtCheck = [.. Checkpoints.Select(_ => (-1, -1, -1))];
    }

    [TestMethod]
    [TestCategory("probe")]
    public void WatchOrangeCats()
    {
        var netPath = Environment.GetEnvironmentVariable("TEA_NET") ?? @"C:\ProgramData\TOS-companion\teahouse\net.bin";
        using var f = File.OpenRead(netPath);
        var net = TeaNet.Load(f);
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("TEA_ROUNDS"), out var r) ? r : 40;
        var tell = int.TryParse(Environment.GetEnvironmentVariable("TEA_TELL"), out var t) ? t : 2;
        var depth = int.TryParse(Environment.GetEnvironmentVariable("TEA_DEPTH"), out var d) ? d : 2;
        var output = Environment.GetEnvironmentVariable("TEA_WATCH") ?? Path.Combine(Path.GetTempPath(), "tea_watch.txt");
        var model = TeaModel.Default();
        var options = new TeaSolverOptions { Net = net, Depth = depth };
        var all = new Stats[rounds];
        Parallel.For(0, rounds, new ParallelOptions { MaxDegreeOfParallelism = 12 }, i => all[i] = Watch(model, options, 50000 + i, i < tell));

        var o = new StringBuilder();
        var s = new Stats();
        foreach (var x in all)
        {
            s.Placements += x.Placements;
            s.BoxedChosen += x.BoxedChosen;
            s.BoxedShareOfChoices += x.BoxedShareOfChoices;
            s.SpreadSum += x.SpreadSum;
            s.GapSum += x.GapSum;
            s.Trapped += x.Trapped;
            s.TrappedFull += x.TrappedFull;
            s.CutleryOnOrange += x.CutleryOnOrange;
            s.Stayed += x.Stayed;
            s.PlacedStayed += x.PlacedStayed;
            s.Moves += x.Moves;
        }

        var n = Math.Max(1, s.Placements);
        o.AppendLine($"{rounds} rounds, {s.Moves} moves, {s.Placements} Orange Cat placements");
        o.AppendLine($"placed in a boxed-in square (no empty neighbour): {s.BoxedChosen * 100.0 / n:F0}% of placements; boxed-in squares were {s.BoxedShareOfChoices * 100.0 / n:F0}% of the empty squares on offer");
        o.AppendLine($"how much the square matters to the search (Orange Cat placements): best minus worst square {s.SpreadSum / n:F0} points on average, best minus second {s.GapSum / n:F0}");
        o.AppendLine($"Orange Cats that became a tea: {s.Trapped} ({s.TrappedFull} with the board full, {s.Trapped - s.TrappedFull} with a square still empty: should be 0); Cutlery used on an Orange Cat: {s.CutleryOnOrange}");
        o.AppendLine($"Orange Cats that stayed put with a square empty: {s.Stayed} on the board, {s.PlacedStayed} just placed (both should be 0)");
        o.AppendLine("cats and empty squares on the board at a move (medians over the rounds still going):");
        for (var c = 0; c < Checkpoints.Length; c++)
        {
            var at = all.Select(x => x.AtCheck[c]).Where(v => v.Empty >= 0).ToList();
            if (at.Count == 0)
            {
                continue;
            }

            static int Median(IEnumerable<int> v) => v.OrderBy(x => x).ElementAt(v.Count() / 2);
            o.AppendLine($"  move {Checkpoints[c],3}: white {Median(at.Select(v => v.White))}, orange {Median(at.Select(v => v.Orange))}, empty {Median(at.Select(v => v.Empty))} ({at.Count} rounds)");
        }
        foreach (var x in all.Where(x => x.Story.Length > 0))
        {
            o.AppendLine();
            o.Append(x.Story);
        }

        File.WriteAllText(output, o.ToString());
        Console.WriteLine(o.ToString());
    }

    private static int[] Oranges(TeaBoard b) => [.. Enumerable.Range(1, TeaBoard.Squares - 1).Where(i => b[i] == TeaKind.OrangeCat)];

    private static Stats Watch(TeaModel model, TeaSolverOptions options, int seed, bool tell)
    {
        var s = new Stats();
        var rng = new Random(seed);
        var board = TeaSelfPlay.StartBoard(rng);
        var current = TeaSelfPlay.Draw(model, rng);
        var turns = 0;
        double score = 0;
        if (tell)
        {
            s.Story.AppendLine($"=== round seed {seed} ===");
        }

        while (turns < 3000 && board.EmptyCount > 0)
        {
            var advice = TeaSolver.Advise(board, current, model, options);
            if (advice.Count == 0)
            {
                break;
            }

            var a = advice[0];
            if (a.Action == TeaAction.Store)
            {
                board.TrayPiece = current;
                current = TeaSelfPlay.Draw(model, rng);
                continue;
            }

            var before = board.Clone();
            var orangesBefore = Oranges(before);
            if (a.Piece == TeaKind.OrangeCat)
            {
                var places = advice.Where(x => x.Piece == TeaKind.OrangeCat && x.Action != TeaAction.Store).OrderByDescending(x => x.Value).ToList();
                var boxed = TeaBoard.Neighbours[a.Square].All(nb => before[nb] != TeaKind.Empty);
                var empties = Enumerable.Range(1, TeaBoard.Squares - 1).Where(i => before[i] == TeaKind.Empty).ToList();
                s.Placements++;
                s.BoxedChosen += boxed ? 1 : 0;
                s.BoxedShareOfChoices += empties.Count(i => TeaBoard.Neighbours[i].All(nb => before[nb] != TeaKind.Empty)) / (double)Math.Max(1, empties.Count);
                if (places.Count > 1)
                {
                    s.SpreadSum += places[0].Value - places[^1].Value;
                    s.GapSum += places[0].Value - places[1].Value;
                }

                if (tell)
                {
                    s.Story.AppendLine($"move {turns}, score {score:F0}: Orange Cat ({a.Action}) to {TeaBoard.Where(a.Square)}{(boxed ? " BOXED IN" : string.Empty)}, {empties.Count} empty squares; options best..worst {places[0].Value:F0}..{places[^1].Value:F0} (next best {(places.Count > 1 ? places[1].Value.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) : "-")} at {(places.Count > 1 ? TeaBoard.Where(places[1].Square) : "-")})");
                    s.Story.AppendLine("   before " + before.ToJournal());
                }
            }

            if (a.Piece == TeaKind.Cutlery && before[a.Square] == TeaKind.OrangeCat)
            {
                s.CutleryOnOrange++;
                if (tell)
                {
                    s.Story.AppendLine($"move {turns}: Cutlery on the Orange Cat at {TeaBoard.Where(a.Square)} (it becomes a tea)");
                }
            }

            if (a.Action == TeaAction.Swap)
            {
                board.TrayPiece = current;
            }

            // what the placement alone leaves (before the cats walk): to tell a trap from a walk
            var placedOnly = before.Clone();
            if (a.Action == TeaAction.Swap)
            {
                placedOnly.TrayPiece = current;
            }

            TeaRules.Place(placedOnly, a.Piece, a.Square, model);
            var fullAfterPlacing = placedOnly.EmptyCount == 0;

            var played = TeaRules.Play(board, a.Piece, a.Square, model, new Random(unchecked((seed * 1000003) + turns)));
            score += played.Points;
            var orangesAfter = Oranges(board);
            var expected = orangesBefore.Length + (a.Piece == TeaKind.OrangeCat ? 1 : 0) - (a.Piece == TeaKind.Cutlery && before[a.Square] == TeaKind.OrangeCat ? 1 : 0);
            if (orangesAfter.Length < expected)
            {
                var lost = expected - orangesAfter.Length;
                s.Trapped += lost;
                s.TrappedFull += fullAfterPlacing ? lost : 0;
                if (tell || !fullAfterPlacing)
                {
                    s.Story.AppendLine($"move {turns}: {lost} Orange Cat(s) became a tea{(fullAfterPlacing ? " (board full: no square to jump to)" : " WITH A SQUARE EMPTY (rule broken)")}; placed {a.Piece} at {TeaBoard.Where(a.Square)}, +{played.Points:F0}");
                    s.Story.AppendLine("   before " + before.ToJournal() + "   after " + board.ToJournal());
                }
            }

            if (board.EmptyCount > 0)
            {
                foreach (var c in orangesBefore)
                {
                    if (board[c] == TeaKind.OrangeCat && placedOnly[c] == TeaKind.OrangeCat && Oranges(placedOnly).Length == orangesAfter.Length)
                    {
                        s.Stayed++;
                        if (tell)
                        {
                            s.Story.AppendLine($"move {turns}: the Orange Cat at {TeaBoard.Where(c)} seems to have stayed put (or another jumped in)");
                        }
                    }
                }

                if (a.Piece == TeaKind.OrangeCat && board[a.Square] == TeaKind.OrangeCat && orangesBefore.Length == 0)
                {
                    s.PlacedStayed++;
                }
            }

            if (tell && a.Piece == TeaKind.OrangeCat)
            {
                var jumpedTo = orangesAfter.Except(orangesBefore).Where(x => x != a.Square).ToList();
                s.Story.AppendLine($"   after  {board.ToJournal()}  (the placed cat {(board[a.Square] == TeaKind.OrangeCat ? "is still there" : "jumped" + (jumpedTo.Count > 0 ? " to " + string.Join("/", jumpedTo.Select(TeaBoard.Where)) : string.Empty))})");
            }

            turns++;
            var check = Array.IndexOf(Checkpoints, turns);
            if (check >= 0)
            {
                s.AtCheck[check] = (Enumerable.Range(1, TeaBoard.Squares - 1).Count(i => board[i] == TeaKind.WhiteCat), Oranges(board).Length, board.EmptyCount);
            }

            current = TeaSelfPlay.Draw(model, rng);
        }

        s.Moves = turns;
        if (tell)
        {
            s.Story.AppendLine($"round over: score {score:F0} after {turns} moves");
        }

        return s;
    }
}
