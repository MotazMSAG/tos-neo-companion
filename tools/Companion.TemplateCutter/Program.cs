using System.Globalization;
using Companion.Contracts;
using Companion.Contracts.Vision;
using Companion.Vision.Internal;

namespace Companion.TemplateCutter;

/// <summary>
/// Command line:
///   seed --proofs &lt;proofs\out&gt; --templates &lt;companion\templates&gt;
///       cuts the SPEC 14 seed set into templates\staging\.
///   cut --templates &lt;dir&gt; --source &lt;png&gt; --layout full|crop|half|window [--offset x,y] --rect l,t,r,b --id &lt;id&gt;
///       [--status provisional|verified] [--mask l,t,r,b]... [--hud collapsed|expanded] [--tags nav,toggle,...]
///       [--radius n] [--threshold x] [--margin x] [--fire down|up] [--gate none|crafting|homestead] [--build fp]
///       cuts one template (e.g. from Calibrate's expanded.png: --layout crop --offset 1440,0).
///   masks --proofs &lt;proofs\out&gt; --templates &lt;companion\templates&gt;
///       derives the per-pixel masks of <see cref="StableMask.Requests"/> (see-through HUD discs) from the committed
///       templates and the right-HUD strips into templates\staging\&lt;id&gt;.mask.png.
/// Exit codes: 0 ok, 1 usage, 2 refused.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                return Usage();
            }

            var opts = ParseOptions(args.Skip(1).ToArray());
            return args[0] switch
            {
                "seed" => Seed(opts),
                "cut" => CutOne(opts),
                "masks" => Masks(opts),
                _ => Usage(),
            };
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine("usage error: " + e.Message);
            return 1;
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }

    private static int Seed(Dictionary<string, List<string>> o)
    {
        var proofs = Required(o, "proofs");
        var templates = Required(o, "templates");
        var results = new List<CutResult>();
        foreach (var (file, req) in TemplateCutterCore.SeedRequests())
        {
            var r = TemplateCutterCore.Cut(File.ReadAllBytes(Path.Combine(proofs, file)), req);
            if (!r.Ok)
            {
                Console.Error.WriteLine($"refused {req.Id}: {r.Refusal}");
                return 2;
            }

            results.Add(r);
        }

        foreach (var r in results)
        {
            Write(templates, r);
        }

        return 0;
    }

    private static int CutOne(Dictionary<string, List<string>> o)
    {
        var templates = Required(o, "templates");
        var source = Required(o, "source");
        var layout = Required(o, "layout") switch
        {
            "full" => SourceLayout.ClientFull,
            "crop" => SourceLayout.ClientCropFull,
            "half" => SourceLayout.ClientHalf,
            "window" => SourceLayout.WindowHalf,
            var x => throw new FormatException("layout " + x),
        };
        var req = new CutRequest(
            Required(o, "id"),
            Path.GetFileName(source),
            layout,
            o.ContainsKey("offset") ? Point(Required(o, "offset")) : default,
            Rect(Required(o, "rect")),
            Optional(o, "status", "provisional") == "verified" ? TemplateStatus.Verified : TemplateStatus.Provisional,
            o.GetValueOrDefault("mask")?.Select(Rect).ToArray() ?? [],
            Optional(o, "hud", "") switch
            {
                "" => null,
                "collapsed" => HudState.Collapsed,
                "expanded" => HudState.Expanded,
                var x => throw new FormatException("hud " + x),
            },
            Enum.Parse<TargetTags>(Optional(o, "tags", "none"), ignoreCase: true),
            int.Parse(Optional(o, "radius", "24"), CultureInfo.InvariantCulture),
            double.Parse(Optional(o, "threshold", "0.90"), CultureInfo.InvariantCulture),
            double.Parse(Optional(o, "margin", "0.10"), CultureInfo.InvariantCulture),
            Enum.Parse<FireOn>(Optional(o, "fire", "down"), ignoreCase: true),
            Enum.Parse<AutomationSwitch>(Optional(o, "gate", "none"), ignoreCase: true),
            null,
            o.ContainsKey("build") ? Required(o, "build") : null);
        var r = TemplateCutterCore.Cut(File.ReadAllBytes(source), req);
        if (!r.Ok)
        {
            Console.Error.WriteLine($"refused {req.Id}: {r.Refusal}");
            return 2;
        }

        Write(templates, r);
        return 0;
    }

    private static int Masks(Dictionary<string, List<string>> o)
    {
        var proofs = Required(o, "proofs");
        var templates = Required(o, "templates");
        var defs = TemplateCutterCore.FromJson(File.ReadAllText(Path.Combine(templates, "templates.json"))).ToDictionary(d => d.Id, StringComparer.Ordinal);
        foreach (var req in StableMask.Requests())
        {
            if (!defs.TryGetValue(req.TemplateId, out var def))
            {
                Console.Error.WriteLine($"refused {req.TemplateId}: not in templates.json");
                return 2;
            }

            var template = PngCodec.Decode(File.ReadAllBytes(Path.Combine(templates, Path.GetFileName(def.ImageFile))));
            var crops = new List<BgraImage>();
            foreach (var capture in req.Captures)
            {
                var (crop, center, score) = StableMask.AlignedCrop(File.ReadAllBytes(Path.Combine(proofs, capture)), def, template);
                Console.WriteLine($"{def.Id}: {capture} aligned at ({center.X},{center.Y}) score {score.ToString("F4", CultureInfo.InvariantCulture)}");
                crops.Add(crop);
            }

            var keep = StableMask.Derive(template, crops, def.MaskRects ?? []);
            var path = TemplateCutterCore.StagingPath(templates, req.MaskFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, StableMask.Encode(keep, template.Width, template.Height));
            Console.WriteLine($"mask {def.Id}: keeps {keep.Count(k => k)}/{keep.Length} px -> staging\\{req.MaskFile}");
        }

        return 0;
    }

    private static void Write(string templatesDir, CutResult r)
    {
        var def = r.Definition!;
        var pngPath = TemplateCutterCore.StagingPath(templatesDir, def.ImageFile);
        var jsonPath = TemplateCutterCore.StagingPath(templatesDir, "templates.json");
        Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
        File.WriteAllBytes(pngPath, r.Png!);
        var existing = File.Exists(jsonPath) ? TemplateCutterCore.FromJson(File.ReadAllText(jsonPath)) : [];
        var merged = existing.Where(d => d.Id != def.Id).Append(def);
        File.WriteAllText(jsonPath, TemplateCutterCore.ToJson(merged));
        Console.WriteLine($"cut {def.Id} {def.Status} scale {def.SourceScale} centre ({def.ExpectedCenter.X},{def.ExpectedCenter.Y}) -> staging\\{def.ImageFile}");
    }

    private static Dictionary<string, List<string>> ParseOptions(string[] a)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= a.Length)
            {
                throw new FormatException("bad option " + a[i]);
            }

            var key = a[i][2..];
            if (!d.TryGetValue(key, out var list))
            {
                d[key] = list = [];
            }

            list.Add(a[++i]);
        }

        return d;
    }

    private static string Required(Dictionary<string, List<string>> o, string key) =>
        o.TryGetValue(key, out var v) && v.Count > 0 ? v[^1] : throw new FormatException("missing --" + key);

    private static string Optional(Dictionary<string, List<string>> o, string key, string fallback) =>
        o.TryGetValue(key, out var v) && v.Count > 0 ? v[^1].ToLowerInvariant() : fallback;

    private static PointI Point(string s)
    {
        var p = s.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return p.Length == 2 ? new PointI(p[0], p[1]) : throw new FormatException("point " + s);
    }

    private static RectI Rect(string s)
    {
        var p = s.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return p.Length == 4 ? new RectI(p[0], p[1], p[2], p[3]) : throw new FormatException("rect " + s);
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: Companion.TemplateCutter seed --proofs <dir> --templates <dir>");
        Console.Error.WriteLine("       Companion.TemplateCutter cut --templates <dir> --source <png> --layout full|crop|half|window --rect l,t,r,b --id <id> [options]");
        Console.Error.WriteLine("       Companion.TemplateCutter masks --proofs <dir> --templates <dir>");
        return 1;
    }
}
