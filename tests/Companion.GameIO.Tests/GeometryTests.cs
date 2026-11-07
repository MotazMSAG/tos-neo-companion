using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;
using Companion.GameIO.Geometry;

namespace Companion.GameIO.Tests;

[TestClass]
public sealed class GeometryTests
{
    // The user's layout: DISPLAY1 (-1920,0), DISPLAY2 (0,0) primary, 1920x1080 each, taskbar 48 px.
    private static readonly MonitorInfo D1 = new(@"\\.\DISPLAY1", new RectI(-1920, 0, 0, 1080), new RectI(-1920, 0, 0, 1032), false, 96);
    private static readonly MonitorInfo D2 = new(@"\\.\DISPLAY2", new RectI(0, 0, 1920, 1080), new RectI(0, 0, 1920, 1032), true, 96);
    private static readonly IReadOnlyList<MonitorInfo> Layout = [D1, D2];
    private static readonly RectI Home = new(-8, -8, 1928, 1040);
    private static readonly PointI Origin = new(0, 23);

    private static readonly IReadOnlyList<RectI> FullCover =
    [
        new RectI(0, 0, 1920, 1032), new RectI(0, 1032, 1920, 1080), new RectI(-1920, 0, 0, 1032), new RectI(-1920, 1032, 0, 1080),
    ];

    [TestMethod]
    public void SlidPositionMatchesC3Run3()
    {
        // C=(918,672), T=(1748,314), client offset (8,31) => (-838,327).
        var p = SlideGeometry.SlidPosition(Home, Origin, new PointI(918, 672), new PointI(1748, 314));
        Assert.AreEqual(new PointI(-838, 327), p);
        Assert.AreEqual(new RectI(-838, 327, 1098, 1375), Home.Offset(p.X - Home.Left, p.Y - Home.Top));
    }

    [TestMethod]
    public void PlanForC3Run3PassesOnTheUsersLayout()
    {
        var plan = SlideGeometry.BuildPlan(Home, Origin, new PointI(918, 672), new PointI(1748, 314), Layout, D2, FullCover);
        Assert.AreEqual(-830, plan.Dx);
        Assert.AreEqual(335, plan.Dy);
        Assert.AreEqual(new RectI(-838, 327, 1098, 1375), plan.SlidRect);
        Assert.AreEqual(D2.DeviceName, plan.SlidMonitor);
        Assert.IsTrue(plan.CenterInsideHomeMonitor);
        Assert.AreEqual(0L, plan.ExposedAreaPx);
        Assert.AreEqual(NotReadyReason.None, SlideGeometry.CheckPlan(plan, D2, D2, D2.DeviceName, Layout));
    }

    [TestMethod]
    public void UncoveredPartOnTheOtherMonitorIsExposed()
    {
        // Nothing above the game on DISPLAY1 except its taskbar: the slid part there (x -838..0, y 327..1032) shows.
        IReadOnlyList<RectI> covers = [new RectI(0, 0, 1920, 1032), new RectI(0, 1032, 1920, 1080), new RectI(-1920, 1032, 0, 1080)];
        var plan = SlideGeometry.BuildPlan(Home, Origin, new PointI(918, 672), new PointI(1748, 314), Layout, D2, covers);
        Assert.AreEqual(838L * 705, plan.ExposedAreaPx);
        // Allowed: this is the c3 PASS geometry; only the area under/around the cursor must stay covered.
        Assert.AreEqual(NotReadyReason.None, SlideGeometry.CheckPlan(plan, D2, D2, D2.DeviceName, Layout));
    }

    [TestMethod]
    public void ExposedAreaCountsOnlyVisibleMonitorArea()
    {
        var slid = new RectI(0, 1000, 100, 1200); // 80 px below the bottom of DISPLAY2 is off-screen
        Assert.AreEqual(100L * 80, SlideGeometry.ExposedArea(slid, [D2.Bounds], []));
        Assert.AreEqual(100L * 30, SlideGeometry.ExposedArea(slid, [D2.Bounds], [new RectI(0, 0, 1920, 1050)]));
        Assert.AreEqual(0L, SlideGeometry.ExposedArea(slid, [D2.Bounds], [new RectI(0, 0, 50, 1080), new RectI(40, 900, 200, 1100)]));
        Assert.AreEqual(0L, SlideGeometry.ExposedArea(new RectI(5000, 5000, 5100, 5100), [D2.Bounds], []));
    }

    [TestMethod]
    public void SlideTooFarIsRefused()
    {
        var plan = SlideGeometry.BuildPlan(Home, Origin, new PointI(-1900, 500), new PointI(1900, 10), Layout, D2, FullCover);
        Assert.IsTrue(Math.Abs(plan.Dx) > HardRules.MaxSlideDistancePx);
        Assert.AreEqual(NotReadyReason.SlideTooFar, SlideGeometry.CheckPlan(plan, D2, D1, null, Layout));
    }

    [TestMethod]
    public void SlidWindowMostlyOnTheOtherMonitorIsAllowedWhenItHasTheExpectedDpi()
    {
        // c12 + user decision 2026-10-14: the game keeps its size and maximized state on a same-DPI monitor; only its
        // frame rate follows that monitor's refresh rate for a moment.
        var plan = SlideGeometry.BuildPlan(Home, Origin, new PointI(-100, 600), new PointI(1000, 314), Layout, D2, FullCover);
        Assert.AreEqual(D1.DeviceName, plan.SlidMonitor);
        Assert.AreEqual(NotReadyReason.None, SlideGeometry.CheckPlan(plan, D2, D1, D1.DeviceName, Layout));
        Assert.AreEqual(NotReadyReason.None, SlideGeometry.CheckPlan(plan, D2, D1, null, Layout));
    }

    [TestMethod]
    public void SlidWindowMostlyOnAnOtherMonitorWithADifferentDpiBreaksTheMonitorRule()
    {
        // A DPI change would make Windows resize the game: refused before the cursor-monitor DPI check.
        var d1Scaled = D1 with { Dpi = 120 };
        IReadOnlyList<MonitorInfo> layout = [d1Scaled, D2];
        var plan = SlideGeometry.BuildPlan(Home, Origin, new PointI(-100, 600), new PointI(1000, 314), layout, D2, FullCover);
        Assert.AreEqual(D1.DeviceName, plan.SlidMonitor);
        Assert.AreEqual(NotReadyReason.MonitorRule, SlideGeometry.CheckPlan(plan, D2, d1Scaled, D1.DeviceName, layout));
        Assert.AreEqual(NotReadyReason.MonitorRule, SlideGeometry.CheckPlan(plan, D2, d1Scaled, null, layout));
    }

    [TestMethod]
    public void SlidFarAboveTheTopEdgeStillOnTheHomeMonitorIsAllowed()
    {
        // Cursor resting near the top of DISPLAY2, target in the lower half (the Make button, live run 2026-10-14): the
        // window slides ~840 px up, its centre leaves the screen, but no monitor exists above, so the monitor is unchanged.
        var plan = SlideGeometry.BuildPlan(Home, Origin, new PointI(900, 60), new PointI(1472, 870), Layout, D2, FullCover);
        Assert.AreEqual(new RectI(-580, -841, 1356, 207), plan.SlidRect);
        Assert.IsFalse(plan.CenterInsideHomeMonitor, "centre is off screen above the top edge");
        Assert.AreEqual(D2.DeviceName, plan.SlidMonitor, "largest overlap is still DISPLAY2");
        Assert.AreEqual(NotReadyReason.None, SlideGeometry.CheckPlan(plan, D2, D2, D2.DeviceName, Layout));
        Assert.AreEqual(NotReadyReason.None, SlideGeometry.CheckPlan(plan, D2, D2, null, Layout));
    }

    [TestMethod]
    public void MajorityOnATallerOtherMonitorFollowsThatMonitorsDpiEvenWithTheCentreAtHome()
    {
        // A taller monitor on the left (top at -360): the slid centre (60,193) stays inside DISPLAY2, but most of the window
        // lies on DISPLAY3, which Windows would treat as the window's monitor: its DPI decides.
        var d3 = new MonitorInfo(@"\\.\DISPLAY3", new RectI(-2560, -360, 0, 1080), new RectI(-2560, -360, 0, 1032), false, 96);
        IReadOnlyList<MonitorInfo> layout = [d3, D2];
        var plan = SlideGeometry.BuildPlan(Home, Origin, new PointI(100, 200), new PointI(1000, 500), layout, D2, FullCover);
        Assert.AreEqual(new RectI(-908, -331, 1028, 717), plan.SlidRect);
        Assert.IsTrue(plan.CenterInsideHomeMonitor);
        Assert.AreEqual(d3.DeviceName, plan.SlidMonitor);
        Assert.AreEqual(NotReadyReason.None, SlideGeometry.CheckPlan(plan, D2, D2, null, layout));
        Assert.AreEqual(NotReadyReason.None, SlideGeometry.CheckPlan(plan, D2, D2, d3.DeviceName, layout));
        IReadOnlyList<MonitorInfo> scaled = [d3 with { Dpi = 144 }, D2];
        Assert.AreEqual(NotReadyReason.MonitorRule, SlideGeometry.CheckPlan(plan, D2, D2, null, scaled));
        Assert.AreEqual(NotReadyReason.MonitorRule, SlideGeometry.CheckPlan(plan, D2, D2, d3.DeviceName, scaled));
    }

    [TestMethod]
    public void Win32SlidMonitorIsHeldToTheSameRule()
    {
        // The pure computation says DISPLAY2 (home); the Win32 answer must also be home or a known monitor at 96 DPI.
        var plan = SlideGeometry.BuildPlan(Home, Origin, new PointI(918, 672), new PointI(1748, 314), Layout, D2, FullCover);
        Assert.AreEqual(NotReadyReason.None, SlideGeometry.CheckPlan(plan, D2, D2, D1.DeviceName, Layout));
        Assert.AreEqual(NotReadyReason.MonitorRule, SlideGeometry.CheckPlan(plan, D2, D2, D1.DeviceName, [D1 with { Dpi = 120 }, D2]));
        Assert.AreEqual(NotReadyReason.MonitorRule, SlideGeometry.CheckPlan(plan, D2, D2, @"\\.\DISPLAY9", Layout));
    }

    [TestMethod]
    public void DpiOtherThan96IsUnsupported()
    {
        var plan = SlideGeometry.BuildPlan(Home, Origin, new PointI(918, 672), new PointI(1748, 314), Layout, D2, FullCover);
        Assert.AreEqual(NotReadyReason.DpiUnsupported, SlideGeometry.CheckPlan(plan, D2 with { Dpi = 120 }, D2, null, Layout));
        Assert.AreEqual(NotReadyReason.DpiUnsupported, SlideGeometry.CheckPlan(plan, D2, D1 with { Dpi = 144 }, null, Layout));
        Assert.AreEqual(NotReadyReason.DpiUnsupported, SlideGeometry.CheckPlan(plan, D2, null, null, Layout));
    }

    [TestMethod]
    public void NearestMonitorUsesLargestOverlapThenDistance()
    {
        Assert.AreEqual(D2, SlideGeometry.NearestMonitor(new RectI(-838, 327, 1098, 1375), Layout));
        Assert.AreEqual(D1, SlideGeometry.NearestMonitor(new RectI(-1108, 327, 828, 1375), Layout));
        Assert.AreEqual(D2, SlideGeometry.NearestMonitor(new RectI(2000, 10, 2100, 20), Layout));
    }

    [TestMethod]
    public void ExpectedMaximizedRectIsWorkAreaPlusFrame()
    {
        Assert.AreEqual(Home, SlideGeometry.ExpectedMaximizedRect(D2.WorkArea, 8));
        Assert.AreEqual(new RectI(-1928, -8, 8, 1040), SlideGeometry.ExpectedMaximizedRect(D1.WorkArea, 8));
    }

    [TestMethod]
    public void RectComparisons()
    {
        var size = new SizeI(1936, 1048);
        Assert.IsTrue(SlideGeometry.IsAt(new RectI(-838, 327, 1098, 1375), -838, 327, size));
        Assert.IsFalse(SlideGeometry.IsAt(new RectI(-838, 327, 1100, 1375), -838, 327, size));
        Assert.IsFalse(SlideGeometry.IsAt(new RectI(-837, 327, 1099, 1375), -838, 327, size));
        Assert.IsTrue(SlideGeometry.SameRect(Home, new RectI(-8, -8, 1928, 1040)));
        Assert.IsFalse(SlideGeometry.SizeChanged(Home, Home.Offset(-830, 335)));
        Assert.IsTrue(SlideGeometry.SizeChanged(Home, new RectI(-1928, -8, 1396, 298)));
    }

    [TestMethod]
    public void ProbePointsAre8AroundTheCursor()
    {
        var pts = SlideGeometry.ProbePoints(new PointI(100, 100));
        Assert.HasCount(8, pts);
        Assert.IsTrue(pts.All(p => p.ChebyshevDistanceTo(new PointI(100, 100)) == HardRules.CoverProbeOffsetPx));
        Assert.HasCount(8, pts.Distinct().ToList());
    }

    [TestMethod]
    public void MouseLParamPacking()
    {
        Assert.AreEqual((314L << 16) | 1748L, SlideGeometry.MouseLParamChecked(new PointI(1748, 314), new SizeI(1920, 1009)));
        Assert.AreEqual(0L, SlideGeometry.MouseLParamChecked(new PointI(0, 0), new SizeI(1920, 1009)));
        Assert.AreEqual((1008L << 16) | 1919L, SlideGeometry.MouseLParamChecked(new PointI(1919, 1008), new SizeI(1920, 1009)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SlideGeometry.MouseLParamChecked(new PointI(1920, 10), new SizeI(1920, 1009)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SlideGeometry.MouseLParamChecked(new PointI(-1, 10), new SizeI(1920, 1009)));
    }

    // ------------------------------------------------------------------ home

    private static HomeFacts ValidHome() => new(Home, true, new SizeI(1920, 1009), true, new SizeI(1920, 1009), Home, TimeSpan.FromSeconds(5));

    [TestMethod]
    public void HomeValidation()
    {
        Assert.IsNull(HomeRules.Validate(ValidHome()));
        Assert.AreEqual("home.notZoomed", HomeRules.Validate(ValidHome() with { Zoomed = false }));
        Assert.AreEqual("home.clientSize", HomeRules.Validate(ValidHome() with { Client = new SizeI(1904, 1001) }));
        Assert.AreEqual("home.noCaption", HomeRules.Validate(ValidHome() with { HasCaption = false }));
        Assert.AreEqual("home.resolutionUnknown", HomeRules.Validate(ValidHome() with { LoggedResolution = null }));
        Assert.AreEqual("home.resolutionLog", HomeRules.Validate(ValidHome() with { LoggedResolution = new SizeI(2400, 1260) }));
        Assert.AreEqual("home.notMaximizedRect", HomeRules.Validate(ValidHome() with { Rect = Home.Offset(-830, 335) }));
        Assert.AreEqual("home.notStable", HomeRules.Validate(ValidHome() with { StableFor = TimeSpan.FromMilliseconds(400) }));
    }

    // ------------------------------------------------------------------ restore ladder

    [TestMethod]
    public void RestoreLadderDecisions()
    {
        CollectionAssert.AreEqual(Array.Empty<RestoreRung>(), HomeRules.PlanLadder(iconic: true, true, true, false).ToArray());
        CollectionAssert.AreEqual(Array.Empty<RestoreRung>(), HomeRules.PlanLadder(false, homeMonitorPresent: false, true, false).ToArray());
        CollectionAssert.AreEqual(new[] { RestoreRung.MoveOnly, RestoreRung.PostedWmSize }, HomeRules.PlanLadder(false, true, false, false).ToArray());
        CollectionAssert.AreEqual(new[] { RestoreRung.MoveOnly, RestoreRung.MoveSize, RestoreRung.PostedWmSize }, HomeRules.PlanLadder(false, true, sizeChangedDuringOurs: true, false).ToArray());
        CollectionAssert.AreEqual(new[] { RestoreRung.MoveOnly, RestoreRung.PostedWmSize }, HomeRules.PlanLadder(false, true, true, moveSizeAlreadyUsed: true).ToArray());
    }

    [TestMethod]
    public void DeviationKinds()
    {
        Assert.AreEqual(IncidentKind.SelfResize, HomeRules.DeviationKind(Home, new RectI(-1928, -8, 1396, 298), true, true));
        Assert.AreEqual(IncidentKind.MaximizedLost, HomeRules.DeviationKind(Home, Home, true, false));
        Assert.AreEqual(IncidentKind.NotHomeAfterInput, HomeRules.DeviationKind(Home, Home.Offset(-830, 335), true, true));
    }

    // ------------------------------------------------------------------ marker crash recovery

    private static readonly DateTimeOffset Start = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly SlideMarkerData Marker = new(4242, 0x1000, Start, Home, Home.Offset(-830, 335), Start.AddHours(1));

    [TestMethod]
    public void MarkerRecoveryDecisions()
    {
        var slid = Home.Offset(-830, 335);
        Assert.AreEqual(MarkerDecision.NoMarker, MarkerRecovery.Decide(null, false, true, 4242, Start, true, Home, false));
        Assert.AreEqual(MarkerDecision.MarkerCorrupt, MarkerRecovery.Decide(null, true, true, 4242, Start, true, Home, false));
        Assert.AreEqual(MarkerDecision.RestoreMoveOnly, MarkerRecovery.Decide(Marker, false, true, 4242, Start, true, slid, false));
        Assert.AreEqual(MarkerDecision.DeleteAtHome, MarkerRecovery.Decide(Marker, false, true, 4242, Start, true, Home, false));
        Assert.AreEqual(MarkerDecision.LeaveAloneNeither, MarkerRecovery.Decide(Marker, false, true, 4242, Start, true, new RectI(0, 0, 800, 600), false));
        Assert.AreEqual(MarkerDecision.WaitIconic, MarkerRecovery.Decide(Marker, false, true, 4242, Start, true, new RectI(-32000, -32000, -31840, -31972), true));
        // identity differs: window gone, other PID, other start time (game restarted, HWND reused), other class
        Assert.AreEqual(MarkerDecision.DeleteIdentityDiffers, MarkerRecovery.Decide(Marker, false, false, 0, null, false, null, false));
        Assert.AreEqual(MarkerDecision.DeleteIdentityDiffers, MarkerRecovery.Decide(Marker, false, true, 5000, Start, true, slid, false));
        Assert.AreEqual(MarkerDecision.DeleteIdentityDiffers, MarkerRecovery.Decide(Marker, false, true, 4242, Start.AddMinutes(5), true, slid, false));
        Assert.AreEqual(MarkerDecision.DeleteIdentityDiffers, MarkerRecovery.Decide(Marker, false, true, 4242, Start, false, slid, false));
    }

    [TestMethod]
    public void MarkerStoreRoundTripAndCorruption()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "rt-marker-" + Guid.NewGuid().ToString("N"));
        try
        {
            var run = Path.Combine(root, "run");
            var store = new SlideMarkerStore(root, run, Path.Combine(run, "slide.json"));
            var (none, noneCorrupt) = store.TryRead();
            Assert.IsNull(none);
            Assert.IsFalse(noneCorrupt);
            Assert.IsTrue(store.TryWrite(Marker));
            var (back, corrupt) = store.TryRead();
            Assert.IsFalse(corrupt);
            Assert.AreEqual(Marker, back);
            Assert.AreEqual(0, Directory.GetFiles(run, "*.tmp").Length, "temp file renamed away");
            File.WriteAllText(store.Path, "{ not json");
            Assert.IsTrue(store.TryRead().Corrupt);
            Assert.IsTrue(store.TryDelete());
            Assert.IsFalse(store.Exists());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
