using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;
using Companion.GameIO.Geometry;
using Companion.GameIO.Native;
using Companion.GameIO.Tests.Fakes;

namespace Companion.GameIO.Tests;

/// <summary>
/// <see cref="GameFolderLocator"/>: the running game's folder from the same read-only search as Connect, without the
/// exe path comparison; Connect's own identity check is unchanged.
/// </summary>
[TestClass]
public sealed class GameFolderLocatorTests
{
    [TestMethod]
    public void TheRunningGame_GivesItsFolder_WhereverItIsInstalled()
    {
        using var rig = new Rig();
        rig.Win32.Processes[Rig.GamePid] = new FakeProcess("TOS", @"E:\SteamLibrary\TOS\TOS.exe", Rig.GameStart);
        Assert.AreEqual(@"E:\SteamLibrary\TOS", GameFolderLocator.FindRunningGameFolder(rig.Win32));
        Assert.IsFalse(rig.Win32.Calls.Any(), "the locator sends nothing");
    }

    [TestMethod]
    public void TheRunningGame_InTheExpectedFolder_IsFoundToo()
    {
        using var rig = new Rig();
        Assert.AreEqual(@"D:\Games\TOS", GameFolderLocator.FindRunningGameFolder(rig.Win32));
    }

    [TestMethod]
    public void NoGame_SeveralWindows_OrNoVisibleWindow_GiveNothing()
    {
        using (var rig = new Rig())
        {
            rig.Win32.Processes.Remove(Rig.GamePid);
            Assert.IsNull(GameFolderLocator.FindRunningGameFolder(rig.Win32));
        }

        using (var rig = new Rig())
        {
            rig.GameWindow.Visible = false;
            Assert.IsNull(GameFolderLocator.FindRunningGameFolder(rig.Win32));
        }

        using (var rig = new Rig())
        {
            rig.Win32.Processes[5000] = new FakeProcess("TOS", @"E:\Other\TOS.exe", Rig.GameStart);
            rig.Win32.Add(new FakeWindow { Hwnd = 0x9000, Pid = 5000, Class = HardRules.GameWindowClass, Rect = new RectI(0, 0, 800, 600) });
            rig.Win32.ZOrder.Add(0x9000);
            Assert.IsNull(GameFolderLocator.FindRunningGameFolder(rig.Win32));
        }
    }

    [TestMethod]
    public void AnExeNotNamedTos_OrAFolderWithoutGameAssembly_IsNotAGameFolder()
    {
        using (var rig = new Rig())
        {
            rig.Win32.Processes[Rig.GamePid] = new FakeProcess("TOS", @"E:\Games\TOS\Launcher.exe", Rig.GameStart);
            Assert.IsNull(GameFolderLocator.FindRunningGameFolder(rig.Win32));
        }

        using (var rig = new Rig())
        {
            rig.Win32.Processes[Rig.GamePid] = new FakeProcess("TOS", @"E:\Games\TOS\TOS.exe", Rig.GameStart);
            rig.Win32.FileMetadata = p => p.EndsWith("GameAssembly.dll", StringComparison.OrdinalIgnoreCase) ? null : (1, DateTime.UnixEpoch);
            Assert.IsNull(GameFolderLocator.FindRunningGameFolder(rig.Win32));
        }

        using (var rig = new Rig())
        {
            rig.Win32.Processes[Rig.GamePid] = new FakeProcess("TOS", null!, Rig.GameStart);
            Assert.IsNull(GameFolderLocator.FindRunningGameFolder(rig.Win32));
        }
    }

    [TestMethod]
    public void ConnectStillRefusesAGameFromAnotherFolder_TheLocatorDoesNotLoosenIt()
    {
        using var rig = new Rig();
        rig.Win32.Processes[Rig.GamePid] = new FakeProcess("TOS", @"E:\Games\TOS\TOS.exe", Rig.GameStart);
        Assert.AreEqual(@"E:\Games\TOS", GameFolderLocator.FindRunningGameFolder(rig.Win32));
        Assert.IsNull(rig.Io.LastConnectProblem);
        Assert.AreEqual(ConnectProblem.ExePathMismatch, rig.Io.Connect(CancellationToken.None).Problem);
        Assert.AreEqual(ConnectProblem.ExePathMismatch, rig.Io.LastConnectProblem);
        Assert.AreEqual(ConnectProblem.ExePathMismatch, GameWindowFinder.Find(rig.Win32, Rig.GameExe).Problem);
    }

    [TestMethod]
    public void LastConnectProblem_IsNoneAfterAGoodConnect()
    {
        using var rig = new Rig();
        Assert.AreEqual(ConnectProblem.None, rig.Io.Connect(CancellationToken.None).Problem);
        Assert.AreEqual(ConnectProblem.None, rig.Io.LastConnectProblem);
    }

    [TestMethod]
    public void FindAnyPath_MatchesFind_OnEverythingButTheExeComparison()
    {
        using var rig = new Rig();
        var any = GameWindowFinder.FindAnyPath(rig.Win32);
        var exact = GameWindowFinder.Find(rig.Win32, Rig.GameExe);
        Assert.AreEqual(exact, any);
        Assert.AreEqual(ConnectProblem.None, any.Problem);
    }
}
