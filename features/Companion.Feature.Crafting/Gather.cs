using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Features.Kit;

namespace Companion.Feature.Crafting;

/// <summary>
/// Gather {item, qty} (2026-10-15): one run of the shared <see cref="Gathering"/> module for one material (the game's
/// item id); the name for reports comes from a learned picture or a recipe material with that id. Succeeded
/// "gather.done"; a material the helper does not list, a switch that is off or a status that never comes are Failed
/// with their code; a halt or stop of a procedure ends the job with it.
/// </summary>
internal sealed class GatherJobHandler : IJobHandler
{
    internal const string JobName = "Gather";

    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        if (!parameters.Values.TryGetValue("item", out var i) || i is not long id || id < 1
            || !parameters.Values.TryGetValue("qty", out var q) || q is not long qty || qty is < 1 or > 9_999)
        {
            return JobKit.Refused("param.missing:item,qty");
        }

        var refusal = JobKit.RequireKnowledge(context);
        if (refusal is not null)
        {
            return refusal;
        }

        var name = context.Knowledge!.GetIconItems().FirstOrDefault(x => x.ItemId == id)?.Name
            ?? context.Knowledge.GetRecipes().SelectMany(r => r.Materials).FirstOrDefault(m => m.ItemId == id)?.Item.Value
            ?? id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var outcome = Gathering.Run(context, [new GatherTarget(id, name, (int)qty)], ct);
        if (outcome.Stop is { } stop)
        {
            return stop;
        }

        var data = JobKit.Data(("gathered", (long)outcome.Gathered.Sum(t => t.Qty)), ("item", name));
        return JobKit.Result(outcome.Code == "gather.done" ? JobState.Succeeded : JobState.Failed, outcome.Code, data);
    }
}
