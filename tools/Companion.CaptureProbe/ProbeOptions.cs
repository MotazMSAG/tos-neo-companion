using System.Globalization;
using Companion.Contracts.Safety;

namespace Companion.CaptureProbe;

/// <summary>Command line of the capture probe (pure; tested offline).</summary>
/// <param name="Dx">Horizontal move of the game window.</param>
/// <param name="Dy">Vertical move of the game window.</param>
/// <param name="DelaysMs">Capture delays after the move, ascending.</param>
/// <param name="OutDir">Output folder, or null for probe-out\capture-probe-&lt;time&gt; next to the exe.</param>
internal sealed record ProbeOptions(int Dx, int Dy, IReadOnlyList<int> DelaysMs, string? OutDir)
{
    /// <summary>Default capture delays (ms after the move).</summary>
    public static readonly IReadOnlyList<int> DefaultDelays = [150, 400, 800];

    /// <summary>Largest delay: keeps the displacement short (c13 used up to 1500 ms).</summary>
    public const int MaxDelayMs = 1500;

    /// <summary>Most delays per run.</summary>
    public const int MaxDelays = 6;

    /// <summary>Usage text.</summary>
    public const string Usage =
        "Companion.CaptureProbe --dx <int> --dy <int> [--delays 150,400,800] [--out <dir>]\n" +
        "  Moves the (covered, not foreground, not minimized) game window by dx,dy behind the window under the cursor,\n" +
        "  captures it at home and at each delay with printwindow (PW_RENDERFULLCONTENT) and Windows.Graphics.Capture,\n" +
        "  moves it back home and writes PNGs (chat blacked out) plus report.txt (default folder: probe-out\\ next to the exe).\n" +
        "  No clicks, no focus or key messages.\n" +
        "  Run elevated (same integrity as the game), with the companion host stopped and the mouse resting still.\n" +
        "  Exit codes: 0 done, 1 usage, 2 refused (nothing moved), 3 stopped early (window back home), 4 window NOT verified home.";

    /// <summary>Parses the arguments. Throws <see cref="FormatException"/> with a short message.</summary>
    /// <param name="args">Arguments ("--dx -988", "--dx=-988" and "+544" are accepted).</param>
    public static ProbeOptions Parse(IReadOnlyList<string> args)
    {
        int? dx = null;
        int? dy = null;
        IReadOnlyList<int>? delays = null;
        string? outDir = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string name;
            string? value;
            var eq = arg.IndexOf('=', StringComparison.Ordinal);
            if (arg.StartsWith("--", StringComparison.Ordinal) && eq > 2)
            {
                name = arg[..eq];
                value = arg[(eq + 1)..];
            }
            else
            {
                name = arg;
                value = i + 1 < args.Count ? args[++i] : null;
            }

            if (!seen.Add(name))
            {
                throw new FormatException("duplicate option " + name);
            }

            if (value is null)
            {
                throw new FormatException("missing value for " + name);
            }

            switch (name)
            {
                case "--dx":
                    dx = Offset(name, value);
                    break;
                case "--dy":
                    dy = Offset(name, value);
                    break;
                case "--delays":
                    delays = Delays(value);
                    break;
                case "--out":
                    outDir = string.IsNullOrWhiteSpace(value) ? throw new FormatException("empty --out") : value;
                    break;
                default:
                    throw new FormatException("unknown option " + name);
            }
        }

        if (dx is null || dy is null)
        {
            throw new FormatException("--dx and --dy are required");
        }

        if (dx == 0 && dy == 0)
        {
            throw new FormatException("dx and dy are both 0: nothing to probe");
        }

        return new ProbeOptions(dx.Value, dy.Value, delays ?? DefaultDelays, outDir);
    }

    private static int Offset(string name, string value)
    {
        if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v))
        {
            throw new FormatException($"{name} must be an integer");
        }

        if (Math.Abs(v) > HardRules.MaxSlideDistancePx)
        {
            throw new FormatException($"{name} is beyond the {HardRules.MaxSlideDistancePx} px slide cap");
        }

        return v;
    }

    private static int[] Delays(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Length > MaxDelays)
        {
            throw new FormatException($"--delays takes 1 to {MaxDelays} values");
        }

        var list = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d < 1 || d > MaxDelayMs)
            {
                throw new FormatException($"--delays values must be whole ms in 1..{MaxDelayMs}");
            }

            if (i > 0 && d <= list[i - 1])
            {
                throw new FormatException("--delays must be strictly ascending");
            }

            list[i] = d;
        }

        return list;
    }
}
