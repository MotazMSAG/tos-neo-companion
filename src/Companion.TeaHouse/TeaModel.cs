namespace Companion.TeaHouse;

/// <summary>One move's score change, kept so the points can be solved again as more moves come in.</summary>
/// <param name="Placed">The piece placed.</param>
/// <param name="Became">What it became (an Oven's choice; the piece itself otherwise).</param>
/// <param name="Joins">Joins the placement made (a chain counts each step; boxed-in cats' joins excluded).</param>
/// <param name="Top">The highest piece created.</param>
/// <param name="Delta">The score's change.</param>
public sealed record TeaPointsObservation(TeaKind Placed, TeaKind Became, int Joins, TeaKind Top, double Delta);

/// <summary>
/// What the game pays and hands out, as far as the helper knows: the points for each piece a join creates, the points
/// for placing a piece, and how often each piece comes as the current piece. The game lists none of it (the user,
/// 2026-10-29), so the tables start from guesses and are learned by watching: the points from the score's change after a
/// move, the odds by counting the current pieces seen. Plain data the host stores; not thread-safe.
/// </summary>
public sealed class TeaModel
{
    /// <summary>Weight of the starting odds, in pieces (a few rounds of counting outweigh it).</summary>
    public const double PriorWeight = 200;

    /// <summary>
    /// The starting odds, measured (2026-10-31: 3,243 pieces of 2026-10-29..31, 1,046 of them exact from the game log's
    /// board presses; no change with score, round progress, board, tray or the previous piece). The old guess gave the
    /// Orange Cat 1.5% (real 4.35%), so practice rounds built on <see cref="Default"/> met a third of the real Orange Cats.
    /// </summary>
    private static readonly (TeaKind Kind, double Share)[] PriorOdds =
    [
        (TeaKind.Cookie, 0.567), (TeaKind.Sponge, 0.166), (TeaKind.Pudding, 0.016), (TeaKind.Choco, 0.009),
        (TeaKind.WhiteCat, 0.129), (TeaKind.OrangeCat, 0.0435), (TeaKind.Oven, 0.038), (TeaKind.Cutlery, 0.031),
    ];

    /// <summary>Points for creating each kind by a join (index = kind).</summary>
    public double[] JoinPoints { get; } = new double[TeaKinds.Count];

    /// <summary>Points for placing each kind (index = kind); none known yet.</summary>
    public double[] PlacePoints { get; } = new double[TeaKinds.Count];

    /// <summary>Current pieces seen per kind (index = kind).</summary>
    public long[] Seen { get; } = new long[TeaKinds.Count];

    /// <summary>Kinds whose join points were measured (not a guess).</summary>
    public bool[] JoinPointsMeasured { get; } = new bool[TeaKinds.Count];

    /// <summary>Kinds whose placing points were measured.</summary>
    public bool[] PlacePointsMeasured { get; } = new bool[TeaKinds.Count];

    /// <summary>The moves the points are solved from (the newest <see cref="MaxObservations"/>).</summary>
    public List<TeaPointsObservation> Observations { get; } = [];

    /// <summary>Moves kept for solving the points.</summary>
    public const int MaxObservations = 300;

    /// <summary>
    /// The cakes' points as measured: Sponge Cake 40, Pudding 200, Chocolate Cake 500, Cupcake 1500, Shortcake 5000,
    /// Strawberry Cake 10000 (2026-10-30: the user confirmed it, and the log shows an Oven finishing three Shortcake Slices
    /// paying exactly 10000; the 5-15-50 guess of 15000 was wrong). The Luxury Cake is a guess: 30000, the levels so far
    /// growing about threefold on average. Unmeasured levels take this ladder, scaled to the highest measured level.
    /// </summary>
    private static readonly double[] Ladder = [0, 0, 40, 200, 500, 1500, 5000, 10000, 30000];

    /// <summary>
    /// The starting values, measured in the user's rounds of 2026-10-29/30: making a Sponge Cake pays 40, a Pudding 200, a
    /// Chocolate Cake 500, a Cupcake 1500, a Shortcake 5000, a Strawberry Cake 10000, a Sundae 1000, a Kitty Tea House 5000;
    /// placing a Cookie 5, a Sponge Cake 40, a Pudding 100, a Chocolate Cake 500; placing a cat, the Cutlery or an Oven that
    /// joins nothing pays nothing (an Oven that joins pays only its joins; 2026-10-31 audit of 2,109 clean moves: 2,099
    /// exact). The Luxury Cake is guessed (30000, see <see cref="Ladder"/>); placing an unmeasured piece is guessed at half
    /// of making it.
    /// </summary>
    public static TeaModel Default()
    {
        var m = new TeaModel();
        for (var k = TeaKind.Sponge; k <= TeaKind.Luxury; k++)
        {
            m.JoinPoints[(int)k] = Ladder[(int)k];
            m.PlacePoints[(int)k] = m.JoinPoints[(int)k] / 2;
        }

        m.PlacePoints[(int)TeaKind.Cookie] = 5;
        m.PlacePoints[(int)TeaKind.Sponge] = 40; // the log audit of 2026-10-30: 25 moves at +40, none at the guessed +20
        m.PlacePoints[(int)TeaKind.Pudding] = 100;
        m.PlacePoints[(int)TeaKind.Choco] = 500;
        m.PlacePoints[(int)TeaKind.Oven] = 0; // joining nothing (4 of 4 Black Tea Ovens paid 0); an Oven that joins pays only its joins
        m.JoinPoints[(int)TeaKind.Sundae] = 1000;
        m.JoinPoints[(int)TeaKind.TeaHouse] = 5000;

        // measured in the user's rounds (not to be boosted by "find out points")
        foreach (var k in new[] { TeaKind.Sponge, TeaKind.Pudding, TeaKind.Choco, TeaKind.Shortcake, TeaKind.WholeCake, TeaKind.Sundae, TeaKind.TeaHouse })
        {
            m.JoinPointsMeasured[(int)k] = true;
        }

        foreach (var k in new[] { TeaKind.Cookie, TeaKind.Sponge, TeaKind.Pudding, TeaKind.Choco, TeaKind.WhiteCat, TeaKind.OrangeCat, TeaKind.Oven, TeaKind.Cutlery })
        {
            m.PlacePointsMeasured[(int)k] = true;
        }

        m.JoinPointsMeasured[(int)TeaKind.Cupcake] = true; // 1500, confirmed by a Sponge chain paying 40 + 200 + 500 + 1500

        return m;
    }

    /// <summary>
    /// Takes in one move's score change and solves the points again from all the moves kept (an early value worked out
    /// against a guessed placing value is corrected once that placing value is measured). Codes of what changed.
    /// </summary>
    public IReadOnlyList<string> Observe(TeaPointsObservation o)
    {
        Observations.Add(o);
        if (Observations.Count > MaxObservations)
        {
            Observations.RemoveAt(0);
        }

        return Solve();
    }

    /// <summary>
    /// The points from the moves kept: placing a piece pays the typical score change of the moves that joined nothing;
    /// each join's piece pays the typical value of what is left once the placing and the other (known) joins of its chain are
    /// taken off, solved level by level; unmeasured cake levels above the highest measured one are guessed. Codes of the
    /// values that changed ("points.Cupcake:1500", "place.Pudding:100").
    /// </summary>
    public IReadOnlyList<string> Solve()
    {
        var changed = new List<string>();
        var place = new Dictionary<TeaKind, List<double>>();
        foreach (var o in Observations.Where(o => o.Joins == 0 && o.Delta >= 0))
        {
            Add(place, o.Placed, o.Delta);
        }

        foreach (var (k, values) in place)
        {
            Set(PlacePoints, PlacePointsMeasured, k, Typical(values), "place", changed);
        }

        var known = new HashSet<TeaKind>();
        for (var pass = 0; pass < 4; pass++)
        {
            var join = new Dictionary<TeaKind, List<double>>();
            foreach (var o in Observations.Where(o => o.Joins > 0 && o.Delta > 0))
            {
                var chain = Chain(o);
                if (chain is null)
                {
                    continue;
                }

                // an Oven that joins pays only its joins (2026-10-30 log: 9 of 9, e.g. a Strawberry Cake exactly 10000)
                var unknown = chain.Where(k => !known.Contains(k)).ToList();
                var placing = o.Placed == TeaKind.Oven ? 0 : PlacePoints[(int)o.Placed];
                var rest = o.Delta - placing - chain.Where(known.Contains).Sum(k => JoinPoints[(int)k]);
                if (unknown.Count == 1 && rest > 0)
                {
                    Add(join, unknown[0], rest);
                }
            }

            if (join.Count == 0)
            {
                break;
            }

            foreach (var (k, values) in join)
            {
                Set(JoinPoints, JoinPointsMeasured, k, Typical(values), "points", changed);
                known.Add(k);
            }
        }

        // the levels above the highest measured cake: guessed
        var top = TeaKind.Empty;
        for (var k = TeaKind.Sponge; k <= TeaKind.Luxury; k++)
        {
            if (JoinPointsMeasured[(int)k])
            {
                top = k;
            }
        }

        var scale = top == TeaKind.Empty ? 1 : JoinPoints[(int)top] / Ladder[(int)top];
        for (var k = top == TeaKind.Empty ? TeaKind.Cupcake : top + 1; k <= TeaKind.Luxury; k++)
        {
            if (!JoinPointsMeasured[(int)k])
            {
                JoinPoints[(int)k] = Ladder[(int)k] * scale;
            }

            if (!PlacePointsMeasured[(int)k])
            {
                PlacePoints[(int)k] = JoinPoints[(int)k] / 2;
            }
        }

        return changed;
    }

    /// <summary>The pieces a move's joins created, lowest first, or null when they cannot be told (cats, odd counts).</summary>
    private static List<TeaKind>? Chain(TeaPointsObservation o)
    {
        var chain = new List<TeaKind>();
        var k = o.Became;
        for (var i = 0; i < o.Joins; i++)
        {
            k = TeaKinds.Next(k);
            if (k == TeaKind.Empty)
            {
                return null;
            }

            chain.Add(k);
        }

        return chain.Count > 0 && chain[^1] == o.Top ? chain : null;
    }

    private static void Add(Dictionary<TeaKind, List<double>> d, TeaKind k, double v)
    {
        if (!d.TryGetValue(k, out var list))
        {
            d[k] = list = [];
        }

        list.Add(v);
    }

    /// <summary>
    /// The value seen most often, ties going to the lower (the game pays exact amounts, and a score change that covered two
    /// moves only ever adds): one outlier among two or three moves cannot win (2026-10-31: the upper median of [100, 145]
    /// made Pudding's placing value 145, and the Chocolate Cake 455 followed from it).
    /// </summary>
    private static double Typical(List<double> values) =>
        values.GroupBy(v => v).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;

    private static void Set(double[] table, bool[] measured, TeaKind k, double value, string what, List<string> changed)
    {
        if (table[(int)k] != value || !measured[(int)k])
        {
            changed.Add($"{what}.{k}:{value:0}");
        }

        table[(int)k] = value;
        measured[(int)k] = true;
    }

    /// <summary>How often <paramref name="kind"/> comes as the current piece (guess blended with the counts).</summary>
    public double Odds(TeaKind kind)
    {
        var total = PriorWeight;
        foreach (var k in TeaKinds.Spawnable)
        {
            total += Seen[(int)k];
        }

        var prior = 0.0;
        foreach (var (k, share) in PriorOdds)
        {
            if (k == kind)
            {
                prior = share;
            }
        }

        var priorSum = PriorOdds.Sum(x => x.Share);
        return ((prior / priorSum * PriorWeight) + Seen[(int)kind]) / total;
    }

    /// <summary>
    /// The likeliest next pieces, most likely first, until they cover <paramref name="cover"/> of the chance (the rest are
    /// too rare to plan for); their chances are scaled to sum to 1.
    /// </summary>
    public IReadOnlyList<(TeaKind Kind, double Chance)> Likely(double cover = 0.97)
    {
        var all = TeaKinds.Spawnable.Select(k => (Kind: k, Chance: Odds(k))).Where(x => x.Chance > 0).OrderByDescending(x => x.Chance).ToList();
        var picked = new List<(TeaKind, double)>();
        var sum = 0.0;
        foreach (var x in all)
        {
            picked.Add(x);
            sum += x.Chance;
            if (sum >= cover)
            {
                break;
            }
        }

        return picked.Select(x => (x.Item1, x.Item2 / sum)).ToList();
    }

    /// <summary>
    /// The model for "find out points" (the user, 2026-10-29: "a temp intelligence that has the goal of building the
    /// highest single cake possible so that we know the points"): a copy where each piece whose points are not measured
    /// yet (the top cakes, the teas) pays <paramref name="boost"/> times its guess, so the move finder aims for it; the real
    /// points are then measured when it is made.
    /// </summary>
    public TeaModel ForExploring(double boost = 10)
    {
        var m = Clone();
        for (var k = TeaKind.Sponge; k <= TeaKind.TeaHouse; k++)
        {
            if (!JoinPointsMeasured[(int)k] && k != TeaKind.Tea)
            {
                m.JoinPoints[(int)k] *= boost;
            }
        }

        return m;
    }

    /// <summary>A copy (the learner changes its own model).</summary>
    public TeaModel Clone()
    {
        var m = new TeaModel();
        Array.Copy(JoinPoints, m.JoinPoints, TeaKinds.Count);
        Array.Copy(PlacePoints, m.PlacePoints, TeaKinds.Count);
        Array.Copy(Seen, m.Seen, TeaKinds.Count);
        Array.Copy(JoinPointsMeasured, m.JoinPointsMeasured, TeaKinds.Count);
        Array.Copy(PlacePointsMeasured, m.PlacePointsMeasured, TeaKinds.Count);
        m.Observations.AddRange(Observations);
        return m;
    }
}
