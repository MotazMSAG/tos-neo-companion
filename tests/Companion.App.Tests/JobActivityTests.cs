using Companion.App.Jobs;
using Companion.App.Tray;
using Companion.Contracts.Engine;
using Companion.Contracts.Host;

namespace Companion.App.Tests;

/// <summary>2026-10-23, the user: "gather everything doesn't do anything clear": the job's own words on the status card.</summary>
[TestClass]
public sealed class JobActivityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 16, 50, 0, TimeSpan.Zero);

    [TestMethod]
    public void TheBoard_KeepsCodesAndValuesPerJob_AndForgetsAnEndedJob()
    {
        var board = new JobActivityBoard();
        board.Set("job-1", "job", "gatherAll", new Dictionary<string, object?> { ["trip"] = 2L, ["phase"] = "refill", ["note"] = "free text, with a quote's" }, T0);
        board.Set("job-1", "other", "ignored", null, T0);

        var snap = board.Snapshot("job-1")!;
        Assert.AreEqual("gatherAll", snap["activity.job"]);
        Assert.AreEqual(2L, snap["activity.job.trip"]);
        Assert.IsNull(snap["activity.job.note"], "never free text");
        Assert.IsFalse(snap.ContainsKey("activity.other"), "unknown slots are ignored");

        board.Set("job-1", "job", null, null, T0);
        Assert.IsNull(board.Snapshot("job-1"), "a null code clears the slot");
        board.Set("job-1", "step", "gather.trip", null, T0);
        board.Clear("job-1");
        Assert.IsNull(board.Snapshot("job-1"));
    }

    [TestMethod]
    public void GatherEverything_SaysItsTripWhatIsLeft_AndWhatTheCharacterIsGathering()
    {
        var board = new JobActivityBoard();
        board.Set("j", "job", "gatherAll", new Dictionary<string, object?> { ["trip"] = 5L, ["phase"] = "refill", ["left"] = 3L }, T0);
        board.Set("j", "step", "gather.trip", new Dictionary<string, object?> { ["items"] = "thyme x200, toothclam x121", ["sinceUtc"] = T0 }, T0);

        var text = CraftingMessages.JobActivityText(board.Snapshot("j"), T0.AddMinutes(7))!;

        StringAssert.Contains(text, "Gather everything, trip 5 (topping up what is not full yet), 3 materials still to fill");
        StringAssert.Contains(text, "the character is out gathering thyme x200, toothclam x121 for 7 min");
    }

    [TestMethod]
    public void GatherEverything_SaysWhenItReadsTheStock_AndWhichEmptiestItTakes()
    {
        var board = new JobActivityBoard();
        board.Set("j", "job", "gatherAll", new Dictionary<string, object?> { ["trip"] = 0L, ["phase"] = "survey", ["left"] = 40L }, T0);
        StringAssert.Contains(CraftingMessages.JobActivityText(board.Snapshot("j"), T0)!, "reading how full each of 40 materials is");

        board.Set("j", "job", "gatherAll", new Dictionary<string, object?> { ["trip"] = 2L, ["phase"] = "driest", ["left"] = 12L, ["next"] = "thyme 2/500, water 160/999" }, T0);
        StringAssert.Contains(CraftingMessages.JobActivityText(board.Snapshot("j"), T0)!, "trip 2 (emptiest first: thyme 2/500, water 160/999), 12 materials still to fill");
    }

    // incident 18 (2026-10-23): a trip from before was still out and the card said "NEEDS YOU ... press Finish or Stop"
    [TestMethod]
    public void AGatheringTripThatIsOut_IsNotNeedsYou_ButAnUnknownStatusStillIs()
    {
        var status = new HostStatus(HostState.Running, T0, new EngineStatus(EngineState.Waiting, "j", "OpenAbility", "OpenAbility/reset", nameof(StepOutcome.AutoCollectNotIdle), T0), null, true, []);

        var (level, text) = CraftingMessages.Health(status, true, "Gather everything, trip 1, 40 materials still to fill", autoCollect: 1);
        Assert.AreEqual(HealthLevel.Green, level);
        StringAssert.Contains(text, "the gathering trip that is out comes back first; nothing to do");
        Assert.IsFalse(text.Contains("NEEDS YOU", StringComparison.Ordinal));

        var (unknownLevel, unknownText) = CraftingMessages.Health(status, true, null, autoCollect: null);
        Assert.AreEqual(HealthLevel.Amber, unknownLevel);
        StringAssert.StartsWith(unknownText, "NEEDS YOU");
    }

    [TestMethod]
    public void TheStatusCard_LeadsWithTheJobsOwnWords_BetweenEngineSteps()
    {
        var status = new HostStatus(HostState.Running, T0, new EngineStatus(EngineState.Idle, "j", null, null, null, T0), null, true, []);

        var (level, text) = CraftingMessages.Health(status, true, "Gather everything, trip 1, 9 materials still to fill");

        Assert.AreEqual(HealthLevel.Green, level);
        Assert.AreEqual("OK, Gather everything, trip 1, 9 materials still to fill.", text);
        Assert.IsFalse(text.Contains("between steps", StringComparison.Ordinal));
    }
}
