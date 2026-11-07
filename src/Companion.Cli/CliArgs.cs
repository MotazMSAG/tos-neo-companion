using System.Globalization;
using System.Text.RegularExpressions;

namespace Companion.Cli;

/// <summary>CLI commands (SPEC 10.5).</summary>
internal enum CliCommand
{
    /// <summary>Host status.</summary>
    Status,

    /// <summary>Today's daily checklist (reads the host's today.json; 2026-11-04).</summary>
    Dailies,
    /// <summary>Whitelisted jobs.</summary>
    Jobs,
    /// <summary>Submit a job.</summary>
    Submit,
    /// <summary>Job result.</summary>
    Result,
    /// <summary>Cancel a job.</summary>
    Cancel,
    /// <summary>Kill switch.</summary>
    Stop,
    /// <summary>Pause.</summary>
    Pause,
    /// <summary>Resume (refused after stop or HALT).</summary>
    Resume,
    /// <summary>Open the companion window (optionally on a tab).</summary>
    Show,
}

/// <summary>Parsed command line.</summary>
/// <param name="Command">Command.</param>
/// <param name="JobName">Job name (submit).</param>
/// <param name="Params">Parameters (submit).</param>
/// <param name="Wait">--wait (submit).</param>
/// <param name="Timeout">--timeout (submit --wait).</param>
/// <param name="JobId">Job id (result, cancel).</param>
internal sealed partial record CliArgs(CliCommand Command, string? JobName, IReadOnlyDictionary<string, string> Params, bool Wait, TimeSpan Timeout, string? JobId)
{
    /// <summary>Default --timeout.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Usage text.</summary>
    public const string Usage =
        "usage: Companion.Cli status | dailies | jobs | submit <JobName> [name=value ...] [--wait] [--timeout <s>] | result <id> | cancel <id> | stop | pause | resume | show [tab]";

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex JobNameRegex();

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex ParamNameRegex();

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex JobIdRegex();

    /// <summary>Parses the arguments; null and an error code when invalid.</summary>
    public static CliArgs? Parse(IReadOnlyList<string> args, out string error)
    {
        error = "usage";
        if (args is null || args.Count == 0)
        {
            return null;
        }

        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        var cmd = args[0].ToLowerInvariant();
        switch (cmd)
        {
            case "status":
            case "dailies":
            case "jobs":
            case "stop":
            case "pause":
            case "resume":
                if (args.Count != 1)
                {
                    error = "usage.extraArguments";
                    return null;
                }

                var c = cmd switch
                {
                    "status" => CliCommand.Status,
                    "dailies" => CliCommand.Dailies,
                    "jobs" => CliCommand.Jobs,
                    "stop" => CliCommand.Stop,
                    "pause" => CliCommand.Pause,
                    _ => CliCommand.Resume,
                };
                return new CliArgs(c, null, empty, false, DefaultTimeout, null);

            case "show":
                if (args.Count > 2 || (args.Count == 2 && !JobNameRegex().IsMatch(args[1])))
                {
                    error = "usage.tab";
                    return null;
                }

                return new CliArgs(CliCommand.Show, args.Count == 2 ? args[1] : null, empty, false, DefaultTimeout, null);

            case "result":
            case "cancel":
                if (args.Count != 2 || !JobIdRegex().IsMatch(args[1]))
                {
                    error = "usage.jobId";
                    return null;
                }

                return new CliArgs(cmd == "result" ? CliCommand.Result : CliCommand.Cancel, null, empty, false, DefaultTimeout, args[1]);

            case "submit":
                return ParseSubmit(args, out error);

            default:
                error = "usage.command";
                return null;
        }
    }

    private static CliArgs? ParseSubmit(IReadOnlyList<string> args, out string error)
    {
        if (args.Count < 2 || !JobNameRegex().IsMatch(args[1]))
        {
            error = "usage.jobName";
            return null;
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var wait = false;
        var timeout = DefaultTimeout;
        var timeoutGiven = false;
        for (var i = 2; i < args.Count; i++)
        {
            var a = args[i];
            if (string.Equals(a, "--wait", StringComparison.Ordinal))
            {
                if (wait)
                {
                    error = "usage.duplicate";
                    return null;
                }

                wait = true;
                continue;
            }

            if (string.Equals(a, "--timeout", StringComparison.Ordinal))
            {
                if (timeoutGiven || i + 1 >= args.Count
                    || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds < 1 || seconds > 86_400)
                {
                    error = "usage.timeout";
                    return null;
                }

                timeoutGiven = true;
                timeout = TimeSpan.FromSeconds(seconds);
                i++;
                continue;
            }

            var eq = a.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                error = "usage.param";
                return null;
            }

            var name = a[..eq];
            var value = a[(eq + 1)..];
            if (!ParamNameRegex().IsMatch(name) || value.Length == 0 || value.Length > 64 || value.Any(char.IsControl))
            {
                error = "usage.param";
                return null;
            }

            if (!parameters.TryAdd(name, value))
            {
                error = "usage.duplicate";
                return null;
            }

            if (parameters.Count > 16)
            {
                error = "usage.tooManyParams";
                return null;
            }
        }

        if (timeoutGiven && !wait)
        {
            error = "usage.timeoutWithoutWait";
            return null;
        }

        error = "ok";
        return new CliArgs(CliCommand.Submit, args[1], parameters, wait, timeout, null);
    }
}
