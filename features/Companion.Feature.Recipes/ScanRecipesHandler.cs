using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Features.Kit;

namespace Companion.Feature.Recipes;

/// <summary>
/// ScanRecipes: known character required; refuses before any input while a stub is reachable; otherwise runs the
/// scan, upserts the read recipe (unverified; an identical, already verified recipe is kept as is) and folds the owned
/// counts.
/// </summary>
internal sealed class ScanRecipesHandler : IJobHandler
{
    /// <summary>Read output key of the procedure's ReadAction (craft.pairs).</summary>
    internal const string ReadKey = "recipe";

    /// <summary>Coverage code of crafting-view observations.</summary>
    internal const string Coverage = "ability:craft.pairs";

    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        var refusal = JobKit.RequireKnowledge(context)
            ?? JobKit.CheckProcedures(context, [RecipesFeature.ScanProcedure]);
        if (refusal is not null)
        {
            return refusal;
        }

        var run = context.Procedures.Run(RecipesFeature.ScanProcedure, context.Run, ct);
        if (run.Status != ProcedureStatus.Succeeded)
        {
            return JobKit.FromProcedure(run);
        }

        var readProblem = JobKit.CheckRead(run, ReadKey, out var read);
        if (readProblem is not null)
        {
            return readProblem;
        }

        var knowledge = context.Knowledge!;
        var data = JobKit.ProcedureData(run);
        var recipe = ReadFold.RecipeFrom(read!, RecipesFeature.Station, context.Clock.UtcNow);
        var observations = ReadFold.Observations(read!, ObservationKind.CraftViewPair, Coverage);
        data["observed"] = (long)observations.Count;
        if (recipe is null)
        {
            return JobKit.Failed("read.noRecipe", data);
        }

        var existing = knowledge.GetRecipes().FirstOrDefault(r => string.Equals(r.Id, recipe.Id, StringComparison.Ordinal));
        var changed = existing is null || !SameContent(existing, recipe);
        if (changed)
        {
            knowledge.UpsertRecipe(recipe);
        }

        if (observations.Count > 0)
        {
            knowledge.ApplyObservations(observations);
        }

        knowledge.Save();
        data["recipeChanged"] = changed;
        data["materials"] = (long)recipe.Materials.Count;
        data["confidence"] = recipe.Confidence.ToString();
        data["userVerified"] = changed ? false : existing!.UserVerified;
        JobKit.Journal(context, JournalKind.Stock, RecipesFeature.ScanProcedure, JobKit.Data(
            ("source", "craft.pairs"),
            ("recipeChanged", changed),
            ("materials", (long)recipe.Materials.Count),
            ("observed", (long)observations.Count)));
        return JobKit.Result(JobState.Succeeded, changed ? "recipe.recorded.unverified" : "recipe.unchanged", data);
    }

    /// <summary>Same product, output, station and materials (order-sensitive, as shown).</summary>
    internal static bool SameContent(Recipe a, Recipe b) =>
        a.Product == b.Product
        && a.OutputQty == b.OutputQty
        && string.Equals(a.Station, b.Station, StringComparison.Ordinal)
        && a.Materials.Count == b.Materials.Count
        && a.Materials.Zip(b.Materials).All(p => p.First.Item == p.Second.Item && p.First.Qty == p.Second.Qty);
}
