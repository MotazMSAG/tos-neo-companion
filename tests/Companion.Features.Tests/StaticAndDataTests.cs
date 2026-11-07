using System.Text.Json;
using System.Xml.Linq;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Perception;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.Features.Kit;

namespace Companion.Features.Tests;

[TestClass]
public sealed class StaticRuleTests
{
    internal static string CompanionRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !(File.Exists(Path.Combine(d.FullName, "SPEC.md")) && Directory.Exists(Path.Combine(d.FullName, "features"))))
        {
            d = d.Parent;
        }

        Assert.IsNotNull(d, "companion root not found above the test output");
        return d.FullName;
    }

    private static IEnumerable<string> Sources(string dir) =>
        Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static string[] FeatureDirs() =>
        Directory.GetDirectories(Path.Combine(CompanionRoot(), "features"), "Companion.Feature.*");

    [TestMethod]
    public void FeatureSourcesAndTheSharedKitPassTheFeatureStaticRules()
    {
        var root = CompanionRoot();
        Assert.HasCount(6, FeatureDirs());
        var files = FeatureDirs().SelectMany(Sources).Concat(Sources(Path.Combine(root, "src", "Companion.Features", "Kit"))).ToList();
        Assert.IsTrue(files.Count >= 15, files.Count.ToString());
        var tokens = StaticRules.ForbiddenTokens.Concat(StaticRules.FeatureForbiddenTokens).Concat(StaticRules.RestrictedTokens.Keys).ToList();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var token in tokens)
            {
                Assert.IsFalse(text.Contains(token, StringComparison.Ordinal), $"{Path.GetFileName(file)} contains '{token}'");
            }
        }
    }

    [TestMethod]
    public void TheFeatureHostPassesTheGeneralStaticRulesAndDeclaresNoPInvoke()
    {
        var root = CompanionRoot();
        var tokens = StaticRules.ForbiddenTokens.Concat(StaticRules.RestrictedTokens.Keys).Concat(["DllImport", "LibraryImport"]).ToList();
        foreach (var file in Sources(Path.Combine(root, "src", "Companion.Features")))
        {
            var text = File.ReadAllText(file);
            foreach (var token in tokens)
            {
                Assert.IsFalse(text.Contains(token, StringComparison.Ordinal), $"{Path.GetFileName(file)} contains '{token}'");
            }
        }

        Assert.IsFalse(StaticRules.PInvokeAllowlist.ContainsKey("Companion.Features"));
    }

    [TestMethod]
    public void FeatureProjectsReferenceOnlyCompanionContracts()
    {
        var root = CompanionRoot();
        var projects = FeatureDirs().SelectMany(d => Directory.GetFiles(d, "*.csproj")).Append(Path.Combine(root, "src", "Companion.Features", "Companion.Features.csproj")).ToList();
        Assert.HasCount(7, projects);
        foreach (var csproj in projects)
        {
            var doc = XDocument.Load(csproj);
            var refs = doc.Descendants("ProjectReference").Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value)).ToList();
            CollectionAssert.AreEqual(StaticRules.FeatureAllowedReferences.ToArray(), refs.ToArray(), csproj);
            Assert.IsFalse(doc.Descendants("PackageReference").Any(), csproj);
            Assert.IsFalse(doc.Descendants("AllowUnsafeBlocks").Any(e => e.Value.Trim() == "true"), csproj);
        }
    }
}

[TestClass]
public sealed class FeatureDataTests
{
    private static readonly Dictionary<string, string> FolderById = new()
    {
        ["inventory"] = "Companion.Feature.Inventory",
        ["recipes"] = "Companion.Feature.Recipes",
        ["crafting"] = "Companion.Feature.Crafting",
        ["survey"] = "Companion.Feature.Survey",
        ["homestead"] = "Companion.Feature.Homestead",
        ["teahouse"] = "Companion.Feature.TeaHouse",
    };

    [TestMethod]
    public void EveryFeatureManifestIsValidAndItsDataFileDeserializesWithTheContractSerializer()
    {
        var root = StaticRuleTests.CompanionRoot();
        var templates = JsonSerializer.Deserialize<TemplateDefinition[]>(File.ReadAllBytes(Path.Combine(root, "templates", "templates.json")), ContractJson.Options)!
            .ToDictionary(t => t.Id, StringComparer.Ordinal);
        foreach (var feature in T.RealFeatures())
        {
            var m = feature.Manifest;
            Assert.AreEqual(ContractsInfo.ApiVersion, m.CoreApiVersion, m.Id);
            Assert.AreEqual(DataFileNames.Procedures, m.Data.Procedures, m.Id);

            // Only the inventory feature (the Stock tab), the crafting feature (the Gathering Helper) and the Tea House
            // (its title, 2026-10-30) bring their own templates: full-scale, verified, cut from tests\TestData\images, a
            // PNG next to its templates.json; no id clashes with the core ones.
            var own = new Dictionary<string, TemplateDefinition>(templates, StringComparer.Ordinal);
            if (m.Id is "inventory" or "crafting" or "teahouse")
            {
                Assert.AreEqual(DataFileNames.Templates, m.Data.Templates);
                var dir = Path.Combine(root, "features", FolderById[m.Id], "data");
                foreach (var t in JsonSerializer.Deserialize<TemplateDefinition[]>(File.ReadAllBytes(Path.Combine(dir, DataFileNames.Templates)), ContractJson.Options)!)
                {
                    Assert.IsTrue(own.TryAdd(t.Id, t), "duplicate template " + t.Id);
                    Assert.AreEqual((TemplateStatus.Verified, 1, AutomationSwitch.None), (t.Status, t.SourceScale, t.Gate), t.Id);
                    Assert.IsFalse(t.Tags.HasFlag(TargetTags.Commit) || t.Tags.HasFlag(TargetTags.NavFixed), t.Id);
                    Assert.IsTrue(File.Exists(Path.Combine(dir, t.ImageFile)), t.ImageFile);
                    StringAssert.Contains(t.Source, "tests/TestData/images/", t.Id);
                }
            }
            else
            {
                Assert.IsNull(m.Data.Templates, m.Id);
            }

            var file = Path.Combine(root, "features", FolderById[m.Id], "data", "procedures.json");
            var procedures = JsonSerializer.Deserialize<ProcedureDefinition[]>(File.ReadAllBytes(file), ContractJson.Options)!;
            Assert.IsTrue(procedures.Length > 0, m.Id);
            foreach (var p in procedures)
            {
                Assert.IsTrue(p.Id.StartsWith(m.Id + ".", StringComparison.Ordinal), p.Id);
                Assert.IsTrue(p.Limits.MaxInputBatches <= HardRules.CeilingInputBatchesPerProcedure && p.Limits.MaxClicks <= HardRules.CeilingClicksPerProcedure && p.Limits.MaxEsc <= HardRules.CeilingEscPerProcedure, p.Id);
                Assert.IsTrue(p.Limits.MaxCountedPerStep <= ProcedureLimits.Default.MaxCountedPerStep && p.Limits.PatienceMs <= ProcedureLimits.Default.PatienceMs, p.Id);
                Assert.AreEqual(p.Steps.Count, p.Steps.Select(s => s.Id).Distinct().Count(), p.Id);
                var commits = p.Steps.Where(s => s.Flags.HasFlag(StepFlags.Commit)).ToList();
                if (commits.Count > 0 || p.Kind == ProcedureKind.Commit)
                {
                    Assert.AreEqual(m.Switch, p.Gate, p.Id);
                    Assert.AreNotEqual(AutomationSwitch.None, p.Gate, p.Id);
                    Assert.IsTrue(commits.All(s => !s.Flags.HasFlag(StepFlags.Optional)), p.Id);
                }

                // Only proven inputs: whitelisted keys, Esc, navigation through the map, captures, reads, and clicks on
                // templates cut VERIFIED from a full-scale capture (live run 2026-10-14). An offset click hangs off a screen
                // anchor and never commits; a commit click is a commit template gated by the feature's switch.
                foreach (var step in p.Steps.Where(s => s.Action is ClickAction))
                {
                    var click = (ClickAction)step.Action;
                    Assert.IsTrue(own.TryGetValue(click.TemplateId, out var t), p.Id + ": no template " + click.TemplateId);
                    Assert.AreEqual((TemplateStatus.Verified, 1), (t!.Status, t.SourceScale), p.Id + "/" + step.Id);
                    if (click.Offset is not null)
                    {
                        Assert.IsTrue(t.Tags.HasFlag(TargetTags.ScreenAnchor) && !step.Flags.HasFlag(StepFlags.Commit), p.Id + "/" + step.Id);
                    }

                    if (step.Flags.HasFlag(StepFlags.Commit))
                    {
                        Assert.IsTrue(t.Tags.HasFlag(TargetTags.Commit) && t.Gate == m.Switch, p.Id + "/" + step.Id);
                    }
                }
                foreach (var key in p.Steps.Select(s => s.Action).OfType<KeyAction>())
                {
                    Assert.IsTrue(HardRules.AllowedKeys.ContainsKey(key.Key), p.Id);
                }

                // The HUD menu stays expanded for good (2026-10-14): no feature procedure collapses or restores it.
                Assert.IsFalse(p.Steps.Any(s => s.Action is RestoreHudAction or ToggleHudAction { Target: HudState.Collapsed }), p.Id);
            }
        }
    }

    [TestMethod]
    public void TheDataFilesSpellOutTheIntendedSteps()
    {
        var (_, env, _) = T.LoadReal();
        var scan = env.Procedures.Get("inventory.scanBag")!;
        CollectionAssert.AreEqual(new[] { "reset", "open", "survey.bag.grid", "read", "close" }, scan.Steps.Select(s => s.Id).ToArray());
        Assert.AreEqual(GameKey.B, ((KeyAction)scan.Steps[1].Action).Key);
        Assert.IsTrue(scan.Steps[1].Pre.HudClean);
        Assert.AreEqual(StepFlags.CompensateOnPark, scan.Steps[1].Flags);
        CollectionAssert.AreEqual(UiExpectation.Opened("UI_BAG_MAIN").ToArray(), scan.Steps[1].Expect.UiEvents!.ToArray());
        Assert.AreEqual(UiExpectation.HiddenTop(null), scan.Steps[0].Expect.UiEvents!.Single());
        Assert.AreEqual(UiExpectation.HiddenTop("UI_BAG_MAIN"), scan.Steps[4].Expect.UiEvents!.Single());
        Assert.AreEqual(new ReadAction("bag.grid", "bag"), scan.Steps[3].Action);
        Assert.AreEqual(ProcedureLimits.Default with { MaxClicks = 0 }, scan.Limits);

        var commit = env.Procedures.Get("crafting.commit")!;
        Assert.AreEqual((ProcedureKind.Commit, AutomationSwitch.Crafting), (commit.Kind, commit.Gate));
        Assert.AreEqual(StepFlags.Commit, commit.Steps[0].Flags);
        var awaitStep = commit.Steps[1];
        Assert.IsInstanceOfType<AwaitCraftAction>(awaitStep.Action);
        Assert.HasCount(2, awaitStep.Expect.Alternatives!);
        Assert.AreEqual("hud.collapsed", awaitStep.Expect.Alternatives![0].Screen);

        foreach (var id in new[] { "survey.captureCrops", "survey.captureFull" })
        {
            var p = env.Procedures.Get(id)!;
            Assert.IsTrue(p.Steps.All(s => s.Action is CaptureAction), id);
            Assert.AreEqual((0, 0, 0), (p.Limits.MaxInputBatches, p.Limits.MaxClicks, p.Limits.MaxEsc), id);
        }

        Assert.AreEqual(new NavigateAction("lumi_hut"), env.Procedures.Get("homestead.status")!.Steps[2].Action);
        Assert.AreEqual((ProcedureKind.Commit, AutomationSwitch.Homestead), (env.Procedures.Get("homestead.collect")!.Kind, env.Procedures.Get("homestead.collect")!.Gate));
    }
}

[TestClass]
public sealed class ReadFoldTests
{
    [TestMethod]
    public void BagCellsFoldExactCountsAndRangesAndSkipUnknownValues()
    {
        var read = T.Read("bag.grid", 900,
            T.Text("name.1", " Herb "), T.Int("count.1", 12),
            T.Text("name.2", "Ore"), new ReadValue("count.2", ReadStatus.Ok, null, null, null, new ValueRange(14_000, 14_999), 0.8, "abbrev"),
            T.Text("name.3", "Water"), T.Unknown("count.3"),
            T.Unknown("name.4"), T.Int("count.4", 5));

        var obs = ReadFold.Observations(read, ObservationKind.BagPartial, "bag:visible");

        Assert.HasCount(2, obs);
        Assert.AreEqual((new ItemKey("herb"), (long?)12, T.Pos(900)), (obs[0].Item, obs[0].Count, obs[0].AtLog));
        Assert.AreEqual((new ItemKey("ore"), (long?)null, new ValueRange(14_000, 14_999)), (obs[1].Item, obs[1].Count, obs[1].Range));
    }

    [TestMethod]
    public void WithoutALogPositionOrAConfirmedScreenNothingIsFolded()
    {
        var noLog = T.Read("bag.grid", 1, T.Text("name.1", "Herb"), T.Int("count.1", 1)) with { LogAt = null };
        Assert.HasCount(0, ReadFold.Observations(noLog, ObservationKind.BagPartial, "x"));
        var mismatch = T.Read("bag.grid", 1, T.Text("name.1", "Herb"), T.Int("count.1", 1)) with { Status = ReadStatus.ScreenMismatch };
        Assert.HasCount(0, ReadFold.Observations(mismatch, ObservationKind.BagPartial, "x"));
        Assert.IsNull(ReadFold.RecipeFrom(mismatch, "lifeskill", DateTimeOffset.UnixEpoch));
    }

    [TestMethod]
    public void RecipesAreReadUnverifiedAndPartialUnlessEveryFieldIsOk()
    {
        var complete = T.Read("craft.pairs", 50,
            T.Text("product.name", "Small Potion"), T.Int("product.qty", 2),
            T.Text("name.1", "Herb"), T.Pair("pair.1", 7, 2),
            T.Text("name.2", "Water"), T.Pair("pair.2", 0, 1));
        var recipe = ReadFold.RecipeFrom(complete, "lifeskill", DateTimeOffset.UnixEpoch)!;
        Assert.AreEqual("small potion|lifeskill|0", recipe.Id);
        Assert.AreEqual((2, RecipeConfidence.Complete, false), (recipe.OutputQty, recipe.Confidence, recipe.UserVerified));
        CollectionAssert.AreEqual(new[] { new MaterialRequirement(new ItemKey("herb"), 2), new MaterialRequirement(new ItemKey("water"), 1) }, recipe.Materials.ToArray());
        var owned = ReadFold.Observations(complete, ObservationKind.CraftViewPair, "c");
        Assert.AreEqual((7L, 0L), (owned[0].Count!.Value, owned[1].Count!.Value));

        var partial = T.Read("craft.pairs", 50, T.Text("product.name", "Small Potion"), T.Text("name.1", "Herb"), T.Unknown("pair.1"));
        var p = ReadFold.RecipeFrom(partial, "lifeskill", DateTimeOffset.UnixEpoch)!;
        Assert.AreEqual((1, RecipeConfidence.Partial), (p.OutputQty, p.Confidence));
        Assert.HasCount(0, p.Materials);
    }

    [TestMethod]
    public void CodesAreSanitisedAndBounded()
    {
        Assert.AreEqual("a_b_c", Codes.Safe("a<b>c"));
        Assert.AreEqual("none", Codes.Safe(null));
        Assert.AreEqual(120, Codes.Safe(new string('x', 500)).Length);
        var joined = Codes.Join("needsSurvey:", Enumerable.Range(0, 50).Select(i => "stub.number." + i));
        Assert.IsTrue(joined.Length <= 120 && joined.EndsWith('+'), joined);
        Assert.IsTrue(Codes.IsCode(joined));
    }
}
