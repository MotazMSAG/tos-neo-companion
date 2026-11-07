using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Perception;

namespace Companion.Perception.Internal;

/// <summary>A parsed value before the agreement and bound checks.</summary>
/// <param name="Ok">True if the text matched the rule.</param>
/// <param name="Value">First number.</param>
/// <param name="Second">Second number (pairs).</param>
/// <param name="Text">Text (Text rule, equipment level).</param>
/// <param name="Range">Range (abbreviated).</param>
/// <param name="Code">Detail code.</param>
internal sealed record Parsed(bool Ok, long? Value, long? Second, string? Text, ValueRange? Range, string Code)
{
    public static Parsed Fail(string code) => new(false, null, null, null, null, code);
}

/// <summary>Strict parsing of OCR/digit text per <see cref="ParseRule"/> (regex per rule, never a guess).</summary>
internal static partial class ValueParser
{
    /// <summary>
    /// Canonical numeric text: whitespace removed, thousands separators dropped, the unambiguous OCR confusions
    /// O/o→0 and l/I/|→1 mapped. Agreement with the digit reader is still required before a value is accepted.
    /// </summary>
    public static string NormalizeNumeric(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case var c when char.IsWhiteSpace(c):
                case ',':
                    break;
                case 'O' or 'o':
                    sb.Append('0');
                    break;
                case 'l' or 'I' or '|':
                    sb.Append('1');
                    break;
                case 'k':
                    sb.Append('K');
                    break;
                case '\\':
                    sb.Append('/');
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Collapsed text (trim, single spaces) for Text rules.</summary>
    public static string NormalizeText(string? s) =>
        string.Join(' ', (s ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Parses canonical text by rule and pair semantics.</summary>
    public static Parsed Parse(string text, ParseRule rule, PairSemantics pair)
    {
        switch (rule)
        {
            case ParseRule.Int:
                return IntPattern().Match(text) is { Success: true } im
                    ? new Parsed(true, long.Parse(im.Value, CultureInfo.InvariantCulture), null, null, null, "int")
                    : Parsed.Fail("int-format");

            case ParseRule.Pair:
            {
                if (pair == PairSemantics.None)
                {
                    return Parsed.Fail("pair-semantics-missing");
                }

                var m = PairPattern().Match(text);
                if (!m.Success)
                {
                    return Parsed.Fail("pair-format");
                }

                var a = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var b = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                return pair switch
                {
                    PairSemantics.StoredCapacity when b <= 0 || a > b => Parsed.Fail("pair-stored-exceeds-capacity"),
                    PairSemantics.AssignedSlots when b <= 0 || a > b => Parsed.Fail("pair-assigned-exceeds-slots"),
                    PairSemantics.OwnedRequired when b <= 0 => Parsed.Fail("pair-required-zero"),
                    PairSemantics.Progress when b <= 0 || a > b => Parsed.Fail("pair-progress-exceeds-target"),
                    _ => new Parsed(true, a, b, null, null, "pair"),
                };
            }

            case ParseRule.Abbreviated:
            {
                var m = AbbreviatedPattern().Match(text);
                if (!m.Success)
                {
                    return Parsed.Fail("abbrev-format");
                }

                var whole = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var frac = m.Groups[2].Success ? m.Groups[2].Value : string.Empty;
                var unit = m.Groups[3].Value switch
                {
                    "K" => 1_000L,
                    "M" => 1_000_000L,
                    "B" => 1_000_000_000L,
                    _ => 1L,
                };
                if (unit == 1 && frac.Length > 0)
                {
                    return Parsed.Fail("abbrev-format");
                }

                // the displayed value is truncated: "1.5K" covers 1500..1599, "14M" covers 14,000,000..14,999,999
                var step = unit;
                var min = whole * unit;
                if (frac.Length > 0)
                {
                    step = unit / (long)Math.Pow(10, frac.Length);
                    if (step < 1)
                    {
                        return Parsed.Fail("abbrev-format");
                    }

                    min += long.Parse(frac, CultureInfo.InvariantCulture) * step;
                }

                var max = min + step - 1;
                return new Parsed(true, unit == 1 ? min : null, null, null, new ValueRange(min, max), unit == 1 ? "abbrev-exact" : "abbrev-range");
            }

            case ParseRule.ItemCellCount:
            {
                if (text.Length == 0)
                {
                    // empty = 1 only for a known stackable: the caller decides (SCAN-03)
                    return Parsed.Fail("cell-empty");
                }

                if (LevelPattern().Match(text) is { Success: true } lv)
                {
                    return new Parsed(true, null, null, "Lv" + lv.Groups[1].Value, null, "cell-equipment");
                }

                return IntPattern().Match(text) is { Success: true } c
                    ? new Parsed(true, long.Parse(c.Value, CultureInfo.InvariantCulture), null, null, null, "cell-count")
                    : Parsed.Fail("cell-format");
            }

            case ParseRule.Text:
            {
                var t = NormalizeText(text);
                return t.Length is > 0 and <= 64 && TextPattern().IsMatch(t)
                    ? new Parsed(true, null, null, t, null, "text")
                    : Parsed.Fail("text-format");
            }

            default:
                return Parsed.Fail("rule-unknown");
        }
    }

    /// <summary>Applies the region's plausible bounds to every number (and range end) in a parsed value.</summary>
    public static bool WithinBounds(Parsed p, long? min, long? max)
    {
        bool In(long v) => (min is null || v >= min) && (max is null || v <= max);
        return (p.Value is null || In(p.Value.Value))
            && (p.Second is null || In(p.Second.Value))
            && (p.Range is null || (In(p.Range.Min) && In(p.Range.Max)));
    }

    [GeneratedRegex(@"^\d{1,12}$", RegexOptions.CultureInvariant)]
    private static partial Regex IntPattern();

    [GeneratedRegex(@"^(\d{1,7})/(\d{1,7})$", RegexOptions.CultureInvariant)]
    private static partial Regex PairPattern();

    [GeneratedRegex(@"^(\d{1,9})(?:\.(\d{1,3}))?([KMB]?)$", RegexOptions.CultureInvariant)]
    private static partial Regex AbbreviatedPattern();

    [GeneratedRegex(@"^(?:Lv|LV|lv|Lv\.)(\d{1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex LevelPattern();

    // '/' since 2026-10-15: the gathering picker's "Stock Limit: 353/500" line is read as text (its own font spaces the slash)
    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} '\-\.:()/]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TextPattern();
}
