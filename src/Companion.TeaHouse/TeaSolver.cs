using System.Collections.Concurrent;

namespace Companion.TeaHouse;

/// <summary>What to do with the current piece.</summary>
public enum TeaAction
{
    /// <summary>Place the current piece.</summary>
    Place,

    /// <summary>Put the current piece in the (empty) tray and take the next one (free: the cats do not move).</summary>
    Store,

    /// <summary>Swap the current piece with the tray's (free), then place the tray's piece.</summary>
    Swap,
}

/// <summary>One option the move finder weighed.</summary>
/// <param name="Action">Place, Store or Swap.</param>
/// <param name="Square">Where the piece goes (-1 for Store).</param>
/// <param name="Piece">The piece placed (the tray's for Swap; the current one for Store).</param>
/// <param name="Value">Expected points of the option: what it pays now plus what the board then promises.</param>
/// <param name="Points">Points the placement pays at once (joins), before the cats move.</param>
/// <param name="Joins">Joins the placement makes at once.</param>
/// <param name="Top">The highest piece those joins create.</param>
/// <param name="Became">What the placed piece turns into (an Oven's choice, Black Tea; the piece itself otherwise).</param>
/// <param name="Deep">True when the value looked one more piece ahead (the leading options).</param>
public sealed record TeaAdvice(TeaAction Action, int Square, TeaKind Piece, double Value, double Points, int Joins, TeaKind Top, TeaKind Became, bool Deep)
{
    /// <summary>Placements looked at: 1 this one only, 2 also the next piece, 3 also the one after.</summary>
    public int Depth { get; init; } = 1;

    /// <summary>Why the referee put this option first on a close call (<see cref="TeaReferee"/>), or null.</summary>
    public string? Reason { get; init; }
}

/// <summary>How hard the move finder thinks.</summary>
public sealed record TeaSolverOptions
{
    /// <summary>The leading options (by a one-move look) that get the full look of <see cref="Depth"/> placements.</summary>
    public int DeepOptions { get; init; } = 8;

    /// <summary>
    /// Placements looked at for the leading options: 2 = this one and every likely next piece (the quick advice); 3 = also
    /// every likely piece after that (the deeper thought while the helper waits for the user's move).
    /// </summary>
    public int Depth { get; init; } = 2;

    /// <summary>Inside a look ahead, the replies to a next piece (ranked by a one-move look) that get looked at further.</summary>
    public int DeepReplies { get; init; } = 3;

    /// <summary>Random cat moves per reply inside a look ahead (the leading options themselves weigh every cat walk).</summary>
    public int InnerCatSamples { get; init; } = 2;

    /// <summary>Stops a long think (the user moved); the move finder then throws <see cref="OperationCanceledException"/>.</summary>
    public CancellationToken Cancel { get; init; }

    /// <summary>
    /// The options from a shallower look, best first: the deepest look goes to the ones it ranked best (instead of the
    /// one-move ranking), so a move that only shines a few pieces ahead is not left out.
    /// </summary>
    public IReadOnlyList<TeaAdvice>? Prior { get; init; }

    /// <summary>Options weighed at the same time (each on its own core); 1 = one after the other.</summary>
    public int Threads { get; init; } = 1;

    /// <summary>
    /// Rule 1 of the rule design (2026-10-29): inside the look ahead, the next piece may also be swapped with the tray's
    /// piece (an Oven finishing a pair, a Cutlery on a cat, a stored cake joining). Without it a held joker is never used
    /// in the look ahead, so "build the pair now, finish it with the Oven next turn" cannot be seen at any depth.
    /// </summary>
    /// <remarks>On by default since its paired test (300 rounds, 2026-10-29): with rules 1-2 the share of rounds reaching
    /// 200k rose from 46.0 % to 58.3 % (64 rounds reached it only with them, 27 only without).</remarks>
    public bool TrayReplies { get; init; } = true;

    /// <summary>
    /// Rule 2: on a joker turn the deep look always includes the best option that spends the joker and the best that
    /// keeps it, whatever their one-move rank (before, two "use the Oven here" squares could take both deep slots).
    /// </summary>
    public bool DiverseDeepSet { get; init; } = true;

    /// <summary>Rule 4 and the user's own rule: expert tie-breaks among the leading options closer than the noise (<see cref="TeaReferee"/>).</summary>
    /// <remarks>Off: its paired test (300 rounds, 2026-10-30) lowered the share of rounds reaching 200k from 58.3 % to 53.7 %
    /// (the search's own close calls were better); keeping the Orange Cat in the tray made no difference (32 rounds each way).</remarks>
    public bool Referee { get; init; }

    /// <summary>The user's rule inside the referee: on a close call the Orange Cat stays in the tray.</summary>
    public bool KeepPrisoner { get; init; } = true;

    /// <summary>Rule 8: with 6 or fewer empty squares every cat move is weighed (Orange Cats' jumps with 4 or fewer).</summary>
    /// <remarks>On since its paired test (300 rounds, 2026-10-30): rounds reaching 200k 58.7 % to 60.3 %, median 229k to 236k
    /// (a small gain, 14 rounds against 9, and the more faithful search). Rule 10 (NoWastedOven) made no difference (5 against 6).</remarks>
    public bool ExactEndgame { get; init; } = true;

    /// <summary>Rule 10: an Oven that can only become Black Tea gets no deep look when a better use exists (soft: a wall by a cat counts).</summary>
    public bool NoWastedOven { get; init; }

    /// <summary>Random cat moves tried per option when the cats' moves cannot all be listed (an Orange Cat, many cats).</summary>
    public int CatSamples { get; init; } = 6;

    /// <summary>Up to this many outcomes, every way the White Cats can walk is weighed by its chance (no sampling).</summary>
    public int ExactCatOutcomes { get; init; } = 16;

    /// <summary>Share of the next-piece chance planned for (the rarest pieces are left out).</summary>
    public double NextPieceCover { get; init; } = 0.97;

    /// <summary>The board's scoring weights (when there is no learned network).</summary>
    public TeaWeights Weights { get; init; } = new();

    /// <summary>The learned board value (<see cref="TeaNet"/>); when set it scores the boards instead of the weights.</summary>
    public TeaNet? Net { get; init; }
}

/// <summary>
/// The move finder (expectimax over the next piece, averaged over random cat moves). For each option (every square the
/// current piece may go, storing it in an empty tray, or swapping with the tray's piece) it plays the placement and its
/// joins, tries a few random cat moves, and scores the result as the points paid plus the board's promise
/// (<see cref="TeaEvaluator"/>). The best options then look one piece further: for each likely next piece, the best
/// reply. Cat moves come from a generator seeded by the board, so the same board always gets the same advice.
/// Pure and thread-safe (no shared state).
/// </summary>
public static class TeaSolver
{
    /// <summary>Every option, best first. Empty when the current piece is unknown or nothing fits.</summary>
    public static IReadOnlyList<TeaAdvice> Advise(TeaBoard board, TeaKind current, TeaModel model, TeaSolverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(model);
        options ??= new TeaSolverOptions();
        if (!Placeable(current))
        {
            return [];
        }

        var context = new Context(model, options, model.Likely(options.NextPieceCover), new Random(Seed(board, current)), Seed(board, current));
        List<TeaAdvice> shallow;
        if (options.Prior is { Count: > 0 } prior)
        {
            shallow = [.. prior];
        }
        else
        {
            shallow = Options(board, current).Select(o => Value(board, current, o, context, 1, inner: false)).ToList();
            shallow.Sort((a, b) => b.Value.CompareTo(a.Value));
        }
        // the leading options are weighed side by side (pure functions of copies; the shared cache is concurrent)
        var result = shallow.ToArray();
        var deep = DeepSet(shallow, board, current, options);
        if (options.Depth > 1 && deep.Count > 0)
        {
            Parallel.ForEach(deep, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Threads), CancellationToken = options.Cancel }, i =>
                result[i] = Value(board, current, shallow[i], context, options.Depth, inner: false));
        }

        // values from different depths are not comparable: the deepest lead, best first
        var ordered = result.OrderByDescending(a => a.Depth).ThenByDescending(a => a.Value).ToList();
        return options.Referee ? TeaReferee.Apply(ordered, board, current, options.KeepPrisoner) : ordered;
    }

    /// <summary>Empty squares from which the endgame weighs every cat move (rule 8).</summary>
    private const int EndgameSquares = 6;

    /// <summary>The Oven and the Cutlery.</summary>
    public static bool IsJoker(TeaKind k) => k is TeaKind.Oven or TeaKind.Cutlery;

    /// <summary>
    /// The options that get the deep look: the best <see cref="TeaSolverOptions.DeepOptions"/> by rank, plus on a joker turn
    /// (with <see cref="TeaSolverOptions.DiverseDeepSet"/>) the best one spending the joker and the best one keeping it.
    /// </summary>
    internal static IReadOnlyList<int> DeepSet(IReadOnlyList<TeaAdvice> ranked, TeaBoard board, TeaKind current, TeaSolverOptions options)
    {
        // rule 10: a wasted Oven (Black Tea, not walling a cat) takes no deep slot while a better use exists
        bool Wasted(TeaAdvice o) => options.NoWastedOven && o.Piece == TeaKind.Oven && o.Became == TeaKind.BlackTea && o.Square > 0
            && !TeaBoard.Neighbours[o.Square].Any(nb => board[nb] == TeaKind.WhiteCat);
        var anyUseful = ranked.Any(o => !Wasted(o));
        var set = new SortedSet<int>();
        for (var i = 0; i < ranked.Count && set.Count < options.DeepOptions; i++)
        {
            if (!anyUseful || !Wasted(ranked[i]))
            {
                set.Add(i);
            }
        }
        if (options.DiverseDeepSet && (IsJoker(current) || IsJoker(board.TrayPiece)))
        {
            var spend = -1;
            var keep = -1;
            for (var i = 0; i < ranked.Count; i++)
            {
                var o = ranked[i];
                if (spend < 0 && IsJoker(o.Piece) && o.Action != TeaAction.Store && (!anyUseful || !Wasted(o)))
                {
                    spend = i;
                }

                var keeps = (o.Action == TeaAction.Store && IsJoker(current))
                    || (o.Action == TeaAction.Swap && IsJoker(current) && !IsJoker(o.Piece))
                    || (o.Action == TeaAction.Place && !IsJoker(current) && IsJoker(board.TrayPiece));
                if (keep < 0 && keeps)
                {
                    keep = i;
                }
            }

            if (spend >= 0)
            {
                set.Add(spend);
            }

            if (keep >= 0)
            {
                set.Add(keep);
            }
        }

        return [.. set];
    }

    /// <summary>
    /// Squares where the tray's piece is worth trying as a reply inside the look ahead (rule 1), at most six: an Oven where
    /// it would join, a Cutlery on a cat or a blocker, a White Cat where it is nearly boxed in, a stored cake or tea where it
    /// joins, an Orange Cat only near the end.
    /// </summary>
    internal static IEnumerable<int> ReplySquares(TeaBoard board, TeaKind tray)
    {
        var n = 0;
        for (var s = 1; s < TeaBoard.Squares && n < 6; s++)
        {
            var take = tray switch
            {
                TeaKind.Oven => board[s] == TeaKind.Empty && TeaRules.OvenChoice(board, s, TeaModel.Default()) != TeaKind.Empty,
                TeaKind.Cutlery => board[s] is TeaKind.WhiteCat or TeaKind.OrangeCat or TeaKind.BlackTea or TeaKind.Unknown,
                TeaKind.WhiteCat => board[s] == TeaKind.Empty && EmptyAround(board, s) <= 1,
                TeaKind.OrangeCat => board[s] == TeaKind.Empty && board.EmptyCount <= 4,
                _ => board[s] == TeaKind.Empty && TeaKinds.Joins(tray) && TouchingOfKind(board, s, tray) >= 2,
            };
            if (take)
            {
                n++;
                yield return s;
            }
        }
    }

    private static int EmptyAround(TeaBoard board, int s)
    {
        var n = 0;
        foreach (var nb in TeaBoard.Neighbours[s])
        {
            n += board[nb] == TeaKind.Empty ? 1 : 0;
        }

        return n;
    }

    /// <summary>Pieces of <paramref name="kind"/> in the distinct groups touching <paramref name="s"/>.</summary>
    private static int TouchingOfKind(TeaBoard board, int s, TeaKind kind)
    {
        Span<int> group = stackalloc int[TeaBoard.Squares];
        Span<bool> seen = stackalloc bool[TeaBoard.Squares];
        var total = 0;
        foreach (var nb in TeaBoard.Neighbours[s])
        {
            if (board[nb] != kind || seen[nb])
            {
                continue;
            }

            var size = TeaRules.Group(board, nb, kind, group);
            for (var g = 0; g < size; g++)
            {
                seen[group[g]] = true;
            }

            total += size;
        }

        return total;
    }

    /// <summary>True for a piece that can be placed (not empty, not unknown, not a board-only kind).</summary>
    public static bool Placeable(TeaKind k) => k is not (TeaKind.Empty or TeaKind.Unknown or TeaKind.Luxury or TeaKind.TeaHouse);

    /// <summary>
    /// True when <paramref name="piece"/> may be advised on <paramref name="square"/>: legal, and never the Cutlery on a
    /// piece the helper cannot see (2026-10-30, the user: the advice followed the game's Cutlery hint; the hint covers a
    /// piece, the reader saw "unknown" there, and cutting an unknown piece looked attractive, so whatever lay under the
    /// hint was chosen).
    /// </summary>
    public static bool Aimable(TeaBoard board, TeaKind piece, int square) =>
        TeaRules.CanPlace(board, piece, square) && !(piece == TeaKind.Cutlery && board[square] == TeaKind.Unknown);

    /// <summary>The options for <paramref name="current"/> (Store and Swap included when the tray allows).</summary>
    internal static IEnumerable<TeaAdvice> Options(TeaBoard board, TeaKind current)
    {
        for (var s = 1; s < TeaBoard.Squares; s++)
        {
            if (Aimable(board, current, s))
            {
                yield return new TeaAdvice(TeaAction.Place, s, current, 0, 0, 0, TeaKind.Empty, current, false);
            }
        }

        var tray = board.TrayPiece;
        if (tray == TeaKind.Empty)
        {
            yield return new TeaAdvice(TeaAction.Store, -1, current, 0, 0, 0, TeaKind.Empty, current, false);
        }
        else if (tray != current && Placeable(tray))
        {
            for (var s = 1; s < TeaBoard.Squares; s++)
            {
                if (Aimable(board, tray, s))
                {
                    yield return new TeaAdvice(TeaAction.Swap, s, tray, 0, 0, 0, TeaKind.Empty, tray, false);
                }
            }
        }
    }

    /// <summary>The option's value on <paramref name="board"/> with <paramref name="current"/> in hand, looking <paramref name="depth"/> placements.</summary>
    private static TeaAdvice Value(TeaBoard board, TeaKind current, TeaAdvice o, Context c, int depth, bool inner)
    {
        switch (o.Action)
        {
            case TeaAction.Store:
                return o with { Value = StoreValue(board, current, c, depth), Deep = depth > 1, Depth = depth };
            case TeaAction.Swap:
                var swapped = board.Clone();
                swapped.TrayPiece = current;
                return PlaceValue(swapped, o, c, depth, inner);
            default:
                return PlaceValue(board, o, c, depth, inner);
        }
    }

    /// <summary>
    /// Places <paramref name="o"/>'s piece on a copy of <paramref name="before"/> (its tray already as it will be), then the
    /// cats; the board after is scored by its promise, or (depth 2 and more) by the best replies to the likely next pieces.
    /// A leading option weighs every way the cats can walk; a reply inside a look ahead samples a few.
    /// </summary>
    private static TeaAdvice PlaceValue(TeaBoard before, TeaAdvice o, Context c, int depth, bool inner)
    {
        var after = before.Clone();
        var placed = TeaRules.Place(after, o.Piece, o.Square, c.Model);
        var justPlaced = TeaKinds.IsCat(o.Piece) ? o.Square : -1;

        // boxed-in cats become teas (certain), then the cats walk: every way when few, else random samples
        var (trapPoints, _, _) = TeaRules.Cats(after, c.Model, rng: null, justPlaced);
        double Future(TeaBoard b) => depth > 1 ? NextPieceValue(b, c, depth - 1) : c.Eval(b);
        var total = 0.0;
        var samples = inner ? c.Options.InnerCatSamples : c.Options.CatSamples;
        // rule 8 of the rule design: near the end every cat move is weighed, Orange Cats' jumps too (one move decides there)
        var endgame = c.Options.ExactEndgame && after.EmptyCount <= EndgameSquares;
        List<(TeaBoard Board, double Chance)>? outcomes = !AnyCatCanMove(after) ? [(after, 1.0)]
            : inner && !endgame ? null
            : TeaRules.CatOutcomes(after, justPlaced, endgame ? 64 : c.Options.ExactCatOutcomes, includeOrange: endgame && after.EmptyCount <= 4);
        if (outcomes is not null)
        {
            foreach (var (b, chance) in outcomes)
            {
                total += chance * Future(b);
            }
        }
        else
        {
            // the same random cat moves for every option (common random numbers): the options differ by the move, not by
            // which jumps their samples happened to draw (2026-10-29 live: with two Orange Cats the best two options were
            // 0.2 % apart, inside the sampling noise, and a Cutlery on the user's own Sponge Cake came out on top)
            var trial = new TeaBoard();
            for (var s = 0; s < samples; s++)
            {
                after.CopyTo(trial);
                TeaRules.MoveCats(trial, new Random(c.BaseSeed + (s * 7919) + (depth * 104729)), justPlaced);
                total += Future(trial) / samples;
            }
        }

        return o with
        {
            Value = placed.Points + trapPoints + total,
            Points = placed.Points,
            Joins = placed.Joins,
            Top = placed.Top,
            Became = placed.Became,
            Deep = depth > 1,
            Depth = depth,
        };
    }

    /// <summary>
    /// Storing costs no turn: the value is the next piece's best option with the tray now full (placing it, or swapping
    /// back and placing the stored piece), averaged over the likely next pieces.
    /// </summary>
    private static double StoreValue(TeaBoard board, TeaKind current, Context c, int depth)
    {
        var stored = board.Clone();
        stored.TrayPiece = current;

        // each likely next piece on its own core when the look is deep (storing a joker was the slowest option)
        var parts = new double[c.Likely.Count];
        void One(int n)
        {
            var (next, chance) = c.Likely[n];
            c.Options.Cancel.ThrowIfCancellationRequested();

            // the next piece's options, by a one-move look; a deeper look re-weighs only its three best (it costs ~50x)
            var ranked = Options(stored, next).Select(o => Value(stored, next, o, c, 1, inner: true)).OrderByDescending(a => a.Value).ToList();
            var best = double.NegativeInfinity;
            for (var i = 0; i < ranked.Count; i++)
            {
                if (depth <= 1)
                {
                    best = ranked[i].Value;
                    break;
                }

                if (i >= StoreDeepOptions)
                {
                    break;
                }

                best = Math.Max(best, Value(stored, next, ranked[i], c, depth, inner: true).Value);
            }

            parts[n] = chance * (double.IsNegativeInfinity(best) ? c.Full : best);
        }

        if (depth >= 3 && c.Options.Threads > 1)
        {
            Parallel.For(0, c.Likely.Count, new ParallelOptions { MaxDegreeOfParallelism = c.Options.Threads, CancellationToken = c.Options.Cancel }, One);
        }
        else
        {
            for (var n = 0; n < c.Likely.Count; n++)
            {
                One(n);
            }
        }

        return parts.Sum();
    }

    /// <summary>Options of the next piece re-weighed by a deep look after a Store.</summary>
    private const int StoreDeepOptions = 3;

    /// <summary>
    /// The expected best reply over the likely next pieces, looking <paramref name="depth"/> placements: at 1 every reply
    /// is scored by the board's promise (one random cat move); deeper, the <see cref="TeaSolverOptions.DeepReplies"/> best
    /// of those replies are looked at further.
    /// </summary>
    private static double NextPieceValue(TeaBoard board, Context c, int depth)
    {
        if (board.EmptyCount == 0)
        {
            return c.Full;
        }

        // the same board comes back many times in a look ahead (pieces placed in another order): each is weighed once
        var key = board + "|" + depth;
        if (c.Memo.TryGetValue(key, out var known))
        {
            return known;
        }

        var value = 0.0;
        var after = new TeaBoard();
        Span<double> cheap = stackalloc double[TeaBoard.Squares];
        Span<double> cheapSwap = stackalloc double[TeaBoard.Squares];
        var tray = board.TrayPiece;
        foreach (var (next, chance) in c.Likely)
        {
            c.Options.Cancel.ThrowIfCancellationRequested();
            var best = double.NegativeInfinity;
            cheap.Fill(double.NegativeInfinity);
            cheapSwap.Fill(double.NegativeInfinity);
            for (var s = 1; s < TeaBoard.Squares; s++)
            {
                if (!TeaRules.CanPlace(board, next, s))
                {
                    continue;
                }

                board.CopyTo(after);
                var placed = TeaRules.Place(after, next, s, c.Model);
                var (catPoints, _, _) = TeaRules.Cats(after, c.Model, new Random(c.BaseSeed + (int)next + (depth * 104729)), TeaKinds.IsCat(next) ? s : -1);
                cheap[s] = placed.Points + catPoints + c.Eval(after);
                best = Math.Max(best, cheap[s]);
            }

            // rule 1: the tray's piece as a reply (the next piece goes to the tray)
            var swaps = c.Options.TrayReplies && tray != TeaKind.Empty && tray != next && Placeable(tray);
            if (swaps)
            {
                foreach (var s in ReplySquares(board, tray))
                {
                    board.CopyTo(after);
                    after.TrayPiece = next;
                    var placed = TeaRules.Place(after, tray, s, c.Model);
                    if (!placed.Legal)
                    {
                        continue;
                    }

                    var (catPoints, _, _) = TeaRules.Cats(after, c.Model, new Random(c.BaseSeed + (int)next + (depth * 104729)), TeaKinds.IsCat(tray) ? s : -1);
                    cheapSwap[s] = placed.Points + catPoints + c.Eval(after);
                    best = Math.Max(best, cheapSwap[s]);
                }
            }

            if (depth > 1 && !double.IsNegativeInfinity(best))
            {
                // the best few replies, looked at one piece further (their value replaces the one-move guess)
                // candidates 0..35 place the next piece, 36..71 swap it for the tray's piece
                best = double.NegativeInfinity;
                var picked = new List<int>(c.Options.DeepReplies);
                for (var k = 0; k < c.Options.DeepReplies; k++)
                {
                    var top = -1;
                    for (var i = 1; i < 2 * TeaBoard.Squares; i++)
                    {
                        var v = i < TeaBoard.Squares ? cheap[i] : cheapSwap[i - TeaBoard.Squares];
                        var vt = top < 0 ? double.NegativeInfinity : top < TeaBoard.Squares ? cheap[top] : cheapSwap[top - TeaBoard.Squares];
                        if (!double.IsNegativeInfinity(v) && !picked.Contains(i) && (top < 0 || v > vt))
                        {
                            top = i;
                        }
                    }

                    if (top < 0)
                    {
                        break;
                    }

                    picked.Add(top);
                    if (top < TeaBoard.Squares)
                    {
                        var reply = new TeaAdvice(TeaAction.Place, top, next, 0, 0, 0, TeaKind.Empty, next, false);
                        best = Math.Max(best, PlaceValue(board, reply, c, depth, inner: true).Value);
                    }
                    else
                    {
                        var swapped = board.Clone();
                        swapped.TrayPiece = next;
                        var reply = new TeaAdvice(TeaAction.Swap, top - TeaBoard.Squares, tray, 0, 0, 0, TeaKind.Empty, tray, false);
                        best = Math.Max(best, PlaceValue(swapped, reply, c, depth, inner: true).Value);
                    }
                }
            }

            if (board.TrayPiece == TeaKind.Empty)
            {
                // storing the next piece keeps the board as it is
                board.CopyTo(after);
                after.TrayPiece = next;
                best = Math.Max(best, c.Eval(after));
            }

            value += chance * (double.IsNegativeInfinity(best) ? c.Full : best);
        }

        c.Memo[key] = value;
        return value;
    }

    private static bool AnyCatCanMove(TeaBoard board)
    {
        for (var i = 1; i < TeaBoard.Squares; i++)
        {
            if (board[i] == TeaKind.OrangeCat)
            {
                return true;
            }

            if (board[i] == TeaKind.WhiteCat)
            {
                foreach (var nb in TeaBoard.Neighbours[i])
                {
                    if (board[nb] == TeaKind.Empty)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>A stable seed from the position (FNV-1a; string hashes change per process).</summary>
    private static int Seed(TeaBoard board, TeaKind current)
    {
        var h = 2166136261u;
        for (var i = 0; i < TeaBoard.Squares; i++)
        {
            h = (h ^ (byte)board[i]) * 16777619u;
        }

        h = (h ^ (byte)current) * 16777619u;
        return (int)(h & 0x7fffffff);
    }

    private sealed record Context(TeaModel Model, TeaSolverOptions Options, IReadOnlyList<(TeaKind Kind, double Chance)> Likely, Random Rng, int BaseSeed)
    {
        /// <summary>Boards already weighed in this search (board and depth to value).</summary>
        public ConcurrentDictionary<string, double> Memo { get; } = new(StringComparer.Ordinal);

        /// <summary>What a full board is worth (the round is over: nothing more with a network, a big loss by the weights).</summary>
        public double Full => Options.Net is null ? Options.Weights.Full : 0;

        /// <summary>The board's promise: the learned network's value, else the hand-made weights.</summary>
        public double Eval(TeaBoard b) => Options.Net is { } net
            ? (b.EmptyCount == 0 ? 0 : net.Value(b))
            : TeaEvaluator.Evaluate(b, Model, Options.Weights);
    }
}
