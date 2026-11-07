using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.GameIo;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Perception;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.Features.Kit;

namespace Companion.Features.Tests;

/// <summary>
/// LearnRecipe (2026-10-15) against fakes: the open Make view's panel as scripted craft.product / craft.pairs reads, a
/// fake engine for the real crafting.productTip procedure (the product tip and the material tips log what the test says),
/// a fake knowledge store. No window, no game, no log.
/// </summary>
[TestClass]
public sealed class LearnRecipeTests
{
    private const string MakeView = "UI_LIFESKILL_MAKE_VIEW";
    private static readonly string Wheat = "icon:" + new string('0', 64);
    private static readonly string Salt = "icon:" + new string('f', 64);
    private static readonly PointI Header = new(1270, 641);

    [TestMethod]
    public void TheJobIsAReadOnlyNavigationJob_WithNoParamsAndNoSwitch()
    {
        var (host, _, _) = T.LoadReal();
        var def = T.Definition(host, "LearnRecipe");
        Assert.AreEqual(("crafting", JobKind.Navigation, AutomationSwitch.None, 0), (def.FeatureId, def.Kind, def.Switch, def.Params.Count));
    }

    [TestMethod]
    public void TheSelectedRecipe_IsReadNamedByItsTip_ItsNewMaterialsLearned_AndStored()
    {
        var w = new World();
        var r = w.Run();

        Assert.AreEqual((JobState.Succeeded, "recipe.learned"), (r.State, r.Code), Data(r));
        var k = w.Ctx.KnowledgeFake!;
        var recipe = k.Recipes.Single();
        Assert.AreEqual((new ItemKey("white toast"), 20_100_001L, "screen:craft.current", RecipeConfidence.Complete),
            (recipe.Product, recipe.ProductItemId, recipe.SourceCapture, recipe.Confidence), "the logged name and id, not the OCR title");
        CollectionAssert.AreEqual(
            new[] { new MaterialRequirement(new ItemKey("wheat flour"), 1, 30_000_001), new MaterialRequirement(new ItemKey("salt"), 3, 30_000_002) },
            recipe.Materials.ToArray(),
            "both materials named by their tips");
        Assert.AreEqual(25L, k.Items[new ItemKey("white toast")].Count);
        Assert.AreEqual(40L, k.Items[new ItemKey("wheat flour")].Count);
        Assert.HasCount(2, k.IconItems);

        // only item tips were pressed: the product's, then each new material once
        Assert.IsTrue(w.Ctx.Runner.Runs.All(p => p.Id == "crafting.productTip"));
        Assert.HasCount(3, w.Ctx.Runner.Runs);
        Assert.AreEqual((3L, 2L, 2L, 6L), ((long)r.Data!["materialTips"]! + 1, (long)r.Data["iconsLearned"]!, (long)r.Data["materials"]!, (long)r.Data["inputs"]!));
        Assert.AreEqual(20_100_001L, r.Data["itemId"]);
        var end = w.Ctx.JournalFake.Records.Single(j => Equals(j.Fields.GetValueOrDefault("event"), "learnRecipe.end"));
        Assert.AreEqual(("recipe.learned", "tip.logged"), (end.Fields["code"], end.Fields["tip"]));
        Assert.IsTrue(w.Ctx.JournalFake.Records.All(j => j.Fields.Values.All(v => v is not string s || Codes.IsCode(s))), "codes only");

        // a second time: the materials are known, only the product's tip is pressed
        var again = w.Run();
        Assert.AreEqual(JobState.Succeeded, again.State, Data(again));
        Assert.HasCount(4, w.Ctx.Runner.Runs);
    }

    [TestMethod]
    public void NothingIsPressed_WhenTheMakeViewIsNotOnTop_OrNoRecipePanelIsShown()
    {
        // another UI on top (the bag, the announcement, or nothing open): a clear code, zero runs, not even a read
        foreach (var top in new[] { "UI_BAG_MAIN", "UI_LIFE_STAGE_PREVIEW", null })
        {
            var w = new World { Top = top };
            var r = w.Run();
            Assert.AreEqual((JobState.Failed, "make.notOnTop"), (r.State, r.Code), top);
            Assert.HasCount(0, w.Ctx.Runner.Runs, top);
            Assert.AreEqual(0, w.Reads, top);
            Assert.HasCount(0, w.Ctx.KnowledgeFake!.Recipes);
        }

        // the Make view on top but another tab (Stock, Learn): the panel's readers do not confirm it
        var stock = new World { PanelShown = false };
        Assert.AreEqual((JobState.Failed, "make.panelNotShown"), Code(stock.Run()));
        Assert.HasCount(0, stock.Ctx.Runner.Runs);

        // nothing selected
        var blank = new World { Blank = true };
        Assert.AreEqual((JobState.Failed, "make.nothingSelected"), Code(blank.Run()));
        Assert.HasCount(0, blank.Ctx.Runner.Runs);

        // the usual refusals, before anything else
        var unknown = new World();
        unknown.Ctx.KnowledgeFake = null;
        Assert.AreEqual((JobState.Refused, "knowledge.unavailable"), Code(unknown.Run()));
        var crafting = new World();
        crafting.Ctx.Ui = T.Snapshot(100, new CraftSessionInfo(true, "UI_LIFE_ALCHEMIST_MAKE_POT", DateTime.Now, T.Pos(50), 3, null, null, false)) with { AdvisoryStack = [MakeView] };
        Assert.AreEqual((JobState.Refused, "craft.inProgress"), Code(crafting.Run()));
        Assert.HasCount(0, crafting.Ctx.Runner.Runs);
    }

    [TestMethod]
    public void AFavouritesRecipe_KeepsItsSlot_AndATipRunThatFails_StoresNothing()
    {
        var w = new World();
        var favourite = new Recipe("white toast|lifeskill|0", new ItemKey("white toast"), 1, "lifeskill", [new MaterialRequirement(new ItemKey("x"), 9)], [], null,
            DateTimeOffset.UnixEpoch, "screen:craft.fav/1", false, RecipeConfidence.Complete, null, null, ProductItemId: 20_100_001);
        w.Ctx.KnowledgeFake!.Recipes.Add(favourite);
        var r = w.Run();
        Assert.AreEqual((JobState.Succeeded, "recipe.kept"), Code(r), Data(r));
        Assert.AreSame(favourite, w.Ctx.KnowledgeFake.Recipes.Single(), "the tray maps CraftLoop's slots from this source");
        Assert.AreEqual(25L, w.Ctx.KnowledgeFake.Items[new ItemKey("white toast")].Count, "the owned counts are still observed");

        var failing = new World { EndTipWith = ProcedureStatus.Halted };
        var f = failing.Run();
        Assert.AreEqual(JobState.Halted, f.State, Data(f));
        Assert.HasCount(0, failing.Ctx.KnowledgeFake!.Recipes);
    }

    private static (JobState, string) Code(JobResult r) => (r.State, r.Code);

    private static string Data(JobResult r) => r.Data is null ? string.Empty : string.Join(", ", r.Data.Select(kv => kv.Key + "=" + kv.Value));

    /// <summary>The open Make view: the panel's reads, and a fake engine for the item tip runs.</summary>
    private sealed class World : IScreenService
    {
        private readonly FeatureHost _host;
        private long _offset = 1000;

        public World()
        {
            var (host, env, _) = T.LoadReal();
            _host = host;
            Ctx = new FakeJobContext(env.Procedures) { ScreenOverride = this };
            Ctx.Runner.Script = Engine;
        }

        public FakeJobContext Ctx { get; }

        public string? Top { get; init; } = MakeView;

        public bool PanelShown { get; init; } = true;

        public bool Blank { get; init; }

        public ProcedureStatus? EndTipWith { get; init; }

        public int Reads { get; private set; }

        public JobResult Run()
        {
            if (Ctx.Ui.AdvisoryTop != Top && !Ctx.Ui.Craft.InProgress)
            {
                Ctx.Ui = Ctx.Ui with { AdvisoryStack = Top is null ? [] : [Top] };
            }

            return T.Handler(_host, "LearnRecipe").Execute(Ctx, JobParameters.Empty, CancellationToken.None);
        }

        public CaptureResult Capture(CancellationToken ct) => throw new NotSupportedException("the job reads, it does not capture");

        public HudClassification ClassifyHudStable(CancellationToken ct) => throw new NotSupportedException();

        public ScreenReadResult Read(string readerId, CancellationToken ct)
        {
            Reads++;
            if (!PanelShown)
            {
                return new ScreenReadResult(readerId, ReadStatus.ScreenMismatch, [], T.Pos(_offset), DateTimeOffset.UnixEpoch);
            }

            _offset += 10;
            if (readerId == "craft.product")
            {
                return Blank
                    ? T.Read(readerId, _offset, new ReadValue("product.name", ReadStatus.Ok, null, null, null, null, 0.9, "absent"))
                    : T.Read(readerId, _offset, T.Text("product.name", "ams"), T.Int("product.owned", 25));
            }

            Assert.AreEqual("craft.pairs", readerId);
            if (Blank)
            {
                return T.Read(readerId, _offset, new ReadValue("materials", ReadStatus.Ok, 0, null, null, null, 0.9, "no-tiles"));
            }

            var read = T.Read(
                readerId,
                _offset,
                new ReadValue("materials", ReadStatus.Ok, 2, null, null, null, 0.9, "tiles"),
                new ReadValue("icon.1", ReadStatus.Ok, null, null, Wheat, null, 0.9, "icon", Rect: new RectI(1154, 674, 1241, 763)),
                T.Pair("pair.1", 40, 1),
                new ReadValue("icon.2", ReadStatus.Ok, null, null, Salt, null, 0.9, "icon", Rect: new RectI(1286, 674, 1373, 763)),
                T.Pair("pair.2", 45, 3));
            return read with { Anchors = new Dictionary<string, PointI> { ["craft.materials.header"] = Header } };
        }

        private ProcedureResult Engine(ProcedureDefinition p)
        {
            Assert.AreEqual("crafting.productTip", p.Id, "only item tips are pressed");
            if (EndTipWith is { } end)
            {
                var halt = end == ProcedureStatus.Halted ? new HaltReport(HaltCode.ForbiddenUi, "UI_RECHARGE", "j", p.Id, "tip.open", DateTimeOffset.UnixEpoch, null) : null;
                return T.Result(p.Id, end, halt: halt, inputs: 1);
            }

            var click = (ClickAction)p.Steps[0].Action;
            var at = Header.Offset(click.Offset!.Value.X, click.Offset.Value.Y);
            (string Name, long Id) item = click.Offset == new PointI(-140, -266) ? ("White Toast", 20_100_001)
                : at.X < 1270 ? ("Wheat Flour", 30_000_001)
                : ("Salt", 30_000_002);
            _offset += 10;
            Ctx.Ui = Ctx.Ui with { AsOf = T.Pos(_offset), LastItemTip = new ItemTipInfo(item.Name, item.Id, new DateTime(2026, 10, 15, 2, 0, 0), T.Pos(_offset)) };
            return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 2);
        }
    }
}
