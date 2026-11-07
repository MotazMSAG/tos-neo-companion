using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Companion.TeaHouse;

namespace Companion.TeaHouse.Tests;

/// <summary>
/// Real rounds end after about 250 presses, practice rounds after about 1,100 (2026-10-30, the user: "nothing so far
/// explains the disparity"). Practice games started from the user's real boards tell where the difference lies: from a
/// real board some presses before a natural end, how many moves do practice continuations survive? Rounds are cut by the
/// log's end screens; the real boards are the helper's readings. TEA_NET brain, TEA_RUNS continuations per board (16),
/// TEA_ROUNDS the round numbers to use (1-based, comma list; default the natural deaths 3,5,7). Writes TEA_SURVIVE.
/// Category probe.
/// </summary>
[TestClass]
public sealed partial class SurviveProbe
{
    [GeneratedRegex(@"^(\S+ \S+)\s.*on_grid_click\s+(\d+)\s+(\d+)\s+(\d+)")]
    private static partial Regex Press();

    [TestMethod]
    [TestCategory("probe")]
    public void SurviveFromRealBoards()
    {
        var netPath = Environment.GetEnvironmentVariable("TEA_NET") ?? @"C:\ProgramData\TOS-companion\teahouse\net.bin";
        using var f = File.OpenRead(netPath);
        var net = TeaNet.Load(f);
        var runs = int.TryParse(Environment.GetEnvironmentVariable("TEA_RUNS"), out var r) ? r : 16;
        var wanted = (Environment.GetEnvironmentVariable("TEA_ROUNDS") ?? "3,5,7").Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToHashSet();
        var output = Environment.GetEnvironmentVariable("TEA_SURVIVE") ?? Path.Combine(Path.GetTempPath(), "tea_survive.txt");

        // the log: board presses and end screens (game time is the PC's local time)
        var presses = new List<DateTime>();
        var ends = new List<DateTime>();
        foreach (var line in LogLines())
        {
            if (line.Contains("on_grid_click", StringComparison.Ordinal) && Press().Match(line) is { Success: true } m)
            {
                if ((int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)) != (1, 1))
                {
                    presses.Add(Time(m.Groups[1].Value));
                }
            }
            else if (line.Contains("show_ui", StringComparison.Ordinal) && line.Contains("UI_TRIPLE_TOWN_SETTLE", StringComparison.Ordinal))
            {
                ends.Add(Time(line[..23]));
            }
        }

        // the helper's readings (UTC)
        var reads = new List<(DateTime At, TeaBoard Board)>();
        foreach (var file in new[] { "2026-10-29.jsonl", "2026-10-30.jsonl" })
        {
            var path = Path.Combine(@"C:\ProgramData\TOS-companion\journal", file);
            if (!File.Exists(path))
            {
                continue;
            }

            foreach (var line in File.ReadLines(path).Where(l => l.Contains("\"teahouse.advice\"", StringComparison.Ordinal)))
            {
                using var doc = JsonDocument.Parse(line);
                var b = doc.RootElement.GetProperty("fields").GetProperty("board").GetString()!;
                if (!b.Contains('u', StringComparison.Ordinal) && b.Count(c => c == '/') == 5)
                {
                    reads.Add((DateTime.Parse(doc.RootElement.GetProperty("utc").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal), TeaBoard.Parse(b)));
                }
            }
        }

        var model = TeaModel.Default();
        var options = new TeaSolverOptions { Net = net, Depth = 2 };
        var o = new StringBuilder();
        o.AppendLine("From a real board some presses before the real end: how many moves practice continuations survive (median, 10%-90%), and how many died within the real remaining presses");
        var bounds = new List<DateTime> { DateTime.MinValue };
        bounds.AddRange(ends);
        for (var round = 1; round <= ends.Count; round++)
        {
            if (!wanted.Contains(round))
            {
                continue;
            }

            var inRound = presses.Where(t => t > bounds[round - 1] && t < bounds[round]).ToList();
            o.AppendLine(CultureInfo.InvariantCulture, $"round {round}: {inRound.Count} board presses");
            foreach (var before in new[] { 150, 100, 50, 25 })
            {
                var k = inRound.Count - before;
                if (k < 1)
                {
                    continue;
                }

                // the reading right after press k (journal UTC = log time - 3 h)
                var at = inRound[k - 1];
                var read = reads.FirstOrDefault(x => x.At > at && x.At < at.AddSeconds(60));
                if (read.Board is null)
                {
                    o.AppendLine(CultureInfo.InvariantCulture, $"  {before,3} before the end: no reading");
                    continue;
                }

                var survived = new int[runs];
                var board = read.Board;
                Parallel.For(0, runs, new ParallelOptions { MaxDegreeOfParallelism = 12 }, i =>
                    survived[i] = TeaSelfPlay.Play(model, options, 70000 + (round * 1000) + (before * 10) + i, maxTurns: 1500, from: board).Turns);
                var sorted = survived.OrderBy(x => x).ToArray();
                o.AppendLine(CultureInfo.InvariantCulture, $"  {before,3} before the end ({board.EmptyCount} empty, {board.ToJournal()}): practice survives median {sorted[runs / 2]} ({sorted[runs / 10]}-{sorted[(9 * runs) / 10]}); died within {before}: {survived.Count(x => x <= before)} of {runs}");
            }
        }

        File.WriteAllText(output, o.ToString());
        Console.WriteLine(o.ToString());
    }

    /// <summary>
    /// Each real press against what practice would play on the same board: the helper's reading just before the press
    /// (board and piece in hand), the press from the log (square; a tray press then a square is a swap), and the practice
    /// brain's options there (depth 2). Reports per round how often the real press was practice's first choice, how many
    /// points of value it gave away, and the costliest presses. Same environment as <see cref="SurviveFromRealBoards"/>.
    /// </summary>
    [TestMethod]
    [TestCategory("probe")]
    public void RealPressesAgainstPractice()
    {
        var netPath = Environment.GetEnvironmentVariable("TEA_NET") ?? @"C:\ProgramData\TOS-companion\teahouse\net.bin";
        using var f = File.OpenRead(netPath);
        var net = TeaNet.Load(f);
        var wanted = (Environment.GetEnvironmentVariable("TEA_ROUNDS") ?? "3,5,7,9").Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToHashSet();
        var output = Environment.GetEnvironmentVariable("TEA_SURVIVE") ?? Path.Combine(Path.GetTempPath(), "tea_presses.txt");

        var presses = new List<(DateTime At, int Square, int Piece)>();
        var ends = new List<DateTime>();
        {
            foreach (var line in LogLines())
            {
                if (line.Contains("on_grid_click", StringComparison.Ordinal) && Press().Match(line) is { Success: true } m)
                {
                    presses.Add((Time(m.Groups[1].Value), ((int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) - 1) * 6) + int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) - 1, int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture)));
                }
                else if (line.Contains("show_ui", StringComparison.Ordinal) && line.Contains("UI_TRIPLE_TOWN_SETTLE", StringComparison.Ordinal))
                {
                    ends.Add(Time(line[..23]));
                }
            }
        }

        var reads = new List<(DateTime At, TeaBoard Board, TeaKind Current)>();
        foreach (var file in new[] { "2026-10-29.jsonl", "2026-10-30.jsonl" })
        {
            var path = Path.Combine(@"C:\ProgramData\TOS-companion\journal", file);
            if (!File.Exists(path))
            {
                continue;
            }

            foreach (var line in File.ReadLines(path).Where(l => l.Contains("\"teahouse.advice\"", StringComparison.Ordinal)))
            {
                using var doc = JsonDocument.Parse(line);
                var fields = doc.RootElement.GetProperty("fields");
                var b = fields.GetProperty("board").GetString()!;
                if (!b.Contains('u', StringComparison.Ordinal) && b.Count(c => c == '/') == 5 && Enum.TryParse<TeaKind>(fields.GetProperty("current").GetString(), out var cur))
                {
                    reads.Add((DateTime.Parse(doc.RootElement.GetProperty("utc").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal), TeaBoard.Parse(b), cur));
                }
            }
        }

        var model = TeaModel.Default();
        var options = new TeaSolverOptions { Net = net, Depth = 2, Threads = 12 };
        var o = new StringBuilder();
        var bounds = new List<DateTime> { DateTime.MinValue };
        bounds.AddRange(ends);
        bounds.Add(DateTime.MaxValue);
        for (var round = 1; round < bounds.Count; round++)
        {
            if (!wanted.Contains(round))
            {
                continue;
            }

            var inRound = presses.Where(p => p.At > bounds[round - 1] && p.At < bounds[round]).ToList();
            int judged = 0, first = 0, swaps = 0, illegal = 0;
            double lost = 0;
            var worst = new List<(double Gap, string Text)>();
            for (var k = 0; k < inRound.Count; k++)
            {
                var press = inRound[k];
                if (press.Square == 0)
                {
                    continue;
                }

                var swap = k > 0 && inRound[k - 1].Square == 0;
                var from = swap ? inRound[k - 1].At : press.At;

                // the reading this press acted on: the last one before it (within a minute)
                var read = reads.LastOrDefault(x => x.At < from && x.At > from.AddSeconds(-60));
                if (read.Board is null)
                {
                    continue;
                }

                var advice = TeaSolver.Advise(read.Board, read.Current, model, options);
                var mine = advice.FirstOrDefault(a => a.Square == press.Square && (swap ? a.Action == TeaAction.Swap : a.Action == TeaAction.Place));
                judged++;
                swaps += swap ? 1 : 0;
                if (mine is null)
                {
                    illegal++;
                    continue;
                }

                var gap = advice[0].Value - mine.Value;
                lost += gap;
                first += gap < 1 ? 1 : 0;
                worst.Add((gap, $"press {k + 1}: {(swap ? "swap, " : string.Empty)}{read.Current} to {TeaBoard.Where(press.Square)}, practice {advice[0].Action} {advice[0].Piece} to {(advice[0].Square < 0 ? "tray" : TeaBoard.Where(advice[0].Square))}, {gap:F0} points of value given away | {read.Board.ToJournal()}"));
            }

            o.AppendLine(CultureInfo.InvariantCulture, $"round {round}: {inRound.Count} presses, {judged} judged ({swaps} swaps); practice's first choice {first} ({first * 100.0 / Math.Max(1, judged - illegal):F0}%); not an option on the read board {illegal}; value given away {lost:F0} in all, {lost / Math.Max(1, judged - illegal):F0} a press");
            foreach (var (_, text) in worst.OrderByDescending(x => x.Gap).Take(6))
            {
                o.AppendLine("    " + text);
            }
        }

        File.WriteAllText(output, o.ToString());
        Console.WriteLine(o.ToString());
    }

    /// <summary>The game log's lines, the previous generation first (a game restart moves log.txt to log.bak); read shared.</summary>
    private static IEnumerable<string> LogLines()
    {
        foreach (var name in new[] { "log.bak", "log.txt" })
        {
            var path = Path.Combine(@"D:\Games\TOS\Log", name);
            if (!File.Exists(path))
            {
                continue;
            }

            using var log = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            while (log.ReadLine() is { } line)
            {
                yield return line;
            }
        }
    }

    /// <summary>A log timestamp (PC local time; this probe assumes the capture PC ran 3 hours ahead of UTC) as UTC.</summary>
    private static DateTime Time(string s) => DateTime.ParseExact(s[..23], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture).AddHours(-3);
}
