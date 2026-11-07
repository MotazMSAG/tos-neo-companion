using System.Text;
using System.Text.Json.Nodes;
using Companion.Contracts.Knowledge;
using Companion.Knowledge.Planning;
using static Companion.Knowledge.Tests.Fx;

namespace Companion.Knowledge.Tests;

[TestClass]
public sealed class KnowledgeStoreTests
{
    private TestEnv _env = null!;

    [TestInitialize]
    public void Init() => _env = new TestEnv();

    [TestCleanup]
    public void Cleanup() => _env.Dispose();

    [TestMethod]
    public void ConstructorsHaveNoSideEffects()
    {
        _ = _env.NewStore();
        _ = new CharacterKeyHasher(_env.Paths);
        _ = new Planner();
        Assert.IsFalse(Directory.Exists(_env.Paths.RuntimeDir));
    }

    [TestMethod]
    public void LoadOnEmptyFolder_IsOk_AndOnlyPinsTheDefaultProfile()
    {
        var report = _env.NewStore().Load();
        Assert.IsTrue(report.Ok);
        Assert.AreEqual(1, report.Characters, "the default profile");
        Assert.IsEmpty(report.RecoveredFromBackup);
        CollectionAssert.AreEqual(new[] { "profile.json" }, Directory.GetFiles(_env.Paths.KnowledgeDir).Select(Path.GetFileName).ToArray(), "only the pin");
        Assert.IsEmpty(Directory.GetDirectories(_env.Paths.KnowledgeDir), "no knowledge folder before the first save");
        StringAssert.Contains(File.ReadAllText(_env.PinFile), DefaultKey.Value);
    }

    [TestMethod]
    public void RoundTripWithZeroArrays()
    {
        var store = _env.NewStore();
        store.Load();
        var k = store.Profile;
        k.SetKeepStockRules([]);
        k.Save();

        var json = JsonNode.Parse(File.ReadAllText(_env.CharacterFile(DefaultKey)))!;
        Assert.AreEqual(2, (int)json["schemaVersion"]!, "written as schema 2 (item ids, icon items)");
        Assert.AreEqual(DefaultKey.Value, (string)json["character"]!);
        Assert.IsEmpty(json["stock"]!["items"]!.AsArray());
        Assert.IsEmpty(json["recipes"]!.AsArray());
        Assert.IsEmpty(json["keepStock"]!.AsArray());

        var back = _env.NewStore();
        Assert.IsTrue(back.Load().Ok);
        var k2 = back.Profile;
        Assert.IsEmpty(k2.GetStock().Items);
        Assert.IsEmpty(k2.GetRecipes());
        Assert.IsEmpty(k2.GetKeepStockRules());
        Assert.AreEqual(0L, k2.GetStock().LastAppliedOffset);
        Assert.IsNull(k2.GetStock().StaleSince);
    }

    [TestMethod]
    public void RoundTripWithOneEntryEach()
    {
        var store = _env.NewStore();
        var k = store.Profile;
        k.ApplyObservations([Obs("fake ore", 12, 100)]);
        var recipe = R("fake-ingot-1", "fake ingot", 1, ("fake ore", 3));
        k.UpsertRecipe(recipe);
        k.SetKeepStockRules([new KeepStockRule(I("fake ore"), 5)]);
        k.Save();

        var k2 = _env.NewStore().Profile;
        var stock = k2.GetStock();
        Assert.HasCount(1, stock.Items);
        Assert.AreEqual(k.GetStock().Items[I("fake ore")], stock.Items[I("fake ore")]);
        Assert.HasCount(1, k2.GetRecipes());
        AssertRecipe(recipe, k2.GetRecipes()[0]);
        Assert.AreEqual(new KeepStockRule(I("fake ore"), 5), k2.GetKeepStockRules().Single());
    }

    [TestMethod]
    public void ScreenLearnedRecipeWithIconKeyedMaterials_IsAcceptedAndRoundTrips()
    {
        // The shapes ScanFavorites writes (crafting feature, 2026-10-14): materials known only by their icon fingerprint,
        // unverified, provenance "screen:craft.fav/<slot>" (a code, not a file path).
        var wheat = new ItemKey("icon:" + new string('0', 32) + new string('f', 32));
        var salt = new ItemKey("icon:" + new string('a', 64));
        var recipe = new Recipe(
            "fake toast|lifeskill|0", I("fake toast"), 1, "lifeskill",
            [new MaterialRequirement(wheat, 1), new MaterialRequirement(salt, 3)],
            [], null, T0, "screen:craft.fav/3", UserVerified: false, RecipeConfidence.Complete, null, null);
        var k = _env.NewStore().Profile;
        k.UpsertRecipe(recipe);
        k.ApplyObservations([
            new StockObservation(I("fake toast"), 25, null, ObservationKind.CraftViewPair, T0, P(100), 0.9, "ability:craft.product"),
            new StockObservation(wheat, 40, null, ObservationKind.CraftViewPair, T0, P(101), 0.9, "ability:craft.pairs"),
        ]);
        k.Save();

        var back = _env.NewStore().Profile;
        AssertRecipe(recipe, back.GetRecipes().Single());
        Assert.AreEqual(40L, back.GetStock().Items[wheat].Count);
        Assert.AreEqual(25L, back.GetStock().Items[I("fake toast")].Count);

        // Without a source (screen) or a user's confirmation a recipe is refused (REC-01).
        Assert.ThrowsExactly<ArgumentException>(() => back.UpsertRecipe(recipe with { SourceCapture = null }));
    }

    [TestMethod]
    public void TheListScansRequiresLineAndCounter_RoundTrip_AndABadCounterIsRefused()
    {
        // ScanRecipeList / ScanFavorites (2026-10-14): the "Requires: owned/required" line as the requirement code
        // "requires:<required>", the 'a/b' counter under the product picture as the optional Counter.
        var cream = new Recipe(
            "fake cream|lifeskill|0", I("fake cream"), 1, "lifeskill",
            [new MaterialRequirement(new ItemKey("icon:" + new string('b', 64)), 6)],
            ["requires:5"], null, T0, "screen:craft.list/7", UserVerified: false, RecipeConfidence.Complete, null, null, Counter: "5/300");
        var k = _env.NewStore().Profile;
        k.UpsertRecipe(cream);
        k.Save();

        var back = _env.NewStore().Profile.GetRecipes().Single();
        AssertRecipe(cream, back);
        Assert.AreEqual("5/300", back.Counter);

        // a recipe without a counter (every recipe stored before 2026-10-14) keeps none
        var old = cream with { Id = "fake toast|lifeskill|0", Product = I("fake toast"), Counter = null, Requirements = [] };
        k.UpsertRecipe(old);
        k.Save();
        Assert.IsNull(_env.NewStore().Profile.GetRecipes().Single(r => r.Id == old.Id).Counter);

        foreach (var bad in new[] { "5/", "a/300", "5 / 300", "12345678/1" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => k.UpsertRecipe(cream with { Counter = bad }), bad);
        }
    }

    [TestMethod]
    public void RoundTripWithManyEntriesAndNestedMaterials()
    {
        var store = _env.NewStore();
        var k = store.Profile;
        var observations = Enumerable.Range(1, 25)
            .Select(i => i % 3 == 0
                ? Obs($"fake item {i}", null, 100 + i, new ValueRange(1000 * i, (1000 * i) + 99), ObservationKind.BagPartial)
                : Obs($"fake item {i}", i * 7, 100 + i, kind: i % 2 == 0 ? ObservationKind.UserEntered : ObservationKind.CraftViewPair))
            .ToList();
        k.ApplyObservations(observations);
        var recipes = Enumerable.Range(1, 6).Select(i => R($"fake-r{i}", $"fake product {i}", i,
                [.. Enumerable.Range(1, i).Select(j => ($"fake item {j}", j))]) with
            {
                Requirements = i % 2 == 0 ? ["fake.grade.2", "fake.energy.10"] : [],
                SuccessRate = i == 4 ? 0.75 : null,
                MedianTickCadence = i == 5 ? TimeSpan.FromSeconds(2.4) : null,
                Confidence = i == 6 ? RecipeConfidence.Partial : RecipeConfidence.Complete,
                UserVerified = i != 6,
                GameBuild = i == 3 ? null : "fake-build",
            }).ToList();
        foreach (var r in recipes)
        {
            k.UpsertRecipe(r);
        }
        k.SetKeepStockRules([new KeepStockRule(I("fake item 1"), 3), new KeepStockRule(I("fake item 2"), 0)]);
        k.MarkStale(StaleScope.Item, I("fake item 4"), "test.mark", T0.AddHours(1));
        Assert.IsTrue(k.ApplyDerived(new DerivedStockDelta(I("fake item 1"), 5, P(500), "craft.tick")));
        k.Save();

        var k2 = _env.NewStore().Profile;
        var a = k.GetStock();
        var b = k2.GetStock();
        Assert.AreEqual(a.Items.Count, b.Items.Count);
        foreach (var (item, entry) in a.Items)
        {
            Assert.AreEqual(entry, b.Items[item], item.Value);
        }
        Assert.AreEqual(a.LastAppliedOffset, b.LastAppliedOffset);
        Assert.AreEqual(a.LogFile, b.LogFile);
        Assert.AreEqual(a.StaleSince, b.StaleSince);
        var back = k2.GetRecipes();
        Assert.HasCount(recipes.Count, back);
        foreach (var r in recipes)
        {
            AssertRecipe(r, back.Single(x => x.Id == r.Id));
        }
        CollectionAssert.AreEqual(k.GetKeepStockRules().ToList(), k2.GetKeepStockRules().ToList());
    }

    [TestMethod]
    public void AtomicWriteLeavesNoTempFilesAndKeepsFiveBackups()
    {
        var k = _env.NewStore().Profile;
        for (var v = 1; v <= 8; v++)
        {
            k.SetKeepStockRules([new KeepStockRule(I("fake ore"), v)]);
            k.Save();
        }
        var dir = Path.GetDirectoryName(_env.CharacterFile(DefaultKey))!;
        var names = Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        CollectionAssert.AreEqual(
            new[] { "knowledge.json", "knowledge.json.bak1", "knowledge.json.bak2", "knowledge.json.bak3", "knowledge.json.bak4", "knowledge.json.bak5" },
            names);
        Assert.AreEqual(8, KeepMinIn(_env.CharacterFile(DefaultKey)));
        for (var gen = 1; gen <= 5; gen++)
        {
            Assert.AreEqual(8 - gen, KeepMinIn($"{_env.CharacterFile(DefaultKey)}.bak{gen}"), $"bak{gen}");
        }
    }

    [TestMethod]
    public void SaveWithoutChangesDoesNotWrite()
    {
        var k = _env.NewStore().Profile;
        k.Save();
        Assert.IsFalse(File.Exists(_env.CharacterFile(DefaultKey)));
        k.SetKeepStockRules([new KeepStockRule(I("fake ore"), 1)]);
        k.Save();
        var stamp = File.GetLastWriteTimeUtc(_env.CharacterFile(DefaultKey));
        k.Save();
        Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(_env.CharacterFile(DefaultKey)));
        Assert.IsFalse(File.Exists(_env.CharacterFile(DefaultKey) + ".bak1"));
    }

    [TestMethod]
    public void CorruptMainFallsBackToNewestValidBackupAndIsMovedAsideOnSave()
    {
        WriteVersions(3);
        File.WriteAllText(_env.CharacterFile(DefaultKey), "{ \"schemaVersion\": 1, \"stock\": [ garbage");

        var report = _env.NewStore().Load();
        Assert.IsFalse(report.Ok);
        CollectionAssert.Contains(report.RecoveredFromBackup.ToList(), $"character.{DefaultKey.Short}:bak1");

        var store2 = _env.NewStore();
        store2.Load();
        var k = store2.Profile;
        Assert.AreEqual(2, k.GetKeepStockRules().Single().Min);

        // The first save repairs the main file; the corrupt one is kept aside and the backups are untouched.
        k.Save();
        Assert.IsTrue(File.Exists(_env.CharacterFile(DefaultKey) + ".corrupt"));
        Assert.AreEqual(2, KeepMinIn(_env.CharacterFile(DefaultKey)));
        Assert.AreEqual(2, KeepMinIn(_env.CharacterFile(DefaultKey) + ".bak1"));
        Assert.AreEqual(1, KeepMinIn(_env.CharacterFile(DefaultKey) + ".bak2"));
        Assert.IsTrue(_env.NewStore().Load().Ok);
    }

    [TestMethod]
    public void CorruptMainAndNewestBackupUseTheOlderBackup()
    {
        WriteVersions(3);
        File.WriteAllBytes(_env.CharacterFile(DefaultKey), [0xFF, 0xFE, 0x00]);
        File.WriteAllText(_env.CharacterFile(DefaultKey) + ".bak1", "");
        var store = _env.NewStore();
        var k = store.Profile;
        Assert.AreEqual(1, k.GetKeepStockRules().Single().Min);
    }

    [TestMethod]
    public void TruncatedMainFromACrashFallsBack()
    {
        WriteVersions(2);
        var bytes = File.ReadAllBytes(_env.CharacterFile(DefaultKey));
        File.WriteAllBytes(_env.CharacterFile(DefaultKey), bytes[..(bytes.Length / 2)]);
        Assert.AreEqual(1, _env.NewStore().Profile.GetKeepStockRules().Single().Min);
    }

    [TestMethod]
    public void MainMissingButBackupPresentIsRecovered()
    {
        WriteVersions(2);
        File.Delete(_env.CharacterFile(DefaultKey));
        var report = _env.NewStore().Load();
        CollectionAssert.Contains(report.RecoveredFromBackup.ToList(), $"character.{DefaultKey.Short}:bak1");
        Assert.AreEqual(1, _env.NewStore().Profile.GetKeepStockRules().Single().Min);
    }

    [TestMethod]
    public void UnsupportedOrMissingSchemaVersionIsRejected()
    {
        WriteVersions(2);
        var path = _env.CharacterFile(DefaultKey);
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node["schemaVersion"] = 3;
        File.WriteAllText(path, node.ToJsonString());
        Assert.AreEqual(1, _env.NewStore().Profile.GetKeepStockRules().Single().Min, "future version falls back");

        node.AsObject().Remove("schemaVersion");
        File.WriteAllText(path, node.ToJsonString());
        Assert.AreEqual(1, _env.NewStore().Profile.GetKeepStockRules().Single().Min, "missing version falls back");
    }

    [TestMethod]
    public void FileOfAnotherCharacterIsRejected()
    {
        // the profile's file must name the profile's folder: a file copied from another folder is not used
        WriteVersions(1);
        Directory.CreateDirectory(Path.GetDirectoryName(_env.CharacterFile(Key2))!);
        File.Copy(_env.CharacterFile(DefaultKey), _env.CharacterFile(Key2));
        _env.WriteLegacyBindings((Key2, "allowed"));
        var k = _env.NewStore().Profile;
        Assert.AreEqual(Key2, k.Character);
        Assert.IsEmpty(k.GetKeepStockRules());
    }

    [TestMethod]
    public void AllCopiesCorruptStartsEmptyAndReportsUnrecoverable()
    {
        WriteVersions(2);
        File.WriteAllText(_env.CharacterFile(DefaultKey), "not json");
        File.WriteAllText(_env.CharacterFile(DefaultKey) + ".bak1", "not json either");
        var report = _env.NewStore().Load();
        Assert.IsFalse(report.Ok);
        CollectionAssert.Contains(report.RecoveredFromBackup.ToList(), $"character.{DefaultKey.Short}:unrecoverable");
        var k = _env.NewStore().Profile;
        Assert.IsEmpty(k.GetStock().Items, "unknown, never invented");
        Assert.IsEmpty(k.GetKeepStockRules());
    }

    [TestMethod]
    public void ALegacyAllowedCharacter_IsTheProfile_AndTheKnowledgeLearnedBeforeIsKept()
    {
        // 4.33 (one profile for every character): the knowledge the user built before is the folder of the character
        // the old characters.json allowed, even when another character's folder is larger; its file is used as it is.
        SeedKnowledge(Key1, rules: 2);
        SeedKnowledge(Key2, rules: 9);
        _env.WriteLegacyBindings((Key2, "readOnly"), (Key1, "allowed"));
        var before = File.ReadAllBytes(_env.CharacterFile(Key1));

        var store = _env.NewStore();
        var report = store.Load();
        Assert.IsTrue(report.Ok);
        Assert.AreEqual(2, report.Characters, "two knowledge folders");
        Assert.AreEqual(Key1, store.Profile.Character);
        CollectionAssert.AreEqual(
            new[] { new KeepStockRule(I("fake item 1"), 1), new KeepStockRule(I("fake item 2"), 2) },
            store.Profile.GetKeepStockRules().ToArray());
        CollectionAssert.AreEqual(before, File.ReadAllBytes(_env.CharacterFile(Key1)), "loading rewrites nothing");
    }

    [TestMethod]
    public void WithoutALegacyAllowedCharacter_TheLargestKnowledgeFolderIsTheProfile()
    {
        SeedKnowledge(Key1, rules: 2);
        SeedKnowledge(Key2, rules: 9);
        Assert.IsFalse(File.Exists(_env.BindingsFile));

        var store = _env.NewStore();
        Assert.AreEqual(2, store.Load().Characters);
        Assert.AreEqual(Key2, store.Profile.Character, "the larger file, not the first name");
        Assert.HasCount(9, store.Profile.GetKeepStockRules());

        // a characters.json that allowed nobody changes nothing
        _env.WriteLegacyBindings((Key1, "readOnly"), (Key2, "unknown"));
        Assert.AreEqual(Key2, _env.NewStore().Profile.Character);
    }

    [TestMethod]
    public void WithNothingOnDisk_TheProfileIsTheDefault_AndItsFirstSaveCreatesItsFolder()
    {
        Assert.AreEqual(new CharacterKey("0000000000000000"), KnowledgeStore.DefaultProfile);
        var store = _env.NewStore();
        Assert.AreEqual(KnowledgeStore.DefaultProfile, store.Profile.Character, "loaded on first use");
        Assert.IsFalse(Directory.Exists(Path.Combine(_env.Paths.KnowledgeDir, "0000000000000000")), "loading makes no knowledge folder (it only pins the profile)");

        store.Profile.SetKeepStockRules([new KeepStockRule(I("fake ore"), 4)]);
        store.Profile.Save();
        var file = Path.Combine(_env.Paths.KnowledgeDir, "0000000000000000", "knowledge.json");
        Assert.IsTrue(File.Exists(file));
        Assert.AreEqual("0000000000000000", (string)JsonNode.Parse(File.ReadAllText(file))!["character"]!);
        Assert.IsFalse(File.Exists(_env.BindingsFile), "no characters.json is made");

        // the next start finds it (the only knowledge folder)
        Assert.AreEqual(4, _env.NewStore().Profile.GetKeepStockRules().Single().Min);
    }

    [TestMethod]
    public void TheLegacyCharactersFile_IsOnlyRead_NeverWritten()
    {
        SeedKnowledge(Key1, rules: 1);
        _env.WriteLegacyBindings((Key2, "unknown"), (Key1, "allowed"));
        var bytes = File.ReadAllBytes(_env.BindingsFile);
        var stamp = File.GetLastWriteTimeUtc(_env.BindingsFile);

        var store = _env.NewStore();
        store.Load();
        var k = store.Profile;
        k.SetKeepStockRules([new KeepStockRule(I("fake ore"), 7)]);
        k.ApplyObservations([Obs("fake ore", 3, 100)]);
        k.MarkStale(StaleScope.AllStock, null, "test.mark", T0.AddHours(1));
        k.Save();
        _env.NewStore().Profile.Save();

        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(_env.BindingsFile));
        Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(_env.BindingsFile));
        CollectionAssert.AreEquivalent(new[] { "characters.json", "profile.json" }, Directory.GetFiles(_env.Paths.KnowledgeDir).Select(Path.GetFileName).ToArray(), "no backup or temp copy of it; the pin");
        Assert.AreEqual(7, _env.NewStore().Profile.GetKeepStockRules().Single().Min, "the changes went to the profile");
    }

    [TestMethod]
    public void ACorruptLegacyCharactersFile_FallsBackToItsBackup_WithoutAWarning_AndIsLeftAsItIs()
    {
        SeedKnowledge(Key1, rules: 9);
        SeedKnowledge(Key2, rules: 1);
        _env.WriteLegacyBindings((Key2, "allowed"));
        File.Move(_env.BindingsFile, _env.BindingsFile + ".bak1");
        File.WriteAllText(_env.BindingsFile, "{ broken");

        var store = _env.NewStore();
        var report = store.Load();
        Assert.IsTrue(report.Ok, "the old file is not the store's any more: no warning at every start (review L6)");
        Assert.IsEmpty(report.RecoveredFromBackup);
        Assert.AreEqual(Key2, store.Profile.Character, "the backup's allowed character, not the larger folder");
        Assert.AreEqual("{ broken", File.ReadAllText(_env.BindingsFile), "not repaired: the store never writes it");

        // nothing readable at all, and nothing pinned: the largest folder
        _env.RemovePin();
        File.WriteAllText(_env.BindingsFile + ".bak1", "not json either");
        var none = _env.NewStore();
        Assert.IsTrue(none.Load().Ok);
        Assert.AreEqual(Key1, none.Profile.Character);
    }

    [TestMethod]
    public void TheChoiceIsPinned_ALaterLargerFolderOrLegacyFileChangesNothing()
    {
        // review L7 (2026-11-04): the profile is chosen once; a folder that grows later or a characters.json that turns
        // up again never moves the knowledge to another folder
        SeedKnowledge(Key1, rules: 2);
        Assert.AreEqual(Key1, _env.NewStore().Profile.Character, "the only folder");
        StringAssert.Contains(File.ReadAllText(_env.PinFile), Key1.Value);
        var pin = File.ReadAllBytes(_env.PinFile);

        SeedKnowledge(Key2, rules: 9); // seeds through its own pin and removes it again
        File.WriteAllBytes(_env.PinFile, pin);
        _env.WriteLegacyBindings((Key2, "allowed"));

        var later = _env.NewStore();
        Assert.IsTrue(later.Load().Ok);
        Assert.AreEqual(Key1, later.Profile.Character);
        Assert.HasCount(2, later.Profile.GetKeepStockRules());
    }

    [TestMethod]
    public void AKnowledgeFolderWithOnlyBackups_IsStillFound()
    {
        // test-migration finding (2026-11-04): a folder whose main file was lost still holds the knowledge (the read falls
        // back to the newest backup), so the largest-folder rule counts its backups
        _env.WritePin(Key1);
        var k = _env.NewStore().Profile;
        k.SetKeepStockRules([new KeepStockRule(I("fake ore"), 1)]);
        k.Save();
        k.SetKeepStockRules([new KeepStockRule(I("fake ore"), 2)]);
        k.Save();
        _env.RemovePin();
        File.Delete(_env.CharacterFile(Key1));

        var store = _env.NewStore();
        var report = store.Load();
        Assert.AreEqual(Key1, store.Profile.Character, "not the empty default profile");
        CollectionAssert.Contains(report.RecoveredFromBackup.ToList(), $"character.{Key1.Short}:bak1");
        Assert.AreEqual(1, store.Profile.GetKeepStockRules().Single().Min, "the newest backup");
    }

    [TestMethod]
    public void MalformedKeysOnDisk_AreNeverTheProfile()
    {
        // Since 4.33 the profile's folder comes from the disk (a legacy binding, a folder name): a name that is not a
        // key (16 lower-case hex) is never used, so neither can point the store at another folder.
        WriteVersions(5);
        foreach (var bad in new[] { "0123456789ABCDEF", "evil", "0123456789abcdef0" })
        {
            Directory.CreateDirectory(Path.Combine(_env.Paths.KnowledgeDir, bad));
            File.Copy(_env.CharacterFile(DefaultKey), Path.Combine(_env.Paths.KnowledgeDir, bad, "knowledge.json"));
        }
        Directory.Delete(Path.GetDirectoryName(_env.CharacterFile(DefaultKey))!, recursive: true);
        _env.WriteLegacyBindings((new CharacterKey("../../evil000000"), "allowed"));

        var store = _env.NewStore();
        var report = store.Load();
        Assert.IsTrue(report.Ok, "a malformed key makes the legacy file invalid: it is ignored, not reported");
        Assert.AreEqual(1, report.Characters, "no valid knowledge folder: the default profile");
        Assert.AreEqual(DefaultKey, store.Profile.Character);
        Assert.IsEmpty(store.Profile.GetKeepStockRules());
    }

    [TestMethod]
    public void ProfileReturnsTheSameInstance_AndALaterLoadKeepsIt()
    {
        var store = _env.NewStore();
        var first = store.Profile;
        Assert.AreSame(first, store.Profile);
        Assert.IsTrue(store.Load().Ok);
        Assert.AreSame(first, store.Profile, "loaded once");
        Assert.AreEqual(DefaultKey, first.Character);
    }

    [TestMethod]
    public void WritesRefuseAReparsePointFolder()
    {
        var target = Path.Combine(_env.Root, "elsewhere");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(_env.Paths.KnowledgeDir);
        var link = Path.Combine(_env.Paths.KnowledgeDir, DefaultKey.Value);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Symbolic links need Developer Mode or elevation; a directory junction (also a reparse point) does not.
            using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/d", "/c", "mklink", "/J", link, target])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            mklink?.WaitForExit(10_000);
            if (!Directory.Exists(link))
            {
                Assert.Inconclusive("Could not create a reparse point on this PC.");
            }
        }
        Assert.IsTrue((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        var k = _env.NewStore().Profile;
        Assert.AreEqual(DefaultKey, k.Character, "the linked folder is the profile's");
        k.SetKeepStockRules([new KeepStockRule(I("fake ore"), 1)]);
        Assert.ThrowsExactly<IOException>(k.Save);
        Assert.IsEmpty(Directory.GetFiles(target));
    }

    [TestMethod]
    public void NoRawTokenOnDisk()
    {
        const string rawToken = "99999-123456";
        const string otherToken = "fake-role-token-TEST-0001";
        var hasher = new CharacterKeyHasher(_env.Paths);
        _ = hasher.Hash(rawToken);
        _ = hasher.Hash(otherToken);
        var k = _env.NewStore().Profile;
        k.ApplyObservations([Obs("fake ore", 4, 50)]);
        k.Save();

        var files = Directory.GetFiles(_env.Paths.RuntimeDir, "*", SearchOption.AllDirectories);
        Assert.IsGreaterThanOrEqualTo(3, files.Length, "the salt, its copy and the profile");
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            foreach (var token in new[] { rawToken, otherToken })
            {
                Assert.IsFalse(Contains(bytes, Encoding.UTF8.GetBytes(token)), $"{token} in {file}");
                Assert.IsFalse(Contains(bytes, Encoding.Unicode.GetBytes(token)), $"{token} (UTF-16) in {file}");
            }
        }
    }

    internal static void AssertRecipe(Recipe expected, Recipe actual)
    {
        var noMaterials = Array.Empty<MaterialRequirement>();
        var noRequirements = Array.Empty<string>();
        Assert.AreEqual(
            expected with { Materials = noMaterials, Requirements = noRequirements },
            actual with { Materials = noMaterials, Requirements = noRequirements });
        CollectionAssert.AreEqual(expected.Materials.ToList(), actual.Materials.ToList());
        CollectionAssert.AreEqual(expected.Requirements.ToList(), actual.Requirements.ToList());
    }

    /// <summary>Saves the profile of a new store (nothing on disk yet: the default profile) with keep-stock min 1..n.</summary>
    private void WriteVersions(int versions)
    {
        var k = _env.NewStore().Profile;
        for (var v = 1; v <= versions; v++)
        {
            k.SetKeepStockRules([new KeepStockRule(I("fake ore"), v)]);
            k.Save();
        }

        _env.RemovePin(); // the disk as a companion before 4.33 left it: the next store chooses again
    }

    /// <summary>
    /// Knowledge an earlier companion kept in <paramref name="key"/>'s folder (<paramref name="rules"/> keep-stock rules,
    /// min 1..n): saved through a pin on <paramref name="key"/>, which is removed again (no legacy file is written).
    /// </summary>
    private void SeedKnowledge(CharacterKey key, int rules)
    {
        _env.WritePin(key);
        var k = _env.NewStore().Profile;
        Assert.AreEqual(key, k.Character, "seeded through the pin");
        k.SetKeepStockRules([.. Enumerable.Range(1, rules).Select(i => new KeepStockRule(I($"fake item {i}"), i))]);
        k.Save();
        _env.RemovePin();
    }

    private static int KeepMinIn(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        return (int)node["keepStock"]![0]!["min"]!;
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
