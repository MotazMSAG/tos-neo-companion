using Companion.TeaHouse;

namespace Companion.TeaHouse.Tests;

[TestClass]
public sealed class RulesTests
{
    private static readonly TeaModel Model = TeaModel.Default();

    private static int Sq(int row, int col) => (row * TeaBoard.Side) + col;

    [TestMethod]
    public void ThreeTouchingCookies_JoinIntoASpongeCake_OnTheSquareJustPlaced()
    {
        var b = TeaBoard.Parse("....../11..../....../....../....../......");
        var r = TeaRules.Place(b, TeaKind.Cookie, Sq(1, 2), Model);
        Assert.IsTrue(r.Legal);
        Assert.AreEqual(1, r.Joins);
        Assert.AreEqual(5 + 40, r.Points, "placing a cookie pays 5, the sponge 40 (live 2026-10-29)");
        Assert.AreEqual("....../..2.../....../....../....../......", b.ToString());
    }

    [TestMethod]
    public void AJoin_JoinsAgainStraightAway_WhenTheNewPieceTouchesTwoOfItsKind()
    {
        var b = TeaBoard.Parse("..2.../11..../..2.../....../....../......");
        var r = TeaRules.Place(b, TeaKind.Cookie, Sq(1, 2), Model);
        Assert.AreEqual(2, r.Joins);
        Assert.AreEqual(TeaKind.Pudding, r.Top);
        Assert.AreEqual(5 + 40 + 200, r.Points);
        Assert.AreEqual("....../..3.../....../....../....../......", b.ToString());
    }

    [TestMethod]
    public void FourInAGroup_PayTheSameAsThree_AndAllLeave()
    {
        var b = TeaBoard.Parse("....../11..../.1..../....../....../......");
        var r = TeaRules.Place(b, TeaKind.Cookie, Sq(1, 2), Model);
        Assert.AreEqual(1, r.Joins);
        Assert.AreEqual(5 + 40, r.Points);
        Assert.AreEqual("....../..2.../....../....../....../......", b.ToString());
    }

    [TestMethod]
    public void DiagonalPieces_DoNotTouch()
    {
        var b = TeaBoard.Parse("....../.1..../..1.../....../....../......");
        var r = TeaRules.Place(b, TeaKind.Cookie, Sq(3, 3), Model);
        Assert.AreEqual(0, r.Joins);
    }

    [TestMethod]
    public void TheOven_BecomesThePieceWhoseJoinPaysMost()
    {
        var b = TeaBoard.Parse("..33../11..../....../....../....../......");
        Assert.AreEqual(TeaKind.Pudding, TeaRules.OvenChoice(b, Sq(1, 2), Model));
        var r = TeaRules.Place(b, TeaKind.Oven, Sq(1, 2), Model);
        Assert.AreEqual(TeaKind.Pudding, r.Became);
        Assert.AreEqual(TeaKind.Choco, b[Sq(1, 2)]);
        Assert.AreEqual(TeaKind.Cookie, b[Sq(1, 1)]);
    }

    [TestMethod]
    public void AnOvenThatJoinsNothing_BecomesBlackTea_WhichNeverJoins()
    {
        var b = TeaBoard.Parse("....../.1..../....../....../....../......");
        var r = TeaRules.Place(b, TeaKind.Oven, Sq(1, 2), Model);
        Assert.AreEqual(TeaKind.BlackTea, r.Became);
        Assert.AreEqual(TeaKind.BlackTea, b[Sq(1, 2)]);
        b[Sq(2, 2)] = TeaKind.BlackTea;
        Assert.AreEqual(0, TeaRules.Place(b, TeaKind.Oven, Sq(3, 2), Model).Joins);
    }

    [TestMethod]
    public void TheCutlery_RemovesAPiece_AndTurnsACatIntoATeaThatMayJoin()
    {
        var b = TeaBoard.Parse("....../.1..../.tw.../..t.../....../......");
        Assert.IsFalse(TeaRules.CanPlace(b, TeaKind.Cutlery, Sq(0, 3)), "never on an empty square");
        TeaRules.Place(b, TeaKind.Cutlery, Sq(1, 1), Model);
        Assert.AreEqual(TeaKind.Empty, b[Sq(1, 1)]);
        var r = TeaRules.Place(b, TeaKind.Cutlery, Sq(2, 2), Model);
        Assert.AreEqual(TeaKind.Sundae, b[Sq(2, 2)], "the cat's tea joined the two teas");
        Assert.AreEqual(1, r.Joins);
    }

    [TestMethod]
    public void TheTraySquare_IsNeverAPlace_AndNeverJoins()
    {
        var b = TeaBoard.Parse("11..../....../....../....../....../......");
        Assert.IsFalse(TeaRules.CanPlace(b, TeaKind.Cookie, TeaBoard.Tray));
        var r = TeaRules.Place(b, TeaKind.Cookie, Sq(0, 2), Model);
        Assert.AreEqual(0, r.Joins, "the tray's cookie does not count");
    }

    [TestMethod]
    public void ABoxedInWhiteCat_BecomesATea()
    {
        var b = TeaBoard.Parse("....../....../....../....../.....1/....1w");
        TeaRules.Cats(b, Model, rng: null);
        Assert.AreEqual(TeaKind.Tea, b[Sq(5, 5)]);
    }

    [TestMethod]
    public void ACatNextToAFreeCat_IsNotBoxedIn()
    {
        var b = TeaBoard.Parse("....../....../....../....../.....1/...bww");
        TeaRules.Cats(b, Model, rng: null);
        Assert.AreEqual(TeaKind.WhiteCat, b[Sq(5, 5)], "its neighbour can still walk up, so the group is free");
    }

    [TestMethod]
    public void AWhiteCatNextToAnOrangeCat_IsNotBoxedIn_UnlessTheBoardIsFull()
    {
        // the Orange Cat is about to jump away, so it is no wall (586 real moves: 570 fit this way, 534 the old way)
        var b = TeaBoard.Parse("....../....../....../....../.....1/...bow");
        TeaRules.Cats(b, Model, rng: null);
        Assert.AreEqual(TeaKind.WhiteCat, b[Sq(5, 5)]);

        // on a full board the Orange Cat cannot move: both become teas
        var full = TeaBoard.Parse(".12121/212121/121212/212121/121212/2121ow");
        TeaRules.Cats(full, Model, rng: null);
        Assert.AreEqual((TeaKind.Tea, TeaKind.Tea), (full[Sq(5, 4)], full[Sq(5, 5)]));
    }

    [TestMethod]
    public void OrangeCatsJumpFirst_AndAWhiteCatWhoseOnlyFreeSquareTheyTook_StaysPut()
    {
        // one empty square left (next to the White Cat): the Orange Cat jumps there first, the White Cat cannot walk and
        // stays a cat (it was not boxed in when the turn began); the real moves showed this 38 times
        var b = TeaBoard.Parse(".o2121/212121/121212/212121/12121./2121bw");
        TeaRules.Cats(b, Model, new Random(1));
        Assert.AreEqual((TeaKind.Empty, TeaKind.OrangeCat, TeaKind.WhiteCat), (b[Sq(0, 1)], b[Sq(4, 5)], b[Sq(5, 5)]));

        // an Orange Cat jumping away from beside a White Cat frees that square for it in the same turn
        var c = TeaBoard.Parse(".12121/212121/121212/212121/1212.o/2121bw");
        TeaRules.Cats(c, Model, new Random(1));
        Assert.AreEqual((TeaKind.OrangeCat, TeaKind.WhiteCat, TeaKind.Empty), (c[Sq(4, 4)], c[Sq(4, 5)], c[Sq(5, 5)]));
    }

    [TestMethod]
    public void ThreeBoxedInCats_BecomeASundae()
    {
        var b = TeaBoard.Parse("....../....../....../....../...bbb/..bwww");
        var (points, joins, top) = TeaRules.Cats(b, Model, rng: null);
        Assert.AreEqual(1, joins);
        Assert.AreEqual(TeaKind.Sundae, top);
        Assert.AreEqual(Model.JoinPoints[(int)TeaKind.Sundae], points);
        Assert.AreEqual("....../....../....../....../...bbb/..b..s", b.ToString(), "on the highest-numbered cat (the log: 3 of 4)");
    }

    [TestMethod]
    public void AWhiteCat_WalksOneSquareUpDownLeftOrRight()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var b = TeaBoard.Parse("....../....../..w.../....../....../......");
            TeaRules.Cats(b, Model, new Random(seed));
            var at = Enumerable.Range(1, 35).Single(i => b[i] == TeaKind.WhiteCat);
            CollectionAssert.Contains(new[] { Sq(1, 2), Sq(3, 2), Sq(2, 1), Sq(2, 3) }, at);
        }
    }

    [TestMethod]
    public void AJustPlacedCat_IsBoxedInAtOnce_OrWalksAwayAtOnce()
    {
        var b = TeaBoard.Parse("....../....../....../....../.....1/....1.");
        var r = TeaRules.Play(b, TeaKind.WhiteCat, Sq(5, 5), Model, new Random(1));
        Assert.IsTrue(r.Legal);
        Assert.AreEqual(TeaKind.Tea, b[Sq(5, 5)]);
        // and a placed cat walks away in the same turn (the log of 2026-10-29: 29 of 32 placements)
        var c = TeaBoard.Parse("....../....../....../....../....../......");
        TeaRules.Play(c, TeaKind.WhiteCat, Sq(2, 2), Model, new Random(1));
        Assert.AreEqual(TeaKind.Empty, c[Sq(2, 2)]);
        CollectionAssert.Contains(new[] { Sq(1, 2), Sq(3, 2), Sq(2, 1), Sq(2, 3) }, Enumerable.Range(1, 35).Single(i => c[i] == TeaKind.WhiteCat));
    }

    [TestMethod]
    public void AnOrangeCat_JumpsAnywhere_AndIsBoxedInOnlyOnAFullBoard()
    {
        var b = TeaBoard.Parse("....../....../..o.../....../....../......");
        TeaRules.Cats(b, Model, new Random(3));
        Assert.AreEqual(1, Enumerable.Range(1, 35).Count(i => b[i] == TeaKind.OrangeCat));
        var full = TeaBoard.Parse(".12121/212121/121212/212o21/121212/212121");
        TeaRules.Cats(full, Model, rng: null);
        Assert.AreEqual(TeaKind.Tea, full[Sq(3, 3)]);
    }

    [TestMethod]
    public void TheBoardText_RoundTrips()
    {
        const string text = "v12345/678tsh/bwox?./....../....../......";
        Assert.AreEqual(text, TeaBoard.Parse(text).ToString());
    }

    [TestMethod]
    public void ACatPlacedToBoxInTwoMore_MakesTheSundaeOnItsOwnSquare()
    {
        // the log of 2026-10-29: the Sundae appeared on the placed cat's square 2 of 2 times
        var b = TeaBoard.Parse("....../....../....../....../..bbb./.bw.wb");
        var r = TeaRules.Play(b, TeaKind.WhiteCat, Sq(5, 3), Model, new Random(1));
        Assert.AreEqual(TeaKind.Sundae, b[Sq(5, 3)]);
        Assert.AreEqual(TeaKind.Sundae, r.Top);
    }

    [TestMethod]
    public void AnOrangeCat_SometimesHopsNextDoor_MostlyJumpsAnywhere()
    {
        var hops = 0;
        for (var seed = 0; seed < 2000; seed++)
        {
            var b = TeaBoard.Parse("....../....../..o.../....../....../......");
            TeaRules.Cats(b, Model, new Random(seed));
            var at = Enumerable.Range(1, 35).Single(i => b[i] == TeaKind.OrangeCat);
            hops += TeaBoard.Neighbours[Sq(2, 2)].Contains(at) ? 1 : 0;
        }

        // 10 % hops plus 4 of the 34 empty squares by chance: about 21 %
        Assert.IsTrue(hops is > 300 and < 560, $"{hops} of 2000 landed next door");
    }
}
