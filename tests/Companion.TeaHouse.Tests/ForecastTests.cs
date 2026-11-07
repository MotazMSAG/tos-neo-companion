using Companion.TeaHouse;

namespace Companion.TeaHouse.Tests;

/// <summary>The round forecast (2026-10-30): outlooks read from the look-alike practice rounds.</summary>
[TestClass]
public sealed class ForecastTests
{
    /// <summary>Two kinds of rounds at the same score: a high best-move value ended at 250k, a low one at 90k.</summary>
    private static TeaForecast Synthetic(double early)
    {
        var samples = new List<(double, double, double)>();
        for (var i = 0; i < 400; i++)
        {
            samples.Add((80_000 + (i % 20 * 100), 150_000, 250_000));
            samples.Add((80_000 + (i % 20 * 100), 40_000, 90_000));
        }

        return TeaForecast.Parse(TeaForecast.Write(samples, early, "synthetic"))!;
    }

    [TestMethod]
    public void TheOutlook_IsHowTheLookAlikeRoundsEnded()
    {
        var f = Synthetic(60_000);
        Assert.AreEqual((800, 60_000.0), (f.Count, f.EarlyScore));

        var good = f.Predict(81_000, 140_000);
        Assert.AreEqual(new TeaOutlook(250_000, 1, 1, false), good);
        var bad = f.Predict(81_000, 45_000);
        Assert.AreEqual(new TeaOutlook(90_000, 0, 0, false), bad);
    }

    [TestMethod]
    public void BelowTheEarlyScore_ItIsTooEarlyToTell()
    {
        Assert.IsTrue(Synthetic(60_000).Predict(20_000, 140_000).TooEarly);
        Assert.IsFalse(Synthetic(60_000).Predict(60_000, 140_000).TooEarly);
    }

    [TestMethod]
    public void TooFewSamplesOrNoFile_GiveNoForecast()
    {
        Assert.IsNull(TeaForecast.Parse(null));
        Assert.IsNull(TeaForecast.Parse("# early 1000\n1,2,3\n"));
        var text = TeaForecast.Write(Enumerable.Repeat((1000.0, 2000.0, 3000.0), TeaForecast.Alike), 1000, "note");
        Assert.IsNotNull(TeaForecast.Parse(text.Replace("\n", "\r\n", StringComparison.Ordinal)), "Windows line ends read too");
    }
}
