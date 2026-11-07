using System.Diagnostics;
using System.Globalization;
using Companion.App.Hosting;
using Companion.Contracts;
using Companion.Contracts.Engine;

namespace Companion.App;

/// <summary>Parsed guardian arguments: <c>--guardian &lt;hostPid&gt; &lt;hostStartTicks&gt;</c>.</summary>
/// <param name="HostPid">Host process id.</param>
/// <param name="HostStartTicks">Host process start time (UTC ticks), so a recycled PID is never mistaken for the host.</param>
internal sealed record GuardianArgs(int HostPid, long HostStartTicks)
{
    /// <summary>The switch.</summary>
    public const string Switch = "--guardian";

    /// <summary>True when <paramref name="args"/> asks for guardian mode (first argument is the switch).</summary>
    public static bool IsGuardian(IReadOnlyList<string> args) =>
        args.Count > 0 && string.Equals(args[0], Switch, StringComparison.Ordinal);

    /// <summary>Parses exactly <c>--guardian pid ticks</c> (positive integers, nothing else); null otherwise.</summary>
    public static GuardianArgs? TryParse(IReadOnlyList<string> args)
    {
        if (args.Count != 3 || !IsGuardian(args))
        {
            return null;
        }

        if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            return null;
        }

        if (!long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0
            || ticks > DateTime.MaxValue.Ticks)
        {
            return null;
        }

        return new GuardianArgs(pid, ticks);
    }

    /// <summary>The command line the host uses to start its guardian.</summary>
    public string ToArguments() =>
        string.Create(CultureInfo.InvariantCulture, $"{Switch} {HostPid} {HostStartTicks}");
}

/// <summary>
/// Guardian process (SPEC 10.1, 7.6): started by the host with UseShellExecute=false, so it inherits the elevated token
/// without a UAC prompt. It has no tray and no pipe. It waits for the host process to exit, then runs the marker-based
/// crash recovery (move-only, only when the marker proves the window is ours and still displaced) and exits.
/// </summary>
internal static class Guardian
{
    /// <summary>Runs the guardian; returns the process exit code.</summary>
    public static int Run(GuardianArgs args)
    {
        // the same game folder as the host, without any UI: TOS_GAME_DIR, then the saved folder, then the default
        var gameDir = GameFolderResolver.ForGuardian(
            Environment.GetEnvironmentVariable(CompanionPaths.GameDirVariable),
            new GameFolderStore(CompanionPaths.DefaultRuntimeDir).Read());
        var paths = CompanionPaths.ForGame(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), gameDir);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(paths.RuntimeDir, "guardian", e.ExceptionObject as Exception);
        var exitCode = WaitForHostExit(args);

        // how the host ended (2026-10-23): a non-zero exit code is written to the diagnostics folder at once, before
        // anything that could fail (the stack overflow, unhandled .NET exception and End task codes tell causes apart)
        if (exitCode is not 0)
        {
            CrashLog.WriteHostExit(paths.RuntimeDir, exitCode, exitCode is null ? "exit code unavailable" : "exited");
        }

        var clock = new SystemClock();
        var services = Composition.Create(paths, clock, currentGameBuild: null);
        try
        {
            var report = services.GameIo.RecoverLeftoverDisplacement();
            services.Journal.Write(new JournalRecord(clock.UtcNow, JournalKind.Incident, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["event"] = "guardian.recover",
                ["hostExitCode"] = (long?)exitCode,
                ["attempted"] = report.Attempted,
                ["success"] = report.Success,
                ["rung"] = report.Rung.ToString(),
            }));
            services.Journal.Flush();
            return report.Attempted && !report.Success ? 2 : 0;
        }
        catch (Exception ex)
        {
            // the one case the guardian exists for must leave a record (audit R3.5 #47: this path wrote nothing)
            CrashLog.Write(paths.RuntimeDir, "guardian.recover", ex);
            try
            {
                services.Journal.Write(new JournalRecord(clock.UtcNow, JournalKind.Incident, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["event"] = "guardian.recover",
                    ["hostExitCode"] = (long?)exitCode,
                    ["error"] = ex.GetType().Name,
                }));
                services.Journal.Flush();
            }
            catch (Exception)
            {
                // the crash file above is the record
            }

            return 1;
        }
    }

    /// <summary>Blocks until the host process (same PID and start time) has exited; its exit code when readable.</summary>
    private static int? WaitForHostExit(GuardianArgs args)
    {
        Process host;
        try
        {
            host = Process.GetProcessById(args.HostPid);
        }
        catch (ArgumentException)
        {
            return null; // already gone
        }

        using (host)
        {
            try
            {
                if (host.StartTime.ToUniversalTime().Ticks != args.HostStartTicks)
                {
                    return null; // PID recycled: the host is gone
                }

                host.WaitForExit();
                return host.ExitCode;
            }
            catch (InvalidOperationException)
            {
                return null; // exited between the lookup and the wait
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null; // cannot query: fall through to recovery, which only acts on a matching marker
            }
        }
    }
}
