using System.Globalization;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Host;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Navigation;
using Companion.Contracts.Perception;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.Engine.Internal;

namespace Companion.Engine;

/// <summary>
/// Runs procedures with the recovery state machine of SPEC 6 (gates, pre-flight, per-action execution, outcome
/// classification, the outcome-to-action table, the counted ladder, budgets, cycle detector, park, pause, HALT with
/// diagnostics). All game I/O goes through the contracts handed to the constructor; the constructor has no side
/// effects. One run at a time (calls are serialised; the host runs every job on its single engine thread).
/// </summary>
public sealed partial class ProcedureRunner : IProcedureRunner, IEngineStatusSource
{
    private const int MaxCallDepth = 3;
    private const int CycleLimit = 8;

    private readonly IGameIo _gameIo;
    private readonly ILogTracker _log;
    private readonly IUiClassifier _classifier;
    private readonly IVision _vision;
    private readonly ITemplateCatalog _templates;
    private readonly IReaderCatalog _readers;
    private readonly IScreenService _screen;
    private readonly IProcedureCatalog _procedures;
    private readonly IUiMapCatalog _uiMap;
    private readonly IScreenIdentifier _identifier;
    private readonly IPathPlanner _planner;
    private readonly IJournal _journal;
    private readonly IArtifactStore _artifacts;
    private readonly IHaltLatch _haltLatch;
    private readonly INotifier _notifier;
    private readonly IAutomationSwitches _switches;
    private readonly IConfigProvider _config;
    private readonly IFileTrust _fileTrust;
    private readonly IStopSwitch _stop;
    private readonly IPauseControl _pause;
    private readonly IClock _clock;
    private readonly CompanionPaths _paths;

    private readonly object _runLock = new();
    private readonly object _statusLock = new();
    private readonly List<(LogPosition Pos, TimeSpan Mono)> _escMarks = [];
    private readonly List<TimeSpan> _incidents = [];
    /// <summary>When exhausted recovery ladders ended Deferred instead of in a HALT (<see cref="HardRules.SoftEndsBeforeHalt"/>).</summary>
    private readonly Queue<TimeSpan> _softEnds = new();

    /// <summary>The soft ends still inside <see cref="HardRules.SoftEndWindow"/>, oldest first (monotonic; all jobs).</summary>
    public IReadOnlyList<TimeSpan> RecentSoftEnds()
    {
        lock (_softEnds)
        {
            return [.. _softEnds.Where(t => Mono - t <= HardRules.SoftEndWindow)];
        }
    }
    private EngineStatus _status;
    private JobScope? _job;

    /// <summary>Creates the runner. Stores dependencies only (no window lookup, threads, files or timers).</summary>
    public ProcedureRunner(
        IGameIo gameIo,
        ILogTracker log,
        IUiClassifier classifier,
        IVision vision,
        ITemplateCatalog templates,
        IReaderCatalog readers,
        IScreenService screen,
        IProcedureCatalog procedures,
        IUiMapCatalog uiMap,
        IScreenIdentifier identifier,
        IPathPlanner planner,
        IJournal journal,
        IArtifactStore artifacts,
        IHaltLatch haltLatch,
        INotifier notifier,
        IAutomationSwitches switches,
        IConfigProvider config,
        IFileTrust fileTrust,
        IStopSwitch stopSwitch,
        IPauseControl pause,
        IClock clock,
        CompanionPaths paths)
    {
        _gameIo = gameIo;
        _log = log;
        _classifier = classifier;
        _vision = vision;
        _templates = templates;
        _readers = readers;
        _screen = screen;
        _procedures = procedures;
        _uiMap = uiMap;
        _identifier = identifier;
        _planner = planner;
        _journal = journal;
        _artifacts = artifacts;
        _haltLatch = haltLatch;
        _notifier = notifier;
        _switches = switches;
        _config = config;
        _fileTrust = fileTrust;
        _stop = stopSwitch;
        _pause = pause;
        _clock = clock;
        _paths = paths;
        _status = new EngineStatus(EngineState.Idle, null, null, null, null, DateTimeOffset.MinValue);
    }

    /// <inheritdoc />
    public EngineStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status;
            }
        }
    }

    /// <inheritdoc />
    public ProcedureResult Run(string procedureId, RunContext context, CancellationToken ct)
    {
        lock (_runLock)
        {
            var def = procedureId is null ? null : _procedures.Get(procedureId);
            if (def is null)
            {
                var empty = new ProcedureDefinition(
                    StepValidationSafeId(procedureId), "missing", [new StepDefinition("missing", StatePredicate.Any, new WaitAction(0), Expectation.Nothing)], ProcedureLimits.Default);
                return Finish(NewRunState(empty, context, ct), ProcedureStatus.Refused, StepOutcome.Refused, null, "procedure.missing");
            }
            return Run(def, context, ct);
        }
    }

    /// <inheritdoc />
    public ProcedureResult Run(ProcedureDefinition procedure, RunContext context, CancellationToken ct)
    {
        lock (_runLock)
        {
            var rs = NewRunState(procedure, context, ct);
            var status = ProcedureStatus.Succeeded;
            StepOutcome? final = null;
            HaltReport? halt = null;
            string? endDetail = null;
            try
            {
                rs.CurrentProcedure = procedure.Id;
                SetStatus(EngineState.Running, rs, null);
                StartChecks(rs);
                Preflight(rs, procedure, 0, new HashSet<string>(StringComparer.Ordinal));
                JournalAutoCollect(rs, "before");
                if (ContainsRestore(procedure))
                {
                    RecordInitialHud(rs, beforeReset: true);
                }
                RunFrame(rs, new ProcFrame(procedure, rs.Limits, 0));
                final = rs.LastOutcome ?? StepOutcome.Ok;
                if (OutcomePolicy.ClassOf(final.Value) != OutcomeClass.Ok)
                {
                    final = StepOutcome.Ok;
                }
            }
            catch (HaltSignal h)
            {
                halt = DoHalt(rs, h);
                status = ProcedureStatus.Halted;
                final = h.Outcome;
            }
            catch (EndSignal e)
            {
                status = StatusOf(e.Outcome);
                final = e.Outcome;
                endDetail = e.Detail;
            }
            catch (OperationCanceledException)
            {
                var stopped = _stop.IsStopRequested;
                status = stopped ? ProcedureStatus.Stopped : ProcedureStatus.Cancelled;
                final = stopped ? StepOutcome.Stopped : StepOutcome.Cancelled;
                endDetail = stopped ? "stop" : "cancelled";
            }
            catch (Exception ex)
            {
                halt = DoHalt(rs, new HaltSignal(HaltCode.InternalError, ex.GetType().Name, rs.CurrentStep, StepOutcome.InternalError));
                status = ProcedureStatus.Halted;
                final = StepOutcome.InternalError;
            }
            return Finish(rs, status, final, halt, endDetail);
        }
    }

    private static string StepValidationSafeId(string? id) =>
        id is not null && JournalSchema.IsCode(id) && id.Length is > 0 and <= 64 && char.IsLetter(id[0]) ? id : "missing";

    private RunState NewRunState(ProcedureDefinition procedure, RunContext context, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        if (_job is null || !string.Equals(_job.JobId, context.JobId, StringComparison.Ordinal))
        {
            _job = new JobScope(context.JobId, _clock.Monotonic, now);
        }
        var group = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + procedure.Id;
        return new RunState
        {
            Job = _job,
            StartedMono = _clock.Monotonic,
            Ctx = context,
            Root = procedure,
            Limits = StepValidation.Clamp(procedure.Limits),
            Ct = ct,
            ArtifactGroup = SafeFiles.SanitizeName(group),
            CurrentProcedure = procedure.Id,
        };
    }

    private ProcedureResult Finish(RunState rs, ProcedureStatus status, StepOutcome? final, HaltReport? halt, string? detail)
    {
        try
        {
            JournalAutoCollect(rs, "after");
            if (status == ProcedureStatus.Stopped)
            {
                Write(rs, JournalKind.Stop, rs.CurrentStep, ("source", _stop.Current?.Source), ("reason", _stop.Current?.ReasonCode ?? detail));
            }
            if (status == ProcedureStatus.Cancelled)
            {
                Write(rs, JournalKind.Park, rs.CurrentStep, ("event", "cancelled"), ("detail", detail));
            }
            Write(rs, JournalKind.Decision, null, ("event", "procedure.end"), ("status", status), ("final", final),
                ("detail", detail), ("inputs", rs.Inputs), ("clicks", rs.Clicks), ("escs", rs.Escs));
        }
        catch
        {
            // journaling never breaks the result
        }
        if (rs.Root.Kind == ProcedureKind.Commit)
        {
            rs.Job.VerifiedMono = null; // a verification serves the next commit run only
        }
        else if (status == ProcedureStatus.Succeeded && rs.Root.VerifiesSelection && rs.Root.Kind == ProcedureKind.Navigation && rs.Root.Gate == AutomationSwitch.None)
        {
            rs.Job.VerifiedMono = rs.StartedMono;
        }

        var state = status switch
        {
            ProcedureStatus.Halted => EngineState.Halted,
            ProcedureStatus.Stopped => EngineState.Stopped,
            _ => EngineState.Idle,
        };
        SetStatus(state, null, null);
        FlushJournal();
        return new ProcedureResult(
            rs.Root.Id,
            status,
            final,
            halt,
            rs.Inputs,
            rs.Clicks,
            rs.Escs,
            [.. rs.Attempts],
            new Dictionary<string, ScreenReadResult>(rs.Reads, StringComparer.Ordinal),
            [.. rs.Artifacts],
            [.. rs.Pending],
            detail);
    }

    private static ProcedureStatus StatusOf(StepOutcome o) => o switch
    {
        StepOutcome.GameExited => ProcedureStatus.GameExited,
        // the game was closed and opened again: the job ends like a game exit, no HALT (2026-10-18, the user: a
        // restart of the game is not a safety problem; the next job connects to the new game window)
        StepOutcome.IdentityChanged => ProcedureStatus.GameExited,
        StepOutcome.Stopped => ProcedureStatus.Stopped,
        StepOutcome.Refused => ProcedureStatus.Refused,
        StepOutcome.NeedsSurvey => ProcedureStatus.NeedsSurvey,
        StepOutcome.Cancelled => ProcedureStatus.Cancelled,
        StepOutcome.Deferred => ProcedureStatus.Deferred,
        _ => ProcedureStatus.Cancelled,
    };

    /// <summary>Checks done once before anything else (no input).</summary>
    private void StartChecks(RunState rs)
    {
        if (_stop.IsStopRequested)
        {
            throw new EndSignal(StepOutcome.Stopped, "stop");
        }
        if (_haltLatch.IsHalted)
        {
            throw new EndSignal(StepOutcome.Refused, "halt.latched");
        }
        var problems = StepValidation.Validate(rs.Root);
        if (problems.Count > 0)
        {
            throw new EndSignal(StepOutcome.Refused, "procedure.invalid");
        }
        if (!_fileTrust.BuildTrusted)
        {
            throw new HaltSignal(HaltCode.TamperedFiles, "build", null, StepOutcome.TamperedFiles);
        }
        // incident 28 (2026-10-28): after the game was closed and opened again, the old window was kept (it is only
        // replaced when there is none), and every job ended "game exited" in half a second until the companion restarted:
        // a remembered window that no longer exists is replaced at the start of a job
        if (_gameIo.Identity is null || !_gameIo.GetState().Exists)
        {
            var c = _gameIo.Connect(rs.Ct);
            if (!c.Ok)
            {
                switch (c.Problem)
                {
                    case ConnectProblem.NotRunning or ConnectProblem.WindowNotFound:
                        throw new EndSignal(StepOutcome.GameExited, "connect." + c.Problem);
                    case ConnectProblem.IntegrityMismatch:
                        throw new HaltSignal(HaltCode.IntegrityMismatch, "connect", null, StepOutcome.IntegrityMismatch);
                    default:
                        throw new EndSignal(StepOutcome.Refused, "connect." + c.Problem);
                }
            }
        }
        rs.Identity = _gameIo.Identity;
        _log.Poll();
        rs.StartLog = _log.Position;
        rs.LogFile = rs.StartLog?.File;
        rs.LastWall = _clock.UtcNow;
        rs.LastMono = _clock.Monotonic;
    }

    // ------------------------------------------------------------------ status / journal / notifications

    private void SetStatus(EngineState state, RunState? rs, string? waitingFor)
    {
        lock (_statusLock)
        {
            if (_status.State == state && _status.StepId == rs?.CurrentStep && _status.WaitingFor == waitingFor && _status.JobId == rs?.Ctx.JobId)
            {
                return;
            }
            _status = new EngineStatus(state, rs?.Ctx.JobId, rs?.CurrentProcedure, rs?.CurrentStep, waitingFor, _clock.UtcNow);
        }
    }

    private void Write(RunState rs, JournalKind kind, string? stepId, params (string Key, object? Value)[] fields)
    {
        try
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (k, v) in fields)
            {
                dict[k] = JournalSchema.Sanitize(v, out _);
            }
            var rec = new JournalRecord(_clock.UtcNow, kind, JournalSchema.Code(rs.Ctx.JobId), JournalSchema.Code(rs.CurrentProcedure), JournalSchema.Code(stepId), dict);
            _journal.Write(rec);
            if (rs.JournalTail.Count < 2000)
            {
                rs.JournalTail.Add(rec);
            }
        }
        catch
        {
            // the journal never throws into control flow
        }
    }

    private void FlushJournal()
    {
        try
        {
            _journal.Flush();
        }
        catch
        {
            // the journal never throws into control flow
        }
    }

    private void Notify(RunState rs, Notification n, bool oncePerJob)
    {
        try
        {
            if (oncePerJob && !rs.Job.Notified.Add(n.Code))
            {
                return;
            }
            _notifier.Notify(n);
            Write(rs, JournalKind.Notification, rs.CurrentStep, ("code", n.Code), ("kind", n.Kind));
        }
        catch
        {
            // notifications are best effort
        }
    }

    private void JournalAutoCollect(RunState rs, string when)
    {
        try
        {
            var s = _log.State.AutoCollectStatus;
            Write(rs, JournalKind.AutoCollect, null, ("when", when), ("status", s.HasValue ? s.Value : null), ("known", s.HasValue));
        }
        catch
        {
            // ignore
        }
    }

    // ------------------------------------------------------------------ HALT

    private HaltReport DoHalt(RunState rs, HaltSignal h)
    {
        _late = null; // the halt covers the last quick click; never read again after the user clears it
        SetStatus(EngineState.Halted, rs, h.Code.ToString());
        if (h.EscForbidden)
        {
            try
            {
                CloseEsc(rs, h.EscUi, compensation: true);
            }
            catch
            {
                // compensation is best effort; the HALT goes on
            }
        }
        var report = new HaltReport(h.Code, JournalSchema.IsCode(h.Subject) ? h.Subject : null, rs.Ctx.JobId, rs.CurrentProcedure, h.StepId, _clock.UtcNow, null);
        Write(rs, JournalKind.Halt, h.StepId, ("code", h.Code), ("subject", report.Subject), ("outcome", h.Outcome),
            ("inputs", rs.Inputs), ("clicks", rs.Clicks), ("escs", rs.Escs), ("slideClick", SafeSlideSwitch()));
        string? diag = null;
        try
        {
            diag = WriteDiagnostics(rs, report);
        }
        catch
        {
            diag = null;
        }
        report = report with { DiagnosticsPath = diag };
        try
        {
            _haltLatch.Halt(report);
        }
        catch
        {
            // the latch implementation reports its own failures
        }
        try
        {
            _notifier.Notify(new Notification(NotificationKind.Halt, "halt." + h.Code, Messages.HaltText(h.Code, report.Subject)));
        }
        catch
        {
            // best effort
        }
        try
        {
            _stop.RequestStop(StopSource.Engine, "halt." + h.Code);
        }
        catch
        {
            // best effort
        }
        return report;
    }

    private bool SafeSlideSwitch()
    {
        try
        {
            return _switches.SlideClickEnabled;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Diagnostics bundle (SPEC 10.4): halt.json, attempts, journal tail, filtered log excerpt, masked crops.</summary>
    private string? WriteDiagnostics(RunState rs, HaltReport report)
    {
        var stamp = report.AtUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var name = SafeFiles.SanitizeName(stamp + "-" + report.Code);
        var catDir = _paths.DiagnosticsDir;
        if (!SafeFiles.EnsureDirectory(_paths.RuntimeDir, catDir))
        {
            return null;
        }
        var (maxEntries, maxBytes) = ArtifactStore.Caps(ArtifactCategory.Diagnostics);
        SafeFiles.Prune(catDir, maxEntries - 1, maxBytes - (2L * 1024 * 1024), null);
        var dir = Path.Combine(catDir, name);
        if (!SafeFiles.EnsureDirectory(_paths.RuntimeDir, dir))
        {
            return null;
        }
        var withPath = report with { DiagnosticsPath = dir };
        SafeFiles.WriteNew(dir, "halt.json", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(withPath, ContractJson.Options));
        var attempts = rs.Attempts.Select(a => a with
        {
            UiSeen = JournalSchema.IsCode(a.UiSeen) ? a.UiSeen : JournalSchema.Redacted,
            DetailCode = JournalSchema.IsCode(a.DetailCode) ? a.DetailCode : JournalSchema.Redacted,
        }).ToList();
        SafeFiles.WriteNew(dir, "attempts.json", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(attempts, ContractJson.Options));
        var sb = new System.Text.StringBuilder();
        foreach (var r in rs.JournalTail)
        {
            sb.Append(Journal.Serialize(r, out _)).Append('\n');
        }
        SafeFiles.WriteNew(dir, "journal.jsonl", System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        try
        {
            if (rs.StartLog is { } from && _log.Position is { } to && from.SameFile(to))
            {
                var lines = _log.ReadFilteredExcerpt(from, to, ArtifactStore.MaxExcerptLines);
                _artifacts.SaveLogExcerpt(ArtifactCategory.Diagnostics, name + "/log", lines);
            }
        }
        catch
        {
            // excerpt is optional
        }
        if (rs.LastFrame is { } frame)
        {
            foreach (var id in rs.TemplatesUsed.Take(8))
            {
                try
                {
                    var def = _templates.Get(id);
                    if (def is null)
                    {
                        continue;
                    }
                    var rect = CropRect(def);
                    if (rect is null)
                    {
                        continue;
                    }
                    var png = _vision.EncodePng(frame, rect.Value, [HudTemplates.ChatPanel]);
                    _artifacts.SaveImage(ArtifactCategory.Diagnostics, name + "/" + id, png);
                }
                catch
                {
                    // crops are optional
                }
            }
        }
        return dir;
    }

    /// <summary>Template-sized crop (template rect inflated by its radius; templates are at most 64 px).</summary>
    private static RectI? CropRect(TemplateDefinition def)
    {
        var half = Math.Clamp(def.SearchRadius, 0, HardRules.MaxSearchRadiusPx) + 32;
        var r = RectI.CenteredAt(def.ExpectedCenter, half * 2, half * 2);
        return r.Intersect(HudTemplates.Client);
    }

    // ------------------------------------------------------------------ small helpers

    private TimeSpan Mono => _clock.Monotonic;

    private DateTime GameNow => _clock.UtcNow.ToLocalTime().DateTime;

    private static DateTime GameTime(DateTimeOffset utc) => utc.ToLocalTime().DateTime;

    private void Sleep(RunState rs, TimeSpan d)
    {
        if (d > TimeSpan.Zero)
        {
            _clock.Sleep(d, rs.Ct);
        }
    }

    private static bool ContainsRestore(ProcedureDefinition p) => p.Steps.Any(s => s.Action is RestoreHudAction);

    private bool IsCommitJob(RunState rs, ProcFrame? f) =>
        rs.Ctx.Kind == JobKind.Commit || rs.Root.Kind == ProcedureKind.Commit || f?.Def.Kind == ProcedureKind.Commit;

    private string UiStateHash(ProcFrame f, int i)
    {
        var ui = _log.State;
        var s = string.Join(",", ui.AdvisoryStack) + "|" + ui.Busy + "|" + f.Restarts + "|" + f.StepBacks[i];
        unchecked
        {
            uint h = 2166136261;
            foreach (var ch in s)
            {
                h = (h ^ ch) * 16777619;
            }
            return h.ToString("x8", CultureInfo.InvariantCulture);
        }
    }
}
