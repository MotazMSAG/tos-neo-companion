using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Host;

namespace Companion.App.Hosting;

/// <summary>
/// Persisted HALT latch (SPEC 6.8, KILL-21): halt.json in RuntimeDir, written atomically, survives restarts until the
/// user clears it in the tray. A new pipe submission never clears it. A damaged halt.json counts as latched
/// (fail-safe). Thread-safe.
/// </summary>
internal sealed class HaltLatch : IHaltLatch
{
    private const int MaxBytes = 64 * 1024;

    private readonly CompanionPaths _paths;
    private readonly INotifier _notifier;
    private readonly IJournal _journal;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private HaltReport? _current;

    /// <summary>Creates the latch (no side effects; call <see cref="Load"/> at host start).</summary>
    public HaltLatch(CompanionPaths paths, INotifier notifier, IJournal journal, IClock clock)
    {
        _paths = paths;
        _notifier = notifier;
        _journal = journal;
        _clock = clock;
    }

    /// <inheritdoc />
    public bool IsHalted
    {
        get
        {
            lock (_gate)
            {
                return _current is not null;
            }
        }
    }

    /// <inheritdoc />
    public HaltReport? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>True when the last <see cref="Halt"/> or <see cref="Clear"/> could not be persisted.</summary>
    public bool PersistFailed { get; private set; }

    /// <summary>Reads halt.json. Missing = not halted; damaged = halted (InternalError, subject "halt.corrupt").</summary>
    public HaltReport? Load()
    {
        lock (_gate)
        {
            var path = _paths.HaltLatchPath;
            if (!File.Exists(path))
            {
                _current = null;
                return null;
            }

            var bytes = SafeFile.ReadSmall(path, MaxBytes);
            HaltReport? report = null;
            if (bytes is not null)
            {
                try
                {
                    report = JsonSerializer.Deserialize<HaltReport>(bytes, ContractJson.Options);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
                {
                    report = null;
                }
            }

            if (report is null || !Enum.IsDefined(report.Code) || report.JobId is null || report.ProcedureId is null)
            {
                report = new HaltReport(HaltCode.InternalError, "halt.corrupt", "unknown", "unknown", null, _clock.UtcNow, null);
            }

            _current = report;
            return report;
        }
    }

    /// <inheritdoc />
    public void Halt(HaltReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        HaltReport latched;
        lock (_gate)
        {
            // The first HALT wins: a later one (e.g. a compensation failure) never hides the original cause.
            _current ??= report;
            latched = _current;
            Persist(latched);
        }

        Journal(JournalKind.Halt, latched.Code.ToString(), "latched");
        _notifier.Notify(HostMessages.Halt(report.Code, report.Subject));
    }

    /// <inheritdoc />
    public void Clear(string byCode)
    {
        HaltCode? cleared;
        lock (_gate)
        {
            cleared = _current?.Code;
            _current = null;
            try
            {
                if (File.Exists(_paths.HaltLatchPath) && !SafeFile.IsReparsePoint(_paths.HaltLatchPath))
                {
                    File.Delete(_paths.HaltLatchPath);
                }

                PersistFailed = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PersistFailed = true;
            }
        }

        Journal(JournalKind.Halt, cleared?.ToString() ?? "none", "cleared:" + byCode);
    }

    private void Persist(HaltReport report)
    {
        try
        {
            SafeFile.WriteAtomic(_paths.RuntimeDir, _paths.HaltLatchPath, JsonSerializer.SerializeToUtf8Bytes(report, ContractJson.Options));
            PersistFailed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still latched in memory: the host refuses jobs until the user clears it.
            PersistFailed = true;
        }
    }

    private void Journal(JournalKind kind, string code, string evt)
    {
        try
        {
            _journal.Write(new JournalRecord(_clock.UtcNow, kind, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = code,
                ["event"] = evt,
                ["persisted"] = !PersistFailed,
            }));
            _journal.Flush();
        }
        catch (Exception)
        {
            // best effort
        }
    }
}
