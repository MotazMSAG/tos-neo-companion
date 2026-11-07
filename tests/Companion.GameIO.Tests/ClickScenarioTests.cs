using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.GameIO.Tests.Fakes;

namespace Companion.GameIO.Tests;

/// <summary>
/// Slide-click state machine and abort orders against the fake Win32 sink (SPEC 7.3, 7.4, 13). The watchdog runs in
/// manual mode and samples on every fake Sleep; no real window is ever touched.
/// </summary>
[TestClass]
public sealed class ClickScenarioTests
{
    private static readonly HashSet<uint> FocusOffIds = [HardRules.WM_KILLFOCUS, HardRules.WM_ACTIVATE, HardRules.WM_NCACTIVATE, HardRules.WM_ACTIVATEAPP];

    private static List<Call> After(IReadOnlyList<Call> calls, Func<Call, bool> first) =>
        calls.SkipWhile(c => !first(c)).ToList();

    private static bool IsDown(Call c) => c.Kind == "post" && c.Msg == HardRules.WM_LBUTTONDOWN;

    private static bool IsUp(Call c) => c.Kind == "post" && c.Msg == HardRules.WM_LBUTTONUP;

    private static bool IsSwp(Call c, int x, int y) => c.Kind == "swp" && c.X == x && c.Y == y;

    [TestMethod]
    public void SuccessfulBagClickFollowsTheProvenTimeline()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.Win32.ClearCalls();

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual(InputDisposition.Sent, r.Disposition, r.Abort.ToString());
        Assert.AreEqual(InputPhase.Completed, r.Phase);
        Assert.AreEqual(AbortReason.None, r.Abort);
        Assert.AreEqual(4, r.FocusMessagesSent);
        Assert.IsTrue(r.FocusLogged && r.DownPosted && r.UpPosted && r.UnfocusSentWhileSlid && r.ExpectedSeenWhileSlid);
        Assert.IsFalse(r.UnfocusSkipped);
        Assert.IsNotNull(r.PressBlock);
        Assert.AreEqual(new PointI(1748, 314), r.PressBlock.ToClient(1009));
        Assert.IsNotNull(r.Slide);
        Assert.AreEqual(-830, r.Slide.Plan.Dx);
        Assert.AreEqual(335, r.Slide.Plan.Dy);
        Assert.AreEqual("Chrome_WidgetWin_1", r.Slide.CoverClass);
        Assert.IsTrue(r.Slide.BackAtHome);
        Assert.IsFalse(r.Slide.ForcedSlideBack);
        Assert.IsTrue(r.Slide.Displaced < HardRules.DisplacementBudget, r.Slide.Displaced.ToString());
        Assert.IsTrue(r.Slide.Watchdog.Samples > 10);
        Assert.IsNull(r.Incident);
        Assert.IsFalse(r.Cursor.MovedBeforeDown || r.Cursor.MovedInP4 || r.Cursor.MovedAfterP4 || r.Cursor.UserButton);
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
        Assert.IsFalse(File.Exists(rig.Paths.SlideMarkerPath), "marker deleted after the verified slide-back");

        // Exact order: focus x4, slide, capture, MOVE, DOWN, UP, unfocus x4 (while slid), slide back.
        var acting = rig.Acting;
        var lp = HardRules.MouseLParam(Rig.BagTarget);
        var expected = new[]
        {
            "send(0x1C,1,0)", "send(0x86,1,0)", "send(0x6,1,0)", "send(0x7,0,0)",
            "swp(-838,327,0x0215)", "print",
            $"post(0x200,0,{lp})", $"post(0x201,1,{lp})", $"post(0x202,0,{lp})",
            "send(0x8,0,0)", "send(0x6,0,0)", "send(0x86,0,0)", "send(0x1C,0,0)",
            "swp(-8,-8,0x0215)",
        };
        CollectionAssert.AreEqual(expected, acting.Select(c => c.ToString()).ToArray(), string.Join(" ", acting));

        // Proven timings: >= 300 ms focus settle, 150 ms slide settle, 50 ms MOVE->DOWN, 70 ms DOWN->UP.
        var t = acting.ToDictionary(c => c.ToString(), c => c.At);
        Assert.IsTrue(t["swp(-838,327,0x0215)"] - t["send(0x1C,1,0)"] >= HardRules.FocusSettle);
        Assert.IsTrue(t[$"post(0x200,0,{lp})"] - t["swp(-838,327,0x0215)"] >= HardRules.SlideSettle);
        Assert.IsTrue(t[$"post(0x201,1,{lp})"] - t[$"post(0x200,0,{lp})"] >= HardRules.MoveToDown);
        Assert.IsTrue(t[$"post(0x202,0,{lp})"] - t[$"post(0x201,1,{lp})"] >= HardRules.DownToUp);

        // Journal written after the slide-back, codes only.
        var input = rig.Journal.Records.Last(j => j.Kind == JournalKind.Input);
        Assert.AreEqual("Sent", input.Fields["disposition"]);
        Assert.IsTrue(rig.Journal.Records.Any(j => j.Kind == JournalKind.Slide));
    }

    [TestMethod]
    public void CursorMovedInP4IsRecordedOnlyForADownFiringTarget()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.OnCall = c =>
        {
            if (IsDown(c))
            {
                rig.Win32.Cursor = new PointI(930, 680);
            }
        };

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual(InputDisposition.Sent, r.Disposition);
        Assert.IsTrue(r.Cursor.MovedInP4);
        Assert.IsFalse(r.Cursor.MovedBeforeDown);
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    [TestMethod]
    public void CursorMovedAfterDownOfAnUpFiringTargetUsesTheForcedOrder()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.OnCall = c =>
        {
            if (IsDown(c))
            {
                rig.Win32.Cursor = new PointI(930, 680);
            }
        };

        var r = rig.Io.SlideClick(rig.BagClick(FireOn.Up), CancellationToken.None);

        var tail = After(rig.Acting, IsDown).Skip(1).Select(c => c.ToString()).ToArray();
        CollectionAssert.AreEqual(
            new[] { "notify(0x8,0,0)", "notify(0x6,0,0)", "notify(0x86,0,0)", "notify(0x1C,0,0)", $"post(0x202,0,{HardRules.MouseLParam(Rig.BagTarget)})", "swp(-8,-8,0x4215)" },
            tail,
            string.Join(" ", tail));
        Assert.AreEqual(AbortReason.CursorMoved, r.Abort);
        Assert.IsTrue(r.Slide!.ForcedSlideBack);
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    [TestMethod]
    public void CursorMovedBeforeTheSlideAbortsWithoutMovingTheWindow()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.OnCall = c =>
        {
            if (c.Kind == "send" && c.Msg == HardRules.WM_SETFOCUS)
            {
                rig.Win32.Cursor = new PointI(700, 500);
            }
        };

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual(InputDisposition.AbortedAfterFocus, r.Disposition);
        Assert.AreEqual(AbortReason.CursorMoved, r.Abort);
        Assert.IsTrue(r.Cursor.MovedBeforeDown);
        Assert.IsFalse(r.DownPosted);
        Assert.IsNull(r.Slide);
        Assert.IsFalse(rig.Acting.Any(c => c.Kind == "swp"), "no SetWindowPos at all");
        Assert.AreEqual(4, rig.Acting.Count(c => c.IsFocusOff), "unfocus after the aborted focus");
        Assert.IsFalse(File.Exists(rig.Paths.SlideMarkerPath));
    }

    [TestMethod]
    public void ThrowAfterDownPostsUpWaits20MsSlidesBackThenUnfocuses()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.OnCall = c =>
        {
            if (IsDown(c))
            {
                rig.Clock.ThrowOnNextSleep = new InvalidOperationException("boom");
            }
        };

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        var tail = After(rig.Acting, IsDown).Skip(1).ToList();
        CollectionAssert.AreEqual(
            new[] { $"post(0x202,0,{HardRules.MouseLParam(Rig.BagTarget)})", "swp(-8,-8,0x0215)", "send(0x8,0,0)", "send(0x6,0,0)", "send(0x86,0,0)", "send(0x1C,0,0)" },
            tail.Select(c => c.ToString()).ToArray(),
            string.Join(" ", tail));
        Assert.IsTrue(tail[1].At - tail[0].At >= TimeSpan.FromMilliseconds(20), "20 ms between UP and the slide-back");
        Assert.AreEqual(InputDisposition.Sent, r.Disposition);
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
        Assert.IsFalse(r.UnfocusSentWhileSlid);
    }

    [TestMethod]
    public void DeadManSendsNotifyUnfocusThenUpThenAsyncSlideBack()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.OnCall = c =>
        {
            if (IsDown(c))
            {
                rig.Clock.Advance(TimeSpan.FromMilliseconds(1600)); // the game stalls right after DOWN
            }
        };

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        var tail = After(rig.Acting, IsDown).Skip(1).Select(c => c.ToString()).ToArray();
        CollectionAssert.AreEqual(
            new[] { "notify(0x8,0,0)", "notify(0x6,0,0)", "notify(0x86,0,0)", "notify(0x1C,0,0)", $"post(0x202,0,{HardRules.MouseLParam(Rig.BagTarget)})", "swp(-8,-8,0x4215)" },
            tail,
            string.Join(" ", tail));
        Assert.AreEqual(AbortReason.DeadMan, r.Abort);
        Assert.AreEqual(InputDisposition.Sent, r.Disposition, "DOWN was delivered: the engine classifies Uncertain");
        Assert.IsTrue(r.Slide!.ForcedSlideBack);
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    [TestMethod]
    public void GameForegroundSlidesBackAtOnceAndNeverUnfocuses()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.OnCall = c =>
        {
            if (IsSwp(c, -838, 327))
            {
                rig.Win32.Foreground = Rig.Game; // the user clicks into the game
            }
        };

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual(InputDisposition.UserTookOver, r.Disposition);
        Assert.AreEqual(AbortReason.GameForeground, r.Abort);
        Assert.IsFalse(rig.Acting.Any(c => c.IsFocusOff), "no fake unfocus while the game is really foreground");
        Assert.IsFalse(rig.Acting.Any(IsDown));
        Assert.AreEqual("swp(-8,-8,0x4215)", rig.Acting.Last(c => c.Kind == "swp").ToString());
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    [TestMethod]
    public void IconicBeforeTheSlideMeansNoSetWindowPosAndNoMessages()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.OnCall = c =>
        {
            if (c.Kind == "send" && c.Msg == HardRules.WM_SETFOCUS)
            {
                rig.GameWindow.Iconic = true;
            }
        };

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual(InputDisposition.UserTookOver, r.Disposition);
        Assert.AreEqual(AbortReason.GameIconic, r.Abort);
        Assert.IsFalse(rig.Acting.Any(c => c.Kind == "swp"));
        Assert.IsFalse(rig.Acting.Any(c => c.IsFocusOff));
        Assert.IsFalse(rig.Acting.Any(c => c.Kind == "post"));
    }

    [TestMethod]
    public void IconicWhileSlidLeavesTheWindowAloneUntilRestored()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.OnCall = c =>
        {
            if (IsSwp(c, -838, 327))
            {
                rig.GameWindow.Iconic = true;
            }
        };

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual(InputDisposition.UserTookOver, r.Disposition);
        Assert.AreEqual(1, rig.Acting.Count(c => c.Kind == "swp"), "only the slide itself; no SetWindowPos while iconic");
        Assert.IsFalse(rig.Acting.Any(c => c.IsFocusOff));
        Assert.IsTrue(File.Exists(rig.Paths.SlideMarkerPath), "marker kept: the window is still displaced");

        // Still minimized: no rung runs.
        var still = rig.Io.RestoreHomeIfOurs();
        Assert.IsFalse(still.Attempted);

        // The user restores it at the slid rect: ours (marker) -> move-only home.
        rig.GameWindow.Iconic = false;
        var restored = rig.Io.RestoreHomeIfOurs();
        Assert.IsTrue(restored.Success);
        Assert.AreEqual(RestoreRung.MoveOnly, restored.Rung);
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
        Assert.IsFalse(File.Exists(rig.Paths.SlideMarkerPath));
    }

    [TestMethod]
    public void StopWhileSlidBeforeDownSlidesBackAsyncThenUnfocuses()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.OnCall = c =>
        {
            if (IsSwp(c, -838, 327))
            {
                rig.Stop.RequestStop(StopSource.Hotkey, "hotkey");
            }
        };

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual(InputDisposition.Stopped, r.Disposition);
        var tail = After(rig.Acting, c => IsSwp(c, -838, 327)).Skip(1).Select(c => c.ToString()).ToArray();
        CollectionAssert.AreEqual(new[] { "swp(-8,-8,0x4215)", "send(0x8,0,0)", "send(0x6,0,0)", "send(0x86,0,0)", "send(0x1C,0,0)" }, tail, string.Join(" ", tail));
        Assert.IsFalse(rig.Acting.Any(IsDown));

        var again = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);
        Assert.AreEqual(InputDisposition.Stopped, again.Disposition);
        Assert.AreEqual(0, again.FocusMessagesSent);
    }

    [TestMethod]
    public void SlidTemplateMismatchAbortsBeforeDown()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.Vision.VerifyResult = new MatchResult("hud.bag", false, new PointI(1700, 300), 0.6, 0.5, 48, MatchFailure.BelowThreshold);

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual(InputDisposition.AbortedAfterFocus, r.Disposition);
        Assert.AreEqual(AbortReason.SlidTemplateMismatch, r.Abort);
        Assert.IsFalse(rig.Acting.Any(c => c.Kind == "post"));
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
        Assert.AreEqual(4, rig.Acting.Count(c => c.IsFocusOff));
    }

    [TestMethod]
    public void ProvisionalTemplateIsRefused()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.Vision.VerifyResult = new MatchResult("menu.ability", false, default, 0, 0, 0, MatchFailure.NotClickable);

        var r = rig.Io.SlideClick(rig.BagClick() with { TemplateId = "menu.ability" }, CancellationToken.None);

        Assert.AreEqual(InputDisposition.Refused, r.Disposition);
        Assert.IsFalse(rig.Acting.Any(IsDown));
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    [TestMethod]
    public void PopupLoggedSinceTheLocateAbortsBeforeDown()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        var req = rig.BagClick();
        rig.Log.Show("UI_CHANT"); // overlay: ignored
        rig.Log.Append((p, s, t) => new Contracts.Logs.UiLogEvent(p, s, t, Contracts.Logs.UiVerb.PopupRealShow, "UI_ITEM_REWARD", null));

        var r = rig.Io.SlideClick(req, CancellationToken.None);

        Assert.AreEqual(AbortReason.LogGatePopup, r.Abort);
        Assert.AreEqual(InputDisposition.AbortedAfterFocus, r.Disposition);
        Assert.IsFalse(rig.Acting.Any(IsDown));
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    [TestMethod]
    public void OverlayShowsDoNotTripThePopupGate()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        var req = rig.BagClick();
        rig.Log.Show("UI_CHANT");
        rig.Log.Show("UI_MONEY");

        var r = rig.Io.SlideClick(req, CancellationToken.None);

        Assert.AreEqual(InputDisposition.Sent, r.Disposition);
    }

    [TestMethod]
    public void GameResizingItselfWhileSlidIsAnIncident()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.OnCall = c =>
        {
            if (IsSwp(c, -838, 327))
            {
                rig.GameWindow.Rect = new RectI(-1928, -8, 1396, 298); // c10: the game applies its internal resolution
            }
        };

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual(InputDisposition.Incident, r.Disposition);
        Assert.IsNotNull(r.Incident);
        Assert.IsFalse(rig.Acting.Any(IsDown));
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect, "restored by the ladder (move+size rung)");
        Assert.IsTrue(rig.Acting.Any(c => c.Kind == "swp" && c.Flags is HardRules.SwpEmergencyMoveSize or HardRules.SwpEmergencyMoveSizeAsync));
    }

    [TestMethod]
    public void ClipCursorChangeWhileFocusedIsAHaltIncident()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.OnCall = c =>
        {
            if (c.Kind == "send" && c.Msg == HardRules.WM_ACTIVATE && c.WParam == 1)
            {
                rig.Win32.Clip = new RectI(-830, 358, 1090, 1367);
            }
        };

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual(InputDisposition.Incident, r.Disposition);
        Assert.AreEqual(IncidentKind.CursorClip, r.Incident!.Kind);
        Assert.IsTrue(r.Cursor.ClipChanged);
        Assert.IsFalse(rig.Acting.Any(c => c.Kind == "swp"));
    }

    [TestMethod]
    public void NotReadyMeansZeroMessages()
    {
        using var rig = new Rig();
        Assert.IsTrue(rig.Io.Connect(CancellationToken.None).Ok);
        rig.Win32.ClearCalls();

        // Cursor not still yet (just connected).
        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);
        Assert.AreEqual(InputDisposition.NotSent, r.Disposition);
        Assert.AreEqual(0, r.FocusMessagesSent);
        Assert.AreEqual(0, rig.Win32.Calls.Count);

        rig.Settle(TimeSpan.FromSeconds(3));
        var old = rig.BagClick() with { LocatedAtMonotonic = rig.Clock.Monotonic - TimeSpan.FromSeconds(2) };
        Assert.AreEqual(AbortReason.LocateTooOld, rig.Io.SlideClick(old, CancellationToken.None).Abort);

        rig.Win32.Idle = TimeSpan.FromMilliseconds(500);
        // TEMPORARY while SystemIdleForSlide is 0 for live testing (2026-10-14): restore the plain assertion with it.
        if (HardRules.SystemIdleForSlide > TimeSpan.Zero)
        {
            Assert.AreEqual(NotReadyReason.SystemNotIdle, rig.Io.CheckReadiness(new ReadinessQuery(InputKind.Click, Rig.BagTarget, false)).Reason);
        }
        Assert.AreEqual(0, rig.Win32.Calls.Count);
    }

    [TestMethod]
    public void SecondSlideWithinASecondIsRateLimited()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        Assert.AreEqual(InputDisposition.Sent, rig.Io.SlideClick(rig.BagClick(), CancellationToken.None).Disposition);
        rig.Win32.ClearCalls();

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual(InputDisposition.RateLimited, r.Disposition);
        Assert.AreEqual(0, rig.Win32.Calls.Count, "not even the WM_NULL probe");
        Assert.IsTrue(rig.Io.Pacing.NextAllowedIn > TimeSpan.Zero);
    }

    [TestMethod]
    public void UnknownGameRefusesAndGoneGameIsReported()
    {
        using var rig = new Rig();
        Assert.AreEqual(InputDisposition.NotSent, rig.Io.SlideClick(rig.BagClick(), CancellationToken.None).Disposition);
        rig.ConnectAndSettle();
        rig.Win32.Processes.Remove(Rig.GamePid);
        rig.Win32.Windows.Remove(Rig.Game);
        rig.Win32.ZOrder.Remove(Rig.Game);
        Assert.AreEqual(InputDisposition.GameGone, rig.Io.SlideClick(rig.BagClick(), CancellationToken.None).Disposition);
    }

    [TestMethod]
    public void JournalNeverHoldsFreeText()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);
        var code = new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9_.:,()\[\]/+\- ]{0,120}$");
        foreach (var rec in rig.Journal.Records)
        {
            foreach (var (k, v) in rec.Fields)
            {
                Assert.IsTrue(v is null or bool or int or long or double || (v is string s && code.IsMatch(s)), $"{k}={v}");
            }
        }
    }
}

/// <summary>Watchdog trigger table (SPEC 7.4) on a manual watchdog over the fake window table.</summary>
[TestClass]
public sealed class WatchdogTriggerTests
{
    private static (Rig Rig, Input.InputSession S, Input.SlideWatchdog Wd, List<Input.Trigger> Fired) Make(FireOn fire = FireOn.Down)
    {
        var rig = new Rig(gameReacts: false);
        var s = new Input.InputSession(
            Rig.Game, Rig.GamePid, Rig.RestingCursor, Rig.Cover, new RectI(0, 0, 1920, 1032), Rig.HomeRect, true, Rig.SlidRect,
            rig.Win32.Clip, true, fire, Rig.BagTarget, rig.Clock.Monotonic);
        var fired = new List<Input.Trigger>();
        var wd = new Input.SlideWatchdog(rig.Win32, rig.Clock, rig.Stop, s, fired.Add, manual: true);
        return (rig, s, wd, fired);
    }

    [TestMethod]
    public void QuietSceneTriggersNothing()
    {
        var (rig, _, wd, fired) = Make();
        using (rig)
        {
            for (var i = 0; i < 5; i++)
            {
                wd.SampleOnce();
                rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
            }

            Assert.AreEqual(0, fired.Count);
            Assert.AreEqual(5, wd.Stop().Samples);
        }
    }

    [TestMethod]
    public void EachSignalFiresItsTrigger()
    {
        void Case(Action<Rig, Input.InputSession> arrange, Input.Trigger expected)
        {
            var (rig, s, wd, fired) = Make();
            using (rig)
            {
                arrange(rig, s);
                wd.SampleOnce();
                CollectionAssert.AreEqual(new[] { expected }, fired.ToArray(), expected.ToString());
            }
        }

        Case((r, _) => r.Win32.Cursor = new PointI(919, 672), Input.Trigger.CursorMoved);
        Case((r, _) => r.Win32.ButtonsDown.Add(0x01), Input.Trigger.UserButton);
        Case((r, _) => r.Win32.CursorOk = false, Input.Trigger.CursorLost);
        Case((r, _) => r.Stop.RequestStop(StopSource.Tray, "tray"), Input.Trigger.Stop);
        Case((r, _) => r.Win32.Foreground = Rig.Game, Input.Trigger.Foreground);
        Case((r, _) => r.GameWindow.Iconic = true, Input.Trigger.Iconic);
        Case((r, _) => r.GameWindow.Rect = new RectI(-8, -8, 1000, 600), Input.Trigger.WindowChanged);
        Case((r, _) => r.GameWindow.Zoomed = false, Input.Trigger.WindowChanged);
        Case((r, _) => r.Win32.Clip = new RectI(0, 0, 100, 100), Input.Trigger.ClipChanged);
        Case((r, _) => r.Win32.Windows[Rig.Cover].Frame = new RectI(0, 0, 1900, 1032), Input.Trigger.CoverChanged);
        Case((r, _) =>
        {
            r.Win32.Add(new FakeWindow { Hwnd = 0x6000, Pid = 555, Class = "Popup", Rect = new RectI(800, 600, 1000, 800) });
            r.Win32.ZOrder.Insert(0, 0x6000);
        }, Input.Trigger.CoverChanged);
        Case((r, _) =>
        {
            r.Win32.ZOrder.Remove(Rig.Cover);
            r.Win32.ZOrder.Remove(Rig.CoverD1);
            r.GameWindow.Frame = new RectI(0, 0, 1920, 1032);
        }, Input.Trigger.GameUnderCursor);
        Case((r, s) =>
        {
            s.MarkSlideStart(r.Clock.Monotonic);
            s.Expect = Input.WindowExpect.HomeOrSlid;
            r.Clock.Advance(HardRules.DeadManDeadline);
        }, Input.Trigger.DeadMan);
        Case((r, _) => r.Clock.Advance(Input.SlideWatchdog.SectionDeadline), Input.Trigger.DeadMan);

        // a pointer hidden by another app (a video player, typing) is evidence only, never an abort (live 2026-10-15)
        var (hidRig, hidSession, hidWd, hidFired) = Make();
        using (hidRig)
        {
            hidRig.Win32.CursorShowing = false;
            hidWd.SampleOnce();
            Assert.IsEmpty(hidFired, "no trigger for a hidden pointer");
            StringAssert.StartsWith(hidSession.ClipDetail, "part=showing");
            Assert.IsFalse(hidSession.ClipChangedSeen);
        }

        // clip and visibility changing together is a clip change, journaled as both
        var (bothRig, bothSession, bothWd, bothFired) = Make();
        using (bothRig)
        {
            bothRig.Win32.CursorShowing = false;
            bothRig.Win32.Clip = new RectI(0, 0, 100, 100);
            bothWd.SampleOnce();
            CollectionAssert.AreEqual(new[] { Input.Trigger.ClipChanged }, bothFired.ToArray());
            StringAssert.StartsWith(bothSession.ClipDetail, "part=both");
        }
    }

    [TestMethod]
    public void MovementAndButtonsAfterDownAreRecordedOnly()
    {
        var (rig, s, wd, fired) = Make();
        using (rig)
        {
            Assert.IsTrue(s.TryClaimDown());
            s.MarkDownDone(rig.Clock.Monotonic, rig.Clock.UtcNow);
            rig.Win32.Cursor = new PointI(925, 672);
            wd.SampleOnce();
            rig.Clock.Advance(TimeSpan.FromMilliseconds(300));
            rig.Win32.Cursor = new PointI(940, 690);
            rig.Win32.ButtonsDown.Add(0x02);
            wd.SampleOnce();
            Assert.AreEqual(0, fired.Count);
            Assert.IsTrue(s.MovedInP4);
            Assert.IsTrue(s.MovedAfterP4);
            Assert.IsTrue(s.UserButtonSeen);
            Assert.IsFalse(s.MovedBeforeDown);
        }
    }

    [TestMethod]
    public void UpFiringTargetMovementBeforeUpForcesTheAbort()
    {
        var (rig, s, wd, fired) = Make(FireOn.Up);
        using (rig)
        {
            Assert.IsTrue(s.TryClaimDown());
            s.MarkDownDone(rig.Clock.Monotonic, rig.Clock.UtcNow);
            rig.Win32.Cursor = new PointI(925, 672);
            wd.SampleOnce();
            CollectionAssert.AreEqual(new[] { Input.Trigger.MovedBeforeUp }, fired.ToArray());
        }
    }

    [TestMethod]
    public void WindowExpectationFollowsOurOwnMoves()
    {
        var (rig, s, wd, fired) = Make();
        using (rig)
        {
            s.Expect = Input.WindowExpect.HomeOrSlid;
            wd.SampleOnce();
            rig.GameWindow.Rect = Rig.SlidRect;
            wd.SampleOnce();
            s.Expect = Input.WindowExpect.Slid;
            wd.SampleOnce();
            Assert.AreEqual(0, fired.Count);
            rig.GameWindow.Rect = Rig.HomeRect;
            wd.SampleOnce();
            CollectionAssert.AreEqual(new[] { Input.Trigger.WindowChanged }, fired.ToArray(), "home while expecting slid = not by us");
        }
    }
}
