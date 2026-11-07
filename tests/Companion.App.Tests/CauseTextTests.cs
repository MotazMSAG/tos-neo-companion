using Companion.App.Jobs;
using Companion.App.Tray;
using Companion.Contracts.Features;
using Companion.Contracts.Host;

namespace Companion.App.Tests;

/// <summary>Release 3 (audit R3.2, R3.3): every pause, wait and end says its journaled cause, never a guess.</summary>
[TestClass]
public sealed class CauseTextTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 18, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow("round.deferred:commit.userActive", "you were using the game")]
    [DataRow("gather.deferred:unreachable.CoverRejected", "the mouse rested where the game cannot be reached")]
    [DataRow("gather.deferred:exhausted.PreconditionLostExhausted", "a step kept failing")]
    [DataRow("makeTab.deferred:exhausted.reset.noProgress", "a game window would not close")]
    [DataRow("loop.deferred:keyboardLayout", "the keyboard layout changed")]
    [DataRow(null, "it could not be done just now")]
    public void ACauseCode_BecomesItsOwnShortReason(string? cause, string expected) =>
        Assert.AreEqual(expected, CraftingMessages.CauseText(cause));

    [TestMethod]
    public void EngineEnds_KeepTheirReason_AndOnlyAClientCancelIsYourRequest()
    {
        static string End(string code) => CraftingMessages.ForEnd(new JobStatusInfo("j", "Craft", JobState.Cancelled, code, T0, T0, null));

        StringAssert.StartsWith(End("cancelled.byClient"), "Stopped at your request.");
        StringAssert.StartsWith(End("cancelled.ruleModeStall"), "Ended by rule mode's watchdog");
        StringAssert.StartsWith(End("round.cancelled:park.expired"), "The companion ended the job: it waited 4 hours");
        StringAssert.StartsWith(End("round.cancelled:commit.uncertain"), "The companion ended the job: it could not confirm a Make press");
        StringAssert.StartsWith(End("cancelled"), "The companion ended the job itself.");
    }

    [TestMethod]
    public void AGoal_SaysItsProgress_AndAPause_SaysItsCauseAndUntilWhen()
    {
        var board = new JobActivityBoard();
        board.Set("j", "job", "plan", new Dictionary<string, object?> { ["item"] = "sauteed clams", ["made"] = 43L, ["qty"] = 100L }, T0);
        board.Set("j", "step", "plan.pause", new Dictionary<string, object?> { ["cause"] = "round.deferred:commit.userActive", ["untilUtc"] = T0.AddMinutes(2) }, T0);

        var text = CraftingMessages.JobActivityText(board.Snapshot("j"), T0)!;

        StringAssert.Contains(text, "Goal: 43 of 100 sauteed clams");
        StringAssert.Contains(text, "paused (you were using the game), going on at " + T0.AddMinutes(2).ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
    }
}
