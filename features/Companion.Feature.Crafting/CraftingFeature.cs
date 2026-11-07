using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;

namespace Companion.Feature.Crafting;

/// <summary>
/// Feature "crafting": Craft {item: ItemKey, qty: Int 1..999}, KeepStock {} and CraftLoop {slot, count, batch} (all
/// Commit, gated by automation.crafting and an Allowed character; R10, KS-02). Craft and KeepStock implement the craft
/// flow of SPEC 11.4 (CR-01..07, STK-01..03, PL-01) on three data procedures that still carry needsSurvey stubs, so
/// they end NeedsSurvey before any input. CraftLoop repeats the surveyed Make round (crafting.loop, live run
/// 2026-10-14) without planning. Requires the "recipes" feature (recipes are learned from the screen by ScanRecipes or
/// typed by the user). ScanFavorites {} is NOT a commit job: a read-only scan of the My Favorites recipes (navigation
/// procedure crafting.scanFavorites, no switch, any character permission, a known character for the store writes).
/// ScanRecipeList {} (2026-10-14) is the same kind of read-only scan over the WHOLE recipe list (navigation procedure
/// crafting.scanList: list anchor + offset clicks and drags, the list scrolled two rows at a time until it stops). Both
/// scans open and close each read recipe's item tip (crafting.productTip, 2026-10-15) and take the product name and id
/// the game logs instead of the OCR title, and press each material icon not known yet once to learn its item; the list
/// scan skips, without a click, every card whose picture it learned before (2026-10-15). LearnRecipe {} (2026-10-15) reads
/// only the recipe the user selected in the open Make view, the same way.
/// ForgetRecipes {confirm} (2026-10-15, Maintenance, local only) backs up the character's knowledge and forgets its
/// recipes and recipe-screen stock for one clean rescan.
/// </summary>
public sealed class CraftingFeature : IFeature
{
    /// <summary>Feature id.</summary>
    internal const string Id = "crafting";

    /// <summary>Creates the feature (no side effects).</summary>
    public CraftingFeature()
    {
    }

    /// <inheritdoc />
    public FeatureManifest Manifest { get; } = new(
        Id,
        "1.0.0",
        ContractsInfo.ApiVersion,
        [new FeatureDependency("recipes", "1.0.0")],
        new FeatureDataFiles(Templates: DataFileNames.Templates, Procedures: DataFileNames.Procedures, Readers: DataFileNames.Readers),
        AutomationSwitch.Crafting,
        null,
        "Crafting: plan, commit, await, verify and update stock; Make loop (crafting switch, allowed character)");

    /// <inheritdoc />
    public void Register(IFeatureRegistrar registrar)
    {
        registrar.AddJob(
            new JobDefinition(
                "Craft",
                Id,
                "Craft qty of an item: resync, plan, select recipe, set and read quantity, commit, await, update stock",
                [new JobParamSpec("item", JobParamType.ItemKey, true), new JobParamSpec("qty", JobParamType.Int, true, 0, 999, MaxLength: 3)], // qty 0 = forever
                AutomationSwitch.Crafting,
                JobKind.Commit,
                MaxPerHour: 30),
            new CraftJobHandler());
        registrar.AddJob(
            new JobDefinition(
                "KeepStock",
                Id,
                "Craft up to the keep-stock minimums, within the keep-stock caps (explicit trigger)",
                [],
                AutomationSwitch.Crafting,
                JobKind.Commit,
                MaxPerHour: 30),
            new KeepStockJobHandler());
        registrar.AddJob(
            new JobDefinition(
                KeepStockLoopJobHandler.JobName,
                Id,
                "Rule mode: keep every keep-stock rule's item at its count (read the owned badge, make the difference with gathering), check again every 10 minutes, until stopped",
                [],
                AutomationSwitch.Crafting,
                JobKind.Commit,
                MaxPerHour: 30),
            new KeepStockLoopJobHandler());
        registrar.AddJob(
            new JobDefinition(
                CraftLoopJobHandler.JobName,
                Id,
                "Repeat Make on the selected recipe or a My Favorites slot (optional max quantity) until count items, no craft starts, or stop",
                CraftLoopJobHandler.Params,
                AutomationSwitch.Crafting,
                JobKind.Commit,
                MaxPerHour: 30),
            new CraftLoopJobHandler());
        registrar.AddJob(
            new JobDefinition(
                ScanFavoritesJobHandler.JobName,
                Id,
                "Read-only: select My Favorites slots 1..10 in turn, record each recipe (materials by icon) and the owned counts; never crafts",
                [],
                AutomationSwitch.None,
                JobKind.Navigation,
                MaxPerHour: 30),
            new ScanFavoritesJobHandler());
        registrar.AddJob(
            new JobDefinition(
                ScanRecipeListJobHandler.JobName,
                Id,
                "Read-only: scroll the Make tab's recipe list from the top, select each recipe card, record its recipe and the owned counts; never crafts",
                [new JobParamSpec("find", JobParamType.Int, false, 1, CraftLoopJobHandler.MaxItemId, MaxLength: 18)],
                AutomationSwitch.None,
                JobKind.Navigation,
                MaxPerHour: 20),
            new ScanRecipeListJobHandler());
        registrar.AddJob(
            new JobDefinition(
                GatherJobHandler.JobName,
                Id,
                "Gathering Helper: queue item (game item id) x qty, Begin (the character travels; needs the crafting switch), wait until the game reports it complete, Finish",
                [new JobParamSpec("item", JobParamType.Int, true, 1, CraftLoopJobHandler.MaxItemId, MaxLength: 18), new JobParamSpec("qty", JobParamType.Int, true, 1, 9_999, MaxLength: 4)],
                AutomationSwitch.None,
                JobKind.Navigation,
                MaxPerHour: 30),
            new GatherJobHandler());
        registrar.AddJob(
            new JobDefinition(
                GatherAllJobHandler.JobName,
                Id,
                "Gathering Helper: every raw material of the known recipes, as many as the stock limits allow, 4 per trip, until all were tried",
                [],
                AutomationSwitch.None,
                JobKind.Navigation,
                MaxPerHour: 30),
            new GatherAllJobHandler());
        registrar.AddJob(
            new JobDefinition(
                LearnRecipeJobHandler.JobName,
                Id,
                "Read-only: read the recipe the user selected in the game's Make view (product and new material tips), record it and the owned counts; nothing else is pressed, never crafts",
                [],
                AutomationSwitch.None,
                JobKind.Navigation,
                MaxPerHour: 30),
            new LearnRecipeJobHandler());
        registrar.AddJob(
            new JobDefinition(
                ForgetRecipesJobHandler.JobName,
                Id,
                "Local only (no input): back up this character's knowledge, then forget its recipes and the owned counts read from the Make tab, for one clean rescan (confirm=yes)",
                ForgetRecipesJobHandler.Params,
                AutomationSwitch.None,
                JobKind.Maintenance,
                MaxPerHour: 20),
            new ForgetRecipesJobHandler());
    }
}
