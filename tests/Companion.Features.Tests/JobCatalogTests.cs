using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;

namespace Companion.Features.Tests;

[TestClass]
public sealed class JobCatalogTests
{
    private static (FeatureHost Host, JobDefinition Job) HostWithParamJob()
    {
        var env = new TestEnv();
        var host = new FeatureHost(env.Clock);
        var job = new JobDefinition(
            "Probe",
            "probe",
            "x",
            [
                new JobParamSpec("item", JobParamType.ItemKey, false),
                new JobParamSpec("qty", JobParamType.Int, false, 1, 999, MaxLength: 4),
                new JobParamSpec("mode", JobParamType.Enum, false, Allowed: ["fast", "safe"]),
                new JobParamSpec("full", JobParamType.Bool, false),
            ],
            AutomationSwitch.None,
            JobKind.Navigation);
        host.LoadAll([new TestFeature("probe", register: r => r.AddJob(job, new OkHandler()))], env.Context);
        return (host, T.Definition(host, "Probe"));
    }

    private static FakeKnowledge KnowledgeWithPotion()
    {
        var k = new FakeKnowledge();
        k.Recipes.Add(new Recipe("small potion|lifeskill|0", new ItemKey("small potion"), 1, "lifeskill", [new MaterialRequirement(new ItemKey("herb"), 2)], [], null, DateTimeOffset.UnixEpoch, null, true, RecipeConfidence.Complete, null, null));
        return k;
    }

    [TestMethod]
    public void IntParamsAreTypedAndBounded()
    {
        var (host, job) = HostWithParamJob();
        string? reason;
        Assert.AreEqual(5L, host.Jobs.Validate(job, new Dictionary<string, string> { ["qty"] = "5" }, null, out reason)!.GetInt("qty"));
        Assert.IsNull(reason);
        Assert.IsNull(host.Jobs.Validate(job, new Dictionary<string, string> { ["qty"] = "0" }, null, out reason));
        Assert.AreEqual("param.range:qty", reason);
        Assert.IsNull(host.Jobs.Validate(job, new Dictionary<string, string> { ["qty"] = "1000" }, null, out reason));
        Assert.AreEqual("param.range:qty", reason);
        Assert.IsNull(host.Jobs.Validate(job, new Dictionary<string, string> { ["qty"] = "12a" }, null, out reason));
        Assert.AreEqual("param.type:qty", reason);
        Assert.IsNull(host.Jobs.Validate(job, new Dictionary<string, string> { ["qty"] = "99999" }, null, out reason));
        Assert.AreEqual("param.length:qty", reason);
    }

    [TestMethod]
    public void EnumAndBoolParamsAcceptOnlyTheirValues()
    {
        var (host, job) = HostWithParamJob();
        var ok = host.Jobs.Validate(job, new Dictionary<string, string> { ["mode"] = "safe", ["full"] = "TRUE" }, null, out var reason);
        Assert.IsNull(reason);
        Assert.AreEqual("safe", ok!.GetEnum("mode"));
        Assert.IsTrue(ok.GetBool("full"));
        Assert.IsNull(host.Jobs.Validate(job, new Dictionary<string, string> { ["mode"] = "SAFE" }, null, out reason));
        Assert.AreEqual("param.enum:mode", reason);
        Assert.IsNull(host.Jobs.Validate(job, new Dictionary<string, string> { ["full"] = "yes" }, null, out reason));
        Assert.AreEqual("param.type:full", reason);
    }

    [TestMethod]
    public void ItemKeysMustBeKnownToTheCharactersStore()
    {
        var (host, job) = HostWithParamJob();
        var knowledge = KnowledgeWithPotion();
        var ok = host.Jobs.Validate(job, new Dictionary<string, string> { ["item"] = "  Small   Potion " }, knowledge, out var reason);
        Assert.IsNull(reason);
        Assert.AreEqual(new ItemKey("small potion"), ok!.GetItem("item"));
        Assert.IsNotNull(host.Jobs.Validate(job, new Dictionary<string, string> { ["item"] = "herb" }, knowledge, out _), "a recipe material is known");
        Assert.IsNull(host.Jobs.Validate(job, new Dictionary<string, string> { ["item"] = "dragon scale" }, knowledge, out reason));
        Assert.AreEqual("param.item.unknown:item", reason);
        Assert.IsNull(host.Jobs.Validate(job, new Dictionary<string, string> { ["item"] = "herb" }, null, out reason));
        Assert.AreEqual("param.item.noKnowledge:item", reason);
    }

    [TestMethod]
    public void UnknownMissingOverlongAndControlCharacterParamsAreRefused()
    {
        var (host, job) = HostWithParamJob();
        Assert.IsNull(host.Jobs.Validate(job, new Dictionary<string, string> { ["path"] = "C:\\x" }, null, out var reason));
        Assert.AreEqual("param.unknown", reason);
        Assert.IsNull(host.Jobs.Validate(job, new Dictionary<string, string> { ["mode"] = "fa\nst" }, null, out reason));
        Assert.AreEqual("param.chars:mode", reason);
        Assert.IsNull(host.Jobs.Validate(job, new Dictionary<string, string> { ["mode"] = new string('a', 65) }, null, out reason));
        Assert.AreEqual("param.length:mode", reason);
        var many = Enumerable.Range(0, 17).ToDictionary(i => "p" + i, _ => "1");
        Assert.IsNull(host.Jobs.Validate(job, many, null, out reason));
        Assert.AreEqual("param.tooMany", reason);

        var (realHost, _, _) = T.LoadReal();
        var craft = T.Definition(realHost, "Craft");
        Assert.IsNull(realHost.Jobs.Validate(craft, new Dictionary<string, string> { ["item"] = "small potion" }, KnowledgeWithPotion(), out reason));
        Assert.AreEqual("param.missing:qty", reason);
        Assert.IsNotNull(realHost.Jobs.Validate(craft, new Dictionary<string, string> { ["item"] = "small potion", ["qty"] = "3" }, KnowledgeWithPotion(), out reason));
        Assert.IsNull(reason);
        Assert.AreEqual(0, realHost.Jobs.Validate(T.Definition(realHost, "Reset"), new Dictionary<string, string>(), null, out _)!.Values.Count);
    }

    [TestMethod]
    public void AThrowingHandlerFailsItsJobAndTheThirdThrowFaultsTheFeature()
    {
        var env = new TestEnv();
        var host = new FeatureHost(env.Clock);
        host.LoadAll(
        [
            new TestFeature("flaky", register: r =>
            {
                r.AddJob(new JobDefinition("FlakyA", "flaky", "x", [], AutomationSwitch.None, JobKind.Navigation), new ThrowingHandler());
                r.AddJob(new JobDefinition("FlakyB", "flaky", "x", [], AutomationSwitch.None, JobKind.Navigation), new OkHandler());
            }),
            new TestFeature("steady"),
        ], env.Context);
        var ctx = new FakeJobContext(env.Procedures);
        var handler = T.Handler(host, "FlakyA");

        for (var i = 1; i <= 3; i++)
        {
            var r = handler.Execute(ctx, JobParameters.Empty, CancellationToken.None);
            Assert.AreEqual((JobState.Failed, "InternalError"), (r.State, r.Code), i.ToString());
            Assert.AreEqual("InvalidOperationException", r.Data!["exception"]);
            Assert.IsFalse(r.Data.Values.OfType<string>().Any(s => s.Contains("Bob", StringComparison.Ordinal)), "no free text from the exception");
            Assert.AreEqual(i < 3, host.Jobs.TryGet("FlakyB", out _, out _), "faulted after the third throw");
        }

        Assert.AreEqual(FeatureLoadStatus.Faulted, host.Reports.Single(r => r.FeatureId == "flaky").Status);
        Assert.IsFalse(host.Jobs.All.Any(j => j.FeatureId == "flaky"));
        Assert.IsTrue(host.Jobs.TryGet("Jobsteady", out _, out _));
        Assert.IsTrue(env.Journal.Records.Any(r => r.Kind == JournalKind.FeatureLoad && Equals(r.Fields.GetValueOrDefault("status"), "Faulted")));
        Assert.AreEqual(3, env.Journal.Records.Count(r => Equals(r.Fields.GetValueOrDefault("event"), "handler.threw")));
    }

    [TestMethod]
    public void FaultRemovesAFeaturesJobsAndIgnoresUnknownIds()
    {
        var (host, _, _) = T.LoadReal();
        host.Fault("nope", "x");
        host.Fault("homestead", "manual");
        Assert.IsFalse(host.Jobs.TryGet("HomesteadStatus", out _, out _));
        Assert.AreEqual(T.ExpectedJobs.Length - 2, host.Jobs.All.Count, "the two homestead jobs left the whitelist");
        var report = host.Reports.Single(r => r.FeatureId == "homestead");
        Assert.AreEqual((FeatureLoadStatus.Faulted, "manual"), (report.Status, report.ReasonCode));
    }

    [TestMethod]
    public void CancellationEndsTheJobCancelledWithoutCountingAsAThrow()
    {
        var env = new TestEnv();
        var host = new FeatureHost(env.Clock);
        host.LoadAll([new TestFeature("slowjob", register: r => r.AddJob(new JobDefinition("Waits", "slowjob", "x", [], AutomationSwitch.None, JobKind.Navigation), new CancelHandler()))], env.Context);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        for (var i = 0; i < 4; i++)
        {
            var r = T.Handler(host, "Waits").Execute(new FakeJobContext(env.Procedures), JobParameters.Empty, cts.Token);
            Assert.AreEqual(JobState.Cancelled, r.State);
        }

        Assert.AreEqual(FeatureLoadStatus.Enabled, host.Reports.Single(r => r.FeatureId == "slowjob").Status);
    }

    [TestMethod]
    public void ResultsLeaveTheGuardAsCodesAndScalarsOnly()
    {
        var r = GuardedJobHandler.Sanitise(new JobResult(
            JobState.Succeeded,
            "chat: hello <Bob>",
            new Dictionary<string, object?> { ["n"] = 3L, ["ok"] = true, ["code"] = "a.b:c", ["text"] = "Bob's \"chat\"", ["list"] = new[] { 1 }, ["bad key!"] = 1 }));
        Assert.AreEqual("code.invalid", r.Code);
        Assert.AreEqual(3L, r.Data!["n"]);
        Assert.AreEqual(true, r.Data["ok"]);
        Assert.AreEqual("a.b:c", r.Data["code"]);
        Assert.AreEqual("redacted", r.Data["text"]);
        Assert.AreEqual("redacted", r.Data["list"]);
        Assert.IsFalse(r.Data.ContainsKey("bad key!"));
    }

    private sealed class CancelHandler : IJobHandler
    {
        public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return new JobResult(JobState.Succeeded, "ok");
        }
    }
}
