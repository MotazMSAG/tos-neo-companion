using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Perception;
using Companion.Features.Kit;

namespace Companion.Feature.Crafting;

/// <summary>
/// The shared read-and-store of one recipe panel of the Make tab (2026-10-15), used by every job that reads a selected
/// recipe: ScanFavorites (a My Favorites slot), ScanRecipeList (a list card) and LearnRecipe (the recipe the user
/// selected). In order, each step its own call so the job can journal, count and stop between them:
/// <list type="number">
/// <item><see cref="Fold"/>: craft.product and craft.pairs into a <see cref="FavoriteRead"/>, the material icons resolved
/// against the store's known keys and the keys this job already used (one material keeps one key);</item>
/// <item><see cref="NameProduct"/>: the product's item tip (<see cref="ProductTip"/>): the name and id the game logs
/// replace the OCR title;</item>
/// <item><see cref="Store"/>: the item tip of every material whose icon is not a known icon item yet (once per icon,
/// ever; the job's pause before each press), then the recipe and the owned counts (<see cref="ScreenRecipes"/>).</item>
/// </list>
/// One instance per job (the keys used and the icons pressed so far). Inputs only through the tip procedure.
/// </summary>
/// <param name="context">The job's context.</param>
/// <param name="tipData">The data procedure "crafting.productTip" (shape checked by the job).</param>
/// <param name="journalProcedure">The job's procedure id for the material tip records.</param>
/// <param name="pause">The job's fixed pause before every material tip press.</param>
/// <param name="ct">The job's token.</param>
internal sealed class RecipePanel(IJobContext context, ProcedureDefinition tipData, string journalProcedure, Action pause, CancellationToken ct)
{
    private readonly ICharacterKnowledge _knowledge = context.Knowledge!;
    private readonly List<string> _usedKeys = [];
    private readonly HashSet<string> _triedIcons = new(StringComparer.Ordinal);

    /// <summary>Folds the two reads (icon keys resolved against the store and this job, known icons named).</summary>
    public FavoriteRead Fold(ScreenReadResult product, ScreenReadResult pairs)
    {
        var known = IconKeys.Known(_knowledge).Concat(_usedKeys).ToList();
        return FavoriteRead.From(product, pairs, known, _knowledge.GetIconItems());
    }

    /// <summary>
    /// Runs the product's item tip once; the tip and the read named by it (unchanged without a logged tip).
    /// <paramref name="skipKnown"/>: no press when the title names a stored, log-identified recipe (the list scan keeps
    /// pressing: its card pictures are learned from the tip).
    /// </summary>
    public (ItemTip.Outcome Tip, FavoriteRead Read) NameProduct(FavoriteRead read, bool skipKnown = false)
    {
        // The title read already names a stored recipe the game identified before: no press (2026-10-15: the tip press
        // and its Esc were ~3 s of each ~10 s favourites slot); a new or unmatched title still asks the game.
        if (skipKnown && KnownRecipe(read) is { ProductItemId: { } knownId } known)
        {
            var none = new ProcedureResult(tipData.Id, ProcedureStatus.Succeeded, StepOutcome.Ok, null, 0, 0, 0, [], new Dictionary<string, ScreenReadResult>(), [], []);
            return (new ItemTip.Outcome(none, null, "tip.known"), read with { Product = known.Product, ProductItemId = knownId });
        }

        var tip = ProductTip.Read(context, tipData, ct);
        return (tip, tip.Succeeded ? ProductTip.Apply(read, tip) : read);
    }

    /// <summary>
    /// The material tips of the icons not known yet (<paramref name="where"/> goes into their journal records), then the
    /// store under <paramref name="source"/> (<paramref name="keepFavorites"/>: a recipe stored from a favourites slot
    /// keeps that source). When a tip run does not succeed nothing is stored (<see cref="Stored.Outcome"/> null; the job
    /// ends with <see cref="ProductTip.MaterialTips.Failed"/>).
    /// </summary>
    public Stored Store(FavoriteRead read, string source, bool keepFavorites, (string Key, object? Value)[] where)
    {
        var tips = ProductTip.ReadMaterials(context, tipData, read, _triedIcons, pause, journalProcedure, where, ct);
        if (tips.Failed is not null)
        {
            return new Stored(read, tips, null);
        }

        read = read.WithIconItems(_knowledge.GetIconItems());
        _usedKeys.AddRange(read.Materials.Where(m => m.IconKey is not null).Select(m => m.IconKey!));
        return new Stored(read, tips, ScreenRecipes.Store(_knowledge, read, source, keepFavorites));
    }

    /// <summary>
    /// The stored, log-identified recipe this panel shows: the title read names it exactly and the materials shown do
    /// not contradict it, or (truncated or unread titles) its materials with their required amounts match exactly one
    /// stored recipe. Null when unsure.
    /// </summary>
    private Recipe? KnownRecipe(FavoriteRead read)
    {
        var stored = _knowledge.GetRecipes().Where(r => r.ProductItemId is > 0).ToList();
        // incident 29 (2026-10-28): the title OCR read "Sashimi" on the Longevity Sashimi panel; the title alone named
        // Sashimi (1 material, the panel showed 4) and no tip was pressed
        if (read.Product is { } title && stored.FirstOrDefault(r => r.Product == title) is { } byTitle && !ScreenRecipes.Contradicts(read, byTitle))
        {
            return byTitle;
        }

        if (read.Materials.Count == 0 || read.Materials.Any(m => m.ItemId is null || m.Required is null))
        {
            return null;
        }

        var seen = read.Materials.Select(m => (m.ItemId!.Value, m.Required!.Value)).OrderBy(x => x).ToList();
        var matches = stored.Where(r => r.Materials.Count == seen.Count
            && r.Materials.All(m => m.ItemId is > 0)
            && r.Materials.Select(m => (m.ItemId!.Value, (long)m.Qty)).OrderBy(x => x).SequenceEqual(seen)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>What <see cref="Store"/> did.</summary>
    /// <param name="Read">The read as stored (materials named where their icon's item is known).</param>
    /// <param name="MaterialTips">The material tip presses (the caller counts their inputs).</param>
    /// <param name="Outcome">The store's outcome, or null when a material tip run did not succeed.</param>
    internal sealed record Stored(FavoriteRead Read, ProductTip.MaterialTips MaterialTips, ScreenRecipes.StoreOutcome? Outcome);
}
