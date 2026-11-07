using System.Text;
using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Host;
using Companion.Contracts.Vision;

namespace Companion.Features.Tests;

[TestClass]
public sealed class LoaderTests
{
    [TestMethod]
    public void RealFeaturesLoadEnabledAndTheWhitelistHasExactlyTheirJobs()
    {
        var (host, env, reports) = T.LoadReal();

        Assert.AreEqual("core", reports[0].FeatureId);
        CollectionAssert.AreEqual(new[] { "core", "inventory", "recipes", "crafting", "survey", "homestead", "teahouse" }, reports.Select(r => r.FeatureId).ToArray());
        foreach (var r in reports)
        {
            Assert.AreEqual(FeatureLoadStatus.Enabled, r.Status, $"{r.FeatureId}: {r.ReasonCode}");
        }

        CollectionAssert.AreEquivalent(T.ExpectedJobs, host.Jobs.All.Select(j => j.Name).ToArray());

        // Feature procedures came from the trusted data files, tagged with their source.
        string[] procedures =
        [
            "inventory.scanBag", "inventory.scanStock", "inventory.stockTip", "recipes.scanRecipes", "crafting.resync", "crafting.prepare", "crafting.commit", "crafting.loop", "crafting.scanFavorites", "crafting.scanList",
            "crafting.productTip", "crafting.gather", "crafting.gatherTip", "crafting.gearNotice", "crafting.qty", "crafting.makeSnapshot", "survey.captureCrops", "survey.captureFull", "homestead.status", "homestead.collect",
            "teahouse.board", "teahouse.board.alt", "teahouse.restart",
        ];
        CollectionAssert.AreEquivalent(procedures, env.Procedures.All.Select(p => p.Id).ToArray());
        Assert.AreEqual("feature:crafting", env.Procedures.Sources["crafting.commit"]);

        // The inventory feature also brings the Stock tab's templates (PNGs next to its templates.json) and readers, the
        // crafting feature the Gathering Helper's (2026-10-15), the Tea House its title (2026-10-30).
        CollectionAssert.AreEquivalent(
            new[]
            {
                "stock.anchor", "stock.sub.ability.on", "stock.sub.homestead.on", "stock.sub.tailor.on", "stock.sub.hyperweather.on",
                "gather.view.anchor", "gather.anchor", "gather.begin", "gather.finish", "gather.set.anchor", "keypad.anchor",
                "gear.notice.box", "gear.notice.emptybox", "gear.notice.replace",
                "teahouse.title", "teahouse.score", "teahouse.current", "teahouse.frame.topright", "teahouse.frame.bottomright",
            },
            env.Templates.All.Select(t => t.Id).ToArray());
        Assert.IsTrue(env.Templates.Sources.All(s => s is "feature:inventory" or "feature:crafting" or "feature:teahouse"));
        CollectionAssert.AreEquivalent(
            new[] { "inventory.stock.grid", "inventory.stock.ability", "inventory.stock.homestead", "inventory.stock.tailor", "inventory.stock.hyperweather", "crafting.gather.grid", "crafting.gather.set.any", "crafting.gather.set", "crafting.gather.set.silent", "crafting.gather.queue", "crafting.gear.notice", "crafting.gear.emptybox" },
            env.Readers.All.Select(r => r.Id).ToArray());
        Assert.IsTrue(env.Trust.Reads.All(p => p.StartsWith(env.Paths.FeaturesDataDir, StringComparison.OrdinalIgnoreCase)));

        // One FeatureLoad journal record per feature, codes only.
        Assert.AreEqual(7, env.Journal.Records.Count(r => r.Kind == JournalKind.FeatureLoad));
        CollectionAssert.AreEquivalent(reports.ToArray(), host.Reports.ToArray());
    }

    [TestMethod]
    public void TheCraftLoopsSlotClicks_CarrySelectionVerified_AndMakeDoesNot()
    {
        // 2026-10-21: the favourites cell clicks may use the off-screen anchor patch rule inside the commit round
        var (_, env, _) = T.LoadReal();
        var loop = env.Procedures.Get("crafting.loop")!;
        var slots = loop.Steps.Where(s => s.Id.StartsWith("slot.", StringComparison.Ordinal)).ToList();
        Assert.HasCount(10, slots);
        Assert.IsTrue(slots.All(s => s.Flags.HasFlag(Companion.Contracts.Engine.StepFlags.SelectionVerified)), string.Join(",", slots.Select(s => s.Id + ":" + s.Flags)));
        Assert.IsFalse(loop.Steps.Single(s => s.Id == "make").Flags.HasFlag(Companion.Contracts.Engine.StepFlags.SelectionVerified));
    }

    [TestMethod]
    public void JobDefinitionsHaveTheSpecifiedKindsSwitchesAndParams()
    {
        var (host, _, _) = T.LoadReal();

        foreach (var name in new[] { "Reset", "OpenBagKey", "OpenBagClick", "ExpandMenu", "CollapseMenu", "OpenAbility" })
        {
            var d = T.Definition(host, name);
            Assert.AreEqual(JobKind.Navigation, d.Kind, name);
            Assert.AreEqual("core", d.FeatureId);
            Assert.HasCount(0, d.Params);
        }

        Assert.AreEqual(JobKind.Maintenance, T.Definition(host, "Calibrate").Kind);
        Assert.AreEqual(JobKind.ReadOnlyScan, T.Definition(host, "ScanBag").Kind);
        Assert.AreEqual(JobKind.ReadOnlyScan, T.Definition(host, "ScanRecipes").Kind);
        Assert.AreEqual((JobKind.Navigation, AutomationSwitch.None), (T.Definition(host, "ScanFavorites").Kind, T.Definition(host, "ScanFavorites").Switch));
        Assert.AreEqual(JobKind.ReadOnlyScan, T.Definition(host, "HomesteadStatus").Kind);
        Assert.AreEqual(JobKind.Survey, T.Definition(host, "SurveyScreen").Kind);

        var craft = T.Definition(host, "Craft");
        Assert.AreEqual(JobKind.Commit, craft.Kind);
        Assert.AreEqual(AutomationSwitch.Crafting, craft.Switch);
        Assert.AreEqual(JobParamType.ItemKey, craft.Params.Single(p => p.Name == "item").Type);
        var qty = craft.Params.Single(p => p.Name == "qty");
        Assert.AreEqual((JobParamType.Int, 0L, 999L, true), (qty.Type, qty.Min!.Value, qty.Max!.Value, qty.Required), "qty 0 = forever (2026-10-16)");

        var keep = T.Definition(host, "KeepStock");
        Assert.AreEqual((JobKind.Commit, AutomationSwitch.Crafting, 0), (keep.Kind, keep.Switch, keep.Params.Count));

        var collect = T.Definition(host, "HomesteadCollect");
        Assert.AreEqual((JobKind.Commit, AutomationSwitch.Homestead), (collect.Kind, collect.Switch));

        var survey = T.Definition(host, "SurveyScreen").Params.Single();
        Assert.AreEqual(("fullFrame", JobParamType.Bool, false), (survey.Name, survey.Type, survey.Required));

        // Every commit job is gated (GATE-01).
        foreach (var d in host.Jobs.All.Where(j => j.Kind == JobKind.Commit))
        {
            Assert.AreNotEqual(AutomationSwitch.None, d.Switch, d.Name);
        }
    }

    [TestMethod]
    public void CoreIsAlwaysFirstAndEnabledEvenWhenConfigDisablesItOrItIsPassedTwice()
    {
        var env = new TestEnv
        {
            Config = CompanionConfig.Default with { Features = new Dictionary<string, bool> { ["core"] = false, ["alpha"] = false } },
        };
        var host = new FeatureHost(env.Clock);
        var reports = host.LoadAll([new CoreFeature(), new TestFeature("alpha"), new CoreFeature()], env.Context);

        Assert.HasCount(2, reports);
        Assert.AreEqual(("core", FeatureLoadStatus.Enabled), (reports[0].FeatureId, reports[0].Status));
        Assert.AreEqual(("alpha", FeatureLoadStatus.DisabledByConfig), (reports[1].FeatureId, reports[1].Status));
        Assert.HasCount(7, host.Jobs.All);
    }

    [TestMethod]
    public void AFeatureMissingFromConfigIsEnabled()
    {
        var env = new TestEnv();
        var host = new FeatureHost(env.Clock);
        var reports = host.LoadAll([new TestFeature("extra")], env.Context);
        Assert.AreEqual(FeatureLoadStatus.Enabled, reports[1].Status);
    }

    [TestMethod]
    public void AThrowingRegisterDisablesOnlyThatFeatureAndKeepsNothingItRegistered()
    {
        var env = new TestEnv();
        var host = new FeatureHost(env.Clock);
        var throwing = new TestFeature("beta", register: r =>
        {
            r.AddJob(new JobDefinition("BetaJob", "beta", "x", [], AutomationSwitch.None, JobKind.Navigation), new OkHandler());
            r.AddProcedure(new ProcedureDefinition("beta.p", "x", [new StepDefinition("w", StatePredicate.Any, new WaitAction(1), Expectation.Nothing)], ProcedureLimits.Default));
            throw new InvalidOperationException("feature bug");
        });

        var reports = host.LoadAll([new TestFeature("alpha"), throwing, new TestFeature("gamma")], env.Context);

        Assert.AreEqual(FeatureLoadStatus.Enabled, reports[1].Status);
        Assert.AreEqual((FeatureLoadStatus.Failed, "register.threw:InvalidOperationException"), (reports[2].Status, reports[2].ReasonCode));
        Assert.AreEqual(FeatureLoadStatus.Enabled, reports[3].Status);
        Assert.IsFalse(host.Jobs.TryGet("BetaJob", out _, out _));
        Assert.IsNull(env.Procedures.Get("beta.p"));
        Assert.IsTrue(host.Jobs.TryGet("Jobalpha", out _, out _));
        Assert.IsTrue(host.Jobs.TryGet("Jobgamma", out _, out _));
    }

    [TestMethod]
    public void ARegisterOverItsBudgetTimesOutAndItsLateRegistrationsAreRejected()
    {
        var env = new TestEnv();
        var host = new FeatureHost(env.Clock, registerBudgetMs: 1000);
        using var release = new ManualResetEventSlim(false);
        using var lateDone = new ManualResetEventSlim(false);
        Exception? late = null;
        var slow = new TestFeature("slow", register: r =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            try
            {
                r.AddJob(new JobDefinition("SlowJob", "slow", "x", [], AutomationSwitch.None, JobKind.Navigation), new OkHandler());
            }
            catch (Exception ex)
            {
                late = ex;
            }
            finally
            {
                lateDone.Set();
            }
        });

        IReadOnlyList<FeatureLoadReport> reports;
        try
        {
            reports = host.LoadAll([slow, new TestFeature("fast")], env.Context);
        }
        finally
        {
            release.Set();
        }

        Assert.IsTrue(lateDone.Wait(TimeSpan.FromSeconds(10)));
        Assert.AreEqual((FeatureLoadStatus.Timeout, "register.timeout"), (reports[1].Status, reports[1].ReasonCode));
        Assert.AreEqual(FeatureLoadStatus.Enabled, reports[2].Status);
        Assert.IsInstanceOfType<InvalidOperationException>(late);
        Assert.IsFalse(host.Jobs.TryGet("SlowJob", out _, out _));
    }

    [TestMethod]
    public void AnApiMismatchIsReportedAndTheFeatureNeverRegisters()
    {
        var env = new TestEnv();
        var host = new FeatureHost(env.Clock);
        var future = new TestFeature("future", api: 2);
        var reports = host.LoadAll([future], env.Context);

        Assert.AreEqual((FeatureLoadStatus.ApiMismatch, "api.mismatch:2"), (reports[1].Status, reports[1].ReasonCode));
        Assert.AreEqual(0, future.RegisterCalls);
    }

    [TestMethod]
    public void DependenciesLoadInTopologicalOrderAndADisabledDependencyCascades()
    {
        var env = new TestEnv();
        var host = new FeatureHost(env.Clock);
        var order = new List<string>();
        TestFeature F(string id, params FeatureDependency[] deps) => new(id, requires: deps, register: r =>
        {
            lock (order)
            {
                order.Add(id);
            }

            r.AddJob(new JobDefinition(T.JobName(id), id, "x", [], AutomationSwitch.None, JobKind.Navigation), new OkHandler());
        });

        var features = new IFeature[]
        {
            F("later", new FeatureDependency("base", "1.0.0")), // listed before its dependency
            F("base"),
            new TestFeature("broken", register: _ => throw new InvalidOperationException()),
            F("b", new FeatureDependency("broken", "1.0.0")),
            F("c", new FeatureDependency("b", "1.0.0")),
            F("orphan", new FeatureDependency("nowhere", "1.0.0")),
            F("old", new FeatureDependency("base", "2.0.0")),
            F("x", new FeatureDependency("y", "1.0.0")),
            F("y", new FeatureDependency("x", "1.0.0")),
        };
        var reports = host.LoadAll(features, env.Context).ToDictionary(r => r.FeatureId);

        Assert.AreEqual(FeatureLoadStatus.Enabled, reports["later"].Status);
        Assert.AreEqual(FeatureLoadStatus.Enabled, reports["base"].Status);
        Assert.IsTrue(order.IndexOf("base") < order.IndexOf("later"), "a dependency registers before its dependent");
        Assert.AreEqual(FeatureLoadStatus.Failed, reports["broken"].Status);
        Assert.AreEqual((FeatureLoadStatus.DependencyUnavailable, "dependency.disabled:broken"), (reports["b"].Status, reports["b"].ReasonCode));
        Assert.AreEqual((FeatureLoadStatus.DependencyUnavailable, "dependency.disabled:b"), (reports["c"].Status, reports["c"].ReasonCode));
        Assert.AreEqual((FeatureLoadStatus.DependencyUnavailable, "dependency.missing:nowhere"), (reports["orphan"].Status, reports["orphan"].ReasonCode));
        Assert.AreEqual((FeatureLoadStatus.DependencyUnavailable, "dependency.version:base"), (reports["old"].Status, reports["old"].ReasonCode));
        Assert.AreEqual((FeatureLoadStatus.DependencyUnavailable, "dependency.cycle"), (reports["x"].Status, reports["x"].ReasonCode));
        Assert.AreEqual((FeatureLoadStatus.DependencyUnavailable, "dependency.cycle"), (reports["y"].Status, reports["y"].ReasonCode));
        CollectionAssert.DoesNotContain(order, "b");
        CollectionAssert.DoesNotContain(order, "c");
    }

    [TestMethod]
    public void RealCraftingIsDisabledWhenRecipesIsDisabledInConfig()
    {
        var (host, _, reports) = T.LoadReal(env =>
            env.Config = CompanionConfig.Default with { Features = new Dictionary<string, bool>(CompanionConfig.Default.Features) { ["recipes"] = false } });
        var byId = reports.ToDictionary(r => r.FeatureId);
        Assert.AreEqual(FeatureLoadStatus.DisabledByConfig, byId["recipes"].Status);
        Assert.AreEqual((FeatureLoadStatus.DependencyUnavailable, "dependency.disabled:recipes"), (byId["crafting"].Status, byId["crafting"].ReasonCode));
        Assert.IsFalse(host.Jobs.TryGet("Craft", out _, out _));
        Assert.IsTrue(host.Jobs.TryGet("ScanBag", out _, out _));
    }

    [TestMethod]
    public void ADuplicateJobNameDisablesTheLaterFeature()
    {
        var env = new TestEnv();
        var host = new FeatureHost(env.Clock);
        TestFeature WithJob(string id, string job) => new(id, register: r =>
            r.AddJob(new JobDefinition(job, id, "x", [], AutomationSwitch.None, JobKind.Navigation), new OkHandler()));

        var reports = host.LoadAll([WithJob("first", "ScanThing"), WithJob("second", "ScanThing"), WithJob("shadow", "reset")], env.Context);

        Assert.AreEqual(FeatureLoadStatus.Enabled, reports[1].Status);
        Assert.AreEqual((FeatureLoadStatus.Failed, "job.duplicate:ScanThing"), (reports[2].Status, reports[2].ReasonCode));
        Assert.AreEqual((FeatureLoadStatus.Failed, "job.duplicate:reset"), (reports[3].Status, reports[3].ReasonCode));
        Assert.AreEqual("first", T.Definition(host, "ScanThing").FeatureId);
    }

    [TestMethod]
    public void AnUntrustedDataFileDisablesTheFeatureAndNothingIsAdded()
    {
        var (host, env, reports) = T.LoadReal(env => env.Trust.Untrusted.Add(Path.Combine(env.Paths.FeaturesDataDir, "homestead", "procedures.json")));
        var homestead = reports.Single(r => r.FeatureId == "homestead");

        Assert.AreEqual((FeatureLoadStatus.Untrusted, "data.untrusted:procedures.json"), (homestead.Status, homestead.ReasonCode));
        Assert.IsNull(env.Procedures.Get("homestead.status"));
        Assert.IsFalse(host.Jobs.TryGet("HomesteadCollect", out _, out _));
        Assert.IsTrue(host.Jobs.TryGet("SurveyScreen", out _, out _));
    }

    [TestMethod]
    public void InvalidOrDamagedDataFilesFailTheFeature()
    {
        var env = new TestEnv();
        env.Trust.Files[env.FeatureFile("bad", "procedures.json")] = Encoding.UTF8.GetBytes("[ { \"id\": ");
        env.Trust.Files[env.FeatureFile("nulls", "procedures.json")] = Encoding.UTF8.GetBytes("[ null ]");
        var host = new FeatureHost(env.Clock);
        var reports = host.LoadAll(
        [
            new TestFeature("bad", data: new FeatureDataFiles(Procedures: "procedures.json")),
            new TestFeature("nulls", data: new FeatureDataFiles(Procedures: "procedures.json")),
        ], env.Context);
        Assert.AreEqual((FeatureLoadStatus.Failed, "data.invalid:procedures.json"), (reports[1].Status, reports[1].ReasonCode));
        Assert.AreEqual((FeatureLoadStatus.Failed, "data.invalid:procedures.json"), (reports[2].Status, reports[2].ReasonCode));
    }

    [TestMethod]
    public void ADataFileWithAUtf8BomIsAccepted()
    {
        var env = new TestEnv();
        var procedure = new ProcedureDefinition("bom.p", "x", [new StepDefinition("w", StatePredicate.Any, new WaitAction(1), Expectation.Nothing)], ProcedureLimits.Default);
        env.Trust.Files[env.FeatureFile("bom", "procedures.json")] =
            [0xEF, 0xBB, 0xBF, .. JsonSerializer.SerializeToUtf8Bytes(new[] { procedure }, ContractJson.Options)];
        var host = new FeatureHost(env.Clock);
        var reports = host.LoadAll([new TestFeature("bom", data: new FeatureDataFiles(Procedures: "procedures.json"))], env.Context);
        Assert.AreEqual(FeatureLoadStatus.Enabled, reports[1].Status, reports[1].ReasonCode);
        Assert.AreEqual("feature:bom", env.Procedures.Sources["bom.p"]);
    }

    [TestMethod]
    public void InvalidManifestsAreRejected()
    {
        var env = new TestEnv();
        var host = new FeatureHost(env.Clock);
        var reports = host.LoadAll(
        [
            new TestFeature("Bad Id"),
            new TestFeature("vers", version: "1.0"),
            new TestFeature("escape", data: new FeatureDataFiles(Procedures: "..\\..\\data\\procedures.json")),
            new TestFeature("rooted", data: new FeatureDataFiles(Procedures: "C:\\x\\procedures.json")),
            new TestFeature("self", requires: [new FeatureDependency("self", "1.0.0")]),
            new TestFeature("ok"),
            new TestFeature("ok"),
            new TestFeature("throws", manifestThrows: true),
            null!,
        ], env.Context);

        Assert.AreEqual("manifest.invalid:id", reports[1].ReasonCode);
        Assert.AreEqual("manifest.invalid:version", reports[2].ReasonCode);
        Assert.AreEqual("manifest.invalid:dataPath", reports[3].ReasonCode);
        Assert.AreEqual("manifest.invalid:dataPath", reports[4].ReasonCode);
        Assert.AreEqual("manifest.invalid:selfDependency", reports[5].ReasonCode);
        Assert.AreEqual(FeatureLoadStatus.Enabled, reports[6].Status);
        Assert.AreEqual((FeatureLoadStatus.Failed, "manifest.duplicateId"), (reports[7].Status, reports[7].ReasonCode));
        Assert.AreEqual((FeatureLoadStatus.Failed, "manifest.threw:InvalidOperationException"), (reports[8].Status, reports[8].ReasonCode));
        Assert.AreEqual((FeatureLoadStatus.Failed, "feature.null"), (reports[9].Status, reports[9].ReasonCode));
        Assert.IsTrue(reports.Skip(1).Where(r => r.FeatureId != "ok").All(r => r.Status != FeatureLoadStatus.Enabled));
        Assert.IsFalse(env.Trust.Reads.Any(), "no data file outside the feature folder is ever read");
    }

    [TestMethod]
    public void CommitJobsAndProceduresMustBeGatedByTheFeaturesOwnSwitch()
    {
        var env = new TestEnv();
        var host = new FeatureHost(env.Clock);
        var steps = new[] { new StepDefinition("go", StatePredicate.Any, new ClickAction("x.commit"), Expectation.Nothing, Flags: StepFlags.Commit) };
        var reports = host.LoadAll(
        [
            new TestFeature("ungated", register: r => r.AddJob(new JobDefinition("Sneaky", "ungated", "x", [], AutomationSwitch.None, JobKind.Commit), new OkHandler())),
            new TestFeature("mismatch", automationSwitch: AutomationSwitch.Crafting, register: r => r.AddJob(new JobDefinition("Other", "mismatch", "x", [], AutomationSwitch.Homestead, JobKind.Commit), new OkHandler())),
            new TestFeature("proc", register: r => r.AddProcedure(new ProcedureDefinition("proc.go", "x", steps, ProcedureLimits.Default))),
            new TestFeature("optional", automationSwitch: AutomationSwitch.Crafting, register: r => r.AddProcedure(new ProcedureDefinition("optional.go", "x", [steps[0] with { Flags = StepFlags.Commit | StepFlags.Optional }], ProcedureLimits.Default, ProcedureKind.Commit, AutomationSwitch.Crafting))),
            new TestFeature("foreign", register: r => r.AddJob(new JobDefinition("Foreign", "someoneelse", "x", [], AutomationSwitch.None, JobKind.Navigation), new OkHandler())),
            new TestFeature("unbounded", register: r => r.AddJob(new JobDefinition("Unbounded", "unbounded", "x", [new JobParamSpec("n", JobParamType.Int, true)], AutomationSwitch.None, JobKind.Navigation), new OkHandler())),
        ], env.Context);

        Assert.AreEqual("job.invalid:commitWithoutSwitch:Sneaky", reports[1].ReasonCode);
        Assert.AreEqual("job.invalid:switchMismatch:Other", reports[2].ReasonCode);
        Assert.AreEqual("procedure.ungatedCommit:proc.go", reports[3].ReasonCode);
        Assert.AreEqual("procedure.optionalCommit:optional.go", reports[4].ReasonCode);
        Assert.AreEqual("job.invalid:featureId:Foreign", reports[5].ReasonCode);
        Assert.AreEqual("param.invalid:bounds:Unbounded", reports[6].ReasonCode);
        Assert.IsTrue(reports.Skip(1).All(r => r.Status == FeatureLoadStatus.Failed));
        Assert.HasCount(7, host.Jobs.All);
    }

    [TestMethod]
    public void ProcedureIdsAlreadyInTheCatalogAreDuplicates()
    {
        var env = new TestEnv();
        env.Procedures.Put(new ProcedureDefinition("Reset", "core", [new StepDefinition("r", StatePredicate.Any, new ResetAction(), Expectation.Nothing)], ProcedureLimits.Default));
        var host = new FeatureHost(env.Clock);
        var reports = host.LoadAll([new TestFeature("dup", register: r => r.AddProcedure(new ProcedureDefinition("Reset", "x", [new StepDefinition("w", StatePredicate.Any, new WaitAction(1), Expectation.Nothing)], ProcedureLimits.Default)))], env.Context);
        Assert.AreEqual((FeatureLoadStatus.Failed, "procedure.duplicate:Reset"), (reports[1].Status, reports[1].ReasonCode));
    }

    [TestMethod]
    public void ABuilderRejectionFailsTheFeatureWithoutWhitelistingItsJobs()
    {
        var (host, env, reports) = T.LoadReal(env => env.Procedures.Reject = p => p.Id == "homestead.collect");
        var homestead = reports.Single(r => r.FeatureId == "homestead");
        Assert.AreEqual((FeatureLoadStatus.Failed, "commit.rejected:procedures:InvalidOperationException"), (homestead.Status, homestead.ReasonCode));
        Assert.IsFalse(host.Jobs.TryGet("HomesteadStatus", out _, out _));
        Assert.IsFalse(host.Jobs.TryGet("HomesteadCollect", out _, out _));
    }

    [TestMethod]
    public void LoadAllRunsOnce()
    {
        var env = new TestEnv();
        var host = new FeatureHost(env.Clock);
        host.LoadAll([], env.Context);
        Assert.ThrowsExactly<InvalidOperationException>(() => host.LoadAll([], env.Context));
    }

    [TestMethod]
    public void TemplatesOfAFeatureValidatedOnAnotherGameBuildBecomeProvisional()
    {
        foreach (var (current, expected, reason) in new (string?, TemplateStatus, string?)[]
                 {
                     ("abc-1", TemplateStatus.Verified, null),
                     ("abc-2", TemplateStatus.Provisional, "templates.provisional:gameBuild"),
                     (null, TemplateStatus.Provisional, "templates.provisional:gameBuild"),
                 })
        {
            var env = new TestEnv();
            var template = new TemplateDefinition(
                "tpl.button", "tpl.png", null, null, new PointI(100, 100), 20, 0.9, 0.1, TemplateStatus.Verified, 1, null,
                TargetTags.Nav, FireOn.Down, AutomationSwitch.None, null, null, "test", "abc-1");
            env.Trust.Files[env.FeatureFile("tpl", "templates.json")] = JsonSerializer.SerializeToUtf8Bytes(new[] { template }, ContractJson.Options);
            env.Trust.Files[env.FeatureFile("tpl", "tpl.png")] = [1, 2, 3];
            var host = new FeatureHost(env.Clock, current);
            var reports = host.LoadAll([new TestFeature("tpl", data: new FeatureDataFiles(Templates: "templates.json"), validatedBuild: "abc-1", register: _ => { })], env.Context);

            Assert.AreEqual(FeatureLoadStatus.Enabled, reports[1].Status, current);
            Assert.AreEqual(reason, reports[1].ReasonCode, current);
            Assert.AreEqual(expected, env.Templates.Get("tpl.button")!.Status, current);
        }
    }

    [TestMethod]
    public void AFeatureTemplateMayNotBeNavFixed()
    {
        // Only the core HUD navigation templates may skip the slid check on a cross-monitor slide (SPEC 7.3).
        var env = new TestEnv();
        var template = new TemplateDefinition(
            "tpl.button", "tpl.png", null, null, new PointI(100, 100), 20, 0.9, 0.1, TemplateStatus.Verified, 1, null,
            TargetTags.Nav | TargetTags.NavFixed, FireOn.Down, AutomationSwitch.None, null, null, "test", null);
        env.Trust.Files[env.FeatureFile("tpl", "templates.json")] = JsonSerializer.SerializeToUtf8Bytes(new[] { template }, ContractJson.Options);
        env.Trust.Files[env.FeatureFile("tpl", "tpl.png")] = [1, 2, 3];
        var host = new FeatureHost(env.Clock);
        var reports = host.LoadAll([new TestFeature("tpl", data: new FeatureDataFiles(Templates: "templates.json"), register: _ => { })], env.Context);
        Assert.AreEqual((FeatureLoadStatus.Failed, "template.navFixedCoreOnly:tpl.button"), (reports[1].Status, reports[1].ReasonCode));
        Assert.IsNull(env.Templates.Get("tpl.button"));
    }

    [TestMethod]
    public void AMissingTemplateImageIsUntrusted()
    {
        var env = new TestEnv();
        var template = new TemplateDefinition(
            "tpl.button", "tpl.png", null, null, new PointI(100, 100), 20, 0.9, 0.1, TemplateStatus.Verified, 1, null,
            TargetTags.Nav, FireOn.Down, AutomationSwitch.None, null, null, "test", null);
        env.Trust.Files[env.FeatureFile("tpl", "templates.json")] = JsonSerializer.SerializeToUtf8Bytes(new[] { template }, ContractJson.Options);
        var host = new FeatureHost(env.Clock);
        var reports = host.LoadAll([new TestFeature("tpl", data: new FeatureDataFiles(Templates: "templates.json"), register: _ => { })], env.Context);
        Assert.AreEqual((FeatureLoadStatus.Untrusted, "data.untrusted:tpl.png"), (reports[1].Status, reports[1].ReasonCode));
        Assert.IsNull(env.Templates.Get("tpl.button"));
    }

    [TestMethod]
    public void JournalRecordsOfTheLoaderAreCodesOnly()
    {
        var (_, env, _) = T.LoadReal();
        var code = new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9_.:,()\[\]/+\- ]{0,120}$");
        foreach (var r in env.Journal.Records)
        {
            foreach (var (k, v) in r.Fields)
            {
                if (v is string s)
                {
                    Assert.IsTrue(code.IsMatch(s), $"{k}={s}");
                }
            }
        }
    }
}
