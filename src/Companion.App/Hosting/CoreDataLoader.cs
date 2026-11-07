using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Host;
using Companion.Contracts.Navigation;
using Companion.Contracts.Perception;
using Companion.Contracts.Vision;

namespace Companion.App.Hosting;

/// <summary>Outcome of the one-time data load (SPEC 4 step 7).</summary>
/// <param name="CoreOk">Every core data file loaded and every core procedure survived sealing.</param>
/// <param name="Problems">Problem codes (core data); empty when <paramref name="CoreOk"/>.</param>
/// <param name="Features">Feature load reports (core first).</param>
internal sealed record DataLoadReport(bool CoreOk, IReadOnlyList<string> Problems, IReadOnlyList<FeatureLoadReport> Features);

/// <summary>
/// The loader (SPEC 4 step 7): reads every core data file ONCE through <see cref="IFileTrust.ReadTrusted"/>, deserializes
/// it with <see cref="ContractJson.Options"/> (templates.json = TemplateDefinition[], ui-map.json = UiMap,
/// procedures.json = ProcedureDefinition[], readers.json = ReaderDefinition[]), hands it to the builders, then loads
/// the compiled-in features, seals every catalog and checks that no core procedure was dropped at sealing. Nothing is
/// read again afterwards (never reload).
/// </summary>
internal static class CoreDataLoader
{
    /// <summary>Source code passed to the builders for core data.</summary>
    public const string CoreSource = "core";

    /// <summary>Loads core data and features into the builders and seals them.</summary>
    public static DataLoadReport Load(HostServices s, IReadOnlyList<IFeature> features, CompanionConfig config)
    {
        ArgumentNullException.ThrowIfNull(s);
        var problems = new List<string>();
        var paths = s.Paths;

        // 1. Templates (definitions + PNG bytes, all integrity-checked).
        var templateDefs = ReadJson<TemplateDefinition[]>(s.FileTrust, Path.Combine(paths.TemplatesDir, DataFileNames.Templates), problems, "templates");
        foreach (var def in templateDefs ?? [])
        {
            if (def is null || string.IsNullOrEmpty(def.Id) || !IsSafeFileName(def.ImageFile) || (def.MaskFile is not null && !IsSafeFileName(def.MaskFile)))
            {
                problems.Add("core.templates.invalid");
                continue;
            }

            var png = s.FileTrust.ReadTrusted(Path.Combine(paths.TemplatesDir, def.ImageFile));
            if (png is null)
            {
                problems.Add("core.templates.untrusted:" + def.Id);
                continue;
            }

            byte[]? mask = null;
            if (def.MaskFile is not null)
            {
                mask = s.FileTrust.ReadTrusted(Path.Combine(paths.TemplatesDir, def.MaskFile));
                if (mask is null)
                {
                    problems.Add("core.templates.untrusted:" + def.Id);
                    continue;
                }
            }

            Try(() => s.Templates.Add(def, png, mask, CoreSource), problems, "core.templates.rejected:" + def.Id);
        }

        // 2. Readers.
        var readerDefs = ReadJson<ReaderDefinition[]>(s.FileTrust, Path.Combine(paths.DataDir, DataFileNames.Readers), problems, "readers");
        foreach (var r in readerDefs ?? [])
        {
            if (r is null)
            {
                problems.Add("core.readers.invalid");
                continue;
            }

            Try(() => s.Readers.Add(r, CoreSource), problems, "core.readers.rejected:" + r.Id);
        }

        // 3. UI map.
        var map = ReadJson<UiMap>(s.FileTrust, Path.Combine(paths.DataDir, DataFileNames.UiMap), problems, "uiMap");
        if (map is not null)
        {
            Try(() => s.UiMap.AddFragment(map, CoreSource), problems, "core.uiMap.rejected");
        }

        // 4. Procedures.
        var procedureDefs = ReadJson<ProcedureDefinition[]>(s.FileTrust, Path.Combine(paths.DataDir, DataFileNames.Procedures), problems, "procedures");
        foreach (var p in procedureDefs ?? [])
        {
            if (p is null)
            {
                problems.Add("core.procedures.invalid");
                continue;
            }

            Try(() => s.Procedures.Add(p, CoreSource), problems, "core.procedures.rejected:" + p.Id);
        }

        // 5. Features (compiled in; the host adds the core feature itself and isolates each Register).
        IReadOnlyList<FeatureLoadReport> reports;
        try
        {
            reports = s.Features.LoadAll(features, new FeatureLoadContext(config, paths, s.Templates, s.UiMap, s.Procedures, s.Readers, s.FileTrust, s.Journal));
        }
        catch (Exception)
        {
            problems.Add("core.features.loadFailed");
            reports = [];
        }

        // 6. Seal (templates and readers first: the map and the procedures resolve references at Seal).
        Try(s.Templates.Seal, problems, "core.seal.templates");
        Try(s.Readers.Seal, problems, "core.seal.readers");
        Try(s.UiMap.Seal, problems, "core.seal.uiMap");
        Try(s.Procedures.Seal, problems, "core.seal.procedures");

        // 7. A core procedure dropped at Seal (dangling reference) means a broken core data set.
        foreach (var p in procedureDefs ?? [])
        {
            if (p?.Id is not null && s.Procedures.Get(p.Id) is null)
            {
                problems.Add("core.procedures.dropped:" + p.Id);
            }
        }

        return new DataLoadReport(problems.Count == 0, problems.Distinct(StringComparer.Ordinal).ToArray(), reports);
    }

    /// <summary>
    /// The daily checklist's catalog (data/dailies.json, 2026-11-04), read once through the trust check like every other
    /// data file. Null with <paramref name="problem"/> 'dailies.untrusted' or 'dailies.invalid' (the checklist then stays off).
    /// </summary>
    public static Companion.Dailies.DailyCatalog? LoadDailies(HostServices s, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(s);
        var bytes = s.FileTrust.ReadTrusted(Path.Combine(s.Paths.DataDir, "dailies.json"));
        if (bytes is null)
        {
            problem = "dailies.untrusted";
            return null;
        }

        try
        {
            ReadOnlySpan<byte> json = bytes;
            if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            {
                json = json[3..];
            }

            problem = null;
            return Companion.Dailies.DailyCatalog.Parse(json);
        }
        catch (InvalidDataException)
        {
            problem = "dailies.invalid";
            return null;
        }
    }

    private static T? ReadJson<T>(IFileTrust trust, string path, List<string> problems, string what)
        where T : class
    {
        var bytes = trust.ReadTrusted(path);
        if (bytes is null)
        {
            problems.Add("core." + what + ".untrusted");
            return null;
        }

        try
        {
            ReadOnlySpan<byte> json = bytes;
            if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            {
                json = json[3..];
            }

            var value = JsonSerializer.Deserialize<T>(json, ContractJson.Options);
            if (value is null)
            {
                problems.Add("core." + what + ".invalid");
            }

            return value;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            problems.Add("core." + what + ".invalid");
            return null;
        }
    }

    private static void Try(Action action, List<string> problems, string code)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // Any builder rejection (validation, duplicates, sealed) is a broken core data set, never a crash.
            problems.Add(code);
        }
    }

    /// <summary>A plain file name inside the templates folder (no folders, no '..', no rooted path).</summary>
    private static bool IsSafeFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 128
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !name.Contains("..", StringComparison.Ordinal)
        && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
}
