using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.EngineNav.Testing;

namespace Companion.Engine.Tests;

/// <summary>
/// The crafting feature's ScanFavorites slot runs (crafting.scanFavorites) through the real engine against the
/// simulated game: read-only navigation, no switch, any permission; the first slot opens the view, later slots only
/// click their cell while the Make view stays on top.
/// </summary>
[TestClass]
public sealed class ScanFavoritesRoundTests : ScenarioBase
{
    private static ProcedureDefinition ScanData()
    {
        var file = Harness.DataFile(Path.Combine("features", "crafting", "procedures.json"));
        return JsonSerializer.Deserialize<ProcedureDefinition[]>(File.ReadAllBytes(file), ContractJson.Options)!.Single(p => p.Id == "crafting.scanFavorites");
    }

    /// <summary>The run as the ScanFavorites handler builds it for a slot.</summary>
    private static ProcedureDefinition Slot(int slot)
    {
        var data = ScanData();
        var keep = "slot." + slot.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        return data with
        {
            Steps = data.Steps
                .Where(s => !s.Id.StartsWith("slot.", StringComparison.Ordinal) || s.Id == keep)
                .Select(s => s.Id == "open" && slot > 1 ? s with { Flags = s.Flags | StepFlags.SkipIfSatisfied } : s)
                .ToArray(),
        };
    }

    private void Prepare()
    {
        H.Templates.Verify("menu.ability");
        H.Templates.Add(FakeTemplates.Def("craft.fav.anchor", 591, 756, TemplateStatus.Verified, 1, null, TargetTags.ScreenAnchor));
        H.Readers.SetReady("craft.product", "UI_LIFESKILL_MAKE_VIEW");
        H.Readers.SetReady("craft.pairs", "UI_LIFESKILL_MAKE_VIEW");
        H.Switches.Crafting = false;
        for (var i = 0; i < 6; i++)
        {
            // The Make view sometimes opens with the "New Ability Phase" announcement on top (live run 2026-10-14).
            H.Game.ClickScript.Enqueue(req => req.TemplateId == "menu.ability"
                ? H.Game.SentClick(req, () =>
                {
                    H.Log.Show("UI_LIFESKILL_MAKE_VIEW");
                    H.Log.Show("UI_LIFE_STAGE_PREVIEW");
                })
                : null);
        }
    }

    [TestMethod]
    public void FirstSlot_OpensTheView_ClosesTheAnnouncement_ClicksTheCell_AndReadsBothReaders()
    {
        Prepare();
        var r = H.Run(Slot(1));

        AssertStatus(ProcedureStatus.Succeeded, r);
        CollectionAssert.AreEqual(new[] { "hud.toggle.collapsed", "menu.ability", "craft.fav.anchor" }, H.Game.ClickRequests.Select(c => c.TemplateId).ToArray());
        Assert.AreEqual(new PointI(364, 498), H.Game.ClickRequests[2].ClientPoint, "slot 1 = located anchor + offset");
        Assert.AreEqual(2, H.Keys(GameKey.Escape), "the reset Esc and one Esc for the announcement");
        CollectionAssert.AreEquivalent(new[] { "product", "pairs" }, r.Reads.Keys.ToArray());
        Assert.AreEqual("craft.pairs", r.Reads["pairs"].ReaderId);
        Assert.AreEqual(0, H.Game.ClickRequests.Count(c => c.TemplateId == "craft.make"), "never crafts");
        H.AssertJournalClean();
    }

    [TestMethod]
    public void LaterSlots_SkipOpeningWhileTheMakeViewIsOnTop_OnlyTheCellIsClicked()
    {
        Prepare();
        AssertStatus(ProcedureStatus.Succeeded, H.Run(Slot(1)));
        var clicks = H.Game.ClickRequests.Count;
        var escs = H.Keys(GameKey.Escape);

        var r = H.Run(Slot(7));
        AssertStatus(ProcedureStatus.Succeeded, r);
        Assert.AreEqual(clicks + 1, H.Game.ClickRequests.Count);
        Assert.AreEqual(("craft.fav.anchor", new PointI(477, 638)), (H.Game.ClickRequests[^1].TemplateId, H.Game.ClickRequests[^1].ClientPoint));
        Assert.AreEqual(escs, H.Keys(GameKey.Escape), "no Reset, no announcement Esc");
        Assert.AreEqual(2, H.Journal.OfKind(JournalKind.Decision).Count(d => FakeJournal.Str(d, "event") == "skip.satisfied" && d.StepId is "open" or "announce.close"));
        Assert.AreEqual(2, r.Reads.Count);
    }

    [TestMethod]
    public void SlotClicks_OptIntoThePatchRule_TheHudNavigationClicksDoNot()
    {
        Prepare();
        AssertStatus(ProcedureStatus.Succeeded, H.Run(Slot(1)));

        var clicks = H.Game.ClickRequests;
        CollectionAssert.AreEqual(
            new[] { ("hud.toggle.collapsed", false, false), ("menu.ability", false, false), ("craft.fav.anchor", true, true) },
            clicks.Select(c => (c.TemplateId, c.PatchVerify, c.LocatedFrame is not null)).ToArray());
        var slot = clicks[2];
        Assert.AreEqual(slot.LocatedAtMonotonic, slot.LocatedFrame!.CapturedAtMonotonic);
        Assert.IsTrue(clicks.Take(2).All(c => c.NavFixed), "the HUD clicks keep their own (cross-monitor) rule");
    }

    [TestMethod]
    public void SlideClicksOff_IsRefusedWithZeroInputs()
    {
        Prepare();
        H.Switches.SlideClickEnabled = false;
        AssertStatus(ProcedureStatus.Refused, H.Run(Slot(1)));
        Assert.AreEqual(0, H.Game.Inputs);
    }
}
