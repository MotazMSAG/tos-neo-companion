using System.Diagnostics;
using System.Globalization;
using Companion.Contracts.Features;
using Companion.Contracts.Host;

namespace Companion.Cli;

/// <summary>Exit codes (SPEC 10.5).</summary>
internal static class ExitCodes
{
    /// <summary>OK.</summary>
    public const int Ok = 0;

    /// <summary>Usage error, or a job that ended Failed / Cancelled / Stopped / GameExited.</summary>
    public const int Failed = 1;

    /// <summary>Refused by the host.</summary>
    public const int Refused = 2;

    /// <summary>Host not running (no pipe within 2 s, or heartbeat older than 5 s).</summary>
    public const int HostNotRunning = 3;

    /// <summary>HALT.</summary>
    public const int Halted = 4;

    /// <summary>--wait timed out.</summary>
    public const int Timeout = 5;

    /// <summary>The job reached a needsSurvey stub.</summary>
    public const int NeedsSurvey = 6;
}

/// <summary>Runs one CLI command against the host. Output: codes and short summaries only.</summary>
internal sealed class CliRunner
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IPipeTransport _transport;
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan> _monotonic;
    private readonly Action<TimeSpan> _sleep;
    private readonly Func<string> _newJobId;

    /// <summary>Creates a runner (time and ids injectable for tests).</summary>
    public CliRunner(
        IPipeTransport transport,
        TextWriter output,
        TextWriter error,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan>? monotonic = null,
        Action<TimeSpan>? sleep = null,
        Func<string>? newJobId = null)
    {
        _transport = transport;
        _out = output;
        _err = error;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        var origin = Stopwatch.GetTimestamp();
        _monotonic = monotonic ?? (() => Stopwatch.GetElapsedTime(origin));
        _sleep = sleep ?? Thread.Sleep;
        _newJobId = newJobId ?? (() => Guid.NewGuid().ToString("N"));
    }

    /// <summary>Parses and runs; returns the exit code.</summary>
    public int Run(IReadOnlyList<string> args)
    {
        var parsed = CliArgs.Parse(args, out var error);
        if (parsed is null)
        {
            _err.WriteLine(error);
            _err.WriteLine(CliArgs.Usage);
            return ExitCodes.Failed;
        }

        return parsed.Command switch
        {
            CliCommand.Status => Status(),
            CliCommand.Dailies => Dailies(),
            CliCommand.Jobs => Jobs(),
            CliCommand.Submit => Submit(parsed),
            CliCommand.Result => Result(parsed.JobId!),
            CliCommand.Cancel => Simple(new PipeRequest(PipeOp.Cancel, parsed.JobId)),
            CliCommand.Stop => Simple(new PipeRequest(PipeOp.Stop)),
            CliCommand.Pause => Simple(new PipeRequest(PipeOp.Pause)),
            CliCommand.Show => Simple(new PipeRequest(PipeOp.Show, null, parsed.JobName)),
            _ => Simple(new PipeRequest(PipeOp.Resume)),
        };
    }

    /// <summary>Exit code for a job state.</summary>
    public static int ExitFor(JobState state) => state switch
    {
        JobState.Succeeded => ExitCodes.Ok,
        JobState.Refused => ExitCodes.Refused,
        JobState.Halted => ExitCodes.Halted,
        JobState.NeedsSurvey => ExitCodes.NeedsSurvey,
        JobState.Queued or JobState.Running or JobState.Parked => ExitCodes.Ok,
        _ => ExitCodes.Failed,
    };

    /// <summary>True when the job will not change any more.</summary>
    public static bool IsTerminal(JobState state) => state is not (JobState.Queued or JobState.Running or JobState.Parked);

    /// <summary>
    /// Prints today's checklist from the host's readable copy (%ProgramData%\TOS-companion\dailies\today.json): the game
    /// day, then one line per item on the user's list. Reads a file only; no pipe call.
    /// </summary>
    private int Dailies()
    {
        var path = DailiesFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TOS-companion", "dailies", "today.json");
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            _out.WriteLine($"game day {root.GetProperty("gameDay").GetString()} | resets {root.GetProperty("nextReset").GetString()} | as of {root.GetProperty("asOf").GetString()}"
                + (root.GetProperty("replaying").GetBoolean() ? " | still reading the log" : string.Empty));
            foreach (var i in root.GetProperty("items").EnumerateArray())
            {
                var kind = i.GetProperty("kind").GetString();
                var count = i.GetProperty("count").GetInt32();
                var per = i.GetProperty("perDay").GetInt32();
                var done = i.GetProperty("done").GetBoolean();
                var missed = i.TryGetProperty("missed", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.True;
                var mark = done ? "done" : missed ? "miss"
                    : kind == "event" && i.GetProperty("open").ValueKind == System.Text.Json.JsonValueKind.True ? "open"
                    : count > 0 ? "part" : "  - ";
                var amount = per > 1 || per == 0 ? $" {count}/{(per == 0 ? "-" : per.ToString(System.Globalization.CultureInfo.InvariantCulture))}" : string.Empty;
                var stack = i.TryGetProperty("stack", out var st) && st.ValueKind == System.Text.Json.JsonValueKind.Number ? $" [stored {st.GetInt32()}]" : string.Empty;
                var at = i.GetProperty("at").GetString() is { } t ? " " + t : string.Empty;
                var by = i.GetProperty("source").GetString() is "you" ? " (you)" : string.Empty;
                _out.WriteLine($"  {mark} {i.GetProperty("name").GetString()}{amount}{stack}{at}{by}");
            }

            if (root.TryGetProperty("cards", out var cards))
            {
                foreach (var c in cards.EnumerateArray())
                {
                    var until = c.GetProperty("until").GetString() is { } u ? " " + u : string.Empty;
                    _out.WriteLine($"  card {c.GetProperty("name").GetString()}: {c.GetProperty("state").GetString()}{until}");
                }
            }

            return ExitCodes.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _out.WriteLine("dailies.unavailable");
            return ExitCodes.Failed;
        }
    }

    /// <summary>today.json path override (tests).</summary>
    internal string? DailiesFile { get; init; }

    private int Status()
    {
        var r = Send(new PipeRequest(PipeOp.Status), out var code);
        if (r is null)
        {
            return code;
        }

        if (!r.Ok || r.Status is null)
        {
            _out.WriteLine(r.Code);
            return ExitCodes.Refused;
        }

        var st = r.Status;
        if (_utcNow() - st.HeartbeatUtc > TimeSpan.FromMilliseconds(PipeProtocol.HeartbeatStaleMs))
        {
            _out.WriteLine("host.heartbeatStale");
            return ExitCodes.HostNotRunning;
        }

        var e = st.Engine;
        _out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"state={st.State} engine={e?.State.ToString() ?? "-"} job={e?.JobId ?? "-"} procedure={e?.ProcedureId ?? "-"} step={e?.StepId ?? "-"} wait={e?.WaitingFor ?? "-"}"));
        _out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"game={(st.GameConnected ? "connected" : "notConnected")} halt={st.Halt?.Code.ToString() ?? "-"} jobs={st.Jobs.Count}"));
        return st.State == HostState.Halted ? ExitCodes.Halted : ExitCodes.Ok;
    }

    private int Jobs()
    {
        var r = Send(new PipeRequest(PipeOp.Status), out var code);
        if (r is null)
        {
            return code;
        }

        if (!r.Ok || r.Status is null)
        {
            _out.WriteLine(r.Code);
            return ExitCodes.Refused;
        }

        foreach (var name in r.Status.Jobs)
        {
            _out.WriteLine(name);
        }

        return ExitCodes.Ok;
    }

    private int Submit(CliArgs a)
    {
        var id = _newJobId();
        var r = Send(new PipeRequest(PipeOp.Submit, id, a.JobName, a.Params.Count > 0 ? a.Params : null), out var code);
        if (r is null)
        {
            return code;
        }

        if (!r.Ok)
        {
            _out.WriteLine(r.Code);
            return ExitCodes.Refused;
        }

        _out.WriteLine("accepted id=" + id);
        if (!a.Wait)
        {
            return ExitCodes.Ok;
        }

        var start = _monotonic();
        while (true)
        {
            var res = Send(new PipeRequest(PipeOp.Result, id), out code);
            if (res is null)
            {
                return code;
            }

            if (res.Ok && res.Job is { } job && IsTerminal(job.State))
            {
                PrintJob(job);
                return ExitFor(job.State);
            }

            if (!res.Ok)
            {
                _out.WriteLine(res.Code);
                return ExitCodes.Failed;
            }

            if (_monotonic() - start >= a.Timeout)
            {
                _out.WriteLine("timeout id=" + id + " state=" + (res.Job?.State.ToString() ?? "-"));
                return ExitCodes.Timeout;
            }

            _sleep(PollInterval);
        }
    }

    private int Result(string jobId)
    {
        var r = Send(new PipeRequest(PipeOp.Result, jobId), out var code);
        if (r is null)
        {
            return code;
        }

        if (!r.Ok || r.Job is null)
        {
            _out.WriteLine(r.Code);
            return string.Equals(r.Code, "notFound", StringComparison.Ordinal) ? ExitCodes.Failed : ExitCodes.Refused;
        }

        PrintJob(r.Job);
        return ExitFor(r.Job.State);
    }

    private int Simple(PipeRequest request)
    {
        var r = Send(request, out var code);
        if (r is null)
        {
            return code;
        }

        _out.WriteLine(r.Code);
        return r.Ok ? ExitCodes.Ok : ExitCodes.Refused;
    }

    private void PrintJob(JobStatusInfo job)
    {
        _out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"job={job.JobId} name={job.JobName} state={job.State} code={job.Code}"));
        if (job.Data is { Count: > 0 } data)
        {
            foreach (var (k, v) in data)
            {
                _out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {k}={v}"));
            }
        }
    }

    private PipeResponse? Send(PipeRequest request, out int exitCode)
    {
        var r = _transport.Send(request, out var error);
        if (r is null)
        {
            _out.WriteLine(error ?? "host.notRunning");
            exitCode = string.Equals(error, "request.tooLarge", StringComparison.Ordinal) ? ExitCodes.Failed : ExitCodes.HostNotRunning;
            return null;
        }

        exitCode = ExitCodes.Ok;
        return r;
    }
}
