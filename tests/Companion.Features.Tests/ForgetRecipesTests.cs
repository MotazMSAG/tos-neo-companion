using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Features.Kit;

namespace Companion.Features.Tests;

/// <summary>
/// ForgetRecipes (2026-10-15): a local maintenance job (no procedure, no input) that has the store back itself up and
/// forget the recipes and the recipe-screen stock, for one clean rescan. Fakes only.
/// </summary>
[TestClass]
public sealed class ForgetRecipesTests
{
    private static readonly string Wheat = "icon:" + new string('0', 64);

    private static JobParameters Confirm => new(new Dictionary<string, object> { ["confirm"] = "yes" });

    [TestMethod]
    public void TheJobIsALocalMaintenanceJob_ThatNeedsConfirmYes()
    {
        var (host, _, _) = T.LoadReal();
        var def = T.Definition(host, "ForgetRecipes");
        Assert.AreEqual(("crafting", JobKind.Maintenance, AutomationSwitch.None), (def.FeatureId, def.Kind, def.Switch));

        Assert.IsNotNull(host.Jobs.Validate(def, new Dictionary<string, string> { ["confirm"] = "yes" }, null, out var reason));
        Assert.IsNull(reason);
        Assert.IsNull(host.Jobs.Validate(def, new Dictionary<string, string>(), null, out reason));
        Assert.AreEqual("param.missing:confirm", reason, "an accidental submission without confirm=yes is refused");
        Assert.IsNull(host.Jobs.Validate(def, new Dictionary<string, string> { ["confirm"] = "YES" }, null, out reason));
        Assert.AreEqual("param.enum:confirm", reason);
    }

    [TestMethod]
    public void ItForgetsTheRecipesAndTheRecipeScreenStock_WithNoInput()
    {
        var (host, env, _) = T.LoadReal();
        var ctx = new FakeJobContext(env.Procedures);
        var k = ctx.KnowledgeFake!;
        k.Recipes.Add(new Recipe("ams|lifeskill|0", new ItemKey("ams"), 1, "lifeskill", [new MaterialRequirement(new ItemKey(Wheat), 1)], [], null,
            DateTimeOffset.UnixEpoch, "screen:craft.list/31", false, RecipeConfidence.Partial, null, null));
        k.Items[new ItemKey("ams")] = new StockEntry(new ItemKey("ams"), 0, null, StockSource.Observed, DateTimeOffset.UnixEpoch, T.Pos(10), 0.4, "ability:craft.product", true);
        k.Items[new ItemKey(Wheat)] = new StockEntry(new ItemKey(Wheat), 40, null, StockSource.Observed, DateTimeOffset.UnixEpoch, T.Pos(11), 0.9, "ability:craft.pairs", false);
        k.Items[new ItemKey("typed")] = new StockEntry(new ItemKey("typed"), 3, null, StockSource.UserEntered, DateTimeOffset.UnixEpoch, null, 1, "user", false);
        k.IconItems.Add(new IconItem("icon:" + new string('a', 64), 2001, "Wheat", DateTimeOffset.UnixEpoch));

        var r = T.Handler(host, "ForgetRecipes").Execute(ctx, Confirm, CancellationToken.None);

        Assert.AreEqual((JobState.Succeeded, "recipes.forgotten"), (r.State, r.Code));
        Assert.AreEqual((1L, 2L, "knowledge.json.forget-20261014T120000Z", 0L), ((long)r.Data!["recipes"]!, (long)r.Data["observations"]!, (string)r.Data["backup"]!, (long)r.Data["inputs"]!));
        CollectionAssert.AreEquivalent(new[] { "ability:craft.product", "ability:craft.pairs" }, k.Forgets.Single().ToArray());
        Assert.IsEmpty(k.Recipes);
        CollectionAssert.AreEquivalent(new[] { new ItemKey("typed") }, k.Items.Keys.ToArray());
        Assert.HasCount(1, k.IconItems, "the learned icon items stay (exact, from the log)");
        Assert.HasCount(0, ctx.Runner.Runs, "no procedure, no input");
        Assert.AreEqual(0, ctx.ScreenFake.Calls, "no capture");
        var end = ctx.JournalFake.Records.Single(j => Equals(j.Fields.GetValueOrDefault("event"), "forgetRecipes.end"));
        Assert.AreEqual("recipes.forgotten", end.Fields["code"]);
        Assert.IsTrue(ctx.JournalFake.Records.All(j => j.Fields.Values.All(v => v is not string s || Codes.IsCode(s))), "codes only");
    }

    [TestMethod]
    public void WithoutKnowledgeOrConfirm_NothingIsForgotten()
    {
        var (host, env, _) = T.LoadReal();
        var none = new FakeJobContext(env.Procedures) { KnowledgeFake = null };
        Assert.AreEqual((JobState.Refused, "knowledge.unavailable"), ToCode(T.Handler(host, "ForgetRecipes").Execute(none, Confirm, CancellationToken.None)));

        var ctx = new FakeJobContext(env.Procedures);
        ctx.KnowledgeFake!.Recipes.Add(new Recipe("x|lifeskill|0", new ItemKey("x"), 1, "lifeskill", [new MaterialRequirement(new ItemKey("y"), 1)], [], null,
            DateTimeOffset.UnixEpoch, "screen:craft.fav/1", false, RecipeConfidence.Partial, null, null));
        Assert.AreEqual((JobState.Refused, "param.missing:confirm"), ToCode(T.Handler(host, "ForgetRecipes").Execute(ctx, JobParameters.Empty, CancellationToken.None)));
        Assert.HasCount(1, ctx.KnowledgeFake.Recipes);
        Assert.IsEmpty(ctx.KnowledgeFake.Forgets);
    }

    private static (JobState, string) ToCode(JobResult r) => (r.State, r.Code);
}
