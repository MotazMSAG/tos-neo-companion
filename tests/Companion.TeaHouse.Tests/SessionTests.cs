using Companion.TeaHouse;

namespace Companion.TeaHouse.Tests;

[TestClass]
public sealed class SessionTests
{
    private const string CookiePrint = "11111111111111111111111111111111111111111111111111111111111111aa";
    private const string OddPrint = "22222222222222222222222222222222222222222222222222222222222222bb";

    private static TeaReading Reading(string board, TeaKind current, string? currentPrint = null)
    {
        var b = TeaBoard.Parse(board);
        var squares = Enumerable.Range(0, TeaBoard.Squares)
            .Select(i => new TeaSquare(i, b[i] == TeaKind.Empty, b[i] == TeaKind.Empty ? null : OddPrint, b[i], 0, 0, []))
            .ToList();
        return new TeaReading(true, 0, squares, currentPrint ?? ("cur" + current), current, 0, -1, b);
    }

    private static TeaObservation Settle(TeaSession s, TeaReading r, double? score)
    {
        s.Observe(r, score);
        return s.Observe(r, score);
    }

    [TestMethod]
    public void ABoard_CountsOnlyOnceTwoReadingsAgree()
    {
        var s = new TeaSession(TeaModel.Default(), TeaLabels.Seeded());
        var r = Reading("....../....../..11../....../....../......", TeaKind.Cookie);
        Assert.IsFalse(s.Observe(r, 0).Stable);
        Assert.IsTrue(s.Observe(r, 0).Stable);
        Assert.AreEqual(1, s.Model.Seen[(int)TeaKind.Cookie]);
    }

    [TestMethod]
    public void AJoin_TeachesWhatTheCreatedPiecePays()
    {
        var s = new TeaSession(TeaModel.Default(), TeaLabels.Seeded());
        Settle(s, Reading("....../....../..22../....../....../......", TeaKind.Sponge), 100);
        var o = Settle(s, Reading("....../....../....3./....../....../......", TeaKind.Cookie, "next"), 440);
        Assert.IsTrue(o.Turn);
        Assert.IsTrue(o.RulesMatch);
        Assert.AreEqual(TeaKind.Sponge, o.Placed);
        Assert.AreEqual(300, s.Model.JoinPoints[(int)TeaKind.Pudding], "440 - 100, less the 40 for placing a sponge");
        CollectionAssert.Contains(o.Learned.ToList(), "points.Pudding:300");
        Assert.AreEqual(1, s.Model.Seen[(int)TeaKind.Cookie], "the new current piece is counted");
    }

    [TestMethod]
    public void AMoveAfterAnUnreadScore_TeachesNoPoints()
    {
        // 2026-10-31: the score of one board was not read, so the next move's change covered two moves (45 + 100)
        var s = new TeaSession(new TeaModel(), TeaLabels.Seeded());
        Settle(s, Reading("....../....../..1.../....../....../......", TeaKind.Cookie, "a"), 100);
        Settle(s, Reading("....../....../..11../....../....../......", TeaKind.Pudding, "b"), null);
        var o = Settle(s, Reading("....../....../..113./....../....../......", TeaKind.Cookie, "c"), 205);
        Assert.IsTrue(o.Turn && o.RulesMatch);
        Assert.IsNull(o.ScoreDelta);
        Assert.IsFalse(s.Model.PlacePointsMeasured[(int)TeaKind.Pudding]);
    }

    [TestMethod]
    public void ACatLeavingASquareTheOtherCatWalksOnto_IsStillAMoveTheRulesExplain()
    {
        // 2026-10-31 audit: half of the moves the rules could not explain were this
        var s = new TeaSession(TeaModel.Default(), TeaLabels.Seeded());
        Settle(s, Reading(".o.w../....../....../....../....../......", TeaKind.Cookie, "a"), 0);
        var o = Settle(s, Reading(".wo.../....../....../....../....../.....1", TeaKind.Sponge, "b"), 5);
        Assert.IsTrue(o.RulesMatch);
        Assert.AreEqual((TeaKind.Cookie, 35), (o.Placed, o.Square));
    }

    [TestMethod]
    public void ASwap_IsCreditedToTheTraysPiece_NeverToThePieceInHand()
    {
        // 2026-10-30 15:09 UTC: a Sponge swapped in from the tray was learned as "an Oven that joins nothing pays 40"
        var s = new TeaSession(new TeaModel(), TeaLabels.Seeded());
        Settle(s, Reading("2...../....../....../....../....../......", TeaKind.Oven, "a"), 100);
        var misread = Settle(s, Reading("v...../....../..b.../....../....../......", TeaKind.Cookie, "b"), 140);
        Assert.IsFalse(misread.RulesMatch, "the tray took the Oven: the Sponge was placed, which a Black Tea does not fit");
        Assert.IsFalse(s.Model.PlacePointsMeasured[(int)TeaKind.Oven]);

        var t = new TeaSession(new TeaModel(), TeaLabels.Seeded());
        Settle(t, Reading("2...../....../....../....../....../......", TeaKind.Oven, "a"), 100);
        var o = Settle(t, Reading("v...../....../..2.../....../....../......", TeaKind.Cookie, "b"), 140);
        Assert.IsTrue(o.RulesMatch);
        Assert.AreEqual(TeaKind.Sponge, o.Placed);
    }

    [TestMethod]
    public void TheSameKindTwiceInARow_IsCountedTwice()
    {
        var s = new TeaSession(TeaModel.Default(), TeaLabels.Seeded());
        Settle(s, Reading("....../....../....../....../....../......", TeaKind.Cookie, "c"), 0);
        Settle(s, Reading("....../....../..1.../....../....../......", TeaKind.Cookie, "c"), 0);
        Assert.AreEqual(2, s.Model.Seen[(int)TeaKind.Cookie]);
    }

    [TestMethod]
    public void AnUnknownCurrentPiece_IsNamedWhenItLands()
    {
        var s = new TeaSession(TeaModel.Default(), TeaLabels.Seeded());
        Settle(s, Reading("....../....../..2.../....../....../......", TeaKind.Unknown, CookiePrint), 0);
        var o = Settle(s, Reading("....../....../..21../....../....../......", TeaKind.Cookie, "next"), 0);
        Assert.IsTrue(o.RulesMatch);
        Assert.AreEqual(TeaKind.Cookie, o.Placed);
        Assert.AreEqual(TeaKind.Cookie, s.Labels.Classify(CookiePrint, TeaPictureSource.Panel).Kind);
    }

    [TestMethod]
    public void AMoveTheRulesCannotExplain_IsReported()
    {
        var s = new TeaSession(TeaModel.Default(), TeaLabels.Seeded());
        Settle(s, Reading("....../....../..11../....../....../......", TeaKind.Cookie), 0);
        var o = Settle(s, Reading("....../....../..111./....../....../......", TeaKind.Cookie, "next"), 5);
        Assert.IsTrue(o.Turn);
        Assert.IsFalse(o.RulesMatch, "three cookies stayed: the rules say they join");
    }

    [TestMethod]
    public void StoringInTheTray_IsNoTurn_ButHandsOutANewPiece()
    {
        var s = new TeaSession(TeaModel.Default(), TeaLabels.Seeded());
        Settle(s, Reading("....../....../....../....../....../......", TeaKind.WhiteCat), 0);
        var o = Settle(s, Reading("w...../....../....../....../....../......", TeaKind.Cookie), 0);
        Assert.IsFalse(o.Turn);
        Assert.AreEqual(1, s.Model.Seen[(int)TeaKind.Cookie]);
    }

    [TestMethod]
    public void AWalkingCat_StillFitsTheRules()
    {
        var s = new TeaSession(TeaModel.Default(), TeaLabels.Seeded());
        Settle(s, Reading("....../.w..../....../....../....../......", TeaKind.Cookie), 0);
        var o = Settle(s, Reading("....../..w.../....../...1../....../......", TeaKind.Cookie, "next"), 0);
        Assert.IsTrue(o.RulesMatch);
        Assert.AreEqual(3 * 6 + 3, o.Square);
    }
}

[TestClass]
public sealed class SessionSpillTests
{
    private static TeaReading Reading(string board, TeaKind current, string currentPrint, int moving = -1)
    {
        var b = TeaBoard.Parse(board);
        var squares = Enumerable.Range(0, TeaBoard.Squares)
            .Select(i => new TeaSquare(i, b[i] == TeaKind.Empty, b[i] == TeaKind.Empty ? null : "33333333333333333333333333333333333333333333333333333333333333cc", b[i], 0, i == moving ? 0.4 : 0, []))
            .ToList();
        return new TeaReading(true, 0, squares, currentPrint, current, 0, -1, b);
    }

    [TestMethod]
    public void ASquareThatOnlyLooksOccupiedForAMoment_IsNoMove_AndTeachesNothing()
    {
        // 2026-10-29 live: the Orange Cat preview's spill made the square below it read as a piece; the Oven in the tray
        // "explained" it as Black Tea and that picture was learned
        var s = new TeaSession(TeaModel.Default(), TeaLabels.Seeded());
        var before = Reading("v1..1./....2./...3.2/.w12../...1.1/1.....", TeaKind.OrangeCat, "orange");
        s.Observe(before, 45);
        s.Observe(before, 45);
        var spill = Reading("v1..1./...?2./...3.2/.w12../...1.1/1.....", TeaKind.OrangeCat, "orange");
        s.Observe(spill, 45);
        var o = s.Observe(spill, 45);
        Assert.IsFalse(o.Turn);
        Assert.AreEqual(0, o.Learned.Count(c => c.StartsWith("picture.", StringComparison.Ordinal)));
        Assert.IsFalse(s.Labels.Entries.Any(e => e.Kind == TeaKind.BlackTea));
    }

    [TestMethod]
    public void AMissedPreviewOnAnEmptySquare_StillLetsTheMoveFit()
    {
        var s = new TeaSession(TeaModel.Default(), TeaLabels.Seeded());
        var before = Reading("....../....../..11../....../....../......", TeaKind.Cookie, "c1");
        s.Observe(before, 0);
        s.Observe(before, 0);
        // the cookie joined at (2,4); the next piece's preview pulses on (0,0)... (0,1), unread
        var after = Reading(".?..../....../....2./....../....../......", TeaKind.WhiteCat, "w1", moving: 1);
        s.Observe(after, 5);
        var o = s.Observe(after, 5);
        Assert.IsTrue(o.Turn);
        Assert.IsTrue(o.RulesMatch);
        Assert.AreEqual(2 * 6 + 4, o.Square);
    }
}

[TestClass]
public sealed class TrayAndCatTests
{
    [TestMethod]
    public void AStore_TeachesTheTrayPictureOfThePieceThatWasCurrent()
    {
        // 2026-10-29 live: the Orange Cat had to be named again in the tray, and a mid-swap frame taught it as an Oven
        var s = new TeaSession(TeaModel.Default(), TeaLabels.Seeded());
        var b = TeaBoard.Parse("....../....../..1.../....../....../......");
        TeaReading R(TeaBoard board, TeaKind cur, string curPrint, string? trayPrint) => new(true, 0,
            Enumerable.Range(0, 36).Select(i => new TeaSquare(i, board[i] == TeaKind.Empty && i != 0, i == 0 ? trayPrint : (board[i] == TeaKind.Empty ? null : "44444444444444444444444444444444444444444444444444444444444444dd"), board[i], 0, 0, [])).ToList(),
            curPrint, cur, 0, -1, board);
        var before = R(b, TeaKind.OrangeCat, "orange", null);
        s.Observe(before, 0);
        s.Observe(before, 0);
        var after = b.Clone();
        after.TrayPiece = TeaKind.Unknown;
        const string plate = "5555555555555555555555555555555555555555555555555555555555555555";
        var r = R(after, TeaKind.Cookie, "cookie", plate);
        s.Observe(r, 0);
        var o = s.Observe(r, 0);
        CollectionAssert.Contains(o.Learned.ToList(), "picture.Tray.OrangeCat");
        Assert.AreEqual(TeaKind.OrangeCat, s.Labels.Classify(plate, TeaPictureSource.Tray).Kind);
        Assert.AreEqual(TeaKind.OrangeCat, s.Settled!.Board.TrayPiece);
    }

    [TestMethod]
    public void EveryWayAWhiteCatCanWalk_IsListedWithItsChance()
    {
        var b = TeaBoard.Parse("....../....../..w.../....../....../......");
        var outcomes = TeaRules.CatOutcomes(b, -1, 16)!;
        Assert.AreEqual(4, outcomes.Count);
        Assert.AreEqual(1.0, outcomes.Sum(x => x.Chance), 1e-9);
        var corner = TeaBoard.Parse("....../....../....../....../.....1/.....w");
        Assert.AreEqual(1, TeaRules.CatOutcomes(corner, -1, 16)!.Count, "in the corner under a cookie it can only step left");
        Assert.IsNull(TeaRules.CatOutcomes(TeaBoard.Parse("....../....../..o.../....../....../......"), -1, 16), "an Orange Cat jumps anywhere: sampled");
        Assert.IsNull(TeaRules.CatOutcomes(TeaBoard.Parse("w.w.w./....../w.w.w./....../....../......"), -1, 16), "too many ways: sampled");
    }
}

[TestClass]
public sealed class NewPieceTests
{
    [TestMethod]
    public void ANewPieceInHand_OnAnUnchangedBoard_IsTheNewState_WithoutAMoveOrACount()
    {
        // 2026-10-30 live: a Cutlery bought in the shop replaced the Cookie in hand; the helper kept advising for the Cookie
        var s = new TeaSession(TeaModel.Default(), TeaLabels.Seeded());
        var board = TeaBoard.Parse("o.ss.1/s3o.s./13.443/416w.3/1h.34./t1...3");
        TeaReading R(TeaKind cur, string print) => new(true, 0,
            Enumerable.Range(0, 36).Select(i => new TeaSquare(i, board[i] == TeaKind.Empty, board[i] == TeaKind.Empty ? null : "66666666666666666666666666666666666666666666666666666666666666ee", board[i], 0, 0, [])).ToList(),
            print, cur, 0, -1, board);
        s.Observe(R(TeaKind.Cookie, "cookie"), 100);
        s.Observe(R(TeaKind.Cookie, "cookie"), 100);
        var cookies = s.Model.Seen[(int)TeaKind.Cookie];
        s.Observe(R(TeaKind.Cutlery, "cutlery"), 100);
        var o = s.Observe(R(TeaKind.Cutlery, "cutlery"), 100);
        Assert.IsFalse(o.Turn);
        Assert.AreEqual(TeaKind.Cutlery, s.Settled!.Current);
        Assert.AreEqual(0, s.Model.Seen[(int)TeaKind.Cutlery], "a bought piece is not counted in the odds");
        Assert.AreEqual(cookies, s.Model.Seen[(int)TeaKind.Cookie]);
    }
}
