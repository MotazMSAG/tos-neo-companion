using System.Globalization;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Contracts.Perception;
using Companion.Features.Kit;

namespace Companion.Feature.Crafting;

/// <summary>
/// ScanFavorites {} (read-only scan; navigation procedure, no commit, no crafting switch, any character permission;
/// store writes need a known character). For My Favorites slot 1..10, one run of the data procedure
/// "crafting.scanFavorites" each:
/// <list type="number">
/// <item>open the Ability view (core OpenAbility; from slot 2 on only if the Make view is not already on top), one Esc
/// only if the phase announcement UI_LIFE_STAGE_PREVIEW is open;</item>
/// <item>select the slot: a click on the located <c>craft.fav.anchor</c> ("Recent" header) + the slot's offset, the
/// SAME offsets as crafting.loop (never a raw point; no anchor, no click);</item>
/// <item>wait for the recipe panel to update, read <c>craft.product</c> (title, owned badge) and <c>craft.pairs</c>
/// (up to 3 material tiles: icon key, owned/required);</item>
/// <item>unless the panel is blank, open and close the product's item tip ("crafting.productTip",
/// <see cref="ProductTip"/>): the name and id the game logs replace the OCR title (also for the repeat check);</item>
/// <item>unless it repeats the previous slot, open and close the item tip of every material tile whose icon is not a
/// known icon item yet (once per icon, ever; the job's 2 s pause before each press): the logged item is learned, and
/// the material is stored under its name and id.</item>
/// </list>
/// Each readable slot is stored at once (atomic save per slot): a recipe per product (materials keyed by item or icon,
/// unverified, source "screen:craft.fav/&lt;slot&gt;"; it replaces an OCR-only recipe of the same slot or materials) and
/// stock observations for the product and every material (owned), with the item ids where known. A partial read of a
/// recipe stored complete keeps the complete content but takes this slot (2026-10-18). When the scan reaches the end
/// of the favourites, every recipe that still claims a slot it was not seen in gives the claim up
/// (<see cref="ScreenRecipes.ReleaseFavorites"/>).
/// The scan stops at the first slot that selects nothing or shows the previous slot's recipe again (an empty slot
/// leaves the panel unchanged), after slot 10, or on any procedure end other than success. A fixed pause separates slots
/// (human pace, on top of the engine's input pacing). Every slot is journaled (with its tip code and item id). The Make
/// view is left open. Reading, naming and storing a slot's panel is the shared <see cref="RecipePanel"/>.
/// </summary>
internal sealed class ScanFavoritesJobHandler : IJobHandler
{
    internal const string JobName = "ScanFavorites";
    internal const string Procedure = "crafting.scanFavorites";
    internal const string OpenStep = "open";
    internal const string SlotStepPrefix = "slot.";
    internal const string ProductKey = "product";
    internal const string PairsKey = "pairs";
    internal const string ProductReader = "craft.product";
    internal const string PairsReader = "craft.pairs";
    internal const string MakeView = "UI_LIFESKILL_MAKE_VIEW";
    internal const int Slots = 10;

    /// <summary>Coverage code of the product's owned count.</summary>
    internal const string ProductCoverage = "ability:craft.product";

    /// <summary>Coverage code of the materials' owned counts.</summary>
    internal const string PairsCoverage = "ability:craft.pairs";

    /// <summary>Fixed pause between slots (human pace, no randomness).</summary>
    internal static readonly TimeSpan SlotPause = TimeSpan.FromMilliseconds(300); // was 2 s; the engine pace (40/min) still spaces the inputs

    /// <summary>Recipe provenance (Recipe.SourceCapture): read from the screen, My Favorites slot n.</summary>
    internal static string SourceOf(int slot) => "screen:craft.fav/" + slot.ToString(CultureInfo.InvariantCulture);

    /// <summary>Step id of a favourites slot ("slot.01" .. "slot.10").</summary>
    internal static string SlotStepId(int slot) => SlotStepPrefix + slot.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>The "Recent" header every favourites slot is pressed from.</summary>
    internal const string FavAnchor = "craft.fav.anchor";

    /// <summary>
    /// A run that ended on a favourites slot because the "Recent" header was not on the screen while the Make view was on
    /// top: the list was scrolled down (2026-10-31 queue failure: after cooking, the game reopened the Make view scrolled
    /// down to the dish's own section; the next goal's favourites read did not find the header and the queue failed).
    /// </summary>
    internal static bool FavoritesScrolledAway(IJobContext context, ProcedureResult run) =>
        run.Status != ProcedureStatus.Succeeded
        && run.Attempts.LastOrDefault(a => a.StepId.StartsWith(SlotStepPrefix, StringComparison.Ordinal)) is { } last
        && last.CauseStartsWith("template.notFound:" + FavAnchor)
        && context.UiState.AdvisoryTop == MakeView;

    /// <summary>
    /// Runs a procedure that presses a favourites slot; when the favourites were scrolled out of sight
    /// (<see cref="FavoritesScrolledAway"/>), drags the Make list back to its top (the list scan's top drags) and, when the
    /// list moved, runs it once more. Journals "favorites.toTop" with the outcome.
    /// </summary>
    internal static ProcedureResult RunOnFavorites(IJobContext context, ProcedureDefinition procedure, CancellationToken ct) =>
        RunOnFavorites(context, procedure, ct, out _);

    /// <summary>
    /// <see cref="RunOnFavorites(IJobContext, ProcedureDefinition, CancellationToken)"/>; <paramref name="reran"/> is true
    /// when the procedure ran a second time. A list already at its top is not run again: something else hides the header,
    /// and a second failed run would only spend another of the engine's soft ends (2026-10-31 sweep: one refresh could
    /// cost two while the goal counted one).
    /// </summary>
    internal static ProcedureResult RunOnFavorites(IJobContext context, ProcedureDefinition procedure, CancellationToken ct, out bool reran)
    {
        reran = false;
        var run = context.Procedures.Run(procedure, context.Run, ct);
        if (!FavoritesScrolledAway(context, run))
        {
            return run;
        }

        // the Make list must be what shows (2026-10-31 audit: the Gather tab and unlogged modals are the same "Make view" in
        // the log, and a drag there only spends another of the engine's soft ends)
        if (!JobKit.Seen(context.Screen.Read(ScanRecipeListJobHandler.ListProbe, ct)))
        {
            JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "favorites.toTop"), ("code", "notOnList")));
            return run;
        }

        var problem = ScanRecipeListJobHandler.ScrollToTop(context, ct, out var moved, out var failedRun);
        JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "favorites.toTop"), ("code", Codes.Safe(problem?.Code ?? (moved ? "top" : "alreadyTop")))));
        if (failedRun is { Status: ProcedureStatus.Halted or ProcedureStatus.Stopped or ProcedureStatus.Cancelled or ProcedureStatus.GameExited })
        {
            return failedRun; // a halt or a stop during the drags is the caller's to see, never an ordinary deferral
        }

        if (problem is not null || !moved)
        {
            return run;
        }

        reran = true;
        return context.Procedures.Run(procedure, context.Run, ct);
    }

    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        // Read-only: no switch and no permission gate; the store needs a known character.
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

        var tipData = context.ProcedureCatalog.Get(ProductTip.Procedure)!;
        if (!ProductTip.IsExpectedShape(tipData))
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

        var tally = new Tally();
        var panel = new RecipePanel(context, tipData, Procedure, () => Pause(context, tally, ct), ct);
        FavoriteRead? previous = null;
        var retried = false;
        // audit R2.7 (rule 2): an end signal (nothing selected, a blank panel, the previous recipe again) can be a lost
        // click, a popup or a capture mid-animation; it released every later favourite. The same slot is read once more,
        // and only a second end signal is the end of My Favorites
        bool EndAgain(int slot, string why)
        {
            if (retried)
            {
                return true;
            }

            retried = true;
            JobKit.Journal(context, JournalKind.Decision, Procedure, JobKit.Data(("event", "favorites.endRetry"), ("slot", (long)slot), ("why", why)));
            return false;
        }

        for (var slot = 1; slot <= Slots; slot++)
        {
            if (slot > 1)
            {
                Pause(context, tally, ct);
            }

            var run = RunOnFavorites(context, BuildSlot(data, slot), ct);
            tally.Add(run);
            if (run.Status != ProcedureStatus.Succeeded)
            {
                JournalSlot(context, slot, "procedure." + run.Status, null, 0, 0, false);
                return End(context, tally, JobKit.FromProcedure(run, "slot."));
            }

            var productProblem = JobKit.CheckRead(run, ProductKey, out var product);
            var pairsProblem = JobKit.CheckRead(run, PairsKey, out var pairs);
            if ((productProblem ?? pairsProblem) is { } problem)
            {
                tally.Slots = slot;
                if (slot > 1 && problem.State == JobState.Failed
                    && (product?.Status == ReadStatus.ScreenMismatch || pairs?.Status == ReadStatus.ScreenMismatch))
                {
                    // the recipe panel is gone after selecting this slot: it selected nothing
                    JournalSlot(context, slot, "screenMismatch", null, 0, 0, false);
                    if (!EndAgain(slot, "screenMismatch"))
                    {
                        slot--;
                        continue;
                    }

                    tally.End = "nothingSelected";
                    break;
                }

                JournalSlot(context, slot, "read.problem", null, 0, 0, false);
                return End(context, tally, problem);
            }

            var read = panel.Fold(product!, pairs!);
            tally.Slots = slot;
            if (read.IsBlank)
            {
                JournalSlot(context, slot, "blank", read, 0, 0, false);
                if (slot > 1 && !EndAgain(slot, "blank"))
                {
                    slot--;
                    continue;
                }

                tally.End = "nothingSelected";
                break;
            }

            // the product's item tip: the name the game logs replaces the OCR title (also for the repeat check)
            var (tip, named) = panel.NameProduct(read, skipKnown: true);
            tally.Add(tip.Run);
            if (!tip.Succeeded)
            {
                JournalSlot(context, slot, ProductTip.RunFailed, read, 0, 0, false, tip);
                return End(context, tally, JobKit.FromProcedure(tip.Run, "tip."));
            }

            tally.Count(tip);
            read = named;
            if (previous is not null && read.IsRepeatOf(previous))
            {
                // an empty slot leaves the previous recipe selected: the end of the favourites
                JournalSlot(context, slot, "repeat", read, 0, 0, false, tip);
                if (!EndAgain(slot, "repeat"))
                {
                    slot--;
                    continue;
                }

                tally.End = "repeat";
                break;
            }

            // the item tips of the materials whose icon is not known yet (each icon pressed once, ever), then the store
            var stored = panel.Store(read, SourceOf(slot), keepFavorites: false, [("slot", (long)slot)]);
            foreach (var tipRun in stored.MaterialTips.Runs)
            {
                tally.Add(tipRun);
            }

            tally.Count(stored.MaterialTips);
            if (stored.Outcome is not { } outcome)
            {
                JournalSlot(context, slot, ProductTip.RunFailed, read, 0, 0, false, tip);
                return End(context, tally, JobKit.FromProcedure(stored.MaterialTips.Failed!, "tip."));
            }

            read = stored.Read;
            tally.Count(outcome);
            if (outcome.RecipeId is { } seenId)
            {
                tally.Seen.Add(seenId);
            }
            else
            {
                tally.Unread.Add(slot);
            }

            JournalSlot(context, slot, outcome.Code, read, outcome.Observed, outcome.Materials, outcome.Changed, tip, outcome.Replaced, stored.MaterialTips.Presses);
            retried = false; // a slot read: the next end signal gets its own second look
            if (read.HasIdentity)
            {
                previous = read;
            }
        }

        if (tally.Recorded == 0 && tally.Observed == 0 && tally.Seen.Count == 0)
        {
            return End(context, tally, JobKit.Failed("favorites.nothingRead"));
        }

        // the scan reached the end of the favourites: a recipe that still claims a slot it was not seen in left My
        // Favorites (or moved): it gives the claim up, so the window and the craft job use the list for it
        tally.Released = ScreenRecipes.ReleaseFavorites(context.Knowledge!, tally.Seen, tally.Unread);
        return End(context, tally, JobKit.Result(JobState.Succeeded, "favorites.scanned"));
    }

    /// <summary>
    /// The run for one slot: every step but the other slots' clicks; from slot 2 on, the open step is skipped while the
    /// Make view is already on top (SkipIfSatisfied on its expectation).
    /// </summary>
    internal static ProcedureDefinition BuildSlot(ProcedureDefinition data, int slot)
    {
        var slotStep = SlotStepId(slot);
        var steps = new List<StepDefinition>();
        foreach (var s in data.Steps)
        {
            if (s.Id.StartsWith(SlotStepPrefix, StringComparison.Ordinal))
            {
                if (s.Id == slotStep)
                {
                    steps.Add(s);
                }

                continue;
            }

            steps.Add(s.Id == OpenStep && slot > 1 ? s with { Flags = s.Flags | StepFlags.SkipIfSatisfied } : s);
        }

        return data with { Steps = steps };
    }

    /// <summary>
    /// The data procedure must be a read-only navigation (no commit step, no gate) that opens the view, has the ten slot
    /// clicks (anchor + offset) and reads both readers after them.
    /// </summary>
    internal static bool IsExpectedShape(ProcedureDefinition p)
    {
        if (p.Kind != ProcedureKind.Navigation || p.Gate != AutomationSwitch.None
            || p.Steps.Any(s => s.Flags.HasFlag(StepFlags.Commit) || s.Action is AwaitCraftAction))
        {
            return false;
        }

        var open = p.Steps.FirstOrDefault(s => s.Id == OpenStep);
        if (open?.Action is not CallProcedureAction || open.Expect.UiEvents?.Any(u => u.UiName == MakeView) != true)
        {
            return false;
        }

        for (var slot = 1; slot <= Slots; slot++)
        {
            if (!p.Steps.Any(s => s.Id == SlotStepId(slot) && s.Action is ClickAction { Offset: not null }))
            {
                return false;
            }
        }

        var lastSlot = p.Steps.ToList().FindLastIndex(s => s.Id.StartsWith(SlotStepPrefix, StringComparison.Ordinal));
        bool ReadsAfter(string reader, string key) =>
            p.Steps.Skip(lastSlot + 1).Any(s => s.Action is ReadAction r && r.ReaderId == reader && r.OutputKey == key);
        return ReadsAfter(ProductReader, ProductKey) && ReadsAfter(PairsReader, PairsKey);
    }

    private static void Pause(IJobContext context, Tally tally, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            context.Clock.Sleep(SlotPause, ct);
        }
        catch (OperationCanceledException)
        {
            JobKit.Journal(context, JournalKind.Stock, Procedure, With(tally.Data(), ("event", "scanFavorites.end"), ("code", "cancelled")));
            throw;
        }
    }

    private static void JournalSlot(IJobContext context, int slot, string code, FavoriteRead? read, int observed, int materials, bool changed, ItemTip.Outcome? tip = null, int replaced = 0, int materialTips = 0) =>
        JobKit.Journal(context, JournalKind.Stock, Procedure, With(JobKit.Data(
            ("event", "scanFavorites.slot"),
            ("slot", (long)slot),
            ("code", code),
            ("product", read?.Product is not null),
            ("materials", (long)materials),
            ("unknownMaterials", (long)(read?.UnknownSlots ?? 0)),
            ("namedMaterials", (long)(read?.Materials.Count(m => m.ItemId is not null) ?? 0)),
            ("observed", (long)observed),
            ("recipeChanged", changed),
            ("replaced", (long)replaced),
            ("materialTips", (long)materialTips)), ItemTip.Fields(tip)));

    private static JobResult End(IJobContext context, Tally tally, JobResult result)
    {
        var data = tally.Data();
        if (result.Data is not null)
        {
            foreach (var (k, v) in result.Data)
            {
                data.TryAdd(k, v);
            }
        }

        JobKit.Journal(context, JournalKind.Stock, Procedure, With(tally.Data(), ("event", "scanFavorites.end"), ("code", result.Code)));
        return result with { Data = data };
    }

    private static Dictionary<string, object?> With(Dictionary<string, object?> data, params (string Key, object? Value)[] extra)
    {
        foreach (var (k, v) in extra)
        {
            data[k] = v;
        }

        return data;
    }

    /// <summary>Totals of one job (codes and counts only).</summary>
    private sealed class Tally
    {
        public int Slots { get; set; }

        public int Recorded { get; private set; }

        public int Changed { get; private set; }

        public int Observed { get; private set; }

        public int Unreadable { get; private set; }

        public int Tips { get; private set; }

        public int TipsMissing { get; private set; }

        public string End { get; set; } = "allSlots";

        private int Inputs { get; set; }

        private int Clicks { get; set; }

        private int Esc { get; set; }

        public void Add(ProcedureResult r)
        {
            Inputs += r.InputBatches;
            Clicks += r.Clicks;
            Esc += r.EscPresses;
        }

        public void Count(ScreenRecipes.StoreOutcome s)
        {
            Recorded += s.Recorded ? 1 : 0;
            Changed += s.Changed ? 1 : 0;
            Observed += s.Observed;
            Unreadable += s.Code is "unreadable" or "noMaterials" ? 1 : 0;
            Replaced += s.Replaced;
        }

        public void Count(ItemTip.Outcome t)
        {
            Tips += t.Tip is not null ? 1 : 0;
            TipsMissing += t.Tip is null ? 1 : 0;
        }

        public void Count(ProductTip.MaterialTips m)
        {
            MaterialTips += m.Presses;
            IconsLearned += m.Learned;
        }

        public int MaterialTips { get; private set; }

        public int IconsLearned { get; private set; }

        public int Replaced { get; private set; }

        /// <summary>The recipe ids this scan saw in a slot (written or kept).</summary>
        public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);

        /// <summary>Slots whose recipe could not be named (their old claims stay).</summary>
        public HashSet<int> Unread { get; } = [];

        /// <summary>Recipes that gave up a favourites slot they were no longer seen in.</summary>
        public int Released { get; set; }

        public Dictionary<string, object?> Data() => JobKit.Data(
            ("recipesReleased", (long)Released),
            ("slots", (long)Slots),
            ("recipes", (long)Recorded),
            ("recipesChanged", (long)Changed),
            ("recipesReplaced", (long)Replaced),
            ("observed", (long)Observed),
            ("unreadable", (long)Unreadable),
            ("tips", (long)Tips),
            ("tipsMissing", (long)TipsMissing),
            ("materialTips", (long)MaterialTips),
            ("iconsLearned", (long)IconsLearned),
            ("end", End),
            ("inputs", (long)Inputs),
            ("clicks", (long)Clicks),
            ("esc", (long)Esc));
    }
}

/// <summary>One material slot of a read (null fields = not read with confidence).</summary>
/// <param name="Slot">1-based tile index.</param>
/// <param name="IconKey">Resolved icon key (the tile's fingerprint), or null when the tile could not be identified.</param>
/// <param name="Owned">Owned count.</param>
/// <param name="Required">Required per craft.</param>
/// <param name="Confidence">Lowest confidence of the icon and the pair.</param>
/// <param name="Item">The key the material is stored under: the item's name key once the game logged the item behind
/// this icon (an icon item of the store, 2026-10-15), else the icon key; null without an icon key.</param>
/// <param name="ItemId">The game's item id, when the icon's item is known.</param>
/// <param name="TipOffset">The tile's centre relative to the "Materials Required" header anchor (where its item tip is
/// pressed), or null when the read gave no tile rect or no anchor.</param>
internal sealed record FavoriteMaterial(
    int Slot,
    string? IconKey,
    long? Owned,
    long? Required,
    double Confidence,
    ItemKey? Item = null,
    long? ItemId = null,
    PointI? TipOffset = null);

/// <summary>An 'a/b' read with confidence (the "Requires" line, the counter under the product picture).</summary>
/// <param name="A">First number.</param>
/// <param name="B">Second number.</param>
internal sealed record ReadPair(long A, long B);

/// <summary>The recipe panel as read for one favourites slot (craft.product + craft.pairs).</summary>
/// <param name="Product">Product key (from the title), or null when the title was not read.</param>
/// <param name="ProductOwned">Owned count of the product.</param>
/// <param name="ProductOwnedConfidence">Its confidence.</param>
/// <param name="ProductAt">Log position of the product capture.</param>
/// <param name="ProductUtc">Product capture time.</param>
/// <param name="Materials">Present material tiles (empty slots excluded).</param>
/// <param name="UnknownSlots">Tiles or pairs that could not be read.</param>
/// <param name="PairsAt">Log position of the pairs capture.</param>
/// <param name="PairsUtc">Pairs capture time.</param>
/// <param name="Requires">The "Requires: owned/required" line (2026-10-14), when shown and read.</param>
/// <param name="RequiresUnknown">The line is shown but could not be read.</param>
/// <param name="Counter">The small 'a/b' counter under the product picture (2026-10-14), when shown and read.</param>
/// <param name="ProductItemId">The game's item id of the product, when its item tip was logged (the name then comes
/// from the same log line, see <see cref="ProductTip.Apply"/>).</param>
internal sealed record FavoriteRead(
    ItemKey? Product,
    long? ProductOwned,
    double ProductOwnedConfidence,
    LogPosition? ProductAt,
    DateTimeOffset ProductUtc,
    IReadOnlyList<FavoriteMaterial> Materials,
    int UnknownSlots,
    LogPosition? PairsAt,
    DateTimeOffset PairsUtc,
    ReadPair? Requires = null,
    bool RequiresUnknown = false,
    ReadPair? Counter = null,
    long? ProductItemId = null)
{
    /// <summary>Title read, every present material fully read, and the "Requires" line read when it is shown.</summary>
    public bool IsComplete => Product is not null && UnknownSlots == 0 && Materials.Count > 0 && !RequiresUnknown
        && Materials.All(m => m.IconKey is not null && m.Required is not null && m.Owned is not null);

    /// <summary>Nothing selected: no title and no material tile at all.</summary>
    public bool IsBlank => Product is null && Materials.Count == 0 && UnknownSlots == 0;

    /// <summary>Enough was read to compare it with another slot.</summary>
    public bool HasIdentity => Product is not null || (Materials.Count > 0 && UnknownSlots == 0 && Materials.All(m => m.IconKey is not null && m.Required is not null));

    /// <summary>
    /// The same recipe as <paramref name="previous"/>: the same product name, or (a title unread) the same fully read
    /// materials (icon + required, in order).
    /// </summary>
    public bool IsRepeatOf(FavoriteRead previous)
    {
        if (Product is { } a && previous.Product is { } b)
        {
            return a == b;
        }

        return HasIdentity && previous.HasIdentity && Materials.Count == previous.Materials.Count
            && Materials.Zip(previous.Materials).All(p =>
                p.First.IconKey is not null && p.First.IconKey == p.Second.IconKey && p.First.Required == p.Second.Required);
    }

    /// <summary>
    /// The same read with every material whose icon is a known icon item stored under that item (name key and id);
    /// the others keep their icon key.
    /// </summary>
    public FavoriteRead WithIconItems(IReadOnlyList<IconItem> iconItems)
    {
        ArgumentNullException.ThrowIfNull(iconItems);
        return this with
        {
            Materials = [.. Materials.Select(m => m.IconKey is { } key && iconItems.FirstOrDefault(i => string.Equals(i.IconKey, key, StringComparison.Ordinal)) is { } icon
                ? m with { Item = icon.Item, ItemId = icon.ItemId }
                : m)],
        };
    }

    /// <summary>
    /// Folds the two reads (only Ok values; region ids icon.k, pair.k, product.name, product.owned, and since 2026-10-14
    /// materials (the detected row: Unknown counts as an unread material), requires and product.counter (optional:
    /// absent = Ok without a value)). Icon keys are resolved against <paramref name="knownIconKeys"/> so the same material
    /// keeps its key; a material whose icon is one of <paramref name="iconItems"/> is stored under that item. Each tile's
    /// centre is kept relative to the "Materials Required" header anchor (for pressing its item tip).
    /// </summary>
    public static FavoriteRead From(ScreenReadResult product, ScreenReadResult pairs, IReadOnlyList<string> knownIconKeys, IReadOnlyList<IconItem>? iconItems = null)
    {
        var name = ReadFold.OkValue(product, ReadFold.ProductNameRegion);
        ItemKey? productKey = name?.Text is { Length: > 0 } t ? ItemKey.FromDisplayName(t) : null;
        if (productKey is { } pk && string.IsNullOrEmpty(pk.Value))
        {
            productKey = null;
        }

        var owned = ReadFold.OkValue(product, ReadFold.ProductOwnedRegion);
        var materials = new List<FavoriteMaterial>();
        var unknown = 0;
        if (pairs.Values.FirstOrDefault(v => v.RegionId == MaterialsRegion) is { Status: not ReadStatus.Ok })
        {
            unknown++; // the row was there but not consistent: never a shorter list taken as complete
        }

        var requiresValue = pairs.Values.FirstOrDefault(v => v.RegionId == RequiresRegion);
        ReadPair? requires = requiresValue is { Status: ReadStatus.Ok, Value: { } ra, Second: { } rb } && ra >= 0 && rb >= 1 ? new ReadPair(ra, rb) : null;
        var requiresUnknown = requiresValue is { Status: not ReadStatus.Ok };
        var counterValue = ReadFold.OkValue(product, CounterRegion);
        ReadPair? counter = counterValue is { Value: { } ca, Second: { } cb } && ca >= 0 && cb >= 1 ? new ReadPair(ca, cb) : null;
        var slots = pairs.Values
            .Where(v => v.RegionId.StartsWith("icon.", StringComparison.Ordinal))
            .Select(v => (Index: int.TryParse(v.RegionId.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var k) ? k : 0, Icon: v))
            .Where(x => x.Index > 0)
            .OrderBy(x => x.Index);
        foreach (var (k, icon) in slots)
        {
            if (icon.Status == ReadStatus.Ok && icon.Text is null)
            {
                continue; // an empty slot: fewer materials
            }

            var pair = ReadFold.OkValue(pairs, "pair." + k.ToString(CultureInfo.InvariantCulture));
            string? key = icon.Status == ReadStatus.Ok && IconKeys.IsIconKey(icon.Text) ? IconKeys.Resolve(icon.Text!, knownIconKeys) : null;
            long? ownedCount = pair?.Value is >= 0 ? pair.Value : null;
            long? required = pair?.Second is >= 1 ? pair.Second : null;
            if (key is null || ownedCount is null || required is null)
            {
                unknown++;
            }

            PointI? offset = icon.Rect is { } tile && pairs.Anchors is { } anchors && anchors.TryGetValue(ProductTip.AnchorTemplate, out var header)
                ? new PointI(tile.Center.X - header.X, tile.Center.Y - header.Y)
                : null;
            materials.Add(new FavoriteMaterial(k, key, ownedCount, required, Math.Min(icon.Confidence, pair?.Confidence ?? 0), key is null ? null : new ItemKey(key), null, offset));
        }

        var read = new FavoriteRead(
            productKey,
            owned?.Value is >= 0 ? owned.Value : null,
            owned?.Confidence ?? 0,
            product.LogAt,
            product.CapturedUtc,
            materials,
            unknown,
            pairs.LogAt,
            pairs.CapturedUtc,
            requires,
            requiresUnknown,
            counter);
        return iconItems is null ? read : read.WithIconItems(iconItems);
    }

    /// <summary>Region id of the detected materials row (craft.pairs, 2026-10-14).</summary>
    internal const string MaterialsRegion = "materials";

    /// <summary>Region id of the "Requires: owned/required" line (craft.pairs, optional).</summary>
    internal const string RequiresRegion = "requires";

    /// <summary>Region id of the counter under the product picture (craft.product, optional).</summary>
    internal const string CounterRegion = "product.counter";
}
