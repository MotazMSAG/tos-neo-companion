using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Perception;
using Companion.Features.Kit;

namespace Companion.Feature.Homestead;

/// <summary>Shared status read: runs homestead.status and returns the products pair (stored, capacity).</summary>
internal static class HomesteadStatusRead
{
    /// <summary>Read output key of the status procedure's ReadAction (lumi.products).</summary>
    internal const string ReadKey = "products";

    /// <summary>Runs the status procedure; on success returns (stored, capacity) or a result to end the job with.</summary>
    public static (long Stored, long Capacity, ProcedureResult Run)? Read(IJobContext context, CancellationToken ct, out JobResult? end)
    {
        end = null;
        var run = context.Procedures.Run(HomesteadFeature.StatusProcedure, context.Run, ct);
        if (run.Status != ProcedureStatus.Succeeded)
        {
            end = JobKit.FromProcedure(run, "status.");
            return null;
        }

        end = JobKit.CheckRead(run, ReadKey, out var read);
        if (end is not null)
        {
            return null;
        }

        var pair = ReadFold.OkValue(read!, ReadFold.ProductsRegion);
        if (pair is null || pair.Value is not { } stored || pair.Second is not { } capacity || stored < 0 || capacity < stored)
        {
            end = JobKit.Failed("read.unknown:" + ReadFold.ProductsRegion, JobKit.ProcedureData(run));
            return null;
        }

        return (stored, capacity, run);
    }
}

/// <summary>HomesteadStatus: read-only; refuses before any input while a stub is reachable.</summary>
internal sealed class HomesteadStatusHandler : IJobHandler
{
    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        var refusal = JobKit.CheckProcedures(context, [HomesteadFeature.StatusProcedure]);
        if (refusal is not null)
        {
            return refusal;
        }

        var status = HomesteadStatusRead.Read(context, ct, out var end);
        if (status is not { } s)
        {
            return end!;
        }

        var data = JobKit.ProcedureData(s.Run);
        data["stored"] = s.Stored;
        data["capacity"] = s.Capacity;
        return JobKit.Result(JobState.Succeeded, s.Stored >= s.Capacity ? "homestead.full" : "homestead.ok", data);
    }
}

/// <summary>
/// HomesteadCollect: switch and permission first (R10, KS-02), then the stub check (zero inputs), then status,
/// collect (Commit procedure, gate homestead) and a verifying status read. Collected products land in the bag, so
/// the character's stock is marked stale.
/// </summary>
internal sealed class HomesteadCollectHandler : IJobHandler
{
    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        var refusal = JobKit.CheckCommitGates(context, AutomationSwitch.Homestead)
            ?? JobKit.CheckProcedures(context, [HomesteadFeature.StatusProcedure, HomesteadFeature.CollectProcedure]);
        if (refusal is not null)
        {
            return refusal;
        }

        var before = HomesteadStatusRead.Read(context, ct, out var end);
        if (before is not { } b)
        {
            return end!;
        }

        if (b.Stored == 0)
        {
            var nothing = JobKit.ProcedureData(b.Run);
            nothing["stored"] = 0L;
            nothing["capacity"] = b.Capacity;
            return JobKit.Result(JobState.Succeeded, "collect.nothing", nothing);
        }

        var collect = context.Procedures.Run(HomesteadFeature.CollectProcedure, context.Run, ct);
        if (collect.Status != ProcedureStatus.Succeeded)
        {
            return JobKit.FromProcedure(collect, "collect.");
        }

        context.Knowledge?.MarkStale(StaleScope.AllStock, null, "homestead.collect", context.Clock.UtcNow);
        context.Knowledge?.Save();

        var after = HomesteadStatusRead.Read(context, ct, out end);
        if (after is not { } a)
        {
            return end!;
        }

        var data = JobKit.ProcedureData(collect);
        data["storedBefore"] = b.Stored;
        data["storedAfter"] = a.Stored;
        data["capacity"] = a.Capacity;
        return a.Stored < b.Stored
            ? JobKit.Result(JobState.Succeeded, "collect.ok", data)
            : JobKit.Failed("collect.unverified", data);
    }
}
