using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Host;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Safety;

namespace Companion.App.Jobs;

/// <summary>Builds the per-job <see cref="IJobContext"/>.</summary>
internal interface IJobContextFactory
{
    /// <summary>Creates the context for one job run.</summary>
    IJobContext Create(RunContext run, ICharacterKnowledge? knowledge);
}

/// <summary>Everything the job host needs (contracts only, so tests use fakes).</summary>
/// <param name="Catalog">Job whitelist (enabled features).</param>
/// <param name="Clock">Clock.</param>
/// <param name="Stop">Kill switch.</param>
/// <param name="Pause">Pause control.</param>
/// <param name="Halt">HALT latch.</param>
/// <param name="Engine">Engine status.</param>
/// <param name="Journal">Journal.</param>
/// <param name="Trust">Trusted build.</param>
/// <param name="Knowledge">Knowledge store (its one profile serves every job, whoever is logged in).</param>
/// <param name="Switches">Automation switches.</param>
/// <param name="GameConnected">True when GameIO holds a game identity.</param>
/// <param name="Contexts">Job context factory.</param>
internal sealed record JobHostDeps(
    IJobCatalog Catalog,
    IClock Clock,
    IStopSwitch Stop,
    IPauseControl Pause,
    IHaltLatch Halt,
    IEngineStatusSource Engine,
    IJournal Journal,
    IFileTrust Trust,
    IKnowledgeStore Knowledge,
    IAutomationSwitches Switches,
    Func<bool> GameConnected,
    IJobContextFactory Contexts,
    JobActivityBoard? Activity = null);

/// <summary>
/// The job channel's core (SPEC 10.2): validates submissions against the whitelist and the automation switches (never
/// against the logged-in character: one knowledge profile for all, 4.33), applies the host limits (at most 10 jobs per hour, 1 queued + 1 running, per-job hourly caps), runs every job on the
/// single engine thread (the only game I/O queue), keeps the last 50 results in memory and journals codes only.
/// Pipe and tray threads call <see cref="Handle"/>; the engine thread calls <see cref="RunPending"/>.
/// </summary>
internal sealed partial class JobHost : IDisposable
{
    /// <summary>Results kept in memory.</summary>
    public const int MaxResults = 50;

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly JobHostDeps _d;

    /// <summary>
    /// A job ended (name, state), raised once per job under the host's lock (2026-11-04: the daily checklist's companion
    /// items). Listeners must be quick and must not call back into the job host.
    /// </summary>
    public event Action<string, JobState>? JobFinished;
    private readonly object _lock = new();
    private readonly Queue<JobEntry> _queue = new();
    private readonly LinkedList<JobEntry> _finished = new();
    private readonly List<(TimeSpan At, string Name)> _accepted = [];
    private readonly ConcurrentQueue<Action> _maintenance = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _shutdown = new();
    private JobEntry? _running;
    private Thread? _engineThread;
    private DateTimeOffset _heartbeatUtc;
    private string? _blockedReason;
    private bool _started;

    /// <summary>Creates the host (no side effects; no thread until <see cref="StartEngine"/>).</summary>
    public JobHost(JobHostDeps deps)
    {
        _d = deps ?? throw new ArgumentNullException(nameof(deps));
        _heartbeatUtc = deps.Clock.UtcNow;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.:,()\[\]/+\- ]{0,120}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeRegex();

    /// <summary>True while a job is running.</summary>
    /// <summary>A job runs or waits in the queue.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_lock)
            {
                return _running is not null || _queue.Count > 0;
            }
        }
    }

    public bool IsJobRunning
    {
        get
        {
            lock (_lock)
            {
                return _running is not null;
            }
        }
    }

    /// <summary>The names of the jobs waiting to run.</summary>
    public IReadOnlyList<string> QueuedJobNames
    {
        get
        {
            lock (_lock)
            {
                return [.. _queue.Select(j => j.Definition.Name)];
            }
        }
    }

    /// <summary>The running job (id, name, state), or null when none runs. The engine status names no job between rounds.</summary>
    public JobStatusInfo? RunningJob
    {
        get
        {
            JobStatusInfo? info;
            lock (_lock)
            {
                info = _running?.Info();
            }

            // the running job's own words for the status card (codes and values, JobActivityBoard)
            return info is not null && _d.Activity?.Snapshot(info.JobId) is { } activity ? info with { Data = activity } : info;
        }
    }

    /// <summary>Marks the host ready (end of the start sequence). <paramref name="blockedReason"/> keeps it Untrusted.</summary>
    public void MarkStarted(string? blockedReason)
    {
        lock (_lock)
        {
            _started = true;
            _blockedReason = blockedReason;
        }
    }

    /// <summary>Reason the host refuses jobs although the build may be trusted (broken core data), or null.</summary>
    public string? BlockedReason
    {
        get
        {
            lock (_lock)
            {
                return _blockedReason;
            }
        }
    }

    /// <summary>Heartbeat (1 s timer, independent of the engine thread).</summary>
    public void Beat()
    {
        lock (_lock)
        {
            _heartbeatUtc = _d.Clock.UtcNow;
        }
    }

    /// <summary>Queues work for the engine thread (knowledge stale marks, store flushes).</summary>
    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        _maintenance.Enqueue(work);
        _wake.Set();
    }

    /// <summary>Starts the single engine thread.</summary>
    public void StartEngine()
    {
        lock (_lock)
        {
            if (_engineThread is not null)
            {
                return;
            }

            _engineThread = new Thread(EngineLoop) { IsBackground = true, Name = "companion-engine", Priority = ThreadPriority.AboveNormal };
            _engineThread.Start();
        }
    }

    /// <summary>Stops the engine thread: cancels the running job and waits up to <paramref name="wait"/>.</summary>
    public bool StopEngine(TimeSpan wait)
    {
        Thread? t;
        lock (_lock)
        {
            t = _engineThread;
            _running?.Cancel("host.exit");
        }

        _shutdown.Cancel();
        _wake.Set();
        return t is null || t.Join(wait);
    }

    /// <summary>Handles one validated pipe request.</summary>
    public PipeResponse Handle(PipeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Op switch
        {
            PipeOp.Submit => Submit(request),
            PipeOp.Status => new PipeResponse(true, "ok", GetStatus()),
            PipeOp.Result => Result(request.JobId),
            PipeOp.Cancel => Cancel(request.JobId),
            PipeOp.Stop => StopAll(StopSource.Pipe, "pipe.stop"),
            PipeOp.Pause => PauseOp(),
            PipeOp.Resume => ResumeOp(),
            PipeOp.Show => ShowOp(request.JobName),
            _ => new PipeResponse(false, "bad.op"),
        };
    }

    /// <summary>Opens the companion window on the UI thread (set by the tray app; null without a tray).</summary>
    public Action<string?>? OpenMain { get; set; }

    private PipeResponse ShowOp(string? tab)
    {
        if (OpenMain is not { } show)
        {
            return new PipeResponse(false, "show.unavailable");
        }

        show(tab);
        return new PipeResponse(true, "shown");
    }

    /// <summary>Current host state.</summary>
    public HostState State
    {
        get
        {
            lock (_lock)
            {
                return ComputeState();
            }
        }
    }

    /// <summary>Status for the pipe and the tray.</summary>
    public HostStatus GetStatus()
    {
        string[] jobs;
        try
        {
            jobs = _d.Catalog.All.Select(j => j.Name).ToArray();
        }
        catch (Exception)
        {
            jobs = [];
        }

        EngineStatus? engine;
        try
        {
            engine = _d.Engine.Status;
        }
        catch (Exception)
        {
            engine = null;
        }

        bool connected;
        try
        {
            connected = _d.GameConnected();
        }
        catch (Exception)
        {
            connected = false;
        }

        lock (_lock)
        {
            return new HostStatus(ComputeState(), _heartbeatUtc, engine, _d.Halt.Current, connected, jobs);
        }
    }

    /// <summary>Kill switch from any source (pipe, tray, hotkey, session end): refuses new inputs, ends the job.</summary>
    public PipeResponse StopAll(StopSource source, string reasonCode)
    {
        _d.Stop.RequestStop(source, reasonCode);
        List<JobEntry> dropped;
        lock (_lock)
        {
            _running?.Cancel("stop");
            dropped = [.. _queue];
            _queue.Clear();
            foreach (var j in dropped)
            {
                FinishLocked(j, JobState.Stopped, "stop", null);
            }
        }

        Journal(JournalKind.Stop, null, ("source", source.ToString()), ("reason", reasonCode));
        try
        {
            _d.Journal.Flush(); // the user's Stop is on disk at once (audit R1.11)
        }
        catch (Exception)
        {
            // best effort
        }

        _wake.Set();
        return new PipeResponse(true, "stopped");
    }

    /// <summary>
    /// Engine thread body for one pass: runs queued maintenance work, then at most one job. Returns true if a job ran.
    /// </summary>
    internal bool RunPending()
    {
        while (_maintenance.TryDequeue(out var work))
        {
            try
            {
                work();
            }
            catch (Exception)
            {
                // maintenance work is best effort and never stops the engine thread
            }
        }

        JobEntry? job;
        lock (_lock)
        {
            if (_running is not null || _queue.Count == 0)
            {
                return false;
            }

            job = _queue.Dequeue();
            _running = job;
            job.State = JobState.Running;
        }

        Execute(job);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        StopEngine(TimeSpan.FromSeconds(2));
        _wake.Dispose();
        _shutdown.Dispose();
    }

    private void EngineLoop()
    {
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!RunPending())
                {
                    WaitHandle.WaitAny([_wake, token.WaitHandle], TimeSpan.FromSeconds(1));
                }
            }
            catch (Exception)
            {
                // never let the engine thread die; the job already failed InternalError
            }
        }
    }

    private PipeResponse Submit(PipeRequest r)
    {
        var jobId = r.JobId;
        if (!PipeCodec.IsJobId(jobId) || r.JobName is null)
        {
            return new PipeResponse(false, "bad.jobId");
        }

        var refusal = Gate();
        if (refusal is not null)
        {
            return Refuse(refusal, null);
        }

        lock (_lock)
        {
            if (Find(jobId!) is not null)
            {
                return Refuse("refused.duplicateId", null);
            }
        }

        if (!_d.Catalog.TryGet(r.JobName, out var def, out var handler) || def is null || handler is null)
        {
            return Refuse("refused.whitelist", null);
        }

        if (def.Kind == JobKind.Commit && def.Switch != AutomationSwitch.None && !_d.Switches.IsOn(def.Switch))
        {
            return Refuse("refused.switch", def.Name);
        }

        var knowledge = SafeProfile();

        JobParameters? parameters;
        string? reason;
        try
        {
            parameters = _d.Catalog.Validate(def, r.Params ?? new Dictionary<string, string>(), knowledge, out reason);
        }
        catch (Exception)
        {
            parameters = null;
            reason = "param.invalid";
        }

        if (parameters is null)
        {
            return Refuse("refused.params:" + SafeCode(reason ?? "param.invalid"), def.Name);
        }

        JobEntry entry;
        lock (_lock)
        {
            var now = _d.Clock.Monotonic;
            _accepted.RemoveAll(a => now - a.At >= Hour);
            if (_accepted.Count >= HardRules.MaxJobsPerHour)
            {
                return Refuse("refused.rate", def.Name);
            }

            var perJob = Math.Clamp(def.MaxPerHour, 0, HardRules.MaxJobsPerHour);
            if (_accepted.Count(a => string.Equals(a.Name, def.Name, StringComparison.Ordinal)) >= perJob)
            {
                return Refuse("refused.rate.job", def.Name);
            }

            if (_queue.Count >= PipeProtocol.MaxQueued)
            {
                return Refuse("refused.queueFull", def.Name);
            }

            if (Find(jobId!) is not null)
            {
                return Refuse("refused.duplicateId", null);
            }

            entry = new JobEntry(jobId!, def, handler, parameters, _d.Clock.UtcNow);
            _queue.Enqueue(entry);
            _accepted.Add((now, def.Name));
        }

        Journal(JournalKind.JobAccepted, entry.Id, ("job", def.Name), ("kind", def.Kind.ToString()));
        _wake.Set();
        return new PipeResponse(true, "accepted", null, entry.Info());
    }

    /// <summary>Host-level refusal (state), or null when submissions may be considered.</summary>
    private string? Gate()
    {
        lock (_lock)
        {
            return ComputeState() switch
            {
                HostState.Starting => "refused.starting",
                HostState.Untrusted => "refused.untrusted",
                HostState.Halted => "refused.halted",
                HostState.Stopped => "refused.stopped",
                _ => null,
            };
        }
    }

    private PipeResponse Refuse(string code, string? whitelistedName)
    {
        Journal(JournalKind.JobRefused, null, ("code", code), ("job", whitelistedName));
        return new PipeResponse(false, code);
    }

    private PipeResponse Result(string? jobId)
    {
        lock (_lock)
        {
            var j = jobId is null ? null : Find(jobId);
            return j is null ? new PipeResponse(false, "notFound") : new PipeResponse(true, "ok", null, j.Info());
        }
    }

    /// <summary>Cancels a job with the caller's reason code (rule mode's stall watchdog: never "at your request").</summary>
    public PipeResponse CancelWithReason(string jobId, string reason)
    {
        lock (_lock)
        {
            if (Find(jobId) is { } j && ReferenceEquals(_running, j))
            {
                j.Cancel(reason);
                return new PipeResponse(true, "cancel.requested", null, j.Info());
            }
        }

        return Cancel(jobId);
    }

    /// <summary>Writes one decision record for a host-side component (codes only).</summary>
    public void JournalDecision(params (string Key, object? Value)[] fields) => Journal(JournalKind.Decision, null, fields);

    private PipeResponse Cancel(string? jobId)
    {
        lock (_lock)
        {
            var j = jobId is null ? null : Find(jobId);
            if (j is null)
            {
                return new PipeResponse(false, "notFound");
            }

            if (_queue.Contains(j))
            {
                var keep = _queue.Where(q => !ReferenceEquals(q, j)).ToList();
                _queue.Clear();
                foreach (var q in keep)
                {
                    _queue.Enqueue(q);
                }

                FinishLocked(j, JobState.Cancelled, "cancelled.queued", null);
                return new PipeResponse(true, "cancelled", null, j.Info());
            }

            if (ReferenceEquals(_running, j))
            {
                j.Cancel("cancelled.byClient");
                return new PipeResponse(true, "cancel.requested", null, j.Info());
            }

            return new PipeResponse(false, "alreadyFinished", null, j.Info());
        }
    }

    private PipeResponse PauseOp()
    {
        _d.Pause.Pause("pipe");
        return new PipeResponse(true, "paused");
    }

    private PipeResponse ResumeOp()
    {
        if (_d.Stop.IsStopRequested || _d.Halt.IsHalted)
        {
            // Resuming after a stop or a HALT is a tray-only decision (SPEC 10.2).
            return new PipeResponse(false, "refused.stopOrHalt");
        }

        _d.Pause.Resume("pipe");
        return new PipeResponse(true, "resumed");
    }

    private void Execute(JobEntry job)
    {
        JobResult result;
        if (_d.Stop.IsStopRequested)
        {
            result = new JobResult(JobState.Stopped, "stop");
        }
        else if (_d.Halt.IsHalted)
        {
            result = new JobResult(JobState.Refused, "halt.latched");
        }
        else
        {
            result = RunHandler(job);
        }

        if (result.State == JobState.Halted && !_d.Halt.IsHalted)
        {
            // A feature-decided HALT (e.g. CraftStoppedEarlyTwice) has no latch API in IJobContext: the host latches it.
            var code = Enum.TryParse<HaltCode>(result.Code, ignoreCase: false, out var hc) && Enum.IsDefined(hc) ? hc : HaltCode.InternalError;
            try
            {
                _d.Halt.Halt(new HaltReport(code, null, job.Id, job.Definition.Name, null, _d.Clock.UtcNow, null));
                _d.Stop.RequestStop(StopSource.Engine, "halt." + code);
            }
            catch (Exception)
            {
                // the latch reports its own persistence failures
            }
        }

        lock (_lock)
        {
            FinishLocked(job, result.State, result.Code, SanitizeData(result.Data));
            _running = null;
        }

        job.DisposeCts();
    }

    private JobResult RunHandler(JobEntry job)
    {
        var knowledge = SafeProfile();
        var run = new RunContext(job.Id, job.Definition.Name, job.Definition.Kind, job.Definition.Switch);
        JobResult result;
        try
        {
            var ctx = _d.Contexts.Create(run, knowledge);
            result = job.Handler.Execute(ctx, job.Parameters, job.Token) ?? new JobResult(JobState.Failed, "result.null");
        }
        catch (OperationCanceledException)
        {
            result = _d.Stop.IsStopRequested
                ? new JobResult(JobState.Stopped, "stop")
                : new JobResult(JobState.Cancelled, job.CancelReason ?? "cancelled");
        }
        catch (Exception)
        {
            result = new JobResult(JobState.Failed, "InternalError");
        }

        _d.Activity?.Clear(job.Id); // the job ended: its activity line goes with it

        if (result.State is JobState.Cancelled && job.CancelReason is not null && !string.Equals(result.Code, job.CancelReason, StringComparison.Ordinal))
        {
            result = result with { Code = job.CancelReason };
        }

        try
        {
            knowledge?.Save();
        }
        catch (Exception)
        {
            // the store reports write failures itself; the job result stands
        }

        return result;
    }

    private void FinishLocked(JobEntry j, JobState state, string code, IReadOnlyDictionary<string, object?>? data)
    {
        j.State = state;
        j.Code = SafeCode(code);
        j.Data = data;
        j.FinishedUtc = _d.Clock.UtcNow;
        _finished.AddLast(j);
        while (_finished.Count > MaxResults)
        {
            _finished.RemoveFirst();
        }

        Journal(JournalKind.JobFinished, j.Id, ("job", j.Definition.Name), ("state", state.ToString()), ("code", j.Code));
        try
        {
            JobFinished?.Invoke(j.Definition.Name, state);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // a listener (the daily checklist) never breaks a job's end
        }

        try
        {
            _d.Journal.Flush();
        }
        catch (Exception)
        {
            // best effort
        }
    }

    private JobEntry? Find(string jobId)
    {
        if (_running is { } r && string.Equals(r.Id, jobId, StringComparison.Ordinal))
        {
            return r;
        }

        return _queue.FirstOrDefault(q => string.Equals(q.Id, jobId, StringComparison.Ordinal))
            ?? _finished.LastOrDefault(f => string.Equals(f.Id, jobId, StringComparison.Ordinal));
    }

    private HostState ComputeState()
    {
        if (!_started)
        {
            return HostState.Starting;
        }

        if (_blockedReason is not null || !SafeTrusted())
        {
            return HostState.Untrusted;
        }

        if (_d.Halt.IsHalted)
        {
            return HostState.Halted;
        }

        if (_d.Stop.IsStopRequested)
        {
            return HostState.Stopped;
        }

        if (_d.Pause.IsPaused)
        {
            return HostState.Paused;
        }

        EngineState engine;
        try
        {
            engine = _d.Engine.Status.State;
        }
        catch (Exception)
        {
            engine = EngineState.Idle;
        }

        if (engine == EngineState.Parked)
        {
            return HostState.Parked;
        }

        return _running is not null ? HostState.Running : HostState.Idle;
    }

    private bool SafeTrusted()
    {
        try
        {
            return _d.Trust.BuildTrusted;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The knowledge profile, or null when the store cannot open it (jobs that need it refuse).</summary>
    private ICharacterKnowledge? SafeProfile()
    {
        try
        {
            return _d.Knowledge.Profile;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string SafeCode(string code) => CodeRegex().IsMatch(code) ? code : "code.redacted";

    /// <summary>Job data that leaves the host: scalars and codes only, at most 16 entries.</summary>
    internal static IReadOnlyDictionary<string, object?>? SanitizeData(IReadOnlyDictionary<string, object?>? data)
    {
        if (data is null)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in data.Take(16))
        {
            if (k is null || !CodeRegex().IsMatch(k) || k.Length > 40)
            {
                continue;
            }

            result[k] = v switch
            {
                null => null,
                bool b => b,
                int i => (long)i,
                long l => l,
                double d when double.IsFinite(d) => d,
                string s when CodeRegex().IsMatch(s) => s,
                Enum e => e.ToString(),
                _ => "value.redacted",
            };
        }

        return result;
    }

    private void Journal(JournalKind kind, string? jobId, params (string Key, object? Value)[] fields)
    {
        try
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (k, v) in fields)
            {
                dict[k] = v;
            }

            _d.Journal.Write(new JournalRecord(_d.Clock.UtcNow, kind, jobId, null, null, dict));
        }
        catch (Exception)
        {
            // the journal never breaks the job channel
        }
    }

    /// <summary>One job (queued, running or finished).</summary>
    private sealed class JobEntry
    {
        private readonly CancellationTokenSource _cts = new();
        private string? _cancelReason;

        public JobEntry(string id, JobDefinition definition, IJobHandler handler, JobParameters parameters, DateTimeOffset createdUtc)
        {
            Id = id;
            Definition = definition;
            Handler = handler;
            Parameters = parameters;
            CreatedUtc = createdUtc;
            Code = "queued";
            Token = _cts.Token;
        }

        public string Id { get; }

        public JobDefinition Definition { get; }

        public IJobHandler Handler { get; }

        public JobParameters Parameters { get; }

        public DateTimeOffset CreatedUtc { get; }

        public CancellationToken Token { get; }

        public JobState State { get; set; } = JobState.Queued;

        public string Code { get; set; }

        public DateTimeOffset? FinishedUtc { get; set; }

        public IReadOnlyDictionary<string, object?>? Data { get; set; }

        public string? CancelReason => Volatile.Read(ref _cancelReason);

        public void Cancel(string reason)
        {
            Interlocked.CompareExchange(ref _cancelReason, reason, null);
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // finished meanwhile
            }
        }

        public void DisposeCts() => _cts.Dispose();

        public JobStatusInfo Info() => new(Id, Definition.Name, State, Code, CreatedUtc, FinishedUtc, Data);
    }
}
