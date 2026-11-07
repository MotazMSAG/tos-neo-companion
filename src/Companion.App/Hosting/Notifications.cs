using System.Text.RegularExpressions;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Host;

namespace Companion.App.Hosting;

/// <summary>Fixed user texts of the host (SPEC 6.9 and 10). Only a code-shaped subject is ever substituted.</summary>
internal static partial class HostMessages
{
    [GeneratedRegex(@"^[A-Za-z0-9_.:\-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SubjectPattern();

    /// <summary>HALT text per code (same wording as the engine's, so either side's notification reads the same).</summary>
    public static string HaltText(HaltCode code, string? subject)
    {
        var s = subject is not null && SubjectPattern().IsMatch(subject) ? subject : "?";
        return code switch
        {
            HaltCode.ForbiddenUi => "A shop or purchase screen opened. I closed it once and stopped. Nothing was bought. Check the game.",
            HaltCode.EscOpenedUi => "Pressing Esc opened a screen instead of closing one. I stopped; please look at the game.",
            HaltCode.GuideBan => "A tutorial step is blocking the game's menus. Finish it in the game, then clear the stop in the tray.",
            HaltCode.ExtraGameWindow => "The game opened an extra window (security or login dialog). I stopped.",
            HaltCode.CursorClipChanged => "The game tried to lock your mouse while I was working. I let go and stopped.",
            HaltCode.RestoreFailed => "I could not put the game window back. Double-click the game's title bar to maximize it again.",
            HaltCode.GameSelfResize => "The game changed its own window size during a step. I stopped; maximize the game again.",
            HaltCode.LayoutChanged or HaltCode.RecalibrateTemplate =>
                $"The button '{s}' is not where I expected; the screen layout changed. Run Calibrate, then update the template.",
            HaltCode.WorldPress => "A click landed in the game world instead of a button. I stopped so nothing else happens.",
            HaltCode.GameStoppedMidPress =>
                "The game stopped responding in the middle of a press while it was in front. After 30 seconds I moved its window back, so that press may land somewhere else when the game wakes up. Look at the game before you allow me again.",
            HaltCode.FocusFailed or HaltCode.IntegrityMismatch =>
                "The game does not react to me. Run both the game and the companion as administrator (one UAC prompt).",
            HaltCode.IdentityChanged => "The game restarted while I was working. Start the job again.",
            HaltCode.LogStalled => "The game log stopped updating. I stopped because I can no longer check what happens.",
            HaltCode.RepeatedState or HaltCode.BudgetExceeded or HaltCode.CountedFailuresExhausted or HaltCode.PreconditionLostExhausted =>
                $"I tried '{s}' several times and it did not work. I stopped; details are in the diagnostics folder.",
            HaltCode.CraftStoppedEarlyTwice or HaltCode.BlockedCraftSpot =>
                "Crafting stopped early twice (inventory full, out of energy, or a blocked spot). I stopped.",
            HaltCode.AutoCollectChanged => "Auto-gathering changed while I was working. I stopped so I do not interrupt it.",
            HaltCode.UnknownUiTwice => "Screens I do not know opened twice. I stopped; run a Survey so I can learn them.",
            HaltCode.TooManyWindowIncidents => "The game window moved unexpectedly several times. I stopped.",
            HaltCode.TamperedFiles => "Companion files changed since you trusted this build. Check them, then choose 'Trust this build' in the tray.",
            _ => "Something went wrong inside the companion. I stopped; details are in the diagnostics folder.",
        };
    }

    /// <summary>The HALT notification (code "halt.&lt;HaltCode&gt;", as the engine sends it).</summary>
    public static Notification Halt(HaltCode code, string? subject) => new(NotificationKind.Halt, "halt." + code, HaltText(code, subject));

    /// <summary>Host notifications (fixed text per code).</summary>
    public static Notification Host(string code) => code switch
    {
        "host.alreadyRunning" => new(NotificationKind.Info, code, "The companion is already running (see the tray icon)."),
        "host.untrusted" => new(NotificationKind.NeedsUser, code,
            "This companion build is not trusted yet. Check it, then choose 'Trust this build' in the tray. No jobs run until then."),
        "host.coreDataBroken" => new(NotificationKind.NeedsUser, code,
            "A companion data file is damaged. No jobs run; reinstall or rebuild the companion."),
        "host.runtimeDir" => new(NotificationKind.Warning, code,
            "The companion could not secure its data folder. No jobs run; details are in the journal."),
        "host.feature" => new(NotificationKind.Warning, code,
            "A companion feature could not be loaded and was disabled. The others work; details are in the tray."),
        "host.stopped" => new(NotificationKind.Info, code, "Stopped. The game window is untouched; clear the stop in the tray to continue."),
        "host.recovered" => new(NotificationKind.Warning, code, "The game window was left moved by a previous run; I put it back."),
        "host.leftover" => new(NotificationKind.NeedsUser, code,
            "A previous run may have left the game window moved, and I could not prove it is mine. Check the game window."),
        "knowledge.recovered" => new(NotificationKind.Warning, code, "Some knowledge files were restored from a backup."),
        "config.invalid" => new(NotificationKind.Warning, code,
            "config.json could not be read; I use the safe defaults (automation off) until you change a setting in the tray."),
        _ => new(NotificationKind.Info, "host.info", "The companion needs your attention. Details are in the journal."),
    };
}

/// <summary>
/// De-duplication by code (SPEC 10.3): a HALT code is shown once until the HALT is cleared; other codes at most once per
/// window. Thread-safe.
/// </summary>
internal sealed class NotificationGate
{
    private readonly IClock _clock;
    private readonly TimeSpan _window;
    private readonly Dictionary<string, TimeSpan> _shown = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Creates the gate.</summary>
    public NotificationGate(IClock clock, TimeSpan window)
    {
        _clock = clock;
        _window = window;
    }

    /// <summary>Notifications suppressed as duplicates.</summary>
    public int Suppressed { get; private set; }

    /// <summary>True if the notification should be shown now (and records it).</summary>
    public bool ShouldShow(Notification n)
    {
        ArgumentNullException.ThrowIfNull(n);
        lock (_gate)
        {
            var now = _clock.Monotonic;
            if (_shown.TryGetValue(n.Code, out var at))
            {
                var sticky = n.Kind == NotificationKind.Halt;
                if (sticky || now - at < _window)
                {
                    Suppressed++;
                    return false;
                }
            }

            _shown[n.Code] = now;
            return true;
        }
    }

    /// <summary>Forgets codes starting with <paramref name="prefix"/> (the tray clears a HALT or a stop).</summary>
    public void Reset(string prefix)
    {
        lock (_gate)
        {
            foreach (var key in _shown.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _shown.Remove(key);
            }
        }
    }
}

/// <summary>
/// The host notifier (SPEC 10.3): non-activating NotifyIcon balloons through <see cref="Sink"/> (set by the tray, which
/// marshals to its UI thread), de-duplicated by code, journaled. Never a dialog, never focus-taking (KILL-21).
/// </summary>
internal sealed class HostNotifier : INotifier
{
    private readonly NotificationGate _gate;
    private readonly IJournal _journal;
    private readonly IClock _clock;
    private readonly List<Notification> _recent = [];
    private readonly object _lock = new();

    /// <summary>Creates the notifier (no side effects).</summary>
    public HostNotifier(NotificationGate gate, IJournal journal, IClock clock)
    {
        _gate = gate;
        _journal = journal;
        _clock = clock;
    }

    /// <summary>Balloon sink; null until the tray exists (notifications are then only journaled and kept).</summary>
    public Action<Notification>? Sink { get; set; }

    /// <summary>The gate (the tray resets it when a HALT is cleared).</summary>
    public NotificationGate Gate => _gate;

    /// <summary>Last shown notifications (newest last, at most 20).</summary>
    public IReadOnlyList<Notification> Recent
    {
        get
        {
            lock (_lock)
            {
                return _recent.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public void Notify(Notification notification)
    {
        if (notification is null || !_gate.ShouldShow(notification))
        {
            return;
        }

        lock (_lock)
        {
            _recent.Add(notification);
            if (_recent.Count > 20)
            {
                _recent.RemoveAt(0);
            }
        }

        try
        {
            _journal.Write(new JournalRecord(_clock.UtcNow, JournalKind.Notification, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = notification.Code,
                ["kind"] = notification.Kind.ToString(),
            }));
        }
        catch (Exception)
        {
            // journaling is best effort
        }

        try
        {
            Sink?.Invoke(notification);
        }
        catch (Exception)
        {
            // a balloon failure never breaks the caller (engine thread)
        }
    }
}
