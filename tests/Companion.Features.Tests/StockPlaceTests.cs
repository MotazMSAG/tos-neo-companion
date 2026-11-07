using Companion.Contracts.Knowledge;
using Companion.Features.Kit;

namespace Companion.Features.Tests;

/// <summary>
/// Place-confirmed Stock identity (2026-10-15: repeat scans recognised only 13-33 of 72 tiles strictly, the pictures
/// drift up to 10 bits between runs): a drifted picture at the place its item was learned is known without a press; a
/// look-alike shifted into that place by a new item is asked about, never filed under its neighbour.
/// </summary>
[TestClass]
public sealed class StockPlaceTests
{
    private const string Here = "ability@100,200";

    /// <summary>A key with the first <paramref name="bits"/> bits set (Hamming distance to Key(0) = bits).</summary>
    private static string Key(int bits)
    {
        var rest = (bits % 4) switch { 1 => "8", 2 => "c", 3 => "e", _ => string.Empty };
        var hex = new string('f', bits / 4) + rest;
        return "icon:" + hex.PadRight(64, '0');
    }

    private static IconItem Item(string key, long id, string? place) => new(key, id, "Item " + id, DateTimeOffset.UnixEpoch) { Place = place };

    private static bool IsHere(string? place) => place == Here;

    [TestMethod]
    public void ADriftedPicture_AtItsPlace_IsKnownWithoutAPress()
    {
        var learned = new[] { Item(Key(0), 1, Here), Item("icon:" + new string('f', 64), 2, "ability@300,200") };

        Assert.IsNull(StockIcons.Match(Key(8), learned).Item, "8 bits: the strict rule asks");
        var m = StockIcons.MatchAt(Key(8), IsHere, learned);
        Assert.AreEqual((1L, "place", 8), (m.Item?.ItemId, m.Code, m.Distance));
        Assert.IsNull(StockIcons.MatchAt(Key(11), IsHere, learned).Item, "beyond PlacedMaxBits");
    }

    [TestMethod]
    public void ALookAlikeShiftedIntoThePlace_IsAskedAbout()
    {
        // two strawberry cones 5 bits apart; a new item pushed cone 2 into cone 1's place
        var learned = new[] { Item(Key(0), 1, Here), Item(Key(5), 2, "ability@220,200") };

        Assert.IsNull(StockIcons.MatchAt(Key(5), IsHere, learned).Item, "nearer to its own learned picture: never cone 1");
        Assert.AreEqual(1L, StockIcons.MatchAt(Key(1), IsHere, learned).Item?.ItemId, "nearer to cone 1 at cone 1's place");
    }

    [TestMethod]
    public void AnotherPlace_OrNoPlace_NeverCounts()
    {
        var learned = new[] { Item(Key(0), 1, "ability@500,900"), Item(Key(0).Replace("icon:0", "icon:1", StringComparison.Ordinal), 3, null) };

        Assert.IsNull(StockIcons.MatchAt(Key(4), IsHere, learned).Item);
        Assert.IsNull(StockIcons.MatchAt("not a key", _ => true, learned).Item);
    }
}
