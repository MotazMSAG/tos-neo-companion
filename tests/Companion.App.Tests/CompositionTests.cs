using Companion.App.Hosting;
using Companion.Contracts;
using Companion.Contracts.Features;
using Companion.Contracts.Host;
using Companion.Contracts.Vision;

namespace Companion.App.Tests;

/// <summary>
/// Composition smoke tests (SPEC 13 app-host): every real implementation is constructed and the real data files of this
/// build (copied to the test output by the App and feature projects) are loaded into the real catalogs, exactly as at
/// host start, WITHOUT <c>HostRuntime.Start</c>: no RuntimeDir, no log, no game, no window, no pipe, no thread.
/// </summary>
[TestClass]
public sealed class CompositionTests
{
    private static readonly string[] ExpectedJobs =
    [
        "Reset", "OpenBagKey", "OpenBagClick", "ExpandMenu", "CollapseMenu", "OpenAbility", "Calibrate",
        "ScanBag", "ScanStock", "ScanRecipes", "Craft", "KeepStock", "CraftLoop", "ScanFavorites", "ScanRecipeList", "Gather", "GatherAll", "KeepStockLoop", "LearnRecipe", "ForgetRecipes", "SurveyScreen", "HomesteadStatus", "HomesteadCollect", "TeaHousePlay",
    ];

    private static FakeTrust ReadInstallFiles(CompanionPaths paths) => new()
    {
        Reader = p =>
        {
            var full = Path.GetFullPath(p);
            return full.StartsWith(paths.InstallDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full)
                ? File.ReadAllBytes(full)
                : null;
        },
    };

    [TestMethod]
    public void Create_ConstructsEverything_WithoutSideEffects()
    {
        var paths = TestDirs.Paths();
        var s = Composition.Create(paths, new FakeClock(), currentGameBuild: null, new FakeTrust());

        Assert.IsNotNull(s.Stop);
        Assert.IsNotNull(s.Journal);
        Assert.IsNotNull(s.Notifier);
        Assert.IsNotNull(s.Config);
        Assert.IsNotNull(s.Halt);
        Assert.IsNotNull(s.Hasher);
        Assert.IsNotNull(s.Knowledge);
        Assert.IsNotNull(s.Classifier);
        Assert.IsNotNull(s.PrivacyFilter);
        Assert.IsNotNull(s.Log);
        Assert.IsNotNull(s.Templates);
        Assert.IsNotNull(s.Vision);
        Assert.IsNotNull(s.Ocr);
        Assert.IsNotNull(s.Digits);
        Assert.IsNotNull(s.ScreenReader);
        Assert.IsNotNull(s.Readers);
        Assert.IsNotNull(s.UiMap);
        Assert.IsNotNull(s.Procedures);
        Assert.IsNotNull(s.GameIo);
        Assert.IsNotNull(s.Planner);
        Assert.IsNotNull(s.Identifier);
        Assert.IsNotNull(s.PathPlanner);
        Assert.IsNotNull(s.Features);
        Assert.IsNotNull(s.Pause);
        Assert.IsNotNull(s.Artifacts);
        Assert.IsNotNull(s.Screen);
        Assert.IsNotNull(s.Runner);
        Assert.IsNotNull(s.Navigator);

        Assert.IsNull(s.GameIo.Identity, "no game lookup at construction");
        Assert.IsNull(s.Log.Position, "the log is not read at construction");
        Assert.IsFalse(s.Stop.IsStopRequested);
        Assert.IsFalse(Directory.Exists(paths.RuntimeDir), "nothing is written at construction");
        Assert.IsFalse(Directory.Exists(paths.GameDir));
        Assert.AreEqual(Contracts.Engine.EngineState.Idle, s.Runner.Status.State);
    }

    [TestMethod]
    public void LoadData_RealCoreAndFeatureData_SealsEverything_AndEveryJobIsWhitelisted()
    {
        var paths = TestDirs.Paths();
        var trust = ReadInstallFiles(paths);
        var s = Composition.Create(paths, new FakeClock(), currentGameBuild: null, trust);
        var report = CoreDataLoader.Load(s, Composition.CompiledFeatures(), CompanionConfig.Default);

        Assert.IsTrue(report.CoreOk, string.Join(", ", report.Problems));
        Assert.AreEqual(7, report.Features.Count, string.Join(", ", report.Features));
        Assert.AreEqual("core", report.Features[0].FeatureId);
        foreach (var r in report.Features)
        {
            Assert.AreEqual(FeatureLoadStatus.Enabled, r.Status, r.FeatureId + " " + r.ReasonCode);
        }

        CollectionAssert.AreEquivalent(ExpectedJobs, s.Features.Jobs.All.Select(j => j.Name).ToArray());

        foreach (var id in new[] { "Reset", "OpenBagKey", "OpenBagClick", "ExpandMenu", "CollapseMenu", "OpenAbility", "Calibrate" })
        {
            Assert.IsNotNull(s.Procedures.Get(id), "core procedure " + id);
        }

        foreach (var id in new[] { "inventory.scanBag", "recipes.scanRecipes", "crafting.commit", "crafting.loop", "crafting.scanFavorites", "crafting.scanList", "survey.captureCrops", "homestead.status" })
        {
            Assert.IsNotNull(s.Procedures.Get(id), "feature procedure " + id);
        }

        Assert.IsNotNull(s.Templates.Get("hud.bag"));
        Assert.IsTrue(s.Templates.IsClickable("hud.bag"));
        Assert.IsTrue(s.Templates.IsClickable("menu.ability"), "calibrated (verified, full-scale) templates are clickable");
        foreach (var id in new[] { "craft.make", "craft.qty.max", "craft.fav.anchor", "craft.list.anchor" })
        {
            Assert.IsTrue(s.Templates.IsClickable(id), id + " (live run 2026-10-14 cut)");
        }

        Assert.AreEqual(TargetTags.Commit, s.Templates.Get("craft.make")!.Tags & TargetTags.Commit);
        Assert.AreEqual(AutomationSwitch.Crafting, s.Templates.Get("craft.make")!.Gate);
        Assert.IsFalse(s.Templates.IsClickable("menu.homestead"), "templates that do not exist are never clickable");
        Assert.IsNotNull(s.Readers.Get("bag.grid"));
        foreach (var id in new[] { "craft.pairs", "craft.product" })
        {
            Assert.AreEqual(Contracts.Perception.ReaderStatus.Ready, s.Readers.Get(id)!.Status, id + " (surveyed 2026-10-14)");
        }
        Assert.IsNotNull(s.UiMap.GetScreen("hud.collapsed"));

        // The Stock scan (inventory feature, 2026-10-15): its templates, readers and procedures passed the catalogs' checks.
        foreach (var id in new[] { "stock.anchor", "stock.tab", "craft.make.tab" })
        {
            Assert.IsTrue(s.Templates.IsClickable(id), id);
        }

        foreach (var id in new[] { "inventory.stock.grid", "inventory.stock.ability", "inventory.stock.homestead", "inventory.stock.tailor", "inventory.stock.hyperweather" })
        {
            Assert.AreEqual(Contracts.Perception.ReaderStatus.Ready, s.Readers.Get(id)!.Status, id);
        }

        Assert.IsNotNull(s.Procedures.Get("inventory.scanStock"));
        Assert.IsNotNull(s.Procedures.Get("inventory.stockTip"));

        // Sealed: nothing can be added after start.
        var bag = s.Templates.Get("hud.bag")!;
        var png = File.ReadAllBytes(Path.Combine(paths.TemplatesDir, bag.ImageFile));
        Assert.ThrowsExactly<InvalidOperationException>(() => s.Templates.Add(bag with { Id = "late.add" }, png, null, "test"));
        Assert.ThrowsExactly<InvalidOperationException>(() => s.Procedures.Add(s.Procedures.Get("Reset")! with { Id = "Late" }, "test"));

        // Every read went through the trust check and stayed inside the install folder.
        Assert.IsTrue(trust.Reads.Count >= 9, trust.Reads.Count.ToString());
        Assert.IsTrue(trust.Reads.All(p => Path.GetFullPath(p).StartsWith(paths.InstallDir, StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(trust.Reads.Any(p => p.EndsWith(Path.Combine("features", "survey", "procedures.json"), StringComparison.OrdinalIgnoreCase)));

        Assert.IsFalse(Directory.Exists(paths.RuntimeDir), "loading data writes nothing (journal is buffered)");
        Assert.IsNull(s.GameIo.Identity);
    }

    [TestMethod]
    public void LoadData_DisabledFeatureAndBrokenCoreFile()
    {
        var paths = TestDirs.Paths();
        var s = Composition.Create(paths, new FakeClock(), null, ReadInstallFiles(paths));
        var config = CompanionConfig.Default with
        {
            Features = new Dictionary<string, bool>(CompanionConfig.Default.Features) { ["recipes"] = false },
        };
        var report = CoreDataLoader.Load(s, Composition.CompiledFeatures(), config);
        Assert.IsTrue(report.CoreOk, string.Join(", ", report.Problems));
        Assert.AreEqual(FeatureLoadStatus.DisabledByConfig, report.Features.Single(r => r.FeatureId == "recipes").Status);
        Assert.AreEqual(FeatureLoadStatus.DependencyUnavailable, report.Features.Single(r => r.FeatureId == "crafting").Status, "crafting requires recipes");
        var jobs = s.Features.Jobs.All.Select(j => j.Name).ToArray();
        CollectionAssert.DoesNotContain(jobs, "ScanRecipes");
        CollectionAssert.DoesNotContain(jobs, "Craft");
        CollectionAssert.Contains(jobs, "SurveyScreen");

        // A core file that fails the trust check sends the host to Untrusted with a reason.
        var broken = TestDirs.Paths();
        var trust = ReadInstallFiles(broken);
        var inner = trust.Reader;
        trust.Reader = p => p.EndsWith("procedures.json", StringComparison.OrdinalIgnoreCase) && p.Contains(Path.Combine(broken.InstallDir, "data"), StringComparison.OrdinalIgnoreCase) ? null : inner(p);
        var s2 = Composition.Create(broken, new FakeClock(), null, trust);
        var r2 = CoreDataLoader.Load(s2, Composition.CompiledFeatures(), CompanionConfig.Default);
        Assert.IsFalse(r2.CoreOk);
        CollectionAssert.Contains(r2.Problems.ToArray(), "core.procedures.untrusted");
        Assert.IsNull(s2.Procedures.Get("Reset"));
    }

    [TestMethod]
    public void TheShippedTemplates_AreTheVisionUnitsSeedSet()
    {
        var paths = TestDirs.Paths();
        var s = Composition.Create(paths, new FakeClock(), null, ReadInstallFiles(paths));
        CoreDataLoader.Load(s, Composition.CompiledFeatures(), CompanionConfig.Default);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "hud.bag", "hud.toggle.collapsed", "hud.toggle.expanded", "menu.ability", "craft.make", "craft.qty.max", "craft.fav.anchor", "craft.materials.header", "craft.list.anchor", "craft.make.tab",
                "stock.anchor", "stock.tab", "stock.sub.ability.on", "stock.sub.homestead.on", "stock.sub.tailor.on", "stock.sub.hyperweather.on",
                "gather.tab", "gather.view.anchor", "gather.anchor", "gather.begin", "gather.finish", "gather.set.anchor", "keypad.anchor", "gear.notice.box", "gear.notice.emptybox", "gear.notice.replace",
                "teahouse.title", "teahouse.score", "teahouse.current", "teahouse.frame.topright", "teahouse.frame.bottomright",
            },
            s.Templates.All.Select(t => t.Id).ToArray(),
            "the core seed set, the inventory feature's Stock templates, the crafting feature's Gathering Helper templates (2026-10-15) and the Tea House title (2026-10-30)");
        Assert.AreEqual(TemplateStatus.Verified, s.Templates.Get("hud.toggle.collapsed")!.Status);
        Assert.IsFalse(Directory.Exists(Path.Combine(paths.InstallDir, "templates", "staging")), "staging never ships");
    }

    [TestMethod]
    public void GameBuildCode_UsesFileMetadataOnly_AndIsNullWithoutTheGame()
    {
        var paths = TestDirs.Paths();
        Assert.IsNull(Composition.GameBuildCode(paths));
        Directory.CreateDirectory(paths.GameDir);
        File.WriteAllBytes(paths.GameExePath, new byte[10]);
        var code = Composition.GameBuildCode(paths);
        Assert.IsNotNull(code);
        StringAssert.StartsWith(code, "a-");
        StringAssert.EndsWith(code, "-0-0");
    }
}
