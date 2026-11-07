using System.Text.Json;
using System.Text.Json.Serialization;

namespace Companion.Contracts;

/// <summary>
/// The one JSON configuration every unit uses for data files (templates.json, ui-map.json, procedures.json,
/// readers.json, config.json, knowledge stores, journal records, pipe messages): camelCase, enums as strings,
/// polymorphic step actions by "type", comments and trailing commas allowed on read. Read-only (frozen).
/// </summary>
public static class ContractJson
{
    /// <summary>Shared, frozen serializer options.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowOutOfOrderMetadataProperties = true,
            MaxDepth = 32,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }
}
