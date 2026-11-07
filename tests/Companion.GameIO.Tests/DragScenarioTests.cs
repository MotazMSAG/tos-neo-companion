using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.GameIO.Geometry;
using Companion.GameIO.Input;
using Companion.GameIO.Tests.Fakes;

namespace Companion.GameIO.Tests;

/// <summary>
/// The slide drag (SPEC 7.3a, port of proofs\p0_c14_drag_scroll.ps1) against the fake Win32 sink: the proven timeline,
/// release-first abort orders, its own dead-man, the release point's readiness, pacing and the marker path. The watchdog
/// runs in manual mode and samples on every fake Sleep; no real window is ever touched.
/// </summary>
[TestClass]
public sealed class DragScenarioTests
{
    /// <summary>The recipe list drag of the list scan: press at (420,860) in the gap of columns 1-2, two rows (280 px) up.</summary>
    private static readonly PointI Start = new(420, 860);
    private static readonly PointI Up280 = new(0, -280);

    // c3 run 3 geometry: cursor (918,672), client offset (8,31): slid x = 918 - 420 - 8 = 490, y = 672 - 860 - 31 = -219.
    private static readonly RectI FirstSlid = new(490, -219, 490 + 1936, -219 + 1048);

    private static DragRequest Drag(Rig rig, PointI? start = null, PointI? delta = null) =>
        new("scroll", "craft.fav.anchor", start ?? Start, new PointI(591, 756), delta ?? Up280, rig.Log.Position, rig.Clock.Monotonic, false);

    private static bool IsDown(Call c) => c.Kind == "post" && c.Msg == HardRules.WM_LBUTTONDOWN;

    private static bool IsUp(Call c) => c.Kind == "post" && c.Msg == HardRules.WM_LBUTTONUP;

    private static bool IsDragMove(Call c) => c.Kind == "post" && c.Msg == HardRules.WM_MOUSEMOVE && c.WParam == HardRules.MK_LBUTTON;

    private static string Post(uint msg, long w, PointI p) => $"post(0x{msg:X},{w},{HardRules.MouseLParam(p)})";

    [TestMethod]
    public void TheDragPlan_SplitsIntoStepsOfAtMost20Px()
    {
        var p = DragPlan.Create(Start, Up280)!;
        Assert.AreEqual((14, new PointI(420, 580)), (p.Steps, p.End));
        Assert.AreEqual(new PointI(420, 840), p.PointAt(1));
        Assert.AreEqual(FirstSlid.Offset(0, 20), p.SlidAt(FirstSlid, 1), "the window moves down while the pointer moves up");
        Assert.AreEqual(15, DragPlan.Create(Start, new PointI(0, -300))!.Steps, "c14: 15 steps of 20 px");
        Assert.AreEqual(1, DragPlan.Create(Start, new PointI(0, 7))!.Steps);
        var diagonal = DragPlan.Create(Start, new PointI(-30, -50))!;
        Assert.AreEqual((3, new PointI(390, 810)), (diagonal.Steps, diagonal.PointAt(3)));
        Assert.IsNull(DragPlan.Create(Start, default));
        Assert.IsNull(DragPlan.Create(Start, new PointI(0, -(HardRules.MaxDragDistancePx + 1))));
        Assert.AreEqual(HardRules.MaxDragSteps, DragPlan.Create(Start, new PointI(HardRules.MaxDragDistancePx, 0))!.Steps);
    }

    [TestMethod]
    public void ASuccessfulDragFollowsTheProvenC14Timeline()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.Win32.ClearCalls();

        var r = rig.Io.SlideDrag(Drag(rig), CancellationToken.None);

        Assert.IsTrue(r.Completed, $"{r.Press.Disposition} {r.Press.Abort}");
        Assert.AreEqual((14, 14, new PointI(420, 580)), (r.StepsPlanned, r.StepsDone, r.Release));
        Assert.AreEqual((InputPhase.Completed, 4), (r.Press.Phase, r.Press.FocusMessagesSent));
        Assert.IsTrue(r.Press.DownPosted && r.Press.UpPosted && r.Press.UnfocusSentWhileSlid);
        Assert.IsTrue(r.Press.Slide!.BackAtHome);
        Assert.IsFalse(r.Press.Slide.ForcedSlideBack);
        Assert.AreEqual((498, -211), (r.Press.Slide.Plan.Dx, r.Press.Slide.Plan.Dy), "first slide to (490,-219) from home (-8,-8)");
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
        Assert.IsFalse(File.Exists(rig.Paths.SlideMarkerPath), "marker deleted after the verified slide-back");

        // Exact order: focus x4, slide, capture, MOVE, DOWN, 14 x (window step, MOVE with MK_LBUTTON), UP, unfocus x4, home.
        var expected = new List<string>
        {
            "send(0x1C,1,0)", "send(0x86,1,0)", "send(0x6,1,0)", "send(0x7,0,0)",
            "swp(490,-219,0x0215)", "print",
            Post(HardRules.WM_MOUSEMOVE, 0, Start), Post(HardRules.WM_LBUTTONDOWN, 1, Start),
        };
        for (var k = 1; k <= 14; k++)
        {
            expected.Add($"swp(490,{-219 + (20 * k)},0x0215)");
            expected.Add(Post(HardRules.WM_MOUSEMOVE, 1, new PointI(420, 860 - (20 * k))));
        }

        expected.AddRange([Post(HardRules.WM_LBUTTONUP, 0, new PointI(420, 580)), "send(0x8,0,0)", "send(0x6,0,0)", "send(0x86,0,0)", "send(0x1C,0,0)", "swp(-8,-8,0x0215)"]);
        var acting = rig.Acting;
        CollectionAssert.AreEqual(expected, acting.Select(c => c.ToString()).ToList(), string.Join(" ", acting));

        // Proven c14 timings: MOVE->DOWN 40 ms, DOWN->first step 120 ms, 45 ms per step, hold 250 ms, UP->unfocus 250 ms.
        var moves = acting.Where(IsDragMove).ToList();
        var steps = acting.Where(c => c.Kind == "swp").Skip(1).Take(14).ToList();
        var down = acting.Single(IsDown);
        var up = acting.Single(IsUp);
        Assert.IsTrue(down.At - acting.First(c => c.Kind == "post").At >= HardRules.DragMoveToDown);
        Assert.IsTrue(steps[0].At - down.At >= HardRules.DragDownToFirstStep);
        for (var k = 1; k < 14; k++)
        {
            Assert.IsTrue(steps[k].At - steps[k - 1].At >= HardRules.DragStepInterval, $"step {k}");
            Assert.IsTrue(moves[k - 1].At >= steps[k - 1].At && moves[k - 1].At < steps[k].At, "each MOVE follows its own window step");
        }

        Assert.IsTrue(up.At - moves[^1].At >= HardRules.DragStepInterval + HardRules.DragHoldBeforeUp, "hold still: no fling");
        Assert.IsTrue(acting.First(c => c.IsFocusOff).At - up.At >= HardRules.DragAfterUp);
        Assert.IsTrue(r.Press.Slide.Displaced < HardRules.DragDeadManDeadline(14));

        // One journal record of the input (codes only), written after the slide-back.
        var input = rig.Journal.Records.Last(j => j.Kind == JournalKind.Input);
        Assert.AreEqual(("drag", "Sent", 14, 14, -280), ((string)input.Fields["input"]!, (string)input.Fields["disposition"]!, (int)input.Fields["dragSteps"]!, (int)input.Fields["dragStepsDone"]!, (int)input.Fields["dragDy"]!));
    }

    [TestMethod]
    public void TheLongestDrag_RunsOnItsOwnDeadMan_AndCountsAsOneBatchOneSlideOneFocusPair()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        var r = rig.Io.SlideDrag(Drag(rig, new PointI(420, 880), new PointI(0, -HardRules.MaxDragDistancePx)), CancellationToken.None);

        Assert.IsTrue(r.Completed, $"{r.Press.Abort}");
        Assert.AreEqual(HardRules.MaxDragSteps, r.StepsDone);
        Assert.IsTrue(r.Press.Slide!.Displaced > HardRules.DeadManDeadline, "displaced longer than a click's dead-man, without a forced end");
        Assert.IsFalse(r.Press.Slide.ForcedSlideBack);
        var pacing = rig.Io.Pacing;
        Assert.AreEqual((1, 1, 1, 1), (pacing.BatchesLastMinute, pacing.SlidesLastMinute, pacing.FocusPairsLastMinute, pacing.BatchesLastHour));
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    [TestMethod]
    public void CursorMovedMidDrag_ReleasesTheButtonFirst_ThenSlidesHome_ThenUnfocuses()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        var moves = 0;
        rig.OnCall = c =>
        {
            if (IsDragMove(c) && ++moves == 3)
            {
                rig.Win32.Cursor = new PointI(930, 680); // the user moves the mouse
            }
        };

        var r = rig.Io.SlideDrag(Drag(rig), CancellationToken.None);

        Assert.IsFalse(r.Completed);
        Assert.AreEqual((InputDisposition.Sent, AbortReason.CursorMoved, 3), (r.Press.Disposition, r.Press.Abort, r.StepsDone));
        Assert.AreEqual(new PointI(420, 800), r.Release, "released where the pointer was");
        var tail = rig.Acting.SkipWhile(c => !(IsDragMove(c) && c.LParam == HardRules.MouseLParam(new PointI(420, 800)))).Skip(1).ToList();
        CollectionAssert.AreEqual(
            new[] { Post(HardRules.WM_LBUTTONUP, 0, new PointI(420, 800)), "swp(-8,-8,0x4215)", "send(0x8,0,0)", "send(0x6,0,0)", "send(0x86,0,0)", "send(0x1C,0,0)" },
            tail.Select(c => c.ToString()).ToArray(),
            string.Join(" ", tail));
        Assert.IsTrue(tail[1].At - tail[0].At >= TimeSpan.FromMilliseconds(20), "20 ms between UP and the slide-back");
        Assert.AreEqual(1, rig.Acting.Count(IsUp), "exactly one release");
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
        Assert.IsFalse(File.Exists(rig.Paths.SlideMarkerPath));
    }

    [TestMethod]
    public void AUserButtonMidDrag_AlsoReleasesFirst()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        var moves = 0;
        rig.OnCall = c =>
        {
            if (IsDragMove(c) && ++moves == 2)
            {
                rig.Win32.ButtonsDown.Add(0x02);
            }
        };

        var r = rig.Io.SlideDrag(Drag(rig), CancellationToken.None);

        Assert.AreEqual((AbortReason.UserButton, 2), (r.Press.Abort, r.StepsDone));
        var afterUp = rig.Acting.SkipWhile(c => !IsUp(c)).ToList();
        Assert.IsTrue(afterUp.Count > 0 && afterUp.Any(c => c.Kind == "swp" && c.X == -8 && c.Y == -8), string.Join(" ", rig.Acting));
        Assert.IsFalse(rig.Acting.TakeWhile(c => !IsUp(c)).Any(c => c.Kind == "swp" && c.X == -8), "no slide-back before the release");
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    [TestMethod]
    public void GameForegroundMidDrag_ReleasesThenSlidesBack_AndNeverUnfocuses()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        var moves = 0;
        rig.OnCall = c =>
        {
            if (IsDragMove(c) && ++moves == 4)
            {
                rig.Win32.Foreground = Rig.Game;
            }
        };

        var r = rig.Io.SlideDrag(Drag(rig), CancellationToken.None);

        Assert.AreEqual((InputDisposition.UserTookOver, AbortReason.GameForeground), (r.Press.Disposition, r.Press.Abort));
        var tail = rig.Acting.SkipWhile(c => !IsUp(c)).Select(c => c.ToString()).ToArray();
        CollectionAssert.AreEqual(new[] { Post(HardRules.WM_LBUTTONUP, 0, new PointI(420, 780)), "swp(-8,-8,0x4215)" }, tail, string.Join(" ", tail));
        Assert.IsFalse(rig.Acting.SkipWhile(c => !IsDown(c)).Any(c => c.IsFocusOff), "no fake unfocus while the game is really foreground");
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    [TestMethod]
    public void DeadManMidDrag_UsesTheForcedOrder_WithTheReleaseBeforeTheSlideBack()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        var moves = 0;
        rig.OnCall = c =>
        {
            if (IsDragMove(c) && ++moves == 2)
            {
                rig.Clock.Advance(HardRules.DragDeadManDeadline(14)); // the game stalls mid-drag
            }
        };

        var r = rig.Io.SlideDrag(Drag(rig), CancellationToken.None);

        Assert.AreEqual(AbortReason.DeadMan, r.Press.Abort);
        var tail = rig.Acting.SkipWhile(c => !(IsDragMove(c) && c.LParam == HardRules.MouseLParam(new PointI(420, 820)))).Skip(1).Select(c => c.ToString()).ToArray();
        CollectionAssert.AreEqual(
            new[] { "notify(0x8,0,0)", "notify(0x6,0,0)", "notify(0x86,0,0)", "notify(0x1C,0,0)", Post(HardRules.WM_LBUTTONUP, 0, new PointI(420, 820)), "swp(-8,-8,0x4215)" },
            tail,
            string.Join(" ", tail));
        Assert.IsTrue(r.Press.Slide!.ForcedSlideBack);
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    [TestMethod]
    public void AStepThatDoesNotLand_ReleasesFirst_AndRestoresHome()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        var steps = 0;
        rig.OnCall = c =>
        {
            if (c.Kind == "swp" && c.Y > -219 && c.Y < 0 && ++steps == 2)
            {
                rig.GameWindow.Rect = rig.GameWindow.Rect.Offset(0, 5); // the game nudges itself: the step did not land
            }
        };

        var r = rig.Io.SlideDrag(Drag(rig), CancellationToken.None);

        Assert.IsFalse(r.Completed);
        Assert.AreEqual(1, r.StepsDone);
        var up = rig.Acting.ToList().FindIndex(IsUp);
        Assert.IsTrue(up > 0, string.Join(" ", rig.Acting));
        Assert.IsFalse(rig.Acting.Skip(up + 1).Any(IsDragMove), "no move after the release");
        Assert.IsFalse(rig.Acting.Take(up).Any(c => c.Kind == "swp" && c.X == -8), "release before any move home");
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    [TestMethod]
    public void AnInvalidDrag_IsRefusedWithZeroMessages()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.Win32.ClearCalls();
        foreach (var (start, delta) in new[]
        {
            (Start, new PointI(0, 0)),
            (Start, new PointI(0, -(HardRules.MaxDragDistancePx + 20))),
            (new PointI(420, 100), new PointI(0, -120)), // release above the client
            (new PointI(1900, 500), new PointI(40, 0)), // release right of the client
        })
        {
            var r = rig.Io.SlideDrag(Drag(rig, start, delta), CancellationToken.None);
            Assert.AreEqual((InputDisposition.Refused, 0), (r.Press.Disposition, r.StepsDone), $"{start} {delta}");
            Assert.AreEqual(0, r.Press.FocusMessagesSent);
        }

        Assert.AreEqual(0, rig.Acting.Count, string.Join(" ", rig.Acting));
    }

    [TestMethod]
    public void AReleasePointOnAnotherMonitor_IsNotSent()
    {
        // Press at x 1500 slides the window to x -590 (largest overlap DISPLAY2); the release 400 px further right needs
        // x -990, mostly on DISPLAY1: the game would change monitor mid-drag.
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.Win32.ClearCalls();

        var r = rig.Io.SlideDrag(Drag(rig, new PointI(1500, 500), new PointI(400, 0)), CancellationToken.None);

        Assert.AreEqual((InputDisposition.NotSent, AbortReason.NotReady), (r.Press.Disposition, r.Press.Abort));
        Assert.AreEqual(0, rig.Acting.Count, "nothing sent");
        Assert.AreEqual("drag.monitorChange", rig.Journal.Records.Last(j => j.Kind == JournalKind.Input).Fields["detail"]);
    }

    [TestMethod]
    public void TheMarkerRecordsTheDragPath_AndCrashRecoveryRestoresAnyRectOnIt()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        string? marker = null;
        var moves = 0;
        rig.OnCall = c =>
        {
            if (IsDragMove(c) && ++moves == 5)
            {
                marker = File.ReadAllText(rig.Paths.SlideMarkerPath);
            }
        };

        Assert.IsTrue(rig.Io.SlideDrag(Drag(rig), CancellationToken.None).Completed);
        Assert.IsNotNull(marker);
        using (var doc = JsonDocument.Parse(marker))
        {
            var end = doc.RootElement.GetProperty("pathEnd").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            CollectionAssert.AreEqual(new[] { 490, 61, 490 + 1936, 61 + 1048 }, end, "the window's last step rect");
        }

        var data = new SlideMarkerData(Rig.GamePid, Rig.Game, Rig.GameStart, Rig.HomeRect, FirstSlid, DateTimeOffset.UnixEpoch, FirstSlid.Offset(0, 280));
        foreach (var dy in new[] { 0, 20, 140, 280 })
        {
            Assert.IsTrue(data.IsOurDisplacement(FirstSlid.Offset(0, dy)), $"step +{dy}");
            Assert.AreEqual(MarkerDecision.RestoreMoveOnly, MarkerRecovery.Decide(data, false, true, Rig.GamePid, Rig.GameStart, true, FirstSlid.Offset(0, dy), false));
        }

        foreach (var off in new[] { new PointI(0, 300), new PointI(0, -20), new PointI(5, 100) })
        {
            Assert.IsFalse(data.IsOurDisplacement(FirstSlid.Offset(off.X, off.Y)), off.ToString());
            Assert.AreEqual(MarkerDecision.LeaveAloneNeither, MarkerRecovery.Decide(data, false, true, Rig.GamePid, Rig.GameStart, true, FirstSlid.Offset(off.X, off.Y), false));
        }

        Assert.IsFalse((data with { PathEnd = null }).IsOurDisplacement(FirstSlid.Offset(0, 20)), "a click's marker accepts its slid rect only");
    }

    /// <summary>
    /// The list scan's scroll drag with the cursor resting high (live run 2026-10-14, card 6 scene): the press (420,860)
    /// slides the window to (899,-414), the list anchor (485,123) to screen y -260, above every monitor.
    /// </summary>
    private static DragRequest ScrollFromTheCardScene(Frame located, bool patch = true) =>
        new("scroll", CardScene.AnchorId, CardScene.ScrollStart, CardScene.Anchor, Up280, located.LogAt, located.CapturedAtMonotonic, false, located, patch);

    [TestMethod]
    public void ScrollDragWithTheAnchorAboveTheScreen_IsPressedAfterThePatchCheck()
    {
        var (rig, _) = CardScene.Create();
        using var disposeRig = rig;
        var located = CardScene.Locate(rig);

        var r = rig.Io.SlideDrag(ScrollFromTheCardScene(located), CancellationToken.None);

        Assert.IsTrue(r.Completed, $"{r.Press.Disposition} {r.Press.Abort}");
        Assert.AreEqual((14, 14, new PointI(420, 580)), (r.StepsPlanned, r.StepsDone, r.Release));
        Assert.AreEqual((907, -406), (r.Press.Slide!.Plan.Dx, r.Press.Slide.Plan.Dy));
        Assert.IsFalse(r.Press.SlidVerification!.Found, "the anchor was not visible while slid");
        CollectionAssert.AreEqual(
            new[]
            {
                "send(0x1C,1,0)", "send(0x86,1,0)", "send(0x6,1,0)", "send(0x7,0,0)",
                "swp(899,-414,0x0215)", "print",
                Post(HardRules.WM_MOUSEMOVE, 0, CardScene.ScrollStart), Post(HardRules.WM_LBUTTONDOWN, 1, CardScene.ScrollStart),
                "swp(899,-394,0x0215)",
            },
            rig.Acting.Take(9).Select(c => c.ToString()).ToArray(),
            string.Join(" ", rig.Acting));
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
        var input = rig.Journal.Records.Last(j => j.Kind == JournalKind.Input);
        Assert.AreEqual(("drag", "patch.anchorOffscreen", "allowed"), ((string)input.Fields["input"]!, (string)input.Fields["verify"]!, (string)input.Fields["patch"]!));
    }

    [TestMethod]
    public void ADragWhosePressPatchChanged_AbortsBeforeTheButtonGoesDown()
    {
        var (rig, scene) = CardScene.Create();
        using var disposeRig = rig;
        scene.SlidShiftX = 113; // the list moved by a card column between the locate and the slid capture
        var located = CardScene.Locate(rig);

        var r = rig.Io.SlideDrag(ScrollFromTheCardScene(located), CancellationToken.None);

        Assert.AreEqual((InputDisposition.AbortedAfterFocus, AbortReason.SlidTemplateMismatch, 0), (r.Press.Disposition, r.Press.Abort, r.StepsDone));
        Assert.IsFalse(r.Press.DownPosted);
        CollectionAssert.AreEqual(
            new[]
            {
                "send(0x1C,1,0)", "send(0x86,1,0)", "send(0x6,1,0)", "send(0x7,0,0)",
                "swp(899,-414,0x0215)", "print", "swp(-8,-8,0x0215)",
                "send(0x8,0,0)", "send(0x6,0,0)", "send(0x86,0,0)", "send(0x1C,0,0)",
            },
            rig.Acting.Select(c => c.ToString()).ToArray(),
            string.Join(" ", rig.Acting));
        Assert.IsFalse(rig.Acting.Any(IsDragMove), "no MK_LBUTTON move");
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
        var input = rig.Journal.Records.Last(j => j.Kind == JournalKind.Input);
        Assert.AreEqual(("mismatch", "patch.mismatch"), ((string)input.Fields["verify"]!, (string)input.Fields["patch"]!));
    }

    [TestMethod]
    public void ADragWithoutTheOptIn_KeepsTheStrictCheck()
    {
        var (rig, _) = CardScene.Create();
        using var disposeRig = rig;
        var located = CardScene.Locate(rig);

        var r = rig.Io.SlideDrag(ScrollFromTheCardScene(located, patch: false), CancellationToken.None);

        Assert.AreEqual((AbortReason.SlidTemplateMismatch, 0), (r.Press.Abort, r.StepsDone));
        Assert.IsFalse(rig.Acting.Any(IsDown));
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
        var input = rig.Journal.Records.Last(j => j.Kind == JournalKind.Input);
        Assert.AreEqual("mismatch", input.Fields["verify"]);
        Assert.IsNull(input.Fields["patch"]);
    }

    [TestMethod]
    public void TheForegroundAbortReleasesFirstOnlyForADrag()
    {
        CollectionAssert.AreEqual(new[] { AbortStep.PostUp, AbortStep.SlideBackAsync }, AbortPlanner.Plan(Trigger.Foreground, true, true, true, true, releaseFirst: true).ToArray());
        CollectionAssert.AreEqual(new[] { AbortStep.SlideBackAsync, AbortStep.PostUp }, AbortPlanner.Plan(Trigger.Foreground, true, true, true, true).ToArray());
        CollectionAssert.AreEqual(
            new[] { AbortStep.PostUp, AbortStep.Wait20, AbortStep.SlideBack, AbortStep.Unfocus },
            AbortPlanner.Plan(Trigger.CursorMoved, true, true, true, true, releaseFirst: true).ToArray());
    }
}
