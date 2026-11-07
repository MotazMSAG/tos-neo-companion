using Companion.App.Tray;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Host;
using Companion.Contracts.Safety;
using Companion.Feature.TeaHouse;

namespace Companion.App.Tests;

[TestClass]
public sealed class JobHostTests
{
    [TestMethod]
    public void Submit_RunsOnTheEngine_AndKeepsTheResult()
    {
        using var rig = new JobRig();
        var id = JobRig.NewId();
        var r = rig.Submit("Reset", id: id);
        Assert.IsTrue(r.Ok, r.Code);
        Assert.AreEqual("accepted", r.Code);
        Assert.AreEqual(JobState.Queued, r.Job!.State);

        Assert.IsTrue(rig.Host.RunPending());
        var res = rig.Host.Handle(new PipeRequest(PipeOp.Result, id));
        Assert.AreEqual(JobState.Succeeded, res.Job!.State);
        Assert.AreEqual("ok", res.Job.Code);
        Assert.AreEqual(1, rig.Handler.Calls);
        Assert.AreEqual("Reset", rig.Contexts.Runs.Single().JobName);
        Assert.AreEqual(JobKind.Navigation, rig.Contexts.Runs.Single().Kind);
        Assert.IsTrue(rig.Journal.Records.Any(j => j.Kind == JournalKind.JobAccepted));
        Assert.IsTrue(rig.Journal.Records.Any(j => j.Kind == JournalKind.JobFinished));
    }

    [TestMethod]
    public void Queue_IsOneQueuedPlusOneRunning()
    {
        using var rig = new JobRig();
        rig.Handler.Gate = new ManualResetEventSlim(false);
        Assert.IsTrue(rig.Submit("Reset").Ok);
        var engine = Task.Run(rig.Host.RunPending);
        Assert.IsTrue(rig.Handler.Entered.Wait(TimeSpan.FromSeconds(10)), "job started");
        Assert.AreEqual(HostState.Running, rig.Host.State);

        for (var i = 0; i < PipeProtocol.MaxQueued; i++)
        {
            Assert.IsTrue(rig.Submit("Reset").Ok, "the queue holds " + PipeProtocol.MaxQueued.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var third = rig.Submit("Reset");
        Assert.IsFalse(third.Ok);
        Assert.AreEqual("refused.queueFull", third.Code);

        rig.Handler.Gate.Set();
        Assert.IsTrue(engine.Wait(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(rig.Host.RunPending(), "the queued one runs next");
        Assert.IsTrue(rig.Submit("Reset").Ok, "room again");
    }

    [TestMethod]
    public void Rate_IsAJobLimitPerRollingHour_AndPerJobCaps()
    {
        // HardRules.MaxJobsPerHour (60 since 2026-10-18: a runaway guard, not a pace; the input pacing keeps the pace)
        using var rig = new JobRig();
        for (var i = 0; i < Companion.Contracts.Safety.HardRules.MaxJobsPerHour; i++)
        {
            var name = i < 2 ? "Scan" : "Reset";
            Assert.IsTrue(rig.Submit(name).Ok, "job " + i);
            Assert.IsTrue(rig.Host.RunPending());
            rig.Clock.Advance(TimeSpan.FromSeconds(50));
        }

        var oneMore = rig.Submit("Reset");
        Assert.AreEqual("refused.rate", oneMore.Code);

        rig.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.IsTrue(rig.Submit("Reset").Ok, "the first ones left the hour window");
        Assert.IsTrue(rig.Host.RunPending());

        // Scan has MaxPerHour 2.
        using var rig2 = new JobRig();
        Assert.IsTrue(rig2.Submit("Scan").Ok);
        Assert.IsTrue(rig2.Host.RunPending());
        Assert.IsTrue(rig2.Submit("Scan").Ok);
        Assert.IsTrue(rig2.Host.RunPending());
        Assert.AreEqual("refused.rate.job", rig2.Submit("Scan").Code);
        Assert.IsTrue(rig2.Submit("Reset").Ok, "other jobs are not affected");
    }

    [TestMethod]
    public void Refusals_HaveCodes_AndNeverRunAnything()
    {
        using var rig = new JobRig();
        Assert.AreEqual("refused.whitelist", rig.Submit("Buy").Code);
        Assert.AreEqual("refused.switch", rig.Submit("Craft").Code, "the switch is checked first");
        Assert.AreEqual("refused.switch", rig.Submit("Craft", new Dictionary<string, string> { ["qty"] = "5" }).Code);
        rig.Switches.Crafting = true;
        Assert.AreEqual("refused.params:param.missing:qty", rig.Submit("Craft").Code);
        Assert.AreEqual("refused.params:param.range:qty", rig.Submit("Craft", new Dictionary<string, string> { ["qty"] = "1000" }).Code);
        Assert.AreEqual("refused.params:param.unknown", rig.Submit("Reset", new Dictionary<string, string> { ["x"] = "1" }).Code);
        for (var i = 0; i < PipeProtocol.MaxQueued; i++)
        {
            Assert.IsTrue(rig.Submit("Reset").Ok);
        }

        Assert.AreEqual("refused.queueFull", rig.Submit("Craft", new Dictionary<string, string> { ["qty"] = "5" }).Code, "the queue is full");
        Assert.AreEqual(0, rig.Handler.Calls);
        Assert.IsTrue(rig.Journal.Records.Count(r => r.Kind == JournalKind.JobRefused) >= 7);
        Assert.IsTrue(rig.Journal.Records.Where(r => r.Kind == JournalKind.JobRefused).All(r => !r.Fields.Values.Contains("Buy")), "the caller's unknown name is never journaled");
    }

    [TestMethod]
    public void ACommitJob_WithNoCharacterKnown_IsAccepted_AndRunsWithTheProfile()
    {
        // 4.33 (one knowledge profile, single-user design): no character binding or permission;
        // the job host does not ask which character is logged in, only the job's switch gates a commit job
        using var rig = new JobRig();
        var qty = new Dictionary<string, string> { ["qty"] = "3" };
        Assert.AreEqual("refused.switch", rig.Submit("Craft", qty).Code, "the switch is still the gate");
        rig.Switches.Crafting = true;
        var id = JobRig.NewId();
        Assert.IsTrue(rig.Submit("Craft", qty, id).Ok);
        Assert.IsTrue(rig.Host.RunPending());
        Assert.AreEqual(JobState.Succeeded, rig.Host.Handle(new PipeRequest(PipeOp.Result, id)).Job!.State);
        Assert.AreEqual(JobKind.Commit, rig.Contexts.Runs.Single().Kind);
        Assert.AreSame(rig.Knowledge.ProfileFake, rig.Contexts.Knowledge.Single(), "every job gets the one profile");
        var accepted = rig.Journal.Records.Single(j => j.Kind == JournalKind.JobAccepted);
        CollectionAssert.AreEquivalent(new[] { "job", "kind" }, accepted.Fields.Keys.ToArray(), "no character is journaled");
    }

    [TestMethod]
    public void AJob_WhenTheStoreCannotOpenTheProfile_RunsWithNoKnowledge()
    {
        // the handler decides (jobs that need the store refuse "knowledge.unavailable"); the host never throws
        using var rig = new JobRig();
        rig.Knowledge.Broken = true;
        Assert.IsTrue(rig.Submit("Reset").Ok);
        Assert.IsTrue(rig.Host.RunPending());
        Assert.AreEqual(1, rig.Handler.Calls);
        Assert.IsNull(rig.Contexts.Knowledge.Single());
    }

    [TestMethod]
    public void HostStates_GateSubmissions()
    {
        using var rig = new JobRig();
        rig.Trust.BuildTrusted = false;
        Assert.AreEqual(HostState.Untrusted, rig.Host.State);
        Assert.AreEqual("refused.untrusted", rig.Submit("Reset").Code);
        rig.Trust.BuildTrusted = true;

        rig.Halt.Halt(new HaltReport(HaltCode.WorldPress, null, "j", "p", null, DateTimeOffset.UnixEpoch, null));
        Assert.AreEqual(HostState.Halted, rig.Host.State);
        Assert.AreEqual("refused.halted", rig.Submit("Reset").Code);
        Assert.AreEqual("refused.stopOrHalt", rig.Host.Handle(new PipeRequest(PipeOp.Resume)).Code);
        Assert.IsTrue(rig.Halt.IsHalted, "a pipe submission never clears the latch");
        rig.Halt.Clear("test");

        rig.Pause.Pause("test");
        Assert.AreEqual(HostState.Paused, rig.Host.State);
        Assert.IsTrue(rig.Submit("Reset").Ok, "a paused host still queues");
        Assert.AreEqual("resumed", rig.Host.Handle(new PipeRequest(PipeOp.Resume)).Code);
        Assert.IsFalse(rig.Pause.IsPaused);
        Assert.AreEqual("paused", rig.Host.Handle(new PipeRequest(PipeOp.Pause)).Code);
        Assert.IsTrue(rig.Pause.IsPaused);
        rig.Pause.Resume("test");

        var stop = rig.Host.Handle(new PipeRequest(PipeOp.Stop));
        Assert.IsTrue(stop.Ok);
        Assert.AreEqual(StopSource.Pipe, rig.Stop.Current!.Source);
        Assert.AreEqual(HostState.Stopped, rig.Host.State);
        Assert.AreEqual("refused.stopped", rig.Submit("Reset").Code);
        Assert.AreEqual("refused.stopOrHalt", rig.Host.Handle(new PipeRequest(PipeOp.Resume)).Code);
        Assert.IsFalse(rig.Host.RunPending(), "the queued job was dropped by the stop");
        Assert.AreEqual(0, rig.Handler.Calls);

        var blocked = new JobRig();
        blocked.Host.MarkStarted("core.procedures.dropped:Reset");
        Assert.AreEqual(HostState.Untrusted, blocked.Host.State);
        Assert.AreEqual("refused.untrusted", blocked.Submit("Reset").Code);
        blocked.Dispose();
    }

    [TestMethod]
    public void Cancel_QueuedRunningAndUnknown()
    {
        using var rig = new JobRig();
        rig.Handler.Gate = new ManualResetEventSlim(false);
        var running = JobRig.NewId();
        var queued = JobRig.NewId();
        Assert.IsTrue(rig.Submit("Reset", id: running).Ok);
        var engine = Task.Run(rig.Host.RunPending);
        Assert.IsTrue(rig.Handler.Entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(rig.Submit("Reset", id: queued).Ok);

        var c1 = rig.Host.Handle(new PipeRequest(PipeOp.Cancel, queued));
        Assert.AreEqual("cancelled", c1.Code);
        Assert.AreEqual(JobState.Cancelled, c1.Job!.State);

        var c2 = rig.Host.Handle(new PipeRequest(PipeOp.Cancel, running));
        Assert.AreEqual("cancel.requested", c2.Code);
        Assert.IsTrue(engine.Wait(TimeSpan.FromSeconds(10)));
        var res = rig.Host.Handle(new PipeRequest(PipeOp.Result, running)).Job!;
        Assert.AreEqual(JobState.Cancelled, res.State);
        Assert.AreEqual("cancelled.byClient", res.Code);

        Assert.AreEqual("notFound", rig.Host.Handle(new PipeRequest(PipeOp.Cancel, JobRig.NewId())).Code);
        Assert.AreEqual("notFound", rig.Host.Handle(new PipeRequest(PipeOp.Result, JobRig.NewId())).Code);
        Assert.AreEqual("alreadyFinished", rig.Host.Handle(new PipeRequest(PipeOp.Cancel, running)).Code);
        Assert.AreEqual("refused.duplicateId", rig.Submit("Reset", id: running).Code);
    }

    [TestMethod]
    public void TheHeartbeat_NeverCancelsARunningJob_WhoeverIsLoggedIn()
    {
        // 4.33: the heartbeat used to cancel a running job when the log's character changed ("character.changed"); with
        // one knowledge profile for every character it leaves the job alone, and a queued job starts as accepted
        using var rig = new JobRig();
        rig.Handler.Gate = new ManualResetEventSlim(false);
        var id = JobRig.NewId();
        Assert.IsTrue(rig.Submit("Reset", id: id).Ok);
        var engine = Task.Run(rig.Host.RunPending);
        Assert.IsTrue(rig.Handler.Entered.Wait(TimeSpan.FromSeconds(10)));
        var queued = JobRig.NewId();
        Assert.IsTrue(rig.Submit("Reset", id: queued).Ok);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Host.Beat();
        Assert.AreEqual(JobState.Running, rig.Host.RunningJob!.State, "still running after the heartbeat");
        rig.Handler.Gate.Set();
        Assert.IsTrue(engine.Wait(TimeSpan.FromSeconds(10)));
        Assert.AreEqual((JobState.Succeeded, "ok"), Outcome(rig, id));

        Assert.IsTrue(rig.Host.RunPending());
        Assert.AreEqual((JobState.Succeeded, "ok"), Outcome(rig, queued));
        Assert.AreEqual(2, rig.Handler.Calls);
    }

    [TestMethod]
    public void HandlerFailures_AndFeatureHalts()
    {
        using var rig = new JobRig();
        rig.Handler.Throw = new InvalidOperationException("boom with free text");
        var id = JobRig.NewId();
        Assert.IsTrue(rig.Submit("Reset", id: id).Ok);
        Assert.IsTrue(rig.Host.RunPending());
        var failed = rig.Host.Handle(new PipeRequest(PipeOp.Result, id)).Job!;
        Assert.AreEqual(JobState.Failed, failed.State);
        Assert.AreEqual("InternalError", failed.Code);

        rig.Handler.Throw = null;
        rig.Handler.Result = new JobResult(JobState.Halted, "CraftStoppedEarlyTwice", new Dictionary<string, object?> { ["ticks"] = 3, ["note"] = "free text!" });
        var id2 = JobRig.NewId();
        Assert.IsTrue(rig.Submit("Reset", id: id2).Ok);
        Assert.IsTrue(rig.Host.RunPending());
        Assert.IsTrue(rig.Halt.IsHalted, "the host latches a feature-decided HALT");
        Assert.AreEqual(HaltCode.CraftStoppedEarlyTwice, rig.Halt.Current!.Code);
        Assert.IsTrue(rig.Stop.IsStopRequested);
        var halted = rig.Host.Handle(new PipeRequest(PipeOp.Result, id2)).Job!;
        Assert.AreEqual(JobState.Halted, halted.State);
        Assert.AreEqual(3L, halted.Data!["ticks"]);
        Assert.AreEqual("value.redacted", halted.Data!["note"]);
    }

    [TestMethod]
    public void Results_KeepTheLast50()
    {
        using var rig = new JobRig();
        var ids = new List<string>();
        for (var i = 0; i < 55; i++)
        {
            var id = JobRig.NewId();
            ids.Add(id);
            Assert.IsTrue(rig.Submit("Reset", id: id).Ok, "job " + i);
            Assert.IsTrue(rig.Host.RunPending());
            rig.Clock.Advance(TimeSpan.FromMinutes(7)); // stay under 10 per hour
        }

        Assert.AreEqual("notFound", rig.Host.Handle(new PipeRequest(PipeOp.Result, ids[0])).Code);
        Assert.AreEqual("notFound", rig.Host.Handle(new PipeRequest(PipeOp.Result, ids[4])).Code);
        Assert.IsTrue(rig.Host.Handle(new PipeRequest(PipeOp.Result, ids[5])).Ok);
        Assert.IsTrue(rig.Host.Handle(new PipeRequest(PipeOp.Result, ids[54])).Ok);
    }

    [TestMethod]
    public void Status_ReportsHeartbeatGameAndWhitelist()
    {
        using var rig = new JobRig();
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        rig.Host.Beat();
        var st = rig.Host.Handle(new PipeRequest(PipeOp.Status)).Status!;
        Assert.AreEqual(HostState.Idle, st.State);
        Assert.AreEqual(rig.Clock.UtcNow, st.HeartbeatUtc);
        Assert.IsFalse(st.GameConnected);
        CollectionAssert.AreEquivalent(new[] { "Reset", "Craft", "Scan" }, st.Jobs.ToArray());
        rig.GameConnected = true;
        Assert.IsTrue(rig.Host.Handle(new PipeRequest(PipeOp.Status)).Status!.GameConnected);

        var starting = new JobRig();
        var fresh = new Companion.App.Jobs.JobHost(new Companion.App.Jobs.JobHostDeps(starting.Catalog, starting.Clock, starting.Stop, starting.Pause, starting.Halt,
            starting.Engine, starting.Journal, starting.Trust, starting.Knowledge, starting.Switches, () => false, starting.Contexts));
        Assert.AreEqual(HostState.Starting, fresh.State);
        Assert.AreEqual("refused.starting", fresh.Handle(new PipeRequest(PipeOp.Submit, JobRig.NewId(), "Reset")).Code);
        fresh.Dispose();
        starting.Dispose();
    }

    [TestMethod]
    public void EngineThread_RunsJobsAndStopsCleanly()
    {
        using var rig = new JobRig();
        rig.Host.StartEngine();
        var id = JobRig.NewId();
        Assert.IsTrue(rig.Submit("Reset", id: id).Ok);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (rig.Host.Handle(new PipeRequest(PipeOp.Result, id)).Job!.State != JobState.Succeeded && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        Assert.AreEqual(JobState.Succeeded, rig.Host.Handle(new PipeRequest(PipeOp.Result, id)).Job!.State);
        Assert.IsTrue(rig.Host.StopEngine(TimeSpan.FromSeconds(5)));
    }

    // ------------------------------------------------------------------ tray helpers submit through the job host

    [TestMethod]
    public void RuleMode_StartsTheRuleJob_WithNoCharacterKnown()
    {
        // 4.33: the keeper waited for a known, Allowed character and bound its submission to it; now an Idle host, a
        // connected game and the job's switch are enough, and the job runs with the one profile
        using var rig = new JobRig { GameConnected = true };
        rig.Catalog.Add(new JobDefinition(RuleModeKeeper.JobName, "crafting", "rules", [], AutomationSwitch.Crafting, JobKind.Commit, 10), rig.Handler);
        var dir = Path.Combine(AppContext.BaseDirectory, "tmp", "rulemode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var keeper = new RuleModeKeeper(rig.Host, dir);
            Assert.AreEqual("refused: refused.switch", keeper.Set(true), "a commit job with its switch off is still refused");
            rig.Switches.Crafting = true;
            Assert.AreEqual("started", keeper.Set(true));
            Assert.IsTrue(rig.Host.RunPending());
            Assert.AreEqual(RuleModeKeeper.JobName, rig.Contexts.Runs.Single().JobName);
            Assert.AreSame(rig.Knowledge.ProfileFake, rig.Contexts.Knowledge.Single());
            Assert.AreEqual("off", keeper.Set(false));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void TeaHouseHands_StartsThePlayJob_WithNoCharacterKnown()
    {
        // 4.33: the helper's submission carries no character; the job runs with the one profile
        using var rig = new JobRig();
        rig.Catalog.Add(new JobDefinition(TeaHouseFeature.PlayJob, "teahouse", "play", [], AutomationSwitch.None, JobKind.Navigation, 30), rig.Handler);
        using var hands = new TeaHouseHands(rig.Host, new TeaHouseDesk(), rig.Stop, rig.Halt) { On = true };
        using var gaveUp = new CancellationTokenSource();
        gaveUp.Cancel(); // the helper stops waiting at once: the press is withdrawn, the started job stays queued

        Assert.AreEqual(new TeaPress(false, "job.noAnswer"), hands.Press(0, gaveUp.Token));
        CollectionAssert.AreEqual(new[] { TeaHouseFeature.PlayJob }, rig.Host.QueuedJobNames.ToArray());
        Assert.IsTrue(rig.Host.RunPending());
        Assert.AreEqual(TeaHouseFeature.PlayJob, rig.Contexts.Runs.Single().JobName);
        Assert.AreSame(rig.Knowledge.ProfileFake, rig.Contexts.Knowledge.Single());
    }

    private static (JobState State, string Code) Outcome(JobRig rig, string id)
    {
        var job = rig.Host.Handle(new PipeRequest(PipeOp.Result, id)).Job!;
        return (job.State, job.Code);
    }
}
