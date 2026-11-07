using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Perception;
using Companion.Contracts.Safety;
using Companion.Features.Kit;

namespace Companion.Features.Tests;

/// <summary>
/// ScanFavorites against fakes: the real crafting.scanFavorites data procedure, a fake engine that returns scripted
/// craft.product / craft.pairs reads per slot, a fake knowledge store and a fake clock. No window, no game, no log.
/// </summary>
[TestClass]
public sealed class ScanFavoritesTests
{
    private static readonly string Wheat = "icon:" + new string('0', 64);
    private static readonly string Salt = "icon:" + new string('f', 64);
    private static readonly string Grapes = "icon:" + new string('0', 32) + new string('f', 32);
    private static readonly string Egg = "icon:" + new string('a', 64);

    /// <summary>One slot as the readers would return it: title (null = unread), owned badge, materials (icon, owned, required).</summary>
    private sealed record Panel(string? Title, long? Owned, params (string? Icon, long? Have, long? Need)[] Materials);

    private static readonly Panel Toast = new("White Toast", 25, (Wheat, 40, 1), (Salt, 45, 3), (Grapes, 340, 3));
    private static readonly Panel Juice = new("Grape Juice", 7, (Grapes, 340, 5));
    private static readonly Panel Omelette = new("Omelette", 0, (Egg, 12, 2), (Salt, 45, 1));

    // ------------------------------------------------------------------ definition and data

    [TestMethod]
    public void TheJobIsAReadOnlyNavigationScan_WithNoParamsAndNoSwitch()
    {
        var (host, env, _) = T.LoadReal();
        var def = T.Definition(host, "ScanFavorites");
        Assert.AreEqual(("crafting", JobKind.Navigation, AutomationSwitch.None, 0), (def.FeatureId, def.Kind, def.Switch, def.Params.Count));

        var p = env.Procedures.Get("crafting.scanFavorites")!;
        Assert.AreEqual((ProcedureKind.Navigation, AutomationSwitch.None), (p.Kind, p.Gate));
        Assert.IsFalse(p.Steps.Any(s => s.Flags.HasFlag(StepFlags.Commit) || s.Action is AwaitCraftAction), "never crafts");
        Assert.IsFalse(p.Steps.Any(s => s.Action is ClickAction c && c.TemplateId != "craft.fav.anchor"), "the only clicks select a favourites slot");
        Assert.AreEqual(new CallProcedureAction("OpenAbility"), p.Steps[0].Action);
        Assert.AreEqual(new ReadAction("craft.product", "product"), p.Steps[^2].Action);
        Assert.AreEqual(new ReadAction("craft.pairs", "pairs"), p.Steps[^1].Action);
        Assert.AreEqual(new WaitAction(1000), p.Steps.Single(s => s.Id == "settle").Action, "the panel updates before it is read");
    }

    [TestMethod]
    public void TheSlotsUseTheSameAnchorAndOffsetsAsCraftLoop()
    {
        var (_, env, _) = T.LoadReal();
        var scan = env.Procedures.Get("crafting.scanFavorites")!;
        var loop = env.Procedures.Get("crafting.loop")!;
        for (var slot = 1; slot <= 10; slot++)
        {
            var id = SlotStep(slot);
            Assert.AreEqual(loop.Steps.Single(s => s.Id == id).Action, scan.Steps.Single(s => s.Id == id).Action, id);
            Assert.AreEqual("UI_LIFESKILL_MAKE_VIEW", scan.Steps.Single(s => s.Id == id).Pre.TopUi);
        }

        var (a, b) = (loop.Steps.Single(s => s.Id == "announce.close"), scan.Steps.Single(s => s.Id == "announce.close"));
        Assert.AreEqual((a.Action, a.Flags), (b.Action, b.Flags));
        CollectionAssert.AreEqual(a.Expect.UiEvents!.ToArray(), b.Expect.UiEvents!.ToArray());
    }

    [TestMethod]
    public void EachSlotRunKeepsOnlyItsSlot_AndOpensTheViewOnlyWhenItIsNotOnTop()
    {
        var panels = Enumerable.Range(1, 10).Select(i => new Panel("Dish " + i, i, (Wheat, 40, i))).ToArray();
        var all = new ScanWorld(panels);
        Assert.AreEqual(JobState.Succeeded, all.Run().State);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, 20).Select(i => i % 2 == 0 ? "crafting.scanFavorites" : "crafting.productTip").ToArray(),
            all.Ctx.Runner.RunIds.ToArray(),
            "each slot run is followed by its product tip run");
        var first = all.SlotRuns[0];
        CollectionAssert.AreEqual(new[] { "open", "announce.close", "slot.01", "settle", "read.product", "read.pairs" }, first.Steps.Select(s => s.Id).ToArray());
        Assert.IsFalse(first.Steps[0].Flags.HasFlag(StepFlags.SkipIfSatisfied), "slot 1 opens the view like CraftLoop (Reset + menu.ability)");
        Assert.AreEqual((ProcedureKind.Navigation, AutomationSwitch.None), (first.Kind, first.Gate));

        var seventh = all.SlotRuns[6];
        CollectionAssert.AreEqual(new[] { "open", "announce.close", "slot.07", "settle", "read.product", "read.pairs" }, seventh.Steps.Select(s => s.Id).ToArray());
        Assert.IsTrue(seventh.Steps[0].Flags.HasFlag(StepFlags.SkipIfSatisfied), "later slots skip opening while the Make view is on top");
        Assert.AreEqual(new ClickAction("craft.fav.anchor", new PointI(-114, -118)), seventh.Steps[2].Action);

        Func<ProcedureDefinition, ProcedureDefinition>[] broken =
        [
            d => d with { Kind = ProcedureKind.Commit },
            d => d with { Gate = AutomationSwitch.Crafting },
            d => d with { Steps = d.Steps.Where(s => s.Id != "slot.04").ToArray() },
            d => d with { Steps = d.Steps.Where(s => s.Id != "read.pairs").ToArray() },
            d => d with { Steps = d.Steps.Select(s => s.Id == "settle" ? s with { Flags = StepFlags.Commit } : s).ToArray() },
        ];
        foreach (var breakIt in broken)
        {
            var w = new ScanWorld();
            w.Catalog.Put(breakIt(w.Catalog.Get("crafting.scanFavorites")!));
            Assert.AreEqual((JobState.Failed, "procedure.invalid:crafting.scanFavorites"), Code(w.Run()));
            Assert.HasCount(0, w.Ctx.Runner.Runs);
        }
    }

    // ------------------------------------------------------------------ scan scenarios

    [TestMethod]
    public void ThreeFavouritesThenAnEmptySlot_StopsAtTheRepeat_AndStoresRecipesAndStock()
    {
        // slot 4 is empty: the panel still shows slot 3's recipe
        var w = new ScanWorld(Toast, Juice, Omelette, Omelette);
        var r = w.Run();

        Assert.AreEqual((JobState.Succeeded, "favorites.scanned"), Code(r));
        Assert.HasCount(5, w.SlotRuns, "stops at the repeat, after a second look at that slot (R2.7)");
        CollectionAssert.AreEqual(new[] { "slot.01", "slot.02", "slot.03", "slot.04", "slot.04" }, w.SlotRuns.Select(p => p.Steps.Single(s => s.Id.StartsWith("slot.", StringComparison.Ordinal)).Id).ToArray());
        Assert.AreEqual((4L, 3L, 3L, "repeat"), ((long)r.Data!["slots"]!, (long)r.Data["recipes"]!, (long)r.Data["recipesChanged"]!, (string)r.Data["end"]!));
        Assert.AreEqual(TimeSpan.FromMilliseconds(4 * 300), w.Ctx.ClockFake.Monotonic - w.StartMono, "0.3 s between slot runs (was 2 s, 2026-10-15), none before the first");

        var k = w.Ctx.KnowledgeFake!;
        Assert.HasCount(3, k.Recipes);
        var toast = k.Recipes.Single(x => x.Id == "white toast|lifeskill|0");
        Assert.AreEqual((new ItemKey("white toast"), 1, "lifeskill", false, RecipeConfidence.Complete, "screen:craft.fav/1"),
            (toast.Product, toast.OutputQty, toast.Station, toast.UserVerified, toast.Confidence, toast.SourceCapture));
        CollectionAssert.AreEqual(
            new[] { new MaterialRequirement(new ItemKey(Wheat), 1), new MaterialRequirement(new ItemKey(Salt), 3), new MaterialRequirement(new ItemKey(Grapes), 3) },
            toast.Materials.ToArray());
        Assert.AreEqual("screen:craft.fav/3", k.Recipes.Single(x => x.Product == new ItemKey("omelette")).SourceCapture);

        // stock: every product and every material, owned as read (the omelette's 0 is a real 0 read from the badge)
        Assert.AreEqual(25L, k.Items[new ItemKey("white toast")].Count);
        Assert.AreEqual(7L, k.Items[new ItemKey("grape juice")].Count);
        Assert.AreEqual(0L, k.Items[new ItemKey("omelette")].Count);
        Assert.AreEqual((40L, 45L, 340L, 12L), (k.Items[new ItemKey(Wheat)].Count, k.Items[new ItemKey(Salt)].Count, k.Items[new ItemKey(Grapes)].Count, k.Items[new ItemKey(Egg)].Count));
        Assert.IsTrue(k.Observations.All(o => o.Kind == ObservationKind.CraftViewPair && o.AtLog.Offset > 0));
        Assert.IsTrue(k.Observations.Where(o => o.Item.Value.StartsWith("icon:", StringComparison.Ordinal)).All(o => o.Coverage == "ability:craft.pairs"));
        Assert.AreEqual("ability:craft.product", k.Observations.First(o => o.Item == new ItemKey("white toast")).Coverage);
        Assert.AreEqual(3, k.Saves, "saved after every stored slot");

        // journal: one record per slot + the end, codes only
        var slots = w.Ctx.JournalFake.Records.Where(j => j.Kind == JournalKind.Stock && Equals(j.Fields["event"], "scanFavorites.slot")).ToList();
        CollectionAssert.AreEqual(new object[] { "recorded", "recorded", "recorded", "repeat", "repeat" }, slots.Select(j => j.Fields["code"]!).ToArray(), "the repeat is looked at twice (R2.7)");
        CollectionAssert.AreEqual(new object[] { 1L, 2L, 3L, 4L, 4L }, slots.Select(j => j.Fields["slot"]!).ToArray());
        Assert.IsTrue(slots.All(j => Equals(j.Fields["tip"], "tip.notLogged") && j.Fields["itemId"] is null), "no tip logged: the OCR titles are kept");
        Assert.AreEqual((0L, 5L), ((long)r.Data["tips"]!, (long)r.Data["tipsMissing"]!));
        Assert.AreEqual(1, w.Ctx.JournalFake.Records.Count(j => Equals(j.Fields.GetValueOrDefault("event"), "favorites.endRetry")));
        Assert.IsTrue(w.Ctx.JournalFake.Records.Any(j => Equals(j.Fields.GetValueOrDefault("event"), "scanFavorites.end") && Equals(j.Fields["code"], "favorites.scanned")));
        Assert.IsTrue(w.Ctx.JournalFake.Records.All(j => j.Fields.Values.All(v => v is not string s || Codes.IsCode(s))), "codes only");
    }

    [TestMethod]
    public void TenDifferentFavourites_AreAllScanned()
    {
        var panels = Enumerable.Range(1, 10).Select(i => new Panel("Dish " + i, i, (Wheat, 40, i))).ToArray();
        var w = new ScanWorld(panels);
        var r = w.Run();
        Assert.AreEqual((JobState.Succeeded, "favorites.scanned"), Code(r));
        Assert.HasCount(10, w.SlotRuns);
        Assert.AreEqual((10L, 10L, "allSlots"), ((long)r.Data!["slots"]!, (long)r.Data["recipes"]!, (string)r.Data["end"]!));
        Assert.AreEqual("screen:craft.fav/10", w.Ctx.KnowledgeFake!.Recipes.Single(x => x.Product == new ItemKey("dish 10")).SourceCapture);
    }

    [TestMethod]
    public void TheLoggedItemName_ReplacesAGarbageOcrTitle_AlsoForTheRepeatCheck()
    {
        // live 2026-10-15: OCR read "ams" for two different dishes; as titles slot 2 would look like a repeat of slot 1
        var w = new ScanWorld(Toast with { Title = "ams" }, Juice with { Title = "ams" }, Omelette with { Title = "om pizza" }, Omelette with { Title = "i pizza" })
        {
            Tips = slot => slot switch { 1 => ("White Toast", 1051000), 2 => ("Grape Juice", 1051001), _ => ("Omelette", 1051002) },
        };
        var r = w.Run();

        Assert.AreEqual((JobState.Succeeded, "favorites.scanned"), Code(r));
        Assert.AreEqual((4L, 3L, "repeat", 5L, 0L), ((long)r.Data!["slots"]!, (long)r.Data["recipes"]!, (string)r.Data["end"]!, (long)r.Data["tips"]!, (long)r.Data["tipsMissing"]!),
            "slot 2 is a new dish, slot 4 (empty: the omelette again) is the repeat although OCR read another title");
        var k = w.Ctx.KnowledgeFake!;
        CollectionAssert.AreEquivalent(new[] { "white toast", "grape juice", "omelette" }, k.Recipes.Select(x => x.Product.Value).ToArray());
        Assert.AreEqual("screen:craft.fav/2", k.Recipes.Single(x => x.Product == new ItemKey("grape juice")).SourceCapture);
        Assert.AreEqual(25L, k.Items[new ItemKey("white toast")].Count);
        Assert.IsFalse(k.Items.ContainsKey(new ItemKey("ams")));
        Assert.HasCount(5, w.TipRuns, "the repeat slot's tip is read on both looks (R2.7)");

        var slots = w.Ctx.JournalFake.Records.Where(j => Equals(j.Fields.GetValueOrDefault("event"), "scanFavorites.slot")).ToList();
        CollectionAssert.AreEqual(new object[] { 1051000L, 1051001L, 1051002L, 1051002L, 1051002L }, slots.Select(j => j.Fields["itemId"]!).ToArray());
        Assert.IsFalse(slots.Any(j => j.Fields.ContainsKey("itemName")), "codes and ids only: the names go to the store");
        Assert.IsTrue(slots.All(j => Equals(j.Fields["tip"], "tip.logged")));
        Assert.IsTrue(w.Ctx.JournalFake.Records.All(j => j.Fields.Values.All(v => v is not string s || Codes.IsCode(s))), "codes only");
    }

    [TestMethod]
    public void ATipLoggedBeforeTheClick_IsIgnored_TheOcrTitleStays()
    {
        // slot 2 logs no tip: slot 1's tip (still the latest in the log) must not make it a repeat of slot 1
        var w = new ScanWorld(Toast with { Title = "ams" }, Juice with { Title = "i pizza" }, Omelette, Omelette)
        {
            Tips = slot => slot == 1 ? ("White Toast", 1051000) : null,
        };
        w.Ctx.Ui = T.Snapshot(1000) with { LastItemTip = new ItemTipInfo("Lime Water", 20330001, new DateTime(2026, 10, 15, 1, 40, 0), T.Pos(950)) };
        var r = w.Run();

        Assert.AreEqual((JobState.Succeeded, "repeat"), (r.State, (string)r.Data!["end"]!));
        CollectionAssert.AreEquivalent(new[] { "white toast", "i pizza", "omelette" }, w.Ctx.KnowledgeFake!.Recipes.Select(x => x.Product.Value).ToArray());
        var slots = w.Ctx.JournalFake.Records.Where(j => Equals(j.Fields.GetValueOrDefault("event"), "scanFavorites.slot")).ToList();
        CollectionAssert.AreEqual(new object[] { "tip.logged", "tip.beforeClick", "tip.beforeClick", "tip.beforeClick", "tip.beforeClick" }, slots.Select(j => j.Fields["tip"]!).ToArray(), "the repeat slot is looked at twice (R2.7)");
        Assert.IsTrue(slots.Skip(1).All(j => j.Fields["itemId"] is null && !j.Fields.ContainsKey("itemName")));

        // a tip from before the job is not the first slot's tip either
        var before = new ScanWorld(Toast with { Title = "ams" }, Toast with { Title = "ams" });
        before.Ctx.Ui = T.Snapshot(1000) with { LastItemTip = new ItemTipInfo("Lime Water", 20330001, new DateTime(2026, 10, 15, 1, 40, 0), T.Pos(950)) };
        before.Run();
        Assert.AreEqual("ams", before.Ctx.KnowledgeFake!.Recipes.Single().Product.Value);
    }

    [TestMethod]
    public void ATipRunThatDoesNotSucceed_EndsTheScan_AndABrokenTipProcedureFailsBeforeAnyInput()
    {
        var w = new ScanWorld(Toast, Juice, Omelette) { EndTipWith = (2, ProcedureStatus.Stopped) };
        var r = w.Run();
        Assert.AreEqual((JobState.Stopped, "tip.stopped"), Code(r));
        Assert.HasCount(1, w.Ctx.KnowledgeFake!.Recipes, "slot 1 was stored before the stop");

        var broken = new ScanWorld(Toast);
        var tip = broken.Catalog.Get("crafting.productTip")!;
        broken.Catalog.Put(tip with { Steps = tip.Steps.Select(s => s.Id == "tip.close" ? s with { Action = new KeyAction(GameKey.B) } : s).ToArray() });
        Assert.AreEqual((JobState.Failed, "procedure.invalid:crafting.productTip"), Code(broken.Run()));
        Assert.HasCount(0, broken.Ctx.Runner.Runs);
    }

    [TestMethod]
    public void ASlightlyDifferentReadingOfAKnownIcon_KeepsTheKnownKey()
    {
        var near = "icon:1" + new string('0', 63); // 1 bit away from Wheat
        Assert.AreEqual(1, IconKeys.Distance(near, Wheat));
        var w = new ScanWorld(new Panel("Bread", 3, (near, 41, 2)));
        w.Ctx.KnowledgeFake!.Items[new ItemKey(Wheat)] = new StockEntry(new ItemKey(Wheat), 40, null, StockSource.Observed, DateTimeOffset.UnixEpoch, T.Pos(10), 0.9, "ability:craft.pairs", false);
        w.Run();
        var bread = w.Ctx.KnowledgeFake.Recipes.Single();
        Assert.AreEqual(new ItemKey(Wheat), bread.Materials.Single().Item, "resolved to the stored key");
        Assert.AreEqual(41L, w.Ctx.KnowledgeFake.Items[new ItemKey(Wheat)].Count);
        Assert.IsFalse(w.Ctx.KnowledgeFake.Items.ContainsKey(new ItemKey(near)));
        Assert.AreEqual(Wheat, IconKeys.Resolve(near, [Salt, Wheat]));
        Assert.AreEqual(Egg, IconKeys.Resolve(Egg, [Salt, Wheat]), "a new icon keeps its own key");
    }

    [TestMethod]
    public void UnreadValuesStayUnknown_ARecipeIsPartial_AndNeverDowngradesAComplete()
    {
        // pair 2 unread: the recipe lists only material 1 and is Partial; salt's stock is not touched
        var partial = new Panel("White Toast", null, (Wheat, 40, 1), (Salt, null, null));
        var w = new ScanWorld(partial, Juice, Juice);
        var r = w.Run();
        Assert.AreEqual(JobState.Succeeded, r.State);
        var toast = w.Ctx.KnowledgeFake!.Recipes.Single(x => x.Product == new ItemKey("white toast"));
        Assert.AreEqual(RecipeConfidence.Partial, toast.Confidence);
        Assert.AreEqual(new MaterialRequirement(new ItemKey(Wheat), 1), toast.Materials.Single());
        Assert.IsFalse(w.Ctx.KnowledgeFake.Items.ContainsKey(new ItemKey(Salt)), "unknown, never 0");
        Assert.IsFalse(w.Ctx.KnowledgeFake.Items.ContainsKey(new ItemKey("white toast")), "an unread badge stays unknown");

        // a complete recipe already stored is kept when a later scan reads it only partially
        var w2 = new ScanWorld(partial, Juice, Juice);
        var complete = new Recipe("white toast|lifeskill|0", new ItemKey("white toast"), 1, "lifeskill",
            [new MaterialRequirement(new ItemKey(Wheat), 1), new MaterialRequirement(new ItemKey(Salt), 3)], [], null, DateTimeOffset.UnixEpoch, "screen:craft.fav/1", true, RecipeConfidence.Complete, null, null);
        w2.Ctx.KnowledgeFake!.Recipes.Add(complete);
        w2.Run();
        var kept = w2.Ctx.KnowledgeFake.Recipes.Single(x => x.Id == complete.Id);
        CollectionAssert.AreEqual(complete.Materials.ToArray(), kept.Materials.ToArray(), "the complete content stays");
        Assert.AreEqual((RecipeConfidence.Complete, "screen:craft.fav/1"), (kept.Confidence, kept.SourceCapture));
        Assert.IsTrue(kept.ObservedAtUtc > complete.ObservedAtUtc, "seen in its slot by this scan");
        Assert.IsTrue(w2.Ctx.JournalFake.Records.Any(j => Equals(j.Fields.GetValueOrDefault("code"), "recipe.keptComplete")));
        Assert.AreEqual(40L, w2.Ctx.KnowledgeFake.Items[new ItemKey(Wheat)].Count, "the stock read still counts");
    }

    [TestMethod]
    public void AKeptCompleteRecipe_MovesToTheSlotItWasSeenIn_AndFormerFavouritesGiveUpTheirSlots()
    {
        // live 2026-10-18: the user reordered favourites; three slots read blurry kept their complete recipes with the
        // old slots, and recipes removed from favourites kept claiming slots: the window greyed out Start for them
        var partial = new Panel("White Toast", null, (Wheat, 40, 1), (Salt, null, null));
        var w = new ScanWorld(Juice, partial, partial);
        var k = w.Ctx.KnowledgeFake!;
        Recipe Stored(string name, string source, params MaterialRequirement[] m) =>
            new(name + "|lifeskill|0", new ItemKey(name), 1, "lifeskill", m, [], null, DateTimeOffset.UnixEpoch, source, false, RecipeConfidence.Complete, null, null);
        k.Recipes.Add(Stored("white toast", "screen:craft.fav/5", new MaterialRequirement(new ItemKey(Wheat), 1), new MaterialRequirement(new ItemKey(Salt), 3)));
        k.Recipes.Add(Stored("old bread", "screen:craft.fav/2", new MaterialRequirement(new ItemKey(Egg), 2)));
        k.Recipes.Add(Stored("lime water", "screen:craft.fav/7", new MaterialRequirement(new ItemKey(Egg), 1)));
        k.Recipes.Add(Stored("roast chicken", "screen:craft.list/4", new MaterialRequirement(new ItemKey(Egg), 3)));
        var r = w.Run();

        Assert.AreEqual((JobState.Succeeded, "repeat", 2L), (r.State, (string)r.Data!["end"]!, (long)r.Data["recipesReleased"]!));
        Assert.AreEqual("screen:craft.fav/1", k.Recipes.Single(x => x.Product == new ItemKey("grape juice")).SourceCapture);
        var toast = k.Recipes.Single(x => x.Product == new ItemKey("white toast"));
        Assert.AreEqual(("screen:craft.fav/2", RecipeConfidence.Complete, 2), (toast.SourceCapture, toast.Confidence, toast.Materials.Count), "moved from slot 5, content kept");
        Assert.AreEqual("screen:craft.fav.old/2", k.Recipes.Single(x => x.Product == new ItemKey("old bread")).SourceCapture, "slot 2 holds the toast now");
        Assert.AreEqual("screen:craft.fav.old/7", k.Recipes.Single(x => x.Product == new ItemKey("lime water")).SourceCapture, "no longer a favourite");
        Assert.AreEqual("screen:craft.list/4", k.Recipes.Single(x => x.Product == new ItemKey("roast chicken")).SourceCapture, "list recipes are not touched");

        // a scan that does not reach the end of the favourites releases nothing; an unreadable slot keeps its claim
        var cut = new ScanWorld(Juice, Toast, Omelette) { EndTipWith = (2, ProcedureStatus.Stopped) };
        cut.Ctx.KnowledgeFake!.Recipes.Add(Stored("lime water", "screen:craft.fav/7", new MaterialRequirement(new ItemKey(Egg), 1)));
        cut.Run();
        Assert.AreEqual("screen:craft.fav/7", cut.Ctx.KnowledgeFake.Recipes.Single(x => x.Product == new ItemKey("lime water")).SourceCapture);

        var blurry = new ScanWorld(Juice, new Panel(null, 9, (Egg, 12, 2)), Juice);
        blurry.Ctx.KnowledgeFake!.Recipes.Add(Stored("lime water", "screen:craft.fav/2", new MaterialRequirement(new ItemKey(Egg), 1)));
        blurry.Run();
        Assert.AreEqual("screen:craft.fav/2", blurry.Ctx.KnowledgeFake.Recipes.Single(x => x.Product == new ItemKey("lime water")).SourceCapture, "slot 2 could not be named");
    }

    // incident 29 (2026-10-28, scan of 2026-10-25 21:47:15): the title OCR read "Sashimi" on the Longevity Sashimi panel
    // (4 materials, one amount unread); the stored Sashimi (1 material) was taken by its title alone, no tip was pressed,
    // and slot 4 belonged to Sashimi for three days
    [TestMethod]
    public void ATitleNamingAStoredRecipeWithOtherMaterials_AsksTheGame_AndTheSlotGoesToTheDishShown()
    {
        var longevity = new Panel("Sashimi", 5, (Wheat, 40, 6), (Salt, 45, 3), (Grapes, 340, null));
        Recipe Sashimi() => new("sashimi|lifeskill|0", new ItemKey("sashimi"), 1, "lifeskill", [new MaterialRequirement(new ItemKey(Egg), 3)], [], null,
            DateTimeOffset.UnixEpoch, "screen:craft.fav/1", false, RecipeConfidence.Complete, null, null, ProductItemId: 1021000);

        var w = new ScanWorld(longevity, longevity) { Tips = _ => ("Longevity Sashimi", 1064030) };
        w.Ctx.KnowledgeFake!.Recipes.Add(Sashimi());
        var r = w.Run();
        Assert.AreEqual((JobState.Succeeded, "repeat"), (r.State, (string)r.Data!["end"]!));
        Assert.HasCount(3, w.TipRuns, "the title did not settle it: the game was asked (the repeat slot twice, R2.7)");
        var k = w.Ctx.KnowledgeFake;
        Assert.AreEqual(("screen:craft.fav/1", 1064030L), (k.Recipes.Single(x => x.Product == new ItemKey("longevity sashimi")).SourceCapture, k.Recipes.Single(x => x.Product == new ItemKey("longevity sashimi")).ProductItemId));
        Assert.AreEqual("screen:craft.fav.old/1", k.Recipes.Single(x => x.Product == new ItemKey("sashimi")).SourceCapture);
        Assert.AreEqual(5L, k.Items[new ItemKey("longevity sashimi")].Count);
        Assert.IsFalse(k.Items.ContainsKey(new ItemKey("sashimi")), "the panel's owned count is not Sashimi's");

        // no tip logged: the OCR title alone neither takes Sashimi's slot nor its count
        var silent = new ScanWorld(longevity, longevity);
        silent.Ctx.KnowledgeFake!.Recipes.Add(Sashimi());
        silent.Run();
        var s = silent.Ctx.KnowledgeFake;
        Assert.AreEqual(("screen:craft.fav.old/1", 1), (s.Recipes.Single(x => x.Product == new ItemKey("sashimi")).SourceCapture, s.Recipes.Single(x => x.Product == new ItemKey("sashimi")).Materials.Count));
        Assert.IsFalse(s.Items.ContainsKey(new ItemKey("sashimi")));
        Assert.IsTrue(silent.Ctx.JournalFake.Records.Any(j => Equals(j.Fields.GetValueOrDefault("code"), "recipe.nameConflict")));

        // a title whose stored recipe fits the materials shown still needs no tip
        var fits = new ScanWorld(Toast, Toast);
        fits.Ctx.KnowledgeFake!.Recipes.Add(new Recipe("white toast|lifeskill|0", new ItemKey("white toast"), 1, "lifeskill",
            [new MaterialRequirement(new ItemKey(Wheat), 1), new MaterialRequirement(new ItemKey(Salt), 3), new MaterialRequirement(new ItemKey(Grapes), 3)], [], null,
            DateTimeOffset.UnixEpoch, "screen:craft.fav/1", false, RecipeConfidence.Complete, null, null, ProductItemId: 1051000));
        fits.Run();
        Assert.HasCount(0, fits.TipRuns);
        Assert.AreEqual("screen:craft.fav/1", fits.Ctx.KnowledgeFake.Recipes.Single().SourceCapture);
    }

    [TestMethod]
    public void AnUnreadableTitle_StoresNoRecipe_ButTheScanGoesOn()
    {
        var noTitle = new Panel(null, 9, (Egg, 12, 2));
        var w = new ScanWorld(Toast, noTitle, Juice, Juice);
        var r = w.Run();
        Assert.AreEqual((JobState.Succeeded, 4L, 2L, 1L), (r.State, (long)r.Data!["slots"]!, (long)r.Data["recipes"]!, (long)r.Data["unreadable"]!));
        Assert.HasCount(2, w.Ctx.KnowledgeFake!.Recipes);
        Assert.AreEqual(12L, w.Ctx.KnowledgeFake.Items[new ItemKey(Egg)].Count, "the material stock is still observed");
    }

    // R2.7 (audit 23): one lost slot click read as "the end of My Favorites" released every later favourite
    [TestMethod]
    public void ABlankPanelThatIsNotBlankOnTheSecondLook_DoesNotEndTheScan()
    {
        var w = new ScanWorld(Toast, Juice, Omelette, Omelette) { BlankOnceAtSlot = 2 };

        var r = w.Run();

        Assert.AreEqual((JobState.Succeeded, "favorites.scanned"), Code(r));
        Assert.AreEqual((4L, 3L, "repeat"), ((long)r.Data!["slots"]!, (long)r.Data["recipes"]!, (string)r.Data["end"]!), "slots 3 and 4 were still read");
        CollectionAssert.AreEquivalent(new[] { "white toast", "grape juice", "omelette" }, w.Ctx.KnowledgeFake!.Recipes.Select(x => x.Product.Value).ToArray());
    }

    [TestMethod]
    public void ABlankPanelOrAMissingPanel_EndsTheScan_AsNothingSelected()
    {
        var blank = new ScanWorld(Toast, new Panel(null, null));
        var r = blank.Run();
        Assert.AreEqual((JobState.Succeeded, "nothingSelected"), (r.State, (string)r.Data!["end"]!));
        Assert.HasCount(3, blank.SlotRuns, "the blank slot is looked at twice (R2.7)");
        Assert.HasCount(1, blank.TipRuns, "no tip is asked for a blank panel");

        var gone = new ScanWorld(Toast, Juice) { MismatchFromSlot = 3 };
        var g = gone.Run();
        Assert.AreEqual((JobState.Succeeded, "nothingSelected", 2L), (g.State, (string)g.Data!["end"]!, (long)g.Data["recipes"]!));

        // on slot 1 a missing panel is the wrong screen, not the end of the favourites
        var wrong = new ScanWorld(Toast) { MismatchFromSlot = 1 };
        Assert.AreEqual((JobState.Failed, "read.screenMismatch:craft.product"), Code(wrong.Run()));
        Assert.HasCount(0, wrong.Ctx.KnowledgeFake!.Recipes);
    }

    [TestMethod]
    public void AnyOtherProcedureEnd_StopsTheScan_KeepingTheSlotsAlreadyStored()
    {
        foreach (var (status, state, code) in new[]
        {
            (ProcedureStatus.Halted, JobState.Halted, "ForbiddenUi"),
            (ProcedureStatus.Stopped, JobState.Stopped, "slot.stopped"),
            (ProcedureStatus.Parked, JobState.Parked, "slot.parked"),
            (ProcedureStatus.Cancelled, JobState.Cancelled, "slot.cancelled"),
        })
        {
            var w = new ScanWorld(Toast, Juice, Omelette) { EndSlotWith = (2, status) };
            var r = w.Run();
            Assert.AreEqual((state, code), Code(r), status.ToString());
            Assert.HasCount(2, w.SlotRuns);
            Assert.HasCount(1, w.Ctx.KnowledgeFake!.Recipes, "slot 1 was stored before the stop");
            Assert.AreEqual(1, w.Ctx.KnowledgeFake.Saves);
        }

        // a needsSurvey reader ends it NeedsSurvey
        var ns = new ScanWorld(Toast) { ProductNeedsSurvey = true };
        Assert.AreEqual(JobState.NeedsSurvey, ns.Run().State);
    }

    [TestMethod]
    public void AStopDuringThePauseEndsTheJobAndIsJournaled()
    {
        using var cts = new CancellationTokenSource();
        var w = new ScanWorld(Toast, Juice) { AfterSlot = n => { if (n == 1) { cts.Cancel(); } } };
        Assert.AreEqual((JobState.Cancelled, "cancelled"), Code(w.Run(cts.Token)));
        Assert.HasCount(1, w.SlotRuns);
        Assert.IsTrue(w.Ctx.JournalFake.Records.Any(j => Equals(j.Fields.GetValueOrDefault("event"), "scanFavorites.end") && Equals(j.Fields["code"], "cancelled")));
    }

    // ------------------------------------------------------------------ material item tips and item ids (2026-10-15)

    [TestMethod]
    public void EveryUnknownMaterialIcon_IsPressedOnce_AndTheRecipeAndStockUseTheLoggedNamesAndIds()
    {
        var items = new Dictionary<string, (string, long)> { [Wheat] = ("Wheat", 2001), [Salt] = ("Salt", 2002), [Grapes] = ("Grapes", 2003) };
        var w = new ScanWorld(Toast, Juice, Juice)
        {
            Tiles = true,
            Tips = slot => slot == 1 ? ("White Toast", 1051000) : ("Grape Juice", 1051001),
            MaterialTips = icon => items.TryGetValue(icon, out var item) ? item : null,
        };
        var r = w.Run();

        Assert.AreEqual((JobState.Succeeded, "favorites.scanned"), Code(r));
        CollectionAssert.AreEqual(new[] { new PointI(-139, 77), new PointI(-7, 77), new PointI(125, 77) }, w.MaterialPresses.ToArray(),
            "the three tiles of slot 1 at their centres; slot 2's grapes are known by then, slot 3 is the repeat");
        Assert.AreEqual((3L, 3L), ((long)r.Data!["materialTips"]!, (long)r.Data["iconsLearned"]!));
        Assert.AreEqual(TimeSpan.FromMilliseconds(6 * 300), w.Ctx.ClockFake.Monotonic - w.StartMono, "0.3 s before every slot run after the first (the repeat is looked at twice) and before every material press");

        var k = w.Ctx.KnowledgeFake!;
        CollectionAssert.AreEquivalent(new[] { Wheat, Salt, Grapes }, k.IconItems.Select(i => i.IconKey).ToArray());
        var toast = k.Recipes.Single(x => x.Product == new ItemKey("white toast"));
        Assert.AreEqual((long?)1051000, toast.ProductItemId);
        CollectionAssert.AreEqual(
            new[] { new MaterialRequirement(new ItemKey("wheat"), 1, 2001), new MaterialRequirement(new ItemKey("salt"), 3, 2002), new MaterialRequirement(new ItemKey("grapes"), 3, 2003) },
            toast.Materials.ToArray());
        Assert.AreEqual(new MaterialRequirement(new ItemKey("grapes"), 5, 2003), k.Recipes.Single(x => x.Product == new ItemKey("grape juice")).Materials.Single());
        Assert.AreEqual((40L, (long?)2001), (k.Items[new ItemKey("wheat")].Count, k.Items[new ItemKey("wheat")].ItemId));
        Assert.AreEqual((long?)1051000, k.Items[new ItemKey("white toast")].ItemId);
        Assert.IsFalse(k.Items.Keys.Any(i => i.Value.StartsWith("icon:", StringComparison.Ordinal)), "no material is left under its icon");

        // journal: one record per press, codes and ids only (no names)
        var presses = w.Ctx.JournalFake.Records.Where(j => Equals(j.Fields.GetValueOrDefault("event"), "tip.material")).ToList();
        CollectionAssert.AreEqual(new object[] { 2001L, 2002L, 2003L }, presses.Select(j => j.Fields["itemId"]!).ToArray());
        Assert.IsTrue(presses.All(j => Equals(j.Fields["code"], "tip.logged") && Equals(j.Fields["slot"], 1L) && !j.Fields.ContainsKey("itemName")));
        Assert.IsTrue(w.Ctx.JournalFake.Records.All(j => j.Fields.Values.All(v => v is not string s || Codes.IsCode(s))), "codes only");

        // a later scan presses no known icon again
        var again = new ScanWorld(Toast, Toast) { Tiles = true, Tips = _ => ("White Toast", 1051000), MaterialTips = icon => items[icon] };
        again.Ctx.KnowledgeFake!.IconItems.AddRange(k.IconItems);
        again.Run();
        Assert.IsEmpty(again.MaterialPresses);
        Assert.AreEqual(new MaterialRequirement(new ItemKey("salt"), 3, 2002), again.Ctx.KnowledgeFake.Recipes.Single().Materials[1], "named from the stored icon items");
    }

    [TestMethod]
    public void AMaterialTipNotLogged_LeavesTheIconKey_AndAFailedPressEndsTheScan()
    {
        var w = new ScanWorld(Toast, Toast) { Tiles = true, MaterialTips = icon => icon == Salt ? ("Salt", 2002) : null };
        w.Run();
        var toast = w.Ctx.KnowledgeFake!.Recipes.Single();
        CollectionAssert.AreEqual(new[] { new ItemKey(Wheat), new ItemKey("salt"), new ItemKey(Grapes) }, toast.Materials.Select(m => m.Item).ToArray());
        CollectionAssert.AreEqual(new object[] { "tip.notLogged", "tip.logged", "tip.beforeClick" },
            w.Ctx.JournalFake.Records.Where(j => Equals(j.Fields.GetValueOrDefault("event"), "tip.material")).Select(j => j.Fields["code"]!).ToArray());
        Assert.HasCount(3, w.MaterialPresses, "the repeat (slot 2) presses nothing");

        // an icon whose tip was not logged is not pressed again in the same job (slot 2's grapes)
        var once = new ScanWorld(Toast, Juice, Juice) { Tiles = true, MaterialTips = _ => null };
        once.Run();
        Assert.HasCount(3, once.MaterialPresses);
        Assert.IsEmpty(once.Ctx.KnowledgeFake!.IconItems);

        // a press that does not succeed ends the scan before the recipe is stored
        var stopped = new ScanWorld(Toast, Juice) { Tiles = true, EndMaterialTipWith = (2, ProcedureStatus.Stopped) };
        Assert.AreEqual((JobState.Stopped, "tip.stopped"), Code(stopped.Run()));
        Assert.HasCount(2, stopped.MaterialPresses);
        Assert.IsEmpty(stopped.Ctx.KnowledgeFake!.Recipes);

        // without measured tiles (or anchor) nothing is pressed
        var noTiles = new ScanWorld(Toast, Toast) { MaterialTips = _ => ("X", 1) };
        noTiles.Run();
        Assert.IsEmpty(noTiles.MaterialPresses);
        Assert.HasCount(3, noTiles.TipRuns, "only the product tips (the repeat slot twice)");
    }

    [TestMethod]
    public void AProductNamedFromTheLog_ReplacesTheOcrOnlyRecipeOfTheSameSlotOrMaterials_NotDuplicates()
    {
        var w = new ScanWorld(Toast with { Title = "ams" }, Toast with { Title = "ams" }) { Tips = _ => ("White Toast", 1051000) };
        var k = w.Ctx.KnowledgeFake!;
        static Recipe Ocr(string name, string source, params (string Icon, int Qty)[] materials) => new(
            name + "|lifeskill|0", new ItemKey(name), 1, "lifeskill", [.. materials.Select(m => new MaterialRequirement(new ItemKey(m.Icon), m.Qty))], [], null,
            DateTimeOffset.UnixEpoch, source, false, RecipeConfidence.Partial, null, null);
        k.Recipes.Add(Ocr("ams", "screen:craft.fav/1", (Wheat, 1)));
        k.Recipes.Add(Ocr("i pizza", "screen:craft.list/6", (Grapes, 3), (Wheat, 1), (Salt, 3)));
        k.Recipes.Add(Ocr("om pizza", "screen:craft.fav/9", (Egg, 2)));
        var r = w.Run();

        CollectionAssert.AreEquivalent(new[] { "white toast", "om pizza" }, k.Recipes.Select(x => x.Product.Value).ToArray(),
            "'ams' (same slot) and 'i pizza' (same materials) were this recipe; 'om pizza' is another one");
        Assert.AreEqual(2L, (long)r.Data!["recipesReplaced"]!);
        Assert.AreEqual(2L, w.Ctx.JournalFake.Records.Single(j => Equals(j.Fields.GetValueOrDefault("event"), "scanFavorites.slot") && Equals(j.Fields["slot"], 1L)).Fields["replaced"]);

        // a title read by OCR only (no logged name) never replaces anything
        var ocrOnly = new ScanWorld(Toast with { Title = "ams" }, Toast with { Title = "ams" });
        ocrOnly.Ctx.KnowledgeFake!.Recipes.Add(Ocr("i pizza", "screen:craft.fav/1", (Wheat, 1)));
        ocrOnly.Run();
        CollectionAssert.AreEquivalent(new[] { "ams", "i pizza" }, ocrOnly.Ctx.KnowledgeFake.Recipes.Select(x => x.Product.Value).ToArray());
    }

    // ------------------------------------------------------------------ gates: refuses only what it must

    [TestMethod]
    public void RunsWithTheCraftingSwitchOff()
    {
        // a read-only scan: no switch (and since 4.33 no per-character permission) is asked
        var w = new ScanWorld(Toast, Toast);
        w.Ctx.SwitchesFake.Crafting = false;
        w.Ctx.SwitchesFake.Homestead = false;
        var r = w.Run();
        Assert.AreEqual(JobState.Succeeded, r.State, r.Code);
        Assert.HasCount(1, w.Ctx.KnowledgeFake!.Recipes);
    }

    [TestMethod]
    public void RefusesWithZeroRuns_OnlyWithoutKnowledgeOrSlideClicks_OrDuringACraft()
    {
        var noKnowledge = new ScanWorld(Toast);
        noKnowledge.Ctx.KnowledgeFake = null;
        Assert.AreEqual((JobState.Refused, "knowledge.unavailable"), Code(noKnowledge.Run()));
        Assert.HasCount(0, noKnowledge.Ctx.Runner.Runs);

        var noClicks = new ScanWorld(Toast);
        noClicks.Ctx.SwitchesFake.SlideClickEnabled = false;
        Assert.AreEqual((JobState.Refused, "slideClick.off"), Code(noClicks.Run()));
        Assert.HasCount(0, noClicks.Ctx.Runner.Runs);

        var crafting = new ScanWorld(Toast);
        crafting.Ctx.Ui = T.Snapshot(100, new CraftSessionInfo(true, "UI_LIFE_ALCHEMIST_MAKE_POT", DateTime.Now, T.Pos(50), 3, null, null, false));
        Assert.AreEqual((JobState.Refused, "craft.inProgress"), Code(crafting.Run()));
        Assert.HasCount(0, crafting.Ctx.Runner.Runs);
    }

    [TestMethod]
    public void AMissingOpenAbilityFailsBeforeAnyInput()
    {
        var w = new ScanWorld(withOpenAbility: false, Toast);
        Assert.AreEqual((JobState.Failed, "procedure.missing:OpenAbility"), Code(w.Run()));
        Assert.HasCount(0, w.Ctx.Runner.Runs);
    }

    private static (JobState, string) Code(JobResult r) => (r.State, r.Code);

    private static string SlotStep(int slot) => "slot." + slot.ToString("00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Fake engine for ScanFavorites: each slot run returns the scripted panel as craft.product + craft.pairs reads.</summary>
    private sealed class ScanWorld
    {
        private readonly FeatureHost _host;
        private readonly Panel[] _panels;
        private long _offset = 1000;
        private int _slot;

        public ScanWorld(params Panel[] panels)
            : this(true, panels)
        {
        }

        public ScanWorld(bool withOpenAbility, params Panel[] panels)
        {
            _panels = panels;
            var (host, env, _) = T.LoadReal();
            _host = host;
            if (withOpenAbility)
            {
                env.Procedures.Put(new ProcedureDefinition("OpenAbility", "core", [new StepDefinition("open", StatePredicate.Any, new ClickAction("menu.ability"), Expectation.Nothing)], ProcedureLimits.Default));
            }

            Catalog = env.Procedures;
            Ctx = new FakeJobContext(env.Procedures) { Ui = T.Snapshot(_offset) };
            Ctx.Runner.Script = Engine;
            StartMono = Ctx.ClockFake.Monotonic;
        }

        public FakeJobContext Ctx { get; }

        public FakeProcedureBuilder Catalog { get; }

        public TimeSpan StartMono { get; }

        public (int Slot, ProcedureStatus Status)? EndSlotWith { get; init; }

        public int? MismatchFromSlot { get; init; }

        /// <summary>The slot whose first look reads a blank panel (a lost click); its second look reads the scripted panel.</summary>
        public int? BlankOnceAtSlot { get; set; }

        public bool ProductNeedsSurvey { get; init; }

        public Action<int>? AfterSlot { get; init; }

        /// <summary>Scripted per slot: the item tip the game logs when the product icon is pressed (null = nothing; default nothing).</summary>
        public Func<int, (string Name, long Id)?>? Tips { get; init; }

        /// <summary>Scripted per slot: that slot's tip run ends with this status.</summary>
        public (int Slot, ProcedureStatus Status)? EndTipWith { get; init; }

        /// <summary>The pairs reads carry the tile rects and the header anchor (as the real tile-row reader does).</summary>
        public bool Tiles { get; init; }

        /// <summary>Scripted per material icon: the item tip the game logs when that tile is pressed (null = nothing).</summary>
        public Func<string, (string Name, long Id)?>? MaterialTips { get; init; }

        /// <summary>Scripted: the n-th material press (1-based) ends with this status.</summary>
        public (int Press, ProcedureStatus Status)? EndMaterialTipWith { get; init; }

        /// <summary>The offsets of the material tip presses, in order.</summary>
        public List<PointI> MaterialPresses { get; } = [];

        /// <summary>The 3-material tile lefts (full scale) and the header anchor of craft.pairs.</summary>
        public static readonly int[] TileLefts = [1088, 1220, 1352];

        public static readonly PointI Header = new(1270, 641);

        public List<ProcedureDefinition> SlotRuns => Ctx.Runner.Runs.Where(r => r.Id == "crafting.scanFavorites").ToList();

        public List<ProcedureDefinition> TipRuns => Ctx.Runner.Runs.Where(r => r.Id == "crafting.productTip").ToList();

        public JobResult Run(CancellationToken ct = default) => T.Handler(_host, "ScanFavorites").Execute(Ctx, JobParameters.Empty, ct);

        private ProcedureResult Engine(ProcedureDefinition p)
        {
            if (p.Id == "crafting.productTip")
            {
                var click = (ClickAction)p.Steps[0].Action;
                Assert.AreEqual("craft.materials.header", click.TemplateId);
                if (click.Offset != new PointI(-140, -266))
                {
                    // a material tile pressed: the tip of the item behind that tile's icon
                    MaterialPresses.Add(click.Offset!.Value);
                    if (EndMaterialTipWith is { } stop && stop.Press == MaterialPresses.Count)
                    {
                        return T.Result(p.Id, stop.Status, inputs: 1);
                    }

                    var at = Header.Offset(click.Offset.Value.X, click.Offset.Value.Y);
                    var k = Array.FindIndex(TileLefts, left => new RectI(left, 674, left + 87, 763).Center == at);
                    Assert.IsGreaterThanOrEqualTo(0, k, "pressed at a tile centre");
                    var panel = _panels[Math.Min(_slot, _panels.Length) - 1];
                    if (MaterialTips?.Invoke(panel.Materials[k].Icon!) is { } item)
                    {
                        _offset += 10;
                        Ctx.Ui = Ctx.Ui with { AsOf = T.Pos(_offset), LastItemTip = new ItemTipInfo(item.Name, item.Id, new DateTime(2026, 10, 15, 1, 45, 0), T.Pos(_offset)) };
                    }

                    return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 2);
                }

                // the product icon pressed and the tip closed: the log (the UI snapshot) gets the scripted tip, if any
                if (EndTipWith is { } end && end.Slot == _slot)
                {
                    return T.Result(p.Id, end.Status, inputs: 1);
                }

                if (Tips?.Invoke(_slot) is { } tip)
                {
                    _offset += 10;
                    Ctx.Ui = Ctx.Ui with { AsOf = T.Pos(_offset), LastItemTip = new ItemTipInfo(tip.Name, tip.Id, new DateTime(2026, 10, 15, 1, 44, 58, 553), T.Pos(_offset)) };
                }

                return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 2);
            }

            Assert.AreEqual("crafting.scanFavorites", p.Id);
            // the slot the run clicks (a second look at the same slot reads the same panel, as in the game; R2.7)
            var slotStep = p.Steps.Single(s => s.Id.StartsWith("slot.", StringComparison.Ordinal)).Id;
            Assert.IsTrue(int.Parse(slotStep["slot.".Length..], System.Globalization.CultureInfo.InvariantCulture) is var n && (n == _slot || n == _slot + 1), "slots in order, or the same slot again");
            _slot = int.Parse(slotStep["slot.".Length..], System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                if (EndSlotWith is { } end && end.Slot == _slot)
                {
                    var halt = end.Status == ProcedureStatus.Halted
                        ? new HaltReport(HaltCode.ForbiddenUi, "UI_RECHARGE", "j", p.Id, "slot.02", DateTimeOffset.UnixEpoch, null)
                        : null;
                    return T.Result(p.Id, end.Status, halt: halt, inputs: 1);
                }

                _offset += 100;
                var panel = _panels[Math.Min(_slot, _panels.Length) - 1];
                if (BlankOnceAtSlot == _slot)
                {
                    BlankOnceAtSlot = null;
                    panel = new Panel(null, null);
                }

                var reads = new Dictionary<string, ScreenReadResult>
                {
                    ["product"] = ProductRead(panel),
                    ["pairs"] = PairsRead(panel),
                };
                return T.Result(p.Id, ProcedureStatus.Succeeded, reads, inputs: _slot == 1 ? 4 : 1);
            }
            finally
            {
                AfterSlot?.Invoke(_slot);
            }
        }

        private ScreenReadResult ProductRead(Panel panel)
        {
            if (ProductNeedsSurvey)
            {
                return new ScreenReadResult("craft.product", ReadStatus.NeedsSurvey, [], T.Pos(_offset), DateTimeOffset.UnixEpoch);
            }

            if (MismatchFromSlot is { } m && _slot >= m)
            {
                return new ScreenReadResult("craft.product", ReadStatus.ScreenMismatch, [], T.Pos(_offset), DateTimeOffset.UnixEpoch);
            }

            return T.Read("craft.product", _offset,
                panel.Title is null ? T.Unknown("product.name") : T.Text("product.name", panel.Title),
                panel.Owned is { } o ? T.Int("product.owned", o) : T.Unknown("product.owned"));
        }

        private ScreenReadResult PairsRead(Panel panel)
        {
            if (MismatchFromSlot is { } m && _slot >= m)
            {
                return new ScreenReadResult("craft.pairs", ReadStatus.ScreenMismatch, [], T.Pos(_offset + 1), DateTimeOffset.UnixEpoch);
            }

            var values = new List<ReadValue>();
            for (var k = 1; k <= 3; k++)
            {
                if (k > panel.Materials.Length)
                {
                    values.Add(new ReadValue("icon." + k, ReadStatus.Ok, null, null, null, null, 0.9, "icon-empty"));
                    values.Add(T.Unknown("pair." + k));
                    continue;
                }

                var (icon, have, need) = panel.Materials[k - 1];
                RectI? tile = Tiles ? new RectI(TileLefts[k - 1], 674, TileLefts[k - 1] + 87, 763) : null;
                values.Add(icon is null ? T.Unknown("icon." + k) : new ReadValue("icon." + k, ReadStatus.Ok, null, null, icon, null, 0.9, "icon", Rect: tile));
                values.Add(have is { } h && need is { } n ? T.Pair("pair." + k, h, n) : T.Unknown("pair." + k));
            }

            var read = T.Read("craft.pairs", _offset + 1, [.. values]);
            return Tiles ? read with { Anchors = new Dictionary<string, PointI> { ["craft.materials.header"] = Header } } : read;
        }
    }
}
