using Companion.Cli;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Host;

namespace Companion.App.Tests;

[TestClass]
public sealed class CliArgsTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void Commands_Parse()
    {
        Assert.AreEqual(CliCommand.Status, CliArgs.Parse(["status"], out _)!.Command);
        Assert.AreEqual(CliCommand.Jobs, CliArgs.Parse(["JOBS"], out _)!.Command);
        Assert.AreEqual(CliCommand.Stop, CliArgs.Parse(["stop"], out _)!.Command);
        Assert.AreEqual(CliCommand.Pause, CliArgs.Parse(["pause"], out _)!.Command);
        Assert.AreEqual(CliCommand.Resume, CliArgs.Parse(["resume"], out _)!.Command);
        Assert.AreEqual(Id, CliArgs.Parse(["result", Id], out _)!.JobId);
        Assert.AreEqual(CliCommand.Cancel, CliArgs.Parse(["cancel", Id], out _)!.Command);

        var s = CliArgs.Parse(["submit", "Craft", "item=Iron Ore", "qty=5", "--wait", "--timeout", "30"], out var err);
        Assert.IsNotNull(s, err);
        Assert.AreEqual(CliCommand.Submit, s.Command);
        Assert.AreEqual("Craft", s.JobName);
        Assert.AreEqual("Iron Ore", s.Params["item"]);
        Assert.AreEqual("5", s.Params["qty"]);
        Assert.IsTrue(s.Wait);
        Assert.AreEqual(TimeSpan.FromSeconds(30), s.Timeout);

        var plain = CliArgs.Parse(["submit", "Reset"], out _)!;
        Assert.IsFalse(plain.Wait);
        Assert.AreEqual(0, plain.Params.Count);
        Assert.AreEqual(CliArgs.DefaultTimeout, plain.Timeout);

        // The CraftLoop command line documented for the user (a long job: wait up to a day, or poll with 'result').
        var loop = CliArgs.Parse(["submit", "CraftLoop", "slot=1", "count=20", "batch=max", "--wait", "--timeout", "86400"], out err);
        Assert.IsNotNull(loop, err);
        Assert.AreEqual(("CraftLoop", "1", "20", "max"), (loop.JobName, loop.Params["slot"], loop.Params["count"], loop.Params["batch"]));
        Assert.AreEqual(TimeSpan.FromDays(1), loop.Timeout);
    }

    [TestMethod]
    [DataRow(new string[0], "usage")]
    [DataRow(new[] { "reload" }, "usage.command")]
    [DataRow(new[] { "status", "x" }, "usage.extraArguments")]
    [DataRow(new[] { "result" }, "usage.jobId")]
    [DataRow(new[] { "result", "0123" }, "usage.jobId")]
    [DataRow(new[] { "cancel", "0123456789ABCDEF0123456789ABCDEF" }, "usage.jobId")]
    [DataRow(new[] { "submit" }, "usage.jobName")]
    [DataRow(new[] { "submit", "../evil" }, "usage.jobName")]
    [DataRow(new[] { "submit", "Craft", "qty" }, "usage.param")]
    [DataRow(new[] { "submit", "Craft", "=5" }, "usage.param")]
    [DataRow(new[] { "submit", "Craft", "qty=" }, "usage.param")]
    [DataRow(new[] { "submit", "Craft", "1qty=5" }, "usage.param")]
    [DataRow(new[] { "submit", "Craft", "qty=5", "qty=6" }, "usage.duplicate")]
    [DataRow(new[] { "submit", "Craft", "--wait", "--wait" }, "usage.duplicate")]
    [DataRow(new[] { "submit", "Craft", "--wait", "--timeout" }, "usage.timeout")]
    [DataRow(new[] { "submit", "Craft", "--wait", "--timeout", "0" }, "usage.timeout")]
    [DataRow(new[] { "submit", "Craft", "--wait", "--timeout", "-5" }, "usage.timeout")]
    [DataRow(new[] { "submit", "Craft", "--timeout", "5" }, "usage.timeoutWithoutWait")]
    public void BadArguments_GiveAUsageCode(string[] args, string expected)
    {
        Assert.IsNull(CliArgs.Parse(args, out var err));
        Assert.AreEqual(expected, err);
    }

    [TestMethod]
    public void OverlongValuesAndTooManyParams_AreRefused()
    {
        Assert.IsNull(CliArgs.Parse(["submit", "Craft", "item=" + new string('x', 65)], out var err));
        Assert.AreEqual("usage.param", err);
        var many = new List<string> { "submit", "Craft" };
        many.AddRange(Enumerable.Range(0, 17).Select(i => "p" + i + "=1"));
        Assert.IsNull(CliArgs.Parse(many, out err));
        Assert.AreEqual("usage.tooManyParams", err);
    }
}

[TestClass]
public sealed class CliRunnerTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    private sealed class FakeTransport : IPipeTransport
    {
        public List<PipeRequest> Sent { get; } = [];

        public Func<PipeRequest, PipeResponse?> Reply { get; set; } = _ => null;

        public string Error { get; set; } = "host.notRunning";

        public PipeResponse? Send(PipeRequest request, out string? error)
        {
            Sent.Add(request);
            var r = Reply(request);
            error = r is null ? Error : null;
            return r;
        }
    }

    private sealed class Rig
    {
        public Rig()
        {
            Runner = new CliRunner(Transport, Out, Err, () => Now, () => Mono, d => Mono += d, () => Id);
        }

        public FakeTransport Transport { get; } = new();

        public StringWriter Out { get; } = new();

        public StringWriter Err { get; } = new();

        public DateTimeOffset Now { get; set; } = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

        public TimeSpan Mono { get; set; }

        public CliRunner Runner { get; }
    }

    private static HostStatus Status(HostState state, DateTimeOffset heartbeat) =>
        new(state, heartbeat, new EngineStatus(EngineState.Idle, null, null, null, null, heartbeat), null, true, ["Reset", "OpenBagKey"]);

    private static JobStatusInfo Job(JobState state, string code = "ok") =>
        new(Id, "Reset", state, code, DateTimeOffset.UnixEpoch, null, null);

    [TestMethod]
    public void HostNotRunning_Is3()
    {
        var rig = new Rig();
        Assert.AreEqual(ExitCodes.HostNotRunning, rig.Runner.Run(["status"]));
        Assert.AreEqual(ExitCodes.HostNotRunning, rig.Runner.Run(["submit", "Reset"]));
        Assert.AreEqual(ExitCodes.HostNotRunning, rig.Runner.Run(["stop"]));
        StringAssert.Contains(rig.Out.ToString(), "host.notRunning");
    }

    [TestMethod]
    public void Status_Heartbeat_AndHalt()
    {
        var rig = new Rig();
        rig.Transport.Reply = _ => new PipeResponse(true, "ok", Status(HostState.Idle, rig.Now.AddSeconds(-1)));
        Assert.AreEqual(ExitCodes.Ok, rig.Runner.Run(["status"]));
        StringAssert.Contains(rig.Out.ToString(), "state=Idle");
        StringAssert.Contains(rig.Out.ToString(), "game=connected halt=- jobs=2");
        Assert.IsFalse(rig.Out.ToString().Contains("character", StringComparison.Ordinal), "4.33: no character, no permission");

        rig.Transport.Reply = _ => new PipeResponse(true, "ok", Status(HostState.Idle, rig.Now.AddSeconds(-6)));
        Assert.AreEqual(ExitCodes.HostNotRunning, rig.Runner.Run(["status"]), "a heartbeat older than 5 s means the host is dead");

        rig.Transport.Reply = _ => new PipeResponse(true, "ok", Status(HostState.Halted, rig.Now));
        Assert.AreEqual(ExitCodes.Halted, rig.Runner.Run(["status"]));

        rig.Transport.Reply = _ => new PipeResponse(true, "ok", Status(HostState.Idle, rig.Now));
        Assert.AreEqual(ExitCodes.Ok, rig.Runner.Run(["jobs"]));
        StringAssert.Contains(rig.Out.ToString(), "OpenBagKey");
        Assert.IsTrue(rig.Transport.Sent.All(r => r.Op == PipeOp.Status));
    }

    [TestMethod]
    public void Submit_WithoutWait_And_Refused()
    {
        var rig = new Rig();
        rig.Transport.Reply = _ => new PipeResponse(true, "accepted", null, Job(JobState.Queued));
        Assert.AreEqual(ExitCodes.Ok, rig.Runner.Run(["submit", "Craft", "qty=2"]));
        var sent = rig.Transport.Sent.Single();
        Assert.AreEqual(PipeOp.Submit, sent.Op);
        Assert.AreEqual(Id, sent.JobId);
        Assert.AreEqual("Craft", sent.JobName);
        Assert.AreEqual("2", sent.Params!["qty"]);
        StringAssert.Contains(rig.Out.ToString(), "accepted id=" + Id);

        rig.Transport.Reply = _ => new PipeResponse(false, "refused.switch");
        Assert.AreEqual(ExitCodes.Refused, rig.Runner.Run(["submit", "Craft", "qty=2"]));
        StringAssert.Contains(rig.Out.ToString(), "refused.switch");
    }

    [TestMethod]
    [DataRow(JobState.Succeeded, 0)]
    [DataRow(JobState.NeedsSurvey, 6)]
    [DataRow(JobState.Halted, 4)]
    [DataRow(JobState.Refused, 2)]
    [DataRow(JobState.Failed, 1)]
    [DataRow(JobState.Cancelled, 1)]
    [DataRow(JobState.Stopped, 1)]
    [DataRow(JobState.GameExited, 1)]
    public void SubmitWait_MapsTheFinalState(JobState final, int expected)
    {
        var rig = new Rig();
        var polls = 0;
        rig.Transport.Reply = r => r.Op == PipeOp.Submit
            ? new PipeResponse(true, "accepted", null, Job(JobState.Queued))
            : new PipeResponse(true, "ok", null, ++polls < 3 ? Job(JobState.Running) : Job(final, "code.x"));
        Assert.AreEqual(expected, rig.Runner.Run(["submit", "Reset", "--wait"]));
        Assert.AreEqual(3, polls);
        StringAssert.Contains(rig.Out.ToString(), "state=" + final);
    }

    [TestMethod]
    public void SubmitWait_TimesOut_With5()
    {
        var rig = new Rig();
        rig.Transport.Reply = r => r.Op == PipeOp.Submit
            ? new PipeResponse(true, "accepted", null, Job(JobState.Queued))
            : new PipeResponse(true, "ok", null, Job(JobState.Parked));
        Assert.AreEqual(ExitCodes.Timeout, rig.Runner.Run(["submit", "Reset", "--wait", "--timeout", "3"]));
        StringAssert.Contains(rig.Out.ToString(), "timeout id=" + Id);
        Assert.IsTrue(rig.Mono >= TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public void Result_Cancel_Stop_Pause_Resume()
    {
        var rig = new Rig();
        rig.Transport.Reply = _ => new PipeResponse(false, "notFound");
        Assert.AreEqual(ExitCodes.Failed, rig.Runner.Run(["result", Id]));

        rig.Transport.Reply = _ => new PipeResponse(true, "ok", null, Job(JobState.NeedsSurvey, "reader.needsSurvey:bag.grid"));
        Assert.AreEqual(ExitCodes.NeedsSurvey, rig.Runner.Run(["result", Id]));
        StringAssert.Contains(rig.Out.ToString(), "code=reader.needsSurvey:bag.grid");

        rig.Transport.Reply = _ => new PipeResponse(true, "cancelled");
        Assert.AreEqual(ExitCodes.Ok, rig.Runner.Run(["cancel", Id]));
        rig.Transport.Reply = _ => new PipeResponse(true, "stopped");
        Assert.AreEqual(ExitCodes.Ok, rig.Runner.Run(["stop"]));
        rig.Transport.Reply = _ => new PipeResponse(true, "paused");
        Assert.AreEqual(ExitCodes.Ok, rig.Runner.Run(["pause"]));
        rig.Transport.Reply = _ => new PipeResponse(false, "refused.stopOrHalt");
        Assert.AreEqual(ExitCodes.Refused, rig.Runner.Run(["resume"]));

        CollectionAssert.AreEqual(
            new[] { PipeOp.Result, PipeOp.Result, PipeOp.Cancel, PipeOp.Stop, PipeOp.Pause, PipeOp.Resume },
            rig.Transport.Sent.Select(r => r.Op).ToArray());
    }

    [TestMethod]
    public void Usage_Is1_AndNothingIsSent()
    {
        var rig = new Rig();
        Assert.AreEqual(ExitCodes.Failed, rig.Runner.Run(["frobnicate"]));
        Assert.AreEqual(0, rig.Transport.Sent.Count);
        StringAssert.Contains(rig.Err.ToString(), "usage");
    }
}
