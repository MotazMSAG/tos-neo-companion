using Companion.Contracts.Knowledge;

namespace Companion.Features.Kit;

/// <summary>How a Stock tile's icon key matched the learned icon items.</summary>
/// <param name="Item">The learned item behind the icon, when the match is clear.</param>
/// <param name="Code">"known" (a clear match), "unknown" (no learned icon close enough) or "ambiguous" (a learned icon of
/// another item is nearly as close).</param>
/// <param name="Distance">Bits to the nearest learned icon (int.MaxValue when there is none).</param>
internal readonly record struct StockIconMatch(IconItem? Item, string Code, int Distance);

/// <summary>
/// The Stock tab's icon identity (2026-10-15): the Stock reader's keys ("icon:" + 64 hex, kept far from every Make-tab
/// key by the reader's domain pattern) are matched to the store's learned icon items STRICTLY, because one item family
/// draws its icon in several colours or twice (on the captures two strawberry cones lie 5 bits apart, the closest other
/// pair 15 bits) while a half-pixel resampling of the same tile moved its key by up to 10 bits (a whole-pixel scroll
/// by none):
/// <list type="bullet">
/// <item>known: the nearest learned icon lies within <see cref="SameIconMaxBits"/>, and every learned icon of ANOTHER
/// item lies more than <see cref="RivalMarginBits"/> farther;</item>
/// <item>otherwise the scan asks the game (presses the tile's item tip once per job) instead of guessing; an icon that
/// drifted is learned again under its new key (one more press, never a wrong item).</item>
/// </list>
/// The same strict rule, with its own bounds, identifies the Make tab's list cards without a click (2026-10-15,
/// <see cref="RecipeList.MatchCard"/>: a card's picture is compared at several rows, the best one counts).
/// Pure.
/// </summary>
internal static class StockIcons
{
    /// <summary>Largest distance at which a learned icon is the same icon.</summary>
    internal const int SameIconMaxBits = 2;

    /// <summary>Another item's learned icon must lie more than this many bits farther than the nearest.</summary>
    internal const int RivalMarginBits = 4;

    /// <summary>Largest distance at which a picture learned at the same grid place is the same item (drift is up to 10).</summary>
    internal const int PlacedMaxBits = 10;

    /// <summary>
    /// The place-confirmed match (2026-10-15: live repeat scans recognised only 13-33 of 72 tiles strictly because
    /// pictures drift up to 10 bits between runs): known when a picture learned at the same place
    /// (<paramref name="isSamePlace"/>) lies within <see cref="PlacedMaxBits"/> AND its item is strictly the nearest of all
    /// learned items, so a look-alike shifted into the place by a new item (nearer to its own learned picture) is asked
    /// about, never filed under its neighbour. Code "place"; otherwise "unknown".
    /// </summary>
    internal static StockIconMatch MatchAt(string key, Func<string?, bool> isSamePlace, IReadOnlyList<IconItem> learned)
    {
        ArgumentNullException.ThrowIfNull(isSamePlace);
        ArgumentNullException.ThrowIfNull(learned);
        if (!IconKeys.IsIconKey(key))
        {
            return new StockIconMatch(null, "unknown", int.MaxValue);
        }

        var placed = learned.Where(i => isSamePlace(i.Place))
            .Select(i => (Item: i, Distance: IconKeys.Distance(key, i.IconKey)))
            .Where(c => c.Distance <= PlacedMaxBits)
            .OrderBy(c => c.Distance)
            .ThenBy(c => c.Item.IconKey, StringComparer.Ordinal)
            .FirstOrDefault();
        if (placed.Item is null)
        {
            return new StockIconMatch(null, "unknown", int.MaxValue);
        }

        var rival = learned.Where(i => i.ItemId != placed.Item.ItemId).Select(i => IconKeys.Distance(key, i.IconKey)).DefaultIfEmpty(int.MaxValue).Min();
        return rival > placed.Distance
            ? new StockIconMatch(placed.Item, "place", placed.Distance)
            : new StockIconMatch(null, "unknown", placed.Distance);
    }

    /// <summary>Matches <paramref name="key"/> against <paramref name="learned"/> (see the class remarks).</summary>
    internal static StockIconMatch Match(string key, IReadOnlyList<IconItem> learned) => Match([key], learned, SameIconMaxBits, RivalMarginBits);

    /// <summary>
    /// The strict match with the caller's bounds, for an icon read at one or more alignments (<paramref name="keys"/>):
    /// the distance to a learned icon is the smallest over the keys. Known: the nearest learned icon lies within
    /// <paramref name="sameMaxBits"/> and every learned icon of ANOTHER item more than <paramref name="rivalMarginBits"/>
    /// farther; otherwise unknown (nothing close enough, or no valid key) or ambiguous.
    /// </summary>
    internal static StockIconMatch Match(IReadOnlyList<string> keys, IReadOnlyList<IconItem> learned, int sameMaxBits, int rivalMarginBits)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(learned);
        var valid = keys.Where(IconKeys.IsIconKey).ToList();
        if (valid.Count == 0)
        {
            return new StockIconMatch(null, "unknown", int.MaxValue);
        }

        int DistanceTo(IconItem item) => valid.Min(k => IconKeys.Distance(k, item.IconKey));
        IconItem? best = null;
        var bestDistance = int.MaxValue;
        foreach (var item in learned.OrderBy(i => i.IconKey, StringComparer.Ordinal))
        {
            var d = DistanceTo(item);
            if (d < bestDistance)
            {
                best = item;
                bestDistance = d;
            }
        }

        if (best is null || bestDistance > sameMaxBits)
        {
            return new StockIconMatch(null, "unknown", bestDistance);
        }

        var rival = learned.Where(i => i.ItemId != best.ItemId).Select(DistanceTo).DefaultIfEmpty(int.MaxValue).Min();
        return rival <= bestDistance + rivalMarginBits
            ? new StockIconMatch(null, "ambiguous", bestDistance)
            : new StockIconMatch(best, "known", bestDistance);
    }
}
