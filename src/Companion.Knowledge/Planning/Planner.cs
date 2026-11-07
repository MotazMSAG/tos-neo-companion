using Companion.Contracts.Knowledge;
using Companion.Contracts.Planning;
using Companion.Knowledge.Storage;

namespace Companion.Knowledge.Planning;

/// <summary>
/// Pure craft planner (SPEC 11.3). No I/O, no clock, no randomness: the same request always gives the same plan.
/// <para>Semantics chosen where the contract leaves room:</para>
/// <list type="bullet">
/// <item>A <see cref="CraftGoal"/> is a quantity to PRODUCE: stock of the goal item itself is not counted against it
/// (re-plan after partial completion = the caller lowers the goal by what was produced; intermediates already made
/// come back through the resynced stock).</item>
/// <item>Material demand is served from stock first (lower bound: exact count, else range minimum), minus the
/// keep-stock reserve; the rest is crafted. Surplus output (whole batches) goes back to the pool, so shared
/// intermediates are aggregated: demand from several parents never makes more batches than the total needs.</item>
/// <item>Recipe choice per item is made once (first demand) and reused. FewestCrafts simulates every candidate and
/// ranks: no new blocking problem first, then fewest total batches, then verified, then id (ordinal).</item>
/// <item>Cycles are detected on (item, recipe) paths; a recipe already on the path is skipped and an alternate is
/// tried; when none is left the item gets a Cycle issue.</item>
/// <item>Success-rate recipes plan with the lower-bound yield floor(batches × outputQty × rate).</item>
/// <item>Stale stock (a StaleStock issue): an entry marked stale, observed at or before the latest stale mark, or, when
/// the request carries its time (2026-10-15), a volatile entry past its fresh window (shared homestead goods).</item>
/// <item>Verified recipes: complete and user-verified; with <see cref="PlannerPolicy.TrustLogNamedRecipes"/> (2026-10-15)
/// also complete recipes whose product the game named in an item tip (a product item id).</item>
/// <item>Not committable on any shortfall or any Cycle, NeedsVerification, UnknownStock, StaleStock, NoRecipe or
/// DepthExceeded issue (DepthExceeded leaves needs unplanned, so it also blocks). BatchLimited is informational,
/// except "batch.excessive" (over 100 000 batches for one recipe), which blocks.</item>
/// </list>
/// Never throws: bad data becomes issues; an unexpected internal failure returns a non-committable plan with the
/// issue code <c>planner.error</c>. Stateless and thread-safe.
/// </summary>
public sealed class Planner : IPlanner
{
    /// <summary>Creates the planner (no dependencies, no side effects).</summary>
    public Planner()
    {
    }

    /// <inheritdoc />
    public CraftPlan Plan(PlanRequest request)
    {
        if (request is null)
        {
            return PlanRun.Failed("planner.request");
        }
        try
        {
            return new PlanRun(request).Run();
        }
        catch (Exception ex) when (ex is ArithmeticException or InvalidOperationException or ArgumentException or NullReferenceException or KeyNotFoundException)
        {
            return PlanRun.Failed("planner.error");
        }
    }
}

/// <summary>One planning run (internal; all state is local to the run).</summary>
internal sealed class PlanRun
{
    /// <summary>Default is_block cadence range per tick (PL-01).</summary>
    internal static readonly TimeSpan DefaultTickMin = TimeSpan.FromSeconds(1.9);

    /// <summary>Default is_block cadence range per tick (PL-01).</summary>
    internal static readonly TimeSpan DefaultTickMax = TimeSpan.FromSeconds(3.0);

    /// <summary>A learned median cadence m gives the per-tick range [0.9 m, 1.2 m].</summary>
    internal const double LearnedMinFactor = 0.9;

    /// <summary>A learned median cadence m gives the per-tick range [0.9 m, 1.2 m].</summary>
    internal const double LearnedMaxFactor = 1.2;

    /// <summary>Simulated expansions allowed for FewestCrafts ranking before falling back to the static tie-break.</summary>
    internal const int SimulationBudget = 50_000;

    /// <summary>Batches for one recipe above which the plan is refused ("batch.excessive").</summary>
    internal const long MaxNodeBatches = 100_000;

    /// <summary>Upper bound on the recursion depth whatever the policy says.</summary>
    internal const int HardMaxDepth = 64;

    private const double Epsilon = 1e-9;

    private readonly IReadOnlyList<CraftGoal> _goals;
    private readonly PlannerPolicy _policy;
    private readonly int _maxBatch;
    private readonly int _maxDepth;
    private readonly Dictionary<ItemKey, List<Recipe>> _byProduct = [];
    private readonly Dictionary<ItemKey, string> _invalidByProduct = [];
    private readonly IReadOnlyDictionary<ItemKey, StockEntry> _stock;
    private readonly Dictionary<ItemKey, long> _reserve = [];
    private readonly DateTimeOffset? _staleSince;
    private readonly DateTimeOffset? _now;
    private int _budget = SimulationBudget;

    internal PlanRun(PlanRequest request)
    {
        _goals = request.Goals ?? [];
        _policy = request.Policy ?? PlannerPolicy.Default;
        _maxBatch = Math.Max(1, _policy.MaxBatchPerCraft);
        _maxDepth = Math.Clamp(_policy.MaxDepth, 1, HardMaxDepth);
        _stock = request.Stock ?? new Dictionary<ItemKey, StockEntry>();
        _staleSince = request.StockStaleSince;
        _now = request.NowUtc;

        foreach (var rule in request.KeepStock ?? [])
        {
            if (rule is not null && KnowledgeSchema.IsValidItem(rule.Item) && rule.Min > 0)
            {
                _reserve[rule.Item] = Math.Max(_reserve.GetValueOrDefault(rule.Item), rule.Min);
            }
        }

        var recipes = (request.Recipes ?? []).Where(r => r is not null).ToList();
        var idCounts = recipes.Where(r => r.Id is not null).GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var r in recipes.OrderBy(r => r.Id ?? string.Empty, StringComparer.Ordinal))
        {
            var invalid = r.Id is null
                || KnowledgeSchema.ValidateRecipeShape(r) is not null
                || (idCounts.TryGetValue(r.Id, out var sameId) && sameId > 1);
            if (invalid)
            {
                if (KnowledgeSchema.IsValidItem(r.Product) && !_invalidByProduct.ContainsKey(r.Product))
                {
                    _invalidByProduct[r.Product] = r.Id ?? string.Empty;
                }
                continue;
            }
            if (!_byProduct.TryGetValue(r.Product, out var list))
            {
                _byProduct[r.Product] = list = [];
            }
            list.Add(r);
        }
    }

    /// <summary>A non-committable empty plan carrying one error code.</summary>
    internal static CraftPlan Failed(string code) => new(
        [],
        [],
        [new PlanIssue(PlanIssueKind.NeedsVerification, new ItemKey(string.Empty), null, code)],
        new TimeRange(TimeSpan.Zero, TimeSpan.Zero),
        false);

    internal static bool IsBlocking(PlanIssueKind kind) => kind != PlanIssueKind.BatchLimited;

    /// <summary>Verified: complete and confirmed by the user (or, with <see cref="PlannerPolicy.TrustLogNamedRecipes"/>, log-named).</summary>
    internal static bool IsVerified(Recipe r, bool trustLogNamed = false) =>
        (r.Confidence == RecipeConfidence.Complete && (r.UserVerified || (trustLogNamed && r.ProductItemId is > 0)))
        || (trustLogNamed && r.ProductItemId is > 0 && IsPlannablePartial(r));

    /// <summary>
    /// A partly read recipe whose every material is named (item id) with a required quantity: only an owned count was
    /// unreadable, which the plan takes from stock anyway (live 2026-10-15: Aquatic Feast stayed partial on one owned
    /// count at glyph score 0.749).
    /// </summary>
    internal static bool IsPlannablePartial(Recipe r) =>
        r.Confidence == RecipeConfidence.Partial && r.Materials.Count > 0 && r.Materials.All(m => m.ItemId is > 0 && m.Qty > 0);

    private bool Verified(Recipe r) => IsVerified(r, _policy.TrustLogNamedRecipes);

    /// <summary>Lower-bound output of <paramref name="batches"/> crafts.</summary>
    internal static long LowerBound(Recipe r, long batches) =>
        r.SuccessRate is double p
            ? (long)Math.Floor(batches * (double)r.OutputQty * p + Epsilon)
            : checked(batches * r.OutputQty);

    /// <summary>Fewest batches whose lower-bound output covers <paramref name="need"/>.</summary>
    internal static long BatchesFor(Recipe r, long need)
    {
        if (need <= 0)
        {
            return 0;
        }
        if (r.SuccessRate is not double p)
        {
            return checked(need + r.OutputQty - 1) / r.OutputQty;
        }
        var b = Math.Max(1L, (long)Math.Ceiling(need / (r.OutputQty * p) - Epsilon));
        while (LowerBound(r, b) < need)
        {
            b = checked(b + 1);
        }
        while (b > 1 && LowerBound(r, b - 1) >= need)
        {
            b--;
        }
        return b;
    }

    internal CraftPlan Run()
    {
        var s = new State();
        foreach (var goal in _goals)
        {
            if (goal is null || !KnowledgeSchema.IsValidItem(goal.Item) || goal.Qty <= 0)
            {
                continue;
            }
            if (!_byProduct.ContainsKey(goal.Item))
            {
                if (_invalidByProduct.TryGetValue(goal.Item, out var bad))
                {
                    AddIssue(s, PlanIssueKind.NeedsVerification, goal.Item, bad, "recipe.invalid");
                }
                else
                {
                    AddIssue(s, PlanIssueKind.NoRecipe, goal.Item, null, "recipe.none");
                }
                continue;
            }
            Produce(s, goal.Item, goal.Qty, null, 1, null);
        }
        return Build(s);
    }

    private void Need(State s, ItemKey item, long qty, PathNode? path, int level, NodeKey consumer)
    {
        if (qty <= 0)
        {
            return;
        }
        var known = Consult(s, item);
        var remaining = qty - Take(s, item, qty, consumer);
        if (remaining == 0)
        {
            return;
        }
        if (!_byProduct.ContainsKey(item))
        {
            if (_invalidByProduct.TryGetValue(item, out var bad))
            {
                AddIssue(s, PlanIssueKind.NeedsVerification, item, bad, "recipe.invalid");
            }
            AddShortfall(s, item, remaining, known ? ShortfallKind.RawMaterial : ShortfallKind.UnknownStock);
            return;
        }
        Produce(s, item, remaining, path, level, consumer);
    }

    private void Produce(State s, ItemKey item, long qty, PathNode? path, int level, NodeKey? consumer)
    {
        _budget--;
        if (level > _maxDepth)
        {
            AddIssue(s, PlanIssueKind.DepthExceeded, item, null, "depth.exceeded");
            return;
        }
        var all = _byProduct[item];
        var open = all.Where(r => !PathNode.Contains(path, item, r.Id)).ToList();
        if (open.Count == 0)
        {
            AddIssue(s, PlanIssueKind.Cycle, item, all[0].Id, "cycle");
            return;
        }
        var recipe = Choose(s, item, qty, all.Count, open, path, level, consumer);
        ProduceWith(s, item, qty, recipe, path, level, consumer);
    }

    private void ProduceWith(State s, ItemKey item, long qty, Recipe r, PathNode? path, int level, NodeKey? consumer)
    {
        if (!Verified(r))
        {
            AddIssue(s, PlanIssueKind.NeedsVerification, item, r.Id,
                r.Confidence == RecipeConfidence.Partial ? "recipe.partial" : "recipe.unverified");
            if (!_policy.AllowUnverifiedRecipes)
            {
                return;
            }
        }

        var key = new NodeKey(item, r.Id);
        var node = s.GetOrAddNode(key, r);
        var batches = BatchesFor(r, qty);
        node.Batches = checked(node.Batches + batches);
        s.TotalBatches = checked(s.TotalBatches + batches);
        if (consumer is { } c)
        {
            AddEdge(s, key, c);
        }
        var surplus = LowerBound(r, batches) - qty;
        if (surplus > 0)
        {
            s.LotsOf(item).Add(new Lot(key, surplus));
        }

        var inner = new PathNode(item, r.Id, path);
        foreach (var m in r.Materials)
        {
            Need(s, m.Item, checked(m.Qty * batches), inner, level + 1, key);
        }
    }

    private Recipe Choose(State s, ItemKey item, long qty, int candidateCount, List<Recipe> open, PathNode? path, int level, NodeKey? consumer)
    {
        if (s.Choice.TryGetValue(item, out var memo) && open.Find(r => r.Id == memo) is { } remembered)
        {
            return remembered;
        }

        Recipe chosen;
        if (_policy.Choice == RecipeChoicePolicy.Preferred
            && _policy.PreferredRecipeByItem is { } preferred
            && preferred.TryGetValue(item.Value, out var preferredId)
            && open.Find(r => string.Equals(r.Id, preferredId, StringComparison.Ordinal)) is { } pick)
        {
            chosen = pick;
        }
        else if (open.Count == 1)
        {
            chosen = open[0];
        }
        else
        {
            chosen = RankFewestCrafts(s, item, qty, open, path, level, consumer);
        }

        if (open.Count == candidateCount)
        {
            s.Choice[item] = chosen.Id; // only remember choices not restricted by the current path
        }
        return chosen;
    }

    private Recipe RankFewestCrafts(State s, ItemKey item, long qty, List<Recipe> open, PathNode? path, int level, NodeKey? consumer)
    {
        if (_budget <= 0)
        {
            return open.OrderBy(r => Verified(r) ? 0 : 1).ThenBy(r => r.Id, StringComparer.Ordinal).First();
        }
        var scored = new List<(Recipe Recipe, bool Blocked, long Batches)>(open.Count);
        foreach (var r in open)
        {
            var sim = s.Clone();
            ProduceWith(sim, item, qty, r, path, level, consumer);
            scored.Add((r, sim.Blocking > s.Blocking, sim.TotalBatches - s.TotalBatches));
        }
        return scored
            .OrderBy(x => x.Blocked)
            .ThenBy(x => x.Batches)
            .ThenBy(x => Verified(x.Recipe) ? 0 : 1)
            .ThenBy(x => x.Recipe.Id, StringComparer.Ordinal)
            .First().Recipe;
    }

    /// <summary>Looks the item up in the stock once per run (issues + reserve-adjusted stock lot). True when known.</summary>
    private bool Consult(State s, ItemKey item)
    {
        var known = TryStock(item, out var lower, out var stale);
        if (s.Consulted.Add(item))
        {
            if (!known)
            {
                AddIssue(s, PlanIssueKind.UnknownStock, item, null, "stock.unknown");
            }
            else
            {
                if (stale)
                {
                    AddIssue(s, PlanIssueKind.StaleStock, item, null, "stock.stale");
                }
                var available = Math.Max(0, lower - _reserve.GetValueOrDefault(item));
                if (available > 0)
                {
                    s.LotsOf(item).Insert(0, new Lot(null, available));
                }
            }
        }
        return known;
    }

    private bool TryStock(ItemKey item, out long lower, out bool stale)
    {
        lower = 0;
        stale = false;
        if (!_stock.TryGetValue(item, out var e) || e is null)
        {
            return false;
        }
        long? value = e.Count ?? e.Range?.Min;
        if (value is not long v || v < 0)
        {
            return false;
        }
        lower = v;
        stale = (_now is { } now ? e.IsStaleAt(now) : e.Stale) || (_staleSince is { } since && e.ObservedAtUtc <= since);
        return true;
    }

    private static long Take(State s, ItemKey item, long qty, NodeKey consumer)
    {
        if (!s.Pool.TryGetValue(item, out var lots))
        {
            return 0;
        }
        long taken = 0;
        foreach (var lot in lots)
        {
            if (taken == qty)
            {
                break;
            }
            if (lot.Qty == 0 || lot.Source == consumer)
            {
                continue; // a craft never feeds on its own output
            }
            var t = Math.Min(lot.Qty, qty - taken);
            lot.Qty -= t;
            taken += t;
            if (lot.Source is { } source)
            {
                AddEdge(s, source, consumer);
            }
        }
        return taken;
    }

    private static void AddEdge(State s, NodeKey from, NodeKey to)
    {
        if (from != to)
        {
            s.Edges.Add((from, to));
        }
    }

    private static void AddIssue(State s, PlanIssueKind kind, ItemKey item, string? recipeId, string code)
    {
        s.Issues.Add(new PlanIssue(kind, item, recipeId, code));
        if (IsBlocking(kind))
        {
            s.Blocking++;
        }
    }

    private static void AddShortfall(State s, ItemKey item, long missing, ShortfallKind kind)
    {
        s.Shortfalls.Add(new Shortfall(item, missing, kind));
        s.Blocking++;
    }

    private CraftPlan Build(State s)
    {
        var ordered = TopologicalOrder(s);
        var crafts = new List<PlannedCraft>();
        var totalMin = TimeSpan.Zero;
        var totalMax = TimeSpan.Zero;
        var excessive = false;
        var order = 1;

        foreach (var node in ordered)
        {
            var r = node.Recipe;
            if (node.Batches > MaxNodeBatches)
            {
                AddIssue(s, PlanIssueKind.BatchLimited, node.Key.Item, r.Id, "batch.excessive");
                excessive = true;
                continue;
            }
            if (node.Batches > _maxBatch)
            {
                AddIssue(s, PlanIssueKind.BatchLimited, node.Key.Item, r.Id, "batch.split");
            }
            var remaining = node.Batches;
            while (remaining > 0)
            {
                var chunk = Math.Min(remaining, _maxBatch);
                remaining -= chunk;
                var estimate = Estimate(r, chunk);
                totalMin += estimate.Min;
                totalMax += estimate.Max;
                crafts.Add(new PlannedCraft(
                    order++,
                    r.Id,
                    node.Key.Item,
                    checked((int)chunk),
                    LowerBound(r, chunk),
                    checked(chunk * r.OutputQty),
                    Consumes(r, chunk),
                    estimate));
            }
        }

        var issues = new List<PlanIssue>();
        var seen = new HashSet<(PlanIssueKind, ItemKey, string?, string)>();
        foreach (var i in s.Issues)
        {
            if (seen.Add((i.Kind, i.Item, i.RecipeId, i.DetailCode)))
            {
                issues.Add(i);
            }
        }

        var shortfalls = new List<Shortfall>();
        foreach (var group in s.Shortfalls.GroupBy(x => (x.Item, x.Kind)))
        {
            shortfalls.Add(new Shortfall(group.Key.Item, group.Sum(x => x.Missing), group.Key.Kind));
        }

        var committable = !excessive && shortfalls.Count == 0 && !issues.Any(i => IsBlocking(i.Kind));
        return new CraftPlan(crafts, shortfalls, issues, new TimeRange(totalMin, totalMax), committable);
    }

    private static List<Node> TopologicalOrder(State s)
    {
        var indegree = s.NodeOrder.ToDictionary(k => k, _ => 0);
        var successors = s.NodeOrder.ToDictionary(k => k, _ => new List<NodeKey>());
        foreach (var (from, to) in s.Edges)
        {
            if (indegree.ContainsKey(from) && indegree.ContainsKey(to))
            {
                successors[from].Add(to);
                indegree[to]++;
            }
        }

        var ready = new SortedSet<int>(s.NodeOrder.Where(k => indegree[k] == 0).Select(k => s.Nodes[k].Index));
        var byIndex = s.NodeOrder.ToDictionary(k => s.Nodes[k].Index, k => s.Nodes[k]);
        var result = new List<Node>(s.NodeOrder.Count);
        while (ready.Count > 0)
        {
            var index = ready.Min;
            ready.Remove(index);
            var node = byIndex[index];
            result.Add(node);
            foreach (var next in successors[node.Key])
            {
                if (--indegree[next] == 0)
                {
                    ready.Add(s.Nodes[next].Index);
                }
            }
        }

        if (result.Count < s.NodeOrder.Count)
        {
            foreach (var key in s.NodeOrder.Where(k => indegree[k] > 0))
            {
                var node = s.Nodes[key];
                AddIssue(s, PlanIssueKind.Cycle, key.Item, node.Recipe.Id, "cycle.order");
                result.Add(node);
            }
        }
        return result;
    }

    private static List<MaterialRequirement> Consumes(Recipe r, long batches)
    {
        var totals = new Dictionary<ItemKey, long>();
        var order = new List<ItemKey>();
        foreach (var m in r.Materials)
        {
            if (!totals.ContainsKey(m.Item))
            {
                order.Add(m.Item);
                totals[m.Item] = 0;
            }
            totals[m.Item] = checked(totals[m.Item] + (m.Qty * batches));
        }
        return [.. order.Select(i => new MaterialRequirement(i, checked((int)totals[i])))];
    }

    private static TimeRange Estimate(Recipe r, long ticks)
    {
        TimeSpan perMin;
        TimeSpan perMax;
        if (r.MedianTickCadence is { } m && m > TimeSpan.Zero)
        {
            perMin = TimeSpan.FromTicks((long)Math.Round(m.Ticks * LearnedMinFactor));
            perMax = TimeSpan.FromTicks((long)Math.Round(m.Ticks * LearnedMaxFactor));
        }
        else
        {
            perMin = DefaultTickMin;
            perMax = DefaultTickMax;
        }
        return new TimeRange(TimeSpan.FromTicks(checked(perMin.Ticks * ticks)), TimeSpan.FromTicks(checked(perMax.Ticks * ticks)));
    }

    /// <summary>A craft node: one (item, recipe) pair, with its batches aggregated over every demand.</summary>
    private readonly record struct NodeKey(ItemKey Item, string RecipeId);

    private sealed class Node(NodeKey key, Recipe recipe, int index)
    {
        public NodeKey Key { get; } = key;

        public Recipe Recipe { get; } = recipe;

        public int Index { get; } = index;

        public long Batches { get; set; }

        public Node Clone() => new(Key, Recipe, Index) { Batches = Batches };
    }

    /// <summary>Available quantity of an item: from stock (Source null) or surplus of a planned craft.</summary>
    private sealed class Lot(NodeKey? source, long qty)
    {
        public NodeKey? Source { get; } = source;

        public long Qty { get; set; } = qty;

        public Lot Clone() => new(Source, Qty);
    }

    /// <summary>(item, recipe) path for cycle detection (immutable linked list; depth is small).</summary>
    private sealed class PathNode(ItemKey item, string recipeId, PathNode? parent)
    {
        public static bool Contains(PathNode? path, ItemKey item, string recipeId)
        {
            for (var p = path; p is not null; p = p._parent)
            {
                if (p._item == item && string.Equals(p._recipeId, recipeId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private readonly ItemKey _item = item;
        private readonly string _recipeId = recipeId;
        private readonly PathNode? _parent = parent;
    }

    /// <summary>Mutable planning state (cloned for FewestCrafts simulations).</summary>
    private sealed class State
    {
        public Dictionary<ItemKey, List<Lot>> Pool { get; } = [];

        public HashSet<ItemKey> Consulted { get; } = [];

        public Dictionary<NodeKey, Node> Nodes { get; } = [];

        public List<NodeKey> NodeOrder { get; } = [];

        public Dictionary<ItemKey, string> Choice { get; } = [];

        public HashSet<(NodeKey From, NodeKey To)> Edges { get; } = [];

        public List<PlanIssue> Issues { get; } = [];

        public List<Shortfall> Shortfalls { get; } = [];

        public long TotalBatches { get; set; }

        public int Blocking { get; set; }

        public List<Lot> LotsOf(ItemKey item)
        {
            if (!Pool.TryGetValue(item, out var lots))
            {
                Pool[item] = lots = [];
            }
            return lots;
        }

        public Node GetOrAddNode(NodeKey key, Recipe recipe)
        {
            if (!Nodes.TryGetValue(key, out var node))
            {
                node = new Node(key, recipe, NodeOrder.Count);
                Nodes[key] = node;
                NodeOrder.Add(key);
            }
            return node;
        }

        public State Clone()
        {
            var c = new State { TotalBatches = TotalBatches, Blocking = Blocking };
            foreach (var (item, lots) in Pool)
            {
                c.Pool[item] = [.. lots.Select(l => l.Clone())];
            }
            c.Consulted.UnionWith(Consulted);
            foreach (var key in NodeOrder)
            {
                c.Nodes[key] = Nodes[key].Clone();
                c.NodeOrder.Add(key);
            }
            foreach (var (item, id) in Choice)
            {
                c.Choice[item] = id;
            }
            c.Edges.UnionWith(Edges);
            c.Issues.AddRange(Issues);
            c.Shortfalls.AddRange(Shortfalls);
            return c;
        }
    }
}
