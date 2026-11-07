using System.Text.Json;
using System.Text.RegularExpressions;
using Companion.Contracts;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;

namespace Companion.Knowledge.Storage;

/// <summary>
/// On-disk schema of the knowledge store (RuntimeDir\knowledge). Every file carries <c>schemaVersion</c>; versions
/// <see cref="MinimumVersion"/>..<see cref="CurrentVersion"/> are read (an unknown, future or missing version makes the
/// file invalid, so the reader falls back to the newest valid backup) and <see cref="CurrentVersion"/> is written.
/// Version 2 (2026-10-15) only ADDS optional fields: the game's item ids (stock entries, recipe products, recipe
/// materials) and the learned icon items of a character file; a version-1 file is read as is (no id known, no icon
/// item) and is written as version 2 on its next save. Serialised with <see cref="ContractJson.Options"/>.
/// </summary>
internal static class KnowledgeSchema
{
    /// <summary>The schema version this build writes (and the newest it reads).</summary>
    public const int CurrentVersion = 2;

    /// <summary>The oldest schema version this build still reads (version 1: before item ids, 2026-10-14).</summary>
    public const int MinimumVersion = 1;

    /// <summary>An icon item's key: "icon:" + 64 lower-case hex (the Make tab's material fingerprint).</summary>
    public static readonly Regex IconKeyPattern = new("^icon:[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    /// <summary>Bindings file name (in KnowledgeDir; before 4.33, now only read on the first 4.33 start).</summary>
    public const string BindingsFileName = "characters.json";

    /// <summary>The pinned profile folder (in KnowledgeDir, 4.33).</summary>
    public const string ProfileFileName = "profile.json";

    /// <summary>Per-character file name (in KnowledgeDir\&lt;key&gt;).</summary>
    public const string CharacterFileName = "knowledge.json";

    /// <summary>Character keys are 16 lower-case hex chars (also the folder name, so this blocks path tricks).</summary>
    public static readonly Regex CharacterKeyPattern = new("^[0-9a-f]{16}$", RegexOptions.CultureInvariant);

    /// <summary>A recipe's optional counter (<see cref="Recipe.Counter"/>): "a/b", digits only.</summary>
    public static readonly Regex CounterPattern = new(@"^\d{1,7}/\d{1,7}$", RegexOptions.CultureInvariant);

    /// <summary>Maximum length of a normalised item key.</summary>
    public const int MaxItemKeyLength = 128;

    /// <summary>Maximum length of ids, station codes, coverage strings and labels.</summary>
    public const int MaxCodeLength = 128;

    /// <summary>Maximum number of entries per list (defensive bound against runaway data).</summary>
    public const int MaxEntries = 100_000;

    /// <summary>True when <paramref name="key"/> is a well-formed character key.</summary>
    public static bool IsValidCharacterKey(CharacterKey key) => key.Value is not null && CharacterKeyPattern.IsMatch(key.Value);

    /// <summary>True when the item key is non-empty, bounded and already in normalised form.</summary>
    public static bool IsValidItem(ItemKey item)
    {
        var v = item.Value;
        if (string.IsNullOrEmpty(v) || v.Length > MaxItemKeyLength || HasControlChars(v))
        {
            return false;
        }
        return string.Equals(ItemKey.FromDisplayName(v).Value, v, StringComparison.Ordinal);
    }

    /// <summary>True when <paramref name="s"/> contains a control character.</summary>
    public static bool HasControlChars(string s)
    {
        foreach (var c in s)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True when the string is a bounded, control-free text (codes, coverage, station).</summary>
    public static bool IsBoundedText(string? s, bool allowEmpty) =>
        s is not null && (allowEmpty || s.Length > 0) && s.Length <= MaxCodeLength && !HasControlChars(s);

    /// <summary>An optional game item id: absent, or positive.</summary>
    public static bool IsValidItemId(long? id) => id is null or > 0;

    /// <summary>True for a schema version this build reads.</summary>
    public static bool IsReadableVersion(int version) => version is >= MinimumVersion and <= CurrentVersion;

    /// <summary>Validates a learned icon item. Returns an error code or null.</summary>
    public static string? ValidateIconItem(IconItem? i)
    {
        if (i is null)
        {
            return "iconItem.null";
        }
        if (i.IconKey is null || !IconKeyPattern.IsMatch(i.IconKey))
        {
            return "iconItem.iconKey";
        }
        if (i.ItemId <= 0)
        {
            return "iconItem.itemId";
        }
        if (!IsBoundedText(i.Name, allowEmpty: false) || i.Name.Trim().Length == 0 || !IsValidItem(i.Item)
            || i.Item.Value.StartsWith("icon:", StringComparison.Ordinal))
        {
            return "iconItem.name";
        }
        return null;
    }

    /// <summary>Validates a stock entry. Returns an error code or null.</summary>
    public static string? ValidateStockEntry(StockEntry? e)
    {
        if (e is null)
        {
            return "stock.null";
        }
        if (!IsValidItem(e.Item))
        {
            return "stock.item";
        }
        if (e.Count is null && e.Range is null)
        {
            return "stock.noValue";
        }
        if (e.Count is < 0)
        {
            return "stock.count";
        }
        if (e.Range is not null && (e.Range.Min < 0 || e.Range.Min > e.Range.Max))
        {
            return "stock.range";
        }
        if (e.Count is long c && e.Range is not null && (c < e.Range.Min || c > e.Range.Max))
        {
            return "stock.countOutsideRange";
        }
        if (!Enum.IsDefined(e.Source))
        {
            return "stock.source";
        }
        if (double.IsNaN(e.Confidence) || e.Confidence < 0 || e.Confidence > 1)
        {
            return "stock.confidence";
        }
        if (!IsBoundedText(e.Coverage, allowEmpty: true))
        {
            return "stock.coverage";
        }
        if (e.ObservedAtLog is not null && (e.ObservedAtLog.File is null || e.ObservedAtLog.Offset < 0))
        {
            return "stock.logPosition";
        }
        if (!IsValidItemId(e.ItemId))
        {
            return "stock.itemId";
        }
        if (e.FreshUntilUtc is { } until && until < e.ObservedAtUtc)
        {
            return "stock.freshUntil";
        }
        return null;
    }

    /// <summary>
    /// Validates the shape of a recipe (shared by the store and the planner). Returns an error code or null.
    /// </summary>
    public static string? ValidateRecipeShape(Recipe? r)
    {
        if (r is null)
        {
            return "recipe.null";
        }
        if (!IsBoundedText(r.Id, allowEmpty: false) || r.Id.Trim().Length == 0)
        {
            return "recipe.id";
        }
        if (!IsValidItem(r.Product))
        {
            return "recipe.product";
        }
        if (r.OutputQty < 1)
        {
            return "recipe.outputQty";
        }
        if (!IsBoundedText(r.Station, allowEmpty: true))
        {
            return "recipe.station";
        }
        if (r.Materials is null)
        {
            return "recipe.materials";
        }
        var seen = new HashSet<ItemKey>();
        var seenIds = new HashSet<long>();
        foreach (var m in r.Materials)
        {
            if (m is null || !IsValidItem(m.Item) || m.Qty < 1 || !IsValidItemId(m.ItemId))
            {
                return "recipe.material";
            }
            if (!seen.Add(m.Item) || (m.ItemId is { } id && !seenIds.Add(id)))
            {
                return "recipe.materialDuplicate";
            }
        }
        if (!IsValidItemId(r.ProductItemId))
        {
            return "recipe.productItemId";
        }
        if (r.Requirements is null)
        {
            return "recipe.requirements";
        }
        foreach (var q in r.Requirements)
        {
            if (!IsBoundedText(q, allowEmpty: false))
            {
                return "recipe.requirement";
            }
        }
        if (r.SuccessRate is double p && (double.IsNaN(p) || p <= 0 || p > 1))
        {
            return "recipe.successRate";
        }
        if (!Enum.IsDefined(r.Confidence))
        {
            return "recipe.confidence";
        }
        if (r.MedianTickCadence is TimeSpan t && (t <= TimeSpan.Zero || t > TimeSpan.FromMinutes(10)))
        {
            return "recipe.cadence";
        }
        if (r.GameBuild is not null && !IsBoundedText(r.GameBuild, allowEmpty: true))
        {
            return "recipe.gameBuild";
        }
        if (r.SourceCapture is not null && (r.SourceCapture.Length > 512 || HasControlChars(r.SourceCapture)))
        {
            return "recipe.sourceCapture";
        }
        if (r.Counter is not null && !CounterPattern.IsMatch(r.Counter))
        {
            return "recipe.counter";
        }
        return null;
    }

    /// <summary>
    /// Screen-only learning (REC-01): a recipe must come from a screen capture (an artifact path that is not inside the
    /// game folder and has no parent-directory segments) or be confirmed by the user. Returns an error code or null.
    /// </summary>
    public static string? ValidateRecipeProvenance(Recipe r, string? gameDir)
    {
        var src = r.SourceCapture;
        if (string.IsNullOrWhiteSpace(src))
        {
            return r.UserVerified ? null : "recipe.provenance";
        }
        var segments = src.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
        {
            return "recipe.sourceCapture.parent";
        }
        if (!string.IsNullOrEmpty(gameDir) && Path.IsPathFullyQualified(src))
        {
            var full = Path.GetFullPath(src);
            var game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDir));
            if (full.Equals(game, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(game + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return "recipe.sourceCapture.gameDir";
            }
        }
        return null;
    }

    /// <summary>Validates keep-stock rules. Returns an error code or null.</summary>
    public static string? ValidateKeepStock(IReadOnlyList<KeepStockRule>? rules)
    {
        if (rules is null)
        {
            return "keepStock.null";
        }
        var seen = new HashSet<ItemKey>();
        foreach (var r in rules)
        {
            if (r is null || !IsValidItem(r.Item))
            {
                return "keepStock.item";
            }
            if (r.Min < 0)
            {
                return "keepStock.min";
            }
            if (!seen.Add(r.Item))
            {
                return "keepStock.duplicate";
            }
        }
        return null;
    }

    /// <summary>Validates an observation. Returns an error code or null.</summary>
    public static string? ValidateObservation(StockObservation? o)
    {
        if (o is null)
        {
            return "obs.null";
        }
        if (!IsValidItem(o.Item))
        {
            return "obs.item";
        }
        if (o.Count is null && o.Range is null)
        {
            return "obs.noValue";
        }
        if (o.Count is < 0)
        {
            return "obs.count";
        }
        if (o.Range is not null && (o.Range.Min < 0 || o.Range.Min > o.Range.Max))
        {
            return "obs.range";
        }
        if (o.Count is long c && o.Range is not null && (c < o.Range.Min || c > o.Range.Max))
        {
            return "obs.countOutsideRange";
        }
        if (!Enum.IsDefined(o.Kind))
        {
            return "obs.kind";
        }
        if (o.AtLog is null || o.AtLog.File is null || o.AtLog.Offset < 0)
        {
            return "obs.logPosition";
        }
        if (double.IsNaN(o.Confidence))
        {
            return "obs.confidence";
        }
        if (!IsBoundedText(o.Coverage, allowEmpty: true))
        {
            return "obs.coverage";
        }
        if (!IsValidItemId(o.ItemId))
        {
            return "obs.itemId";
        }
        if (o.FreshUntilUtc is { } until && until < o.AtUtc)
        {
            return "obs.freshUntil";
        }
        return null;
    }

    /// <summary>Strips a UTF-8 BOM if present.</summary>
    public static ReadOnlySpan<byte> WithoutBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes.AsSpan(3) : bytes.AsSpan();

    /// <summary>Serialises with the one shared serializer.</summary>
    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options);

    /// <summary>Parses and validates the pinned profile file. Returns the file or an error code.</summary>
    public static (ProfileFileDto? File, string? Error) ParseProfile(byte[] bytes)
    {
        ProfileFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ProfileFileDto>(WithoutBom(bytes), ContractJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException)
        {
            return (null, "json.parse");
        }
        if (dto is null)
        {
            return (null, "json.null");
        }
        if (!IsReadableVersion(dto.SchemaVersion))
        {
            return (null, dto.SchemaVersion > CurrentVersion ? "schema.version.unsupported" : "schema.version");
        }
        return dto.Profile is not null && CharacterKeyPattern.IsMatch(dto.Profile) ? (dto, null) : (null, "profile.key");
    }

    /// <summary>Parses and validates a bindings file. Returns the file or an error code.</summary>
    public static (BindingsFileDto? File, string? Error) ParseBindings(byte[] bytes)
    {
        BindingsFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<BindingsFileDto>(WithoutBom(bytes), ContractJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException)
        {
            return (null, "json.parse");
        }
        var err = ValidateBindings(dto);
        return err is null ? (dto, null) : (null, err);
    }

    /// <summary>Validates a bindings file. Returns an error code or null.</summary>
    public static string? ValidateBindings(BindingsFileDto? dto)
    {
        if (dto is null)
        {
            return "json.null";
        }
        if (!IsReadableVersion(dto.SchemaVersion))
        {
            return dto.SchemaVersion > CurrentVersion ? "schema.version.unsupported" : "schema.version";
        }
        if (dto.Characters is null || dto.Characters.Count > MaxEntries)
        {
            return "bindings.characters";
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in dto.Characters)
        {
            if (b is null || b.Character is null || !CharacterKeyPattern.IsMatch(b.Character))
            {
                return "bindings.key";
            }
            if (!seen.Add(b.Character))
            {
                return "bindings.duplicate";
            }
            if (b.Permission is not null && !IsBoundedText(b.Permission, allowEmpty: false))
            {
                return "bindings.permission";
            }
            if (b.Label is not null && !IsBoundedText(b.Label, allowEmpty: false))
            {
                return "bindings.label";
            }
        }
        return null;
    }

    /// <summary>Parses and validates a character file. Returns the file or an error code.</summary>
    public static (CharacterFileDto? File, string? Error) ParseCharacter(byte[] bytes, CharacterKey expected, string? gameDir)
    {
        CharacterFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<CharacterFileDto>(WithoutBom(bytes), ContractJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException)
        {
            return (null, "json.parse");
        }
        var err = ValidateCharacter(dto, expected, gameDir);
        return err is null ? (dto, null) : (null, err);
    }

    /// <summary>Validates a character file. Returns an error code or null.</summary>
    public static string? ValidateCharacter(CharacterFileDto? dto, CharacterKey expected, string? gameDir)
    {
        if (dto is null)
        {
            return "json.null";
        }
        if (!IsReadableVersion(dto.SchemaVersion))
        {
            return dto.SchemaVersion > CurrentVersion ? "schema.version.unsupported" : "schema.version";
        }
        if (!string.Equals(dto.Character, expected.Value, StringComparison.Ordinal))
        {
            return "character.key";
        }
        if (dto.Stock is null || dto.Stock.Items is null || dto.Stock.Items.Count > MaxEntries)
        {
            return "stock.items";
        }
        if (dto.Stock.LastAppliedOffset < 0)
        {
            return "stock.lastAppliedOffset";
        }
        var items = new HashSet<ItemKey>();
        foreach (var e in dto.Stock.Items)
        {
            var err = ValidateStockEntry(e);
            if (err is not null)
            {
                return err;
            }
            if (!items.Add(e!.Item))
            {
                return "stock.duplicate";
            }
        }
        if (dto.Recipes is null || dto.Recipes.Count > MaxEntries)
        {
            return "recipes";
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in dto.Recipes)
        {
            var err = ValidateRecipeShape(r) ?? ValidateRecipeProvenance(r!, gameDir);
            if (err is not null)
            {
                return err;
            }
            if (!ids.Add(r!.Id))
            {
                return "recipe.duplicate";
            }
        }
        if (dto.IconItems is { } icons)
        {
            if (icons.Count > MaxEntries)
            {
                return "iconItems";
            }
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var i in icons)
            {
                var err = ValidateIconItem(i);
                if (err is not null)
                {
                    return err;
                }
                if (!keys.Add(i!.IconKey))
                {
                    return "iconItem.duplicate";
                }
            }
        }
        return ValidateKeepStock(dto.KeepStock);
    }
}

/// <summary>characters.json: the user's permission per character before 4.33 (KS-02); now only read to find the profile.</summary>
internal sealed class BindingsFileDto
{
    /// <summary>Schema version (1 or 2; the same shape).</summary>
    public int SchemaVersion { get; set; }

    /// <summary>When the file was written.</summary>
    public DateTimeOffset SavedUtc { get; set; }

    /// <summary>Bindings.</summary>
    public List<BindingDto>? Characters { get; set; }
}

/// <summary>profile.json: the knowledge folder every character shares (4.33), chosen once and kept.</summary>
internal sealed class ProfileFileDto
{
    /// <summary>Schema version.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>When the file was written.</summary>
    public DateTimeOffset SavedUtc { get; set; }

    /// <summary>The profile's folder key (16 lower-case hex).</summary>
    public string? Profile { get; set; }
}

/// <summary>One binding (character key as a plain string).</summary>
internal sealed class BindingDto
{
    /// <summary>Character key (16 lower-case hex).</summary>
    public string? Character { get; set; }

    /// <summary>User decision before 4.33 ("unknown", "readOnly" or "allowed").</summary>
    public string? Permission { get; set; }

    /// <summary>First seen.</summary>
    public DateTimeOffset FirstSeenUtc { get; set; }

    /// <summary>Optional user label.</summary>
    public string? Label { get; set; }
}

/// <summary>knowledge.json of one character: stock, recipes, keep-stock rules and learned icon items in ONE atomic file (STK-02).</summary>
internal sealed class CharacterFileDto
{
    /// <summary>Schema version (1 or 2; 2 adds the optional item ids and <see cref="IconItems"/>).</summary>
    public int SchemaVersion { get; set; }

    /// <summary>When the file was written.</summary>
    public DateTimeOffset SavedUtc { get; set; }

    /// <summary>Character key (must equal the folder name).</summary>
    public string? Character { get; set; }

    /// <summary>Stock (with LastAppliedOffset persisted in the same write).</summary>
    public StockDto? Stock { get; set; }

    /// <summary>Recipes (screen-learned or user-entered only).</summary>
    public List<Recipe>? Recipes { get; set; }

    /// <summary>Keep-stock rules.</summary>
    public List<KeepStockRule>? KeepStock { get; set; }

    /// <summary>Material icons whose game item is known (schema 2; absent in version-1 files = none).</summary>
    public List<IconItem>? IconItems { get; set; }
}

/// <summary>Stock part of a character file.</summary>
internal sealed class StockDto
{
    /// <summary>Entries (unique by item).</summary>
    public List<StockEntry>? Items { get; set; }

    /// <summary>Latest AllStock stale mark.</summary>
    public DateTimeOffset? StaleSince { get; set; }

    /// <summary>Last derived-event offset folded in.</summary>
    public long LastAppliedOffset { get; set; }

    /// <summary>Log generation of LastAppliedOffset.</summary>
    public LogFileIdentity? LogFile { get; set; }
}
