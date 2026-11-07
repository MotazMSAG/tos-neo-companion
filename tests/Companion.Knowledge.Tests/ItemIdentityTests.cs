using System.Text.Json.Nodes;
using Companion.Contracts.Knowledge;
using static Companion.Knowledge.Tests.Fx;

namespace Companion.Knowledge.Tests;

/// <summary>
/// Schema 2 (2026-10-15): optional game item ids on stock, recipe products and materials, the learned icon items, item
/// identity (the id when both sides know it, else the key), the backward-compatible load of version-1 files, and
/// ForgetRecipes (backup first, then the recipes and the recipe-screen stock).
/// </summary>
[TestClass]
public sealed class ItemIdentityTests
{
    private static readonly string IconA = "icon:" + new string('a', 64);
    private static readonly string IconB = "icon:" + new string('b', 64);

    private TestEnv _env = null!;

    [TestInitialize]
    public void Init() => _env = new TestEnv();

    [TestCleanup]
    public void Cleanup() => _env.Dispose();

    /// <summary>
    /// A version-1 file of the same shape as the live store of 2026-10-15 (fake names and values): stock with log
    /// positions, a stale mark, no log file, recipes with icon-keyed materials, list and favourites sources, a counter.
    /// </summary>
    private static string Version1File(string character) => $$"""
        {
          "schemaVersion": 1,
          "savedUtc": "2026-01-01T13:00:00+00:00",
          "character": "{{character}}",
          "stock": {
            "items": [
              { "item": { "value": "fake ams" }, "count": 0, "source": "observed", "observedAtUtc": "2026-01-01T12:10:00+00:00",
                "observedAtLog": { "file": { "creationUtc": "2026-01-01T00:00:00Z", "fileIndex": 101, "headFingerprint": "fake-head-1" }, "offset": 900 },
                "confidence": 0.413, "coverage": "ability:craft.product", "stale": true },
              { "item": { "value": "{{IconA}}" }, "count": 40, "source": "observed", "observedAtUtc": "2026-01-01T12:10:00+00:00",
                "observedAtLog": { "file": { "creationUtc": "2026-01-01T00:00:00Z", "fileIndex": 101, "headFingerprint": "fake-head-1" }, "offset": 901 },
                "confidence": 0.9, "coverage": "ability:craft.pairs", "stale": false },
              { "item": { "value": "fake typed item" }, "count": 3, "source": "userEntered", "observedAtUtc": "2026-01-01T12:00:00+00:00",
                "confidence": 1, "coverage": "user", "stale": false }
            ],
            "staleSince": "2026-01-01T12:30:00+00:00",
            "lastAppliedOffset": 0
          },
          "recipes": [
            { "id": "fake ams|lifeskill|0", "product": { "value": "fake ams" }, "outputQty": 1, "station": "lifeskill",
              "materials": [ { "item": { "value": "{{IconA}}" }, "qty": 6 }, { "item": { "value": "{{IconB}}" }, "qty": 1 } ],
              "requirements": [], "observedAtUtc": "2026-01-01T12:10:00+00:00", "sourceCapture": "screen:craft.list/31", "userVerified": false, "confidence": "partial" },
            { "id": "fake combo|lifeskill|0", "product": { "value": "fake combo" }, "outputQty": 1, "station": "lifeskill",
              "materials": [ { "item": { "value": "{{IconB}}" }, "qty": 3 } ],
              "requirements": [ "requires:5" ], "observedAtUtc": "2026-01-01T12:11:00+00:00", "sourceCapture": "screen:craft.fav/2", "userVerified": false, "confidence": "complete", "counter": "33/50" }
          ],
          "keepStock": [ { "item": { "value": "fake typed item" }, "min": 2 } ]
        }
        """;

    private void WriteVersion1()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_env.CharacterFile(Key1))!);
        File.WriteAllText(_env.CharacterFile(Key1), Version1File(Key1.Value));
    }

    [TestMethod]
    public void AVersion1File_LoadsAsIs_AndIsWrittenAsVersion2OnTheNextSave()
    {
        WriteVersion1();
        Assert.IsTrue(_env.NewStore().Load().Ok, "a version-1 file is valid, no fallback to a backup");

        var k = _env.NewStore().Profile;
        Assert.AreEqual(Key1, k.Character, "the only knowledge folder is the profile");
        Assert.HasCount(2, k.GetRecipes());
        Assert.HasCount(3, k.GetStock().Items);
        Assert.IsEmpty(k.GetIconItems());
        Assert.IsTrue(k.GetRecipes().All(r => r.ProductItemId is null && r.Materials.All(m => m.ItemId is null)), "no id known yet");
        Assert.IsTrue(k.GetStock().Items.Values.All(e => e.ItemId is null));
        Assert.AreEqual("33/50", k.GetRecipes().Single(r => r.Id == "fake combo|lifeskill|0").Counter);
        Assert.AreEqual(2, k.GetKeepStockRules().Single().Min);
        Assert.AreEqual(T0.AddMinutes(30), k.GetStock().StaleSince);

        // untouched: nothing is written on load
        Assert.AreEqual(1, (int)JsonNode.Parse(File.ReadAllText(_env.CharacterFile(Key1)))!["schemaVersion"]!);

        k.LearnIconItem(new IconItem(IconA, 200101, "Fake Wheat", T0.AddHours(2)));
        k.Save();
        var json = JsonNode.Parse(File.ReadAllText(_env.CharacterFile(Key1)))!;
        Assert.AreEqual(2, (int)json["schemaVersion"]!);
        Assert.AreEqual(IconA, (string)json["iconItems"]![0]!["iconKey"]!);
        Assert.IsNull(json["iconItems"]![0]!["item"], "the item key is derived from the name, not stored");
        Assert.AreEqual(1, (int)JsonNode.Parse(File.ReadAllText(_env.CharacterFile(Key1) + ".bak1"))!["schemaVersion"]!, "the version-1 file is the first backup");

        var back = _env.NewStore().Profile;
        Assert.AreEqual(new IconItem(IconA, 200101, "Fake Wheat", T0.AddHours(2)), back.GetIconItems().Single());
        Assert.AreEqual((40L, (long?)200101), (back.GetStock().Items[I("fake wheat")].Count, back.GetStock().Items[I("fake wheat")].ItemId));
    }

    [TestMethod]
    public void LearningAnIconsItem_MovesItsStockAndRecipeMaterialsToTheItem()
    {
        WriteVersion1();
        var k = _env.NewStore().Profile;
        k.LearnIconItem(new IconItem(IconA, 200101, "  Fake Wheat ", T0.AddHours(2)));

        var stock = k.GetStock().Items;
        Assert.IsFalse(stock.ContainsKey(new ItemKey(IconA)), "the icon key is gone");
        var wheat = stock[I("fake wheat")];
        Assert.AreEqual((40L, (long?)200101, "ability:craft.pairs"), (wheat.Count, wheat.ItemId, wheat.Coverage), "the same reading, now the item's");
        var ams = k.GetRecipes().Single(r => r.Id == "fake ams|lifeskill|0");
        CollectionAssert.AreEqual(
            new[] { new MaterialRequirement(I("fake wheat"), 6, 200101), new MaterialRequirement(new ItemKey(IconB), 1) },
            ams.Materials.ToArray());

        // a newer reading of the item under its name wins over the moved icon reading
        k.ApplyObservations([new StockObservation(I("fake salt"), 7, null, ObservationKind.CraftViewPair, T0.AddHours(3), P(5000), 0.9, "ability:craft.pairs")]);
        k.ApplyObservations([new StockObservation(new ItemKey(IconB), 2, null, ObservationKind.CraftViewPair, T0.AddHours(1), P(1000), 0.9, "ability:craft.pairs")]);
        k.LearnIconItem(new IconItem(IconB, 200102, "Fake Salt", T0.AddHours(4)));
        Assert.AreEqual((7L, (long?)200102), (k.GetStock().Items[I("fake salt")].Count, k.GetStock().Items[I("fake salt")].ItemId));
        Assert.IsFalse(k.GetStock().Items.ContainsKey(new ItemKey(IconB)));
        Assert.AreEqual(new MaterialRequirement(I("fake salt"), 3, 200102), k.GetRecipes().Single(r => r.Id == "fake combo|lifeskill|0").Materials.Single());

        // the store refuses what is not an icon item
        foreach (var bad in new[]
        {
            new IconItem("icon:abc", 1, "Fake", T0),
            new IconItem(IconA, 0, "Fake", T0),
            new IconItem(IconA, 1, " ", T0),
            new IconItem(IconA, 1, "bad\nname", T0),
            new IconItem(IconA, 1, IconB, T0),
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() => k.LearnIconItem(bad), bad.ToString());
        }
    }

    [TestMethod]
    public void AnObservationWithAnItemId_IsTheSameItemAsTheEntryWithThatId_UnderAnyKey()
    {
        var k = _env.NewStore().Profile;
        k.ApplyObservations([new StockObservation(I("fake old name"), 5, null, ObservationKind.CraftViewPair, T0, P(100), 0.9, "ability:craft.product", 300001)]);

        // renamed in the game (same id): the entry moves to the new key
        k.ApplyObservations([new StockObservation(I("fake new name"), 6, null, ObservationKind.CraftViewPair, T0.AddMinutes(1), P(200), 0.9, "ability:craft.product", 300001)]);
        Assert.IsFalse(k.GetStock().Items.ContainsKey(I("fake old name")));
        Assert.AreEqual((6L, (long?)300001), (k.GetStock().Items[I("fake new name")].Count, k.GetStock().Items[I("fake new name")].ItemId));

        // an older reading of the same id does not move it back
        k.ApplyObservations([new StockObservation(I("fake old name"), 1, null, ObservationKind.CraftViewPair, T0, P(150), 0.9, "ability:craft.product", 300001)]);
        Assert.IsFalse(k.GetStock().Items.ContainsKey(I("fake old name")));
        Assert.AreEqual(6L, k.GetStock().Items[I("fake new name")].Count);

        // another id under the same key is another item: no recency contest
        k.ApplyObservations([new StockObservation(I("fake new name"), 9, null, ObservationKind.CraftViewPair, T0, P(120), 0.9, "ability:craft.product", 300002)]);
        Assert.AreEqual((9L, (long?)300002), (k.GetStock().Items[I("fake new name")].Count, k.GetStock().Items[I("fake new name")].ItemId));

        // a reading without an id keeps the known id (same key = same item)
        k.ApplyObservations([new StockObservation(I("fake new name"), 10, null, ObservationKind.CraftViewPair, T0.AddMinutes(5), P(400), 0.9, "ability:craft.product")]);
        Assert.AreEqual((10L, (long?)300002), (k.GetStock().Items[I("fake new name")].Count, k.GetStock().Items[I("fake new name")].ItemId));

        // ids are positive
        k.ApplyObservations([new StockObservation(I("fake bad id"), 1, null, ObservationKind.CraftViewPair, T0, P(500), 0.9, "x", 0)]);
        Assert.IsFalse(k.GetStock().Items.ContainsKey(I("fake bad id")), "an invalid observation is ignored");
    }

    [TestMethod]
    public void ARecipeWithAProductItemId_ReplacesTheRecipeWithThatIdUnderAnotherName_AndRoundTrips()
    {
        var k = _env.NewStore().Profile;
        var old = R("fake ams|lifeskill|0", "fake ams", 1, ("fake wheat", 1)) with { UserVerified = false, ProductItemId = 400001 };
        k.UpsertRecipe(old);
        var named = R("fake toast|lifeskill|0", "fake toast", 1, ("fake wheat", 1)) with
        {
            UserVerified = false,
            ProductItemId = 400001,
            Materials = [new MaterialRequirement(I("fake wheat"), 1, 200101)],
        };
        k.UpsertRecipe(named);
        Assert.AreEqual("fake toast|lifeskill|0", k.GetRecipes().Single().Id, "one product item, one recipe");

        // an OCR-only recipe of another product is untouched; RemoveRecipe removes by id
        k.UpsertRecipe(R("fake other|lifeskill|0", "fake other", 1, ("fake wheat", 2)));
        Assert.HasCount(2, k.GetRecipes());
        Assert.IsTrue(k.RemoveRecipe("fake other|lifeskill|0"));
        Assert.IsFalse(k.RemoveRecipe("fake other|lifeskill|0"));
        k.Save();

        var back = _env.NewStore().Profile.GetRecipes().Single();
        KnowledgeStoreTests.AssertRecipe(named, back);
        Assert.AreEqual((long?)400001, back.ProductItemId);

        // a re-read without the id (title read by OCR) keeps the known id; ids must be positive
        k.UpsertRecipe(named with { ProductItemId = null, Materials = [new MaterialRequirement(I("fake wheat"), 1)] });
        Assert.AreEqual((long?)400001, k.GetRecipes().Single().ProductItemId);
        Assert.ThrowsExactly<ArgumentException>(() => k.UpsertRecipe(named with { ProductItemId = -1 }));
        Assert.ThrowsExactly<ArgumentException>(() => k.UpsertRecipe(named with { Materials = [new MaterialRequirement(I("fake a"), 1, 7), new MaterialRequirement(I("fake b"), 1, 7)] }), "one id, one material");
    }

    [TestMethod]
    public void AVerifiedRecipe_StaysVerified_WhenItsMaterialsAreReadByNameInsteadOfIcon()
    {
        var k = _env.NewStore().Profile;
        var verified = R("fake toast|lifeskill|0", "fake toast", 1, ("fake wheat", 1)) with { ProductItemId = 400001, Materials = [new MaterialRequirement(I("fake wheat"), 1, 200101)] };
        k.UpsertRecipe(verified);
        k.UpsertRecipe(verified with { UserVerified = false, Materials = [new MaterialRequirement(I("fake wheat renamed"), 1, 200101)], Confidence = RecipeConfidence.Partial });
        Assert.IsTrue(k.GetRecipes().Single().UserVerified, "the same item id is the same material whatever its name");
    }

    [TestMethod]
    public void ForgetRecipes_BacksUpFirst_ThenForgetsTheRecipesAndTheRecipeScreenStock()
    {
        WriteVersion1();
        var k = _env.NewStore().Profile;
        k.LearnIconItem(new IconItem(IconB, 200102, "Fake Salt", T0.AddHours(2)));
        _env.Clock.UtcNow = new DateTimeOffset(2026, 10, 15, 3, 4, 5, TimeSpan.Zero);

        var report = k.ForgetRecipes(["ability:craft.product", "ability:craft.pairs"]);
        Assert.AreEqual(("knowledge.json.forget-20261015T030405Z", 2, 2), (report.BackupFile, report.Recipes, report.Observations));

        // the backup holds everything as it was (and is a valid store file)
        var backup = Path.Combine(Path.GetDirectoryName(_env.CharacterFile(Key1))!, report.BackupFile);
        var saved = JsonNode.Parse(File.ReadAllText(backup))!;
        Assert.AreEqual(2, saved["recipes"]!.AsArray().Count);
        Assert.AreEqual(3, saved["stock"]!["items"]!.AsArray().Count);
        Assert.AreEqual(2, (int)saved["schemaVersion"]!);

        // forgotten: the recipes and the Make-tab stock; kept: typed stock, keep-stock rules, icon items
        var back = _env.NewStore().Profile;
        Assert.IsEmpty(back.GetRecipes());
        CollectionAssert.AreEquivalent(new[] { I("fake typed item") }, back.GetStock().Items.Keys.ToArray());
        Assert.AreEqual(2, back.GetKeepStockRules().Single().Min);
        Assert.AreEqual(IconB, back.GetIconItems().Single().IconKey);

        // a second forget in the same second gets its own backup
        Assert.AreEqual("knowledge.json.forget-20261015T030405Z-2", k.ForgetRecipes(["ability:craft.pairs"]).BackupFile);
    }

    [TestMethod]
    public void ForgetRecipes_RemovesNothing_WhenTheBackupCannotBeWritten()
    {
        WriteVersion1();
        var k = _env.NewStore().Profile;
        _env.Clock.UtcNow = new DateTimeOffset(2026, 10, 15, 3, 4, 5, TimeSpan.Zero);
        var blocked = _env.CharacterFile(Key1) + ".forget-20261015T030405Z";
        Directory.CreateDirectory(blocked); // a folder where the backup must go: the write fails
        Assert.Throws<Exception>(() => k.ForgetRecipes(["ability:craft.pairs"]));
        Assert.HasCount(2, k.GetRecipes(), "nothing forgotten without a backup");
        Assert.HasCount(3, k.GetStock().Items);
        Assert.HasCount(2, _env.NewStore().Profile.GetRecipes());
    }

    [TestMethod]
    public void IdentityRule_PrefersTheIdWhenBothKnowIt()
    {
        Assert.IsTrue(ItemIdentity.Same(I("a"), 1, I("b"), 1), "same id, other names");
        Assert.IsFalse(ItemIdentity.Same(I("a"), 1, I("a"), 2), "same name, other ids");
        Assert.IsTrue(ItemIdentity.Same(I("a"), 1, I("a"), null), "one id unknown: the keys");
        Assert.IsFalse(ItemIdentity.Same(I("a"), null, I("b"), 1));
        Assert.IsTrue(ItemIdentity.SameMaterials(
            [new MaterialRequirement(I("a"), 1, 5), new MaterialRequirement(I("icon"), 2)],
            [new MaterialRequirement(I("icon"), 2), new MaterialRequirement(I("renamed"), 1, 5)]));
        Assert.IsFalse(ItemIdentity.SameMaterials([new MaterialRequirement(I("a"), 1)], [new MaterialRequirement(I("a"), 2)]), "quantities count");
    }
}
