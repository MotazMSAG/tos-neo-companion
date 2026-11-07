using System.Text.Json;
using System.Text.RegularExpressions;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Host;
using Companion.Contracts.Navigation;
using Companion.Contracts.Perception;
using Companion.Contracts.Vision;
using Companion.Features.Kit;

namespace Companion.Features;

/// <summary>
/// Feature host (unit "features", SPEC 11.4). <see cref="LoadAll"/> runs once at host start:
/// <list type="number">
/// <item>the built-in core feature is always first and always enabled;</item>
/// <item>manifests are validated (API version, ids, semver, data paths, duplicates) and config-disabled ones skipped;</item>
/// <item>features are ordered by their dependencies (deterministic topological order); a missing, disabled or too
/// old dependency, or a cycle, disables the dependent;</item>
/// <item>each Register runs on a worker with a 5 s budget into a staging registrar (a throw or timeout disables the
/// feature and nothing it registered is kept);</item>
/// <item>declared data files are read once through <see cref="IFileTrust.ReadTrusted"/> (missing or mismatched =
/// Untrusted) and deserialized with <see cref="ContractJson.Options"/>;</item>
/// <item>everything is validated (duplicates against the builders, commit gating, job and parameter specs) BEFORE
/// anything is added to a builder; then templates, ui-map, readers and procedures are added and the jobs join the
/// whitelist.</item>
/// </list>
/// Handlers are wrapped: an exception fails the job InternalError, three exceptions fault the feature and its jobs
/// leave the whitelist. The constructor has no side effects.
/// </summary>
public sealed class FeatureHost : IFeatureHost
{
    private const int HandlerThrowsToFault = 3;
    private const int MaxRegisterBudgetMs = 5000;

    private static readonly Regex ItemId = new(@"^[A-Za-z][A-Za-z0-9_.\-]{0,63}$", RegexOptions.CultureInvariant);

    private readonly IClock _clock;
    private readonly string? _currentGameBuild;
    private readonly TimeSpan _registerBudget;
    private readonly CoreFeature _core = new();
    private readonly object _gate = new();
    private readonly JobCatalog _jobs = new();
    private readonly Dictionary<string, int> _handlerThrows = new(StringComparer.Ordinal);
    private List<FeatureLoadReport> _reports = [];
    private IJournal? _journal;
    private bool _loaded;

    /// <summary>Creates the host (stores dependencies only).</summary>
    /// <param name="clock">Clock (journal timestamps).</param>
    /// <param name="currentGameBuild">
    /// Current game build fingerprint code (<c>GameBuildFingerprint.Code</c>), if known. A feature whose
    /// <c>ValidatedGameBuild</c> differs from it, or any feature declaring one while it is unknown, gets its templates
    /// flipped to Provisional (not clickable).
    /// </param>
    /// <param name="registerBudgetMs">Register budget per feature in milliseconds (1..5000, default 5000).</param>
    public FeatureHost(IClock clock, string? currentGameBuild = null, int registerBudgetMs = MaxRegisterBudgetMs)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _currentGameBuild = currentGameBuild;
        _registerBudget = TimeSpan.FromMilliseconds(Math.Clamp(registerBudgetMs, 1, MaxRegisterBudgetMs));
    }

    /// <inheritdoc />
    public IJobCatalog Jobs => _jobs;

    /// <inheritdoc />
    public IReadOnlyList<FeatureLoadReport> Reports
    {
        get
        {
            lock (_gate)
            {
                return _reports.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<FeatureLoadReport> LoadAll(IReadOnlyList<IFeature> features, FeatureLoadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            if (_loaded)
            {
                throw new InvalidOperationException("Features are loaded once, at host start.");
            }

            _loaded = true;
            _journal = context.Journal;
        }

        var list = new List<IFeature?> { _core };
        foreach (var f in features ?? [])
        {
            if (f is not CoreFeature)
            {
                list.Add(f);
            }
        }

        var reports = new FeatureLoadReport?[list.Count];
        var candidates = new List<Candidate>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < list.Count; i++)
        {
            var f = list[i];
            if (f is null)
            {
                reports[i] = new FeatureLoadReport("null", "0.0.0", FeatureLoadStatus.Failed, "feature.null");
                continue;
            }

            FeatureManifest? m;
            try
            {
                m = f.Manifest;
            }
            catch (Exception ex)
            {
                reports[i] = new FeatureLoadReport("unknown", "0.0.0", FeatureLoadStatus.Failed, "manifest.threw:" + Codes.Safe(ex.GetType().Name));
                continue;
            }

            if (m is null)
            {
                reports[i] = new FeatureLoadReport("unknown", "0.0.0", FeatureLoadStatus.Failed, "manifest.null");
                continue;
            }

            var id = Codes.Safe(m.Id);
            var version = m.Version is not null && ManifestRules.SemVer.IsMatch(m.Version) ? m.Version : "0.0.0";
            if (m.CoreApiVersion != ContractsInfo.ApiVersion)
            {
                reports[i] = new FeatureLoadReport(id, version, FeatureLoadStatus.ApiMismatch, "api.mismatch:" + m.CoreApiVersion);
                continue;
            }

            var invalid = ManifestRules.Validate(m);
            if (invalid is not null)
            {
                reports[i] = new FeatureLoadReport(id, version, FeatureLoadStatus.Failed, invalid);
                continue;
            }

            if (!seenIds.Add(m.Id))
            {
                reports[i] = new FeatureLoadReport(id, version, FeatureLoadStatus.Failed, "manifest.duplicateId");
                continue;
            }

            if (i != 0 && !IsEnabledByConfig(context.Config, m.Id))
            {
                reports[i] = new FeatureLoadReport(id, version, FeatureLoadStatus.DisabledByConfig, "config.disabled");
                continue;
            }

            candidates.Add(new Candidate(i, f, m));
        }

        var (ordered, cyclic) = DependencyOrder(candidates);
        foreach (var c in cyclic)
        {
            reports[c.Index] = Report(c.Manifest, FeatureLoadStatus.DependencyUnavailable, "dependency.cycle");
        }

        var enabled = new Dictionary<string, FeatureManifest>(StringComparer.Ordinal);
        foreach (var c in ordered)
        {
            var depProblem = CheckDependencies(c.Manifest, seenIds, enabled);
            var report = depProblem is not null
                ? Report(c.Manifest, FeatureLoadStatus.DependencyUnavailable, depProblem)
                : LoadOne(c, context);
            reports[c.Index] = report;
            if (report.Status == FeatureLoadStatus.Enabled)
            {
                enabled[c.Manifest.Id] = c.Manifest;
            }
        }

        var final = reports.Select(r => r!).ToList();
        lock (_gate)
        {
            _reports = final;
        }

        foreach (var r in final)
        {
            JournalReport(r);
        }

        return final.ToArray();
    }

    /// <inheritdoc />
    public void Fault(string featureId, string reasonCode)
    {
        FeatureLoadReport? faulted = null;
        lock (_gate)
        {
            var idx = _reports.FindIndex(r => string.Equals(r.FeatureId, featureId, StringComparison.Ordinal));
            if (idx < 0 || _reports[idx].Status != FeatureLoadStatus.Enabled)
            {
                return;
            }

            faulted = _reports[idx] with { Status = FeatureLoadStatus.Faulted, ReasonCode = Codes.Safe(reasonCode) };
            _reports[idx] = faulted;
        }

        _jobs.Fault(featureId);
        JournalReport(faulted);
    }

    private static bool IsEnabledByConfig(CompanionConfig config, string featureId) =>
        config.Features is null || !config.Features.TryGetValue(featureId, out var on) || on;

    private static FeatureLoadReport Report(FeatureManifest m, FeatureLoadStatus status, string? reason) =>
        new(m.Id, m.Version, status, reason is null ? null : Codes.Safe(reason));

    /// <summary>Kahn's algorithm, ties by input order (core first). Nodes left over are on or behind a cycle.</summary>
    private static (List<Candidate> Ordered, List<Candidate> Cyclic) DependencyOrder(List<Candidate> candidates)
    {
        var byId = candidates.ToDictionary(c => c.Manifest.Id, StringComparer.Ordinal);
        var indegree = candidates.ToDictionary(c => c.Manifest.Id, _ => 0, StringComparer.Ordinal);
        var dependents = candidates.ToDictionary(c => c.Manifest.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            foreach (var d in c.Manifest.Requires)
            {
                if (byId.ContainsKey(d.FeatureId))
                {
                    indegree[c.Manifest.Id]++;
                    dependents[d.FeatureId].Add(c.Manifest.Id);
                }
            }
        }

        var ready = new SortedSet<int>(candidates.Where(c => indegree[c.Manifest.Id] == 0).Select(c => c.Index));
        var byIndex = candidates.ToDictionary(c => c.Index);
        var ordered = new List<Candidate>();
        while (ready.Count > 0)
        {
            var next = byIndex[ready.Min];
            ready.Remove(next.Index);
            ordered.Add(next);
            foreach (var dep in dependents[next.Manifest.Id])
            {
                if (--indegree[dep] == 0)
                {
                    ready.Add(byId[dep].Index);
                }
            }
        }

        var cyclic = candidates.Where(c => !ordered.Contains(c)).ToList();
        return (ordered, cyclic);
    }

    private static string? CheckDependencies(FeatureManifest m, HashSet<string> knownIds, Dictionary<string, FeatureManifest> enabled)
    {
        foreach (var d in m.Requires)
        {
            if (!enabled.TryGetValue(d.FeatureId, out var dep))
            {
                return (knownIds.Contains(d.FeatureId) ? "dependency.disabled:" : "dependency.missing:") + d.FeatureId;
            }

            if (ManifestRules.CompareVersions(dep.Version, d.MinVersion) < 0)
            {
                return "dependency.version:" + d.FeatureId;
            }
        }

        return null;
    }

    private FeatureLoadReport LoadOne(Candidate c, FeatureLoadContext ctx)
    {
        var m = c.Manifest;

        // 1. Register, isolated.
        var registrar = new FeatureRegistrar(m.Id);
        var registerProblem = RunRegister(c.Feature, registrar);
        if (registerProblem is not null)
        {
            return Report(m, registerProblem.Value.Status, registerProblem.Value.Reason);
        }

        var (jobs, codeProcedures, codeReaders, errors) = registrar.Snapshot();
        if (errors.Count > 0)
        {
            return Report(m, FeatureLoadStatus.Failed, "register." + errors[0]);
        }

        // 2. Jobs.
        var jobNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (def, _) in jobs)
        {
            var reason = ManifestRules.ValidateJob(def, m);
            if (reason is not null)
            {
                return Report(m, FeatureLoadStatus.Failed, reason);
            }

            if (_jobs.ContainsName(def.Name) || !jobNames.Add(def.Name))
            {
                return Report(m, FeatureLoadStatus.Failed, "job.duplicate:" + def.Name);
            }
        }

        // 3. Data files (trusted read, parse).
        var data = new LoadedData();
        var dataProblem = LoadData(m, ctx, data);
        if (dataProblem is not null)
        {
            return Report(m, dataProblem.Value.Status, dataProblem.Value.Reason);
        }

        var procedures = data.Procedures.Concat(codeProcedures).ToList();
        var readers = data.Readers.Concat(codeReaders).ToList();

        // 4. Validate everything before the first builder call.
        var validation = ValidateTemplates(m, ctx, data.Templates)
            ?? ValidateUiMap(m, ctx, data.UiMap)
            ?? ValidateReaders(ctx, readers)
            ?? ValidateProcedures(m, ctx, procedures);
        if (validation is not null)
        {
            return Report(m, FeatureLoadStatus.Failed, validation);
        }

        // 5. Commit (templates, map, readers, procedures), then the jobs.
        var source = "feature:" + m.Id;
        var flipped = 0;
        var stage = "templates";
        try
        {
            foreach (var t in data.Templates)
            {
                var def = t.Definition;
                if (m.ValidatedGameBuild is not null && !string.Equals(m.ValidatedGameBuild, _currentGameBuild, StringComparison.Ordinal)
                    && def.Status != TemplateStatus.Provisional)
                {
                    def = def with { Status = TemplateStatus.Provisional };
                    flipped++;
                }

                ctx.Templates.Add(def, t.Png, t.MaskPng, source);
            }

            stage = "uiMap";
            if (data.UiMap is not null)
            {
                ctx.UiMap.AddFragment(data.UiMap, source);
            }

            stage = "readers";
            foreach (var r in readers)
            {
                ctx.Readers.Add(r, source);
            }

            stage = "procedures";
            foreach (var p in procedures)
            {
                ctx.Procedures.Add(p, source);
            }
        }
        catch (Exception ex)
        {
            // Builders have no remove: entries added before the rejection stay in the (not yet sealed) catalogs.
            return Report(m, FeatureLoadStatus.Failed, "commit.rejected:" + stage + ":" + Codes.Safe(ex.GetType().Name));
        }

        foreach (var (def, handler) in jobs)
        {
            _jobs.Add(def, new GuardedJobHandler(m.Id, handler, OnHandlerThrew));
        }

        return Report(m, FeatureLoadStatus.Enabled, flipped > 0 ? "templates.provisional:gameBuild" : null);
    }

    private (FeatureLoadStatus Status, string Reason)? RunRegister(IFeature feature, FeatureRegistrar registrar)
    {
        var task = Task.Factory.StartNew(
            () => feature.Register(registrar),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
        bool completed;
        try
        {
            completed = task.Wait(_registerBudget);
        }
        catch (AggregateException ae)
        {
            registrar.Close();
            var inner = ae.Flatten().InnerExceptions.FirstOrDefault();
            return (FeatureLoadStatus.Failed, "register.threw:" + Codes.Safe(inner?.GetType().Name));
        }

        registrar.Close();
        if (!completed)
        {
            // The worker cannot be aborted; it is abandoned (its late registrations throw) and its fault observed.
            _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return (FeatureLoadStatus.Timeout, "register.timeout");
        }

        return null;
    }

    private static (FeatureLoadStatus Status, string Reason)? LoadData(FeatureManifest m, FeatureLoadContext ctx, LoadedData data)
    {
        var files = m.Data;
        if (files.UiMap is null && files.Templates is null && files.Procedures is null && files.Readers is null)
        {
            return null;
        }

        var root = Path.GetFullPath(Path.Combine(ctx.Paths.FeaturesDataDir, m.Id));

        if (files.Procedures is not null)
        {
            var (value, problem) = ReadJson<ProcedureDefinition[]>(ctx, root, root, files.Procedures);
            if (problem is not null)
            {
                return problem;
            }

            data.Procedures.AddRange(value!);
        }

        if (files.Readers is not null)
        {
            var (value, problem) = ReadJson<ReaderDefinition[]>(ctx, root, root, files.Readers);
            if (problem is not null)
            {
                return problem;
            }

            data.Readers.AddRange(value!);
        }

        if (files.UiMap is not null)
        {
            var (value, problem) = ReadJson<UiMap>(ctx, root, root, files.UiMap);
            if (problem is not null)
            {
                return problem;
            }

            data.UiMap = value;
        }

        if (files.Templates is not null)
        {
            var (value, problem) = ReadJson<TemplateDefinition[]>(ctx, root, root, files.Templates);
            if (problem is not null)
            {
                return problem;
            }

            var templatesDir = Path.GetDirectoryName(Resolve(root, root, files.Templates)!) ?? root;
            foreach (var def in value!)
            {
                if (def is null || def.ImageFile is null || !ManifestRules.IsSafeRelativePath(def.ImageFile, ".png")
                    || (def.MaskFile is not null && !ManifestRules.IsSafeRelativePath(def.MaskFile, ".png")))
                {
                    return (FeatureLoadStatus.Failed, "data.invalid:templateFile");
                }

                var png = ReadBytes(ctx, root, templatesDir, def.ImageFile);
                if (png is null)
                {
                    return (FeatureLoadStatus.Untrusted, "data.untrusted:" + def.ImageFile);
                }

                byte[]? mask = null;
                if (def.MaskFile is not null)
                {
                    mask = ReadBytes(ctx, root, templatesDir, def.MaskFile);
                    if (mask is null)
                    {
                        return (FeatureLoadStatus.Untrusted, "data.untrusted:" + def.MaskFile);
                    }
                }

                data.Templates.Add(new LoadedTemplate(def, png, mask));
            }
        }

        return null;
    }

    private static (T? Value, (FeatureLoadStatus Status, string Reason)? Problem) ReadJson<T>(FeatureLoadContext ctx, string root, string baseDir, string relative)
        where T : class
    {
        var bytes = ReadBytes(ctx, root, baseDir, relative);
        if (bytes is null)
        {
            return (null, (FeatureLoadStatus.Untrusted, "data.untrusted:" + relative));
        }

        try
        {
            // A data file saved with a UTF-8 BOM (hand edit) is accepted: the reader does not skip it on its own.
            ReadOnlySpan<byte> json = bytes;
            if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            {
                json = json[3..];
            }

            var value = JsonSerializer.Deserialize<T>(json, ContractJson.Options);
            if (value is null || (value is Array a && HasNullElement(a)))
            {
                return (null, (FeatureLoadStatus.Failed, "data.invalid:" + relative));
            }

            return (value, null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return (null, (FeatureLoadStatus.Failed, "data.invalid:" + relative));
        }
    }

    private static bool HasNullElement(Array a)
    {
        foreach (var o in a)
        {
            if (o is null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Trusted read of a file that must stay inside the feature folder; null if outside, missing or untrusted.</summary>
    private static byte[]? ReadBytes(FeatureLoadContext ctx, string root, string baseDir, string relative)
    {
        var full = Resolve(root, baseDir, relative);
        return full is null ? null : ctx.FileTrust.ReadTrusted(full);
    }

    private static string? Resolve(string root, string baseDir, string relative)
    {
        if (!ManifestRules.IsSafeRelativePath(relative, null))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(baseDir, relative));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string? ValidateTemplates(FeatureManifest m, FeatureLoadContext ctx, List<LoadedTemplate> templates)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in templates)
        {
            var d = t.Definition;
            if (d.Id is null || !ItemId.IsMatch(d.Id))
            {
                return "template.invalid:id";
            }

            if (ctx.Templates.Get(d.Id) is not null || !ids.Add(d.Id))
            {
                return "template.duplicate:" + d.Id;
            }

            if (d.Tags.HasFlag(TargetTags.Commit) && (d.Gate == AutomationSwitch.None || d.Gate != m.Switch))
            {
                return "template.ungatedCommit:" + d.Id;
            }

            // Only the core HUD navigation templates may skip the slid check on a cross-monitor slide (SPEC 7.3).
            if (d.Tags.HasFlag(TargetTags.NavFixed))
            {
                return "template.navFixedCoreOnly:" + d.Id;
            }

            if (d.Gate != AutomationSwitch.None && d.Gate != m.Switch)
            {
                return "template.gateMismatch:" + d.Id;
            }
        }

        return null;
    }

    private static string? ValidateUiMap(FeatureManifest m, FeatureLoadContext ctx, UiMap? map)
    {
        if (map is null)
        {
            return null;
        }

        if (map.Screens is null || map.Buttons is null || map.DenyZones is null)
        {
            return "uimap.invalid";
        }

        var screens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in map.Screens)
        {
            if (s?.Id is null || !ItemId.IsMatch(s.Id))
            {
                return "uimap.invalid:screen";
            }

            if (ctx.UiMap.GetScreen(s.Id) is not null || !screens.Add(s.Id))
            {
                return "uimap.duplicate:" + s.Id;
            }
        }

        var buttons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in map.Buttons)
        {
            if (b?.Id is null || !ItemId.IsMatch(b.Id))
            {
                return "uimap.invalid:button";
            }

            if (ctx.UiMap.GetButton(b.Id) is not null || !buttons.Add(b.Id))
            {
                return "uimap.duplicate:" + b.Id;
            }

            if (b.Safety == ButtonSafety.Commit && (b.Gate == AutomationSwitch.None || b.Gate != m.Switch))
            {
                return "uimap.ungatedCommit:" + b.Id;
            }

            if (b.Gate != AutomationSwitch.None && b.Gate != m.Switch)
            {
                return "uimap.gateMismatch:" + b.Id;
            }
        }

        return null;
    }

    private static string? ValidateReaders(FeatureLoadContext ctx, List<ReaderDefinition> readers)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in readers)
        {
            if (r.Id is null || !ItemId.IsMatch(r.Id))
            {
                return "reader.invalid:id";
            }

            if (ctx.Readers.Get(r.Id) is not null || !ids.Add(r.Id))
            {
                return "reader.duplicate:" + r.Id;
            }
        }

        return null;
    }

    private static string? ValidateProcedures(FeatureManifest m, FeatureLoadContext ctx, List<ProcedureDefinition> procedures)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in procedures)
        {
            if (p.Id is null || !ItemId.IsMatch(p.Id))
            {
                return "procedure.invalid:id";
            }

            if (ctx.Procedures.Get(p.Id) is not null || !ids.Add(p.Id))
            {
                return "procedure.duplicate:" + p.Id;
            }

            if (p.Steps is null || p.Steps.Count == 0 || p.Limits is null || !Enum.IsDefined(p.Kind) || !Enum.IsDefined(p.Gate))
            {
                return "procedure.invalid:" + p.Id;
            }

            var stepIds = new HashSet<string>(StringComparer.Ordinal);
            var hasCommit = p.Kind == ProcedureKind.Commit;
            foreach (var s in p.Steps)
            {
                if (s?.Id is null || s.Action is null || s.Pre is null || s.Expect is null || !stepIds.Add(s.Id))
                {
                    return "procedure.invalid:step:" + p.Id;
                }

                if (s.Flags.HasFlag(StepFlags.Commit))
                {
                    hasCommit = true;
                    if (s.Flags.HasFlag(StepFlags.Optional))
                    {
                        return "procedure.optionalCommit:" + p.Id;
                    }
                }
            }

            // GATE-01: commit steps are always gated, and only by the feature's own switch.
            if (hasCommit && (p.Gate == AutomationSwitch.None || p.Gate != m.Switch))
            {
                return "procedure.ungatedCommit:" + p.Id;
            }

            if (p.Gate != AutomationSwitch.None && p.Gate != m.Switch)
            {
                return "procedure.gateMismatch:" + p.Id;
            }
        }

        return null;
    }

    private void OnHandlerThrew(string featureId, Exception ex)
    {
        int count;
        lock (_gate)
        {
            _handlerThrows.TryGetValue(featureId, out count);
            count++;
            _handlerThrows[featureId] = count;
        }

        Journal(JournalKind.FeatureLoad, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["featureId"] = Codes.Safe(featureId),
            ["event"] = "handler.threw",
            ["exception"] = Codes.Safe(ex.GetType().Name),
            ["count"] = (long)count,
        });

        if (count >= HandlerThrowsToFault)
        {
            Fault(featureId, "handler.threw:" + count);
        }
    }

    private void JournalReport(FeatureLoadReport r) =>
        Journal(JournalKind.FeatureLoad, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["featureId"] = Codes.Safe(r.FeatureId),
            ["version"] = Codes.Safe(r.Version),
            ["status"] = r.Status.ToString(),
            ["reason"] = r.ReasonCode is null ? null : Codes.Safe(r.ReasonCode),
        });

    private void Journal(JournalKind kind, IReadOnlyDictionary<string, object?> fields)
    {
        var journal = _journal;
        if (journal is null)
        {
            return;
        }

        try
        {
            journal.Write(new JournalRecord(_clock.UtcNow, kind, null, null, null, fields));
        }
        catch (Exception)
        {
            // The journal never throws by contract; loading must not fail because of it.
        }
    }

    private sealed record Candidate(int Index, IFeature Feature, FeatureManifest Manifest);

    private sealed record LoadedTemplate(TemplateDefinition Definition, byte[] Png, byte[]? MaskPng);

    private sealed class LoadedData
    {
        public List<ProcedureDefinition> Procedures { get; } = [];

        public List<ReaderDefinition> Readers { get; } = [];

        public List<LoadedTemplate> Templates { get; } = [];

        public UiMap? UiMap { get; set; }
    }
}
