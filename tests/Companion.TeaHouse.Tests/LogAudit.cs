using System.Text.Json;
using Companion.TeaHouse;

namespace Companion.TeaHouse.Tests;

/// <summary>
/// Replays every logged move (the teahouse.advice board and current piece, then the teahouse.move board) under the rules
/// and sorts what the game did that the rules do not predict. Reads the journal only. Category probe; TEA_SINCE limits it
/// to moves after a UTC time.
/// </summary>
[TestClass]
public sealed class LogAuditProbe
{
    private sealed record Pair(string Utc, TeaBoard Before, TeaKind Current, TeaBoard After, double? Delta);

    [TestMethod]
    [TestCategory("probe")]
    public void AuditTheRules()
    {
        var since = Environment.GetEnvironmentVariable("TEA_SINCE") ?? "2026-10-29T00:00";
        var pairs = ReadPairs(since);
        var model = TeaModel.Default();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var examples = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Count(string what, string? example = null)
        {
            counts[what] = counts.TryGetValue(what, out var n) ? n + 1 : 1;
            if (example is null)
            {
                return;
            }

            if (!examples.TryGetValue(what, out var list))
            {
                examples[what] = list = [];
            }

            if (list.Count < 5)
            {
                list.Add(example);
            }
        }

        foreach (var p in pairs)
        {
            var label = $"{p.Utc[11..19]} {p.Current} | {p.Before} -> {p.After}";
            var fit = Explain(p, model);
            if (fit is null)
            {
                Count("no placement explains it: " + Diagnose(p), label);
                continue;
            }

            Count("explained by the rules");
            var (piece, placed, trial, catPoints) = fit.Value;
            if (piece == TeaKind.Oven)
            {
                Count($"Oven became {placed.Became}", label);
            }

            foreach (var k in new[] { TeaKind.WhiteCat, TeaKind.OrangeCat })
            {
                var expected = Enumerable.Range(1, 35).Count(i => trial[i] == k);
                var seen = Enumerable.Range(1, 35).Count(i => p.After[i] == k);
                if (expected != seen)
                {
                    Count($"{k} count {(seen > expected ? "higher" : "lower")} than the rules leave", label);
                }
            }

            var whites = Enumerable.Range(1, 35).Where(i => trial[i] == TeaKind.WhiteCat).ToList();
            var whitesAfter = Enumerable.Range(1, 35).Where(i => p.After[i] == TeaKind.WhiteCat).ToList();
            if (whites.Count == whitesAfter.Count)
            {
                foreach (var w in whites)
                {
                    var free = TeaBoard.Neighbours[w].Any(n => trial[n] == TeaKind.Empty);
                    var step = whitesAfter.Any(a => TeaBoard.Neighbours[w].Contains(a));
                    var stays = whitesAfter.Contains(w);
                    if (step)
                    {
                        Count("White Cat: stepped next door");
                    }
                    else if (stays)
                    {
                        Count(free ? "White Cat: stayed though it had room" : "White Cat: stayed (boxed by cats that moved?)", free ? label : null);
                    }
                    else
                    {
                        Count("White Cat: landed further than one step", label);
                    }
                }
            }

            if (p.Delta is { } delta && delta >= 0)
            {
                var pays = placed.Points + catPoints;
                if (Math.Abs(delta - pays) < 1)
                {
                    Count("points as modelled");
                }
                else
                {
                    Count($"points differ ({piece}{(placed.Joins > 0 ? " making " + placed.Top : string.Empty)})", $"{label} | score +{delta}, rules +{pays}");
                }
            }
        }

        Console.WriteLine($"moves {pairs.Count} since {since}");
        foreach (var (k, n) in counts.OrderByDescending(x => x.Value))
        {
            Console.WriteLine($"{n,5}  {k}");
        }

        foreach (var (k, list) in examples)
        {
            Console.WriteLine($"--- {k}");
            foreach (var e in list)
            {
                Console.WriteLine("    " + e);
            }
        }
    }

    /// <summary>
    /// Tests the user's idea (2026-10-30): "orange cats decide their move before white cats, so a white cat can be stuck
    /// without a move (and without turning into a cup) because an orange cat jumped in the white cat's only free place".
    /// For every explained move: a White Cat that stayed although it had room after the placement, were all its free
    /// squares taken by Orange Cats afterwards? And a White Cat the rules boxed in (turned into a tea) that is still a
    /// cat afterwards, did an Orange Cat next to it leave? Category probe; TEA_SINCE as above.
    /// </summary>
    [TestMethod]
    [TestCategory("probe")]
    public void AuditCatOrder()
    {
        var since = Environment.GetEnvironmentVariable("TEA_SINCE") ?? "2026-10-29T00:00";
        var model = TeaModel.Default();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var examples = new List<string>();
        void Count(string what) => counts[what] = counts.TryGetValue(what, out var n) ? n + 1 : 1;
        foreach (var p in ReadPairs(since))
        {
            if (Explain(p, model) is not { } fit)
            {
                continue;
            }

            var (_, _, trial, _) = fit;
            var label = $"{p.Utc[11..19]} {p.Current} | {p.Before} -> {p.After}";

            // after the placement, before any cat moved: the board the cats start from (boxed cats not yet teas)
            var start = trial;
            foreach (var w in Enumerable.Range(1, TeaBoard.Squares - 1).Where(i => start[i] == TeaKind.WhiteCat))
            {
                var free = TeaBoard.Neighbours[w].Where(nb => start[nb] == TeaKind.Empty).ToList();
                var stays = p.After[w] == TeaKind.WhiteCat && !TeaBoard.Neighbours[w].Any(nb => p.After[nb] == TeaKind.WhiteCat && start[nb] != TeaKind.WhiteCat);
                if (free.Count == 0 || !stays)
                {
                    continue;
                }

                if (free.All(nb => p.After[nb] == TeaKind.OrangeCat))
                {
                    Count("White Cat stayed with room: every free square taken by an Orange Cat (fits the idea)");
                    if (examples.Count < 8)
                    {
                        examples.Add("taken: " + label + $"  cat at {TeaBoard.Where(w)}");
                    }
                }
                else
                {
                    Count("White Cat stayed with room: a free square was left (the idea does not explain it)");
                }
            }

            // White Cats the rules turned into teas (boxed in after the placement) that are still cats afterwards
            for (var i = 1; i < TeaBoard.Squares; i++)
            {
                if (start[i] == TeaKind.Tea && p.Before[i] == TeaKind.WhiteCat && p.After[i] == TeaKind.WhiteCat)
                {
                    var orangeLeft = TeaBoard.Neighbours[i].Any(nb => p.Before[nb] == TeaKind.OrangeCat && p.After[nb] != TeaKind.OrangeCat);
                    Count(orangeLeft
                        ? "White Cat the rules made a tea stayed a cat: an Orange Cat next to it left (fits oranges first)"
                        : "White Cat the rules made a tea stayed a cat: no Orange Cat left next to it");
                    if (examples.Count < 12)
                    {
                        examples.Add((orangeLeft ? "freed: " : "kept: ") + label + $"  cat at {TeaBoard.Where(i)}");
                    }
                }
            }
        }

        foreach (var (k, n) in counts.OrderByDescending(x => x.Value))
        {
            Console.WriteLine($"{n,5}  {k}");
        }

        foreach (var e in examples)
        {
            Console.WriteLine("    " + e);
        }
    }

    /// <summary>
    /// Which White Cat cup rule fits the real moves? For each explained move, the number of White Cats left afterwards is
    /// predicted four ways: (1) the rules today: boxed right after the placement, cats count as walls; (2) the same but
    /// cats are no walls (they will move); (3) Orange Cats jump first (to where the log's next reading shows them), then
    /// the boxed check with cats as walls; (4) Orange Cats first, cats no walls. Category probe; TEA_SINCE as above.
    /// </summary>
    [TestMethod]
    [TestCategory("probe")]
    public void AuditCupRules()
    {
        var since = Environment.GetEnvironmentVariable("TEA_SINCE") ?? "2026-10-29T00:00";
        var model = TeaModel.Default();
        var right = new int[4];
        var total = 0;
        var withWhites = 0;
        foreach (var p in ReadPairs(since))
        {
            if (Explain(p, model) is not { } fit)
            {
                continue;
            }

            // the board right after the placement, before any cat became a tea or moved
            var (piece, _, _, _) = fit;
            var placed = p.Before.Clone();
            var square = Enumerable.Range(1, TeaBoard.Squares - 1).FirstOrDefault(i => TeaRules.CanPlace(p.Before, piece, i) && Matches(p, piece, i, model));
            if (square == 0)
            {
                continue;
            }

            if (piece != p.Current)
            {
                placed.TrayPiece = p.Current;
            }

            TeaRules.Place(placed, piece, square, model);
            var whites = Enumerable.Range(1, TeaBoard.Squares - 1).Count(i => placed[i] == TeaKind.WhiteCat);
            var seen = Enumerable.Range(1, TeaBoard.Squares - 1).Count(i => p.After[i] == TeaKind.WhiteCat);
            total++;
            if (whites == 0)
            {
                continue;
            }

            withWhites++;

            // Orange Cats where the next reading shows them (their jump), the rest as placed
            var orangesFirst = placed.Clone();
            for (var i = 1; i < TeaBoard.Squares; i++)
            {
                if (orangesFirst[i] == TeaKind.OrangeCat)
                {
                    orangesFirst[i] = TeaKind.Empty;
                }
            }

            for (var i = 1; i < TeaBoard.Squares; i++)
            {
                if (p.After[i] == TeaKind.OrangeCat && orangesFirst[i] == TeaKind.Empty)
                {
                    orangesFirst[i] = TeaKind.OrangeCat;
                }
            }

            var variants = new[] { (placed, true), (placed, false), (orangesFirst, true), (orangesFirst, false) };
            for (var v = 0; v < 4; v++)
            {
                var (b, catsAreWalls) = variants[v];
                if (whites - Boxed(b, catsAreWalls) == seen)
                {
                    right[v]++;
                }
            }
        }

        Console.WriteLine($"{total} explained moves, {withWhites} with White Cats after the placement: White Cats left as seen afterwards");
        string[] names = ["rules today (boxed right after the placement, cats are walls)", "cats are no walls", "Orange Cats jump first, cats are walls", "Orange Cats jump first, cats are no walls"];
        for (var v = 0; v < 4; v++)
        {
            Console.WriteLine($"  {right[v],4} of {withWhites}  {names[v]}");
        }
    }

    /// <summary>
    /// Real points against the rules' points, move by move (2026-10-30: the latest round scored half of practice at the
    /// same move while playing nearly the same moves). For every explained move with a read score change: the real change
    /// and what the rules pay, summed by what the move did (piece placed, top piece made). Category probe; TEA_SINCE as above.
    /// </summary>
    [TestMethod]
    [TestCategory("probe")]
    public void AuditPoints()
    {
        var since = Environment.GetEnvironmentVariable("TEA_SINCE") ?? "2026-10-30T01:16";
        var model = TeaModel.Default();
        var rows = new Dictionary<string, (int N, double Real, double Rules)>(StringComparer.Ordinal);
        int moves = 0, withScore = 0;
        double real = 0, rules = 0;
        foreach (var p in ReadPairs(since))
        {
            moves++;
            if (p.Delta is not { } delta || delta < 0 || Explain(p, model) is not { } fit)
            {
                continue;
            }

            var (piece, placed, _, catPoints) = fit;
            var pays = placed.Points + catPoints;
            withScore++;
            real += delta;
            rules += pays;
            var key = placed.Joins > 0 ? $"{piece} making {placed.Top}" : catPoints > 0 ? $"{piece} (cats made teas)" : $"{piece} placed";
            var r = rows.TryGetValue(key, out var x) ? x : (0, 0, 0);
            rows[key] = (r.N + 1, r.Real + delta, r.Rules + pays);
        }

        Console.WriteLine($"{moves} moves since {since}, {withScore} explained with a score change read: real {real:F0}, rules {rules:F0}");
        foreach (var (k, v) in rows.OrderByDescending(x => Math.Abs(x.Value.Rules - x.Value.Real)))
        {
            Console.WriteLine($"  {v.N,4}  {k,-34} real {v.Real,8:F0}  rules {v.Rules,8:F0}  ({(v.Real - v.Rules) / Math.Max(1, v.N):+0;-0} a move)");
        }
    }

    /// <summary>White Cats in groups with no empty neighbour (and, when cats are no walls, no cat neighbour either).</summary>
    private static int Boxed(TeaBoard b, bool catsAreWalls)
    {
        var done = new bool[TeaBoard.Squares];
        var boxed = 0;
        for (var i = 1; i < TeaBoard.Squares; i++)
        {
            if (done[i] || b[i] != TeaKind.WhiteCat)
            {
                continue;
            }

            var group = new List<int> { i };
            done[i] = true;
            for (var g = 0; g < group.Count; g++)
            {
                foreach (var nb in TeaBoard.Neighbours[group[g]])
                {
                    if (!done[nb] && b[nb] == TeaKind.WhiteCat)
                    {
                        done[nb] = true;
                        group.Add(nb);
                    }
                }
            }

            var free = group.SelectMany(g => TeaBoard.Neighbours[g]).Any(nb => b[nb] == TeaKind.Empty || (!catsAreWalls && b[nb] == TeaKind.OrangeCat));
            boxed += free ? 0 : group.Count;
        }

        return boxed;
    }

    /// <summary>True when placing <paramref name="piece"/> on <paramref name="square"/> explains the move (as <see cref="Explain"/>).</summary>
    private static bool Matches(Pair p, TeaKind piece, int square, TeaModel model)
    {
        var trial = p.Before.Clone();
        if (piece != p.Current)
        {
            trial.TrayPiece = p.Current;
        }

        if (!TeaRules.Place(trial, piece, square, model).Legal)
        {
            return false;
        }

        TeaRules.Cats(trial, model, rng: null, TeaKinds.IsCat(piece) ? square : -1);
        for (var i = 1; i < TeaBoard.Squares; i++)
        {
            if (trial[i] != p.After[i] && !TeaKinds.IsCat(trial[i]) && !TeaKinds.IsCat(p.After[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static List<Pair> ReadPairs(string since)
    {
        var pairs = new List<Pair>();
        foreach (var file in Directory.GetFiles(@"C:\ProgramData\TOS-companion\journal", "2026-10-*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
        {
            TeaBoard? before = null;
            var current = TeaKind.Unknown;
            foreach (var line in File.ReadLines(file).Where(l => l.Contains("\"teahouse", StringComparison.Ordinal)))
            {
                using var doc = JsonDocument.Parse(line);
                // other journal lines that mention the Tea House (job records, auto-play presses) have another shape
                if (!doc.RootElement.TryGetProperty("utc", out var utcEl) || !doc.RootElement.TryGetProperty("fields", out var f)
                    || !f.TryGetProperty("event", out var evEl) || evEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var utc = utcEl.GetString()!;
                var ev = evEl.GetString();
                var board = f.TryGetProperty("board", out var b) ? b.GetString() : null;
                var ok = board is not null && !board.Contains('u', StringComparison.Ordinal) && !board.Contains('<', StringComparison.Ordinal);
                if (ev == "teahouse.advice")
                {
                    before = ok ? TeaBoard.Parse(board!) : null;
                    current = Enum.TryParse<TeaKind>(f.GetProperty("current").GetString(), out var k) ? k : TeaKind.Unknown;
                }
                else if (ev == "teahouse.move")
                {
                    if (before is not null && ok && string.CompareOrdinal(utc, since) >= 0)
                    {
                        double? delta = f.TryGetProperty("scoreDelta", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
                        pairs.Add(new Pair(utc, before, current, TeaBoard.Parse(board!), delta));
                    }

                    before = null;
                }
            }
        }

        return pairs;
    }

    /// <summary>The placement (current piece, or the tray's by a swap) whose result matches the board after on every non-cat square.</summary>
    private static (TeaKind Piece, TeaPlacement Placed, TeaBoard Trial, double CatPoints)? Explain(Pair p, TeaModel model)
    {
        var candidates = new List<(TeaKind Piece, bool Swap)> { (p.Current, false) };
        if (TeaSolver.Placeable(p.Before.TrayPiece) && p.Before.TrayPiece != p.Current)
        {
            candidates.Add((p.Before.TrayPiece, true));
        }

        foreach (var (piece, swap) in candidates)
        {
            for (var s = 1; s < TeaBoard.Squares; s++)
            {
                var trial = p.Before.Clone();
                if (swap)
                {
                    trial.TrayPiece = p.Current;
                }

                var placed = TeaRules.Place(trial, piece, s, model);
                if (!placed.Legal)
                {
                    continue;
                }

                var (catPoints, _, _) = TeaRules.Cats(trial, model, rng: null, TeaKinds.IsCat(piece) ? s : -1);
                var match = true;
                for (var i = 1; i < TeaBoard.Squares && match; i++)
                {
                    var a = trial[i];
                    var b = p.After[i];
                    match = a == b || TeaKinds.IsCat(a) || TeaKinds.IsCat(b);
                }

                if (match)
                {
                    return (piece, placed, trial, catPoints);
                }
            }
        }

        return null;
    }

    private static string Diagnose(Pair p)
    {
        var appeared = Enumerable.Range(1, 35).Where(i => !TeaKinds.IsCat(p.After[i]) && p.After[i] != TeaKind.Empty && p.Before[i] != p.After[i]).ToList();
        var vanished = Enumerable.Range(1, 35).Where(i => !TeaKinds.IsCat(p.Before[i]) && p.Before[i] != TeaKind.Empty && p.After[i] == TeaKind.Empty).ToList();
        if (appeared.Count == 0 && vanished.Count == 0)
        {
            return "only cats changed (a store, or the placed piece hidden)";
        }

        if (appeared.Count >= 2 && vanished.Count == 0)
        {
            return "two or more pieces appeared (two moves between readings?)";
        }

        if (appeared.Any(i => p.After[i] is TeaKind.Tea or TeaKind.Sundae or TeaKind.TeaHouse))
        {
            return "a tea appeared or grew where the rules put none";
        }

        if (vanished.Count > 0 && appeared.Count == 0)
        {
            return "pieces vanished with nothing created";
        }

        return "a join or a placement the rules do not reproduce";
    }
}
