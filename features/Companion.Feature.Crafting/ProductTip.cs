using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.Features.Kit;

namespace Companion.Feature.Crafting;

/// <summary>
/// The item tips of the selected recipe (live 2026-10-15), shared by ScanRecipeList and ScanFavorites, on the shared
/// <see cref="ItemTip"/> helper (any item tip: the game logs the exact name and id of a pressed item icon; OCR titles
/// are often garbage, "ams", "i pizza"). The data procedure "crafting.productTip" clicks an icon (anchor
/// craft.materials.header + offset; never a commit), expects the tip, and presses Esc expecting it to close.
/// <list type="bullet">
/// <item>The product (<see cref="Read"/>, after a recipe panel was read): the data procedure's own offset (the product
/// picture, the same point for the 3- and 4-material layouts); the logged name and id replace the OCR title
/// (<see cref="Apply"/>).</item>
/// <item>The materials (<see cref="ReadMaterials"/>, 2026-10-15): every material tile whose icon is not yet a known
/// icon item of the store is pressed ONCE, at the tile's centre as the tile-row reader measured it (anchor-relative,
/// inside the materials band), after the job's fixed pause; the logged item is learned as an icon item
/// (<see cref="ICharacterKnowledge.LearnIconItem"/>), so a known icon is never pressed again.</item>
/// </list>
/// Without a fresh tip the OCR title (or the icon key) stays and the card or slot journals the code. The journal gets
/// codes and item ids only.
/// </summary>
internal static class ProductTip
{
    /// <summary>Data procedure id.</summary>
    internal const string Procedure = "crafting.productTip";

    /// <summary>Step that clicks the icon.</summary>
    internal const string OpenStep = ItemTip.OpenStep;

    /// <summary>Step that closes the tip with Esc.</summary>
    internal const string CloseStep = ItemTip.CloseStep;

    /// <summary>The item tip UI.</summary>
    internal const string TipUi = ItemTip.TipUi;

    /// <summary>Anchor of the icon click: the "Materials Required" header of the recipe panel.</summary>
    internal const string AnchorTemplate = "craft.materials.header";

    /// <summary>Journal code: the name and id were taken from the log.</summary>
    internal const string Logged = ItemTip.Logged;

    /// <summary>Journal code: the run succeeded but no item tip is in the log.</summary>
    internal const string NotLogged = ItemTip.NotLogged;

    /// <summary>Journal code: the latest item tip was logged before the click (ignored).</summary>
    internal const string BeforeClick = ItemTip.BeforeClick;

    /// <summary>Journal code: the run ended without success (the scan ends there).</summary>
    internal const string RunFailed = ItemTip.RunFailed;

    /// <summary>Journal code: no tip was asked for (nothing selected, or the panel was not read).</summary>
    internal const string Skipped = ItemTip.Skipped;

    /// <summary>Journal code of a material: a tip was logged, but its name is not a valid item key (nothing learned).</summary>
    internal const string BadName = ItemTip.BadName;

    /// <summary>Journal event of one material tip press.</summary>
    internal const string MaterialEvent = "tip.material";

    /// <summary>
    /// The materials row of craft.pairs relative to the header anchor (data\readers.json, region "materials": client
    /// (940..1587, 674..763) with the header at (1270,641)). A material tip is pressed only inside it.
    /// </summary>
    internal static readonly RectI MaterialsBand = new(-330, 33, 317, 122);

    /// <summary>
    /// The data procedure must be a read-only navigation (no gate, no commit, nothing skipped) of exactly two steps: the
    /// icon click (anchor + offset) expecting the tip, then Esc expecting the tip to close.
    /// </summary>
    internal static bool IsExpectedShape(ProcedureDefinition p) => ItemTip.IsExpectedShape(p, AnchorTemplate);

    /// <summary>Runs the tip procedure once and returns the tip logged after its click started, if any.</summary>
    internal static ItemTip.Outcome Read(IJobContext context, ProcedureDefinition data, CancellationToken ct) => ItemTip.Read(context, data, ct);

    /// <summary>
    /// The read with the logged name as its product (the OCR title is replaced) and the logged id as its product item
    /// id; unchanged without a tip.
    /// </summary>
    internal static FavoriteRead Apply(FavoriteRead read, ItemTip.Outcome tip) =>
        tip.Tip is { } t && ItemKey.FromDisplayName(t.ItemName) is { Value.Length: > 0 } key
            ? read with { Product = key, ProductItemId = t.ItemId > 0 ? t.ItemId : null }
            : read;

    /// <summary>
    /// Presses, once each, the material tiles of <paramref name="read"/> whose icon is not a known icon item of the store,
    /// and learns the item each logged tip names. Every press follows <paramref name="pause"/> (the job's fixed pause)
    /// and is journaled (event <see cref="MaterialEvent"/>, codes and the item id only, plus <paramref name="where"/>).
    /// A tile without a measured centre, or outside the materials band, is never pressed, and an icon already pressed in
    /// this job (<paramref name="tried"/>, kept by the caller) is not pressed again even when its tip was not logged. The
    /// first run that does not succeed stops the presses (the caller ends its scan with it).
    /// </summary>
    internal static MaterialTips ReadMaterials(
        IJobContext context,
        ProcedureDefinition data,
        FavoriteRead read,
        ISet<string> tried,
        Action pause,
        string journalProcedure,
        (string Key, object? Value)[] where,
        CancellationToken ct)
    {
        var knowledge = context.Knowledge!;
        var runs = new List<ProcedureResult>();
        int learned = 0, known = 0, noTile = 0;
        foreach (var m in read.Materials)
        {
            if (m.IconKey is not { } key)
            {
                continue;
            }

            if (knowledge.GetIconItems().Any(i => string.Equals(i.IconKey, key, StringComparison.Ordinal)))
            {
                known++; // learned before (or earlier in this job): never pressed again
                continue;
            }

            if (m.TipOffset is not { } offset || !MaterialsBand.Contains(offset))
            {
                noTile++;
                continue;
            }

            if (!tried.Add(key))
            {
                continue; // pressed earlier in this job without a usable tip
            }

            pause();
            var tip = Read(context, ItemTip.WithOffset(data, offset), ct);
            runs.Add(tip.Run);
            if (!tip.Succeeded)
            {
                JournalMaterial(context, journalProcedure, where, m.Slot, RunFailed, null);
                return new MaterialTips(runs, learned, known, noTile, tip.Run);
            }

            var code = tip.Code;
            if (tip.Tip is { } t)
            {
                if (ItemTip.Learn(knowledge, key, t, context.Clock.UtcNow) is not null)
                {
                    learned++;
                }
                else
                {
                    code = BadName;
                }
            }

            JournalMaterial(context, journalProcedure, where, m.Slot, code, tip.Tip?.ItemId);
        }

        return new MaterialTips(runs, learned, known, noTile, null);
    }

    private static void JournalMaterial(IJobContext context, string procedure, (string Key, object? Value)[] where, int slot, string code, long? itemId)
    {
        var data = JobKit.Data(("event", MaterialEvent), ("material", (long)slot), ("code", code), ("itemId", itemId));
        foreach (var (k, v) in where)
        {
            data[k] = v;
        }

        JobKit.Journal(context, JournalKind.Stock, procedure, data);
    }

    /// <summary>What <see cref="ReadMaterials"/> did.</summary>
    /// <param name="Runs">Every tip run (the caller counts their inputs).</param>
    /// <param name="Learned">Icons whose item was learned.</param>
    /// <param name="Known">Icons already known (not pressed).</param>
    /// <param name="NoTile">Unknown icons not pressed: no measured tile centre, or outside the materials band.</param>
    /// <param name="Failed">The run that did not succeed (the presses stopped there), or null.</param>
    internal sealed record MaterialTips(IReadOnlyList<ProcedureResult> Runs, int Learned, int Known, int NoTile, ProcedureResult? Failed)
    {
        /// <summary>Presses made.</summary>
        public int Presses => Runs.Count;
    }
}
