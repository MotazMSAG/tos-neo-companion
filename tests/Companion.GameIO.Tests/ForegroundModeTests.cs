using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.GameIO.Geometry;
using Companion.GameIO.Input;
using Companion.GameIO.Native;
using Companion.GameIO.Safety;
using Companion.GameIO.Tests.Fakes;

namespace Companion.GameIO.Tests;

/// <summary>
/// Foreground mode (SPEC 7.3b, the user's decision 2026-11-02, EVIDENCE c15): the game is the foreground window and the
/// cursor rests on it. No focus message ever, the window slides visibly and back, the game is drained before it goes
/// home, and the user's own input ends the section at any phase.
/// </summary>
[TestClass]
public sealed class ForegroundModeTests
{
    private static bool IsDown(Call c) => c.Kind == "post" && c.Msg == HardRules.WM_LBUTTONDOWN;

    private static bool IsUp(Call c) => c.Kind == "post" && c.Msg == HardRules.WM_LBUTTONUP;

    private static bool IsNull(Call c) => c.Kind == "send" && c.Msg == HardRules.WM_NULL;

    private static bool IsFocus(Call c) => c.IsFocusOn || c.IsFocusOff || c.Kind == "notify";

    private static bool IsHome(Call c) => c.Kind == "swp" && c.X == Rig.HomeRect.Left && c.Y == Rig.HomeRect.Top;

    private static bool IsSlide(Call c) => c.Kind == "swp" && c.X == Rig.SlidRect.Left && c.Y == Rig.SlidRect.Top;

    private static Rig FrontRig()
    {
        var rig = new Rig();
        rig.ConnectAndSettle();
        rig.GameInFront();
        rig.Win32.ClearCalls();
        return rig;
    }

    private static JournalRecord LastInput(Rig rig) => rig.Journal.Records.Last(j => j.Kind == JournalKind.Input);

    // ------------------------------------------------------------------ the click

    [TestMethod]
    public void AClickWithTheGameInFront_SendsNoFocusMessage_AndDrainsBeforeTheWindowGoesHome()
    {
        using var rig = FrontRig();

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual(InputDisposition.Sent, r.Disposition, r.Abort.ToString());
        Assert.AreEqual(InputMode.Foreground, r.Mode);
        Assert.AreEqual(0, r.FocusMessagesSent);
        Assert.IsFalse(r.FocusLogged || r.UnfocusSentWhileSlid || r.UnfocusSkipped);
        Assert.IsNull(r.PressBlock, "no unfocus, so no press block");
        Assert.IsTrue(r.DownPosted && r.UpPosted && r.ExpectedSeenWhileSlid);
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
        Assert.IsTrue(r.Slide!.BackAtHome);
        Assert.AreEqual(HardRules.GameWindowClass, r.Slide.CoverClass);

        var calls = rig.Win32.Calls.ToList();
        Assert.IsFalse(calls.Any(IsFocus), "no focus message at all: " + string.Join(" ", calls));
        var slide = calls.FindIndex(IsSlide);
        var down = calls.FindIndex(IsDown);
        var up = calls.FindIndex(IsUp);
        var home = calls.FindIndex(IsHome);
        var drains = calls.Select((c, i) => (c, i)).Where(x => x.i > up && IsNull(x.c)).Select(x => x.i).ToList();
        Assert.IsTrue(slide >= 0 && slide < down && down < up && home > up, string.Join(" ", calls));
        Assert.AreEqual(2, drains.Count, "two WM_NULL round trips after UP: " + string.Join(" ", calls));
        Assert.IsTrue(drains[1] < home, "the window moves only after the second round trip");
        Assert.IsTrue(calls[drains[1]].At - calls[drains[0]].At >= HardRules.ForegroundDrainSettle, "a frame between the round trips");

        var input = LastInput(rig);
        Assert.AreEqual("Foreground", input.Fields["mode"]);
        Assert.AreEqual(true, input.Fields["drained"]);
        Assert.AreEqual("Foreground", rig.Journal.Records.Last(j => j.Kind == JournalKind.Slide).Fields["mode"]);
    }

    [TestMethod]
    public void TheChokePointRefusesEveryFocusMessageDuringAForegroundInput()
    {
        var clock = new FakeClock();
        var w = new FakeWin32(clock);
        w.Add(new FakeWindow { Hwnd = Rig.Game, Pid = Rig.GamePid, Class = HardRules.GameWindowClass });
        w.Foreground = 0;
        var c = new ChokePoint(w, Rig.Game, Rig.GamePid, new SizeI(1920, 1009));
        using (c.EnterForegroundInput())
        {
            foreach (var m in HardRules.FocusOnSequence.Concat(HardRules.FocusOffSequence))
            {
                Assert.ThrowsExactly<HardRuleViolationException>(() => c.Send(Rig.Game, m.Message, m.WParam, m.LParam));
            }

            Assert.ThrowsExactly<HardRuleViolationException>(() => c.Notify(Rig.Game, HardRules.WM_KILLFOCUS, 0, 0));
            Assert.AreEqual(SendOutcome.Ok, c.Send(Rig.Game, HardRules.WM_NULL, 0, 0), "the drain's WM_NULL stays allowed");
        }

        Assert.AreEqual(SendOutcome.Ok, c.Send(Rig.Game, HardRules.WM_ACTIVATE, 1, 0), "cover mode again after the input");
        Assert.AreEqual(2, w.Calls.Count);
    }

    [TestMethod]
    public void TheUsersKeyBeforeDown_EndsTheSection_NothingPressed()
    {
        using var rig = FrontRig();
        var typed = false;
        rig.BeforeSample = () =>
        {
            if (!typed && rig.GameWindow.Rect == Rig.SlidRect)
            {
                typed = true;
                rig.Win32.LastInputTick += 5; // a key: the cursor did not move
            }
        };

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual((InputDisposition.AbortedAfterFocus, AbortReason.UserInput), (r.Disposition, r.Abort), "slid, so the game was touched");
        Assert.IsFalse(r.DownPosted);
        Assert.IsTrue(r.Cursor.UserInput);
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
        Assert.IsFalse(rig.Win32.Calls.Any(c => c.Kind == "post" && c.Msg != HardRules.WM_NULL), "no MOVE, DOWN or UP");
    }

    [TestMethod]
    public void TheUsersClickAfterDown_PostsUp_DrainsThen_SlidesHome()
    {
        using var rig = FrontRig();
        rig.BeforeSample = () =>
        {
            if (rig.Win32.Calls.Any(IsDown) && rig.Win32.ButtonsDown.Count == 0)
            {
                rig.Win32.ButtonsDown.Add(1); // the user's real press on the slid game
            }
        };

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual((InputDisposition.Sent, AbortReason.UserButton), (r.Disposition, r.Abort));
        Assert.IsTrue(r.Cursor.UserButton);
        var calls = rig.Win32.Calls.ToList();
        var up = calls.FindIndex(IsUp);
        var drain = calls.FindLastIndex(IsNull);
        var home = calls.FindIndex(IsHome);
        Assert.IsTrue(up >= 0 && up < drain && drain < home, "UP, drain, then home: " + string.Join(" ", calls));
        Assert.IsFalse(calls.Any(IsFocus));
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    [TestMethod]
    public void TheUserSwitchingAwayAfterDown_StillDrainsBeforeTheWindowMoves()
    {
        // safety review 2026-11-02 (H1): the game may read our DOWN before the deactivation, so even then the window waits
        using var rig = FrontRig();
        rig.BeforeSample = () =>
        {
            if (rig.Win32.Calls.Any(IsDown))
            {
                rig.Win32.Foreground = Rig.Cover;
            }
        };

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual((InputDisposition.UserTookOver, AbortReason.ForegroundLost), (r.Disposition, r.Abort));
        var calls = rig.Win32.Calls.ToList();
        var up = calls.FindIndex(IsUp);
        var home = calls.FindIndex(IsHome);
        Assert.IsTrue(up >= 0 && home > up, string.Join(" ", calls));
        Assert.AreEqual(2, calls.Skip(up).Take(home - up).Count(IsNull), "UP, two round trips, then home: " + string.Join(" ", calls));
    }

    [TestMethod]
    public void AGameThatStopsRespondingWithOurPressUnread_KeepsTheWindowSlid_UntilItAnswers()
    {
        // safety review 2026-11-02 (H2): a window moved home before the game reads our DOWN makes the press land at home
        using var rig = FrontRig();
        TimeSpan? answersAt = null;
        rig.OnCall = c =>
        {
            if (IsDown(c) && answersAt is null)
            {
                answersAt = rig.Clock.Monotonic + TimeSpan.FromSeconds(5); // the game freezes for 5 s
            }
        };
        rig.Win32.SendBehavior = m => m == HardRules.WM_NULL && answersAt is { } t && rig.Clock.Monotonic < t ? (false, 0) : (true, 0);

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        var calls = rig.Win32.Calls.ToList();
        var home = calls.First(IsHome);
        Assert.IsTrue(home.At >= answersAt, $"the window stayed slid until the game answered: home at {home.At}, answer at {answersAt}");
        Assert.IsNull(r.Incident, "drained in the end: no incident");
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    [TestMethod]
    public void AGameThatNeverAnswers_GoesHomeAfterTheLongWait_AsAHaltIncident()
    {
        using var rig = FrontRig();
        var frozen = false;
        rig.OnCall = c => frozen |= IsDown(c);
        rig.Win32.SendBehavior = m => m == HardRules.WM_NULL && frozen ? (false, 0) : (true, 0);

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        var calls = rig.Win32.Calls.ToList();
        var down = calls.First(IsDown);
        var home = calls.First(IsHome);
        Assert.IsTrue(home.At - down.At >= HardRules.ForegroundDrainHangMax, (home.At - down.At).ToString());
        Assert.AreEqual(InputDisposition.Incident, r.Disposition);
        Assert.AreEqual(IncidentKind.UndrainedSlideBack, r.Incident!.Kind);
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect, "R3: home in the end");
    }

    [TestMethod]
    public void AGameThatClosesDuringTheDrain_EndsAtOnce_AsGameGone()
    {
        // re-review 2026-11-02: a crash right after our DOWN must not hold the I/O lock for the 30 s hang wait
        using var rig = FrontRig();
        TimeSpan? downAt = null;
        rig.OnCall = c =>
        {
            if (IsUp(c) && downAt is null)
            {
                downAt = rig.Clock.Monotonic;
                rig.Win32.Windows.Remove(Rig.Game); // the client crashed: its window and queue are gone
                rig.Win32.Processes.Remove(Rig.GamePid);
            }
        };
        rig.Win32.SendBehavior = m => m == HardRules.WM_NULL && downAt is not null ? (false, 1400) : (true, 0);

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual(InputDisposition.GameGone, r.Disposition, $"{r.Abort} {r.Incident?.DetailCode}");
        Assert.IsTrue(rig.Clock.Monotonic - downAt!.Value < TimeSpan.FromSeconds(3), (rig.Clock.Monotonic - downAt.Value).ToString());
        Assert.IsFalse(File.Exists(rig.Paths.SlideMarkerPath), "nothing left to recover");
    }

    [TestMethod]
    public void ASendThatIsRefused_GivesUpAtOnce_AsAHaltIncident()
    {
        using var rig = FrontRig();
        var refused = false;
        rig.OnCall = c => refused |= IsUp(c);
        rig.Win32.SendBehavior = m => m == HardRules.WM_NULL && refused ? (false, 5) : (true, 0);

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        var calls = rig.Win32.Calls.ToList();
        Assert.IsTrue(calls.First(IsHome).At - calls.First(IsUp).At < TimeSpan.FromSeconds(3), "no 30 s wait for an error that does not pass");
        Assert.AreEqual(IncidentKind.UndrainedSlideBack, r.Incident?.Kind);
    }

    [TestMethod]
    public void TheMarkerOfAForegroundInput_IsRestoredOnlyAfterTheDrain()
    {
        // re-review 2026-11-02: the guardian / next start must not move the window while our press may be unread
        using var rig = new Rig();
        var store = new SlideMarkerStore(rig.Paths.RuntimeDir, rig.Paths.RunDir, rig.Paths.SlideMarkerPath);
        Assert.IsTrue(store.TryWrite(new SlideMarkerData(Rig.GamePid, Rig.Game, Rig.GameStart, Rig.HomeRect, Rig.SlidRect, rig.Clock.UtcNow, null, Foreground: true)));
        Assert.IsTrue(store.TryRead().Data!.Foreground, "round trip");
        rig.GameWindow.Rect = Rig.SlidRect;
        var frozen = true;
        rig.Win32.SendBehavior = m => m == HardRules.WM_NULL && frozen ? (false, 0) : (true, 0);

        var still = rig.Io.RecoverLeftoverDisplacement();

        Assert.IsFalse(still.Attempted);
        Assert.AreEqual("marker.pressPending", still.Incident?.DetailCode);
        Assert.AreEqual(Rig.SlidRect, rig.GameWindow.Rect, "not moved while the game does not answer");
        Assert.IsTrue(File.Exists(rig.Paths.SlideMarkerPath), "kept for the next recovery");

        frozen = false;
        var done = rig.Io.RecoverLeftoverDisplacement();

        Assert.IsTrue(done.Attempted && done.Success);
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
        Assert.IsFalse(File.Exists(rig.Paths.SlideMarkerPath));
    }

    [TestMethod]
    public void AKeyAfterACursorMove_AfterDown_IsStillRecordedAsTheUsersInput()
    {
        // M3: a movement keeps its own rule (recorded after DOWN), but the input is recorded for the engine
        using var rig = FrontRig();
        rig.BeforeSample = () =>
        {
            if (rig.Win32.Calls.Any(IsDown) && rig.Win32.Cursor == Rig.RestingCursor)
            {
                rig.Win32.Cursor = Rig.RestingCursor.Offset(1, 0);
                rig.Win32.LastInputTick += 1;
            }
        };

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.IsTrue(r.DownPosted);
        Assert.IsTrue(r.Cursor.UserInput, "recorded");
    }

    [TestMethod]
    public void TheUserSwitchingAway_SlidesHomeAtOnce_WithoutAFocusMessage()
    {
        using var rig = FrontRig();
        rig.BeforeSample = () =>
        {
            if (rig.GameWindow.Rect == Rig.SlidRect)
            {
                rig.Win32.Foreground = Rig.Cover; // alt-tab to Chrome
            }
        };

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual((InputDisposition.UserTookOver, AbortReason.ForegroundLost), (r.Disposition, r.Abort));
        Assert.IsFalse(rig.Win32.Calls.Any(IsFocus));
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    [TestMethod]
    public void AWindowPoppingUpUnderTheCursor_EndsTheSection()
    {
        using var rig = FrontRig();
        rig.Win32.Add(new FakeWindow { Hwnd = 0x6000, Pid = 555, Class = "Toast", Rect = new RectI(800, 600, 1100, 750), ExStyle = Win32Const.WS_EX_TOPMOST });
        rig.BeforeSample = () =>
        {
            if (rig.GameWindow.Rect == Rig.SlidRect && !rig.Win32.ZOrder.Contains(0x6000))
            {
                rig.Win32.ZOrder.Insert(0, 0x6000);
            }
        };

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual(AbortReason.CoverChanged, r.Abort);
        Assert.IsFalse(r.DownPosted);
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    // ------------------------------------------------------------------ readiness

    [TestMethod]
    public void TheGameInFront_IsReadyOnlyInForegroundMode_AndOnlyForItsOwnWindow()
    {
        using var rig = FrontRig();
        var click = new ReadinessQuery(InputKind.Click, Rig.BagTarget, false);

        var ready = rig.Io.CheckReadiness(click);
        Assert.IsTrue(ready.Ready, ready.Reason.ToString());
        Assert.AreEqual(InputMode.Foreground, ready.Mode);

        rig.ForegroundSlideOn = false;
        Assert.AreEqual((false, NotReadyReason.GameForeground, InputMode.Cover), (rig.Io.CheckReadiness(click).Ready, rig.Io.CheckReadiness(click).Reason, rig.Io.CheckReadiness(click).Mode));

        rig.ForegroundSlideOn = true;
        rig.Win32.Add(new FakeWindow { Hwnd = 0x7000, Pid = Rig.GamePid, Class = "#32770", Rect = new RectI(700, 400, 1200, 700), Visible = false });
        rig.Win32.Foreground = 0x7000; // a game-owned dialog in front
        Assert.AreEqual(NotReadyReason.GameForeground, rig.Io.CheckReadiness(click).Reason);
    }

    [TestMethod]
    public void AFreshSwitchIn_SettlesFirst()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();
        rig.GameInFront(TimeSpan.FromSeconds(2));
        var click = new ReadinessQuery(InputKind.Click, Rig.BagTarget, false);

        Assert.AreEqual(NotReadyReason.ForegroundSettling, rig.Io.CheckReadiness(click).Reason);
        rig.Settle(TimeSpan.FromSeconds(4));
        Assert.IsTrue(rig.Io.CheckReadiness(click).Ready);

        rig.Log.StateValue = rig.Log.StateValue with { Focus = FocusState.Lost }; // the game thinks it is unfocused
        Assert.AreEqual(NotReadyReason.ForegroundSettling, rig.Io.CheckReadiness(click).Reason);
    }

    [TestMethod]
    public void RecentInput_ATypingUser_AndAnOpenChat_AreNotReady()
    {
        using var rig = FrontRig();
        var click = new ReadinessQuery(InputKind.Click, Rig.BagTarget, false);
        var key = new ReadinessQuery(InputKind.Key, null, false);

        rig.Win32.Idle = TimeSpan.FromSeconds(1);
        Assert.AreEqual(NotReadyReason.UserInputRecent, rig.Io.CheckReadiness(click).Reason);
        rig.Win32.Idle = TimeSpan.FromSeconds(2.5);
        Assert.IsTrue(rig.Io.CheckReadiness(click).Ready, "a click needs 2 s");
        Assert.AreEqual(NotReadyReason.UserInputRecent, rig.Io.CheckReadiness(key).Reason, "a key needs 3 s");
        rig.Win32.Idle = TimeSpan.FromSeconds(30);
        Assert.IsTrue(rig.Io.CheckReadiness(key).Ready);

        rig.Win32.ButtonsDown.Add(2);
        Assert.AreEqual(NotReadyReason.UserButtonDown, rig.Io.CheckReadiness(key).Reason);
        rig.Win32.ButtonsDown.Clear();

        rig.Log.StateValue = rig.Log.StateValue with { AdvisoryStack = ["UI_CHAT"] };
        var b = rig.Io.PressKey(new KeyRequest("b", GameKey.B, UiExpectation.Opened("UI_BAG_MAIN")), CancellationToken.None);
        Assert.AreEqual((InputDisposition.NotSent, AbortReason.NotReady), (b.Disposition, b.Abort));
        Assert.AreEqual("ChatOpen", LastInput(rig).Fields["detail"]);
        var esc = rig.Io.PressKey(new KeyRequest("esc", GameKey.Escape, [UiExpectation.HiddenTop("UI_CHAT")]), CancellationToken.None);
        Assert.AreEqual(InputDisposition.Sent, esc.Disposition, "Esc may close the chat");
    }

    [TestMethod]
    public void TheCursorMustRestOnTheGamesClient_AwayFromItsEdges()
    {
        using var rig = FrontRig();
        var click = new ReadinessQuery(InputKind.Click, Rig.BagTarget, false);

        foreach (var (cursor, why) in new[]
        {
            (new PointI(918, 10), "the caption (client starts at y 23)"),
            (new PointI(918, 30), "within 16 px of the client's top"),
            (new PointI(1910, 500), "within 16 px of the client's right edge"),
            (new PointI(500, 1050), "the taskbar"),
        })
        {
            rig.Win32.Cursor = cursor;
            rig.Settle(TimeSpan.FromSeconds(3));
            Assert.AreEqual(NotReadyReason.CursorNotOnGame, rig.Io.CheckReadiness(click).Reason, why);
        }
    }

    [TestMethod]
    public void TheGameUseClock_GrowsWhileTheUserRests_AndRestartsOnInput()
    {
        using var rig = FrontRig();
        var key = new ReadinessQuery(InputKind.Key, null, false);
        rig.Settle(TimeSpan.FromSeconds(20));
        var resting = rig.Io.CheckReadiness(key);
        Assert.IsTrue(resting.SinceGameUse >= TimeSpan.FromSeconds(20), resting.SinceGameUse.ToString());
        Assert.IsTrue(resting.SinceGameForeground < TimeSpan.FromSeconds(1), "the game is in front all the time");

        rig.Win32.LastInputTick += 1; // the user clicks in the game
        rig.Win32.Idle = TimeSpan.FromMilliseconds(100);
        Assert.IsTrue(rig.Io.CheckReadiness(key).SinceGameUse < TimeSpan.FromSeconds(1));
    }

    // ------------------------------------------------------------------ keys and drags

    [TestMethod]
    public void AKeyWithTheGameInFront_IsPostedWithoutFocusMessages()
    {
        using var rig = FrontRig();

        var r = rig.Io.PressKey(new KeyRequest("b", GameKey.B, UiExpectation.Opened("UI_BAG_MAIN")), CancellationToken.None);

        Assert.AreEqual(InputDisposition.Sent, r.Disposition, r.Abort.ToString());
        Assert.AreEqual((InputMode.Foreground, 0, false), (r.Mode, r.FocusMessagesSent, r.UserInputDuring));
        Assert.IsTrue(r.ExpectedSeen);
        var calls = rig.Win32.Calls.ToList();
        Assert.IsFalse(calls.Any(IsFocus), string.Join(" ", calls));
        CollectionAssert.AreEqual(new[] { (uint)HardRules.WM_KEYDOWN, (uint)HardRules.WM_KEYUP }, calls.Where(c => c.Kind == "post").Select(c => c.Msg).ToArray());
    }

    [TestMethod]
    public void TheUsersInputDuringAKey_IsReported()
    {
        using var rig = FrontRig();
        rig.OnCall = c =>
        {
            if (c.Kind == "post" && c.Msg == HardRules.WM_KEYDOWN)
            {
                rig.Win32.LastInputTick += 1;
            }
        };

        var during = rig.Io.PressKey(new KeyRequest("esc", GameKey.Escape, []), CancellationToken.None);
        Assert.AreEqual(InputDisposition.Sent, during.Disposition);
        Assert.IsTrue(during.UserInputDuring);
        Assert.AreEqual(true, LastInput(rig).Fields["userInput"]);
    }

    [TestMethod]
    public void ADragWithTheGameInFront_WaitsForItsOwnSwitch()
    {
        // M4: drags in front are not proven live (Windows sends the game real released-button moves while the window
        // steps): off unless the user's foreground-drag switch is on
        using var rig = FrontRig();
        var drag = new ReadinessQuery(InputKind.Click, new PointI(420, 860), false, CrossMonitorAllowed: false, Drag: true);
        Assert.AreEqual(NotReadyReason.GameForeground, rig.Io.CheckReadiness(drag).Reason);

        var r = rig.Io.SlideDrag(
            new DragRequest("scroll", "craft.fav.anchor", new PointI(420, 860), new PointI(591, 756), new PointI(0, -280), rig.Log.Position, rig.Clock.Monotonic, false),
            CancellationToken.None);

        Assert.AreEqual((InputDisposition.NotSent, AbortReason.NotReady), (r.Press.Disposition, r.Press.Abort));
        Assert.AreEqual(0, rig.Win32.Calls.Count(c => c.Kind == "swp"), "nothing moved");
        rig.ForegroundDragOn = true;
        Assert.IsTrue(rig.Io.CheckReadiness(drag).Ready);
    }

    [TestMethod]
    public void ADragWithTheGameInFront_KeepsTheGameUnderTheCursor_AndDrainsInsteadOfTheUnfocus()
    {
        using var rig = FrontRig();
        rig.ForegroundDragOn = true;
        var start = new PointI(420, 860);

        var r = rig.Io.SlideDrag(
            new DragRequest("scroll", "craft.fav.anchor", start, new PointI(591, 756), new PointI(0, -280), rig.Log.Position, rig.Clock.Monotonic, false),
            CancellationToken.None);

        Assert.IsTrue(r.Completed, $"{r.Press.Disposition} {r.Press.Abort}");
        Assert.AreEqual((InputMode.Foreground, 0, 14), (r.Press.Mode, r.Press.FocusMessagesSent, r.StepsDone));
        var calls = rig.Win32.Calls.ToList();
        Assert.IsFalse(calls.Any(IsFocus), string.Join(" ", calls));
        var up = calls.FindIndex(IsUp);
        var drain = calls.FindLastIndex(IsNull);
        Assert.IsTrue(up >= 0 && up < drain && drain < calls.FindIndex(IsHome));
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    // ------------------------------------------------------------------ pure rules

    [TestMethod]
    public void ForegroundAbortOrders_NeverSendAFocusMessage_AndDrainAfterDown()
    {
        foreach (var t in Enum.GetValues<Trigger>())
        {
            foreach (var down in new[] { false, true })
            {
                var plan = AbortPlanner.Plan(t, down, upPending: true, slid: true, focusSent: false, foreground: true);
                Assert.IsFalse(plan.Contains(AbortStep.Unfocus) || plan.Contains(AbortStep.UnfocusNotify), $"{t} down={down}");
                if (down && t is not (Trigger.None or Trigger.Iconic))
                {
                    var drain = plan.ToList().IndexOf(AbortStep.Drain);
                    Assert.IsTrue(drain > plan.ToList().IndexOf(AbortStep.PostUp), $"{t}: UP before the drain");
                    var back = plan.ToList().FindIndex(s => s is AbortStep.SlideBack or AbortStep.SlideBackAsync or AbortStep.RestoreLadder);
                    Assert.IsTrue(back > drain, $"{t}: the window moves only after the drain");
                }
            }
        }

        CollectionAssert.AreEqual(
            new[] { AbortStep.PostUp, AbortStep.Drain, AbortStep.SlideBackAsync },
            AbortPlanner.Plan(Trigger.ForegroundLost, true, true, true, false, foreground: true).ToArray(),
            "switched away after DOWN: the game may read DOWN before the deactivation (H1)");
        CollectionAssert.AreEqual(new[] { AbortStep.SlideBackAsync }, AbortPlanner.Plan(Trigger.ForegroundLost, false, true, true, false, foreground: true).ToArray());
        CollectionAssert.AreEqual(new[] { AbortStep.SlideBack }, AbortPlanner.Plan(Trigger.UserInput, false, true, true, false, foreground: true).ToArray());
        Assert.AreEqual(InputDisposition.AbortedAfterFocus, AbortPlanner.DispositionOf(Trigger.UserInput, false, touched: true));
        Assert.AreEqual(InputDisposition.UserTookOver, AbortPlanner.DispositionOf(Trigger.ForegroundLost, true, touched: true));
    }

    [TestMethod]
    public void ForegroundRules_TheGameUnderAndAroundTheCursor_WellInsideItsClient()
    {
        const long g = 1;
        var origin = new PointI(0, 23);
        var client = new SizeI(1920, 1009);
        var probes = Enumerable.Repeat(g, 8).ToList();

        Assert.AreEqual(ForegroundReject.None, ForegroundRules.Evaluate(g, g, probes, new PointI(918, 672), origin, client));
        Assert.AreEqual(ForegroundReject.None, ForegroundRules.Evaluate(g, g, probes, new PointI(16, 39), origin, client), "exactly the margin");
        Assert.AreEqual(ForegroundReject.CursorNearEdge, ForegroundRules.Evaluate(g, g, probes, new PointI(15, 39), origin, client));
        Assert.AreEqual(ForegroundReject.CursorNearEdge, ForegroundRules.Evaluate(g, g, probes, new PointI(1904, 500), origin, client));
        Assert.AreEqual(ForegroundReject.NotGameUnderCursor, ForegroundRules.Evaluate(g, 2, probes, new PointI(918, 672), origin, client));
        Assert.AreEqual(ForegroundReject.RootMismatch, ForegroundRules.Evaluate(g, g, [.. probes.Take(7), 2], new PointI(918, 672), origin, client));
        Assert.AreEqual(ForegroundReject.NoClient, ForegroundRules.Evaluate(g, g, probes, new PointI(918, 672), null, client));
    }
}
