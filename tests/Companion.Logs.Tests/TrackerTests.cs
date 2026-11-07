using System.Diagnostics;
using System.Text;
using Companion.Contracts;
using Companion.Contracts.Logs;
using static Companion.Logs.Tests.Synthetic;

namespace Companion.Logs.Tests;

/// <summary>The file-level tracker (SPEC 6.1): polling, partial entries, rotation, shrink, sharing, bootstrap, ring, excerpt.</summary>
[TestClass]
public sealed class TrackerTests
{
    public TestContext TestContext { get; set; } = null!;

    private static string InWorldText(DateTime t) => string.Concat(
        Header(t),
        Entry(t.AddSeconds(1), Show("UI_SELECT_ROLE")),
        Entry(t.AddSeconds(2), Login()),
        Entry(t.AddSeconds(3), StartLoad),
        Entry(t.AddSeconds(4), "[LOG] -> module/lifeskill/lifeskill_auto_collect_mgr.lua:210    [collect_mgr] set_auto_collect_status    2    stack traceback:",
            "\tLuaScript/module/lifeskill/lifeskill_auto_collect_mgr.lua:210: in function 'set_auto_collect_status'"),
        Entry(t.AddSeconds(5), OpenConst("UI_MAIN")),
        Entry(t.AddSeconds(6), CurrentRes),
        Entry(t.AddSeconds(7), HideProgressReal));

    [TestMethod]
    public void Constructor_HasNoSideEffects()
    {
        var clock = new FakeClock();
        var missing = Path.Combine(AppContext.BaseDirectory, "tmp", "does-not-exist", Guid.NewGuid().ToString("N"));
        var paths = new CompanionPaths(missing, missing, missing, missing, missing, missing, Path.Combine(missing, "log.txt"), Path.Combine(missing, "TOS.exe"));
        var tracker = new LogTracker(paths, clock, new FakeHasher(), new UiClassifier(), new LogPrivacyFilter());
        Assert.IsNull(tracker.Position);
        Assert.IsFalse(tracker.Health.Readable);
        Assert.IsNull(tracker.Health.LastPollUtc);
        Assert.AreEqual(BusyKind.Unknown, tracker.State.Busy);
        Assert.IsFalse(Directory.Exists(missing));

        var poll = tracker.Poll();
        Assert.AreEqual(LogReadProblem.FileMissing, poll.Problem);
        Assert.HasCount(0, poll.Events);
        var report = tracker.Bootstrap(CancellationToken.None);
        Assert.IsFalse(report.Ok);
    }

    [TestMethod]
    public void FirstPoll_BootstrapsImplicitly_ThenReturnsOnlyNewEvents()
    {
        using var log = new TempLog();
        var clock = new FakeClock();
        log.Write(InWorldText(T0) + Entry(T0.AddSeconds(8), Show("UI_BAG_MAIN")));
        var tracker = log.Tracker(clock);

        var first = tracker.Poll();
        Assert.AreEqual(LogReadProblem.None, first.Problem);
        Assert.IsFalse(first.Rotated);
        Assert.HasCount(0, first.Events, "history is not reported as new");
        Assert.AreEqual(new FileInfo(log.LogPath).Length, first.End!.Offset);
        Assert.IsTrue(tracker.State.GameAvailable);
        CollectionAssert.AreEqual(new[] { "UI_BAG_MAIN" }, tracker.State.AdvisoryStack.ToArray());
        Assert.AreEqual(2, tracker.State.AutoCollectStatus);

        log.Append(Entry(T0.AddSeconds(9), HideTop("UI_BAG_MAIN")));
        var second = tracker.Poll();
        var ui = (UiLogEvent)second.Events.Single();
        Assert.AreEqual(UiVerb.HideTop, ui.Verb);
        Assert.AreEqual(first.End.Offset, ui.StartOffset);
        Assert.HasCount(0, tracker.State.AdvisoryStack);
        Assert.AreEqual(tracker.Position, tracker.State.AsOf);
    }

    [TestMethod]
    public void ItemTip_TheStateCarriesTheLatestTip_SoACallerCanRequireOneLoggedAfterItsPosition()
    {
        using var log = new TempLog();
        var clock = new FakeClock();
        log.Write(InWorldText(T0) + Entry(T0.AddSeconds(8), ItemTip("Water", "    20330037")));
        var tracker = log.Tracker(clock);
        tracker.Poll();
        var before = tracker.State;
        Assert.AreEqual(("Water", 20330037L), (before.LastItemTip!.ItemName, before.LastItemTip.ItemId));
        Assert.IsFalse(before.LastItemTip.LoggedAfter(before.AsOf), "logged before the snapshot");

        // the click: the live line, then the tip's show
        log.Append(LiveItemTipLine + "\r\n\r\n" + Entry(T0.AddSeconds(10), Show("UI_ITEM_TIPS_PASS_TOUCH")));
        var poll = tracker.Poll();
        var tip = tracker.State.LastItemTip!;
        Assert.AreEqual(("White Toast", 1051000L, new DateTime(2026, 10, 15, 1, 44, 58, 553)), (tip.ItemName, tip.ItemId, tip.At));
        Assert.AreEqual(poll.Events.OfType<ItemTipLogEvent>().Single().Position, tip.Position);
        Assert.IsTrue(tip.LoggedAfter(before.AsOf));
        CollectionAssert.AreEqual(new[] { "UI_ITEM_TIPS_PASS_TOUCH" }, tracker.State.AdvisoryStack.ToArray());

        // the excerpt keeps the tip line as it is (item data passes the privacy filter)
        CollectionAssert.Contains(tracker.ReadFilteredExcerpt(before.AsOf!, tracker.Position!, 10).ToArray(), LiveItemTipLine);
    }

    [TestMethod]
    public void GrowingFile_ByteByByte_DeliversEveryEventExactlyOnce()
    {
        using var log = new TempLog();
        var clock = new FakeClock();
        var bytes = Bytes(Header(T0)).Concat(Fixtures.Load(Fixtures.C3Run3)).ToArray();
        var expected = Parsing.All(bytes).Select(Describe).ToList();
        log.Write([]);
        var tracker = log.Tracker(clock);
        var received = new List<string>();
        for (var i = 0; i < bytes.Length; i++)
        {
            log.Append(bytes, i, 1);
            var r = tracker.Poll();
            Assert.AreEqual(LogReadProblem.None, r.Problem);
            Assert.IsFalse(r.Rotated, $"byte {i}");
            received.AddRange(r.Events.Select(Describe));
        }

        CollectionAssert.AreEqual(expected, received);
        Assert.AreEqual((long)bytes.Length, tracker.Position!.Offset);
        var press = tracker.EventsSince(tracker.Position with { Offset = 0 }).OfType<PressBlockLogEvent>().Single();
        Assert.AreEqual(new PointI(1748, 314), press.ToClient(1009));
    }

    [TestMethod]
    public void PartialEntry_WaitsForItsBlankTerminator()
    {
        using var log = new TempLog();
        var clock = new FakeClock();
        log.Write(InWorldText(T0));
        var tracker = log.Tracker(clock);
        tracker.Poll();
        var before = tracker.Position!;

        var entry = Entry(T0.AddSeconds(10), Show("UI_MAIL"));
        log.Append(entry[..^2]);
        var r = tracker.Poll();
        Assert.HasCount(0, r.Events);
        Assert.AreEqual(before, r.End);

        log.Append(entry[^2..]);
        r = tracker.Poll();
        Assert.AreEqual("UI_MAIL", ((UiLogEvent)r.Events.Single()).UiName);
    }

    [TestMethod]
    public void Rotation_NewFileAlreadyLargerThanTheOldOffset_ResetsAndRebootstraps()
    {
        using var log = new TempLog();
        var clock = new FakeClock();
        log.Write(InWorldText(T0) + Entry(T0.AddSeconds(8), Show("UI_BAG_MAIN")));
        var tracker = log.Tracker(clock);
        tracker.Poll();
        var oldPos = tracker.Position!;
        Assert.IsTrue(tracker.State.GameAvailable);

        // The game restarts: log.txt -> log.bak1, then a new, larger log.txt (no UI_MAIN yet).
        log.RotateAway();
        var t1 = T0.AddMinutes(5);
        var sb = new StringBuilder(Header(t1));
        while (sb.Length < oldPos.Offset * 3)
        {
            sb.Append(Entry(t1.AddSeconds(1), "[LOG] -> some/noise.lua:1    starting up"));
        }

        sb.Append(Entry(t1.AddSeconds(2), Show("UI_SELECT_ROLE")));
        log.Write(sb.ToString());

        var r = tracker.Poll();
        Assert.IsTrue(r.Rotated);
        Assert.AreEqual(LogReadProblem.None, r.Problem);
        Assert.AreNotEqual(oldPos.File, r.End!.File);
        Assert.AreEqual((long)Encoding.UTF8.GetByteCount(sb.ToString()), r.End.Offset);
        Assert.IsTrue(r.Events.OfType<SessionLogEvent>().Any(s => s.Marker == SessionMarker.LogCallbackAdded));
        var s = tracker.State;
        Assert.IsFalse(s.GameAvailable);
        Assert.AreEqual(BusyKind.Login, s.Busy);
        Assert.HasCount(0, s.AdvisoryStack);
        Assert.IsNull(s.AutoCollectStatus);
        Assert.HasCount(0, tracker.EventsSince(oldPos), "positions of the old generation answer nothing");
        Assert.HasCount(0, tracker.EventsBetween(oldPos, r.End));
        Assert.AreEqual(1, tracker.Health.Rotations);

        var again = tracker.Poll();
        Assert.IsFalse(again.Rotated);
        Assert.HasCount(0, again.Events);
    }

    [TestMethod]
    public void Rotation_InTheMiddleOfASession_KeepsTheSession()
    {
        // incident 32 (2026-10-30): the game, running since 10-27, moved its 98 MB log aside and went on in a new log.txt
        // that began with a scene load; resetting the model there left the game "not available" until the next login
        using var log = new TempLog();
        var clock = new FakeClock();
        log.Write(InWorldText(T0) + Entry(T0.AddSeconds(8), Show("UI_ACTIVITY_CENTER")));
        var tracker = log.Tracker(clock);
        tracker.Poll();
        var before = tracker.State;
        Assert.IsTrue(before.GameAvailable);

        // a line the last poll did not see yet, then the game moves the file to log.bak and starts a new one 0.08 s later
        var t1 = T0.AddHours(3);
        log.Append(Entry(t1.AddSeconds(-1), Show("UI_TRIPLE_TOWN_HELP")));
        var bak = log.RotateAway("log.bak");
        File.SetLastWriteTime(bak, t1);
        log.Write(Entry(t1, "[LOG] -> some/noise.lua:1    End BundleLoader") + Entry(t1.AddSeconds(1), Show("UI_TRIPLE_TOWN_SETTLE")));
        var r = tracker.Poll();
        Assert.IsTrue(r.Rotated);
        var s = tracker.State;
        Assert.IsTrue(s.GameAvailable, "the session goes on");
        Assert.AreEqual(before.Character, s.Character);
        CollectionAssert.AreEqual(new[] { "UI_ACTIVITY_CENTER", "UI_TRIPLE_TOWN_HELP", "UI_TRIPLE_TOWN_SETTLE" }, s.AdvisoryStack.ToArray(), "the old file is read to its end first");
        CollectionAssert.AreEqual(new[] { "UI_TRIPLE_TOWN_HELP", "UI_TRIPLE_TOWN_SETTLE" }, r.Events.OfType<UiLogEvent>().Select(e => e.UiName).ToArray());
        Assert.IsNull(s.LastHideTopNil, "a position in the old file proves nothing in the new one (safety review)");
    }

    [TestMethod]
    public void Rotation_WhenTheNewFileBeganLongAfterTheOldOneEnded_IsANewSession()
    {
        // a crash and a restart whose log does not begin with the start-up marker: more than 5 s between the files
        using var log = new TempLog();
        var clock = new FakeClock();
        log.Write(InWorldText(T0));
        var tracker = log.Tracker(clock);
        tracker.Poll();
        var t1 = T0.AddHours(3);
        File.SetLastWriteTime(log.RotateAway("log.bak"), t1.AddSeconds(-14));
        log.Write(Entry(t1, "[LOG] -> some/noise.lua:1    End BundleLoader"));
        tracker.Poll();
        Assert.IsFalse(tracker.State.GameAvailable);
        Assert.IsNull(tracker.State.Character);

        // and when log.bak is not the old file at all, the model is reset as before
        using var other = new TempLog();
        other.Write(InWorldText(T0));
        var second = other.Tracker(clock);
        second.Poll();
        other.RotateAway("log.bak1");
        File.WriteAllText(Path.Combine(other.Dir, "log.bak"), InWorldText(T0));
        other.Write(Entry(t1, "[LOG] -> some/noise.lua:1    End BundleLoader"));
        second.Poll();
        Assert.IsFalse(second.State.GameAvailable);
    }

    [TestMethod]
    public void Rotation_UntilTheNewFileHasALine_TheGameIsNotAvailable_ThenTheLineDecides()
    {
        using var log = new TempLog();
        var clock = new FakeClock();
        log.Write(InWorldText(T0));
        var tracker = log.Tracker(clock);
        tracker.Poll();
        File.SetLastWriteTime(log.RotateAway("log.bak"), T0.AddHours(1));
        var line = Entry(T0.AddHours(1), "[LOG] -> some/noise.lua:1    End BundleLoader");
        log.Write(line[..10]);
        tracker.Poll();
        Assert.IsFalse(tracker.State.GameAvailable, "a rotation with no complete first line yet: not known what happened");

        log.Append(line[10..]);
        tracker.Poll();
        Assert.IsTrue(tracker.State.GameAvailable, "not the start-up marker: the same session");
    }

    [TestMethod]
    public void StartingOnAFileThatContinuesASession_ReadsTheFileTheGameMovedAside_OnlyWhenItWasJustMoved()
    {
        // the companion starts after a mid-session rotation: the login is in log.bak, written up to the new file's first line
        using var log = new TempLog();
        var t1 = T0.AddHours(3);
        var bak = Path.Combine(log.Dir, "log.bak");
        File.WriteAllText(bak, InWorldText(T0) + Entry(T0.AddSeconds(8), Show("UI_ACTIVITY_CENTER")));
        File.SetLastWriteTime(bak, t1);
        log.Write(Entry(t1, "[LOG] -> some/noise.lua:1    End BundleLoader") + Entry(t1.AddSeconds(1), Show("UI_TRIPLE_TOWN_SETTLE")));
        var tracker = log.Tracker(new FakeClock());
        tracker.Poll();
        var s = tracker.State;
        Assert.IsTrue(s.GameAvailable);
        Assert.IsNotNull(s.Character);
        CollectionAssert.AreEqual(new[] { "UI_ACTIVITY_CENTER", "UI_TRIPLE_TOWN_SETTLE" }, s.AdvisoryStack.ToArray());

        // the host's start-up read (Bootstrap) does the same (the live install of 10-30 16:2x went through it, not Poll)
        var started = log.Tracker(new FakeClock());
        Assert.IsTrue(started.Bootstrap(CancellationToken.None).Ok);
        Assert.IsTrue(started.State.GameAvailable);
        Assert.IsNotNull(started.State.Character);

        // a log.bak from another time is never used (more than 5 s before the current file's first line)
        File.SetLastWriteTime(bak, t1.AddSeconds(-30));
        var stale = log.Tracker(new FakeClock());
        stale.Poll();
        Assert.IsFalse(stale.State.GameAvailable);
        Assert.IsNull(stale.State.Character);
    }

    [TestMethod]
    public void Shrink_SameFileTruncatedAndRewritten_IsARotation()
    {
        using var log = new TempLog();
        var clock = new FakeClock();
        var text = InWorldText(T0) + Entry(T0.AddSeconds(8), Show("UI_BAG_MAIN"));
        log.Write(text);
        var tracker = log.Tracker(clock);
        tracker.Poll();

        // Same file (same index, same first line), but shorter than our offset.
        log.Truncate(Encoding.UTF8.GetByteCount(Header(T0)));
        var r = tracker.Poll();
        Assert.IsTrue(r.Rotated);
        Assert.IsFalse(tracker.State.GameAvailable);
        Assert.AreEqual((long)Encoding.UTF8.GetByteCount(Header(T0)), r.End!.Offset);
    }

    [TestMethod]
    public void FileShareDelete_TheGameCanRenameTheLogWhileWeHoldIt()
    {
        using var log = new TempLog();
        log.Write(InWorldText(T0));
        using (var fs = LogFileAccess.OpenShared(log.LogPath))
        {
            Assert.IsNotNull(LogFileAccess.TryGetIdentity(fs, fs.Length));
            log.RotateAway();   // must not throw: we opened with FileShare.Delete
            Assert.IsFalse(File.Exists(log.LogPath));
        }

        // Polling while the log is missing reports it and never throws; the new file is a rotation.
        var clock = new FakeClock();
        using var log2 = new TempLog();
        log2.Write(InWorldText(T0));
        var tracker = log2.Tracker(clock);
        tracker.Poll();
        log2.RotateAway();
        var missing = tracker.Poll();
        Assert.AreEqual(LogReadProblem.FileMissing, missing.Problem);
        Assert.IsFalse(tracker.Health.Readable);
        log2.Write(Header(T0.AddMinutes(1)));
        var r = tracker.Poll();
        Assert.IsTrue(r.Rotated);
        Assert.IsTrue(tracker.Health.Readable);
    }

    [TestMethod]
    public void NewFileWithoutCompleteFirstLine_RotationIsReportedOnce_EventsFollowWhenReady()
    {
        using var log = new TempLog();
        var clock = new FakeClock();
        log.Write(InWorldText(T0));
        var tracker = log.Tracker(clock);
        tracker.Poll();
        log.RotateAway();
        var header = Header(T0.AddMinutes(2));
        log.Write(header[..10]);
        var r = tracker.Poll();
        Assert.IsTrue(r.Rotated);
        Assert.IsNull(r.End);
        Assert.IsFalse(tracker.State.GameAvailable);

        log.Append(header[10..] + Entry(T0.AddMinutes(2).AddSeconds(1), Show("UI_SELECT_ROLE")));
        r = tracker.Poll();
        Assert.IsFalse(r.Rotated);
        Assert.HasCount(2, r.Events, "the new generation's first events are delivered");
        Assert.AreEqual(1, tracker.Health.Rotations);
    }

    [TestMethod]
    public void Bootstrap_LargeFile_FindsChangeOnlyStateNearTheStart_AndRebuildsTheStackFromTheTail()
    {
        using var log = new TempLog();
        var clock = new FakeClock();
        var classifier = new UiClassifier();
        var hasher = new FakeHasher();
        var head = InWorldText(T0)
            + Entry(T0.AddSeconds(8), OpenConst("UI_BAG_MAIN"))
            + Entry(T0.AddSeconds(9), HideTop("UI_EARLY_SCREEN"))
            + Entry(T0.AddSeconds(9.5), HideTop("UI_BAG_MAIN"))
            + Entry(T0.AddSeconds(10), FocusRecover, FocusPipes);
        var noise = Entry(T0.AddSeconds(11), "[LOG] -> module/some/noise.lua:77    filler line with nothing interesting    0123456789abcdef");
        var tailText = Entry(T0.AddHours(1), Show("UI_MAIL")) + Entry(T0.AddHours(1).AddSeconds(1), Show("UI_RANK_VIEW"));
        var targetBytes = 24 * 1024 * 1024;
        var noiseBytes = Bytes(noise);
        using (var fs = new FileStream(log.LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            fs.Write(Bytes(head));
            while (fs.Length < targetBytes)
            {
                fs.Write(noiseBytes);
            }

            fs.Write(Bytes(tailText));
        }

        var length = new FileInfo(log.LogPath).Length;
        var tracker = log.Tracker(clock, classifier, hasher);
        var sw = Stopwatch.StartNew();
        var report = tracker.Bootstrap(CancellationToken.None);
        sw.Stop();
        TestContext.WriteLine($"bootstrap of {length / (1024 * 1024)} MB took {sw.ElapsedMilliseconds} ms");

        Assert.IsTrue(report.Ok);
        Assert.AreEqual(length, report.ScannedBytes);
        Assert.AreEqual(2, report.AutoCollectStatus);
        Assert.AreEqual(FakeHasher.Of(FakeRoleToken), report.Character);
        Assert.AreEqual(new SizeI(1920, 1009), report.LastResolution);
        Assert.IsTrue(report.InWorld);
        Assert.IsLessThan(10_000L, sw.ElapsedMilliseconds, "target is 2 s for 80 MB; 10 s is the hard test limit");
        Assert.AreEqual(length, tracker.Position!.Offset);
        Assert.AreEqual(report.File, tracker.Position.File);

        var s = tracker.State;
        CollectionAssert.AreEqual(new[] { "UI_MAIL", "UI_RANK_VIEW" }, s.AdvisoryStack.ToArray());
        Assert.AreEqual(FocusState.Recovered, s.Focus);
        Assert.IsTrue(classifier.IsLearnedStack("UI_EARLY_SCREEN"), "hide top ui names anywhere in the file are learned");
        Assert.AreEqual(1, hasher.Calls);

        // The tail events are buffered, the needle-scan events are not.
        var buffered = tracker.EventsSince(tracker.Position with { Offset = 0 });
        Assert.HasCount(2, buffered);
        Assert.IsTrue(buffered.All(e => e.StartOffset > length - (2 * 1024 * 1024)));

        // A second (warm) bootstrap gives the same answer; its time is the steady-state cost.
        sw.Restart();
        var warm = tracker.Bootstrap(CancellationToken.None);
        sw.Stop();
        TestContext.WriteLine($"warm bootstrap of {length / (1024 * 1024)} MB took {sw.ElapsedMilliseconds} ms");
        Assert.AreEqual(report with { Elapsed = TimeSpan.Zero }, warm with { Elapsed = TimeSpan.Zero });
        Assert.AreEqual(2, hasher.Calls);

        // Nothing the tracker reports carries the raw role token.
        var excerpt = tracker.ReadFilteredExcerpt(tracker.Position with { Offset = 0 }, tracker.Position, 100_000);
        var everything = string.Join("\n", excerpt) + report + s + string.Join("\n", buffered);
        Assert.DoesNotContain(FakeRoleToken, everything);
        Assert.DoesNotContain("req_login_role", everything);
    }

    [TestMethod]
    public void Bootstrap_SmallFile_ParsesEverything_AndCancellationIsReported()
    {
        using var log = new TempLog();
        var clock = new FakeClock();
        log.Write(InWorldText(T0));
        var tracker = log.Tracker(clock);
        var report = tracker.Bootstrap(CancellationToken.None);
        Assert.IsTrue(report.Ok);
        Assert.IsTrue(report.InWorld);
        // header, UI_SELECT_ROLE, login, start load, collect, UI_MAIN, resolution, hide_progress real
        Assert.HasCount(8, tracker.EventsSince(tracker.Position! with { Offset = 0 }));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = tracker.Bootstrap(cts.Token);
        Assert.IsFalse(cancelled.Ok);
        Assert.IsNull(tracker.Position);
        Assert.AreEqual(BusyKind.Unknown, tracker.State.Busy);
    }

    [TestMethod]
    public void NeedleScan_BoundaryInsideAnEntry_NothingLostNothingDoubled()
    {
        using var log = new TempLog();
        var clock = new FakeClock();
        var hasher = new FakeHasher();
        var sb = new StringBuilder(InWorldText(T0));
        for (var i = 0; i < 400; i++)
        {
            sb.Append(Entry(T0.AddSeconds(10 + i), i % 2 == 0 ? Show("UI_MAIL") : HideTop("UI_MAIL")));
            sb.Append(Entry(T0.AddSeconds(10 + i), FocusRecover, FocusPipes));
        }

        log.Write(sb.ToString());
        var options = LogTrackerOptions.Default with { BootstrapTailBytes = 7_777, NeedleChunkBytes = 4096 };
        var tracker = log.Tracker(clock, hasher: hasher, options: options);
        var report = tracker.Bootstrap(CancellationToken.None);
        Assert.IsTrue(report.Ok);
        Assert.AreEqual(2, report.AutoCollectStatus);
        Assert.AreEqual(1, hasher.Calls, "the login line is applied exactly once");
        Assert.HasCount(0, tracker.State.AdvisoryStack, "the last UI_MAIL event is a hide");
        Assert.AreEqual((long)Encoding.UTF8.GetByteCount(sb.ToString()), tracker.Position!.Offset);
    }

    [TestMethod]
    public void PerPollCap_LargeAppend_IsConsumedOverSeveralPolls_WithoutLoss()
    {
        using var log = new TempLog();
        var clock = new FakeClock();
        log.Write(InWorldText(T0));
        var options = LogTrackerOptions.Default with { MaxReadBytes = 4096 };
        var tracker = log.Tracker(clock, options: options);
        tracker.Poll();
        var sb = new StringBuilder();
        for (var i = 0; i < 300; i++)
        {
            sb.Append(Entry(T0.AddSeconds(20 + i), Show($"UI_X{i}")));
        }

        log.Append(sb.ToString());
        var received = new List<LogEvent>();
        var polls = 0;
        while (polls++ < 1000)
        {
            var r = tracker.Poll();
            received.AddRange(r.Events);
            if (r.Events.Count == 0)
            {
                break;
            }
        }

        Assert.HasCount(300, received);
        Assert.IsGreaterThan(5, polls);
        Assert.AreEqual("UI_X299", ((UiLogEvent)received[^1]).UiName);
    }

    [TestMethod]
    public void EventsSinceAndBetween_UseEndOffsets()
    {
        using var log = new TempLog();
        var clock = new FakeClock();
        log.Write(InWorldText(T0));
        var tracker = log.Tracker(clock);
        tracker.Poll();
        var mark = tracker.Position!;
        log.Append(Entry(T0.AddSeconds(20), Show("UI_A")) + Entry(T0.AddSeconds(21), Show("UI_B")) + Entry(T0.AddSeconds(22), Show("UI_C")));
        var r = tracker.Poll();
        Assert.HasCount(3, r.Events);
        CollectionAssert.AreEqual(r.Events.ToArray(), tracker.EventsSince(mark).ToArray());
        CollectionAssert.AreEqual(new[] { r.Events[1] }, tracker.EventsBetween(r.Events[0].Position, r.Events[1].Position).ToArray());
        Assert.HasCount(0, tracker.EventsSince(r.End!));
        var foreign = new LogPosition(new LogFileIdentity(DateTime.UnixEpoch, 1, "other"), 0);
        Assert.HasCount(0, tracker.EventsSince(foreign));
    }

    [TestMethod]
    public void Ring_EvictsByCountAndByAge()
    {
        using var log = new TempLog();
        var clock = new FakeClock();
        log.Write(InWorldText(T0));
        var options = LogTrackerOptions.Default with { RingCapacity = 5 };
        var tracker = log.Tracker(clock, options: options);
        tracker.Poll();
        var start = tracker.Position! with { Offset = 0 };
        var sb = new StringBuilder();
        for (var i = 0; i < 8; i++)
        {
            sb.Append(Entry(T0.AddSeconds(30 + i), Show($"UI_R{i}")));
        }

        log.Append(sb.ToString());
        tracker.Poll();
        var kept = tracker.EventsSince(start);
        Assert.HasCount(5, kept);
        Assert.AreEqual("UI_R7", ((UiLogEvent)kept[^1]).UiName);

        clock.Advance(TimeSpan.FromMinutes(16));
        tracker.Poll();
        Assert.HasCount(0, tracker.EventsSince(start), "older than 15 min");
    }

    [TestMethod]
    public void Health_TracksGrowthReadabilityAndPolls()
    {
        using var log = new TempLog();
        var clock = new FakeClock();
        log.Write(InWorldText(T0));
        var tracker = log.Tracker(clock);
        tracker.Poll();
        var h = tracker.Health;
        Assert.IsTrue(h.Readable);
        Assert.AreEqual(clock.UtcNow, h.LastPollUtc);
        Assert.IsNull(h.LastGrowthUtc, "growth is unknown until the file is seen growing");
        Assert.AreEqual(new FileInfo(log.LogPath).Length, h.Length);

        clock.Advance(TimeSpan.FromSeconds(3));
        log.Append(Entry(T0.AddSeconds(40), Show("UI_A")));
        tracker.Poll();
        Assert.AreEqual(clock.UtcNow, tracker.Health.LastGrowthUtc);

        clock.Advance(TimeSpan.FromSeconds(3));
        tracker.Poll();
        Assert.AreEqual(clock.UtcNow - TimeSpan.FromSeconds(3), tracker.Health.LastGrowthUtc);
        Assert.AreEqual(clock.UtcNow, tracker.Health.LastPollUtc);
    }

    [TestMethod]
    public void State_TimeWindowsUseTheClock()
    {
        using var log = new TempLog();
        var clock = new FakeClock();
        log.Write(InWorldText(T0) + Entry(T0.AddSeconds(30), Show("UI_REVIVE")));
        var tracker = log.Tracker(clock);
        tracker.Poll();
        clock.SetLocal(T0.AddSeconds(40));
        Assert.AreEqual(BusyKind.Revive, tracker.State.Busy);
        clock.SetLocal(T0.AddSeconds(91));
        Assert.AreEqual(BusyKind.None, tracker.State.Busy);
    }

    [TestMethod]
    public void ReadFilteredExcerpt_KeepsOnlyAllowedLines_LastN_PressFieldsOnly()
    {
        using var log = new TempLog();
        var clock = new FakeClock();
        var text = InWorldText(T0)
            + Entry(T0.AddSeconds(50), "[LOG] -> module/chat/chat_mgr.lua:300    recv msg    PlayerOne: hello 12345-678901")
            + Entry(T0.AddSeconds(51), "[LOG] -> vendor/ema/ema_vendor.lua:954    baLogEvent upload:     login    account_hash=abcdef0123456789    role_id=12345-678901")
            + Entry(T0.AddSeconds(52), "[LOG] -> cave/cave_helper.lua:232    [scene_load] before loading_empty    town    12345-678901")
            + Entry(T0.AddSeconds(53), HideProgressReal);
        log.Write(text);
        var tracker = log.Tracker(clock);
        tracker.Poll();
        var start = tracker.Position! with { Offset = 0 };
        log.Append(Fixtures.Load(Fixtures.C3Run3));
        tracker.Poll();

        var all = tracker.ReadFilteredExcerpt(start, tracker.Position!, 10_000);
        Assert.IsTrue(all.All(l => !l.Contains("PlayerOne", StringComparison.Ordinal)));
        Assert.IsTrue(all.All(l => !l.Contains("account_hash", StringComparison.Ordinal)));
        Assert.IsTrue(all.All(l => !l.Contains("12345-678901", StringComparison.Ordinal)));
        Assert.IsTrue(all.Any(l => l.Contains("[scene_load] before loading_empty    town    <id>", StringComparison.Ordinal)));
        Assert.IsTrue(all.All(l => !l.StartsWith("|\teventCamera", StringComparison.Ordinal)));
        CollectionAssert.Contains(all.ToArray(), "|\tmodule: Name: Canvas_Bag (UnityEngine.GameObject)");
        CollectionAssert.Contains(all.ToArray(), "|\tscreenPosition: (1748.00, 694.00)");
        Assert.IsTrue(all.All(l => !l.Contains("UnityEngine.Logger", StringComparison.Ordinal)));
        Assert.IsTrue(all.All(l => !l.EndsWith('\r')));
        var filter = new LogPrivacyFilter();
        Assert.IsTrue(all.All(filter.IsAllowed));

        var last3 = tracker.ReadFilteredExcerpt(start, tracker.Position!, 3);
        CollectionAssert.AreEqual(all.Skip(all.Count - 3).ToArray(), last3.ToArray());
        Assert.HasCount(0, tracker.ReadFilteredExcerpt(start, tracker.Position!, 0));
        var foreign = new LogPosition(new LogFileIdentity(DateTime.UnixEpoch, 1, "other"), 0);
        Assert.HasCount(0, tracker.ReadFilteredExcerpt(foreign, tracker.Position!, 10));
    }

    [TestMethod]
    public void ConcurrentPollsAndReads_AreSerialised()
    {
        using var log = new TempLog();
        var clock = new FakeClock();
        log.Write(InWorldText(T0));
        var tracker = log.Tracker(clock);
        tracker.Poll();
        var start = tracker.Position! with { Offset = 0 };
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                log.Append(Entry(T0.AddSeconds(100 + i), Show($"UI_C{i}")));
            }
        });
        var pollers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                tracker.Poll();
                _ = tracker.State;
                _ = tracker.EventsSince(start);
            }
        })).ToArray();
        Task.WaitAll([writer, .. pollers]);
        tracker.Poll();
        var names = tracker.EventsSince(start).OfType<UiLogEvent>().Select(e => e.UiName).Where(n => n!.StartsWith("UI_C", StringComparison.Ordinal)).ToList();
        CollectionAssert.AreEqual(Enumerable.Range(0, 200).Select(i => $"UI_C{i}").ToArray(), names.ToArray());
    }

    private static string Describe(LogEvent e) => e with { Position = e.Position with { File = Parsing.FixtureFile } } is var x ? x.ToString() : string.Empty;
}
