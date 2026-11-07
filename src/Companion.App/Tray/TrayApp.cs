using System.Drawing;
using System.Globalization;
using Companion.App.Hosting;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.GameIo;
using Companion.Contracts.Host;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Safety;
using Microsoft.Win32;

namespace Companion.App.Tray;

/// <summary>
/// The tray (SPEC 10.3), the only UI: status line, Pause/Resume, Stop now (also Ctrl+Alt+Pause), Clear stop / Clear
/// HALT, automation switches, auto-collect confirmation, policy, the Crafting window,
/// knowledge entry, feature reports, "Trust this build..." and Exit. Every setting change asks for a confirmation, is written atomically
/// and journaled. Notifications are non-activating balloons.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly HostRuntime _host;
    private readonly HostServices _s;
    private readonly Jobs.JobHost _jobs;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly SynchronizationContext _ui;
    private readonly HotkeyWindow _hotkey;
    private CraftingForm? _crafting;
    private TeaHouseForm? _teaHouse;
    private MainForm? _main;
    private readonly RuleModeKeeper _ruleMode;
    private bool _exiting;

    /// <summary>Creates the tray for a started host (UI thread).</summary>
    public TrayApp(HostRuntime host)
    {
        _host = host;
        _s = host.Services ?? throw new InvalidOperationException("host not started");
        _jobs = host.Jobs ?? throw new InvalidOperationException("host not started");
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _jobs.OpenMain = tab => _ui.Post(_ => OpenMain(tab), null); // "Companion.Cli show [tab]" (2026-10-17)
        _ruleMode = new RuleModeKeeper(_jobs, _s.Paths.RuntimeDir);
        _icon = new NotifyIcon { Icon = SystemIcons.Shield, Text = "TOS companion", ContextMenuStrip = _menu, Visible = true };
        // a left click opens the companion window (2026-10-16, the user: "can't I just click the tray to open a window")
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                OpenMain();
            }
        };
        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();

        _s.Notifier.Sink = n => _ui.Post(_ => ShowBalloon(n), null);
        _hotkey = new HotkeyWindow(() => StopNow(StopSource.Hotkey, "hotkey.stop"));
        SystemEvents.SessionEnding += OnSessionEnding;
        _iconTimer.Tick += (_, _) =>
        {
            UpdateIconText();
            CheckGameFolderMismatch();
        };
        _iconTimer.Start();

        // Replay the most important start-up notification (they were raised before the tray existed).
        var recent = _s.Notifier.Recent;
        var first = recent.FirstOrDefault(n => n.Kind is NotificationKind.Halt or NotificationKind.NeedsUser) ?? recent.LastOrDefault();
        if (first is not null)
        {
            ShowBalloon(first);
        }

        UpdateIconText();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseCrafting();
            _ruleMode.Dispose();
            SystemEvents.SessionEnding -= OnSessionEnding;
            _iconTimer.Dispose();
            _hotkey.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        StopNow(StopSource.SessionEnding, "session.ending");
        try
        {
            // Windows ends the process after this (audit R1.11, rule 5): the journal says why, written now
            _s.Journal.Write(new JournalRecord(DateTimeOffset.UtcNow, JournalKind.HostStop, null, null, null,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["event"] = "session.ending" }));
            _s.Journal.Flush();
        }
        catch (Exception)
        {
            // best effort
        }
    }

    private void ShowBalloon(Notification n)
    {
        if (_exiting)
        {
            return;
        }

        var icon = n.Kind switch
        {
            NotificationKind.Halt => ToolTipIcon.Error,
            NotificationKind.Warning or NotificationKind.NeedsUser => ToolTipIcon.Warning,
            _ => ToolTipIcon.Info,
        };
        _icon.ShowBalloonTip(10_000, "TOS companion", n.Text, icon);
        UpdateIconText();
    }

    private void UpdateIconText()
    {
        // the window's own sentence, refreshed every few seconds (audit R3.3 #43: it changed only with a balloon)
        string text;
        try
        {
            var st = _jobs.GetStatus();
            text = "TOS companion: " + CraftingMessages.Health(st, _jobs.IsJobRunning, CraftingMessages.JobActivityText(_jobs.RunningJob?.Data, DateTimeOffset.UtcNow), _s.Log.State.AutoCollectStatus).Text;
        }
        catch (Exception)
        {
            text = "TOS companion";
        }

        if (DailiesLeft() is { } left)
        {
            text = (left == 0 ? "Dailies: all done" : "Dailies: " + left + " left") + Environment.NewLine + text; // the checklist first (2026-11-04)
        }

        _icon.Text = text.Length > 120 ? text[..117] + "..." : text;
    }

    /// <summary>Refreshes the tray tooltip (audit R3.3 #43).</summary>
    private readonly System.Windows.Forms.Timer _iconTimer = new() { Interval = 5_000 };

    private void StopNow(StopSource source, string code)
    {
        _jobs.StopAll(source, code);
        _s.Notifier.Notify(HostMessages.Host("host.stopped"));
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var st = _jobs.GetStatus();
        var cfg = _s.Config.Current;

        _menu.Items.Add(new ToolStripMenuItem(StatusLine(st)) { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Open companion (left click)", null, (_, _) => OpenMain()) { Font = new Font(_menu.Font, FontStyle.Bold) });
        if (DailiesLeft() is { } left)
        {
            _menu.Items.Add("Today: " + (left == 0 ? "all dailies done" : left + (left == 1 ? " daily left" : " dailies left")), null, (_, _) => OpenMain("Today"));
        }

        _menu.Items.Add("Kitty Tea House helper...", null, (_, _) => OpenTeaHouse());

        if (_s.Pause.IsPaused)
        {
            _menu.Items.Add("Resume", null, (_, _) => _s.Pause.Resume("tray"));
        }
        else
        {
            _menu.Items.Add("Pause", null, (_, _) => _s.Pause.Pause("tray"));
        }

        _menu.Items.Add("Stop now (Ctrl+Alt+Pause)", null, (_, _) => StopNow(StopSource.Tray, "tray.stop"));
        // shown only when they apply (the menu stays short)
        if (_s.Stop.IsStopRequested && !_s.Halt.IsHalted)
        {
            _menu.Items.Add("Allow again (after Stop)...", null, (_, _) => ClearStop());
        }

        if (_s.Halt.IsHalted)
        {
            _menu.Items.Add(new ToolStripMenuItem("Continue after a safety stop...", null, (_, _) => ClearHalt()) { ForeColor = Color.Firebrick });
        }

        _menu.Items.Add(new ToolStripSeparator());

        // the switches are in the window's Settings tab; the rarely needed rest stays here
        var settings = new ToolStripMenuItem("Advanced");
        var automation = new ToolStripMenuItem("Automation");
        automation.DropDownItems.Add(Toggle("Crafting", cfg.Automation.Crafting, on =>
            Change(on ? "Turn ON crafting automation? The companion may then start crafts." : "Turn OFF crafting automation?",
                c => c with { Automation = c.Automation with { Crafting = on } }, "automation.crafting." + (on ? "on" : "off"))));
        automation.DropDownItems.Add(Toggle("Homestead", cfg.Automation.Homestead, on =>
            Change(on ? "Turn ON homestead automation? The companion may then collect homestead products." : "Turn OFF homestead automation?",
                c => c with { Automation = c.Automation with { Homestead = on } }, "automation.homestead." + (on ? "on" : "off"))));
        automation.DropDownItems.Add(Toggle("Slide clicks", cfg.Input.SlideClick, on =>
            Change(on ? "Allow slide clicks again?" : "Turn OFF slide clicks? Only key-based jobs will run.",
                c => c with { Input = c.Input with { SlideClick = on } }, "input.slideClick." + (on ? "on" : "off"))));
        settings.DropDownItems.Add(automation);

        settings.DropDownItems.Add(AutoCollectMenu(cfg));

        var policy = new ToolStripMenuItem("Policy");
        policy.DropDownItems.Add(Toggle("May close chat/mail windows", cfg.Policy.CloseUserUisAtJobStart, on =>
            Change(on ? "Allow the companion to close chat or mail windows you left open (Esc) at the start of a job?" : "Stop closing chat or mail windows at the start of a job?",
                c => c with { Policy = c.Policy with { CloseUserUisAtJobStart = on } }, "policy.closeUserUis." + (on ? "on" : "off"))));
        settings.DropDownItems.Add(policy);
        settings.DropDownItems.Add(new ToolStripMenuItem("Knowledge...", null, (_, _) => OpenKnowledge()));
        settings.DropDownItems.Add(FeaturesMenu());
        settings.DropDownItems.Add("Game folder...", null, (_, _) => ChooseGameFolder());
        settings.DropDownItems.Add("Trust this build...", null, (_, _) => TrustBuild());
        _menu.Items.Add(settings);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitHost());
    }

    private static ToolStripMenuItem Toggle(string text, bool on, Action<bool> set) =>
        new(text, null, (_, _) => set(!on)) { Checked = on };

    /// <summary>Dailies left on the user's list now; null when the checklist is not running.</summary>
    private int? DailiesLeft()
    {
        try
        {
            return _host.Dailies is { } d ? Today.TodayModel.Build(d.Snapshot(), d.Prefs, d.Replaying).Left : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private string StatusLine(HostStatus st)
    {
        var engine = st.Engine;
        var job = engine?.JobId is { Length: >= 8 } id ? id[..8] : "-";
        var wait = engine?.WaitingFor ?? "-";
        var game = st.GameConnected ? "connected" : "not connected";
        var reason = st.State == HostState.Untrusted ? " (" + (_host.BlockedReason ?? _host.Trust?.Code ?? "untrusted") + ")" : string.Empty;
        var halt = st.Halt is { } h ? " HALT " + h.Code : string.Empty;
        var health = CraftingMessages.Health(st, _jobs.IsJobRunning, CraftingMessages.JobActivityText(_jobs.RunningJob?.Data, DateTimeOffset.UtcNow), _s.Log.State.AutoCollectStatus).Text; // the window's own sentence (2026-10-18: "not human")
        _ = (job, wait, game, reason, halt);
        return health.Length > 110 ? health[..110] + "..." : health;
    }

    private void Change(string question, Func<CompanionConfig, CompanionConfig> change, string code)
    {
        if (!TrayForms.Confirm("TOS companion", question))
        {
            return;
        }

        if (!_s.Config.Update(change, code))
        {
            _s.Notifier.Notify(new Notification(NotificationKind.Warning, "config.writeFailed", "The setting could not be saved. Nothing was changed."));
        }
    }


    private ToolStripMenuItem AutoCollectMenu(CompanionConfig cfg)
    {
        var menu = new ToolStripMenuItem("Auto-collect");
        var status = _s.Log.State.AutoCollectStatus;
        var idle = cfg.Policy.TreatAutoCollectStatusAsIdle ?? [];
        var shown = status?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        menu.DropDownItems.Add(new ToolStripMenuItem("Current status: " + shown + (idle.Count > 0 ? " (idle: 0," + string.Join(",", idle) + ")" : " (idle: 0)")) { Enabled = false });
        if (status is { } n && n != 0 && !idle.Contains(n))
        {
            menu.DropDownItems.Add($"Status {n} means not gathering...", null, (_, _) =>
                Change($"Confirm that auto-collect status {n} means your character is NOT gathering? Jobs will then run while it shows {n}.",
                    c => c with { Policy = c.Policy with { TreatAutoCollectStatusAsIdle = [.. c.Policy.TreatAutoCollectStatusAsIdle ?? [], n] } },
                    "policy.autoCollectIdle.add"));
        }

        if (idle.Count > 0)
        {
            menu.DropDownItems.Add("Forget confirmed statuses...", null, (_, _) =>
                Change("Forget the auto-collect statuses you confirmed? Only status 0 will count as not gathering.",
                    c => c with { Policy = c.Policy with { TreatAutoCollectStatusAsIdle = null } }, "policy.autoCollectIdle.clear"));
        }

        return menu;
    }

    private ToolStripMenuItem FeaturesMenu()
    {
        var menu = new ToolStripMenuItem("Features");
        foreach (var r in _s.Features.Reports)
        {
            var reason = r.ReasonCode is null ? string.Empty : " (" + r.ReasonCode + ")";
            menu.DropDownItems.Add(new ToolStripMenuItem($"{r.FeatureId} {r.Version}: {r.Status}{reason}") { Enabled = false, Checked = r.Status == FeatureLoadStatus.Enabled });
        }

        if (_host.DataReport is { CoreOk: false } dr)
        {
            foreach (var p in dr.Problems.Take(8))
            {
                menu.DropDownItems.Add(new ToolStripMenuItem("core data: " + p) { Enabled = false });
            }
        }

        return menu;
    }

    private void ClearStop()
    {
        if (TrayForms.Confirm("TOS companion", "Let the companion work again? The Stop cancelled the jobs that were running or waiting: start them again if you still want them."))
        {
            _s.Stop.Clear();
            _s.NotificationGate.Reset("host.stopped");
            ResumeIfPaused("stop.cleared");
            Journal("stop.cleared");
        }
    }

    /// <summary>
    /// Audit R3.4 (#37): Stop left the pause on, so after "Allow again" a new job waited silently at its first gate; the
    /// dialog says "work again", so the pause goes too.
    /// </summary>
    private void ResumeIfPaused(string source)
    {
        if (_s.Pause.IsPaused)
        {
            _s.Pause.Resume(source);
        }
    }

    private void ClearHalt()
    {
        var halt = _s.Halt.Current;
        var text = halt is null ? "Clear the HALT?" : "The companion stopped: " + HostMessages.HaltText(halt.Code, halt.Subject) + "\n\nHave you checked the game? Clear the HALT and the stop?";
        if (TrayForms.Confirm("TOS companion", text, "Clear"))
        {
            _s.Halt.Clear("tray");
            _s.Stop.Clear();
            ResumeIfPaused("halt.cleared");
            _s.NotificationGate.Reset("halt.");
            _s.NotificationGate.Reset("host.stopped");
            Journal("halt.cleared");
        }
    }

    /// <summary>The knowledge profile for the window's rules page, or null when the store cannot open it.</summary>
    private ICharacterKnowledge? SafeProfile()
    {
        try
        {
            return _s.Knowledge.Profile;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private void OpenKnowledge()
    {
        using var form = new KnowledgeForm(_s.Knowledge.Profile, _s.Clock, () => _s.Log.Position, () => _jobs.IsJobRunning);
        form.ShowDialog();
    }

    /// <summary>
    /// Opens the Kitty Tea House helper (one at a time; a second click shows the open one again). It only watches the game
    /// while it is open and no job runs.
    /// </summary>
    private void OpenTeaHouse()
    {
        if (_exiting)
        {
            return;
        }

        if (_teaHouse is { IsDisposed: false } open)
        {
            if (open.WindowState == FormWindowState.Minimized)
            {
                open.WindowState = FormWindowState.Normal;
            }

            open.Activate();
            return;
        }

        _teaHouse = new TeaHouseForm(_s, () => _jobs.IsJobRunning, new TeaHouseHands(_jobs, _s.TeaHouse, _s.Stop, _s.Halt));
        _teaHouse.FormClosed += (_, _) => _teaHouse = null;
        _teaHouse.Show();
    }

    /// <summary>Opens the Crafting window (one at a time; a second click shows the open one again).</summary>
    /// <summary>
    /// The companion's main window (<see cref="MainForm"/>): the crafting window as its Craft tab, the queue, the activity
    /// and the settings (the same actions as the tray menu, with the same confirmations).
    /// </summary>
    private void OpenMain() => OpenMain(null);

    private void OpenMain(string? tab)
    {
        if (_exiting)
        {
            return;
        }

        if (string.Equals(tab, "teahouse", StringComparison.OrdinalIgnoreCase))
        {
            OpenTeaHouse();
            return;
        }

        if (_main is { IsDisposed: false } open)
        {
            if (open.WindowState == FormWindowState.Minimized)
            {
                open.WindowState = FormWindowState.Normal;
            }

            open.Activate();
            if (tab is not null)
            {
                open.SelectTab(tab);
            }

            return;
        }

        var backend = new HostCraftingBackend(_s, _jobs);
        var vm = new CraftingViewModel(backend);
        var cfg = () => _s.Config.Current;
        var settings = new List<SettingToggle>
        {
            new("Crafting automation", "The companion may press Make.", () => cfg().Automation.Crafting, on =>
                Change(on ? "Turn ON crafting automation? The companion may then start crafts." : "Turn OFF crafting automation?",
                    c => c with { Automation = c.Automation with { Crafting = on } }, "automation.crafting." + (on ? "on" : "off"))),
            new("Slide clicks", "Clicks by sliding the game window under your resting mouse (needed for crafting and gathering).", () => cfg().Input.SlideClick, on =>
                Change(on ? "Allow slide clicks again?" : "Turn OFF slide clicks? Only key-based jobs will run.",
                    c => c with { Input = c.Input with { SlideClick = on } }, "input.slideClick." + (on ? "on" : "off"))),
            new("Homestead automation", "The companion may collect homestead products (not used yet).", () => cfg().Automation.Homestead, on =>
                Change(on ? "Turn ON homestead automation? The companion may then collect homestead products." : "Turn OFF homestead automation?",
                    c => c with { Automation = c.Automation with { Homestead = on } }, "automation.homestead." + (on ? "on" : "off"))),
            new("May close chat/mail windows", "At the start of a job, chat or mail windows you left open may be closed with Esc.", () => cfg().Policy.CloseUserUisAtJobStart, on =>
                Change(on ? "Allow the companion to close chat or mail windows you left open (Esc) at the start of a job?" : "Stop closing chat or mail windows at the start of a job?",
                    c => c with { Policy = c.Policy with { CloseUserUisAtJobStart = on } }, "policy.closeUserUis." + (on ? "on" : "off"))),
        };
        var actions = new MainActions(
            () => _s.Pause.IsPaused,
            () => _s.Pause.Pause("tray"),
            () => _s.Pause.Resume("tray"),
            () => StopNow(StopSource.Tray, "tray.stop"),
            () => _s.Halt.IsHalted,
            ClearHalt,
            settings,
            TrustBuild,
            Path.Combine(_s.Paths.RuntimeDir, "journal"),
            () => _s.Stop.IsStopRequested && !_s.Halt.IsHalted,
            ClearStop,
            () => SafeProfile(),
            () => _jobs.IsJobRunning,
            () => _ruleMode.On,
            on => _ruleMode.Set(on),
            _host.AwayCard,
            _ruleMode.Status,
            _host.Dailies is { } dailies ? new Today.HostTodayBackend(dailies) : null);
        var form = new MainForm(vm, backend, actions);
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_main, form))
            {
                _main = null;
            }
        };
        _main = form;
        form.Show();
        if (tab is not null)
        {
            form.SelectTab(tab);
        }
    }

    private void OpenCrafting()
    {
        if (_exiting)
        {
            return;
        }

        if (_crafting is { IsDisposed: false } open)
        {
            if (open.WindowState == FormWindowState.Minimized)
            {
                open.WindowState = FormWindowState.Normal;
            }

            open.Activate();
            return;
        }

        var form = new CraftingForm(new CraftingViewModel(new HostCraftingBackend(_s, _jobs)));
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_crafting, form))
            {
                _crafting = null;
            }
        };
        _crafting = form;
        form.Show();
    }

    private void CloseCrafting()
    {
        var form = _crafting;
        _crafting = null;
        if (form is { IsDisposed: false })
        {
            form.Close();
            form.Dispose();
        }
    }

    /// <summary>
    /// Advanced > Game folder...: shows the folder in use and saves another one the user chooses (same validation as the
    /// start dialog). The new folder is used after a restart; the companion never restarts by itself.
    /// </summary>
    private void ChooseGameFolder()
    {
        var current = _s.Paths.GameDir;
        var running = GameFolderResolver.FindRunningGame();
        var elsewhere = GameFolderResolver.IsOtherGameFolder(running, current) ? running : null;
        var variableWins = CompanionPaths.NormalizeGameDir(Environment.GetEnvironmentVariable(CompanionPaths.GameDirVariable)) is not null;
        if (GameFolderDialog.AskFromTray(current, elsewhere, variableWins) is not { } chosen)
        {
            return;
        }

        if (!new GameFolderStore(_s.Paths.RuntimeDir).Save(chosen))
        {
            _s.Notifier.Notify(HostMessages.Host("game.folderNotSaved"));
            return;
        }

        Journal("gameFolder.changed");
        _s.Notifier.Notify(HostMessages.Host("game.folderSaved"));
    }

    /// <summary>When the last connect found the game but its TOS.exe is not the one in use, look at most once a minute.</summary>
    private static readonly TimeSpan FolderHintPeriod = TimeSpan.FromMinutes(1);

    private bool _folderHintShown;
    private DateTimeOffset _folderHintChecked = DateTimeOffset.MinValue;

    /// <summary>
    /// One plain-words notification per run when GameIO refused the running game because its TOS.exe lives in another
    /// valid game folder (ExePathMismatch). Nothing switches by itself: the exe-path identity check is a safety rule.
    /// </summary>
    private void CheckGameFolderMismatch()
    {
        if (_folderHintShown || _exiting || _s.GameIo.LastConnectProblem != ConnectProblem.ExePathMismatch)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _folderHintChecked < FolderHintPeriod)
        {
            return;
        }

        _folderHintChecked = now;
        if (GameFolderResolver.IsOtherGameFolder(GameFolderResolver.FindRunningGame(), _s.Paths.GameDir))
        {
            _folderHintShown = true;
            _s.Notifier.Notify(HostMessages.Host("game.folderMismatch"));
        }
    }

    private void TrustBuild()
    {
        var trust = _host.FileTrust;
        var scanned = trust?.Scanned;
        if (trust is null || scanned is null)
        {
            return;
        }

        var cmp = trust.Comparison?.Code ?? "unknown";
        var text = string.Create(CultureInfo.InvariantCulture,
            $"Build hash: {scanned.ShortHash}\nFiles: {scanned.Files.Count}\nCompared with the trusted list: {cmp}\n\nTrust exactly this build? Only do this for a build you made or installed yourself.");
        if (!TrayForms.Confirm("Trust this build", text, "Trust this build"))
        {
            return;
        }

        var result = trust.TrustCurrentBuild();
        Journal(result);
        if (!string.Equals(result, "trust.ok", StringComparison.Ordinal))
        {
            _s.Notifier.Notify(new Notification(NotificationKind.Warning, result, "The build could not be trusted (it changed since the companion started, or the list could not be written). Restart the companion and try again."));
        }
    }

    private void ExitHost()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        CloseCrafting();
        _icon.Visible = false;
        _host.Shutdown();
        ExitThread();
    }

    private void Journal(string code)
    {
        try
        {
            _s.Journal.Write(new Contracts.Engine.JournalRecord(_s.Clock.UtcNow, Contracts.Engine.JournalKind.ConfigChange, null, null, null,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["change"] = code, ["by"] = "tray" }));
            _s.Journal.Flush();
        }
        catch (Exception)
        {
            // best effort
        }
    }
}
