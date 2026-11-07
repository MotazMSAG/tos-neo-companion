using Companion.App.Hosting;
using Companion.App.Jobs;
using Companion.App.Tray;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Host;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;

namespace Companion.App.Tests;

/// <summary>
/// The tray's Crafting window logic (view-model and rules; no UI automation): recipe rows from knowledge records,
/// CraftLoop / ScanFavorites parameters, button rules, code-to-text mapping, and that every request takes the job
/// channel's full validation (pipe codec + job host gates, limits and the real crafting job definitions).
/// </summary>
[TestClass]
public sealed class CraftingTests
{
    private static readonly CharacterKey Char = new("00112233aabbccdd");
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly string IconA = "icon:" + new string('a', 64);
    private static readonly string IconB = "icon:" + new string('b', 64);
    private static readonly string IconC = "icon:" + new string('c', 64);

    [TestMethod]
    [DataRow("screen:craft.fav/1", 1)]
    [DataRow("screen:craft.fav/3", 3)]
    [DataRow("screen:craft.fav/10", 10)]
    [DataRow("screen:craft.fav/0", 0)]
    [DataRow("screen:craft.fav/11", 0)]
    [DataRow("screen:craft.fav/", 0)]
    [DataRow("screen:craft.fav/x", 0)]
    [DataRow("screen:craft.fav/-1", 0)]
    [DataRow("screen:craft.fav/ 3", 0)]
    [DataRow("screen:craft.fav/100", 0)]
    [DataRow("screen:craft.pairs/3", 0)]
    [DataRow("user", 0)]
    [DataRow("", 0)]
    [DataRow(null, 0)]
    public void ParseSlot_ReadsTheFavouritesSlotFromTheSource(string? source, int expected) =>
        Assert.AreEqual(expected, CraftingRules.ParseSlot(source));

    [TestMethod]
    public void Map_BuildsRowsFromTheScanFavoritesRecords()
    {
        var recipes = new List<Recipe>
        {
            MakeRecipe("white toast", "screen:craft.fav/3", Now.AddMinutes(-5), RecipeConfidence.Complete, (IconA, 1), (IconB, 3), (IconC, 3)),
            MakeRecipe("old bread", "screen:craft.fav/3", Now.AddDays(-2), RecipeConfidence.Complete, (IconA, 2)),
            MakeRecipe("berry jam", "screen:craft.fav/1", Now.AddMinutes(-6), RecipeConfidence.Partial, (IconB, 4)),
            MakeRecipe("apple pie", null, Now.AddDays(-1), RecipeConfidence.Complete, ("flour", 2)),
        };
        var stock = new Dictionary<ItemKey, StockEntry>
        {
            [new ItemKey("white toast")] = Entry("white toast", 25, Now.AddMinutes(-5), stale: false),
            [new ItemKey(IconA)] = Entry(IconA, 40, Now.AddMinutes(-5), stale: false),
            [new ItemKey(IconB)] = Entry(IconB, 45, Now.AddHours(-3), stale: true),
        };

        var rows = CraftingRules.Map(recipes, new StockView(Char, stock, null, 0, null));

        CollectionAssert.AreEqual(new[] { "Berry Jam", "White Toast", "Apple Pie", "Old Bread" }, rows.Select(r => r.Title).ToArray(), "by slot, then by name");

        var toast = rows.Single(r => r.RecipeId == "white toast|lifeskill|0");
        Assert.AreEqual(3, toast.Slot);
        Assert.IsFalse(toast.SlotOutdated);
        Assert.IsFalse(toast.Partial);
        Assert.AreEqual(25L, toast.ProductOwned);
        Assert.AreEqual(Now.AddMinutes(-5), toast.ProductObservedUtc);
        CollectionAssert.AreEqual(new[] { "Material 1", "Material 2", "Material 3" }, toast.Materials.Select(m => m.Label).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 3, 3 }, toast.Materials.Select(m => m.Required).ToArray());
        Assert.AreEqual(40L, toast.Materials[0].Owned);
        Assert.AreEqual(Now.AddMinutes(-5), toast.Materials[0].ObservedUtc);
        Assert.IsFalse(toast.Materials[0].Stale);
        Assert.AreEqual(45L, toast.Materials[1].Owned);
        Assert.IsTrue(toast.Materials[1].Stale);
        Assert.IsNull(toast.Materials[2].Owned, "an unseen material is unknown, never 0");
        Assert.IsNull(toast.Materials[2].ObservedUtc);

        Assert.AreEqual(Now.AddMinutes(-5), toast.SlotSeenUtc);
        Assert.IsFalse(toast.SlotTakenLater);
        Assert.IsFalse(toast.PlayedSinceScan, "no stale mark in this stock");
        var details = CraftingRules.Details(toast, Now);
        StringAssert.Contains(details, "Favourites slot 3, as of the scan 5 min ago: the companion selects it before each Make.");
        Assert.IsFalse(details.Contains("Since that scan", StringComparison.Ordinal));
        StringAssert.Contains(details, "White Toast: owned 25 (seen 5 min ago)");
        StringAssert.Contains(details, "Material 1: needs 1 per craft, owned 40 (seen 5 min ago)");
        StringAssert.Contains(details, "Material 2: needs 3 per craft, owned 45 (seen 3 h ago, may have changed since)");
        StringAssert.Contains(details, "Material 3: needs 3 per craft, owned: not seen yet");
        Assert.AreEqual("Favourite 3: White Toast", CraftingRules.ListText(toast));

        var jam = rows.Single(r => r.Title == "Berry Jam");
        Assert.AreEqual(1, jam.Slot);
        Assert.IsTrue(jam.Partial, "a partial recipe is shown as partial");
        Assert.AreEqual("Favourite 1: Berry Jam (partial read)", CraftingRules.ListText(jam));
        StringAssert.Contains(CraftingRules.Details(jam, Now), "Partial read");
        Assert.IsNull(jam.ProductOwned);

        var old = rows.Single(r => r.Title == "Old Bread");
        Assert.AreEqual(3, old.Slot);
        Assert.IsTrue(old.SlotOutdated, "white toast was seen in slot 3 later");
        Assert.IsTrue(old.SlotTakenLater);
        Assert.AreEqual("Favourite 3: Old Bread (slot outdated, scan again)", CraftingRules.ListText(old));
        StringAssert.Contains(CraftingRules.Details(old, Now), "Another recipe was seen in favourites slot 3 later. Start still works with a count: the companion looks for this recipe in the Make list");

        var pie = rows.Single(r => r.Title == "Apple Pie");
        Assert.AreEqual(0, pie.Slot, "no favourites source: keep the recipe selected in game");
        Assert.IsFalse(pie.SlotOutdated);
        Assert.IsNull(pie.SlotSeenUtc);
        Assert.AreEqual("Flour", pie.Materials.Single().Label, "a named material keeps its name");
        StringAssert.Contains(CraftingRules.Details(pie, Now), "finds this recipe in the Make list", "not a favourite: the Craft plan, never the in-game selection");
    }

    [TestMethod]
    public void Map_ShowsTheLoggedNames_AndCraftLoopSendsTheProductItemId()
    {
        // 2026-10-15: product and material names come from the game's item tips (with their ids)
        var toast = MakeRecipe("white toast", "screen:craft.fav/3", Now.AddMinutes(-5), RecipeConfidence.Complete, ("wheat", 1), (IconB, 3)) with
        {
            ProductItemId = 1051000,
            Materials = [new MaterialRequirement(new ItemKey("wheat"), 1, 2001), new MaterialRequirement(new ItemKey(IconB), 3)],
        };
        var stock = new Dictionary<ItemKey, StockEntry>
        {
            [new ItemKey("wheat")] = Entry("wheat", 40, Now.AddMinutes(-5), stale: false),
            [new ItemKey("salt")] = Entry("salt", 45, Now.AddMinutes(-5), stale: false),
        };

        // an icon-keyed material whose icon's item is known shows that item's name and count
        var rows = CraftingRules.Map([toast], new StockView(Char, stock, null, 0, null), [new IconItem(IconB, 2002, "Salt", Now)]);
        var row = rows.Single();
        CollectionAssert.AreEqual(new[] { "Wheat", "Salt" }, row.Materials.Select(m => m.Label).ToArray());
        CollectionAssert.AreEqual(new long?[] { 40, 45 }, row.Materials.Select(m => m.Owned).ToArray());
        Assert.AreEqual((long?)1051000, row.ProductItemId);
        Assert.AreEqual("Material 2", CraftingRules.Map([toast], new StockView(Char, stock, null, 0, null)).Single().Materials[1].Label, "unknown icon: no name is made up");

        // CraftLoop gets the id for a favourites row (the job refuses when the slot now holds another product)
        var loop = CraftingRules.CraftLoopRequest(Id(1), row, untilMaterialsRunOut: false, count: 2, batchMax: false);
        AssertParams(new() { ["slot"] = "3", ["count"] = "2", ["batch"] = "typed", ["item"] = "1051000" }, loop.Params!);
        var inGame = CraftingRules.CraftLoopRequest(Id(2), row with { Slot = 0 }, false, 2, false);
        Assert.IsFalse(inGame.Params!.ContainsKey("item"), "no slot: nothing to check");
        StringAssert.Contains(CraftingMessages.ForRefusal("slotItem.changed"), "found another recipe in this favourites slot");
    }

    [TestMethod]
    public void Map_AFavouritesSlotCounts_OnlyWhenTheLatestScanSawTheRecipeThere()
    {
        // Three days ago a scan recorded slots 1..4. Today the user removed two favourites and scanned again: slots 1
        // and 2 were recorded, slot 3 repeated slot 2 and the scan stopped. Nothing else claimed slots 3 and 4, but the
        // latest scan did not see apple pie and old bread there: crafting them could click an empty slot (which leaves
        // the previous recipe selected) or another recipe.
        var old = Now.AddDays(-3).AddMinutes(-10);
        var rows = CraftingRules.Map(
        [
            MakeRecipe("white toast", "screen:craft.fav/1", Now.AddMinutes(-4), RecipeConfidence.Complete, (IconA, 1)),
            MakeRecipe("berry jam", "screen:craft.fav/2", Now.AddMinutes(-3), RecipeConfidence.Complete, (IconB, 2)),
            MakeRecipe("apple pie", "screen:craft.fav/3", old.AddMinutes(2), RecipeConfidence.Complete, (IconC, 1)),
            MakeRecipe("old bread", "screen:craft.fav/4", old.AddMinutes(3), RecipeConfidence.Complete, (IconA, 2)),
        ], null);

        CollectionAssert.AreEqual(new[] { "White Toast", "Berry Jam", "Apple Pie", "Old Bread" }, rows.Select(r => r.Title).ToArray(), "confirmed slots first");
        Assert.IsFalse(Row(rows, "White Toast").SlotOutdated);
        Assert.IsFalse(Row(rows, "Berry Jam").SlotOutdated);
        foreach (var title in new[] { "Apple Pie", "Old Bread" })
        {
            var row = Row(rows, title);
            Assert.IsTrue(row.SlotOutdated, title);
            Assert.IsFalse(row.SlotTakenLater, title + ": no other recipe was seen in that slot");
            Assert.IsTrue(CraftingRules.Buttons(false, false, row, false, 5).Start, title + ": Start with a count finds it in the Make list (2026-10-18)");
            Assert.IsFalse(CraftingRules.Buttons(false, false, row, true, 0).Start, title + ": 'until materials run out' needs a confirmed slot");
            StringAssert.EndsWith(CraftingRules.ListText(row), "(slot outdated, scan again)");
            var details = CraftingRules.Details(row, Now);
            StringAssert.Contains(details, "is not confirmed: the last 'Scan favourites' did not see this recipe there (it was seen there 3 days ago)");
            StringAssert.Contains(details, "Start still works with a count: the companion looks for this recipe in the Make list", "the fallback is named");
        }

        // A rescan minutes after the first one, after the user removed favourite 5: the times alone cannot tell the two
        // scans apart, the slot order can (every scan starts again at slot 1).
        rows = CraftingRules.Map(
        [
            MakeRecipe("a", "screen:craft.fav/1", Now.AddMinutes(-3), RecipeConfidence.Complete, (IconA, 1)),
            MakeRecipe("b", "screen:craft.fav/2", Now.AddMinutes(-2.5), RecipeConfidence.Complete, (IconA, 1)),
            MakeRecipe("c", "screen:craft.fav/3", Now.AddMinutes(-2), RecipeConfidence.Complete, (IconA, 1)),
            MakeRecipe("d", "screen:craft.fav/4", Now.AddMinutes(-1.5), RecipeConfidence.Complete, (IconA, 1)),
            MakeRecipe("e", "screen:craft.fav/5", Now.AddMinutes(-7), RecipeConfidence.Complete, (IconA, 1)),
        ], null);
        CollectionAssert.AreEqual(new[] { "E" }, rows.Where(r => r.SlotOutdated).Select(r => r.Title).ToArray());

        // A complete recipe is never replaced by a partial read ('recipe.keptComplete'): it keeps the slot and the time
        // of the older scan, although it may have moved. Only the latest scan's slots count.
        rows = CraftingRules.Map(
        [
            MakeRecipe("a", "screen:craft.fav/1", Now.AddMinutes(-5), RecipeConfidence.Complete, (IconA, 1)),
            MakeRecipe("kept", "screen:craft.fav/2", Now.AddDays(-1), RecipeConfidence.Complete, (IconB, 1)),
            MakeRecipe("c", "screen:craft.fav/3", Now.AddMinutes(-4), RecipeConfidence.Complete, (IconC, 1)),
        ], null);
        CollectionAssert.AreEqual(new[] { "Kept" }, rows.Where(r => r.SlotOutdated).Select(r => r.Title).ToArray());

        // Slots of one scan follow each other closely; a long gap (here a 40 min park) is taken as two scans (safe side).
        rows = CraftingRules.Map(
        [
            MakeRecipe("a", "screen:craft.fav/1", Now.AddMinutes(-42), RecipeConfidence.Complete, (IconA, 1)),
            MakeRecipe("b", "screen:craft.fav/2", Now.AddMinutes(-2), RecipeConfidence.Complete, (IconB, 1)),
        ], null);
        CollectionAssert.AreEqual(new[] { "A" }, rows.Where(r => r.SlotOutdated).Select(r => r.Title).ToArray());
        rows = CraftingRules.Map(
        [
            MakeRecipe("a", "screen:craft.fav/1", Now.AddMinutes(-12), RecipeConfidence.Complete, (IconA, 1)),
            MakeRecipe("b", "screen:craft.fav/2", Now.AddMinutes(-2), RecipeConfidence.Complete, (IconB, 1)),
        ], null);
        Assert.IsFalse(rows.Any(r => r.SlotOutdated), "a 10 min wait inside one scan");
    }

    [TestMethod]
    public void Map_WarnsWhenTheGameWasUsedSinceTheScan()
    {
        var recipe = MakeRecipe("white toast", "screen:craft.fav/1", Now.AddHours(-2), RecipeConfidence.Complete, (IconA, 1));
        var empty = new Dictionary<ItemKey, StockEntry>();

        var played = CraftingRules.Map([recipe], new StockView(Char, empty, Now.AddHours(-1), 0, null)).Single();
        Assert.IsTrue(played.PlayedSinceScan);
        Assert.IsFalse(played.SlotOutdated, "a warning only");
        Assert.IsTrue(CraftingRules.Buttons(false, false, played, false, 5).Start);
        var details = CraftingRules.Details(played, Now);
        StringAssert.Contains(details, "Favourites slot 1, as of the scan 2 h ago");
        StringAssert.Contains(details, "Since that scan you used the game or the companion crafted. If you changed your My Favorites in the game, run 'Scan favourites' again first.");

        var fresh = CraftingRules.Map([recipe], new StockView(Char, empty, Now.AddHours(-3), 0, null)).Single();
        Assert.IsFalse(fresh.PlayedSinceScan, "the stale mark is older than the scan");
        Assert.IsFalse(CraftingRules.Details(fresh, Now).Contains("Since that scan", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Map_EmptyKnowledge_AndTexts()
    {
        Assert.AreEqual(0, CraftingRules.Map([], null).Count);
        Assert.AreEqual("just now", CraftingRules.AgeText(Now, Now));
        Assert.AreEqual("just now", CraftingRules.AgeText(Now.AddMinutes(1), Now), "clock skew");
        Assert.AreEqual("59 min ago", CraftingRules.AgeText(Now.AddMinutes(-59), Now));
        Assert.AreEqual("47 h ago", CraftingRules.AgeText(Now.AddHours(-47), Now));
        Assert.AreEqual("3 days ago", CraftingRules.AgeText(Now.AddDays(-3), Now));
        Assert.AreEqual("owned 10..20 (seen 2 min ago)", CraftingRules.OwnedText(null, new ValueRange(10, 20), Now.AddMinutes(-2), false, Now));
        Assert.AreEqual("(Recipe selected in the game)", CraftingRules.ListText(CraftRecipeRow.SelectedInGame));
    }

    [TestMethod]
    public void Parameters_MapToCraftLoopAndScanFavorites()
    {
        AssertParams(new() { ["slot"] = "3", ["count"] = "25", ["batch"] = "typed" }, CraftingRules.CraftLoopParams(3, false, 25, false));
        AssertParams(new() { ["slot"] = "3", ["count"] = "0", ["batch"] = "max" }, CraftingRules.CraftLoopParams(3, true, 25, true));
        AssertParams(new() { ["slot"] = "0", ["count"] = "1", ["batch"] = "typed" }, CraftingRules.CraftLoopParams(0, false, 1, false));

        var toast = CraftingRules.Map([MakeRecipe("white toast", "screen:craft.fav/7", Now, RecipeConfidence.Complete, (IconA, 1))], null).Single();
        var loop = CraftingRules.CraftLoopRequest(Id(1), toast, untilMaterialsRunOut: false, count: 12, batchMax: true);
        Assert.AreEqual(PipeOp.Submit, loop.Op);
        Assert.AreEqual(Id(1), loop.JobId);
        Assert.AreEqual("CraftLoop", loop.JobName);
        AssertParams(new() { ["slot"] = "7", ["count"] = "12", ["batch"] = "max" }, loop.Params!);

        var inGame = CraftingRules.CraftLoopRequest(Id(2), CraftRecipeRow.SelectedInGame, true, 5, false);
        AssertParams(new() { ["slot"] = "0", ["count"] = "0", ["batch"] = "typed" }, inGame.Params!);

        // 4.33: no request is bound to a character (one knowledge profile): op, id, job name and parameters only
        Assert.AreEqual(new PipeRequest(PipeOp.Submit, Id(1), "CraftLoop", loop.Params), loop);
        var scan = CraftingRules.ScanRequest(Id(3));
        Assert.AreEqual(new PipeRequest(PipeOp.Submit, Id(3), "ScanFavorites"), scan, "ScanFavorites takes no parameters");

        var all = CraftingRules.ScanAllRequest(Id(4));
        Assert.AreEqual(new PipeRequest(PipeOp.Submit, Id(4), "ScanRecipeList"), all, "no 'find': the whole list");
    }

    [TestMethod]
    public void Requests_PassTheRealCraftingJobDefinitions_ThroughTheJobChannel()
    {
        using var real = RealCatalogRig.Create();
        var channel = new LocalJobChannel(real.Host.Handle);
        var toast = CraftingRules.Map([MakeRecipe("white toast", "screen:craft.fav/3", Now, RecipeConfidence.Complete, (IconA, 1))], null).Single();

        // The gate is the crafting switch alone (4.33: no character binding or permission; this host knows no character).
        Assert.AreEqual("refused.switch", channel.Send(CraftingRules.CraftLoopRequest(Id(1), toast, false, 5, false)).Code);
        real.Switches.Crafting = true;

        // The window's parameter shapes are valid for the real CraftLoop definition (slot, count, batch 1|max).
        foreach (var (row, until, count, max, n) in new[]
        {
            (toast, false, 5, false, 4),
            (toast, true, 1, true, 5),
            (CraftRecipeRow.SelectedInGame, false, CraftingRules.MaxCount, true, 6),
        })
        {
            var accepted = channel.Send(CraftingRules.CraftLoopRequest(Id(n), row, until, count, max));
            Assert.IsTrue(accepted.Ok, accepted.Code);
            Assert.AreEqual("CraftLoop", accepted.Job!.JobName);
            Assert.AreEqual("cancelled", channel.Send(new PipeRequest(PipeOp.Cancel, Id(n))).Code, "make room in the queue");
        }

        // The catalog still validates: a slot outside 0..10 or a count above the bound is refused.
        Assert.AreEqual("refused.params:param.range:slot", channel.Send(new PipeRequest(PipeOp.Submit, Id(7), "CraftLoop",
            new Dictionary<string, string> { ["slot"] = "11", ["count"] = "1", ["batch"] = "1" })).Code);
        Assert.AreEqual("refused.params:param.range:count", channel.Send(CraftingRules.CraftLoopRequest(Id(8), toast, false, CraftingRules.MaxCount + 1, false)).Code);

        // ScanFavorites is read-only: no switch needed.
        real.Switches.Crafting = false;
        var scan = channel.Send(CraftingRules.ScanRequest(Id(10)));
        Assert.IsTrue(scan.Ok, scan.Code);
        Assert.AreEqual("ScanFavorites", scan.Job!.JobName);
        Assert.AreEqual(0, real.Contexts.Runs.Count, "nothing ran: the engine thread was never started");
    }

    [TestMethod]
    public void LocalChannel_AppliesThePipeCodecValidation_BeforeTheHost()
    {
        var calls = 0;
        var channel = new LocalJobChannel(_ =>
        {
            calls++;
            return new PipeResponse(true, "ok");
        });

        Assert.AreEqual("bad.jobId", channel.Send(new PipeRequest(PipeOp.Submit, "NOT-AN-ID", "CraftLoop")).Code);
        Assert.AreEqual("bad.params.value", channel.Send(new PipeRequest(PipeOp.Submit, Id(1), "CraftLoop", new Dictionary<string, string> { ["count"] = new string('1', 65) })).Code);
        Assert.AreEqual("bad.jobName", channel.Send(new PipeRequest(PipeOp.Submit, Id(1), "Craft Loop")).Code);
        Assert.AreEqual("bad.size", channel.Send(new PipeRequest(PipeOp.Submit, Id(1), "CraftLoop",
            Enumerable.Range(0, 16).ToDictionary(i => "p" + i, _ => new string('9', 64)))).Code);
        Assert.AreEqual(0, calls, "refused before the job host");

        Assert.AreEqual("ok", channel.Send(new PipeRequest(PipeOp.Status)).Code);
        Assert.AreEqual(1, calls);

        var throwing = new LocalJobChannel(_ => throw new InvalidOperationException("boom"));
        Assert.AreEqual("host.error", throwing.Send(new PipeRequest(PipeOp.Status)).Code);
    }

    [TestMethod]
    public void ANonFavouriteRecipe_GoesThroughTheCraftJob()
    {
        // 2026-10-15: a recipe that is not a favourite is planned (sub-recipes first) and found in the Make list
        var chicken = CraftingRules.Map([MakeRecipe("roast chicken", "screen:craft.list", Now, RecipeConfidence.Complete, ("chicken", 1))], null).Single();
        Assert.IsTrue(chicken.ViaPlan);
        Assert.IsFalse(CraftRecipeRow.SelectedInGame.ViaPlan, "the in-game entry keeps CraftLoop slot 0");

        var craft = CraftingRules.CraftRequest(Id(1), chicken, 4);
        Assert.AreEqual(new PipeRequest(PipeOp.Submit, Id(1), "Craft", craft.Params), craft, "no character (4.33)");
        AssertParams(new() { ["item"] = "roast chicken", ["qty"] = "4" }, craft.Params!);

        Assert.IsTrue(CraftingRules.Buttons(false, false, chicken, false, 999).Start);
        Assert.IsFalse(CraftingRules.Buttons(false, false, chicken, false, 1000).Start, "the Craft job's qty bound");
        Assert.IsFalse(CraftingRules.Buttons(false, false, chicken, true, 5).Start, "no 'until materials run out' for a plan");
        StringAssert.Contains(CraftingRules.Details(chicken, Now), "finds this recipe in the Make list");

        var partial = CraftingMessages.ForEnd(Job(JobState.Failed, "plan.partial", new Dictionary<string, object?> { ["made"] = 2L, ["missing"] = "chicken x1", ["notFound"] = "" }));
        Assert.AreEqual("Partly done. Made 2 items. Still missing: chicken x1. [plan.partial]", partial);
    }

    [TestMethod]
    public void Buttons_FollowTheJobAndTheSelection()
    {
        var toast = new CraftRecipeRow("white toast|lifeskill|0", "White Toast", 3, false, false, null, null, null, false, []);
        var outdated = toast with { SlotOutdated = true };
        var partial = toast with { Partial = true };

        Assert.AreEqual(new CraftingButtons(Start: true, Stop: false, Scan: true, Count: true), CraftingRules.Buttons(false, false, toast, false, 5));
        Assert.AreEqual(new CraftingButtons(Start: false, Stop: true, Scan: false, Count: true), CraftingRules.Buttons(true, false, toast, false, 5), "a job runs (any job)");
        Assert.AreEqual(new CraftingButtons(Start: false, Stop: true, Scan: false, Count: true), CraftingRules.Buttons(false, true, toast, false, 5), "this window's job is queued");
        Assert.IsFalse(CraftingRules.Buttons(false, false, null, false, 5).Start, "no recipe selected");
        Assert.IsTrue(CraftingRules.Buttons(false, false, null, false, 5).Scan, "scanning needs no selection");
        Assert.IsTrue(CraftingRules.Buttons(false, false, CraftRecipeRow.SelectedInGame, false, 5).Start, "the recipe selected in game");
        Assert.IsFalse(CraftingRules.Buttons(false, false, outdated, false, 5).Start, "an outdated slot would craft another recipe");
        var outdatedNamed = outdated with { ProductKey = "white toast" };
        Assert.IsTrue(CraftingRules.Buttons(false, false, outdatedNamed, false, 5).Start, "a named recipe falls back to the Make list");
        Assert.IsFalse(CraftingRules.Buttons(false, false, outdatedNamed, true, 0).Start, "'until materials run out' needs the slot");
        Assert.IsTrue(CraftingRules.Buttons(false, false, partial, false, 5).Start, "a partial recipe can still be crafted (slot known)");
        Assert.IsFalse(CraftingRules.Buttons(false, false, toast, false, 0).Start, "count 0 needs the checkbox");
        Assert.IsFalse(CraftingRules.Buttons(false, false, toast, false, CraftingRules.MaxCount + 1).Start);
        Assert.AreEqual(new CraftingButtons(Start: true, Stop: false, Scan: true, Count: false), CraftingRules.Buttons(false, false, toast, true, 0), "until the materials run out");
    }

    [TestMethod]
    public void Messages_RefusalCodesInPlainLanguage()
    {
        // Channel codes, job codes, and the engine's refusals inside a round or slot: JobKit.FromProcedure builds those
        // as "round.refused:<detail>" / "slot.refused:<detail>".
        foreach (var code in new[] { "refused.switch", "switch.off:crafting", "round.refused:switch.off", "round.refused:switch.off:crafting" })
        {
            var text = CraftingMessages.ForRefusal(code);
            StringAssert.Contains(text, "crafting automation is switched off");
            StringAssert.Contains(text, "tray menu");
            StringAssert.EndsWith(text, "[" + code + "]");
        }

        var activity = CraftingMessages.ForRefusal("round.refused:commit.userActivity");
        Assert.AreEqual("Stopped before Make: you used the game since the job started. Leave the game alone while it crafts and start again. "
            + "[round.refused:commit.userActivity]", activity);
        foreach (var code in new[] { "slideClick.off", "slot.refused:slideClick.off", "round.refused:slideClick.off" })
        {
            StringAssert.Contains(CraftingMessages.ForRefusal(code), "Slide clicks", code);
        }

        // deferred parts (FALLBACKS.md, 2026-10-16) say why: the user played, the mouse could not reach the game, or a
        // step kept failing (no halt)
        StringAssert.Contains(CraftingMessages.DeferredText("round.deferred:commit.userActivity"), "you were using the game");
        StringAssert.Contains(CraftingMessages.DeferredText("qty.deferred:unreachable.CrossMonitor"), "where the mouse rested");
        StringAssert.Contains(CraftingMessages.DeferredText("round.deferred:exhausted.PreconditionLostExhausted"), "without a halt");

        StringAssert.Contains(CraftingMessages.ForRefusal("round.refused:halt.latched"), "Clear HALT");
        StringAssert.Contains(CraftingMessages.ForRefusal("halt.latched"), "Clear HALT");
        Assert.AreEqual("Refused. [round.refused]", CraftingMessages.ForRefusal("round.refused"), "no detail");
        StringAssert.Contains(CraftingMessages.ForRefusal("refused.halted"), "Clear HALT");
        StringAssert.Contains(CraftingMessages.ForRefusal("refused.stopped"), "Clear stop");
        StringAssert.Contains(CraftingMessages.ForRefusal("refused.untrusted"), "Trust this build");
        StringAssert.Contains(CraftingMessages.ForRefusal("refused.rate"), "a lot of jobs were started in the last hour");
        StringAssert.Contains(CraftingMessages.ForRefusal("refused.rate.job"), "started many times in the last hour");
        StringAssert.Contains(CraftingMessages.ForRefusal("refused.queueFull"), "already waiting");
        StringAssert.Contains(CraftingMessages.ForRefusal("craft.inProgress"), "already running");
        StringAssert.Contains(CraftingMessages.ForRefusal("refused.params:param.range:slot"), "setting was not accepted");
        StringAssert.Contains(CraftingMessages.ForRefusal("needsSurvey:craft.quantity"), "surveyed");
        Assert.AreEqual("Refused. [something.new]", CraftingMessages.ForRefusal("something.new"));

        // An accepted job tells the user to put another window in front with the pointer resting on it (SPEC 7.5).
        var craft = CraftingMessages.ForSubmit("CraftLoop", new PipeResponse(true, "accepted"));
        StringAssert.StartsWith(craft, "Accepted: crafting starts as soon as the companion is free. Now click another window that is not the game");
        StringAssert.EndsWith(craft, CraftingMessages.CoverGuidance);
        var scan = CraftingMessages.ForSubmit("ScanFavorites", new PipeResponse(true, "accepted"));
        StringAssert.Contains(scan, "read-only");
        StringAssert.EndsWith(scan, CraftingMessages.CoverGuidance);
        StringAssert.Contains(CraftingMessages.CoverGuidance, "cannot click while this window is in front or under the mouse");
        var refused = CraftingMessages.ForSubmit("CraftLoop", new PipeResponse(false, "refused.switch"));
        StringAssert.Contains(refused, "Automation > Crafting");
        Assert.IsFalse(refused.Contains(CraftingMessages.CoverGuidance, StringComparison.Ordinal), "nothing to wait for");

        // The engine's wait reasons in plain words, the code kept in brackets.
        Assert.AreEqual("you are using the game, or used it a few seconds ago [UserActive]", CraftingMessages.WaitText("UserActive"));
        StringAssert.Contains(CraftingMessages.WaitText("NotSent"), "another window (not the game, not this one) must be in front");
        StringAssert.EndsWith(CraftingMessages.WaitText("NotSent"), "[NotSent]");
        StringAssert.StartsWith(CraftingMessages.WaitText("Busy"), "the game is loading or busy");
        StringAssert.StartsWith(CraftingMessages.WaitText("CombatRecent"), "there was combat");
        StringAssert.StartsWith(CraftingMessages.WaitText("paused"), "paused");
        Assert.AreEqual("SomethingNew", CraftingMessages.WaitText("SomethingNew"));

        StringAssert.StartsWith(CraftingMessages.ForCancel("cancel.requested"), "Stop requested");
        StringAssert.Contains(CraftingMessages.ForCancel("alreadyFinished"), "already finished");
    }

    [TestMethod]
    public void Messages_JobEndsInPlainLanguage()
    {
        var data = new Dictionary<string, object?> { ["rounds"] = 3L, ["total"] = 12L };
        Assert.AreEqual("Done: the count was reached. Made 12 items in 3 rounds. [loop.countReached]", CraftingMessages.ForEnd(Job(JobState.Succeeded, "loop.countReached", data)));
        StringAssert.StartsWith(CraftingMessages.ForEnd(Job(JobState.Succeeded, "loop.materialsOut", data)), "Done: the materials ran out. Made 12 items");
        StringAssert.StartsWith(CraftingMessages.ForEnd(Job(JobState.Failed, "loop.materialsOut", data)), "Stopped early: the materials ran out");
        StringAssert.StartsWith(CraftingMessages.ForEnd(Job(JobState.Failed, "loop.noCraft", null)), "No craft started");
        StringAssert.Contains(CraftingMessages.ForEnd(Job(JobState.Succeeded, "favorites.scanned", new Dictionary<string, object?> { ["recipes"] = 4L })), "4 recipe(s) recorded");
        StringAssert.Contains(CraftingMessages.ForEnd(Job(JobState.Failed, "favorites.nothingRead", null)), "could not read any recipe");
        StringAssert.Contains(CraftingMessages.ForEnd(Job(JobState.Refused, "switch.off:crafting", null)), "tray menu");

        var halted = CraftingMessages.ForEnd(Job(JobState.Halted, "BlockedCraftSpot", null));
        StringAssert.StartsWith(halted, HostMessages.HaltText(HaltCode.BlockedCraftSpot, null));
        StringAssert.Contains(halted, "Clear HALT");

        StringAssert.StartsWith(CraftingMessages.ForEnd(Job(JobState.Cancelled, "cancelled.byClient", new Dictionary<string, object?> { ["rounds"] = 1L, ["total"] = 1L })),
            "Stopped at your request. Made 1 item in 1 round.");
        Assert.AreEqual("Stopped at your request. [cancelled.queued]", CraftingMessages.ForEnd(Job(JobState.Cancelled, "cancelled.queued", null)));

        // The engine ended a round or slot itself (commit.userActive, park.expired, commit.unverified...): the job
        // keeps "round.cancelled" / "slot.cancelled", and the user did not ask for that stop.
        var self = CraftingMessages.ForEnd(Job(JobState.Cancelled, "round.cancelled", data));
        StringAssert.StartsWith(self, "The companion ended the job itself (you used the game in the last minute or while it worked, "
            + "it waited too long for a safe moment to click, or it could not confirm the Make press). Details are in the journal.");
        StringAssert.EndsWith(self, " Made 12 items in 3 rounds. [round.cancelled]");
        var scanSelf = CraftingMessages.ForEnd(Job(JobState.Cancelled, "slot.cancelled", null));
        StringAssert.StartsWith(scanSelf, "The companion ended the scan itself");
        StringAssert.EndsWith(scanSelf, "Details are in the journal. [slot.cancelled]");
        foreach (var text in new[] { self, scanSelf, CraftingMessages.ForEnd(Job(JobState.Cancelled, "something.new", null)) })
        {
            Assert.IsFalse(text.Contains("your request", StringComparison.Ordinal), text);
        }
        StringAssert.Contains(CraftingMessages.ForEnd(Job(JobState.Stopped, "stop", null)), "Clear stop");
        StringAssert.Contains(CraftingMessages.ForEnd(Job(JobState.GameExited, "round.gameExited", null)), "game closed");
        StringAssert.Contains(CraftingMessages.ForEnd(Job(JobState.NeedsSurvey, "needsSurvey:x", null)), "surveyed");
        StringAssert.EndsWith(CraftingMessages.ForEnd(Job(JobState.Failed, "InternalError", null)), "[InternalError]");
    }

    [TestMethod]
    public void ViewModel_StartSubmitsTheCraftGoal_AndFollowsItToItsEnd()
    {
        using var rig = CraftRig();
        rig.Switches.Crafting = true;
        var backend = new RigBackend(rig);
        var knowledge = new ReadOnlyKnowledge(Char);
        knowledge.Recipes.Add(MakeRecipe("white toast", "screen:craft.fav/3", Now, RecipeConfidence.Complete, (IconA, 1), (IconB, 3)));
        knowledge.Items[new ItemKey(IconA)] = Entry(IconA, 40, Now, stale: false);
        backend.Knowledge = knowledge;
        var vm = new CraftingViewModel(backend, () => Id(42));

        vm.Refresh();
        Assert.AreEqual(2, vm.Recipes.Count, "'selected in game' + one recipe");
        Assert.IsNull(vm.GateHint, "switch on, slide clicks on");
        Assert.AreEqual("1 recipe known.", vm.KnowledgeLine);
        Assert.IsFalse(vm.Buttons.Start, "nothing selected yet");
        vm.Select("white toast|lifeskill|0");
        vm.Count = 5;
        Assert.IsTrue(vm.Buttons.Start);
        StringAssert.Contains(vm.Details, "Material 1: needs 1 per craft, owned 40");

        vm.Start();
        var sent = backend.Sent.Single(r => r.Op == PipeOp.Submit);
        Assert.AreEqual("Craft", sent.JobName, "1.0: a known recipe goes through the goal loop (plan, gather, craft in batches)");
        Assert.AreEqual(Id(42), sent.JobId);
        Assert.AreEqual(new PipeRequest(PipeOp.Submit, Id(42), "Craft", sent.Params), sent, "no character (4.33)");
        AssertParams(new() { ["item"] = "white toast", ["qty"] = "5" }, sent.Params!);
        StringAssert.StartsWith(vm.Message, "Accepted", vm.Message);
        StringAssert.EndsWith(vm.Message, CraftingMessages.CoverGuidance, "what to do so the companion can click");
        Assert.IsTrue(vm.OwnJobActive);
        Assert.AreEqual(new CraftingButtons(Start: false, Stop: true, Scan: false, Count: true), vm.Buttons, "queued: Start disabled");

        // While this window is in front or under the pointer, no click can be sent: say so.
        Assert.IsNull(vm.ClickHint, "another window is in front");
        vm.WindowBlocksClicks = true;
        Assert.AreEqual(CraftingMessages.CoverHint, vm.ClickHint);
        rig.Engine.Status = new EngineStatus(EngineState.Waiting, Id(42), "crafting.loop", "make", "UserActive", Now);
        vm.Refresh();
        StringAssert.Contains(vm.StatusLine, "engine Waiting (you are using the game, or used it a few seconds ago [UserActive]), crafting.loop / make");
        rig.Engine.Status = new EngineStatus(EngineState.Idle, null, null, null, null, Now);

        rig.Handler.Result = new JobResult(JobState.Succeeded, "plan.done", new Dictionary<string, object?> { ["made"] = 5L, ["gathered"] = "water x5" });
        Assert.IsTrue(rig.Host.RunPending());
        vm.Refresh();
        Assert.IsFalse(vm.OwnJobActive);
        Assert.IsNull(vm.ClickHint, "nothing waits for a click any more");
        Assert.AreEqual("Done: everything in the plan was made. Made 5 items. [plan.done]", vm.Message);
        Assert.IsTrue(vm.Buttons.Start, "free again");
        Assert.AreEqual("white toast|lifeskill|0", vm.Selected!.RecipeId, "the selection survives the reload");
        Assert.AreEqual("Craft", rig.Contexts.Runs.Single().JobName);
    }

    [TestMethod]
    public void ViewModel_UntilMaterialsRunOutAndMaxBatch_AndTheRecipeSelectedInGame()
    {
        using var rig = CraftRig();
        rig.Switches.Crafting = true;
        var backend = new RigBackend(rig);
        var vm = new CraftingViewModel(backend, () => Id(5));
        vm.Refresh();
        vm.Select(string.Empty);
        Assert.IsTrue(vm.Selected!.IsSelectedInGame);
        vm.UntilMaterialsRunOut = true;
        vm.BatchMax = true;
        Assert.IsFalse(vm.Buttons.Count);
        vm.Start();
        AssertParams(new() { ["slot"] = "0", ["count"] = "0", ["batch"] = "max" }, backend.Sent.Single(r => r.Op == PipeOp.Submit).Params!);
    }

    [TestMethod]
    public void ViewModel_RefusalsAreShownWithTheTrayHint_AndNothingRuns()
    {
        using var rig = CraftRig();
        var backend = new RigBackend(rig) { Knowledge = new ReadOnlyKnowledge(Char) };
        var vm = new CraftingViewModel(backend, () => Id(7));
        vm.Refresh();
        StringAssert.Contains(vm.GateHint!, "Automation > Crafting");
        Assert.IsFalse(vm.GateHint!.Contains("Character", StringComparison.Ordinal), "4.33: no character to allow");

        vm.Select(string.Empty);
        vm.Start();
        StringAssert.Contains(vm.Message, "Automation > Crafting");
        StringAssert.EndsWith(vm.Message, "[refused.switch]");
        Assert.IsFalse(vm.OwnJobActive);
        Assert.IsTrue(vm.Buttons.Start, "the user may try again after changing the switch");
        Assert.IsFalse(rig.Host.RunPending(), "nothing was queued");
        Assert.AreEqual(0, rig.Handler.Calls);
    }

    [TestMethod]
    public void ViewModel_StopCancelsItsJob_OrTheRunningJob()
    {
        using var rig = CraftRig();
        rig.Switches.Crafting = true;
        rig.Handler.Gate = new ManualResetEventSlim(false);
        var backend = new RigBackend(rig);
        var vm = new CraftingViewModel(backend, () => Id(11));
        vm.Refresh();
        vm.Select(string.Empty);
        vm.Start();

        var engine = Task.Run(rig.Host.RunPending);
        Assert.IsTrue(rig.Handler.Entered.Wait(TimeSpan.FromSeconds(10)), "job started");
        vm.Refresh();
        StringAssert.Contains(vm.StatusLine, "job CraftLoop " + Id(11)[..8] + " (running)");
        Assert.IsTrue(vm.Buttons.Stop);
        vm.Stop();
        StringAssert.StartsWith(vm.Message, "Stop requested");
        Assert.IsTrue(engine.Wait(TimeSpan.FromSeconds(10)));
        vm.Refresh();
        Assert.AreEqual("Stopped at your request. [cancelled.byClient]", vm.Message);
        Assert.IsFalse(rig.Stop.IsStopRequested, "a cancel, not the kill switch: nothing to clear afterwards");

        // A job started elsewhere (CLI): Start is disabled, Stop cancels it by its id.
        var foreign = Id(12);
        Assert.IsTrue(rig.Submit("Reset", id: foreign).Ok);
        rig.Handler.Entered.Reset();
        engine = Task.Run(rig.Host.RunPending);
        Assert.IsTrue(rig.Handler.Entered.Wait(TimeSpan.FromSeconds(10)));
        vm.Refresh();
        Assert.IsFalse(vm.Buttons.Start);
        Assert.IsFalse(vm.Buttons.Scan);
        Assert.IsTrue(vm.Buttons.Stop);
        vm.Stop();
        Assert.IsTrue(engine.Wait(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(JobState.Cancelled, rig.Host.Handle(new PipeRequest(PipeOp.Result, foreign)).Job!.State);
        vm.Refresh();
        Assert.IsTrue(vm.Buttons.Start);
        vm.Stop();
        Assert.AreEqual("No job is running.", vm.Message);
    }

    [TestMethod]
    public void ViewModel_ScanSubmitsScanFavorites_AndReloadsTheRecipesAtItsEnd()
    {
        using var rig = CraftRig();
        var backend = new RigBackend(rig);
        var knowledge = new ReadOnlyKnowledge(Char);
        backend.Knowledge = knowledge;
        var vm = new CraftingViewModel(backend, () => Id(21));
        vm.Refresh();
        Assert.AreEqual(1, vm.Recipes.Count);
        Assert.IsTrue(vm.Buttons.Scan, "read-only: no switch needed");

        vm.Scan();
        var sent = backend.Sent.Single(r => r.Op == PipeOp.Submit);
        Assert.AreEqual("ScanFavorites", sent.JobName);
        Assert.IsNull(sent.Params);
        Assert.AreEqual(new PipeRequest(PipeOp.Submit, Id(21), "ScanFavorites"), sent, "no character (4.33)");
        Assert.IsFalse(vm.Buttons.Scan, "one at a time");

        // The scan (engine thread) records a recipe; the window shows it once the job has ended.
        knowledge.Recipes.Add(MakeRecipe("white toast", "screen:craft.fav/1", Now, RecipeConfidence.Partial, (IconA, 1)));
        var version = vm.RecipesVersion;
        rig.Handler.Result = new JobResult(JobState.Succeeded, "favorites.scanned", new Dictionary<string, object?> { ["recipes"] = 1L });
        Assert.IsTrue(rig.Host.RunPending());
        vm.Refresh();
        Assert.IsTrue(vm.RecipesVersion > version);
        Assert.AreEqual(2, vm.Recipes.Count);
        Assert.AreEqual("Favourite 1: White Toast (partial read)", CraftingRules.ListText(vm.Recipes[1]));
        StringAssert.StartsWith(vm.Message, "Scan finished: 1 recipe(s) recorded");
    }

    [TestMethod]
    public void ViewModel_LearnSubmitsLearnRecipe_ReadOnly_AndTellsTheEndInPlainWords()
    {
        // 2026-10-15: "Learn selected recipe" reads the recipe the user selected in the game's Make tab
        using var rig = CraftRig();
        var backend = new RigBackend(rig) { Knowledge = new ReadOnlyKnowledge(Char) };
        var vm = new CraftingViewModel(backend, () => Id(23));
        vm.Refresh();
        Assert.IsTrue(vm.Buttons.Scan, "read-only like the scan: no switch, no selection needed");
        var request = CraftingRules.LearnRequest(Id(24));
        Assert.AreEqual(new PipeRequest(PipeOp.Submit, Id(24), "LearnRecipe"), request, "no parameters, no character (4.33)");

        vm.Learn();
        var sent = backend.Sent.Single(r => r.Op == PipeOp.Submit);
        Assert.AreEqual(new PipeRequest(PipeOp.Submit, Id(23), "LearnRecipe"), sent);
        StringAssert.StartsWith(vm.Message, "Accepted: the companion reads the recipe selected in the game's Make tab");
        StringAssert.EndsWith(vm.Message, CraftingMessages.CoverGuidance);
        Assert.IsFalse(vm.Buttons.Scan, "one job at a time");

        rig.Handler.Result = new JobResult(JobState.Failed, "make.notOnTop");
        Assert.IsTrue(rig.Host.RunPending());
        vm.Refresh();
        StringAssert.StartsWith(vm.Message, "Nothing was pressed: open the Ability view's Make tab in the game and select the recipe there first.");
        StringAssert.EndsWith(vm.Message, "[make.notOnTop]");

        StringAssert.StartsWith(
            CraftingMessages.ForEnd(new JobStatusInfo(Id(25), "LearnRecipe", JobState.Succeeded, "recipe.learned", Now, Now, new Dictionary<string, object?> { ["materials"] = 3L })),
            "Learned the recipe selected in the game (3 material(s)).");
    }

    [TestMethod]
    public void ViewModel_ReloadsTheList_WhenAJobStartedElsewhereEnds()
    {
        using var rig = CraftRig();
        rig.Handler.Gate = new ManualResetEventSlim(false);
        var backend = new RigBackend(rig);
        var knowledge = new ReadOnlyKnowledge(Char);
        backend.Knowledge = knowledge;
        var vm = new CraftingViewModel(backend);
        vm.Refresh();
        Assert.AreEqual(1, vm.Recipes.Count);

        // A scan submitted from the CLI records a recipe: the window's favourites mapping follows once it has ended.
        Assert.IsTrue(rig.Submit("ScanFavorites", id: Id(61)).Ok);
        var engine = Task.Run(rig.Host.RunPending);
        Assert.IsTrue(rig.Handler.Entered.Wait(TimeSpan.FromSeconds(10)));
        vm.Refresh();
        Assert.IsFalse(vm.OwnJobActive);
        knowledge.Recipes.Add(MakeRecipe("white toast", "screen:craft.fav/1", Now, RecipeConfidence.Complete, (IconA, 1)));
        var version = vm.RecipesVersion;
        vm.Refresh();
        Assert.AreEqual(version, vm.RecipesVersion, "not while the job runs");

        rig.Handler.Gate.Set();
        Assert.IsTrue(engine.Wait(TimeSpan.FromSeconds(10)));
        vm.Refresh();
        Assert.IsTrue(vm.RecipesVersion > version);
        Assert.AreEqual(2, vm.Recipes.Count);
        Assert.AreEqual("Favourite 1: White Toast", CraftingRules.ListText(vm.Recipes[1]));
    }

    [TestMethod]
    public void ViewModel_ListsTheOneProfile_WithNoCharacterKnown_AndCountsItsRecipes()
    {
        // 4.33 (one knowledge profile for every character): the list shows the profile's recipes whoever is logged in
        // (it used to stay empty until the log named a character), and the line says how many recipes are known
        using var rig = CraftRig();
        var profile = new ReadOnlyKnowledge(Char);
        profile.Recipes.Add(MakeRecipe("white toast", "screen:craft.fav/1", Now, RecipeConfidence.Complete, (IconA, 1)));
        profile.Recipes.Add(MakeRecipe("berry jam", "screen:craft.fav/2", Now, RecipeConfidence.Complete, (IconB, 1)));
        var vm = new CraftingViewModel(new RigBackend(rig) { Knowledge = profile });
        Assert.AreEqual("No recipes known yet.", vm.KnowledgeLine, "before the first refresh");

        vm.Refresh();
        Assert.AreEqual(3, vm.Recipes.Count, "'selected in game' + the profile's two recipes");
        Assert.AreEqual("2 recipes known.", vm.KnowledgeLine);
        vm.Select("white toast|lifeskill|0");
        vm.Refresh();
        Assert.AreEqual("white toast|lifeskill|0", vm.Selected!.RecipeId, "nothing reloads the list under the user");

        // the store could not open the profile: only the in-game entry
        var none = new CraftingViewModel(new RigBackend(rig));
        none.Refresh();
        Assert.AreEqual(1, none.Recipes.Count);
        Assert.AreEqual("0 recipes known.", none.KnowledgeLine);
    }

    [TestMethod]
    public void ViewModel_ShowsCraftProgressFromTheGameLog()
    {
        using var rig = CraftRig();
        rig.Switches.Crafting = true;
        var backend = new RigBackend(rig) { CraftSession = Session(100, 9) };
        var vm = new CraftingViewModel(backend, () => Id(31));
        vm.Refresh();
        vm.Select(string.Empty);
        vm.Start();
        StringAssert.Contains(vm.StatusLine, "so far: 0 crafts started, about 0 items", "the earlier craft is not counted");

        backend.CraftSession = Session(200, 3);
        vm.Refresh();
        backend.CraftSession = Session(200, 6);
        vm.Refresh();
        backend.CraftSession = Session(300, 2);
        vm.Refresh();
        StringAssert.Contains(vm.StatusLine, "so far: 2 crafts started, about 6 items (game log)");
    }

    [TestMethod]
    public void CraftProgress_CountsOnlySessionsAfterTheStart()
    {
        var p = new CraftProgress();
        p.Observe(Session(10, 5));
        Assert.IsFalse(p.Started);
        Assert.AreEqual(0, p.Crafts);

        p.Begin(Session(10, 5));
        p.Observe(Session(10, 8));
        Assert.AreEqual(0, p.Crafts, "the craft running before the start");
        p.Observe(CraftSessionInfo.None);
        p.Observe(Session(20, 1));
        p.Observe(Session(20, 4));
        p.Observe(Session(30, 0));
        Assert.AreEqual(2, p.Crafts);
        Assert.AreEqual(3L, p.Items, "ticks = is_block lines after the first: (4-1) + 0");

        p.Begin(CraftSessionInfo.None);
        Assert.AreEqual(0, p.Crafts);
        p.Observe(Session(10, 2));
        Assert.AreEqual(1, p.Crafts);
    }

    [TestMethod]
    public void JobHost_RunningJob_IsTheJobOnTheEngine()
    {
        using var rig = new JobRig();
        Assert.IsNull(rig.Host.RunningJob);
        rig.Handler.Gate = new ManualResetEventSlim(false);
        var id = Id(51);
        Assert.IsTrue(rig.Submit("Reset", id: id).Ok);
        Assert.IsNull(rig.Host.RunningJob, "queued is not running");
        var engine = Task.Run(rig.Host.RunPending);
        Assert.IsTrue(rig.Handler.Entered.Wait(TimeSpan.FromSeconds(10)));
        var running = rig.Host.RunningJob!;
        Assert.AreEqual(id, running.JobId);
        Assert.AreEqual("Reset", running.JobName);
        Assert.AreEqual(JobState.Running, running.State);
        rig.Handler.Gate.Set();
        Assert.IsTrue(engine.Wait(TimeSpan.FromSeconds(10)));
        Assert.IsNull(rig.Host.RunningJob);
    }

    private static string Id(int n) => n.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);

    private static CraftRecipeRow Row(IReadOnlyList<CraftRecipeRow> rows, string title) => rows.Single(r => r.Title == title);

    private static void AssertParams(Dictionary<string, string> expected, IReadOnlyDictionary<string, string> actual)
    {
        Assert.AreEqual(expected.Count, actual.Count, string.Join(",", actual.Keys));
        foreach (var (k, v) in expected)
        {
            Assert.IsTrue(actual.TryGetValue(k, out var a), "missing " + k);
            Assert.AreEqual(v, a, k);
        }
    }

    private static Recipe MakeRecipe(string product, string? source, DateTimeOffset at, RecipeConfidence confidence, params (string Item, int Qty)[] materials) =>
        new(product + "|lifeskill|0", new ItemKey(product), 1, "lifeskill",
            materials.Select(m => new MaterialRequirement(new ItemKey(m.Item), m.Qty)).ToArray(), [], null, at, source, false, confidence, null, null);

    private static StockEntry Entry(string item, long count, DateTimeOffset at, bool stale) =>
        new(new ItemKey(item), count, null, StockSource.Observed, at, null, 0.9, "ability:craft.pairs", stale);

    private static JobStatusInfo Job(JobState state, string code, IReadOnlyDictionary<string, object?>? data) =>
        new(Id(99), "CraftLoop", state, code, Now, Now, data);

    private static CraftSessionInfo Session(long startOffset, int isBlocks) =>
        new(true, "UI_LIFE_ALCHEMIST_MAKE_POT", null, new LogPosition(new LogFileIdentity(new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc), 1, "head"), startOffset), isBlocks, null, null, false);

    /// <summary>A JobRig whose whitelist also has CraftLoop (commit, crafting) and ScanFavorites (navigation, no switch).</summary>
    private static JobRig CraftRig()
    {
        var rig = new JobRig();
        rig.Catalog.Add(new JobDefinition("CraftLoop", "crafting", "loop",
            [new JobParamSpec("slot", JobParamType.Int, false, 0, 10), new JobParamSpec("count", JobParamType.Int, true, 0, 100_000), new JobParamSpec("batch", JobParamType.Enum, false, Allowed: ["1", "max", "typed"])],
            AutomationSwitch.Crafting, JobKind.Commit, 6), rig.Handler);
        rig.Catalog.Add(new JobDefinition("ScanFavorites", "crafting", "scan", [], AutomationSwitch.None, JobKind.Navigation, 4), rig.Handler);
        rig.Catalog.Add(new JobDefinition("Craft", "crafting", "goal",
            [new JobParamSpec("item", JobParamType.ItemKey, true), new JobParamSpec("qty", JobParamType.Int, true, 1, 999)],
            AutomationSwitch.Crafting, JobKind.Commit, 6), rig.Handler);
        rig.Catalog.Add(new JobDefinition("LearnRecipe", "crafting", "learn", [], AutomationSwitch.None, JobKind.Navigation, 10), rig.Handler);
        return rig;
    }

    /// <summary>The window's backend over a JobRig; records every request the job host received.</summary>
    private sealed class RigBackend : ICraftingBackend
    {
        private readonly JobRig _rig;
        private readonly LocalJobChannel _channel;

        public RigBackend(JobRig rig)
        {
            _rig = rig;
            _channel = new LocalJobChannel(r =>
            {
                lock (Sent)
                {
                    Sent.Add(r);
                }

                return rig.Host.Handle(r);
            });
        }

        public List<PipeRequest> Sent { get; } = [];

        /// <summary>The store's one profile (null: the store could not open it).</summary>
        public ICharacterKnowledge? Knowledge { get; set; }

        public JobStatusInfo? RunningJob => _rig.Host.RunningJob;

        public bool CraftingSwitchOn => _rig.Switches.Crafting;

        public bool SlideClicksOn => _rig.Switches.SlideClickEnabled;

        public CraftSessionInfo CraftSession { get; set; } = CraftSessionInfo.None;

        public DateTimeOffset UtcNow => _rig.Clock.UtcNow;

        public PipeResponse Send(PipeRequest request) => _channel.Send(request);
    }

    /// <summary>Knowledge the window may only read: every write throws.</summary>
    private sealed class ReadOnlyKnowledge(CharacterKey character) : ICharacterKnowledge
    {
        public List<Recipe> Recipes { get; } = [];

        public Dictionary<ItemKey, StockEntry> Items { get; } = [];

        public CharacterKey Character { get; } = character;

        public StockView GetStock() => new(Character, new Dictionary<ItemKey, StockEntry>(Items), null, 0, null);

        public IReadOnlyList<Recipe> GetRecipes() => Recipes.ToArray();

        public IReadOnlyList<KeepStockRule> GetKeepStockRules() => [];

        public void ApplyObservations(IReadOnlyList<StockObservation> observations) => throw new InvalidOperationException("read-only");

        public bool ApplyDerived(DerivedStockDelta delta) => throw new InvalidOperationException("read-only");

        public void MarkStale(StaleScope scope, ItemKey? item, string reasonCode, DateTimeOffset atUtc) => throw new InvalidOperationException("read-only");

        public void UpsertRecipe(Recipe recipe) => throw new InvalidOperationException("read-only");

        public bool RemoveRecipe(string recipeId) => throw new InvalidOperationException("read-only");

        public List<IconItem> IconItems { get; } = [];

        public IReadOnlyList<IconItem> GetIconItems() => IconItems.ToArray();

        public void LearnIconItem(IconItem item) => throw new InvalidOperationException("read-only");

        public ForgetRecipesReport ForgetRecipes(IReadOnlyCollection<string> coverages) => throw new InvalidOperationException("read-only");

        public void SetKeepStockRules(IReadOnlyList<KeepStockRule> rules) => throw new InvalidOperationException("read-only");

        public void Save() => throw new InvalidOperationException("read-only");
    }

    /// <summary>A job host over the REAL job catalog of this build (real data, real feature definitions), fakes elsewhere.</summary>
    private sealed class RealCatalogRig : IDisposable
    {
        private RealCatalogRig(IJobCatalog catalog)
        {
            Host = new JobHost(new JobHostDeps(catalog, Clock, new FakeStop(), new FakePause(), new FakeHalt(), new FakeEngine(), new FakeJournal(),
                new FakeTrust(), Knowledge, Switches, () => false, Contexts));
            Host.MarkStarted(null);
        }

        public FakeClock Clock { get; } = new();

        public FakeKnowledgeStore Knowledge { get; } = new();

        public FakeSwitches Switches { get; } = new();

        public FakeContexts Contexts { get; } = new();

        public JobHost Host { get; }

        public static RealCatalogRig Create()
        {
            var paths = TestDirs.Paths();
            var trust = new FakeTrust
            {
                Reader = p =>
                {
                    var full = Path.GetFullPath(p);
                    return full.StartsWith(paths.InstallDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full)
                        ? File.ReadAllBytes(full)
                        : null;
                },
            };
            var s = Composition.Create(paths, new FakeClock(), currentGameBuild: null, trust);
            var report = CoreDataLoader.Load(s, Composition.CompiledFeatures(), CompanionConfig.Default);
            Assert.IsTrue(report.CoreOk, string.Join(", ", report.Problems));
            return new RealCatalogRig(s.Features.Jobs);
        }

        public void Dispose() => Host.Dispose();
    }
}
