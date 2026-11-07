using Companion.TeaHouse;

namespace Companion.TeaHouse.Tests;

[TestClass]
public sealed class PointsTests
{
    private static TeaPointsObservation Move(TeaKind placed, int joins, TeaKind top, double delta, TeaKind? became = null) =>
        new(placed, became ?? placed, joins, top, delta);

    [TestMethod]
    public void ThePointsOfTheUsersRounds_AreSolvedLevelByLevel_WhateverOrderTheMovesCameIn()
    {
        // the score changes of 2026-10-29: the Cupcake's move came before the Chocolate Cake's placing value was known
        var moves = new[]
        {
            Move(TeaKind.Choco, 1, TeaKind.Cupcake, 2000),
            Move(TeaKind.Cookie, 0, TeaKind.Empty, 5),
            Move(TeaKind.Cookie, 1, TeaKind.Sponge, 45),
            Move(TeaKind.Pudding, 0, TeaKind.Empty, 100),
            Move(TeaKind.Pudding, 1, TeaKind.Choco, 600),
            Move(TeaKind.Choco, 0, TeaKind.Empty, 500),
            Move(TeaKind.Cookie, 2, TeaKind.Pudding, 245),
        };
        var m = new TeaModel();
        foreach (var o in moves)
        {
            m.Observe(o);
        }

        Assert.AreEqual(5, m.PlacePoints[(int)TeaKind.Cookie]);
        Assert.AreEqual(40, m.JoinPoints[(int)TeaKind.Sponge]);
        Assert.AreEqual(200, m.JoinPoints[(int)TeaKind.Pudding], "the chain: 245 - 5 - 40");
        Assert.AreEqual(500, m.JoinPoints[(int)TeaKind.Choco]);
        Assert.AreEqual(1500, m.JoinPoints[(int)TeaKind.Cupcake], "2000 less the 500 for placing the Chocolate Cake, measured later");
        Assert.AreEqual(5000, m.JoinPoints[(int)TeaKind.Shortcake], "an unmeasured level: the ladder above the Cupcake's 1500");
        Assert.AreEqual(30000, m.JoinPoints[(int)TeaKind.Luxury], "the Luxury Cake's guess");
        Assert.IsFalse(m.JoinPointsMeasured[(int)TeaKind.Shortcake]);
    }

    [TestMethod]
    public void AnOvensJoin_CountsFromWhatItBecame()
    {
        var m = new TeaModel();
        m.Observe(Move(TeaKind.Oven, 0, TeaKind.Empty, 0, became: TeaKind.BlackTea));
        m.Observe(Move(TeaKind.Oven, 1, TeaKind.TeaHouse, 5000, became: TeaKind.Sundae));
        Assert.AreEqual(5000, m.JoinPoints[(int)TeaKind.TeaHouse]);
    }

    [TestMethod]
    public void AnOvenThatJoins_PaysOnlyItsJoins_SoTheStrawberryCakeMeasuresAtTenThousand()
    {
        // 2026-10-30: an Oven finishing three Shortcake Slices paid exactly 10000 (the user confirmed it), while the helper
        // had learned 40 for an Oven placed alone and took it off every Oven join (9960)
        var m = new TeaModel();
        m.Observe(Move(TeaKind.Oven, 0, TeaKind.Empty, 40, became: TeaKind.BlackTea));
        m.Observe(Move(TeaKind.Oven, 1, TeaKind.WholeCake, 10000, became: TeaKind.Shortcake));
        Assert.AreEqual(10000, m.JoinPoints[(int)TeaKind.WholeCake]);

        var d = TeaModel.Default();
        Assert.AreEqual(10000, d.JoinPoints[(int)TeaKind.WholeCake]);
        Assert.IsTrue(d.JoinPointsMeasured[(int)TeaKind.WholeCake]);
        var board = TeaBoard.Parse("....../.66.../....../....../....../......");
        var placed = TeaRules.Place(board, TeaKind.Oven, 9, d);
        Assert.AreEqual(TeaKind.Shortcake, placed.Became);
        Assert.AreEqual(10000, placed.Points, "no placing points on top of the join");
    }

    [TestMethod]
    public void AStoredValue_NeverBeatsAConfirmedOne_ButAGuessedKindKeepsWhatWasMeasured()
    {
        // 2026-10-31: model.json kept the Strawberry Cake at 9960 after the fix (its moves had left the window and every
        // save wrote it back); the helper and the trainer now load the file the same way
        var file = new TeaModelFile(2,
            new Dictionary<string, double> { ["WholeCake"] = 9960, ["Choco"] = 455, ["Luxury"] = 25000 },
            new Dictionary<string, bool> { ["WholeCake"] = true, ["Choco"] = true, ["Luxury"] = true },
            new Dictionary<string, double> { ["Pudding"] = 145, ["Oven"] = 40 },
            new Dictionary<string, long> { ["Cookie"] = 57, ["OrangeCat"] = 4 })
        {
            PlaceMeasured = new Dictionary<string, bool> { ["Pudding"] = true, ["Oven"] = true },
        };

        var m = TeaModelFile.Load(file.ToJson());

        Assert.AreEqual(10000, m.JoinPoints[(int)TeaKind.WholeCake]);
        Assert.AreEqual(500, m.JoinPoints[(int)TeaKind.Choco]);
        Assert.AreEqual(100, m.PlacePoints[(int)TeaKind.Pudding]);
        Assert.AreEqual(0, m.PlacePoints[(int)TeaKind.Oven], "an Oven that joins nothing pays nothing");
        Assert.AreEqual(25000, m.JoinPoints[(int)TeaKind.Luxury], "never made by the game's measure, so the stored value stands");
        Assert.AreEqual(57, m.Seen[(int)TeaKind.Cookie], "the counts are kept");
        Assert.AreEqual(10000, TeaModelFile.Load(TeaModelFile.From(m).ToJson()).JoinPoints[(int)TeaKind.WholeCake], "and survive a save");
        Assert.AreEqual(TeaModel.Default().JoinPoints[(int)TeaKind.WholeCake], TeaModelFile.Load("{not json").JoinPoints[(int)TeaKind.WholeCake]);
    }

    [TestMethod]
    public void OneOutlier_CannotSetAValue()
    {
        // 2026-10-31: a two-move score change (+45 and +100) was learned as placing a Pudding pays 145; the upper median
        // of [100, 145] took it, and every Chocolate Cake made from a Pudding then measured 455
        var m = new TeaModel();
        m.Observe(Move(TeaKind.Pudding, 0, TeaKind.Empty, 100));
        m.Observe(Move(TeaKind.Pudding, 0, TeaKind.Empty, 145));
        Assert.AreEqual(100, m.PlacePoints[(int)TeaKind.Pudding]);
        m.Observe(Move(TeaKind.Pudding, 1, TeaKind.Choco, 600));
        Assert.AreEqual(500, m.JoinPoints[(int)TeaKind.Choco]);
    }

    [TestMethod]
    public void TheStartingOdds_AreTheMeasuredOnes()
    {
        // 2026-10-31: 3,243 pieces; the old guess gave the Orange Cat 1.5%
        var d = TeaModel.Default();
        Assert.AreEqual(0.0435, d.Odds(TeaKind.OrangeCat), 0.002);
        Assert.AreEqual(0.567, d.Odds(TeaKind.Cookie), 0.005);
        Assert.IsTrue(d.Likely().Any(x => x.Kind == TeaKind.OrangeCat), "the look-ahead plans for Orange Cats");
    }
}

[TestClass]
public sealed class NetTests
{
    [TestMethod]
    public void ALearnedValue_MovesTowardItsTarget_AndSurvivesTheFile()
    {
        var net = new TeaNet();
        var b = TeaBoard.Parse("....../..2.../..11../....../....../......");
        for (var i = 0; i < 200; i++)
        {
            net.Learn(b, 1500, 0.1);
        }

        Assert.AreEqual(1500, net.Value(b), 15);
        using var ms = new MemoryStream();
        net.Save(ms);
        ms.Position = 0;
        var back = TeaNet.Load(ms)!;
        Assert.AreEqual(net.Value(b), back.Value(b), 1e-3);
        Assert.IsNull(TeaNet.Load(new MemoryStream([1, 2, 3])), "a damaged file is no network");
    }

    [TestMethod]
    public void ATrainingRound_PlaysToTheEnd_AndCountsItself()
    {
        // whether training helps is measured by self-play (NetProbe, the trainer's own averages), not here
        var net = new TeaNet();
        var score = TeaTrainer.Round(net, TeaModel.Default(), new Random(5), 0.02);
        Assert.IsTrue(score > 0);
        Assert.AreEqual(1, net.Trained);
    }

    [TestMethod]
    public void FindingOutPoints_AimsAtThePiecesNotMeasuredYet()
    {
        var m = TeaModel.Default();
        m.JoinPointsMeasured[(int)TeaKind.Sponge] = true;
        var explore = m.ForExploring(10);
        Assert.AreEqual(m.JoinPoints[(int)TeaKind.Sponge], explore.JoinPoints[(int)TeaKind.Sponge], "measured: unchanged");
        Assert.AreEqual(m.JoinPoints[(int)TeaKind.Luxury] * 10, explore.JoinPoints[(int)TeaKind.Luxury]);
    }
}

[TestClass]
public sealed class FeatureTests
{
    [TestMethod]
    public void EveryFeatureIndex_FitsItsTable_OnRealBoards()
    {
        foreach (var text in new[]
        {
            "o.sst1/s3o.st/13.443/416wo3/1hs34./t1s..3",
            "vww.../.2ww.w/2tt.24/w.24../w.332t/2.1t1t",
            "x11t11/2o.1tt/....11/..o.t2/1..4w./1..11.",
            "....../....../....../....../....../......",
            "v12121/212121/1o..21/212121/121212/212121",
        })
        {
            var board = TeaBoard.Parse(text);
            Span<(int Table, int Index)> into = stackalloc (int, int)[TeaFeatures.Tables + TeaBoard.Squares];
            var n = TeaFeatures.Indices(board, into);
            Assert.IsTrue(n >= TeaFeatures.Tables - 1, text);
            for (var i = 0; i < n; i++)
            {
                Assert.IsTrue(into[i].Index >= 0 && into[i].Index < TeaFeatures.Sizes[into[i].Table], $"{text}: table {into[i].Table} index {into[i].Index}");
            }
        }
    }

    [TestMethod]
    public void AFeatureBrain_LearnsAndSurvivesItsFile_AndAnOldBrainStillLoads()
    {
        var net = new TeaNet { UsesFeatures = true };
        var b = TeaBoard.Parse("o.sst1/s3o.st/13.443/416wo3/1hs34./t1s..3");
        for (var i = 0; i < 200; i++)
        {
            net.Learn(b, 3000, 0.1);
        }

        Assert.AreEqual(3000, net.Value(b), 30);
        using var ms = new MemoryStream();
        net.Save(ms);
        ms.Position = 0;
        var back = TeaNet.Load(ms)!;
        Assert.IsTrue(back.UsesFeatures);
        Assert.AreEqual(net.Value(b), back.Value(b), 1e-3);

        var old = new TeaNet();
        using var ms2 = new MemoryStream();
        old.Save(ms2);
        ms2.Position = 0;
        Assert.IsFalse(TeaNet.Load(ms2)!.UsesFeatures);
    }
}
