using System.Text;
using System.Text.Json;
using Companion.App.Jobs;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Host;

namespace Companion.App.Tests;

[TestClass]
public sealed class PipeCodecTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    private static PipeRequest? Parse(string json, out string code) => PipeCodec.ParseRequest(Encoding.UTF8.GetBytes(json), out code);

    [TestMethod]
    public void EveryOp_ParsesWithItsOwnFieldsOnly()
    {
        Assert.AreEqual(PipeOp.Status, Parse("{\"op\":\"status\"}", out _)!.Op);
        Assert.AreEqual(PipeOp.Stop, Parse("{\"op\":\"stop\"}", out _)!.Op);
        Assert.AreEqual(PipeOp.Pause, Parse("{\"op\":\"pause\"}", out _)!.Op);
        Assert.AreEqual(PipeOp.Resume, Parse("{\"op\":\"resume\"}", out _)!.Op);
        Assert.AreEqual(Id, Parse($"{{\"op\":\"result\",\"jobId\":\"{Id}\"}}", out _)!.JobId);
        Assert.AreEqual(Id, Parse($"{{\"op\":\"cancel\",\"jobId\":\"{Id}\"}}", out _)!.JobId);

        var submit = Parse($"{{\"op\":\"submit\",\"jobId\":\"{Id}\",\"jobName\":\"Craft\",\"params\":{{\"item\":\"Iron Ore\",\"qty\":\"5\"}}}}", out var code);
        Assert.IsNotNull(submit, code);
        Assert.AreEqual(PipeOp.Submit, submit.Op);
        Assert.AreEqual("Craft", submit.JobName);
        Assert.AreEqual("Iron Ore", submit.Params!["item"]);
        Assert.AreEqual("5", submit.Params!["qty"]);
    }

    [TestMethod]
    public void ASubmitCarryingACharacterKey_IsRefusedAsAnUnknownField()
    {
        // 4.33: no character binding any more; a caller that still sends "characterKey" (an old CLI or script) is told so
        // instead of having its job run for a character it did not mean
        foreach (var key in new[] { "\"00112233aabbccdd\"", "\"XYZ\"", "null" })
        {
            var json = $"{{\"op\":\"submit\",\"jobId\":\"{Id}\",\"jobName\":\"Reset\",\"characterKey\":{key}}}";
            Assert.IsNull(Parse(json, out var code), key);
            Assert.AreEqual("bad.unknownField", code, key);
        }

        Assert.IsNotNull(Parse($"{{\"op\":\"submit\",\"jobId\":\"{Id}\",\"jobName\":\"Reset\"}}", out var ok), ok);
    }

    [TestMethod]
    public void TheCliWireFormat_RoundTripsThroughTheServerParser()
    {
        var req = new PipeRequest(PipeOp.Submit, Id, "SurveyScreen", new Dictionary<string, string> { ["fullFrame"] = "true" });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(req, Cli.NamedPipeTransport.WireOptions);
        Assert.IsFalse(bytes.Contains((byte)'\n'), "one line");
        var parsed = PipeCodec.ParseRequest(bytes, out var code);
        Assert.IsNotNull(parsed, code);
        Assert.AreEqual("SurveyScreen", parsed.JobName);
        Assert.AreEqual("true", parsed.Params!["fullFrame"]);

        foreach (var op in Enum.GetValues<PipeOp>())
        {
            var r = op is PipeOp.Submit ? req : new PipeRequest(op, op is PipeOp.Result or PipeOp.Cancel ? Id : null);
            Assert.IsNotNull(PipeCodec.ParseRequest(JsonSerializer.SerializeToUtf8Bytes(r, Cli.NamedPipeTransport.WireOptions), out code), op + " " + code);
        }
    }

    [TestMethod]
    [DataRow("", "bad.empty")]
    [DataRow("not json", "bad.json")]
    [DataRow("[1,2]", "bad.shape")]
    [DataRow("{\"op\":\"status\",\"extra\":1}", "bad.unknownField")]
    [DataRow("{\"op\":\"status\",\"op\":\"stop\"}", "bad.duplicateField")]
    [DataRow("{\"op\":\"STATUS\"}", "bad.op")]
    [DataRow("{\"op\":\"reload\"}", "bad.op")]
    [DataRow("{\"op\":1}", "bad.op")]
    [DataRow("{\"jobId\":\"0123456789abcdef0123456789abcdef\"}", "bad.op")]
    [DataRow("{\"op\":\"status\",\"jobId\":\"0123456789abcdef0123456789abcdef\"}", "bad.fieldForOp")]
    [DataRow("{\"op\":\"stop\",\"params\":{}}", "bad.fieldForOp")]
    [DataRow("{\"op\":\"result\",\"jobName\":\"Reset\",\"jobId\":\"0123456789abcdef0123456789abcdef\"}", "bad.fieldForOp")]
    [DataRow("{\"op\":\"result\"}", "bad.jobId")]
    [DataRow("{\"op\":\"result\",\"jobId\":\"0123456789ABCDEF0123456789ABCDEF\"}", "bad.jobId")]
    [DataRow("{\"op\":\"cancel\",\"jobId\":\"0123\"}", "bad.jobId")]
    [DataRow("{\"op\":\"cancel\",\"jobId\":5}", "bad.jobId")]
    [DataRow("{\"op\":\"submit\",\"jobId\":\"0123456789abcdef0123456789abcdef\"}", "bad.jobName")]
    [DataRow("{\"op\":\"submit\",\"jobId\":\"0123456789abcdef0123456789abcdef\",\"jobName\":\"../x\"}", "bad.jobName")]
    [DataRow("{\"op\":\"submit\",\"jobId\":\"0123456789abcdef0123456789abcdef\",\"jobName\":\"Reset\",\"params\":[]}", "bad.params")]
    [DataRow("{\"op\":\"submit\",\"jobId\":\"0123456789abcdef0123456789abcdef\",\"jobName\":\"Reset\",\"params\":{\"q\":5}}", "bad.params.type")]
    [DataRow("{\"op\":\"submit\",\"jobId\":\"0123456789abcdef0123456789abcdef\",\"jobName\":\"Reset\",\"params\":{\"1q\":\"5\"}}", "bad.params.name")]
    [DataRow("{\"op\":\"submit\",\"jobId\":\"0123456789abcdef0123456789abcdef\",\"jobName\":\"Reset\",\"params\":{\"q\":\"\"}}", "bad.params.value")]
    [DataRow("{\"op\":\"submit\",\"jobId\":\"0123456789abcdef0123456789abcdef\",\"jobName\":\"Reset\",\"params\":{\"q\":\"a\\u0007b\"}}", "bad.params.value")]
    [DataRow("{\"op\":\"submit\",\"jobId\":\"0123456789abcdef0123456789abcdef\",\"jobName\":\"Reset\",\"characterKey\":\"XYZ\"}", "bad.unknownField")]
    [DataRow("{\"op\":\"status\",}", "bad.json")]
    [DataRow("{\"op\":\"status\"/*c*/}", "bad.json")]
    public void InvalidRequests_AreRefusedWithACode(string json, string expected)
    {
        Assert.IsNull(Parse(json, out var code));
        Assert.AreEqual(expected, code);
    }

    [TestMethod]
    public void Size_IsLimitedTo1024Bytes_AndValuesTo64Chars()
    {
        var pad = new string('a', 64);
        var p = string.Join(",", Enumerable.Range(0, 15).Select(i => $"\"p{i}\":\"{pad}\""));
        var big = $"{{\"op\":\"submit\",\"jobId\":\"{Id}\",\"jobName\":\"Reset\",\"params\":{{{p}}}}}";
        Assert.IsTrue(Encoding.UTF8.GetByteCount(big) > PipeProtocol.MaxMessageBytes);
        Assert.IsNull(Parse(big, out var code));
        Assert.AreEqual("bad.size", code);

        var longValue = $"{{\"op\":\"submit\",\"jobId\":\"{Id}\",\"jobName\":\"Reset\",\"params\":{{\"q\":\"{new string('x', 65)}\"}}}}";
        Assert.IsNull(Parse(longValue, out code));
        Assert.AreEqual("bad.params.value", code);

        var tooMany = string.Join(",", Enumerable.Range(0, 17).Select(i => $"\"p{i}\":\"v\""));
        Assert.IsNull(Parse($"{{\"op\":\"submit\",\"jobId\":\"{Id}\",\"jobName\":\"Reset\",\"params\":{{{tooMany}}}}}", out code));
        Assert.AreEqual("bad.params.tooMany", code);

        var longName = $"{{\"op\":\"submit\",\"jobId\":\"{Id}\",\"jobName\":\"{new string('A', 41)}\"}}";
        Assert.IsNull(Parse(longName, out code));
        Assert.AreEqual("bad.jobName", code);
    }

    [TestMethod]
    public void Responses_AreOneLine_AndAlwaysFit1024Bytes()
    {
        var engine = new EngineStatus(EngineState.Waiting, Id, "OpenBagClick", "open", "UserActive", DateTimeOffset.UnixEpoch);
        var halt = new HaltReport(HaltCode.RecalibrateTemplate, "hud.bag", Id, "OpenBagClick", "open", DateTimeOffset.UnixEpoch, @"C:\ProgramData\TOS-companion\diagnostics\20261014T120000Z-RecalibrateTemplate");
        var jobs = Enumerable.Range(0, 60).Select(i => "SomeLongJobName" + i).ToArray();
        var status = new HostStatus(HostState.Halted, DateTimeOffset.UnixEpoch, engine, halt, true, jobs);
        var data = Enumerable.Range(0, 40).ToDictionary(i => "key" + i, i => (object?)("value.code." + i));
        var job = new JobStatusInfo(Id, "Craft", JobState.Halted, "CraftStoppedEarlyTwice", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, data);

        var bytes = PipeCodec.EncodeResponse(new PipeResponse(true, "ok", status, job));
        Assert.IsTrue(bytes.Length <= PipeProtocol.MaxMessageBytes, bytes.Length.ToString());
        Assert.IsFalse(bytes.Contains((byte)'\n'));
        var back = Cli.NamedPipeTransport.Decode(bytes, out var err);
        Assert.IsNotNull(back, err);
        Assert.AreEqual(HostState.Halted, back.Status!.State);
        Assert.AreEqual(JobState.Halted, back.Job!.State);
        Assert.IsNull(back.Job.Data, "data is dropped first");

        var small = PipeCodec.EncodeResponse(new PipeResponse(true, "ok", status with { Jobs = ["Reset", "OpenBagKey"], Halt = null, Engine = null }));
        var smallBack = Cli.NamedPipeTransport.Decode(small, out _);
        CollectionAssert.AreEqual(new[] { "Reset", "OpenBagKey" }, smallBack!.Status!.Jobs.ToArray());
    }
}
