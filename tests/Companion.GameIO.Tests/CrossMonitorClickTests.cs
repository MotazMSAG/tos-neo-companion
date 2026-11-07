using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.GameIO.Geometry;
using Companion.GameIO.Tests.Fakes;

namespace Companion.GameIO.Tests;

/// <summary>
/// The cross-monitor rule of the slid verification (SPEC 7.3, EVIDENCE c13) against the fake Win32 sink. The resting
/// cursor (867,885) puts the far-right toggle (1855,318) under it with the c13 slide (dx -988, dy +544): the slid window's
/// largest overlap is DISPLAY1, and the fake capture renders only the part of the client on that monitor (the rest is
/// black), exactly what PrintWindow returned live.
/// </summary>
[TestClass]
public sealed class CrossMonitorClickTests
{
    private static readonly PointI CrossCursor = new(867, 885);
    private static readonly PointI Toggle = new(1855, 318);

    private static bool IsDown(Call c) => c.Kind == "post" && c.Msg == HardRules.WM_LBUTTONDOWN;

    /// <summary>A rig whose cursor rests where the toggle needs the c13 cross-monitor slide; captures render like c13.</summary>
    private static Rig CrossRig(Func<RectI, PointI, SizeI, byte[]?>? pixels = null)
    {
        var rig = new Rig();
        rig.Win32.Cursor = CrossCursor;
        rig.Win32.PrintPixels = pixels ?? ((rect, origin, size) => MajorityMonitorOnly(rig, rect, origin, size));
        rig.Vision.VerifyResult = new MatchResult("x", false, Toggle, 0.12, 0.10, 0, MatchFailure.BelowThreshold);
        rig.ConnectAndSettle();
        rig.Win32.ClearCalls();
        return rig;
    }

    /// <summary>c13: only the client pixels on the monitor with the window's largest overlap are drawn (gray), the rest is black.</summary>
    private static byte[] MajorityMonitorOnly(Rig rig, RectI windowRect, PointI origin, SizeI size)
    {
        var drawn = SlideGeometry.NearestMonitor(windowRect, rig.Win32.Monitors)!.Bounds;
        return Paint(size, (x, y) => drawn.Contains(new PointI(origin.X + x, origin.Y + y)));
    }

    private static byte[] Paint(SizeI size, Func<int, int, bool> drawn)
    {
        var px = new byte[size.Width * size.Height * 4];
        for (var y = 0; y < size.Height; y++)
        {
            for (var x = 0; x < size.Width; x++)
            {
                var i = ((y * size.Width) + x) * 4;
                var v = drawn(x, y) ? (byte)100 : (byte)0;
                px[i] = v;
                px[i + 1] = v;
                px[i + 2] = v;
                px[i + 3] = 255;
            }
        }

        return px;
    }

    private static ClickRequest ToggleClick(Rig rig, string templateId = "hud.toggle.collapsed", bool navFixed = true, PointI? templateCenter = null) =>
        new("toggle", templateId, Toggle, FireOn.Down, rig.Log.Position, rig.Clock.Monotonic, [], false, templateCenter, navFixed);

    private static JournalRecord InputRecord(Rig rig) => rig.Journal.Records.Last(j => j.Kind == JournalKind.Input);

    [TestMethod]
    public void ACrossMonitorSlide_IsNotReady_UnlessTheCallerAllowsIt()
    {
        // 2026-10-16: with the cursor resting on another monitor, a press that is not a fixed HUD button failed its slid
        // check 57 times and ended in a halt; readiness now says CrossMonitor so the engine waits for the cursor instead
        using var rig = CrossRig();

        Assert.IsTrue(rig.Io.CheckReadiness(new ReadinessQuery(InputKind.Click, Toggle, false)).Ready, "allowed (the NavFixed rule)");
        var refused = rig.Io.CheckReadiness(new ReadinessQuery(InputKind.Click, Toggle, false, CrossMonitorAllowed: false));
        Assert.AreEqual((false, NotReadyReason.CrossMonitor), (refused.Ready, refused.Reason));

        rig.Win32.Cursor = Rig.RestingCursor; // back on the game's monitor
        rig.Settle(TimeSpan.FromSeconds(3));
        Assert.IsTrue(rig.Io.CheckReadiness(new ReadinessQuery(InputKind.Click, Toggle, false, CrossMonitorAllowed: false)).Ready);
    }

    [TestMethod]
    public void CrossMonitorNavFixedToggleIsPressedWithoutTheSlidCheck()
    {
        using var rig = CrossRig();

        var r = rig.Io.SlideClick(ToggleClick(rig), CancellationToken.None);

        Assert.AreEqual(InputDisposition.Sent, r.Disposition, r.Abort.ToString());
        Assert.AreEqual(AbortReason.None, r.Abort);
        Assert.IsTrue(r.DownPosted && r.UpPosted && r.UnfocusSentWhileSlid);
        Assert.AreEqual(-988, r.Slide!.Plan.Dx);
        Assert.AreEqual(544, r.Slide.Plan.Dy);
        Assert.AreEqual(@"\\.\DISPLAY1", r.Slide.Plan.SlidMonitor, "c13: the largest overlap is the other monitor");
        Assert.IsFalse(r.SlidVerification!.Found, "the failed check is reported as it was");
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
        Assert.IsTrue(r.Slide.BackAtHome);

        // Same timeline as a verified click: focus, slide, capture, MOVE, DOWN, UP, unfocus while slid, slide back.
        var lp = HardRules.MouseLParam(Toggle);
        CollectionAssert.AreEqual(
            new[]
            {
                "send(0x1C,1,0)", "send(0x86,1,0)", "send(0x6,1,0)", "send(0x7,0,0)",
                "swp(-996,536,0x0215)", "print",
                $"post(0x200,0,{lp})", $"post(0x201,1,{lp})", $"post(0x202,0,{lp})",
                "send(0x8,0,0)", "send(0x6,0,0)", "send(0x86,0,0)", "send(0x1C,0,0)",
                "swp(-8,-8,0x0215)",
            },
            rig.Acting.Select(c => c.ToString()).ToArray(),
            string.Join(" ", rig.Acting));

        var input = InputRecord(rig);
        Assert.AreEqual(SlidVerifyRules.SkippedCode, input.Fields["verify"]);
        Assert.AreEqual("skipped.crossMonitor.navFixed", input.Fields["verify"]);
        Assert.AreEqual("allowed", input.Fields["navFixed"]);
        Assert.AreEqual(true, rig.Journal.Records.Last(j => j.Kind == JournalKind.Slide).Fields["crossMonitor"]);
    }

    [TestMethod]
    public void CrossMonitorMenuAndBagNavFixedTargetsAlsoProceed()
    {
        foreach (var id in new[] { "hud.toggle.expanded", "menu.ability", "hud.bag" })
        {
            using var rig = CrossRig();
            var r = rig.Io.SlideClick(ToggleClick(rig, id), CancellationToken.None);
            Assert.AreEqual(InputDisposition.Sent, r.Disposition, id + " " + r.Abort);
            Assert.AreEqual(SlidVerifyRules.SkippedCode, InputRecord(rig).Fields["verify"], id);
        }
    }

    [TestMethod]
    public void CrossMonitorAimInsideTheButtonStillCounts()
    {
        using var rig = CrossRig();
        var r = rig.Io.SlideClick(ToggleClick(rig, templateCenter: Toggle.Offset(12, -8)), CancellationToken.None);
        Assert.AreEqual(InputDisposition.Sent, r.Disposition, r.Abort.ToString());
    }

    [TestMethod]
    public void CrossMonitorCommitGatedOffsetAndOtherTargetsAbortAsBefore()
    {
        var cases = new (string Name, Action<Rig> Arrange, Func<Rig, ClickRequest> Request, string Code)[]
        {
            ("make (commit, gated, not NavFixed)", _ => { }, r => ToggleClick(r, "craft.make"), "tag.missing"),
            ("qty max (gated, not NavFixed)", _ => { }, r => ToggleClick(r, "craft.qty.max"), "tag.missing"),
            ("NavFixed + Commit", r => r.Templates.Add(FakeTemplates.Def("test.navcommit", 1855, 318, TargetTags.NavFixed | TargetTags.Commit, AutomationSwitch.Crafting)), r => ToggleClick(r, "test.navcommit"), "commit"),
            ("NavFixed + gate", r => r.Templates.Add(FakeTemplates.Def("test.navgated", 1855, 318, TargetTags.NavFixed, AutomationSwitch.Crafting)), r => ToggleClick(r, "test.navgated"), "gated"),
            ("NavFixed + anchor", r => r.Templates.Add(FakeTemplates.Def("test.navanchor", 1855, 318, TargetTags.NavFixed) with { AnchorId = "hud.bag", OffsetFromAnchor = new PointI(107, 6) }), r => ToggleClick(r, "test.navanchor"), "anchored"),
            ("favourites-style offset click", _ => { }, r => ToggleClick(r, templateCenter: Toggle.Offset(-112, 0)), "offset"),
            ("engine did not opt in", _ => { }, r => ToggleClick(r, navFixed: false), "notRequested"),
            ("suspect template", r => r.Templates.Suspect.Add("hud.toggle.collapsed"), r => ToggleClick(r), "template.notClickable"),
            ("unknown template", _ => { }, r => ToggleClick(r, "test.unknown"), "template.missing"),
        };

        foreach (var (name, arrange, request, code) in cases)
        {
            using var rig = CrossRig();
            arrange(rig);

            var r = rig.Io.SlideClick(request(rig), CancellationToken.None);

            Assert.AreEqual(InputDisposition.AbortedAfterFocus, r.Disposition, name);
            Assert.AreEqual(AbortReason.SlidTemplateMismatch, r.Abort, name);
            Assert.IsFalse(rig.Acting.Any(IsDown), name + ": no DOWN");
            Assert.IsFalse(rig.Acting.Any(c => c.Kind == "post"), name + ": no button message at all");
            Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect, name);
            Assert.AreEqual(4, rig.Acting.Count(c => c.IsFocusOff), name + ": unfocus after the abort");
            var input = InputRecord(rig);
            Assert.AreEqual("mismatch", input.Fields["verify"], name);
            Assert.AreEqual(code, input.Fields["navFixed"], name);
        }
    }

    [TestMethod]
    public void CrossMonitorWithoutTheC13CaptureGapAborts()
    {
        var cases = new (string Name, Func<RectI, PointI, SizeI, byte[]?> Pixels, string Code)[]
        {
            ("target drawn but not matching (a real mismatch)", (_, _, s) => Paint(s, (_, _) => true), "target.notBlack"),
            ("whole capture black (black game image)", (_, _, s) => Paint(s, (_, _) => false), "otherPart.black"),
            ("short capture buffer", (_, _, _) => new byte[16], "frame.short"),
        };

        foreach (var (name, pixels, code) in cases)
        {
            using var rig = CrossRig(pixels);

            var r = rig.Io.SlideClick(ToggleClick(rig), CancellationToken.None);

            Assert.AreEqual(AbortReason.SlidTemplateMismatch, r.Abort, name);
            Assert.IsFalse(rig.Acting.Any(IsDown), name);
            Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect, name);
            Assert.AreEqual(code, InputRecord(rig).Fields["navFixed"], name);
        }
    }

    [TestMethod]
    public void CrossMonitorCaptureFailureStillAborts()
    {
        using var rig = CrossRig();
        rig.Win32.PrintFails = true;

        var r = rig.Io.SlideClick(ToggleClick(rig), CancellationToken.None);

        Assert.AreEqual(AbortReason.SlidCaptureFailed, r.Abort);
        Assert.IsFalse(rig.Acting.Any(IsDown));
        Assert.AreEqual("captureFailed", InputRecord(rig).Fields["verify"]);
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    [TestMethod]
    public void CrossMonitorNotClickableIsRefusedAsBefore()
    {
        using var rig = CrossRig();
        rig.Vision.VerifyResult = new MatchResult("hud.toggle.collapsed", false, default, 0, 0, 0, MatchFailure.NotClickable);

        var r = rig.Io.SlideClick(ToggleClick(rig), CancellationToken.None);

        Assert.AreEqual(InputDisposition.Refused, r.Disposition);
        Assert.IsFalse(rig.Acting.Any(IsDown));
        Assert.AreEqual("notClickable", InputRecord(rig).Fields["verify"]);
    }

    [TestMethod]
    public void CrossMonitorVerifiedCaptureIsANormalClick()
    {
        using var rig = CrossRig();
        rig.Vision.VerifyResult = null; // the slid check passes (should a capture ever show the target)

        var r = rig.Io.SlideClick(ToggleClick(rig, "craft.make"), CancellationToken.None);

        Assert.AreEqual(InputDisposition.Sent, r.Disposition, r.Abort.ToString());
        Assert.AreEqual("ok", InputRecord(rig).Fields["verify"]);
        Assert.IsNull(InputRecord(rig).Fields["navFixed"]);
    }

    [TestMethod]
    public void CrossMonitorPopupSinceTheLocateStillAbortsANavFixedClick()
    {
        using var rig = CrossRig();
        var req = ToggleClick(rig);
        rig.Log.Append((p, s, t) => new UiLogEvent(p, s, t, UiVerb.PopupRealShow, "UI_ITEM_REWARD", null));

        var r = rig.Io.SlideClick(req, CancellationToken.None);

        Assert.AreEqual(AbortReason.LogGatePopup, r.Abort);
        Assert.IsFalse(rig.Acting.Any(IsDown));
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    [TestMethod]
    public void CrossMonitorCursorMoveWhileSlidStillAbortsANavFixedClick()
    {
        using var rig = CrossRig();
        rig.OnCall = c =>
        {
            if (c.Kind == "print")
            {
                rig.Win32.Cursor = CrossCursor.Offset(3, 0);
            }
        };

        var r = rig.Io.SlideClick(ToggleClick(rig), CancellationToken.None);

        Assert.AreEqual(AbortReason.CursorMoved, r.Abort);
        Assert.IsFalse(rig.Acting.Any(IsDown));
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    [TestMethod]
    public void SameMonitorMismatchStillAbortsEvenForANavFixedTarget()
    {
        using var rig = new Rig();
        rig.Win32.PrintPixels = (_, _, s) => Paint(s, (x, _) => x < 988); // even a c13-like gap does not matter here
        rig.ConnectAndSettle();
        rig.Vision.VerifyResult = new MatchResult("hud.bag", false, Rig.BagTarget, 0.12, 0.10, 0, MatchFailure.BelowThreshold);

        var r = rig.Io.SlideClick(rig.BagClick() with { NavFixed = true }, CancellationToken.None);

        Assert.AreEqual(InputDisposition.AbortedAfterFocus, r.Disposition);
        Assert.AreEqual(AbortReason.SlidTemplateMismatch, r.Abort);
        Assert.AreEqual(r.Slide!.Plan.HomeMonitor, r.Slide.Plan.SlidMonitor, "same-monitor slide");
        Assert.IsFalse(rig.Acting.Any(IsDown));
        var input = InputRecord(rig);
        Assert.AreEqual("mismatch", input.Fields["verify"]);
        Assert.IsNull(input.Fields["navFixed"]);
    }

    [TestMethod]
    public void SameMonitorVerifiedClickJournalsVerifyOk()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual(InputDisposition.Sent, r.Disposition);
        Assert.AreEqual("ok", InputRecord(rig).Fields["verify"]);
    }
}

/// <summary>The pure cross-monitor rule (<see cref="SlidVerifyRules"/>).</summary>
[TestClass]
public sealed class SlidVerifyRulesTests
{
    private static readonly SizeI Client = new(1920, 1009);
    private static readonly RectI Display1 = new(-1920, 0, 0, 1080);
    private static readonly RectI Display2 = new(0, 0, 1920, 1080);
    private static readonly PointI SlidOrigin = new(-988, 567);
    private static readonly PointI Toggle = new(1855, 318);

    private static ClickRequest Req(bool navFixed = true, PointI? center = null) =>
        new("s", "hud.toggle.collapsed", Toggle, FireOn.Down, null, TimeSpan.Zero, [], false, center, navFixed);

    private static TemplateDefinition Nav(TargetTags tags = TargetTags.Toggle | TargetTags.HudAnchor | TargetTags.NavFixed) =>
        FakeTemplates.Def("hud.toggle.collapsed", 1855, 318, tags);

    private static byte[] Frame(Func<int, int, bool> drawn)
    {
        var px = new byte[Client.Width * Client.Height * 4];
        for (var y = 0; y < Client.Height; y++)
        {
            for (var x = 0; x < Client.Width; x++)
            {
                if (drawn(x, y))
                {
                    var i = ((y * Client.Width) + x) * 4;
                    px[i] = 90;
                    px[i + 1] = 120;
                    px[i + 2] = 60;
                }
            }
        }

        return px;
    }

    [TestMethod]
    public void NavFixedRefusalCodes()
    {
        Assert.IsNull(SlidVerifyRules.NavFixedRefusal(Req(), Nav(), true));
        Assert.IsNull(SlidVerifyRules.NavFixedRefusal(Req(center: Toggle.Offset(18, -10)), Nav(), true), "largest aim inside the button");
        Assert.AreEqual("notRequested", SlidVerifyRules.NavFixedRefusal(Req(navFixed: false), Nav(), true));
        Assert.AreEqual("template.missing", SlidVerifyRules.NavFixedRefusal(Req(), null, false));
        Assert.AreEqual("template.notClickable", SlidVerifyRules.NavFixedRefusal(Req(), Nav(), false));
        Assert.AreEqual("template.notClickable", SlidVerifyRules.NavFixedRefusal(Req(), Nav() with { Status = TemplateStatus.Provisional }, true));
        Assert.AreEqual("tag.missing", SlidVerifyRules.NavFixedRefusal(Req(), Nav(TargetTags.Toggle | TargetTags.HudAnchor), true));
        Assert.AreEqual("commit", SlidVerifyRules.NavFixedRefusal(Req(), Nav(TargetTags.NavFixed | TargetTags.Commit) with { Gate = AutomationSwitch.Crafting }, true));
        Assert.AreEqual("commit", SlidVerifyRules.NavFixedRefusal(Req(), Nav(TargetTags.NavFixed | TargetTags.Forbidden), true));
        Assert.AreEqual("gated", SlidVerifyRules.NavFixedRefusal(Req(), Nav() with { Gate = AutomationSwitch.Homestead }, true));
        Assert.AreEqual("anchored", SlidVerifyRules.NavFixedRefusal(Req(), Nav() with { AnchorId = "hud.bag", OffsetFromAnchor = new PointI(1, 1) }, true));
        Assert.AreEqual("offset", SlidVerifyRules.NavFixedRefusal(Req(center: Toggle.Offset(19, 0)), Nav(), true));
        Assert.AreEqual("offset", SlidVerifyRules.NavFixedRefusal(Req(center: Toggle.Offset(0, 11)), Nav(), true));
        Assert.AreEqual("offset", SlidVerifyRules.NavFixedRefusal(Req(center: Toggle.Offset(-1, -258)), Nav(), true), "a favourites slot (anchor + offset)");
    }

    [TestMethod]
    public void CrossMonitorIsTheLargestOverlapOnAnotherMonitor()
    {
        var plan = new SlidePlan(new RectI(-8, -8, 1928, 1040), new RectI(-996, 536, 940, 1584), -988, 544, @"\\.\DISPLAY2", @"\\.\DISPLAY1", false, 0);
        Assert.IsTrue(SlidVerifyRules.IsCrossMonitor(plan));
        Assert.IsFalse(SlidVerifyRules.IsCrossMonitor(plan with { SlidMonitor = @"\\.\DISPLAY2" }));
    }

    [TestMethod]
    public void CaptureGapNeedsTheExactC13Signature()
    {
        var c13 = Frame((x, y) => x < 988 && y < 513);
        Assert.IsNull(SlidVerifyRules.CaptureGapRefusal(c13, Client, Toggle, SlidOrigin, Display2, Display1));

        Assert.AreEqual("frame.short", SlidVerifyRules.CaptureGapRefusal(new byte[16], Client, Toggle, SlidOrigin, Display2, Display1));
        Assert.AreEqual("frame.short", SlidVerifyRules.CaptureGapRefusal(null, Client, Toggle, SlidOrigin, Display2, Display1));
        Assert.AreEqual("slidMonitor.unknown", SlidVerifyRules.CaptureGapRefusal(c13, Client, Toggle, SlidOrigin, Display2, null));
        Assert.AreEqual("target.notOnHomeMonitor", SlidVerifyRules.CaptureGapRefusal(c13, Client, new PointI(500, 300), SlidOrigin, Display2, Display1), "target on the drawn side");
        Assert.AreEqual("target.notBlack", SlidVerifyRules.CaptureGapRefusal(Frame((_, _) => true), Client, Toggle, SlidOrigin, Display2, Display1));
        Assert.AreEqual("target.notBlack", SlidVerifyRules.CaptureGapRefusal(Frame((x, y) => x < 988 || (x == Toggle.X && y == Toggle.Y) || (x > 1840 && y > 300 && y < 310)), Client, Toggle, SlidOrigin, Display2, Display1), "a few drawn pixels are enough to refuse");
        Assert.AreEqual("otherPart.black", SlidVerifyRules.CaptureGapRefusal(Frame((_, _) => false), Client, Toggle, SlidOrigin, Display2, Display1));
        Assert.AreEqual("otherPart.missing", SlidVerifyRules.CaptureGapRefusal(c13, Client, Toggle, SlidOrigin, Display2, new RectI(4000, 0, 5920, 1080)));
    }

    [TestMethod]
    public void BlackFractionCountsNearBlackOnly()
    {
        var px = new byte[4 * 4 * 4];
        px[0] = 8;
        px[1] = 8;
        px[2] = 8; // (0,0) near black: counts
        px[4] = 9; // (1,0) one channel above: not black
        Assert.AreEqual(15.0 / 16, SlidVerifyRules.BlackFraction(px, 4, new RectI(0, 0, 4, 4), 1), 1e-9);
        Assert.AreEqual(1.0, SlidVerifyRules.BlackFraction(px, 4, new RectI(0, 2, 4, 4), 1), 1e-9);
        Assert.AreEqual(1.0, SlidVerifyRules.BlackFraction(px, 4, new RectI(0, 0, 4, 4), 2), 1e-9, "step 2 samples (0,0),(2,0),(0,2),(2,2)");
    }
}
