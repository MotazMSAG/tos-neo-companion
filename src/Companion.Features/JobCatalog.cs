using System.Globalization;
using Companion.Contracts.Features;
using Companion.Contracts.Knowledge;

namespace Companion.Features;

/// <summary>
/// The job whitelist: jobs of enabled, non-faulted features (SPEC 11.4). Thread-safe: the pipe thread validates and
/// looks up while the engine thread runs handlers and may fault a feature.
/// </summary>
internal sealed class JobCatalog : IJobCatalog
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly HashSet<string> _faulted = new(StringComparer.Ordinal);

    public IReadOnlyList<JobDefinition> All
    {
        get
        {
            lock (_gate)
            {
                return _entries.Where(e => !_faulted.Contains(e.Definition.FeatureId)).Select(e => e.Definition).ToArray();
            }
        }
    }

    public bool TryGet(string name, out JobDefinition? definition, out IJobHandler? handler)
    {
        lock (_gate)
        {
            foreach (var e in _entries)
            {
                if (string.Equals(e.Definition.Name, name, StringComparison.Ordinal) && !_faulted.Contains(e.Definition.FeatureId))
                {
                    definition = e.Definition;
                    handler = e.Handler;
                    return true;
                }
            }
        }

        definition = null;
        handler = null;
        return false;
    }

    /// <summary>True if a job of that name (any case, faulted or not) was ever registered.</summary>
    public bool ContainsName(string name)
    {
        lock (_gate)
        {
            return _entries.Any(e => string.Equals(e.Definition.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Add(JobDefinition definition, IJobHandler handler)
    {
        lock (_gate)
        {
            _entries.Add(new Entry(definition, handler));
        }
    }

    public void Fault(string featureId)
    {
        lock (_gate)
        {
            _faulted.Add(featureId);
        }
    }

    public JobParameters? Validate(JobDefinition definition, IReadOnlyDictionary<string, string> raw, ICharacterKnowledge? knowledge, out string? reasonCode)
    {
        reasonCode = null;
        if (definition is null)
        {
            reasonCode = "job.unknown";
            return null;
        }

        raw ??= new Dictionary<string, string>();
        if (raw.Count > ManifestRules.MaxParams)
        {
            reasonCode = "param.tooMany";
            return null;
        }

        var specs = definition.Params ?? [];
        foreach (var key in raw.Keys)
        {
            // Never echo the caller's text: the name may be anything.
            if (!specs.Any(s => string.Equals(s.Name, key, StringComparison.Ordinal)))
            {
                reasonCode = "param.unknown";
                return null;
            }
        }

        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            if (!raw.TryGetValue(spec.Name, out var text) || text is null)
            {
                if (spec.Required)
                {
                    reasonCode = "param.missing:" + spec.Name;
                    return null;
                }

                continue;
            }

            var maxLength = Math.Clamp(spec.MaxLength, 1, ManifestRules.MaxParamLength);
            if (text.Length == 0 || text.Length > maxLength)
            {
                reasonCode = "param.length:" + spec.Name;
                return null;
            }

            if (text.Any(char.IsControl))
            {
                reasonCode = "param.chars:" + spec.Name;
                return null;
            }

            switch (spec.Type)
            {
                case JobParamType.Int:
                    if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
                    {
                        reasonCode = "param.type:" + spec.Name;
                        return null;
                    }

                    if (spec.Min is not { } min || spec.Max is not { } max || n < min || n > max)
                    {
                        reasonCode = "param.range:" + spec.Name;
                        return null;
                    }

                    values[spec.Name] = n;
                    break;

                case JobParamType.Bool:
                    if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
                    {
                        values[spec.Name] = true;
                    }
                    else if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
                    {
                        values[spec.Name] = false;
                    }
                    else
                    {
                        reasonCode = "param.type:" + spec.Name;
                        return null;
                    }

                    break;

                case JobParamType.Enum:
                    var allowed = spec.Allowed?.FirstOrDefault(a => string.Equals(a, text, StringComparison.Ordinal));
                    if (allowed is null)
                    {
                        reasonCode = "param.enum:" + spec.Name;
                        return null;
                    }

                    values[spec.Name] = allowed;
                    break;

                case JobParamType.ItemKey:
                    if (knowledge is null)
                    {
                        reasonCode = "param.item.noKnowledge:" + spec.Name;
                        return null;
                    }

                    var key = ItemKey.FromDisplayName(text);
                    if (key.Value.Length > 0 && !IsKnownItem(knowledge, key) && ItemById(knowledge, text) is { } byId)
                    {
                        key = byId; // the game's item id of a known item (2026-10-15: "item=<itemId or name>")
                    }

                    if (key.Value.Length == 0 || !IsKnownItem(knowledge, key))
                    {
                        reasonCode = "param.item.unknown:" + spec.Name;
                        return null;
                    }

                    values[spec.Name] = key;
                    break;

                default:
                    reasonCode = "param.type:" + spec.Name;
                    return null;
            }
        }

        return new JobParameters(values);
    }

    /// <summary>A known item key of the store: in stock, a recipe product or material, or a keep-stock item.</summary>
    private static bool IsKnownItem(ICharacterKnowledge knowledge, ItemKey key)
    {
        if (knowledge.GetStock().Items.ContainsKey(key))
        {
            return true;
        }

        foreach (var r in knowledge.GetRecipes())
        {
            if (r.Product == key || r.Materials.Any(m => m.Item == key))
            {
                return true;
            }
        }

        return knowledge.GetKeepStockRules().Any(r => r.Item == key);
    }

    /// <summary>
    /// The key of the known item whose game item id is <paramref name="text"/> (digits only): a recipe product or
    /// material, a stock entry or a learned icon item with that id; null when the text is not an id or no item has it.
    /// </summary>
    private static ItemKey? ItemById(ICharacterKnowledge knowledge, string text)
    {
        if (text.Length > 18 || !text.All(char.IsAsciiDigit) || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1)
        {
            return null;
        }

        foreach (var r in knowledge.GetRecipes())
        {
            if (r.ProductItemId == id)
            {
                return r.Product;
            }

            if (r.Materials.FirstOrDefault(m => m.ItemId == id) is { } m)
            {
                return m.Item;
            }
        }

        if (knowledge.GetStock().Items.Values.FirstOrDefault(e => e.ItemId == id) is { } entry)
        {
            return entry.Item;
        }

        return knowledge.GetIconItems().FirstOrDefault(i => i.ItemId == id) is { } icon ? icon.Item : null;
    }

    private sealed record Entry(JobDefinition Definition, IJobHandler Handler);
}
