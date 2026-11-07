using Companion.Contracts.Engine;
using Companion.Contracts.Host;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Safety;
using Companion.Feature.TeaHouse;

namespace Companion.App.Tray;

/// <summary>
/// The Kitty Tea House helper's auto-play (2026-10-30, the user: "make the helper click while I'm afk"): starts the
/// TeaHousePlay job through the job host (same gates and limits as every job) and posts the helper's presses on the
/// shared <see cref="TeaHouseDesk"/>. The job presses through the engine (slide clicks, never the real cursor) and
/// confirms each press from the game log; this class only asks. A Stop or a safety stop while auto-play is on switches
/// it off (watched every second), so auto-play never starts again by itself after "Allow again" or a cleared safety stop
/// (safety review 2026-10-30): the user ticks it again.
/// </summary>
internal sealed class TeaHouseHands : IDisposable
{
    /// <summary>How long one press may take, the job's start included (the press procedure's own patience is 20 s).</summary>
    public static readonly TimeSpan PressTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan WatchEvery = TimeSpan.FromSeconds(1);

    private readonly Jobs.JobHost _jobs;
    private readonly TeaHouseDesk _desk;
    private readonly IStopSwitch _stop;
    private readonly IHaltLatch _halt;
    private readonly System.Threading.Timer _watch;

    public TeaHouseHands(Jobs.JobHost jobs, TeaHouseDesk desk, IStopSwitch stop, IHaltLatch halt)
    {
        _jobs = jobs;
        _desk = desk;
        _stop = stop;
        _halt = halt;
        _watch = new System.Threading.Timer(_ => Watch(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Auto-play on or off (off withdraws the press in progress; the job ends). On is refused while stopped.</summary>
    public bool On
    {
        get => _desk.AutoPlay;
        set
        {
            var on = value && !Stopped;
            _desk.AutoPlay = on;
            _watch.Change(on ? WatchEvery : Timeout.InfiniteTimeSpan, on ? WatchEvery : Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>True while the TeaHousePlay job runs.</summary>
    public bool JobRunning => _desk.JobRunning;

    /// <summary>A Stop or a safety stop is in force.</summary>
    public bool Stopped => _stop.IsStopRequested || _halt.IsHalted;

    /// <summary>
    /// Presses <paramref name="square"/> (0 the tray .. 35, or <see cref="TeaHouseDesk.Restart"/>), starting the job
    /// first when it does not run. Returns the job's answer, or a not-done press saying why nothing was pressed.
    /// </summary>
    public TeaPress Press(int square, CancellationToken ct)
    {
        if (Stopped)
        {
            On = false;
            return new TeaPress(false, "refused:stopped");
        }

        if (!_desk.AutoPlay)
        {
            return new TeaPress(false, "autoPlay.off");
        }

        if (!_desk.JobRunning && Start() is { } refused)
        {
            return new TeaPress(false, refused);
        }

        return _desk.Press(square, PressTimeout, ct) ?? new TeaPress(false, "job.noAnswer");
    }

    public void Dispose()
    {
        _desk.AutoPlay = false;
        _watch.Dispose();
    }

    private void Watch()
    {
        if (!_desk.AutoPlay)
        {
            _watch.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        if (Stopped)
        {
            On = false;
        }
    }

    /// <summary>Submits the job unless it runs or waits; the refusal code, else null.</summary>
    private string? Start()
    {
        if (_jobs.QueuedJobNames.Contains(TeaHouseFeature.PlayJob))
        {
            return null;
        }

        if (_jobs.RunningJob is { } running && running.JobName != TeaHouseFeature.PlayJob)
        {
            return "busy:" + running.JobName;
        }

        var answer = _jobs.Handle(new PipeRequest(PipeOp.Submit, Guid.NewGuid().ToString("N"), TeaHouseFeature.PlayJob));
        return answer.Ok ? null : "refused:" + answer.Code;
    }
}
