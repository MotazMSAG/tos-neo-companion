using Companion.Contracts.Knowledge;

namespace Companion.Contracts.Planning;

/// <summary>Craft at least <paramref name="Qty"/> of <paramref name="Item"/> (in addition to keep-stock reserves).</summary>
/// <param name="Item">Product.</param>
/// <param name="Qty">Quantity wanted.</param>
public sealed record CraftGoal(ItemKey Item, int Qty);

/// <summary>Which recipe to use when several produce the same item.</summary>
public enum RecipeChoicePolicy
{
    /// <summary>Fewest total crafts (ties: verified first, then id ordinal) — deterministic (PL-02).</summary>
    FewestCrafts,
    /// <summary>Only the recipe named in <see cref="PlannerPolicy.PreferredRecipeByItem"/>, else FewestCrafts.</summary>
    Preferred,
}

/// <summary>Planner settings.</summary>
/// <param name="Choice">Recipe choice policy.</param>
/// <param name="MaxBatchPerCraft">Batch cap per craft step (also capped by the UI max read before commit, PL-01).</param>
/// <param name="MaxDepth">Recursion depth limit.</param>
/// <param name="PreferredRecipeByItem">Item key value to recipe id.</param>
/// <param name="AllowUnverifiedRecipes">False (default): recipes with UserVerified=false or Partial confidence produce needsVerification issues instead of planned crafts.</param>
/// <param name="TrustLogNamedRecipes">
/// Optional (2026-10-15): a recipe stored Complete whose product the game named in an item tip
/// (<see cref="Recipe.ProductItemId"/> known: every field read from the screen, the name and id from the log) counts as
/// verified, as a user-verified one does. Partial recipes, and Complete ones with an OCR-only product name, still need
/// verification. False (default): only user-verified recipes count.
/// </param>
public sealed record PlannerPolicy(
    RecipeChoicePolicy Choice,
    int MaxBatchPerCraft,
    int MaxDepth,
    IReadOnlyDictionary<string, string>? PreferredRecipeByItem,
    bool AllowUnverifiedRecipes = false,
    bool TrustLogNamedRecipes = false)
{
    /// <summary>Defaults: FewestCrafts, batch 99, depth 8, verified recipes only.</summary>
    public static PlannerPolicy Default { get; } = new(RecipeChoicePolicy.FewestCrafts, 99, 8, null);

    /// <summary>
    /// The craft jobs' policy (Craft, KeepStock and the tray's plan preview, 2026-10-15): the defaults, with complete
    /// log-named recipes trusted (<see cref="TrustLogNamedRecipes"/>).
    /// </summary>
    public static PlannerPolicy CraftJobs { get; } = Default with { TrustLogNamedRecipes = true };
}

/// <summary>Planner input (pure function input; no I/O).</summary>
/// <param name="Goals">Goals.</param>
/// <param name="KeepStock">Keep-stock rules: their minimums are reserved and unavailable to goals (PL-02).</param>
/// <param name="Recipes">Known recipes.</param>
/// <param name="Stock">Current stock (missing item = unknown, never 0).</param>
/// <param name="Policy">Policy.</param>
/// <param name="StockStaleSince">Latest stale mark; stale stock makes the plan non-committable (resync first, STK-03).</param>
/// <param name="NowUtc">Optional (2026-10-15): the time the plan is made; a volatile entry past its
/// <see cref="StockEntry.FreshUntilUtc"/> then counts as stale stock. Null = fresh windows are not checked.</param>
public sealed record PlanRequest(
    IReadOnlyList<CraftGoal> Goals,
    IReadOnlyList<KeepStockRule> KeepStock,
    IReadOnlyList<Recipe> Recipes,
    IReadOnlyDictionary<ItemKey, StockEntry> Stock,
    PlannerPolicy Policy,
    DateTimeOffset? StockStaleSince,
    DateTimeOffset? NowUtc = null);

/// <summary>Time range (estimates are ranges, PL-01).</summary>
/// <param name="Min">Lower bound.</param>
/// <param name="Max">Upper bound.</param>
public sealed record TimeRange(TimeSpan Min, TimeSpan Max);

/// <summary>One craft step of a plan.</summary>
/// <param name="Order">Execution order (dependencies first).</param>
/// <param name="RecipeId">Recipe.</param>
/// <param name="Product">Product.</param>
/// <param name="Batches">Number of crafts (ticks) requested.</param>
/// <param name="ExpectedOutputMin">Lower-bound output (success rate applied).</param>
/// <param name="ExpectedOutputMax">Upper-bound output.</param>
/// <param name="Consumes">Total materials consumed.</param>
/// <param name="Estimate">Duration estimate (from learned tick cadence).</param>
public sealed record PlannedCraft(
    int Order,
    string RecipeId,
    ItemKey Product,
    int Batches,
    long ExpectedOutputMin,
    long ExpectedOutputMax,
    IReadOnlyList<MaterialRequirement> Consumes,
    TimeRange Estimate);

/// <summary>Kinds of shortfall.</summary>
public enum ShortfallKind
{
    /// <summary>Raw material (no recipe) missing.</summary>
    RawMaterial,
    /// <summary>Stock of the item is unknown (never seen): resync needed.</summary>
    UnknownStock,
}

/// <summary>Missing material.</summary>
/// <param name="Item">Item.</param>
/// <param name="Missing">Amount missing (lower bound).</param>
/// <param name="Kind">Kind.</param>
public sealed record Shortfall(ItemKey Item, long Missing, ShortfallKind Kind);

/// <summary>Plan issue kinds.</summary>
public enum PlanIssueKind
{
    /// <summary>A cycle on (item, recipe choice) paths.</summary>
    Cycle,
    /// <summary>Recipe unverified or partially read (not trusted by the policy).</summary>
    NeedsVerification,
    /// <summary>Stock unknown for an item the plan needs.</summary>
    UnknownStock,
    /// <summary>Stock stale: resync before committing.</summary>
    StaleStock,
    /// <summary>No recipe for an intermediate that is not in stock.</summary>
    NoRecipe,
    /// <summary>Depth limit hit.</summary>
    DepthExceeded,
    /// <summary>Quantity limited by the batch cap (split into several crafts).</summary>
    BatchLimited,
}

/// <summary>A plan issue.</summary>
/// <param name="Kind">Kind.</param>
/// <param name="Item">Item concerned.</param>
/// <param name="RecipeId">Recipe concerned, if any.</param>
/// <param name="DetailCode">Short code.</param>
public sealed record PlanIssue(PlanIssueKind Kind, ItemKey Item, string? RecipeId, string DetailCode);

/// <summary>Planner output.</summary>
/// <param name="Crafts">Ordered crafts.</param>
/// <param name="Shortfalls">Raw-material and unknown-stock shortfalls.</param>
/// <param name="Issues">Issues.</param>
/// <param name="Estimate">Total duration estimate.</param>
/// <param name="Committable">True only when there are no shortfalls, no Cycle/NeedsVerification/UnknownStock/StaleStock/NoRecipe issues.</param>
public sealed record CraftPlan(
    IReadOnlyList<PlannedCraft> Crafts,
    IReadOnlyList<Shortfall> Shortfalls,
    IReadOnlyList<PlanIssue> Issues,
    TimeRange Estimate,
    bool Committable);

/// <summary>
/// Pure craft planner (unit "knowledge-planner"): recursion with cycle detection on (item, recipe) paths, recipe
/// choice policy, shared intermediates, keep-stock reservations, batch limits, lower-bound yields, deterministic
/// tie-breaks. Re-plan after partial completion = call <see cref="Plan"/> again with resynced stock.
/// </summary>
public interface IPlanner
{
    /// <summary>Builds a plan. Never throws for bad data (reported as issues).</summary>
    CraftPlan Plan(PlanRequest request);
}
