using System.Drawing;
using System.Security.Principal;
using Companion.App.Hosting;
using Companion.App.Tray;
using Companion.Contracts;
using Companion.Contracts.Engine;

namespace Companion.App;

/// <summary>
/// Entry point (SPEC 4 "App start sequence"):
/// 1. <c>--guardian &lt;hostPid&gt; &lt;hostStartTicks&gt;</c> runs the guardian and exits;
/// 2. asserts 64-bit and elevated, takes the single-instance mutex (AbandonedMutexException = acquired);
/// 2a. resolves the game folder (<see cref="GameFolderResolver"/>; a dialog only when nothing is found);
/// 3-9. <see cref="HostRuntime.Start"/>, then the tray message loop.
/// </summary>
internal static class Program
{
    /// <summary>Single-instance mutex (both instances are elevated; clients use the pipe, never the mutex).</summary>
    public const string MutexName = @"Local\TOS-companion.host";

    /// <summary>Exit codes.</summary>
    public const int ExitOk = 0;

    /// <summary>Guardian arguments invalid.</summary>
    public const int ExitBadArguments = 64;

    /// <summary>Not a 64-bit process.</summary>
    public const int ExitNot64Bit = 65;

    /// <summary>Not elevated (the manifest requires administrator).</summary>
    public const int ExitNotElevated = 66;

    /// <summary>Another host owns the mutex.</summary>
    public const int ExitAlreadyRunning = 67;

    /// <summary>The game folder was not found and the user chose Exit in the start dialog.</summary>
    public const int ExitNoGameFolder = 68;

    [STAThread]
    private static int Main(string[] args)
    {
        if (GuardianArgs.IsGuardian(args))
        {
            var g = GuardianArgs.TryParse(args);
            return g is null ? ExitBadArguments : Guardian.Run(g);
        }

        if (!Environment.Is64BitProcess)
        {
            return ExitNot64Bit;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || identity.User is null)
        {
            return ExitNotElevated;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var mutex = new Mutex(false, MutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        if (!owned)
        {
            ShowAlreadyRunning();
            return ExitAlreadyRunning;
        }

        // fatal-error recorder (2026-10-23: the host vanished mid-job with nothing written anywhere): any unhandled error
        // leaves a crash report in the diagnostics folder; a UI-thread error is reported instead of ending the app.
        // This must come before the start dialog below: WinForms refuses to change the exception mode once a window
        // exists on this thread (review 2026-11-08: the first start after choosing a folder crashed here).
        InstallCrashHandlers(CompanionPaths.DefaultRuntimeDir);

        // the game folder, found once before the host starts (no environment variable needed): TOS_GAME_DIR, the saved
        // folder, the running game, the usual places, else a small dialog; a found or chosen folder is saved
        var resolved = ResolveGameFolder(identity.User);
        if (resolved is null)
        {
            mutex.ReleaseMutex();
            return ExitNoGameFolder;
        }

        var paths = CompanionPaths.ForGame(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), resolved.Value.Choice.Folder);

        try
        {
            var clock = new SystemClock();
            using var host = new HostRuntime(paths, clock, identity.User);
            host.Start();
            JournalGameFolder(host, clock, resolved.Value.Choice, resolved.Value.Saved);
            using var tray = new TrayApp(host);
            Application.Run(tray);
            host.Shutdown();
            return ExitOk;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// The fatal-error recorder. Must run before any window is created on the main thread (the start dialog included):
    /// <see cref="Application.SetUnhandledExceptionMode(UnhandledExceptionMode)"/> throws once one exists.
    /// </summary>
    private static void InstallCrashHandlers(string runtimeDir)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(runtimeDir, "unhandled", e.ExceptionObject as Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => CrashLog.Write(runtimeDir, "uiThread", e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(runtimeDir, "task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// The game folder for this start (<see cref="GameFolderResolver"/>) and whether it was saved; null when the user
    /// pressed Exit in the dialog. The runtime folder is secured first (the host does it again at start) so a found folder
    /// can be saved there. A failed save is journaled after the host starts; the guardian gets the folder on its command
    /// line, so it never depends on the saved file.
    /// </summary>
    private static (GameFolderChoice Choice, bool? Saved)? ResolveGameFolder(SecurityIdentifier user)
    {
        var runtimeDir = CompanionPaths.DefaultRuntimeDir;
        var canSave = RuntimeDirectory.EnsureProtected(runtimeDir, user) is null;
        var store = new GameFolderStore(runtimeDir);
        var choice = GameFolderResolver.Resolve(
            Environment.GetEnvironmentVariable(CompanionPaths.GameDirVariable),
            store.Read(),
            GameFolderResolver.DetectOnThisPc,
            GameFolderDialog.AskAtStart);
        if (choice is null)
        {
            return null;
        }

        bool? saved = choice.ShouldSave ? canSave && store.Save(choice.Folder) : null;
        return (choice, saved);
    }

    /// <summary>Journals where the game folder came from, and a failed save (the search then runs again next start).</summary>
    private static void JournalGameFolder(HostRuntime host, IClock clock, GameFolderChoice choice, bool? saved)
    {
        try
        {
            if (host.Services is not { } s)
            {
                return;
            }

            s.Journal.Write(new JournalRecord(clock.UtcNow, saved is false ? JournalKind.Incident : JournalKind.HostStart, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["event"] = saved is false ? "gameFolder.saveFailed" : "gameFolder",
                ["source"] = choice.Source.ToString(),
                ["saved"] = saved,
            }));
        }
        catch (Exception)
        {
            // the journal is a record only; the host runs on
        }
    }

    /// <summary>A short-lived, non-activating balloon: "already running".</summary>
    private static void ShowAlreadyRunning()
    {
        using var icon = new NotifyIcon { Icon = SystemIcons.Shield, Text = "TOS companion", Visible = true };
        var n = HostMessages.Host("host.alreadyRunning");
        icon.ShowBalloonTip(4000, "TOS companion", n.Text, ToolTipIcon.Info);
        using var timer = new System.Windows.Forms.Timer { Interval = 5000 };
        timer.Tick += (_, _) => Application.ExitThread();
        timer.Start();
        Application.Run();
        icon.Visible = false;
    }
}
