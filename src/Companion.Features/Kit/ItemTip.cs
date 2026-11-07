using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;

namespace Companion.Features.Kit;

/// <summary>
/// Any item's tip (live 2026-10-15), shared by the crafting scans (the product and material icons of the Make tab) and
/// the Stock scan (the Stock tab's grid): a press on an item icon opens UI_ITEM_TIPS_PASS_TOUCH and the game logs the
/// exact item name and id ('item_tips_mgr ... print name, uuid, id'). A tip procedure is two steps: the icon click
/// (a screen anchor + offset, never a commit) expecting the tip, then Esc expecting it to close. Only a tip logged after
/// the click started (a later log position than the UI snapshot taken right before the run, and not the tip that
/// snapshot already held) is taken. A logged item is learned as the item behind the pressed icon's key
/// (<see cref="ICharacterKnowledge.LearnIconItem"/>). Journal fields are codes and item ids only; the name goes to the
/// knowledge store.
/// </summary>
internal static class ItemTip
{
    /// <summary>Step that clicks the icon.</summary>
    internal const string OpenStep = "tip.open";

    /// <summary>Step that closes the tip with Esc.</summary>
    internal const string CloseStep = "tip.close";

    /// <summary>The item tip UI.</summary>
    internal const string TipUi = "UI_ITEM_TIPS_PASS_TOUCH";

    /// <summary>Journal code: the name and id were taken from the log.</summary>
    internal const string Logged = "tip.logged";

    /// <summary>Journal code: the run succeeded but no item tip is in the log.</summary>
    internal const string NotLogged = "tip.notLogged";

    /// <summary>Journal code: the latest item tip was logged before the click (ignored).</summary>
    internal const string BeforeClick = "tip.beforeClick";

    /// <summary>Journal code: the run ended without success (the scan ends there).</summary>
    internal const string RunFailed = "tip.procedure";

    /// <summary>Journal code: no tip was asked for.</summary>
    internal const string Skipped = "tip.skipped";

    /// <summary>Journal code: a tip was logged, but its name is not a valid item key (nothing learned).</summary>
    internal const string BadName = "tip.badName";

    /// <summary>
    /// A tip procedure is a read-only navigation (no gate, no commit, nothing skipped) of exactly two steps: the icon click
    /// (<paramref name="anchorTemplate"/> + offset) expecting the tip, then Esc expecting the tip to close.
    /// </summary>
    internal static bool IsExpectedShape(ProcedureDefinition p, string anchorTemplate)
    {
        if (p.Kind != ProcedureKind.Navigation || p.Gate != AutomationSwitch.None || p.Steps.Count != 2
            || p.Steps.Any(s => s.Flags.HasFlag(StepFlags.Commit) || s.Flags.HasFlag(StepFlags.SkipIfSatisfied)))
        {
            return false;
        }

        var (open, close) = (p.Steps[0], p.Steps[1]);
        return open.Id == OpenStep && open.Action is ClickAction { Offset: not null } click && click.TemplateId == anchorTemplate && Expects(open, UiVerb.Show)
            && close.Id == CloseStep && close.Action is KeyAction { Key: GameKey.Escape } && Expects(close, UiVerb.HideTop);
    }

    /// <summary>Runs the tip procedure once and returns the tip logged after its click started, if any.</summary>
    internal static Outcome Read(IJobContext context, ProcedureDefinition data, CancellationToken ct)
    {
        var before = context.UiState;
        var run = context.Procedures.Run(data, context.Run, ct);
        if (run.Status != ProcedureStatus.Succeeded)
        {
            return new Outcome(run, null, RunFailed);
        }

        return context.UiState.LastItemTip switch
        {
            null => new Outcome(run, null, NotLogged),
            { } old when old == before.LastItemTip || !old.LoggedAfter(before.AsOf) => new Outcome(run, null, BeforeClick),
            { } tip => new Outcome(run, tip, Logged),
        };
    }

    /// <summary>The tip procedure with its icon click moved to <paramref name="offset"/> (anchor-relative).</summary>
    internal static ProcedureDefinition WithOffset(ProcedureDefinition data, PointI offset) => data with
    {
        Steps = [.. data.Steps.Select(s => s.Id == OpenStep && s.Action is ClickAction c ? s with { Action = c with { Offset = offset } } : s)],
    };

    /// <summary>
    /// Journal fields of an event with a tip: the tip code and the item id (codes and ids only: the name goes to the
    /// knowledge store, not to the journal). A null id when no tip was taken.
    /// </summary>
    internal static (string Key, object? Value)[] Fields(Outcome? tip) =>
    [
        ("tip", tip?.Code ?? Skipped),
        ("itemId", tip?.Tip?.ItemId),
    ];

    /// <summary>
    /// Learns the logged item as the item behind <paramref name="iconKey"/>; the learned icon item, or null when the tip
    /// has no usable id or name (the store validates the name as an item key).
    /// </summary>
    internal static IconItem? Learn(ICharacterKnowledge knowledge, string iconKey, ItemTipInfo tip, DateTimeOffset nowUtc)
    {
        var name = tip.ItemName?.Trim() ?? string.Empty;
        if (tip.ItemId <= 0 || ItemKey.FromDisplayName(name).Value.Length == 0)
        {
            return null;
        }

        try
        {
            var item = new IconItem(iconKey, tip.ItemId, name, nowUtc);
            knowledge.LearnIconItem(item);
            return item;
        }
        catch (ArgumentException)
        {
            return null; // not a valid item key (the store validates): the icon stays unnamed
        }
    }

    private static bool Expects(StepDefinition s, UiVerb verb) =>
        s.Expect.UiEvents?.Any(u => u.Verb == verb && u.UiName == TipUi) == true;

    /// <summary>One tip run.</summary>
    /// <param name="Run">The procedure result (the caller counts its inputs; anything but success ends its scan).</param>
    /// <param name="Tip">The tip logged after the click started, or null.</param>
    /// <param name="Code">Journal code: <see cref="Logged"/>, <see cref="NotLogged"/>, <see cref="BeforeClick"/> or <see cref="RunFailed"/>.</param>
    internal sealed record Outcome(ProcedureResult Run, ItemTipInfo? Tip, string Code)
    {
        /// <summary>The run ended with success.</summary>
        public bool Succeeded => Run.Status == ProcedureStatus.Succeeded;
    }
}
