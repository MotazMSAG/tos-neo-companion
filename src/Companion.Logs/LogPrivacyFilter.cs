using System.Text.RegularExpressions;
using Companion.Contracts.Logs;

namespace Companion.Logs;

/// <summary>
/// Allow-list privacy filter for raw log lines (SPEC 12, PRIV-01/14, F31). A line is kept only if it is a timestamped
/// line whose message matches one of the exact allowed patterns (ui_mgr UI events, StageMgr focus, UpdateModule
/// LostFocuse, [graphic] resolution, collect_mgr auto-collect status, scene_load, lifeskill_cook_mgr is_block,
/// jank_detector, item_tips_mgr item tips: an item name and id are game data, not personal data; Kitty Tea House board
/// presses: a square and a piece number), or one of the two
/// press-block continuation lines ('|\tmodule: Name: ...' and
/// '|\tscreenPosition: (x, y)'). Everything else (chat, analytics, login/role dumps, pay, team/vendor, mount lines,
/// untimestamped dumps) is dropped. Role-id-like tokens are scrubbed from what is kept. Stateless and thread-safe;
/// the constructor has no side effects.
/// </summary>
public sealed partial class LogPrivacyFilter : ILogPrivacyFilter
{
    /// <summary>Replacement for scrubbed role-id-like tokens.</summary>
    internal const string Scrubbed = "<id>";

    /// <summary>Creates the filter (no side effects).</summary>
    public LogPrivacyFilter()
    {
    }

    /// <inheritdoc />
    public bool IsAllowed(string rawLine)
    {
        ArgumentNullException.ThrowIfNull(rawLine);
        var line = TrimEol(rawLine);
        if (line.Length >= 2 && line[0] == '|' && line[1] == '\t')
        {
            return PipeModule().IsMatch(line) || PipeScreenPosition().IsMatch(line);
        }

        var prefix = LogPatterns.TimestampPrefix().Match(line);
        if (!prefix.Success)
        {
            return false;
        }

        var msg = line.AsSpan(prefix.Length);
        return LogPatterns.UiVerb().IsMatch(msg)
            || UiGuideBan().IsMatch(msg)
            || LogPatterns.Focus().IsMatch(msg)
            || Press().IsMatch(msg)
            || Resolution().IsMatch(msg)
            || Collect().IsMatch(msg)
            || Scene().IsMatch(msg)
            || IsBlock().IsMatch(msg)
            || Jank().IsMatch(msg)
            || LogPatterns.ItemTip().IsMatch(msg)
            || LogPatterns.GridClick().IsMatch(msg);
    }

    /// <inheritdoc />
    public string? Filter(string rawLine)
    {
        ArgumentNullException.ThrowIfNull(rawLine);
        if (!IsAllowed(rawLine))
        {
            return null;
        }

        return LogPatterns.RoleIdLike().Replace(TrimEol(rawLine), Scrubbed);
    }

    private static string TrimEol(string s) => s.TrimEnd('\r', '\n');

    // Stricter (end-anchored) variants of the parse patterns: what is kept must be exactly the allowed shape.
    [GeneratedRegex(@"^\[LOG\] -> module/foundation/ui/ui_mgr\.lua:\d+\s+ui_mgr:true_show_ui ban by guide, ui_id\s+[A-Z][A-Z0-9_]*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex UiGuideBan();

    [GeneratedRegex(@"^UpdateModule LostFocuse(?: Name: [^\t]* \(UnityEngine\.GameObject\))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Press();

    [GeneratedRegex(@"^\|\tmodule: Name: [^\t]* \(UnityEngine\.GameObject\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PipeModule();

    [GeneratedRegex(@"^\|\tscreenPosition: \(-?[\d.]+, -?[\d.]+\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PipeScreenPosition();

    [GeneratedRegex(@"^\[LOG\] -> (?:\S+\.lua:\d+\s+)?\[graphic\] (?:Current screen resolution|Device resolution)\s+\d+\s+\d+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Resolution();

    [GeneratedRegex(@"^\[LOG\] -> \S+\.lua:\d+\s+\[collect_mgr\] set_auto_collect_status\s+\d+(?:\s+stack traceback:)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Collect();

    [GeneratedRegex(@"^\[LOG\] -> cave/cave_(?:cli|helper)\.lua:\d+\s+\[scene_load\][\x20-\x7E]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Scene();

    [GeneratedRegex(@"^\[LOG\] -> module/lifeskill/cook/lifeskill_cook_mgr\.lua:\d+\s+is_block\s+-?[\d.]+\s+-?[\d.]+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex IsBlock();

    [GeneratedRegex(@"^(?:\[LOG\] -> \S+:\d+\s+)?\[jank_detector\] jank over\s*:\s*[\d.]+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Jank();
}
