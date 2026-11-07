using Companion.Contracts.Logs;

namespace Companion.Dailies.Tests;

/// <summary>
/// The checklist on PCs in other time zones and servers (2026-11-08): the data file's times are written at UTC+3
/// ('timesUtcOffset'); the service moves them to the PC's offset, or to the daily update the game log showed. The PC's
/// offset is injected, so these tests give the same result on a machine in any time zone.
/// </summary>
[TestClass]
public sealed class TimeZoneTests
{
    // Wednesday 2026-11-04, Sunday 2026-11-08 (local times on the PC under test).
    private static readonly DateTime Wed = new(2026, 11, 4, 0, 0, 0);
    private static readonly DateTime Sun = new(2026, 11, 8, 0, 0, 0);

    private readonly Log _log = new();
    private DateTime _now;

    private static Func<DateTime, TimeSpan> Pc(double hours) => _ => TimeSpan.FromHours(hours);

    private DailyService Service(Func<DateTime, TimeSpan>? pc, byte[]? state = null, DailyCatalog? catalog = null) =>
        new(catalog ?? Data.Catalog(), state, () => _now, null, pc);

    private static DailyItemView Item(DailySnapshot s, string id) => s.Items.Single(i => i.Id == id);

    [TestMethod]
    public void AUtcPlus1Pc_SeesTheDailyResetAt0500_AndThe2144RaccoonAt1944()
    {
        _now = Wed.AddHours(12);
        var svc = Service(Pc(1));
        svc.CompleteReplay([]);
        var s = svc.Snapshot();
        Assert.AreEqual(TimeSpan.FromHours(-2), svc.Shift);
        Assert.AreEqual(Wed.AddDays(1).AddHours(5), s.NextDailyReset);
        Assert.AreEqual(new DateOnly(2026, 11, 4), s.GameDay);
        var bear = Item(s, "bear").Windows!.Single();
        Assert.AreEqual(Wed.AddHours(19).AddMinutes(44), bear.Start);
        Assert.AreEqual(Wed.AddHours(19).AddMinutes(59), bear.End);
        Assert.AreEqual("20:59–22:59", Item(s, "pvp").Usual, "the usual hours move too");

        _now = Wed.AddDays(1).AddHours(4).AddMinutes(59);
        Assert.AreEqual(new DateOnly(2026, 11, 4), svc.Snapshot().GameDay, "04:59 is still Wednesday's game day");
        _now = Wed.AddDays(1).AddHours(5);
        Assert.AreEqual(new DateOnly(2026, 11, 5), svc.Snapshot().GameDay);
    }

    [TestMethod]
    public void AUtcMinus5Pc_StartsMondaysGameDayAndTheWeekOnSundayAt2300()
    {
        _now = Sun.AddHours(22).AddMinutes(30);
        var svc = Service(Pc(-5));
        svc.CompleteReplay([]);
        var s = svc.Snapshot();
        Assert.AreEqual(TimeSpan.FromHours(-8), svc.Shift);
        Assert.AreEqual(Sun.AddHours(23), s.NextWeeklyReset, "Monday 07:00 at UTC+3 is Sunday 23:00 at UTC-5");
        Assert.AreEqual(DayOfWeek.Sunday, s.NextWeeklyReset.DayOfWeek);
        Assert.AreEqual(Sun.AddHours(23), s.NextDailyReset);
        Assert.AreEqual(DayOfWeek.Sunday, s.GameDay.DayOfWeek);
        Assert.IsFalse(Item(s, "abyssal").Today, "Abyssal is Monday, Wednesday and Friday");

        _now = Sun.AddHours(23).AddMinutes(30);
        s = svc.Snapshot();
        Assert.AreEqual(DayOfWeek.Monday, s.GameDay.DayOfWeek, "the game day keeps its weekday name");
        Assert.IsTrue(Item(s, "abyssal").Today);
        Assert.AreEqual(Sun.AddDays(7).AddHours(23), s.NextWeeklyReset);
        Assert.AreEqual(Sun.AddDays(1).AddHours(23), s.NextDailyReset);
        // Monday's 21:44 raccoon (UTC+3) is Monday 13:44 here
        Assert.AreEqual(Sun.AddDays(1).AddHours(13).AddMinutes(44), Item(s, "bear").Windows!.Single().Start);
    }

    [TestMethod]
    public void AUtcPlus9Pc_KeepsTheNightWindowsOnTheirGameDay()
    {
        _now = Wed.AddHours(14);
        var svc = Service(Pc(9));
        svc.CompleteReplay([]);
        var s = svc.Snapshot();
        Assert.AreEqual(TimeSpan.FromHours(6), svc.Shift);
        Assert.AreEqual(Wed.AddDays(1).AddHours(13), s.NextDailyReset);
        Assert.AreEqual(new DateOnly(2026, 11, 4), s.GameDay);
        Assert.AreEqual(Wed.AddDays(1).AddHours(3).AddMinutes(44), Item(s, "bear").Windows!.Single().Start, "21:44 + 6 h, the next calendar day");
        CollectionAssert.AreEqual(
            new[] { Wed.AddHours(19), Wed.AddHours(21), Wed.AddDays(1).AddHours(2), Wed.AddDays(1).AddHours(6), Wed.AddDays(1).AddHours(8) },
            Item(s, "wboss").Windows!.Select(w => w.Start).ToArray(),
            "13:00, 15:00, 20:00, then the night's 00:00 and 02:00, all before the next 13:00 reset");
    }

    [TestMethod]
    public void ALearnedShift_FromTheDailyUpdate_OverridesTheOffsetGuess_AndSurvivesASaveAndLoad()
    {
        // a UTC+1 PC guesses -2 h, but the server's daily update came at 06:00:04: the shift is -1 h
        _now = Wed.AddHours(12);
        var svc = Service(Pc(1));
        Assert.AreEqual(TimeSpan.FromHours(-2), svc.Shift);
        svc.CompleteReplay([_log.Reset(Wed.AddHours(6).AddSeconds(4))]);
        Assert.AreEqual(TimeSpan.FromHours(-1), svc.LearnedShift);
        Assert.AreEqual(TimeSpan.FromHours(-1), svc.Shift);
        var s = svc.Snapshot();
        Assert.AreEqual(Wed.AddDays(1).AddHours(6), s.NextDailyReset);
        Assert.AreEqual(Wed.AddHours(20).AddMinutes(44), Item(s, "bear").Windows!.Single().Start);

        var saved = svc.TakeStateToSave();
        Assert.IsNotNull(saved);
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(saved), "\"learnedResetUtcMinutes\":300", "05:00 UTC");
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(svc.TodayFile()), "\"shiftMinutes\":-60");

        _now = Wed.AddHours(12).AddMinutes(5);
        var again = Service(Pc(1), saved);
        Assert.AreEqual(TimeSpan.FromHours(-1), again.Shift, "before any log is replayed");
        Assert.IsFalse(again.Recounted, "counted with the same shift");
        again.CompleteReplay([]);
        Assert.AreEqual(Wed.AddDays(1).AddHours(6), again.Snapshot().NextDailyReset);
    }

    [TestMethod]
    public void ALearnedShift_FarWest_KeepsTheGameDaysWeekdayName()
    {
        // a UTC-10 PC: the 04:00 UTC daily update shows at 18:00 the evening before; the shift is -13 h, not +11 h
        _now = Sun.AddHours(18).AddMinutes(30);
        var svc = Service(Pc(-10));
        svc.CompleteReplay([_log.Reset(Sun.AddHours(18).AddSeconds(2))]);
        Assert.AreEqual(TimeSpan.FromHours(-13), svc.Shift);
        var s = svc.Snapshot();
        Assert.AreEqual(DayOfWeek.Monday, s.GameDay.DayOfWeek);
        Assert.AreEqual(Sun.AddHours(18), s.NextWeeklyReset.AddDays(-7));
    }

    [TestMethod]
    public void ALearnedShiftThatChanges_AtStartUp_RecountsOnce_AndKeepsTheStoredStacks()
    {
        // yesterday's state, counted at the file's own times: Arena 2/5, 7 train requests stored
        _now = Wed.AddHours(20);
        var first = Service(null);
        first.CompleteReplay([]);
        first.Feed(ArenaRuns(Wed.AddHours(19)));
        Assert.IsTrue(first.SetStack("train", 7));
        var state = first.TakeStateToSave()!;

        _now = Wed.AddHours(20).AddMinutes(30);
        var svc = Service(null, state);
        Assert.AreEqual(0, svc.Recounts, "same data file, same shift");
        // the replay holds this morning's daily update an hour early (another server region) and the same two runs
        svc.CompleteReplay([_log.Reset(Wed.AddHours(6).AddSeconds(4)), .. ArenaRuns(Wed.AddHours(19))]);
        Assert.AreEqual(1, svc.Recounts);
        Assert.AreEqual(TimeSpan.FromHours(-1), svc.Shift);
        var s = svc.Snapshot();
        Assert.AreEqual(2, Item(s, "arena").Count, "recounted, not doubled");
        Assert.AreEqual(7, Item(s, "train").Stack, "the stored requests stay");

        // the next morning's update says the same: no second recount
        _now = Wed.AddDays(1).AddHours(6).AddMinutes(1);
        svc.Feed([_log.Reset(Wed.AddDays(1).AddHours(6).AddSeconds(10))]);
        Assert.AreEqual(1, svc.Recounts);
    }

    [TestMethod]
    public void ALearnedShiftThatChanges_WhileRunning_RecountsOnce_AndKeepsTheStoredStacks()
    {
        _now = Wed.AddHours(20);
        var svc = Service(null);
        svc.CompleteReplay(ArenaRuns(Wed.AddHours(19)));
        Assert.IsTrue(svc.SetStack("train", 5));
        Assert.AreEqual(2, Item(svc.Snapshot(), "arena").Count);

        // the daily update arrives live at 06:00 instead of 07:00
        _now = Wed.AddDays(1).AddHours(6).AddSeconds(5);
        svc.Feed([_log.Reset(Wed.AddDays(1).AddHours(6).AddSeconds(4))]);
        Assert.AreEqual(1, svc.Recounts);
        Assert.AreEqual(TimeSpan.FromHours(-1), svc.Shift);
        var s = svc.Snapshot();
        Assert.AreEqual(new DateOnly(2026, 11, 5), s.GameDay, "Thursday began at 06:00");
        Assert.AreEqual(8, Item(s, "train").Stack, "the 5 stored requests stay, and the 06:00 reset added 3");

        var state = DailyService.Deserialize(svc.TakeStateToSave()!)!;
        Assert.AreEqual(2, state.Progress.Single(p => p.Item == "arena").Count, "Wednesday's runs recounted once");
        Assert.AreEqual(3 * 60, state.LearnedResetUtcMinutes, "06:00 at the file's +03:00 is 03:00 UTC");

        svc.Feed([_log.Reset(Wed.AddDays(1).AddHours(6).AddMinutes(1))]);
        Assert.AreEqual(1, svc.Recounts, "the same shift again changes nothing");
        svc.Feed([_log.Reset(Wed.AddDays(1).AddHours(9).AddMinutes(37))]);
        Assert.AreEqual((1, TimeSpan.FromHours(-1)), (svc.Recounts, svc.Shift), "09:37 is no reset moment: ignored");
    }

    [TestMethod]
    public void ADaylightSavingChange_AcrossTheWeeklyMidnight_KeepsTheWeeksCounts()
    {
        // a US Eastern PC: UTC-4 until Sunday 2026-11-01 02:00, then UTC-5; the shift goes from -7 h to -8 h, and the
        // weekly reset from Monday 00:00 to Sunday 23:00 (review 2026-11-08: the week's key moved and Honor War read 0)
        static TimeSpan Eastern(DateTime t) => TimeSpan.FromHours(t < new DateTime(2026, 11, 1, 2, 0, 0) ? -4 : -5);
        var monday = new DateTime(2026, 10, 26, 18, 20, 0);
        _now = monday.AddMinutes(25);
        var first = Service(Eastern);
        first.CompleteReplay([]);
        Assert.AreEqual(TimeSpan.FromHours(-7), first.Shift);
        first.Feed([
            _log.Push(monday, "honor_war_network", "notify_honor_war_stage", true),
            _log.Push(monday.AddMinutes(10), "honor_war_network", "notify_honor_war_stage", false),
            _log.Push(monday.AddMinutes(20), "honor_war_network", "notify_honor_war_stage", true)]);
        Assert.AreEqual(3, Item(first.Snapshot(), "honor").Count);
        var state = first.TakeStateToSave()!;

        // Sunday morning, after the change; the replayed log only begins on Thursday
        _now = new DateTime(2026, 11, 1, 10, 0, 0);
        var svc = Service(Eastern, state);
        svc.CompleteReplay([_log.Show(new DateTime(2026, 10, 29, 12, 0, 0), "UI_ARENA_VIEW")]);
        Assert.AreEqual(TimeSpan.FromHours(-8), svc.Shift);
        Assert.AreEqual(1, svc.Recounts, "a new shift is a recount");
        var s = svc.Snapshot();
        Assert.AreEqual(3, Item(s, "honor").Count, "the week's rounds keep their week (older than the replayed log)");
        Assert.AreEqual(new DateTime(2026, 11, 1, 23, 0, 0), s.NextWeeklyReset);
    }

    [TestMethod]
    public void ALearnedDailyUpdate_FollowsADataFileReBasedToAnotherOffset()
    {
        // a UTC+3 PC on a server whose daily update comes at 06:00 local (03:00 UTC)
        _now = Wed.AddHours(12);
        var svc = Service(Pc(3));
        svc.CompleteReplay([_log.Reset(Wed.AddHours(6).AddSeconds(4))]);
        Assert.AreEqual(TimeSpan.FromHours(-1), svc.Shift);
        var saved = svc.TakeStateToSave()!;

        // the data file now says its times are UTC: daily and weekly 04:00, the same server moment as 07:00 at +03:00
        var c = Data.Catalog();
        var utcFile = c with
        {
            TimesUtcOffset = "+00:00",
            Resets = c.Resets with { Daily = TimeSpan.FromHours(4), Weekly = TimeSpan.FromHours(4), Calendar = TimeSpan.FromHours(-1) },
        };
        _now = Wed.AddHours(12).AddMinutes(5);
        var again = Service(Pc(3), saved, utcFile);
        Assert.AreEqual(TimeSpan.FromHours(2), again.Shift, "03:00 UTC is 06:00 here, 04:00 + 2 h");
        again.CompleteReplay([]);
        Assert.AreEqual(Wed.AddDays(1).AddHours(6), again.Snapshot().NextDailyReset, "still the learned 06:00, not 07:00 or 03:00");

        // and a PC moved to UTC+1 sees the same server moment at 04:00 without a new update
        var moved = Service(Pc(1), saved, utcFile);
        moved.CompleteReplay([]);
        Assert.AreEqual(Wed.AddDays(1).AddHours(4), moved.Snapshot().NextDailyReset);
    }

    [TestMethod]
    public void AStateFromBeforeTimeShifts_LoadsAndKeepsItsCounts()
    {
        _now = Wed.AddHours(20);
        var old = """
            {"schemaVersion":1,"watermark":"2026-11-04T19:30:00","catalog":null,"progress":[],"overrides":[],
             "stacks":[{"item":"train","value":4,"asOf":"2026-11-04T19:00:00"}]}
            """u8.ToArray();
        var state = DailyService.Deserialize(old);
        Assert.IsNotNull(state);
        Assert.IsNull(state.LearnedResetUtcMinutes);
        var svc = Service(null, old);
        Assert.IsNull(svc.StateProblem);
        Assert.IsNull(svc.LearnedShift);
        svc.CompleteReplay([]);
        Assert.AreEqual(4, Item(svc.Snapshot(), "train").Stack);
    }

    [TestMethod]
    public void TheCatalog_ReadsAndChecksTimesUtcOffset_AndShiftsEveryTime()
    {
        var c = Data.Catalog();
        Assert.AreEqual("+03:00", c.TimesUtcOffset);
        Assert.AreEqual(TimeSpan.FromHours(3), c.TimesOffset);
        Assert.AreSame(c, c.Shifted(TimeSpan.Zero));

        var minus8 = c.Shifted(TimeSpan.FromHours(-8));
        Assert.AreEqual(TimeSpan.FromHours(-1), minus8.Resets.Daily, "kept before midnight so Monday's game day stays Monday");
        Assert.AreEqual(TimeSpan.FromHours(-6), minus8.Resets.Calendar);
        Assert.AreEqual((DayOfWeek.Monday, TimeSpan.FromHours(-1)), (minus8.Resets.WeeklyDay, minus8.Resets.Weekly), "Monday -01:00, Sunday 23:00; the week keeps its Monday");
        Assert.AreEqual("13:44", minus8.Items.Single(i => i.Id == "bear").Windows.Single().At);
        Assert.AreEqual(c.Fingerprint + "@-480", minus8.Fingerprint);
        Assert.AreEqual(c.Fingerprint, minus8.Shifted(TimeSpan.FromHours(8)).Fingerprint, "shifting back gives the file's own");

        static DailyCatalog Parse(string offset) => DailyCatalog.Parse(System.Text.Encoding.UTF8.GetBytes(
            "{ \"schemaVersion\": 1" + (offset.Length > 0 ? ", \"timesUtcOffset\": \"" + offset + "\"" : "") + " }"));

        Assert.AreEqual(TimeSpan.FromHours(3), Parse("").TimesOffset, "absent = +03:00");
        Assert.AreEqual(new TimeSpan(-5, -30, 0), Parse("-05:30").TimesOffset);
        foreach (var bad in new[] { "+3", "03:00", "+15:00", "-13:00", "+03:75", "UTC+3" })
        {
            Assert.ThrowsExactly<InvalidDataException>(() => Parse(bad), bad);
        }
    }

    [TestMethod]
    public void TheDailiesModule_ReadsThePcsTimeZoneInOnePlaceOnly()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Companion.Dailies")))
        {
            dir = dir.Parent;
        }

        Assert.IsNotNull(dir, "the source tree is next to the tests");
        var sep = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(Path.Combine(dir.FullName, "src", "Companion.Dailies"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(sep + "bin" + sep, StringComparison.OrdinalIgnoreCase) && !f.Contains(sep + "obj" + sep, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => Path.GetFileName(f), File.ReadAllText);
        foreach (var token in new[] { "DateTime.Now", "DateTime.Today", "DateTimeOffset.Now", "TimeZoneInfo.FindSystemTimeZoneById" })
        {
            Assert.IsFalse(files.Any(f => f.Value.Contains(token, StringComparison.Ordinal)), token);
        }

        var local = files.Where(f => f.Value.Contains("TimeZoneInfo.Local", StringComparison.Ordinal)).ToList();
        Assert.HasCount(1, local);
        Assert.AreEqual("DailyService.cs", local[0].Key);
        Assert.AreEqual(1, local[0].Value.Split("TimeZoneInfo.Local").Length - 1, "only DailyService.PcUtcOffset");
    }

    /// <summary>Two Arena runs (results screens with the Arena screen open) from <paramref name="t"/>.</summary>
    private LogEvent[] ArenaRuns(DateTime t) =>
        [_log.Show(t, "UI_ARENA_VIEW"), _log.Settle(t.AddSeconds(8)), _log.Settle(t.AddSeconds(9))];
}
