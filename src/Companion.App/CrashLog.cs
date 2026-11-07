using System.Globalization;
using System.Text;

namespace Companion.App;

/// <summary>
/// Fatal-error recorder (2026-10-23: the host vanished mid-job with no journal line, no Windows crash event and no
/// guardian record, so nothing said why). Unhandled exceptions of any thread, of the UI thread and of unobserved tasks are
/// written to <c>&lt;runtime&gt;\diagnostics\&lt;utc&gt;-crash-&lt;kind&gt;\crash.txt</c>: UTC, kind, process, exception
/// types, messages and stack traces (code locations only; no chat, no screen content). Never throws.
/// </summary>
internal static class CrashLog
{
    /// <summary>Writes one crash report; returns its path, or null when it could not be written.</summary>
    public static string? Write(string runtimeDir, string kind, Exception? error)
    {
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd't'HHmmss'z'", CultureInfo.InvariantCulture);
            var dir = Path.Combine(runtimeDir, "diagnostics", stamp + "-crash-" + kind);
            Directory.CreateDirectory(dir);
            var text = new StringBuilder()
                .Append("utc: ").AppendLine(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                .Append("kind: ").AppendLine(kind)
                .Append("process: ").Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)).Append(' ').AppendLine(Environment.CommandLine.Contains("--guardian", StringComparison.Ordinal) ? "guardian" : "host")
                .AppendLine()
                .AppendLine(error?.ToString() ?? "(no exception object)")
                .ToString();
            var path = Path.Combine(dir, "crash.txt");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Writes the guardian's view of how the host ended (its exit code), when the exit was not clean.</summary>
    public static void WriteHostExit(string runtimeDir, int? exitCode, string how)
    {
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd't'HHmmss'z'", CultureInfo.InvariantCulture);
            var dir = Path.Combine(runtimeDir, "diagnostics", stamp + "-host-exit");
            Directory.CreateDirectory(dir);
            var code = exitCode is { } c ? c.ToString(CultureInfo.InvariantCulture) + " (0x" + c.ToString("X8", CultureInfo.InvariantCulture) + ")" : "unknown";
            File.WriteAllText(Path.Combine(dir, "exit.txt"),
                "utc: " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + Environment.NewLine
                + "how: " + how + Environment.NewLine
                + "exitCode: " + code + Environment.NewLine
                + "hint: 0xC00000FD = stack overflow, 0xE0434352 = unhandled .NET exception, 0xC0000005 = access violation, 1 = terminated (End task / taskkill)" + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // best effort
        }
    }
}
