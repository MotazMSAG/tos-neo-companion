using System.Diagnostics;
using Companion.TeaHouse;

namespace Companion.TeaHouse.Tests;

[TestClass]
public sealed class SolverTests
{
    private static readonly TeaModel Model = TeaModel.Default();

    private static int Sq(int row, int col) => (row * TeaBoard.Side) + col;

    [TestMethod]
    public void ACookieNextToAPair_IsAdvisedToJoinIt()
    {
        var b = TeaBoard.Parse("....../....../..11../....../....../......");
        var best = TeaSolver.Advise(b, TeaKind.Cookie, Model)[0];
        Assert.AreEqual(TeaAction.Place, best.Action);
        Assert.AreEqual(1, best.Joins, $"advised {TeaBoard.Where(best.Square)}");
    }

    [TestMethod]
    public void AChainIsPreferred_OverASingleJoin()
    {
        // (2,2) completes the cookies, and the new sponge joins the sponges above and below it; the cookie pair on the
        // right offers only a single join
        // (with rules 1-2 the top advice may be to store the Cookie first: free, it shows the next piece, and the chain
        // stays there for the stored Cookie; what matters here is that the chain outranks the single join)
        var b = TeaBoard.Parse("....../..2.../11...1/..2..1/....../......");
        var advice = TeaSolver.Advise(b, TeaKind.Cookie, Model);
        var chain = advice.Single(a => a.Action == TeaAction.Place && a.Square == Sq(2, 2));
        Assert.AreEqual(2, chain.Joins);
        var singles = advice.Where(a => a.Action == TeaAction.Place && a.Joins == 1 && a.Depth == chain.Depth).ToList();
        Assert.IsTrue(singles.Count > 0 && singles.All(a => a.Value < chain.Value), "the chain beats every single join");
        Assert.IsTrue(advice[0] == chain || advice[0].Action == TeaAction.Store, $"advised {advice[0].Action} {advice[0].Square}");
    }

    [TestMethod]
    public void TheOptions_IncludeTheTray()
    {
        var empty = TeaBoard.Parse("....../....../....../....../....../......");
        Assert.IsTrue(TeaSolver.Options(empty, TeaKind.WhiteCat).Any(o => o.Action == TeaAction.Store));
        var full = TeaBoard.Parse("v...../....../....../....../....../......");
        Assert.IsTrue(TeaSolver.Options(full, TeaKind.Cookie).Any(o => o.Action == TeaAction.Swap && o.Piece == TeaKind.Oven));
        Assert.IsFalse(TeaSolver.Options(full, TeaKind.Cookie).Any(o => o.Action == TeaAction.Store));
    }

    [TestMethod]
    public void TheSameBoard_GetsTheSameAdvice()
    {
        var b = TeaBoard.Parse("vww.../.2ww.w/2tt.24/w.24../w.332t/2.1t1t");
        var a1 = TeaSolver.Advise(b, TeaKind.WhiteCat, Model);
        var a2 = TeaSolver.Advise(b, TeaKind.WhiteCat, Model);
        Assert.AreEqual(a1[0], a2[0]);
    }

    [TestMethod]
    public void AMidGameBoard_IsAdvisedWithinASecond()
    {
        // the user's board of 2026-10-29 (score 4475), a White Cat in hand
        var b = TeaBoard.Parse("vww.../.2ww.w/2tt.24/w.24../w.332t/2.1t1t");
        var sw = Stopwatch.StartNew();
        var advice = TeaSolver.Advise(b, TeaKind.WhiteCat, Model);
        sw.Stop();
        Assert.IsTrue(advice.Count > 0);
        Assert.IsTrue(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [TestMethod]
    public void TheUnknownCurrentPiece_GetsNoAdvice()
    {
        var b = TeaBoard.Parse("....../....../....../....../....../......");
        Assert.AreEqual(0, TeaSolver.Advise(b, TeaKind.Unknown, Model).Count);
    }

    [TestMethod]
    public void ASelfPlayRound_PlaysToTheEnd()
    {
        var fast = new TeaSolverOptions { DeepOptions = 0, CatSamples = 1 };
        var round = TeaSelfPlay.Play(Model, fast, seed: 7, maxTurns: 400);
        Assert.IsTrue(round.Turns > 20, $"{round.Turns} turns");
        Assert.IsTrue(round.Score > 0);
    }
}

[TestClass]
public sealed class RefereeTests
{
    private static TeaAdvice A(TeaAction action, int square, TeaKind piece, double value, int joins = 0, TeaKind top = TeaKind.Empty) =>
        new(action, square, piece, value, 0, joins, top, piece, true) { Depth = 4 };

    [TestMethod]
    public void OnACloseCall_TheOrangeCatStaysInTheTray()
    {
        // 2026-10-29 live: "there's no way it wants me to release the orange cat from its prison": +296 points ahead
        var board = TeaBoard.Parse("o1..4w/1.4s2./1s.ts2/11.s.s/12..23/3115.s");
        var ranked = new[] { A(TeaAction.Swap, 11, TeaKind.OrangeCat, 10296), A(TeaAction.Place, 26, TeaKind.Cookie, 10000) };
        var r = TeaReferee.Apply(ranked, board, TeaKind.Cookie);
        Assert.AreEqual(TeaAction.Place, r[0].Action);
        Assert.IsNotNull(r[0].Reason);
        var sure = new[] { A(TeaAction.Swap, 11, TeaKind.OrangeCat, 12000), A(TeaAction.Place, 26, TeaKind.Cookie, 10000) };
        Assert.AreEqual(TeaAction.Swap, TeaReferee.Apply(sure, board, TeaKind.Cookie)[0].Action, "far ahead, the move finder's choice stands");
    }

    [TestMethod]
    public void OnACloseCall_TheSurerPointsWin_AndOutsideTheBandNothingChanges()
    {
        var board = TeaBoard.Parse("....../....../..11../....../....../......");
        var close = new[] { A(TeaAction.Place, 3, TeaKind.Cookie, 1000), A(TeaAction.Place, 14, TeaKind.Cookie, 900, 1, TeaKind.Sponge) };
        Assert.AreEqual(14, TeaReferee.Apply(close, board, TeaKind.Cookie)[0].Square);
        var far = new[] { A(TeaAction.Place, 3, TeaKind.Cookie, 2000), A(TeaAction.Place, 14, TeaKind.Cookie, 900, 1, TeaKind.Sponge) };
        Assert.AreEqual(3, TeaReferee.Apply(far, board, TeaKind.Cookie)[0].Square);
    }
}

[TestClass]
public sealed class EndgameAndOvenTests
{
    [TestMethod]
    public void NearTheEnd_EveryOrangeCatJumpIsListed()
    {
        var board = TeaBoard.Parse("v12121/212121/1o..21/212121/121212/212121");
        var outcomes = TeaRules.CatOutcomes(board, -1, 64, includeOrange: true)!;
        Assert.AreEqual(2, outcomes.Count, "two empty squares: two jumps");
        Assert.AreEqual(1.0, outcomes.Sum(o => o.Chance), 1e-9);
        Assert.IsNull(TeaRules.CatOutcomes(board, -1, 64), "without the endgame rule an Orange Cat is sampled");
    }

    [TestMethod]
    public void AnOvenThatCanOnlyBecomeBlackTea_GetsNoDeepLook_WhileABetterUseExists()
    {
        var board = TeaBoard.Parse("....../....../..11../....../....../......");
        TeaAdvice O(int sq, TeaKind became, double v) => new(TeaAction.Place, sq, TeaKind.Oven, v, 0, 0, TeaKind.Empty, became, false);
        var ranked = new[] { O(1, TeaKind.BlackTea, 900), O(14, TeaKind.Cookie, 800) };
        var set = TeaSolver.DeepSet(ranked, board, TeaKind.Oven, new TeaSolverOptions { DeepOptions = 1, NoWastedOven = true });
        CollectionAssert.AreEqual(new[] { 1 }, set.ToArray(), "the joining Oven gets the deep slot");
        var off = TeaSolver.DeepSet(ranked, board, TeaKind.Oven, new TeaSolverOptions { DeepOptions = 1 });
        CollectionAssert.Contains(off.ToArray(), 0);
    }
}
