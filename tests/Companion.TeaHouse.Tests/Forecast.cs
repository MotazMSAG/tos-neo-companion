using System.Globalization;
using System.Text;
using Companion.TeaHouse;

namespace Companion.TeaHouse.Tests;

/// <summary>
/// Can a round's fate be told early? RecordRounds plays TEA_ROUNDS self-play rounds with the helper's search and the brain
/// (TEA_NET, depth TEA_DEPTH) and writes every turn's forecast (score so far + the chosen option's value) to TEA_OUT;
/// AnalyseRounds reads that file and reports, per checkpoint turn, how well the forecast separates rounds that end at the
/// goal from those that do not, and which "restart below X at turn N" rule makes the most goal rounds per hour of play.
/// Thresholds are chosen on the odd seeds and checked on the even ones. Category tune.
/// </summary>
[TestClass]
public sealed class ForecastProbe
{
    private static readonly int[] Checkpoints = [10, 20, 30, 40, 50, 60, 80, 100, 125, 150, 200, 250, 300, 400, 500, 600, 800];

    private sealed record Rec(int Seed, double Final, int Turns, TeaKind Top, double[] Score, double[] Forecast, int[] Empties);

    [TestMethod]
    [TestCategory("tune")]
    public void RecordRounds()
    {
        var netPath = Environment.GetEnvironmentVariable("TEA_NET") ?? @"C:\ProgramData\TOS-companion\teahouse\net.bin";
        using var f = File.OpenRead(netPath);
        var net = TeaNet.Load(f);
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("TEA_ROUNDS"), out var r) ? r : 1000;
        var depth = int.TryParse(Environment.GetEnvironmentVariable("TEA_DEPTH"), out var d) ? d : 2;
        var first = int.TryParse(Environment.GetEnvironmentVariable("TEA_FIRST"), out var s) ? s : 30000;
        var output = Environment.GetEnvironmentVariable("TEA_OUT") ?? Path.Combine(Path.GetTempPath(), "tea_forecast.csv");
        var model = TeaModel.Default();
        var options = new TeaSolverOptions { Net = net, Depth = depth };
        var lines = new string[rounds];
        var done = 0;
        Parallel.For(0, rounds, new ParallelOptions { MaxDegreeOfParallelism = 12 }, i =>
        {
            var seed = first + i;
            var turns = new SortedDictionary<int, (double Score, double Forecast, int Empties)>();
            var round = TeaSelfPlay.Play(model, options, seed, maxTurns: 3000, watch: t => turns[t.Number] = (t.Score, t.Score + t.Best.Value, t.Board.EmptyCount));
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"{seed},{round.Score:F0},{round.Turns},{round.Top}");
            foreach (var (n, t) in turns)
            {
                sb.Append(CultureInfo.InvariantCulture, $",{n}:{t.Score:F0}:{t.Forecast:F0}:{t.Empties}");
            }

            lines[i] = sb.ToString();
            var k = Interlocked.Increment(ref done);
            if (k % 50 == 0)
            {
                Console.WriteLine($"{k} rounds");
            }
        });
        File.WriteAllLines(output, lines);
    }

    /// <summary>
    /// The helper's think time per move with each brain (TEA_NET, TEA_NET2) on real boards from the journal: depth 2, 3, 4
    /// over 8 options (each look ranking the next, as TeaHouseHelper.ThinkDeeper), then depth 5 over 2 on joker boards.
    /// </summary>
    [TestMethod]
    [TestCategory("tune")]
    public void TimeBrains()
    {
        var boards = new List<(TeaBoard Board, TeaKind Current)>();
        foreach (var line in File.ReadLines(@"C:\ProgramData\TOS-companion\journal\2026-10-29.jsonl").Where(l => l.Contains("\"teahouse.advice\"", StringComparison.Ordinal)))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var f = doc.RootElement.GetProperty("fields");
            var b = f.GetProperty("board").GetString()!;
            if (!b.Contains('u', StringComparison.Ordinal) && !b.Contains('<', StringComparison.Ordinal) && b.Count(c => c == '/') == 5
                && Enum.TryParse<TeaKind>(f.GetProperty("current").GetString(), out var k) && k != TeaKind.Unknown)
            {
                boards.Add((TeaBoard.Parse(b), k));
            }
        }

        boards = [.. boards.Where((_, i) => i % Math.Max(1, boards.Count / 30) == 0).Take(30)];
        var model = TeaModel.Default();
        foreach (var name in new[] { "TEA_NET", "TEA_NET2" })
        {
            using var file = File.OpenRead(Environment.GetEnvironmentVariable(name)!);
            var net = TeaNet.Load(file)!;
            var times = new List<double>();
            var jokers = new List<double>();
            foreach (var (board, current) in boards)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                IReadOnlyList<TeaAdvice> found = [];
                for (var depth = 2; depth <= 4; depth++)
                {
                    found = TeaSolver.Advise(board, current, model, new TeaSolverOptions { Net = net, Depth = depth, DeepOptions = 8, Threads = Math.Max(1, Environment.ProcessorCount / 2), Prior = found.Count > 0 ? found : null });
                }

                times.Add(sw.Elapsed.TotalSeconds);
                if (TeaSolver.IsJoker(current) || TeaSolver.IsJoker(board.TrayPiece))
                {
                    sw.Restart();
                    TeaSolver.Advise(board, current, model, new TeaSolverOptions { Net = net, Depth = 5, DeepOptions = 2, Threads = Math.Max(1, Environment.ProcessorCount / 2), Prior = found });
                    jokers.Add(sw.Elapsed.TotalSeconds);
                }
            }

            Console.WriteLine($"{name} ({net.Trained:N0} rounds, features {net.UsesFeatures}): {boards.Count} boards, look 4 pieces: mean {times.Average():F2} s, max {times.Max():F2} s; joker look 5: {jokers.Count} boards, mean {(jokers.Count > 0 ? jokers.Average() : 0):F1} s, max {(jokers.Count > 0 ? jokers.Max() : 0):F1} s");
        }
    }

    /// <summary>
    /// Builds the helper's forecast samples (TEA_EXPORT) from recorded rounds (TEA_OUT): one sample every 10 moves (score so
    /// far, best move's value, final score). Its quality is measured on the even seeds with samples from the odd ones,
    /// by score band: how well the outlook's 160k share separates the rounds (0.50 = coin toss) and whether it is
    /// calibrated. The early score (too early to tell below it) is the start of the first band from which every band
    /// separates at 0.70 or better.
    /// </summary>
    [TestMethod]
    [TestCategory("tune")]
    public void ExportForecast()
    {
        var input = Environment.GetEnvironmentVariable("TEA_OUT") ?? Path.Combine(Path.GetTempPath(), "tea_forecast.csv");
        var export = Environment.GetEnvironmentVariable("TEA_EXPORT") ?? Path.ChangeExtension(input, ".samples.csv");
        var note = Environment.GetEnvironmentVariable("TEA_NOTE") ?? "self-play";
        var recs = File.ReadAllLines(input).Where(l => l.Length > 0).Select(Parse).ToList();
        IEnumerable<(double, double, double)> Samples(IEnumerable<Rec> rs) =>
            rs.SelectMany(x => Enumerable.Range(1, x.Turns - 1).Where(t => t % 10 == 0).Select(t => (x.Score[t], x.Forecast[t] - x.Score[t], x.Final)));

        var reference = TeaForecast.Parse(TeaForecast.Write(Samples(recs.Where(x => x.Seed % 2 == 1)), 0, "odd seeds"))!;
        var o = new StringBuilder();
        o.AppendLine(CultureInfo.InvariantCulture, $"{recs.Count} rounds; reference {reference.Count} samples from the odd seeds, checked on the even seeds");
        o.AppendLine(" score band   rounds  reached 160k  outlook said  separation (0.50 = coin toss)");
        var early = double.NaN;
        var bands = new List<(double From, double Auc)>();
        for (var from = 0.0; from < 260_000; from += 20_000)
        {
            var points = new List<(double P, bool Hit)>();
            foreach (var x in recs.Where(x => x.Seed % 2 == 0))
            {
                var t = Enumerable.Range(1, x.Turns - 1).FirstOrDefault(t => x.Score[t] >= from && x.Score[t] < from + 20_000);
                if (t > 0)
                {
                    points.Add((reference.Predict(x.Score[t], x.Forecast[t] - x.Score[t]).Share160, x.Final >= 160_000));
                }
            }

            if (points.Count < 30)
            {
                continue;
            }

            var auc = Auc([.. points.Select(p => p.P)], [.. points.Select(p => p.Hit)]);
            bands.Add((from, auc));
            o.AppendLine(CultureInfo.InvariantCulture, $" {from / 1000,3:F0}-{(from + 20_000) / 1000,3:F0}k  {points.Count,7}  {points.Count(p => p.Hit) * 100.0 / points.Count,10:F0}%  {points.Average(p => p.P) * 100,10:F0}%  {auc,10:F2}");
        }

        for (var i = 0; i < bands.Count && double.IsNaN(early); i++)
        {
            if (bands.Skip(i).All(b => double.IsNaN(b.Auc) || b.Auc >= 0.70))
            {
                early = bands[i].From;
            }
        }

        early = double.IsNaN(early) ? 200_000 : early;
        o.AppendLine(CultureInfo.InvariantCulture, $"too early to tell below {early:F0}");
        File.WriteAllText(export, TeaForecast.Write(Samples(recs), early, note));
        File.WriteAllText(Path.ChangeExtension(export, ".report.txt"), o.ToString());
        Console.WriteLine(o.ToString());
    }

    [TestMethod]
    [TestCategory("tune")]
    public void AnalyseRounds()
    {
        var input = Environment.GetEnvironmentVariable("TEA_OUT") ?? Path.Combine(Path.GetTempPath(), "tea_forecast.csv");
        var report = Environment.GetEnvironmentVariable("TEA_REPORT") ?? Path.ChangeExtension(input, ".report.txt");
        var restart = int.TryParse(Environment.GetEnvironmentVariable("TEA_RESTART_TURNS"), out var rt) ? rt : 10;
        var recs = File.ReadAllLines(input).Where(l => l.Length > 0).Select(Parse).ToList();
        var o = new StringBuilder();
        var finals = recs.Select(x => x.Final).OrderBy(x => x).ToArray();
        var lengths = recs.Select(x => (double)x.Turns).OrderBy(x => x).ToArray();
        o.AppendLine(CultureInfo.InvariantCulture, $"{recs.Count} rounds. Final score: median {finals[finals.Length / 2]:F0}, top 10% {finals[(int)(0.9 * finals.Length)]:F0}; 160k+ {Share(recs, x => x.Final >= 160000):F1}%, 200k+ {Share(recs, x => x.Final >= 200000):F1}%");
        o.AppendLine(CultureInfo.InvariantCulture, $"Round length (pieces placed): 10% {lengths[(int)(0.1 * lengths.Length)]:F0}, median {lengths[lengths.Length / 2]:F0}, 90% {lengths[(int)(0.9 * lengths.Length)]:F0}");
        var shortOnes = recs.Where(x => x.Final < 160000).Select(x => (double)x.Turns).OrderBy(x => x).ToArray();
        var longOnes = recs.Where(x => x.Final >= 160000).Select(x => (double)x.Turns).OrderBy(x => x).ToArray();
        if (shortOnes.Length > 0 && longOnes.Length > 0)
        {
            o.AppendLine(CultureInfo.InvariantCulture, $"Median length of rounds under 160k: {shortOnes[shortOnes.Length / 2]:F0} pieces; of rounds at 160k+: {longOnes[longOnes.Length / 2]:F0}");
        }

        foreach (var goal in new[] { 160000.0, 200000.0 })
        {
            o.AppendLine();
            o.AppendLine(CultureInfo.InvariantCulture, $"=== Goal {goal / 1000:F0}k ===");
            o.AppendLine("How well each sign tells, at a turn, which of the rounds still going will reach the goal (1.00 = perfectly, 0.50 = a coin toss):");
            o.AppendLine(" turn  going  reach%   forecast  score  outlook  empty squares   (outlook = forecast - score)");
            foreach (var c in Checkpoints)
            {
                var alive = recs.Where(x => x.Turns > c).ToList();
                if (alive.Count < 40)
                {
                    continue;
                }

                var hit = alive.Select(x => x.Final >= goal).ToArray();
                o.AppendLine(CultureInfo.InvariantCulture, $"{c,5} {alive.Count,6} {Share(alive, x => x.Final >= goal),6:F1}   {Auc(alive.Select(x => x.Forecast[c]).ToArray(), hit),8:F2} {Auc(alive.Select(x => x.Score[c]).ToArray(), hit),6:F2} {Auc(alive.Select(x => x.Forecast[c] - x.Score[c]).ToArray(), hit),8:F2} {Auc(alive.Select(x => (double)x.Empties[c]).ToArray(), hit),8:F2}");
            }

            o.AppendLine();
            o.AppendLine("The rounds still going at a turn, split into five equal groups by forecast (lowest first): forecast range and share that reached the goal:");
            foreach (var c in Checkpoints)
            {
                var alive = recs.Where(x => x.Turns > c).OrderBy(x => x.Forecast[c]).ToList();
                if (alive.Count < 40)
                {
                    continue;
                }

                o.Append(CultureInfo.InvariantCulture, $"{c,5} ");
                for (var q = 0; q < 5; q++)
                {
                    var group = alive.Skip(q * alive.Count / 5).Take(((q + 1) * alive.Count / 5) - (q * alive.Count / 5)).ToList();
                    o.Append(CultureInfo.InvariantCulture, $"| {group[0].Forecast[c] / 1000,3:F0}-{group[^1].Forecast[c] / 1000,3:F0}k {Share(group, x => x.Final >= goal),3:F0}% ");
                }

                o.AppendLine();
            }

            // restart rules, judged by goal rounds per 1000 pieces placed (each restart costs TEA_RESTART_TURNS pieces' time)
            var train = recs.Where(x => x.Seed % 2 == 1).ToList();
            var test = recs.Where(x => x.Seed % 2 == 0).ToList();
            o.AppendLine();
            o.AppendLine(CultureInfo.InvariantCulture, $"Restart rules (a restart costs the time of {restart} pieces). Goal rounds per 1000 pieces of play; picked on odd seeds, checked on even seeds:");
            o.AppendLine(CultureInfo.InvariantCulture, $"  never restart: {Rate(train, [], goal, restart):F2} (picked half), {Rate(test, [], goal, restart):F2} (check half)");
            var thresholds = Enumerable.Range(0, 71).Select(i => 50000.0 + (i * 5000)).ToArray();
            var singles = new List<(double Train, double Test, (int, double)[] Rule)>();
            foreach (var c in Checkpoints.Where(c => c <= 600))
            {
                var best = thresholds.Select(t => new (int, double)[] { (c, t) }).MaxBy(rule => Rate(train, rule, goal, restart))!;
                singles.Add((Rate(train, best, goal, restart), Rate(test, best, goal, restart), best));
                o.AppendLine(CultureInfo.InvariantCulture, $"  at turn {c,3}, restart if the forecast is under {best[0].Item2 / 1000,3:F0}k: {singles[^1].Train:F2} / {singles[^1].Test:F2}   (restarts {Killed(test, best) * 100.0 / test.Count:F0}% of rounds, keeps {Kept(test, best, goal) * 100.0 / Math.Max(1, test.Count(x => x.Final >= goal)):F0}% of the goal rounds)");
            }

            // two checks, an early cheap one and a later surer one
            (double Train, (int, double)[] Rule) pair = (0, []);
            foreach (var c1 in Checkpoints.Where(c => c <= 100))
            {
                foreach (var c2 in Checkpoints.Where(c => c > c1 && c <= 600))
                {
                    foreach (var t1 in thresholds.Where((_, i) => i % 2 == 0))
                    {
                        foreach (var t2 in thresholds.Where((_, i) => i % 2 == 0))
                        {
                            var rule = new (int, double)[] { (c1, t1), (c2, t2) };
                            var v = Rate(train, rule, goal, restart);
                            if (v > pair.Train)
                            {
                                pair = (v, rule);
                            }
                        }
                    }
                }
            }

            o.AppendLine(CultureInfo.InvariantCulture, $"  two checks: turn {pair.Rule[0].Item1} under {pair.Rule[0].Item2 / 1000:F0}k, then turn {pair.Rule[1].Item1} under {pair.Rule[1].Item2 / 1000:F0}k: {pair.Train:F2} / {Rate(test, pair.Rule, goal, restart):F2}   (restarts {Killed(test, pair.Rule) * 100.0 / test.Count:F0}%, keeps {Kept(test, pair.Rule, goal) * 100.0 / Math.Max(1, test.Count(x => x.Final >= goal)):F0}% of the goal rounds)");
        }

        File.WriteAllText(report, o.ToString());
        Console.WriteLine(o.ToString());
    }

    private static Rec Parse(string line)
    {
        var parts = line.Split(',');
        var turns = int.Parse(parts[2], CultureInfo.InvariantCulture);
        var score = new double[turns + 1];
        var forecast = new double[turns + 1];
        var empties = new int[turns + 1];
        foreach (var p in parts.Skip(4))
        {
            var t = p.Split(':');
            var n = int.Parse(t[0], CultureInfo.InvariantCulture);
            if (n > turns)
            {
                continue;
            }

            score[n] = double.Parse(t[1], CultureInfo.InvariantCulture);
            forecast[n] = double.Parse(t[2], CultureInfo.InvariantCulture);
            empties[n] = int.Parse(t[3], CultureInfo.InvariantCulture);
        }

        return new Rec(int.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture), turns, Enum.Parse<TeaKind>(parts[3]), score, forecast, empties);
    }

    private static double Share(IReadOnlyCollection<Rec> recs, Func<Rec, bool> test) => recs.Count == 0 ? 0 : recs.Count(test) * 100.0 / recs.Count;

    /// <summary>The chance that a random goal round scores higher on the sign than a random other round (ties half).</summary>
    private static double Auc(double[] sign, bool[] hit)
    {
        var pos = sign.Where((_, i) => hit[i]).ToArray();
        var neg = sign.Where((_, i) => !hit[i]).OrderBy(x => x).ToArray();
        if (pos.Length == 0 || neg.Length == 0)
        {
            return double.NaN;
        }

        double sum = 0;
        foreach (var p in pos)
        {
            var below = LowerBound(neg, p);
            var notAbove = UpperBound(neg, p);
            sum += below + ((notAbove - below) / 2.0);
        }

        return sum / ((double)pos.Length * neg.Length);
    }

    private static int LowerBound(double[] a, double v)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (a[mid] < v)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private static int UpperBound(double[] a, double v)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (a[mid] <= v)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>The turn a rule restarts the round at, or -1 when it plays it out.</summary>
    private static int KillTurn(Rec x, (int Turn, double Below)[] rule)
    {
        foreach (var (turn, below) in rule)
        {
            if (x.Turns > turn && x.Forecast[turn] < below)
            {
                return turn;
            }
        }

        return -1;
    }

    private static double Rate(List<Rec> recs, (int, double)[] rule, double goal, int restart)
    {
        double pieces = 0;
        var hits = 0;
        foreach (var x in recs)
        {
            var k = KillTurn(x, rule);
            pieces += (k < 0 ? x.Turns : k) + restart;
            if (k < 0 && x.Final >= goal)
            {
                hits++;
            }
        }

        return hits * 1000.0 / pieces;
    }

    private static int Killed(List<Rec> recs, (int, double)[] rule) => recs.Count(x => KillTurn(x, rule) >= 0);

    private static int Kept(List<Rec> recs, (int, double)[] rule, double goal) => recs.Count(x => x.Final >= goal && KillTurn(x, rule) < 0);
}
