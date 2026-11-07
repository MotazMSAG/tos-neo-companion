using System.Globalization;
using Companion.Contracts.Engine;
using Companion.Contracts.Host;
using Companion.Contracts.Knowledge;

namespace Companion.App.Tray;

/// <summary>
/// Keeps rule mode on (2026-10-17, the user: keep-stock rules "then I go AFK"): the KeepStockLoop job ends on a game
/// disconnect, a companion restart or a HALT the user cleared; while rule mode is on (a flag file in the runtime folder,
/// so it survives restarts) this keeper starts it again. Every minute, when nothing runs or waits in the queue, the host
/// is Idle (not stopped, paused, parked or halted) and the game is connected, it submits
/// KeepStockLoop. A running rule job whose journal has not been written for <see cref="StallMinutes"/> is cancelled
/// (the next tick starts it again). Three quick ends in a row (under 2 minutes each) wait <see cref="BackoffMinutes"/>
/// before the next start. Turning rule mode off removes the flag and cancels the rule job.
/// </summary>
internal sealed class RuleModeKeeper : IDisposable
{
    internal const string JobName = "KeepStockLoop";

    /// <summary>A rule job silent this long is stuck (a trip journals every minute, a rule pass waits 10 minutes).</summary>
    internal const int StallMinutes = 20;

    internal const int BackoffMinutes = 15;

    private readonly Jobs.JobHost _jobs;
    private readonly string _flag;
    private readonly string _journalDir;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 60_000 };
    private DateTime _lastStartUtc = DateTime.MinValue;
    private string? _lastJob;
    private int _quickEnds;
    private DateTime _backoffUntilUtc = DateTime.MinValue;

    public RuleModeKeeper(Jobs.JobHost jobs, string runtimeDir)
    {
        _jobs = jobs;
        _flag = Path.Combine(runtimeDir, "rulemode.on");
        _journalDir = Path.Combine(runtimeDir, "journal");
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>Rule mode is on (kept across restarts).</summary>
    public bool On => File.Exists(_flag);

    /// <summary>The end code of the latest rule job (audit #40).</summary>
    public string? LastEndCode { get; private set; }

    /// <summary>The latest refusal of a start (cleared by a start).</summary>
    public string? LastRefusal { get; private set; }

    /// <summary>
    /// What the keeper is doing while no rule job runs, in plain words for the Rules tab (audit R3.3, #35: it always said
    /// "starts again within a minute", also during a 15-minute back-off).
    /// </summary>
    public string Status()
    {
        var now = DateTime.UtcNow;
        if (now < _backoffUntilUtc)
        {
            return "Rule mode is ON: waiting until " + _backoffUntilUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                + " after 3 quick ends" + (LastEndCode is { } code ? " (last: " + code + ")" : string.Empty) + ".";
        }

        return LastRefusal is { } refusal
            ? "Rule mode is ON, but the start is refused (" + refusal + "); it tries again every minute."
            : "Rule mode is ON: starts again within a minute when the companion is free.";
    }

    /// <summary>Turns rule mode on (and starts the rule job now when possible) or off (and cancels the rule job).</summary>
    public string Set(bool on)
    {
        try
        {
            if (on)
            {
                File.WriteAllText(_flag, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                _quickEnds = 0;
                _backoffUntilUtc = DateTime.MinValue;
                return Tick(force: true) ?? "on";
            }

            if (File.Exists(_flag))
            {
                File.Delete(_flag);
            }

            if (_jobs.RunningJob is { JobName: JobName } running)
            {
                _jobs.Handle(new PipeRequest(PipeOp.Cancel, running.JobId));
            }

            return "off";
        }
        catch (IOException ex)
        {
            return "error: " + ex.GetType().Name;
        }
        catch (UnauthorizedAccessException ex)
        {
            return "error: " + ex.GetType().Name;
        }
    }

    /// <summary>One check; returns what it did (null when nothing).</summary>
    private string? Tick(bool force = false)
    {
        if (!On)
        {
            return null;
        }

        var status = _jobs.GetStatus();
        if (_jobs.RunningJob is { } running)
        {
            // audit R3.1 (#11, #16): a parked or waiting engine and a user pause write nothing for a long time and are not
            // stuck; a real stall is cancelled with its own code (it read "Stopped at your request") and journaled
            var waiting = status.State == HostState.Paused || status.Engine?.State is EngineState.Parked or EngineState.Waiting;
            if (running.JobName == JobName && !waiting && JournalAgeMinutes() is { } age && age >= StallMinutes)
            {
                _jobs.JournalDecision(("event", "ruleMode.stalled"), ("minutes", (long)age), ("engine", status.Engine?.State.ToString()));
                _jobs.CancelWithReason(running.JobId, "cancelled.ruleModeStall"); // stuck: the next tick starts it again
                return "stalled";
            }

            return null;
        }

        if (_jobs.IsBusy || status.State != HostState.Idle || status.Halt is not null || !status.GameConnected)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        if (!force && now < _backoffUntilUtc)
        {
            return null;
        }

        if (_lastJob is not null)
        {
            // the previous rule job ended; a quick end counts towards the back-off, with its code (audit #40: the cycle
            // repeated forever without a reason)
            var ended = _jobs.Handle(new PipeRequest(PipeOp.Result, _lastJob)).Job;
            LastEndCode = ended?.Code;
            _quickEnds = now - _lastStartUtc < TimeSpan.FromMinutes(2) ? _quickEnds + 1 : 0;
            _lastJob = null;
            if (_quickEnds >= 3 && !force)
            {
                _quickEnds = 0;
                _backoffUntilUtc = now.AddMinutes(BackoffMinutes);
                _jobs.JournalDecision(("event", "ruleMode.backoff"), ("minutes", (long)BackoffMinutes), ("lastCode", LastEndCode));
                return "backoff";
            }
        }

        var id = Guid.NewGuid().ToString("N");
        var answer = _jobs.Handle(new PipeRequest(PipeOp.Submit, id, JobName));
        if (answer.Ok)
        {
            _lastJob = id;
            _lastStartUtc = now;
            LastRefusal = null;
            return "started";
        }

        if (LastRefusal != answer.Code)
        {
            _jobs.JournalDecision(("event", "ruleMode.refused"), ("code", answer.Code));
        }

        LastRefusal = answer.Code;
        return "refused: " + answer.Code;
    }

    /// <summary>Minutes since the newest journal's last record (null when there is none).</summary>
    private double? JournalAgeMinutes()
    {
        try
        {
            var file = new DirectoryInfo(_journalDir).GetFiles("*.jsonl").OrderByDescending(f => f.Name).FirstOrDefault();
            if (file is null)
            {
                return null;
            }

            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0)
            {
                return null;
            }

            stream.Seek(Math.Max(0, stream.Length - 4096), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var tail = reader.ReadToEnd();
            var lastLine = tail.TrimEnd('\r', '\n').Split('\n')[^1];
            var at = lastLine.IndexOf("\"utc\":\"", StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }

            var start = at + 7;
            var end = lastLine.IndexOf('"', start);
            return end > start && DateTimeOffset.TryParse(lastLine[start..end], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
                ? (DateTimeOffset.UtcNow - t).TotalMinutes
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
