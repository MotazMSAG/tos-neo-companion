using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;
using Companion.GameIO.Input;
using Companion.GameIO.Native;
using Companion.GameIO.Safety;
using Companion.GameIO.Tests.Fakes;

namespace Companion.GameIO.Tests;

/// <summary>Choke-point runtime asserts (SPEC 1.5): nothing reaches the (fake) Win32 layer when a rule would break.</summary>
[TestClass]
public sealed class ChokePointTests
{
    private const long Game = 0x1000;
    private FakeClock _clock = null!;
    private FakeWin32 _w = null!;
    private ChokePoint _c = null!;

    [TestInitialize]
    public void Init()
    {
        _clock = new FakeClock();
        _w = new FakeWin32(_clock);
        _w.Add(new FakeWindow { Hwnd = Game, Pid = 4242, Class = HardRules.GameWindowClass });
        _w.Add(new FakeWindow { Hwnd = 0x2000, Pid = 777 });
        _w.Foreground = 0x2000;
        _c = new ChokePoint(_w, Game, 4242, new SizeI(1920, 1009));
    }

    private void Refused(Action a)
    {
        var before = _w.Calls.Count;
        Assert.ThrowsExactly<HardRuleViolationException>(a);
        Assert.AreEqual(before, _w.Calls.Count, "nothing may reach Win32 after a refused assert");
    }

    [TestMethod]
    public void HwndMustBeTheValidatedGame()
    {
        Refused(() => _c.Post(0x2000, HardRules.WM_MOUSEMOVE, 0, HardRules.MouseLParam(new PointI(10, 10)), out _));
        Refused(() => _c.Send(0, HardRules.WM_NULL, 0, 0));
        Refused(() => _c.SetWindowPos(0x2000, 0, 0, 0, 0, 0, HardRules.SwpSlide, out _));
        Refused(() => _c.PrintClient(0x2000, HardRules.PW_RENDERFULLCONTENT, default, default, default));
        Assert.ThrowsExactly<HardRuleViolationException>(() => new ChokePoint(_w, 0, 1, new SizeI(1920, 1009)));
    }

    [TestMethod]
    public void OnlyAllowlistedMessages()
    {
        foreach (var m in HardRules.ForbiddenMessages)
        {
            Refused(() => _c.Post(Game, m, 0, 0, out _));
        }

        Refused(() => _c.Post(Game, 0x0102, 0x42, 0, out _)); // WM_CHAR
        Refused(() => _c.Post(Game, HardRules.WM_SIZE, HardRules.SIZE_MAXIMIZED, (1009L << 16) | 1920, out _)); // emergency only
        Assert.IsTrue(_c.Post(Game, HardRules.WM_NULL, 0, 0, out _));
    }

    [TestMethod]
    public void WmSizeOnlyInsideTheEmergencyLadderAndOnlyMaximized1920x1009()
    {
        using (_c.EnterEmergency())
        {
            Refused(() => _c.Post(Game, HardRules.WM_SIZE, HardRules.SIZE_MAXIMIZED, (1260L << 16) | 2400, out _));
            Refused(() => _c.Post(Game, HardRules.WM_SIZE, 0, (1009L << 16) | 1920, out _));
            Assert.IsTrue(_c.Post(Game, HardRules.WM_SIZE, HardRules.SIZE_MAXIMIZED, (1009L << 16) | 1920, out _));
        }

        Assert.IsFalse(_c.InEmergency);
        Refused(() => _c.Post(Game, HardRules.WM_SIZE, HardRules.SIZE_MAXIMIZED, (1009L << 16) | 1920, out _));
    }

    [TestMethod]
    public void MouseLParamMustLieInsideTheClient()
    {
        Refused(() => _c.Post(Game, HardRules.WM_LBUTTONDOWN, HardRules.MK_LBUTTON, HardRules.MouseLParam(new PointI(1920, 10)), out _));
        Refused(() => _c.Post(Game, HardRules.WM_LBUTTONDOWN, HardRules.MK_LBUTTON, HardRules.MouseLParam(new PointI(10, 1009)), out _));
        Refused(() => _c.Post(Game, HardRules.WM_MOUSEMOVE, 0, HardRules.MouseLParam(new PointI(-1, 10)), out _));
        Refused(() => _c.Post(Game, HardRules.WM_MOUSEMOVE, 0, 0x1_0000_0000L, out _));
        Refused(() => _c.Post(Game, HardRules.WM_LBUTTONDOWN, 0, HardRules.MouseLParam(new PointI(10, 10)), out _)); // wParam must be MK_LBUTTON
        Refused(() => _c.Post(Game, HardRules.WM_LBUTTONUP, 1, HardRules.MouseLParam(new PointI(10, 10)), out _));
        Refused(() => _c.Post(Game, HardRules.WM_MOUSEMOVE, 2, HardRules.MouseLParam(new PointI(10, 10)), out _)); // only MK_LBUTTON (a drag)
        Refused(() => _c.Post(Game, HardRules.WM_MOUSEMOVE, HardRules.MK_LBUTTON, HardRules.MouseLParam(new PointI(10, 1009)), out _));
        Assert.IsTrue(_c.Post(Game, HardRules.WM_LBUTTONDOWN, HardRules.MK_LBUTTON, HardRules.MouseLParam(new PointI(1748, 314)), out _));
        Assert.IsTrue(_c.Post(Game, HardRules.WM_MOUSEMOVE, HardRules.MK_LBUTTON, HardRules.MouseLParam(new PointI(420, 840)), out _), "a drag step's move");
        Assert.IsTrue(ChokePoint.MouseLParamInsideClient(HardRules.MouseLParam(new PointI(1919, 1008)), new SizeI(1920, 1009)));
    }

    [TestMethod]
    public void KeysAreExactWhitelistedPairsAndNeverDenied()
    {
        var b = HardRules.AllowedKeys[GameKey.B];
        var esc = HardRules.AllowedKeys[GameKey.Escape];
        Assert.IsTrue(_c.Post(Game, HardRules.WM_KEYDOWN, b.VirtualKey, HardRules.KeyDownLParam(b), out _));
        Assert.IsTrue(_c.Post(Game, HardRules.WM_KEYUP, esc.VirtualKey, HardRules.KeyUpLParam(esc), out _));
        foreach (var denied in HardRules.DeniedKeys)
        {
            Refused(() => _c.Post(Game, HardRules.WM_KEYDOWN, denied.VirtualKey, HardRules.KeyDownLParam(denied), out _));
        }

        var wrongScan = new KeyCode(0x42, 0x31);
        Refused(() => _c.Post(Game, HardRules.WM_KEYDOWN, wrongScan.VirtualKey, HardRules.KeyDownLParam(wrongScan), out _));
        Refused(() => _c.Post(Game, HardRules.WM_KEYDOWN, b.VirtualKey, HardRules.KeyUpLParam(b), out _)); // up lParam on DOWN
        Refused(() => _c.Post(Game, HardRules.WM_KEYDOWN, 0x41, HardRules.KeyDownLParam(new KeyCode(0x41, 0x1E)), out _)); // 'A' not whitelisted
    }

    [TestMethod]
    public void KeyWhitelistAndDenyList()
    {
        Assert.IsTrue(Guards.IsKeyAllowed(new KeyCode(0x1B, 0x01)));
        Assert.IsTrue(Guards.IsKeyAllowed(new KeyCode(0x42, 0x30)));
        Assert.IsFalse(Guards.IsKeyAllowed(new KeyCode(0x0D, 0x1C)));
        Assert.IsFalse(Guards.IsKeyAllowed(new KeyCode(0x59, 0x15)));
        Assert.IsFalse(Guards.IsKeyAllowed(new KeyCode(0x49, 0x17)));
        Assert.IsTrue(Guards.IsKeyDenied(new KeyCode(0x59, 0x30)), "a denied VK is denied whatever the scan code");
        Assert.AreEqual(0x00300001L, HardRules.KeyDownLParam(new KeyCode(0x42, 0x30)));
        Assert.AreEqual(0xC0300001L, HardRules.KeyUpLParam(new KeyCode(0x42, 0x30)));
        Assert.AreEqual(0x00010001L, HardRules.KeyDownLParam(new KeyCode(0x1B, 0x01)));
    }

    [TestMethod]
    public void FocusMessagesAreSentNeverPostedAndOnlyWithProvenParams()
    {
        foreach (var m in HardRules.FocusOnSequence.Concat(HardRules.FocusOffSequence))
        {
            Refused(() => _c.Post(Game, m.Message, m.WParam, m.LParam, out _));
            Assert.AreEqual(SendOutcome.Ok, _c.Send(Game, m.Message, m.WParam, m.LParam));
        }

        Refused(() => _c.Send(Game, HardRules.WM_ACTIVATE, 2, 0));
        Refused(() => _c.Send(Game, HardRules.WM_SETFOCUS, 1, 0));
        Refused(() => _c.Send(Game, HardRules.WM_LBUTTONDOWN, HardRules.MK_LBUTTON, 0)); // input is posted, never sent
        Refused(() => _c.Notify(Game, HardRules.WM_ACTIVATE, 1, 0)); // notify: unfocus only
        Assert.IsTrue(_w.Calls.Where(c => c.Kind == "send").All(c => c.Flags == (Win32Const.SMTO_ABORTIFHUNG | Win32Const.SMTO_BLOCK)));
    }

    [TestMethod]
    public void FakeUnfocusIsNeverSentWhileTheGameIsReallyForeground()
    {
        _w.Foreground = Game;
        foreach (var m in HardRules.FocusOffSequence)
        {
            Assert.AreEqual(SendOutcome.SkippedForeground, _c.Send(Game, m.Message, m.WParam, m.LParam));
            Assert.AreEqual(SendOutcome.SkippedForeground, _c.Notify(Game, m.Message, m.WParam, m.LParam));
        }

        _w.Add(new FakeWindow { Hwnd = 0x6000, Pid = 4242 }); // another window of the game PID (FOC-8: PID, not HWND)
        _w.Foreground = 0x6000;
        Assert.AreEqual(SendOutcome.SkippedForeground, _c.Send(Game, HardRules.WM_KILLFOCUS, 0, 0));
        Assert.AreEqual(0, _w.Calls.Count);
    }

    [TestMethod]
    public void SendOutcomesMapLastError()
    {
        _w.SendBehavior = _ => (false, Win32Const.ERROR_ACCESS_DENIED);
        Assert.AreEqual(SendOutcome.AccessDenied, _c.Send(Game, HardRules.WM_NULL, 0, 0));
        _w.SendBehavior = _ => (false, Win32Const.ERROR_TIMEOUT);
        Assert.AreEqual(SendOutcome.Timeout, _c.Send(Game, HardRules.WM_NULL, 0, 0));
    }

    [TestMethod]
    public void SetWindowPosFlagsAndInsertAfter()
    {
        Assert.IsTrue(_c.SetWindowPos(Game, 0, -838, 327, 0, 0, HardRules.SwpSlide, out _));
        Assert.IsTrue(_c.SetWindowPos(Game, 0, -8, -8, 0, 0, HardRules.SwpSlideAsync, out _));
        Refused(() => _c.SetWindowPos(Game, 1, -8, -8, 0, 0, HardRules.SwpSlide, out _)); // HWND_BOTTOM
        Refused(() => _c.SetWindowPos(Game, -1, -8, -8, 0, 0, HardRules.SwpSlide, out _)); // HWND_TOPMOST
        Refused(() => _c.SetWindowPos(Game, 0, -8, -8, 0, 0, HardRules.SWP_NOSIZE | HardRules.SWP_NOZORDER, out _)); // activates
        Refused(() => _c.SetWindowPos(Game, 0, -8, -8, 0, 0, 0x0040 | HardRules.SwpSlide, out _)); // SWP_SHOWWINDOW
        Refused(() => _c.SetWindowPos(Game, 0, -8, -8, 1936, 1048, HardRules.SwpEmergencyMoveSize, out _)); // emergency only
        using (_c.EnterEmergency())
        {
            Assert.IsTrue(_c.SetWindowPos(Game, 0, -8, -8, 1936, 1048, HardRules.SwpEmergencyMoveSize, out _));
            Assert.IsTrue(_c.SetWindowPos(Game, 0, -8, -8, 1936, 1048, HardRules.SwpEmergencyMoveSizeAsync, out _));
            Refused(() => _c.SetWindowPos(Game, 0, -8, -8, 0, 0, HardRules.SwpEmergencyMoveSize, out _)); // size required
        }
    }

    [TestMethod]
    public void PrintWindowOnlyWithRenderFullContent()
    {
        Refused(() => _c.PrintClient(Game, 0, new RectI(0, 0, 10, 10), default, new SizeI(10, 10)));
        Assert.IsNotNull(_c.PrintClient(Game, HardRules.PW_RENDERFULLCONTENT, new RectI(0, 0, 10, 10), default, new SizeI(10, 10)));
    }

    [TestMethod]
    public void GetAsyncKeyStateOnlyForMouseButtons()
    {
        _w.ButtonsDown.Add(Win32Const.VK_RBUTTON);
        Assert.IsFalse(Guards.IsMouseButtonDown(_w, 0x01));
        Assert.IsTrue(Guards.IsMouseButtonDown(_w, 0x02));
        Assert.IsTrue(Guards.AnyMouseButtonDown(_w));
        foreach (var vk in new[] { 0x00, 0x03, 0x05, 0x10, 0x41, 0x42, 0x1B, 0x0D })
        {
            Assert.ThrowsExactly<HardRuleViolationException>(() => Guards.IsMouseButtonDown(_w, vk));
        }
    }

    [TestMethod]
    public void OpenProcessOnlyWithQueryLimitedInformation()
    {
        _w.Processes[4242] = new FakeProcess("TOS", Rig.GameExe, Rig.GameStart);
        Assert.AreEqual(Rig.GameExe, Guards.QueryImagePath(_w, 4242, 0x1000));
        foreach (var access in new uint[] { 0x0400, 0x0010, 0x0020, 0x1F0FFF, 0x1001, 0 })
        {
            Assert.ThrowsExactly<HardRuleViolationException>(() => Guards.QueryImagePath(_w, 4242, access));
        }
    }
}

[TestClass]
public sealed class PacingTests
{
    [TestMethod]
    public void AtLeastOneSecondBetweenBatches()
    {
        var clock = new FakeClock();
        var p = new PacingBucket(clock);
        Assert.IsTrue(p.TryAcquire(false, true));
        Assert.IsFalse(p.TryAcquire(false, true));
        Assert.AreEqual(TimeSpan.FromSeconds(1), p.NextAllowedIn(false, false));
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.IsFalse(p.TryAcquire(false, false));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.IsTrue(p.TryAcquire(false, false));
    }

    [TestMethod]
    public void TheCapsAreTheUsersHumanPace_AndConsistent()
    {
        // User decisions: 2026-10-14 about one click every 3 s, 2026-10-15 human browsing speed (one every 1.5 s) while the
        // commit pace stays at the 2026-10-14 values. The caps must stay consistent with each other: a focus pair and a
        // slide are always part of a batch, and the hour cap is never looser than the minute cap allows.
        int slides = HardRules.MaxSlidesPerMinute, batches = HardRules.MaxInputBatchesPerMinute;
        int pairs = HardRules.MaxFocusPairsPerMinute, hour = HardRules.MaxInputBatchesPerHour;
        Assert.AreEqual(TimeSpan.FromSeconds(1.5), TimeSpan.FromMinutes(1) / slides, "one slide every 1.5 s at most");
        Assert.AreEqual((40, 40, 40, 2400), (slides, batches, pairs, hour));
        Assert.IsLessThanOrEqualTo(batches, slides, "a slide is a batch");
        Assert.IsLessThanOrEqualTo(batches, pairs, "a focus pair is a batch");
        Assert.IsLessThanOrEqualTo(60 * batches, hour, "the hour cap is never looser than 60 minute caps");
        Assert.IsTrue(HardRules.MinGapBetweenInputBatches * batches < TimeSpan.FromMinutes(1), "the minute cap is reachable");

        // the commit pace: today's (2026-10-14) values, never looser than the browsing pace
        int cSlides = HardRules.MaxCommitSlidesPerMinute, cBatches = HardRules.MaxCommitInputBatchesPerMinute;
        int cPairs = HardRules.MaxCommitFocusPairsPerMinute, cHour = HardRules.MaxCommitInputBatchesPerHour;
        Assert.AreEqual((20, 20, 20, 1200), (cSlides, cBatches, cPairs, cHour));
        Assert.AreEqual(TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(1) / cSlides, "one commit slide every 3 s at most");
        Assert.IsTrue(cSlides <= slides && cBatches <= batches && cPairs <= pairs && cHour <= hour, "the commit pace is the stricter one");
        Assert.IsLessThanOrEqualTo(60 * cBatches, cHour);
    }

    [TestMethod]
    public void ACommitInput_KeepsTheCommitPace_WhileBrowsingInputsUseTheFasterOne()
    {
        var clock = new FakeClock();
        var p = new PacingBucket(clock);
        var gap = HardRules.MinGapBetweenInputBatches;
        var first = clock.Monotonic;
        for (var i = 0; i < HardRules.MaxCommitSlidesPerMinute; i++)
        {
            Assert.IsTrue(p.TryAcquire(true, true), $"browsing slide {i}");
            clock.Advance(gap);
        }

        // The commit caps are full in this minute: a commit slide waits, a browsing slide still goes.
        Assert.IsTrue(clock.Monotonic - first < TimeSpan.FromMinutes(1));
        Assert.IsFalse(p.TryAcquire(true, true, commit: true), "the Make button keeps 20 slides per minute");
        Assert.AreEqual(first + TimeSpan.FromMinutes(1) - clock.Monotonic, p.NextAllowedIn(true, true, commit: true));
        Assert.AreEqual(TimeSpan.Zero, p.NextAllowedIn(true, true));
        Assert.IsTrue(p.TryAcquire(true, true), "a browsing slide goes on at the faster pace");

        // The window rolls: once the first slides leave the minute, the commit slide may start.
        clock.Advance(first + TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1) - clock.Monotonic);
        Assert.IsTrue(p.TryAcquire(true, true, commit: true));
    }

    [TestMethod]
    public void SlidesPerRollingMinuteAreCappedByTheHardRule()
    {
        var clock = new FakeClock();
        var p = new PacingBucket(clock);
        var gap = HardRules.MinGapBetweenInputBatches;
        var first = clock.Monotonic;
        for (var i = 0; i < HardRules.MaxSlidesPerMinute; i++)
        {
            Assert.IsTrue(p.TryAcquire(true, false), $"slide {i}");
            clock.Advance(gap);
        }

        Assert.IsTrue(clock.Monotonic - first < TimeSpan.FromMinutes(1), "every slide above fell in the same minute");
        Assert.IsFalse(p.TryAcquire(true, false), "one slide over the cap");
        Assert.AreEqual(HardRules.MaxSlidesPerMinute, p.Status().SlidesLastMinute);

        // A key batch is not a slide: it is held back only by the batch cap.
        Assert.AreEqual(HardRules.MaxSlidesPerMinute < HardRules.MaxInputBatchesPerMinute, p.TryAcquire(false, false), "a key batch is not a slide");

        // The window rolls: the next slide is possible exactly when the first one leaves the minute.
        clock.Advance(first + TimeSpan.FromMinutes(1) - clock.Monotonic - TimeSpan.FromMilliseconds(1));
        Assert.IsFalse(p.TryAcquire(true, false));
        Assert.AreEqual(TimeSpan.FromMilliseconds(1), p.NextAllowedIn(true, false));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.IsTrue(p.TryAcquire(true, false));
        Assert.AreEqual(HardRules.MaxSlidesPerMinute, p.Status().SlidesLastMinute);
    }

    [TestMethod]
    public void BatchesAndFocusPairsPerRollingMinuteAreCappedByTheHardRules()
    {
        var clock = new FakeClock();
        var p = new PacingBucket(clock);
        var gap = HardRules.MinGapBetweenInputBatches;
        var first = clock.Monotonic;
        for (var i = 0; i < HardRules.MaxFocusPairsPerMinute; i++)
        {
            Assert.IsTrue(p.TryAcquire(false, true), $"pair {i}");
            clock.Advance(gap);
        }

        Assert.IsFalse(p.TryAcquire(false, true), "one focus pair over the cap");
        for (var i = HardRules.MaxFocusPairsPerMinute; i < HardRules.MaxInputBatchesPerMinute; i++)
        {
            Assert.IsTrue(p.TryAcquire(false, false), $"batch {i}");
            clock.Advance(gap);
        }

        Assert.IsTrue(clock.Monotonic - first < TimeSpan.FromMinutes(1));
        Assert.IsFalse(p.TryAcquire(false, false), "one batch over the cap");
        Assert.AreEqual(HardRules.MaxInputBatchesPerMinute, p.Status().BatchesLastMinute);
        Assert.AreEqual(HardRules.MaxFocusPairsPerMinute, p.Status().FocusPairsLastMinute);

        clock.Advance(first + TimeSpan.FromMinutes(1) - clock.Monotonic);
        Assert.IsTrue(p.TryAcquire(false, true), "the first pair left the minute");
    }

    [TestMethod]
    public void BatchesPerRollingHourNeverExceedTheHardRule_AtTheFastestAllowedPace()
    {
        var clock = new FakeClock();
        var p = new PacingBucket(clock);
        var taken = new List<TimeSpan>();
        var end = clock.Monotonic + TimeSpan.FromHours(1) + TimeSpan.FromMinutes(5);
        while (clock.Monotonic < end)
        {
            var wait = p.NextAllowedIn(false, false);
            if (wait > TimeSpan.Zero)
            {
                clock.Advance(wait);
                continue;
            }

            Assert.IsTrue(p.TryAcquire(false, false));
            taken.Add(clock.Monotonic);
            var inHour = taken.Count(t => clock.Monotonic - t < TimeSpan.FromHours(1));
            Assert.IsTrue(inHour <= HardRules.MaxInputBatchesPerHour, $"{inHour} batches in a rolling hour");
            Assert.AreEqual(inHour, p.Status().BatchesLastHour);
        }

        // The pace is as fast as the caps allow: the hour fills up to (not past) the hour cap.
        var firstHour = taken.Count(t => t - taken[0] < TimeSpan.FromHours(1));
        Assert.AreEqual(Math.Min(HardRules.MaxInputBatchesPerHour, 60 * HardRules.MaxInputBatchesPerMinute), firstHour);
    }
}

[TestClass]
public sealed class RegistryTests
{
    [TestMethod]
    public void UnityPrefNamesAreComputedNotEnumerated()
    {
        Assert.AreEqual("Screenmanager Resolution Width_h182942802", GamePrefsReader.UnityValueName("Screenmanager Resolution Width"));
        Assert.AreEqual("Screenmanager Resolution Height_h2627697771", GamePrefsReader.UnityValueName("Screenmanager Resolution Height"));
        Assert.AreEqual("UnitySelectMonitor_h17969598", GamePrefsReader.UnityValueName("UnitySelectMonitor"));
        Assert.AreEqual("Screenmanager Fullscreen mode_h3630240806", GamePrefsReader.UnityValueName("Screenmanager Fullscreen mode"));
    }

    [TestMethod]
    public void RegistryWhitelist()
    {
        foreach (var prefix in StaticRules.RegistryReadWhitelistPrefixes)
        {
            Assert.IsTrue(GamePrefsReader.IsWhitelisted(GamePrefsReader.UnityValueName(prefix)), prefix);
        }

        Assert.IsFalse(GamePrefsReader.IsWhitelisted("PlayerName_h123"));
        Assert.IsFalse(GamePrefsReader.IsWhitelisted("account_token"));
        Assert.IsFalse(GamePrefsReader.IsWhitelisted("screenmanager Fullscreen mode_h1"), "ordinal, case-sensitive");
    }

    [TestMethod]
    public void ReaderReadsOnlyWhitelistedValues()
    {
        var w = new FakeWin32(new FakeClock());
        w.Registry[GamePrefsReader.UnityValueName("Screenmanager Fullscreen mode")] = 3;
        w.Registry[GamePrefsReader.UnityValueName("Screenmanager Window Position X")] = BitConverter.GetBytes(-1928);
        w.Registry[GamePrefsReader.UnityValueName("Resolution Window Width")] = 1920;
        w.Registry[GamePrefsReader.UnityValueName("Screenmanager Resolution Height")] = 1009L;
        w.Registry["SecretThing_h1"] = 42;
        var prefs = GamePrefsReader.Read(w);
        Assert.AreEqual(3, prefs.FullscreenMode);
        Assert.AreEqual(-1928, prefs.WindowPositionX);
        Assert.IsNull(prefs.WindowPositionY);
        Assert.AreEqual(1920, prefs.ResolutionWidth);
        Assert.AreEqual(1009, prefs.ResolutionHeight);
        Assert.IsTrue(w.RegistryReads.All(GamePrefsReader.IsWhitelisted));
        Assert.IsFalse(w.RegistryReads.Contains("SecretThing_h1"));
    }

    [TestMethod]
    public void PrefsDifferences()
    {
        var a = new GameWindowPrefs(3, 0, 0, 0, 1920, 1009, 1, 1);
        Assert.IsNull(GamePrefsReader.Difference(a, a with { LogOpen = 0 }));
        Assert.AreEqual("prefs.fullscreenMode", GamePrefsReader.Difference(a, a with { FullscreenMode = 1 }));
        Assert.AreEqual("prefs.position", GamePrefsReader.Difference(a, a with { WindowPositionX = -1928 }));
        Assert.AreEqual("prefs.monitor", GamePrefsReader.Difference(a, a with { SelectMonitor = 1 }));
        Assert.AreEqual("prefs.resolution", GamePrefsReader.Difference(a, a with { ResolutionHeight = 1260 }));
        Assert.AreEqual(7, GamePrefsReader.ToInt(new byte[] { 7, 0, 0, 0, 0, 0, 0, 0 }));
        Assert.IsNull(GamePrefsReader.ToInt("3"));
    }
}

[TestClass]
public sealed class AbortPlannerTests
{
    private static AbortStep[] P(Trigger t, bool down, bool upPending = true, bool slid = true, bool focus = true) =>
        AbortPlanner.Plan(t, down, upPending, slid, focus).ToArray();

    [TestMethod]
    public void CoverChangedStopOrExceptionAfterDownPostsUpWaitsSlidesBackThenUnfocuses()
    {
        var expected = new[] { AbortStep.PostUp, AbortStep.Wait20, AbortStep.SlideBack, AbortStep.Unfocus };
        foreach (var t in new[] { Trigger.CoverChanged, Trigger.GameUnderCursor, Trigger.Stop, Trigger.Exception, Trigger.CursorLost })
        {
            CollectionAssert.AreEqual(expected, P(t, down: true), t.ToString());
        }

        CollectionAssert.AreEqual(new[] { AbortStep.SlideBack, AbortStep.Unfocus }, P(Trigger.Stop, down: true, upPending: false));
    }

    [TestMethod]
    public void BeforeDownSlideBackThenUnfocus()
    {
        foreach (var t in new[] { Trigger.CursorMoved, Trigger.UserButton, Trigger.CoverChanged, Trigger.Stop, Trigger.SlidTemplateMismatch, Trigger.LogGatePopup, Trigger.FocusFailed })
        {
            CollectionAssert.AreEqual(new[] { AbortStep.SlideBack, AbortStep.Unfocus }, P(t, down: false), t.ToString());
        }

        CollectionAssert.AreEqual(new[] { AbortStep.Unfocus }, P(Trigger.CursorMoved, down: false, slid: false));
        CollectionAssert.AreEqual(Array.Empty<AbortStep>(), P(Trigger.Stop, down: false, slid: false, focus: false));
    }

    [TestMethod]
    public void DeadManUsesTheForcedOrder()
    {
        foreach (var t in new[] { Trigger.DeadMan, Trigger.SendTimeout, Trigger.MovedBeforeUp })
        {
            CollectionAssert.AreEqual(new[] { AbortStep.UnfocusNotify, AbortStep.PostUp, AbortStep.SlideBackAsync }, P(t, down: true), t.ToString());
            CollectionAssert.AreEqual(new[] { AbortStep.UnfocusNotify, AbortStep.SlideBackAsync }, P(t, down: false), t.ToString());
        }
    }

    [TestMethod]
    public void ForegroundNeverUnfocuses()
    {
        CollectionAssert.AreEqual(new[] { AbortStep.SlideBackAsync }, P(Trigger.Foreground, down: false));
        CollectionAssert.AreEqual(new[] { AbortStep.SlideBackAsync, AbortStep.PostUp }, P(Trigger.Foreground, down: true));
    }

    [TestMethod]
    public void IconicNeverMovesTheWindow()
    {
        CollectionAssert.AreEqual(Array.Empty<AbortStep>(), P(Trigger.Iconic, down: false));
        CollectionAssert.AreEqual(new[] { AbortStep.PostUp }, P(Trigger.Iconic, down: true));
    }

    [TestMethod]
    public void WindowChangedRunsTheLadderAndClipChangedSlidesBack()
    {
        CollectionAssert.AreEqual(new[] { AbortStep.Unfocus, AbortStep.RestoreLadder }, P(Trigger.WindowChanged, down: false));
        CollectionAssert.AreEqual(new[] { AbortStep.PostUp, AbortStep.Unfocus, AbortStep.RestoreLadder }, P(Trigger.WindowChanged, down: true));
        CollectionAssert.AreEqual(new[] { AbortStep.Unfocus, AbortStep.SlideBack }, P(Trigger.ClipChanged, down: false));
        CollectionAssert.AreEqual(new[] { AbortStep.PostUp, AbortStep.Unfocus, AbortStep.SlideBack }, P(Trigger.ClipChanged, down: true));
    }

    [TestMethod]
    public void Dispositions()
    {
        Assert.AreEqual(InputDisposition.NotSent, AbortPlanner.DispositionOf(Trigger.CursorMoved, false, false));
        Assert.AreEqual(InputDisposition.AbortedAfterFocus, AbortPlanner.DispositionOf(Trigger.CursorMoved, false, true));
        Assert.AreEqual(InputDisposition.Sent, AbortPlanner.DispositionOf(Trigger.DeadMan, true, true));
        Assert.AreEqual(InputDisposition.UserTookOver, AbortPlanner.DispositionOf(Trigger.Foreground, true, true));
        Assert.AreEqual(InputDisposition.UserTookOver, AbortPlanner.DispositionOf(Trigger.Iconic, false, true));
        Assert.AreEqual(InputDisposition.Stopped, AbortPlanner.DispositionOf(Trigger.Stop, true, true));
        Assert.AreEqual(InputDisposition.Incident, AbortPlanner.DispositionOf(Trigger.ClipChanged, false, true));
        Assert.AreEqual(InputDisposition.Refused, AbortPlanner.DispositionOf(Trigger.NotClickable, false, true));
        Assert.AreEqual(InputDisposition.FocusFailed, AbortPlanner.DispositionOf(Trigger.FocusFailed, false, true));
        Assert.AreEqual(AbortReason.SlidTemplateMismatch, AbortPlanner.ReasonOf(Trigger.SlidTemplateMismatch));
        Assert.AreEqual(AbortReason.LogGatePopup, AbortPlanner.ReasonOf(Trigger.LogGatePopup));
        Assert.AreEqual(AbortReason.DeadMan, AbortPlanner.ReasonOf(Trigger.DeadMan));
    }

    [TestMethod]
    public void JournalCodesAreSanitized()
    {
        Assert.AreEqual("Chrome_WidgetWin_1", GameIo.Code("Chrome_WidgetWin_1"));
        Assert.AreEqual("_32770", GameIo.Code("#32770"));
        Assert.AreEqual(120, GameIo.Code(new string('a', 500)).Length);
        Assert.AreEqual("a_b_c", GameIo.Code("a\tb\nc"));
    }
}
