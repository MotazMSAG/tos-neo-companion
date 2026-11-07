using Companion.Contracts;
using Companion.Perception.Internal;

namespace Companion.Perception.Tests;

/// <summary>
/// The Gathering Helper's material list (2026-10-15): the Stock tab's icon grid detector with the helper's tile sizes
/// (<see cref="IconGridGeometry.Helper"/>, about 98x100 tiles) finds every whole tile of the full-scale captures
/// (tests\TestData\images, copies of proofs\out) and no cut one; the Stock geometry finds none there; the helper keys
/// lie far from the Stock keys of the same screen. Read-only.
/// </summary>
[TestClass]
public sealed class HelperGridCaptureTests
{
    /// <summary>The list's visible area (client): below the queue's title row, above the list's bottom clip.</summary>
    internal static readonly RectI ListRegion = new(318, 530, 992, 908);

    [TestMethod]
    [DataRow("gather_helper_full.png", 10, 2)]
    [DataRow("gather_helper_s1_full.png", 9, 2)]
    [DataRow("gather_helper_s2_full.png", 10, 2)]
    public void EveryWholeTileIsFound(string file, int tiles, int rows)
    {
        var frame = BuiltInFontTests.Capture(file);
        var grid = IconGrid.Detect(frame, ListRegion, IconGridGeometry.Helper);
        var raw = string.Join(" ", IconGrid.FindTiles(frame, ListRegion, IconGridGeometry.Helper).Select(b => $"({b.Left},{b.Top},{b.Width}x{b.Height})"));

        Assert.AreEqual("tiles", grid.Code, file + ": " + raw);
        Assert.AreEqual(tiles, grid.Tiles.Count, raw);
        Assert.AreEqual(rows, grid.Tiles.Select(t => t.Box.Top / 20).Distinct().Count(), "rows");
        Assert.IsTrue(grid.Tiles.All(t => t.Box.Width is >= 92 and <= 104 && t.Box.Height is >= 94 && t.Box.Height <= 106), string.Join(" ", grid.Tiles.Select(t => t.Box)));
        Assert.IsTrue(grid.Tiles.All(t => t.Count is null), "the helper's counts are not read");
        Assert.HasCount(tiles, grid.Tiles.Select(t => t.Key).Distinct().ToList(), "distinct pictures");
    }

    [TestMethod]
    public void GreyedTilesAreFaded()
    {
        // at their stock limit (the game ignores a press on them): row 1 tiles 2-4 (Lv50 300, 500, 500), row 2 tile 2 (Lv30 300)
        var grid = IconGrid.Detect(BuiltInFontTests.Capture("gather_helper_full.png"), ListRegion, IconGridGeometry.Helper);
        CollectionAssert.AreEqual(
            new[] { false, true, true, true, false, false, true, false, false, false },
            grid.Tiles.Select(t => t.Faded).ToArray());
    }

    [TestMethod]
    public void TheGrapeTileWithFourOwned_IsInColour()
    {
        // live 2026-10-22 (the user's screenshot, aligned to the client): My Favorites, Scarlet Grape owned 4, in colour
        // (row 2 tile 1); the companion had journaled it "at limit"
        var grid = IconGrid.Detect(BuiltInFontTests.Capture("gather_helper_grape4_full.png"), ListRegion, IconGridGeometry.Helper);
        var dump = string.Join(" | ", grid.Tiles.Select(t => $"({t.Box.Left},{t.Box.Top}) faded={t.Faded} {t.Key}"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "grape4_tiles.txt"), dump);
        Assert.AreEqual("tiles", grid.Code, dump);
    }

    [TestMethod]
    public void TheStockGeometryFindsNoHelperTile_AndTheKeysDifferByDomain()
    {
        var frame = BuiltInFontTests.Capture("gather_helper_full.png");
        Assert.IsEmpty(IconGrid.Detect(frame, ListRegion).Tiles);

        var tile = IconGrid.Detect(frame, ListRegion, IconGridGeometry.Helper).Tiles[0];
        var asStock = IconGrid.Key(frame, tile.Box);
        Assert.IsGreaterThan(100, Bits(tile.Key, asStock), "another salt: never merged with Stock keys");
    }

    private static int Bits(string a, string b) =>
        Convert.FromHexString(a["icon:".Length..]).Zip(Convert.FromHexString(b["icon:".Length..]), (x, y) => System.Numerics.BitOperations.PopCount((uint)(x ^ y))).Sum();
}
