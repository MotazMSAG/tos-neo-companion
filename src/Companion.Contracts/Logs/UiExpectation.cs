namespace Companion.Contracts.Logs;

/// <summary>
/// An exact expected log event: (verb, name) after our byte offset — never a substring match (LOG-11).
/// UiName null means 'nil' (only meaningful with <see cref="UiVerb.HideTop"/>).
/// </summary>
/// <param name="Verb">Expected verb.</param>
/// <param name="UiName">Expected UI name, or null for 'nil'.</param>
public sealed record UiExpectation(UiVerb Verb, string? UiName)
{
    /// <summary>True if the event matches exactly.</summary>
    public bool Matches(UiLogEvent e) =>
        e.Verb == Verb && string.Equals(e.UiName, UiName, StringComparison.Ordinal) && (UiName is not null || e.NumericId is null);

    /// <summary>'show_ui X' or 'open const ui X' (either counts as opened).</summary>
    public static IReadOnlyList<UiExpectation> Opened(string uiName) => [new(UiVerb.Show, uiName), new(UiVerb.OpenConst, uiName)];

    /// <summary>'[ui mgr]hide top ui X'.</summary>
    public static UiExpectation HiddenTop(string? uiName) => new(UiVerb.HideTop, uiName);
}
