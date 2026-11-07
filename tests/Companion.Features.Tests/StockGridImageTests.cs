using Companion.Contracts;
using Companion.Contracts.Vision;
using Companion.Features.Kit;

namespace Companion.Features.Tests;

/// <summary>
/// The Stock scan's image checks on the real captures (read-only, the tests' own copies in tests\TestData\images): the
/// Ability sub-tab at its top and after 1, 2 and 3 drags of 300 px up at (940,700) (2026-10-15 02:34-02:35), and the
/// Homestead sub-tab with its footer.
/// </summary>
[TestClass]
public sealed class StockGridImageTests
{
    public TestContext TestContext { get; set; } = null!;

    private static Frame Capture(string name) => RecipeListImageTests.Capture(name);

    [TestMethod]
    public void TheShiftOfEachLiveDrag_IsMeasured_AndNoneSkippedARow()
    {
        string[] frames = ["stock_ability_top_full.png", "stock_ability_s1_full.png", "stock_ability_s2_full.png", "stock_ability_s3_full.png"];
        int[] expected = [-326, -334, -377];
        for (var i = 0; i < expected.Length; i++)
        {
            var shift = StockGrid.MeasureShift(Capture(frames[i]), Capture(frames[i + 1]));
            TestContext.WriteLine($"{frames[i]} -> {frames[i + 1]}: {shift}");
            Assert.IsTrue(shift.Known && !shift.Unchanged, shift.ToString());
            Assert.AreEqual(expected[i], shift.Dy, frames[i + 1]);
            Assert.IsLessThanOrEqualTo(StockGrid.SafeShift, -shift.Dy, "a 300 px drag moved the grid up to 377 px: still no row skipped");
        }

        // the same capture twice: the grid did not move (the end of the list)
        foreach (var f in new[] { "stock_ability_top_full.png", "stock_homestead_full.png", "stock_tailor_full.png" })
        {
            Assert.IsTrue(StockGrid.MeasureShift(Capture(f), Capture(f)).Unchanged, f);
        }

        // the scan drags 240 px: even with the largest overshoot seen (377 / 300) it stays under the safe shift
        Assert.IsLessThanOrEqualTo(StockGrid.SafeShift, (int)Math.Ceiling(240 * 377 / 300.0));
    }

    [TestMethod]
    public void TheForbiddenAreas_CoverTheHomesteadFooter_AndNoTileOfTheGrid()
    {
        var f = Capture("stock_homestead_full.png");

        // "Expand Inventory" (a green button, about (1357..1535, 843..900)) and its red dot lie inside a forbidden area
        var expand = new RectI(1357, 843, 1535, 900);
        Assert.IsTrue(StockGrid.ForbiddenAreas.Any(a => a.Id == "expand.inventory" && a.Rect.Intersect(expand) == expand));
        var green = 0;
        for (var y = expand.Top; y < expand.Bottom; y += 4)
        {
            for (var x = expand.Left; x < expand.Right; x += 4)
            {
                var o = (y * f.Stride) + (x * 4);
                green += f.Bgra[o + 1] > f.Bgra[o + 2] + 15 ? 1 : 0;
            }
        }

        Assert.IsGreaterThan(200, green, "the button really is there on the capture");

        // the footer starts below the grid's area: the grid's last whole row ends at 757, the clip line at 815
        Assert.IsTrue(StockGrid.ForbiddenAreas.All(a => a.Rect.Top >= StockGrid.Viewport.Bottom));
        Assert.IsFalse(StockGrid.Pressable(new PointI(1446, 872)));
        Assert.IsFalse(StockGrid.Pressable(new PointI(1627, 940)), "Treebond Warehouse");
        Assert.IsTrue(StockGrid.Pressable(StockGrid.PressPoint(new RectI(1411, 638, 1526, 757))), "the last whole row of the capture");
    }
}
