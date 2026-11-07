using Companion.CaptureProbe;
using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;
using Companion.GameIO.Diagnostics;
using Companion.GameIO.Tests.Fakes;

namespace Companion.GameIO.Tests;

/// <summary>
/// Offline tests of the operator-run capture probe (tools\Companion.CaptureProbe): argument parsing, image metrics, stop
/// rules, and GameIO's ProbeWindow against the fake Win32 table. Nothing here touches a real window.
/// </summary>
[TestClass]
public sealed class CaptureProbeTests
{
    private static readonly SizeI Client = new(1920, 1009);

    private static byte[] Solid(int w, int h, byte v)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = v;
            px[i + 1] = v;
            px[i + 2] = v;
            px[i + 3] = 255;
        }

        return px;
    }

    // ------------------------------------------------------------------ arguments

    [TestMethod]
    public void Options_ParseTheFailingCaseAndDefaults()
    {
        var o = ProbeOptions.Parse(["--dx", "-988", "--dy", "+544"]);
        Assert.AreEqual((-988, 544), (o.Dx, o.Dy));
        CollectionAssert.AreEqual(new[] { 150, 400, 800 }, o.DelaysMs.ToArray());
        Assert.IsNull(o.OutDir);

        var p = ProbeOptions.Parse(["--dx=-988", "--dy=544", "--delays", "100,250,1500", "--out", @"C:\probe out"]);
        Assert.AreEqual((-988, 544, @"C:\probe out"), (p.Dx, p.Dy, p.OutDir));
        CollectionAssert.AreEqual(new[] { 100, 250, 1500 }, p.DelaysMs.ToArray());
        Assert.AreEqual((0, 7), (ProbeOptions.Parse(["--dy", "7", "--dx", "0"]).Dx, ProbeOptions.Parse(["--dy", "7", "--dx", "0"]).Dy));
    }

    [TestMethod]
    public void Options_RejectBadInput()
    {
        string[][] bad =
        [
            ["--dx", "-988"],
            ["--dy", "544"],
            ["--dx", "0", "--dy", "0"],
            ["--dx", "2001", "--dy", "0"],
            ["--dx", "1", "--dy", "-2001"],
            ["--dx", "abc", "--dy", "1"],
            ["--dx", "1.5", "--dy", "1"],
            ["--dx", "1", "--dy", "1", "--delays", "400,150"],
            ["--dx", "1", "--dy", "1", "--delays", "150,150"],
            ["--dx", "1", "--dy", "1", "--delays", "0"],
            ["--dx", "1", "--dy", "1", "--delays", "1501"],
            ["--dx", "1", "--dy", "1", "--delays", "-5"],
            ["--dx", "1", "--dy", "1", "--delays", "1,2,3,4,5,6,7"],
            ["--dx", "1", "--dy", "1", "--delays", ""],
            ["--dx", "1", "--dy", "1", "--out", " "],
            ["--dx", "1", "--dy", "1", "--click", "yes"],
            ["--dx", "1", "--dx", "2", "--dy", "1"],
            ["--dx", "1", "--dy"],
        ];
        foreach (var args in bad)
        {
            Assert.ThrowsExactly<FormatException>(() => ProbeOptions.Parse(args), string.Join(" ", args));
        }
    }

    // ------------------------------------------------------------------ metrics

    [TestMethod]
    public void Metrics_MeanAbsDiffAndPureBlack()
    {
        var a = Solid(1920, 1009, 100);
        var b = Solid(1920, 1009, 100);
        Assert.AreEqual(0.0, ProbeMetrics.MeanAbsDiff(a, b, 1920, 1009, ProbeMetrics.ToggleArea));

        // Half of the toggle area black in b: mean |diff| = 50 per channel.
        for (var y = 290; y < 346; y++)
        {
            for (var x = 1827; x < 1855; x++)
            {
                var i = ((y * 1920) + x) * 4;
                b[i] = b[i + 1] = b[i + 2] = 0;
            }
        }

        Assert.AreEqual(50.0, ProbeMetrics.MeanAbsDiff(a, b, 1920, 1009, ProbeMetrics.ToggleArea), 1e-9);
        Assert.AreEqual(0.5, ProbeMetrics.PureBlackFraction(b, 1920, 1009, ProbeMetrics.ToggleArea), 1e-9);
        Assert.AreEqual(28.0 * 56 / (1920.0 * 1009), ProbeMetrics.PureBlackFraction(b, 1920, 1009, new RectI(0, 0, 1920, 1009)), 1e-12);
        Assert.AreEqual(0.0, ProbeMetrics.PureBlackFraction(Solid(4, 4, 1), 4, 4, new RectI(0, 0, 4, 4)), "near black is not pure black");
        Assert.IsTrue(double.IsNaN(ProbeMetrics.MeanAbsDiff(a, new byte[8], 1920, 1009, ProbeMetrics.ToggleArea)), "short buffer");
        Assert.IsTrue(double.IsNaN(ProbeMetrics.PureBlackFraction(a, 1920, 1009, new RectI(3000, 0, 3100, 10))), "region outside");
    }

    [TestMethod]
    public void Metrics_ClientPartsOfTheC13Slide()
    {
        // c13: window (-996,536)-(940,1584), client origin (-988,567); DISPLAY1 left, DISPLAY2 primary.
        var origin = new PointI(-988, 567);
        Assert.AreEqual(new RectI(988, 0, 1920, 513), ProbeMetrics.ClientPartOn(new RectI(0, 0, 1920, 1080), origin, Client));
        Assert.AreEqual(new RectI(0, 0, 988, 513), ProbeMetrics.ClientPartOn(new RectI(-1920, 0, 0, 1080), origin, Client));
        Assert.IsNull(ProbeMetrics.ClientPartOn(new RectI(1920, 0, 3840, 1080), origin, Client));
        Assert.AreEqual(new RectI(0, 0, 1920, 1009), ProbeMetrics.ClientPartOn(new RectI(0, 0, 1920, 1080), new PointI(0, 23), Client), "at home");
    }

    [TestMethod]
    public void Metrics_ClientOffsetInsideTheWgcFrame()
    {
        var window = new RectI(-8, -8, 1928, 1040);
        var frame = new RectI(0, 0, 1920, 1032);
        var origin = new PointI(0, 23);
        Assert.AreEqual((new PointI(0, 23), "frameBounds"), ProbeMetrics.ClientOffsetInCapture(new SizeI(1920, 1032), window, frame, origin));
        Assert.AreEqual((new PointI(8, 31), "windowRect"), ProbeMetrics.ClientOffsetInCapture(new SizeI(1936, 1048), window, frame, origin));
        Assert.AreEqual((new PointI(0, 23), "frameBounds(sizeMismatch)"), ProbeMetrics.ClientOffsetInCapture(new SizeI(1000, 500), window, frame, origin));
        Assert.AreEqual((new PointI(8, 31), "windowRect(sizeMismatch)"), ProbeMetrics.ClientOffsetInCapture(new SizeI(1000, 500), window, null, origin));
    }

    [TestMethod]
    public void Metrics_CropClientCopiesRowsAndForcesAlpha()
    {
        // 4x3 source with stride 20 (padding), pixel value = x + 10*y; alpha 0.
        var src = new byte[20 * 3];
        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var i = (y * 20) + (x * 4);
                src[i] = (byte)(x + (10 * y));
                src[i + 1] = 7;
                src[i + 2] = 9;
            }
        }

        var c = ProbeMetrics.CropClient(src, 4, 3, 20, new PointI(1, 1), new SizeI(2, 2))!;
        CollectionAssert.AreEqual(new byte[] { 11, 7, 9, 255, 12, 7, 9, 255, 21, 7, 9, 255, 22, 7, 9, 255 }, c);
        Assert.IsNull(ProbeMetrics.CropClient(src, 4, 3, 20, new PointI(3, 0), new SizeI(2, 2)), "does not fit");
        Assert.IsNull(ProbeMetrics.CropClient(src, 4, 3, 20, new PointI(-1, 0), new SizeI(2, 2)));
        Assert.IsNull(ProbeMetrics.CropClient(new byte[10], 4, 3, 20, new PointI(0, 0), new SizeI(2, 2)), "short source");
    }

    // ------------------------------------------------------------------ stop rules

    [TestMethod]
    public void Guards_StopReasonsWhileMoved()
    {
        var slid = new RectI(-996, 536, 940, 1584);
        var cursor = new PointI(867, 885);
        var ok = new ProbeSample(true, false, false, slid, cursor, false, false);
        Assert.IsNull(ProbeGuards.StopReason(ok, cursor, slid));
        Assert.AreEqual("game window gone", ProbeGuards.StopReason(ok with { Exists = false }, cursor, slid));
        Assert.AreEqual("game became foreground", ProbeGuards.StopReason(ok with { Foreground = true }, cursor, slid));
        Assert.AreEqual("game minimized", ProbeGuards.StopReason(ok with { Minimized = true }, cursor, slid));
        Assert.AreEqual("window rect changed (not by the probe)", ProbeGuards.StopReason(ok with { Rect = slid.Offset(0, 1) }, cursor, slid));
        Assert.AreEqual("window rect changed (not by the probe)", ProbeGuards.StopReason(ok with { Rect = null }, cursor, slid));
        Assert.AreEqual("cursor moved", ProbeGuards.StopReason(ok with { Cursor = cursor.Offset(1, 0) }, cursor, slid));
        Assert.AreEqual("cursor moved", ProbeGuards.StopReason(ok with { Cursor = null }, cursor, slid));
        Assert.AreEqual("mouse button pressed", ProbeGuards.StopReason(ok with { ButtonDown = true }, cursor, slid));
        Assert.AreEqual("game under the cursor", ProbeGuards.StopReason(ok with { GameUnderCursor = true }, cursor, slid));
    }

    [TestMethod]
    public void Guards_CoverAndMonitorRefusals()
    {
        var cursor = new PointI(867, 885);
        var frame = new RectI(0, 0, 1920, 1032);
        Assert.IsNull(ProbeGuards.CoverRefusal(cursor, 0x2000, 0x1000, true, frame));
        Assert.IsNotNull(ProbeGuards.CoverRefusal(cursor, 0, 0x1000, true, frame));
        Assert.IsNotNull(ProbeGuards.CoverRefusal(cursor, 0x1000, 0x1000, true, frame), "the game itself");
        Assert.IsNotNull(ProbeGuards.CoverRefusal(cursor, 0x2000, 0x1000, false, frame), "below the game");
        Assert.IsNotNull(ProbeGuards.CoverRefusal(new PointI(10, 885), 0x2000, 0x1000, true, frame), "too close to the edge");
        Assert.IsNotNull(ProbeGuards.CoverRefusal(cursor, 0x2000, 0x1000, true, null));

        var d1 = new MonitorInfo(@"\\.\DISPLAY1", new RectI(-1920, 0, 0, 1080), new RectI(-1920, 0, 0, 1032), false, 96);
        var d2 = new MonitorInfo(@"\\.\DISPLAY2", new RectI(0, 0, 1920, 1080), new RectI(0, 0, 1920, 1032), true, 96);
        var hiDpi = d1 with { Dpi = 144 };
        Assert.IsNull(ProbeGuards.MonitorRefusal(d2, d1, d1.DeviceName, [d1, d2]), "same-DPI cross-monitor (user decision after c12)");
        Assert.IsNull(ProbeGuards.MonitorRefusal(d2, d2, null, [d1, d2]));
        Assert.IsNotNull(ProbeGuards.MonitorRefusal(d2, hiDpi, hiDpi.DeviceName, [hiDpi, d2]), "a DPI change would resize the game");
        Assert.IsNotNull(ProbeGuards.MonitorRefusal(d2, d2, hiDpi.DeviceName, [hiDpi, d2]), "Win32 disagrees and names the other-DPI monitor");
        Assert.IsNotNull(ProbeGuards.MonitorRefusal(null, d1, null, [d1, d2]));
        Assert.IsNotNull(ProbeGuards.MonitorRefusal(d2 with { Dpi = 120 }, d2 with { Dpi = 120 }, null, [d1, d2]), "home monitor must be 96 DPI");
    }

    // ------------------------------------------------------------------ GameIO's probe window over the fake Win32 table

    [TestMethod]
    public void ProbeWindow_FindsTheGameLikeConnectMovesMoveOnlyAndSendsNoMessage()
    {
        using var rig = new Rig(gameReacts: false);
        var w = ProbeWindow.Open(rig.Win32, Rig.GameExe, out var problem);
        Assert.IsNotNull(w, problem);
        Assert.AreEqual((Rig.Game, Rig.GamePid), (w.Hwnd, w.Pid));
        Assert.IsTrue(w.Exists);
        Assert.IsFalse(w.IsForeground);
        Assert.AreEqual(Rig.HomeRect, w.Rect);
        Assert.AreEqual(Rig.Cover, w.TopWindowAt(Rig.RestingCursor));
        Assert.IsTrue(w.IsAboveGame(Rig.Cover));
        Assert.IsFalse(w.IsAboveGame(Rig.Game));

        Assert.IsTrue(w.MoveTo(-996, 536));
        Assert.AreEqual(new RectI(-996, 536, 940, 1584), rig.GameWindow.Rect);
        Assert.IsNotNull(w.CaptureClient(rig.GameWindow.Rect, new PointI(-988, 567), Client));
        Assert.IsTrue(w.MoveTo(Rig.HomeRect.Left, Rig.HomeRect.Top));
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);

        CollectionAssert.AreEqual(new[] { "swp(-996,536,0x0215)", "print", "swp(-8,-8,0x0215)" }, rig.Win32.Calls.Select(c => c.ToString()).ToArray());
        Assert.IsFalse(rig.Win32.Calls.Any(c => c.Kind is "post" or "send" or "notify"), "no window message of any kind");
        Assert.AreEqual(HardRules.SwpSlide, rig.Win32.Calls[0].Flags);
    }

    [TestMethod]
    public void ProbeWindow_RefusesWithoutElevationOrGame()
    {
        using (var rig = new Rig(gameReacts: false))
        {
            rig.Win32.Elevated = false;
            Assert.IsNull(ProbeWindow.Open(rig.Win32, Rig.GameExe, out var p));
            Assert.AreEqual("notElevated", p);
        }

        using (var rig = new Rig(gameReacts: false))
        {
            rig.Win32.Processes.Remove(Rig.GamePid);
            Assert.IsNull(ProbeWindow.Open(rig.Win32, Rig.GameExe, out var p));
            Assert.AreEqual(nameof(ConnectProblem.NotRunning), p);
        }

        using (var rig = new Rig(gameReacts: false))
        {
            Assert.IsNull(ProbeWindow.Open(rig.Win32, @"D:\Games\Other\TOS.exe", out var p));
            Assert.AreEqual(nameof(ConnectProblem.ExePathMismatch), p);
            Assert.AreEqual(0, rig.Win32.Calls.Count);
        }
    }

    [TestMethod]
    public void ProbeWindowSource_SendsNoMessages()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props")))
        {
            root = root.Parent;
        }

        Assert.IsNotNull(root);
        var text = File.ReadAllText(Path.Combine(root.FullName, "src", "Companion.GameIO", "Diagnostics", "ProbeWindow.cs"));
        foreach (var call in new[] { ".Post(", ".Send(", ".Notify(", "EnterEmergency", "SwpSlideAsync", "SwpEmergency" })
        {
            Assert.IsFalse(text.Contains(call, StringComparison.Ordinal), call);
        }
    }
}
