using Companion.App.Tray;

namespace Companion.App.Tests;

/// <summary>Roadmap A ("while you were away") and C (the stats page): what the journal says the companion did.</summary>
[TestClass]
public sealed class JournalStatsTests
{
    private const long Clams = 1064020;
    private const long Thyme = 20330050;
    private const long Essence = 20330054;

    // the real journal's shape (2026-10-23, the Sauteed Clams goal), trimmed to the fields the stats read
    private static readonly string[] Lines =
    [
        """{"utc":"2026-10-23T14:50:54.000Z","kind":"stock","fields":{"event":"craftLoop.round","itemId":1064020,"produced":10}}""",
        """{"utc":"2026-10-23T14:53:16.000Z","kind":"stock","fields":{"event":"craftLoop.round","itemId":1064020,"produced":33}}""",
        """{"utc":"2026-10-23T14:55:33.000Z","kind":"stock","fields":{"event":"craftLoop.round","itemId":1064020,"produced":0}}""",
        """{"utc":"2026-10-23T14:58:24.000Z","kind":"stock","fields":{"event":"gather.queued","itemId":20330050,"qty":340}}""",
        """{"utc":"2026-10-23T14:58:24.100Z","kind":"stock","fields":{"event":"gather.queued","itemId":20330054,"qty":200}}""",
        """{"utc":"2026-10-23T14:58:24.200Z","kind":"stock","fields":{"event":"gather.clamped","itemId":20330050,"typed":340,"accepted":200}}""",
        """{"utc":"2026-10-23T14:58:24.300Z","kind":"stock","fields":{"event":"gather.clamped","itemId":20330054,"typed":200,"accepted":60}}""",
        """{"utc":"2026-10-23T15:17:36.000Z","kind":"stock","fields":{"event":"plan.gather","round":2,"code":"gather.done","gathered":2}}""",
        """{"utc":"2026-10-23T15:20:00.000Z","kind":"decision","fields":{"event":"plan.deferred","round":3,"cause":"round.deferred:unreachable.CoverRejected"}}""",
        """{"utc":"2026-10-23T15:56:00.000Z","kind":"decision","fields":{"event":"gear.notice","code":"dismissed"}}""",
        """{"utc":"2026-10-23T15:57:00.000Z","kind":"halt","fields":{"code":"BudgetExceeded","subject":"reset"}}""",
        """{"utc":"2026-10-23T15:58:00.000Z","kind":"jobFinished","fields":{"job":"Craft","state":"Succeeded","code":"plan.done"}}""",
        "not json at all",
        """{"utc":"2026-10-24T09:00:00.000Z","kind":"stock","fields":{"event":"craftLoop.round","itemId":1064020,"produced":5}}""",
    ];

    private static string? Name(long id) => id switch { Clams => "sauteed clams", Thyme => "thyme", Essence => "wisdom essence", _ => null };

    [TestMethod]
    public void TheSpan_CountsCraftsTripsWhatTheGameAcceptedPausesGearStopsAndJobs()
    {
        var s = JournalStats.From(Lines, DateTimeOffset.Parse("2026-10-23T14:00:00Z"), DateTimeOffset.Parse("2026-10-24T00:00:00Z"));

        Assert.AreEqual(43L, s.MadeTotal, "the next day's 5 are outside the span");
        Assert.AreEqual(1, s.Trips);
        Assert.AreEqual(200L, s.Gathered[Thyme], "what the game accepted, not what was typed");
        Assert.AreEqual(60L, s.Gathered[Essence]);
        Assert.AreEqual(1, s.Pauses);
        Assert.AreEqual(1, s.GearNotices.Count);
        Assert.AreEqual(1, s.SafetyStops);
        Assert.AreEqual(1, s.JobsSucceeded);
        Assert.AreEqual(TimeSpan.FromSeconds(142), s.CraftingTime, "between rounds that made something, less than 10 minutes apart");
    }

    [TestMethod]
    public void TheAwaySummary_IsOnePlainParagraph_AndNothingWhenNothingHappened()
    {
        var text = JournalStats.From(Lines, DateTimeOffset.Parse("2026-10-23T14:00:00Z"), DateTimeOffset.Parse("2026-10-24T00:00:00Z")).Summary(Name)!;

        StringAssert.StartsWith(text, "Made 43 sauteed clams, 1 gathering trip (thyme 200, wisdom essence 60), 1 pause, gear worn out at ");
        StringAssert.Contains(text, "1 safety stop, last job: Craft (plan.done).");
        Assert.IsNull(JournalStats.From(Lines, DateTimeOffset.Parse("2026-10-22T00:00:00Z"), DateTimeOffset.Parse("2026-10-22T01:00:00Z")).Summary(Name));
    }

    [TestMethod]
    public void TheStatsPage_CountsEachLocalDayInOnePass()
    {
        var days = JournalStats.ByDay(Lines, new DateTime(2026, 10, 20));

        var total = days.Values.Sum(d => d.MadeTotal);
        Assert.AreEqual(48L, total);
        Assert.IsTrue(days.Count >= 1);
    }
}
