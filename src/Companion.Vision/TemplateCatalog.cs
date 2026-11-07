using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Companion.Contracts;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.Vision.Internal;

namespace Companion.Vision;

/// <summary>
/// Template catalog (unit "vision-ocr"). The host adds every template once at start (core + features) with the PNG
/// bytes it already integrity-checked, then seals. Code floors are applied on <see cref="Add"/> (threshold &gt;= 0.85,
/// margin &gt;= 0.05, radius &lt;= 40, Verified only with SourceScale 1), so <see cref="Get"/> returns the effective
/// definition. Suspect marks are per host session. Thread-safe: Add/Seal under a lock, reads lock-free after Seal.
/// </summary>
public sealed partial class TemplateCatalog : ITemplateCatalogBuilder
{
    private readonly object gate = new();
    private readonly Dictionary<string, TemplateImage> building = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> suspects = new(StringComparer.Ordinal);
    private volatile FrozenDictionary<string, TemplateImage>? sealedImages;

    /// <summary>Creates an empty catalog (no I/O).</summary>
    public TemplateCatalog()
    {
    }

    /// <inheritdoc />
    public IReadOnlyCollection<TemplateDefinition> All
    {
        get
        {
            var s = sealedImages;
            if (s is not null)
            {
                return s.Values.Select(i => i.Definition).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
            }

            lock (gate)
            {
                return building.Values.Select(i => i.Definition).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
            }
        }
    }

    /// <inheritdoc />
    public void Add(TemplateDefinition definition, byte[] png, byte[]? maskPng, string sourceCode)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(png);
        Validate(definition);
        var effective = TemplateFloors.Apply(definition);
        var image = PngCodec.Decode(png);
        var mask = maskPng is null ? null : PngCodec.Decode(maskPng);
        var prepared = TemplateImage.Build(effective, image, mask, string.IsNullOrWhiteSpace(sourceCode) ? "unknown" : sourceCode);

        lock (gate)
        {
            if (sealedImages is not null)
            {
                throw new InvalidOperationException("template catalog is sealed");
            }

            if (!building.TryAdd(effective.Id, prepared))
            {
                throw new ArgumentException($"template.duplicate:{effective.Id}");
            }
        }
    }

    /// <inheritdoc />
    public void Seal()
    {
        lock (gate)
        {
            if (sealedImages is not null)
            {
                return;
            }

            foreach (var t in building.Values)
            {
                CheckAnchorChain(t.Definition);
            }

            sealedImages = building.ToFrozenDictionary(StringComparer.Ordinal);
        }
    }

    /// <inheritdoc />
    public TemplateDefinition? Get(string id) => GetImage(id)?.Definition;

    /// <inheritdoc />
    public SizeI? SizeOf(string id) => GetImage(id) is { } t ? new SizeI(t.Width, t.Height) : null;

    /// <inheritdoc />
    public bool IsClickable(string id)
    {
        var d = Get(id);
        return d is not null
            && d.Status == TemplateStatus.Verified
            && d.SourceScale == 1
            && !d.Tags.HasFlag(TargetTags.Forbidden)
            && d.Threshold >= HardRules.MinNccThreshold
            && d.MinMargin >= HardRules.MinMatchMargin
            && d.SearchRadius <= HardRules.MaxSearchRadiusPx
            && !IsSuspect(id);
    }

    /// <inheritdoc />
    public void MarkSuspect(string id, string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(id);
        suspects[id] = string.IsNullOrWhiteSpace(reasonCode) ? "suspect" : reasonCode;
    }

    /// <inheritdoc />
    public bool IsSuspect(string id) => id is not null && suspects.ContainsKey(id);

    /// <summary>Prepared template (pixels) for the matcher.</summary>
    internal TemplateImage? GetImage(string id)
    {
        if (id is null)
        {
            return null;
        }

        var s = sealedImages;
        if (s is not null)
        {
            return s.GetValueOrDefault(id);
        }

        lock (gate)
        {
            return building.GetValueOrDefault(id);
        }
    }

    private static void Validate(TemplateDefinition d)
    {
        if (string.IsNullOrWhiteSpace(d.Id) || !IdPattern().IsMatch(d.Id))
        {
            throw new ArgumentException($"template.badId:{d.Id}");
        }

        if (string.IsNullOrWhiteSpace(d.ImageFile))
        {
            throw new ArgumentException($"template.noImage:{d.Id}");
        }

        if (d.SourceScale < 1)
        {
            throw new ArgumentException($"template.badSourceScale:{d.Id}");
        }

        if (d.SearchRadius < 0)
        {
            throw new ArgumentException($"template.badRadius:{d.Id}");
        }

        var client = new RectI(0, 0, HardRules.ExpectedClientWidth, HardRules.ExpectedClientHeight);
        if (d.AnchorId is null && !client.Contains(d.ExpectedCenter))
        {
            throw new ArgumentException($"template.centerOutsideClient:{d.Id}");
        }

        if (d.AnchorId is not null && d.OffsetFromAnchor is null)
        {
            throw new ArgumentException($"template.anchorWithoutOffset:{d.Id}");
        }

        if (d.Tags.HasFlag(TargetTags.Commit) && d.Gate == AutomationSwitch.None)
        {
            throw new ArgumentException($"template.commitWithoutGate:{d.Id}");
        }

        // NavFixed lets a cross-monitor press skip the slid check (SPEC 7.3): fixed HUD navigation only.
        if (d.Tags.HasFlag(TargetTags.NavFixed)
            && ((d.Tags & (TargetTags.Commit | TargetTags.Forbidden)) != 0 || d.Gate != AutomationSwitch.None || d.AnchorId is not null || d.OffsetFromAnchor is not null))
        {
            throw new ArgumentException($"template.navFixedInvalid:{d.Id}");
        }
    }

    private void CheckAnchorChain(TemplateDefinition d)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { d.Id };
        var current = d;
        while (current.AnchorId is { } anchor)
        {
            if (!building.TryGetValue(anchor, out var next))
            {
                throw new InvalidOperationException($"template.anchorMissing:{d.Id}->{anchor}");
            }

            if (!seen.Add(anchor) || seen.Count > 3)
            {
                throw new InvalidOperationException($"template.anchorCycle:{d.Id}");
            }

            current = next.Definition;
        }
    }

    [GeneratedRegex(@"^[a-z0-9]+(?:[._-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
