using Companion.Contracts.Logs;

namespace Companion.Contracts.Knowledge;

/// <summary>
/// A character, as a salted local hash of the log's login role token (KS-01). The raw server/role id is never stored,
/// journaled or shown. Value = lower-case hex, 16 chars. Since 4.33 it only names the knowledge profile's folder: the
/// companion keeps one profile for every character and never asks which character is logged in.
/// </summary>
/// <param name="Value">Hash value.</param>
public readonly record struct CharacterKey(string Value)
{
    /// <summary>Short form for UI (first 4 chars, upper-case).</summary>
    public string Short => Value.Length >= 4 ? Value[..4].ToUpperInvariant() : Value.ToUpperInvariant();

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Hashes a raw role token with the local salt (unit "knowledge-planner"; salt in RuntimeDir\knowledge, admin-only).</summary>
public interface ICharacterKeyHasher
{
    /// <summary>Returns the salted hash. Must not log or keep the raw token.</summary>
    CharacterKey Hash(string rawRoleToken);
}

/// <summary>
/// An item, keyed by its normalised on-screen name (trim, collapse spaces, case-fold) as read by OCR, logged by the game
/// in an item tip, or typed by the user; or, for a Make-tab material whose name is not known yet, by its icon
/// fingerprint ("icon:" + 64 hex). The game's numeric item id, when the game logged it in an item tip (never mined from
/// client data, REC-01), is kept NEXT to the key (<see cref="StockEntry.ItemId"/>, <see cref="Recipe.ProductItemId"/>,
/// <see cref="MaterialRequirement.ItemId"/>) and wins over the key when both sides know it.
/// </summary>
/// <param name="Value">Normalised name.</param>
public readonly record struct ItemKey(string Value)
{
    /// <summary>Normalises a display name into a key.</summary>
    public static ItemKey FromDisplayName(string name) =>
        new(string.Join(' ', name.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant());

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Inclusive value range (abbreviated or uncertain counts, SCAN-03).</summary>
/// <param name="Min">Lower bound.</param>
/// <param name="Max">Upper bound.</param>
public sealed record ValueRange(long Min, long Max);

/// <summary>Where a stock value came from.</summary>
public enum StockSource
{
    /// <summary>Read from the screen.</summary>
    Observed,
    /// <summary>Computed from craft ticks after the last observation.</summary>
    Derived,
    /// <summary>Typed by the user.</summary>
    UserEntered,
}

/// <summary>Which screen produced an observation.</summary>
public enum ObservationKind
{
    /// <summary>Visible part of the bag (never a full snapshot, SCAN-02).</summary>
    BagPartial,
    /// <summary>Owned count from a crafting-view owned/required pair (screen identity confirmed).</summary>
    CraftViewPair,
    /// <summary>Typed by the user.</summary>
    UserEntered,

    /// <summary>
    /// A count from the Ability view's Stock tab (2026-10-15; screen identity confirmed): the owned count of every item
    /// the game lists there, a faded tile without a count meaning 0.
    /// </summary>
    StockTab,
}

/// <summary>Current stock of one item.</summary>
/// <param name="Item">Item.</param>
/// <param name="Count">Exact count, or null when only a range is known.</param>
/// <param name="Range">Range, when the count is abbreviated/uncertain.</param>
/// <param name="Source">Source.</param>
/// <param name="ObservedAtUtc">Time of the underlying observation.</param>
/// <param name="ObservedAtLog">Log position at capture (derived events apply only after it, STK-02).</param>
/// <param name="Confidence">0..1, synthesised (OCR has no confidence, OCR-01).</param>
/// <param name="Coverage">Which screen/tab/visible range it came from.</param>
/// <param name="Stale">True after any stale mark later than the observation.</param>
/// <param name="ItemId">Optional (schema 2, 2026-10-15): the game's item id, when an item tip logged it.</param>
/// <param name="FreshUntilUtc">Optional (schema 2, 2026-10-15): the count is volatile and trusted only until then (the
/// shared homestead's goods: a homestead partner may collect or use them at any time); afterwards it reads as stale
/// (<see cref="IsStaleAt"/>) until it is observed again. Null = no such limit.</param>
public sealed record StockEntry(
    ItemKey Item,
    long? Count,
    ValueRange? Range,
    StockSource Source,
    DateTimeOffset ObservedAtUtc,
    LogPosition? ObservedAtLog,
    double Confidence,
    string Coverage,
    bool Stale,
    long? ItemId = null,
    DateTimeOffset? FreshUntilUtc = null)
{
    /// <summary>Stale at <paramref name="nowUtc"/>: marked stale, or volatile and past its fresh window.</summary>
    public bool IsStaleAt(DateTimeOffset nowUtc) => Stale || (FreshUntilUtc is { } until && nowUtc > until);
}

/// <summary>An observation to fold into the store.</summary>
/// <param name="Item">Item.</param>
/// <param name="Count">Exact count, if read.</param>
/// <param name="Range">Range, if only a range was read.</param>
/// <param name="Kind">Screen kind.</param>
/// <param name="AtUtc">Capture time.</param>
/// <param name="AtLog">Log position at capture.</param>
/// <param name="Confidence">0..1.</param>
/// <param name="Coverage">Tab / visible range / screen id.</param>
/// <param name="ItemId">Optional (2026-10-15): the game's item id, when an item tip logged it; the store then treats an
/// entry with the same id under another key as the same item.</param>
/// <param name="FreshUntilUtc">Optional (2026-10-15): a volatile count, trusted only until then (shared homestead goods,
/// see <see cref="StockEntry.FreshUntilUtc"/>); not before <paramref name="AtUtc"/>.</param>
public sealed record StockObservation(
    ItemKey Item,
    long? Count,
    ValueRange? Range,
    ObservationKind Kind,
    DateTimeOffset AtUtc,
    LogPosition AtLog,
    double Confidence,
    string Coverage,
    long? ItemId = null,
    DateTimeOffset? FreshUntilUtc = null);

/// <summary>A derived change from craft ticks (applied only if its position is after the item's observation).</summary>
/// <param name="Item">Item.</param>
/// <param name="Delta">Signed change.</param>
/// <param name="EventPosition">Log position of the event that caused it.</param>
/// <param name="CauseCode">Code (e.g. "craft.tick").</param>
public sealed record DerivedStockDelta(ItemKey Item, long Delta, LogPosition EventPosition, string CauseCode);

/// <summary>What a stale mark covers.</summary>
public enum StaleScope
{
    /// <summary>All stock of the character (user played, log rotated).</summary>
    AllStock,
    /// <summary>Food items (autoeat line).</summary>
    Food,
    /// <summary>One item.</summary>
    Item,
}

/// <summary>A material line of a recipe.</summary>
/// <param name="Item">Material.</param>
/// <param name="Qty">Quantity per craft.</param>
/// <param name="ItemId">Optional (schema 2, 2026-10-15): the game's item id of the material, when an item tip logged it.</param>
public sealed record MaterialRequirement(ItemKey Item, int Qty, long? ItemId = null);

/// <summary>How much of a recipe was read with confidence.</summary>
public enum RecipeConfidence
{
    /// <summary>Every field read with agreeing OCR + digit reader, or typed/confirmed by the user.</summary>
    Complete,
    /// <summary>Some fields unknown or OCR-only: planner marks branches needsVerification (PL-02).</summary>
    Partial,
}

/// <summary>A recipe learned only from the screen or typed by the user (never from game files).</summary>
/// <param name="Id">Stable id (product key + station + index).</param>
/// <param name="Product">Product item.</param>
/// <param name="OutputQty">Output per craft (per tick), as observed.</param>
/// <param name="Station">Station / profession code.</param>
/// <param name="Materials">Materials per craft.</param>
/// <param name="Requirements">Other requirements as codes (grade, energy...).</param>
/// <param name="SuccessRate">Observed success rate (enchant-like recipes), or null = always succeeds.</param>
/// <param name="ObservedAtUtc">When read.</param>
/// <param name="SourceCapture">Artifact path of the capture it was read from.</param>
/// <param name="UserVerified">User confirmed every field (required before real commits).</param>
/// <param name="Confidence">Field confidence.</param>
/// <param name="GameBuild">Game build fingerprint when read.</param>
/// <param name="MedianTickCadence">Median is_block interval learned from crafts (PL-01).</param>
/// <param name="Counter">Optional (added 2026-10-14): the small 'a/b' counter the Make tab shows under some products'
/// picture, as last read ("5/300", digits only); null when the screen shows none or it was not read. Informational: it
/// changes as the recipe is used and is not part of the recipe's content.</param>
/// <param name="ProductItemId">Optional (schema 2, 2026-10-15): the game's item id of the product, when the item tip of
/// the product icon logged it (the product name then comes from the same log line, not from OCR). A recipe with an id
/// is the same recipe as any other recipe with that id, whatever its key.</param>
public sealed record Recipe(
    string Id,
    ItemKey Product,
    int OutputQty,
    string Station,
    IReadOnlyList<MaterialRequirement> Materials,
    IReadOnlyList<string> Requirements,
    double? SuccessRate,
    DateTimeOffset ObservedAtUtc,
    string? SourceCapture,
    bool UserVerified,
    RecipeConfidence Confidence,
    string? GameBuild,
    TimeSpan? MedianTickCadence,
    string? Counter = null,
    long? ProductItemId = null);

/// <summary>
/// The game item behind a Make-tab material icon (schema 2, 2026-10-15), learned once from the item tip the game logs
/// when that icon is pressed ('item_tips_mgr ... print name, uuid, id'): the scans never press a known icon again.
/// </summary>
/// <param name="IconKey">The icon fingerprint key ("icon:" + 64 lower-case hex).</param>
/// <param name="ItemId">The game's item id.</param>
/// <param name="Name">The item name as logged (game item data, never chat or personal text).</param>
/// <param name="LearnedUtc">When the tip was logged.</param>
public sealed record IconItem(string IconKey, long ItemId, string Name, DateTimeOffset LearnedUtc)
{
    /// <summary>The item key of <see cref="Name"/> (not stored: derived).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ItemKey Item => ItemKey.FromDisplayName(Name);
}

/// <summary>
/// Item identity (schema 2, 2026-10-15), one rule for the store, the planner and the features: two items are the same
/// when both know the game's item id and the ids are equal; when either id is unknown, when their keys are equal.
/// </summary>
public static class ItemIdentity
{
    /// <summary>The same item: equal ids when both are known, else equal keys.</summary>
    public static bool Same(ItemKey a, long? aId, ItemKey b, long? bId) => aId is { } x && bId is { } y ? x == y : a == b;

    /// <summary>The same material item (the quantity is not compared).</summary>
    public static bool Same(MaterialRequirement a, MaterialRequirement b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Same(a.Item, a.ItemId, b.Item, b.ItemId);
    }

    /// <summary>The same product.</summary>
    public static bool SameProduct(Recipe a, Recipe b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Same(a.Product, a.ProductItemId, b.Product, b.ProductItemId);
    }

    /// <summary>The same materials as a set: every line of one matches a line of the other (same item, same quantity).</summary>
    public static bool SameMaterials(IReadOnlyList<MaterialRequirement> a, IReadOnlyList<MaterialRequirement> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count != b.Count)
        {
            return false;
        }

        var rest = b.ToList();
        foreach (var m in a)
        {
            var i = rest.FindIndex(x => x.Qty == m.Qty && Same(m, x));
            if (i < 0)
            {
                return false;
            }

            rest.RemoveAt(i);
        }

        return true;
    }
}

/// <summary>What <see cref="ICharacterKnowledge.ForgetRecipes"/> did.</summary>
/// <param name="BackupFile">File name (no folder) of the backup written before anything was removed.</param>
/// <param name="Recipes">Recipes removed.</param>
/// <param name="Observations">Stock entries removed.</param>
public sealed record ForgetRecipesReport(string BackupFile, int Recipes, int Observations);

/// <summary>Keep at least <paramref name="Min"/> of an item (evaluated only on an explicit trigger or inside the user's session window, TOS-01).</summary>
/// <param name="Item">Item.</param>
/// <param name="Min">Minimum.</param>
public sealed record KeepStockRule(ItemKey Item, int Min);

/// <summary>Stock view for one character.</summary>
/// <param name="Character">Character.</param>
/// <param name="Items">Entries by item.</param>
/// <param name="StaleSince">Latest AllStock stale mark, if any.</param>
/// <param name="LastAppliedOffset">Last derived-event offset folded in (persisted with the stock, STK-02).</param>
/// <param name="LogFile">Log generation of <paramref name="LastAppliedOffset"/>.</param>
public sealed record StockView(
    CharacterKey Character,
    IReadOnlyDictionary<ItemKey, StockEntry> Items,
    DateTimeOffset? StaleSince,
    long LastAppliedOffset,
    LogFileIdentity? LogFile);

/// <summary>
/// The companion's knowledge (unit "knowledge-planner"): one profile shared by every character (4.33, the user: the
/// per-character binding only got in the way). Writes are atomic (temp in same folder, Flush(true), parse + validate,
/// File.Replace with backups, N generations); only the host process writes. Not thread-safe: the engine thread is the
/// only caller.
/// </summary>
public interface ICharacterKnowledge
{
    /// <summary>The profile's folder key.</summary>
    CharacterKey Character { get; }

    /// <summary>Current stock.</summary>
    StockView GetStock();

    /// <summary>Folds observations (unseen items stay unknown, never 0).</summary>
    void ApplyObservations(IReadOnlyList<StockObservation> observations);

    /// <summary>Applies a derived delta if its position is after the item's observation and after LastAppliedOffset; returns false if skipped.</summary>
    bool ApplyDerived(DerivedStockDelta delta);

    /// <summary>Marks stock stale (user played, autoeat, rotation).</summary>
    void MarkStale(StaleScope scope, ItemKey? item, string reasonCode, DateTimeOffset atUtc);

    /// <summary>Known recipes.</summary>
    IReadOnlyList<Recipe> GetRecipes();

    /// <summary>
    /// Adds or replaces a recipe (by id; a recipe with a product item id also replaces any other recipe with that id).
    /// </summary>
    void UpsertRecipe(Recipe recipe);

    /// <summary>Removes a recipe by id; false when there is none.</summary>
    bool RemoveRecipe(string recipeId);

    /// <summary>The material icons whose game item is known (learned from item tips).</summary>
    IReadOnlyList<IconItem> GetIconItems();

    /// <summary>
    /// Records the item behind an icon (replacing an older entry of that icon) and moves every stock entry and recipe
    /// material keyed by that icon to the item's name key and id (a newer reading of the item wins, as for observations).
    /// </summary>
    void LearnIconItem(IconItem item);

    /// <summary>
    /// For one clean rescan: writes a backup of the whole knowledge file next to it first (nothing is removed when that
    /// fails: the exception propagates), then removes every recipe and every stock entry whose coverage is one of
    /// <paramref name="coverages"/> (the owned counts read from the recipe screens), and saves. Keep-stock rules, other
    /// stock and the learned icon items stay.
    /// </summary>
    ForgetRecipesReport ForgetRecipes(IReadOnlyCollection<string> coverages);

    /// <summary>Keep-stock rules.</summary>
    IReadOnlyList<KeepStockRule> GetKeepStockRules();

    /// <summary>Replaces keep-stock rules (tray UI only).</summary>
    void SetKeepStockRules(IReadOnlyList<KeepStockRule> rules);

    /// <summary>Persists pending changes atomically.</summary>
    void Save();
}

/// <summary>
/// The knowledge store (unit "knowledge-planner"): one profile for every character, whoever is logged in (4.33).
/// Constructor has no side effects; <see cref="Load"/> reads from RuntimeDir\knowledge.
/// </summary>
public interface IKnowledgeStore
{
    /// <summary>Loads the profile (once at host start). Corrupt files fall back to the newest valid backup and are reported.</summary>
    KnowledgeLoadReport Load();

    /// <summary>The one profile (loads it on first use when <see cref="Load"/> was not called).</summary>
    ICharacterKnowledge Profile { get; }
}

/// <summary>Result of loading the store.</summary>
/// <param name="Ok">Loaded without fallbacks.</param>
/// <param name="Characters">Profile folders found (the profile is the first one the old characters list allowed).</param>
/// <param name="RecoveredFromBackup">Files restored from a backup (codes).</param>
public sealed record KnowledgeLoadReport(bool Ok, int Characters, IReadOnlyList<string> RecoveredFromBackup);
