using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;

namespace Companion.Feature.Homestead;

/// <summary>
/// Feature "homestead": HomesteadStatus (ReadOnlyScan: open Lumi's Hut, read products stored/capacity, Esc) and
/// HomesteadCollect (Commit, gated by automation.homestead and an Allowed character). Both are NeedsSurvey until the
/// <c>menu.homestead</c> template is cut (Calibrate), the <c>lumi.products</c> reader is surveyed and the collect
/// button is known; their data procedures carry the explicit stubs, so nothing is sent until then.
/// </summary>
public sealed class HomesteadFeature : IFeature
{
    /// <summary>Feature id.</summary>
    internal const string Id = "homestead";

    /// <summary>Status procedure (data\procedures.json).</summary>
    internal const string StatusProcedure = "homestead.status";

    /// <summary>Collect procedure (Commit, gate homestead).</summary>
    internal const string CollectProcedure = "homestead.collect";

    /// <summary>Creates the feature (no side effects).</summary>
    public HomesteadFeature()
    {
    }

    /// <inheritdoc />
    public FeatureManifest Manifest { get; } = new(
        Id,
        "1.0.0",
        ContractsInfo.ApiVersion,
        [],
        new FeatureDataFiles(Procedures: DataFileNames.Procedures),
        AutomationSwitch.Homestead,
        null,
        "Lumi's Hut status (read-only) and product collection (commit, homestead switch)");

    /// <inheritdoc />
    public void Register(IFeatureRegistrar registrar)
    {
        registrar.AddJob(
            new JobDefinition("HomesteadStatus", Id, "Open Lumi's Hut, read products stored/capacity, close it", [], AutomationSwitch.None, JobKind.ReadOnlyScan),
            new HomesteadStatusHandler());
        registrar.AddJob(
            new JobDefinition("HomesteadCollect", Id, "Collect Lumi's Hut products (needs the homestead switch and an allowed character)", [], AutomationSwitch.Homestead, JobKind.Commit, MaxPerHour: 30),
            new HomesteadCollectHandler());
    }
}
