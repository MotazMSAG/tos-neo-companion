using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Vision;
using Companion.TeaHouse;
using Companion.Vision;

namespace Companion.TeaHouse.Tests;

/// <summary>
/// The auto-play anchor (feature teahouse, 2026-10-30): the engine's matcher finds the Tea House title on every helper
/// capture at its place, unique and verifiable while slid, and the board presses hang off it at the reader's squares.
/// </summary>
[TestClass]
public sealed class AnchorTests
{
    private static string FeatureData()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "features", "Companion.Feature.TeaHouse")))
        {
            d = d.Parent;
        }

        Assert.IsNotNull(d, "companion root not found above the test output");
        return Path.Combine(d.FullName, "features", "Companion.Feature.TeaHouse", "data");
    }

    [TestMethod]
    public void EveryAnchor_IsFoundAtItsPlace_OnEveryCapture_AndTheSquaresAreTheReaders()
    {
        // five static pictures around the board (2026-10-30 live: with the title alone, a press low on the board slid the
        // title above the monitor and the slid check refused it)
        var dir = FeatureData();
        var defs = JsonSerializer.Deserialize<TemplateDefinition[]>(File.ReadAllBytes(Path.Combine(dir, DataFileNames.Templates)), ContractJson.Options)!;
        Assert.HasCount(5, defs);
        var catalog = new TemplateCatalog();
        foreach (var def in defs)
        {
            catalog.Add(def, File.ReadAllBytes(Path.Combine(dir, def.ImageFile)), null, "feature:teahouse");
        }

        catalog.Seal();
        var vision = new VisionEngine(catalog);
        var captureDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        var names = (Directory.Exists(captureDir) ? Directory.GetFiles(captureDir, "teahouse_*_full.png") : []).Select(Path.GetFileName).ToList();
        if (names.Count == 0)
        {
            Assert.Inconclusive("private captures not included in the public repository");
        }

        Assert.HasCount(20, names);
        foreach (var def in defs)
        {
            foreach (var name in names)
            {
                var m = vision.Locate(Captures.Get(name!), def.Id);
                Assert.IsTrue(m.Found && m.Failure == MatchFailure.None, def.Id + " " + name + " " + m);
                Assert.IsTrue(m.Center.ChebyshevDistanceTo(def.ExpectedCenter) <= 1 && m.Score >= 0.99 && m.Margin >= 0.10, def.Id + " " + name + " " + m);
                Assert.IsTrue(vision.VerifyAt(Captures.Get(name!), def.Id, m.Center, 2).Found, def.Id + " " + name + " verifies at its centre (slid check)");
            }
        }

        // the press points are the centres of the reader's squares
        for (var square = 0; square < TeaBoard.Squares; square++)
        {
            var r = TeaGeometry.Square(square);
            Assert.AreEqual(new PointI(r.Left + 63, r.Top + 63), new PointI((int)Math.Round(TeaGeometry.Left + (TeaGeometry.PitchX * TeaBoard.Column(square))) + 63, (int)Math.Round(TeaGeometry.Top + (TeaGeometry.PitchY * TeaBoard.Row(square))) + 63));
        }
    }
}
