using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.GameIO.Geometry;
using Companion.GameIO.Native;

namespace Companion.GameIO.Tests;

/// <summary>Cover rules on synthetic window tables (SPEC 7.5, 13).</summary>
[TestClass]
public sealed class CoverRulesTests
{
    private const long Game = 0x1000;
    private const long Chrome = 0x2000;
    private static readonly PointI Cursor = new(918, 672);

    private static WindowFacts Valid() => new(
        Chrome, "Chrome_WidgetWin_1", 777, true, false, false, 0, 255, new RectI(0, 0, 1920, 1032), Chrome,
        @"C:\Program Files\Google\Chrome\Application\chrome.exe");

    private static CoverContext Ctx() => new(
        Game, 4242, 9999, @"D:\Games\TOS", Chrome, 777, Cursor, Enumerable.Repeat(Chrome, 8).ToList(),
        TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(800), true, true);

    [TestMethod]
    public void ValidCoverPasses() => Assert.AreEqual(CoverRejectReason.None, CoverRules.Evaluate(Valid(), Ctx()));

    [TestMethod]
    public void TheGameItselfIsNoCover() =>
        Assert.AreEqual(CoverRejectReason.IsGame, CoverRules.Evaluate(Valid() with { Hwnd = Game }, Ctx()));

    [TestMethod]
    [DataRow("Shell_TrayWnd")]
    [DataRow("Shell_SecondaryTrayWnd")]
    [DataRow("NotifyIconOverflowWindow")]
    [DataRow("Windows.UI.Core.CoreWindow")]
    [DataRow("XamlExplorerHostIslandWindow")]
    [DataRow("MultitaskingViewFrame")]
    [DataRow("ForegroundStaging")]
    [DataRow("tooltips_class32")]
    [DataRow("#32768")]
    [DataRow("Ghost")]
    public void TaskbarAndTransientShellWindowsAreRejected(string cls) =>
        Assert.AreEqual(CoverRejectReason.ShellOrTransient, CoverRules.Evaluate(Valid() with { ClassName = cls }, Ctx()));

    [TestMethod]
    public void TopmostIsRejected() =>
        Assert.AreEqual(CoverRejectReason.Topmost, CoverRules.Evaluate(Valid() with { ExStyle = Win32Const.WS_EX_TOPMOST }, Ctx()));

    [TestMethod]
    public void ToolAndNoActivateWindowsAreRejected()
    {
        Assert.AreEqual(CoverRejectReason.ToolWindow, CoverRules.Evaluate(Valid() with { ExStyle = Win32Const.WS_EX_TOOLWINDOW }, Ctx()));
        Assert.AreEqual(CoverRejectReason.ToolWindow, CoverRules.Evaluate(Valid() with { ExStyle = Win32Const.WS_EX_NOACTIVATE }, Ctx()));
    }

    [TestMethod]
    public void TranslucentLayeredIsRejected()
    {
        Assert.AreEqual(CoverRejectReason.Translucent, CoverRules.Evaluate(Valid() with { ExStyle = Win32Const.WS_EX_LAYERED, LayeredAlpha = 200 }, Ctx()));
        Assert.AreEqual(CoverRejectReason.None, CoverRules.Evaluate(Valid() with { ExStyle = Win32Const.WS_EX_LAYERED, LayeredAlpha = 255 }, Ctx()));
        Assert.AreEqual(CoverRejectReason.Translucent, CoverRules.Evaluate(Valid() with { ExStyle = Win32Const.WS_EX_TRANSPARENT }, Ctx()));
    }

    [TestMethod]
    public void CloakedAndInvisibleAreRejected()
    {
        Assert.AreEqual(CoverRejectReason.Cloaked, CoverRules.Evaluate(Valid() with { Cloaked = true }, Ctx()));
        Assert.AreEqual(CoverRejectReason.NotVisible, CoverRules.Evaluate(Valid() with { Visible = false }, Ctx()));
        Assert.AreEqual(CoverRejectReason.NotVisible, CoverRules.Evaluate(Valid() with { Iconic = true }, Ctx()));
    }

    [TestMethod]
    public void GameOwnedWindowsAreRejected()
    {
        Assert.AreEqual(CoverRejectReason.GameOwned, CoverRules.Evaluate(Valid() with { Pid = 4242 }, Ctx() with { ForegroundPid = 4242 }));
        Assert.AreEqual(CoverRejectReason.GameOwned, CoverRules.Evaluate(Valid() with { RootOwner = Game }, Ctx()));
        Assert.AreEqual(CoverRejectReason.GameOwned, CoverRules.Evaluate(Valid() with { ExePath = @"D:\Games\TOS\TuanjieCrashHandler64.exe" }, Ctx()));
        Assert.AreEqual(CoverRejectReason.GameOwned, CoverRules.Evaluate(Valid() with { ExePath = @"C:\Temp\TuanjieCrashHandler64.exe" }, Ctx()));
        Assert.AreEqual(CoverRejectReason.GameOwned, CoverRules.Evaluate(Valid() with { ExePath = @"D:\Games\TOS\webview\helper.exe" }, Ctx()));
        Assert.AreEqual(CoverRejectReason.None, CoverRules.Evaluate(Valid() with { ExePath = @"D:\Games\TOS-companion\other.exe" }, Ctx()));
    }

    [TestMethod]
    public void OwnWindowIsRejected() =>
        Assert.AreEqual(CoverRejectReason.OwnWindow, CoverRules.Evaluate(Valid() with { Pid = 9999 }, Ctx() with { ForegroundPid = 9999 }));

    [TestMethod]
    public void NonForegroundCoverIsRejectedUnlessSameProcess()
    {
        Assert.AreEqual(CoverRejectReason.NotForeground, CoverRules.Evaluate(Valid(), Ctx() with { ForegroundHwnd = 0x7000, ForegroundPid = 1234 }));
        Assert.AreEqual(CoverRejectReason.None, CoverRules.Evaluate(Valid(), Ctx() with { ForegroundHwnd = 0x7000, ForegroundPid = 777 }));
    }

    [TestMethod]
    public void CursorNearTheFrameEdgeIsRejected()
    {
        Assert.AreEqual(CoverRejectReason.CursorNearEdge, CoverRules.Evaluate(Valid(), Ctx() with { Cursor = new PointI(10, 600) }));
        Assert.AreEqual(CoverRejectReason.CursorNearEdge, CoverRules.Evaluate(Valid(), Ctx() with { Cursor = new PointI(900, 1020) }));
        Assert.AreEqual(CoverRejectReason.None, CoverRules.Evaluate(Valid(), Ctx() with { Cursor = new PointI(16, 16) }));
        Assert.AreEqual(CoverRejectReason.CursorNearEdge, CoverRules.Evaluate(Valid() with { FrameBounds = null }, Ctx()));
    }

    [TestMethod]
    public void EightPointProbeMustFindTheSameRoot()
    {
        var probes = Enumerable.Repeat(Chrome, 8).ToList();
        probes[5] = 0x3000;
        Assert.AreEqual(CoverRejectReason.RootMismatch, CoverRules.Evaluate(Valid(), Ctx() with { ProbeRoots = probes }));
        Assert.AreEqual(CoverRejectReason.RootMismatch, CoverRules.Evaluate(Valid(), Ctx() with { ProbeRoots = Enumerable.Repeat(Chrome, 7).ToList() }));
    }

    [TestMethod]
    public void UnstableCoverIsRejected()
    {
        Assert.AreEqual(CoverRejectReason.Unstable, CoverRules.Evaluate(Valid(), Ctx() with { CoverStableFor = TimeSpan.FromMilliseconds(500) }));
        Assert.AreEqual(CoverRejectReason.Unstable, CoverRules.Evaluate(Valid(), Ctx() with { CoverStableFor = TimeSpan.FromSeconds(1), RequiredStillness = TimeSpan.FromMilliseconds(1500) }));
    }

    [TestMethod]
    public void CoverBelowTheGameIsRejected()
    {
        Assert.AreEqual(CoverRejectReason.NotAboveGame, CoverRules.Evaluate(Valid(), Ctx() with { AboveGameInSnapshot = false }));
        Assert.AreEqual(CoverRejectReason.NotAboveGame, CoverRules.Evaluate(Valid(), Ctx() with { AboveGameByWalk = false }));
    }

    [TestMethod]
    public void ExposureCoversMustBeOpaque()
    {
        Assert.IsTrue(CoverRules.HidesWhatIsBehind(true, false, false, 0, null));
        Assert.IsTrue(CoverRules.HidesWhatIsBehind(true, false, false, Win32Const.WS_EX_TOPMOST, null));
        Assert.IsFalse(CoverRules.HidesWhatIsBehind(false, false, false, 0, 255));
        Assert.IsFalse(CoverRules.HidesWhatIsBehind(true, true, false, 0, 255));
        Assert.IsFalse(CoverRules.HidesWhatIsBehind(true, false, true, 0, 255));
        Assert.IsFalse(CoverRules.HidesWhatIsBehind(true, false, false, Win32Const.WS_EX_TRANSPARENT, 255));
        Assert.IsFalse(CoverRules.HidesWhatIsBehind(true, false, false, Win32Const.WS_EX_LAYERED, null));
        Assert.IsFalse(CoverRules.HidesWhatIsBehind(true, false, false, Win32Const.WS_EX_LAYERED, 254));
        Assert.IsTrue(CoverRules.HidesWhatIsBehind(true, false, false, Win32Const.WS_EX_LAYERED, 255));
    }
}
