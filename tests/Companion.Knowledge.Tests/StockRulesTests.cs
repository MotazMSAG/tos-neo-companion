using System.Text.Json.Nodes;
using Companion.Contracts.Knowledge;
using static Companion.Knowledge.Tests.Fx;

namespace Companion.Knowledge.Tests;

[TestClass]
public sealed class StockRulesTests
{
    private TestEnv _env = null!;
    private ICharacterKnowledge _k = null!;

    [TestInitialize]
    public void Init()
    {
        _env = new TestEnv();
        _k = _env.NewStore().Profile;
    }

    [TestCleanup]
    public void Cleanup() => _env.Dispose();

    private StockEntry Entry(string item) => _k.GetStock().Items[I(item)];

    // ---------- observations ----------

    [TestMethod]
    public void UnseenItemsStayUnknownNeverZero()
    {
        _k.ApplyObservations([Obs("fake ore", 5, 100)]);
        var items = _k.GetStock().Items;
        Assert.HasCount(1, items);
        Assert.IsFalse(items.ContainsKey(I("fake wood")));
        Assert.AreEqual(StockSource.Observed, items[I("fake ore")].Source);
    }

    [TestMethod]
    public void NewerObservationWinsAndOlderIsIgnored()
    {
        _k.ApplyObservations([Obs("fake ore", 5, 100)]);
        _k.ApplyObservations([Obs("fake ore", 7, 200)]);
        Assert.AreEqual(7L, Entry("fake ore").Count);
        _k.ApplyObservations([Obs("fake ore", 1, 150, at: T0.AddDays(5))]);
        Assert.AreEqual(7L, Entry("fake ore").Count, "log offset decides within one log generation, not wall time");
    }

    [TestMethod]
    public void NewerLogGenerationWins()
    {
        _k.ApplyObservations([Obs("fake ore", 5, 9000)]);
        _k.ApplyObservations([Obs("fake ore", 2, 10, file: File2)]);
        Assert.AreEqual(2L, Entry("fake ore").Count);
        _k.ApplyObservations([Obs("fake ore", 9, 9999)]);
        Assert.AreEqual(2L, Entry("fake ore").Count, "older generation ignored");
    }

    [TestMethod]
    public void SamePositionPrecedenceUserThenExactThenConfidence()
    {
        _k.ApplyObservations([Obs("fake ore", null, 100, new ValueRange(100, 199), ObservationKind.BagPartial, confidence: 0.99)]);
        _k.ApplyObservations([Obs("fake ore", 150, 100, kind: ObservationKind.CraftViewPair, confidence: 0.5)]);
        Assert.AreEqual(150L, Entry("fake ore").Count, "exact beats range at the same position");
        _k.ApplyObservations([Obs("fake ore", 160, 100, kind: ObservationKind.CraftViewPair, confidence: 0.4)]);
        Assert.AreEqual(150L, Entry("fake ore").Count, "lower confidence loses");
        _k.ApplyObservations([Obs("fake ore", 170, 100, kind: ObservationKind.CraftViewPair, confidence: 0.6)]);
        Assert.AreEqual(170L, Entry("fake ore").Count, "higher confidence wins");
        _k.ApplyObservations([Obs("fake ore", 3, 100, kind: ObservationKind.UserEntered, confidence: 0.1)]);
        Assert.AreEqual(3L, Entry("fake ore").Count, "the user wins at the same position");
        Assert.AreEqual(StockSource.UserEntered, Entry("fake ore").Source);
        _k.ApplyObservations([Obs("fake ore", 4, 100, kind: ObservationKind.CraftViewPair, confidence: 1.0)]);
        Assert.AreEqual(3L, Entry("fake ore").Count);
        _k.ApplyObservations([Obs("fake ore", 4, 101, kind: ObservationKind.CraftViewPair)]);
        Assert.AreEqual(4L, Entry("fake ore").Count, "a later screen reading replaces the user's number");
    }

    [TestMethod]
    public void RangeContainingTheExactCountKeepsItAndDriftReplacesIt()
    {
        _k.ApplyObservations([Obs("fake ore", 1250, 100)]);
        _k.ApplyObservations([Obs("fake ore", null, 200, new ValueRange(1200, 1299), ObservationKind.BagPartial)]);
        var e = Entry("fake ore");
        Assert.AreEqual(1250L, e.Count);
        Assert.AreEqual(new ValueRange(1200, 1299), e.Range);
        Assert.AreEqual(P(200), e.ObservedAtLog);

        _k.ApplyObservations([Obs("fake ore", null, 300, new ValueRange(1300, 1399), ObservationKind.BagPartial)]);
        e = Entry("fake ore");
        Assert.IsNull(e.Count, "drift: the old exact count is outside the new range");
        Assert.AreEqual(new ValueRange(1300, 1399), e.Range);
    }

    [TestMethod]
    public void InvalidObservationsAreIgnored()
    {
        _k.ApplyObservations(
        [
            Obs("fake a", -1, 100),
            Obs("fake b", null, 100, new ValueRange(5, 2)),
            Obs("fake c", null, 100),
            Obs("fake d", 50, 100, new ValueRange(1, 10)),
            Obs("fake e", 1, 100) with { Item = new ItemKey(" Not Normalised ") },
            Obs("fake f", 1, 100) with { AtLog = null! },
            Obs("fake g", 1, 100),
        ]);
        CollectionAssert.AreEqual(new[] { I("fake g") }, _k.GetStock().Items.Keys.ToArray());
    }

    // ---------- derived deltas ----------

    [TestMethod]
    public void DerivedBeforeTheObservationIsSkipped()
    {
        _k.ApplyObservations([Obs("fake ingot", 10, 500)]);
        Assert.IsFalse(_k.ApplyDerived(new DerivedStockDelta(I("fake ingot"), 3, P(400), "craft.tick")));
        Assert.IsFalse(_k.ApplyDerived(new DerivedStockDelta(I("fake ingot"), 3, P(500), "craft.tick")));
        Assert.AreEqual(10L, Entry("fake ingot").Count);
        Assert.AreEqual(0L, _k.GetStock().LastAppliedOffset);
    }

    [TestMethod]
    public void DerivedAfterTheObservationAppliesAndMovesTheWatermark()
    {
        _k.ApplyObservations([Obs("fake ingot", 10, 500), Obs("fake ore", 20, 500)]);
        Assert.IsTrue(_k.ApplyDerived(new DerivedStockDelta(I("fake ingot"), 3, P(800), "craft.tick")));
        Assert.IsTrue(_k.ApplyDerived(new DerivedStockDelta(I("fake ore"), -6, P(800), "craft.tick")), "one craft event carries several item deltas");
        var view = _k.GetStock();
        Assert.AreEqual(13L, view.Items[I("fake ingot")].Count);
        Assert.AreEqual(14L, view.Items[I("fake ore")].Count);
        Assert.AreEqual(StockSource.Derived, view.Items[I("fake ingot")].Source);
        Assert.AreEqual(T0.AddSeconds(500), view.Items[I("fake ingot")].ObservedAtUtc, "time of the underlying observation is kept");
        Assert.AreEqual(800L, view.LastAppliedOffset);
        Assert.AreEqual(File1, view.LogFile);

        Assert.IsFalse(_k.ApplyDerived(new DerivedStockDelta(I("fake ingot"), 3, P(800), "craft.tick")), "replay");
        Assert.AreEqual(13L, Entry("fake ingot").Count);
    }

    [TestMethod]
    public void DerivedOlderThanTheWatermarkIsSkipped()
    {
        _k.ApplyObservations([Obs("fake ingot", 10, 500), Obs("fake ore", 20, 500)]);
        Assert.IsTrue(_k.ApplyDerived(new DerivedStockDelta(I("fake ingot"), 1, P(900), "craft.tick")));
        Assert.IsFalse(_k.ApplyDerived(new DerivedStockDelta(I("fake ore"), -2, P(700), "craft.tick")));
        Assert.AreEqual(20L, Entry("fake ore").Count);
        Assert.AreEqual(900L, _k.GetStock().LastAppliedOffset);
    }

    [TestMethod]
    public void DerivedForAnUnknownItemIsSkippedAndItStaysUnknown()
    {
        Assert.IsFalse(_k.ApplyDerived(new DerivedStockDelta(I("fake ingot"), 3, P(800), "craft.tick")));
        Assert.IsEmpty(_k.GetStock().Items);
    }

    [TestMethod]
    public void RotationMakesDerivedStockStale()
    {
        _k.ApplyObservations([Obs("fake ingot", 10, 500)]);
        Assert.IsTrue(_k.ApplyDerived(new DerivedStockDelta(I("fake ingot"), 2, P(800), "craft.tick")));
        // New log generation: offsets restart, so the event cannot be ordered against the old observation.
        Assert.IsFalse(_k.ApplyDerived(new DerivedStockDelta(I("fake ingot"), 2, P(50, File2), "craft.tick")));
        var e = Entry("fake ingot");
        Assert.AreEqual(12L, e.Count);
        Assert.IsTrue(e.Stale);

        // A fresh observation in the new generation re-anchors the item; derived deltas apply again.
        _k.ApplyObservations([Obs("fake ingot", 30, 100, file: File2)]);
        Assert.IsFalse(Entry("fake ingot").Stale);
        Assert.IsTrue(_k.ApplyDerived(new DerivedStockDelta(I("fake ingot"), 1, P(120, File2), "craft.tick")));
        Assert.AreEqual(31L, Entry("fake ingot").Count);
        Assert.AreEqual(File2, _k.GetStock().LogFile);
        Assert.AreEqual(120L, _k.GetStock().LastAppliedOffset);
    }

    [TestMethod]
    public void NegativeResultClampsToZeroAndMarksStale()
    {
        _k.ApplyObservations([Obs("fake ore", 2, 500)]);
        Assert.IsTrue(_k.ApplyDerived(new DerivedStockDelta(I("fake ore"), -5, P(600), "craft.tick")));
        Assert.AreEqual(0L, Entry("fake ore").Count);
        Assert.IsTrue(Entry("fake ore").Stale);
    }

    [TestMethod]
    public void RangeOnlyEntriesShiftWithDeltas()
    {
        _k.ApplyObservations([Obs("fake ore", null, 500, new ValueRange(1000, 1099), ObservationKind.BagPartial)]);
        Assert.IsTrue(_k.ApplyDerived(new DerivedStockDelta(I("fake ore"), -10, P(600), "craft.tick")));
        Assert.AreEqual(new ValueRange(990, 1089), Entry("fake ore").Range);
        Assert.IsNull(Entry("fake ore").Count);
    }

    [TestMethod]
    public void LastAppliedOffsetIsPersistedInTheSameFileAsTheStock()
    {
        _k.ApplyObservations([Obs("fake ingot", 10, 500)]);
        _k.ApplyDerived(new DerivedStockDelta(I("fake ingot"), 3, P(800), "craft.tick"));
        _k.Save();
        var json = JsonNode.Parse(File.ReadAllText(_env.CharacterFile(DefaultKey)))!;
        Assert.AreEqual(800L, (long)json["stock"]!["lastAppliedOffset"]!);
        Assert.AreEqual(13L, (long)json["stock"]!["items"]![0]!["count"]!);

        var reopened = _env.NewStore().Profile;
        Assert.AreEqual(800L, reopened.GetStock().LastAppliedOffset);
        Assert.IsFalse(reopened.ApplyDerived(new DerivedStockDelta(I("fake ingot"), 3, P(800), "craft.tick")), "no double application after a restart");
        Assert.AreEqual(13L, reopened.GetStock().Items[I("fake ingot")].Count);
    }

    [TestMethod]
    public void AVolatileObservation_KeepsItsFreshWindow_ThroughASaveAndANewerReadingWithoutOne()
    {
        // 2026-10-15: the Stock tab's Homestead goods (shared with a homestead partner) and the item id they were named with
        var home = Obs("fake jam", 25, 500, kind: ObservationKind.StockTab) with { ItemId = 1_051_000, FreshUntilUtc = T0.AddSeconds(500).AddMinutes(10) };
        _k.ApplyObservations([home]);
        Assert.AreEqual((T0.AddSeconds(500).AddMinutes(10), (long?)1_051_000), (Entry("fake jam").FreshUntilUtc, Entry("fake jam").ItemId));
        Assert.IsFalse(Entry("fake jam").IsStaleAt(T0.AddSeconds(500).AddMinutes(9)));
        Assert.IsTrue(Entry("fake jam").IsStaleAt(T0.AddSeconds(500).AddMinutes(11)));
        _k.Save();
        var json = JsonNode.Parse(File.ReadAllText(_env.CharacterFile(DefaultKey)))!;
        Assert.IsNotNull(json["stock"]!["items"]![0]!["freshUntilUtc"]);
        var reopened = _env.NewStore().Profile;
        Assert.AreEqual(home.FreshUntilUtc, reopened.GetStock().Items[I("fake jam")].FreshUntilUtc);

        // a window before its own reading is invalid (ignored); a newer reading without a window clears it
        _k.ApplyObservations([home with { Count = 1, AtLog = P(600), AtUtc = T0.AddSeconds(600), FreshUntilUtc = T0 }]);
        Assert.AreEqual(25L, Entry("fake jam").Count);
        _k.ApplyObservations([Obs("fake jam", 30, 700, kind: ObservationKind.StockTab) with { ItemId = 1_051_000 }]);
        Assert.AreEqual((30L, (DateTimeOffset?)null), (Entry("fake jam").Count!.Value, Entry("fake jam").FreshUntilUtc));
    }

    // ---------- stale marks ----------

    [TestMethod]
    public void AllStockMarkMakesEarlierEntriesStaleAndLaterObservationsFresh()
    {
        _k.ApplyObservations([Obs("fake ore", 5, 100), Obs("fake wood", 6, 100)]);
        var mark = T0.AddSeconds(300);
        _k.MarkStale(StaleScope.AllStock, null, "user.foreground", mark);
        var view = _k.GetStock();
        Assert.AreEqual(mark, view.StaleSince);
        Assert.IsTrue(view.Items.Values.All(e => e.Stale));

        _k.ApplyObservations([Obs("fake ore", 5, 400)]);
        Assert.IsFalse(Entry("fake ore").Stale);
        Assert.IsTrue(Entry("fake wood").Stale);

        // A reading captured before the mark is stale on arrival.
        _k.ApplyObservations([Obs("fake gem", 1, 200)]);
        Assert.IsTrue(Entry("fake gem").Stale);

        // An older mark does not move StaleSince back.
        _k.MarkStale(StaleScope.AllStock, null, "log.rotated", T0);
        Assert.AreEqual(mark, _k.GetStock().StaleSince);
        Assert.IsFalse(Entry("fake ore").Stale);
    }

    [TestMethod]
    public void ItemAndFoodScopes()
    {
        _k.ApplyObservations([Obs("fake bread", 5, 100), Obs("fake ore", 6, 100)]);
        _k.MarkStale(StaleScope.Item, I("fake ore"), "test.item", T0.AddHours(1));
        Assert.IsTrue(Entry("fake ore").Stale);
        Assert.IsFalse(Entry("fake bread").Stale);
        Assert.IsNull(_k.GetStock().StaleSince);

        _k.MarkStale(StaleScope.Food, I("fake bread"), "autoeat", T0.AddHours(1));
        Assert.IsTrue(Entry("fake bread").Stale);

        _k.ApplyObservations([Obs("fake bread", 5, 9000, at: T0.AddHours(2)), Obs("fake ore", 6, 9000, at: T0.AddHours(2))]);
        _k.MarkStale(StaleScope.Food, null, "autoeat", T0.AddHours(3));
        Assert.IsTrue(Entry("fake bread").Stale && Entry("fake ore").Stale, "unnamed food: every entry may have been eaten");
        Assert.IsNull(_k.GetStock().StaleSince, "Food is not an AllStock mark");
    }

    [TestMethod]
    public void MarkOlderThanTheObservationDoesNotStaleIt()
    {
        _k.ApplyObservations([Obs("fake ore", 5, 100)]);
        _k.MarkStale(StaleScope.Item, I("fake ore"), "test.item", T0);
        Assert.IsFalse(Entry("fake ore").Stale);
    }

    [TestMethod]
    public void MarkStaleValidatesArguments()
    {
        Assert.ThrowsExactly<ArgumentException>(() => _k.MarkStale(StaleScope.Item, null, "x", T0));
        Assert.ThrowsExactly<ArgumentException>(() => _k.MarkStale(StaleScope.AllStock, null, "", T0));
        Assert.ThrowsExactly<ArgumentException>(() => _k.MarkStale(StaleScope.Item, new ItemKey("Bad  Key"), "x", T0));
    }

    // ---------- recipes ----------

    [TestMethod]
    public void UpsertReplacesByIdAndListsInIdOrder()
    {
        _k.UpsertRecipe(R("fake-b", "fake ingot", 1, ("fake ore", 3)));
        _k.UpsertRecipe(R("fake-a", "fake plank", 2, ("fake wood", 1)));
        _k.UpsertRecipe(R("fake-b", "fake ingot", 2, ("fake ore", 3)));
        var list = _k.GetRecipes();
        CollectionAssert.AreEqual(new[] { "fake-a", "fake-b" }, list.Select(r => r.Id).ToArray());
        Assert.AreEqual(2, list[1].OutputQty);
    }

    [TestMethod]
    public void VerifiedRecipeReReadIdenticallyStaysVerified()
    {
        _k.UpsertRecipe(R("fake-r", "fake ingot", 1, ("fake ore", 3), ("fake coal", 1)) with { MedianTickCadence = TimeSpan.FromSeconds(2.2) });
        var reread = R("fake-r", "fake ingot", 1, ("fake coal", 1), ("fake ore", 3)) with
        {
            UserVerified = false,
            Confidence = RecipeConfidence.Partial,
            ObservedAtUtc = T0.AddDays(1),
            SourceCapture = "survey/fake-2.png",
        };
        _k.UpsertRecipe(reread);
        var r = _k.GetRecipes().Single();
        Assert.IsTrue(r.UserVerified);
        Assert.AreEqual(RecipeConfidence.Complete, r.Confidence);
        Assert.AreEqual(TimeSpan.FromSeconds(2.2), r.MedianTickCadence, "learned cadence kept");
        Assert.AreEqual(T0.AddDays(1), r.ObservedAtUtc);
        Assert.AreEqual("survey/fake-2.png", r.SourceCapture);
    }

    [TestMethod]
    public void RecipeDriftDropsVerification()
    {
        _k.UpsertRecipe(R("fake-r", "fake ingot", 1, ("fake ore", 3)));
        _k.UpsertRecipe(R("fake-r", "fake ingot", 1, ("fake ore", 4)) with { UserVerified = false });
        var r = _k.GetRecipes().Single();
        Assert.IsFalse(r.UserVerified);
        Assert.AreEqual(4, r.Materials.Single().Qty);
    }

    [TestMethod]
    public void RecipesAreLearnedOnlyFromTheScreenOrTheUser()
    {
        var noSource = R("fake-r", "fake ingot", 1, ("fake ore", 3)) with { SourceCapture = null, UserVerified = false };
        Assert.ThrowsExactly<ArgumentException>(() => _k.UpsertRecipe(noSource));
        _k.UpsertRecipe(noSource with { UserVerified = true });

        var gameFile = Path.Combine(_env.Paths.GameDir, "data", "fake.bytes");
        Assert.ThrowsExactly<ArgumentException>(() => _k.UpsertRecipe(noSource with { SourceCapture = gameFile, UserVerified = true }));
        Assert.ThrowsExactly<ArgumentException>(() => _k.UpsertRecipe(noSource with { SourceCapture = @"survey\..\..\x.png" }));
        Assert.HasCount(1, _k.GetRecipes());
    }

    [TestMethod]
    public void InvalidRecipesAreRefused()
    {
        var ok = R("fake-r", "fake ingot", 1, ("fake ore", 3));
        Recipe[] bad =
        [
            ok with { OutputQty = 0 },
            ok with { Id = " " },
            ok with { Materials = [new MaterialRequirement(I("fake ore"), 0)] },
            ok with { Materials = [new MaterialRequirement(I("fake ore"), 1), new MaterialRequirement(I("fake ore"), 2)] },
            ok with { SuccessRate = 0 },
            ok with { SuccessRate = 1.5 },
            ok with { Product = new ItemKey("Fake Ingot") },
            ok with { Materials = null! },
        ];
        foreach (var r in bad)
        {
            Assert.ThrowsExactly<ArgumentException>(() => _k.UpsertRecipe(r));
        }
        Assert.IsEmpty(_k.GetRecipes());
    }

    // ---------- keep-stock ----------

    [TestMethod]
    public void KeepStockRulesAreValidated()
    {
        _k.SetKeepStockRules([new KeepStockRule(I("fake ore"), 5), new KeepStockRule(I("fake wood"), 0)]);
        Assert.HasCount(2, _k.GetKeepStockRules());
        Assert.ThrowsExactly<ArgumentException>(() => _k.SetKeepStockRules([new KeepStockRule(I("fake ore"), 1), new KeepStockRule(I("fake ore"), 2)]));
        Assert.ThrowsExactly<ArgumentException>(() => _k.SetKeepStockRules([new KeepStockRule(I("fake ore"), -1)]));
        Assert.HasCount(2, _k.GetKeepStockRules());
    }
}
