using Companion.Contracts;
using Companion.Contracts.Vision;
using Companion.TeaHouse;

namespace Companion.TeaHouse.Tests;

[TestClass]
public sealed class ReaderTests
{
    [TestMethod]
    public void TheUsersBoard_IsReadSquareBySquare_WithThePulsingPreviewTakenOut()
    {
        var r = TeaReader.Read(Captures.All(), TeaLabels.Seeded());
        Assert.IsTrue(r.OnScreen, $"title {r.TitleDistance} bits off");
        Assert.AreEqual(31, r.Preview, "the White Cat pulses on row 6, column 2");
        Assert.AreEqual(TeaKind.WhiteCat, r.Current);
        Assert.AreEqual(Captures.Board, r.Board.ToString());
        Assert.AreEqual(0, r.UnknownCount);
    }

    [TestMethod]
    public void EachFrameAlone_ReadsTheSamePieces()
    {
        foreach (var f in Captures.All())
        {
            var r = TeaReader.Read([f], TeaLabels.Seeded());
            for (var i = 0; i < TeaBoard.Squares; i++)
            {
                if (i != 31)
                {
                    Assert.AreEqual(TeaBoard.Parse(Captures.Board)[i], r.Board[i], $"square {i}");
                }
            }
        }
    }

    [TestMethod]
    public void AnotherView_IsNotTheTeaHouse()
    {
        var gray = new byte[1920 * 1009 * 4];
        for (var i = 0; i < gray.Length; i += 4)
        {
            gray[i] = (byte)(i / 4 % 1920 / 8);
            gray[i + 1] = 90;
            gray[i + 2] = (byte)(i / 4 / 1920 / 4);
            gray[i + 3] = 255;
        }

        var frame = new Frame(1920, 1009, 1920 * 4, gray, TimeSpan.Zero, DateTimeOffset.UnixEpoch, null, new PointI(0, 0), false);
        Assert.IsFalse(TeaReader.Read([frame], TeaLabels.Seeded()).OnScreen);
    }

    [TestMethod]
    public void APictureNamedByTheUser_IsKnownFromThenOn_AndSurvivesTheFile()
    {
        var labels = TeaLabels.Seeded();
        var cookie = labels.Entries.First(e => e.Kind == TeaKind.Cookie).Print;
        var odd = new string(cookie.Select((c, i) => i < 16 ? (c == 'f' ? '0' : 'f') : c).ToArray());
        Assert.AreEqual(TeaKind.Unknown, labels.Classify(odd, TeaPictureSource.Board).Kind);
        Assert.IsTrue(labels.Learn(odd, TeaKind.Cupcake, TeaPictureSource.Board));
        var back = TeaLabels.FromJson(labels.ToJson());
        Assert.AreEqual(TeaKind.Cupcake, back.Classify(odd, TeaPictureSource.Board).Kind);
        Assert.AreEqual(TeaKind.Cookie, back.Classify(cookie, TeaPictureSource.Board).Kind);
    }

    [TestMethod]
    public void ADamagedLabelsFile_GivesTheSeed()
    {
        Assert.AreEqual(TeaLabels.Seeded().Entries.Count, TeaLabels.FromJson("{not json").Entries.Count);
    }
}

[TestClass]
public sealed class VeilTests
{
    [TestMethod]
    public void UnderTheReconnectingVeil_TheBoardIsNotRead()
    {
        // 2026-10-30 17:48 UTC (phantom_1a, chat and banner blacked out): under "Reconnecting..." light cakes read as empty
        // squares and the helper settled and advised a board that was not there; the title's fingerprint still matched
        var veiled = Captures.Get("veil_reconnect_full.png");
        Assert.IsTrue(TeaReader.MeanLuma(veiled, TeaGeometry.Title) < TeaReader.VeilTitleLuma);
        Assert.IsFalse(TeaReader.Read([veiled], TeaLabels.Seeded()).OnScreen);
        Assert.IsTrue(TeaReader.MeanLuma(Captures.Get("teahouse_t0_full.png"), TeaGeometry.Title) > TeaReader.VeilTitleLuma + 40, "the bright title");
    }
}

[TestClass]
public sealed class CutleryTests
{
    private static List<TeaSquare> Squares(string board, int preview)
    {
        var b = TeaBoard.Parse(board);
        // a cat stands on a plain square, never on a tile
        return Enumerable.Range(0, 36).Select(i => new TeaSquare(i, b[i] == TeaKind.Empty, null, i == preview ? TeaKind.Unknown : b[i], 0, i == preview ? 0.4 : 0, []) { OnTile = i != preview }).ToList();
    }

    [TestMethod]
    public void UnderTheCutlerysPreview_IsTheCatThatJumpedThere_NotAnEmptySquare()
    {
        // 2026-10-29 live: the game suggests the Cutlery on the Orange Cat, which had jumped after the last settled board
        var remembered = TeaBoard.Parse("....../.o..../....../....../....../......");
        var now = Squares("....../....../..x.../....../....../......", preview: 14);
        Assert.AreEqual(TeaKind.OrangeCat, TeaReader.UnderCutlery(14, now, remembered));
    }

    [TestMethod]
    public void UnderTheCutlerysPreview_IsTheCatJustPlaced_WhenNoColourIsShort()
    {
        // 2026-10-30 18:39 UTC: the hint covered the Orange Cat the user had just placed; one cat short on the board
        var remembered = TeaBoard.Parse("....../.o..../....../....../....../......");
        var now = Squares("....../.o..../..x.../....../....../......", preview: 14);
        Assert.AreEqual(TeaKind.OrangeCat, TeaReader.UnderCutlery(14, now, remembered, TeaKind.OrangeCat));
        Assert.AreEqual(TeaKind.WhiteCat, TeaReader.UnderCutlery(14, now, remembered, TeaKind.WhiteCat));
    }

    [TestMethod]
    public void UnderTheCutlerysPreview_IsTheRememberedPiece_WhenThereWasOne()
    {
        var remembered = TeaBoard.Parse("....../....../..3.../....../....../......");
        Assert.AreEqual(TeaKind.Pudding, TeaReader.UnderCutlery(14, Squares("....../....../..x.../....../....../......", 14), remembered));
        Assert.AreEqual(TeaKind.Unknown, TeaReader.UnderCutlery(14, Squares("....../....../..x.../....../....../......", 14), new TeaBoard()), "never empty");
    }
}

[TestClass]
public sealed class PreviewVersusCatTests
{
    /// <summary>
    /// The board of 2026-10-29 18:5x (look-only captures, four frames): a Cookie in hand pulsing on row 2 column 1, an
    /// Orange Cat on row 5 column 5 and one in the tray, a White Cat on row 3 column 1.
    /// </summary>
    private static Frame[] Frames() => [.. Enumerable.Range(0, 4).Select(i => Captures.Get($"teahouse_c{i}_full.png"))];

    [TestMethod]
    public void ThePulsingCookie_IsThePreview_AndNoCatAppearsOrVanishes()
    {
        var r = TeaReader.Read(Frames(), TeaLabels.Seeded());
        Assert.AreEqual(6, r.Preview, $"preview scores: {string.Join(" ", r.Squares.Select(s => $"{s.Square}:{s.PreviewScore:F2}"))}");
        Assert.AreEqual(TeaKind.Empty, r.Board[6]);
        Assert.AreEqual(TeaKind.OrangeCat, r.Board[28], "the Orange Cat on row 5, column 5");
        Assert.AreEqual(1, Enumerable.Range(1, 35).Count(i => r.Board[i] == TeaKind.OrangeCat), "one Orange Cat on the board, none made up");
        Assert.AreEqual(TeaKind.WhiteCat, r.Board[12]);
    }
}

[TestClass]
public sealed class SpillTests
{
    [TestMethod]
    public void TheOrangeCatPreviewsHat_InTheSquareAbove_DoesNotMakeItAPiece()
    {
        // 2026-10-29 live ("stuck"): the preview on row 4 column 2 reached into row 3 column 2 in 2 of 4 frames; that empty
        // square read as unknown and the advice stopped
        var frames = Enumerable.Range(0, 4).Select(i => Captures.Get($"teahouse_s{i}_full.png")).ToArray();
        var r = TeaReader.Read(frames, TeaLabels.Seeded());
        Assert.AreEqual(19, r.Preview);
        Assert.AreEqual(TeaKind.Empty, r.Board[13], "the square above the preview");
        Assert.AreEqual(0, r.UnknownCount, string.Join(" ", r.Squares.Where(s => s.Kind == TeaKind.Unknown).Select(s => s.Square)));
    }

    [TestMethod]
    public void TheFarSide_IsTheHalfAwayFromThePreview()
    {
        var above = TeaReader.FarSide(13, 19);
        var whole = TeaGeometry.Interior(13);
        Assert.AreEqual(whole.Top, above.Top, "the preview is below: the top part is kept");
        Assert.IsTrue(above.Height < whole.Height);
        Assert.AreEqual(whole.Width, above.Width);
    }
}

[TestClass]
public sealed class TileTests
{
    [TestMethod]
    public void PlacedCakesSitOnTheirTile_TheCookiePreviewDoesNot_EvenInOneFrame()
    {
        // 2026-10-29 live: the Cookie preview read as a placed Cookie and the Cutlery was advised onto it
        var one = Captures.Get("teahouse_c0_full.png");
        Assert.IsFalse(TeaReader.TileCorner(one, TeaGeometry.Square(6)), "the Cookie preview on row 2, column 1");
        Assert.IsTrue(TeaReader.TileCorner(one, TeaGeometry.Square(17)), "a placed Cookie");
        Assert.IsFalse(TeaReader.TileCorner(one, TeaGeometry.Square(1)), "an empty square");
    }

    [TestMethod]
    public void ACookieOffItsTile_IsThePreview_EvenAtASlowMomentOfItsPulse()
    {
        // the live case: the preview read as a placed Cookie and barely moved between the frames
        var squares = Enumerable.Range(0, 36).Select(i => new TeaSquare(i, i != 6 && i != 17, null, i is 6 or 17 ? TeaKind.Cookie : TeaKind.Empty, 0, i == 6 ? 0.05 : 0, [])
        {
            OnTile = i != 6,
        }).ToList();
        Assert.AreEqual(6, TeaReader.FindPreview(squares, TeaKind.Cookie));
    }
}

[TestClass]
public sealed class CutleryOnPreviewTests
{
    [TestMethod]
    public void TheBoardWhereTheCutleryWasAdvisedOntoThePreview_ReadsThatSquareEmpty()
    {
        // 2026-10-29 live, twice: Cookie in hand, the Cutlery in the tray, the Cookie preview on row 2 column 3
        var frames = Enumerable.Range(0, 4).Select(i => Captures.Get($"teahouse_x{i}_full.png")).ToArray();
        var r = TeaReader.Read(frames, TeaLabels.Seeded());
        Assert.AreEqual(8, r.Preview);
        Assert.AreEqual(TeaKind.Empty, r.Board[8]);
        Assert.IsFalse(r.Squares[8].OnTile);
        foreach (var placed in new[] { 1, 2, 4, 5, 9, 16, 17, 24, 30, 33, 34 })
        {
            Assert.IsTrue(r.Squares[placed].OnTile, $"the placed Cookie on square {placed}");
        }

        var advice = TeaSolver.Advise(r.Board, r.Current, TeaModel.Default());
        Assert.IsFalse(advice.Any(a => a.Square == 8 && a.Piece == TeaKind.Cutlery), "never the Cutlery onto the preview");
    }
}

[TestClass]
public sealed class CutleryPreviewOnTileTests
{
    private static List<TeaSquare> Squares(Func<int, TeaSquare> make) => Enumerable.Range(0, 36).Select(make).ToList();

    [TestMethod]
    public void WithTheCutleryInHand_TheMovingTile_IsThePreview()
    {
        // 2026-10-29 live: the Cutlery's preview over the Cookie just placed on row 4 column 3 moved; read as an Orange Cat
        var squares = Squares(i => new TeaSquare(i, i is not (20 or 21 or 22), null, i == 20 ? TeaKind.Unknown : i == 21 ? TeaKind.OrangeCat : i == 22 ? TeaKind.Cookie : TeaKind.Empty, 0, i == 20 ? 0.2 : i == 21 ? 0.3 : 0, [])
        {
            OnTile = i is 20 or 22,
        });
        Assert.AreEqual(20, TeaReader.FindPreview(squares, TeaKind.Cutlery));
    }

    [TestMethod]
    public void UnderTheCutlerysPreview_IsThePieceJustPlaced_OrWhatItJoinedInto()
    {
        var before = TeaBoard.Parse("....../....../....../.1.s../....../......");
        var squares = Squares(i => new TeaSquare(i, false, null, TeaKind.Unknown, 0, 0, []) { OnTile = i == 20 });
        Assert.AreEqual(TeaKind.Cookie, TeaReader.UnderCutlery(20, squares, before, TeaKind.Cookie), "the Cookie just placed");
        var pair = TeaBoard.Parse("....../....../....11/.1.s../....../......");
        Assert.AreEqual(TeaKind.Sponge, TeaReader.UnderCutlery(15, Squares(i => new TeaSquare(i, false, null, TeaKind.Unknown, 0, 0, []) { OnTile = i == 15 }), pair, TeaKind.Cookie), "it joined into a Sponge Cake");
    }
}

[TestClass]
public sealed class PreviewLikenessTests
{
    [TestMethod]
    public void TheOffTileSquareMostLikeTheCurrentPiece_IsThePreview_NotALivelyOrangeCat()
    {
        // the log audit of 2026-10-30: a lively Orange Cat (unknown pose) passed for the Cookie preview about 50 times
        var squares = Enumerable.Range(0, 36).Select(i => new TeaSquare(i, i is not (8 or 20), null, TeaKind.Unknown, 99, i == 8 ? 0.35 : i == 20 ? 0.5 : 0, []) { OnTile = false }).ToList();
        var toCurrent = Enumerable.Repeat(int.MaxValue, 36).ToArray();
        toCurrent[8] = 34;
        toCurrent[20] = 82;
        Assert.AreEqual(8, TeaReader.FindPreview(squares, TeaKind.Cookie, toCurrent));
    }

    [TestMethod]
    public void TheRealCookiePreviewBoards_StillReadRight()
    {
        foreach (var name in new[] { "c", "x" })
        {
            var frames = Enumerable.Range(0, 4).Select(i => Captures.Get($"teahouse_{name}{i}_full.png")).ToArray();
            var r = TeaReader.Read(frames, TeaLabels.Seeded());
            Assert.AreEqual(name == "c" ? 6 : 8, r.Preview, name);
        }
    }
}
