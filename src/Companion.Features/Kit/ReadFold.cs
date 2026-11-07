using Companion.Contracts.Knowledge;
using Companion.Contracts.Perception;

namespace Companion.Features.Kit;

/// <summary>
/// Turns screen reads into knowledge (stock observations, recipes). Readers are data; the screens they read are not
/// surveyed yet, so this fixes the REGION ID CONVENTION a surveyed reader must follow for the features to use it:
/// <list type="bullet">
/// <item><c>name.&lt;k&gt;</c> (Text): display name of item k;</item>
/// <item><c>count.&lt;k&gt;</c> (Int, ItemCellCount or Abbreviated): count of item k (bag grid);</item>
/// <item><c>pair.&lt;k&gt;</c> (Pair, OwnedRequired): owned / required of material k (crafting view);</item>
/// <item><c>product.name</c> (Text) and <c>product.qty</c> (Int): the recipe product and its output per craft;</item>
/// <item><c>quantity</c> (Int): requested craft quantity (craft.quantity);</item>
/// <item><c>products</c> (Pair, StoredCapacity): Lumi's Hut products stored / capacity (lumi.products);</item>
/// <item><c>icon.&lt;k&gt;</c> (IconFingerprint): identity of material k when the screen shows no name (craft.pairs on
/// the Make tab): Text = an <see cref="IconKeys"/> key, Ok without text = slot k is empty;</item>
/// <item><c>product.owned</c> (Int): the owned count of the recipe product (craft.product).</item>
/// <item><c>grid</c> (IconGrid, 2026-10-15) with <c>icon.&lt;k&gt;</c> and <c>count.&lt;k&gt;</c>: the Stock tab's whole tiles
/// (the inventory feature's ScanStock folds them itself: identity from the learned icon items or the item tip).</item>
/// </list>
/// Only values with status Ok are used; anything else stays unknown (never a guess, never 0).
/// </summary>
internal static class ReadFold
{
    /// <summary>Station code of recipes read from the Ability (lifeskill) view; also used by the recipes feature.</summary>
    public const string AbilityStation = "lifeskill";

    /// <summary>Region id of the product owned count (craft.product).</summary>
    public const string ProductOwnedRegion = "product.owned";

    /// <summary>Region id of the requested quantity (craft.quantity).</summary>
    public const string QuantityRegion = "quantity";

    /// <summary>Region id of Lumi's Hut products (lumi.products).</summary>
    public const string ProductsRegion = "products";

    /// <summary>Region id of the recipe product name.</summary>
    public const string ProductNameRegion = "product.name";

    /// <summary>Region id of the recipe output per craft.</summary>
    public const string ProductQtyRegion = "product.qty";

    /// <summary>
    /// Stock observations from name.k + count.k (bag) or name.k + pair.k (owned side, crafting view). Requires the
    /// read's log position (derived deltas are ordered against it, STK-02); without it nothing is folded.
    /// </summary>
    public static IReadOnlyList<StockObservation> Observations(ScreenReadResult read, ObservationKind kind, string coverage)
    {
        var result = new List<StockObservation>();

        // Unknown overall = some values unknown: the Ok ones are still folded (each value is checked below).
        if (read.LogAt is null || read.Status is ReadStatus.NeedsSurvey or ReadStatus.ScreenMismatch)
        {
            return result;
        }

        var byId = ById(read);
        foreach (var (suffix, name) in Names(byId))
        {
            var item = ItemKey.FromDisplayName(name.Text!);
            if (byId.TryGetValue("count." + suffix, out var count) && count.Status == ReadStatus.Ok)
            {
                if (count.Value is { } exact && exact >= 0)
                {
                    result.Add(new StockObservation(item, exact, null, kind, read.CapturedUtc, read.LogAt, Math.Min(name.Confidence, count.Confidence), coverage));
                }
                else if (count.Range is { } range && range.Min >= 0 && range.Max >= range.Min)
                {
                    result.Add(new StockObservation(item, null, range, kind, read.CapturedUtc, read.LogAt, Math.Min(name.Confidence, count.Confidence), coverage));
                }

                continue;
            }

            if (byId.TryGetValue("pair." + suffix, out var pair) && pair.Status == ReadStatus.Ok && pair.Value is { } owned && owned >= 0)
            {
                result.Add(new StockObservation(item, owned, null, kind, read.CapturedUtc, read.LogAt, Math.Min(name.Confidence, pair.Confidence), coverage));
            }
        }

        return result;
    }

    /// <summary>
    /// A recipe from product.name, product.qty and name.k + pair.k (required side). Never UserVerified (the user
    /// confirms in the tray); Partial unless every field was read Ok. Null when the product name was not read.
    /// </summary>
    public static Recipe? RecipeFrom(ScreenReadResult read, string station, DateTimeOffset nowUtc)
    {
        if (read.Status is ReadStatus.NeedsSurvey or ReadStatus.ScreenMismatch)
        {
            return null;
        }

        var byId = ById(read);
        if (!byId.TryGetValue(ProductNameRegion, out var productName) || productName.Status != ReadStatus.Ok || string.IsNullOrWhiteSpace(productName.Text))
        {
            return null;
        }

        var complete = read.Status == ReadStatus.Ok;
        var outputQty = 1;
        if (byId.TryGetValue(ProductQtyRegion, out var qty) && qty.Status == ReadStatus.Ok && qty.Value is { } q && q is >= 1 and <= int.MaxValue)
        {
            outputQty = (int)q;
        }
        else
        {
            complete = false;
        }

        var materials = new List<MaterialRequirement>();
        foreach (var (suffix, name) in Names(byId))
        {
            if (byId.TryGetValue("pair." + suffix, out var pair) && pair.Status == ReadStatus.Ok && pair.Second is { } required && required is >= 1 and <= int.MaxValue)
            {
                materials.Add(new MaterialRequirement(ItemKey.FromDisplayName(name.Text!), (int)required));
            }
            else
            {
                complete = false;
            }
        }

        if (materials.Count == 0)
        {
            complete = false;
        }

        var product = ItemKey.FromDisplayName(productName.Text!);
        return new Recipe(
            RecipeId(product, station),
            product,
            outputQty,
            station,
            materials,
            [],
            null,
            nowUtc,
            null,
            false,
            complete ? RecipeConfidence.Complete : RecipeConfidence.Partial,
            null,
            null);
    }

    /// <summary>Stable recipe id: product key + station + index 0 (one recipe per product per station until surveyed otherwise).</summary>
    public static string RecipeId(ItemKey product, string station) => product.Value + "|" + station + "|0";

    /// <summary>The Int value of a region, if read Ok.</summary>
    public static long? IntValue(ScreenReadResult read, string regionId)
    {
        foreach (var v in read.Values)
        {
            if (string.Equals(v.RegionId, regionId, StringComparison.Ordinal))
            {
                return v.Status == ReadStatus.Ok ? v.Value : null;
            }
        }

        return null;
    }

    /// <summary>A region value if read Ok.</summary>
    public static ReadValue? OkValue(ScreenReadResult read, string regionId)
    {
        foreach (var v in read.Values)
        {
            if (string.Equals(v.RegionId, regionId, StringComparison.Ordinal))
            {
                return v.Status == ReadStatus.Ok ? v : null;
            }
        }

        return null;
    }

    private static Dictionary<string, ReadValue> ById(ScreenReadResult read)
    {
        var d = new Dictionary<string, ReadValue>(StringComparer.Ordinal);
        foreach (var v in read.Values)
        {
            d.TryAdd(v.RegionId, v);
        }

        return d;
    }

    private static IEnumerable<(string Suffix, ReadValue Name)> Names(Dictionary<string, ReadValue> byId)
    {
        foreach (var (id, v) in byId.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (id.StartsWith("name.", StringComparison.Ordinal) && id.Length > 5
                && v.Status == ReadStatus.Ok && !string.IsNullOrWhiteSpace(v.Text))
            {
                yield return (id[5..], v);
            }
        }
    }
}
