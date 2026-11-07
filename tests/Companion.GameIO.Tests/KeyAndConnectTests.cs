using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.GameIO.Geometry;
using Companion.GameIO.Native;
using Companion.GameIO.Tests.Fakes;

namespace Companion.GameIO.Tests;

/// <summary>Key batch (SPEC 7.2): proven lib.ps1 Send-GameKey order and timings, KEYUP always after KEYDOWN.</summary>
[TestClass]
public sealed class KeyBatchTests
{
    private static KeyRequest B() => new("open", GameKey.B, UiExpectation.Opened("UI_BAG_MAIN"));

    [TestMethod]
    public void KeyBFollowsTheProvenSequence()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.Win32.ClearCalls();

        var r = rig.Io.PressKey(B(), CancellationToken.None);

        Assert.AreEqual(InputDisposition.Sent, r.Disposition);
        Assert.IsTrue(r.FocusLogged);
        Assert.IsTrue(r.ExpectedSeen);
        Assert.IsFalse(r.UnfocusSkipped);
        var acting = rig.Acting;
        CollectionAssert.AreEqual(
            new[]
            {
                "send(0x1C,1,0)", "send(0x86,1,0)", "send(0x6,1,0)", "send(0x7,0,0)",
                "post(0x100,66,3145729)", "post(0x101,66,3224371201)",
                "send(0x8,0,0)", "send(0x6,0,0)", "send(0x86,0,0)", "send(0x1C,0,0)",
            },
            acting.Select(c => c.ToString()).ToArray(),
            string.Join(" ", acting));
        Assert.IsTrue(acting[4].At - acting[0].At >= HardRules.FocusSettle);
        Assert.IsTrue(acting[5].At - acting[4].At >= HardRules.KeyHold);
        Assert.IsFalse(acting.Any(c => c.Kind == "swp"), "a key never moves the window");
    }

    [TestMethod]
    public void AStaleUnfocusWhileTheGameIsInFront_IsResynced_OnlyAfterASecond_AndNeverWhenItIsBehind()
    {
        // live 2026-10-18: the user's UI clicks were ignored (keyboard still worked) until a minimise/restore: the game
        // was in front while Unity's last focus line said LostFocus
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.Log.StateValue = rig.Log.StateValue with { Focus = FocusState.Lost };
        rig.Win32.ClearCalls();

        Assert.IsFalse(rig.Io.ResyncFocus(), "behind another window: a lost focus is the truth");
        rig.Win32.Foreground = Rig.Game;
        Assert.IsFalse(rig.Io.ResyncFocus(), "first sight of the mismatch: a real switch logs RecoverFocus in ms");
        rig.Clock.Advance(TimeSpan.FromMilliseconds(1200));
        Assert.IsTrue(rig.Io.ResyncFocus());
        CollectionAssert.AreEqual(
            new[] { "send(0x1C,1,0)", "send(0x86,1,0)", "send(0x6,1,0)", "send(0x7,0,0)" },
            rig.Acting.Select(c => c.ToString()).ToArray(), "the focus-on sequence only: no unfocus, no input, no move");

        rig.Win32.ClearCalls();
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.IsFalse(rig.Io.ResyncFocus(), "at most every 5 s");
        rig.Log.StateValue = rig.Log.StateValue with { Focus = FocusState.Recovered };
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.IsFalse(rig.Io.ResyncFocus(), "focused: nothing to fix");
        Assert.HasCount(0, rig.Acting);
    }

    [TestMethod]
    public void AChangedKeyboardLayout_RefusesALetterKey_ButNeverEscape()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.Win32.KeyboardLayout = 0x04010401; // the user switched the input language after the connect

        var b = rig.Io.PressKey(B(), CancellationToken.None);
        Assert.AreEqual((InputDisposition.NotSent, AbortReason.KeyboardLayoutChanged), (b.Disposition, b.Abort));

        rig.Settle(TimeSpan.FromSeconds(3));
        var esc = rig.Io.PressKey(new KeyRequest("close", GameKey.Escape, []), CancellationToken.None);
        Assert.AreNotEqual(AbortReason.KeyboardLayoutChanged, esc.Abort, "Esc is the same key in every layout");
        Assert.AreEqual(InputDisposition.Sent, esc.Disposition);
    }

    [TestMethod]
    public void KeyUpAlwaysFollowsKeyDown()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.OnCall = c =>
        {
            if (c.Kind == "post" && c.Msg == HardRules.WM_KEYDOWN)
            {
                rig.Clock.ThrowOnNextSleep = new InvalidOperationException("boom during the 70 ms hold");
            }
        };

        var r = rig.Io.PressKey(B(), CancellationToken.None);

        var posts = rig.Acting.Where(c => c.Kind == "post").ToList();
        Assert.HasCount(2, posts);
        Assert.AreEqual(HardRules.WM_KEYDOWN, posts[0].Msg);
        Assert.AreEqual(HardRules.WM_KEYUP, posts[1].Msg);
        Assert.AreEqual(4, rig.Acting.Count(c => c.IsFocusOff));
        Assert.AreEqual(InputDisposition.Sent, r.Disposition);
    }

    [TestMethod]
    public void NoRecoverFocusMeansFocusFailedAndUnfocus()
    {
        using var rig = new Rig(gameReacts: false);
        rig.ConnectAndSettle();
        rig.Win32.ClearCalls();

        var r = rig.Io.PressKey(B(), CancellationToken.None);

        Assert.AreEqual(InputDisposition.FocusFailed, r.Disposition);
        Assert.IsFalse(r.FocusLogged);
        Assert.IsFalse(rig.Acting.Any(c => c.Kind == "post"), "no key without a logged RecoverFocus");
        Assert.AreEqual(4, rig.Acting.Count(c => c.IsFocusOff));
    }

    [TestMethod]
    public void UserTakingOverSkipsTheUnfocus()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.OnCall = c =>
        {
            if (c.Kind == "send" && c.Msg == HardRules.WM_SETFOCUS)
            {
                rig.Win32.Foreground = Rig.Game;
            }
        };

        var r = rig.Io.PressKey(B(), CancellationToken.None);

        Assert.AreEqual(InputDisposition.UserTookOver, r.Disposition);
        Assert.IsFalse(rig.Acting.Any(c => c.IsFocusOff));
        Assert.IsFalse(rig.Acting.Any(c => c.Kind == "post" && c.Msg != HardRules.WM_NULL));
    }

    [TestMethod]
    public void ClipCursorChangeDuringAKeyIsAnIncident()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.OnCall = c =>
        {
            if (c.Kind == "send" && c.Msg == HardRules.WM_SETFOCUS)
            {
                rig.Win32.Clip = new RectI(0, 23, 1920, 1032);
            }
        };

        var r = rig.Io.PressKey(B(), CancellationToken.None);

        Assert.AreEqual(InputDisposition.Incident, r.Disposition);
        Assert.AreEqual(AbortReason.CursorClipChanged, r.Abort);
        Assert.IsFalse(rig.Acting.Any(c => c.Kind == "post"), "no key once the cursor was clipped");
        Assert.AreEqual(4, rig.Acting.Count(c => c.IsFocusOff));
    }

    [TestMethod]
    public void NonWhitelistedKeyIsRefusedWithZeroMessages()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.Win32.ClearCalls();

        var r = rig.Io.PressKey(new KeyRequest("x", (GameKey)7, []), CancellationToken.None);

        Assert.AreEqual(InputDisposition.Refused, r.Disposition);
        Assert.AreEqual(0, r.FocusMessagesSent);
        Assert.AreEqual(0, rig.Win32.Calls.Count);
    }

    [TestMethod]
    public void GameForegroundOrStopMeansNotSent()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.Win32.ClearCalls();
        rig.Win32.Foreground = Rig.Game;
        Assert.AreEqual(InputDisposition.NotSent, rig.Io.PressKey(B(), CancellationToken.None).Disposition);
        Assert.AreEqual(NotReadyReason.GameForeground, rig.Io.CheckReadiness(new ReadinessQuery(InputKind.Key, null, false)).Reason);

        rig.Win32.Foreground = Rig.Cover;
        rig.Io.SamplerTick();
        Assert.AreEqual(NotReadyReason.Cooldown, rig.Io.CheckReadiness(new ReadinessQuery(InputKind.Key, null, false)).Reason, "cooldown after the game was foreground");
        rig.Settle(HardRules.CooldownAfterGameForeground);
        Assert.AreEqual(NotReadyReason.None, rig.Io.CheckReadiness(new ReadinessQuery(InputKind.Key, null, false)).Reason);

        rig.Stop.RequestStop(StopSource.Pipe, "pipe");
        Assert.AreEqual(InputDisposition.Stopped, rig.Io.PressKey(B(), CancellationToken.None).Disposition);
        Assert.AreEqual(0, rig.Acting.Count);
    }

    [TestMethod]
    public void ForeignRecoverFocusStartsTheCooldown()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.Log.Focus(recovered: true); // not caused by us
        Assert.AreEqual(NotReadyReason.Cooldown, rig.Io.CheckReadiness(new ReadinessQuery(InputKind.Key, null, false)).Reason);
        rig.Settle(HardRules.CooldownAfterForeignFocus);
        Assert.AreEqual(NotReadyReason.None, rig.Io.CheckReadiness(new ReadinessQuery(InputKind.Key, null, false)).Reason);
    }

    [TestMethod]
    public void OurOwnRecoverFocusDoesNotStartTheCooldown()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        Assert.AreEqual(InputDisposition.Sent, rig.Io.PressKey(B(), CancellationToken.None).Disposition);
        Assert.AreEqual(NotReadyReason.None, rig.Io.CheckReadiness(new ReadinessQuery(InputKind.Key, null, false)).Reason);
    }
}

/// <summary>Connect, readiness, prefs and crash recovery on the synthetic scene.</summary>
[TestClass]
public sealed class ConnectAndRecoveryTests
{
    [TestMethod]
    public void ConnectFindsTheGameAndReadsTheBaseline()
    {
        using var rig = new Rig();
        var r = rig.Io.Connect(CancellationToken.None);
        Assert.IsTrue(r.Ok);
        Assert.AreEqual(Rig.Game, r.Game!.Hwnd);
        Assert.AreEqual(Rig.GamePid, r.Game.Pid);
        Assert.AreEqual(3, r.Prefs!.FullscreenMode);
        Assert.IsNotNull(r.Build);
        Assert.IsFalse(rig.Win32.Calls.Any(c => c.Msg != HardRules.WM_NULL), "Connect sends no input");
    }

    [TestMethod]
    public void TheGameWindowIsPresentForTheStatus_WithoutBindingOrSendingAnything()
    {
        // incident 39 (2026-11-04): after the game was closed and opened again, the window said "THE GAME IS NOT RUNNING"
        // until the next job bound the new window; the status now looks for the window, read-only
        using var rig = new Rig();
        var calls = rig.Win32.Calls.Count();
        Assert.IsTrue(rig.Io.GameWindowPresent(), "not bound yet, but the window is there");
        Assert.IsNull(rig.Io.Identity, "nothing is bound");
        Assert.AreEqual(calls, rig.Win32.Calls.Count(), "nothing is sent");

        Assert.IsTrue(rig.Io.Connect(CancellationToken.None).Ok);
        Assert.IsTrue(rig.Io.GameWindowPresent());

        // the game closes
        rig.Win32.Windows.Remove(Rig.Game);
        rig.Win32.ZOrder.Remove(Rig.Game);
        rig.Win32.Processes.Remove(Rig.GamePid);
        Assert.IsFalse(rig.Io.GameWindowPresent());

        // and opens again: a window and a process the held identity does not know
        rig.Win32.Processes[5000] = new FakeProcess("TOS", Rig.GameExe, Rig.GameStart.AddHours(1));
        rig.Win32.Add(new FakeWindow { Hwnd = 0x9000, Pid = 5000, Class = HardRules.GameWindowClass, Rect = Rig.HomeRect });
        rig.Win32.ZOrder.Add(0x9000);
        Assert.IsTrue(rig.Io.GameWindowPresent());
        Assert.AreEqual(Rig.Game, rig.Io.Identity!.Hwnd, "still the old identity: a job binds the new window at its own start");
    }

    [TestMethod]
    public void ConnectProblems()
    {
        using (var rig = new Rig())
        {
            rig.Win32.Elevated = false;
            Assert.AreEqual(ConnectProblem.NotElevated, rig.Io.Connect(CancellationToken.None).Problem);
        }

        using (var rig = new Rig())
        {
            rig.Win32.Processes.Remove(Rig.GamePid);
            Assert.AreEqual(ConnectProblem.NotRunning, rig.Io.Connect(CancellationToken.None).Problem);
        }

        using (var rig = new Rig())
        {
            rig.Win32.Processes[Rig.GamePid] = new FakeProcess("TOS", @"C:\Elsewhere\TOS.exe", Rig.GameStart);
            Assert.AreEqual(ConnectProblem.ExePathMismatch, rig.Io.Connect(CancellationToken.None).Problem);
        }

        using (var rig = new Rig())
        {
            rig.GameWindow.Visible = false;
            Assert.AreEqual(ConnectProblem.WindowNotFound, rig.Io.Connect(CancellationToken.None).Problem);
        }

        using (var rig = new Rig())
        {
            rig.Win32.Processes[5000] = new FakeProcess("TOS", Rig.GameExe, Rig.GameStart);
            rig.Win32.Add(new FakeWindow { Hwnd = 0x9000, Pid = 5000, Class = HardRules.GameWindowClass, Rect = new RectI(0, 0, 800, 600) });
            rig.Win32.ZOrder.Add(0x9000);
            Assert.AreEqual(ConnectProblem.Ambiguous, rig.Io.Connect(CancellationToken.None).Problem);
        }

        using (var rig = new Rig())
        {
            rig.Win32.PostBehavior = m => m == HardRules.WM_NULL ? (false, Win32Const.ERROR_ACCESS_DENIED) : (true, 0);
            Assert.AreEqual(ConnectProblem.IntegrityMismatch, rig.Io.Connect(CancellationToken.None).Problem);
        }
    }

    [TestMethod]
    public void ReadinessForAClickPassesOnTheUsersLayout()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        var r = rig.Io.CheckReadiness(new ReadinessQuery(InputKind.Click, Rig.BagTarget, false));
        Assert.IsTrue(r.Ready, r.Reason.ToString());
        Assert.AreEqual(CoverRejectReason.None, r.Cover!.Reject);
        Assert.AreEqual("Chrome_WidgetWin_1", r.Cover.ClassName);
        Assert.AreEqual(Rig.SlidRect, r.Plan!.SlidRect);
        Assert.AreEqual(0L, r.Plan.ExposedAreaPx);
        Assert.AreEqual(0, rig.Win32.Calls.Count(c => c.Kind != "post" || c.Msg != HardRules.WM_NULL), "readiness is passive");
    }

    [TestMethod]
    public void ReadinessReasonsOnTheScene()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        ReadinessReport Click() => rig.Io.CheckReadiness(new ReadinessQuery(InputKind.Click, Rig.BagTarget, false));

        rig.Win32.Cursor = new PointI(900, 1050); // over the taskbar
        rig.Settle(TimeSpan.FromSeconds(3));
        var r = Click();
        Assert.AreEqual(NotReadyReason.CoverRejected, r.Reason);
        Assert.AreEqual(CoverRejectReason.ShellOrTransient, r.Cover!.Reject);

        rig.Win32.Cursor = Rig.RestingCursor;
        rig.Settle(TimeSpan.FromSeconds(3));
        rig.Win32.ZOrder.Remove(Rig.CoverD1); // DISPLAY1 shows the slid game: allowed, the cursor area stays covered
        Assert.IsTrue(Click().Ready, Click().Reason.ToString());
        rig.Win32.ZOrder.Insert(3, Rig.CoverD1);
        Assert.IsTrue(Click().Ready, Click().Reason.ToString());

        rig.Win32.Cursor = new PointI(920, 672); // moved: needs 0.8 s of stillness again (1.5 s near a deny zone)
        Assert.AreEqual(NotReadyReason.CursorMoving, Click().Reason);
        rig.Settle(TimeSpan.FromSeconds(1));
        Assert.IsTrue(Click().Ready);
        Assert.AreEqual(NotReadyReason.CursorMoving, rig.Io.CheckReadiness(new ReadinessQuery(InputKind.Click, Rig.BagTarget, NearDenyZone: true)).Reason);

        rig.Win32.ButtonsDown.Add(0x01);
        Assert.AreEqual(NotReadyReason.UserButtonDown, Click().Reason);
        rig.Win32.ButtonsDown.Clear();

        rig.GameWindow.Zoomed = false;
        Assert.AreEqual(NotReadyReason.DisplayModeWrong, Click().Reason);
        rig.GameWindow.Zoomed = true;
        Assert.AreEqual(NotReadyReason.WindowNotAtHome, Click().Reason, "home must be stable for 1 s again");
        rig.Settle(TimeSpan.FromSeconds(2));

        rig.GameWindow.Iconic = true;
        Assert.AreEqual(NotReadyReason.GameIconic, Click().Reason);
        rig.GameWindow.Iconic = false;

        rig.Win32.Monitors[1] = rig.Win32.Monitors[1] with { Dpi = 120 };
        Assert.AreEqual(NotReadyReason.DpiUnsupported, Click().Reason);
        rig.Win32.Monitors[1] = rig.Win32.Monitors[1] with { Dpi = 96 };

        rig.Log.StateValue = rig.Log.StateValue with { LastJank = rig.Clock.UtcNow.LocalDateTime.AddMilliseconds(-500) };
        Assert.AreEqual(NotReadyReason.RecentJank, Click().Reason);
        rig.Log.StateValue = rig.Log.StateValue with { LastJank = null, LastResolution = new SizeI(2400, 1260) };
        Assert.AreEqual(NotReadyReason.WindowNotAtHome, Click().Reason);
        rig.Log.StateValue = rig.Log.StateValue with { LastResolution = new SizeI(1920, 1009) };

        rig.Win32.Add(new FakeWindow { Hwnd = 0x7000, Pid = Rig.GamePid, Class = "#32770", Rect = new RectI(500, 300, 900, 600) });
        rig.Win32.ZOrder.Insert(0, 0x7000);
        Assert.AreEqual(NotReadyReason.ExtraGameWindow, Click().Reason);
        rig.Win32.ZOrder.Remove(0x7000);

        rig.Win32.InputDesktop = false;
        Assert.AreEqual(NotReadyReason.SessionNotInteractive, Click().Reason);
        rig.Win32.InputDesktop = true;

        Assert.IsTrue(Click().Ready, Click().Reason.ToString());
    }

    [TestMethod]
    public void CrashRecoveryRestoresALeftoverSlideMoveOnly()
    {
        using var rig = new Rig();
        var store = new SlideMarkerStore(rig.Paths.RuntimeDir, rig.Paths.RunDir, rig.Paths.SlideMarkerPath);
        Assert.IsTrue(store.TryWrite(new SlideMarkerData(Rig.GamePid, Rig.Game, Rig.GameStart, Rig.HomeRect, Rig.SlidRect, rig.Clock.UtcNow)));
        rig.GameWindow.Rect = Rig.SlidRect; // the host died while displaced

        var r = rig.Io.RecoverLeftoverDisplacement(); // no Connect needed (host start / guardian)

        Assert.IsTrue(r.Attempted && r.Success);
        Assert.AreEqual(RestoreRung.MoveOnly, r.Rung);
        Assert.AreEqual(IncidentKind.LeftoverFromCrash, r.Incident!.Kind);
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
        CollectionAssert.AreEqual(new[] { "swp(-8,-8,0x0215)" }, rig.Win32.Calls.Select(c => c.ToString()).ToArray());
        Assert.IsFalse(File.Exists(rig.Paths.SlideMarkerPath));
    }

    [TestMethod]
    public void CrashRecoveryNeverMovesAWindowThatIsNotProvablyOurs()
    {
        using var rig = new Rig();
        var store = new SlideMarkerStore(rig.Paths.RuntimeDir, rig.Paths.RunDir, rig.Paths.SlideMarkerPath);

        // Game restarted (other start time): delete the marker only.
        Assert.IsTrue(store.TryWrite(new SlideMarkerData(Rig.GamePid, Rig.Game, Rig.GameStart.AddHours(-3), Rig.HomeRect, Rig.SlidRect, rig.Clock.UtcNow)));
        rig.GameWindow.Rect = Rig.SlidRect;
        var r1 = rig.Io.RecoverLeftoverDisplacement();
        Assert.IsFalse(r1.Attempted);
        Assert.IsFalse(File.Exists(rig.Paths.SlideMarkerPath));

        // Rect matches neither home nor slid: notify, leave the window alone, keep the marker.
        Assert.IsTrue(store.TryWrite(new SlideMarkerData(Rig.GamePid, Rig.Game, Rig.GameStart, Rig.HomeRect, Rig.SlidRect, rig.Clock.UtcNow)));
        rig.GameWindow.Rect = new RectI(100, 100, 900, 700);
        var r2 = rig.Io.RecoverLeftoverDisplacement();
        Assert.IsFalse(r2.Attempted);
        Assert.AreEqual(IncidentKind.MovedByOthers, r2.Incident!.Kind);
        Assert.IsTrue(File.Exists(rig.Paths.SlideMarkerPath));

        // Already home: delete only.
        rig.GameWindow.Rect = Rig.HomeRect;
        var r3 = rig.Io.RecoverLeftoverDisplacement();
        Assert.IsTrue(r3.Success && !r3.Attempted);
        Assert.IsFalse(File.Exists(rig.Paths.SlideMarkerPath));
        Assert.AreEqual(0, rig.Win32.Calls.Count(c => c.Kind == "swp"));
    }

    [TestMethod]
    public void DeviationsByOthersAreNeverFought()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.GameWindow.Rect = new RectI(100, 100, 2036, 1148); // the user moved it
        rig.Win32.ClearCalls();
        var check = rig.Io.CheckHome();
        Assert.IsFalse(check.AtHome);
        Assert.AreEqual(DeviationOwner.Others, check.Owner);
        var liveMaximized = check.ExpectedMaximized;
        Assert.AreEqual(Rig.HomeRect, liveMaximized);
        var r = rig.Io.RestoreHomeIfOurs();
        Assert.IsFalse(r.Attempted);
        Assert.AreEqual(IncidentKind.MovedByOthers, r.Incident!.Kind);
        Assert.AreEqual(0, rig.Win32.Calls.Count);
    }

    [TestMethod]
    public void StateAndCapture()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        var st = rig.Io.GetState();
        Assert.IsTrue(st.Exists);
        Assert.IsTrue(st.Maximized && st.HasCaption && !st.Foreground && !st.Iconic);
        Assert.AreEqual(new SizeI(1920, 1009), st.ClientSize);
        Assert.AreEqual(new PointI(0, 23), st.ClientOrigin);
        Assert.AreEqual(@"\\.\DISPLAY2", st.Monitor!.DeviceName);
        Assert.AreEqual(0, st.ExtraVisibleGameWindows);

        var cap = rig.Io.Capture(CancellationToken.None);
        Assert.IsTrue(cap.Ok, cap.Problem.ToString());
        Assert.IsFalse(cap.Frame!.WhileSlid);
        Assert.AreEqual(1920, cap.Frame.Width);
        rig.Win32.PrintFails = true;
        Assert.AreEqual(CaptureProblem.PrintWindowFailed, rig.Io.Capture(CancellationToken.None).Problem);
        rig.Win32.PrintFails = false;
        rig.Vision.Quality = Contracts.Vision.FrameQuality.Black;
        Assert.AreEqual(CaptureProblem.InvalidImage, rig.Io.Capture(CancellationToken.None).Problem);
        rig.GameWindow.Iconic = true;
        Assert.AreEqual(CaptureProblem.Iconic, rig.Io.Capture(CancellationToken.None).Problem);
        Assert.IsTrue(rig.Win32.Calls.All(c => c.Kind is "print" or "post" && (c.Kind == "print" || c.Msg == HardRules.WM_NULL)), "capture only prints");
    }

    [TestMethod]
    public void StopSwitchFirstRequestWinsUntilCleared()
    {
        var clock = new FakeClock();
        var s = new StopSwitch(clock);
        Assert.IsFalse(s.IsStopRequested);
        Assert.IsFalse(s.Handle.WaitOne(0));
        s.RequestStop(StopSource.Hotkey, "hotkey");
        clock.Advance(TimeSpan.FromSeconds(1));
        s.RequestStop(StopSource.Pipe, "pipe");
        Assert.IsTrue(s.IsStopRequested);
        Assert.AreEqual(StopSource.Hotkey, s.Current!.Source);
        Assert.IsTrue(s.Handle.WaitOne(0));
        s.Clear();
        Assert.IsFalse(s.IsStopRequested);
        Assert.IsNull(s.Current);
        Assert.IsFalse(s.Handle.WaitOne(0));
    }

    [TestMethod]
    public void ConstructorsHaveNoSideEffects()
    {
        var clock = new FakeClock();
        var w = new FakeWin32(clock);
        var log = new FakeLogTracker(clock);
        var paths = CompanionPaths.Default(Path.Combine(AppContext.BaseDirectory, "never-created-" + Guid.NewGuid().ToString("N")));
        _ = new GameIo(clock, new StopSwitch(clock), log, new FakeUiClassifier(), new FakeVision(), new FakeTemplates(), new FakeJournal(), paths, w, new GameIoOptions(true, false));
        Assert.AreEqual(0, w.Calls.Count);
        Assert.AreEqual(0, log.Polls);
        Assert.IsFalse(Directory.Exists(paths.InstallDir));
    }
}
