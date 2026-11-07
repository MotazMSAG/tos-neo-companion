using Companion.Contracts;
using Companion.Contracts.Vision;

namespace Companion.Features.Kit;

/// <summary>A sub-tab of the Stock tab.</summary>
/// <param name="Name">Code ("ability", "homestead", "tailor", "hyperweather"); also the coverage suffix.</param>
/// <param name="StepId">Step of the data procedure that presses it ("sub." + name).</param>
/// <param name="ReaderId">Its grid reader (identity: the Stock tab AND this sub-tab selected).</param>
/// <param name="Center">Client centre of its label (where it is pressed).</param>
/// <param name="Volatile">Its goods are shared (the homestead may have a partner): counts are fresh only briefly.</param>
internal sealed record StockSubTab(string Name, string StepId, string ReaderId, PointI Center, bool Volatile);

/// <summary>A client area of the Stock tab that is never pressed.</summary>
/// <param name="Id">Code.</param>
/// <param name="Rect">Client rect (pressing anywhere within <see cref="StockGrid.ForbiddenMargin"/> of it is refused).</param>
internal sealed record ForbiddenArea(string Id, RectI Rect);

/// <summary>
/// Geometry of the Ability view's Stock tab (live 2026-10-15, full-scale captures tests\TestData\images\stock_*_full.png,
/// copies of proofs\out), shared by the Stock scan and its tests. Every press and drag hangs off the selected right-hand
/// "Stock" tab (template <c>stock.anchor</c>, centre (1755,577), fixed; the hanging "Stock" title swings): the engine
/// locates it right before each input and adds the step's offset.
/// <list type="bullet">
/// <item>Opening: the right-hand tab <c>stock.tab</c> at (1740,577) on the Make view (its press logs no UI event, only
/// "LostFocuse Label_Text"); the switch is verified by a read whose screen identity needs <c>stock.anchor</c>.</item>
/// <item>Sub-tabs at y 226: Ability 380, Homestead 610, Tailor 842, Hyperweather 1072 (each press logs "Label_Text"
/// only); the switch is verified by the sub-tab's own reader (it also needs the selected sub-tab's template).</item>
/// <item>The grid is drawn in <see cref="Viewport"/>; a press lands at least <see cref="ClickMargin"/> inside it, on a
/// tile's icon (<see cref="PressPoint"/>), never in a <see cref="ForbiddenAreas">forbidden area</see> (the Homestead
/// footer: storage, building materials, "Expand Inventory" and "Treebond Warehouse").</item>
/// <item>Scrolling: drags in the gap between the 3rd and 4th columns (x 940, as the live exploration), 240 px (a 300 px
/// drag moved the list 326-377 px); a shift over <see cref="SafeShift"/> may have skipped a row (journaled).</item>
/// </list>
/// Pure; no I/O.
/// </summary>
internal static class StockGrid
{
    /// <summary>The anchor of every Stock press and drag: the selected right-hand "Stock" tab.</summary>
    internal const string AnchorTemplate = "stock.anchor";

    /// <summary>The right-hand "Stock" tab as shown on the Make view (not selected): opens the Stock tab.</summary>
    internal const string StockTabTemplate = "stock.tab";

    /// <summary>The right-hand "Make" tab as shown on the Stock view (not selected): back to the Make tab.</summary>
    internal const string MakeTabTemplate = "craft.make.tab";

    /// <summary>The reader that tells whether the Stock tab is open (any sub-tab).</summary>
    internal const string ProbeReader = "inventory.stock.grid";

    /// <summary>The anchor's expected centre (templates.json).</summary>
    internal static readonly PointI AnchorAt = new(1755, 577);

    /// <summary>Client rect where the grid draws its tiles (the readers' region).</summary>
    internal static readonly RectI Viewport = new(300, 270, 1540, 812);

    /// <summary>The sub-tab strip (a sub-tab press lands inside it).</summary>
    internal static readonly RectI SubTabStrip = new(262, 196, 1188, 256);

    /// <summary>A press lands at least this far inside the viewport.</summary>
    internal const int ClickMargin = 20;

    /// <summary>A press lands at least this far from every forbidden area.</summary>
    internal const int ForbiddenMargin = 20;

    /// <summary>Tile height (outline to outline).</summary>
    internal const int TileHeight = 120;

    /// <summary>A larger shift between two reads may have skipped a whole row (viewport height - tile height - 10).</summary>
    internal static int SafeShift => Viewport.Height - TileHeight - 10;

    /// <summary>The Stock tab's sub-tabs, in scan order.</summary>
    internal static readonly IReadOnlyList<StockSubTab> SubTabs =
    [
        new("ability", "sub.ability", "inventory.stock.ability", new PointI(380, 226), false),
        new("homestead", "sub.homestead", "inventory.stock.homestead", new PointI(610, 226), true),
        new("tailor", "sub.tailor", "inventory.stock.tailor", new PointI(842, 226), false),
        new("hyperweather", "sub.hyperweather", "inventory.stock.hyperweather", new PointI(1072, 226), false),
    ];

    /// <summary>
    /// Never pressed (2026-10-15): the Homestead sub-tab's footer (storage used/limit, the three building materials),
    /// "Expand Inventory" (spends currency: FORBIDDEN) and "Treebond Warehouse".
    /// </summary>
    internal static readonly IReadOnlyList<ForbiddenArea> ForbiddenAreas =
    [
        new("footer", new RectI(296, 815, 1560, 945)),
        new("expand.inventory", new RectI(1350, 836, 1545, 905)),
        new("treebond.warehouse", new RectI(1555, 830, 1700, 985)),
    ];

    /// <summary>The offset from the anchor (found at <paramref name="anchor"/>, else expected) that lands on <paramref name="clientPoint"/>.</summary>
    internal static PointI OffsetOf(PointI clientPoint, PointI? anchor = null)
    {
        var a = anchor ?? AnchorAt;
        return new PointI(clientPoint.X - a.X, clientPoint.Y - a.Y);
    }

    /// <summary>Where a tile is pressed for its item tip: on its icon, above the count line.</summary>
    internal static PointI PressPoint(RectI tile) => new(tile.Center.X, tile.Top + 45);

    /// <summary>True inside (or within <see cref="ForbiddenMargin"/> of) a forbidden area.</summary>
    internal static bool Forbidden(PointI p) => ForbiddenAreas.Any(a => a.Rect.Inflate(ForbiddenMargin).Contains(p));

    /// <summary>True when a press at <paramref name="p"/> stays well inside the grid and away from every forbidden area.</summary>
    internal static bool Pressable(PointI p) => Viewport.Inflate(-ClickMargin).Contains(p) && !Forbidden(p);

    /// <summary>How far the grid moved between two captures (<see cref="ListScroll.Measure"/>, 6 bands across the grid).</summary>
    internal static ListShift MeasureShift(Frame before, Frame after) => ListScroll.Measure(before, after, Viewport, Viewport.Left, Viewport.Right, 6);
}
