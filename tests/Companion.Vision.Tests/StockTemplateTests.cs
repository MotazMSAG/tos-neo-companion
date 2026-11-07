using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Vision;
using Companion.Vision.Internal;

namespace Companion.Vision.Tests;

/// <summary>
/// The Stock tab's templates (the inventory feature's data, 2026-10-15), cut at full scale from the tests' own copies
/// of the live captures (tests\TestData\images: stock_*_full.png, the Ability sub-tab at the top and after 1-3 drags,
/// Homestead, Tailor, Hyperweather; live_ability_make_full.png and make_fav*_full.png, the Make tab): each is found at
/// its place on every capture of its own state and nowhere else.
/// </summary>
[TestClass]
public sealed class StockTemplateTests
{
    private static readonly string[] StockCaptures =
    [
        "stock_ability_top_full.png", "stock_ability_s1_full.png", "stock_ability_s2_full.png", "stock_ability_s3_full.png",
        "stock_homestead_full.png", "stock_tailor_full.png", "stock_hyperweather_full.png",
    ];

    private static readonly string[] MakeCaptures = ["live_ability_make_full.png", "make_fav1_whitetoast_full.png", "make_fav5_full.png"];

    private static TemplateCatalog catalog = null!;
    private static VisionEngine vision = null!;

    public TestContext TestContext { get; set; } = null!;

    private static string InventoryData => Path.Combine(TestData.CompanionRoot, "features", "Companion.Feature.Inventory", "data");

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        catalog = new TemplateCatalog();
        foreach (var dir in new[] { TestData.TemplatesDir, InventoryData })
        {
            foreach (var d in JsonSerializer.Deserialize<TemplateDefinition[]>(File.ReadAllText(Path.Combine(dir, DataFileNames.Templates)), ContractJson.Options)!)
            {
                catalog.Add(d, File.ReadAllBytes(Path.Combine(dir, d.ImageFile)), d.MaskFile is null ? null : File.ReadAllBytes(Path.Combine(dir, d.MaskFile)), "test");
            }
        }

        catalog.Seal();
        vision = new VisionEngine(catalog);
    }

    private static Frame Capture(string name) => SourceImages.ToFrame(TestData.Image(name), SourceLayout.ClientFull);

    [TestMethod]
    public void TheTemplatesAreVerifiedFullScaleCuts_OfTheirSourceRects()
    {
        var expected = new Dictionary<string, (string Capture, RectI Rect, TargetTags Tags)>
        {
            ["stock.anchor"] = ("stock_ability_top_full.png", new RectI(1700, 555, 1810, 600), TargetTags.ScreenAnchor),
            ["stock.sub.ability.on"] = ("stock_ability_top_full.png", new RectI(272, 190, 488, 270), TargetTags.None),
            ["stock.sub.homestead.on"] = ("stock_homestead_full.png", new RectI(502, 190, 718, 270), TargetTags.None),
            ["stock.sub.tailor.on"] = ("stock_tailor_full.png", new RectI(734, 190, 950, 270), TargetTags.None),
            ["stock.sub.hyperweather.on"] = ("stock_hyperweather_full.png", new RectI(964, 190, 1180, 270), TargetTags.None),
        };
        foreach (var (id, (file, rect, tags)) in expected)
        {
            var def = catalog.Get(id)!;
            Assert.AreEqual((TemplateStatus.Verified, 1, tags, AutomationSwitch.None, rect.Center), (def.Status, def.SourceScale, def.Tags, def.Gate, def.ExpectedCenter), id);
            Assert.IsTrue(catalog.IsClickable(id), id);
            StringAssert.Contains(def.Source, "tests/TestData/images/" + file);
            StringAssert.Contains(def.Source, $"client rect ({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})");

            // provenance: the PNG is exactly the capture's client rect
            var png = PngCodec.Decode(File.ReadAllBytes(Path.Combine(InventoryData, def.ImageFile)));
            var cut = SourceImages.ToClient(TestData.Image(file), SourceLayout.ClientFull, default, out _).CropCopy(rect);
            Assert.AreEqual((cut.Width, cut.Height), (png.Width, png.Height), id);
            Assert.IsTrue(cut.Data.Zip(png.Data, (x, y) => Math.Abs(x - y)).Max() <= 1, id);
        }
    }

    [TestMethod]
    public void TheStockAnchor_IsOnEveryStockCapture_AtItsPlace_AndNotOnTheMakeTab()
    {
        foreach (var file in StockCaptures)
        {
            var m = vision.Locate(Capture(file), "stock.anchor");
            TestContext.WriteLine($"{file} {m}");
            Assert.IsTrue(m.Found && m.Center == new PointI(1755, 577) && m.Score >= 0.99 && m.Margin >= 0.1, $"{file} {m}");
            Assert.IsTrue(vision.VerifyAt(Capture(file), "stock.anchor", m.Center, 2).Found, file + ": verifies at its centre (slid check)");
            Assert.IsTrue(vision.Locate(Capture(file), "craft.make.tab").Found, file + ": the Make tab to go back to (the core template since 2026-10-16)");
            Assert.IsFalse(vision.Locate(Capture(file), "stock.tab").Found, file + ": the unselected Stock tab is not shown");
            Assert.IsFalse(vision.Locate(Capture(file), "craft.list.anchor").Found, file + ": not the Make tab");
        }

        foreach (var file in MakeCaptures)
        {
            var f = Capture(file);
            Assert.IsFalse(vision.Locate(f, "stock.anchor").Found, file);
            Assert.IsFalse(vision.Locate(f, "craft.make.tab").Found, file);
            var tab = vision.Locate(f, "stock.tab");
            Assert.IsTrue(tab.Found && tab.Center == new PointI(1740, 577) && tab.Score >= 0.99, $"{file} {tab}");
        }
    }

    [TestMethod]
    public void EachSelectedSubTab_IsFoundOnlyWhereItIsSelected()
    {
        var selected = new Dictionary<string, string>
        {
            ["stock_ability_top_full.png"] = "ability",
            ["stock_ability_s1_full.png"] = "ability",
            ["stock_ability_s2_full.png"] = "ability",
            ["stock_ability_s3_full.png"] = "ability",
            ["stock_homestead_full.png"] = "homestead",
            ["stock_tailor_full.png"] = "tailor",
            ["stock_hyperweather_full.png"] = "hyperweather",
        };
        foreach (var (file, on) in selected)
        {
            var f = Capture(file);
            foreach (var sub in new[] { "ability", "homestead", "tailor", "hyperweather" })
            {
                var m = vision.Locate(f, $"stock.sub.{sub}.on");
                TestContext.WriteLine($"{file} {sub} {m.Score:F3} {m.Failure}");
                Assert.AreEqual(sub == on, m.Found, $"{file} {sub}: {m}");
                if (sub != on)
                {
                    Assert.IsTrue(m.Score < 0.8, $"{file} {sub}: an unselected tab stays far below the threshold: {m.Score:F3}");
                }
            }
        }

        foreach (var file in MakeCaptures)
        {
            Assert.IsFalse(vision.Locate(Capture(file), "stock.sub.ability.on").Found, file);
        }
    }
}
