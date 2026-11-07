using Companion.Contracts;
using Companion.GameIO.Input;

namespace Companion.GameIO.Tests;

/// <summary>
/// The clip check's journal detail (live 2026-10-14: a CursorClipChanged halt recorded only a boolean, so nobody could
/// tell whether the clip rectangle or the pointer visibility changed, or to what): numbers and flags only.
/// </summary>
[TestClass]
public sealed class ClipDetailTests
{
    [TestMethod]
    public void AClipChange_NamesThePart_AndBothRectangles()
    {
        var d = SlideWatchdog.ClipDetailOf(true, false, new RectI(-1920, 0, 1920, 1080), new RectI(0, 23, 1920, 1032), true, true);
        Assert.AreEqual("part=clip base=-1920,0,1920,1080 seen=0,23,1920,1032 showBase=1 showSeen=1", d);
    }

    [TestMethod]
    public void AVisibilityChange_AndUnknownValues_AreReportedAsSuch()
    {
        Assert.AreEqual("part=showing base=- seen=- showBase=1 showSeen=0", SlideWatchdog.ClipDetailOf(false, true, null, null, true, false));
        Assert.AreEqual("part=both base=0,0,1,1 seen=0,0,2,2 showBase=- showSeen=-", SlideWatchdog.ClipDetailOf(true, true, new RectI(0, 0, 1, 1), new RectI(0, 0, 2, 2), null, null));
    }
}
