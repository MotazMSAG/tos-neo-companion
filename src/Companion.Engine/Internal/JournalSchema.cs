using System.Globalization;
using System.Text.RegularExpressions;

namespace Companion.Engine.Internal;

/// <summary>
/// Journal field lint (SPEC 6.10, PRIV-14): values are bool, integer, double or a CODE string matching the regex
/// documented on <c>JournalRecord</c>. Role-id-like strings are rejected too. Anything else becomes "&lt;redacted&gt;".
/// </summary>
internal static partial class JournalSchema
{
    public const string Redacted = "<redacted>";

    [GeneratedRegex(@"^[A-Za-z0-9_.:,()\[\]/+\- ]{0,120}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeRegex();

    [GeneratedRegex(@"\b\d{4,5}-\d{4,6}\b", RegexOptions.CultureInvariant)]
    private static partial Regex RoleIdRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.]{0,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();

    /// <summary>True for null or a valid code string.</summary>
    public static bool IsCode(string? s) => s is null || (CodeRegex().IsMatch(s) && !RoleIdRegex().IsMatch(s));

    /// <summary>True for a valid field key.</summary>
    public static bool IsKey(string? k) => k is not null && KeyRegex().IsMatch(k);

    /// <summary>The value if it is a code, otherwise the redaction marker.</summary>
    public static string? Code(string? s) => IsCode(s) ? s : Redacted;

    /// <summary>Converts a field value to its journal form; <paramref name="redacted"/> tells whether it was replaced.</summary>
    public static object? Sanitize(object? value, out bool redacted)
    {
        redacted = false;
        switch (value)
        {
            case null:
                return null;
            case bool b:
                return b;
            case int or long or short or byte or uint or ushort or sbyte:
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            case ulong ul:
                return ul <= long.MaxValue ? (long)ul : (double)ul;
            case double d:
                return double.IsFinite(d) ? d : null;
            case float f:
                return float.IsFinite(f) ? (double)f : null;
            case decimal m:
                return (double)m;
            case TimeSpan ts:
                return Math.Round(ts.TotalMilliseconds, 3);
            case DateTimeOffset dto:
                return dto.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            case Enum e:
                {
                    var s = e.ToString();
                    if (IsCode(s))
                    {
                        return s;
                    }
                    redacted = true;
                    return Redacted;
                }
            case string s:
                if (IsCode(s))
                {
                    return s;
                }
                redacted = true;
                return Redacted;
            default:
                redacted = true;
                return Redacted;
        }
    }

    /// <summary>True when every field of the record is already in journal form (lint used by tests).</summary>
    public static bool IsClean(Contracts.Engine.JournalRecord record, out string? problem)
    {
        problem = null;
        if (!IsCode(record.JobId) || !IsCode(record.ProcedureId) || !IsCode(record.StepId))
        {
            problem = "ids";
            return false;
        }
        foreach (var (k, v) in record.Fields)
        {
            if (!IsKey(k))
            {
                problem = "key:" + k;
                return false;
            }
            Sanitize(v, out var red);
            if (red)
            {
                problem = "value:" + k;
                return false;
            }
        }
        return true;
    }
}
