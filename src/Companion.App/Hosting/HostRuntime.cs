using Companion.Contracts.Host;
using System.Globalization;
using System.Diagnostics;
using System.Security.Principal;
using Companion.App.Jobs;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.Dailies;
using Companion.Logs;

namespace Companion.App.Hosting;

/// <summary>
/// The running host (SPEC 4 steps 3-9): secures RuntimeDir, loads config and the HALT latch, checks the trusted build,
/// constructs the graph, loads data once, starts the log tracker (reads only), the knowledge store, the guardian, the
/// pipe server, the heartbeat and the engine thread. Owned by the tray (UI thread); everything game-facing runs on the
/// engine thread.
/// </summary>
internal sealed class HostRuntime : IDisposable
{
    private static readonly TimeSpan LogPollPeriod = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(1);

    private readonly CompanionPaths _paths;
    private readonly IClock _clock;
    private readonly SecurityIdentifier _user;
    private readonly object _pumpLock = new();
    private System.Threading.Timer? _logTimer;
    private System.Threading.Timer? _heartbeatTimer;
    private PipeServer? _pipe;
    private LogPosition? _pumpPosition;
    private int _pumpRotations = -1;
    private int _shutdownStarted;
    private DateTimeOffset _dailiesSaved;
    private DateTimeOffset _todayWritten;
    private bool _disposed;

    /// <summary>Creates the runtime (no side effects).</summary>
    public HostRuntime(CompanionPaths paths, IClock clock, SecurityIdentifier interactiveUser)
    {
        _paths = paths;
        _clock = clock;
        _user = interactiveUser;
    }

    /// <summary>Composed services (after <see cref="Start"/>).</summary>
    public HostServices? Services { get; private set; }

    /// <summary>Job host (after <see cref="Start"/>).</summary>
    public JobHost? Jobs { get; private set; }

    /// <summary>The daily checklist (2026-11-04); null when its data could not be loaded.</summary>
    public DailyService? Dailies { get; private set; }

    /// <summary>The checklist's runtime folder (state, prefs, today.json).</summary>
    public string DailiesDir => Path.Combine(_paths.RuntimeDir, "dailies");

    /// <summary>Data load report.</summary>
    public DataLoadReport? DataReport { get; private set; }

    /// <summary>Trust comparison at start.</summary>
    public TrustComparison? Trust { get; private set; }

    /// <summary>Problem that blocks jobs (RuntimeDir, core data), or null.</summary>
    public string? BlockedReason { get; private set; }

    /// <summary>The real trust checker (tray "Trust this build...").</summary>
    public FileTrust? FileTrust => Services?.FileTrust as FileTrust;

    /// <summary>Runs the start sequence (steps 3-9). Never touches the game except the marker-proven crash recovery.</summary>
    public void Start()
    {
        // 3. RuntimeDir with its protected ACL; refuse reparse points.
        var runtimeProblem = RuntimeDirectory.EnsureProtected(_paths.RuntimeDir, _user);

        // 6. Construct (no side effects). The game build code is file metadata only.
        var services = Composition.Create(_paths, _clock, Composition.GameBuildCode(_paths));
        Services = services;

        // 4. Config (missing = defaults, automation off) and the HALT latch.
        var config = services.Config.Load();
        services.Halt.Load();

        // 5. Trusted build (hash every install-side file).
        var fileTrust = (FileTrust)services.FileTrust;
        Trust = fileTrust.ScanAndCompare();

        // 7. Load data once, then seal.
        DataReport = CoreDataLoader.Load(services, Composition.CompiledFeatures(services.TeaHouse), config);

        BlockedReason = runtimeProblem ?? (DataReport.CoreOk ? null : DataReport.Problems.FirstOrDefault() ?? "core.data");

        var jobs = new JobHost(new JobHostDeps(
            services.Features.Jobs,
            _clock,
            services.Stop,
            services.Pause,
            services.Halt,
            services.Runner,
            services.Journal,
            services.FileTrust,
            services.Knowledge,
            services.Config,
            () => services.GameIo.GameWindowPresent(), // incident 28: a closed game's window is not "connected"; incident 39: a reopened one is, before a job binds it
            new HostJobContextFactory(services),
            services.Activity));
        Jobs = jobs;

        JournalStart(services, config);
        JournalPreviousRun(services);
        StartDailies(services, jobs);

        // 8. Log tracker (reads only), knowledge, guardian, game connect (only if running), crash recovery.
        try
        {
            services.Log.Bootstrap(CancellationToken.None);
        }
        catch (Exception)
        {
            // Bootstrap reports I/O problems in its result; anything else must not stop the host
        }

        var knowledgeReport = SafeLoadKnowledge(services);
        SpawnGuardian();
        if (IsGameRunning())
        {
            try
            {
                services.GameIo.Connect(CancellationToken.None);
            }
            catch (Exception)
            {
                // reconnect lazily at the next job
            }
        }

        try
        {
            var recovery = services.GameIo.RecoverLeftoverDisplacement();
            if (recovery.Attempted)
            {
                services.Notifier.Notify(HostMessages.Host(recovery.Success ? "host.recovered" : "host.leftover"));
            }
        }
        catch (Exception)
        {
            // recovery problems are journaled by GameIO
        }

        // 9. Pipe server, heartbeat, log poll timer, engine thread.
        jobs.MarkStarted(BlockedReason);
        _pipe = new PipeServer(jobs.Handle, services.Journal, _clock, _user);
        _pipe.Start();
        _heartbeatTimer = new System.Threading.Timer(_ => SafeBeat(), null, TimeSpan.Zero, HeartbeatPeriod);
        _logTimer = new System.Threading.Timer(_ => PumpLog(), null, LogPollPeriod, LogPollPeriod);
        jobs.StartEngine();

        // Start-up notifications (fixed texts, de-duplicated).
        if (runtimeProblem is not null)
        {
            services.Notifier.Notify(HostMessages.Host("host.runtimeDir"));
        }
        else if (!DataReport.CoreOk)
        {
            services.Notifier.Notify(HostMessages.Host("host.coreDataBroken"));
        }
        else if (!Trust.Trusted)
        {
            services.Notifier.Notify(HostMessages.Host("host.untrusted"));
        }

        if (services.Config.LoadProblem is not null)
        {
            services.Notifier.Notify(HostMessages.Host("config.invalid"));
        }

        if (DataReport.Features.Any(r => r.Status != FeatureLoadStatus.Enabled && r.Status != FeatureLoadStatus.DisabledByConfig))
        {
            services.Notifier.Notify(HostMessages.Host("host.feature"));
        }

        if (knowledgeReport is { Ok: false })
        {
            services.Notifier.Notify(HostMessages.Host("knowledge.recovered"));
        }

        services.Journal.Flush();
    }

    /// <summary>Exit: stop, end the engine thread, restore if a marker proves a displacement, then dispose.</summary>
    public void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        var s = Services;
        var jobs = Jobs;
        var engineJoined = true;
        if (jobs is not null)
        {
            jobs.StopAll(StopSource.Tray, "host.exit");
            engineJoined = jobs.StopEngine(TimeSpan.FromSeconds(10));
        }

        if (s is not null)
        {
            // an engine thread that did not stop may hold the game I/O lock (a slide in flight): the guardian runs the same
            // marker recovery after the exit, so the exit never blocks on it (audit R3.5 #45)
            if (engineJoined)
            {
                try
                {
                    s.GameIo.RecoverLeftoverDisplacement();
                }
                catch (Exception)
                {
                    // the guardian runs the same recovery after we exit
                }
            }

            SaveDailies(force: true);
            try
            {
                s.Journal.Write(new JournalRecord(_clock.UtcNow, JournalKind.HostStop, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal) { ["event"] = "exit", ["engineJoined"] = engineJoined }));
                s.Journal.Flush();
            }
            catch (Exception)
            {
                // best effort
            }
        }

        HostAliveMarker.Delete(_paths.RuntimeDir); // a clean exit: the next start finds no marker
        Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logTimer?.Dispose();
        _heartbeatTimer?.Dispose();
        _pipe?.Dispose();
        Jobs?.Dispose();
    }

    private void SafeBeat()
    {
        try
        {
            Jobs?.Beat();
        }
        catch (Exception)
        {
            // the heartbeat never stops
        }

        try
        {
            // audit R1.11 (rule 5): the journal was flushed only after engine step decisions, so a pause, a Stop or a
            // long wait sat in memory for minutes and a process death lost it; now at most a second is lost
            Services?.Journal.Flush();
            if (++_beats % MarkerBeats == 0 && Volatile.Read(ref _shutdownStarted) == 0)
            {
                HostAliveMarker.Write(_paths.RuntimeDir, FileTrust?.Scanned?.ShortHash, _clock.UtcNow);
                TrackAway();
            }
        }
        catch (Exception)
        {
            // the heartbeat never stops
        }
    }

    /// <summary>Heartbeats between refreshes of the run marker's last-beat time.</summary>
    private const int MarkerBeats = 5;

    /// <summary>An absence from the game this long gets a summary when the user comes back (roadmap A).</summary>
    internal static readonly TimeSpan AwayMinimum = TimeSpan.FromMinutes(20);

    private DateTimeOffset? _awayStart;
    private (DateTimeOffset From, DateTimeOffset To, string Text)? _lastAway;
    private readonly object _awayGate = new();

    /// <summary>
    /// Roadmap A ("while you were away"), every 5 heartbeats: the game not in front for a while is an absence; when it is in
    /// front again after at least <see cref="AwayMinimum"/>, what the companion did in between (from the journal) becomes
    /// one notification and the card on the Activity tab. Read-only: the game's foreground time from the passive sampler.
    /// </summary>
    private void TrackAway()
    {
        var s = Services;
        if (s?.GameIo.SinceGameForeground is not { } since)
        {
            return;
        }

        var now = _clock.UtcNow;
        string? text = null;
        DateTimeOffset start = default;
        lock (_awayGate)
        {
            if (since >= TimeSpan.FromSeconds(10))
            {
                _awayStart ??= now - since; // when the user left the game
                return;
            }

            if (_awayStart is { } left && now - left >= AwayMinimum)
            {
                start = left;
                text = AwayText(s, left, now);
                if (text is not null)
                {
                    _lastAway = (left, now, text);
                }
            }

            _awayStart = null;
        }

        if (text is not null)
        {
            s.Notifier.Notify(new Contracts.Host.Notification(Contracts.Host.NotificationKind.Info, "away.summary",
                "While you were away (" + start.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " to "
                + now.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "): " + text));
        }
    }

    /// <summary>
    /// The card on the Activity tab: what the companion has done so far in the current absence (20 min or more), or the
    /// summary of the latest absence (shown for 12 hours); null when there is nothing to say.
    /// </summary>
    public string? AwayCard()
    {
        var s = Services;
        if (s is null)
        {
            return null;
        }

        var now = _clock.UtcNow;
        DateTimeOffset? current;
        (DateTimeOffset From, DateTimeOffset To, string Text)? last;
        lock (_awayGate)
        {
            current = _awayStart;
            last = _lastAway;
        }

        string Hm(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        if (current is { } left && now - left >= AwayMinimum && AwayText(s, left, now) is { } sofar)
        {
            return "While you are away (since " + Hm(left) + "): " + sofar;
        }

        return last is { } l && now - l.To < TimeSpan.FromHours(12) ? "While you were away (" + Hm(l.From) + " to " + Hm(l.To) + "): " + l.Text : null;
    }

    private static string? AwayText(HostServices s, DateTimeOffset from, DateTimeOffset to)
    {
        var names = new Dictionary<long, string>();
        try
        {
            if (s.Knowledge.Profile is { } knowledge)
            {
                foreach (var i in knowledge.GetIconItems())
                {
                    names.TryAdd(i.ItemId, i.Name);
                }

                foreach (var r in knowledge.GetRecipes().Where(r => r.ProductItemId is not null))
                {
                    names.TryAdd(r.ProductItemId!.Value, r.Product.Value);
                }
            }
        }
        catch (Exception)
        {
            // names are a nicety: the counts stand without them
        }

        return Tray.JournalStats.From(Tray.JournalStats.Lines(s.Paths.JournalDir, from, to), from, to).Summary(id => names.GetValueOrDefault(id));
    }

    private long _beats;

    /// <summary>
    /// 500 ms log poll (reads only). Host-side reactions, posted to the engine thread (the knowledge store's only writer):
    /// a rotation marks all stock stale; an autoeat line marks food stale; a RecoverFocus while no job runs is the user
    /// playing, which marks all stock stale. Which character is logged in does not matter (one profile, 4.33).
    /// </summary>
    private void PumpLog()
    {
        var s = Services;
        var jobs = Jobs;
        if (s is null || jobs is null || !Monitor.TryEnter(_pumpLock))
        {
            return;
        }

        try
        {
            s.Log.Poll();
            try
            {
                s.GameIo.ResyncFocus(); // the user's clicks work again when a stale fake unfocus left the game "unfocused"
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // never break the log pump
            }

            var health = s.Log.Health;
            var now = _clock.UtcNow;

            if (_pumpRotations >= 0 && health.Rotations != _pumpRotations)
            {
                jobs.Post(() => MarkStale(s, StaleScope.AllStock, "log.rotated", now));
                _pumpPosition = null;
            }

            _pumpRotations = health.Rotations;

            if (_pumpPosition is null)
            {
                _pumpPosition = s.Log.Position;
            }
            else
            {
                var events = s.Log.EventsSince(_pumpPosition);
                if (events.Count > 0)
                {
                    _pumpPosition = events[^1].Position;
                }

                if (events.Count > 0)
                {
                    Dailies?.Feed(events); // the daily checklist (reads only)
                }

                // a chat message to the user arrived (2026-10-20): a Windows notification per message, never merged
                foreach (var m in events.OfType<MessageArrivedLogEvent>())
                {
                    s.Notifier.Notify(new Notification(
                        NotificationKind.NeedsUser,
                        "chat.message." + m.StartOffset.ToString(CultureInfo.InvariantCulture),
                        "New message for you in Tree of Savior (" + m.Timestamp.ToString("HH:mm", CultureInfo.InvariantCulture) + ")."));
                }

                if (events.Any(e => e is AutoEatLogEvent))
                {
                    jobs.Post(() => MarkStale(s, StaleScope.Food, "autoeat", now));
                }

                if (!jobs.IsJobRunning && events.Any(e => e is FocusLogEvent { Recovered: true }))
                {
                    jobs.Post(() => MarkStale(s, StaleScope.AllStock, "user.foreground", now));
                }
            }
        }
        catch (Exception)
        {
            // the poll timer never stops; the tracker reports I/O problems itself
        }
        finally
        {
            Monitor.Exit(_pumpLock);
        }

        SaveDailies(force: false);
        AlertDailies();
    }

    /// <summary>
    /// The checklist's tray messages (learning day, entries 3/4/24): an event opening (the raccoons, the world boss, the
    /// spa...) or a homestead producer's countdown ending. Shown only while the game is not the window in front (the
    /// user sees the game then); never activates or touches the game. Read-only: the passive foreground sampler.
    /// </summary>
    private void AlertDailies()
    {
        var d = Dailies;
        var s = Services;
        if (d is null || s is null)
        {
            return;
        }

        try
        {
            var alerts = d.TakeAlerts();
            if (alerts.Count == 0)
            {
                return;
            }

            var inFront = s.GameIo.SinceGameForeground is { } since && since < TimeSpan.FromSeconds(3);
            if (inFront)
            {
                return;
            }

            foreach (var (alert, text) in alerts)
            {
                var stamp = alert.Until?.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)
                    ?? _clock.UtcNow.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
                s.Notifier.Notify(new Notification(NotificationKind.Info, "dailies." + alert.ItemId + "." + stamp, text));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // a reminder is never worth breaking the poll
        }
    }

    /// <summary>
    /// The daily checklist (DAILIES_MODULE.md): its data, its saved state and the user's list choices; then a start-up
    /// replay of today's part of the game log on a worker thread (live events wait meanwhile). Reads only.
    /// </summary>
    private void StartDailies(HostServices services, JobHost jobs)
    {
        try
        {
            if (CoreDataLoader.LoadDailies(services, out var loadProblem) is not { } catalog)
            {
                JournalDailies(services, loadProblem ?? "dailies.unavailable");
                return;
            }

            var svc = new DailyService(catalog, ReadIfExists("state.json"), () => _clock.UtcNow.LocalDateTime, ReadIfExists("prefs.json"));
            Dailies = svc;
            jobs.JobFinished += (name, state) => svc.OnJob(name, state.ToString());
            if (svc.StateProblem is { } problem)
            {
                JournalDailies(services, problem);
            }

            var from = svc.ReplayFrom;
            var logDir = Path.GetDirectoryName(_paths.GameLogPath) ?? string.Empty;
            _ = Task.Run(() =>
            {
                List<LogEvent> replayed = [];
                try
                {
                    replayed = [.. new[] { "log.bak2", "log.bak1", "log.bak", "log.txt" }
                        .Select(f => LogReplay.Read(Path.Combine(logDir, f), from))
                        .Where(l => l.Count > 0)
                        .OrderBy(l => l[0].Timestamp)
                        .SelectMany(l => l)];
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    JournalDailies(services, "dailies.replayFailed");
                }

                svc.CompleteReplay(replayed);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Dailies = null;
            JournalDailies(services, "dailies.unavailable");
        }
    }

    private byte[]? ReadIfExists(string name) => RuntimeFiles.ReadIfExists(Path.Combine(DailiesDir, name));

    /// <summary>
    /// Writes the checklist's state and prefs when they changed (at most once a second) and its readable today.json
    /// with them or every minute (the day can change while nothing happens). <paramref name="force"/> at exit.
    /// </summary>
    private void SaveDailies(bool force)
    {
        var d = Dailies;
        if (d is null)
        {
            return;
        }

        var now = _clock.UtcNow;
        if (!force && now - _dailiesSaved < TimeSpan.FromSeconds(1))
        {
            return;
        }

        try
        {
            var state = d.TakeStateToSave();
            var prefs = d.TakePrefsToSave();
            var periodic = now - _todayWritten >= TimeSpan.FromMinutes(1);
            if (state is null && prefs is null && !periodic && !force)
            {
                return;
            }

            _dailiesSaved = now;
            if (!SafeFile.EnsureSafeDirectory(_paths.RuntimeDir, DailiesDir))
            {
                return;
            }

            if (state is not null)
            {
                SafeFile.WriteAtomic(_paths.RuntimeDir, Path.Combine(DailiesDir, "state.json"), state);
            }

            if (prefs is not null)
            {
                SafeFile.WriteAtomic(_paths.RuntimeDir, Path.Combine(DailiesDir, "prefs.json"), prefs);
            }

            SafeFile.WriteAtomic(_paths.RuntimeDir, Path.Combine(DailiesDir, "today.json"), d.TodayFile());
            _todayWritten = now;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // the next save tries again; the checklist keeps working in memory
        }
    }

    private void JournalDailies(HostServices s, string code)
    {
        try
        {
            s.Journal.Write(new JournalRecord(_clock.UtcNow, JournalKind.Incident, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = code }));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // best effort
        }
    }

    private static void MarkStale(HostServices s, StaleScope scope, string reason, DateTimeOffset at)
    {
        var k = s.Knowledge.Profile;
        k.MarkStale(scope, null, reason, at);
        k.Save();
    }


    private static KnowledgeLoadReport? SafeLoadKnowledge(HostServices s)
    {
        try
        {
            return s.Knowledge.Load();
        }
        catch (Exception)
        {
            return new KnowledgeLoadReport(false, 0, ["knowledge.loadFailed"]);
        }
    }

    private static bool IsGameRunning()
    {
        try
        {
            var found = Process.GetProcessesByName("TOS");
            var any = found.Length > 0;
            foreach (var p in found)
            {
                p.Dispose();
            }

            return any;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void SpawnGuardian()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null)
            {
                return;
            }

            using var self = Process.GetCurrentProcess();
            var args = new GuardianArgs(Environment.ProcessId, self.StartTime.ToUniversalTime().Ticks);
            var info = new ProcessStartInfo(exe, args.ToArguments())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            using var child = Process.Start(info);
        }
        catch (Exception)
        {
            // Without a guardian the slide marker still lets the next host start recover.
            Services?.Journal.Write(new JournalRecord(_clock.UtcNow, JournalKind.HostStart, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal) { ["guardian"] = "spawnFailed" }));
        }
    }

    /// <summary>
    /// Audit R1.12 (rule 5, incident 12): a run marker left by a host that no longer runs is an exit nothing recorded (a
    /// killed process tree, a power loss): journaled as host.previousUnclean with its values; then this host's marker.
    /// </summary>
    private void JournalPreviousRun(HostServices s)
    {
        try
        {
            if (HostAliveMarker.TakeLeftover(_paths.RuntimeDir, HostAliveMarker.IsAlive) is { } prev)
            {
                s.Journal.Write(new JournalRecord(_clock.UtcNow, JournalKind.Incident, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["event"] = "host.previousUnclean",
                    ["pid"] = HostAliveMarker.Number(prev, "pid"),
                    ["lastBeatUnix"] = HostAliveMarker.Number(prev, "lastBeatUnix"),
                    ["buildHash"] = prev.TryGetValue("buildHash", out var hash) ? hash : null,
                }));
                s.Journal.Flush();
            }
        }
        catch (Exception)
        {
            // best effort
        }

        HostAliveMarker.Write(_paths.RuntimeDir, FileTrust?.Scanned?.ShortHash, _clock.UtcNow);
    }

    private void JournalStart(HostServices s, Contracts.Host.CompanionConfig config)
    {
        try
        {
            s.Journal.Write(new JournalRecord(_clock.UtcNow, JournalKind.HostStart, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["apiVersion"] = (long)ContractsInfo.ApiVersion,
                ["trust"] = Trust?.Code,
                ["buildHash"] = FileTrust?.Scanned?.ShortHash,
                ["coreData"] = DataReport?.CoreOk,
                ["blocked"] = BlockedReason,
                ["crafting"] = config.Automation.Crafting,
                ["homestead"] = config.Automation.Homestead,
                ["slideClick"] = config.Input.SlideClick,
                ["features"] = (long)(DataReport?.Features.Count(r => r.Status == FeatureLoadStatus.Enabled) ?? 0),
            }));
        }
        catch (Exception)
        {
            // best effort
        }
    }
}
