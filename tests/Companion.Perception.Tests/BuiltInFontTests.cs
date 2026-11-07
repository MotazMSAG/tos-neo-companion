using System.Text;
using Companion.Contracts;
using Companion.Contracts.Vision;
using Companion.Perception.Internal;
using Companion.Vision;
using Companion.Vision.Internal;

namespace Companion.Perception.Tests;

/// <summary>
/// Provenance of the compiled-in digit fonts: every glyph is re-cut from its full-scale capture (the tests' own copies
/// in tests\TestData\images, read-only) and must equal the embedded mask; every font reads its own source texts back;
/// the borrowed glyphs are exactly the listed ones (a new real instance must replace a borrowed one deliberately).
/// </summary>
[TestClass]
public sealed class BuiltInFontTests
{
    private static readonly VisionEngine Vision = new(new TemplateCatalog());

    private static readonly Dictionary<string, Frame> Frames = new(StringComparer.Ordinal);

    public TestContext TestContext { get; set; } = null!;

    /// <summary>A full-scale capture from tests\TestData\images (cached).</summary>
    internal static Frame Capture(string name)
    {
        lock (Frames)
        {
            if (!Frames.TryGetValue(name, out var f))
            {
                f = SourceImages.ToFrame(Support.Image(name), SourceLayout.ClientFull);
                Frames[name] = f;
            }

            return f;
        }
    }

    private static string Recut(GlyphSource s)
    {
        var crop = s.FromTile
            ? IconGrid.FindCount(Capture(s.Capture), s.Region)?.Image ?? throw new AssertFailedException($"{s.Font} '{s.Char}': no count in the tile {s.Region} of {s.Capture}")
            : Vision.Crop(Capture(s.Capture), s.Region);
        var glyphs = GlyphCutter.Cut(crop, s.Text);
        Assert.IsNotNull(glyphs, $"{s.Font} '{s.Char}': '{s.Text}' does not segment into {s.Text.Length} glyphs at {s.Region}");
        var g = glyphs[s.Index];
        Assert.AreEqual(s.Char, g.Char, $"{s.Font}: index {s.Index} of '{s.Text}'");
        return g.Mask;
    }

    [TestMethod]
    public void MasksMatchTheCaptures()
    {
        var masks = BuiltInDigitFonts.Masks;
        Assert.HasCount(BuiltInDigitFonts.Sources.Count, masks);
        for (var i = 0; i < masks.Count; i++)
        {
            var s = BuiltInDigitFonts.Sources[i];
            Assert.AreEqual((s.Font, s.Char), (masks[i].Font, masks[i].Char), $"entry {i}");
            Assert.AreEqual(Recut(s), masks[i].Mask, $"{s.Font} '{s.Char}' differs from {s.Capture} {s.Region}: run PrintMasks and paste");
        }

        // every instance is a different mask (a copy adds nothing)
        var duplicates = masks.GroupBy(m => m).Where(g => g.Count() > 1).Select(g => $"{g.Key.Font} '{g.Key.Char}'").ToList();
        Assert.IsEmpty(duplicates, string.Join(", ", duplicates));
    }

    [TestMethod]
    public void FontsAreCompleteDigitSets_WithSeveralRealInstances()
    {
        var fonts = BuiltInDigitFonts.Fonts;
        CollectionAssert.AreEquivalent(new[] { BuiltInDigitFonts.ParchmentPair, BuiltInDigitFonts.BadgeDark, BuiltInDigitFonts.CounterDark, BuiltInDigitFonts.StockCount }, fonts.Keys.ToArray());
        CollectionAssert.AreEquivalent("0123456789/".ToCharArray(), fonts[BuiltInDigitFonts.ParchmentPair].Select(g => g.Char).Distinct().ToArray());
        CollectionAssert.AreEquivalent("0123456789".ToCharArray(), fonts[BuiltInDigitFonts.BadgeDark].Select(g => g.Char).Distinct().ToArray());
        CollectionAssert.AreEquivalent("0123456789/".ToCharArray(), fonts[BuiltInDigitFonts.CounterDark].Select(g => g.Char).Distinct().ToArray());
        CollectionAssert.AreEquivalent("0123456789".ToCharArray(), fonts[BuiltInDigitFonts.StockCount].Select(g => g.Char).Distinct().ToArray());
        Assert.IsTrue(BuiltInDigitFonts.Sources.All(s => s.FromTile == (s.Font == BuiltInDigitFonts.StockCount)), "only the Stock counts are cut from their tiles' clean count images");
        Assert.IsTrue(BuiltInDigitFonts.Sources.All(s => BuiltInDigitFonts.Captures.Contains(s.Capture)), "full-scale captures in tests\\TestData\\images only");
        foreach (var capture in BuiltInDigitFonts.Captures)
        {
            Support.Capture(Path.Combine(Support.ImagesDir, capture));
        }

        foreach (var g in fonts.Values.SelectMany(f => f))
        {
            Assert.IsTrue(g.Image.Width >= 7 && g.Image.Height >= 20 && g.Image.Height <= 40, $"{g.Image.Width}x{g.Image.Height}");
        }

        // several real instances per character where the captures show them (subpixel phase)
        foreach (var font in fonts.Keys)
        {
            foreach (var group in BuiltInDigitFonts.Sources.Where(s => s.Font == font && !s.Borrowed).GroupBy(s => s.Char))
            {
                Assert.IsLessThanOrEqualTo(4, group.Count(), $"{font} '{group.Key}'");
            }
        }
    }

    /// <summary>
    /// The borrowed glyphs (cut from another text of the game because no capture shows the character in the font's own
    /// text): exactly these. A real instance of one of them must replace it (delete the borrowed entries).
    /// </summary>
    [TestMethod]
    public void BorrowedGlyphs_AreExactlyTheCharactersNoCaptureShows()
    {
        string Borrowed(string font) => new(BuiltInDigitFonts.Sources.Where(s => s.Font == font && s.Borrowed).Select(s => s.Char).Distinct().Order().ToArray());
        string Real(string font) => new(BuiltInDigitFonts.Sources.Where(s => s.Font == font && !s.Borrowed).Select(s => s.Char).Distinct().Order().ToArray());
        Assert.AreEqual("28", Borrowed(BuiltInDigitFonts.ParchmentPair), "the pairs of the captures show no 2 and no 8");
        Assert.AreEqual("/01345679", Real(BuiltInDigitFonts.ParchmentPair));
        Assert.AreEqual("34789", Borrowed(BuiltInDigitFonts.BadgeDark), "the badges show 0, 1, 11, 25 and 26 only");
        Assert.AreEqual("01256", Real(BuiltInDigitFonts.BadgeDark));
        Assert.AreEqual("124789", Borrowed(BuiltInDigitFonts.CounterDark), "the counters show 0/300, 5/300 and 6/300 only");
        Assert.AreEqual("/0356", Real(BuiltInDigitFonts.CounterDark));
        Assert.AreEqual(string.Empty, Borrowed(BuiltInDigitFonts.StockCount), "the Stock captures show every digit");
        Assert.AreEqual("0123456789", Real(BuiltInDigitFonts.StockCount));
        foreach (var font in BuiltInDigitFonts.Fonts.Keys)
        {
            var overlap = Borrowed(font).Intersect(Real(font), EqualityComparer<char>.Default).ToArray();
            Assert.IsEmpty(overlap, $"{font}: a real instance exists, delete the borrowed '{new string(overlap)}'");
        }
    }

    /// <summary>Prints the C# data block of BuiltInDigitFonts (run after changing a source, paste the output).</summary>
    [TestMethod]
    public void PrintMasks()
    {
        static string Name(string font) => font switch
        {
            BuiltInDigitFonts.ParchmentPair => "ParchmentPair",
            BuiltInDigitFonts.BadgeDark => "BadgeDark",
            BuiltInDigitFonts.StockCount => "StockCount",
            _ => "CounterDark",
        };

        var sb = new StringBuilder();
        foreach (var s in BuiltInDigitFonts.Sources)
        {
            sb.AppendLine($"        ({Name(s.Font)}, '{s.Char}', \"{Recut(s)}\"),");
        }

        TestContext.WriteLine(sb.ToString());
        Assert.IsGreaterThan(0, sb.Length);
    }
}
