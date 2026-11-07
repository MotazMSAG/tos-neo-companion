using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Host;
using Companion.Contracts.Knowledge;
using Companion.Features.Kit;

namespace Companion.Feature.Inventory;

/// <summary>
/// ScanBag: needs a known character (store writes, KS-01); refuses before any input while the procedure still
/// carries a needsSurvey stub; otherwise runs the procedure and folds the Ok cell reads as partial observations.
/// </summary>
internal sealed class ScanBagHandler : IJobHandler
{
    /// <summary>Read output key of the procedure's ReadAction.</summary>
    internal const string ReadKey = "bag";

    /// <summary>Coverage code of a bag observation (visible tab and slots only).</summary>
    internal const string Coverage = "bag:visible";

    /// <summary>TOS-01: scans mark items as seen in the game.</summary>
    private static readonly Notification SeenNotice = new(
        NotificationKind.Info,
        "inventory.scanMarksSeen",
        "Scanning the bag marks new items as seen in the game.");

    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        var refusal = JobKit.RequireKnowledge(context)
            ?? JobKit.CheckProcedures(context, [InventoryFeature.ScanBagProcedure]);
        if (refusal is not null)
        {
            return refusal;
        }

        context.Notifier.Notify(SeenNotice);
        var run = context.Procedures.Run(InventoryFeature.ScanBagProcedure, context.Run, ct);
        if (run.Status != ProcedureStatus.Succeeded)
        {
            return JobKit.FromProcedure(run);
        }

        var readProblem = JobKit.CheckRead(run, ReadKey, out var read);
        if (readProblem is not null)
        {
            return readProblem;
        }

        var observations = ReadFold.Observations(read!, ObservationKind.BagPartial, Coverage);
        var data = JobKit.ProcedureData(run);
        data["observed"] = (long)observations.Count;
        data["notice"] = SeenNotice.Code;
        if (observations.Count == 0)
        {
            return JobKit.Failed("read.noValues", data);
        }

        var knowledge = context.Knowledge!;
        knowledge.ApplyObservations(observations);
        knowledge.Save();
        JobKit.Journal(context, JournalKind.Stock, InventoryFeature.ScanBagProcedure, JobKit.Data(
            ("source", "bag.grid"),
            ("coverage", Coverage),
            ("observed", (long)observations.Count)));
        return JobKit.Result(JobState.Succeeded, "scan.partial", data);
    }
}
