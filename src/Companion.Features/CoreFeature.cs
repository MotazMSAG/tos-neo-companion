using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Features.Kit;

namespace Companion.Features;

/// <summary>
/// The built-in "core" feature (SPEC 9, 11.4): the seven core procedures of data\procedures.json exposed as jobs
/// without parameters. Always enabled; its procedures come from the core data files loaded by the App, so its
/// manifest declares no data. The HUD menu stays EXPANDED (2026-10-14, the far-right toggle is often out of reach of
/// the user's resting cursor, c13): Calibrate ends expanded, and CollapseMenu is the only job that collapses it.
/// </summary>
public sealed class CoreFeature : IFeature
{
    /// <summary>Feature id.</summary>
    internal const string Id = "core";

    /// <summary>Job name = procedure id, kind, description.</summary>
    internal static readonly IReadOnlyList<(string Name, JobKind Kind, string Description)> Jobs =
    [
        ("Reset", JobKind.Navigation, "Esc until the HUD is clean (hide top ui nil)"),
        ("OpenBagKey", JobKind.Navigation, "Reset, open the bag with B, close it with Esc"),
        ("OpenBagClick", JobKind.Navigation, "Reset, open the bag with a slide click on hud.bag, close it with Esc"),
        ("ExpandMenu", JobKind.Navigation, "Reset, expand the HUD menu (no click if already expanded)"),
        ("CollapseMenu", JobKind.Navigation, "Reset, collapse the HUD menu (explicit use only: every other job keeps it expanded)"),
        ("OpenAbility", JobKind.Navigation, "Reset, expand, open the Ability view (needs menu.ability verified)"),
        ("Calibrate", JobKind.Maintenance, "Capture the right HUD strip as found and expanded; leaves the menu expanded"),
    ];

    /// <summary>Creates the core feature (no side effects).</summary>
    public CoreFeature()
    {
    }

    /// <inheritdoc />
    public FeatureManifest Manifest { get; } = new(
        Id,
        "1.0.0",
        ContractsInfo.ApiVersion,
        [],
        new FeatureDataFiles(),
        AutomationSwitch.None,
        null,
        "Core navigation and maintenance procedures as jobs");

    /// <inheritdoc />
    public void Register(IFeatureRegistrar registrar)
    {
        foreach (var (name, kind, description) in Jobs)
        {
            registrar.AddJob(new JobDefinition(name, Id, description, [], AutomationSwitch.None, kind), new ProcedureJobHandler(name));
        }
    }
}
