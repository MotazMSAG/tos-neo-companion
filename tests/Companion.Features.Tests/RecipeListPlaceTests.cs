using Companion.Contracts;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Safety;
using Companion.Features.Kit;

namespace Companion.Features.Tests;

/// <summary>
/// The pure helpers of the faster find walk (2026-10-16): a list card's remembered place, the hints a find jumps to, the
/// short-move rule for the list top and the length of a jump drag, with the numbers the live journal of 2026-10-15 showed
/// (a 280 px drag moved the list 263..266 px; every first top drag after the open moved 31 px; Roast Chicken at list
/// row 2021 took five scroll drags).
/// </summary>
[TestClass]
public sealed class RecipeListPlaceTests
{
    private static IconItem Card(long id, string name, string? place) =>
        new("icon:" + new string('a', 64), id, name, DateTimeOffset.UnixEpoch) { Place = place };

    [TestMethod]
    public void APlace_IsTheColumnAndTheListRow_AndOnlyListPlacesParse()
    {
        Assert.AreEqual("craft.list@2,2021", RecipeList.PlaceOf(2, 2021));
        Assert.AreEqual((2, 2021), RecipeList.ParsePlace("craft.list@2,2021"));
        Assert.AreEqual((0, -15), RecipeList.ParsePlace("craft.list@0,-15"));
        foreach (var other in new[] { null, "gather@list", "ability@338,1240", "craft.list@", "craft.list@5,100", "craft.list@1", "craft.list@a,100", "craft.list@1,2,3" })
        {
            Assert.IsNull(RecipeList.ParsePlace(other), other ?? "null");
        }

        Assert.IsTrue(RecipeList.SamePlace("craft.list@1,886", 1, 900, 20));
        Assert.IsFalse(RecipeList.SamePlace("craft.list@1,886", 1, 910, 20), "a row further");
        Assert.IsFalse(RecipeList.SamePlace("craft.list@1,886", 2, 886, 20), "another column");
        Assert.IsFalse(RecipeList.SamePlace("gather@list", 1, 886, 20));
    }

    [TestMethod]
    public void TheHints_AreTheWantedProductsPlaces_ShallowestFirst_OnePerCardPlace()
    {
        // live 2026-10-16: Salad Savory in Recent (card 10, row 886) and in its section (card 32, row 1601), two grades
        IconItem[] learned =
        [
            Card(1064010, "Salad Savory", "craft.list@3,1602"),
            Card(1064011, "Salad Savory", "craft.list@1,886"),
            Card(1064011, "Salad Savory", "craft.list@3,1590"), // the same card at another scroll phase
            Card(1064011, "Salad Savory", null),
            Card(1044002, "Roast Chicken", "craft.list@2,2021"),
            Card(1064011, "Salad Savory", "ability@338,1240"), // a Stock tile's place: never a list hint
        ];

        var hints = RecipeList.PlaceHints(learned, i => i.Item == new ItemKey("salad savory"));

        CollectionAssert.AreEqual(new[] { (1, 886), (3, 1590) }, hints.ToArray());
        Assert.HasCount(0, RecipeList.PlaceHints(learned, i => i.ItemId == 1));
    }

    [TestMethod]
    public void TheTop_IsReachedWhenADragMovesTheListClearlyLessThanAFullDrag()
    {
        Assert.IsTrue(RecipeList.TopReached(new ListShift(true, 31, 1.2, 6.3), 280), "live: the first top drag after the open");
        Assert.IsTrue(RecipeList.TopReached(new ListShift(true, 0, 0, 6.5), 280), "no movement");
        Assert.IsTrue(RecipeList.TopReached(new ListShift(true, 2, 0, 6.5), 280));
        Assert.IsTrue(RecipeList.TopReached(new ListShift(true, 220, 1, 9), 280));
        Assert.IsFalse(RecipeList.TopReached(new ListShift(true, 264, 3.3, 9.3), 280), "a full move: the list had room");
        Assert.IsFalse(RecipeList.TopReached(new ListShift(true, 230, 3.3, 9.3), 280), "too close to a full move to be sure");
        Assert.IsFalse(RecipeList.TopReached(new ListShift(false, 31, 8, 9), 280), "not measured");
        Assert.IsFalse(RecipeList.TopReached(new ListShift(true, -40, 1, 9), 280), "moved the wrong way");
    }

    [TestMethod]
    public void AJump_UsesTheLongestDragInsideTheList_ThenAShorterOneToCentreTheCard()
    {
        // the data's scroll press point (anchor (485,123) + (-65,737)) = (420,860): even the engine's 400 px fit inside the
        // list; a jump keeps to 360 px so the frames before and after still share 174 rows for the shift measurement
        Assert.AreEqual(RecipeList.MaxJumpDragPx, RecipeList.MaxUpDragPx(new PointI(420, 860)));
        Assert.IsLessThanOrEqualTo(HardRules.MaxDragDistancePx, RecipeList.MaxJumpDragPx);
        Assert.IsGreaterThanOrEqualTo(170, RecipeList.Viewport.Height - (RecipeList.MaxJumpDragPx - RecipeList.DragLossPx));
        Assert.AreEqual(80, RecipeList.MaxUpDragPx(new PointI(420, 500)), "release at 420 at most 20 px inside the list top, whole steps");
        Assert.AreEqual(0, RecipeList.MaxUpDragPx(new PointI(420, 400)));

        // Roast Chicken (row 2021): a list that follows all but 16 px of a drag
        var offset = 0;
        var drags = new List<int>();
        while (RecipeList.JumpDragPx(2021, offset, RecipeList.MaxJumpDragPx) is { } px)
        {
            drags.Add(px);
            offset -= px - RecipeList.DragLossPx;
        }

        CollectionAssert.AreEqual(new[] { 360, 360, 360, 360 }, drags.ToArray(), "four drags (the walk needed five scroll drags of 280 px)");
        Assert.IsTrue(drags.All(px => px % HardRules.DragStepPx == 0 && px >= RecipeList.MinJumpDragPx));
        Assert.AreEqual(RecipeList.JumpAimY, 2021 + offset, RecipeList.JumpTolerancePx);
        Assert.IsTrue(RecipeList.ShownWhole(2021, offset));

        Assert.AreEqual(260, RecipeList.JumpDragPx(RecipeList.JumpAimY + 225, 0, 360), "a shorter last drag: the distance plus the drag's loss, in whole steps");
        Assert.IsNull(RecipeList.JumpDragPx(700, 0, 400), "already shown near the middle");
        Assert.IsNull(RecipeList.JumpDragPx(500, 0, 400), "above the middle: a jump never drags back");
        Assert.AreEqual(140, RecipeList.JumpDragPx(RecipeList.JumpAimY + RecipeList.JumpTolerancePx + 1, 0, 400), "just outside the tolerance: the distance plus the drag's loss, in whole steps");
        Assert.IsNull(RecipeList.JumpDragPx(2021, 0, 20), "no room for a drag");
    }

    [TestMethod]
    public void ACardIsShownWhole_OnlyWellInsideTheList()
    {
        Assert.IsTrue(RecipeList.ShownWhole(RecipeList.JumpAimY, 0));
        Assert.IsTrue(RecipeList.ShownWhole(886, -264), "live: card 10 after one scroll drag");
        Assert.IsFalse(RecipeList.ShownWhole(886, 0), "cut by the list's bottom");
        Assert.IsFalse(RecipeList.ShownWhole(400, 0), "cut by the list's top");
    }
}
