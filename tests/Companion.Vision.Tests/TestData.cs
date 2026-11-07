using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Vision;
using Companion.Vision;
using Companion.Vision.Internal;

namespace Companion.Vision.Tests;

/// <summary>
/// Locates companion\templates, proofs\out and tests\TestData\images (read-only) and builds catalogs/frames from them.
/// </summary>
internal static class TestData
{
    private static readonly Lazy<string> CompanionRootLazy = new(FindCompanionRoot);

    public static string CompanionRoot => CompanionRootLazy.Value;

    public static string TemplatesDir => Path.Combine(CompanionRoot, "templates");

    public static string ProofsOut => Path.GetFullPath(Path.Combine(CompanionRoot, "..", "proofs", "out"));

    /// <summary>
    /// tests\TestData\images: byte-identical copies of the captures the image tests depend on (provenance in its
    /// README.md), so a proof run that rewrites proofs\out never changes what a test sees.
    /// </summary>
    public static string ImagesDir => Path.Combine(CompanionRoot, "tests", "TestData", "images");

    public static byte[] Proof(string name) => Capture(Path.Combine(ProofsOut, name));

    /// <summary>A tests-owned capture from <see cref="ImagesDir"/>.</summary>
    public static byte[] Image(string name) => Capture(Path.Combine(ImagesDir, name));

    /// <summary>Reads a capture; the author's full game screenshots are not in the public repo, so a missing one makes the test inconclusive.</summary>
    public static byte[] Capture(string path)
    {
        if (!File.Exists(path))
        {
            Assert.Inconclusive("private capture not included in the public repository: " + Path.GetFileName(path));
        }

        return File.ReadAllBytes(path);
    }


    public static TemplateDefinition[] LoadDefinitions() =>
        JsonSerializer.Deserialize<TemplateDefinition[]>(File.ReadAllText(Path.Combine(TemplatesDir, DataFileNames.Templates)), ContractJson.Options)
        ?? throw new InvalidOperationException("templates.json is empty");

    /// <summary>The real catalog: templates.json + PNGs, sealed.</summary>
    public static TemplateCatalog LoadCatalog()
    {
        var c = new TemplateCatalog();
        foreach (var d in LoadDefinitions())
        {
            var mask = d.MaskFile is null ? null : File.ReadAllBytes(Path.Combine(TemplatesDir, d.MaskFile));
            c.Add(d, File.ReadAllBytes(Path.Combine(TemplatesDir, d.ImageFile)), mask, "core");
        }

        c.Seal();
        return c;
    }

    /// <summary>c3b right strip (full scale) pasted at x=1440 into a 1920x1009 frame (collapsed HUD).</summary>
    public static Frame C3bStripFrame(int dx = 0, int dy = 0) => StripFrame("c3b_hud_right_x1440.png", dx, dy);

    /// <summary>A full-scale right-HUD strip (client x 1440, 480x1009) pasted into a 1920x1009 frame.</summary>
    public static Frame StripFrame(string name, int dx = 0, int dy = 0) =>
        SourceImages.ToFrame(Proof(name), SourceLayout.ClientCropFull, new PointI(1440 + dx, dy));

    /// <summary>A half-scale client capture upscaled x2.</summary>
    public static Frame HalfClientFrame(string name) => SourceImages.ToFrame(Proof(name), SourceLayout.ClientHalf);

    /// <summary>A half-scale window capture (968x524) upscaled x2 and cropped to the client.</summary>
    public static Frame HalfWindowFrame(string name) => SourceImages.ToFrame(Proof(name), SourceLayout.WindowHalf);

    private static string FindCompanionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SPEC.md")) && Directory.Exists(Path.Combine(dir.FullName, "templates")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("companion root (SPEC.md + templates\\) not found above " + AppContext.BaseDirectory);
    }
}
