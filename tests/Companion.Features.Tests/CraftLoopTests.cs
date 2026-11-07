using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;

namespace Companion.Features.Tests;

/// <summary>
/// CraftLoop against fakes: the real crafting.loop data procedure, a fake engine that simulates one craft session per
/// round in the UI model, a fake clock. No window, no game, no log.
/// </summary>
[TestClass]
public sealed class CraftLoopTests
{
    private static JobParameters P(long? slot = null, long? count = null, string? batch = null)
    {
        var d = new Dictionary<string, object>();
        if (slot is { } s)
        {
            d["slot"] = s;
        }

        if (count is { } c)
        {
            d["count"] = c;
        }

        if (batch is not null)
        {
            d["batch"] = batch;
        }

        return new JobParameters(d);
    }

    // ------------------------------------------------------------------ params

    [TestMethod]
    public void ParamsAreTypedAndBounded()
    {
        var (host, _, _) = T.LoadReal();
        var def = T.Definition(host, "CraftLoop");
        Assert.AreEqual((JobKind.Commit, AutomationSwitch.Crafting, "crafting"), (def.Kind, def.Switch, def.FeatureId));

        string? reason;
        var ok = host.Jobs.Validate(def, new Dictionary<string, string> { ["slot"] = "10", ["count"] = "100000", ["batch"] = "max" }, null, out reason);
        Assert.IsNull(reason);
        Assert.AreEqual((10L, 100000L, "max"), (ok!.GetInt("slot"), ok.GetInt("count"), ok.GetEnum("batch")));

        var minimal = host.Jobs.Validate(def, new Dictionary<string, string> { ["count"] = "0" }, null, out reason);
        Assert.IsNull(reason);
        Assert.AreEqual(0L, minimal!.GetInt("count"));
        Assert.IsFalse(minimal.Values.ContainsKey("slot"), "slot defaults to 0 (keep the selected recipe)");
        Assert.IsFalse(minimal.Values.ContainsKey("batch"), "batch defaults to 1");

        (Dictionary<string, string> Raw, string Reason)[] bad =
        [
            (new() { ["slot"] = "1", ["batch"] = "1" }, "param.missing:count"),
            (new() { ["count"] = "-1" }, "param.range:count"),
            (new() { ["count"] = "100001" }, "param.range:count"),
            (new() { ["count"] = "1000000" }, "param.length:count"),
            (new() { ["count"] = "5x" }, "param.type:count"),
            (new() { ["count"] = "5", ["slot"] = "11" }, "param.range:slot"),
            (new() { ["count"] = "5", ["slot"] = "-1" }, "param.range:slot"),
            (new() { ["count"] = "5", ["slot"] = "100" }, "param.length:slot"),
            (new() { ["count"] = "5", ["batch"] = "2" }, "param.enum:batch"),
            (new() { ["count"] = "5", ["batch"] = "MAX" }, "param.enum:batch"),
            (new() { ["count"] = "5", ["batch"] = "all" }, "param.enum:batch"),
            (new() { ["count"] = "5", ["qty"] = "5" }, "param.unknown"),
        ];
        foreach (var (raw, expected) in bad)
        {
            Assert.IsNull(host.Jobs.Validate(def, raw, null, out reason), expected);
            Assert.AreEqual(expected, reason);
        }
    }

    [TestMethod]
    public void TheExpectedProductItemId_IsCheckedAgainstTheSlotsLatestScan_BeforeAnyInput()
    {
        // the tray sends the product item id of the recipe it shows for the slot (2026-10-15)
        var (host, _, _) = T.LoadReal();
        var def = T.Definition(host, "CraftLoop");
        var ok = host.Jobs.Validate(def, new Dictionary<string, string> { ["slot"] = "3", ["count"] = "1", ["item"] = "1051000" }, null, out var reason);
        Assert.AreEqual((null, 1051000L), (reason, ok!.GetInt("item")));
        Assert.IsNull(host.Jobs.Validate(def, new Dictionary<string, string> { ["count"] = "1", ["item"] = "0" }, null, out reason));
        Assert.AreEqual("param.range:item", reason);

        static Recipe Slot(int slot, long? id, int minutes) => new(
            "dish " + minutes + "|lifeskill|0", new ItemKey("dish " + minutes), 1, "lifeskill", [new MaterialRequirement(new ItemKey("wheat"), 1)], [], null,
            DateTimeOffset.UnixEpoch.AddMinutes(minutes), "screen:craft.fav/" + slot, false, RecipeConfidence.Complete, null, null, ProductItemId: id);
        JobParameters WithItem(long item) => new(new Dictionary<string, object> { ["slot"] = 3L, ["count"] = 1L, ["item"] = item });

        // the latest scan saw another product in slot 3: refused, nothing run
        var changed = new LoopWorld();
        changed.Ctx.KnowledgeFake!.Recipes.AddRange([Slot(3, 1051000, 1), Slot(3, 1051009, 2), Slot(4, 1051000, 3)]);
        Assert.AreEqual((JobState.Refused, "slotItem.changed"), Code(changed.Run(WithItem(1051000))));
        Assert.HasCount(0, changed.Ctx.Runner.Runs);

        // the same product: it runs (its tip is read before the Make, 2026-10-16), and the id is journaled with the rounds
        var same = new LoopWorld { SelectedItemId = 1051000 };
        same.Ctx.KnowledgeFake!.Recipes.AddRange([Slot(3, 1051009, 1), Slot(3, 1051000, 2)]);
        var r = same.Run(WithItem(1051000));
        Assert.AreEqual(JobState.Succeeded, r.State);
        Assert.AreEqual(1051000L, r.Data!["itemId"]);
        Assert.IsTrue(same.Ctx.JournalFake.Records.Where(j => Equals(j.Fields.GetValueOrDefault("event"), "craftLoop.round")).All(j => Equals(j.Fields["itemId"], 1051000L)));

        // no id stored for the slot: the caller's item is still verified by its tip before the Make (2026-10-16), and it runs
        var unknown = new LoopWorld { SelectedItemId = 1051000 };
        unknown.Ctx.KnowledgeFake!.Recipes.Add(Slot(3, null, 1));
        Assert.AreEqual(JobState.Succeeded, unknown.Run(WithItem(1051000)).State);
        Assert.AreEqual(JobState.Succeeded, new LoopWorld { SelectedItemId = 7 }.Run(new JobParameters(new Dictionary<string, object> { ["count"] = 1L, ["item"] = 7L })).State);
    }

    [TestMethod]
    public void ATypedBatch_TypesTheCountLeft_AndMakesItWithOnePress()
    {
        // 2026-10-15: the Make view's quantity opens the game's keypad; a typed count is clamped by the game to the most
        // it can make, so a round makes up to the whole count with one Make press
        var w = new LoopWorld { ItemsPerRound = [3, 2] };
        var r = w.Run(new JobParameters(new Dictionary<string, object> { ["slot"] = 2L, ["count"] = 5L, ["batch"] = "typed" }));

        Assert.AreEqual((JobState.Succeeded, "loop.countReached"), Code(r));
        Assert.AreEqual(5L, r.Data!["total"]);
        var runs = w.Ctx.Runner.Runs;
        // per round: select (the slot click resets the game's quantity), type the count (outside the commit), then Make
        CollectionAssert.AreEqual(new[] { "crafting.loop", "crafting.qty", "crafting.loop", "crafting.loop", "crafting.qty", "crafting.loop" }, runs.Select(r => r.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "open", "announce.close", "slot.02" }, runs[0].Steps.Select(s => s.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "qty", "qty.settle", "key.clear", "key.5", "key.ok", "qty.done" }, runs[1].Steps.Select(s => s.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "make", "await" }, runs[2].Steps.Select(s => s.Id).ToArray());
        Assert.AreEqual("key.2", runs[4].Steps.Single(s => s.Id.StartsWith("key.", StringComparison.Ordinal) && s.Id is not ("key.clear" or "key.ok")).Id, "round 2 types what is left");
        CollectionAssert.AreEqual(new[] { 5, 2 }, runs.Where(r => r.Steps.Any(s => s.Id == "await")).Select(p => ((AwaitCraftAction)p.Steps[^1].Action).RequestedTicks).ToArray());
    }

    [TestMethod]
    public void WhenTheCountIsNotTyped_MakeIsNeverPressed()
    {
        var w = new LoopWorld { QtyStatus = ProcedureStatus.Parked };
        var r = w.Run(new JobParameters(new Dictionary<string, object> { ["slot"] = 2L, ["count"] = 5L, ["batch"] = "typed" }));

        Assert.AreEqual((JobState.Failed, "qty.notTyped"), Code(r));
        Assert.IsFalse(w.Ctx.Runner.Runs.Any(p => p.Steps.Any(s => s.Id == "make")), "no Make: the field may hold any count");
    }

    [TestMethod]
    public void ARepeatedDigit_GetsItsOwnStep_AndOtherBatchesTypeNothing()
    {
        var repeated = new LoopWorld { ItemsPerRound = [44] };
        Assert.AreEqual(JobState.Succeeded, repeated.Run(new JobParameters(new Dictionary<string, object> { ["slot"] = 1L, ["count"] = 44L, ["batch"] = "typed" })).State);
        var typing = repeated.Ctx.Runner.Runs.Single(r => r.Id == "crafting.qty");
        CollectionAssert.AreEqual(new[] { "key.clear", "key.4", "key.4-2", "key.ok" }, typing.Steps.Select(s => s.Id).Where(id => id.StartsWith("key.", StringComparison.Ordinal)).ToArray());
        Assert.AreEqual(44, ((AwaitCraftAction)repeated.Ctx.Runner.Runs.Single(r => r.Steps.Any(s => s.Id == "await")).Steps[^1].Action).RequestedTicks);

        // a count left above the game's 50 per craft session is typed as 50 (2026-10-31 sweep); the craft wait keeps the count left
        var hundred = new LoopWorld { ItemsPerRound = [100] };
        Assert.AreEqual(JobState.Succeeded, hundred.Run(new JobParameters(new Dictionary<string, object> { ["slot"] = 1L, ["count"] = 100L, ["batch"] = "typed" })).State);
        CollectionAssert.AreEqual(new[] { "key.clear", "key.5", "key.0", "key.ok" }, hundred.Ctx.Runner.Runs.Single(r => r.Id == "crafting.qty").Steps.Select(s => s.Id).Where(id => id.StartsWith("key.", StringComparison.Ordinal)).ToArray());
        Assert.AreEqual(100, ((AwaitCraftAction)hundred.Ctx.Runner.Runs.Single(r => r.Steps.Any(s => s.Id == "await")).Steps[^1].Action).RequestedTicks, "the craft wait keeps the count left");

        // every typed step id fits the engine's step id rule (live 2026-10-16: "key.9#2" for 99 was refused as procedure.invalid)
        foreach (var n in new long[] { 99, 100, 111, 999, 7 })
        {
            var w = new LoopWorld { ItemsPerRound = [(int)n] };
            w.Run(new JobParameters(new Dictionary<string, object> { ["slot"] = 1L, ["count"] = n, ["batch"] = "typed" }));
            var ids = w.Ctx.Runner.Runs.Single(r => r.Id == "crafting.qty").Steps.Select(s => s.Id).ToList();
            Assert.IsTrue(ids.All(id => System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z][A-Za-z0-9._-]{0,63}$")), string.Join(",", ids));
            Assert.AreEqual(ids.Count, ids.Distinct().Count(), "unique ids");
        }

        var one = new LoopWorld();
        Assert.AreEqual(JobState.Succeeded, one.Run(P(slot: 1, count: 1, batch: "1")).State);
        Assert.AreEqual("crafting.loop", one.Ctx.Runner.Runs.Single().Id, "batch 1 types nothing");
    }

    [TestMethod]
    public void Slot0WithAnItem_ReadsTheSelectedProductBeforeEveryMake_AndNeverReopens()
    {
        // live 2026-10-15: the Craft plan's list walk selected Roast Chicken, CraftLoop's reopen selected a water recipe
        static JobParameters Chicken(long count) => new(new Dictionary<string, object> { ["count"] = count, ["item"] = 1044002L });

        var right = new LoopWorld { SelectedItemId = 1044002 };
        Assert.AreEqual(JobState.Succeeded, right.Run(Chicken(2)).State);
        // 2026-10-16 safety review: every Make's selection is verified (the commit window starts at the latest verification)
        CollectionAssert.AreEqual(
            new[] { "crafting.productTip", "crafting.loop:announce.close", "crafting.loop:make", "crafting.loop:announce.close", "crafting.productTip", "crafting.loop:make" },
            right.Ctx.Runner.Runs.Select(r => r.Id == "crafting.loop" ? r.Id + ":" + r.Steps[0].Id : r.Id).ToArray());
        Assert.IsTrue(right.Ctx.Runner.Runs.Skip(1).All(r => r.Steps.All(s => s.Id != "open")));

        var wrong = new LoopWorld { SelectedItemId = 20330037 };
        Assert.AreEqual((JobState.Refused, "selectedItem.changed"), Code(wrong.Run(Chicken(1))));
        Assert.IsTrue(wrong.Ctx.Runner.Runs.All(r => r.Id == "crafting.productTip"), "nothing crafted");

        var noTip = new LoopWorld();
        Assert.AreEqual((JobState.Refused, "selectedItem.changed"), Code(noTip.Run(Chicken(1))), "no logged tip: never a guess");
    }

    [TestMethod]
    public void TheHandlerRefusesParamsOutsideItsRangeEvenIfTheCatalogWasBypassed()
    {
        var w = new LoopWorld();
        Assert.AreEqual((JobState.Refused, "param.missing:count"), Code(w.Run(P(slot: 1))));
        Assert.AreEqual((JobState.Refused, "param.range:slot"), Code(w.Run(P(slot: 11, count: 1))));
        Assert.AreEqual((JobState.Refused, "param.range:count"), Code(w.Run(P(count: 100_001))));
        Assert.AreEqual((JobState.Refused, "param.enum:batch"), Code(w.Run(P(count: 1, batch: "2"))));
        Assert.HasCount(0, w.Ctx.Runner.Runs);
    }

    // ------------------------------------------------------------------ the round procedure

    [TestMethod]
    public void TheRoundKeepsOnlyTheChosenSlotAndTheMaxStepForAMaxBatch()
    {
        var plainWorld = new LoopWorld();
        Assert.AreEqual(JobState.Succeeded, plainWorld.Run(P(count: 1)).State);
        var plain = plainWorld.Ctx.Runner.Runs.Single();
        CollectionAssert.AreEqual(new[] { "announce.close", "make", "await" }, plain.Steps.Select(s => s.Id).ToArray(), "slot 0: no reopen (it would select another recipe)");
        Assert.AreEqual(1, ((AwaitCraftAction)plain.Steps[^1].Action).RequestedTicks);

        var slotWorld = new LoopWorld();
        Assert.AreEqual(JobState.Succeeded, slotWorld.Run(P(slot: 7, count: 1, batch: "max")).State);
        var slotMax = slotWorld.Ctx.Runner.Runs.Single();
        CollectionAssert.AreEqual(new[] { "open", "announce.close", "slot.07", "max", "make", "await" }, slotMax.Steps.Select(s => s.Id).ToArray());
        Assert.AreEqual(new ClickAction("craft.fav.anchor", new PointI(-114, -118)), slotMax.Steps[2].Action);
        Assert.AreEqual(new ClickAction("craft.qty.max"), slotMax.Steps[3].Action);
        Assert.AreEqual(999, ((AwaitCraftAction)slotMax.Steps[^1].Action).RequestedTicks, "a max batch is not read back: largest await bound");
        Assert.AreEqual((ProcedureKind.Commit, AutomationSwitch.Crafting), (slotMax.Kind, slotMax.Gate));

        // A data procedure of the wrong shape is never run.
        Func<ProcedureDefinition, ProcedureDefinition>[] broken =
        [
            d => d with { Kind = ProcedureKind.Navigation },
            d => d with { Steps = d.Steps.Where(s => s.Id != "slot.03").ToArray() },
            d => d with { Steps = d.Steps.Where(s => s.Id != "make").ToArray() },
            d => d with { Steps = d.Steps.Where(s => s.Action is not AwaitCraftAction).ToArray() },
        ];
        foreach (var breakIt in broken)
        {
            var w = new LoopWorld();
            w.Catalog.Put(breakIt(w.Catalog.Get("crafting.loop")!));
            Assert.AreEqual((JobState.Failed, "procedure.invalid:crafting.loop"), Code(w.Run(P(slot: 3, count: 1))));
            Assert.HasCount(0, w.Ctx.Runner.Runs);
        }
    }

    [TestMethod]
    public void TheDataProcedureIsTheSurveyedMakeRound()
    {
        var w = new LoopWorld();
        var p = w.Ctx.ProcedureCatalog.Get("crafting.loop")!;
        Assert.AreEqual(new CallProcedureAction("OpenAbility"), p.Steps[0].Action);
        var announce = p.Steps.Single(s => s.Id == "announce.close");
        Assert.AreEqual(new KeyAction(GameKey.Escape), announce.Action);
        Assert.AreEqual(StepFlags.SkipIfSatisfied, announce.Flags, "Esc only while the announcement is open");
        Assert.AreEqual(UiExpectation.HiddenTop("UI_LIFE_STAGE_PREVIEW"), announce.Expect.UiEvents!.Single());

        var make = p.Steps.Single(s => s.Id == "make");
        Assert.AreEqual(new ClickAction("craft.make"), make.Action);
        Assert.AreEqual(StepFlags.Commit, make.Flags);
        Assert.AreEqual("UI_LIFESKILL_MAKE_VIEW", make.Pre.TopUi);
        CollectionAssert.AreEquivalent(
            // the item tip is the game's "a material is missing" answer to Make (live 2026-10-15: Water 1/3 for White Toast)
            new[] { "UI_LIFE_ALCHEMIST_MAKE_POT", "UI_LIFE_ENCHANT_TABLE", "UI_ITEM_TIPS_PASS_TOUCH" },
            make.Expect.UiEvents!.Select(u => u.UiName).ToArray());
        Assert.IsTrue(p.Steps.SkipWhile(s => s.Id != "make").Skip(1).Single().Action is AwaitCraftAction, "await right after Make");

        // Ten slots, row-major, 5 per row: one anchor, offsets ~113 px apart and two rows 140 px apart.
        var slots = Enumerable.Range(1, 10).Select(i => (ClickAction)p.Steps.Single(s => s.Id == $"slot.{i:00}").Action).ToList();
        Assert.IsTrue(slots.All(c => c.TemplateId == "craft.fav.anchor" && c.Offset is not null));
        for (var i = 1; i < 10; i++)
        {
            var (a, b) = (slots[i - 1].Offset!.Value, slots[i].Offset!.Value);
            if (i % 5 == 0)
            {
                Assert.AreEqual((0, 140), (b.X - slots[i - 5].Offset!.Value.X, b.Y - a.Y), "row 2 below row 1");
            }
            else
            {
                Assert.IsTrue(b.X - a.X is >= 112 and <= 114 && b.Y == a.Y, $"slot {i + 1}");
            }
        }
    }

    // ------------------------------------------------------------------ loop scenarios

    [TestMethod]
    public void StopsWhenTheCountIsReached()
    {
        var w = new LoopWorld();

        var r = w.Run(P(slot: 2, count: 3, batch: "1"));

        Assert.AreEqual((JobState.Succeeded, "loop.countReached"), Code(r));
        Assert.HasCount(3, w.Ctx.Runner.Runs);
        Assert.IsTrue(w.Ctx.Runner.Runs.All(p => p.Id == "crafting.loop" && p.Steps.Any(s => s.Id == "slot.02") && p.Steps.All(s => s.Id != "max")));
        Assert.AreEqual((3L, 3L, 3L), ((long)r.Data!["total"]!, (long)r.Data["rounds"]!, (long)r.Data["count"]!));
        Assert.AreEqual(TimeSpan.FromSeconds(6), w.Ctx.ClockFake.Monotonic - w.StartMono, "3 s pause between rounds, none before the first");
        var rounds = w.Ctx.JournalFake.Records.Where(j => j.Kind == JournalKind.Stock && Equals(j.Fields["event"], "craftLoop.round")).ToList();
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, rounds.Select(j => (long)j.Fields["total"]!).ToArray(), "every round journaled");
        Assert.IsTrue(rounds.All(j => Equals(j.Fields["produced"], 1L)));
        Assert.IsTrue(w.Ctx.KnowledgeFake!.StaleMarks.Any(m => m.Scope == StaleScope.AllStock && m.Reason == "craft.loop"), "stock knowledge outdated");
    }

    [TestMethod]
    public void AMaxBatchCountsEveryItemOfTheRound()
    {
        var w = new LoopWorld { ItemsPerRound = [13, 13] };
        var r = w.Run(P(count: 20, batch: "max"));
        Assert.AreEqual((JobState.Succeeded, "loop.countReached"), Code(r));
        Assert.AreEqual(26L, r.Data!["total"], "a max batch may overshoot the count");
        Assert.IsTrue(w.Ctx.Runner.Runs.All(p => p.Steps.Any(s => s.Id == "max") && p.Steps.All(s => !s.Id.StartsWith("slot.", StringComparison.Ordinal))));
    }

    [TestMethod]
    public void StopsWhenARoundStartsNoCraft()
    {
        // Materials out: Make registered but no station opened -> the engine ends the commit step "commit.noEffect".
        var w = new LoopWorld { ItemsPerRound = [1, 1, -1] };
        var r = w.Run(P(count: 0));
        Assert.AreEqual((JobState.Succeeded, "loop.materialsOut"), Code(r));
        Assert.AreEqual((2L, 3L), ((long)r.Data!["total"]!, (long)r.Data["rounds"]!));
        Assert.HasCount(3, w.Ctx.Runner.Runs.Where(x => x.Id != "crafting.makeSnapshot"), "never another Make after a round without a craft");

        // With a count still open the job did not reach its goal.
        var w2 = new LoopWorld { ItemsPerRound = [1, -1] };
        Assert.AreEqual((JobState.Failed, "loop.materialsOut"), Code(w2.Run(P(count: 5))));

        // Nothing at all could be made.
        var w3 = new LoopWorld { ItemsPerRound = [-1] };
        var r3 = w3.Run(P(count: 0));
        Assert.AreEqual((JobState.Failed, "loop.noCraft"), Code(r3));
        Assert.AreEqual(0L, r3.Data!["total"]);
    }

    [TestMethod]
    public void StopsWhenARoundProducesNothing()
    {
        // A craft session started but made 0 items (one is_block only).
        var w = new LoopWorld { ItemsPerRound = [2, 0] };
        var r = w.Run(P(count: 10));
        Assert.AreEqual((JobState.Failed, "loop.materialsOut"), Code(r));
        Assert.AreEqual(2L, r.Data!["total"]);
        Assert.HasCount(2, w.Ctx.Runner.Runs);

        // The round succeeded but no new craft session began after it.
        var w2 = new LoopWorld { ItemsPerRound = [1, int.MinValue] };
        var r2 = w2.Run(P(count: 0));
        Assert.AreEqual((JobState.Succeeded, "loop.materialsOut"), Code(r2));
        Assert.AreEqual(1L, r2.Data!["total"]);
    }

    [TestMethod]
    public void StopsOnAStopRequestAndOnAnyOtherProcedureEnd()
    {
        var w = new LoopWorld { EndRoundWith = (2, ProcedureStatus.Stopped) };
        var r = w.Run(P(count: 0));
        Assert.AreEqual((JobState.Stopped, "round.stopped"), Code(r));
        Assert.AreEqual(1L, r.Data!["total"]);
        Assert.HasCount(2, w.Ctx.Runner.Runs);

        var halted = new LoopWorld { EndRoundWith = (1, ProcedureStatus.Halted) };
        Assert.AreEqual((JobState.Halted, nameof(HaltCode.ForbiddenUi)), Code(halted.Run(P(count: 0))));

        var userActive = new LoopWorld { EndRoundWith = (1, ProcedureStatus.Cancelled) };
        Assert.AreEqual((JobState.Cancelled, "round.cancelled"), Code(userActive.Run(P(count: 0))), "not mistaken for materials out");
    }

    [TestMethod]
    public void AStopDuringThePauseBetweenRoundsEndsTheJobAndIsJournaled()
    {
        using var cts = new CancellationTokenSource();
        var w = new LoopWorld { AfterRound = n => { if (n == 1) { cts.Cancel(); } } };
        // The handler throws; the feature host's guard turns the cancellation into a Cancelled result (the stop source
        // is the host's to report).
        Assert.AreEqual((JobState.Cancelled, "cancelled"), Code(w.Run(P(count: 0), cts.Token)));
        Assert.HasCount(1, w.Ctx.Runner.Runs);
        Assert.IsTrue(w.Ctx.JournalFake.Records.Any(j => Equals(j.Fields.GetValueOrDefault("event"), "craftLoop.end") && Equals(j.Fields["code"], "cancelled")));
    }

    [TestMethod]
    public void TurningTheSwitchOffMidLoopStopsBeforeTheNextRound()
    {
        var w = new LoopWorld();
        w.AfterRoundCtx = (n, ctx) => ctx.SwitchesFake.Crafting = n < 2;
        var r = w.Run(P(count: 0));
        Assert.AreEqual((JobState.Refused, "switch.off:crafting"), Code(r));
        Assert.HasCount(2, w.Ctx.Runner.Runs);
        Assert.AreEqual(2L, r.Data!["total"]);
    }

    [TestMethod]
    public void NeverRunsWithTheSwitchOffOrSlideClicksOff()
    {
        // 4.33: no per-character permission any more (one knowledge profile); the switch is the commit gate
        var off = new LoopWorld();
        off.Ctx.SwitchesFake.Crafting = false;
        Assert.AreEqual((JobState.Refused, "switch.off:crafting"), Code(off.Run(P(slot: 1, count: 5))));
        Assert.HasCount(0, off.Ctx.Runner.Runs);
        Assert.AreEqual(0, off.Ctx.ScreenFake.Calls);

        var clicksOff = new LoopWorld();
        clicksOff.Ctx.SwitchesFake.SlideClickEnabled = false;
        Assert.AreEqual((JobState.Refused, "slideClick.off"), Code(clicksOff.Run(P(count: 5))));
        Assert.HasCount(0, clicksOff.Ctx.Runner.Runs);
    }

    [TestMethod]
    public void ACraftInProgressOrAReviveRefusesWithZeroInputs()
    {
        var busy = new LoopWorld();
        busy.Ctx.Ui = T.Snapshot(100, new CraftSessionInfo(true, "UI_LIFE_ALCHEMIST_MAKE_POT", DateTime.Now, T.Pos(50), 3, null, null, false));
        Assert.AreEqual((JobState.Refused, "craft.inProgress"), Code(busy.Run(P(count: 1))));
        Assert.HasCount(0, busy.Ctx.Runner.Runs);

        var dead = new LoopWorld();
        dead.Ctx.Ui = T.Snapshot(100, busy: BusyKind.Revive);
        Assert.AreEqual((JobState.Refused, "busy:Revive"), Code(dead.Run(P(count: 1))));
        Assert.HasCount(0, dead.Ctx.Runner.Runs);
    }

    [TestMethod]
    public void AMissingOpenAbilityFailsBeforeAnyInput()
    {
        var w = new LoopWorld(withOpenAbility: false);
        var r = w.Run(P(count: 1));
        Assert.AreEqual((JobState.Failed, "procedure.missing:OpenAbility"), Code(r));
        Assert.HasCount(0, w.Ctx.Runner.Runs);
    }

    private static Recipe Cake() => new(
        "scallion fish cake|lifeskill|0", new ItemKey("scallion fish cake"), 1, "lifeskill", [new MaterialRequirement(new ItemKey("golden juice"), 5, 20330051)], [], null,
        DateTimeOffset.UnixEpoch, "screen:craft.fav/7", false, RecipeConfidence.Complete, null, null, ProductItemId: 1063000);

    private static JobParameters CakeParams(long count) => new(new Dictionary<string, object> { ["slot"] = 7L, ["count"] = count, ["item"] = 1063000L });

    private static long Events(LoopWorld w, string name) => w.Ctx.JournalFake.Records.Count(j => Equals(j.Fields.GetValueOrDefault("event"), name));

    // R2.12, live 2026-10-23 (incident 14): the slot 7 click went out while the user's mouse moved and selected Thick Fish
    // Soup; slot 7 still held the cake, but the loop called it a moved slot and the goal walked the whole list
    [TestMethod]
    public void AWrongSelection_IsSelectedOnceMore_BeforeTheSlotCountsAsMoved()
    {
        var once = new LoopWorld { SelectedSequence = new Queue<long>([1063000, 1061000, 1063000]) };
        once.Ctx.KnowledgeFake!.Recipes.Add(Cake());
        Assert.AreEqual((JobState.Succeeded, "loop.countReached"), Code(once.Run(CakeParams(2))));
        Assert.AreEqual(1L, Events(once, "craftLoop.reselect"));

        var twice = new LoopWorld { SelectedSequence = new Queue<long>([1063000, 1061000, 1061000]) };
        twice.Ctx.KnowledgeFake!.Recipes.Add(Cake());
        var r = twice.Run(CakeParams(2));
        Assert.AreEqual((JobState.Refused, "slotItem.changed"), Code(r), "a second wrong product is a moved slot");
        Assert.AreEqual(1L, r.Data!["total"], "what was made before stays in the result");
    }

    // incident 29 (2026-10-28): slot 4 held Longevity Sashimi, the store still gave it to Sashimi; every goal clicked it
    [TestMethod]
    public void AMovedSlot_GivesUpTheStoredRecipesClaim_OnlyAfterTheSecondLook()
    {
        var once = new LoopWorld { SelectedSequence = new Queue<long>([1063000, 1061000, 1063000]) };
        once.Ctx.KnowledgeFake!.Recipes.Add(Cake());
        once.Run(CakeParams(2));
        Assert.AreEqual("screen:craft.fav/7", once.Ctx.KnowledgeFake.Recipes.Single().SourceCapture, "a disturbed click proves nothing");
        Assert.AreEqual(0L, Events(once, "favorites.slotReleased"));

        var moved = new LoopWorld { SelectedSequence = new Queue<long>([1063000, 1061000, 1061000]) };
        moved.Ctx.KnowledgeFake!.Recipes.Add(Cake());
        moved.Run(CakeParams(2));
        Assert.AreEqual("screen:craft.fav.old/7", moved.Ctx.KnowledgeFake.Recipes.Single().SourceCapture, "the goal finds it in the list from now on");
        Assert.AreEqual(1L, Events(moved, "favorites.slotReleased"));
    }

    // R2.2: after crafting every count was marked outdated, and the next round planned from the pre-craft counts
    [TestMethod]
    public void MadeItems_ChangeTheStockByTheCraft_InsteadOfMarkingEverythingOutdated()
    {
        var w = new LoopWorld { SelectedItemId = 1063000, ItemsPerRound = [5] };
        var k = w.Ctx.KnowledgeFake!;
        k.Recipes.Add(Cake());
        var cake = new ItemKey("scallion fish cake");
        var juice = new ItemKey("golden juice");
        k.Items[cake] = new StockEntry(cake, 10, null, StockSource.Observed, DateTimeOffset.UnixEpoch, T.Pos(100), 0.9, "test", false);
        k.Items[juice] = new StockEntry(juice, 300, null, StockSource.Observed, DateTimeOffset.UnixEpoch, T.Pos(100), 0.9, "test", false);

        Assert.AreEqual((JobState.Succeeded, "loop.countReached"), Code(w.Run(CakeParams(5))));

        Assert.AreEqual(15L, k.Items[cake].Count);
        Assert.AreEqual(275L, k.Items[juice].Count);
        Assert.IsFalse(k.Items[juice].Stale);
        Assert.IsFalse(k.StaleMarks.Any(m => m.Scope == StaleScope.AllStock), "no blanket stale mark");
    }

    // R2.2 (audit 39): Make's "missing" means fewer than one craft needs, not 0
    [TestMethod]
    public void AMaterialMakeNamesMissing_IsStoredAsShort_NotAsZero()
    {
        var w = new LoopWorld { SelectedItemId = 1063000, ItemsPerRound = [-1] };
        w.AfterRoundCtx = (_, ctx) => ctx.Ui = ctx.Ui with
        {
            AsOf = T.Pos(9000),
            LastItemTip = new ItemTipInfo("Golden Juice", 20330051, new DateTime(2026, 10, 23, 13, 40, 4), T.Pos(9000)),
        };
        w.Ctx.KnowledgeFake!.Recipes.Add(Cake());

        var r = w.Run(CakeParams(15));

        Assert.AreEqual((JobState.Failed, "loop.noCraft"), Code(r));
        Assert.AreEqual("golden juice", r.Data!["missingItem"]);
        var stored = w.Ctx.KnowledgeFake.Items[new ItemKey("golden juice")];
        Assert.IsNull(stored.Count);
        Assert.AreEqual(new ValueRange(0, 4), stored.Range, "0 to 4: fewer than the 5 one cake needs");
    }

    private static (JobState, string) Code(JobResult r) => (r.State, r.Code);

    /// <summary>Fake engine for CraftLoop: one craft session per round in the UI model.</summary>
    private sealed class LoopWorld
    {
        private readonly FeatureHost _host;
        private long _offset = 1000;
        private int _round;

        public LoopWorld(bool withOpenAbility = true)
        {
            var (host, env, _) = T.LoadReal();
            _host = host;
            if (withOpenAbility)
            {
                env.Procedures.Put(new ProcedureDefinition("OpenAbility", "core", [new StepDefinition("open", StatePredicate.Any, new ClickAction("menu.ability"), Expectation.Nothing)], ProcedureLimits.Default));
            }

            Catalog = env.Procedures;
            // the Make view is on top, as in the game while a recipe is crafted (the tip check needs it, 2026-10-16)
            Ctx = new FakeJobContext(env.Procedures) { Ui = T.Snapshot(_offset) with { AdvisoryStack = ["UI_LIFESKILL_MAKE_VIEW"] } };
            Ctx.SwitchesFake.Crafting = true;
            Ctx.Runner.Script = Engine;
            StartMono = Ctx.ClockFake.Monotonic;
        }

        public FakeJobContext Ctx { get; }

        public FakeProcedureBuilder Catalog { get; }

        public TimeSpan StartMono { get; }

        /// <summary>Items per round: n &gt;= 0 = a session with n ticks; -1 = Make pressed, no craft (commit.noEffect);
        /// int.MinValue = round succeeded but no new session. Rounds past the list make 1 item.</summary>
        public int[] ItemsPerRound { get; init; } = [];

        /// <summary>The round (1-based) that ends with the given status instead.</summary>
        public (int Round, ProcedureStatus Status)? EndRoundWith { get; init; }

        public Action<int>? AfterRound { get; init; }

        public Action<int, FakeJobContext>? AfterRoundCtx { get; set; }

        /// <summary>How the typing run (crafting.qty) ends.</summary>
        public ProcedureStatus QtyStatus { get; init; } = ProcedureStatus.Succeeded;

        /// <summary>The product id the tip of the selected recipe logs (crafting.productTip), or null for no tip.</summary>
        public long? SelectedItemId { get; init; }

        /// <summary>Product ids the tip logs, one per tip run, before <see cref="SelectedItemId"/> takes over.</summary>
        public Queue<long>? SelectedSequence { get; init; }

        public JobResult Run(JobParameters parameters, CancellationToken ct = default) =>
            T.Handler(_host, "CraftLoop").Execute(Ctx, parameters, ct);

        /// <summary>Diagnostics captures run (at most one per 30 minutes in a process).</summary>
        public int Snapshots { get; private set; }

        private ProcedureResult Engine(ProcedureDefinition p)
        {
            if (p.Id == "crafting.qty")
            {
                return T.Result(p.Id, QtyStatus, inputs: p.Steps.Count(s => s.Action is ClickAction));
            }

            if (p.Id == "crafting.productTip")
            {
                _offset += 10;
                var selected = SelectedSequence is { Count: > 0 } queue ? queue.Dequeue() : SelectedItemId;
                Ctx.Ui = selected is { } id
                    ? Ctx.Ui with { AsOf = T.Pos(_offset), LastItemTip = new ItemTipInfo("Selected", id, new DateTime(2026, 10, 15, 2, 0, 0), T.Pos(_offset)) }
                    : Ctx.Ui with { AsOf = T.Pos(_offset) };
                return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 2);
            }

            if (p.Id == "crafting.makeSnapshot")
            {
                Snapshots++;
                return T.Result(p.Id, ProcedureStatus.Succeeded); // incident 26: the diagnostics capture after a Make with no effect
            }

            Assert.AreEqual("crafting.loop", p.Id);
            if (!p.Steps.Any(s => s.Action is AwaitCraftAction))
            {
                return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: p.Steps.Count); // a typed round's selection part
            }

            _round++;
            try
            {
                if (EndRoundWith is { } end && end.Round == _round)
                {
                    var halt = end.Status == ProcedureStatus.Halted
                        ? new HaltReport(HaltCode.ForbiddenUi, "UI_RECHARGE", "j", p.Id, "make", DateTimeOffset.UnixEpoch, null)
                        : null;
                    return T.Result(p.Id, end.Status, halt: halt, inputs: 3);
                }

                var items = _round <= ItemsPerRound.Length ? ItemsPerRound[_round - 1] : 1;
                if (items == -1)
                {
                    var attempt = new StepAttemptRecord("make", 1, StepOutcome.None, OutcomeClass.Counted, RecoveryAction.End, null, true, null, "commit.noEffect", DateTimeOffset.UnixEpoch);
                    return T.Result(p.Id, ProcedureStatus.Cancelled, inputs: 4, attempts: [attempt]);
                }

                if (items == int.MinValue)
                {
                    return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 4);
                }

                var start = _offset + 10;
                _offset += 1000;
                Ctx.Ui = T.Snapshot(_offset, new CraftSessionInfo(false, "UI_LIFE_ALCHEMIST_MAKE_POT", DateTime.Now, T.Pos(start), items + 1, DateTime.Now, DateTime.Now, true)) with { AdvisoryStack = ["UI_LIFESKILL_MAKE_VIEW"] };
                return T.Result(p.Id, ProcedureStatus.Succeeded, inputs: 5);
            }
            finally
            {
                AfterRound?.Invoke(_round);
                AfterRoundCtx?.Invoke(_round, Ctx);
            }
        }
    }
}
