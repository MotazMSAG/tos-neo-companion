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
/// ScanStock against a simulated Stock tab: the real inventory.scanStock / inventory.stockTip data procedures, a fake
/// engine that opens the Stock tab, switches sub-tabs, scrolls each sub-tab's grid when the drag runs arrive and "logs"
/// the item tip of the tile under a tip press (anchor + offset); reads built from the simulated grid (screen identity
/// per reader: the Stock tab, the selected sub-tab); frames drawn from the grid for the job's shift measurement. No
/// window, no game, no log.
/// </summary>
[TestClass]
public sealed class ScanStockTests
{
    private const string MakeView = "UI_LIFESKILL_MAKE_VIEW";

    // ------------------------------------------------------------------ definition and data

    [TestMethod]
    public void TheJobIsAReadOnlyNavigationScan_WithNoParamsAndNoSwitch()
    {
        var (host, env, _) = T.LoadReal();
        var def = T.Definition(host, "ScanStock");
        Assert.AreEqual(("inventory", JobKind.Navigation, AutomationSwitch.None, 0, 20), (def.FeatureId, def.Kind, def.Switch, def.Params.Count, def.MaxPerHour));
        var p = env.Procedures.Get("inventory.scanStock")!;
        Assert.AreEqual((ProcedureKind.Navigation, AutomationSwitch.None), (p.Kind, p.Gate));
        Assert.IsFalse(p.Steps.Any(s => s.Flags.HasFlag(StepFlags.Commit) || s.Action is AwaitCraftAction), "never crafts");
        CollectionAssert.AreEqual(
            new[] { "open", "announce.close", "stock.open", "stock.settle", "sub.ability", "sub.homestead", "sub.tailor", "sub.hyperweather", "sub.settle", "top", "scroll", "scroll.settle", "back", "back.settle" },
            p.Steps.Select(s => s.Id).ToArray());
        Assert.AreEqual(new ClickAction(StockGrid.StockTabTemplate), p.Steps.Single(s => s.Id == "stock.open").Action, "the right-hand Stock tab, on its own template");
        Assert.AreEqual(new ClickAction(StockGrid.MakeTabTemplate), p.Steps.Single(s => s.Id == "back").Action);
        Assert.IsTrue(p.Steps.Where(s => s.Action is DragAction).All(s => s.Action is DragAction { TemplateId: StockGrid.AnchorTemplate }), "every drag hangs off the Stock anchor");
        foreach (var sub in StockGrid.SubTabs)
        {
            var click = (ClickAction)p.Steps.Single(s => s.Id == sub.StepId).Action;
            Assert.AreEqual(StockGrid.AnchorTemplate, click.TemplateId);
            Assert.AreEqual(sub.Center, StockGrid.AnchorAt.Offset(click.Offset!.Value.X, click.Offset.Value.Y), sub.Name + ": the live sub-tab centre (y 226)");
            Assert.AreEqual(MakeView, p.Steps.Single(s => s.Id == sub.StepId).Pre.TopUi);
        }

        // the drags start in the gap between the 3rd and 4th columns (x 940, as the live exploration) and stay in the grid
        var top = (DragAction)p.Steps.Single(s => s.Id == "top").Action;
        var scroll = (DragAction)p.Steps.Single(s => s.Id == "scroll").Action;
        Assert.AreEqual((new PointI(940, 420), new PointI(0, 240)), (StockGrid.AnchorAt.Offset(top.Offset.X, top.Offset.Y), top.Delta));
        // row 2 of the grid, not the bottom: near the list end the bottom is plain parchment and the drag patch is refused (live 2026-10-15)
        Assert.AreEqual((new PointI(940, 600), new PointI(0, -240)), (StockGrid.AnchorAt.Offset(scroll.Offset.X, scroll.Offset.Y), scroll.Delta));

        var tip = env.Procedures.Get("inventory.stockTip")!;
        CollectionAssert.AreEqual(new[] { "tip.open", "tip.close" }, tip.Steps.Select(s => s.Id).ToArray());
        Assert.IsTrue(tip.Steps[0].Action is ClickAction { TemplateId: StockGrid.AnchorTemplate, Offset: not null });
        CollectionAssert.AreEqual(new[] { new UiExpectation(UiVerb.Show, "UI_ITEM_TIPS_PASS_TOUCH") }, tip.Steps[0].Expect.UiEvents!.ToArray());
        Assert.AreEqual((new KeyAction(GameKey.Escape), "UI_ITEM_TIPS_PASS_TOUCH"), (tip.Steps[1].Action, tip.Steps[1].Pre.TopUi));
    }

    [TestMethod]
    public void ABrokenDataProcedure_FailsBeforeAnyInput()
    {
        static Func<ProcedureDefinition, ProcedureDefinition> Step(string id, StepAction action) =>
            d => d with { Steps = d.Steps.Select(s => s.Id == id ? s with { Action = action } : s).ToArray() };
        Func<ProcedureDefinition, ProcedureDefinition>[] broken =
        [
            d => d with { Kind = ProcedureKind.Commit },
            Step("scroll", new DragAction(StockGrid.AnchorTemplate, new PointI(-815, 123), new PointI(0, -380))), // could skip a row
            Step("scroll", new DragAction(StockGrid.AnchorTemplate, new PointI(-815, 223), new PointI(0, -240))), // starts below the grid
            Step("top", new DragAction(StockGrid.AnchorTemplate, new PointI(-815, -157), new PointI(0, -240))),
            Step("sub.homestead", new ClickAction(StockGrid.AnchorTemplate, new PointI(-913, -351))), // presses Tailor
            Step("sub.tailor", new ClickAction(StockGrid.AnchorTemplate)),
            Step("stock.open", new ClickAction(StockGrid.AnchorTemplate, new PointI(-15, 0))),
            Step("back", new ClickAction("craft.make")),
            d => d with { Steps = d.Steps.Where(s => s.Id != "sub.hyperweather").ToArray() },
        ];
        foreach (var breakIt in broken)
        {
            var w = new StockWorld(StockWorld.Grid(1));
            w.Catalog.Put(breakIt(w.Catalog.Get("inventory.scanStock")!));
            Assert.AreEqual((JobState.Failed, "procedure.invalid:inventory.scanStock"), Code(w.Run()));
            Assert.HasCount(0, w.Ctx.Runner.Runs);
        }

        Func<ProcedureDefinition, ProcedureDefinition>[] brokenTip =
        [
            d => d with { Steps = d.Steps.Select(s => s.Id == "tip.open" ? s with { Flags = StepFlags.Commit } : s).ToArray() },
            Step("tip.open", new ClickAction(StockGrid.AnchorTemplate)),
            Step("tip.open", new ClickAction(StockGrid.AnchorTemplate, new PointI(0, 330))), // lands on the Homestead footer
            Step("tip.open", new ClickAction("craft.materials.header", new PointI(-140, -266))),
            d => d with { Steps = d.Steps.Where(s => s.Id != "tip.close").ToArray() },
        ];
        foreach (var breakIt in brokenTip)
        {
            var w = new StockWorld(StockWorld.Grid(1));
            w.Catalog.Put(breakIt(w.Catalog.Get("inventory.stockTip")!));
            Assert.AreEqual((JobState.Failed, "procedure.invalid:inventory.stockTip"), Code(w.Run()));
            Assert.HasCount(0, w.Ctx.Runner.Runs);
        }
    }

    // ------------------------------------------------------------------ scans

    [TestMethod]
    public void FirstRun_PressesEachNewIconOnce_LearnsItsItem_AndRecordsEveryCountUnderTheItemId()
    {
        var w = new StockWorld(StockWorld.Grid(3, first: 1), homestead: StockWorld.Grid(1, first: 101), tailor: StockWorld.Grid(1, first: 201, perRow: 5), hyperweather: StockWorld.Grid(1, first: 301, perRow: 3));
        var r = w.Run();

        Assert.AreEqual((JobState.Succeeded, "stock.scanned"), Code(r), Data(r));
        var items = 18 + 6 + 5 + 3;
        Assert.AreEqual(items, w.TipItems.Count, "one tip per icon");
        CollectionAssert.AreEquivalent(w.AllTiles.Select(t => t.ItemId).ToArray(), w.TipItems.ToArray(), "every icon was pressed exactly once");
        var k = w.Ctx.KnowledgeFake!;
        Assert.HasCount(items, k.IconItems, "every icon's item is learned");
        foreach (var tile in w.AllTiles)
        {
            var key = new ItemKey(StockWorld.NameOf(tile.ItemId).ToLowerInvariant());
            Assert.IsTrue(k.Items.TryGetValue(key, out var e), key.Value);
            Assert.AreEqual((tile.Count ?? 0, (long?)tile.ItemId), (e!.Count!.Value, e.ItemId), key.Value);
        }

        Assert.IsTrue(k.Observations.All(o => o.Kind == ObservationKind.StockTab && o.ItemId is not null));
        CollectionAssert.AreEquivalent(
            new[] { "screen:stock/ability", "screen:stock/homestead", "screen:stock/tailor", "screen:stock/hyperweather" },
            k.Observations.Select(o => o.Coverage).Distinct().ToArray());
        Assert.AreEqual(((long)items, 0L, (long)items, 0L, "allSubTabs", true), (Sum(r, "items"), Sum(r, "known"), Sum(r, "named"), Sum(r, "unreadable"), (string)r.Data!["end"]!, (bool)r.Data["backToMake"]!));
        Assert.AreEqual(4, r.Data.Keys.Count(key => key.EndsWith(".end", StringComparison.Ordinal)), "per sub-tab: items, known, named, unreadable, end");
        Assert.IsLessThanOrEqualTo(32, r.Data.Count, "the host keeps 32 result fields");
        Assert.AreEqual((18L, 0L, 18L, 0L, "listEnd"), ((long)r.Data["ability.items"]!, (long)r.Data["ability.known"]!, (long)r.Data["ability.named"]!, (long)r.Data["ability.unreadable"]!, (string)r.Data["ability.end"]!));
        Assert.AreEqual((5L, 5L), ((long)r.Data["tailor.items"]!, (long)r.Data["tailor.named"]!));
        Assert.IsTrue(k.Saves >= 4, "stored when each sub-tab ends");
        Assert.AreEqual(1, w.BackPresses, "back to the Make tab at the end");
        Assert.IsTrue(w.ClickPoints.All(StockGrid.Pressable), "every press inside the grid");

        // human pace: a fixed pause before every input after the first
        Assert.IsTrue(w.Ctx.ClockFake.Monotonic - TimeSpan.FromHours(1) >= TimeSpan.FromSeconds(2) * (items + w.Drags + 4));
    }

    [TestMethod]
    public void ASecondRun_PressesNoKnownIcon_AndOnlyReadsTheCounts()
    {
        var w = new StockWorld(StockWorld.Grid(2, first: 1), homestead: StockWorld.Grid(1, first: 101));
        Assert.AreEqual(JobState.Succeeded, w.Run().State);
        var pressed = w.TipItems.Count;
        Assert.AreEqual(18, pressed);

        // the counts changed in the meantime; the icons did not
        w.Recount(id => id * 3);
        w.ResetScroll();
        w.TipItems.Clear();
        var r = w.Run();

        Assert.AreEqual((JobState.Succeeded, "stock.scanned"), Code(r), Data(r));
        Assert.IsEmpty(w.TipItems, "a known icon is never pressed again");
        Assert.AreEqual((18L, 18L, 0L, 0L), (Sum(r, "items"), Sum(r, "known"), Sum(r, "named"), (long)r.Data!["tips"]!));
        Assert.AreEqual(5L * 3, w.Ctx.KnowledgeFake!.Items[new ItemKey("item 5")].Count, "the new count, under the item learned in the first run");
        Assert.IsTrue(w.Ctx.JournalFake.Records.Where(j => Equals(j.Fields.GetValueOrDefault("event"), "stock.tile")).TakeLast(18).All(j => Equals(j.Fields["code"], "known")));
    }

    [TestMethod]
    public void AFadedTileWithoutACount_IsZero_AndATileInColourWithoutACountIsLeftUnknown()
    {
        var tiles = new[]
        {
            new StockWorld.Tile(1, 4, false), new StockWorld.Tile(2, null, true), new StockWorld.Tile(3, null, true),
            new StockWorld.Tile(4, null, false), new StockWorld.Tile(5, 12, false), new StockWorld.Tile(6, null, true),
        };
        var w = new StockWorld([tiles]);
        var r = w.Run();

        Assert.AreEqual(JobState.Succeeded, r.State, Data(r));
        var k = w.Ctx.KnowledgeFake!;
        Assert.AreEqual((0L, 0L, 0L), (k.Items[new ItemKey("item 2")].Count!.Value, k.Items[new ItemKey("item 3")].Count!.Value, k.Items[new ItemKey("item 6")].Count!.Value), "faded = 0");
        Assert.IsFalse(k.Items.ContainsKey(new ItemKey("item 4")), "no count and not faded: unknown, never 0");
        Assert.AreEqual((4L, 12L), (k.Items[new ItemKey("item 1")].Count!.Value, k.Items[new ItemKey("item 5")].Count!.Value));
        Assert.AreEqual((5L, 1L, 6L), ((long)r.Data!["ability.items"]!, (long)r.Data["ability.unreadable"]!, (long)r.Data["tips"]!), "item 4's name is still learned");
        Assert.AreEqual(3L, (long)r.Data["zeros"]!);
        var faded = w.Ctx.JournalFake.Records.Where(j => Equals(j.Fields.GetValueOrDefault("event"), "stock.tile") && Equals(j.Fields["faded"], true)).ToList();
        Assert.HasCount(3, faded);
        Assert.IsTrue(faded.All(j => Equals(j.Fields["count"], 0L)));
    }

    [TestMethod]
    public void FromTheMiddle_ItScrollsToTheTop_ReadsEveryTileOnce_AndStopsWhenTheGridNoLongerMoves()
    {
        var w = new StockWorld(StockWorld.Grid(8, first: 1)) { StartScroll = 500 };
        var r = w.Run();

        Assert.AreEqual((JobState.Succeeded, "stock.scanned"), Code(r), Data(r));
        Assert.AreEqual("listEnd", r.Data!["ability.end"]);
        Assert.AreEqual(48L, (long)r.Data["ability.items"]!, "every tile once");
        Assert.AreEqual(48, w.TipItems.Distinct().Count());
        Assert.AreEqual(48, w.TipItems.Count, "no tile pressed twice although each shows on two screens");
        Assert.AreEqual(w.MaxScroll("ability"), w.ScrollOf("ability"), "the scan ends at the bottom of the grid");
        Assert.AreEqual(0L, (long)r.Data["gaps"]!, "280 px moves never skip a row");

        var drags = w.Ctx.JournalFake.Records.Where(j => Equals(j.Fields.GetValueOrDefault("event"), "stock.drag") && Equals(j.Fields["subTab"], "ability")).ToList();
        Assert.AreEqual(w.DragsOf("ability"), drags.Count);
        Assert.IsTrue(drags.Any(j => Equals(j.Fields["shiftPx"], -280L)), "the job measures the real movement (280 px for a 240 px drag)");
        Assert.IsTrue(drags.Last().Fields["unchanged"] is true, "a drag that no longer moves the grid ends it");
    }

    [TestMethod]
    public void EachSubTabSwitch_IsVerifiedByItsOwnReader_AndOneThatDoesNotSwitchEndsTheScan()
    {
        var ok = new StockWorld(StockWorld.Grid(1, first: 1), homestead: StockWorld.Grid(1, first: 101));
        Assert.AreEqual(JobState.Succeeded, ok.Run().State);
        CollectionAssert.AreEqual(new[] { "ability", "homestead", "tailor", "hyperweather" }, ok.SubTabPresses.ToArray(), "every sub-tab once, in order");
        foreach (var sub in StockGrid.SubTabs)
        {
            // right after its press, the next read is the sub-tab's own reader (its identity needs the selected sub-tab)
            var press = ok.Events.IndexOf("press:" + sub.Name);
            Assert.AreEqual("read:" + sub.ReaderId, ok.Events[press + 1], sub.Name);
        }

        var w = new StockWorld(StockWorld.Grid(1, first: 1), homestead: StockWorld.Grid(1, first: 101)) { BrokenSubTabs = ["homestead"] };
        var r = w.Run();
        Assert.AreEqual((JobState.Failed, "subtab.notSwitched:homestead"), Code(r), Data(r));
        Assert.AreEqual(2, w.Events.Count(e => e == "read:inventory.stock.homestead"), "read once more after a pause before giving up");
        Assert.IsFalse(w.TipItems.Any(id => id > 100), "nothing of the other sub-tab was pressed or read as homestead");
        Assert.AreEqual(6L, (long)r.Data!["ability.items"]!, "the Ability sub-tab read before is kept");
        Assert.AreEqual("notSwitched", r.Data["homestead.end"]);
        Assert.IsTrue(w.Ctx.JournalFake.Records.Any(j => Equals(j.Fields.GetValueOrDefault("event"), "stock.subTab") && Equals(j.Fields["code"], "notSwitched")));
    }

    [TestMethod]
    public void AListThatEndsOnScreen_IsReadWithoutAScrollDrag()
    {
        // Tailor and Hyperweather (one row, nothing drawn below it): only the drag that proves the top
        var w = new StockWorld(StockWorld.Grid(3, first: 1), tailor: StockWorld.Grid(1, first: 201, perRow: 5), hyperweather: StockWorld.Grid(1, first: 301, perRow: 3));
        var r = w.Run();
        Assert.AreEqual(JobState.Succeeded, r.State, Data(r));
        Assert.AreEqual((1, 1, 1), (w.DragsOf("tailor"), w.DragsOf("hyperweather"), w.DragsOf("homestead")), "an empty sub-tab ends on screen too");
        Assert.IsGreaterThan(1, w.DragsOf("ability"), "three rows reach the bottom edge: the end is proven by a drag");
        Assert.AreEqual(("listEnd", "listEnd"), ((string)r.Data!["tailor.end"]!, (string)r.Data["hyperweather.end"]!), Data(r));
        Assert.AreEqual(3, w.Ctx.JournalFake.Records.Count(j => Equals(j.Fields.GetValueOrDefault("event"), "stock.listEnd") && Equals(j.Fields["code"], "endVisible")));
    }

    [TestMethod]
    public void TheStockTab_IsPressedOnlyWhenNotShown_AndItsSwitchIsVerified()
    {
        var already = new StockWorld(StockWorld.Grid(1)) { StartOnStock = true };
        Assert.AreEqual(JobState.Succeeded, already.Run().State);
        Assert.AreEqual(0, already.StockPresses, "a read already shows the Stock tab");

        var closed = new StockWorld(StockWorld.Grid(1));
        var r = closed.Run();
        Assert.AreEqual(JobState.Succeeded, r.State);
        Assert.AreEqual(1, closed.StockPresses);
        Assert.IsTrue(closed.Ctx.JournalFake.Records.Any(j => Equals(j.Fields.GetValueOrDefault("event"), "stock.open") && Equals(j.Fields["code"], "opened")));
        var press = closed.Events.IndexOf("press:stock");
        Assert.AreEqual("read:" + StockGrid.ProbeReader, closed.Events[press + 1], "the switch logs no UI event: a read proves it");

        var broken = new StockWorld(StockWorld.Grid(1)) { StockTabBroken = true };
        var b = broken.Run();
        Assert.AreEqual((JobState.Failed, "stock.notOpened"), Code(b), Data(b));
        Assert.IsEmpty(broken.SubTabPresses, "no sub-tab is pressed on another tab");
        Assert.IsEmpty(broken.TipItems);
    }

    [TestMethod]
    public void AForbiddenArea_IsNeverPressed()
    {
        // a tile drawn over the Homestead footer ("Expand Inventory" at about (1446,872)) is never pressed for its tip
        var w = new StockWorld(StockWorld.Grid(1, first: 1)) { ExtraTile = (new StockWorld.Tile(99, 7, false), new RectI(1411, 820, 1526, 940)) };
        var r = w.Run();
        Assert.AreEqual(JobState.Succeeded, r.State, Data(r));
        Assert.IsFalse(w.TipItems.Contains(99));
        Assert.IsTrue(w.ClickPoints.All(p => StockGrid.Pressable(p) && !StockGrid.Forbidden(p)));
        Assert.IsTrue(w.Ctx.JournalFake.Records.Any(j => Equals(j.Fields.GetValueOrDefault("code"), "notPressable")));
        Assert.IsFalse(w.Ctx.KnowledgeFake!.IconItems.Any(i => i.ItemId == 99));

        // the geometry: the footer's buttons lie in the forbidden areas; no tile of the grid does
        foreach (var p in new[] { new PointI(1446, 872), new PointI(1627, 900), new PointI(460, 880), new PointI(780, 875), new PointI(1220, 875) })
        {
            Assert.IsTrue(StockGrid.Forbidden(p) && !StockGrid.Pressable(p), p.ToString());
        }

        foreach (var left in new[] { 356, 568, 779, 989, 1200, 1411 })
        {
            foreach (var top in new[] { 280, 450, 640, 690 })
            {
                Assert.IsTrue(StockGrid.Pressable(StockGrid.PressPoint(new RectI(left, top, left + 115, top + 120))), $"{left},{top}");
            }
        }
    }

    [TestMethod]
    public void HomesteadCounts_AreVolatile_TheOthersAreNot()
    {
        var w = new StockWorld(StockWorld.Grid(1, first: 1), homestead: StockWorld.Grid(1, first: 101));
        Assert.AreEqual(JobState.Succeeded, w.Run().State);
        var k = w.Ctx.KnowledgeFake!;
        var home = k.Observations.Where(o => o.Coverage == "screen:stock/homestead").ToList();
        Assert.HasCount(6, home);
        Assert.IsTrue(home.All(o => o.FreshUntilUtc == o.AtUtc + TimeSpan.FromMinutes(10)), "shared goods: fresh for 10 minutes");
        Assert.IsTrue(k.Observations.Where(o => o.Coverage != "screen:stock/homestead").All(o => o.FreshUntilUtc is null));
    }

    [TestMethod]
    public void WithoutALoggedTip_TheCountIsKeptUnderTheIconKey_AndTheIconIsNotPressedAgainInTheJob()
    {
        // the game names nothing; one icon is drawn twice (pressed once, its two counts cannot be told apart)
        var twice = StockWorld.KeyOf(7);
        var row = new[] { new StockWorld.Tile(7, 3, false), new StockWorld.Tile(8, 4, false, twice), new StockWorld.Tile(9, 5, false) };
        var w = new StockWorld([row]) { Tips = _ => TipLog.Nothing };
        var r = w.Run();

        Assert.AreEqual(JobState.Succeeded, r.State, Data(r));
        Assert.HasCount(2, w.ClickPoints, "one press per icon");
        Assert.AreEqual((2L, 2L, 1L, 1L), ((long)r.Data!["tips"]!, (long)r.Data["tipsMissing"]!, (long)r.Data["conflicts"]!, (long)r.Data["ability.items"]!));
        var k = w.Ctx.KnowledgeFake!;
        Assert.AreEqual(5L, k.Items[new ItemKey(StockWorld.KeyOf(9))].Count, "the icon is the identity until the game names it");
        Assert.IsFalse(k.Items.ContainsKey(new ItemKey(twice)), "one unnamed icon at two places records nothing");
        Assert.IsTrue(w.Ctx.JournalFake.Records.Any(j => Equals(j.Fields.GetValueOrDefault("code"), "pressedBefore")) || w.Ctx.JournalFake.Records.Any(j => Equals(j.Fields.GetValueOrDefault("code"), "conflict")));
        Assert.IsEmpty(k.IconItems);
    }

    [TestMethod]
    public void AnIconThatIsNotClearlyALearnedOne_IsAskedFor_NeverTakenForALookAlike()
    {
        // two strawberry cones (one drawing, 5 bits apart on the capture): both learned
        var a = StockWorld.KeyOf(1);
        var b = Flip(a, 5);
        var learned = new List<IconItem> { new(a, 1, "Item 1", DateTimeOffset.UnixEpoch), new(b, 2, "Item 2", DateTimeOffset.UnixEpoch) };
        Assert.AreEqual(("known", 1L), (StockIcons.Match(a, learned).Code, StockIcons.Match(a, learned).Item!.ItemId));
        Assert.AreEqual(("known", 2L), (StockIcons.Match(b, learned).Code, StockIcons.Match(b, learned).Item!.ItemId));
        Assert.AreEqual(("known", 1L), (StockIcons.Match(Flip(a, 2, from: 200), learned).Code, StockIcons.Match(Flip(a, 2, from: 200), learned).Item!.ItemId), "2 bits from item 1, 7 from item 2");
        Assert.AreEqual("ambiguous", StockIcons.Match(Flip(a, 2), learned).Code, "2 bits from item 1, 3 from item 2: too close to tell");
        Assert.AreEqual("unknown", StockIcons.Match(Flip(a, 3, from: 150), learned).Code, "3 bits from the nearest: another drawing");
        Assert.AreEqual("unknown", StockIcons.Match(StockWorld.KeyOf(3), learned).Code);
        Assert.AreEqual("unknown", StockIcons.Match("icon:xyz", learned).Code);
        Assert.AreEqual("known", StockIcons.Match(Flip(a, 1, from: 30), [learned[0], new(Flip(a, 2, from: 40), 1, "Item 1", DateTimeOffset.UnixEpoch)]).Code, "two keys of one item are no rivals");

        // in a scan: an unlearned look-alike 2 bits from item 1 first matches item 1, then collides with item 1's own tile,
        // so the game is asked about it; one 3 bits away is asked for at once
        var row = new[]
        {
            new StockWorld.Tile(1, 10, false, a), new StockWorld.Tile(2, 20, false, b),
            new StockWorld.Tile(4, 40, false, Flip(a, 2, from: 120)), new StockWorld.Tile(3, 30, false, Flip(a, 3, from: 100)),
        };
        var w = new StockWorld([row]);
        w.Ctx.KnowledgeFake!.IconItems.AddRange(learned);
        var r = w.Run();
        Assert.AreEqual(JobState.Succeeded, r.State, Data(r));
        CollectionAssert.AreEqual(new long[] { 4, 3 }, w.TipItems.ToArray(), "only the unclear icons are pressed");
        var k = w.Ctx.KnowledgeFake;
        Assert.AreEqual((10L, 20L, 30L, 40L), (k.Items[new ItemKey("item 1")].Count!.Value, k.Items[new ItemKey("item 2")].Count!.Value, k.Items[new ItemKey("item 3")].Count!.Value, k.Items[new ItemKey("item 4")].Count!.Value));
        Assert.AreEqual((2L, 2L, 1L), ((long)r.Data!["ability.known"]!, (long)r.Data["ability.named"]!, (long)r.Data["conflicts"]!));
    }

    [TestMethod]
    public void OneItemAtTwoPlaces_IsAConflict_TheGameIsAsked_AndATipBeatsAnIconMatch()
    {
        // two tiles whose icons both match learned item 1: the game is asked about the newer one; it says item 1 too, so
        // the tip's tile is kept and the icon match dropped
        var a = StockWorld.KeyOf(1);
        var row = new[] { new StockWorld.Tile(1, 10, false, a), new StockWorld.Tile(1, 11, false, Flip(a, 1, from: 60)) };
        var w = new StockWorld([row]);
        w.Ctx.KnowledgeFake!.IconItems.Add(new IconItem(a, 1, "Item 1", DateTimeOffset.UnixEpoch));
        var r = w.Run();
        Assert.AreEqual(JobState.Succeeded, r.State, Data(r));
        Assert.AreEqual((1L, 1L, 1L), ((long)r.Data!["conflicts"]!, (long)r.Data["ability.items"]!, (long)r.Data["tips"]!));
        Assert.AreEqual(11L, w.Ctx.KnowledgeFake.Items[new ItemKey("item 1")].Count);

        // the same, but the game names nothing: neither count is trusted
        var silent = new StockWorld([row]) { Tips = _ => TipLog.Nothing };
        silent.Ctx.KnowledgeFake!.IconItems.Add(new IconItem(a, 1, "Item 1", DateTimeOffset.UnixEpoch));
        var s = silent.Run();
        Assert.AreEqual((JobState.Failed, "stock.nothingRead"), Code(s), Data(s));
        Assert.AreEqual((1L, 0L), ((long)s.Data!["conflicts"]!, (long)s.Data["ability.items"]!));
        Assert.IsFalse(silent.Ctx.KnowledgeFake.Items.ContainsKey(new ItemKey("item 1")));

        // an icon matched to item 1, then the game names another tile item 1: the tip wins
        var row2 = new[] { new StockWorld.Tile(1, 10, false, a), new StockWorld.Tile(1, 12, false, StockWorld.KeyOf(9)) };
        var w2 = new StockWorld([row2]);
        w2.Ctx.KnowledgeFake!.IconItems.Add(new IconItem(a, 1, "Item 1", DateTimeOffset.UnixEpoch));
        var r2 = w2.Run();
        Assert.AreEqual(JobState.Succeeded, r2.State, Data(r2));
        Assert.AreEqual(12L, w2.Ctx.KnowledgeFake.Items[new ItemKey("item 1")].Count);
        Assert.AreEqual(1L, (long)r2.Data!["conflicts"]!);
    }

    [TestMethod]
    public void ATipRunThatDoesNotSucceed_EndsTheScan_KeepingWhatWasReadOfThatSubTab()
    {
        var w = new StockWorld(StockWorld.Grid(1, first: 1)) { EndTipWith = (4, ProcedureStatus.Halted) };
        var r = w.Run();
        Assert.AreEqual((JobState.Halted, "ForbiddenUi"), Code(r), Data(r));
        Assert.HasCount(3, w.Ctx.KnowledgeFake!.Items, "tiles 1-3 were read before");
        Assert.AreEqual("stopped", r.Data!["end"]);
    }

    [TestMethod]
    public void JournalAndResult_CarryCodesIdsAndCountsOnly()
    {
        var w = new StockWorld(StockWorld.Grid(1, first: 1));
        var r = w.Run();
        Assert.AreEqual(JobState.Succeeded, r.State);
        Assert.IsTrue(w.Ctx.JournalFake.Records.All(j => j.Fields.Values.All(v => v is not string s || Codes.IsCode(s))), "codes only");
        Assert.IsFalse(w.Ctx.JournalFake.Records.Any(j => j.Fields.Values.Any(v => v is string s && s.Contains("Item", StringComparison.Ordinal))), "no item name in the journal");
        Assert.IsTrue(r.Data!.Values.All(v => v is null or long or bool or string), "scalars");
        Assert.IsTrue(r.Data.Values.OfType<string>().All(Codes.IsCode));
        var tiles = w.Ctx.JournalFake.Records.Where(j => Equals(j.Fields.GetValueOrDefault("event"), "stock.tile")).ToList();
        Assert.HasCount(6, tiles);
        CollectionAssert.AreEqual(Enumerable.Range(1, 6).Select(i => (object)(long)i).ToArray(), tiles.Select(j => j.Fields["itemId"]!).ToArray());
        CollectionAssert.AreEqual(Enumerable.Range(1, 6).Select(i => (object)(long)i).ToArray(), tiles.Select(j => j.Fields["column"]!).ToArray());
        Assert.IsTrue(w.Ctx.JournalFake.Records.Any(j => Equals(j.Fields.GetValueOrDefault("event"), "stock.end") && Equals(j.Fields["code"], "stock.scanned")));
    }

    [TestMethod]
    public void RefusesWithZeroRuns_WithoutKnowledgeOrSlideClicks_OrDuringACraft_ButRunsOtherwise()
    {
        var unknown = new StockWorld(StockWorld.Grid(1));
        unknown.Ctx.KnowledgeFake = null;
        Assert.AreEqual((JobState.Refused, "knowledge.unavailable"), Code(unknown.Run()));
        Assert.HasCount(0, unknown.Ctx.Runner.Runs);

        var noSlides = new StockWorld(StockWorld.Grid(1));
        noSlides.Ctx.SwitchesFake.SlideClickEnabled = false;
        Assert.AreEqual((JobState.Refused, "slideClick.off"), Code(noSlides.Run()));
        Assert.HasCount(0, noSlides.Ctx.Runner.Runs);

        var crafting = new StockWorld(StockWorld.Grid(1));
        crafting.Ctx.Ui = T.Snapshot(100, new CraftSessionInfo(true, "UI_LIFE_ALCHEMIST_MAKE_POT", DateTime.Now, T.Pos(50), 3, null, null, false));
        Assert.AreEqual((JobState.Refused, "craft.inProgress"), Code(crafting.Run()));
        Assert.HasCount(0, crafting.Ctx.Runner.Runs);

        // a read-only scan: no switch (and since 4.33 no per-character permission) is asked
        var w = new StockWorld(StockWorld.Grid(1));
        w.Ctx.SwitchesFake.Crafting = false;
        Assert.AreEqual(JobState.Succeeded, w.Run().State);
    }

    private static (JobState, string) Code(JobResult r) => (r.State, r.Code);

    /// <summary>A per-sub-tab field summed over the sub-tabs ("items" sums "ability.items", "homestead.items", ...).</summary>
    private static long Sum(JobResult r, string field) =>
        r.Data!.Where(kv => kv.Key.EndsWith("." + field, StringComparison.Ordinal)).Sum(kv => (long)kv.Value!);

    private static string Data(JobResult r) => r.Data is null ? string.Empty : string.Join(", ", r.Data.Select(kv => kv.Key + "=" + kv.Value));

    /// <summary>The key with <paramref name="bits"/> bits flipped, starting at bit <paramref name="from"/>.</summary>
    private static string Flip(string key, int bits, int from = 0)
    {
        var bytes = Convert.FromHexString(key[IconKeys.Prefix.Length..]);
        for (var i = from; i < from + bits; i++)
        {
            bytes[i / 8] ^= (byte)(0x80 >> (i % 8));
        }

        return IconKeys.Prefix + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>What the game logs when a tile is pressed.</summary>
    internal enum TipLog
    {
        /// <summary>The tile's item tip (its name and id), after the click.</summary>
        Logged,

        /// <summary>Nothing.</summary>
        Nothing,
    }

    /// <summary>
    /// A simulated Stock tab behind the job's contracts. Each sub-tab is a grid of tile rows (6 columns at the real x,
    /// rows 169 px apart); a drag run scrolls the selected sub-tab by 280 px (a 240 px drag overshoots, as live), clamped
    /// at both ends; a tip run "logs" the item of the tile under its press point. Reads are built from what is visible
    /// (whole tiles only), with the screen identity of the reader asked for.
    /// </summary>
    internal sealed class StockWorld : IScreenService
    {
        internal const int Moved = 280;
        private const int RowPitch = 169;
        private const int TopPad = 30;
        private const int TileW = 115;
        private const int TileH = 120;
        private static readonly int[] Lefts = [356, 568, 779, 989, 1200, 1411];
        private readonly FeatureHost _host;
        private readonly Dictionary<string, List<Tile?[]>> _grids = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _scroll = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _drags = new(StringComparer.Ordinal);
        private long _offset = 1000;
        private bool _open;
        private string _selected = "ability";
        private int _tipRuns;
        private bool _started;

        public StockWorld(Tile?[][] ability, Tile?[][]? homestead = null, Tile?[][]? tailor = null, Tile?[][]? hyperweather = null)
        {
            _grids["ability"] = [.. ability];
            _grids["homestead"] = [.. homestead ?? []];
            _grids["tailor"] = [.. tailor ?? []];
            _grids["hyperweather"] = [.. hyperweather ?? []];
            var (host, env, _) = T.LoadReal();
            _host = host;
            env.Procedures.Put(new ProcedureDefinition("OpenAbility", "core", [new StepDefinition("open", StatePredicate.Any, new ClickAction("menu.ability"), Expectation.Nothing)], ProcedureLimits.Default));
            Catalog = env.Procedures;
            Ctx = new FakeJobContext(env.Procedures) { Ui = T.Snapshot(_offset), ScreenOverride = this };
            Ctx.Runner.Script = Engine;
        }

        public FakeJobContext Ctx { get; }

        public FakeProcedureBuilder Catalog { get; }

        /// <summary>The Stock tab is already open when the job starts.</summary>
        public bool StartOnStock { get; init; }

        /// <summary>The Ability sub-tab's scroll when the job starts.</summary>
        public int StartScroll { get; init; }

        /// <summary>Sub-tabs whose press does nothing.</summary>
        public HashSet<string> BrokenSubTabs { get; init; } = [];

        /// <summary>The Stock tab press does nothing.</summary>
        public bool StockTabBroken { get; init; }

        /// <summary>A tile drawn at a fixed client rect of the Ability sub-tab (whatever the scroll).</summary>
        public (Tile Tile, RectI Box)? ExtraTile { get; init; }

        /// <summary>What the game logs when a tile is pressed.</summary>
        public Func<Tile, TipLog> Tips { get; init; } = _ => TipLog.Logged;

        /// <summary>Scripted per tip run (1-based): end that run with this status.</summary>
        public (int Run, ProcedureStatus Status)? EndTipWith { get; init; }

        /// <summary>Item ids whose tip was pressed, in order.</summary>
        public List<long> TipItems { get; } = [];

        /// <summary>Client points of every tip press.</summary>
        public List<PointI> ClickPoints { get; } = [];

        /// <summary>Sub-tabs pressed, in order.</summary>
        public List<string> SubTabPresses { get; } = [];

        /// <summary>Presses, reads and drags in order ("press:ability", "read:inventory.stock.ability", "drag:scroll").</summary>
        public List<string> Events { get; } = [];

        public int StockPresses { get; private set; }

        public int BackPresses { get; private set; }

        public int Drags => _drags.Values.Sum();

        public IEnumerable<Tile> AllTiles => _grids.Values.SelectMany(g => g).SelectMany(r => r).OfType<Tile>();

        public static string NameOf(long id) => "Item " + id;

        /// <summary>A distinct 256-bit icon key per item (random keys lie about 128 bits apart).</summary>
        public static string KeyOf(long id)
        {
            var bytes = new byte[32];
            new Random((int)id).NextBytes(bytes);
            return IconKeys.Prefix + Convert.ToHexString(bytes).ToLowerInvariant();
        }

        /// <summary><paramref name="rows"/> full rows of items numbered from <paramref name="first"/>, count = id.</summary>
        public static Tile?[][] Grid(int rows, int first = 1, int perRow = 6) =>
            [.. Enumerable.Range(0, rows).Select(r => Enumerable.Range(0, perRow).Select(c => (Tile?)new Tile(first + (r * perRow) + c, first + (r * perRow) + c, false)).ToArray())];

        public int ScrollOf(string sub) => _scroll.GetValueOrDefault(sub);

        public int DragsOf(string sub) => _drags.GetValueOrDefault(sub);

        public int MaxScroll(string sub) => Math.Max(0, TopPad + (_grids[sub].Count * RowPitch) + 20 - StockGrid.Viewport.Height);

        public void Recount(Func<long, long> count)
        {
            foreach (var g in _grids.Values)
            {
                for (var r = 0; r < g.Count; r++)
                {
                    g[r] = [.. g[r].Select(t => t is null || t.Count is null ? t : t with { Count = count(t.ItemId) })];
                }
            }
        }

        public void ResetScroll()
        {
            _scroll.Clear();
            _open = false;
            _started = false;
        }

        public JobResult Run(CancellationToken ct = default)
        {
            if (!_started)
            {
                _open = StartOnStock;
                _scroll["ability"] = StartScroll;
                _started = true;
            }

            return T.Handler(_host, "ScanStock").Execute(Ctx, JobParameters.Empty, ct);
        }

        // ---- IScreenService

        public CaptureResult Capture(CancellationToken ct) => new(CaptureProblem.None, Draw(), TimeSpan.FromMilliseconds(30));

        public HudClassification ClassifyHudStable(CancellationToken ct) => new(HudState.Covered, 0, 0, false, false, "stock");

        public ScreenReadResult Read(string readerId, CancellationToken ct)
        {
            Events.Add("read:" + readerId);
            _offset += 5;
            var sub = StockGrid.SubTabs.FirstOrDefault(s => s.ReaderId == readerId);
            var matches = _open && (readerId == StockGrid.ProbeReader || sub?.Name == _selected);
            if (!matches)
            {
                return new ScreenReadResult(readerId, ReadStatus.ScreenMismatch, [new ReadValue("grid", ReadStatus.ScreenMismatch, null, null, null, null, 0, "anchor-not-found")], T.Pos(_offset), Ctx.ClockFake.UtcNow);
            }

            var values = new List<ReadValue>();
            var tiles = Visible(_selected);
            var code = (tiles.Count > 0 ? "tiles" : "no-tiles") + (EndVisible(_selected) ? "-end" : string.Empty);
            values.Add(new ReadValue("grid", ReadStatus.Ok, tiles.Count, null, null, null, 0.9, code, Rect: StockGrid.Viewport));
            for (var k = 1; k <= tiles.Count; k++)
            {
                var (tile, box) = tiles[k - 1];
                values.Add(new ReadValue("icon." + k, ReadStatus.Ok, null, null, tile.Key ?? KeyOf(tile.ItemId), null, 0.9, tile.Faded ? "icon-faded" : "icon", Rect: box));
                values.Add((tile.Count, tile.Faded) switch
                {
                    (null, true) => new ReadValue("count." + k, ReadStatus.Ok, 0, null, null, null, 0.8, "faded", Rect: box),
                    (null, false) => new ReadValue("count." + k, ReadStatus.Unknown, null, null, null, null, 0, "count-absent", Rect: box),
                    ({ } n, _) => new ReadValue("count." + k, ReadStatus.Ok, n, null, null, null, 0.95, "int", Rect: box),
                });
            }

            var status = values.Any(v => v.Status != ReadStatus.Ok) ? ReadStatus.Unknown : ReadStatus.Ok;
            return new ScreenReadResult(readerId, status, values, T.Pos(_offset), Ctx.ClockFake.UtcNow, new Dictionary<string, PointI> { [StockGrid.AnchorTemplate] = StockGrid.AnchorAt });
        }

        /// <summary>The list's last row shows with at least 150 plain rows under it (the reader's "-end").</summary>
        private bool EndVisible(string sub)
        {
            var vp = StockGrid.Viewport;
            var rows = _grids[sub].Count;
            var lastBottom = rows == 0 ? vp.Top : vp.Top + TopPad + ((rows - 1) * RowPitch) + TileH - ScrollOf(sub);
            return lastBottom + 10 + 150 <= vp.Bottom - 2 && (sub != "ability" || ExtraTile is null);
        }

        /// <summary>Whole tiles of a sub-tab at its scroll, row by row (client boxes).</summary>
        private List<(Tile Tile, RectI Box)> Visible(string sub)
        {
            var list = new List<(Tile, RectI)>();
            var vp = StockGrid.Viewport;
            var grid = _grids[sub];
            for (var r = 0; r < grid.Count; r++)
            {
                var top = vp.Top + TopPad + (r * RowPitch) - ScrollOf(sub);
                if (top < vp.Top + 2 || top + TileH > vp.Bottom - 2)
                {
                    continue;
                }

                for (var c = 0; c < grid[r].Length; c++)
                {
                    if (grid[r][c] is { } t)
                    {
                        list.Add((t, new RectI(Lefts[c], top, Lefts[c] + TileW, top + TileH)));
                    }
                }
            }

            if (sub == "ability" && ExtraTile is { } extra)
            {
                list.Add(extra);
            }

            return list;
        }

        // ---- the engine

        private ProcedureResult Engine(ProcedureDefinition p)
        {
            if (p.Id == "inventory.stockTip")
            {
                return TipRun(p);
            }

            Assert.AreEqual("inventory.scanStock", p.Id);
            var ids = p.Steps.Select(s => s.Id).ToArray();
            var attempts = new List<StepAttemptRecord>
            {
                Attempt("open", ids[0] == "open" && !p.Steps[0].Flags.HasFlag(StepFlags.SkipIfSatisfied) ? "call.done" : "skip.satisfied"),
            };
            if (ids.Contains("stock.open"))
            {
                Events.Add("press:stock");
                StockPresses++;
                if (!StockTabBroken)
                {
                    _open = true;
                }

                return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 1, attempts: attempts);
            }

            foreach (var sub in StockGrid.SubTabs.Where(s => ids.Contains(s.StepId)))
            {
                Events.Add("press:" + sub.Name);
                SubTabPresses.Add(sub.Name);
                if (!BrokenSubTabs.Contains(sub.Name))
                {
                    _selected = sub.Name;
                }

                return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 1, attempts: attempts);
            }

            if (ids.Contains("top") || ids.Contains("scroll"))
            {
                var step = ids.Contains("top") ? "top" : "scroll";
                Events.Add("drag:" + step);
                _drags[_selected] = DragsOf(_selected) + 1;
                _scroll[_selected] = Math.Clamp(ScrollOf(_selected) + (step == "top" ? -Moved : Moved), 0, MaxScroll(_selected));
                attempts.Add(Attempt(step, "drag.done"));
                return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 1, attempts: attempts);
            }

            if (ids.Contains("back"))
            {
                Events.Add("press:back");
                BackPresses++;
                _open = false;
            }

            return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 1, attempts: attempts);
        }

        private static StepAttemptRecord Attempt(string step, string detail) =>
            new(step, 1, StepOutcome.Ok, OutcomeClass.Ok, RecoveryAction.Proceed, null, false, null, detail, DateTimeOffset.UnixEpoch);

        private ProcedureResult TipRun(ProcedureDefinition p)
        {
            _tipRuns++;
            var click = (ClickAction)p.Steps.Single(s => s.Id == "tip.open").Action;
            Assert.AreEqual(StockGrid.AnchorTemplate, click.TemplateId);
            var point = StockGrid.AnchorAt.Offset(click.Offset!.Value.X, click.Offset.Value.Y); // the anchor is found where expected
            ClickPoints.Add(point);
            Events.Add("tip");
            if (EndTipWith is { } end && end.Run == _tipRuns)
            {
                var halt = end.Status == ProcedureStatus.Halted ? new HaltReport(HaltCode.ForbiddenUi, "UI_RECHARGE", "j", p.Id, "tip.open", DateTimeOffset.UnixEpoch, null) : null;
                return T.Result(p.Id, end.Status, halt: halt, inputs: 1);
            }

            var hit = Visible(_selected).FirstOrDefault(v => v.Box.Contains(point));
            Assert.IsNotNull(hit.Tile, $"the press at {point} hits a tile");
            TipItems.Add(hit.Tile.ItemId);
            if (Tips(hit.Tile) == TipLog.Logged)
            {
                _offset += 10;
                Ctx.Ui = Ctx.Ui with { AsOf = T.Pos(_offset), LastItemTip = new ItemTipInfo(NameOf(hit.Tile.ItemId), hit.Tile.ItemId, new DateTime(2026, 10, 15, 2, 0, 0), T.Pos(_offset)) };
            }

            return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 2);
        }

        /// <summary>The selected sub-tab's grid as the real list looks to the shift measurement (unique rows).</summary>
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

            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    Put(x, y, 128);
                }
            }

            var vp = StockGrid.Viewport;
            var grid = _open ? _grids[_selected] : [];
            var scroll = ScrollOf(_selected);
            var height = TopPad + (grid.Count * RowPitch) + 20;
            for (var y = vp.Top; y < vp.Bottom; y++)
            {
                var listY = y - vp.Top + scroll;
                for (var x = vp.Left; x < vp.Right; x++)
                {
                    Put(x, y, listY < height ? 228 : 200);
                }

                for (var r = 0; r < grid.Count; r++)
                {
                    var t = listY - (TopPad + (r * RowPitch));
                    if (t is >= -25 and < -12)
                    {
                        // a section-header-like bar, different per row, between the rows
                        for (var x = 640; x < 640 + 40 + ((r * 37) % 500); x++)
                        {
                            Put(x, y, 150 + ((r * 23) % 60));
                        }
                    }

                    if (t < 0 || t >= TileH)
                    {
                        continue;
                    }

                    for (var c = 0; c < grid[r].Length; c++)
                    {
                        if (grid[r][c] is not { } tile)
                        {
                            continue;
                        }

                        for (var x = Lefts[c]; x < Lefts[c] + TileW; x++)
                        {
                            var edge = x == Lefts[c] || x == Lefts[c] + TileW - 1 || t == 0 || t == TileH - 1;
                            Put(x, y, edge ? 120 : 40 + (int)((tile.ItemId * 37) % 120) + (t * 40 / TileH));
                        }
                    }
                }
            }

            return new Frame(w, h, w * 4, px, TimeSpan.Zero, DateTimeOffset.UnixEpoch, T.Pos(_offset), default, false);
        }

        /// <summary>A tile: its item, its count (null = none shown), faded, and its icon key (default: the item's own).</summary>
        public sealed record Tile(long ItemId, long? Count, bool Faded, string? Key = null);
    }
}
