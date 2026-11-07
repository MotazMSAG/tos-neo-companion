using System.Collections.ObjectModel;
using System.Globalization;
using Companion.Contracts;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Knowledge.Storage;

namespace Companion.Knowledge;

/// <summary>
/// One character's knowledge (SPEC 11.2). Stock, recipes and keep-stock rules live in ONE file
/// (RuntimeDir\knowledge\&lt;key&gt;\knowledge.json) so <see cref="StockView.LastAppliedOffset"/> is persisted in the same
/// atomic write as the stock (STK-02). Changes stay in memory until <see cref="Save"/>.
/// <para>Merge rules for observations (unseen items stay unknown, never 0):</para>
/// <list type="number">
/// <item>Invalid observations (negative, empty, inconsistent count/range, no log position) are ignored, never guessed.</item>
/// <item>An observation older than the current entry is ignored. Recency = log offset in the same log generation,
/// else the newer log generation (file creation time), else the capture time.</item>
/// <item>At the same position: UserEntered &gt; Observed &gt; Derived, then exact over range, then higher confidence;
/// a full tie keeps the current entry.</item>
/// <item>Drift: a newer range-only reading that still contains the current fresh exact count keeps that count
/// (with the new range); a reading outside it replaces it.</item>
/// <item>An observation captured at or before the latest AllStock stale mark is stored as stale.</item>
/// <item>A volatile observation (2026-10-15, shared homestead goods) keeps its fresh window on the entry
/// (<see cref="StockEntry.FreshUntilUtc"/>); the window belongs to the reading that set the count, so a newer reading
/// without one clears it.</item>
/// </list>
/// <para>Derived deltas (craft ticks) apply only when the item is known, its observation (or last applied delta) is
/// in the same log generation and before the event, and the event is at or after <see cref="StockView.LastAppliedOffset"/>
/// (one craft event carries several item deltas at the same offset; the per-item check stops double application).
/// A delta that cannot be ordered against the observation (other log generation) marks the item stale instead.</para>
/// <para>Item identity (schema 2, <see cref="ItemIdentity"/>): an observation that carries the game's item id is the same
/// item as an entry with that id under another key (the entry moves to the new key); an entry under the same key with
/// another id is another item (replaced). A recipe with a product item id replaces any other recipe with that id.
/// Learning an icon's item (<see cref="LearnIconItem"/>) moves the icon-keyed stock and recipe materials to the item.</para>
/// Thread-safety: the engine thread is the only intended caller; members lock anyway so a tray read is safe.
/// </summary>
internal sealed class CharacterKnowledge : ICharacterKnowledge
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly AtomicFile _files;
    private readonly IClock _clock;
    private readonly string? _gameDir;
    private readonly Dictionary<ItemKey, StockEntry> _items = [];
    private readonly SortedDictionary<string, Recipe> _recipes = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, IconItem> _iconItems = new(StringComparer.Ordinal);
    private List<KeepStockRule> _keepStock = [];
    private DateTimeOffset? _staleSince;
    private long _lastAppliedOffset;
    private LogFileIdentity? _logFile;
    private bool _dirty;
    private bool _moveAsideInvalidMain;

    internal CharacterKnowledge(
        CharacterKey character,
        string path,
        AtomicFile files,
        IClock clock,
        string? gameDir,
        CharacterFileDto? loaded,
        bool repairOnSave,
        bool mainInvalid)
    {
        Character = character;
        _path = path;
        _files = files;
        _clock = clock;
        _gameDir = gameDir;
        _dirty = repairOnSave;
        _moveAsideInvalidMain = mainInvalid;
        if (loaded is not null)
        {
            foreach (var e in loaded.Stock!.Items!)
            {
                _items[e.Item] = e;
            }
            _staleSince = loaded.Stock.StaleSince;
            _lastAppliedOffset = loaded.Stock.LastAppliedOffset;
            _logFile = loaded.Stock.LogFile;
            foreach (var r in loaded.Recipes!)
            {
                _recipes[r.Id] = r;
            }
            _keepStock = [.. loaded.KeepStock!];
            foreach (var i in loaded.IconItems ?? [])
            {
                _iconItems[i.IconKey] = i;
            }
        }
    }

    /// <inheritdoc />
    public CharacterKey Character { get; }

    /// <summary>File path (tests).</summary>
    internal string FilePath => _path;

    /// <summary>Unsaved changes pending.</summary>
    internal bool IsDirty
    {
        get
        {
            lock (_gate)
            {
                return _dirty;
            }
        }
    }

    /// <inheritdoc />
    public StockView GetStock()
    {
        lock (_gate)
        {
            return new StockView(
                Character,
                new ReadOnlyDictionary<ItemKey, StockEntry>(new Dictionary<ItemKey, StockEntry>(_items)),
                _staleSince,
                _lastAppliedOffset,
                _logFile);
        }
    }

    /// <inheritdoc />
    public void ApplyObservations(IReadOnlyList<StockObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        lock (_gate)
        {
            foreach (var o in observations)
            {
                if (KnowledgeSchema.ValidateObservation(o) is not null)
                {
                    continue;
                }
                var candidate = new StockEntry(
                    o.Item,
                    o.Count,
                    o.Range,
                    o.Kind == ObservationKind.UserEntered ? StockSource.UserEntered : StockSource.Observed,
                    o.AtUtc,
                    o.AtLog,
                    Math.Clamp(o.Confidence, 0.0, 1.0),
                    o.Coverage,
                    Stale: _staleSince is { } since && o.AtUtc <= since,
                    o.ItemId,
                    o.FreshUntilUtc);

                // identity: the same key with another known id is another item (no recency contest); the same id under
                // another key is this item (renamed, or keyed by its icon before): it moves to this key
                var current = _items.TryGetValue(o.Item, out var byKey) && ItemIdentity.Same(byKey.Item, byKey.ItemId, o.Item, o.ItemId) ? byKey : null;
                var sameId = o.ItemId is { } id ? _items.Values.FirstOrDefault(e => e.ItemId == id && e.Item != o.Item) : null;
                current ??= sameId;
                if (current is not null)
                {
                    var recency = CompareRecency(candidate, current);
                    if (recency < 0 || (recency == 0 && Precedence(candidate, current) <= 0))
                    {
                        continue;
                    }
                    if (candidate.Count is null && candidate.Range is { } range && current.Count is long exact && !current.Stale
                        && exact >= range.Min && exact <= range.Max)
                    {
                        candidate = candidate with { Count = exact };
                    }
                    if (candidate.ItemId is null && current.ItemId is { } known)
                    {
                        candidate = candidate with { ItemId = known };
                    }
                }
                if (sameId is not null)
                {
                    _items.Remove(sameId.Item);
                }
                _items[o.Item] = candidate;
                _dirty = true;
            }
        }
    }

    /// <inheritdoc />
    public bool ApplyDerived(DerivedStockDelta delta)
    {
        if (delta is null || !KnowledgeSchema.IsValidItem(delta.Item) || delta.EventPosition?.File is null
            || delta.EventPosition.Offset < 0 || !KnowledgeSchema.IsBoundedText(delta.CauseCode, allowEmpty: false))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_items.TryGetValue(delta.Item, out var current))
            {
                return false; // unseen items stay unknown
            }

            var pos = delta.EventPosition;
            if (current.ObservedAtLog is not { } observed || !observed.SameFile(pos))
            {
                if (current.ObservedAtLog is { } other && other.File.CreationUtc > pos.File.CreationUtc)
                {
                    return false; // event from an older log generation: the observation already supersedes it
                }
                // Cannot order the event against the observation (log rotated / no position): the count may be off.
                if (!current.Stale)
                {
                    _items[delta.Item] = current with { Stale = true };
                    _dirty = true;
                }
                return false;
            }

            if (pos.Offset <= observed.Offset)
            {
                return false; // already included in the observation (or this delta was applied before)
            }

            var sameGeneration = _logFile is null || _logFile == pos.File;
            if (_logFile is not null)
            {
                if (sameGeneration && pos.Offset < _lastAppliedOffset)
                {
                    return false; // replay of an event older than the watermark
                }
                if (!sameGeneration && _logFile.CreationUtc > pos.File.CreationUtc)
                {
                    return false; // older log generation
                }
            }

            var stale = current.Stale;
            long? count = null;
            if (current.Count is long c)
            {
                var n = c + delta.Delta;
                if (n < 0)
                {
                    n = 0;
                    stale = true; // drift: the stock we knew was wrong
                }
                count = n;
            }
            ValueRange? range = null;
            if (current.Range is { } r)
            {
                var min = r.Min + delta.Delta;
                var max = r.Max + delta.Delta;
                if (max < 0)
                {
                    stale = true;
                }
                range = new ValueRange(Math.Max(0, min), Math.Max(0, max));
                if (count is long k && (k < range.Min || k > range.Max))
                {
                    range = null;
                }
            }

            _items[delta.Item] = current with
            {
                Count = count,
                Range = range,
                Source = StockSource.Derived,
                ObservedAtLog = pos,
                Stale = stale,
            };
            _lastAppliedOffset = sameGeneration ? Math.Max(_lastAppliedOffset, pos.Offset) : pos.Offset;
            _logFile = pos.File;
            _dirty = true;
            return true;
        }
    }

    /// <inheritdoc />
    public void MarkStale(StaleScope scope, ItemKey? item, string reasonCode, DateTimeOffset atUtc)
    {
        if (!KnowledgeSchema.IsBoundedText(reasonCode, allowEmpty: false))
        {
            throw new ArgumentException("knowledge.stale.reasonCode", nameof(reasonCode));
        }
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }
        if (item is { } k && !KnowledgeSchema.IsValidItem(k))
        {
            throw new ArgumentException("knowledge.stale.item", nameof(item));
        }
        if (scope == StaleScope.Item && item is null)
        {
            throw new ArgumentException("knowledge.stale.itemRequired", nameof(item));
        }

        lock (_gate)
        {
            if (scope == StaleScope.AllStock && (_staleSince is null || atUtc > _staleSince))
            {
                _staleSince = atUtc;
                _dirty = true;
            }

            // Food: the autoeat line names no item, so without one every entry is treated as possibly eaten.
            IEnumerable<ItemKey> targets = item is { } one && scope != StaleScope.AllStock
                ? [one]
                : [.. _items.Keys];
            foreach (var key in targets)
            {
                if (_items.TryGetValue(key, out var e) && !e.Stale && e.ObservedAtUtc <= atUtc)
                {
                    _items[key] = e with { Stale = true };
                    _dirty = true;
                }
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Recipe> GetRecipes()
    {
        lock (_gate)
        {
            return [.. _recipes.Values];
        }
    }

    /// <inheritdoc />
    public void UpsertRecipe(Recipe recipe)
    {
        var error = KnowledgeSchema.ValidateRecipeShape(recipe) ?? KnowledgeSchema.ValidateRecipeProvenance(recipe, _gameDir);
        if (error is not null)
        {
            throw new ArgumentException("knowledge." + error, nameof(recipe));
        }
        var copy = recipe with { Materials = [.. recipe.Materials], Requirements = [.. recipe.Requirements] };
        lock (_gate)
        {
            if (copy.ProductItemId is { } id)
            {
                // the same product item under another id (renamed, or first read with another name): replaced
                foreach (var other in _recipes.Values.Where(r => r.ProductItemId == id && r.Id != copy.Id).ToList())
                {
                    _recipes.Remove(other.Id);
                }
            }
            _recipes[copy.Id] = _recipes.TryGetValue(copy.Id, out var current) ? MergeRecipe(current, copy) : copy;
            _dirty = true;
        }
    }

    /// <inheritdoc />
    public bool RemoveRecipe(string recipeId)
    {
        if (recipeId is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_recipes.Remove(recipeId))
            {
                return false;
            }
            _dirty = true;
            return true;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IconItem> GetIconItems()
    {
        lock (_gate)
        {
            return [.. _iconItems.Values];
        }
    }

    /// <inheritdoc />
    public void LearnIconItem(IconItem item)
    {
        var error = KnowledgeSchema.ValidateIconItem(item);
        if (error is not null)
        {
            throw new ArgumentException("knowledge." + error, nameof(item));
        }

        lock (_gate)
        {
            _iconItems[item.IconKey] = item;
            _dirty = true;
            var icon = new ItemKey(item.IconKey);
            var target = item.Item;

            // stock: the icon-keyed entry becomes the item's (the more recent of the two readings wins)
            if (_items.Remove(icon, out var byIcon))
            {
                var moved = byIcon with { Item = target, ItemId = item.ItemId };
                if (_items.TryGetValue(target, out var named) && ItemIdentity.Same(named.Item, named.ItemId, target, item.ItemId))
                {
                    var recency = CompareRecency(moved, named);
                    if (recency < 0 || (recency == 0 && Precedence(moved, named) <= 0))
                    {
                        moved = named with { ItemId = item.ItemId };
                    }
                }
                _items[target] = moved;
            }
            else if (_items.TryGetValue(target, out var named) && named.ItemId is null)
            {
                _items[target] = named with { ItemId = item.ItemId };
            }

            // recipes: the icon-keyed material becomes the item (a line already naming the item keeps its quantity)
            foreach (var r in _recipes.Values.ToList())
            {
                if (!r.Materials.Any(m => m.Item == icon || (m.Item == target && m.ItemId is null)))
                {
                    continue;
                }
                var materials = new List<MaterialRequirement>();
                foreach (var m in r.Materials)
                {
                    var line = m.Item == icon || m.Item == target ? m with { Item = target, ItemId = item.ItemId } : m;
                    if (!materials.Any(x => ItemIdentity.Same(x, line)))
                    {
                        materials.Add(line);
                    }
                }
                var updated = r with { Materials = materials };
                if (KnowledgeSchema.ValidateRecipeShape(updated) is null)
                {
                    _recipes[r.Id] = updated;
                }
            }
        }
    }

    /// <inheritdoc />
    public ForgetRecipesReport ForgetRecipes(IReadOnlyCollection<string> coverages)
    {
        ArgumentNullException.ThrowIfNull(coverages);
        var forget = new HashSet<string>(coverages.Where(c => c is not null), StringComparer.Ordinal);
        lock (_gate)
        {
            // 1. the backup: the whole store as it is now (a validated atomic write); a failure throws, nothing removed
            var stamp = _clock.UtcNow.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            var backup = $"{_path}.forget-{stamp}";
            for (var n = 2; File.Exists(backup) && n < 100; n++)
            {
                backup = $"{_path}.forget-{stamp}-{n.ToString(CultureInfo.InvariantCulture)}";
            }
            _files.Write(backup, KnowledgeSchema.Serialize(ToFile()), b => KnowledgeSchema.ParseCharacter(b, Character, _gameDir).Error, moveAsideInvalidMain: false);

            // 2. forget the recipes and the owned counts read from the recipe screens
            var recipes = _recipes.Count;
            _recipes.Clear();
            var stock = _items.Values.Where(e => forget.Contains(e.Coverage)).Select(e => e.Item).ToList();
            foreach (var key in stock)
            {
                _items.Remove(key);
            }
            _dirty = true;
            Save();
            return new ForgetRecipesReport(Path.GetFileName(backup), recipes, stock.Count);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<KeepStockRule> GetKeepStockRules()
    {
        lock (_gate)
        {
            return [.. _keepStock];
        }
    }

    /// <inheritdoc />
    public void SetKeepStockRules(IReadOnlyList<KeepStockRule> rules)
    {
        var error = KnowledgeSchema.ValidateKeepStock(rules);
        if (error is not null)
        {
            throw new ArgumentException("knowledge." + error, nameof(rules));
        }
        lock (_gate)
        {
            _keepStock = [.. rules];
            _dirty = true;
        }
    }

    /// <inheritdoc />
    public void Save()
    {
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }
            var bytes = KnowledgeSchema.Serialize(ToFile());
            _files.Write(_path, bytes, b => KnowledgeSchema.ParseCharacter(b, Character, _gameDir).Error, _moveAsideInvalidMain);
            _dirty = false;
            _moveAsideInvalidMain = false;
        }
    }

    /// <summary>The file form of the current state (caller holds the lock).</summary>
    private CharacterFileDto ToFile() => new()
    {
        SchemaVersion = KnowledgeSchema.CurrentVersion,
        SavedUtc = _clock.UtcNow,
        Character = Character.Value,
        Stock = new StockDto
        {
            Items = [.. _items.Values.OrderBy(e => e.Item.Value, StringComparer.Ordinal)],
            StaleSince = _staleSince,
            LastAppliedOffset = _lastAppliedOffset,
            LogFile = _logFile,
        },
        Recipes = [.. _recipes.Values],
        KeepStock = [.. _keepStock],
        IconItems = [.. _iconItems.Values],
    };

    /// <summary>Positive when <paramref name="a"/> is more recent than <paramref name="b"/>.</summary>
    internal static int CompareRecency(StockEntry a, StockEntry b)
    {
        if (a.ObservedAtLog is { } pa && b.ObservedAtLog is { } pb)
        {
            if (pa.SameFile(pb))
            {
                return pa.Offset.CompareTo(pb.Offset);
            }
            var byGeneration = pa.File.CreationUtc.CompareTo(pb.File.CreationUtc);
            if (byGeneration != 0)
            {
                return byGeneration;
            }
        }
        return a.ObservedAtUtc.CompareTo(b.ObservedAtUtc);
    }

    /// <summary>Precedence at the same moment: positive when <paramref name="a"/> wins over <paramref name="b"/>.</summary>
    internal static int Precedence(StockEntry a, StockEntry b)
    {
        static int SourceRank(StockSource s) => s switch
        {
            StockSource.UserEntered => 3,
            StockSource.Observed => 2,
            _ => 1,
        };

        var bySource = SourceRank(a.Source).CompareTo(SourceRank(b.Source));
        if (bySource != 0)
        {
            return bySource;
        }
        var byExact = (a.Count is not null).CompareTo(b.Count is not null);
        return byExact != 0 ? byExact : a.Confidence.CompareTo(b.Confidence);
    }

    /// <summary>
    /// Recipe drift rules: a user-verified recipe re-read with the same content stays verified (fresh capture,
    /// build and cadence are taken over); a re-read with different content replaces it unverified (needs
    /// verification again); the learned tick cadence is kept when the new reading has none.
    /// </summary>
    internal static Recipe MergeRecipe(Recipe current, Recipe incoming)
    {
        var cadence = incoming.MedianTickCadence ?? current.MedianTickCadence;
        var productId = incoming.ProductItemId ?? (current.Product == incoming.Product ? current.ProductItemId : null);
        if (!incoming.UserVerified && current.UserVerified && SameContent(current, incoming))
        {
            return incoming with { UserVerified = true, Confidence = RecipeConfidence.Complete, MedianTickCadence = cadence, ProductItemId = productId };
        }
        return incoming with { MedianTickCadence = cadence, ProductItemId = productId };
    }

    /// <summary>True when two recipes describe the same craft (product and materials by <see cref="ItemIdentity"/>, materials as a set).</summary>
    internal static bool SameContent(Recipe a, Recipe b)
    {
        if (!ItemIdentity.SameProduct(a, b) || a.OutputQty != b.OutputQty || !string.Equals(a.Station, b.Station, StringComparison.Ordinal)
            || a.SuccessRate != b.SuccessRate)
        {
            return false;
        }
        return ItemIdentity.SameMaterials(a.Materials, b.Materials) && a.Requirements.SequenceEqual(b.Requirements, StringComparer.Ordinal);
    }
}
