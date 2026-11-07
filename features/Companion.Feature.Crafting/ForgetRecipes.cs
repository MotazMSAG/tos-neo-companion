using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Features.Kit;

namespace Companion.Feature.Crafting;

/// <summary>
/// ForgetRecipes {confirm: yes} (2026-10-15; kind Maintenance, no switch, any character permission, a known character).
/// Local only: it never runs a procedure, sends no input and touches no window. For one clean rescan after the OCR-title
/// era (recipes stored as "ams", "i pizza", "om pizza", "dtsticker"...): the character's knowledge store first writes
/// a backup of itself next to its file ("knowledge.json.forget-&lt;UTC time&gt;"; nothing is removed when that fails),
/// then forgets every recipe and the stock read from the recipe screens (coverage <see cref="ScreenRecipes.ProductCoverage"/>
/// and <see cref="ScreenRecipes.PairsCoverage"/>: the owned counts the scans observed). Keep-stock rules, other stock and
/// the learned icon items (exact, from the game's log) stay, so the rescan presses no known icon again. The required
/// "confirm=yes" stops an accidental submission. Journaled with codes and counts.
/// CLI: <c>Companion.Cli submit ForgetRecipes confirm=yes --wait</c>, then ScanFavorites / ScanRecipeList.
/// </summary>
internal sealed class ForgetRecipesJobHandler : IJobHandler
{
    internal const string JobName = "ForgetRecipes";

    /// <summary>The only accepted value of the confirm parameter.</summary>
    internal const string Confirm = "yes";

    /// <summary>Journal procedure code of the job's records (no procedure runs).</summary>
    internal const string JournalCode = "crafting.forgetRecipes";

    /// <summary>Parameter specs (validated by the job catalog before the handler runs).</summary>
    internal static readonly IReadOnlyList<JobParamSpec> Params =
    [
        new JobParamSpec("confirm", JobParamType.Enum, true, Allowed: [Confirm], MaxLength: 3),
    ];

    /// <summary>The stock coverages that are recipe-derived (written by the Make-tab scans and ScanRecipes).</summary>
    internal static readonly IReadOnlyList<string> Coverages = [ScreenRecipes.ProductCoverage, ScreenRecipes.PairsCoverage];

    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        var refusal = JobKit.RequireKnowledge(context);
        if (refusal is not null)
        {
            return refusal;
        }

        if (!parameters.Values.TryGetValue("confirm", out var c) || c is not string confirm || confirm != Confirm)
        {
            return JobKit.Refused("param.missing:confirm");
        }

        ct.ThrowIfCancellationRequested();
        ForgetRecipesReport report;
        try
        {
            report = context.Knowledge!.ForgetRecipes(Coverages);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            JobKit.Journal(context, JournalKind.Stock, JournalCode, JobKit.Data(("event", "forgetRecipes.end"), ("code", "knowledge.backupFailed")));
            return JobKit.Failed("knowledge.backupFailed");
        }

        var data = JobKit.Data(
            ("recipes", (long)report.Recipes),
            ("observations", (long)report.Observations),
            ("backup", Codes.Safe(report.BackupFile)),
            ("inputs", 0L));
        var journal = new Dictionary<string, object?>(data, StringComparer.Ordinal) { ["event"] = "forgetRecipes.end", ["code"] = "recipes.forgotten" };
        JobKit.Journal(context, JournalKind.Stock, JournalCode, journal);
        return JobKit.Result(JobState.Succeeded, "recipes.forgotten", data);
    }
}
