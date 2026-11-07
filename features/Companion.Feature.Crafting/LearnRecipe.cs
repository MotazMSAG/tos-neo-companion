using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Logs;
using Companion.Contracts.Perception;
using Companion.Features.Kit;

namespace Companion.Feature.Crafting;

/// <summary>
/// LearnRecipe {} (2026-10-15; read-only, no commit, no crafting switch, any character permission; store writes need a
/// known character). The user selects a recipe in the game themselves (the Ability view's Make tab open with that recipe
/// shown); the job reads ONLY that open panel, through the shared <see cref="RecipePanel"/> (as ScanFavorites reads a
/// slot):
/// <list type="number">
/// <item>nothing is pressed unless the Make view is on top (the log's top UI; else "make.notOnTop") and both readers
/// confirm the recipe panel (craft.product and craft.pairs, whose identity needs the "Materials Required" header; else
/// "make.panelNotShown") with something selected (else "make.nothingSelected");</item>
/// <item>the product's item tip: the name and id the game logs replace the OCR title;</item>
/// <item>the item tips of the material icons not known yet (once per icon, ever; a short pause before each press);</item>
/// <item>the recipe and the owned counts are stored with the source <see cref="Source"/> (a recipe stored from a
/// favourites slot keeps that source: the tray maps CraftLoop's slots from it).</item>
/// </list>
/// The only presses are those item tips (anchor craft.materials.header + offset, then Esc): never the list, a tab, a slot
/// or Make. Journal: one record with codes, counts and the product's item id.
/// </summary>
internal sealed class LearnRecipeJobHandler : IJobHandler
{
    internal const string JobName = "LearnRecipe";

    /// <summary>Recipe provenance (Recipe.SourceCapture): read from the screen, the recipe the user selected.</summary>
    internal const string Source = "screen:craft.current";

    /// <summary>Procedure code of the journal records (the job has no data procedure of its own).</summary>
    internal const string JournalProcedure = "crafting.learnRecipe";

    internal const string MakeView = "UI_LIFESKILL_MAKE_VIEW";

    /// <summary>Fixed pause before every material tip press (human browsing pace, as ScanRecipeList).</summary>
    internal static readonly TimeSpan TipPause = TimeSpan.FromMilliseconds(300);

    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        var refusal = JobKit.RequireKnowledge(context) ?? JobKit.CheckProcedures(context, [ProductTip.Procedure]);
        if (refusal is not null)
        {
            return refusal;
        }

        var tipData = context.ProcedureCatalog.Get(ProductTip.Procedure)!;
        if (!ProductTip.IsExpectedShape(tipData))
        {
            return JobKit.Failed("procedure.invalid:" + ProductTip.Procedure);
        }

        var ui = context.UiState;
        if (ui.Craft.InProgress)
        {
            return JobKit.Refused("craft.inProgress");
        }

        if (ui.Busy is BusyKind.Revive or BusyKind.Match)
        {
            return JobKit.Refused("busy:" + ui.Busy);
        }

        var tally = new Tally();

        // 1. the open panel only, proved before anything is pressed
        if (!string.Equals(ui.AdvisoryTop, MakeView, StringComparison.Ordinal))
        {
            return End(context, tally, JobKit.Failed("make.notOnTop"));
        }

        var product = context.Screen.Read(ScanFavoritesJobHandler.ProductReader, ct);
        var pairs = context.Screen.Read(ScanFavoritesJobHandler.PairsReader, ct);
        if (product.Status == ReadStatus.NeedsSurvey || pairs.Status == ReadStatus.NeedsSurvey)
        {
            return End(context, tally, JobKit.NeedsSurvey([.. new[] { product, pairs }.Where(r => r.Status == ReadStatus.NeedsSurvey).Select(r => r.ReaderId)]));
        }

        if (product.Status == ReadStatus.ScreenMismatch || pairs.Status == ReadStatus.ScreenMismatch)
        {
            return End(context, tally, JobKit.Failed("make.panelNotShown"));
        }

        var panel = new RecipePanel(context, tipData, JournalProcedure, () => Pause(context, ct), ct);
        var read = panel.Fold(product, pairs);
        if (read.IsBlank)
        {
            return End(context, tally, JobKit.Failed("make.nothingSelected"));
        }

        // 2. the product's item tip: the logged name and id replace the OCR title
        var (tip, named) = panel.NameProduct(read);
        tally.Add(tip.Run);
        tally.Tip = tip;
        if (!tip.Succeeded)
        {
            return End(context, tally, JobKit.FromProcedure(tip.Run, "tip."));
        }

        read = named;
        tally.Read = read;

        // 3. + 4. the material tips of the icons not known yet, then the store
        var stored = panel.Store(read, Source, keepFavorites: true, [("job", JobName)]);
        foreach (var run in stored.MaterialTips.Runs)
        {
            tally.Add(run);
        }

        tally.MaterialTips = stored.MaterialTips;
        if (stored.Outcome is not { } outcome)
        {
            return End(context, tally, JobKit.FromProcedure(stored.MaterialTips.Failed!, "tip."));
        }

        tally.Read = stored.Read;
        tally.Outcome = outcome;
        return End(context, tally, outcome switch
        {
            { Recorded: true } => JobKit.Result(JobState.Succeeded, "recipe.learned"),
            { Code: "recipe.keptComplete" or "recipe.keptFavorite" } => JobKit.Result(JobState.Succeeded, "recipe.kept"),
            _ => JobKit.Failed("recipe.unreadable"),
        });
    }

    private static void Pause(IJobContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        context.Clock.Sleep(TipPause, ct);
    }

    private static JobResult End(IJobContext context, Tally tally, JobResult result)
    {
        var data = tally.Data();
        if (result.Data is not null)
        {
            foreach (var (k, v) in result.Data)
            {
                data.TryAdd(k, v);
            }
        }

        var record = tally.Data();
        record["event"] = "learnRecipe.end";
        record["code"] = result.Code;
        JobKit.Journal(context, JournalKind.Stock, JournalProcedure, record);
        return result with { Data = data };
    }

    /// <summary>What the job did (codes, counts and the product's item id only).</summary>
    private sealed class Tally
    {
        private int _inputs;
        private int _clicks;
        private int _esc;

        public ItemTip.Outcome? Tip { get; set; }

        public FavoriteRead? Read { get; set; }

        public ProductTip.MaterialTips? MaterialTips { get; set; }

        public ScreenRecipes.StoreOutcome? Outcome { get; set; }

        public void Add(ProcedureResult r)
        {
            _inputs += r.InputBatches;
            _clicks += r.Clicks;
            _esc += r.EscPresses;
        }

        public Dictionary<string, object?> Data()
        {
            var data = JobKit.Data(
                ("recipe", Outcome?.Code ?? "none"),
                ("recipeChanged", Outcome?.Changed ?? false),
                ("materials", (long)(Outcome?.Materials ?? 0)),
                ("unknownMaterials", (long)(Read?.UnknownSlots ?? 0)),
                ("namedMaterials", (long)(Read?.Materials.Count(m => m.ItemId is not null) ?? 0)),
                ("observed", (long)(Outcome?.Observed ?? 0)),
                ("materialTips", (long)(MaterialTips?.Presses ?? 0)),
                ("iconsLearned", (long)(MaterialTips?.Learned ?? 0)),
                ("inputs", (long)_inputs),
                ("clicks", (long)_clicks),
                ("esc", (long)_esc));
            foreach (var (k, v) in ItemTip.Fields(Tip))
            {
                data[k] = v;
            }

            return data;
        }
    }
}
