using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.GameIo;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Perception;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.Features.Kit;

namespace Companion.Features.Tests;

/// <summary>
/// ScanRecipeList against a simulated recipe list: the real crafting.scanList data procedure, a fake engine that scrolls
/// a list of sections and card rows when the drag runs arrive and "selects" the card under a click's point (anchor +
/// offset), and frames drawn from that list (tiles with outline and white border, as on the real Make tab) for the
/// job's own captures. No window, no game, no log.
/// </summary>
[TestClass]
public sealed class ScanRecipeListTests
{
    /// <summary>A distinct 256-bit icon key per material (far more than 24 bits apart, so never merged).</summary>
    private static string Icon(int id)
    {
        var bytes = new byte[32];
        new Random(id).NextBytes(bytes);
        return "icon:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ------------------------------------------------------------------ definition and data

    [TestMethod]
    public void TheJobIsAReadOnlyNavigationScan_WithNoParamsAndNoSwitch()
    {
        var (host, env, _) = T.LoadReal();
        var def = T.Definition(host, "ScanRecipeList");
        // one optional parameter: "find" (2026-10-15, the planner's recipe selection)
        Assert.AreEqual(("crafting", JobKind.Navigation, AutomationSwitch.None, 1, 20), (def.FeatureId, def.Kind, def.Switch, def.Params.Count, def.MaxPerHour));
        var p = env.Procedures.Get("crafting.scanList")!;
        Assert.AreEqual((ProcedureKind.Navigation, AutomationSwitch.None), (p.Kind, p.Gate));
        Assert.IsFalse(p.Steps.Any(s => s.Flags.HasFlag(StepFlags.Commit) || s.Action is AwaitCraftAction), "never crafts");
        Assert.IsTrue(p.Steps.Where(s => s.Action is ClickAction or DragAction && s.Id != "tab").All(s => s.Action is ClickAction { TemplateId: RecipeList.AnchorTemplate } or DragAction { TemplateId: RecipeList.AnchorTemplate }),
            "every list input is placed from the list anchor");
        CollectionAssert.AreEqual(new[] { "open", "announce.close", "top", "card", "settle", "read.product", "read.pairs", "scroll", "scroll.settle", "tab", "tab.settle" }, p.Steps.Select(s => s.Id).ToArray());
        var tab = p.Steps.Single(s => s.Id == "tab");
        Assert.AreEqual((new ClickAction("craft.make.tab"), "UI_LIFESKILL_MAKE_VIEW", StepFlags.None), (tab.Action, tab.Pre.TopUi, tab.Flags), "back to the Make tab: its own template, only on the Make view");
    }

    [TestMethod]
    public void TheWaitsAreHumanBrowsingSpeed()
    {
        // user 2026-10-15: "a human doesn't click a recipe once every 10 seconds"
        var (_, env, _) = T.LoadReal();
        var p = env.Procedures.Get("crafting.scanList")!;
        Assert.AreEqual(new WaitAction(400), p.Steps.Single(s => s.Id == "settle").Action, "the panel's settle after a card click (was 1000 ms)");
        var w = new ListWorld(ListWorld.Rows(2));
        var r = w.Run();
        Assert.AreEqual(JobState.Succeeded, r.State, Data(r));
        var cardRuns = w.Ctx.Runner.Runs.Where(x => x.Steps.Any(s => s.Id == "card")).ToList();
        Assert.IsTrue(cardRuns.All(x => Equals(x.Steps.Single(s => s.Id == "settle").Action, new WaitAction(400))));

        // the job's own pause: 300 ms before every card and every drag after the first input, nothing else
        var pauses = (10 + w.Drags) * 0.3;
        Assert.AreEqual(pauses, (w.Ctx.ClockFake.Monotonic - TimeSpan.FromHours(1)).TotalSeconds, 0.001, "300 ms per card and per drag (was 2 s)");
        var cards = CardRecords(w);
        Assert.IsTrue(cards.All(j => j.Fields["ms"] is long), "every card journals its duration");
        Assert.AreEqual(300L, cards[1].Fields["ms"], "the pause; the fake engine itself takes no time");
        Assert.AreEqual(300L, r.Data!["msPerSelected"]);
    }

    [TestMethod]
    public void ABrokenDataProcedure_FailsBeforeAnyInput()
    {
        Func<ProcedureDefinition, ProcedureDefinition>[] broken =
        [
            d => d with { Kind = ProcedureKind.Commit },
            d => d with { Steps = d.Steps.Select(s => s.Id == "scroll" ? s with { Action = new DragAction(RecipeList.AnchorTemplate, new PointI(-65, 737), new PointI(0, -400)) } : s).ToArray() }, // would skip a row
            d => d with { Steps = d.Steps.Select(s => s.Id == "top" ? s with { Action = new DragAction(RecipeList.AnchorTemplate, new PointI(-65, 297), new PointI(0, -280)) } : s).ToArray() },
            d => d with { Steps = d.Steps.Select(s => s.Id == "card" ? s with { Action = new ClickAction(RecipeList.AnchorTemplate) } : s).ToArray() },
            d => d with { Steps = d.Steps.Select(s => s.Id == "scroll" ? s with { Action = new DragAction("craft.fav.anchor", new PointI(-65, 737), new PointI(0, -280)) } : s).ToArray() },
            d => d with { Steps = d.Steps.Select(s => s.Id == "top" ? s with { Action = new DragAction(RecipeList.AnchorTemplate, new PointI(-65, 840), new PointI(0, 280)) } : s).ToArray() }, // release below the list
            d => d with { Steps = d.Steps.Where(s => s.Id != "read.pairs").ToArray() },
        ];
        foreach (var breakIt in broken)
        {
            var w = new ListWorld(ListWorld.Rows(2));
            w.Catalog.Put(breakIt(w.Catalog.Get("crafting.scanList")!));
            Assert.AreEqual((JobState.Failed, "procedure.invalid:crafting.scanList"), Code(w.Run()));
            Assert.HasCount(0, w.Ctx.Runner.Runs);
        }
    }

    [TestMethod]
    public void TheProductTipProcedure_ClicksTheIconFromTheMaterialsHeader_ThenEscClosesTheTip()
    {
        var (_, env, _) = T.LoadReal();
        var p = env.Procedures.Get("crafting.productTip")!;
        Assert.AreEqual((ProcedureKind.Navigation, AutomationSwitch.None), (p.Kind, p.Gate));
        CollectionAssert.AreEqual(new[] { "tip.open", "tip.close" }, p.Steps.Select(s => s.Id).ToArray());
        var open = p.Steps[0];
        Assert.AreEqual(new ClickAction("craft.materials.header", new PointI(-140, -266)), open.Action, "live (1130,375) = the header's centre (1270,641) + offset, for 3 and 4 materials");
        Assert.AreEqual((StepFlags.None, "UI_LIFESKILL_MAKE_VIEW"), (open.Flags, open.Pre.TopUi), "never a commit, only on the Make tab");
        CollectionAssert.AreEqual(new[] { new UiExpectation(UiVerb.Show, "UI_ITEM_TIPS_PASS_TOUCH") }, open.Expect.UiEvents!.ToArray());
        var close = p.Steps[1];
        Assert.AreEqual((new KeyAction(GameKey.Escape), "UI_ITEM_TIPS_PASS_TOUCH"), (close.Action, close.Pre.TopUi));
        CollectionAssert.AreEqual(new[] { UiExpectation.HiddenTop("UI_ITEM_TIPS_PASS_TOUCH") }, close.Expect.UiEvents!.ToArray());
    }

    [TestMethod]
    public void ABrokenProductTipProcedure_FailsBeforeAnyInput()
    {
        Func<ProcedureDefinition, ProcedureDefinition>[] broken =
        [
            d => d with { Steps = d.Steps.Select(s => s.Id == "tip.open" ? s with { Flags = StepFlags.Commit } : s).ToArray() },
            d => d with { Steps = d.Steps.Select(s => s.Id == "tip.open" ? s with { Action = new ClickAction("craft.materials.header") } : s).ToArray() },
            d => d with { Steps = d.Steps.Where(s => s.Id != "tip.close").ToArray() },
            d => d with { Gate = AutomationSwitch.Crafting },
        ];
        foreach (var breakIt in broken)
        {
            var w = new ListWorld(ListWorld.Rows(1));
            w.Catalog.Put(breakIt(w.Catalog.Get("crafting.productTip")!));
            Assert.AreEqual((JobState.Failed, "procedure.invalid:crafting.productTip"), Code(w.Run()));
            Assert.HasCount(0, w.Ctx.Runner.Runs);
        }
    }

    // ------------------------------------------------------------------ scans

    [TestMethod]
    public void FromTheBottom_ItScrollsToTheTop_ReadsEveryCardOnce_AndStopsWhenTheListNoLongerMoves()
    {
        // My Favorites (9), Recent (5, all also further down), then three dish sections; product 1 shows twice.
        var w = new ListWorld(
            ListWorld.Header(1), ListWorld.Cards(1, 2, 3, 4, 5), ListWorld.Cards(6, 7, 8, 9),
            ListWorld.Header(2), ListWorld.Cards(3, 1, 8, 12, 7),
            ListWorld.Header(3), ListWorld.Cards(10, 11, 12, 13, 14), ListWorld.Cards(15, 16, 17, 18, 19),
            ListWorld.Header(4), ListWorld.Cards(20, 21, 22, 23, 24), ListWorld.Cards(1, 25, 26),
            ListWorld.Header(5), ListWorld.Cards(27, 28, 29, 30))
        {
            StartAtBottom = true,
        };

        var r = w.Run();

        Assert.AreEqual((JobState.Succeeded, "list.scanned"), Code(r), Data(r));
        Assert.AreEqual("listEnd", r.Data!["end"]);
        var k = w.Ctx.KnowledgeFake!;
        CollectionAssert.AreEquivalent(Enumerable.Range(1, 30).Select(i => new ItemKey("dish " + i)).ToArray(), k.Recipes.Select(x => x.Product).ToArray(), "every product once");
        // The Recent row is cut by the list's bottom when it is first seen: its cards are selected as before (a part of a
        // picture is never matched), the repeats by name; product 1 further down is known from its picture: no click.
        Assert.AreEqual((36L, 30L, 1L, 5L, 35L), ((long)r.Data["cards"]!, (long)r.Data["recipes"]!, (long)r.Data["knownSkips"]!, (long)r.Data["duplicates"]!, (long)r.Data["selected"]!), Data(r));
        Assert.AreEqual(35, w.Selections.Count, "no card is selected twice (positions survive the drags)");
        Assert.AreEqual(30L, r.Data["cardKeys"], "every selected card whose whole picture was shown was learned as its product");
        Assert.IsTrue(w.ClickPoints.All(RecipeList.Clickable), "never a click outside the list");
        Assert.IsTrue(w.Selections.All(s => s > 0), "every click landed on a card");
        Assert.AreEqual("screen:craft.list/1", k.Recipes.Single(x => x.Product == new ItemKey("dish 1")).SourceCapture);
        Assert.AreEqual(w.MaxScroll, w.ScrollY, "the scan ends at the bottom of the list");
        Assert.IsTrue(k.Recipes.All(x => x.Confidence == RecipeConfidence.Complete && !x.UserVerified && x.Requirements.Count == 0 && x.Counter is null));
        Assert.HasCount(61, k.Items, "30 products, their 30 first materials and the shared second material: every owned count observed");

        // Journal: one record per card and per drag, codes only.
        var cards = w.Ctx.JournalFake.Records.Where(j => Equals(j.Fields.GetValueOrDefault("event"), "scanList.card")).ToList();
        Assert.HasCount(36, cards);
        Assert.AreEqual(5, cards.Count(j => Equals(j.Fields["code"], "duplicate")));
        Assert.AreEqual(1, cards.Count(j => Equals(j.Fields["code"], "card.knownSkip")));
        Assert.AreEqual(5, cards.Count(j => Equals(j.Fields["cardKey"], "noKey")), "the cut Recent row");
        Assert.IsTrue(cards.Where(j => Equals(j.Fields["code"], "card.knownSkip")).All(j => j.Fields["itemId"] is long && Equals(j.Fields["ms"], 0L)));
        var drags = w.Ctx.JournalFake.Records.Where(j => Equals(j.Fields.GetValueOrDefault("event"), "scanList.drag")).ToList();
        Assert.AreEqual(w.Drags, drags.Count);
        Assert.IsTrue(drags.Any(j => Equals(j.Fields["shiftPx"], -266L)), "the job measures the real movement (266 px for a 280 px drag)");
        Assert.IsTrue(w.Ctx.JournalFake.Records.All(j => j.Fields.Values.All(v => v is not string str || Codes.IsCode(str))), "codes only");
        Assert.IsTrue(w.Ctx.JournalFake.Records.Any(j => Equals(j.Fields.GetValueOrDefault("event"), "scanList.end") && Equals(j.Fields["code"], "list.scanned")));

        // Human pace: a fixed pause before every selected card and every drag after the first input.
        Assert.IsTrue(w.Ctx.ClockFake.Monotonic - TimeSpan.FromHours(1) >= TimeSpan.FromMilliseconds(300) * (35 + w.Drags));
    }

    /// <summary>
    /// Favourites runs on the simulated list: a slot run is done when the list is at its top, else it ends where the "Recent"
    /// header was not found (as the engine reports it). Every other run is the list's own.
    /// </summary>
    private static Func<ProcedureDefinition, ProcedureResult> FavoritesAtTop(ListWorld w, Func<ProcedureDefinition, ProcedureResult> world, List<string> favRuns) => p =>
    {
        if (p.Id != "crafting.scanFavorites")
        {
            return world(p);
        }

        var slot = p.Steps.First(s => s.Id.StartsWith("slot.", StringComparison.Ordinal)).Id;
        favRuns.Add(slot);
        return w.ScrollY == 0
            ? T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 1)
            : T.Result(p.Id, ProcedureStatus.Deferred, attempts: [new StepAttemptRecord(slot, 3, StepOutcome.PreconditionLost, OutcomeClass.Counted,
                RecoveryAction.End, null, false, null, "exhausted.PreconditionLostExhausted", DateTimeOffset.UnixEpoch, "template.notFound:craft.fav.anchor")]);
    };

    [TestMethod]
    public void FavoritesScrolledAway_TheListIsDraggedToItsTop_AndTheSlotRunsAgain()
    {
        // 2026-10-31 queue failure: after cooking, the game reopened the Make view scrolled down to the dish's own section;
        // the next goal's favourites read did not find the "Recent" header and the queue failed
        var w = new ListWorld(ListWorld.Rows(12));
        w.ScrollTo(w.MaxScroll);
        var favRuns = new List<string>();
        w.Ctx.Runner.Script = FavoritesAtTop(w, w.Ctx.Runner.Script!, favRuns);

        T.Handler(w.Host, "ScanFavorites").Execute(w.Ctx, JobParameters.Empty, CancellationToken.None);

        Assert.AreEqual(0, w.ScrollY, "the list is back at its top");
        CollectionAssert.AreEqual(new[] { "slot.01", "slot.01" }, favRuns.Take(2).ToArray(), "the first slot ran once more after the drags");
        Assert.IsTrue(w.Drags > 0 && w.DragDeltas.All(d => d > 0), "top drags only");
        Assert.IsTrue(w.Ctx.JournalFake.Records.Any(j => Equals(j.Fields.GetValueOrDefault("event"), "favorites.toTop") && Equals(j.Fields["code"], "top")));
    }

    [TestMethod]
    public void FavoritesNotFound_WhileAnotherWindowIsOnTop_NothingIsDragged()
    {
        // the user opened something else over the Make view (live 2026-10-31: the Tea House): the run keeps its deferral
        var w = new ListWorld(ListWorld.Rows(12));
        w.ScrollTo(w.MaxScroll);
        w.Ctx.Ui = w.Ctx.Ui with { AdvisoryStack = ["UI_ACTIVITY_CENTER"] };
        var favRuns = new List<string>();
        w.Ctx.Runner.Script = FavoritesAtTop(w, w.Ctx.Runner.Script!, favRuns);

        var r = T.Handler(w.Host, "ScanFavorites").Execute(w.Ctx, JobParameters.Empty, CancellationToken.None);

        Assert.AreEqual(0, w.Drags);
        CollectionAssert.AreEqual(new[] { "slot.01" }, favRuns.ToArray());
        Assert.AreNotEqual(JobState.Succeeded, r.State);
    }

    [TestMethod]
    public void AListThatFitsOnOneScreen_IsReadWithOneDragEachWay()
    {
        var w = new ListWorld(ListWorld.Header(1), ListWorld.Cards(1, 2, 3), ListWorld.Header(2), ListWorld.Cards(4, 5));
        var r = w.Run();
        Assert.AreEqual((JobState.Succeeded, "list.scanned"), Code(r), Data(r));
        Assert.AreEqual((5L, 5L, 2L), ((long)r.Data!["cards"]!, (long)r.Data["recipes"]!, (long)r.Data["drags"]!), "one drag to prove the top, one to prove the end");
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, w.Selections.ToArray(), "row by row, left to right");
    }

    [TestMethod]
    public void TheRequiresLineAndTheCounter_AreStoredWithTheRecipe()
    {
        var w = new ListWorld(ListWorld.Rows(1));
        w.WithRequires.Add(3);
        w.Run();
        var k = w.Ctx.KnowledgeFake!;
        var three = k.Recipes.Single(x => x.Product == new ItemKey("dish 3"));
        CollectionAssert.AreEqual(new[] { "requires:5" }, three.Requirements.ToArray());
        Assert.AreEqual(("5/300", RecipeConfidence.Complete), (three.Counter, three.Confidence));
        Assert.IsTrue(k.Recipes.Where(x => x.Product != new ItemKey("dish 3")).All(x => x.Requirements.Count == 0 && x.Counter is null));
    }

    [TestMethod]
    public void TheLoggedItemName_ReplacesAGarbageOcrTitle_AndTheItemIdIsJournaled()
    {
        // live 2026-10-15: OCR read titles like "ams" and "i pizza"; the item tip logs the exact name and id
        var w = new ListWorld(ListWorld.Rows(1)) { OcrTitle = id => id % 2 == 0 ? "i pizza" : "ams" };
        var r = w.Run();

        Assert.AreEqual((JobState.Succeeded, "list.scanned"), Code(r), Data(r));
        var k = w.Ctx.KnowledgeFake!;
        CollectionAssert.AreEquivalent(Enumerable.Range(1, 5).Select(i => new ItemKey("dish " + i)).ToArray(), k.Recipes.Select(x => x.Product).ToArray(), "no garbage title became a product");
        Assert.AreEqual((5L, 0L, 5L, 0L), ((long)r.Data!["recipes"]!, (long)r.Data["duplicates"]!, (long)r.Data["tips"]!, (long)r.Data["tipsMissing"]!),
            "cards with the same garbage title are still different products");
        Assert.AreEqual(4L, k.Items[new ItemKey("dish 4")].Count, "the owned badge is stored under the logged name");
        Assert.IsFalse(k.Items.ContainsKey(new ItemKey("ams")));
        Assert.AreEqual(5, w.TipRuns, "one tip per card");

        var cards = CardRecords(w);
        CollectionAssert.AreEqual(Enumerable.Repeat<object>("tip.logged", 5).ToArray(), cards.Select(j => j.Fields["tip"]!).ToArray());
        CollectionAssert.AreEqual(Enumerable.Range(1, 5).Select(i => (object)ListWorld.ItemId(i)).ToArray(), cards.Select(j => j.Fields["itemId"]!).ToArray());
        Assert.IsFalse(cards[2].Fields.ContainsKey("itemName"), "codes and ids only: the name goes to the store");
        Assert.IsTrue(w.Ctx.JournalFake.Records.All(j => j.Fields.Values.All(v => v is not string s || Codes.IsCode(s))), "codes only");
    }

    [TestMethod]
    public void WithoutALoggedTip_TheOcrTitleIsKept_AndTheCodeIsJournaled()
    {
        var w = new ListWorld(ListWorld.Rows(1)) { Tips = _ => TipLog.Nothing, OcrTitle = id => id == 2 ? "om pizza" : "Dish " + id };
        var r = w.Run();

        Assert.AreEqual((JobState.Succeeded, "list.scanned"), Code(r), Data(r));
        CollectionAssert.AreEquivalent(new[] { "dish 1", "om pizza", "dish 3", "dish 4", "dish 5" }, w.Ctx.KnowledgeFake!.Recipes.Select(x => x.Product.Value).ToArray(), "today's behaviour: the OCR title");
        Assert.AreEqual((0L, 5L), ((long)r.Data!["tips"]!, (long)r.Data["tipsMissing"]!));
        var cards = CardRecords(w);
        Assert.IsTrue(cards.All(j => Equals(j.Fields["tip"], "tip.notLogged") && j.Fields["itemId"] is null && !j.Fields.ContainsKey("itemName")));
        Assert.AreEqual(5, w.TipRuns, "the tip was asked for every card");
    }

    [TestMethod]
    public void ATipLoggedBeforeTheClick_IsIgnored()
    {
        // a tip from before the job, the previous card's tip, and a tip line older than the click never rename a card
        var w = new ListWorld(ListWorld.Rows(1))
        {
            OcrTitle = id => "ams " + id,
            Tips = id => id switch { 2 => TipLog.Logged, 4 => TipLog.BeforeTheClick, _ => TipLog.Nothing },
        };
        w.Ctx.Ui = T.Snapshot(1000) with { LastItemTip = new ItemTipInfo("Lime Water", 20_330_001, new DateTime(2026, 10, 15, 1, 40, 0), T.Pos(950)) };
        var r = w.Run();

        Assert.AreEqual((JobState.Succeeded, "list.scanned"), Code(r), Data(r));
        CollectionAssert.AreEquivalent(new[] { "ams 1", "dish 2", "ams 3", "ams 4", "ams 5" }, w.Ctx.KnowledgeFake!.Recipes.Select(x => x.Product.Value).ToArray());
        Assert.AreEqual((0L, 1L, 4L), ((long)r.Data!["duplicates"]!, (long)r.Data["tips"]!, (long)r.Data["tipsMissing"]!));
        CollectionAssert.AreEqual(
            new object[] { "tip.beforeClick", "tip.logged", "tip.beforeClick", "tip.beforeClick", "tip.beforeClick" },
            CardRecords(w).Select(j => j.Fields["tip"]!).ToArray());
        CollectionAssert.AreEqual(new object?[] { null, ListWorld.ItemId(2), null, null, null }, CardRecords(w).Select(j => j.Fields["itemId"]).ToArray());
    }

    [TestMethod]
    public void ATipRunThatDoesNotSucceed_EndsTheScan_KeepingTheCardsAlreadyStored()
    {
        var w = new ListWorld(ListWorld.Rows(1)) { EndTipWith = (3, ProcedureStatus.Halted) };
        var r = w.Run();
        Assert.AreEqual((JobState.Halted, "ForbiddenUi"), Code(r), Data(r));
        Assert.HasCount(2, w.Ctx.KnowledgeFake!.Recipes, "cards 1 and 2 were stored before");
        Assert.AreEqual("tip.procedure", CardRecords(w)[^1].Fields["code"]);
        Assert.AreEqual("stopped", r.Data!["end"]);
    }

    [TestMethod]
    public void ARecipeFromAFavouritesSlot_KeepsItsSlot_ButItsStockIsStillObserved()
    {
        var w = new ListWorld(ListWorld.Rows(1));
        var favourite = new Recipe("dish 2|lifeskill|0", new ItemKey("dish 2"), 1, "lifeskill", [new MaterialRequirement(new ItemKey("x"), 9)], [], null,
            DateTimeOffset.UnixEpoch, "screen:craft.fav/4", false, RecipeConfidence.Complete, null, null);
        w.Ctx.KnowledgeFake!.Recipes.Add(favourite);
        w.Run();
        Assert.AreSame(favourite, w.Ctx.KnowledgeFake.Recipes.Single(x => x.Id == favourite.Id), "the tray maps the CraftLoop slots from this source");
        Assert.AreEqual(2L, w.Ctx.KnowledgeFake.Items[new ItemKey("dish 2")].Count);
        Assert.IsTrue(w.Ctx.JournalFake.Records.Any(j => Equals(j.Fields.GetValueOrDefault("code"), "recipe.keptFavorite")));
    }

    [TestMethod]
    public void ALoggedName_ReplacesTheOcrOnlyRecipeOfTheSameCard_ButNeverAFavouritesRecipe()
    {
        // 2026-10-15: the store held OCR garbage ("ams") for list card 2; a favourites recipe with card 3's materials stays
        var w = new ListWorld(ListWorld.Rows(1));
        var k = w.Ctx.KnowledgeFake!;
        static Recipe Ocr(string name, string source, params (string Icon, int Qty)[] materials) => new(
            name + "|lifeskill|0", new ItemKey(name), 1, "lifeskill", [.. materials.Select(m => new MaterialRequirement(new ItemKey(m.Icon), m.Qty))], [], null,
            DateTimeOffset.UnixEpoch, source, false, RecipeConfidence.Partial, null, null);
        k.Recipes.Add(Ocr("ams", "screen:craft.list/2", (Icon(9), 1)));
        k.Recipes.Add(Ocr("pizza i", "screen:craft.fav/7", (Icon(3), 1), (Icon(200), 2)));
        var r = w.Run();

        Assert.AreEqual((JobState.Succeeded, "list.scanned"), Code(r), Data(r));
        CollectionAssert.AreEquivalent(new[] { "dish 1", "dish 2", "dish 3", "dish 4", "dish 5", "pizza i" }, k.Recipes.Select(x => x.Product.Value).ToArray());
        Assert.AreEqual(1L, (long)r.Data!["recipesReplaced"]!);
        Assert.AreEqual(ListWorld.ItemId(2), k.Recipes.Single(x => x.Product == new ItemKey("dish 2")).ProductItemId);
        Assert.AreEqual(1L, CardRecords(w).Single(j => Equals(j.Fields["card"], 2L)).Fields["replaced"]);
    }

    [TestMethod]
    public void DragsCutShortWithoutMovement_AreTriedAgain_ThenTheScanEndsIncomplete()
    {
        var w = new ListWorld(ListWorld.Rows(5)) { PartialDrag = n => n >= 1 }; // the first drag (towards the top) is fine, every later one is cut short
        var r = w.Run();
        Assert.AreEqual((JobState.Failed, "list.dragIncomplete"), Code(r), Data(r));
        Assert.AreEqual(1 + 1 + 2, w.Drags, "the top drag, one scroll drag and two retries");
        Assert.AreEqual((4L, 1L, 3L, "stopped"), ((long)r.Data!["drags"]!, (long)r.Data["topDrags"]!, (long)r.Data["scrollDrags"]!, (string)r.Data["end"]!));
        Assert.IsTrue((long)r.Data!["recipes"]! > 0, "what was read is kept");
    }

    [TestMethod]
    public void AnyOtherProcedureEnd_StopsTheScan_KeepingTheCardsAlreadyStored()
    {
        foreach (var (status, state, code) in new[]
        {
            (ProcedureStatus.Halted, JobState.Halted, "ForbiddenUi"),
            (ProcedureStatus.Stopped, JobState.Stopped, "card.stopped"),
            (ProcedureStatus.Parked, JobState.Parked, "card.parked"),
        })
        {
            var w = new ListWorld(ListWorld.Rows(2)) { EndCardWith = (3, status) };
            var r = w.Run();
            Assert.AreEqual((state, code), Code(r), status.ToString());
            Assert.HasCount(2, w.Ctx.KnowledgeFake!.Recipes, "cards 1 and 2 were stored before");
            // live 2026-10-14: a halt on card 6 was reported as end "listEnd", which read like a finished scan
            Assert.AreEqual("stopped", r.Data!["end"], status.ToString());
        }
    }

    [TestMethod]
    public void ACardWhosePanelIsNotARecipe_IsSkipped_ButThreeInARowEndTheScan()
    {
        // live 2026-10-15: one card with a non-recipe panel (card 53) ended a scan of 52 good cards
        var w = new ListWorld(ListWorld.Rows(2)) { MismatchProducts = [3] };
        var r = w.Run();
        Assert.AreEqual(JobState.Succeeded, r.State, Data(r));
        Assert.AreEqual(1L, (long)r.Data!["panelMismatches"]!);
        Assert.HasCount(9, w.Ctx.KnowledgeFake!.Recipes, "every other card is still stored");

        var lost = new ListWorld(ListWorld.Rows(2)) { MismatchProducts = [2, 3, 4] }.Run();
        Assert.AreEqual(JobState.Failed, lost.State, Data(lost));
        Assert.AreEqual(2L, (long)lost.Data!["panelMismatches"]!, "the third in a row ends the scan");
    }

    [TestMethod]
    public void AnUnlearnedCard_SwitchingToTheLearnTab_IsClickedOnce_AndNeverEndsTheScan()
    {
        // live 2026-10-15: each restart after the Learn-tab switch clicked the same unlearned card again until "tabLost"
        var w = new ListWorld(ListWorld.Rows(3)) { MismatchProducts = [4], TabAwayOnMismatch = true };
        var r = w.Run();
        Assert.AreNotEqual("list.tabLost", Code(r).Item2, Data(r));
        Assert.AreEqual(1, w.Selections.Count(p => p == 4), "the unlearned card is clicked once, then skipped by its picture");
        Assert.AreEqual((0L, 1L), ((long)r.Data!["tabRecoveries"]!, (long)r.Data["expectedTabRecoveries"]!), Data(r));
    }

    [TestMethod]
    public void AViewThatHadToBeOpenedAgain_EndsTheScan_PositionsAreNoLongerKnown()
    {
        var w = new ListWorld(ListWorld.Rows(2)) { ReopenOnCard = 2 };
        var r = w.Run();
        Assert.AreEqual((JobState.Failed, "list.viewReopened"), Code(r), Data(r));
        Assert.HasCount(1, w.Ctx.KnowledgeFake!.Recipes);
    }

    [TestMethod]
    public void AVeryLongList_StopsAtTheSafetyCap()
    {
        var w = new ListWorld(ListWorld.Rows(70)); // 350 cards
        var r = w.Run();
        Assert.AreEqual((JobState.Succeeded, "cap"), (r.State, (string)r.Data!["end"]!), Data(r));
        Assert.AreEqual(300L, (long)r.Data["cards"]!);
    }

    [TestMethod]
    public void RefusesWithZeroRuns_WithoutKnowledgeOrSlideClicks_OrDuringACraft_ButRunsOtherwise()
    {
        var unknown = new ListWorld(ListWorld.Rows(1));
        unknown.Ctx.KnowledgeFake = null;
        Assert.AreEqual((JobState.Refused, "knowledge.unavailable"), Code(unknown.Run()));
        Assert.HasCount(0, unknown.Ctx.Runner.Runs);

        var noSlides = new ListWorld(ListWorld.Rows(1));
        noSlides.Ctx.SwitchesFake.SlideClickEnabled = false;
        Assert.AreEqual((JobState.Refused, "slideClick.off"), Code(noSlides.Run()));
        Assert.HasCount(0, noSlides.Ctx.Runner.Runs);

        var crafting = new ListWorld(ListWorld.Rows(1));
        crafting.Ctx.Ui = T.Snapshot(100, new CraftSessionInfo(true, "UI_LIFE_ALCHEMIST_MAKE_POT", DateTime.Now, T.Pos(50), 3, null, null, false));
        Assert.AreEqual((JobState.Refused, "craft.inProgress"), Code(crafting.Run()));
        Assert.HasCount(0, crafting.Ctx.Runner.Runs);

        // a read-only scan: no switch (and since 4.33 no per-character permission) is asked
        var w = new ListWorld(ListWorld.Rows(1));
        w.Ctx.SwitchesFake.Crafting = false;
        Assert.AreEqual(JobState.Succeeded, w.Run().State);
    }

    [TestMethod]
    public void TheFavoritesAndRecentRepeats_AreFreeAfterTheirFirstIdentification()
    {
        // My Favorites (1..5), then Recent (3, 1, 5) wholly on screen: the repeats are known from their picture
        var w = new ListWorld(ListWorld.Header(1), ListWorld.Cards(1, 2, 3, 4, 5), ListWorld.Header(2), ListWorld.Cards(3, 1, 5));
        var r = w.Run();
        Assert.AreEqual((JobState.Succeeded, "list.scanned"), Code(r), Data(r));
        Assert.AreEqual((8L, 3L, 5L, 0L, 5L), ((long)r.Data!["cards"]!, (long)r.Data["knownSkips"]!, (long)r.Data["selected"]!, (long)r.Data["duplicates"]!, (long)r.Data["recipes"]!));
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, w.Selections.ToArray(), "no click on a repeat");
        Assert.AreEqual(5, w.TipRuns, "no tip for a repeat");
        var skips = CardRecords(w).Where(j => Equals(j.Fields["code"], "card.knownSkip")).ToList();
        CollectionAssert.AreEqual(new object[] { ListWorld.ItemId(3), ListWorld.ItemId(1), ListWorld.ItemId(5) }, skips.Select(j => j.Fields["itemId"]!).ToArray());
        Assert.IsTrue(skips.All(j => Equals(j.Fields["bits"], 0L)));
    }

    [TestMethod]
    public void ASecondScan_SkipsEveryKnownCard_WithoutAnyClick()
    {
        var w = new ListWorld(ListWorld.Rows(3));
        Assert.AreEqual(JobState.Succeeded, w.Run().State);
        var k = w.Ctx.KnowledgeFake!;
        Assert.HasCount(15, k.IconItems, "one learned picture per product (its own key space)");
        Assert.IsTrue(k.IconItems.All(i => i.ItemId == ListWorld.ItemId(int.Parse(i.Name[5..], System.Globalization.CultureInfo.InvariantCulture))));
        Assert.IsTrue(k.IconItems.All(i => !k.Recipes.SelectMany(x => x.Materials).Any(m => m.Item.Value == i.IconKey)), "never a material key");
        var recipes = k.Recipes.ToList();
        var firstClicks = w.Selections.Count;
        var firstTips = w.TipRuns;

        var again = w.Run();

        Assert.AreEqual((JobState.Succeeded, "list.scanned"), Code(again), Data(again));
        Assert.AreEqual((15L, 15L, 0L, 0L), ((long)again.Data!["cards"]!, (long)again.Data["knownSkips"]!, (long)again.Data["selected"]!, (long)again.Data["recipes"]!));
        Assert.AreEqual((firstClicks, firstTips), (w.Selections.Count, w.TipRuns), "no card click, no tip");
        Assert.IsFalse(w.Ctx.Runner.Runs.Skip(w.RunsBefore).Any(x => x.Steps.Any(s => s.Id == "card")), "no card run at all");
        CollectionAssert.AreEqual(recipes, k.Recipes.ToList(), "the stored recipes stay as they are");
        Assert.AreEqual(15, CardRecords(w).Count(j => Equals(j.Fields["code"], "card.knownSkip") && Equals(j.Fields["match"], "known")));
    }

    [TestMethod]
    public void AKnownProductWithoutACompleteRecipe_IsStillSelected()
    {
        // ForgetRecipes keeps the learned pictures: a rescan must still read every recipe
        var w = new ListWorld(ListWorld.Rows(1));
        w.Run();
        w.Ctx.KnowledgeFake!.Recipes.Clear();
        var again = w.Run();
        Assert.AreEqual((5L, 0L, 5L), ((long)again.Data!["cards"]!, (long)again.Data["knownSkips"]!, (long)again.Data["selected"]!));
        Assert.HasCount(5, w.Ctx.KnowledgeFake.Recipes);
        Assert.IsTrue(CardRecords(w).TakeLast(5).All(j => Equals(j.Fields["cardKey"], "known") && Equals(j.Fields["match"], "known")), "already learned: not learned again");
    }

    [TestMethod]
    public void AnAmbiguousCard_IsSelectedAndRead()
    {
        // two learned products whose pictures lie within the margin of each other: no guess, a click
        var w = new ListWorld(ListWorld.Rows(1));
        w.Run();
        var k = w.Ctx.KnowledgeFake!;
        var three = k.IconItems.Single(i => i.ItemId == ListWorld.ItemId(3));
        var bytes = Convert.FromHexString(three.IconKey[5..]);
        bytes[0] ^= 0x07; // 3 bits away
        k.IconItems.Add(new IconItem("icon:" + Convert.ToHexString(bytes).ToLowerInvariant(), 9_999_999, "Look-alike", DateTimeOffset.UnixEpoch));
        var clicks = w.Selections.Count;

        var again = w.Run();

        Assert.AreEqual((4L, 1L), ((long)again.Data!["knownSkips"]!, (long)again.Data["selected"]!), Data(again));
        Assert.AreEqual(clicks + 1, w.Selections.Count);
        Assert.AreEqual(3, w.Selections[^1], "the ambiguous card was clicked");
        var card = CardRecords(w).Last(j => Equals(j.Fields["itemId"], ListWorld.ItemId(3)));
        Assert.AreEqual(("ambiguous", "learned"), (card.Fields["match"], card.Fields["cardKey"]));
    }

    [TestMethod]
    public void TheFirstIdentification_StoresTheCardPicture_OnlyWhenTheClickedCardIsTheSelectedOne()
    {
        // card 2's click did not move the selection frame (a press that missed): its picture is never learned
        var w = new ListWorld(ListWorld.Rows(1)) { NotSelected = [2] };
        var r = w.Run();
        Assert.AreEqual(JobState.Succeeded, r.State, Data(r));
        var k = w.Ctx.KnowledgeFake!;
        CollectionAssert.AreEquivalent(new[] { 1, 3, 4, 5 }.Select(ListWorld.ItemId).ToArray(), k.IconItems.Select(i => i.ItemId).ToArray());
        var cards = CardRecords(w);
        CollectionAssert.AreEqual(new object[] { "learned", "notSelected", "learned", "learned", "learned" }, cards.Select(j => j.Fields["cardKey"]!).ToArray());
        Assert.AreEqual(4L, r.Data!["cardKeys"]);

        // the learned key is the card's own picture: the same card in a fresh frame matches it strictly
        var frame = w.Capture(CancellationToken.None).Frame!;
        var first = RecipeList.FindCards(frame)[0];
        var match = RecipeList.MatchCard(RecipeList.CardKeys(frame, first), k.GetIconItems());
        Assert.AreEqual(("known", ListWorld.ItemId(1), 0), (match.Code, match.Item?.ItemId, match.Distance));

        // the next scan skips the four learned cards and selects card 2 again
        var again = w.Run();
        Assert.AreEqual((4L, 1L), ((long)again.Data!["knownSkips"]!, (long)again.Data["selected"]!));
        Assert.AreEqual(2, w.Selections[^1]);
    }

    [TestMethod]
    public void AStrayPressOnAnotherTab_IsRecovered_PressingTheMakeTab_AndTheScanRestartsFromTheTop()
    {
        // live 2026-10-15: a press landed on the Learn tab; the list anchor was gone and the scroll step halted
        var w = new ListWorld(ListWorld.Rows(4)) { TabAwayAfterCard = 7 };
        var r = w.Run();

        Assert.AreEqual((JobState.Succeeded, "list.scanned"), Code(r), Data(r));
        Assert.AreEqual((1L, 20L), ((long)r.Data!["tabRecoveries"]!, (long)r.Data["recipes"]!));
        Assert.AreEqual(1, w.TabRuns, "one press on the Make tab");
        Assert.AreEqual(0, w.InputsWhileAway, "no list input while another tab was shown");
        Assert.AreEqual(20, w.Selections.Count, "every card selected once: the cards read before are skipped by their picture");
        Assert.IsTrue((long)r.Data["knownSkips"]! >= 7, "the restart from the top skips the known cards");
        var tab = w.Ctx.JournalFake.Records.Single(j => Equals(j.Fields.GetValueOrDefault("event"), "list.tabRecovered"));
        Assert.AreEqual(("recovered", 1L), (tab.Fields["code"], tab.Fields["n"]));
        var tabRun = w.Ctx.Runner.Runs.Single(x => x.Steps.Any(s => s.Id == "tab"));
        CollectionAssert.AreEqual(new[] { "open", "announce.close", "tab", "tab.settle" }, tabRun.Steps.Select(s => s.Id).ToArray());
        Assert.IsTrue(tabRun.Steps[0].Flags.HasFlag(StepFlags.SkipIfSatisfied));
    }

    [TestMethod]
    public void AListThatDoesNotComeBack_EndsTheScanWithoutAHalt_AndAnUnknownViewIsNeverPressed()
    {
        var stuck = new ListWorld(ListWorld.Rows(2)) { TabAwayAfterCard = 3, TabStuck = true };
        var r = stuck.Run();
        Assert.AreEqual((JobState.Failed, "list.tabNotRecovered"), Code(r), Data(r));
        Assert.AreEqual((1, 0), (stuck.TabRuns, stuck.InputsWhileAway));
        Assert.HasCount(3, stuck.Ctx.KnowledgeFake!.Recipes, "what was read is kept");

        // the list gone and no other tab recognised (something covers the view): nothing is pressed
        var covered = new ListWorld(ListWorld.Rows(2)) { TabAwayAfterCard = 3, OtherTabShown = false };
        var c = covered.Run();
        Assert.AreEqual((JobState.Failed, "list.notShown"), Code(c), Data(c));
        Assert.AreEqual((0, 0), (covered.TabRuns, covered.InputsWhileAway));

        // another UI on top (not the Make view): the run's own steps handle it, no tab press
        var popup = new ListWorld(ListWorld.Rows(1)) { TabAwayAfterCard = 2, TopWhileAway = "UI_ITEM_TIPS_PASS_TOUCH" };
        popup.Run();
        Assert.AreEqual(0, popup.TabRuns);
    }

    [TestMethod]
    public void TheTabRecoveries_AreCapped()
    {
        var w = new ListWorld(ListWorld.Rows(3)) { TabAwayEvery = 2 };
        var r = w.Run();
        Assert.AreEqual((JobState.Failed, "list.tabLost"), Code(r), Data(r));
        Assert.AreEqual(ScanRecipeListCap, w.TabRuns);
        Assert.AreEqual(0, w.InputsWhileAway);
    }

    // ------------------------------------------------------------------ the faster walk (2026-10-16)

    [TestMethod]
    public void TheTop_IsReachedWithOneDrag_WhenTheListMovesLessThanADrag()
    {
        // live 2026-10-15: every walk's first top drag moved 31 px and a second drag (~7 s) only confirmed the top
        var w = new ListWorld(ListWorld.Rows(5)) { StartScrollY = 31 };
        var r = w.Run();
        Assert.AreEqual((JobState.Succeeded, "list.scanned"), Code(r), Data(r));
        Assert.AreEqual(1L, r.Data!["topDrags"]);

        var bottom = new ListWorld(ListWorld.Rows(12)) { StartAtBottom = true };
        var b = bottom.Run();
        Assert.AreEqual(JobState.Succeeded, b.State, Data(b));
        Assert.AreEqual(5L, b.Data!["topDrags"], "1182 px: four full moves of 266 px, the fifth moves 118 px and reaches the top");
        Assert.HasCount(60, bottom.Ctx.KnowledgeFake!.Recipes, "no row missed");
    }

    [TestMethod]
    public void AfterADrag_TheNextListInputNeedsNoProbe_AndTheFrameAfterIsReused()
    {
        // captures are rate-limited to 2/s: the probe and the second capture per drag were ~1 s of every 7.7 s drag
        var w = new ListWorld(ListWorld.Rows(12));
        Assert.AreEqual(JobState.Succeeded, w.Run().State);
        var (probes, captures, drags) = (w.Probes, w.Captures, w.Drags);

        var again = w.Run(); // every card known: no click, only drags

        Assert.AreEqual((JobState.Succeeded, "list.scanned"), Code(again), Data(again));
        Assert.AreEqual(1, w.Probes - probes, "one probe before the first drag; a drag proves the list for the next one");
        Assert.AreEqual(w.Drags - drags + 1, w.Captures - captures, "one capture before the first drag, then only the one after each drag");
    }

    [TestMethod]
    public void EveryLearnedCard_KeepsItsPlace_AndAFindJumpsThereWithLongDrags_ClickingOnlyTheWantedCard()
    {
        var w = new ListWorld(ListWorld.Rows(12));
        Assert.AreEqual(JobState.Succeeded, w.Run().State);
        var k = w.Ctx.KnowledgeFake!;
        Assert.IsTrue(k.IconItems.All(i => RecipeList.ParsePlace(i.Place) is not null), "every learned card picture keeps its place");
        var place = RecipeList.ParsePlace(k.IconItems.Single(i => i.ItemId == ListWorld.ItemId(50)).Place)!.Value;
        Assert.AreEqual(4, place.Column);
        Assert.AreEqual(1725, place.ListY, 3, "row 10 of the list, measured from its top");
        var (drags, clicks, tips) = (w.Drags, w.Selections.Count, w.TipRuns);

        var r = w.Run(ListWorld.ItemId(50));

        Assert.AreEqual((JobState.Succeeded, "list.found"), Code(r), Data(r));
        Assert.AreEqual(("jump", ListWorld.ItemId(50)), (r.Data!["via"], r.Data["itemId"]));
        CollectionAssert.AreEqual(new[] { 50 }, w.Selections.Skip(clicks).ToArray(), "only the wanted card is clicked");
        Assert.AreEqual(tips, w.TipRuns, "no tip: the picture, the selection frame and the panel agree (the craft loop reads the tip before Make)");
        Assert.AreEqual(1L, r.Data["tipsKnown"]);
        Assert.AreEqual((1L, 3L, 0L), ((long)r.Data["topDrags"]!, (long)r.Data["jumpDrags"]!, (long)r.Data["scrollDrags"]!), "the walk needed four scroll drags");
        CollectionAssert.AreEqual(new[] { 280, -360, -360, -360 }, w.DragDeltas.Skip(drags).ToArray(), "the longest jump drags");
        Assert.IsTrue(w.DragStarts.Skip(drags + 1).Zip(w.DragDeltas.Skip(drags + 1)).All(x => RecipeList.Clickable(x.First) && RecipeList.Clickable(x.First.Offset(0, x.Second))),
            "both ends of every jump inside the list");
        var find = w.Ctx.JournalFake.Records.Last(j => Equals(j.Fields.GetValueOrDefault("event"), "scanList.find"));
        Assert.AreEqual((1L, true), (find.Fields["hints"], find.Fields["named"]));
        Assert.AreEqual(3, w.Ctx.JournalFake.Records.Count(j => Equals(j.Fields.GetValueOrDefault("event"), "scanList.drag") && Equals(j.Fields["kind"], "jump")));

        // without any remembered place: the walk of today (every known card skipped by its picture)
        for (var i = 0; i < k.IconItems.Count; i++)
        {
            k.IconItems[i] = k.IconItems[i] with { Place = null };
        }

        w.Select(1); // the panel shows another recipe (else the selected card is found without a walk)
        clicks = w.Selections.Count;
        var walk = w.Run(ListWorld.ItemId(50));
        Assert.AreEqual((JobState.Succeeded, "list.found"), Code(walk), Data(walk));
        Assert.AreEqual(("walk", 4L, 0L), ((string)walk.Data!["via"]!, (long)walk.Data["scrollDrags"]!, (long)walk.Data["jumpDrags"]!));
        CollectionAssert.AreEqual(new[] { 50 }, w.Selections.Skip(clicks).ToArray());
        Assert.AreEqual((4, 1725), (RecipeList.ParsePlace(k.IconItems.Single(i => i.ItemId == ListWorld.ItemId(50)).Place)!.Value.Column, 1725), "the walk stored the place again");
        Assert.IsGreaterThan(0L, (long)walk.Data["placesStored"]!);
    }

    [TestMethod]
    public void ACardThatMovedDown_IsFoundByWalkingOnFromItsOldPlace_WithoutClickingTheNewCardsOnTheWay()
    {
        // the user learned ten recipes in the rows above it: the wanted card is two rows further down
        var first = new ListWorld(ListWorld.Rows(12));
        Assert.AreEqual(JobState.Succeeded, first.Run().State);
        ListWorld.Row[] rows = [.. ListWorld.Rows(12).Take(9), ListWorld.Cards(101, 102, 103, 104, 105), ListWorld.Cards(106, 107, 108, 109, 110), .. ListWorld.Rows(12).Skip(9)];
        var w = new ListWorld(rows);
        w.Ctx.KnowledgeFake!.IconItems.AddRange(first.Ctx.KnowledgeFake!.IconItems);
        w.Ctx.KnowledgeFake.Recipes.AddRange(first.Ctx.KnowledgeFake.Recipes);

        var r = w.Run(ListWorld.ItemId(50));

        Assert.AreEqual((JobState.Succeeded, "list.found"), Code(r), Data(r));
        Assert.AreEqual("jump", r.Data!["via"]);
        CollectionAssert.AreEqual(new[] { 105, 50 }, w.Selections.ToArray(), "only the unknown card at the remembered place, then the wanted one");
        Assert.AreEqual(9L, r.Data["deferred"], "the other new cards are left for later");
        Assert.IsFalse(w.Ctx.JournalFake.Records.Any(j => Equals(j.Fields.GetValueOrDefault("event"), "scanList.fullPass")));
        var place = RecipeList.ParsePlace(w.Ctx.KnowledgeFake.IconItems.Single(i => i.ItemId == ListWorld.ItemId(50)).Place)!.Value;
        Assert.AreEqual(2005, place.ListY, 3, "the new place is remembered");
    }

    [TestMethod]
    public void ACardThatMovedUp_IsFoundByTheFallbackWalkFromTheTop()
    {
        // five rows above it are gone and others were learned below: its old place shows other cards, the list ends
        var first = new ListWorld(ListWorld.Rows(12));
        Assert.AreEqual(JobState.Succeeded, first.Run().State);
        ListWorld.Row[] rows = [.. ListWorld.Rows(12).Skip(5), .. ListWorld.Rows(17).Skip(12)];
        var w = new ListWorld(rows);
        w.Ctx.KnowledgeFake!.IconItems.AddRange(first.Ctx.KnowledgeFake!.IconItems);
        w.Ctx.KnowledgeFake.Recipes.AddRange(first.Ctx.KnowledgeFake.Recipes);

        var r = w.Run(ListWorld.ItemId(50));

        Assert.AreEqual((JobState.Succeeded, "list.found"), Code(r), Data(r));
        Assert.AreEqual("fallback", r.Data!["via"]);
        CollectionAssert.AreEqual(new[] { 75, 50 }, w.Selections.ToArray(), "the unknown card at the old place, then the wanted card on the walk from the top");
        Assert.AreEqual(1, w.Ctx.JournalFake.Records.Count(j => Equals(j.Fields.GetValueOrDefault("event"), "scanList.fullPass")));
    }

    [TestMethod]
    public void AJumpWhoseMovementCannotBeMeasured_RestartsAsTheNormalWalkFromTheTop()
    {
        var first = new ListWorld(ListWorld.Rows(12));
        Assert.AreEqual(JobState.Succeeded, first.Run().State);
        var w = new ListWorld(ListWorld.Rows(12)) { BlankAfterDrag = n => n == 1 }; // the first jump drag
        w.Ctx.KnowledgeFake!.IconItems.AddRange(first.Ctx.KnowledgeFake!.IconItems);
        w.Ctx.KnowledgeFake.Recipes.AddRange(first.Ctx.KnowledgeFake.Recipes);

        var r = w.Run(ListWorld.ItemId(50));

        Assert.AreEqual((JobState.Succeeded, "list.found"), Code(r), Data(r));
        Assert.AreEqual(("fallback", 1L), ((string)r.Data!["via"]!, (long)r.Data["jumpDrags"]!));
        CollectionAssert.AreEqual(new[] { 50 }, w.Selections.ToArray());
        var pass = w.Ctx.JournalFake.Records.Single(j => Equals(j.Fields.GetValueOrDefault("event"), "scanList.fullPass"));
        Assert.AreEqual("jumpLost", pass.Fields["why"], "at once, not after walking to the list end");
        Assert.IsTrue(w.Ctx.JournalFake.Records.Any(j => Equals(j.Fields.GetValueOrDefault("event"), "scanList.positionsLost")));
    }

    [TestMethod]
    public void TheSameDishAtAnotherGrade_IsTheWantedCard()
    {
        // live 2026-10-16: Salad Savory 1064010 became 1064011 (another card picture); the walk passed it twice
        var w = new ListWorld(ListWorld.Rows(3));
        w.Ctx.KnowledgeFake!.Recipes.Add(new Recipe("dish 7|lifeskill|0", new ItemKey("dish 7"), 1, "lifeskill", [new MaterialRequirement(new ItemKey("x"), 9)], [], null,
            DateTimeOffset.UnixEpoch, "screen:craft.list/7", false, RecipeConfidence.Complete, null, null, ProductItemId: 999));

        var r = w.Run(999);

        Assert.AreEqual((JobState.Succeeded, "list.found"), Code(r), Data(r));
        Assert.AreEqual(ListWorld.ItemId(7), r.Data!["itemId"], "the id the game logged: the craft loop must expect this one");
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6, 7 }, w.Selections.ToArray());
    }

    [TestMethod]
    public void TheWantedRecipeStillSelected_IsFoundWithOneTip_AndNoListInput()
    {
        var w = new ListWorld(ListWorld.Rows(3));
        Assert.AreEqual("walk", w.Run(ListWorld.ItemId(8)).Data!["via"]);
        var (tips, drags, clicks) = (w.TipRuns, w.Drags, w.Selections.Count);

        var r = w.Run(ListWorld.ItemId(8)); // the craft left the recipe selected on the open Make view

        Assert.AreEqual((JobState.Succeeded, "list.found"), Code(r), Data(r));
        Assert.AreEqual(("selected", ListWorld.ItemId(8)), (r.Data!["via"], r.Data["itemId"]));
        Assert.AreEqual((tips + 1, drags, clicks), (w.TipRuns, w.Drags, w.Selections.Count), "one tip proves it; no drag, no card click");
        Assert.IsTrue(w.Ctx.Runner.Runs.Skip(w.RunsBefore).All(x => x.Id == "crafting.productTip"), "not even the open step");

        // another recipe selected: nothing pressed for it, the walk finds the card (its picture is known: no tip)
        w.Select(3);
        tips = w.TipRuns;
        var other = w.Run(ListWorld.ItemId(8));
        Assert.AreEqual((JobState.Succeeded, "list.found", "walk"), (other.State, other.Code, (string)other.Data!["via"]!), Data(other));
        Assert.AreEqual(tips, w.TipRuns);
        Assert.AreEqual(8, w.Selections[^1]);
        var check = w.Ctx.JournalFake.Records.Last(j => Equals(j.Fields.GetValueOrDefault("event"), "scanList.selected"));
        Assert.AreEqual("other", check.Fields["code"]);
    }

    [TestMethod]
    public void TheWantedPicture_IsHandledFirst_NoUnknownCardBeforeItIsClicked()
    {
        var w = new ListWorld(ListWorld.Rows(1));
        Assert.AreEqual(JobState.Succeeded, w.Run().State);
        var k = w.Ctx.KnowledgeFake!;
        k.IconItems.RemoveAll(i => i.ItemId != ListWorld.ItemId(5)); // cards 1..4 unknown again, no place to jump to
        k.IconItems[0] = k.IconItems[0] with { Place = null };
        w.Select(1);
        var clicks = w.Selections.Count;

        var r = w.Run(ListWorld.ItemId(5));

        Assert.AreEqual((JobState.Succeeded, "list.found", "walk"), (r.State, r.Code, (string)r.Data!["via"]!), Data(r));
        CollectionAssert.AreEqual(new[] { 5 }, w.Selections.Skip(clicks).ToArray(), "cards 1..4 come before it in the row, but are not clicked");
    }

    [TestMethod]
    public void AFindThatReachesTheEnd_IsNotFound()
    {
        var w = new ListWorld(ListWorld.Rows(2));
        Assert.AreEqual(JobState.Succeeded, w.Run().State);
        var clicks = w.Selections.Count;
        var r = w.Run(123);
        Assert.AreEqual((JobState.Failed, "list.notFound"), Code(r), Data(r));
        Assert.AreEqual(("listEnd", clicks), ((string)r.Data!["end"]!, w.Selections.Count), "every known card of another product skipped without a click");
    }

    [TestMethod]
    public void ADeferredRun_EndsTheWalkWithTheEnginesDeferredCode()
    {
        // the engine's "not now, try later" rung: never a halt; the goal loop tries this craft again later
        var w = new ListWorld(ListWorld.Rows(2)) { EndCardWith = (1, ProcedureStatus.Deferred) };
        var r = w.Run(ListWorld.ItemId(3));
        Assert.AreEqual(JobState.Failed, r.State, Data(r));
        StringAssert.Contains(r.Code, "deferred");
    }

    private const int ScanRecipeListCap = 3;

    private static (JobState, string) Code(JobResult r) => (r.State, r.Code);

    private static string Data(JobResult r) => r.Data is null ? string.Empty : string.Join(", ", r.Data.Select(kv => kv.Key + "=" + kv.Value));

    private static List<JournalRecord> CardRecords(ListWorld w) =>
        w.Ctx.JournalFake.Records.Where(j => Equals(j.Fields.GetValueOrDefault("event"), "scanList.card")).ToList();

    /// <summary>
    /// A simulated Make-tab list behind the job's contracts. The list is a column of sections (a header bar) and card rows
    /// (cards in columns 1..5, each a product id); the fake engine scrolls it by the drag's distance less 14 px (266 px for
    /// a 280 px drag: a drag loses its first pixels, like proof c14) when a drag run arrives, clamps at both ends, and
    /// selects the card under a click's point.
    /// Frames draw the viewport the way the real list looks to <see cref="RecipeList"/>.
    /// </summary>
    internal sealed class ListWorld : IScreenService
    {
        private const int HeaderHeight = 60;
        private const int RowHeight = 140;
        private const int TileTop = 20;
        private const int TileHeight = 100;
        private const int Lost = 14;
        private readonly FeatureHost _host;
        private readonly List<(int Top, int[] Ids, int Header)> _rows = [];
        private readonly int _height;
        private long _offset = 1000;
        private int _selected;

        public ListWorld(params Row[] rows)
            : this(true, rows)
        {
        }

        public ListWorld(bool withOpenAbility, params Row[] rows)
        {
            var y = 10;
            foreach (var row in rows)
            {
                _rows.Add((y, row.Ids, row.Header));
                y += row.Header > 0 ? HeaderHeight : RowHeight;
            }

            _height = y + 10;
            var (host, env, _) = T.LoadReal();
            _host = host;
            if (withOpenAbility)
            {
                env.Procedures.Put(new ProcedureDefinition("OpenAbility", "core", [new StepDefinition("open", StatePredicate.Any, new ClickAction("menu.ability"), Expectation.Nothing)], ProcedureLimits.Default));
            }

            Catalog = env.Procedures;
            Ctx = new FakeJobContext(env.Procedures) { Ui = T.Snapshot(_offset) with { AdvisoryStack = [MakeView] }, ScreenOverride = this };
            Ctx.Runner.Script = Engine;
        }

        private const string MakeView = "UI_LIFESKILL_MAKE_VIEW";

        /// <summary>Products whose card click does not move the selection frame (a press that missed).</summary>
        public HashSet<int> NotSelected { get; init; } = [];

        /// <summary>Scripted: after this card run (1-based, counted over the whole world) another tab is shown.</summary>
        public int? TabAwayAfterCard { get; init; }

        /// <summary>Scripted: after every n-th card run another tab is shown.</summary>
        public int? TabAwayEvery { get; init; }

        /// <summary>Selecting a product in <see cref="MismatchProducts"/> (unlearned) also switches to another tab, as the game does.</summary>
        public bool TabAwayOnMismatch { get; init; }

        /// <summary>The press on the Make tab does not bring the list back.</summary>
        public bool TabStuck { get; init; }

        /// <summary>While the list is away, the unselected Make tab is shown (false: something else covers the view).</summary>
        public bool OtherTabShown { get; init; } = true;

        /// <summary>The top UI while the list is away (default: the Make view stays on top, as on another tab).</summary>
        public string TopWhileAway { get; init; } = MakeView;

        /// <summary>The list page is not shown.</summary>
        public bool Away { get; private set; }

        /// <summary>Presses on the Make tab.</summary>
        public int TabRuns { get; private set; }

        /// <summary>List inputs (card clicks, drags) that arrived while the list was not shown.</summary>
        public int InputsWhileAway { get; private set; }

        /// <summary>Runner runs before the latest <see cref="Run"/>.</summary>
        public int RunsBefore { get; private set; }

        public FakeJobContext Ctx { get; }

        /// <summary>The loaded features (for jobs other than the scan).</summary>
        public FeatureHost Host => _host;

        public FakeProcedureBuilder Catalog { get; }

        public bool StartAtBottom { get; init; }

        public int ScrollY { get; private set; }

        public int MaxScroll => Math.Max(0, _height - RecipeList.Viewport.Height);

        public List<PointI> ClickPoints { get; } = [];

        /// <summary>Product id selected by each card run (0 = the click hit no card).</summary>
        public List<int> Selections { get; } = [];

        public int Drags { get; private set; }

        /// <summary>Scripted per drag (0-based count): the run reports "drag.partial" and the list does not move.</summary>
        public Func<int, bool>? PartialDrag { get; init; }

        /// <summary>Scripted per card run (1-based): end that run with this status.</summary>
        public (int Run, ProcedureStatus Status)? EndCardWith { get; init; }

        /// <summary>Scripted per card run (1-based): the run had to open the view again.</summary>
        public int? ReopenOnCard { get; init; }

        /// <summary>Products whose panel is not a recipe panel (both readers report ScreenMismatch).</summary>
        public HashSet<int> MismatchProducts { get; init; } = [];

        /// <summary>Products shown with a "Requires" line and a counter.</summary>
        public HashSet<int> WithRequires { get; } = [];

        /// <summary>The title OCR reads for a product (default its real name, "Dish n").</summary>
        public Func<int, string>? OcrTitle { get; init; }

        /// <summary>What the game logs when the selected product's icon is pressed (default: its item tip).</summary>
        public Func<int, TipLog> Tips { get; init; } = _ => TipLog.Logged;

        /// <summary>Product-tip runs (one icon click and one Esc each).</summary>
        public int TipRuns { get; private set; }

        /// <summary>Scripted per tip run (1-based): end that run with this status.</summary>
        public (int Run, ProcedureStatus Status)? EndTipWith { get; init; }

        /// <summary>The item id the game logs for a product.</summary>
        public static long ItemId(int id) => 1_051_000 + id;

        public static Row Header(int n) => new([], n);

        public static Row Cards(params int[] ids) => new(ids, 0);

        /// <summary><paramref name="n"/> full card rows of distinct products.</summary>
        public static Row[] Rows(int n) => Enumerable.Range(0, n).Select(r => Cards(Enumerable.Range((r * 5) + 1, 5).ToArray())).ToArray();

        public JobResult Run(CancellationToken ct = default) => Run(null, ct);

        /// <summary>Runs the job; with <paramref name="find"/> in find mode (the product's item id).</summary>
        public JobResult Run(long? find, CancellationToken ct = default)
        {
            ScrollY = StartAtBottom ? MaxScroll : Math.Min(StartScrollY, MaxScroll);
            RunsBefore = Ctx.Runner.Runs.Count;
            var parameters = find is { } id ? new JobParameters(new Dictionary<string, object> { ["find"] = id }) : JobParameters.Empty;
            return T.Handler(_host, "ScanRecipeList").Execute(Ctx, parameters, ct);
        }

        /// <summary>Where the list sits when a run starts (live 2026-10-15: 31 px below its top after the view opened).</summary>
        public int StartScrollY { get; init; }

        /// <summary>Puts the list where the game left it, for runs that do not start the scan job.</summary>
        public void ScrollTo(int scrollY) => ScrollY = Math.Clamp(scrollY, 0, MaxScroll);

        /// <summary>The pointer distance of every drag run (positive = towards the top).</summary>
        public List<int> DragDeltas { get; } = [];

        /// <summary>The press point (anchor where expected + offset) of every drag run.</summary>
        public List<PointI> DragStarts { get; } = [];

        /// <summary>Identity probes of the list page.</summary>
        public int Probes { get; private set; }

        /// <summary>The job's own captures.</summary>
        public int Captures { get; private set; }

        /// <summary>Reads of the selected recipe's panel outside a card run.</summary>
        public int PanelReads { get; private set; }

        /// <summary>The item id the game logs for a product's tip (default <see cref="ItemId"/>; another grade of a dish).</summary>
        public Func<int, long>? TipItemId { get; init; }

        /// <summary>The recipe shown selected in the panel (as the game keeps it after a craft).</summary>
        public void Select(int product) => _selected = product;

        private void SetAway(bool away)
        {
            Away = away;
            Ctx.Ui = Ctx.Ui with { AdvisoryStack = [away ? TopWhileAway : MakeView] };
        }

        // ---- IScreenService: frames of the list as it is now

        public CaptureResult Capture(CancellationToken ct)
        {
            Captures++;
            if (_blankNext)
            {
                // a frame that shows nothing of the list (the movement cannot be measured on it)
                _blankNext = false;
                var gray = new byte[1920 * 1009 * 4];
                Array.Fill(gray, (byte)128);
                return new(CaptureProblem.None, new Frame(1920, 1009, 1920 * 4, gray, TimeSpan.Zero, DateTimeOffset.UnixEpoch, T.Pos(_offset), default, false), TimeSpan.FromMilliseconds(30));
            }

            return new(CaptureProblem.None, Draw(), TimeSpan.FromMilliseconds(30));
        }

        private bool _blankNext;

        /// <summary>Scripted per drag (0-based count): the capture after it shows nothing of the list.</summary>
        public Func<int, bool>? BlankAfterDrag { get; init; }

        public HudClassification ClassifyHudStable(CancellationToken ct) => new(HudState.Covered, 0, 0, false, false, "list");

        /// <summary>The identity probes (the list page, another tab); the panel reads come from the procedure runs.</summary>
        public ScreenReadResult Read(string readerId, CancellationToken ct)
        {
            if (readerId is "craft.product" or "craft.pairs")
            {
                // the panel of the selected recipe (find mode reads it before any input); none shown away from the list
                PanelReads++;
                var mismatch = new ScreenReadResult(readerId, ReadStatus.ScreenMismatch, [], T.Pos(_offset), DateTimeOffset.UnixEpoch);
                return Away || _selected == 0 ? mismatch : readerId == "craft.product" ? ProductRead(_selected) : PairsRead(_selected);
            }

            var ok = readerId switch
            {
                "craft.list" => !Away,
                "craft.other.tab" => Away && OtherTabShown && TopWhileAway == MakeView,
                _ => throw new NotSupportedException("reads come from the procedure runs: " + readerId),
            };
            Probes += readerId == "craft.list" ? 1 : 0;
            return new ScreenReadResult(readerId, ok ? ReadStatus.Ok : ReadStatus.ScreenMismatch, [], T.Pos(_offset), DateTimeOffset.UnixEpoch);
        }

        private int _cardRuns;

        private ProcedureResult Engine(ProcedureDefinition p)
        {
            if (p.Id == "crafting.productTip")
            {
                return TipRun(p);
            }

            Assert.AreEqual("crafting.scanList", p.Id);
            var ids = p.Steps.Select(s => s.Id).ToArray();
            if (ids.Contains("tab"))
            {
                Assert.AreEqual(new ClickAction("craft.make.tab"), p.Steps.Single(s => s.Id == "tab").Action);
                TabRuns++;
                if (!TabStuck)
                {
                    SetAway(false);
                }

                return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 1, attempts: [Attempt("open", "skip.satisfied"), Attempt("tab", "click.done")]);
            }

            if (Away && (ids.Contains("top") || ids.Contains("scroll") || ids.Contains("card")))
            {
                InputsWhileAway++;
            }

            var attempts = new List<StepAttemptRecord>
            {
                Attempt("open", ids[0] == "open" && !p.Steps[0].Flags.HasFlag(StepFlags.SkipIfSatisfied) ? "call.done" : "skip.satisfied"),
            };
            if (ids.Contains("top") || ids.Contains("scroll"))
            {
                var step = ids.Contains("top") ? "top" : "scroll";
                var drag = (DragAction)p.Steps.Single(s => s.Id == step).Action;
                DragDeltas.Add(drag.Delta.Y);
                DragStarts.Add(RecipeList.AnchorAt.Offset(drag.Offset.X, drag.Offset.Y));
                var partial = PartialDrag?.Invoke(Drags) == true;
                _blankNext = BlankAfterDrag?.Invoke(Drags) == true;
                Drags++;
                if (!partial)
                {
                    // the list follows all of a drag but its first 14 px (a 280 px drag moves it 266 px, like proof c14)
                    var moved = Math.Abs(drag.Delta.Y) - Lost;
                    ScrollY = Math.Clamp(ScrollY + (drag.Delta.Y > 0 ? -moved : moved), 0, MaxScroll);
                }

                attempts.Add(Attempt(step, partial ? "drag.partial" : "drag.done"));
                return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 1, attempts: attempts);
            }

            if (!ids.Contains("card"))
            {
                return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 3, attempts: attempts);
            }

            _cardRuns++;
            if (EndCardWith is { } end && end.Run == _cardRuns)
            {
                var halt = end.Status == ProcedureStatus.Halted ? new HaltReport(HaltCode.ForbiddenUi, "UI_RECHARGE", "j", p.Id, "card", DateTimeOffset.UnixEpoch, null) : null;
                return T.Result(p.Id, end.Status, halt: halt, inputs: 1);
            }

            if (ReopenOnCard == _cardRuns)
            {
                attempts[0] = Attempt("open", "call.done");
            }

            var click = (ClickAction)p.Steps.Single(s => s.Id == "card").Action;
            var point = RecipeList.AnchorAt.Offset(click.Offset!.Value.X, click.Offset.Value.Y); // the anchor is found where expected
            ClickPoints.Add(point);
            var hit = CardAt(point);
            Selections.Add(hit);
            if (hit > 0)
            {
                _selected = hit;
                if (!NotSelected.Contains(hit))
                {
                    _selectedAt = CardPlaceAt(point);
                }
            }

            _totalCardRuns++;
            if (_totalCardRuns == TabAwayAfterCard || (TabAwayEvery is { } every && _totalCardRuns % every == 0)
                || (TabAwayOnMismatch && MismatchProducts.Contains(_selected)))
            {
                // a stray press switched the tab right after this card: the panel was read, the list is gone
                _pendingAway = true;
            }

            _offset += 100;
            if (_pendingAway)
            {
                _pendingAway = false;
                SetAway(true);
            }

            var reads = MismatchProducts.Contains(_selected)
                ? new Dictionary<string, ScreenReadResult>
                {
                    ["product"] = ProductRead(_selected) with { Status = ReadStatus.ScreenMismatch },
                    ["pairs"] = PairsRead(_selected) with { Status = ReadStatus.ScreenMismatch },
                }
                : new Dictionary<string, ScreenReadResult>
                {
                    ["product"] = ProductRead(_selected),
                    ["pairs"] = PairsRead(_selected),
                };
            return T.Result(p.Id, ProcedureStatus.Succeeded, reads, inputs: 1, attempts: attempts);
        }

        private static StepAttemptRecord Attempt(string step, string detail) =>
            new(step, 1, StepOutcome.Ok, OutcomeClass.Ok, RecoveryAction.Proceed, null, false, null, detail, DateTimeOffset.UnixEpoch);

        /// <summary>The product icon pressed and the tip closed: the log (the UI snapshot) gets what <see cref="Tips"/> says.</summary>
        private ProcedureResult TipRun(ProcedureDefinition p)
        {
            Assert.AreEqual(new ClickAction("craft.materials.header", new PointI(-140, -266)), p.Steps.Single(s => s.Id == "tip.open").Action, "the product icon, placed from the materials header");
            TipRuns++;
            if (EndTipWith is { } end && end.Run == TipRuns)
            {
                var halt = end.Status == ProcedureStatus.Halted ? new HaltReport(HaltCode.ForbiddenUi, "UI_RECHARGE", "j", p.Id, "tip.open", DateTimeOffset.UnixEpoch, null) : null;
                return T.Result(p.Id, end.Status, halt: halt, inputs: 1);
            }

            var at = new DateTime(2026, 10, 15, 1, 44, 58, 553);
            switch (Tips(_selected))
            {
                case TipLog.Logged:
                    _offset += 10;
                    Ctx.Ui = Ctx.Ui with { AsOf = T.Pos(_offset), LastItemTip = new ItemTipInfo("Dish " + _selected, TipItemId?.Invoke(_selected) ?? ItemId(_selected), at, T.Pos(_offset)) };
                    break;
                case TipLog.BeforeTheClick:
                    // a tip line older than the click (not after the snapshot's position) shows up only now
                    Ctx.Ui = Ctx.Ui with { LastItemTip = new ItemTipInfo("Lime Water", 20_330_001, at, T.Pos(Ctx.Ui.AsOf!.Offset - 5)) };
                    break;
            }

            return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 2);
        }

        private ScreenReadResult ProductRead(int id)
        {
            var values = new List<ReadValue> { T.Text("product.name", OcrTitle?.Invoke(id) ?? "Dish " + id), T.Int("product.owned", id) };
            values.Add(WithRequires.Contains(id) ? T.Pair("product.counter", 5, 300) : new ReadValue("product.counter", ReadStatus.Ok, null, null, null, null, 0.9, "absent"));
            return T.Read("craft.product", _offset, [.. values]);
        }

        private ScreenReadResult PairsRead(int id)
        {
            var values = new List<ReadValue>
            {
                new("materials", ReadStatus.Ok, 2, null, null, null, 0.9, "tiles"),
                new("icon.1", ReadStatus.Ok, null, null, Icon(id), null, 0.9, "icon"),
                T.Pair("pair.1", 10 + id, 1 + (id % 3)),
                new("icon.2", ReadStatus.Ok, null, null, Icon(200), null, 0.9, "icon"),
                T.Pair("pair.2", 500, 2),
                WithRequires.Contains(id) ? T.Pair("requires", 86, 5) : new ReadValue("requires", ReadStatus.Ok, null, null, null, null, 0.9, "absent"),
            };
            return T.Read("craft.pairs", _offset + 1, [.. values]);
        }

        private int _totalCardRuns;
        private bool _pendingAway;
        private (int Row, int Column)? _selectedAt;

        /// <summary>The list row index and column of the card under a client point, or null.</summary>
        private (int Row, int Column)? CardPlaceAt(PointI p)
        {
            var listY = p.Y - RecipeList.Viewport.Top + ScrollY;
            for (var i = 0; i < _rows.Count; i++)
            {
                var (top, ids, header) = _rows[i];
                if (header > 0 || listY < top + TileTop || listY >= top + TileTop + TileHeight)
                {
                    continue;
                }

                for (var c = 0; c < ids.Length; c++)
                {
                    if (Math.Abs(p.X - (RecipeList.Columns[c] - 1)) <= 48)
                    {
                        return (i, c);
                    }
                }
            }

            return null;
        }

        /// <summary>The product id of the card under a client point, 0 when none.</summary>
        private int CardAt(PointI p)
        {
            var listY = p.Y - RecipeList.Viewport.Top + ScrollY;
            foreach (var (top, ids, header) in _rows)
            {
                if (header > 0 || listY < top + TileTop || listY >= top + TileTop + TileHeight)
                {
                    continue;
                }

                for (var c = 0; c < ids.Length; c++)
                {
                    if (Math.Abs(p.X - (RecipeList.Columns[c] - 1)) <= 48)
                    {
                        return ids[c];
                    }
                }
            }

            return 0;
        }

        private Frame Draw()
        {
            const int w = 1920;
            const int h = 1009;
            var px = new byte[w * h * 4];
            void Put(int x, int y, int luma)
            {
                var o = ((y * w) + x) * 4;
                px[o] = px[o + 1] = px[o + 2] = (byte)luma;
                px[o + 3] = 255;
            }

            void PutRgb(int x, int y, (byte R, byte G, byte B) c)
            {
                var o = ((y * w) + x) * 4;
                (px[o], px[o + 1], px[o + 2], px[o + 3]) = (c.B, c.G, c.R, 255);
            }

            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    Put(x, y, 128);
                }
            }

            var vp = RecipeList.Viewport;
            for (var y = vp.Top; y < vp.Bottom; y++)
            {
                var listY = y - vp.Top + ScrollY;
                for (var x = vp.Left; x < vp.Right; x++)
                {
                    Put(x, y, listY < _height ? 211 : 200);
                }

                for (var row = 0; row < _rows.Count; row++)
                {
                    var (top, ids, header) = _rows[row];
                    var r = listY - top;
                    if (_selectedAt is { } sel && sel.Row == row && r >= TileTop + 30 && r <= TileTop + TileHeight + 6)
                    {
                        // the selection frame: mint strips just outside both side outlines of the selected card
                        var left = RecipeList.Columns[sel.Column] - 50;
                        for (var x = left - 11; x <= left - 5; x++)
                        {
                            PutRgb(x, y, (200, 252, 228));
                        }

                        for (var x = left + 102; x <= left + 108; x++)
                        {
                            PutRgb(x, y, (200, 252, 228));
                        }
                    }

                    if (header > 0 && r is >= 20 and < 40)
                    {
                        for (var x = 330; x < 330 + (60 * header); x++)
                        {
                            Put(x, y, 90);
                        }
                    }

                    if (header > 0 || r < TileTop || r >= TileTop + TileHeight)
                    {
                        continue;
                    }

                    var t = r - TileTop;
                    for (var c = 0; c < ids.Length; c++)
                    {
                        var left = RecipeList.Columns[c] - 50;
                        for (var x = left; x <= left + 97; x++)
                        {
                            var dx = x - left;
                            var edge = dx == 0 || dx == 97 || t == 0 || t == TileHeight - 1;
                            var border = dx is >= 1 and <= 4 || dx is >= 93 and <= 96 || t is >= 1 and <= 4 || t is >= TileHeight - 5;
                            if (edge || border)
                            {
                                Put(x, y, edge ? 120 : 245);
                            }
                            else
                            {
                                PutRgb(x, y, Picture(ids[c], dx, t));
                            }
                        }
                    }
                }
            }

            return new Frame(w, h, w * 4, px, TimeSpan.Zero, DateTimeOffset.UnixEpoch, T.Pos(_offset), default, false);
        }

        private static readonly Dictionary<int, (byte R, byte G, byte B)[]> Pictures = [];

        /// <summary>
        /// A product's picture inside its tile: 8 x 8 blocks of colours of its own (the same product looks the same in
        /// every place, different products differ), darker than the white border, so the tile outlines stay as they are.
        /// </summary>
        private static (byte R, byte G, byte B) Picture(int id, int dx, int t)
        {
            (byte R, byte G, byte B)[] blocks;
            lock (Pictures)
            {
                if (!Pictures.TryGetValue(id, out blocks!))
                {
                    var rnd = new Random(1000 + id);
                    blocks = [.. Enumerable.Range(0, 64).Select(_ => ((byte)rnd.Next(30, 200), (byte)rnd.Next(30, 200), (byte)rnd.Next(30, 200)))];
                    Pictures[id] = blocks;
                }
            }

            return blocks[(Math.Clamp(t / 13, 0, 7) * 8) + Math.Clamp(dx / 13, 0, 7)];
        }

        /// <summary>A section header (n &gt; 0) or a row of product ids.</summary>
        public sealed record Row(int[] Ids, int Header);
    }

    /// <summary>What the game logs when a product icon is pressed.</summary>
    internal enum TipLog
    {
        /// <summary>The product's item tip (its real name and id), after the click.</summary>
        Logged,

        /// <summary>Nothing.</summary>
        Nothing,

        /// <summary>Only an item tip from before the click.</summary>
        BeforeTheClick,
    }
}
