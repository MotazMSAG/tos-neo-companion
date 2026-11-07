using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Logs;
using Companion.Contracts.Navigation;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.Feature.TeaHouse;
using Companion.Features;

namespace Companion.Features.Tests;

/// <summary>
/// TeaHousePlay (Kitty Tea House auto-play, 2026-10-30): the helper posts presses on the desk, the job presses one square's
/// click step and answers "pressed" only when the game logs on_grid_click for that square after the click.
/// </summary>
[TestClass]
public sealed class TeaHousePlayTests
{
    private const string BoardUi = "UI_ACTIVITY_CENTER";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static (IJobHandler Handler, FakeJobContext Ctx, TeaHouseDesk Desk) Load()
    {
        var env = TestEnv.WithRealData();
        var host = new FeatureHost(env.Clock);
        var desk = new TeaHouseDesk();
        var reports = host.LoadAll([new TeaHouseFeature(desk)], env.Context);
        Assert.IsTrue(reports.All(r => r.Status == FeatureLoadStatus.Enabled), string.Join(", ", reports));
        return (T.Handler(host, TeaHouseFeature.PlayJob), new FakeJobContext(env.Procedures), desk);
    }

    private static UiStateSnapshot OnTop(string ui, long asOf = 1000) => T.Snapshot(asOf) with { AdvisoryStack = [ui] };

    /// <summary>The board runs (presses), without the no-input settle run after the last one.</summary>
    private static List<ProcedureDefinition> Presses(FakeJobContext ctx) => ctx.Runner.Runs.Where(r => r.Id != "teahouse.settle").ToList();

    /// <summary>Runs the job, presses <paramref name="squares"/> one by one like the helper, then switches auto-play off.</summary>
    private static (List<TeaPress?> Presses, JobResult Result) Play(IJobHandler handler, FakeJobContext ctx, TeaHouseDesk desk, params int[] squares)
    {
        desk.AutoPlay = true;
        var job = Task.Run(() => handler.Execute(ctx, JobParameters.Empty, CancellationToken.None));
        var presses = new List<TeaPress?>();
        foreach (var square in squares)
        {
            presses.Add(desk.Press(square, Wait, CancellationToken.None));
        }

        desk.AutoPlay = false;
        Assert.IsTrue(job.Wait(Wait), "the job ends when auto-play goes off");
        Assert.IsFalse(desk.JobRunning);
        return (presses, job.Result);
    }

    [TestMethod]
    public void APress_RunsOnlyThatSquaresClickStep_AndIsDoneWhenTheLogShowsIt()
    {
        var (handler, ctx, desk) = Load();
        var before = OnTop(BoardUi);
        var after = OnTop(BoardUi, 1200) with { LastGridClick = new GridClickInfo(3, 4, 1, new DateTime(2026, 10, 30, 2, 0, 0), T.Pos(1100)) };
        ctx.UiProvider = () => Presses(ctx).Count == 0 ? before : after;

        var (presses, result) = Play(handler, ctx, desk, 15);

        Assert.AreEqual(new TeaPress(true, "pressed", 1), presses.Single());
        var run = Presses(ctx).Single();
        Assert.AreEqual("teahouse.board", run.Id);
        var step = run.Steps.Single();
        Assert.AreEqual("sq.15", step.Id, "row 3, column 4");
        StringAssert.StartsWith(((ClickAction)step.Action).TemplateId, "teahouse.");
        Assert.AreEqual((JobState.Succeeded, "teahouse.autoPlayOff", 1L), (result.State, result.Code, (long)result.Data!["presses"]!));
        Assert.IsTrue(ctx.JournalFake.Records.Any(r => Equals(r.Fields["event"], "teahouse.press") && Equals(r.Fields["done"], true)));
    }

    [TestMethod]
    public void AfterTheLastPress_ANoInputSettleRun_ReadsItsFullWindow()
    {
        // 2026-10-31: the engine's late check of the last quick press runs even when no press follows
        var (handler, ctx, desk) = Load();
        var after = OnTop(BoardUi, 1200) with { LastGridClick = new GridClickInfo(3, 4, 1, new DateTime(2026, 10, 30, 2, 0, 0), T.Pos(1100)) };
        ctx.UiProvider = () => Presses(ctx).Count == 0 ? OnTop(BoardUi) : after;

        Play(handler, ctx, desk, 15);

        var settle = ctx.Runner.Runs.Single(r => r.Id == "teahouse.settle");
        Assert.IsTrue(settle.Steps.All(s => s.Action is WaitAction), "no input");
        Assert.AreEqual("teahouse.settle", ctx.Runner.Runs[^1].Id, "after the press");
    }

    [TestMethod]
    public void APress_IsNotDone_OffTheBoard_UnloggedOrLoggedOnAnotherSquare()
    {
        var (handler, ctx, desk) = Load();

        // another window on top: nothing is pressed
        ctx.UiProvider = () => OnTop("UI_BAG_MAIN");
        var (presses, _) = Play(handler, ctx, desk, 15);
        Assert.AreEqual(new TeaPress(false, "notOnBoard:UI_BAG_MAIN"), presses.Single());
        Assert.IsEmpty(Presses(ctx));

        // the click ran but the game logged nothing (an older press does not count)
        var old = OnTop(BoardUi) with { LastGridClick = new GridClickInfo(3, 4, 1, default, T.Pos(900)) };
        ctx.UiProvider = () => old;
        (presses, _) = Play(handler, ctx, desk, 15);
        Assert.AreEqual(new TeaPress(false, "press.notLogged"), presses.Single());

        // the game logged a press on another square (the user's hand?)
        ctx.UiProvider = () => Presses(ctx).Count < 2 ? OnTop(BoardUi) : OnTop(BoardUi, 1200) with { LastGridClick = new GridClickInfo(3, 5, 2, default, T.Pos(1100)) };
        (presses, _) = Play(handler, ctx, desk, 15);
        Assert.AreEqual(new TeaPress(false, "press.otherSquare:16", 2), presses.Single());
    }

    [TestMethod]
    public void AnEngineStop_EndsTheJobWithItsResult_ButARefusedPressDoesNot()
    {
        var (handler, ctx, desk) = Load();
        ctx.UiProvider = () => OnTop(BoardUi);
        ctx.Runner.Script = p => T.Result(p.Id, ProcedureStatus.Refused);
        var (presses, result) = Play(handler, ctx, desk, 15, 16);
        Assert.IsTrue(presses.All(p => p is { Done: false } && p.Code.StartsWith("press.refused", StringComparison.Ordinal)), string.Join(", ", presses));
        Assert.AreEqual(JobState.Succeeded, result.State, "a refused press is answered and the job goes on");

        ctx.Runner.Script = p => T.Result(p.Id, ProcedureStatus.Halted, halt: new HaltReport(HaltCode.CursorClipChanged, "test", "job", "teahouse.board", "sq.15", DateTimeOffset.UnixEpoch, null));
        desk.AutoPlay = true;
        var job = Task.Run(() => handler.Execute(ctx, JobParameters.Empty, CancellationToken.None));
        var press = desk.Press(15, Wait, CancellationToken.None);
        Assert.IsTrue(job.Wait(Wait), "a halt ends the job at once");
        Assert.AreEqual(false, press!.Done);
        Assert.AreEqual(JobState.Halted, job.Result.State);
        Assert.IsFalse(desk.AutoPlay, "auto-play goes off with a halt: it never starts again by itself");
        Assert.IsNull(desk.Press(16, TimeSpan.FromMilliseconds(300), CancellationToken.None), "no job takes presses after a halt");
        desk.AutoPlay = false;
    }

    [TestMethod]
    public void APressWithdrawnByTheHelper_IsCancelled_AnsweredAsWithdrawn_AndNeverEndsTheJobAsAStop()
    {
        // the user unticks Auto-play while the engine holds the press up (safety review 2026-10-30): the press is cancelled
        var (handler, ctx, desk) = Load();
        ctx.UiProvider = () => OnTop(BoardUi);
        ctx.Runner.Script = p =>
        {
            desk.AutoPlay = false;
            return T.Result(p.Id, ProcedureStatus.Cancelled);
        };
        desk.AutoPlay = true;
        var job = Task.Run(() => handler.Execute(ctx, JobParameters.Empty, CancellationToken.None));
        var press = desk.Press(15, Wait, CancellationToken.None);
        Assert.IsTrue(job.Wait(Wait));
        Assert.AreEqual(new TeaPress(false, "press.withdrawn"), press);
        Assert.AreEqual((JobState.Succeeded, "teahouse.autoPlayOff"), (job.Result.State, job.Result.Code));

        // a press posted while auto-play is off is never taken
        Assert.IsNull(desk.Press(15, Wait, CancellationToken.None));
        Assert.HasCount(1, Presses(ctx));
    }

    [TestMethod]
    public void AHiddenAnchor_PressesTheSquareOnceMoreOnItsSecondAnchor_OtherDeferralsDoNot()
    {
        // 2026-10-30 live: the frame corner was not found twice and the engine's reset pressed Esc, closing the minigame
        static ProcedureResult Deferred(string id, string detail) => T.Result(id, ProcedureStatus.Deferred, attempts:
            [new StepAttemptRecord("sq.10", 1, StepOutcome.PreconditionLost, OutcomeClass.Counted, RecoveryAction.End, null, false, null, detail, DateTimeOffset.UnixEpoch)]);

        var (handler, ctx, desk) = Load();
        var logged = OnTop(BoardUi, 1200) with { LastGridClick = new GridClickInfo(2, 5, 1, default, T.Pos(1100)) };
        ctx.UiProvider = () => Presses(ctx).Count < 2 ? OnTop(BoardUi) : logged;
        ctx.Runner.Script = p => p.Id == "teahouse.board" ? Deferred(p.Id, "template.notFound:teahouse.frame.topright") : T.Result(p.Id, ProcedureStatus.Succeeded);
        var (presses, _) = Play(handler, ctx, desk, 10);
        Assert.AreEqual(new TeaPress(true, "pressed", 1), presses.Single());
        CollectionAssert.AreEqual(new[] { "teahouse.board", "teahouse.board.alt" }, Presses(ctx).Select(r => r.Id).ToArray());
        var first = (ClickAction)Presses(ctx)[0].Steps.Single().Action;
        var second = (ClickAction)Presses(ctx)[1].Steps.Single().Action;
        Assert.AreEqual("sq.10", Presses(ctx)[1].Steps.Single().Id);
        Assert.AreNotEqual(first.TemplateId, second.TemplateId, "another picture");
        Assert.IsTrue(ctx.JournalFake.Records.Any(r => Equals(r.Fields["event"], "teahouse.altAnchor")));

        // both hidden: answered as not done with the reason, nothing more
        (handler, ctx, desk) = Load();
        ctx.UiProvider = () => OnTop(BoardUi);
        ctx.Runner.Script = p => Deferred(p.Id, "template.notFound:" + ((ClickAction)p.Steps.Single().Action).TemplateId);
        (presses, _) = Play(handler, ctx, desk, 10);
        Assert.IsFalse(presses.Single()!.Done);
        StringAssert.Contains(presses.Single()!.Code, "|template.notFound:");
        Assert.HasCount(2, Presses(ctx));

        // a press the engine deferred for another reason (the mouse moved) is not pressed again here
        (handler, ctx, desk) = Load();
        ctx.UiProvider = () => OnTop(BoardUi);
        ctx.Runner.Script = p => Deferred(p.Id, "ready.CursorMoving");
        (presses, _) = Play(handler, ctx, desk, 10);
        Assert.IsFalse(presses.Single()!.Done);
        Assert.HasCount(1, Presses(ctx));
    }

    [TestMethod]
    public void APressThatOpensAnotherWindow_EndsTheJob_ButTheEndScreenIsExpected()
    {
        var (handler, ctx, desk) = Load();
        var logged = new GridClickInfo(6, 6, 2, default, T.Pos(1100));
        ctx.UiProvider = () => Presses(ctx).Count == 0 ? OnTop(BoardUi) : OnTop("UI_TRIPLE_TOWN_SETTLE", 1200) with { LastGridClick = logged };
        var (presses, result) = Play(handler, ctx, desk, 35);
        Assert.AreEqual(new TeaPress(true, "pressed", 2), presses.Single(), "the press that fills the board shows the end screen");
        Assert.AreEqual(JobState.Succeeded, result.State);

        (handler, ctx, desk) = Load();
        ctx.UiProvider = () => Presses(ctx).Count == 0 ? OnTop(BoardUi) : OnTop("UI_SINGLE_SHOP", 1200) with { LastGridClick = logged };
        desk.AutoPlay = true;
        var job = Task.Run(() => handler.Execute(ctx, JobParameters.Empty, CancellationToken.None));
        var press = desk.Press(35, Wait, CancellationToken.None);
        Assert.IsTrue(job.Wait(Wait), "an unexpected window ends the job");
        Assert.AreEqual(new TeaPress(false, "press.openedWindow:UI_SINGLE_SHOP", 2), press);
        Assert.AreEqual((JobState.Failed, "teahouse.windowOpened:UI_SINGLE_SHOP"), (job.Result.State, job.Result.Code));
        Assert.IsFalse(desk.AutoPlay);
    }

    [TestMethod]
    public void Restart_IsAStub_UntilTheEndScreenIsCut()
    {
        var (handler, ctx, desk) = Load();
        ctx.UiProvider = () => OnTop("UI_TRIPLE_TOWN_SETTLE");
        var (presses, _) = Play(handler, ctx, desk, TeaHouseDesk.Restart);
        Assert.AreEqual(new TeaPress(false, "restart.needsSurvey"), presses.Single());
        Assert.IsEmpty(Presses(ctx), "nothing is pressed on the end screen");

        ctx.UiProvider = () => OnTop(BoardUi);
        (presses, _) = Play(handler, ctx, desk, TeaHouseDesk.Restart);
        Assert.AreEqual(new TeaPress(false, "notOnEndScreen:" + BoardUi), presses.Single());
    }

    [TestMethod]
    public void ThePressPoints_AreTheSquareCentres_OnTheBoard_AndClearOfEveryDenyZone()
    {
        var dir = Path.Combine(StaticRuleTests.CompanionRoot(), "features", "Companion.Feature.TeaHouse", "data");
        var anchors = JsonSerializer.Deserialize<TemplateDefinition[]>(File.ReadAllBytes(Path.Combine(dir, DataFileNames.Templates)), ContractJson.Options)!.ToDictionary(t => t.Id);
        var procedures = JsonSerializer.Deserialize<ProcedureDefinition[]>(File.ReadAllBytes(Path.Combine(dir, DataFileNames.Procedures)), ContractJson.Options)!;
        var board = procedures.Single(p => p.Id == "teahouse.board");
        var alt = procedures.Single(p => p.Id == "teahouse.board.alt");
        var map = JsonSerializer.Deserialize<UiMap>(File.ReadAllBytes(Path.Combine(dir, DataFileNames.UiMap)), ContractJson.Options)!;
        Assert.IsTrue(anchors.Values.All(t => t.Tags == TargetTags.ScreenAnchor && t.Gate == AutomationSwitch.None));
        Assert.HasCount(36, board.Steps);
        Assert.HasCount(36, alt.Steps);
        var ids = Enumerable.Range(0, 36).Select(i => "sq." + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        CollectionAssert.AreEqual(ids, board.Steps.Select(s => s.Id).ToArray());
        CollectionAssert.AreEqual(ids, alt.Steps.Select(s => s.Id).ToArray());

        // never an Esc reset: Esc closes the minigame (2026-10-30 live)
        Assert.IsTrue(procedures.All(p => p.Limits.MaxRestarts == 0), "no teahouse procedure resets with Esc");
        foreach (var step in board.Steps.Concat(alt.Steps))
        {
            var square = int.Parse(step.Id[3..], System.Globalization.CultureInfo.InvariantCulture);
            if (alt.Steps.Contains(step))
            {
                Assert.AreNotEqual(((ClickAction)board.Steps[square].Action).TemplateId, ((ClickAction)step.Action).TemplateId, step.Id + " has another picture");
            }
            Assert.AreEqual("sq." + square.ToString("00", System.Globalization.CultureInfo.InvariantCulture), step.Id);
            Assert.AreEqual((BoardUi, StepFlags.QuickAttribution), (step.Pre.TopUi, step.Flags), step.Id + ": the job checks the press itself (2026-10-31: the short window read)");
            Assert.AreEqual(Expectation.Nothing, step.Expect, step.Id);

            // the helper reader's square geometry (TeaGeometry: left 1023, top 155.5, pitch 130.8 x 130.5, size 127)
            var click = (ClickAction)step.Action;
            var at = anchors[click.TemplateId].ExpectedCenter.Offset(click.Offset!.Value.X, click.Offset.Value.Y);
            var centre = new PointI((int)Math.Round(1023 + (130.8 * (square % 6))) + 63, (int)Math.Round(155.5 + (130.5 * (square / 6))) + 63);
            Assert.AreEqual(centre, at, step.Id);
            Assert.IsTrue(at.X is > 1023 and < 1808 && at.Y is > 155 and < 938, step.Id);
            foreach (var zone in map.DenyZones)
            {
                Assert.IsFalse(zone.ClientRect.Inflate(HardRules.DenyZoneRefusePx).Contains(at), step.Id + " in " + zone.Id);

                // and beyond the guard distance, so the off-screen anchor check stays on (2026-10-30 live)
                var dx = Math.Max(Math.Max(zone.ClientRect.Left - at.X, 0), at.X - (zone.ClientRect.Right - 1));
                var dy = Math.Max(Math.Max(zone.ClientRect.Top - at.Y, 0), at.Y - (zone.ClientRect.Bottom - 1));
                Assert.IsTrue(Math.Sqrt((dx * dx) + (dy * dy)) > HardRules.DenyZoneGuardPx, step.Id + " near " + zone.Id);
            }
        }

        Assert.IsTrue(map.DenyZones.All(z => z.ScreenId == "teahouse"));
        Assert.AreEqual(BoardUi, map.Screens.Single().LogUiName);
        Assert.IsTrue(board.Limits.PatienceMs <= 20_000, "a press the engine holds up gives up within 20 s");
    }

    [TestMethod]
    public void TheDesk_WithdrawsAPressNoJobTook()
    {
        var desk = new TeaHouseDesk { AutoPlay = true };
        Assert.IsNull(desk.Press(3, TimeSpan.FromMilliseconds(200), CancellationToken.None));
        Assert.IsFalse(desk.JobRunning);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => desk.Press(36, Wait, CancellationToken.None));
    }
}
