using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Host;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Navigation;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;

namespace Companion.Features.Tests;

[TestClass]
public sealed class CoreJobTests
{
    private static readonly string[] CoreProcedures = ["Reset", "OpenBagKey", "OpenBagClick", "ExpandMenu", "CollapseMenu", "OpenAbility", "Calibrate"];

    private static FakeProcedureBuilder CoreCatalog()
    {
        var c = new FakeProcedureBuilder();
        foreach (var id in CoreProcedures)
        {
            StepAction action = id switch
            {
                "OpenBagClick" => new ClickAction("hud.bag"),
                "ExpandMenu" or "OpenAbility" or "Calibrate" => new ToggleHudAction(HudState.Expanded),
                "CollapseMenu" => new ToggleHudAction(HudState.Collapsed),
                "OpenBagKey" => new KeyAction(GameKey.B),
                _ => new ResetAction(),
            };
            c.Put(new ProcedureDefinition(id, "core", [new StepDefinition("s", StatePredicate.Any, action, Expectation.Nothing)], ProcedureLimits.Default));
        }

        return c;
    }

    [TestMethod]
    public void CoreJobsMapOneToOneToTheSevenCoreProcedures()
    {
        var (host, _, _) = T.LoadReal();
        var ctx = new FakeJobContext(CoreCatalog());
        foreach (var id in CoreProcedures)
        {
            var before = ctx.Runner.Runs.Count;
            var r = T.Handler(host, id).Execute(ctx, JobParameters.Empty, CancellationToken.None);
            Assert.AreEqual((JobState.Succeeded, "ok"), (r.State, r.Code), id);
            Assert.AreEqual(before + 1, ctx.Runner.Runs.Count);
            Assert.AreEqual(id, ctx.Runner.Runs[^1].Id);
        }

        CollectionAssert.AreEquivalent(CoreProcedures, host.Jobs.All.Where(j => j.FeatureId == "core").Select(j => j.Name).ToArray());
    }

    [TestMethod]
    public void ProcedureOutcomesMapToJobStatesAndCodes()
    {
        var (host, _, _) = T.LoadReal();
        var ctx = new FakeJobContext(CoreCatalog());
        var halt = new HaltReport(HaltCode.ForbiddenUi, "UI_RECHARGE", "j", "OpenBagClick", "open", DateTimeOffset.UnixEpoch, null);
        var attempt = new StepAttemptRecord("open", 1, StepOutcome.NeedsSurvey, OutcomeClass.Terminal, RecoveryAction.End, null, false, null, "template.provisional:menu.ability", DateTimeOffset.UnixEpoch);
        (ProcedureResult Result, JobState State, string Code)[] cases =
        [
            (T.Result("OpenAbility", ProcedureStatus.Halted, halt: halt, inputs: 2), JobState.Halted, "ForbiddenUi"),
            (T.Result("OpenAbility", ProcedureStatus.NeedsSurvey, attempts: [attempt]), JobState.NeedsSurvey, "needsSurvey:template.provisional:menu.ability"),
            (T.Result("OpenAbility", ProcedureStatus.Parked), JobState.Parked, "parked"),
            (T.Result("OpenAbility", ProcedureStatus.Stopped), JobState.Stopped, "stopped"),
            (T.Result("OpenAbility", ProcedureStatus.GameExited), JobState.GameExited, "gameExited"),
            (T.Result("OpenAbility", ProcedureStatus.Cancelled), JobState.Cancelled, "cancelled"),
            (T.Result("OpenAbility", ProcedureStatus.Refused), JobState.Refused, "refused"),
        ];
        foreach (var (result, state, code) in cases)
        {
            ctx.Runner.Script = _ => result;
            var r = T.Handler(host, "OpenAbility").Execute(ctx, JobParameters.Empty, CancellationToken.None);
            Assert.AreEqual((state, code), (r.State, r.Code));
            Assert.AreEqual((long)result.InputBatches, r.Data!["inputs"]);
        }
    }

    [TestMethod]
    public void AMissingCoreProcedureFailsWithoutRunning()
    {
        var (host, _, _) = T.LoadReal();
        var ctx = new FakeJobContext(new FakeProcedureBuilder());
        var r = T.Handler(host, "Calibrate").Execute(ctx, JobParameters.Empty, CancellationToken.None);
        Assert.AreEqual((JobState.Failed, "procedure.missing:Calibrate"), (r.State, r.Code));
        Assert.HasCount(0, ctx.Runner.Runs);
    }

    [TestMethod]
    public void WithSlideClicksOffClickProceduresAreRefusedAndKeyOnlyOnesRun()
    {
        var (host, _, _) = T.LoadReal();
        var ctx = new FakeJobContext(CoreCatalog());
        ctx.SwitchesFake.SlideClickEnabled = false;
        var click = T.Handler(host, "OpenBagClick").Execute(ctx, JobParameters.Empty, CancellationToken.None);
        var toggle = T.Handler(host, "ExpandMenu").Execute(ctx, JobParameters.Empty, CancellationToken.None);
        Assert.AreEqual((JobState.Refused, "slideClick.off"), (click.State, click.Code));
        Assert.AreEqual((JobState.Refused, "slideClick.off"), (toggle.State, toggle.Code));
        Assert.HasCount(0, ctx.Runner.Runs);

        var key = T.Handler(host, "OpenBagKey").Execute(ctx, JobParameters.Empty, CancellationToken.None);
        Assert.AreEqual(JobState.Succeeded, key.State);
        CollectionAssert.AreEqual(new[] { "OpenBagKey" }, ctx.Runner.RunIds.ToArray());
    }
}

[TestClass]
public sealed class NeedsSurveyStubTests
{
    private static FakeJobContext ContextFor(TestEnv env)
    {
        var ctx = new FakeJobContext(env.Procedures);
        ctx.SwitchesFake.Crafting = true;
        ctx.SwitchesFake.Homestead = true;
        ctx.Config = CompanionConfig.Default with { KeepStock = new KeepStockConfig(Enabled: true) };
        ctx.KnowledgeFake!.Rules.Add(new KeepStockRule(new ItemKey("small potion"), 5));
        return ctx;
    }

    [TestMethod]
    public void EveryStubbedJobEndsNeedsSurveyWithZeroInputsAndSaysWhatToSurvey()
    {
        var (host, env, _) = T.LoadReal();
        var craftParams = new JobParameters(new Dictionary<string, object> { ["item"] = new ItemKey("small potion"), ["qty"] = 3L });
        (string Job, JobParameters Params, string[] Stubs)[] cases =
        [
            ("ScanBag", JobParameters.Empty, ["bag.grid"]),
            ("ScanRecipes", JobParameters.Empty, ["menu.ability", "craft.pairs"]),
            // "Craft" no longer runs the stubbed CraftFlow procedures (CraftPlanRun, 2026-10-15); KeepStock still does
            ("KeepStock", JobParameters.Empty, ["menu.ability", "craft.pairs", "craft.recipe.select", "craft.quantity.set", "craft.quantity", "craft.commit.button"]),
            ("HomesteadStatus", JobParameters.Empty, ["menu.homestead", "lumi.products"]),
            ("HomesteadCollect", JobParameters.Empty, ["menu.homestead", "lumi.products", "homestead.collect.button"]),
        ];

        foreach (var (job, parameters, stubs) in cases)
        {
            var ctx = ContextFor(env);
            var r = T.Handler(host, job).Execute(ctx, parameters, CancellationToken.None);
            Assert.AreEqual(JobState.NeedsSurvey, r.State, $"{job}: {r.Code}");
            Assert.AreEqual("needsSurvey:" + string.Join(",", stubs), r.Code, job);
            Assert.AreEqual(0L, r.Data!["inputs"], job);
            Assert.HasCount(0, ctx.Runner.Runs, job + " ran a procedure");
            Assert.AreEqual(0, ctx.ScreenFake.Calls, job);
            Assert.AreEqual(0, ctx.Nav.IdentifyCalls + ctx.Nav.NavigateCalls.Count, job);
            Assert.HasCount(0, ctx.KnowledgeFake!.Observations, job);
            Assert.AreEqual(0, ctx.KnowledgeFake.Saves, job);
        }
    }

    [TestMethod]
    public void JobsThatWriteTheStoreRefuseWithoutKnowledge()
    {
        var (host, env, _) = T.LoadReal();
        foreach (var job in new[] { "ScanBag", "ScanRecipes" })
        {
            var ctx = ContextFor(env);
            ctx.KnowledgeFake = null; // the store could not open the profile
            var r = T.Handler(host, job).Execute(ctx, JobParameters.Empty, CancellationToken.None);
            Assert.AreEqual((JobState.Refused, "knowledge.unavailable"), (r.State, r.Code), job);
            Assert.HasCount(0, ctx.Runner.Runs);
        }
    }

    [TestMethod]
    public void OnceSurveyedScanBagReadsKeyOnlyAndFoldsPartialObservations()
    {
        var (host, env, _) = T.LoadReal();
        var ctx = new FakeJobContext(T.SurveyedCatalog(env.Procedures));
        ctx.Runner.Script = p => T.Result(p.Id, ProcedureStatus.Succeeded, new Dictionary<string, Contracts.Perception.ScreenReadResult>
        {
            ["bag"] = T.Read("bag.grid", 4000, T.Text("name.01", "Herb"), T.Int("count.01", 12), T.Text("name.02", "Water"), T.Unknown("count.02")),
        }, inputs: 3);

        var r = T.Handler(host, "ScanBag").Execute(ctx, JobParameters.Empty, CancellationToken.None);

        Assert.AreEqual((JobState.Succeeded, "scan.partial"), (r.State, r.Code));
        Assert.AreEqual(1L, r.Data!["observed"]);
        var run = ctx.Runner.Runs.Single();
        Assert.IsTrue(run.Steps.All(s => s.Action is not ClickAction and not ToggleHudAction), "key-only");
        Assert.AreEqual(0, run.Limits.MaxClicks);
        var obs = ctx.KnowledgeFake!.Observations.Single();
        Assert.AreEqual((new ItemKey("herb"), 12L, ObservationKind.BagPartial, "bag:visible"), (obs.Item, obs.Count!.Value, obs.Kind, obs.Coverage));
        Assert.IsFalse(ctx.KnowledgeFake.Items.ContainsKey(new ItemKey("water")), "an unread count stays unknown, never 0");
        Assert.AreEqual("inventory.scanMarksSeen", ctx.NotifierFake.Sent.Single().Code);
    }
}

[TestClass]
public sealed class AutomationSwitchTests
{
    private static readonly JobParameters CraftParams = new(new Dictionary<string, object> { ["item"] = new ItemKey("small potion"), ["qty"] = 3L });

    private static readonly JobParameters LoopParams = new(new Dictionary<string, object> { ["slot"] = 1L, ["count"] = 5L, ["batch"] = "max" });

    [TestMethod]
    public void CommitJobsAreRefusedWithZeroInputsWhileTheirSwitchIsOff()
    {
        var (host, env, _) = T.LoadReal();

        // Even with fully surveyed procedures, a switched-off commit job runs nothing.
        var surveyed = T.SurveyedCatalog(env.Procedures);
        (string Job, JobParameters Params, string Code, Action<FakeSwitches> Set)[] cases =
        [
            ("Craft", CraftParams, "switch.off:crafting", s => s.Homestead = true),
            ("KeepStock", JobParameters.Empty, "switch.off:crafting", s => s.Homestead = true),
            ("CraftLoop", LoopParams, "switch.off:crafting", s => s.Homestead = true),
            ("HomesteadCollect", JobParameters.Empty, "switch.off:homestead", s => s.Crafting = true),
        ];
        foreach (var (job, parameters, code, set) in cases)
        {
            var ctx = new FakeJobContext(surveyed) { Config = CompanionConfig.Default with { KeepStock = new KeepStockConfig(Enabled: true) } };
            set(ctx.SwitchesFake);
            var r = T.Handler(host, job).Execute(ctx, parameters, CancellationToken.None);
            Assert.AreEqual((JobState.Refused, code), (r.State, r.Code), job);
            Assert.HasCount(0, ctx.Runner.Runs, job);
            Assert.AreEqual(0, ctx.ScreenFake.Calls + ctx.Nav.IdentifyCalls, job);
        }
    }

    [TestMethod]
    public void CommitJobsNeedNoCharacterPermission_TheSwitchIsTheirOnlyGate()
    {
        // 4.33 (one knowledge profile for every character): with its switch on, a commit job gets past the gates,
        // whoever is logged in
        var (host, env, _) = T.LoadReal();
        foreach (var job in new[] { "Craft", "KeepStock", "CraftLoop", "HomesteadCollect" })
        {
            var ctx = new FakeJobContext(T.SurveyedCatalog(env.Procedures)) { Config = CompanionConfig.Default with { KeepStock = new KeepStockConfig(Enabled: true) } };
            ctx.SwitchesFake.Crafting = true;
            ctx.SwitchesFake.Homestead = true;
            var parameters = job switch { "Craft" => CraftParams, "CraftLoop" => LoopParams, _ => JobParameters.Empty };
            var r = T.Handler(host, job).Execute(ctx, parameters, CancellationToken.None);
            Assert.IsFalse(r.Code.StartsWith("switch.off", StringComparison.Ordinal) || r.Code.Contains("character", StringComparison.Ordinal), job + ": " + r.Code);
            Assert.IsNull(Companion.Features.Kit.JobKit.CheckCommitGates(ctx, T.Definition(host, job).Switch), job);
        }
    }

    [TestMethod]
    public void ReadOnlyJobsAreNotGatedBySwitches()
    {
        var (host, env, _) = T.LoadReal();
        var ctx = new FakeJobContext(T.SurveyedCatalog(env.Procedures));
        ctx.Runner.Script = p => T.Result(p.Id, ProcedureStatus.Succeeded, new Dictionary<string, Contracts.Perception.ScreenReadResult>
        {
            ["products"] = T.Read("lumi.products", 10, T.Pair("products", 18, 18)),
        });
        var r = T.Handler(host, "HomesteadStatus").Execute(ctx, JobParameters.Empty, CancellationToken.None);
        Assert.AreEqual((JobState.Succeeded, "homestead.full"), (r.State, r.Code));
        Assert.AreEqual((18L, 18L), ((long)r.Data!["stored"]!, (long)r.Data["capacity"]!));
    }

    [TestMethod]
    public void HomesteadCollectWithTheSwitchOnCollectsAndVerifies()
    {
        var (host, env, _) = T.LoadReal();
        var ctx = new FakeJobContext(T.SurveyedCatalog(env.Procedures));
        ctx.SwitchesFake.Homestead = true;
        var stored = 18L;
        ctx.Runner.Script = p =>
        {
            if (p.Id == "homestead.collect")
            {
                Assert.AreEqual((ProcedureKind.Commit, AutomationSwitch.Homestead), (p.Kind, p.Gate));
                stored = 0;
                return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 4);
            }

            return T.Result(p.Id, ProcedureStatus.Succeeded, new Dictionary<string, Contracts.Perception.ScreenReadResult>
            {
                ["products"] = T.Read("lumi.products", 10, T.Pair("products", stored, 18)),
            });
        };

        var r = T.Handler(host, "HomesteadCollect").Execute(ctx, JobParameters.Empty, CancellationToken.None);

        Assert.AreEqual((JobState.Succeeded, "collect.ok"), (r.State, r.Code));
        CollectionAssert.AreEqual(new[] { "homestead.status", "homestead.collect", "homestead.status" }, ctx.Runner.RunIds.ToArray());
        Assert.IsTrue(ctx.KnowledgeFake!.StaleMarks.Any(m => m.Scope == StaleScope.AllStock));
    }
}

[TestClass]
public sealed class SurveyTests
{
    [TestMethod]
    public void SurveyScreenSendsNoInputAndRunsOnlyACaptureOfTheRequestedScope()
    {
        var (host, env, _) = T.LoadReal();
        foreach (var (fullFrame, procedure, scope) in new[] { (false, "survey.captureCrops", CaptureScope.TemplateCrops), (true, "survey.captureFull", CaptureScope.FullFrameMasked) })
        {
            var ctx = new FakeJobContext(env.Procedures);
            ctx.KnowledgeFake = null;
            ctx.Ui = ctx.Ui with { AdvisoryStack = ["UI_BAG_MAIN"] };
            ctx.Nav.Identification = new ScreenIdentification("bag", IdentificationConfidence.LogOnly, "UI_BAG_MAIN", HudState.Covered, "log.top");
            ctx.Runner.Script = p => T.Result(p.Id, ProcedureStatus.Succeeded) with { Artifacts = ["a.png", "b.png"] };
            var parameters = new JobParameters(new Dictionary<string, object> { ["fullFrame"] = fullFrame });

            var r = T.Handler(host, "SurveyScreen").Execute(ctx, parameters, CancellationToken.None);

            Assert.AreEqual((JobState.Succeeded, "survey.saved"), (r.State, r.Code));
            var run = ctx.Runner.Runs.Single();
            Assert.AreEqual(procedure, run.Id);
            Assert.IsTrue(run.Steps.All(s => s.Action is CaptureAction c && c.Scope == scope), "capture only");
            Assert.AreEqual(1, ctx.Nav.IdentifyCalls);
            Assert.HasCount(0, ctx.Nav.NavigateCalls);
            Assert.AreEqual("bag", r.Data!["screen"]);
            Assert.AreEqual("UI_BAG_MAIN", r.Data["logTop"]);
            Assert.AreEqual(2L, r.Data["artifacts"]);
            Assert.AreEqual(fullFrame, r.Data["fullFrame"]);
            Assert.AreEqual(0L, r.Data["inputs"]);
        }
    }

    [TestMethod]
    public void ASurveyProcedureThatCouldSendInputIsRefusedBeforeRunning()
    {
        var (host, _, _) = T.LoadReal();
        var tampered = new FakeProcedureBuilder();
        tampered.Put(new ProcedureDefinition("survey.captureCrops", "x",
        [
            new StepDefinition("c", StatePredicate.Any, new CaptureAction("x", CaptureScope.TemplateCrops), Expectation.Nothing),
            new StepDefinition("k", StatePredicate.Any, new KeyAction(GameKey.Escape), Expectation.Nothing),
        ], ProcedureLimits.Default, ProcedureKind.Maintenance));
        tampered.Put(new ProcedureDefinition("survey.captureFull", "x",
        [
            new StepDefinition("c", StatePredicate.Any, new CaptureAction("x", CaptureScope.TemplateCrops), Expectation.Nothing),
        ], ProcedureLimits.Default, ProcedureKind.Maintenance));
        var ctx = new FakeJobContext(tampered);

        var crops = T.Handler(host, "SurveyScreen").Execute(ctx, JobParameters.Empty, CancellationToken.None);
        var full = T.Handler(host, "SurveyScreen").Execute(ctx, new JobParameters(new Dictionary<string, object> { ["fullFrame"] = true }), CancellationToken.None);

        Assert.AreEqual((JobState.Refused, "survey.notPassive:survey.captureCrops"), (crops.State, crops.Code));
        Assert.AreEqual((JobState.Refused, "survey.scopeMismatch:survey.captureFull"), (full.State, full.Code));
        Assert.HasCount(0, ctx.Runner.Runs);
        Assert.AreEqual(0, ctx.Nav.IdentifyCalls);
    }

    [TestMethod]
    public void AnUnknownScreenIsStillSurveyedAndReportedAsCodes()
    {
        var (host, env, _) = T.LoadReal();
        var ctx = new FakeJobContext(env.Procedures);
        ctx.Nav.Identification = new ScreenIdentification(null, IdentificationConfidence.Unknown, "UI_SOMETHING_NEW", HudState.Unknown, "free text: Bob's \"screen\"");
        var r = T.Handler(host, "SurveyScreen").Execute(ctx, JobParameters.Empty, CancellationToken.None);
        Assert.AreEqual((JobState.Succeeded, "survey.saved.unknownScreen"), (r.State, r.Code));
        Assert.AreEqual("unknown", r.Data!["screen"]);
        Assert.IsFalse(((string)r.Data["identify"]!).Contains('"'));
    }
}
