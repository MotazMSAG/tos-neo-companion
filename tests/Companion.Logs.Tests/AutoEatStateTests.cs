using Companion.Contracts.Logs;
using static Companion.Logs.Tests.Synthetic;

namespace Companion.Logs.Tests;

/// <summary>Roadmap B ("keep food at N"): the latest auto-eat is part of the UI state, so rule mode can re-check soon.</summary>
[TestClass]
public sealed class AutoEatStateTests
{
    [TestMethod]
    public void TheLatestAutoEat_IsInTheSnapshot()
    {
        var text = string.Concat(Entry(T0, AutoEatReq), Entry(T0.AddMinutes(3), AutoEatReq));

        var model = Parsing.Model(Parsing.All(Bytes(text)));

        Assert.AreEqual(T0.AddMinutes(3), model.Snapshot(null, T0.AddMinutes(4)).LastAutoEat);
        Assert.IsNull(Parsing.Model([]).Snapshot(null, T0).LastAutoEat, "no auto-eat yet");
    }
}
