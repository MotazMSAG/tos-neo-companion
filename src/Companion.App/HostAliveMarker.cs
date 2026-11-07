using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Companion.App;

/// <summary>
/// The run marker (audit R1.12, incident 12): on 2026-10-22 the host vanished mid-job and left nothing, no journal line,
/// no crash file and no guardian record, because a kill of the process tree ends the guardian too. While the host runs,
/// <c>&lt;runtime&gt;\run\host.alive</c> holds its process id, process start time, build hash and last heartbeat. A clean
/// exit deletes it. A start that finds it while that process no longer runs records <c>host.previousUnclean</c> with
/// those values, so every exit leaves a record (design rule 5). Plain numbers only; never throws.
/// </summary>
internal static class HostAliveMarker
{
    internal static string PathOf(string runtimeDir) => Path.Combine(runtimeDir, "run", "host.alive");

    /// <summary>
    /// The fields of a marker left by a host that no longer runs (an unclean exit), or null when there is none or its
    /// process is still alive.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? TakeLeftover(string runtimeDir, Func<long, long, bool> isAlive)
    {
        try
        {
            var path = PathOf(runtimeDir);
            if (!File.Exists(path))
            {
                return null;
            }

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(path))
            {
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    fields[line[..eq]] = line[(eq + 1)..].Trim();
                }
            }

            return isAlive(Number(fields, "pid"), Number(fields, "startTicks")) ? null : fields;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A numeric field of a marker, 0 when missing or not a number.</summary>
    public static long Number(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>Writes (or refreshes) this host's marker.</summary>
    public static void Write(string runtimeDir, string? buildHash, DateTimeOffset nowUtc)
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var path = PathOf(runtimeDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = new StringBuilder()
                .Append("pid=").AppendLine(Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
                .Append("startTicks=").AppendLine(self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture))
                .Append("buildHash=").AppendLine(buildHash ?? string.Empty)
                .Append("lastBeatUnix=").AppendLine(nowUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
                .ToString();
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception)
        {
            // best effort: the journal still has its own last line
        }
    }

    /// <summary>Deletes the marker (a clean exit).</summary>
    public static void Delete(string runtimeDir)
    {
        try
        {
            File.Delete(PathOf(runtimeDir));
        }
        catch (Exception)
        {
            // best effort
        }
    }

    /// <summary>True when the process <paramref name="pid"/> runs and started at <paramref name="startTicks"/> (not a reused id).</summary>
    public static bool IsAlive(long pid, long startTicks)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
