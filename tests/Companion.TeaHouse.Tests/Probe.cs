using System.Diagnostics;
using Companion.TeaHouse;

namespace Companion.TeaHouse.Tests;

/// <summary>Prints the advice for a board (run with --filter TestCategory=probe; it asserts nothing).</summary>
[TestClass]
public sealed class Probe
{
    [TestMethod]
    [TestCategory("probe")]
    public void PrintAdvice()
    {
        var board = Environment.GetEnvironmentVariable("TEA_BOARD") ?? "vww.../.2ww.w/2tt.24/w.24../w.332t/2.1t1t";
        var current = TeaKinds.TryParse((Environment.GetEnvironmentVariable("TEA_CURRENT") ?? "w")[0], out var k) ? k : TeaKind.WhiteCat;
        var depth = int.TryParse(Environment.GetEnvironmentVariable("TEA_DEPTH"), out var d) ? d : 2;
        var deepOptions = int.TryParse(Environment.GetEnvironmentVariable("TEA_DEEP"), out var n) ? n : 8;
        var sw = Stopwatch.StartNew();
        TeaNet? net = null;
        var netPath = Environment.GetEnvironmentVariable("TEA_NET");
        if (!string.IsNullOrEmpty(netPath) && File.Exists(netPath))
        {
            using var f = File.OpenRead(netPath);
            net = TeaNet.Load(f);
        }

        var model = Environment.GetEnvironmentVariable("TEA_EXPLORE") == "1" ? TeaModel.Default().ForExploring() : TeaModel.Default();
        var advice = TeaSolver.Advise(TeaBoard.Parse(board), current, model, new TeaSolverOptions { Depth = depth, DeepOptions = deepOptions, Net = net, Threads = int.TryParse(Environment.GetEnvironmentVariable("TEA_THREADS"), out var th) ? th : 1 });
        Console.WriteLine($"net {(net is null ? "none" : net.Trained.ToString())}, explore {Environment.GetEnvironmentVariable("TEA_EXPLORE") == "1"}");
        Console.WriteLine($"{sw.ElapsedMilliseconds} ms, {advice.Count} options");
        foreach (var a in advice.Take(int.TryParse(Environment.GetEnvironmentVariable("TEA_TOP"), out var top) ? top : 8))
        {
            Console.WriteLine($"{a.Action} {a.Piece} at {(a.Square < 0 ? "-" : TeaBoard.Where(a.Square))}: value {a.Value:F0}, now {a.Points:F0} ({a.Joins} joins, top {a.Top}), depth={a.Depth}");
        }
    }

    [TestMethod]
    [TestCategory("probe")]
    public void SelfPlayScores()
    {
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("TEA_ROUNDS"), out var r) ? r : 5;
        var sw = Stopwatch.StartNew();
        var opts = new TeaSolverOptions();
        for (var seed = 1; seed <= rounds; seed++)
        {
            var round = TeaSelfPlay.Play(TeaModel.Default(), opts, seed);
            Console.WriteLine($"seed {seed}: score {round.Score:F0}, {round.Turns} turns, top {round.Top}");
        }

        Console.WriteLine($"{sw.ElapsedMilliseconds} ms");
    }
}

/// <summary>Prints the fingerprints of the test captures (for the seed).</summary>
[TestClass]
public sealed class PrintProbe
{
    [TestMethod]
    [TestCategory("probe")]
    public void PrintFingerprints()
    {
        var frames = Captures.All();
        Console.WriteLine("title " + Companion.Contracts.Vision.IconPrint.Compute(frames[0], TeaGeometry.Title));
        Console.WriteLine("panel " + string.Join(" ", frames.Select(f => Companion.Contracts.Vision.IconPrint.Compute(f, TeaGeometry.CurrentIcon))));
        var board = TeaBoard.Parse(Captures.Board);
        for (var i = 0; i < TeaBoard.Squares; i++)
        {
            var rect = TeaGeometry.Interior(i);
            var spread = TeaReader.Spread(frames[0], rect);
            var change = TeaReader.Change(frames, rect);
            Console.WriteLine($"sq {i} {TeaKinds.Code(board[i])} spread {spread:F1} change {change:F3} " + string.Join(" ", frames.Select(f => Environment.GetEnvironmentVariable("TEA_PLAIN") == "1" ? Companion.Contracts.Vision.IconPrint.Compute(f, rect) : Companion.Contracts.Vision.IconPrint.ComputeWithHue(f, rect))));
        }
    }
}

/// <summary>Reads a capture given by TEA_FRAME (a full-scale client PNG) and prints what the reader sees.</summary>
[TestClass]
public sealed class FrameProbe
{
    [TestMethod]
    [TestCategory("probe")]
    public void ReadFrame()
    {
        var path = Environment.GetEnvironmentVariable("TEA_FRAME");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var frames = path.Split(';').Select(p => Companion.Vision.Internal.SourceImages.ToFrame(File.ReadAllBytes(p), Companion.Vision.Internal.SourceLayout.ClientFull)).ToArray();
        var labelsPath = Environment.GetEnvironmentVariable("TEA_LABELS");
        var labels = !string.IsNullOrEmpty(labelsPath) && File.Exists(labelsPath) ? TeaLabels.FromJson(File.ReadAllText(labelsPath)) : TeaLabels.Seeded();
        var r = TeaReader.Read(frames, labels);
        Console.WriteLine($"onScreen {r.OnScreen} title {r.TitleDistance} preview {r.Preview} current {r.Current} ({r.CurrentDistance}) board {r.Board} unknown {r.UnknownCount}");
        Console.WriteLine("title print " + Companion.Contracts.Vision.IconPrint.Compute(frames[0], TeaGeometry.Title));
        foreach (var s in r.Squares.Where(s => !s.Empty))
        {
            Console.WriteLine($"sq {s.Square} {s.Kind} d{s.Distance} change {s.Change:F2} {s.Print}");
        }
    }
}

/// <summary>Tunes the board weights by self-play (coordinate search, common seeds). Run with --filter TestCategory=tune.</summary>
[TestClass]
public sealed class TuneProbe
{
    [TestMethod]
    [TestCategory("tune")]
    public void TuneWeights()
    {
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("TEA_ROUNDS"), out var r) ? r : 40;
        var model = TeaModel.Default();
        var w = new TeaWeights();
        double Eval(TeaWeights x) => TeaSelfPlay.Average(model, new TeaSolverOptions { DeepOptions = 0, CatSamples = 2, Weights = x }, rounds);
        var best = Eval(w);
        Console.WriteLine($"baseline {best:F0}");
        var knobs = new (string Name, Func<TeaWeights, double> Get, Func<TeaWeights, double, TeaWeights> Set)[]
        {
            ("Empty", x => x.Empty, (x, v) => x with { Empty = v }),
            ("Crowded", x => x.Crowded, (x, v) => x with { Crowded = v }),
            ("Pair", x => x.Pair, (x, v) => x with { Pair = v }),
            ("DeadPair", x => x.DeadPair, (x, v) => x with { DeadPair = v }),
            ("Single", x => x.Single, (x, v) => x with { Single = v }),
            ("Chain", x => x.Chain, (x, v) => x with { Chain = v }),
            ("Central", x => x.Central, (x, v) => x with { Central = v }),
            ("BlockerCentral", x => x.BlockerCentral, (x, v) => x with { BlockerCentral = v }),
            ("Cat", x => x.Cat, (x, v) => x with { Cat = v }),
            ("CatNearlyBoxed", x => x.CatNearlyBoxed, (x, v) => x with { CatNearlyBoxed = v }),
            ("OrangeCat", x => x.OrangeCat, (x, v) => x with { OrangeCat = v }),
            ("BlackTea", x => x.BlackTea, (x, v) => x with { BlackTea = v }),
            ("TrayJoker", x => x.TrayJoker, (x, v) => x with { TrayJoker = v }),
        };
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var (name, get, set) in knobs)
            {
                foreach (var f in new[] { 0.5, 2.0 })
                {
                    var trial = set(w, get(w) * f);
                    var score = Eval(trial);
                    Console.WriteLine($"pass {pass} {name} x{f}: {score:F0} (best {best:F0})");
                    if (score > best * 1.01)
                    {
                        best = score;
                        w = trial;
                        Console.WriteLine($"  kept {name} = {get(w)}");
                    }
                }
            }
        }

        Console.WriteLine("best " + best.ToString("F0") + " " + w);
    }
}

/// <summary>Compares search settings by self-play (TEA_ROUNDS rounds each). Run with --filter TestCategory=tune.</summary>
[TestClass]
public sealed class SearchProbe
{
    [TestMethod]
    [TestCategory("tune")]
    public void CompareSearch()
    {
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("TEA_ROUNDS"), out var r) ? r : 10;
        var model = TeaModel.Default();
        foreach (var (deep, cats) in new[] { (8, 4), (16, 4), (16, 8), (35, 4) })
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var opts = new TeaSolverOptions { DeepOptions = deep, CatSamples = cats };
            var scores = Enumerable.Range(1, rounds).Select(s => TeaSelfPlay.Play(model, opts, s)).ToList();
            var moves = scores.Sum(x => x.Turns);
            Console.WriteLine($"deep {deep} cats {cats}: avg {scores.Average(x => x.Score):F0}, min {scores.Min(x => x.Score):F0}, turns {scores.Average(x => x.Turns):F0}, {sw.ElapsedMilliseconds / Math.Max(1, moves):F1} ms/move");
        }
    }
}

/// <summary>Compares the hand-made weights and a learned network (TEA_NET) by self-play on the same seeds. Category tune.</summary>
[TestClass]
public sealed class NetProbe
{
    [TestMethod]
    [TestCategory("tune")]
    public void CompareNet()
    {
        var netPath = Environment.GetEnvironmentVariable("TEA_NET");
        if (string.IsNullOrEmpty(netPath) || !File.Exists(netPath))
        {
            return;
        }

        TeaNet net;
        using (var f = File.OpenRead(netPath))
        {
            net = TeaNet.Load(f)!;
        }

        var rounds = int.TryParse(Environment.GetEnvironmentVariable("TEA_ROUNDS"), out var r) ? r : 30;
        var model = TeaModel.Default();
        var setups = new (string Name, TeaSolverOptions Options)[]
        {
            ("hand, 1 piece", new TeaSolverOptions { DeepOptions = 0 }),
            ("hand, 2 pieces", new TeaSolverOptions()),
            ("net, 1 piece", new TeaSolverOptions { DeepOptions = 0, Net = net }),
            ("net, 2 pieces", new TeaSolverOptions { Net = net }),
        };
        foreach (var (name, options) in setups)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var scores = Enumerable.Range(1000, rounds).Select(s => TeaSelfPlay.Play(model, options, s)).ToList();
            var sorted = scores.Select(x => x.Score).OrderBy(x => x).ToList();
            Console.WriteLine($"{name}: avg {scores.Average(x => x.Score):F0}, median {sorted[sorted.Count / 2]:F0}, turns {scores.Average(x => x.Turns):F0}, {sw.Elapsed.TotalSeconds:F0} s");
        }
    }
}

/// <summary>Prints how the reader saw the x captures (the Cutlery-on-the-preview board). Category probe.</summary>
[TestClass]
public sealed class XProbe
{
    [TestMethod]
    [TestCategory("probe")]
    public void ReadX()
    {
        var frames = Enumerable.Range(0, 4).Select(i => Captures.Get($"teahouse_x{i}_full.png")).ToArray();
        var labels = TeaLabels.Seeded();
        var r = TeaReader.Read(frames, labels);
        Console.WriteLine($"preview {r.Preview} current {r.Current} board {r.Board}");
        foreach (var s in r.Squares.Where(s => !s.Empty))
        {
            var toCookie = s.Prints.Count == 0 ? -1 : s.Prints.Min(p => labels.Entries.Where(e => e.Kind == TeaKind.Cookie && e.Source == TeaPictureSource.Board).Min(e => TeaLabels.Distance(p, e.Print)));
            Console.WriteLine($"sq {s.Square} {s.Kind} d{s.Distance} byColour {s.ByColour} onTile {s.OnTile} change {s.Change:F2} ring {s.Ring:F2} score {s.PreviewScore:F2} toCookie {toCookie}");
        }
    }
}

/// <summary>
/// A/B of search rules by paired self-play (same seeds, same piece and cat streams): TEA_ROUNDS rounds per arm, the learned
/// network from TEA_NET, judged by the share of rounds reaching 200k and the top 10 %. Category tune.
/// </summary>
[TestClass]
public sealed class RuleABProbe
{
    [TestMethod]
    [TestCategory("tune")]
    public void CompareRules()
    {
        var netPath = Environment.GetEnvironmentVariable("TEA_NET");
        TeaNet? net = null;
        if (!string.IsNullOrEmpty(netPath) && File.Exists(netPath))
        {
            using var f = File.OpenRead(netPath);
            net = TeaNet.Load(f);
        }

        var rounds = int.TryParse(Environment.GetEnvironmentVariable("TEA_ROUNDS"), out var r) ? r : 200;
        var depth = int.TryParse(Environment.GetEnvironmentVariable("TEA_DEPTH"), out var d) ? d : 2;
        var model = TeaModel.Default();
        var which = Environment.GetEnvironmentVariable("TEA_AB") ?? "rules12";
        TeaNet? net2 = null;
        var net2Path = Environment.GetEnvironmentVariable("TEA_NET2");
        if (!string.IsNullOrEmpty(net2Path) && File.Exists(net2Path))
        {
            using var f2 = File.OpenRead(net2Path);
            net2 = TeaNet.Load(f2);
        }

        var arms = which == "nets"
            ? new (string Name, TeaSolverOptions Options)[]
            {
                ($"current brain ({net?.Trained:N0} rounds)", new TeaSolverOptions { Net = net, Depth = depth }),
                ($"new brain ({net2?.Trained:N0} rounds)", new TeaSolverOptions { Net = net2, Depth = depth }),
            }
            : which == "rules810"
            ? new (string Name, TeaSolverOptions Options)[]
            {
                ("without rules 8 and 10", new TeaSolverOptions { Net = net, Depth = depth }),
                ("with rule 8 (exact endgame)", new TeaSolverOptions { Net = net, Depth = depth, ExactEndgame = true }),
                ("with rules 8 and 10", new TeaSolverOptions { Net = net, Depth = depth, ExactEndgame = true, NoWastedOven = true }),
            }
            : which == "referee"
            ? new (string Name, TeaSolverOptions Options)[]
            {
                ("no tie-breaks", new TeaSolverOptions { Net = net, Depth = depth }),
                ("tie-breaks, Orange Cat free", new TeaSolverOptions { Net = net, Depth = depth, Referee = true, KeepPrisoner = false }),
                ("tie-breaks + keep the Orange Cat", new TeaSolverOptions { Net = net, Depth = depth, Referee = true, KeepPrisoner = true }),
            }
            : new (string Name, TeaSolverOptions Options)[]
            {
                ("without rules 1-2", new TeaSolverOptions { Net = net, Depth = depth, TrayReplies = false, DiverseDeepSet = false }),
                ("with rules 1-2", new TeaSolverOptions { Net = net, Depth = depth, TrayReplies = true, DiverseDeepSet = true }),
            };
        var results = new double[arms.Length][];
        var tops = new TeaKind[arms.Length][];
        for (var a = 0; a < arms.Length; a++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var scores = new double[rounds];
            var top = new TeaKind[rounds];
            var options = arms[a].Options;
            Parallel.For(0, rounds, new ParallelOptions { MaxDegreeOfParallelism = 12 }, i =>
            {
                var round = TeaSelfPlay.Play(model, options, 10000 + i);
                scores[i] = round.Score;
                top[i] = round.Top;
            });
            results[a] = scores;
            tops[a] = top;
            var sorted = scores.OrderBy(x => x).ToArray();
            Console.WriteLine($"{arms[a].Name}: 200k+ {scores.Count(x => x >= 200000) * 100.0 / rounds:F1}%, top10% {sorted[(int)(0.9 * rounds)]:F0}, median {sorted[rounds / 2]:F0}, mean {scores.Average():F0}, made Shortcake+ {top.Count(t => t is >= TeaKind.Shortcake and <= TeaKind.Luxury) * 100.0 / rounds:F1}%, {sw.Elapsed.TotalSeconds:F0} s");
        }

        for (var b = 1; b < arms.Length; b++)
        {
            for (var a = 0; a < b; a++)
            {
                var wins = Enumerable.Range(0, rounds).Count(i => results[b][i] > results[a][i]);
                var losses = Enumerable.Range(0, rounds).Count(i => results[b][i] < results[a][i]);
                var hitOnlyB = Enumerable.Range(0, rounds).Count(i => results[b][i] >= 200000 && results[a][i] < 200000);
                var hitOnlyA = Enumerable.Range(0, rounds).Count(i => results[a][i] >= 200000 && results[b][i] < 200000);
                Console.WriteLine($"paired '{arms[b].Name}' vs '{arms[a].Name}': better in {wins}, worse in {losses}; 200k only with the first {hitOnlyB}, only with the second {hitOnlyA}");
            }
        }
    }
}
