using System.Globalization;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Perception;
using Companion.Contracts.Vision;
using Companion.Features.Kit;

namespace Companion.Feature.Crafting;

/// <summary>
/// ScanRecipeList {} (2026-10-14; read-only scan, navigation procedure "crafting.scanList", no commit, no crafting
/// switch, any character permission; store writes need a known character). Reads EVERY recipe of the Make tab's list
/// (My Favorites, Recent, then the dish-type sections), not only the ten favourites:
/// <list type="number">
/// <item>open the Make view (core OpenAbility; one Esc only for the phase announcement);</item>
/// <item>scroll to the top: drag the list down (DragAction on the list anchor) until a drag moves it clearly less than a
/// drag moves a list with room (<see cref="RecipeList.TopReached"/>; live 2026-10-15 every first top drag moved 31 px and
/// a second one only confirmed it, ~7 s per walk);</item>
/// <item>per round: find the whole cards (<see cref="RecipeList.FindCards"/>) on the capture taken right after the last
/// drag, and for each card not yet seen:
/// a card whose picture strictly matches a product learned from an earlier card (<see cref="RecipeList.MatchCard"/>,
/// 2026-10-15) and whose recipe is stored complete is skipped WITHOUT a click (journal "card.knownSkip": the
/// Favorites/Recent repeats and every recipe stored by an earlier run cost nothing); any other card is selected (a click
/// on the located list anchor + the card's offset, never outside the list), the panel read and named by the product's
/// item tip, the unknown material icons pressed once, the recipe and the owned counts stored (the shared
/// <see cref="RecipePanel"/>; a product already read in this scan is skipped by name; a recipe stored from a favourites
/// slot keeps that source); when the game shows the clicked card selected (<see cref="RecipeList.IsSelected"/> on a
/// fresh capture), the card's picture key is learned as that product (an icon item in the store, its own key space);
/// every learned picture also keeps where it was seen (<see cref="RecipeList.PlaceOf"/>, 2026-10-16);</item>
/// <item>drag the list up by two card rows (280 px, less than the list minus a row, so no row is ever skipped) and
/// measure how far it moved by comparing the list before and after (<see cref="RecipeList.MeasureShift"/>): cards already
/// read are recognised by their position in the list; no movement = the end of the list.</item>
/// </list>
/// Find mode (parameter "find" = the product's item id, 2026-10-15; the Craft goal loop): the walk stops as soon as the
/// wanted card is selected (result "list.found", data "itemId" = the id the game logged, "via" = "selected", "jump",
/// "walk" or "fallback"; else "list.notFound"); a known card of another
/// product is never clicked. Speed-ups and their fallbacks (2026-10-16; live 2026-10-15 a find took 40-72 s, 7.7 s per
/// drag, and a grade change sent one walk to the list end and into a stuck tab recovery):
/// <list type="bullet">
/// <item>the wanted recipe still selected on the open Make view (after its craft): the panel read and one product tip
/// prove it, no list input at all ("via" "selected"); anything else, the normal walk;</item>
/// <item>the card's remembered places (<see cref="RecipeList.PlaceHints"/>): after the top, long drags (up to
/// <see cref="RecipeList.MaxJumpDragPx"/>, no card handled between them) bring the shallowest remembered place to the
/// middle of the list ("via" "jump");
/// the cards shown there are handled as in any round, then the next place, then the normal rounds to the list end; an
/// unknown card is selected on that pass only at a remembered place (the wanted card with a new picture), any other is
/// left for later; when the jumps skipped rows or unknown cards were left and the card was not found, one normal walk
/// from the top (journal "scanList.fullPass");</item>
/// <item>in every round the card whose picture is the wanted product's is handled first (no unknown card is clicked
/// when the wanted one is shown); a card whose picture strictly matched the wanted product, shown selected, with a panel
/// that names the wanted stored recipe, needs no product tip (the craft loop reads that tip before Make anyway);</item>
/// <item>the same dish at another grade counts (live 2026-10-16: Salad Savory 1064010 became 1064011 with another card
/// picture; the walk passed it twice and went on to the end): the logged name of the wanted recipe's product, any id.</item>
/// </list>
/// Before a list input that follows a click the identity probe "craft.list" proves the recipe list page is shown: the
/// right-hand tab column switches tabs without any UI event (live 2026-10-15: a stray press opened the Learn tab, and the
/// list anchor was gone). Right after a drag the probe is not needed (the engine located the list anchor for the drag and
/// nothing was pressed since; captures are rate-limited to 2/s, so every capture saved is ~0.5 s per drag). With the Make
/// view on top, the list gone and the unselected Make tab shown (probe "craft.other.tab"), the scan presses the Make tab
/// (craft.make.tab), proves the list is back and restarts from the list top (journal "list.tabRecovered"; known cards
/// are skipped, so this is cheap), at most <see cref="MaxTabRecoveries"/> times; never a HALT for it. Stops at the end of
/// the list, at a safety cap (rounds, cards), or on any procedure end other than success (a procedure the engine
/// deferred ends the walk with its code, "deferred" in it, for the caller to try later). A short fixed pause separates
/// the inputs (human browsing pace, on top of the engine's pacing). Every card (with its tip code, item id and duration
/// in ms) and every drag is journaled. The Make view is left open.
/// </summary>
internal sealed class ScanRecipeListJobHandler : IJobHandler
{
    internal const string JobName = "ScanRecipeList";
    internal const string Procedure = "crafting.scanList";
    internal const string OpenStep = "open";
    internal const string AnnounceStep = "announce.close";
    internal const string TopStep = "top";
    internal const string CardStep = "card";
    internal const string SettleStep = "settle";
    internal const string ScrollStep = "scroll";
    internal const string ScrollSettleStep = "scroll.settle";
    internal const string TabStep = "tab";
    internal const string TabSettleStep = "tab.settle";
    internal const string ProductKey = "product";
    internal const string PairsKey = "pairs";
    internal const string MakeView = "UI_LIFESKILL_MAKE_VIEW";

    /// <summary>The identity probe of the recipe list page (the list anchor on the Make view).</summary>
    internal const string ListProbe = "craft.list";

    /// <summary>The identity probe of another tab of the Ability view (the unselected Make tab is shown).</summary>
    internal const string OtherTabProbe = "craft.other.tab";

    /// <summary>The unselected Make tab of the right-hand column (pressed to go back to the list).</summary>
    internal const string MakeTabTemplate = "craft.make.tab";

    /// <summary>Journal code of a card skipped without a click.</summary>
    internal const string KnownSkip = "card.knownSkip";

    /// <summary>Result code of find mode: the wanted card is selected.</summary>
    internal const string Found = "list.found";

    /// <summary>Result code of find mode: the walk reached the end (or a cap) without the wanted card.</summary>
    internal const string NotFound = RecipeList.FindNotFound;

    /// <summary>The tip code <see cref="RecipePanel.NameProduct"/> gives when the panel names a stored recipe and no tip was pressed.</summary>
    internal const string KnownTip = "tip.known";

    /// <summary>Most drags towards the top before giving up (12 x 280 px).</summary>
    internal const int MaxTopDrags = 12;

    /// <summary>Most rounds (views of cards handled) per job (safety cap).</summary>
    internal const int MaxRounds = 40;

    /// <summary>Most cards handled in one job, skipped or selected (safety cap).</summary>
    internal const int MaxCards = 300;

    /// <summary>Most drags of one jump to a remembered place (12 x 360 px, far more than the list).</summary>
    internal const int MaxJumpDrags = 12;

    /// <summary>Drags cut short (the user moved the mouse) that are tried again when the list did not move.</summary>
    internal const int MaxPartialRetries = 2;

    /// <summary>Two cards are the same list position when their list (content) rows differ by at most this.</summary>
    internal const int SamePositionPx = 20;

    /// <summary>Most returns to the Make tab after a stray press switched the tab (then the scan ends, never a HALT).</summary>
    internal const int MaxTabRecoveries = 3;

    /// <summary>All tab recoveries per job, including those expected after an unlearned card (a runaway backstop).</summary>
    internal const int MaxAllTabRecoveries = 10;

    /// <summary>Journal code of a card skipped without a click because its picture is a card found unlearned this job.</summary>
    internal const string UnlearnedSkip = "card.unlearnedSkip";

    /// <summary>
    /// Fixed pause before every card and every drag after the first input (user 2026-10-15: human browsing speed; was 2 s;
    /// the engine's pacing and the panel's settle wait come on top; no randomness).
    /// </summary>
    internal static readonly TimeSpan InputPause = TimeSpan.FromMilliseconds(300);

    /// <summary>Recipe provenance (Recipe.SourceCapture): read from the screen, n-th card of a list scan.</summary>
    internal static string SourceOf(int card) => "screen:craft.list/" + card.ToString(CultureInfo.InvariantCulture);

    private enum RunKind
    {
        Open,
        Top,
        Card,
        Scroll,
        Tab,
        Jump,
    }

    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        var refusal = JobKit.RequireKnowledge(context) ?? JobKit.CheckProcedures(context, [Procedure, ProductTip.Procedure]);
        if (refusal is not null)
        {
            return refusal;
        }

        var data = context.ProcedureCatalog.Get(Procedure)!;
        if (!IsExpectedShape(data))
        {
            return JobKit.Failed("procedure.invalid:" + Procedure);
        }

        var tip = context.ProcedureCatalog.Get(ProductTip.Procedure)!;
        if (!ProductTip.IsExpectedShape(tip))
        {
            return JobKit.Failed("procedure.invalid:" + ProductTip.Procedure);
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

        // find mode (2026-10-15): walk until the card of this product is selected, then stop (the planner crafts it)
        long? find = parameters.Values.TryGetValue("find", out var f) && f is long id ? id : null;
        return new Scan(context, data, tip, ct, find).Run();
    }

    /// <summary>
    /// The data procedure must be a read-only navigation (no commit step, no gate) with the open step, the card click and
    /// the two drags on the list anchor (top: down; scroll: up by at most two rows, both ends inside the list), the two
    /// reads after the card, and the press on the unselected Make tab (its own template, no offset) with its settle wait.
    /// </summary>
    internal static bool IsExpectedShape(ProcedureDefinition p)
    {
        if (p.Kind != ProcedureKind.Navigation || p.Gate != AutomationSwitch.None
            || p.Steps.Any(s => s.Flags.HasFlag(StepFlags.Commit) || s.Action is AwaitCraftAction))
        {
            return false;
        }

        StepDefinition? Step(string id) => p.Steps.FirstOrDefault(s => s.Id == id);
        if (Step(OpenStep) is not { Action: CallProcedureAction } open || open.Expect.UiEvents?.Any(u => u.UiName == MakeView) != true)
        {
            return false;
        }

        if (Step(CardStep)?.Action is not ClickAction { TemplateId: RecipeList.AnchorTemplate, Offset: not null })
        {
            return false;
        }

        if (Step(TabStep) is not { Action: ClickAction { TemplateId: MakeTabTemplate, Offset: null } } tab || tab.Pre.TopUi != MakeView
            || Step(TabSettleStep)?.Action is not WaitAction)
        {
            return false;
        }

        if (Step(TopStep)?.Action is not DragAction top || !DragInsideList(top) || top.Delta.Y <= 0
            || Step(ScrollStep)?.Action is not DragAction scroll || !DragInsideList(scroll) || scroll.Delta.Y >= 0
            || -scroll.Delta.Y > RecipeList.Viewport.Height - RecipeList.RowPitch - 60)
        {
            return false;
        }

        var card = p.Steps.ToList().FindIndex(s => s.Id == CardStep);
        bool ReadsAfter(string reader, string key) =>
            p.Steps.Skip(card + 1).Any(s => s.Action is ReadAction r && r.ReaderId == reader && r.OutputKey == key);
        return ReadsAfter(ScanFavoritesJobHandler.ProductReader, ProductKey) && ReadsAfter(ScanFavoritesJobHandler.PairsReader, PairsKey)
            && Step(SettleStep)?.Action is WaitAction && Step(ScrollSettleStep)?.Action is WaitAction && Step(AnnounceStep) is not null;
    }

    private static bool DragInsideList(DragAction d)
    {
        if (d.TemplateId != RecipeList.AnchorTemplate || d.Delta.X != 0)
        {
            return false;
        }

        var start = RecipeList.AnchorAt.Offset(d.Offset.X, d.Offset.Y);
        return RecipeList.Clickable(start) && RecipeList.Clickable(start.Offset(d.Delta.X, d.Delta.Y));
    }

    /// <summary>
    /// Drags the Make list to its top with the scan's top drags until the list stops moving (at most
    /// <see cref="MaxTopDrags"/>): My Favorites shows again when the list was left scrolled down (2026-10-31: the game
    /// reopens the Make view after cooking scrolled to the dish's own section). Null when the top was reached;
    /// <paramref name="moved"/> says whether the list moved at all (false: it was at its top already).
    /// </summary>
    internal static JobResult? ScrollToTop(IJobContext context, CancellationToken ct, out bool moved) =>
        ScrollToTop(context, ct, out moved, out _);

    /// <summary>
    /// <see cref="ScrollToTop(IJobContext, CancellationToken, out bool)"/>; <paramref name="failedRun"/> is the drag run that
    /// did not succeed (its halt, stop or deferral), else null.
    /// </summary>
    internal static JobResult? ScrollToTop(IJobContext context, CancellationToken ct, out bool moved, out ProcedureResult? failedRun)
    {
        moved = false;
        failedRun = null;
        if (context.ProcedureCatalog.Get(Procedure) is not { } data || !IsExpectedShape(data))
        {
            return JobKit.Failed("list.procedureMissing");
        }

        var top = Build(data, RunKind.Top, first: false);
        var topPx = DragOf(data, TopStep).Delta.Y;
        for (var i = 1; i <= MaxTopDrags; i++)
        {
            var before = context.Screen.Capture(ct);
            if (!before.Ok)
            {
                return JobKit.Failed("capture." + before.Problem);
            }

            var run = context.Procedures.Run(top, context.Run, ct);
            if (run.Status != ProcedureStatus.Succeeded)
            {
                failedRun = run;
                return JobKit.FromProcedure(run, "top.");
            }

            var after = context.Screen.Capture(ct);
            if (!after.Ok)
            {
                return JobKit.Failed("capture." + after.Problem);
            }

            var partial = run.Attempts.Any(a => a.StepId == TopStep && a.DetailCode == "drag.partial");
            var shift = RecipeList.MeasureShift(before.Frame!, after.Frame!);
            moved |= shift.Known && !shift.Unchanged;
            if (!partial && RecipeList.TopReached(shift, topPx))
            {
                return null;
            }
        }

        return JobKit.Failed("list.topNotReached");
    }

    /// <summary>The data procedure's drag of step <paramref name="id"/> (shape checked).</summary>
    private static DragAction DragOf(ProcedureDefinition data, string id) => (DragAction)data.Steps.Single(s => s.Id == id).Action;

    /// <summary>
    /// One run of the data procedure: the open step (skipped while the Make view is on top, except for the first run),
    /// the announcement Esc, then the steps of <paramref name="kind"/>; a card run's click gets the card's offset; a jump
    /// is the scroll step dragging <paramref name="upPx"/> up from the same press point (the release stays inside the
    /// list: <see cref="RecipeList.MaxUpDragPx"/>), then the scroll settle.
    /// </summary>
    private static ProcedureDefinition Build(ProcedureDefinition data, RunKind kind, bool first, PointI? cardOffset = null, int? upPx = null)
    {
        string[] keep = kind switch
        {
            RunKind.Open => [],
            RunKind.Top => [TopStep, ScrollSettleStep],
            RunKind.Card => [CardStep, SettleStep, "read.*"],
            RunKind.Tab => [TabStep, TabSettleStep],
            _ => [ScrollStep, ScrollSettleStep],
        };
        var steps = new List<StepDefinition>();
        foreach (var s in data.Steps)
        {
            if (s.Id == OpenStep)
            {
                steps.Add(first ? s : s with { Flags = s.Flags | StepFlags.SkipIfSatisfied });
            }
            else if (s.Id == AnnounceStep)
            {
                steps.Add(s);
            }
            else if (keep.Contains(s.Id) || (keep.Contains("read.*") && s.Action is ReadAction))
            {
                var step = s;
                if (s.Id == CardStep && cardOffset is { } o && s.Action is ClickAction c)
                {
                    step = s with { Action = c with { Offset = o } };
                }
                else if (s.Id == ScrollStep && kind == RunKind.Jump && upPx is { } px && s.Action is DragAction d)
                {
                    step = s with { Action = d with { Delta = new PointI(0, -px) } };
                }

                steps.Add(step);
            }
        }

        return data with { Steps = steps };
    }

    /// <summary>The state of one scan.</summary>
    private sealed class Scan
    {
        /// <summary>Cards in a row whose panel is not a recipe panel before the scan ends.</summary>
        private const int MaxMismatchRun = 3;

        private readonly IJobContext _context;
        private readonly ProcedureDefinition _data;
        private readonly CancellationToken _ct;
        private readonly ICharacterKnowledge _knowledge;
        private readonly RecipePanel _panel;
        private readonly HashSet<ItemKey> _seen = [];
        private readonly List<(int Column, int ListY)> _done = [];

        // drag geometry from the data procedure: the pointer distances of the top and scroll drags, the longest jump
        private readonly int _topPx;
        private readonly int _scrollPx;
        private readonly int _maxJumpPx;

        private int _offset;
        private bool _anyInput;
        private int _inputs;
        private int _clicks;
        private int _esc;
        private int _cards;
        private int _selected;
        private int _knownSkips;
        private int _cardKeys;
        private long _selectedMs;
        private int _recorded;
        private int _changed;
        private int _observed;
        private int _duplicates;
        private int _unreadable;
        private int _drags;
        private int _topDrags;
        private int _panelMismatches;
        private int _mismatchRun;
        private int _rounds;
        private int _tips;
        private int _tipsMissing;
        private int _tipsKnown;
        private int _materialTips;
        private int _iconsLearned;
        private int _replaced;
        private int _tabRecoveries;
        // Cards whose click showed no recipe panel: not learned yet; the game also switches to the Learn tab for them
        // (live 2026-10-15: every tab switch followed one, and each restart clicked the same card again until the cap).
        private readonly List<IReadOnlyList<string>> _unlearned = [];
        private int _unlearnedSkips;
        private bool _expectTabSwitch;
        private int _expectedRecoveries;
        // "listEnd", "cap" and "found" are set where they are decided; any other exit (a halt, a failed drag) is a stop
        private string _end = "stopped";

        // The latest capture taken with no input since (2026-10-16): the frame after a drag is the next round's frame and
        // the next drag's "before" (captures are rate-limited to 2/s: each one saved is ~0.5 s).
        private Frame? _frame;

        // The latest list input was a drag (the engine located the list anchor for it) and nothing was pressed since: the
        // next list input needs no identity probe (a tab only switches after a press, live 2026-10-15).
        private bool _listShown;

        // The list rows are measured from the top (after a top run, until a shift that could not be measured): only then
        // are places remembered and jumps made.
        private bool _positionsKnown;
        private int _placesStored;

        // find mode
        private readonly long? _find;
        private readonly ItemKey? _wantedName;
        private readonly Recipe? _wantedRecipe;
        private readonly List<(int Column, int ListY)> _hints = [];
        private readonly int _hintCount;
        private bool _jumpPass;
        private bool _jumped;
        private bool _fullPass;
        private int _jumpDrags;
        private int _deferred;
        private string? _via;
        private long? _foundItemId;

        public Scan(IJobContext context, ProcedureDefinition data, ProcedureDefinition tipData, CancellationToken ct, long? find = null)
        {
            _find = find;
            _context = context;
            _data = data;
            _ct = ct;
            _knowledge = context.Knowledge!;
            _panel = new RecipePanel(context, tipData, ScanRecipeListJobHandler.Procedure, Pause, ct);
            _topPx = DragOf(data, TopStep).Delta.Y;
            var scroll = DragOf(data, ScrollStep);
            _scrollPx = -scroll.Delta.Y;
            _maxJumpPx = RecipeList.MaxUpDragPx(RecipeList.AnchorAt.Offset(scroll.Offset.X, scroll.Offset.Y));
            if (find is { } wanted)
            {
                // the wanted product's name (the same dish at another grade has another id, live 2026-10-16)
                _wantedRecipe = _knowledge.GetRecipes().FirstOrDefault(r => r.ProductItemId == wanted);
                _wantedName = _wantedRecipe?.Product ?? _knowledge.GetIconItems().FirstOrDefault(i => i.ItemId == wanted)?.Item;
                _wantedRecipe ??= _wantedName is { } n ? _knowledge.GetRecipes().FirstOrDefault(r => r.Product == n) : null;
                _hints.AddRange(RecipeList.PlaceHints(_knowledge.GetIconItems(), IsWanted));
                _hintCount = _hints.Count;
            }
        }

        /// <summary>How handling one card ended: go on, start again from the list top, or end the scan.</summary>
        private readonly record struct CardEnd(JobResult? Problem, bool Restart)
        {
            public static CardEnd Next => default;
        }

        public JobResult Run()
        {
            // 0. find: the wanted recipe is still selected on the open Make view (after its craft): no walk at all
            if (_find is not null)
            {
                JournalFind();
                if (AlreadySelected() is { } selected)
                {
                    return End(selected);
                }
            }

            // 1. the Make view
            var open = RunProcedure(Build(_data, RunKind.Open, first: true), "open.", reopenCheck: false);
            if (open.Problem is { } p0)
            {
                return End(p0);
            }

            _jumpPass = _hints.Count > 0;

            // 2. to the top: drag down until the list no longer moves (again after a return to the Make tab)
            while (true)
            {
                var top = ToTop();
                if (top.Problem is { } p1)
                {
                    return End(p1);
                }

                if (top.Restart)
                {
                    continue;
                }

                // 3. rounds: read the new cards, then drag up two rows (or jump to the wanted card's remembered place)
                var rounds = Rounds();
                if (rounds.Problem is { } p2)
                {
                    return End(p2);
                }

                if (rounds.Restart)
                {
                    continue;
                }

                if (_find is not null && (_jumped || _deferred > 0) && !_fullPass && _end == "listEnd")
                {
                    // fallback: the jumps skipped rows and unknown cards were left for later; the card may have moved up (a
                    // favourite removed, the Recent row changed) or show a new picture: one normal walk from the top, every
                    // row seen and every unknown card selected (the cards read so far are known: no click)
                    FullPass("listEnd");
                    continue;
                }

                return Finish();
            }
        }

        /// <summary>Drags towards the top until the list moved clearly less than a drag moves it, or not at all.</summary>
        private CardEnd ToTop()
        {
            _done.Clear();
            _offset = 0;
            _positionsKnown = false;
            for (var i = 1; i <= MaxTopDrags; i++)
            {
                var (problem, restart, shift, partial) = Drag(RunKind.Top);
                if (problem is not null || restart)
                {
                    return new CardEnd(problem, restart);
                }

                JournalDrag("top", i, shift, partial, _topPx);
                if (!partial && RecipeList.TopReached(shift, _topPx))
                {
                    _positionsKnown = true;
                    return CardEnd.Next;
                }
            }

            return new CardEnd(JobKit.Failed("list.topNotReached"), false);
        }

        /// <summary>The rounds from the top to the end of the list (or a cap).</summary>
        private CardEnd Rounds()
        {
            var stillRetries = 0;
            while (true)
            {
                if (++_rounds > MaxRounds)
                {
                    _end = "cap";
                    return CardEnd.Next;
                }

                var frame = _frame;
                if (frame is null)
                {
                    var capture = _context.Screen.Capture(_ct);
                    if (!capture.Ok)
                    {
                        return new CardEnd(JobKit.Failed("capture." + capture.Problem), false);
                    }

                    frame = _frame = capture.Frame!;
                }

                var handled = HandleRound(frame);
                if (handled.Problem is not null || handled.Restart || _end == "cap")
                {
                    return handled;
                }

                // find mode: a remembered place further down than one drag shows is reached with long drags
                if (_jumpPass && _positionsKnown && NextHint() is { } hint
                    && !RecipeList.ShownWhole(hint.ListY, _offset - (_scrollPx - RecipeList.DragLossPx)))
                {
                    var jump = JumpTo(hint);
                    if (jump.Problem is not null || jump.Restart)
                    {
                        return jump;
                    }

                    continue;
                }

                var (dragProblem, restart, shift, partial) = Drag(RunKind.Scroll);
                if (dragProblem is not null || restart)
                {
                    return new CardEnd(dragProblem, restart);
                }

                JournalDrag("scroll", _rounds, shift, partial, _scrollPx);
                if (shift.Unchanged)
                {
                    if (partial && stillRetries < MaxPartialRetries)
                    {
                        stillRetries++;
                        continue; // the drag was cut short: try it again
                    }

                    if (partial)
                    {
                        return new CardEnd(JobKit.Failed("list.dragIncomplete"), false);
                    }

                    _end = "listEnd";
                    return CardEnd.Next;
                }

                stillRetries = 0;
                if (shift.Known)
                {
                    _offset += shift.Dy;
                }
                else
                {
                    PositionsLost();
                }
            }
        }

        /// <summary>
        /// The cards of one view not handled yet, row by row, left to right; in find mode the card whose picture is the
        /// wanted product's first (no unknown card is clicked while the wanted one is shown).
        /// </summary>
        private CardEnd HandleRound(Frame frame)
        {
            var fresh = new List<(ListCard Card, int ListY, IReadOnlyList<string> Keys)>();
            foreach (var card in RecipeList.FindCards(frame))
            {
                var listY = card.Center.Y - _offset;
                if (_done.Any(d => d.Column == card.Column && Math.Abs(d.ListY - listY) <= SamePositionPx))
                {
                    continue; // read before this drag: it only moved
                }

                fresh.Add((card, listY, RecipeList.Clickable(card.Center) ? RecipeList.CardKeys(frame, card) : Array.Empty<string>()));
            }

            if (_find is not null)
            {
                // the wanted picture first, then an unknown card at a remembered place (the wanted card with a new picture)
                var learned = _knowledge.GetIconItems();
                fresh = [.. fresh.OrderBy(c => RecipeList.MatchCard(c.Keys, learned).Item is { } i ? (IsWanted(i) ? 0 : 2) : AtHint(c.Card, c.ListY) ? 1 : 2)];
            }

            foreach (var (card, listY, keys) in fresh)
            {
                if (_cards >= MaxCards)
                {
                    _end = "cap";
                    return CardEnd.Next;
                }

                if (!RecipeList.Clickable(card.Center))
                {
                    JournalCard(card, "outsideList", null, null);
                    continue;
                }

                // matched against the store as it is now: a card learned earlier in this view (a Recent repeat) is known
                var start = _context.Clock.Monotonic;
                var handled = HandleCard(card, keys, RecipeList.MatchCard(keys, _knowledge.GetIconItems()), start);
                if (handled.Problem is not null || handled.Restart)
                {
                    return handled;
                }

                _done.Add((card.Column, listY));
            }

            return CardEnd.Next;
        }

        /// <summary>The shallowest remembered place of the wanted card below the cards shown now, or null.</summary>
        private (int Column, int ListY)? NextHint() =>
            _hints.Where(h => h.ListY + _offset > RecipeList.Viewport.Bottom - RecipeList.WholeCardMarginPx).Select(h => ((int, int)?)h).FirstOrDefault();

        /// <summary>A card at a remembered place of the wanted card (its column, within a third of a row).</summary>
        private bool AtHint(ListCard card, int listY) =>
            _hints.Any(h => h.Column == card.Column && Math.Abs(h.ListY - listY) <= RecipeList.RowPitch / 3);

        /// <summary>
        /// Long drags up (no card handled between them) until the remembered <paramref name="place"/> is at the middle of
        /// the list. Each drag is measured like a scroll drag. The list's end before the place (it did not move, or moved
        /// clearly less than dragged): the places beyond it are stale and dropped. A movement that could not be measured
        /// ends the jumps (positions lost): the full walk from the top starts at once (a restart).
        /// </summary>
        private CardEnd JumpTo((int Column, int ListY) place)
        {
            var hint = place.ListY;
            _jumped = true;
            var stillRetries = 0;
            for (var n = 0; n < MaxJumpDrags; n++)
            {
                if (RecipeList.JumpDragPx(hint, _offset, _maxJumpPx) is not { } px)
                {
                    break;
                }

                var (problem, restart, shift, partial) = Drag(RunKind.Jump, px);
                if (problem is not null || restart)
                {
                    return new CardEnd(problem, restart);
                }

                _jumpDrags++;
                JournalDrag("jump", _jumpDrags, shift, partial, px);
                if (!shift.Known)
                {
                    // fallback: the rows are no longer known: the normal walk from the top at once (not after the list end)
                    PositionsLost();
                    FullPass("jumpLost");
                    return new CardEnd(null, true);
                }

                if (shift.Unchanged)
                {
                    if (partial && stillRetries++ < MaxPartialRetries)
                    {
                        continue; // cut short: try it again
                    }

                    break; // the end of the list
                }

                _offset += shift.Dy;
                if (!partial && -shift.Dy < px - RecipeList.DragLossPx - RecipeList.TopShortMovePx)
                {
                    break; // moved less than dragged: the end of the list
                }
            }

            if (!RecipeList.ShownWhole(hint, _offset))
            {
                _hints.RemoveAll(h => h.ListY + _offset > RecipeList.Viewport.Bottom - RecipeList.WholeCardMarginPx);
            }

            return CardEnd.Next;
        }

        /// <summary>Find mode: the fallback walk from the top starts (no jumps, every unknown card selected).</summary>
        private void FullPass(string why)
        {
            _fullPass = true;
            _jumpPass = false;
            JobKit.Journal(_context, JournalKind.Decision, ScanRecipeListJobHandler.Procedure, JobKit.Data(
                ("event", "scanList.fullPass"), ("why", why), ("rounds", (long)_rounds), ("cards", (long)_cards)));
        }

        /// <summary>The list's movement could not be measured: the positions (and places, and jumps) are no longer known.</summary>
        private void PositionsLost()
        {
            // the names still avoid double records
            _done.Clear();
            _offset = 0;
            _positionsKnown = false;
            JobKit.Journal(_context, JournalKind.Decision, ScanRecipeListJobHandler.Procedure, JobKit.Data(("event", "scanList.positionsLost"), ("round", (long)_rounds)));
        }

        private JobResult Finish()
        {
            if (_find is not null)
            {
                // "not found" only after the walk reached the list's end; a walk stopped by its round or card cap did not
                // see every card (audit R1.6)
                return End(JobKit.Failed(_end == "cap" ? "list.capped" : NotFound));
            }

            if (_recorded == 0 && _observed == 0 && _knownSkips == 0)
            {
                return End(JobKit.Failed("list.nothingRead"));
            }

            return End(JobKit.Result(JobState.Succeeded, "list.scanned"));
        }

        /// <summary>
        /// One card: skipped when its picture is a learned product with a complete recipe (find mode: any learned product
        /// but the wanted one), else selected and read.
        /// </summary>
        private CardEnd HandleCard(ListCard card, IReadOnlyList<string> keys, StockIconMatch match, TimeSpan start)
        {
            if (_find is not null && match.Item is { } other && !IsWanted(other))
            {
                _cards++;
                _knownSkips++;
                RememberPlace(other, card);
                return CardEnd.Next; // find mode: a known card of another product is never clicked
            }

            if (_find is not null && match.Item is null && _jumpPass && !AtHint(card, card.Center.Y - _offset))
            {
                // find mode with remembered places: an unknown card elsewhere is left for the fallback walk (no click,
                // no learning on the way to the wanted card)
                _deferred++;
                return CardEnd.Next;
            }

            if (_find is null && match.Item is { } known && _knowledge.GetRecipes().Any(r => r.ProductItemId == known.ItemId && r.Confidence == RecipeConfidence.Complete))
            {
                _cards++;
                _knownSkips++;
                _seen.Add(known.Item);
                RememberPlace(known, card);
                JournalCard(card, KnownSkip, null, null, start: start, match: match.Code, bits: match.Distance, itemId: known.ItemId);
                return CardEnd.Next;
            }

            if (_unlearned.Any(u => u.Any(a => keys.Any(b => IconKeys.Distance(a, b) <= RecipeList.CardSameMaxBits))))
            {
                _cards++;
                _unlearnedSkips++;
                JournalCard(card, UnlearnedSkip, null, null, start: start, match: match.Code);
                return CardEnd.Next;
            }

            return ReadCard(card, keys, match, start);
        }

        /// <summary>Selects one card, reads the panel, learns the card and stores the recipe.</summary>
        private CardEnd ReadCard(ListCard card, IReadOnlyList<string> keys, StockIconMatch match, TimeSpan start)
        {
            var (listProblem, recovered) = EnsureList();
            if (listProblem is not null || recovered)
            {
                return new CardEnd(listProblem, recovered);
            }

            Pause();
            _cards++;
            _selected++;
            var run = RunProcedure(Build(_data, RunKind.Card, first: false, RecipeList.OffsetOf(card.Center)), "card.", reopenCheck: true);
            if (run.Problem is { } problem)
            {
                JournalCard(card, "procedure", null, null, start: start, match: match.Code);
                return new CardEnd(problem, false);
            }

            var productProblem = JobKit.CheckRead(run.Result!, ProductKey, out var product);
            var pairsProblem = JobKit.CheckRead(run.Result!, PairsKey, out var pairs);
            if ((productProblem ?? pairsProblem) is { } readProblem)
            {
                if (readProblem.State == JobState.Failed
                    && (product?.Status == ReadStatus.ScreenMismatch || pairs?.Status == ReadStatus.ScreenMismatch)
                    && ++_mismatchRun < MaxMismatchRun)
                {
                    // one card whose panel is not a recipe panel (live 2026-10-15, card 53: a locked recipe?) skips that
                    // card; the next card's list input proves the list first (a switched tab is recovered there)
                    _panelMismatches++;
                    _unlearned.Add(keys);
                    _expectTabSwitch = true;
                    JournalCard(card, "panelMismatch", null, null, start: start, match: match.Code);
                    return CardEnd.Next;
                }

                JournalCard(card, "read.problem", null, null, start: start, match: match.Code);
                return new CardEnd(readProblem, false);
            }

            _mismatchRun = 0;

            // the game shows the clicked card selected: the panel is that card's (a press that missed or landed elsewhere is
            // never learned)
            var selected = keys.Count > 0 && SelectedNow(card);
            var read = _panel.Fold(product!, pairs!);
            if (read.IsBlank)
            {
                JournalCard(card, "blank", read, null, start: start, match: match.Code);
                return CardEnd.Next;
            }

            // the product's item tip: the name the game logs replaces the OCR title (also for the duplicate check)
            var (tip, named) = NameProduct(read, match, selected);
            Count(tip.Run);
            if (!tip.Succeeded)
            {
                JournalCard(card, ProductTip.RunFailed, read, null, tip, start: start, match: match.Code);
                return new CardEnd(JobKit.FromProcedure(tip.Run, "tip."), false);
            }

            _tips += tip.Tip is not null ? 1 : 0;
            _tipsKnown += tip.Code == KnownTip ? 1 : 0;
            _tipsMissing += tip.Tip is null && tip.Code != KnownTip ? 1 : 0;
            read = named;
            var cardKey = LearnCard(keys, match, tip, selected, card);
            if (read.Product is { } name && !_seen.Add(name))
            {
                _duplicates++;
                if (cardKey == "learned")
                {
                    _knowledge.Save();
                }

                JournalCard(card, "duplicate", read, null, tip, start: start, match: match.Code, cardKey: cardKey); // a Recent / My Favorites card, or a click that did not select
                return IsWanted(read) ? FoundCard(read) : CardEnd.Next;
            }

            // the item tips of the materials whose icon is not known yet (each icon pressed once, ever), then the store
            var stored = _panel.Store(read, SourceOf(_cards), keepFavorites: true, [("card", (long)_cards)]);
            foreach (var tipRun in stored.MaterialTips.Runs)
            {
                Count(tipRun);
            }

            _materialTips += stored.MaterialTips.Presses;
            _iconsLearned += stored.MaterialTips.Learned;
            if (stored.Outcome is not { } outcome)
            {
                JournalCard(card, ProductTip.RunFailed, read, null, tip, start: start, match: match.Code, cardKey: cardKey);
                return new CardEnd(JobKit.FromProcedure(stored.MaterialTips.Failed!, "tip."), false);
            }

            _recorded += outcome.Recorded ? 1 : 0;
            _changed += outcome.Changed ? 1 : 0;
            _replaced += outcome.Replaced;
            _observed += outcome.Observed;
            _unreadable += outcome.Code is "unreadable" or "noMaterials" ? 1 : 0;
            JournalCard(card, outcome.Code, stored.Read, outcome, tip, stored.MaterialTips.Presses, start, match.Code, cardKey: cardKey);
            return IsWanted(stored.Read) ? FoundCard(stored.Read) : CardEnd.Next;
        }

        /// <summary>Find mode: the wanted card is selected; the walk ends (the card is left selected for the caller).</summary>
        private CardEnd FoundCard(FavoriteRead read)
        {
            _end = "found";
            _via ??= _fullPass ? "fallback" : _jumped ? "jump" : "walk";
            _foundItemId = read.ProductItemId;
            return new CardEnd(JobKit.Result(JobState.Succeeded, Found), false);
        }

        /// <summary>
        /// The product's name and id for a selected card: its item tip; in find mode no tip when the card's picture
        /// strictly matched the wanted product, the game shows it selected and the panel names the wanted stored recipe
        /// (2026-10-16: the craft loop reads the selected product's tip before its first Make anyway; a panel naming any
        /// other recipe still gets its tip).
        /// </summary>
        private (ItemTip.Outcome Tip, FavoriteRead Read) NameProduct(FavoriteRead read, StockIconMatch match, bool selected)
        {
            if (_find is { } wanted && selected && match.Item?.ItemId == wanted && LooksWanted(read))
            {
                var (known, named) = _panel.NameProduct(read, skipKnown: true);
                if (known.Code != KnownTip || IsWanted(named))
                {
                    return (known, named);
                }
            }

            return _panel.NameProduct(read);
        }

        /// <summary>
        /// Find mode, before any list input: the open Make view's panel already shows the wanted recipe (its title, or its
        /// materials with their amounts, are the wanted stored recipe's) and the product's tip logs the wanted product:
        /// found without a walk. Null (the walk goes on) when the view is not on top, the panel is not shown, empty, or
        /// another recipe's; a tip run that does not succeed ends the job with its code.
        /// </summary>
        private JobResult? AlreadySelected()
        {
            if (!string.Equals(_context.UiState.AdvisoryTop, MakeView, StringComparison.Ordinal))
            {
                return null;
            }

            var product = _context.Screen.Read(ScanFavoritesJobHandler.ProductReader, _ct);
            var pairs = _context.Screen.Read(ScanFavoritesJobHandler.PairsReader, _ct);
            if (product.Status is ReadStatus.ScreenMismatch or ReadStatus.NeedsSurvey || pairs.Status is ReadStatus.ScreenMismatch or ReadStatus.NeedsSurvey)
            {
                JournalSelected("notShown", null);
                return null;
            }

            var read = _panel.Fold(product, pairs);
            if (read.IsBlank || !LooksWanted(read))
            {
                JournalSelected(read.IsBlank ? "blank" : "other", null);
                return null;
            }

            var (tip, named) = _panel.NameProduct(read);
            Count(tip.Run);
            if (!tip.Succeeded)
            {
                JournalSelected(ProductTip.RunFailed, tip);
                return JobKit.FromProcedure(tip.Run, "tip.");
            }

            _tips += tip.Tip is not null ? 1 : 0;
            _tipsMissing += tip.Tip is null ? 1 : 0;
            if (!IsWanted(named))
            {
                JournalSelected("tipOther", tip);
                return null;
            }

            JournalSelected("found", tip);
            _end = "found";
            _via = "selected";
            _foundItemId = named.ProductItemId;
            return JobKit.Result(JobState.Succeeded, Found);
        }

        /// <summary>
        /// The panel read names the wanted stored recipe: its title, or (an unread or garbled title: live OCR read "ams")
        /// its materials with their amounts.
        /// </summary>
        private bool LooksWanted(FavoriteRead read)
        {
            if (read.Product is { } title && _wantedName is { } name && title == name)
            {
                return true;
            }

            if (_wantedRecipe is not { } recipe || read.Materials.Count == 0 || read.Materials.Count != recipe.Materials.Count
                || read.Materials.Any(m => m.ItemId is null || m.Required is null) || recipe.Materials.Any(m => m.ItemId is null))
            {
                return false;
            }

            var seen = read.Materials.Select(m => (m.ItemId!.Value, m.Required!.Value)).Order().ToList();
            return recipe.Materials.Select(m => (m.ItemId!.Value, (long)m.Qty)).Order().SequenceEqual(seen);
        }

        /// <summary>Find mode: an item is the wanted product (its id, or the same dish name at another grade).</summary>
        private bool IsWanted(long? itemId, ItemKey? name) =>
            _find is { } wanted && itemId is { } id && (id == wanted || (_wantedName is { } n && name == n));

        private bool IsWanted(IconItem item) => IsWanted(item.ItemId, item.Item);

        private bool IsWanted(FavoriteRead read) => IsWanted(read.ProductItemId, read.Product);

        /// <summary>
        /// Learns the card's picture key (its key at the found rows) as the product the game logged, when the clicked card
        /// is the selected one and the key is not already that product's; the picture keeps where the card was seen. The
        /// code journaled with the card.
        /// </summary>
        private string LearnCard(IReadOnlyList<string> keys, StockIconMatch match, ItemTip.Outcome tip, bool selected, ListCard card)
        {
            if (keys.Count == 0)
            {
                return "noKey";
            }

            if (tip.Code == KnownTip && match.Item is { } trusted)
            {
                RememberPlace(trusted, card);
                return "known"; // no tip asked: the picture matched the wanted product
            }

            if (tip.Tip is not { ItemId: > 0 } logged)
            {
                return "noTip";
            }

            if (!selected)
            {
                return "notSelected";
            }

            if (match.Item is { } item && item.ItemId == logged.ItemId)
            {
                RememberPlace(item, card);
                return "known";
            }

            if (ItemTip.Learn(_knowledge, keys[0], logged, _context.Clock.UtcNow) is not { } learned)
            {
                return ItemTip.BadName;
            }

            RememberPlace(learned, card);
            _cardKeys++;
            return "learned";
        }

        /// <summary>
        /// Keeps where a learned card picture was seen (its icon item's place), while the list rows are known and when the
        /// place changed. A hint for the next find walk, never the truth.
        /// </summary>
        private void RememberPlace(IconItem item, ListCard card)
        {
            var listY = card.Center.Y - _offset;
            if (!_positionsKnown || RecipeList.SamePlace(item.Place, card.Column, listY, SamePositionPx)
                || (item.Place is not null && RecipeList.ParsePlace(item.Place) is null))
            {
                return; // unknown rows, the same place, or a picture placed elsewhere (never a list card's)
            }

            try
            {
                _knowledge.LearnIconItem(item with { Place = RecipeList.PlaceOf(card.Column, listY) });
                _placesStored++;
            }
            catch (ArgumentException)
            {
                // not storable: the place stays unknown
            }
        }

        /// <summary>A fresh capture shows the card at the same list place selected.</summary>
        private bool SelectedNow(ListCard card)
        {
            var capture = _context.Screen.Capture(_ct);
            if (!capture.Ok)
            {
                return false;
            }

            var frame = capture.Frame!;
            return RecipeList.FindCards(frame).Any(c => c.Column == card.Column && Math.Abs(c.Center.Y - card.Center.Y) <= SamePositionPx && RecipeList.IsSelected(frame, c));
        }

        /// <summary>
        /// Proves the recipe list page is shown before a list input. The list gone while the Make view is on top and the
        /// unselected Make tab shown: another tab of the right-hand column was opened (a stray press); the Make tab is
        /// pressed and the list proved again: the scan restarts from the top. Another UI on top is left to the run's own
        /// steps (open, stray-popup Esc). Never a HALT: a list that cannot be brought back ends the scan. Right after a drag
        /// (nothing pressed since) the list is known to be shown: no probe.
        /// </summary>
        private (JobResult? Problem, bool Recovered) EnsureList()
        {
            if (_listShown)
            {
                return (null, false);
            }

            var probe = _context.Screen.Read(ListProbe, _ct);
            for (var settle = 0; settle < 3 && probe.Status == ReadStatus.ScreenMismatch && string.Equals(_context.UiState.AdvisoryTop, MakeView, StringComparison.Ordinal); settle++)
            {
                // the view may still be opening (live 2026-10-17: probed 0.6 s after the Ability view opened, the list
                // anchor matched at 0.989 on a capture moments later, and the find ended "unknownView"): look again
                _context.Clock.Sleep(TimeSpan.FromMilliseconds(500), _ct);
                probe = _context.Screen.Read(ListProbe, _ct);
            }

            if (probe.Status == ReadStatus.NeedsSurvey)
            {
                return (JobKit.NeedsSurvey([probe.ReaderId]), false);
            }

            if (probe.Status != ReadStatus.ScreenMismatch || !string.Equals(_context.UiState.AdvisoryTop, MakeView, StringComparison.Ordinal))
            {
                return (null, false);
            }

            var other = _context.Screen.Read(OtherTabProbe, _ct);
            if (other.Status != ReadStatus.Ok)
            {
                // neither the list nor another tab: never press anything blind
                JournalTab("unknownView");
                return (other.Status == ReadStatus.NeedsSurvey ? JobKit.NeedsSurvey([other.ReaderId]) : JobKit.Failed("list.notShown"), false);
            }

            // a switch right after an unlearned card is the game's own doing: recovered without using the cap, within a
            // backstop for runaway loops
            var expected = _expectTabSwitch;
            _expectTabSwitch = false;
            if ((!expected && _tabRecoveries >= MaxTabRecoveries) || _tabRecoveries + _expectedRecoveries >= MaxAllTabRecoveries)
            {
                JournalTab("capReached");
                return (JobKit.Failed("list.tabLost"), false);
            }

            if (expected)
            {
                _expectedRecoveries++;
            }
            else
            {
                _tabRecoveries++;
            }
            Pause();
            var run = RunProcedure(Build(_data, RunKind.Tab, first: false), "tab.", reopenCheck: true);
            if (run.Problem is { } problem)
            {
                JournalTab("procedure");
                return (problem, false);
            }

            // the press logs no UI event: the list's own anchor in a fresh read is the proof
            var back = _context.Screen.Read(ListProbe, _ct);
            if (back.Status == ReadStatus.ScreenMismatch)
            {
                Pause();
                back = _context.Screen.Read(ListProbe, _ct);
            }

            if (back.Status == ReadStatus.ScreenMismatch)
            {
                JournalTab("notBack");
                return (JobKit.Failed("list.tabNotRecovered"), false);
            }

            JournalTab("recovered");
            return (null, true);
        }

        /// <summary>
        /// One drag run between two captures of the list (the "before" frame is the latest one when nothing was pressed
        /// since); the shift and whether the drag was cut short. A jump drags <paramref name="upPx"/> up.
        /// </summary>
        private (JobResult? Problem, bool Restart, ListShift Shift, bool Partial) Drag(RunKind kind, int? upPx = null)
        {
            var (listProblem, recovered) = EnsureList();
            if (listProblem is not null || recovered)
            {
                return (listProblem, recovered, default, false);
            }

            Pause();
            var before = _frame;
            if (before is null)
            {
                var capture = _context.Screen.Capture(_ct);
                if (!capture.Ok)
                {
                    return (JobKit.Failed("capture." + capture.Problem), false, default, false);
                }

                before = capture.Frame!;
            }

            var step = kind == RunKind.Top ? TopStep : ScrollStep;
            var run = RunProcedure(Build(_data, kind, first: false, upPx: upPx), step + ".", reopenCheck: true);
            if (run.Problem is { } problem)
            {
                return (problem, false, default, false);
            }

            _drags++;
            if (kind == RunKind.Top)
            {
                _topDrags++;
            }

            var after = _context.Screen.Capture(_ct);
            if (!after.Ok)
            {
                return (JobKit.Failed("capture." + after.Problem), false, default, false);
            }

            _frame = after.Frame!;
            _listShown = true;
            var partial = run.Result!.Attempts.Any(a => a.StepId == step && a.DetailCode == "drag.partial");
            return (null, false, RecipeList.MeasureShift(before, after.Frame!), partial);
        }

        /// <summary>
        /// Runs one procedure; a run that is not a success, or that had to open the view again, ends the scan (a deferred
        /// run keeps the engine's "deferred" code). Any run may press: the latest frame and the list proof are dropped.
        /// </summary>
        private (ProcedureResult? Result, JobResult? Problem) RunProcedure(ProcedureDefinition p, string codePrefix, bool reopenCheck)
        {
            _frame = null;
            _listShown = false;
            var r = _context.Procedures.Run(p, _context.Run, _ct);
            Count(r);
            if (r.Status != ProcedureStatus.Succeeded)
            {
                return (r, JobKit.FromProcedure(r, codePrefix));
            }

            if (reopenCheck && r.Attempts.Any(a => a.StepId == OpenStep && a.DetailCode == "call.done"))
            {
                // the Make view had been closed (by the user or a popup): the list positions are no longer known
                return (r, JobKit.Failed("list.viewReopened"));
            }

            return (r, null);
        }

        /// <summary>Counts one procedure run's inputs (a tip run presses too: the latest frame and the list proof are dropped).</summary>
        private void Count(ProcedureResult r)
        {
            _anyInput = true;
            _inputs += r.InputBatches;
            _clicks += r.Clicks;
            _esc += r.EscPresses;
            if (r.InputBatches > 0)
            {
                _frame = null;
                _listShown = false;
            }
        }

        private void Pause()
        {
            if (!_anyInput)
            {
                return;
            }

            try
            {
                _ct.ThrowIfCancellationRequested();
                _context.Clock.Sleep(InputPause, _ct);
            }
            catch (OperationCanceledException)
            {
                JobKit.Journal(_context, JournalKind.Stock, ScanRecipeListJobHandler.Procedure, With(Data(), ("event", "scanList.end"), ("code", "cancelled")));
                throw;
            }
        }

        private void JournalCard(
            ListCard card,
            string code,
            FavoriteRead? read,
            ScreenRecipes.StoreOutcome? stored,
            ItemTip.Outcome? tip = null,
            int materialTips = 0,
            TimeSpan? start = null,
            string? match = null,
            int? bits = null,
            long? itemId = null,
            string? cardKey = null)
        {
            var ms = start is { } t0 ? (long)Math.Max(0, (_context.Clock.Monotonic - t0).TotalMilliseconds) : 0L;
            if (code != KnownSkip && start is not null)
            {
                _selectedMs += ms;
            }

            var data = With(JobKit.Data(
                ("event", "scanList.card"),
                ("card", (long)_cards),
                ("round", (long)_rounds),
                ("column", (long)(card.Column + 1)),
                ("listY", (long)(card.Center.Y - _offset)),
                ("code", code),
                ("ms", ms),
                ("match", match),
                ("bits", bits is { } b && b != int.MaxValue ? (long)b : null),
                ("cardKey", cardKey),
                ("product", read?.Product is not null),
                ("materials", (long)(stored?.Materials ?? 0)),
                ("unknownMaterials", (long)(read?.UnknownSlots ?? 0)),
                ("namedMaterials", (long)(read?.Materials.Count(m => m.ItemId is not null) ?? 0)),
                ("requires", read?.Requires is not null),
                ("counter", read?.Counter is not null),
                ("observed", (long)(stored?.Observed ?? 0)),
                ("recipeChanged", stored?.Changed ?? false),
                ("replaced", (long)(stored?.Replaced ?? 0)),
                ("materialTips", (long)materialTips)), ItemTip.Fields(tip));
            if (itemId is not null)
            {
                data["itemId"] = itemId;
            }

            JobKit.Journal(_context, JournalKind.Stock, ScanRecipeListJobHandler.Procedure, data);
        }

        private void JournalDrag(string kind, int n, ListShift shift, bool partial, int px) =>
            JobKit.Journal(_context, JournalKind.Stock, ScanRecipeListJobHandler.Procedure, JobKit.Data(
                ("event", "scanList.drag"),
                ("kind", kind),
                ("n", (long)n),
                ("px", (long)px),
                ("shiftKnown", shift.Known),
                ("shiftPx", (long)shift.Dy),
                ("unchanged", shift.Unchanged),
                ("partial", partial),
                ("error", shift.Error),
                ("next", shift.Next)));

        private void JournalTab(string code) =>
            JobKit.Journal(_context, JournalKind.Decision, ScanRecipeListJobHandler.Procedure, JobKit.Data(
                ("event", "list.tabRecovered"),
                ("code", code),
                ("n", (long)_tabRecoveries),
                ("round", (long)_rounds),
                ("cards", (long)_cards)));

        /// <summary>Find mode: what the walk looks for (the wanted id, whether its dish name is known, its remembered places).</summary>
        private void JournalFind() =>
            JobKit.Journal(_context, JournalKind.Decision, ScanRecipeListJobHandler.Procedure, JobKit.Data(
                ("event", "scanList.find"),
                ("itemId", _find),
                ("named", _wantedName is not null),
                ("hints", (long)_hintCount),
                ("firstHint", _hints.Count > 0 ? (long)_hints[0].ListY : null)));

        /// <summary>Find mode: the check of the recipe already selected on the open Make view.</summary>
        private void JournalSelected(string code, ItemTip.Outcome? tip) =>
            JobKit.Journal(_context, JournalKind.Decision, ScanRecipeListJobHandler.Procedure, With(
                JobKit.Data(("event", "scanList.selected"), ("code", code)), ItemTip.Fields(tip)));

        private JobResult End(JobResult result)
        {
            if (_placesStored > 0)
            {
                _knowledge.Save(); // the places learned on the way (a hint for the next find)
            }

            var data = Data();
            if (result.Data is not null)
            {
                foreach (var (k, v) in result.Data)
                {
                    data.TryAdd(k, v);
                }
            }

            JobKit.Journal(_context, JournalKind.Stock, ScanRecipeListJobHandler.Procedure, With(Data(), ("event", "scanList.end"), ("code", result.Code)));
            return result with { Data = data };
        }

        /// <summary>
        /// The result fields, the most telling first (the host passes on the first 16; a job result keeps 32): cards
        /// handled, skipped without a click, selected, the average ms of a selected card, recipes, how the scan ended (and,
        /// found, how and which id); then the rest (clicks and Esc presses, also in "inputs", only in the journal).
        /// </summary>
        private Dictionary<string, object?> Data() => JobKit.Data(
            ("cards", (long)_cards),
            ("knownSkips", (long)_knownSkips),
            ("selected", (long)_selected),
            ("msPerSelected", _selected == 0 ? 0L : _selectedMs / _selected),
            ("recipes", (long)_recorded),
            ("recipesChanged", (long)_changed),
            ("duplicates", (long)_duplicates),
            ("end", _end),
            ("via", _via),
            ("itemId", _foundItemId),
            ("cardKeys", (long)_cardKeys),
            ("tabRecoveries", (long)_tabRecoveries),
            ("drags", (long)_drags),
            ("jumpDrags", (long)_jumpDrags),
            ("rounds", (long)_rounds),
            ("inputs", (long)_inputs),
            ("unreadable", (long)_unreadable),
            ("panelMismatches", (long)_panelMismatches),
            ("tips", (long)_tips),
            ("tipsKnown", (long)_tipsKnown),
            ("recipesReplaced", (long)_replaced),
            ("observed", (long)_observed),
            ("topDrags", (long)_topDrags),
            ("scrollDrags", (long)(_drags - _topDrags - _jumpDrags)),
            ("tipsMissing", (long)_tipsMissing),
            ("materialTips", (long)_materialTips),
            ("iconsLearned", (long)_iconsLearned),
            ("hints", (long)_hintCount),
            ("placesStored", (long)_placesStored),
            ("deferred", (long)_deferred),
            ("unlearnedSkips", (long)_unlearnedSkips),
            ("expectedTabRecoveries", (long)_expectedRecoveries),
            ("clicks", (long)_clicks),
            ("esc", (long)_esc));

        private static Dictionary<string, object?> With(Dictionary<string, object?> data, params (string Key, object? Value)[] extra)
        {
            foreach (var (k, v) in extra)
            {
                data[k] = v;
            }

            return data;
        }
    }
}
