using System.Globalization;
using Companion.Contracts.Knowledge;
using Companion.Features.Kit;

namespace Companion.Feature.Crafting;

/// <summary>
/// Stores what one selected recipe showed on the Make tab (ScanFavorites and ScanRecipeList): a recipe per product
/// ("&lt;product key&gt;|lifeskill|0", output 1, materials keyed by their item when the icon's item is known, else by
/// their icon, the "Requires" line as the requirement code "requires:&lt;required&gt;", the counter under the product
/// picture as <see cref="Recipe.Counter"/>; never userVerified) and the owned counts (product and every material) as
/// stock observations, with the game's item ids where an item tip logged them (2026-10-15). A partial reading never
/// replaces a stored complete recipe. A product named from the log replaces, instead of duplicating, a stored recipe
/// with an OCR-only name (no product item id) for the same card or slot, or with the same materials. Atomic save after
/// each recipe.
/// </summary>
internal static class ScreenRecipes
{
    /// <summary>Recipe source prefix of the My Favorites scan.</summary>
    internal const string FavoritesSourcePrefix = "screen:craft.fav/";

    /// <summary>Requirement code of the "Requires: owned/required" line: "requires:" + required per craft.</summary>
    internal const string RequiresCodePrefix = "requires:";

    /// <summary>Coverage code of the product's owned count.</summary>
    internal const string ProductCoverage = "ability:craft.product";

    /// <summary>Coverage code of the materials' owned counts.</summary>
    internal const string PairsCoverage = "ability:craft.pairs";

    /// <summary>
    /// Stores one reading. With <paramref name="keepFavorites"/> (the list scan) a recipe whose source is a My Favorites
    /// slot is left as it is (code "recipe.keptFavorite"): the tray maps CraftLoop's slots from that source, and
    /// ScanFavorites keeps it current. The owned counts are always observed.
    /// </summary>
    internal static StoreOutcome Store(ICharacterKnowledge knowledge, FavoriteRead read, string source, bool keepFavorites)
    {
        var observations = Observations(read);
        var code = "unreadable";
        var changed = false;
        var materials = 0;
        var replaced = 0;
        string? recipeId = null;
        if (read.Product is { } p)
        {
            var list = read.Materials
                .Where(m => m.Item is not null && m.Required is >= 1 and <= int.MaxValue)
                .GroupBy(m => m.Item!.Value)
                .Select(g => new MaterialRequirement(g.Key, (int)g.First().Required!.Value, g.First().ItemId))
                .ToList();
            materials = list.Count;
            if (list.Count == 0)
            {
                code = "noMaterials";
            }
            else
            {
                var recipe = new Recipe(
                    ReadFold.RecipeId(p, ReadFold.AbilityStation),
                    p,
                    1, // one item per craft tick (live run 2026-10-14: one UI_ITEM_GET_TIP per is_block)
                    ReadFold.AbilityStation,
                    list,
                    read.Requires is { } r ? [RequiresCodePrefix + r.B.ToString(CultureInfo.InvariantCulture)] : [],
                    null,
                    read.PairsUtc,
                    source,
                    false,
                    read.IsComplete && list.Count == read.Materials.Count ? RecipeConfidence.Complete : RecipeConfidence.Partial,
                    null,
                    null,
                    read.Counter is { } c ? c.A.ToString(CultureInfo.InvariantCulture) + "/" + c.B.ToString(CultureInfo.InvariantCulture) : null,
                    read.ProductItemId);
                var stored = knowledge.GetRecipes();
                var existing = stored.FirstOrDefault(x => string.Equals(x.Id, recipe.Id, StringComparison.Ordinal))
                    ?? (recipe.ProductItemId is { } id ? stored.FirstOrDefault(x => x.ProductItemId == id) : null);
                recipeId = existing?.Id ?? recipe.Id;
                if (existing is { ProductItemId: not null } && read.ProductItemId is null && Contradicts(read, existing))
                {
                    // incident 29 (2026-10-28): an OCR-only title names a log-identified recipe the panel's materials do not
                    // fit (the Longevity Sashimi panel read "Sashimi"): neither the recipe, its slot nor its owned count
                    // is this panel's; a slot it claims is given up
                    code = "recipe.nameConflict";
                    recipeId = null;
                    observations = Observations(read with { Product = null });
                    if (source.StartsWith(FavoritesSourcePrefix, StringComparison.Ordinal) && string.Equals(existing.SourceCapture, source, StringComparison.Ordinal))
                    {
                        knowledge.UpsertRecipe(existing with { SourceCapture = FormerFavoritesSourcePrefix + source[FavoritesSourcePrefix.Length..] });
                        changed = true;
                    }
                }
                else if (existing is not null && recipe.Confidence == RecipeConfidence.Partial && existing.Confidence == RecipeConfidence.Complete)
                {
                    code = "recipe.keptComplete"; // never replace a complete recipe by a partial reading
                    if (!keepFavorites && source.StartsWith(FavoritesSourcePrefix, StringComparison.Ordinal))
                    {
                        // the favourites scan saw it in this slot now: the complete content stays, the slot, the time and
                        // the logged id move with it (live 2026-10-18: three blurry reads kept their old slots, so the
                        // window took them as outdated and greyed out Start)
                        changed = !string.Equals(existing.SourceCapture, source, StringComparison.Ordinal);
                        knowledge.UpsertRecipe(existing with { SourceCapture = source, ObservedAtUtc = read.PairsUtc, ProductItemId = read.ProductItemId ?? existing.ProductItemId });
                    }
                }
                else if (keepFavorites && existing?.SourceCapture?.StartsWith(FavoritesSourcePrefix, StringComparison.Ordinal) == true)
                {
                    code = "recipe.keptFavorite";
                }
                else
                {
                    if (recipe.ProductItemId is not null)
                    {
                        // the name came from the log: an OCR-only recipe for the same card or slot, or with the same
                        // materials, is this recipe read with a garbage title ("ams", "i pizza"): replaced, not duplicated
                        foreach (var old in stored.Where(x => !ReferenceEquals(x, existing) && IsOcrDuplicate(x, recipe, keepFavorites)))
                        {
                            replaced += knowledge.RemoveRecipe(old.Id) ? 1 : 0;
                        }
                    }

                    changed = existing is null || replaced > 0 || !SameContent(existing, recipe);
                    knowledge.UpsertRecipe(recipe);
                    code = recipe.Confidence == RecipeConfidence.Complete ? "recorded" : "recorded.partial";
                }
            }
        }

        if (observations.Count > 0)
        {
            knowledge.ApplyObservations(observations);
        }

        knowledge.Save();
        return new StoreOutcome(code, code is "recorded" or "recorded.partial", changed, observations.Count, materials, replaced, recipeId);
    }

    /// <summary>Source prefix of a recipe that left My Favorites: no slot any more (the craft job finds it in the Make list).</summary>
    internal const string FormerFavoritesSourcePrefix = "screen:craft.fav.old/";

    /// <summary>
    /// After a complete favourites scan: every recipe that still claims a favourites slot but was not seen in this scan
    /// (<paramref name="seen"/>, recipe ids) gives the claim up (source "screen:craft.fav.old/&lt;slot&gt;"); a claim on a
    /// slot the scan could not name (<paramref name="unread"/>) stays. Saves when anything changed; returns the count
    /// (2026-10-18: removed and moved favourites kept their old slots for days).
    /// </summary>
    internal static int ReleaseFavorites(ICharacterKnowledge knowledge, IReadOnlySet<string> seen, IReadOnlySet<int> unread)
    {
        var released = 0;
        foreach (var r in knowledge.GetRecipes().ToList())
        {
            if (r.SourceCapture is not { } s || !s.StartsWith(FavoritesSourcePrefix, StringComparison.Ordinal) || seen.Contains(r.Id)
                || !int.TryParse(s.AsSpan(FavoritesSourcePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var slot) || unread.Contains(slot))
            {
                continue;
            }

            knowledge.UpsertRecipe(r with { SourceCapture = FormerFavoritesSourcePrefix + slot.ToString(CultureInfo.InvariantCulture) });
            released++;
        }

        if (released > 0)
        {
            knowledge.Save();
        }

        return released;
    }

    /// <summary>
    /// A craft found another dish in favourites slot <paramref name="slot"/> twice (incident 29, 2026-10-28: slot 4 held
    /// Longevity Sashimi, the store still gave it to Sashimi): the recipe that claims the slot gives the claim up (source
    /// "screen:craft.fav.old/&lt;slot&gt;"), unless it is the dish the game logged (<paramref name="seenId"/>, or the same
    /// dish at another grade, <paramref name="seenName"/>). The dish seen does not take the slot: an empty slot also
    /// leaves the previous selection shown; the favourites scan names the slots. The released recipe id, or null.
    /// </summary>
    internal static string? ReleaseSlot(ICharacterKnowledge knowledge, int slot, long seenId, ItemKey? seenName)
    {
        var source = FavoritesSourcePrefix + slot.ToString(CultureInfo.InvariantCulture);
        var claims = knowledge.GetRecipes().Where(r => string.Equals(r.SourceCapture, source, StringComparison.Ordinal)).ToList();
        if (claims.Count == 0 || claims.Any(r => r.ProductItemId == seenId || (seenName is { } name && r.Product == name)))
        {
            return null;
        }

        foreach (var r in claims)
        {
            knowledge.UpsertRecipe(r with { SourceCapture = FormerFavoritesSourcePrefix + slot.ToString(CultureInfo.InvariantCulture) });
        }

        knowledge.Save();
        return claims[0].Id;
    }

    /// <summary>
    /// The panel shows another recipe than <paramref name="recipe"/> (incident 29, 2026-10-28: the title OCR read
    /// "Sashimi" on the Longevity Sashimi panel, 4 material icons where Sashimi has 1, and the favourites scan gave slot 4
    /// to Sashimi): more material icons than the recipe has materials, another number of materials when every tile was
    /// read, or a material the game identified that the recipe does not use. Amounts are not compared (a misread digit is
    /// no proof); a panel with no materials read contradicts nothing.
    /// </summary>
    internal static bool Contradicts(FavoriteRead read, Recipe recipe)
    {
        if (read.Materials.Count == 0 || recipe.Materials.Count == 0)
        {
            return false;
        }

        if (read.Materials.Count(m => m.IconKey is not null) > recipe.Materials.Count
            || (read.UnknownSlots == 0 && read.Materials.Count != recipe.Materials.Count))
        {
            return true;
        }

        return read.Materials.Any(m => m.ItemId is { } id
            && !recipe.Materials.Any(r => r.ItemId == id || (r.ItemId is null && m.Item is { } key && r.Item == key)));
    }

    /// <summary>
    /// The owned counts one reading shows, as stock observations (the product's badge and every material whose item and
    /// owned count were read), with the game's item ids where known.
    /// </summary>
    internal static List<StockObservation> Observations(FavoriteRead read)
    {
        var observations = new List<StockObservation>();
        if (read.Product is { } product && read.ProductOwned is { } owned && read.ProductAt is { } at)
        {
            observations.Add(new StockObservation(product, owned, null, ObservationKind.CraftViewPair, read.ProductUtc, at, read.ProductOwnedConfidence, ProductCoverage, read.ProductItemId));
        }

        if (read.PairsAt is { } pairsAt)
        {
            foreach (var m in read.Materials)
            {
                if (m.Item is { } item && m.Owned is { } o)
                {
                    observations.Add(new StockObservation(item, o, null, ObservationKind.CraftViewPair, read.PairsUtc, pairsAt, m.Confidence, PairsCoverage, m.ItemId));
                }
            }
        }

        return observations;
    }

    /// <summary>
    /// Folds only the owned counts of one reading into the store (the craft flow, 2026-10-15: the panel of the recipe it
    /// is about to craft; the recipe itself is left as stored). Saves when anything was observed; returns the count.
    /// </summary>
    internal static int Observe(ICharacterKnowledge knowledge, FavoriteRead read)
    {
        var observations = Observations(read);
        if (observations.Count > 0)
        {
            knowledge.ApplyObservations(observations);
            knowledge.Save();
        }

        return observations.Count;
    }

    /// <summary>
    /// A stored recipe with an OCR-only name (no product item id, another id) that is the same screen recipe as
    /// <paramref name="named"/>: read from the same card or favourites slot, or with the same materials (item and
    /// quantity, as a set). The list scan never touches a favourites recipe (the tray maps CraftLoop's slots from it).
    /// </summary>
    internal static bool IsOcrDuplicate(Recipe stored, Recipe named, bool keepFavorites)
    {
        if (stored.ProductItemId is not null || string.Equals(stored.Id, named.Id, StringComparison.Ordinal)
            || !string.Equals(stored.Station, named.Station, StringComparison.Ordinal))
        {
            return false;
        }

        if (keepFavorites && stored.SourceCapture?.StartsWith(FavoritesSourcePrefix, StringComparison.Ordinal) == true)
        {
            return false;
        }

        return (stored.SourceCapture is { } s && string.Equals(s, named.SourceCapture, StringComparison.Ordinal))
            || (stored.Materials.Count > 0 && ItemIdentity.SameMaterials(stored.Materials, named.Materials));
    }

    /// <summary>Same product, output, station, materials (as a set, by item identity) and requirements; the counter is not content.</summary>
    internal static bool SameContent(Recipe a, Recipe b) =>
        ItemIdentity.SameProduct(a, b)
        && a.OutputQty == b.OutputQty
        && string.Equals(a.Station, b.Station, StringComparison.Ordinal)
        && ItemIdentity.SameMaterials(a.Materials, b.Materials)
        && a.Requirements.SequenceEqual(b.Requirements, StringComparer.Ordinal);

    /// <summary>What storing one reading did.</summary>
    /// <param name="Code">recorded, recorded.partial, recipe.keptComplete, recipe.keptFavorite, recipe.nameConflict, noMaterials or unreadable.</param>
    /// <param name="Recorded">A recipe was written.</param>
    /// <param name="Changed">Its content differs from the stored one (or it is new, or it replaced an OCR-only one).</param>
    /// <param name="Observed">Stock observations applied.</param>
    /// <param name="Materials">Materials in the recipe.</param>
    /// <param name="Replaced">Stored recipes with an OCR-only name it replaced.</param>
    /// <param name="RecipeId">The stored recipe this reading is (written or kept), or null when no product was read.</param>
    internal sealed record StoreOutcome(string Code, bool Recorded, bool Changed, int Observed, int Materials, int Replaced = 0, string? RecipeId = null);
}
