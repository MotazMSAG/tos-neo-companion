using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.EngineNav.Testing;

namespace Companion.Engine.Tests;

/// <summary>
/// The crafting feature's CraftLoop round (crafting.loop, live run 2026-10-14) through the real engine against the
/// simulated game, plus the offset-click guards it relies on.
/// </summary>
[TestClass]
public sealed class CraftLoopRoundTests : ScenarioBase
{
    private static ProcedureDefinition LoopData()
    {
        var file = Harness.DataFile(Path.Combine("features", "crafting", "procedures.json"));
        return JsonSerializer.Deserialize<ProcedureDefinition[]>(File.ReadAllBytes(file), ContractJson.Options)!.Single(p => p.Id == "crafting.loop");
    }

    /// <summary>The round as the CraftLoop handler builds it: one slot step (or none), the max step for a max batch.</summary>
    private static ProcedureDefinition Round(int slot, bool max)
    {
        var data = LoopData();
        var keep = slot > 0 ? "slot." + slot.ToString("00", System.Globalization.CultureInfo.InvariantCulture) : null;
        return data with
        {
            Steps = data.Steps
                .Where(s => (!s.Id.StartsWith("slot.", StringComparison.Ordinal) || s.Id == keep) && (s.Id != "max" || max))
                .Where(s => !s.Id.StartsWith("qty", StringComparison.Ordinal) && !s.Id.StartsWith("key.", StringComparison.Ordinal)) // typed batches only
                .Select(s => s.Action is AwaitCraftAction ? s with { Action = new AwaitCraftAction(max ? 999 : 1) } : s)
                .ToArray(),
        };
    }

    private void Prepare(bool stationOpens = true, bool announcement = true)
    {
        H.Templates.Verify("menu.ability");
        H.Templates.Add(FakeTemplates.Def("craft.fav.anchor", 591, 756, TemplateStatus.Verified, 1, null, TargetTags.ScreenAnchor));
        H.Templates.Add(FakeTemplates.Def("craft.qty.max", 1264, 868, TemplateStatus.Verified, 1, null, TargetTags.None, AutomationSwitch.Crafting));
        H.Templates.Add(FakeTemplates.Def("craft.make", 1472, 870, TemplateStatus.Verified, 1, null, TargetTags.Commit, AutomationSwitch.Crafting));
        H.Switches.Crafting = true;
        for (var i = 0; i < 12; i++)
        {
            H.Game.ClickScript.Enqueue(req => req.TemplateId switch
            {
                // The Make view sometimes opens with the "New Ability Phase" announcement on top (live run 2026-10-14).
                "menu.ability" => H.Game.SentClick(req, () =>
                {
                    H.Log.Show("UI_LIFESKILL_MAKE_VIEW");
                    if (announcement)
                    {
                        H.Log.Show("UI_LIFE_STAGE_PREVIEW");
                    }
                }),
                // Like real_craft_session.log: the view goes to the close list, the station opens, ticks follow.
                "craft.make" when stationOpens => H.Game.SentClick(req, () =>
                {
                    H.Log.Ui(UiVerb.AddToCloseList, "UI_LIFESKILL_MAKE_VIEW");
                    H.Log.Show("UI_LIFE_ALCHEMIST_MAKE_POT");
                    H.Log.Craft = new CraftSessionInfo(true, "UI_LIFE_ALCHEMIST_MAKE_POT", H.Clock.GameNow, H.Log.Position, 1, H.Clock.GameNow, null, false);
                    H.Clock.At(TimeSpan.FromSeconds(9), () =>
                    {
                        H.Log.Ui(UiVerb.AddToCloseList, "UI_LIFE_ALCHEMIST_MAKE_POT");
                        H.Log.Craft = H.Log.Craft with { InProgress = false, IsBlockCount = 4, LastTickAt = H.Clock.GameNow, EndedAt = H.Clock.GameNow };
                    });
                }),
                _ => null,
            });
        }
    }

    [TestMethod]
    public void Round_Slot3MaxBatch_OpensClosesTheAnnouncementClicksTheCellMaxMakeAndAwaits()
    {
        Prepare();
        var r = H.Run(Round(3, max: true), JobKind.Commit, AutomationSwitch.Crafting);

        AssertStatus(ProcedureStatus.Succeeded, r);
        CollectionAssert.AreEqual(
            new[] { "hud.toggle.collapsed", "menu.ability", "craft.fav.anchor", "craft.qty.max", "craft.make" },
            H.Game.ClickRequests.Select(c => c.TemplateId).ToArray());
        var cell = H.Game.ClickRequests[2];
        Assert.AreEqual(new PointI(590, 498), cell.ClientPoint, "slot 3 = located anchor + offset");
        Assert.AreEqual(new PointI(591, 756), cell.TemplateCenter, "the anchor is what GameIO verifies while slid");
        Assert.IsNull(H.Game.ClickRequests[3].TemplateCenter);
        Assert.AreEqual(new PointI(1472, 870), H.Game.ClickRequests[4].ClientPoint, "Make at its own centre");
        Assert.AreEqual(2, H.Keys(GameKey.Escape), "the reset Esc and one Esc for the announcement");
        Assert.AreEqual(UiExpectation.HiddenTop("UI_LIFE_STAGE_PREVIEW"), H.Game.KeyRequests[1].ExpectedUi.Single());
        Assert.IsTrue(H.Journal.OfKind(JournalKind.Decision).Any(d => FakeJournal.Str(d, "event") == "click.offset" && FakeJournal.Str(d, "dy") == "-258"));
        Assert.IsTrue(H.Journal.OfKind(JournalKind.Decision).Any(d => FakeJournal.Str(d, "event") == "craft.end"));
        Assert.IsFalse(H.Log.State.Craft.InProgress, "the round ends after the craft session");
        Assert.AreEqual(3, H.Log.State.Craft.Ticks);
    }

    [TestMethod]
    public void Round_OnlyTheFixedHudNavigationClicksOptInToTheCrossMonitorRule()
    {
        // SPEC 7.3 / EVIDENCE c13: the toggle and menu.ability may be pressed without the slid check on a cross-monitor
        // slide; the favourites cell (offset), the gated max button and the Make commit keep the strict check.
        Prepare();
        var r = H.Run(Round(3, max: true), JobKind.Commit, AutomationSwitch.Crafting);

        AssertStatus(ProcedureStatus.Succeeded, r);
        CollectionAssert.AreEqual(
            new[] { ("hud.toggle.collapsed", true), ("menu.ability", true), ("craft.fav.anchor", false), ("craft.qty.max", false), ("craft.make", false) },
            H.Game.ClickRequests.Select(c => (c.TemplateId, c.NavFixed)).ToArray());
    }

    [TestMethod]
    public void TheCommitLoop_KeepsTheStrictSlidCheck_ButTheVerifiedSlotClickMayUseThePatch()
    {
        // 2026-10-21: the favourites cell click is a selection CraftLoop verifies before every Make (the product's logged
        // tip), so it carries SelectionVerified and may use the off-screen anchor patch rule (a cursor low on the monitor
        // slid the anchor off the drawn screen and the strict check cancelled the cell click again and again). Every other
        // click of the commit round, the Make press above all, keeps the strict slid check.
        Prepare();
        var r = H.Run(Round(3, max: true), JobKind.Commit, AutomationSwitch.Crafting);

        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.HasCount(5, H.Game.ClickRequests);
        var summary = string.Join(",", H.Game.ClickRequests.Select(c => c.TemplateId + ":" + c.StepId + ":" + c.PatchVerify));
        Assert.IsTrue(H.Game.ClickRequests.Where(c => c.StepId == "slot.03").All(c => c.PatchVerify && c.LocatedFrame is not null), summary);
        Assert.IsTrue(H.Game.ClickRequests.Where(c => c.StepId != "slot.03").All(c => !c.PatchVerify && c.LocatedFrame is null), summary);
        Assert.IsTrue(H.Game.ClickRequests.Single(c => c.StepId == "make") is { PatchVerify: false }, "the commit press stays strict");
    }

    [TestMethod]
    public void BagClick_OptsIn_ButTheSameTemplateInACommitStepDoesNot()
    {
        // OpenBagClick: a plain navigation click on hud.bag (NavFixed) opts in.
        AssertStatus(ProcedureStatus.Succeeded, H.Run("OpenBagClick"));
        Assert.IsTrue(H.Game.ClickRequests.Single(c => c.TemplateId == "hud.bag").NavFixed);

        // The same NavFixed template pressed by a commit step keeps the strict slid check.
        H.Switches.Crafting = true;
        var commit = new ProcedureDefinition("TestCommitBag", "commit step on a NavFixed template", [
            new StepDefinition("reset", StatePredicate.Any, new ResetAction(), new Expectation([UiExpectation.HiddenTop(null)])),
            new StepDefinition("bag", new StatePredicate(HudClean: true), new ClickAction("hud.bag"), new Expectation(UiExpectation.Opened("UI_BAG_MAIN")), 3000, StepFlags.Commit),
        ], ProcedureLimits.Default, ProcedureKind.Commit, AutomationSwitch.Crafting);
        H.Run(commit, JobKind.Commit, AutomationSwitch.Crafting);

        var bagClicks = H.Game.ClickRequests.Where(c => c.TemplateId == "hud.bag").ToList();
        Assert.IsTrue(bagClicks.Count >= 2, "the commit step clicked the bag");
        Assert.IsFalse(bagClicks[^1].NavFixed);
    }

    [TestMethod]
    public void Round_SelectedRecipeBatchOne_NoAnnouncement_IsOnlyTheMakePress()
    {
        Prepare(announcement: false);
        var r = H.Run(Round(0, max: false), JobKind.Commit, AutomationSwitch.Crafting);

        AssertStatus(ProcedureStatus.Succeeded, r);
        CollectionAssert.AreEqual(new[] { "hud.toggle.collapsed", "menu.ability", "craft.make" }, H.Game.ClickRequests.Select(c => c.TemplateId).ToArray());
        Assert.AreEqual(1, H.Keys(GameKey.Escape), "no Esc while the announcement is not open");
        Assert.IsTrue(H.Journal.OfKind(JournalKind.Decision).Any(d => FakeJournal.Str(d, "event") == "skip.satisfied"));
    }

    [TestMethod]
    public void Round_MakeStartsNoCraft_EndsCommitNoEffect_AndMakeIsNeverPressedAgain()
    {
        // Materials out: the press registers, no station UI opens. The handler reads this as "no craft started".
        Prepare(stationOpens: false, announcement: false);
        var r = H.Run(Round(0, max: false), JobKind.Commit, AutomationSwitch.Crafting);

        AssertStatus(ProcedureStatus.Cancelled, r);
        Assert.AreEqual(("make", "commit.noEffect"), (r.Attempts[^1].StepId, r.Attempts[^1].DetailCode));
        Assert.AreEqual(1, H.Game.ClickRequests.Count(c => c.TemplateId == "craft.make"));
    }

    [TestMethod]
    public void Round_SwitchOff_RefusedWithZeroInputs_AndTheSwitchIsTheOnlyGate()
    {
        Prepare();
        H.Switches.Crafting = false;
        AssertStatus(ProcedureStatus.Refused, H.Run(Round(1, max: true), JobKind.Commit, AutomationSwitch.Crafting));
        Assert.AreEqual(0, H.Game.Inputs);

        // 4.33: no per-character permission (one knowledge profile): switched on, the same round runs
        H.Switches.Crafting = true;
        AssertStatus(ProcedureStatus.Succeeded, H.Run(Round(1, max: true), JobKind.Commit, AutomationSwitch.Crafting));
        Assert.AreEqual(1, H.Game.ClickRequests.Count(c => c.TemplateId == "craft.make"), "Make pressed once");
    }

    [TestMethod]
    public void OffsetClickIntoADenyZone_HaltsBeforeAnyClick()
    {
        // hud.bag + (-800, 400) = (948, 712): inside the chat panel deny zone of the HUD.
        var p = new ProcedureDefinition("OffsetDeny", "test", [
            new StepDefinition("reset", StatePredicate.Any, new ResetAction(), new Expectation([UiExpectation.HiddenTop(null)])),
            new StepDefinition("cell", new StatePredicate(HudClean: true), new ClickAction("hud.bag", new PointI(-800, 400)), Expectation.Nothing),
        ], ProcedureLimits.Default);
        var r = H.Run(p);
        AssertHalt(HaltCode.LayoutChanged, r);
        Assert.AreEqual(0, H.Game.ClickCount);
    }

    [TestMethod]
    public void OffsetClickOutsideTheClient_RefusedBeforeAnyClick()
    {
        var p = new ProcedureDefinition("OffsetOut", "test", [
            new StepDefinition("reset", StatePredicate.Any, new ResetAction(), new Expectation([UiExpectation.HiddenTop(null)])),
            new StepDefinition("cell", new StatePredicate(HudClean: true), new ClickAction("hud.bag", new PointI(300, 0)), Expectation.Nothing),
        ], ProcedureLimits.Default);
        var r = H.Run(p);
        AssertStatus(ProcedureStatus.Refused, r);
        Assert.AreEqual("click.offsetOutsideClient", r.Attempts[^1].DetailCode);
        Assert.AreEqual(0, H.Game.ClickCount);
    }

    [TestMethod]
    public void ACommitTemplateIsNeverClickedWithAnOffset()
    {
        Prepare();
        var p = new ProcedureDefinition("OffsetCommit", "test", [
            new StepDefinition("make", StatePredicate.Any, new ClickAction("craft.make", new PointI(30, 0)), Expectation.Nothing),
        ], ProcedureLimits.Default, ProcedureKind.Commit, AutomationSwitch.Crafting);
        var r = H.Run(p, JobKind.Commit, AutomationSwitch.Crafting);
        AssertStatus(ProcedureStatus.Refused, r);
        Assert.AreEqual("click.commitOffset", r.Attempts[^1].DetailCode);
        Assert.AreEqual(0, H.Game.Inputs);
        Assert.AreEqual(0, H.Game.Captures, "refused before locating");
    }
}
