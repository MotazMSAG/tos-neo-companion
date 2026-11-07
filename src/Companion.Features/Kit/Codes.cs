using System.Text;
using System.Text.RegularExpressions;

namespace Companion.Features.Kit;

/// <summary>
/// Code strings for job results and journal fields (PRIV-14): the journal's code alphabet, at most 120 characters.
/// Shared by the feature host and every feature (compiled into each feature assembly; no I/O, no game access).
/// </summary>
internal static class Codes
{
    /// <summary>Same alphabet as <c>JournalRecord</c> code values.</summary>
    private static readonly Regex CodePattern = new(@"^[A-Za-z0-9_.:,()\[\]/+\- ]{0,120}$", RegexOptions.CultureInvariant);

    /// <summary>Max code length.</summary>
    public const int MaxLength = 120;

    /// <summary>True if <paramref name="value"/> is a valid code.</summary>
    public static bool IsCode(string? value) => value is not null && CodePattern.IsMatch(value);

    /// <summary>Returns a code: disallowed characters become '_', the result is truncated to <see cref="MaxLength"/>.</summary>
    public static string Safe(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "none";
        }

        if (IsCode(value))
        {
            return value;
        }

        var sb = new StringBuilder(Math.Min(value.Length, MaxLength));
        foreach (var ch in value)
        {
            if (sb.Length >= MaxLength)
            {
                break;
            }

            sb.Append(IsCodeChar(ch) ? ch : '_');
        }

        return sb.ToString();
    }

    /// <summary>Joins codes with ',' and keeps the result within <see cref="MaxLength"/> (a '+' marks truncation).</summary>
    public static string Join(string prefix, IEnumerable<string> parts)
    {
        var sb = new StringBuilder(prefix);
        var first = true;
        foreach (var raw in parts)
        {
            var part = Safe(raw);
            var extra = (first ? 0 : 1) + part.Length;
            if (sb.Length + extra > MaxLength)
            {
                if (sb.Length < MaxLength)
                {
                    sb.Append('+');
                }

                break;
            }

            if (!first)
            {
                sb.Append(',');
            }

            sb.Append(part);
            first = false;
        }

        return Safe(sb.ToString());
    }

    private static bool IsCodeChar(char ch) =>
        ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
            or '_' or '.' or ':' or ',' or '(' or ')' or '[' or ']' or '/' or '+' or '-' or ' ';
}
