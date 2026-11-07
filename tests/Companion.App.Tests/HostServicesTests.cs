using System.Text;
using Companion.App.Hosting;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Host;

namespace Companion.App.Tests;

[TestClass]
public sealed class ConfigStoreTests
{
    [TestMethod]
    public void MissingFile_GivesDefaults_AutomationOff_SlideClicksOn_NothingWritten()
    {
        var paths = TestDirs.Paths();
        var store = new ConfigStore(paths, new FakeJournal(), new FakeClock());
        var c = store.Load();
        Assert.IsFalse(c.Automation.Crafting);
        Assert.IsFalse(c.Automation.Homestead);
        Assert.IsTrue(c.Input.SlideClick);
        Assert.IsFalse(c.Policy.CloseUserUisAtJobStart);
        Assert.IsNull(store.LoadProblem);
        Assert.IsTrue(store.IsOn(AutomationSwitch.None));
        Assert.IsFalse(store.IsOn(AutomationSwitch.Crafting));
        Assert.IsFalse(store.IsOn(AutomationSwitch.Homestead));
        Assert.IsTrue(store.SlideClickEnabled);
        Assert.IsTrue(c.Features.Values.All(v => v), "every feature enabled by default");
        Assert.IsFalse(Directory.Exists(paths.RuntimeDir), "loading never writes");
        Assert.IsFalse(CompanionConfig.Default.Automation.Crafting || CompanionConfig.Default.Automation.Homestead);
    }

    [TestMethod]
    public void Update_IsAtomic_Journaled_AndRoundTrips()
    {
        var paths = TestDirs.Paths();
        var journal = new FakeJournal();
        var store = new ConfigStore(paths, journal, new FakeClock());
        store.Load();
        Assert.IsTrue(store.Update(c => c with { Automation = c.Automation with { Crafting = true } }, "automation.crafting.on"));
        Assert.IsTrue(store.Update(c => c with { Policy = c.Policy with { TreatAutoCollectStatusAsIdle = [2, 2, 3] } }, "policy.autoCollectIdle.add"));
        Assert.IsTrue(store.Update(c => c with { Input = c.Input with { SlideClick = false } }, "input.slideClick.off"));
        Assert.IsTrue(store.IsOn(AutomationSwitch.Crafting));
        Assert.IsFalse(store.SlideClickEnabled);
        Assert.AreEqual(3, journal.Records.Count(r => r.Kind == JournalKind.ConfigChange));

        Assert.IsTrue(File.Exists(paths.ConfigPath));
        Assert.AreEqual(0, Directory.GetFiles(paths.RuntimeDir, "*.tmp").Length, "no temp file left");

        var again = new ConfigStore(paths, new FakeJournal(), new FakeClock());
        var c2 = again.Load();
        Assert.IsTrue(c2.Automation.Crafting);
        Assert.IsFalse(c2.Automation.Homestead);
        Assert.IsFalse(c2.Input.SlideClick);
        CollectionAssert.AreEqual(new[] { 2, 3 }, c2.Policy.TreatAutoCollectStatusAsIdle!.ToArray());
        Assert.IsNull(again.LoadProblem);
    }

    [TestMethod]
    public void InvalidOrPartialFiles()
    {
        var paths = TestDirs.Paths();
        Directory.CreateDirectory(paths.RuntimeDir);

        File.WriteAllText(paths.ConfigPath, "{ not json");
        var s1 = new ConfigStore(paths, new FakeJournal(), new FakeClock());
        Assert.IsFalse(s1.Load().Automation.Crafting);
        Assert.AreEqual("config.invalid", s1.LoadProblem);
        Assert.AreEqual("{ not json", File.ReadAllText(paths.ConfigPath), "an invalid file is left for the user");

        File.WriteAllText(paths.ConfigPath, "{\"schemaVersion\":2,\"automation\":{\"crafting\":true}}");
        var s2 = new ConfigStore(paths, new FakeJournal(), new FakeClock());
        Assert.IsFalse(s2.Load().Automation.Crafting, "unknown schema = safe defaults");
        Assert.AreEqual("config.schema", s2.LoadProblem);

        File.WriteAllText(paths.ConfigPath, "{\"schemaVersion\":1,\"automation\":{\"homestead\":true},\"features\":{\"core\":false,\"crafting\":false},\"keepStock\":{\"maxDailyCrafts\":100000}}", new UTF8Encoding(true));
        var s3 = new ConfigStore(paths, new FakeJournal(), new FakeClock());
        var c3 = s3.Load();
        Assert.IsNull(s3.LoadProblem);
        Assert.IsTrue(c3.Automation.Homestead);
        Assert.IsFalse(c3.Automation.Crafting);
        Assert.IsTrue(c3.Input.SlideClick, "missing section = default");
        Assert.IsTrue(c3.Features["core"], "core cannot be disabled");
        Assert.IsFalse(c3.Features["crafting"]);
        Assert.IsTrue(c3.Features["survey"]);
        Assert.AreEqual(500, c3.KeepStock.MaxDailyCrafts, "caps are clamped");

        // the user's config.json before 4.32 has "input":{"slideClick":true} only: foreground mode is on by default
        File.WriteAllText(paths.ConfigPath, "{\"schemaVersion\":1,\"input\":{\"slideClick\":true}}");
        var s4 = new ConfigStore(paths, new FakeJournal(), new FakeClock());
        Assert.IsTrue(s4.Load().Input.ForegroundSlide, "a missing foregroundSlide = on (the user's decision 2026-11-02)");
        File.WriteAllText(paths.ConfigPath, "{\"schemaVersion\":1,\"input\":{\"slideClick\":true,\"foregroundSlide\":false}}");
        var s5 = new ConfigStore(paths, new FakeJournal(), new FakeClock());
        Assert.IsFalse(s5.Load().Input.ForegroundSlide, "the switch turns it off");
    }
}

[TestClass]
public sealed class HaltLatchTests
{
    [TestMethod]
    public void Halt_Persists_SurvivesRestart_UntilCleared()
    {
        var paths = TestDirs.Paths();
        var notifier = new FakeNotifier();
        var latch = new HaltLatch(paths, notifier, new FakeJournal(), new FakeClock());
        Assert.IsNull(latch.Load());
        Assert.IsFalse(latch.IsHalted);

        var report = new HaltReport(HaltCode.RecalibrateTemplate, "hud.bag", "0123456789abcdef0123456789abcdef", "OpenBagClick", "open", DateTimeOffset.UnixEpoch, null);
        latch.Halt(report);
        Assert.IsTrue(latch.IsHalted);
        Assert.IsFalse(latch.PersistFailed);
        Assert.IsTrue(File.Exists(paths.HaltLatchPath));
        Assert.AreEqual("halt.RecalibrateTemplate", notifier.Shown.Single().Code);
        StringAssert.Contains(notifier.Shown.Single().Text, "hud.bag");

        latch.Halt(report with { Code = HaltCode.InternalError });
        Assert.AreEqual(HaltCode.RecalibrateTemplate, latch.Current!.Code, "the first HALT wins");

        var restarted = new HaltLatch(paths, new FakeNotifier(), new FakeJournal(), new FakeClock());
        var loaded = restarted.Load();
        Assert.IsTrue(restarted.IsHalted);
        Assert.AreEqual(HaltCode.RecalibrateTemplate, loaded!.Code);
        Assert.AreEqual("hud.bag", loaded.Subject);
        Assert.AreEqual("OpenBagClick", loaded.ProcedureId);

        restarted.Clear("tray");
        Assert.IsFalse(restarted.IsHalted);
        Assert.IsFalse(File.Exists(paths.HaltLatchPath));
        var third = new HaltLatch(paths, new FakeNotifier(), new FakeJournal(), new FakeClock());
        Assert.IsNull(third.Load());
        Assert.IsFalse(third.IsHalted);
    }

    [TestMethod]
    public void DamagedLatchFile_CountsAsHalted()
    {
        var paths = TestDirs.Paths();
        Directory.CreateDirectory(paths.RuntimeDir);
        File.WriteAllText(paths.HaltLatchPath, "{\"code\":");
        var latch = new HaltLatch(paths, new FakeNotifier(), new FakeJournal(), new FakeClock());
        var r = latch.Load();
        Assert.IsTrue(latch.IsHalted);
        Assert.AreEqual(HaltCode.InternalError, r!.Code);
        Assert.AreEqual("halt.corrupt", r.Subject);
    }
}

[TestClass]
public sealed class NotificationTests
{
    [TestMethod]
    public void Gate_DeduplicatesByCode()
    {
        var clock = new FakeClock();
        var gate = new NotificationGate(clock, TimeSpan.FromMinutes(10));
        var a = new Notification(NotificationKind.Warning, "wait.displayMode", "x");
        var b = new Notification(NotificationKind.Info, "host.stopped", "y");
        Assert.IsTrue(gate.ShouldShow(a));
        Assert.IsFalse(gate.ShouldShow(a));
        Assert.IsTrue(gate.ShouldShow(b), "codes are independent");
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.IsTrue(gate.ShouldShow(a), "again after the window");
        Assert.AreEqual(1, gate.Suppressed);

        var halt = HostMessages.Halt(HaltCode.WorldPress, null);
        Assert.IsTrue(gate.ShouldShow(halt));
        clock.Advance(TimeSpan.FromHours(5));
        Assert.IsFalse(gate.ShouldShow(halt), "a HALT is shown once until it is cleared");
        gate.Reset("halt.");
        Assert.IsTrue(gate.ShouldShow(halt));
    }

    [TestMethod]
    public void Notifier_ShowsOnce_JournalsCodesOnly()
    {
        var clock = new FakeClock();
        var journal = new FakeJournal();
        var notifier = new HostNotifier(new NotificationGate(clock, TimeSpan.FromMinutes(10)), journal, clock);
        var shown = new List<Notification>();
        notifier.Sink = shown.Add;
        var engineHalt = new Notification(NotificationKind.Halt, "halt.ForbiddenUi", "engine text");
        notifier.Notify(engineHalt);
        notifier.Notify(HostMessages.Halt(HaltCode.ForbiddenUi, null)); // the latch notifies the same code
        Assert.AreEqual(1, shown.Count, "engine and latch notifications are de-duplicated by code");
        Assert.AreEqual(1, journal.Records.Count(r => r.Kind == JournalKind.Notification));
        Assert.IsFalse(journal.Records.SelectMany(r => r.Fields.Values).Any(v => v is string s && s.Contains("text", StringComparison.Ordinal)), "texts are not journaled");

        notifier.Sink = _ => throw new InvalidOperationException("tray gone");
        notifier.Notify(new Notification(NotificationKind.Info, "host.stopped", "t")); // never throws
        Assert.AreEqual(2, notifier.Recent.Count);
    }

    [TestMethod]
    public void HaltTexts_AreFixed_AndOnlyCodeSubjectsAreSubstituted()
    {
        foreach (var code in Enum.GetValues<HaltCode>())
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(HostMessages.HaltText(code, null)), code.ToString());
        }

        StringAssert.Contains(HostMessages.HaltText(HaltCode.LayoutChanged, "hud.bag"), "'hud.bag'");
        StringAssert.Contains(HostMessages.HaltText(HaltCode.LayoutChanged, "<script> chat text"), "'?'");
        Assert.AreEqual("halt.GuideBan", HostMessages.Halt(HaltCode.GuideBan, null).Code);
        Assert.AreEqual(NotificationKind.Halt, HostMessages.Halt(HaltCode.GuideBan, null).Kind);
        Assert.AreEqual("host.info", HostMessages.Host("no.such.code").Code);
    }
}

[TestClass]
public sealed class GuardianArgsTests
{
    [TestMethod]
    public void ValidArguments_RoundTrip()
    {
        var g = GuardianArgs.TryParse(["--guardian", "1234", "638950000000000000"]);
        Assert.IsNotNull(g);
        Assert.AreEqual(1234, g.HostPid);
        Assert.AreEqual(638950000000000000L, g.HostStartTicks);
        Assert.AreEqual("--guardian 1234 638950000000000000", g.ToArguments());
        Assert.AreEqual(g, GuardianArgs.TryParse(g.ToArguments().Split(' ')));
        Assert.IsTrue(GuardianArgs.IsGuardian(["--guardian"]));
        Assert.IsFalse(GuardianArgs.IsGuardian([]));
        Assert.IsFalse(GuardianArgs.IsGuardian(["status", "--guardian"]));
    }

    [TestMethod]
    [DataRow(new[] { "--guardian" })]
    [DataRow(new[] { "--guardian", "1234" })]
    [DataRow(new[] { "--guardian", "1234", "5", "extra" })]
    [DataRow(new[] { "--guardian", "abc", "5" })]
    [DataRow(new[] { "--guardian", "-1", "5" })]
    [DataRow(new[] { "--guardian", "+12", "5" })]
    [DataRow(new[] { "--guardian", " 12", "5" })]
    [DataRow(new[] { "--guardian", "0", "5" })]
    [DataRow(new[] { "--guardian", "12", "0" })]
    [DataRow(new[] { "--guardian", "12", "99999999999999999999" })]
    [DataRow(new[] { "--GUARDIAN", "12", "5" })]
    [DataRow(new[] { "12", "--guardian", "5" })]
    public void InvalidArguments_AreRefused(string[] args) => Assert.IsNull(GuardianArgs.TryParse(args));
}
