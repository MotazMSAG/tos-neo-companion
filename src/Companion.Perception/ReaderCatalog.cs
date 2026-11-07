using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Companion.Contracts;
using Companion.Contracts.Perception;
using Companion.Contracts.Safety;

namespace Companion.Perception;

/// <summary>
/// Reader catalog (unit "vision-ocr"): readers from data\readers.json and feature reader files, added once at host
/// start and sealed. A Ready reader must have measured regions (inside the client, or anchor-relative), a log UI name
/// or anchors, and pair semantics on every Pair region; a Ready reader without regions is an identity probe (2026-10-15,
/// e.g. "is the Make tab's recipe list shown"): it must name anchors, and a read only confirms the screen (Ok) or not
/// (ScreenMismatch). A NeedsSurvey reader is an explicit stub and may have no regions at all (no invented coordinates).
/// Thread-safe: Add/Seal under a lock, reads lock-free after Seal.
/// </summary>
public sealed partial class ReaderCatalog : IReaderCatalogBuilder
{
    private readonly object gate = new();
    private readonly Dictionary<string, (ReaderDefinition Reader, string Source)> building = new(StringComparer.Ordinal);
    private volatile FrozenDictionary<string, ReaderDefinition>? sealedReaders;

    /// <summary>Creates an empty catalog (no I/O).</summary>
    public ReaderCatalog()
    {
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ReaderDefinition> All
    {
        get
        {
            var s = sealedReaders;
            if (s is not null)
            {
                return s.Values.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
            }

            lock (gate)
            {
                return building.Values.Select(v => v.Reader).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
            }
        }
    }

    /// <inheritdoc />
    public void Add(ReaderDefinition reader, string sourceCode)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Validate(reader);
        lock (gate)
        {
            if (sealedReaders is not null)
            {
                throw new InvalidOperationException("reader catalog is sealed");
            }

            if (!building.TryAdd(reader.Id, (reader, string.IsNullOrWhiteSpace(sourceCode) ? "unknown" : sourceCode)))
            {
                throw new ArgumentException($"reader.duplicate:{reader.Id}");
            }
        }
    }

    /// <inheritdoc />
    public void Seal()
    {
        lock (gate)
        {
            sealedReaders ??= building.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.Reader, StringComparer.Ordinal);
        }
    }

    /// <inheritdoc />
    public ReaderDefinition? Get(string id)
    {
        if (id is null)
        {
            return null;
        }

        var s = sealedReaders;
        if (s is not null)
        {
            return s.GetValueOrDefault(id);
        }

        lock (gate)
        {
            return building.TryGetValue(id, out var v) ? v.Reader : null;
        }
    }

    private static void Validate(ReaderDefinition r)
    {
        if (string.IsNullOrWhiteSpace(r.Id) || !IdPattern().IsMatch(r.Id))
        {
            throw new ArgumentException($"reader.badId:{r.Id}");
        }

        ArgumentNullException.ThrowIfNull(r.Screen, nameof(r.Screen));
        ArgumentNullException.ThrowIfNull(r.Regions, nameof(r.Regions));
        var anchors = r.Screen.AnchorTemplateIds ?? [];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var region in r.Regions)
        {
            if (string.IsNullOrWhiteSpace(region.Id) || !ids.Add(region.Id))
            {
                throw new ArgumentException($"reader.badRegionId:{r.Id}/{region.Id}");
            }
        }

        if (r.Status != ReaderStatus.Ready)
        {
            return;
        }

        if (r.Regions.Count == 0 && anchors.Count == 0)
        {
            // only an identity probe (anchors, no regions) reads nothing but its screen identity
            throw new ArgumentException($"reader.readyWithoutRegions:{r.Id}");
        }

        if ((r.Screen.LogUiName is null || r.Screen.LogUiName == ScreenReader.AnyTopUi) && anchors.Count == 0)
        {
            throw new ArgumentException($"reader.noIdentity:{r.Id}");
        }

        var client = new RectI(0, 0, HardRules.ExpectedClientWidth, HardRules.ExpectedClientHeight);
        foreach (var region in r.Regions)
        {
            if (region.ClientRect.IsEmpty)
            {
                throw new ArgumentException($"reader.emptyRegion:{r.Id}/{region.Id}");
            }

            if (region.AnchorId is null && client.Intersect(region.ClientRect) != region.ClientRect)
            {
                throw new ArgumentException($"reader.regionOutsideClient:{r.Id}/{region.Id}");
            }

            if (region.Rule is ParseRule.Pair or ParseRule.TileRow or ParseRule.LabeledPair && region.Pair == PairSemantics.None)
            {
                throw new ArgumentException($"reader.pairWithoutSemantics:{r.Id}/{region.Id}");
            }

            if (region.Rule == ParseRule.IconFingerprint
                && (region.ClientRect.Width < 3 * Internal.IconFingerprint.Inset || region.ClientRect.Height < 3 * Internal.IconFingerprint.Inset))
            {
                throw new ArgumentException($"reader.iconTooSmall:{r.Id}/{region.Id}");
            }

            if (region.Rule == ParseRule.TileRow && (region.ClientRect.Height < 3 * Internal.IconFingerprint.Inset || region.ClientRect.Width < region.ClientRect.Height))
            {
                throw new ArgumentException($"reader.tileRowTooSmall:{r.Id}/{region.Id}");
            }

            if (region.Rule == ParseRule.IconGrid && (region.ClientRect.Width < 150 || region.ClientRect.Height < 150 || region.FontStyle is null))
            {
                throw new ArgumentException($"reader.iconGridInvalid:{r.Id}/{region.Id}");
            }

            if (!Enum.IsDefined(region.Rule))
            {
                throw new ArgumentException($"reader.badRule:{r.Id}/{region.Id}");
            }

            if (region.Min is { } min && region.Max is { } max && min > max)
            {
                throw new ArgumentException($"reader.badBounds:{r.Id}/{region.Id}");
            }
        }
    }

    [GeneratedRegex(@"^[a-z0-9]+(?:[._-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
