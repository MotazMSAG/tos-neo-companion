using Companion.App.Tray;
using Companion.Contracts.Host;

namespace Companion.App.Tests;

/// <summary>
/// The window's status sentence tells the truth about the host's state (2026-10-18, the user: "so confusing, clear stop,
/// stop there, pause there, not human"): stopped, paused, the game not running and ready are said as such, with the
/// button that helps.
/// </summary>
[TestClass]
public sealed class HealthTextTests
{
    private static HostStatus Status(HostState state, bool game = true) =>
        new(state, DateTimeOffset.UnixEpoch, null, null, game, []);

    [TestMethod]
    public void Stopped_SaysStopped_AndHowToGoOn()
    {
        var (level, text) = CraftingMessages.Health(Status(HostState.Stopped), false);
        Assert.AreEqual(HealthLevel.Red, level);
        StringAssert.StartsWith(text, "STOPPED: you pressed Stop");
        StringAssert.Contains(text, "Allow again");
    }

    [TestMethod]
    public void Paused_SaysPaused_AndHowToGoOn()
    {
        var (level, text) = CraftingMessages.Health(Status(HostState.Paused), true);
        Assert.AreEqual(HealthLevel.Amber, level);
        StringAssert.Contains(text, "Resume");
    }

    [TestMethod]
    public void TheGameNotRunning_IsSaid()
    {
        var (level, text) = CraftingMessages.Health(Status(HostState.Idle, game: false), false);
        Assert.AreEqual(HealthLevel.Amber, level);
        Assert.AreEqual("THE GAME IS NOT RUNNING. Start Tree of Savior.", text, "4.33: no crafting character to log into");
    }

    [TestMethod]
    public void Idle_IsReady()
    {
        // 4.33: no "NEEDS YOU: log into your crafting character" any more; a connected, idle host is ready
        var (level, text) = CraftingMessages.Health(Status(HostState.Idle), false);
        Assert.AreEqual(HealthLevel.Grey, level);
        StringAssert.StartsWith(text, "Ready.");
    }
}
