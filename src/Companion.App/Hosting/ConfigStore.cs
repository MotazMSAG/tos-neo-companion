using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Host;

namespace Companion.App.Hosting;

/// <summary>
/// config.json (SPEC 10.3, 11.5): read once at start (missing = <see cref="CompanionConfig.Default"/>, automation off,
/// slide clicks on), changed ONLY by the elevated tray after a user confirmation, written atomically and journaled.
/// Implements the read-only views handed to the engine and the features. Thread-safe (immutable snapshots).
/// </summary>
internal sealed class ConfigStore : IConfigProvider, IAutomationSwitches
{
    /// <summary>Largest config.json accepted.</summary>
    public const int MaxConfigBytes = 64 * 1024;

    private readonly CompanionPaths _paths;
    private readonly IJournal _journal;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private volatile CompanionConfig _current = CompanionConfig.Default;

    /// <summary>Creates the store (no side effects).</summary>
    public ConfigStore(CompanionPaths paths, IJournal journal, IClock clock)
    {
        _paths = paths;
        _journal = journal;
        _clock = clock;
    }

    /// <inheritdoc />
    public CompanionConfig Current => _current;

    /// <inheritdoc />
    public bool SlideClickEnabled => _current.Input.SlideClick;

    /// <summary>Problem found at load ("config.invalid", "config.schema", ...), null if none.</summary>
    public string? LoadProblem { get; private set; }

    /// <inheritdoc />
    public bool IsOn(AutomationSwitch automationSwitch) => automationSwitch switch
    {
        AutomationSwitch.None => true,
        AutomationSwitch.Crafting => _current.Automation.Crafting,
        AutomationSwitch.Homestead => _current.Automation.Homestead,
        _ => false,
    };

    /// <summary>
    /// Loads config.json. A missing file gives the defaults (nothing is written). An unreadable or invalid file gives the
    /// defaults too (automation off) and a problem code; the file is left as it is until the user changes a setting.
    /// </summary>
    public CompanionConfig Load()
    {
        lock (_gate)
        {
            LoadProblem = null;
            var path = _paths.ConfigPath;
            if (!File.Exists(path))
            {
                _current = CompanionConfig.Default;
                return _current;
            }

            var bytes = SafeFile.ReadSmall(path, MaxConfigBytes);
            if (bytes is null)
            {
                LoadProblem = "config.unreadable";
                _current = CompanionConfig.Default;
                return _current;
            }

            var parsed = Parse(bytes, out var problem);
            LoadProblem = problem;
            _current = parsed ?? CompanionConfig.Default;
            return _current;
        }
    }

    /// <summary>Parses and normalises config bytes; null and a problem code if invalid.</summary>
    public static CompanionConfig? Parse(byte[] bytes, out string? problem)
    {
        problem = null;
        try
        {
            ReadOnlySpan<byte> json = bytes;
            if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            {
                json = json[3..];
            }

            var raw = JsonSerializer.Deserialize<CompanionConfig>(json, ContractJson.Options);
            if (raw is null)
            {
                problem = "config.invalid";
                return null;
            }

            if (raw.SchemaVersion != 1)
            {
                problem = "config.schema";
                return null;
            }

            return Normalize(raw);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            problem = "config.invalid";
            return null;
        }
    }

    /// <summary>Fills missing sections with defaults and clamps caps to sane ranges.</summary>
    public static CompanionConfig Normalize(CompanionConfig c)
    {
        var features = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var kv in CompanionConfig.Default.Features)
        {
            features[kv.Key] = kv.Value;
        }

        if (c.Features is not null)
        {
            foreach (var kv in c.Features)
            {
                if (!string.IsNullOrEmpty(kv.Key) && kv.Key.Length <= 64)
                {
                    features[kv.Key] = kv.Value;
                }
            }
        }

        features["core"] = true;
        var policy = c.Policy ?? new PolicyConfig();
        var idle = policy.TreatAutoCollectStatusAsIdle?.Where(v => v is >= 0 and <= 1000).Distinct().Order().ToArray();
        policy = policy with
        {
            TreatAutoCollectStatusAsIdle = idle is { Length: > 0 } ? idle : null,
            ServerUtcOffsetHours = Math.Clamp(policy.ServerUtcOffsetHours, -12, 14),
        };
        var keep = c.KeepStock ?? new KeepStockConfig();
        keep = keep with
        {
            MaxCraftsPerHour = Math.Clamp(keep.MaxCraftsPerHour, 0, 60),
            MaxSessionMinutes = Math.Clamp(keep.MaxSessionMinutes, 0, 24 * 60),
            MaxDailyCrafts = Math.Clamp(keep.MaxDailyCrafts, 0, 500),
        };
        return new CompanionConfig(1, c.Automation ?? new AutomationConfig(), c.Input ?? new InputConfig(), features, policy, keep);
    }

    /// <summary>
    /// Applies a tray change (the caller already asked the user to confirm): normalises, writes config.json atomically
    /// into RuntimeDir, journals the change code and swaps the snapshot. Returns false (snapshot unchanged) if the write
    /// failed.
    /// </summary>
    public bool Update(Func<CompanionConfig, CompanionConfig> change, string changeCode)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var next = Normalize(change(_current));
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(next, ContractJson.Options);
                SafeFile.WriteAtomic(_paths.RuntimeDir, _paths.ConfigPath, bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Journal(changeCode, false);
                return false;
            }

            _current = next;
            LoadProblem = null;
            Journal(changeCode, true);
            return true;
        }
    }

    private void Journal(string changeCode, bool ok)
    {
        try
        {
            var c = _current;
            _journal.Write(new JournalRecord(_clock.UtcNow, JournalKind.ConfigChange, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["change"] = changeCode,
                ["ok"] = ok,
                ["crafting"] = c.Automation.Crafting,
                ["homestead"] = c.Automation.Homestead,
                ["slideClick"] = c.Input.SlideClick,
                ["foregroundSlide"] = c.Input.ForegroundSlide,
                ["foregroundDrag"] = c.Input.ForegroundDrag,
                ["closeUserUis"] = c.Policy.CloseUserUisAtJobStart,
            }));
            _journal.Flush();
        }
        catch (Exception)
        {
            // the journal never breaks a config change
        }
    }
}
