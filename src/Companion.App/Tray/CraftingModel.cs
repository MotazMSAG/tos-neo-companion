using System.Globalization;
using System.Text;
using Companion.App.Hosting;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Host;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;

namespace Companion.App.Tray;

/// <summary>One material line of a recipe as the Crafting window shows it.</summary>
/// <param name="Index">1-based position in the recipe.</param>
/// <param name="Label">The material's name (logged by the game's item tip, 2026-10-15), or "Material 1".."Material 4"
/// while it is known only by its icon (the Make tab shows no names).</param>
/// <param name="Required">Required per craft.</param>
/// <param name="Owned">Last observed owned count (null = not seen, never 0).</param>
/// <param name="OwnedRange">Owned range when only a range is known.</param>
/// <param name="ObservedUtc">When the owned count was observed.</param>
/// <param name="Stale">True when the count may have changed since (user played, craft, rotation).</param>
internal sealed record CraftMaterialRow(int Index, string Label, int Required, long? Owned, ValueRange? OwnedRange, DateTimeOffset? ObservedUtc, bool Stale);

/// <summary>A recipe as the Crafting window lists it (mapped read-only from the character's knowledge).</summary>
/// <param name="RecipeId">Recipe id; empty for the "recipe selected in the game" entry.</param>
/// <param name="Title">Product name for display.</param>
/// <param name="Slot">My Favorites slot 1..10 from the recipe's source, 0 = unknown (keep the recipe selected in game).</param>
/// <param name="SlotOutdated">
/// The latest favourites scan did not confirm this slot: another recipe was seen there later, or the latest scan did not
/// see this recipe there (it stopped earlier or kept an older read). Start is disabled until the next scan.
/// </param>
/// <param name="Partial">The recipe was read only partially.</param>
/// <param name="ProductOwned">Last observed owned count of the product.</param>
/// <param name="ProductRange">Owned range of the product when only a range is known.</param>
/// <param name="ProductObservedUtc">When the product count was observed.</param>
/// <param name="ProductStale">True when the product count may have changed since.</param>
/// <param name="Materials">Materials in recipe order.</param>
internal sealed record CraftRecipeRow(
    string RecipeId,
    string Title,
    int Slot,
    bool SlotOutdated,
    bool Partial,
    long? ProductOwned,
    ValueRange? ProductRange,
    DateTimeOffset? ProductObservedUtc,
    bool ProductStale,
    IReadOnlyList<CraftMaterialRow> Materials)
{
    /// <summary>The first list entry: craft whatever recipe is selected in the game (slot 0, no favourites click).</summary>
    public static CraftRecipeRow SelectedInGame { get; } = new(string.Empty, "Recipe selected in the game", 0, false, false, null, null, null, false, []);

    /// <summary>True for the "recipe selected in the game" entry.</summary>
    public bool IsSelectedInGame => RecipeId.Length == 0;

    /// <summary>When the recipe was read in its favourites slot (the scan's time); null without a slot.</summary>
    public DateTimeOffset? SlotSeenUtc { get; init; }

    /// <summary>Another recipe was seen in the same favourites slot later (one reason for <see cref="SlotOutdated"/>).</summary>
    public bool SlotTakenLater { get; init; }

    /// <summary>
    /// All stock was marked stale after the scan that saw this slot (the user played or the companion crafted since), so
    /// the favourites may have changed. A warning only: Start stays available.
    /// </summary>
    public bool PlayedSinceScan { get; init; }

    /// <summary>
    /// The game's item id of the product (logged by its item tip, 2026-10-15), or null. Sent with CraftLoop for a
    /// favourites slot, so the job refuses when a later scan found another product in that slot.
    /// </summary>
    public long? ProductItemId { get; init; }

    /// <summary>The product's item key (the Craft job's "item"), or empty.</summary>
    public string ProductKey { get; init; } = string.Empty;

    /// <summary>
    /// A known recipe goes through the Craft job (2026-10-15, 1.0: "ask for N, get N"): it plans the needed sub-recipes,
    /// gathers the missing raw materials, finds the card (favourite slot or the Make list) and crafts in typed batches.
    /// Only "until materials run out" on a favourite keeps the direct CraftLoop.
    /// </summary>
    public bool ViaPlan => !IsSelectedInGame && ProductKey.Length > 0;
}

/// <summary>Which controls of the Crafting window are enabled.</summary>
/// <param name="Start">Start (CraftLoop).</param>
/// <param name="Stop">Stop (cancel the job).</param>
/// <param name="Scan">Scan favourites (ScanFavorites) and Learn selected recipe (LearnRecipe): the read-only jobs, no selection needed.</param>
/// <param name="Count">The count box (off while "until materials run out" is ticked).</param>
internal readonly record struct CraftingButtons(bool Start, bool Stop, bool Scan, bool Count);

/// <summary>
/// The Crafting window's pure rules: recipe rows from knowledge records, CraftLoop / ScanFavorites requests, button
/// states and display texts. No I/O; every request still goes through the job channel's whole validation.
/// </summary>
internal static class CraftingRules
{
    /// <summary>The Make loop job (feature "crafting").</summary>
    public const string CraftLoopJob = "CraftLoop";

    /// <summary>The planned craft job (feature "crafting"): sub-recipes first, a recipe found in the Make list.</summary>
    public const string CraftJob = "Craft";

    /// <summary>Highest quantity of the Craft job.</summary>
    public const int MaxPlanQty = 999;

    /// <summary>The read-only favourites scan job (feature "crafting").</summary>
    public const string ScanFavoritesJob = "ScanFavorites";

    /// <summary>The read-only scan of the WHOLE Make list (feature "crafting"; "Scan all recipes", 2026-10-18).</summary>
    public const string ScanListJob = "ScanRecipeList";

    /// <summary>The read-only job that reads the recipe selected in the game's Make view (feature "crafting", 2026-10-15).</summary>
    public const string LearnRecipeJob = "LearnRecipe";

    /// <summary>Recipe source written by ScanFavorites: "screen:craft.fav/&lt;slot&gt;".</summary>
    public const string FavoritesSourcePrefix = "screen:craft.fav/";

    /// <summary>Highest favourites slot (CraftLoop's slot bound).</summary>
    public const int MaxSlot = 10;

    /// <summary>Highest count (CraftLoop's count bound; 0 = until the materials run out).</summary>
    public const int MaxCount = 100_000;

    /// <summary>Batch "1": one item per Make.</summary>
    public const string BatchOne = "1";

    /// <summary>Batch "max": the max-quantity button before Make.</summary>
    public const string BatchMax = "max";

    /// <summary>
    /// Batch "typed" (2026-10-16): the count left is typed on the game's keypad before each Make, so one press makes up
    /// to a whole batch (the game caps a session at 50 and clamps to what the materials allow).
    /// </summary>
    public const string BatchTyped = "typed";

    private const string IconPrefix = "icon:";

    /// <summary>The favourites slot of a recipe source ("screen:craft.fav/3" = 3); 0 when unknown or out of range.</summary>
    public static int ParseSlot(string? sourceCapture)
    {
        if (sourceCapture is null || !sourceCapture.StartsWith(FavoritesSourcePrefix, StringComparison.Ordinal))
        {
            return 0;
        }

        var digits = sourceCapture.AsSpan(FavoritesSourcePrefix.Length);
        return digits.Length is >= 1 and <= 2
            && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var slot)
            && slot is >= 1 and <= MaxSlot
            ? slot
            : 0;
    }

    /// <summary>
    /// Longest gap between two consecutive slots of one favourites scan: a slot may wait up to the engine's 10 min
    /// patience for a safe moment to click, plus pacing. A longer gap is taken as the end of an earlier scan.
    /// </summary>
    public static readonly TimeSpan ScanSlotGap = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maps the character's recipes and stock to rows: slot from the source, product and material owned counts from
    /// the stock (keyed by name, or by "icon:&lt;hex&gt;" while a material is known only by its icon), partial recipes
    /// flagged. A material whose icon's item the game logged (<paramref name="iconItems"/>) shows that item's name and
    /// owned count. A favourites slot counts only when the latest scan saw the recipe there (<see cref="LatestScan"/>);
    /// every other favourites row is marked outdated (Start disabled until the next scan). Order: by slot, then by name.
    /// </summary>
    public static IReadOnlyList<CraftRecipeRow> Map(IReadOnlyList<Recipe> recipes, StockView? stock, IReadOnlyList<IconItem>? iconItems = null)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        var items = stock?.Items ?? new Dictionary<ItemKey, StockEntry>();
        var icons = (iconItems ?? []).GroupBy(i => i.IconKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var favourites = recipes
            .Select(r => (Recipe: r, Slot: ParseSlot(r.SourceCapture)))
            .Where(x => x.Slot > 0)
            .ToList();
        var owners = favourites
            .GroupBy(x => x.Slot)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.Recipe.ObservedAtUtc).ThenBy(x => x.Recipe.Id, StringComparer.Ordinal).First().Recipe);
        var confirmed = LatestScan(favourites);

        var rows = new List<CraftRecipeRow>(recipes.Count);
        foreach (var r in recipes)
        {
            var slot = ParseSlot(r.SourceCapture);
            var outdated = slot > 0 && !confirmed.Contains(r);
            var takenLater = slot > 0 && !ReferenceEquals(owners[slot], r);
            var playedSince = slot > 0 && stock?.StaleSince is { } staleSince && staleSince > r.ObservedAtUtc;
            items.TryGetValue(r.Product, out var product);
            var materials = new List<CraftMaterialRow>(r.Materials.Count);
            for (var i = 0; i < r.Materials.Count; i++)
            {
                var m = r.Materials[i];
                var item = m.Item.Value is { } key && icons.TryGetValue(key, out var icon) ? icon.Item : m.Item;
                if (!items.TryGetValue(item, out var owned))
                {
                    items.TryGetValue(m.Item, out owned);
                }

                materials.Add(new CraftMaterialRow(i + 1, MaterialLabel(item, i + 1), m.Qty, owned?.Count, owned?.Range, owned?.ObservedAtUtc, owned?.Stale ?? false));
            }

            rows.Add(new CraftRecipeRow(
                r.Id,
                DisplayName(r.Product),
                slot,
                outdated,
                r.Confidence == RecipeConfidence.Partial,
                product?.Count,
                product?.Range,
                product?.ObservedAtUtc,
                product?.Stale ?? false,
                materials)
            {
                SlotSeenUtc = slot > 0 ? r.ObservedAtUtc : null,
                SlotTakenLater = takenLater,
                PlayedSinceScan = playedSince,
                ProductItemId = r.ProductItemId,
                ProductKey = r.Product.Value ?? string.Empty,
            });
        }

        return rows
            .OrderBy(r => r.Slot > 0 && !r.SlotOutdated ? r.Slot : MaxSlot + 1)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.RecipeId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The favourites recipes the latest scan saw in their slots. A scan reads slot 1, 2, 3... in order and stamps each
    /// recipe it records with its read time, so going back in time from the newest favourites recipe, the slots of
    /// that scan strictly decrease and follow each other within <see cref="ScanSlotGap"/>. The walk stops at the first
    /// recipe that breaks either rule: it belongs to an earlier scan, and so does everything older. That earlier
    /// mapping is unconfirmed: the latest scan stopped before that slot ('repeat' or an empty slot after the user
    /// removed or reordered favourites), or kept an older complete read of a recipe that may have moved.
    /// </summary>
    private static HashSet<Recipe> LatestScan(IReadOnlyList<(Recipe Recipe, int Slot)> favourites)
    {
        var latest = new HashSet<Recipe>(ReferenceEqualityComparer.Instance);
        var previousSlot = MaxSlot + 1;
        DateTimeOffset? previousAt = null;
        foreach (var (recipe, slot) in favourites
            .OrderByDescending(x => x.Recipe.ObservedAtUtc)
            .ThenByDescending(x => x.Slot)
            .ThenBy(x => x.Recipe.Id, StringComparer.Ordinal))
        {
            if (slot >= previousSlot || (previousAt is { } at && at - recipe.ObservedAtUtc > ScanSlotGap))
            {
                break;
            }

            latest.Add(recipe);
            previousSlot = slot;
            previousAt = recipe.ObservedAtUtc;
        }

        return latest;
    }

    /// <summary>CraftLoop parameters: slot (0 = keep the recipe selected in game), count (0 = until the materials run out), batch.</summary>
    public static IReadOnlyDictionary<string, string> CraftLoopParams(int slot, bool untilMaterialsRunOut, int count, bool batchMax) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["slot"] = slot.ToString(CultureInfo.InvariantCulture),
            ["count"] = (untilMaterialsRunOut ? 0 : count).ToString(CultureInfo.InvariantCulture),
            ["batch"] = batchMax ? BatchMax : BatchTyped,
        };

    /// <summary>
    /// The CraftLoop submission for a recipe row; a favourites row with a known product item id also sends it ("item"),
    /// so the job refuses when the slot now holds another product.
    /// </summary>
    public static PipeRequest CraftLoopRequest(string jobId, CraftRecipeRow recipe, bool untilMaterialsRunOut, int count, bool batchMax)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var parameters = new Dictionary<string, string>(CraftLoopParams(recipe.Slot, untilMaterialsRunOut, count, batchMax), StringComparer.Ordinal);
        if (recipe.Slot > 0 && recipe.ProductItemId is { } id and > 0)
        {
            parameters["item"] = id.ToString(CultureInfo.InvariantCulture);
        }

        return new PipeRequest(PipeOp.Submit, jobId, CraftLoopJob, parameters);
    }

    /// <summary>The Craft submission for a recipe row that is not a favourite (<see cref="CraftRecipeRow.ViaPlan"/>).</summary>
    public static PipeRequest CraftRequest(string jobId, CraftRecipeRow recipe, int count)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["item"] = recipe.ProductKey,
            ["qty"] = count.ToString(CultureInfo.InvariantCulture),
        };
        return new PipeRequest(PipeOp.Submit, jobId, CraftJob, parameters);
    }

    /// <summary>The ScanFavorites submission (no parameters).</summary>
    public static PipeRequest ScanRequest(string jobId) =>
        new(PipeOp.Submit, jobId, ScanFavoritesJob);

    /// <summary>The ScanRecipeList submission (no parameters: the whole list).</summary>
    public static PipeRequest ScanAllRequest(string jobId) =>
        new(PipeOp.Submit, jobId, ScanListJob);

    /// <summary>The LearnRecipe submission (no parameters).</summary>
    public static PipeRequest LearnRequest(string jobId) =>
        new(PipeOp.Submit, jobId, LearnRecipeJob);

    /// <summary>
    /// Button rules: nothing starts while any job runs or this window's job is still queued or running; Start needs a
    /// recipe that is not slot-outdated and a count of 1..<see cref="MaxCount"/> (or "until materials run out").
    /// Stop is available while a job runs.
    /// </summary>
    public static CraftingButtons Buttons(bool hostJobRunning, bool ownJobActive, CraftRecipeRow? selected, bool untilMaterialsRunOut, int count)
    {
        var busy = hostJobRunning || ownJobActive;
        var countOk = selected is { ViaPlan: true }
            ? (untilMaterialsRunOut ? selected.Slot > 0 : count is >= 0 and <= MaxPlanQty) // 0 = forever (2026-10-16)
            : untilMaterialsRunOut || count is >= 1 and <= MaxCount;
        // an outdated favourite still starts with a count: the Craft job finds it in the Make list when its old slot holds
        // another recipe (fallback, 2026-10-18: the user saw Clam Chowder as "uncraftable"); only "until materials run
        // out" needs a confirmed slot
        var startable = selected is { SlotOutdated: false } || (selected is { ViaPlan: true } && !untilMaterialsRunOut);
        return new CraftingButtons(
            Start: !busy && startable && countOk,
            Stop: busy,
            Scan: !busy,
            Count: !untilMaterialsRunOut);
    }

    /// <summary>True when a job state will not change any more.</summary>
    public static bool IsTerminal(JobState state) => state is not (JobState.Queued or JobState.Running or JobState.Parked);

    /// <summary>List text of a row.</summary>
    public static string ListText(CraftRecipeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsSelectedInGame)
        {
            return "(" + row.Title + ")";
        }

        var text = new StringBuilder();
        if (row.Slot > 0)
        {
            text.Append("Favourite ").Append(row.Slot.ToString(CultureInfo.InvariantCulture)).Append(": ");
        }

        text.Append(row.Title);
        if (row.Partial)
        {
            text.Append(" (partial read)");
        }

        if (row.SlotOutdated)
        {
            text.Append(" (slot outdated, scan again)");
        }

        return text.ToString();
    }

    /// <summary>
    /// Details of a row: slot (with the age of the scan that saw it, or why it is outdated), partial note, product owned
    /// count, materials with required and owned counts.
    /// </summary>
    public static string Details(CraftRecipeRow row, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsSelectedInGame)
        {
            return "Crafts the recipe that is selected in the game's Make view right now (no favourites click).";
        }

        var lines = new List<string>();
        var slot = row.Slot.ToString(CultureInfo.InvariantCulture);
        var seen = row.SlotSeenUtc is { } at ? AgeText(at, nowUtc) : "earlier";
        var fallback = row.ViaPlan
            ? "Start still works with a count: the companion looks for this recipe in the Make list. 'Scan favourites' makes it a quick favourite again."
            : "Run 'Scan favourites' again before crafting this one, or select it in the game yourself and use '(Recipe selected in the game)'.";
        if (row.Slot == 0)
        {
            lines.Add(row.ViaPlan
                ? "Not a favourite: the companion finds this recipe in the Make list (a count of 1-"
                    + MaxPlanQty.ToString(CultureInfo.InvariantCulture) + ", or 0 = forever; not 'until materials run out')."
                : "No favourites slot known: the companion crafts the recipe selected in the game.");
        }
        else if (row.SlotTakenLater)
        {
            lines.Add("Another recipe was seen in favourites slot " + slot + " later. " + fallback);
        }
        else if (row.SlotOutdated)
        {
            lines.Add("Favourites slot " + slot + " is not confirmed: the last 'Scan favourites' did not see this recipe there (it was seen there "
                + seen + "). Your favourites may have changed. " + fallback);
        }
        else
        {
            lines.Add("Favourites slot " + slot + ", as of the scan " + seen + ": the companion selects it before each Make.");
            if (row.PlayedSinceScan)
            {
                lines.Add("Since that scan you used the game or the companion crafted. If you changed your My Favorites in the game, run 'Scan favourites' again first.");
            }
        }

        if (row.ViaPlan)
        {
            lines.Add("Start makes the count you ask for: sub-recipes first, missing raw materials gathered in the Gathering Helper, then crafted in batches. Count 0 = forever (until Stop, or until a material that cannot be gathered runs out).");
        }

        if (row.Partial)
        {
            lines.Add("Partial read: some fields could not be read with confidence.");
        }

        lines.Add(row.Title + ": " + OwnedText(row.ProductOwned, row.ProductRange, row.ProductObservedUtc, row.ProductStale, nowUtc));
        foreach (var m in row.Materials)
        {
            lines.Add(m.Label + ": needs " + m.Required.ToString(CultureInfo.InvariantCulture) + " per craft, "
                + OwnedText(m.Owned, m.OwnedRange, m.ObservedUtc, m.Stale, nowUtc));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>"owned 40 (seen 5 min ago)", "owned 40 (seen 2 h ago, may have changed)" or "owned: not seen yet".</summary>
    public static string OwnedText(long? count, ValueRange? range, DateTimeOffset? observedUtc, bool stale, DateTimeOffset nowUtc)
    {
        var value = count?.ToString(CultureInfo.InvariantCulture)
            ?? (range is { } r ? r.Min.ToString(CultureInfo.InvariantCulture) + ".." + r.Max.ToString(CultureInfo.InvariantCulture) : null);
        if (value is null)
        {
            return "owned: not seen yet";
        }

        var seen = observedUtc is { } at ? "seen " + AgeText(at, nowUtc) : "seen earlier";
        return "owned " + value + " (" + seen + (stale ? ", may have changed since" : string.Empty) + ")";
    }

    /// <summary>Plain age of an observation: "just now", "5 min ago", "3 h ago", "2 days ago".</summary>
    public static string AgeText(DateTimeOffset atUtc, DateTimeOffset nowUtc)
    {
        var age = nowUtc - atUtc;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return ((int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min ago";
        }

        if (age < TimeSpan.FromHours(48))
        {
            return ((int)age.TotalHours).ToString(CultureInfo.InvariantCulture) + " h ago";
        }

        return ((int)age.TotalDays).ToString(CultureInfo.InvariantCulture) + " days ago";
    }

    /// <summary>Display name of an item key ("white toast" = "White Toast").</summary>
    public static string DisplayName(ItemKey item)
    {
        var value = item.Value ?? string.Empty;
        if (value.Length == 0)
        {
            return "(no name)";
        }

        return value.StartsWith(IconPrefix, StringComparison.Ordinal) ? "(item known by its icon)" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value);
    }

    private static string MaterialLabel(ItemKey item, int index) =>
        item.Value is null || item.Value.Length == 0 || item.Value.StartsWith(IconPrefix, StringComparison.Ordinal)
            ? "Material " + index.ToString(CultureInfo.InvariantCulture)
            : DisplayName(item);
}

/// <summary>
/// Plain-language texts for the job channel's codes (submission refusals, job ends, cancel answers). Every text ends
/// with the code in brackets so a report can quote it.
/// </summary>
/// <summary>The crafting window's health colour.</summary>
internal enum HealthLevel
{
    Grey,
    Green,
    Amber,
    Red,
}

internal static class CraftingMessages
{
    /// <summary>
    /// What the user must do once a job is accepted: a slide click needs a valid cover (SPEC 7.5), the foreground
    /// window with the pointer resting on it, and never the game or a companion window. While this window is in front
    /// or under the pointer, no click can be sent.
    /// </summary>
    public const string CoverGuidance =
        "Now click another window that is not the game (for example your browser) and leave the mouse resting on it. "
        + "The companion cannot click while this window is in front or under the mouse. "
        + "Using the game pauses it: it never presses Make while you play, and goes on about a minute after you leave the game.";

    /// <summary>Shown while this window's job is active and this window is in front or under the pointer.</summary>
    public const string CoverHint =
        "Waiting for you: the companion cannot click the game while this window is in front or under the mouse. "
        + "Click another window that is not the game (for example your browser) and leave the mouse resting on it.";

    private const string TrayMenu = "The switches are in the tray menu.";

    /// <summary>Text for a submission answer (an accepted job also gets <see cref="CoverGuidance"/>).</summary>
    public static string ForSubmit(string jobName, PipeResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!response.Ok)
        {
            return ForRefusal(response.Code);
        }

        return (jobName switch
        {
            CraftingRules.ScanFavoritesJob => "Accepted: the companion reads your My Favorites recipes (read-only, nothing is crafted). ",
            CraftingRules.LearnRecipeJob => "Accepted: the companion reads the recipe selected in the game's Make tab (read-only: only its item tips are pressed, nothing is crafted). ",
            _ => "Accepted: crafting starts as soon as the companion is free. ",
        }) + CoverGuidance;
    }

    /// <summary>
    /// Plain words for the engine's wait reason (a <see cref="StepOutcome"/> name or "paused"), followed by the code
    /// in brackets; an unknown reason is shown as it is.
    /// </summary>
    /// <summary>
    /// The window's one-line health (2026-10-16, the user: "state clearly in green if everything is going smoothly"):
    /// green while a job works or waits for a normal moment (a click chance, a craft, human pace), amber when it needs
    /// the user (a display, auto-collect, a window or a popup to deal with, paused, parked), red when stopped by a HALT or
    /// the companion is not reachable, grey when idle.
    /// </summary>
    public static (HealthLevel Level, string Text) Health(HostStatus? status, bool jobRunning, string? jobActivity = null, int? autoCollect = null)
    {
        if (status is null)
        {
            return (HealthLevel.Red, "STOPPED: the companion is not reachable (start Companion.App).");
        }

        if (status.Halt is { } halt)
        {
            return (HealthLevel.Red, "STOPPED FOR SAFETY: " + HostMessages.HaltText(halt.Code, halt.Subject) + " Check the game, then press Continue.");
        }

        if (status.State == HostState.Untrusted)
        {
            return (HealthLevel.Red, "NOT READY: this version is not trusted yet (Settings tab: Trust this build).");
        }

        if (status.State == HostState.Stopped)
        {
            return (HealthLevel.Red, "STOPPED: you pressed Stop. Nothing runs until you press Allow again.");
        }

        if (status.State == HostState.Paused)
        {
            return (HealthLevel.Amber, "PAUSED by you. Press Resume to go on.");
        }

        if (!status.GameConnected)
        {
            return (HealthLevel.Amber, "THE GAME IS NOT RUNNING. Start Tree of Savior.");
        }

        var engine = status.Engine;
        if (!jobRunning && engine is not { State: EngineState.Running or EngineState.Waiting or EngineState.Parked })
        {
            return (HealthLevel.Grey, "Ready. Nothing is running right now.");
        }

        var activity = Activity(engine?.ProcedureId, engine?.StepId);
        if (engine?.State == EngineState.Parked)
        {
            return (HealthLevel.Amber, "NEEDS YOU: parked (" + WaitText(engine.WaitingFor) + ").");
        }

        var wait = engine?.WaitingFor;
        if (wait == nameof(StepOutcome.AutoCollectNotIdle) && autoCollect == 1)
        {
            // incident 18: a gathering trip is out (status 1, known): the companion waits for it, nothing for the user to do
            var doingNow = jobActivity is { Length: > 0 } j ? j : activity;
            return (HealthLevel.Green, "OK, " + doingNow + " (waiting: the gathering trip that is out comes back first; nothing to do).");
        }

        string? needsUser = wait switch
        {
            nameof(StepOutcome.DisplayModeWrong) => "the game window is minimized or not maximized: restore it (click it in the taskbar), then click another window",
            nameof(StepOutcome.AutoCollectNotIdle) => "the Gathering Helper is in a state the companion does not know: open it in the game and press Finish or Stop",
            nameof(StepOutcome.ForeignBlockingUi) => "a shop, exchange or invite window was opened that the companion must not touch: close it; if it is already closed, press Esc once in the game with no window open",
            nameof(StepOutcome.UserUiOpen) => "a game window is open that the companion must not close: close it yourself",
            nameof(StepOutcome.SessionUnavailable) => "Windows is locked or shows another desktop",
            "paused" => "paused (tray menu: Resume)",
            _ => null,
        };
        if (needsUser is not null)
        {
            return (HealthLevel.Amber, "NEEDS YOU: " + needsUser + ".");
        }

        // the job's own words first (what it is doing overall, and the wait between engine steps), then the engine step
        // (2026-10-23, the user: "gather everything doesn't do anything clear": between steps the card said only
        // "between steps (planning, or waiting for a gathering trip to finish)")
        var doing = jobActivity is { Length: > 0 } job
            ? string.IsNullOrEmpty(engine?.ProcedureId) ? job : job + "; now " + activity
            : activity;
        return wait is { Length: > 0 }
            ? (HealthLevel.Green, "OK, " + doing + " (waiting: " + ShortWait(wait) + ").")
            : (HealthLevel.Green, "OK, " + doing + ".");
    }

    /// <summary>
    /// The running job's own report (JobActivityBoard values in the job info) in plain words, or null. Slot "job": the
    /// job's progress; slot "step": the current wait. Unknown codes give nothing (never a raw code in the sentence).
    /// </summary>
    internal static string? JobActivityText(IReadOnlyDictionary<string, object?>? data, DateTimeOffset nowUtc)
    {
        if (data is null)
        {
            return null;
        }

        string? Str(string key) => data.TryGetValue(key, out var v) && v is not null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        long? Num(string key) => data.TryGetValue(key, out var v) && v is long n ? n : data.TryGetValue(key, out var w) && w is int i ? i : null;
        string Since(string slot) => data.TryGetValue("activity." + slot + ".sinceUtc", out var v) && v is DateTimeOffset at
            ? " for " + Math.Max(0, (int)(nowUtc - at).TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min"
            : string.Empty;

        var parts = new List<string>();
        switch (Str("activity.job"))
        {
            case "gatherAll":
                var trip = Num("activity.job.trip") ?? 1;
                var left = Num("activity.job.left") ?? 0;
                var materials = left.ToString(CultureInfo.InvariantCulture) + " material" + (left == 1 ? string.Empty : "s");
                if (Str("activity.job.phase") == "survey")
                {
                    parts.Add("Gather everything: reading how full each of " + materials + " is (the emptiest are gathered first)");
                    break;
                }

                parts.Add("Gather everything, trip " + trip.ToString(CultureInfo.InvariantCulture)
                    + (Str("activity.job.phase") == "refill" ? " (topping up what is not full yet)" : string.Empty)
                    + (Str("activity.job.next") is { Length: > 0 } next ? " (emptiest first: " + next + ")" : string.Empty)
                    + ", " + materials + " still to fill");
                break;
        }

        string At(string key) => data.TryGetValue(key, out var v) && v is DateTimeOffset t ? t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "soon";
        if (Str("activity.job") == "plan")
        {
            var made = Num("activity.job.made") ?? 0;
            var qty = Num("activity.job.qty") ?? 0;
            parts.Add("Goal: " + made.ToString(CultureInfo.InvariantCulture) + (qty > 0 ? " of " + qty.ToString(CultureInfo.InvariantCulture) : " (forever)") + " " + (Str("activity.job.item") ?? "items"));
        }

        switch (Str("activity.step"))
        {
            case "plan.pause":
            case "gatherAll.pause":
                parts.Add("paused (" + CauseText(Str("activity.step.cause")) + "), going on at " + At("activity.step.untilUtc"));
                break;
            case "plan.idle":
                parts.Add("nothing could be made this round (no materials?), trying again at " + At("activity.step.untilUtc"));
                break;
            case "keepStock.wait":
                parts.Add("rule mode: next check at " + At("activity.step.untilUtc") + " (sooner when the character eats)");
                break;
            case "gather.trip":
                parts.Add("the character is out gathering " + (Str("activity.step.items") ?? "the queued materials") + Since("step"));
                break;
            case "gather.walk":
                parts.Add("choosing materials in the Gathering Helper");
                break;
        }

        return parts.Count == 0 ? null : string.Join(": ", parts);
    }

    /// <summary>What the companion is doing right now, from the running procedure and step (plain words).</summary>
    internal static string Activity(string? procedure, string? step)
    {
        var st = step ?? string.Empty;
        bool Is(params string[] prefixes) => prefixes.Any(x => st.StartsWith(x, StringComparison.Ordinal) || st.Contains("/" + x, StringComparison.Ordinal));
        return procedure switch
        {
            null or "" => "between steps (planning, or waiting for a gathering trip to finish)",
            "OpenAbility" => "opening the Ability window",
            "crafting.gather" when Is("begin") => "gathering: starting the trip",
            "crafting.gather" when Is("finish") => "gathering: collecting the finished trip",
            "crafting.gather" when Is("tile", "set.", "qty", "key.") => "gathering: queuing materials in the Helper",
            "crafting.gather" when Is("top", "scroll") => "gathering: scrolling the Helper list",
            "crafting.gather" => "gathering: opening the Gathering Helper",
            "crafting.gatherTip" => "gathering: reading a material",
            "crafting.productTip" => "checking the selected recipe",
            "crafting.qty" => "crafting: typing the amount",
            "crafting.loop" when Is("make") => "crafting: pressing Make",
            "crafting.loop" when Is("await") => "crafting: making the batch",
            "crafting.loop" when Is("slot") => "crafting: selecting the recipe",
            "crafting.loop" => "crafting: getting the Make tab ready",
            var p when p.Contains("list", StringComparison.Ordinal) => "finding the recipe in the list",
            var p when p.Contains("fav", StringComparison.OrdinalIgnoreCase) => "reading your favourites",
            var p when p.Contains("stock", StringComparison.OrdinalIgnoreCase) => "reading your stock",
            var p => "working (" + p + ")",
        };
    }

    /// <summary>A short plain reason for a normal wait (the long one is in the status line below).</summary>
    private static string ShortWait(string wait) => wait switch
    {
        nameof(StepOutcome.UserActive) => "you are using the game; click another window",
        nameof(StepOutcome.NotSent) => "for the mouse to rest over another window",
        nameof(StepOutcome.CraftInProgress) => "a craft is running",
        nameof(StepOutcome.RateLimited) or nameof(StepOutcome.Throttled) => "human pace",
        nameof(StepOutcome.Busy) => "the game is busy",
        _ => "a safe moment",
    };

    public static string WaitText(string? waitingFor)
    {
        var code = waitingFor ?? "unknown";
        string? text = code switch
        {
            nameof(StepOutcome.NotSent) => "no safe moment to click yet: another window (not the game, not this one) must be in front with the mouse resting on it, on the game's monitor",
            nameof(StepOutcome.UserActive) => "you are using the game, or used it a few seconds ago",
            nameof(StepOutcome.Busy) => "the game is loading or busy",
            nameof(StepOutcome.CombatRecent) => "there was combat a moment ago",
            nameof(StepOutcome.RateLimited) or nameof(StepOutcome.Throttled) => "keeping a human pace",
            nameof(StepOutcome.AutoCollectNotIdle) => "auto-collect is on in the game",
            nameof(StepOutcome.DisplayModeWrong) => "the game window is not in the expected display mode (windowed and maximized)",
            nameof(StepOutcome.ForeignBlockingUi) => "a shop, exchange or invite window was opened that the companion must not touch (close it; if it is already closed, press Esc once in the game with no window open)",
            nameof(StepOutcome.UserUiOpen) => "one of your game windows is open (close it yourself)",
            nameof(StepOutcome.CraftInProgress) => "a craft is running in the game",
            nameof(StepOutcome.LogQuiet) => "the game log has not changed for a few seconds",
            nameof(StepOutcome.SessionUnavailable) => "Windows is locked or shows another desktop",
            nameof(StepOutcome.ClockJump) => "the clock jumped (sleep or resume), settling",
            nameof(StepOutcome.AbortedAfterFocus) => "the last click was called off, trying again shortly",
            nameof(StepOutcome.FocusFailedOnce) => "the game did not take the last click, trying again shortly",
            nameof(StepOutcome.PreconditionTransient) => "waiting for the game screen to settle",
            nameof(StepOutcome.Popup) => "a popup appeared in the game",
            nameof(StepOutcome.WindowIncidentRestored) => "the game window was put back, pausing a moment",
            "paused" => "paused (tray menu: Resume)",
            _ => null,
        };

        return text is null ? code : text + " [" + code + "]";
    }

    /// <summary>Text for a refusal code (from the job channel or from the job itself).</summary>
    public static string ForRefusal(string? code)
    {
        var c = code ?? "unknown";
        var inner = StripRefusedPrefix(StripRoundPrefix(c));
        string text;
        if (inner == "refused.switch" || inner.StartsWith("switch.off", StringComparison.Ordinal))
        {
            text = "Refused: crafting automation is switched off. Turn it on in the tray menu: Automation > Crafting. " + TrayMenu;
        }
        else if (inner == "slideClick.off")
        {
            text = "Refused: slide clicks are switched off. Turn them on in the tray menu: Automation > Slide clicks. " + TrayMenu;
        }
        else if (inner is "refused.halted" or "halt.latched")
        {
            text = "Refused: the companion is halted. Check the game, then choose 'Clear HALT...' in the tray menu.";
        }
        else if (inner is "refused.stopped" or "stop")
        {
            text = "Refused: the companion is stopped. Choose 'Clear stop...' in the tray menu first.";
        }
        else if (inner == "commit.userActivity")
        {
            text = "Stopped before Make: you used the game since the job started. Leave the game alone while it crafts and start again.";
        }
        else if (inner == "refused.untrusted")
        {
            text = "Refused: this companion build is not trusted yet. Choose 'Trust this build...' in the tray menu.";
        }
        else if (inner == "refused.starting")
        {
            text = "The companion is still starting. Try again in a moment.";
        }
        else if (inner == "refused.whitelist")
        {
            text = "Refused: this job is not available (its feature is disabled). See Features in the tray menu.";
        }
        else if (inner == "refused.rate")
        {
            text = "Not now: a lot of jobs were started in the last hour (a safety limit against runaway loops). Try again in a few minutes.";
        }
        else if (inner == "refused.rate.job")
        {
            text = "Not now: this job was started many times in the last hour (a safety limit against runaway loops). Try again in a few minutes.";
        }
        else if (inner == "refused.queueFull")
        {
            text = "Refused: another job is already waiting. Wait until it has finished.";
        }
        else if (inner == "knowledge.unavailable")
        {
            text = "Refused: the companion's knowledge file could not be opened. Restart the companion; if this repeats, the journal has the details.";
        }
        else if (inner == "craft.inProgress")
        {
            text = "Refused: a craft is already running in the game. Wait until it has finished.";
        }
        else if (inner.StartsWith("busy:", StringComparison.Ordinal))
        {
            text = "Refused: the game is busy (revive or match). Try again later.";
        }
        else if (inner == "slotItem.changed")
        {
            text = "Refused: the last 'Scan favourites' found another recipe in this favourites slot. The list was reloaded; check it and try again.";
        }
        else if (inner.StartsWith("needsSurvey", StringComparison.Ordinal))
        {
            text = "Not possible yet: a game screen this job needs has not been surveyed.";
        }
        else if (inner.StartsWith("refused.params", StringComparison.Ordinal) || inner.StartsWith("param.", StringComparison.Ordinal)
            || inner.StartsWith("bad.", StringComparison.Ordinal))
        {
            text = "Refused: a setting was not accepted.";
        }
        else
        {
            text = "Refused.";
        }

        return text + " [" + c + "]";
    }

    /// <summary>Text for a finished job (state and end code, with its counts where the job reports them).</summary>
    public static string ForEnd(JobStatusInfo job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var code = job.Code ?? "unknown";
        var total = Number(job, "total");
        var rounds = Number(job, "rounds");
        var made = total is { } t ? " Made " + t.ToString(CultureInfo.InvariantCulture) + " item" + (t == 1 ? string.Empty : "s")
            + (rounds is { } r ? " in " + r.ToString(CultureInfo.InvariantCulture) + " round" + (r == 1 ? string.Empty : "s") : string.Empty) + "." : string.Empty;
        string text;
        switch (job.State)
        {
            case JobState.Succeeded:
                text = code switch
                {
                    "loop.countReached" => "Done: the count was reached." + made,
                    "loop.materialsOut" => "Done: the materials ran out." + made,
                    "favorites.scanned" => "Scan finished: " + (Number(job, "recipes") ?? 0).ToString(CultureInfo.InvariantCulture)
                        + " recipe(s) recorded from your favourites.",
                    "recipe.learned" => "Learned the recipe selected in the game (" + (Number(job, "materials") ?? 0).ToString(CultureInfo.InvariantCulture)
                        + " material(s)).",
                    "recipe.kept" => "The selected recipe was already stored (complete, or as a favourite); its owned counts were updated.",
                    "plan.done" => "Done: everything in the plan was made." + Made(job),
                    "plan.nothingToDo" => "Nothing to do: the plan needs no craft.",
                    _ => "Finished.",
                };
                break;
            case JobState.Failed:
                text = code switch
                {
                    "loop.noCraft" => "No craft started: probably not enough materials, or the recipe could not be selected. Nothing was made.",
                    "loop.materialsOut" => "Stopped early: the materials ran out before the count was reached." + made,
                    "favorites.nothingRead" => "The scan could not read any recipe from your favourites.",
                    "make.notOnTop" or "make.panelNotShown" => "Nothing was pressed: open the Ability view's Make tab in the game and select the recipe there first.",
                    "make.nothingSelected" => "Nothing was pressed: no recipe is selected in the game's Make tab.",
                    "recipe.unreadable" => "The selected recipe could not be read completely; nothing was recorded for it.",
                    "plan.partial" => "Partly done." + Made(job) + PlanNotes(job),
                    "plan.shortfall" => "Nothing was crafted: materials are missing." + PlanNotes(job),
                    "plan.needsVerification" => "Nothing was crafted: a recipe in the plan is only partly read. Run 'Scan favourites' or 'Learn selected recipe' for it.",
                    "plan.blocked" => "Nothing was crafted: the plan is blocked." + PlanNotes(job),
                    "plan.deferred" => "Stopped after pausing several times: the last steps could not be done (you were using the game, or the mouse rested where the companion cannot reach it). Start the goal again; it continues from your stock." + Made(job) + PlanNotes(job),
                    _ when code.Contains("deferred", StringComparison.Ordinal) => DeferredText(code) + made,
                    "InternalError" => "Something went wrong inside the companion. Details are in the journal.",
                    _ => "The job did not finish." + made,
                };
                break;
            case JobState.Refused:
                return ForRefusal(code);
            case JobState.Halted:
                text = (Enum.TryParse<HaltCode>(code, ignoreCase: false, out var halt) && Enum.IsDefined(halt)
                        ? HostMessages.HaltText(halt, null)
                        : "The companion halted.")
                    + " After checking the game, choose 'Clear HALT...' in the tray menu.";
                break;
            case JobState.Cancelled:
                // Only a client cancel is "your request": the engine also ends rounds itself (game used during a
                // commit job, park expired, Make press not confirmed), reported as round./slot.cancelled (no detail).
                text = code switch
                {
                    "cancelled.byClient" or "cancelled.queued" => "Stopped at your request.",
                    "cancelled.ruleModeStall" => "Ended by rule mode's watchdog: no progress for 20 minutes. Rule mode starts it again.",
                    var c when c.Contains("cancelled:park.expired", StringComparison.Ordinal) => "The companion ended the job: it waited 4 hours for the game to be ready.",
                    var c when c.Contains("cancelled:commit.uncertain", StringComparison.Ordinal) || c.Contains("cancelled:commit.unverified", StringComparison.Ordinal)
                        => "The companion ended the job: it could not confirm a Make press, so it did not press again.",
                    var c when c.Contains("cancelled:commit.noEffect", StringComparison.Ordinal) => "The companion ended the job: a Make press had no effect (materials may be missing).",
                    "cancelled" => "The companion ended the job itself. Details are in the journal.",
                    "host.exit" => "Stopped: the companion is closing.",
                    "round.cancelled" => "The companion ended the job itself (you used the game in the last minute or while it worked, "
                        + "it waited too long for a safe moment to click, or it could not confirm the Make press). Details are in the journal.",
                    "slot.cancelled" => "The companion ended the scan itself (it waited too long for a safe moment to click, "
                        + "or it could not go on safely). Details are in the journal.",
                    _ => "The job was cancelled.",
                } + made;
                break;
            case JobState.Stopped:
                text = "Stopped by 'Stop now'. Choose 'Clear stop...' in the tray menu to continue." + made;
                break;
            case JobState.NeedsSurvey:
                text = "Not possible yet: a game screen this job needs has not been surveyed.";
                break;
            case JobState.GameExited:
                text = "Stopped: the game closed or restarted." + made;
                break;
            case JobState.Parked:
                text = "The job is parked: it waits until the game is ready again (see the tray notification).";
                break;
            default:
                text = "The job is " + job.State.ToString().ToLowerInvariant() + ".";
                break;
        }

        return text + " [" + code + "]";
    }

    /// <summary>Text for the answer to a cancel request.</summary>
    public static string ForCancel(string? code) => code switch
    {
        "cancel.requested" => "Stop requested: the job ends at its next safe point.",
        "cancelled" => "Cancelled before it started.",
        "alreadyFinished" => "The job had already finished.",
        "notFound" => "That job is not known any more.",
        _ => "The stop request was not accepted. Use 'Stop now' in the tray menu (Ctrl+Alt+Pause).",
    } + " [" + (code ?? "unknown") + "]";

    private static string StripRoundPrefix(string code) =>
        code.StartsWith("round.", StringComparison.Ordinal) ? code["round.".Length..]
        : code.StartsWith("slot.", StringComparison.Ordinal) ? code["slot.".Length..]
        : code;

    /// <summary>A refusal inside a round or slot comes as "refused:&lt;engine detail&gt;" (JobKit.FromProcedure).</summary>
    private static string StripRefusedPrefix(string code) =>
        code.StartsWith("refused:", StringComparison.Ordinal) ? code["refused:".Length..] : code;

    private static string Made(JobStatusInfo job) =>
        Number(job, "made") is { } m ? " Made " + m.ToString(CultureInfo.InvariantCulture) + " item" + (m == 1 ? string.Empty : "s") + "." : string.Empty;

    /// <summary>
    /// A deferral's cause code in a few plain words (audit R3.2: every pause read "you were using the game, or it could not
    /// be reached", whatever the cause): the cause is journaled with each pause since Release 1.
    /// </summary>
    internal static string CauseText(string? cause) => cause switch
    {
        null or "" or "?" => "it could not be done just now",
        var c when c.Contains("stillGathering", StringComparison.Ordinal) => "the character is still out gathering; the companion comes back for it",
        var c when c.Contains("userActive", StringComparison.Ordinal) || c.Contains("commit.user", StringComparison.Ordinal) || c.Contains("game.foreground", StringComparison.Ordinal) => "you were using the game",
        var c when c.Contains("unreachable", StringComparison.Ordinal) => "the mouse rested where the game cannot be reached",
        var c when c.Contains("keyboardLayout", StringComparison.Ordinal) => "the keyboard layout changed",
        var c when c.Contains("park.repeated", StringComparison.Ordinal) => "a wait repeated without anything changing",
        var c when c.Contains("reset.", StringComparison.Ordinal) => "a game window would not close",
        var c when c.Contains("aborted", StringComparison.Ordinal) => "clicks kept being called off (the mouse moved)",
        var c when c.Contains("exhausted", StringComparison.Ordinal) => "a step kept failing",
        var c when c.Contains("makeTab", StringComparison.Ordinal) || c.Contains("stock", StringComparison.Ordinal) => "the Make tab could not be read",
        var c when c.Contains("engineEnded", StringComparison.Ordinal) => "the companion ended a round it could not confirm",
        _ => "it could not be done just now",
    };

    /// <summary>Why a job part was deferred (FALLBACKS.md), from the reason its code carries.</summary>
    internal static string DeferredText(string code) =>
        code.Contains("commit.user", StringComparison.Ordinal)
            ? "Not done now: you were using the game during a Make step (the companion never presses Make while you play). Start it again when you are done."
            : code.Contains("unreachable", StringComparison.Ordinal)
                ? "Not done now: the game could not be reached from where the mouse rested (another monitor, or no window under it). Try again with the mouse resting on the game's monitor."
                : "Not done now: a step kept failing, so it was stopped without a halt. Details are in the journal.";

    private static string PlanNotes(JobStatusInfo job)
    {
        var notes = new StringBuilder();
        foreach (var (key, label) in new[]
        {
            ("gathered", "Gathered"), ("missing", "Still missing"), ("gameSaysMissing", "The game said missing"), ("notGatherable", "Not gatherable now (not in the Gathering Helper, or greyed at its stock limit)"),
            ("notSeen", "Not shown by the Gathering Helper now (greyed when full, or not offered here)"),
            ("atStockLimit", "At its stock limit"), ("limitedBy", "The goal is limited by"), ("gatheringBlocked", "Gathering failed twice the same way, so this goal crafted from your stock only (check your gathering tool's durability, or the Gathering Helper)"), ("deferred", "Paused and tried again (" + CauseText(job.Data is { } jd && jd.TryGetValue("deferredWhy", out var why) ? why as string : null) + ")"), ("notFound", "Not found in the Make list"), ("endedEarly", "Ended early"),
        })
        {
            if (job.Data is { } data && data.TryGetValue(key, out var v) && v is string text && text.Length > 0)
            {
                notes.Append(' ').Append(label).Append(": ").Append(text).Append('.');
            }
        }

        return notes.ToString();
    }

    private static long? Number(JobStatusInfo job, string key)
    {
        if (job.Data is not { } data || !data.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            long l => l,
            int i => i,
            _ => null,
        };
    }
}

/// <summary>
/// Progress of a running CraftLoop, estimated from the game log's craft sessions (the job reports its own totals only
/// when it ends): every craft session that started after the job was accepted counts as one round, its ticks as items.
/// </summary>
internal sealed class CraftProgress
{
    private readonly Dictionary<LogPosition, int> _sessions = [];
    private LogPosition? _baseline;
    private bool _started;

    /// <summary>True once <see cref="Begin"/> was called.</summary>
    public bool Started => _started;

    /// <summary>Craft sessions seen since <see cref="Begin"/>.</summary>
    public int Crafts => _sessions.Count;

    /// <summary>Items made in those sessions (ticks).</summary>
    public long Items => _sessions.Values.Sum(v => (long)v);

    /// <summary>Starts counting; the session current at this moment (an earlier craft) is not counted.</summary>
    public void Begin(CraftSessionInfo? current)
    {
        _sessions.Clear();
        _baseline = current?.StartPosition;
        _started = true;
    }

    /// <summary>Folds the latest craft session.</summary>
    public void Observe(CraftSessionInfo? session)
    {
        if (!_started || session?.StartPosition is not { } start || start == _baseline)
        {
            return;
        }

        _sessions[start] = Math.Max(_sessions.GetValueOrDefault(start), session.Ticks);
    }
}
