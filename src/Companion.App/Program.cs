using System.Drawing;
using System.Security.Principal;
using Companion.App.Hosting;
using Companion.App.Tray;
using Companion.Contracts;

namespace Companion.App;

/// <summary>
/// Entry point (SPEC 4 "App start sequence"):
/// 1. <c>--guardian &lt;hostPid&gt; &lt;hostStartTicks&gt;</c> runs the guardian and exits;
/// 2. asserts 64-bit and elevated, takes the single-instance mutex (AbandonedMutexException = acquired);
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

        var paths = CompanionPaths.Default(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));

        // fatal-error recorder (2026-10-23: the host vanished mid-job with nothing written anywhere): any unhandled error
        // leaves a crash report in the diagnostics folder; a UI-thread error is reported instead of ending the app
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(paths.RuntimeDir, "unhandled", e.ExceptionObject as Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => CrashLog.Write(paths.RuntimeDir, "uiThread", e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(paths.RuntimeDir, "task", e.Exception);
            e.SetObserved();
        };

        try
        {
            using var host = new HostRuntime(paths, new SystemClock(), identity.User);
            host.Start();
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
