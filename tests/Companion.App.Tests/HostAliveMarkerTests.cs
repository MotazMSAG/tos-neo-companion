namespace Companion.App.Tests;

/// <summary>R1.12 (incident 12): a host that vanished leaves a marker; the next start records the unclean exit.</summary>
[TestClass]
public sealed class HostAliveMarkerTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Init() => _dir = Directory.CreateTempSubdirectory("marker-").FullName;

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [TestMethod]
    public void AMarkerOfAProcessThatNoLongerRuns_IsAnUncleanExit()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "run"));
        File.WriteAllText(HostAliveMarker.PathOf(_dir), "pid=999999\nstartTicks=1\nbuildHash=abcdef0123456789\nlastBeatUnix=1792700443\n");

        var prev = HostAliveMarker.TakeLeftover(_dir, HostAliveMarker.IsAlive);

        Assert.IsNotNull(prev);
        Assert.AreEqual(999999L, HostAliveMarker.Number(prev, "pid"));
        Assert.AreEqual(1792700443L, HostAliveMarker.Number(prev, "lastBeatUnix"));
        Assert.AreEqual("abcdef0123456789", prev["buildHash"]);
    }

    [TestMethod]
    public void TheRunningHostsOwnMarker_IsNotAnUncleanExit_AndACleanExitRemovesIt()
    {
        HostAliveMarker.Write(_dir, "abcdef0123456789", DateTimeOffset.UtcNow);

        Assert.IsNull(HostAliveMarker.TakeLeftover(_dir, HostAliveMarker.IsAlive), "this process is alive");

        HostAliveMarker.Delete(_dir);
        Assert.IsFalse(File.Exists(HostAliveMarker.PathOf(_dir)));
        Assert.IsNull(HostAliveMarker.TakeLeftover(_dir, (_, _) => false), "no marker after a clean exit");
    }

    [TestMethod]
    public void AReusedProcessId_IsNotTakenForTheOldHost()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();

        Assert.IsTrue(HostAliveMarker.IsAlive(Environment.ProcessId, self.StartTime.ToUniversalTime().Ticks));
        Assert.IsFalse(HostAliveMarker.IsAlive(Environment.ProcessId, 1), "same id, other start time: a different process");
    }
}
