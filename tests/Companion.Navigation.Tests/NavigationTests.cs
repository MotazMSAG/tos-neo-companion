using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Logs;
using Companion.Contracts.Navigation;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.Engine;
using Companion.EngineNav.Testing;
using Companion.Navigation;

namespace Companion.Navigation.Tests;

internal static class Nav
{
    public static Frame Frame(Harness h) => h.Game.Capture(CancellationToken.None).Frame!;

    public static ScreenIdentification Identify(Harness h) => h.Identifier.Identify(Frame(h), h.Log.State);

    public static UiMapCatalog MapWith(Harness h, UiMap fragment)
    {
        var map = new UiMapCatalog(h.Templates, h.Journal, h.Clock);
        map.AddFragment(Harness.LoadUiMap(), "core");
        map.AddFragment(fragment, "test");
        map.Seal();
        return map;
    }

    public static ButtonDefinition Button(string id, string screen, string? template, GameKey? key, string? leadsTo, string? ui,
        ButtonSafety safety = ButtonSafety.Navigation, SurveyStatus status = SurveyStatus.Known, AutomationSwitch gate = AutomationSwitch.None) =>
        new(id, screen, template, key, leadsTo, ui, safety, gate, status, null);

    public static ScreenDefinition Screen(string id, string logUi, IReadOnlyList<string>? anchors = null, SurveyStatus status = SurveyStatus.Known, bool escCloses = true) =>
        new(id, logUi, anchors ?? [], false, null, status, escCloses, null);
}

[TestClass]
public sealed class IdentifyTests
{
    [TestMethod]
    public void HudCollapsed_VisionOnly()
    {
        var h = new Harness();
        var id = Nav.Identify(h);
        Assert.AreEqual("hud.collapsed", id.ScreenId);
        Assert.AreEqual(IdentificationConfidence.VisionOnly, id.Confidence);
        Assert.AreEqual(HudState.Collapsed, id.Hud);
        Assert.IsNull(id.LogTop);
    }

    [TestMethod]
    public void HudExpanded_VisionOnly()
    {
        var h = new Harness();
        h.Game.Hud = HudState.Expanded;
        var id = Nav.Identify(h);
        Assert.AreEqual("hud.expanded", id.ScreenId);
        Assert.AreEqual(IdentificationConfidence.VisionOnly, id.Confidence);
    }

    [TestMethod]
    [DataRow("UI_BAG_MAIN", "bag")]
    [DataRow("UI_LIFESKILL_MAKE_VIEW", "ability")]
    [DataRow("UI_HOME_PET_VIEW", "lumi_hut")]
    [DataRow("UI_LIFE_ALCHEMIST_MAKE_POT", "craft_station")]
    public void LogScreensWithoutSurveyedAnchors_LogOnly(string ui, string screen)
    {
        var h = new Harness();
        h.Log.Show(ui);
        var id = Nav.Identify(h);
        Assert.AreEqual(screen, id.ScreenId);
        Assert.AreEqual(IdentificationConfidence.LogOnly, id.Confidence);
        Assert.AreEqual(ui, id.LogTop);
    }

    [TestMethod]
    public void TopUiNotInMap_Unknown()
    {
        var h = new Harness();
        h.Log.Show("UI_MAIL");
        var id = Nav.Identify(h);
        Assert.IsNull(id.ScreenId);
        Assert.AreEqual(IdentificationConfidence.Unknown, id.Confidence);
        Assert.AreEqual("top.notInMap", id.DetailCode);
    }

    [TestMethod]
    public void Busy_Unknown()
    {
        var h = new Harness();
        h.Log.Busy = BusyKind.Loading;
        Assert.AreEqual(IdentificationConfidence.Unknown, Nav.Identify(h).Confidence);
    }

    [TestMethod]
    [DataRow(HudState.Unknown)]
    [DataRow(HudState.Absent)]
    [DataRow(HudState.Covered)]
    public void NoTopAndNoHudSignature_Unknown(HudState state)
    {
        var h = new Harness();
        h.Vision.HudOverride = state;
        var id = Nav.Identify(h);
        Assert.AreEqual(IdentificationConfidence.Unknown, id.Confidence);
        Assert.IsNull(id.ScreenId);
    }

    [TestMethod]
    public void VerifiedAnchorsFound_Confirmed_NotFound_Unknown()
    {
        var h = new Harness();
        var map = Nav.MapWith(h, new UiMap(1, [Nav.Screen("test.screen", "UI_TEST_SCREEN", ["hud.bag"])], [], []));
        var identifier = new ScreenIdentifier(map, h.Vision, h.Templates);
        h.Log.Show("UI_TEST_SCREEN");
        var id = identifier.Identify(Nav.Frame(h), h.Log.State);
        Assert.AreEqual("test.screen", id.ScreenId);
        Assert.AreEqual(IdentificationConfidence.Confirmed, id.Confidence);
        h.Vision.LocateFailure["hud.bag"] = MatchFailure.BelowThreshold;
        var miss = identifier.Identify(Nav.Frame(h), h.Log.State);
        Assert.AreEqual(IdentificationConfidence.Unknown, miss.Confidence);
        Assert.AreEqual("anchors.notFound", miss.DetailCode);
    }

    [TestMethod]
    public void ProvisionalAnchorsOnly_LogOnly()
    {
        var h = new Harness();
        var map = Nav.MapWith(h, new UiMap(1, [Nav.Screen("test.screen", "UI_TEST_SCREEN", ["menu.ability"])], [], []));
        var identifier = new ScreenIdentifier(map, h.Vision, h.Templates);
        h.Log.Show("UI_TEST_SCREEN");
        Assert.AreEqual(IdentificationConfidence.LogOnly, identifier.Identify(Nav.Frame(h), h.Log.State).Confidence);
    }
}

[TestClass]
public sealed class PathTests
{
    private static string[] Ids(NavigationPath? p) => p?.Hops.Select(b => b.Id).ToArray() ?? [];

    [TestMethod]
    public void ShortestPaths_OnTheSeedMap()
    {
        var h = new Harness();
        CollectionAssert.AreEqual(new[] { "bag.click.c" }, Ids(h.Planner.FindPath("hud.collapsed", "bag")), "tie broken by button id (ordinal)");
        CollectionAssert.AreEqual(new[] { "bag.click.e" }, Ids(h.Planner.FindPath("hud.expanded", "bag")));
        CollectionAssert.AreEqual(new[] { "hud.expand" }, Ids(h.Planner.FindPath("hud.collapsed", "hud.expanded")));
        CollectionAssert.AreEqual(new[] { "hud.collapse" }, Ids(h.Planner.FindPath("hud.expanded", "hud.collapsed")));
        CollectionAssert.AreEqual(new[] { "hud.expand", "menu.ability" }, Ids(h.Planner.FindPath("hud.collapsed", "ability")));
        CollectionAssert.AreEqual(new[] { "hud.expand", "menu.homestead" }, Ids(h.Planner.FindPath("hud.collapsed", "lumi_hut")));
        Assert.AreEqual(0, h.Planner.FindPath("bag", "bag")!.Hops.Count);
    }

    [TestMethod]
    public void NeverThroughCommitOrForbidden()
    {
        var h = new Harness();
        Assert.IsNull(h.Planner.FindPath("ability", "craft_station"), "station.commit is a Commit button");
        Assert.IsNull(h.Planner.FindPath("hud.collapsed", "craft_station"));
        var map = Nav.MapWith(h, new UiMap(1,
            [Nav.Screen("test.shop", "UI_TEST_SHOP")],
            [Nav.Button("a.shop", "hud.collapsed", "hud.bag", null, "test.shop", "UI_TEST_SHOP", ButtonSafety.Forbidden)], []));
        var planner = new PathPlanner(map, h.Templates);
        Assert.IsNull(planner.FindPath("hud.collapsed", "test.shop"));
    }

    [TestMethod]
    public void ExecutablePathPreferredOverShorterProvisionalOne()
    {
        var h = new Harness();
        var map = Nav.MapWith(h, new UiMap(1,
            [Nav.Screen("test.a", "UI_TEST_A")],
            [
                Nav.Button("a.direct", "hud.collapsed", "menu.ability", null, "test.a", "UI_TEST_A", status: SurveyStatus.Provisional),
                Nav.Button("z.fromBag", "bag", "hud.bag", null, "test.a", "UI_TEST_A"),
            ], []));
        var planner = new PathPlanner(map, h.Templates);
        CollectionAssert.AreEqual(new[] { "bag.click.c", "z.fromBag" }, Ids(planner.FindPath("hud.collapsed", "test.a")));
    }

    [TestMethod]
    public void UnknownScreens_NoPath()
    {
        var h = new Harness();
        Assert.IsNull(h.Planner.FindPath("hud.collapsed", "nowhere"));
        Assert.IsNull(h.Planner.FindPath("nowhere", "bag"));
    }

    [TestMethod]
    public void BuildProcedure_ResetThenOneStepPerHop()
    {
        var h = new Harness();
        var path = h.Planner.FindPath("hud.collapsed", "ability")!;
        var p = h.Planner.BuildProcedure(path, startWithReset: true);
        CollectionAssert.AreEqual(new[] { "reset", "hop1:hud.expand", "hop2:menu.ability" }, p.Steps.Select(s => s.Id).ToArray());
        Assert.IsInstanceOfType<ResetAction>(p.Steps[0].Action);
        var toggle = p.Steps[1];
        Assert.IsInstanceOfType<ToggleHudAction>(toggle.Action);
        Assert.AreEqual(HudState.Expanded, ((ToggleHudAction)toggle.Action).Target);
        Assert.AreEqual(HudState.Expanded, toggle.Expect.Hud);
        Assert.AreEqual("hud.collapsed", toggle.Pre.Screen);
        Assert.AreEqual(true, toggle.Pre.HudClean);
        var click = p.Steps[2];
        Assert.AreEqual("menu.ability", ((ClickAction)click.Action).TemplateId);
        Assert.AreEqual("hud.expanded", click.Pre.Screen);
        CollectionAssert.AreEqual(UiExpectation.Opened("UI_LIFESKILL_MAKE_VIEW").ToArray(), click.Expect.UiEvents!.ToArray());
        Assert.IsTrue(click.Flags.HasFlag(StepFlags.CompensateOnPark), "ability is escCloses");
        Assert.AreEqual(6000, click.TimeoutMs, "provisional target screen gets the first-open timeout");
        Assert.AreEqual(ProcedureKind.Navigation, p.Kind);
        Assert.AreEqual(AutomationSwitch.None, p.Gate);
        // structurally valid for the engine
        var catalog = new ProcedureCatalog(h.Templates, h.Readers, h.Map, h.Journal, h.Clock);
        catalog.Add(p, "nav");
        catalog.Seal();
        Assert.IsNotNull(catalog.Get(p.Id));
    }

    [TestMethod]
    public void BuildProcedure_KeyHopAndNoReset()
    {
        var h = new Harness();
        var path = new NavigationPath("hud.collapsed", "bag", [h.Map.GetButton("bag.key.c")!]);
        var p = h.Planner.BuildProcedure(path, startWithReset: false);
        var s = p.Steps.Single();
        Assert.AreEqual(GameKey.B, ((KeyAction)s.Action).Key);
        Assert.AreEqual(true, s.Pre.HudClean, "letter keys need a clean HUD");
        Assert.IsNull(s.Expect.Screen, "bag anchors are not surveyed: log only");
        CollectionAssert.AreEqual(UiExpectation.Opened("UI_BAG_MAIN").ToArray(), s.Expect.UiEvents!.ToArray());
    }
}

/// <summary>Records runs; simulates the game effect of Reset and of navigation procedures.</summary>
internal sealed class FakeRunner(Harness h) : IProcedureRunner
{
    public List<string> Runs { get; } = [];

    public bool ResetWorks { get; set; } = true;

    public ProcedureStatus NavStatus { get; set; } = ProcedureStatus.Succeeded;

    public ProcedureResult Run(string procedureId, RunContext context, CancellationToken ct) => Run(h.Procedures.Get(procedureId)!, context, ct);

    public ProcedureResult Run(ProcedureDefinition procedure, RunContext context, CancellationToken ct)
    {
        Runs.Add(procedure.Id);
        var status = ProcedureStatus.Succeeded;
        if (procedure.Id == "Reset")
        {
            if (ResetWorks)
            {
                while (h.Log.State.AdvisoryTop is { } top)
                {
                    h.Log.HideTop(top);
                }
                h.Log.HideTop(null);
            }
        }
        else
        {
            status = NavStatus;
            if (status == ProcedureStatus.Succeeded)
            {
                foreach (var step in procedure.Steps)
                {
                    switch (step.Action)
                    {
                        case ToggleHudAction t:
                            h.Game.Hud = t.Target;
                            break;
                        case KeyAction or ClickAction when step.Expect.UiEvents is { Count: > 0 } ev:
                            h.Log.Show(ev[0].UiName!);
                            break;
                    }
                }
            }
        }
        return new ProcedureResult(procedure.Id, status, status == ProcedureStatus.Succeeded ? StepOutcome.Ok : StepOutcome.Refused, null, 1, 0, 0, [],
            new Dictionary<string, Companion.Contracts.Perception.ScreenReadResult>(), [], []);
    }
}

[TestClass]
public sealed class NavigatorTests
{
    private static (Harness H, FakeRunner Runner, Navigator Nav) New()
    {
        var h = new Harness();
        var runner = new FakeRunner(h);
        var nav = new Navigator(h.Screen, h.Identifier, h.Planner, runner, h.Procedures, h.Map, h.Templates, h.Log);
        return (h, runner, nav);
    }

    [TestMethod]
    public void Unknown_ResetThenIdentify_ThenNavigate()
    {
        var (h, runner, nav) = New();
        h.Log.Show("UI_MAIL");
        var r = nav.NavigateTo("bag", h.Context(), CancellationToken.None);
        Assert.AreEqual(NavigationStatus.Arrived, r.Status);
        Assert.AreEqual("bag", r.ScreenId);
        CollectionAssert.AreEqual(new[] { "Reset", "nav.hud.collapsed.bag" }, runner.Runs);
    }

    [TestMethod]
    public void StillUnknownAfterReset_PreconditionLost()
    {
        var (h, runner, nav) = New();
        runner.ResetWorks = false;
        h.Log.Show("UI_MAIL");
        var r = nav.NavigateTo("bag", h.Context(), CancellationToken.None);
        Assert.AreEqual(NavigationStatus.Failed, r.Status);
        Assert.AreEqual(StepOutcome.PreconditionLost, r.Procedure!.FinalOutcome);
        CollectionAssert.AreEqual(new[] { "Reset" }, runner.Runs);
    }

    [TestMethod]
    public void ProvisionalOrNeedsSurveyHop_NeedsSurvey_NoRun()
    {
        var (h, runner, nav) = New();
        Assert.AreEqual(NavigationStatus.NeedsSurvey, nav.NavigateTo("ability", h.Context(), CancellationToken.None).Status);
        Assert.AreEqual(NavigationStatus.NeedsSurvey, nav.NavigateTo("lumi_hut", h.Context(), CancellationToken.None).Status);
        Assert.AreEqual(0, runner.Runs.Count);
        Assert.AreEqual(0, h.Game.Inputs);
    }

    [TestMethod]
    public void AlreadyThere_AndUnknownTarget()
    {
        var (h, runner, nav) = New();
        Assert.AreEqual(NavigationStatus.AlreadyThere, nav.NavigateTo("hud.collapsed", h.Context(), CancellationToken.None).Status);
        Assert.AreEqual(NavigationStatus.NoPath, nav.NavigateTo("nowhere", h.Context(), CancellationToken.None).Status);
        Assert.AreEqual(0, runner.Runs.Count);
    }

    [TestMethod]
    public void LeavingAnEscClosesScreen_GoesThroughReset()
    {
        var (h, runner, nav) = New();
        h.Log.Show("UI_BAG_MAIN");
        var r = nav.NavigateTo("hud.expanded", h.Context(), CancellationToken.None);
        Assert.AreEqual(NavigationStatus.Arrived, r.Status);
        CollectionAssert.AreEqual(new[] { "Reset", "nav.hud.collapsed.hud.expanded" }, runner.Runs);
    }

    [TestMethod]
    public void CommitTarget_NoPath()
    {
        var (h, runner, nav) = New();
        h.Log.Show("UI_LIFESKILL_MAKE_VIEW");
        var r = nav.NavigateTo("craft_station", h.Context(), CancellationToken.None);
        Assert.AreEqual(NavigationStatus.NoPath, r.Status);
        Assert.IsFalse(runner.Runs.Any(id => id.StartsWith("nav.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RunnerFailure_Failed()
    {
        var (h, runner, nav) = New();
        runner.NavStatus = ProcedureStatus.Halted;
        var r = nav.NavigateTo("bag", h.Context(), CancellationToken.None);
        Assert.AreEqual(NavigationStatus.Failed, r.Status);
        Assert.AreEqual(ProcedureStatus.Halted, r.Procedure!.Status);
    }

    [TestMethod]
    public void EndToEnd_WithTheRealEngine()
    {
        var h = new Harness();
        var nav = new Navigator(h.Screen, h.Identifier, h.Planner, h.Runner, h.Procedures, h.Map, h.Templates, h.Log);
        var r = nav.NavigateTo("bag", h.Context(), CancellationToken.None);
        Assert.AreEqual(NavigationStatus.Arrived, r.Status, string.Join(",", r.Procedure?.Attempts.Select(a => a.StepId + ":" + a.Outcome) ?? []));
        Assert.AreEqual(1, h.Game.ClickCount);
        Assert.AreEqual(1, h.Game.Keys(GameKey.Escape));
        var id = nav.IdentifyNow(CancellationToken.None);
        Assert.AreEqual("bag", id.ScreenId);
        h.AssertJournalClean();
    }

    [TestMethod]
    public void IdentifyNow_CaptureFailure_Unknown()
    {
        var (h, _, nav) = New();
        h.Game.Iconic = true;
        var id = nav.IdentifyNow(CancellationToken.None);
        Assert.AreEqual(IdentificationConfidence.Unknown, id.Confidence);
        Assert.AreEqual("capture.Iconic", id.DetailCode);
    }
}

[TestClass]
public sealed class UiMapCatalogTests
{
    [TestMethod]
    public void DuplicateIdsRejectTheWholeFragment()
    {
        var h = new Harness();
        var map = new UiMapCatalog(h.Templates, h.Journal, h.Clock);
        map.AddFragment(Harness.LoadUiMap(), "core");
        var dup = new UiMap(1, [Nav.Screen("bag", "UI_BAG_MAIN"), Nav.Screen("fresh", "UI_FRESH")], [], []);
        Assert.ThrowsExactly<InvalidDataException>(() => map.AddFragment(dup, "feature"));
        Assert.IsNull(map.GetScreen("fresh"), "nothing of a rejected fragment is merged");
    }

    [TestMethod]
    public void InvalidReferencesAndRules_Rejected()
    {
        var h = new Harness();
        var map = new UiMapCatalog(h.Templates, h.Journal, h.Clock);
        map.AddFragment(Harness.LoadUiMap(), "core");
        UiMap[] bad =
        [
            new(1, [], [Nav.Button("x1", "no.screen", "hud.bag", null, "bag", "UI_BAG_MAIN")], []),
            new(1, [], [Nav.Button("x2", "hud.collapsed", "hud.bag", null, "no.screen", null)], []),
            new(1, [], [Nav.Button("x3", "hud.collapsed", "hud.bag", GameKey.B, "bag", null)], []),
            new(1, [], [Nav.Button("x4", "ability", null, null, "craft_station", null, ButtonSafety.Commit, SurveyStatus.NeedsSurvey)], []),
            new(1, [], [Nav.Button("x5", "hud.collapsed", null, null, "bag", null)], []),
            new(1, [], [], [new DenyZone("z1", "hud.collapsed", new RectI(1900, 1000, 1950, 1020), "outside")]),
            new(1, [], [], [new DenyZone("z2", "nowhere", new RectI(0, 0, 10, 10), "x")]),
            new(2, [], [], []),
            new(1, [new ScreenDefinition("hudx", null, [], true, null, SurveyStatus.Known, false, null)], [], []),
            new(1, [new ScreenDefinition("nolog", null, [], false, null, SurveyStatus.Known, true, null)], [], []),
        ];
        foreach (var f in bad)
        {
            Assert.ThrowsExactly<InvalidDataException>(() => map.AddFragment(f, "test"));
        }
    }

    [TestMethod]
    public void MissingTemplatesAtSeal_ButtonNeedsSurvey_AnchorDropped()
    {
        var h = new Harness();
        var map = new UiMapCatalog(h.Templates, h.Journal, h.Clock);
        map.AddFragment(Harness.LoadUiMap(), "core");
        map.AddFragment(new UiMap(1,
            [Nav.Screen("test.s", "UI_TEST_S", ["no.such.anchor", "hud.bag"])],
            [Nav.Button("t.b", "hud.collapsed", "no.such.template", null, "test.s", "UI_TEST_S")], []), "feature");
        map.Seal();
        Assert.AreEqual(SurveyStatus.NeedsSurvey, map.GetButton("t.b")!.Status);
        CollectionAssert.AreEqual(new[] { "hud.bag" }, map.GetScreen("test.s")!.AnchorTemplateIds.ToArray());
        Assert.IsTrue(map.SealReport.Count >= 2);
        Assert.ThrowsExactly<InvalidOperationException>(() => map.AddFragment(new UiMap(1, [], [], []), "late"));
    }

    [TestMethod]
    public void Lookups()
    {
        var h = new Harness();
        Assert.AreEqual(2, h.Map.ButtonsForTemplate("hud.bag").Count);
        Assert.AreEqual(6, h.Map.DenyZonesFor("hud.collapsed").Count);
        Assert.AreEqual(0, h.Map.DenyZonesFor("bag").Count);
        Assert.AreEqual("hud.toggle.collapsed", h.Map.GetButton("hud.expand")!.TemplateId);
    }
}

[TestClass]
public sealed class DataFileTests
{
    private static readonly string[] SpecTemplates = ["hud.bag", "hud.toggle.collapsed", "hud.toggle.expanded", "menu.ability"];

    private static string CompanionRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SPEC.md")))
        {
            d = d.Parent;
        }
        return d?.FullName ?? throw new DirectoryNotFoundException("companion root");
    }

    [TestMethod]
    public void UiMap_MatchesTheSeedTable()
    {
        var m = Harness.LoadUiMap();
        Assert.AreEqual(1, m.SchemaVersion);
        CollectionAssert.AreEquivalent(new[] { "hud.collapsed", "hud.expanded", "bag", "ability", "lumi_hut", "craft_station" }, m.Screens.Select(s => s.Id).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "hud.expand", "hud.collapse", "bag.key.c", "bag.key.e", "bag.click.c", "bag.click.e", "menu.ability", "menu.homestead", "station.commit" },
            m.Buttons.Select(b => b.Id).ToArray());
        CollectionAssert.AreEquivalent(new[] { "hud.market", "hud.event", "hud.whatson", "hud.timed_offer", "hud.left_offers", "chat_panel" }, m.DenyZones.Select(z => z.Id).ToArray());
        var s = m.Screens.ToDictionary(x => x.Id);
        Assert.AreEqual(SurveyStatus.Known, s["hud.collapsed"].Status);
        Assert.AreEqual(SurveyStatus.Provisional, s["hud.expanded"].Status);
        Assert.AreEqual(SurveyStatus.Known, s["bag"].Status);
        Assert.AreEqual(SurveyStatus.Provisional, s["ability"].Status);
        Assert.AreEqual(SurveyStatus.NeedsSurvey, s["lumi_hut"].Status);
        Assert.AreEqual(SurveyStatus.NeedsSurvey, s["craft_station"].Status);
        Assert.IsFalse(s["craft_station"].EscCloses, "never Esc during a craft");
        Assert.IsTrue(s["bag"].EscCloses && s["ability"].EscCloses && s["lumi_hut"].EscCloses);
        Assert.AreEqual(HudState.Collapsed, s["hud.collapsed"].Hud);
        CollectionAssert.AreEqual(new[] { "hud.bag", "hud.toggle.collapsed" }, s["hud.collapsed"].AnchorTemplateIds.ToArray());
        var b = m.Buttons.ToDictionary(x => x.Id);
        Assert.AreEqual(ButtonSafety.Commit, b["station.commit"].Safety);
        Assert.AreEqual(AutomationSwitch.Crafting, b["station.commit"].Gate);
        Assert.AreEqual(SurveyStatus.NeedsSurvey, b["menu.homestead"].Status);
        Assert.IsNull(b["menu.homestead"].TemplateId);
        Assert.AreEqual(GameKey.B, b["bag.key.c"].Key);
        Assert.AreEqual(ButtonSafety.Toggle, b["hud.expand"].Safety);
        foreach (var btn in m.Buttons.Where(x => x.Key is not null))
        {
            Assert.IsTrue(HardRules.AllowedKeys.ContainsKey(btn.Key!.Value));
            Assert.IsFalse(HardRules.DeniedKeys.Contains(HardRules.AllowedKeys[btn.Key.Value]));
        }
    }

    [TestMethod]
    public void UiMap_TemplateReferencesExist()
    {
        var m = Harness.LoadUiMap();
        var refs = m.Screens.SelectMany(s => s.AnchorTemplateIds).Concat(m.Buttons.Where(b => b.TemplateId is not null).Select(b => b.TemplateId!)).Distinct().ToList();
        foreach (var r in refs)
        {
            CollectionAssert.Contains(SpecTemplates, r, "SPEC 14 template");
        }
        var templatesJson = Path.Combine(CompanionRoot(), "templates", "templates.json");
        if (File.Exists(templatesJson))
        {
            var defs = JsonSerializer.Deserialize<TemplateDefinition[]>(File.ReadAllBytes(templatesJson), ContractJson.Options)!;
            var ids = defs.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var r in refs)
            {
                Assert.IsTrue(ids.Contains(r), "templates.json lacks " + r);
            }
        }
    }

    [TestMethod]
    public void DenyZones_GuardTheBagButNeverContainIt()
    {
        var m = Harness.LoadUiMap();
        var bag = new PointI(1748, 312);
        var toggle = new PointI(1855, 318);
        foreach (var z in m.DenyZones)
        {
            Assert.IsFalse(z.ClientRect.Inflate(HardRules.DenyZoneRefusePx).Contains(bag), z.Id);
            Assert.IsFalse(z.ClientRect.Inflate(HardRules.DenyZoneRefusePx).Contains(toggle), z.Id);
        }
        var offer = m.DenyZones.Single(z => z.Id == "hud.timed_offer").ClientRect;
        Assert.IsTrue(bag.X - (offer.Right - 1) is > 40 and < 70, "about 55 px from the timed-offer zone");
    }

    [TestMethod]
    public void Procedures_DeserializeAndCrossReference()
    {
        var procs = Harness.LoadProcedures();
        CollectionAssert.AreEquivalent(new[] { "Reset", "OpenBagKey", "OpenBagClick", "ExpandMenu", "CollapseMenu", "OpenAbility", "Calibrate" }, procs.Select(p => p.Id).ToArray());
        var map = Harness.LoadUiMap();
        var screens = map.Screens.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var p in procs)
        {
            foreach (var s in p.Steps)
            {
                switch (s.Action)
                {
                    case ClickAction c:
                        CollectionAssert.Contains(SpecTemplates, c.TemplateId);
                        break;
                    case NavigateAction n:
                        Assert.IsTrue(screens.Contains(n.ScreenId));
                        break;
                }
                foreach (var t in s.Pre.TemplatesPresent ?? [])
                {
                    CollectionAssert.Contains(SpecTemplates, t);
                }
            }
            Assert.IsTrue(p.Limits.MaxInputBatches <= HardRules.CeilingInputBatchesPerProcedure);
            Assert.IsTrue(p.Limits.MaxEsc <= HardRules.CeilingEscPerProcedure);
            Assert.IsTrue(p.Limits.MaxClicks <= HardRules.CeilingClicksPerProcedure);
        }
        var reset = procs.Single(p => p.Id == "Reset");
        Assert.AreEqual(10, ((ResetAction)reset.Steps[0].Action).MaxEsc);
        Assert.AreEqual(UiVerb.HideTop, reset.Steps[0].Expect.UiEvents![0].Verb);
        Assert.IsNull(reset.Steps[0].Expect.UiEvents![0].UiName);
        var key = procs.Single(p => p.Id == "OpenBagKey");
        Assert.AreEqual(true, key.Steps[1].Pre.HudClean);
        Assert.IsTrue(key.Steps[1].Flags.HasFlag(StepFlags.CompensateOnPark));
        var click = procs.Single(p => p.Id == "OpenBagClick");
        CollectionAssert.AreEqual(new[] { "hud.bag" }, click.Steps[1].Pre.TemplatesPresent!.ToArray());
        var cal = procs.Single(p => p.Id == "Calibrate");
        // 2026-10-14: the menu stays expanded for good; Calibrate no longer collapses or restores it.
        CollectionAssert.AreEqual(new[] { "reset", "initial", "expand", "cap-exp" }, cal.Steps.Select(s => s.Id).ToArray());
        Assert.AreEqual(CaptureScope.HudStripRight, ((CaptureAction)cal.Steps[1].Action).Scope);
        Assert.AreEqual(new ToggleHudAction(HudState.Expanded), cal.Steps[2].Action);
        Assert.IsTrue(cal.Steps[2].Flags.HasFlag(StepFlags.SkipIfSatisfied));
        Assert.AreEqual(HudState.Expanded, cal.Steps[^1].Pre.Hud, "the last step runs, and leaves the menu, expanded");
    }

    [TestMethod]
    public void Procedures_LoadIntoTheCatalogWithoutRejections()
    {
        var h = new Harness();
        Assert.AreEqual(7, h.Procedures.All.Count);
    }

    [TestMethod]
    public void DataFiles_RoundTripWithContractJson()
    {
        var procs = Harness.LoadProcedures();
        var json = JsonSerializer.Serialize(procs, ContractJson.Options);
        var back = JsonSerializer.Deserialize<ProcedureDefinition[]>(json, ContractJson.Options)!;
        Assert.AreEqual(json, JsonSerializer.Serialize(back, ContractJson.Options));
        var map = Harness.LoadUiMap();
        var mjson = JsonSerializer.Serialize(map, ContractJson.Options);
        var mback = JsonSerializer.Deserialize<UiMap>(mjson, ContractJson.Options)!;
        Assert.AreEqual(mjson, JsonSerializer.Serialize(mback, ContractJson.Options));
    }
}
