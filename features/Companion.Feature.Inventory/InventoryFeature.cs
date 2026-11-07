using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;

namespace Companion.Feature.Inventory;

/// <summary>
/// Feature "inventory": ScanBag (ReadOnlyScan, key-only: Reset, B, read the visible bag grid, Esc). A bag scan is
/// always partial (visible tab and slots only, SCAN-02); unseen items stay unknown. Clicks inside the bag (tabs,
/// cells) are not used: they would fall under the crafting gate and need surveyed templates (SCAN-01).
/// NeedsSurvey until the <c>bag.grid</c> reader is surveyed (its data procedure carries the explicit stub).
/// ScanStock {} (2026-10-15) is a read-only Navigation scan of the Ability view's Stock tab (every sub-tab's grid,
/// item names and ids learned once from the item tips, then counts only; procedures inventory.scanStock and
/// inventory.stockTip, the Stock templates and readers in this feature's data files; no switch, any character
/// permission, a known character for the store writes).
/// </summary>
public sealed class InventoryFeature : IFeature
{
    /// <summary>Feature id.</summary>
    internal const string Id = "inventory";

    /// <summary>Procedure run by ScanBag (data\procedures.json).</summary>
    internal const string ScanBagProcedure = "inventory.scanBag";

    /// <summary>Creates the feature (no side effects).</summary>
    public InventoryFeature()
    {
    }

    /// <inheritdoc />
    public FeatureManifest Manifest { get; } = new(
        Id,
        "1.1.0",
        ContractsInfo.ApiVersion,
        [],
        new FeatureDataFiles(Templates: DataFileNames.Templates, Procedures: DataFileNames.Procedures, Readers: DataFileNames.Readers),
        AutomationSwitch.None,
        null,
        "Bag scan (B, read the visible grid, Esc) and Stock scan (the Ability view's Stock tab, every sub-tab; read-only)");

    /// <inheritdoc />
    public void Register(IFeatureRegistrar registrar)
    {
        registrar.AddJob(
            new JobDefinition("ScanBag", Id, "Open the bag with B, read the visible slots into the stock, close it with Esc", [], AutomationSwitch.None, JobKind.ReadOnlyScan),
            new ScanBagHandler());
        registrar.AddJob(
            new JobDefinition(
                ScanStockJobHandler.JobName,
                Id,
                "Read-only: open the Ability view's Stock tab, read every sub-tab's items and counts (each new icon's name from its item tip, once); never crafts or buys",
                [],
                AutomationSwitch.None,
                JobKind.Navigation,
                MaxPerHour: 20),
            new ScanStockJobHandler());
    }
}
