using System.Globalization;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Perception;
using Companion.Features.Kit;

namespace Companion.Feature.Inventory;

/// <summary>
/// ScanStock {} (2026-10-15; read-only scan, navigation procedure "inventory.scanStock", no commit, no switch, any
/// character permission; store writes need a known character). Reads every item of the Ability view's Stock tab
/// (<see cref="StockGrid"/>):
/// <list type="number">
/// <item>open the Make view (core OpenAbility; one Esc only for the phase announcement), then the Stock tab (the
/// right-hand tab <c>stock.tab</c>) unless a read already shows it; the press logs no UI event, so the switch is verified
/// by a read whose screen identity needs the selected "Stock" tab (<c>stock.anchor</c>);</item>
/// <item>per sub-tab (Ability, Homestead, Tailor, Hyperweather): press it (anchor + offset), verify the switch by the
/// sub-tab's own reader (its identity also needs the selected sub-tab), drag the grid down until a drag no longer moves
/// it (the top);</item>
/// <item>per screen: read the grid (whole tiles only: icon key and count; a faded tile without a count is 0); a tile
/// whose icon is not CLEARLY a learned icon item (<see cref="StockIcons"/>) gets its item tip pressed, once per job
/// ("inventory.stockTip", <see cref="ItemTip"/>: the logged name and id are learned for that icon); the count is recorded
/// under the item (name key and id) or, when the game named nothing, under the icon key; one item seen at two places of
/// one sub-tab is a conflict: two icon matches ask the game about the newer tile, a tip beats an icon match, and a
/// conflict the game cannot settle records nothing;</item>
/// <item>unless the read shows the end of the list (nothing drawn below the last row), drag the grid up 240 px and
/// measure how far it moved (<see cref="StockGrid.MeasureShift"/>): tiles already read are recognised by their position
/// in the list; no movement = the end of the list; a move over <see cref="StockGrid.SafeShift"/> may have skipped a row
/// (journaled, counted as a gap).</item>
/// </list>
/// Each sub-tab's counts are stored when it ends (stock observations from the Stock tab, coverage
/// "screen:stock/&lt;sub-tab&gt;"; the Homestead sub-tab's goods are shared with a homestead partner: fresh for
/// <see cref="HomesteadFresh"/> only). Never pressed: anything outside the grid and the Homestead footer
/// (<see cref="StockGrid.ForbiddenAreas"/>: "Expand Inventory", "Treebond Warehouse"). The job goes back to the Make tab at
/// the end. A fixed pause separates every input (human pace, on top of the engine's pacing). Journal: codes, item ids
/// and counts only.
/// </summary>
internal sealed class ScanStockJobHandler : IJobHandler
{
    internal const string JobName = "ScanStock";
    internal const string Procedure = "inventory.scanStock";
    internal const string TipProcedure = "inventory.stockTip";
    internal const string OpenStep = "open";
    internal const string AnnounceStep = "announce.close";
    internal const string StockOpenStep = "stock.open";
    internal const string StockSettleStep = "stock.settle";
    internal const string SubSettleStep = "sub.settle";
    internal const string TopStep = "top";
    internal const string ScrollStep = "scroll";
    internal const string ScrollSettleStep = "scroll.settle";
    internal const string BackStep = "back";
    internal const string BackSettleStep = "back.settle";
    internal const string MakeView = "UI_LIFESKILL_MAKE_VIEW";

    /// <summary>The readers' grid region.</summary>
    internal const string GridRegion = "grid";

    /// <summary>Most drags towards the top before giving up (per sub-tab).</summary>
    internal const int MaxTopDrags = 12;

    /// <summary>Most screens read per sub-tab (safety cap).</summary>
    internal const int MaxRounds = 30;

    /// <summary>Most item tips pressed in one job (safety cap; a first run presses one per item).</summary>
    internal const int MaxTips = 200;

    /// <summary>Drags cut short (the user moved the mouse) that are tried again when the grid did not move.</summary>
    internal const int MaxPartialRetries = 2;

    /// <summary>Two tiles are the same list position when their list rows (and columns) differ by at most this.</summary>
    internal const int SamePositionPx = 20;

    /// <summary>A drag moves the grid up to about 1.3 times its length (300 px moved it 326-377 px).</summary>
    internal const double DragOvershoot = 1.3;

    /// <summary>Fixed pause before every input after the first (human pace, no randomness).</summary>
    internal static readonly TimeSpan InputPause = TimeSpan.FromSeconds(2);

    /// <summary>How long a Homestead count stays fresh (shared goods: a homestead partner may collect or use them).</summary>
    internal static readonly TimeSpan HomesteadFresh = TimeSpan.FromMinutes(10);

    /// <summary>Observation coverage (stock provenance) of a sub-tab: "screen:stock/&lt;name&gt;".</summary>
    internal static string CoverageOf(StockSubTab sub) => "screen:stock/" + sub.Name;

    private enum RunKind
    {
        Open,
        StockTab,
        SubTab,
        Top,
        Scroll,
        Back,
    }

    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        var refusal = JobKit.RequireKnowledge(context) ?? JobKit.CheckProcedures(context, [Procedure, TipProcedure]);
        if (refusal is not null)
        {
            return refusal;
        }

        var data = context.ProcedureCatalog.Get(Procedure)!;
        if (!IsExpectedShape(data))
        {
            return JobKit.Failed("procedure.invalid:" + Procedure);
        }

        var tip = context.ProcedureCatalog.Get(TipProcedure)!;
        if (!IsExpectedTipShape(tip))
        {
            return JobKit.Failed("procedure.invalid:" + TipProcedure);
        }

        var ui = context.UiState;
        if (ui.Craft.InProgress)
        {
            return JobKit.Refused("craft.inProgress");
        }

        if (ui.Busy is BusyKind.Revive or BusyKind.Match)
        {
            return JobKit.Refused("busy:" + ui.Busy);
        }

        return new Scan(context, data, tip, ct).Run();
    }

    /// <summary>
    /// The data procedure must be a read-only navigation (no commit step, no gate): the open step, the announcement Esc,
    /// the Stock tab click (its own template), a click per sub-tab (the anchor + an offset landing on that sub-tab), the two
    /// drags (top: down; scroll: up, short enough that no row can be skipped; both ends inside the grid and outside every
    /// forbidden area), the click back to the Make tab and the settle waits.
    /// </summary>
    internal static bool IsExpectedShape(ProcedureDefinition p)
    {
        if (p.Kind != ProcedureKind.Navigation || p.Gate != AutomationSwitch.None
            || p.Steps.Any(s => s.Flags.HasFlag(StepFlags.Commit) || s.Action is AwaitCraftAction))
        {
            return false;
        }

        StepDefinition? Step(string id) => p.Steps.FirstOrDefault(s => s.Id == id);
        if (Step(OpenStep) is not { Action: CallProcedureAction } open || open.Expect.UiEvents?.Any(u => u.UiName == MakeView) != true || Step(AnnounceStep) is null)
        {
            return false;
        }

        if (Step(StockOpenStep)?.Action is not ClickAction { TemplateId: StockGrid.StockTabTemplate, Offset: null }
            || Step(BackStep)?.Action is not ClickAction { TemplateId: StockGrid.MakeTabTemplate, Offset: null })
        {
            return false;
        }

        foreach (var sub in StockGrid.SubTabs)
        {
            if (Step(sub.StepId)?.Action is not ClickAction { TemplateId: StockGrid.AnchorTemplate, Offset: { } o })
            {
                return false;
            }

            var at = StockGrid.AnchorAt.Offset(o.X, o.Y);
            if (!StockGrid.SubTabStrip.Contains(at) || at.ChebyshevDistanceTo(sub.Center) > 30 || StockGrid.Forbidden(at))
            {
                return false;
            }
        }

        if (Step(TopStep)?.Action is not DragAction top || !DragInside(top) || top.Delta.Y <= 0
            || Step(ScrollStep)?.Action is not DragAction scroll || !DragInside(scroll) || scroll.Delta.Y >= 0
            || -scroll.Delta.Y * DragOvershoot > StockGrid.SafeShift)
        {
            return false;
        }

        return new[] { StockSettleStep, SubSettleStep, ScrollSettleStep, BackSettleStep }.All(id => Step(id)?.Action is WaitAction);
    }

    /// <summary>The tip procedure: the shared item-tip shape on the Stock anchor, its own press point inside the grid.</summary>
    internal static bool IsExpectedTipShape(ProcedureDefinition p) =>
        ItemTip.IsExpectedShape(p, StockGrid.AnchorTemplate)
        && p.Steps[0].Action is ClickAction { Offset: { } o }
        && StockGrid.Pressable(StockGrid.AnchorAt.Offset(o.X, o.Y));

    private static bool DragInside(DragAction d)
    {
        if (d.TemplateId != StockGrid.AnchorTemplate || d.Delta.X != 0)
        {
            return false;
        }

        var start = StockGrid.AnchorAt.Offset(d.Offset.X, d.Offset.Y);
        return StockGrid.Pressable(start) && StockGrid.Pressable(start.Offset(d.Delta.X, d.Delta.Y));
    }

    /// <summary>
    /// One run of the data procedure: the open step (skipped while the Make view is on top, except for the first run),
    /// the announcement Esc, then the steps of <paramref name="kind"/> (a sub-tab run presses <paramref name="sub"/>).
    /// </summary>
    private static ProcedureDefinition Build(ProcedureDefinition data, RunKind kind, bool first, StockSubTab? sub = null)
    {
        string[] keep = kind switch
        {
            RunKind.Open => [],
            RunKind.StockTab => [StockOpenStep, StockSettleStep],
            RunKind.SubTab => [sub!.StepId, SubSettleStep],
            RunKind.Top => [TopStep, ScrollSettleStep],
            RunKind.Scroll => [ScrollStep, ScrollSettleStep],
            _ => [BackStep, BackSettleStep],
        };
        var steps = new List<StepDefinition>();
        foreach (var s in data.Steps)
        {
            if (s.Id == OpenStep)
            {
                steps.Add(first ? s : s with { Flags = s.Flags | StepFlags.SkipIfSatisfied });
            }
            else if (s.Id == AnnounceStep || keep.Contains(s.Id))
            {
                steps.Add(s);
            }
        }

        return data with { Steps = steps };
    }

    /// <summary>The state of one scan.</summary>
    private sealed class Scan(IJobContext context, ProcedureDefinition data, ProcedureDefinition tipData, CancellationToken ct)
    {
        private readonly ICharacterKnowledge _knowledge = context.Knowledge!;
        private readonly HashSet<string> _pressed = new(StringComparer.Ordinal);
        private readonly List<SubTally> _subTabs = [];
        private bool _anyInput;
        private int _inputs;
        private int _clicks;
        private int _esc;
        private int _drags;
        private int _tips;
        private int _tipsMissing;
        private int _gaps;
        private bool _stockOpened;
        private bool _backConfirmed;
        // "allSubTabs" is set when every sub-tab was read; any other exit (a halt, a failed read) is a stop
        private string _end = "stopped";

        public JobResult Run()
        {
            // 1. the Make view, then the Stock tab
            var open = RunProcedure(Build(data, RunKind.Open, first: true), "open.", reopenCheck: false);
            if (open.Problem is { } p0)
            {
                return End(p0);
            }

            if (OpenStock() is { } p1)
            {
                return End(p1);
            }

            // 2. every sub-tab
            foreach (var sub in StockGrid.SubTabs)
            {
                if (ScanSubTab(sub) is { } problem)
                {
                    return End(problem);
                }
            }

            _end = "allSubTabs";

            // 3. back to the Make tab (other jobs expect it); verified by the Stock tab being gone
            Pause();
            var back = RunProcedure(Build(data, RunKind.Back, first: false), "back.", reopenCheck: true);
            if (back.Problem is { } p2)
            {
                return End(p2);
            }

            _backConfirmed = context.Screen.Read(StockGrid.ProbeReader, ct).Status == ReadStatus.ScreenMismatch;
            if (!_backConfirmed)
            {
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "stock.back"), ("code", "notConfirmed")));
            }

            return _subTabs.Sum(t => t.Items) == 0
                ? End(JobKit.Failed("stock.nothingRead"))
                : End(JobKit.Result(JobState.Succeeded, "stock.scanned"));
        }

        /// <summary>Opens the Stock tab unless a read already shows it; null, or the result that ends the scan.</summary>
        private JobResult? OpenStock()
        {
            var probe = context.Screen.Read(StockGrid.ProbeReader, ct);
            if (probe.Status == ReadStatus.NeedsSurvey)
            {
                return JobKit.NeedsSurvey([probe.ReaderId]);
            }

            if (JobKit.Seen(probe))
            {
                // a failed capture is not "already open" (incident 22)
                JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "stock.open"), ("code", "alreadyOpen")));
                return null;
            }

            Pause();
            var run = RunProcedure(Build(data, RunKind.StockTab, first: false), "stock.", reopenCheck: true);
            if (run.Problem is { } problem)
            {
                return problem;
            }

            // the press logs no UI event: the selected "Stock" tab in a fresh read is the proof
            var verified = ReadVerified(StockGrid.ProbeReader);
            _stockOpened = JobKit.Seen(verified);
            JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "stock.open"), ("code", _stockOpened ? "opened" : "notOpened")));
            return _stockOpened ? null : JobKit.Failed("stock.notOpened");
        }

        /// <summary>Reads one sub-tab from its top to its end; null, or the result that ends the scan.</summary>
        private JobResult? ScanSubTab(StockSubTab sub)
        {
            Pause();
            var run = RunProcedure(Build(data, RunKind.SubTab, first: false, sub), "sub.", reopenCheck: true);
            if (run.Problem is { } problem)
            {
                return problem;
            }

            var tally = new SubTally(sub);
            _subTabs.Add(tally);

            // the press logs only "Label_Text": the sub-tab's own reader (its identity needs the selected sub-tab) proves it
            var verify = ReadVerified(sub.ReaderId);
            if (verify.Status == ReadStatus.NeedsSurvey)
            {
                return JobKit.NeedsSurvey([verify.ReaderId]);
            }

            if (verify.Status == ReadStatus.ScreenMismatch)
            {
                tally.End = "notSwitched";
                JournalSubTab(tally);
                return JobKit.Failed("subtab.notSwitched:" + sub.Name);
            }

            // to the top: drag down until the grid no longer moves
            var atTop = false;
            for (var i = 1; i <= MaxTopDrags && !atTop; i++)
            {
                var (dragProblem, shift, partial) = Drag(RunKind.Top);
                if (dragProblem is not null)
                {
                    return dragProblem;
                }

                JournalDrag(sub, "top", i, shift, partial);
                atTop = shift.Unchanged && !partial;
            }

            if (!atTop)
            {
                tally.End = "topNotReached";
                JournalSubTab(tally);
                return JobKit.Failed("list.topNotReached:" + sub.Name);
            }

            var stillRetries = 0;
            while (true)
            {
                if (++tally.Rounds > MaxRounds)
                {
                    tally.End = "cap";
                    break;
                }

                var read = context.Screen.Read(sub.ReaderId, ct);
                if (read.Status == ReadStatus.NeedsSurvey)
                {
                    Flush(tally);
                    return JobKit.NeedsSurvey([read.ReaderId]);
                }

                if (read.Status == ReadStatus.ScreenMismatch)
                {
                    // the sub-tab or the view changed under the scan (the user, a popup): what was read is kept
                    tally.End = "lost";
                    Flush(tally);
                    JournalSubTab(tally);
                    return JobKit.Failed("subtab.lost:" + sub.Name);
                }

                if (ReadScreen(tally, read) is { } tipProblem)
                {
                    tally.End = "tip";
                    Flush(tally);
                    JournalSubTab(tally);
                    return tipProblem;
                }

                if (read.Values.FirstOrDefault(v => v.RegionId == GridRegion) is { Status: ReadStatus.Ok, DetailCode: { } detail } && detail.EndsWith("-end", StringComparison.Ordinal))
                {
                    // nothing is drawn below the last row: the end of the list is on screen (no drag over a blank area)
                    JobKit.Journal(context, JournalKind.Stock, Procedure, JobKit.Data(("event", "stock.listEnd"), ("subTab", sub.Name), ("round", (long)tally.Rounds), ("code", "endVisible")));
                    tally.End = "listEnd";
                    break;
                }

                var (scrollProblem, moved, cut) = Drag(RunKind.Scroll);
                if (scrollProblem is not null)
                {
                    Flush(tally);
                    JournalSubTab(tally);
                    return scrollProblem;
                }

                JournalDrag(sub, "scroll", tally.Rounds, moved, cut);
                if (moved.Unchanged)
                {
                    if (cut && stillRetries < MaxPartialRetries)
                    {
                        stillRetries++;
                        continue; // the drag was cut short: read and try again
                    }

                    if (cut)
                    {
                        Flush(tally);
                        JournalSubTab(tally);
                        return JobKit.Failed("list.dragIncomplete:" + sub.Name);
                    }

                    tally.End = "listEnd";
                    break;
                }

                stillRetries = 0;
                if (moved.Known)
                {
                    if (-moved.Dy > StockGrid.SafeShift)
                    {
                        _gaps++;
                        JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "stock.gap"), ("subTab", sub.Name), ("shiftPx", (long)moved.Dy)));
                    }

                    tally.Offset += moved.Dy;
                }
                else
                {
                    // the movement could not be measured: forget the positions (a re-read tile is only recorded again)
                    tally.Done.Clear();
                    tally.ItemsAt.Clear();
                    tally.IconsAt.Clear();
                    tally.Offset = 0;
                    JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "stock.positionsLost"), ("subTab", sub.Name), ("round", (long)tally.Rounds)));
                }
            }

            Flush(tally);
            JournalSubTab(tally);
            return null;
        }

        /// <summary>The new tiles of one read: identity (map or tip), then the count. Null, or a tip run's failure.</summary>
        private JobResult? ReadScreen(SubTally tally, ScreenReadResult read)
        {
            var grid = read.Values.FirstOrDefault(v => v.RegionId == GridRegion);
            if (grid is not { Status: ReadStatus.Ok, Value: { } n })
            {
                tally.Irregular++;
                JobKit.Journal(context, JournalKind.Stock, Procedure, JobKit.Data(("event", "stock.grid"), ("subTab", tally.Sub.Name), ("round", (long)tally.Rounds), ("code", Codes.Safe(grid?.DetailCode))));
                return null;
            }

            PointI? anchor = read.Anchors is { } anchors && anchors.TryGetValue(StockGrid.AnchorTemplate, out var a) ? a : null;
            for (var k = 1; k <= n; k++)
            {
                var suffix = k.ToString(CultureInfo.InvariantCulture);
                var icon = read.Values.FirstOrDefault(v => v.RegionId == "icon." + suffix);
                var count = read.Values.FirstOrDefault(v => v.RegionId == "count." + suffix);
                if (icon is not { Status: ReadStatus.Ok, Text: { } key, Rect: { } tile } || !IconKeys.IsIconKey(key))
                {
                    tally.Unreadable++;
                    continue;
                }

                var position = (Left: tile.Left, ListY: tile.Top - tally.Offset);
                if (tally.Done.Any(d => Math.Abs(d.Left - position.Left) <= SamePositionPx && Math.Abs(d.ListY - position.ListY) <= SamePositionPx))
                {
                    continue; // read before the last drag: it only moved
                }

                tally.Done.Add(position);
                if (ReadTile(tally, read, key, tile, count, position, anchor) is { } problem)
                {
                    return problem;
                }
            }

            return null;
        }

        /// <summary>
        /// One new tile: its item (a clear icon match, else the game's item tip), then its count. One item at two places of
        /// one sub-tab is a conflict: two icon matches ask the game about the newer tile; a tip beats an icon match; a
        /// conflict the game cannot settle records nothing. Null, or a tip run's failure.
        /// </summary>
        private JobResult? ReadTile(SubTally tally, ScreenReadResult read, string key, RectI tile, ReadValue? count, (int Left, int ListY) position, PointI? anchor)
        {
            var learned = _knowledge.GetIconItems();
            var place = PlaceOf(tally.Sub, position);
            var match = StockIcons.Match(key, learned);
            if (match.Item is null)
            {
                // a drifted picture at the place its item was learned: no press (StockIcons.MatchAt)
                var placed = StockIcons.MatchAt(key, p => SamePlace(p, tally.Sub, position), learned);
                match = placed.Item is null ? match : placed;
            }

            var item = match.Item;
            var via = item is null ? "none" : "map";
            var code = match.Code;
            ItemTip.Outcome? tip = null;
            if (item is null)
            {
                var asked = Ask(tally, key, tile, position, anchor);
                if (asked.Problem is not null)
                {
                    return asked.Problem;
                }

                (item, via, code, tip) = (asked.Item, asked.Item is null ? "none" : "tip", asked.Code, asked.Tip);
            }

            var counted = false;
            if (item is not null && Conflict(tally, item, position) is { } first && via == "map" && first.Via == "map")
            {
                // two icons matched to one item: the newer one may be a look-alike that was never learned
                JournalConflict(tally, item.ItemId, via, first.Via);
                counted = true;
                var asked = Ask(tally, key, tile, position, anchor);
                if (asked.Problem is not null)
                {
                    return asked.Problem;
                }

                tip = asked.Tip ?? tip;
                if (asked.Item is null)
                {
                    tally.Pending.Remove(item.ItemId);
                    JournalTile(tally, tile, position, "conflict", null, false, tip);
                    return null;
                }

                (item, via, code) = (asked.Item, "tip", asked.Code);
            }

            if (item is not null && Conflict(tally, item, position) is { } other)
            {
                if (!counted)
                {
                    JournalConflict(tally, item.ItemId, via, other.Via);
                }

                if (via == "tip" && other.Via == "map")
                {
                    tally.Pending.Remove(item.ItemId); // the game's answer beats an icon match
                }
                else
                {
                    JournalTile(tally, tile, position, "conflict", null, false, tip);
                    return null;
                }
            }

            if (item is not null)
            {
                tally.ItemsAt[item.ItemId] = (position, via);
                tally.ViaOf[item.ItemId] = via;
                RememberPlace(key, item, place);
            }
            else if (tally.IconsAt.TryGetValue(key, out var iconAt) && (iconAt != position || tally.IconConflicts.Contains(key)))
            {
                // one unnamed icon at two places: its counts cannot be told apart
                tally.Conflicts++;
                tally.IconConflicts.Add(key);
                tally.ByIcon.Remove(key);
                JournalTile(tally, tile, position, "conflict", null, false, tip);
                return null;
            }

            if (count is not { Status: ReadStatus.Ok, Value: { } value } || value < 0 || read.LogAt is not { } at)
            {
                tally.Unreadable++;
                JournalTile(tally, tile, position, code, null, false, tip, item?.ItemId);
                return null;
            }

            var faded = count.DetailCode == "faded";
            var observation = new StockObservation(
                item?.Item ?? new ItemKey(key),
                value,
                null,
                ObservationKind.StockTab,
                read.CapturedUtc,
                at,
                count.Confidence,
                CoverageOf(tally.Sub),
                item?.ItemId,
                tally.Sub.Volatile ? read.CapturedUtc + HomesteadFresh : null);
            if (item is not null)
            {
                tally.Pending[item.ItemId] = observation;
            }
            else
            {
                tally.IconsAt[key] = position;
                tally.ByIcon[key] = observation;
            }

            JournalTile(tally, tile, position, code, value, faded, tip, item?.ItemId);
            return null;
        }

        /// <summary>Stores this tile's picture with its item and place, so the next scan knows it there without a press.</summary>
        private void RememberPlace(string key, IconItem item, string place)
        {
            if (_knowledge.GetIconItems().Any(i => i.IconKey == key && i.ItemId == item.ItemId && i.Place == place))
            {
                return;
            }

            try
            {
                _knowledge.LearnIconItem(new IconItem(key, item.ItemId, item.Name, context.Clock.UtcNow) { Place = place });
            }
            catch (ArgumentException)
            {
                // not storable: the next scan asks again
            }
        }

        /// <summary>"&lt;sub-tab&gt;@&lt;left&gt;,&lt;listY&gt;": a tile's place in its sub-tab's list.</summary>
        private static string PlaceOf(StockSubTab sub, (int Left, int ListY) position) =>
            string.Create(CultureInfo.InvariantCulture, $"{sub.Name}@{position.Left},{position.ListY}");

        /// <summary>True when <paramref name="place"/> is this sub-tab's place within <see cref="SamePositionPx"/>.</summary>
        private static bool SamePlace(string? place, StockSubTab sub, (int Left, int ListY) position)
        {
            if (place is null || !place.StartsWith(sub.Name + "@", StringComparison.Ordinal))
            {
                return false;
            }

            var parts = place[(sub.Name.Length + 1)..].Split(',');
            return parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var left)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var listY)
                && Math.Abs(left - position.Left) <= SamePositionPx
                && Math.Abs(listY - position.ListY) <= SamePositionPx;
        }

        /// <summary>The place and identification of the item at another place of this sub-tab, or null.</summary>
        private static ((int Left, int ListY) Position, string Via)? Conflict(SubTally tally, IconItem item, (int Left, int ListY) position) =>
            tally.ItemsAt.TryGetValue(item.ItemId, out var other) && other.Position != position ? other : null;

        /// <summary>
        /// Asks the game which item a tile shows: its item tip, pressed once per icon and job, only inside the grid and
        /// away from every forbidden area. The learned item (null when nothing usable was logged or no press was made),
        /// the tile's code, the tip, or the failed run's result.
        /// </summary>
        private (JobResult? Problem, IconItem? Item, string Code, ItemTip.Outcome? Tip) Ask(SubTally tally, string key, RectI tile, (int Left, int ListY) position, PointI? anchor)
        {
            var point = StockGrid.PressPoint(tile);
            if (!StockGrid.Pressable(point))
            {
                return (null, null, "notPressable", null);
            }

            if (_tips >= MaxTips)
            {
                return (null, null, "tipCap", null);
            }

            if (!_pressed.Add(key))
            {
                return (null, null, "pressedBefore", null);
            }

            Pause();
            var tip = ItemTip.Read(context, ItemTip.WithOffset(tipData, StockGrid.OffsetOf(point, anchor)), ct);
            Count(tip.Run);
            _tips++;
            tally.Tips++;
            if (!tip.Succeeded)
            {
                JournalTile(tally, tile, position, ItemTip.RunFailed, null, false, tip);
                return (JobKit.FromProcedure(tip.Run, "tip."), null, ItemTip.RunFailed, tip);
            }

            if (tip.Tip is not { } logged)
            {
                _tipsMissing++;
                return (null, null, tip.Code, tip);
            }

            var learned = ItemTip.Learn(_knowledge, key, logged, context.Clock.UtcNow);
            if (learned is null)
            {
                return (null, null, ItemTip.BadName, tip);
            }

            tally.Named++;
            tally.Learned = true;
            return (null, learned, "learned", tip);
        }

        private void JournalConflict(SubTally tally, long itemId, string via, string otherVia)
        {
            tally.Conflicts++;
            JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "stock.conflict"), ("subTab", tally.Sub.Name), ("itemId", itemId), ("via", via), ("otherVia", otherVia)));
        }

        /// <summary>Stores a sub-tab's counts (one atomic save).</summary>
        private void Flush(SubTally tally)
        {
            var observations = tally.Pending.Values.Concat(tally.ByIcon.Values).ToList();
            tally.Items = observations.Count;
            tally.Known = tally.Pending.Keys.Count(id => tally.ViaOf.GetValueOrDefault(id) == "map");
            tally.Zeros = observations.Count(o => o.Count == 0);
            if (observations.Count > 0)
            {
                _knowledge.ApplyObservations(observations);
            }

            if (observations.Count > 0 || tally.Learned)
            {
                _knowledge.Save();
            }
        }

        /// <summary>A read of <paramref name="readerId"/>, read once more after a pause when the screen did not match yet.</summary>
        private ScreenReadResult ReadVerified(string readerId)
        {
            var read = context.Screen.Read(readerId, ct);
            if (JobKit.Seen(read) || read.Status == ReadStatus.NeedsSurvey)
            {
                return read;
            }

            Pause(); // not matched yet, or the capture failed (incident 22): one more read
            return context.Screen.Read(readerId, ct);
        }

        /// <summary>One drag run between two captures of the grid; the shift and whether the drag was cut short.</summary>
        private (JobResult? Problem, ListShift Shift, bool Partial) Drag(RunKind kind)
        {
            Pause();
            var before = context.Screen.Capture(ct);
            if (!before.Ok)
            {
                return (JobKit.Failed("capture." + before.Problem), default, false);
            }

            var step = kind == RunKind.Top ? TopStep : ScrollStep;
            var run = RunProcedure(Build(data, kind, first: false), step + ".", reopenCheck: true);
            if (run.Problem is { } problem)
            {
                return (problem, default, false);
            }

            _drags++;
            var after = context.Screen.Capture(ct);
            if (!after.Ok)
            {
                return (JobKit.Failed("capture." + after.Problem), default, false);
            }

            var partial = run.Result!.Attempts.Any(a => a.StepId == step && a.DetailCode == "drag.partial");
            return (null, StockGrid.MeasureShift(before.Frame!, after.Frame!), partial);
        }

        /// <summary>Runs one procedure; a run that is not a success, or that had to open the view again, ends the scan.</summary>
        private (ProcedureResult? Result, JobResult? Problem) RunProcedure(ProcedureDefinition p, string codePrefix, bool reopenCheck)
        {
            var r = context.Procedures.Run(p, context.Run, ct);
            Count(r);
            if (r.Status != ProcedureStatus.Succeeded)
            {
                return (r, JobKit.FromProcedure(r, codePrefix));
            }

            if (reopenCheck && r.Attempts.Any(a => a.StepId == OpenStep && a.DetailCode == "call.done"))
            {
                // the Ability view had been closed (by the user or a popup): it opens on another tab, positions are lost
                return (r, JobKit.Failed("stock.viewReopened"));
            }

            return (r, null);
        }

        private void Count(ProcedureResult r)
        {
            _anyInput = true;
            _inputs += r.InputBatches;
            _clicks += r.Clicks;
            _esc += r.EscPresses;
        }

        private void Pause()
        {
            if (!_anyInput)
            {
                return;
            }

            try
            {
                ct.ThrowIfCancellationRequested();
                context.Clock.Sleep(InputPause, ct);
            }
            catch (OperationCanceledException)
            {
                JobKit.Journal(context, JournalKind.Stock, Procedure, With(Data(), ("event", "stock.end"), ("code", "cancelled")));
                throw;
            }
        }

        private void JournalTile(SubTally tally, RectI tile, (int Left, int ListY) position, string code, long? count, bool faded, ItemTip.Outcome? tip, long? itemId = null)
        {
            var data = With(
                JobKit.Data(
                    ("event", "stock.tile"),
                    ("subTab", tally.Sub.Name),
                    ("round", (long)tally.Rounds),
                    ("column", (long)Math.Clamp(((tile.Left - 356 + 105) / 211) + 1, 1, 6)),
                    ("listY", (long)position.ListY),
                    ("code", code),
                    ("count", count),
                    ("faded", faded)),
                ItemTip.Fields(tip));
            data["itemId"] = tip?.Tip?.ItemId ?? itemId;
            JobKit.Journal(context, JournalKind.Stock, Procedure, data);
        }

        private void JournalDrag(StockSubTab sub, string kind, int n, ListShift shift, bool partial) =>
            JobKit.Journal(context, JournalKind.Stock, Procedure, JobKit.Data(
                ("event", "stock.drag"),
                ("subTab", sub.Name),
                ("kind", kind),
                ("n", (long)n),
                ("shiftKnown", shift.Known),
                ("shiftPx", (long)shift.Dy),
                ("unchanged", shift.Unchanged),
                ("partial", partial),
                ("error", shift.Error),
                ("next", shift.Next)));

        private void JournalSubTab(SubTally t) =>
            JobKit.Journal(context, JournalKind.Stock, Procedure, JobKit.Data(
                ("event", "stock.subTab"),
                ("subTab", t.Sub.Name),
                ("code", t.End),
                ("items", (long)t.Items),
                ("known", (long)t.Known),
                ("named", (long)t.Named),
                ("unreadable", (long)t.Unreadable),
                ("zeros", (long)t.Zeros),
                ("tips", (long)t.Tips),
                ("conflicts", (long)t.Conflicts),
                ("rounds", (long)t.Rounds)));

        private JobResult End(JobResult result)
        {
            // the host keeps 32 result fields: this scan's own fields, then the halt code of a halted run
            var data = Data();
            if (result.Data is not null && result.Data.TryGetValue("halt", out var halt))
            {
                data.TryAdd("halt", halt);
            }

            JobKit.Journal(context, JournalKind.Stock, Procedure, With(Data(), ("event", "stock.end"), ("code", result.Code), ("stockPressed", _stockOpened)));
            return result with { Data = data };
        }

        /// <summary>
        /// The result fields (at most 31): per sub-tab its items, the ones known from their icon, the ones newly named by
        /// their item tip, the unreadable ones and how the sub-tab ended; then the scan's totals.
        /// </summary>
        private Dictionary<string, object?> Data()
        {
            var data = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var t in _subTabs)
            {
                data[t.Sub.Name + ".items"] = (long)t.Items;
                data[t.Sub.Name + ".known"] = (long)t.Known;
                data[t.Sub.Name + ".named"] = (long)t.Named;
                data[t.Sub.Name + ".unreadable"] = (long)t.Unreadable;
                data[t.Sub.Name + ".end"] = t.End;
            }

            data["zeros"] = (long)_subTabs.Sum(t => t.Zeros);
            data["conflicts"] = (long)_subTabs.Sum(t => t.Conflicts);
            data["tips"] = (long)_tips;
            data["tipsMissing"] = (long)_tipsMissing;
            data["gaps"] = (long)_gaps;
            data["drags"] = (long)_drags;
            data["end"] = _end;
            data["backToMake"] = _backConfirmed;
            data["inputs"] = (long)_inputs;
            data["clicks"] = (long)_clicks;
            data["esc"] = (long)_esc;
            return data;
        }

        private static Dictionary<string, object?> With(Dictionary<string, object?> data, params (string Key, object? Value)[] extra)
        {
            foreach (var (k, v) in extra)
            {
                data[k] = v;
            }

            return data;
        }
    }

    /// <summary>One sub-tab's scan: positions read, counts waiting to be stored, totals (codes and counts only).</summary>
    private sealed class SubTally(StockSubTab sub)
    {
        public StockSubTab Sub { get; } = sub;

        /// <summary>Tiles read, by column (left edge) and list row (top edge minus <see cref="Offset"/>).</summary>
        public List<(int Left, int ListY)> Done { get; } = [];

        /// <summary>Sum of the measured shifts since the top (negative after dragging up).</summary>
        public int Offset { get; set; }

        /// <summary>Counts by item id, stored when the sub-tab ends.</summary>
        public Dictionary<long, StockObservation> Pending { get; } = [];

        /// <summary>Counts of tiles the game did not name, stored under their icon key.</summary>
        public Dictionary<string, StockObservation> ByIcon { get; } = new(StringComparer.Ordinal);

        /// <summary>Where each unnamed icon was seen.</summary>
        public Dictionary<string, (int Left, int ListY)> IconsAt { get; } = new(StringComparer.Ordinal);

        /// <summary>Unnamed icons seen at two places (never recorded).</summary>
        public HashSet<string> IconConflicts { get; } = new(StringComparer.Ordinal);

        /// <summary>Where each item was seen, and how it was identified ("map" or "tip").</summary>
        public Dictionary<long, ((int Left, int ListY) Position, string Via)> ItemsAt { get; } = [];

        /// <summary>How each recorded item was identified ("map" or "tip"; kept when the positions are lost).</summary>
        public Dictionary<long, string> ViaOf { get; } = [];

        /// <summary>An icon item was learned (the store must be saved even without a count).</summary>
        public bool Learned { get; set; }

        public string End { get; set; } = "stopped";

        public int Rounds { get; set; }

        public int Items { get; set; }

        public int Known { get; set; }

        public int Named { get; set; }

        public int Unreadable { get; set; }

        public int Zeros { get; set; }

        public int Tips { get; set; }

        public int Conflicts { get; set; }

        public int Irregular { get; set; }
    }
}
