using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;

namespace Companion.Feature.Recipes;

/// <summary>
/// Feature "recipes": ScanRecipes (ReadOnlyScan). Opens the Ability (crafting) view, reads the recipe currently
/// shown (product, output, materials with owned/required) and records it UNVERIFIED (the user confirms it in the tray
/// before it can be used for a commit) plus the owned counts as stock observations. Recipes are learned only from the
/// screen or the user (REC-01). NeedsSurvey until <c>menu.ability</c> is cut and <c>craft.pairs</c> is surveyed.
/// </summary>
public sealed class RecipesFeature : IFeature
{
    /// <summary>Feature id.</summary>
    internal const string Id = "recipes";

    /// <summary>Procedure run by ScanRecipes (data\procedures.json).</summary>
    internal const string ScanProcedure = "recipes.scanRecipes";

    /// <summary>Station code of recipes read from the Ability view.</summary>
    internal const string Station = "lifeskill";

    /// <summary>Creates the feature (no side effects).</summary>
    public RecipesFeature()
    {
    }

    /// <inheritdoc />
    public FeatureManifest Manifest { get; } = new(
        Id,
        "1.0.0",
        ContractsInfo.ApiVersion,
        [],
        new FeatureDataFiles(Procedures: DataFileNames.Procedures),
        AutomationSwitch.None,
        null,
        "Recipe scan: read the recipe shown in the crafting view (read-only)");

    /// <inheritdoc />
    public void Register(IFeatureRegistrar registrar) =>
        registrar.AddJob(
            new JobDefinition("ScanRecipes", Id, "Open the crafting view, read the shown recipe and the owned materials", [], AutomationSwitch.None, JobKind.ReadOnlyScan),
            new ScanRecipesHandler());
}
