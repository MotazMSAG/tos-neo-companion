using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Perception;
using Companion.Features.Kit;

namespace Companion.Features.Tests;

/// <summary>
/// The fallback ladder's last rung at the job level (FALLBACKS.md, 2026-10-16): a procedure that ends Deferred is not a
/// stop; the job sees a failure with a "deferred" code (its reason kept) and goes on with other work.
/// </summary>
[TestClass]
public sealed class FallbackTests
{
    private static ProcedureResult Result(ProcedureStatus status, string? detail) =>
        new("crafting.loop", status, StepOutcome.Deferred, null, 0, 0, 0, [], new Dictionary<string, ScreenReadResult>(), [], [], detail);

    [TestMethod]
    public void ADeferredRun_IsAFailureWithItsReason_NotAStop()
    {
        var r = JobKit.FromProcedure(Result(ProcedureStatus.Deferred, "unreachable.CrossMonitor"), "round.");
        Assert.AreEqual(JobState.Failed, r.State);
        Assert.AreEqual("round.deferred:unreachable.CrossMonitor", r.Code);
    }

    [TestMethod]
    public void ADeferredRunWithoutDetail_IsPlainDeferred() =>
        Assert.AreEqual("deferred", JobKit.FromProcedure(Result(ProcedureStatus.Deferred, null)).Code);
}
