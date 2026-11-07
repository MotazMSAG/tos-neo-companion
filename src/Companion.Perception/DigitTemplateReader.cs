using Companion.Contracts;
using Companion.Contracts.Perception;
using Companion.Contracts.Vision;
using Companion.Perception.Internal;

namespace Companion.Perception;

/// <summary>Per-glyph detail of a digit read (unit-internal: the screen reader's digits-only rule needs the margins).</summary>
/// <param name="Ok">Every glyph matched at least <see cref="DigitTemplateReader.MinGlyphScore"/>.</param>
/// <param name="Text">Best characters ('?' below the floor).</param>
/// <param name="MinGlyphScore">Weakest best score.</param>
/// <param name="MinMargin">Smallest gap between a glyph's best character and its best OTHER character.</param>
/// <param name="Glyphs">Glyph count.</param>
internal sealed record DigitReadDetail(bool Ok, string Text, double MinGlyphScore, double MinMargin, int Glyphs);

/// <summary>
/// <see cref="IDigitReader"/>: reads digits by template, per font style (OCR-01). The crop is binarised (Otsu, the
/// minority class is ink), split into glyphs at empty columns, and every glyph is compared with the font's glyphs
/// (normalised NCC minus an aspect-ratio penalty); a font may hold several instances of a character (the score of a
/// character is its best instance). A glyph below <see cref="MinGlyphScore"/> makes the read not Ok: the reader never
/// guesses. Glyphs must come from FULL-scale captures. The parameterless constructor carries the built-in fonts cut
/// from the Make-tab captures of 2026-10-14/15 (<c>parchment-pair</c>: the owned/required pairs; <c>badge-dark</c>:
/// the product's owned-count badge; <c>counter-dark</c>: the counter under the product picture; see
/// <see cref="BuiltInDigitFonts"/> for the real and the borrowed glyphs); any other style has no glyphs and every read
/// of it is not Ok (the screen reader then returns Unknown). Thread-safe.
/// </summary>
public sealed class DigitTemplateReader : IDigitReader
{
    /// <summary>Characters a font may define.</summary>
    public const string AllowedGlyphs = "0123456789/KM";

    /// <summary>Minimum per-glyph score for an Ok read.</summary>
    internal const double MinGlyphScore = 0.75;

    /// <summary>Per-glyph score for a digits-only acceptance (OCR unreadable, see <see cref="ScreenReader"/>).</summary>
    internal const double StrongGlyphScore = 0.80;

    /// <summary>Per-glyph margin (best minus best other character) for a digits-only acceptance.</summary>
    internal const double StrongGlyphMargin = 0.25;

    private const int NormW = 12;
    private const int NormH = 18;

    private readonly IReadOnlyDictionary<string, IReadOnlyList<FontGlyph>> fonts;
    private readonly Lazy<Dictionary<string, Glyph[]>> prepared;

    /// <summary>A reader with the built-in digit fonts (cut at full scale from the live captures, compiled in).</summary>
    public DigitTemplateReader()
        : this(BuiltInDigitFonts.Fonts)
    {
    }

    /// <summary>A reader over glyph images per font style (one image per character, cut at full scale).</summary>
    /// <param name="fonts">Font style → character → glyph crop.</param>
    public DigitTemplateReader(IReadOnlyDictionary<string, IReadOnlyDictionary<char, GrayImage>> fonts)
        : this(Instances(fonts))
    {
    }

    /// <summary>A reader over glyph instances per font style (several images per character allowed).</summary>
    /// <param name="fonts">Font style → glyph instances.</param>
    internal DigitTemplateReader(IReadOnlyDictionary<string, IReadOnlyList<FontGlyph>> fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        this.fonts = fonts;
        prepared = new Lazy<Dictionary<string, Glyph[]>>(Prepare, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> FontStyles => fonts.Keys.ToArray();

    /// <inheritdoc />
    public DigitReadResult Read(GrayImage image, string fontStyle)
    {
        var d = ReadDetailed(image, fontStyle);
        return new DigitReadResult(d.Ok, d.Text, d.MinGlyphScore);
    }

    /// <summary>Same as <see cref="Read"/>, with the per-glyph margins.</summary>
    internal DigitReadDetail ReadDetailed(GrayImage image, string fontStyle)
    {
        ArgumentNullException.ThrowIfNull(image);
        var none = new DigitReadDetail(false, string.Empty, 0, 0, 0);
        if (fontStyle is null || !prepared.Value.TryGetValue(fontStyle, out var glyphs) || glyphs.Length == 0)
        {
            return none;
        }

        if (image.Width < 2 || image.Height < 2)
        {
            return none;
        }

        var segments = Segment(image);
        if (segments.Count == 0)
        {
            return none;
        }

        var text = new System.Text.StringBuilder();
        var min = double.MaxValue;
        var minMargin = double.MaxValue;
        foreach (var s in segments)
        {
            var best = (Char: '?', Score: double.MinValue);
            var scores = new Dictionary<char, double>();
            foreach (var g in glyphs)
            {
                var score = Compare(s, g);
                scores[g.Char] = scores.TryGetValue(g.Char, out var prev) ? Math.Max(prev, score) : score;
                if (score > best.Score)
                {
                    best = (g.Char, score);
                }
            }

            var other = scores.Where(kv => kv.Key != best.Char).Select(kv => kv.Value).DefaultIfEmpty(-1.0).Max();
            text.Append(best.Score >= MinGlyphScore ? best.Char : '?');
            min = Math.Min(min, best.Score);
            minMargin = Math.Min(minMargin, best.Score - other);
        }

        return new DigitReadDetail(min >= MinGlyphScore, text.ToString(), Math.Max(0, min), Math.Max(0, minMargin), segments.Count);
    }

    private static Dictionary<string, IReadOnlyList<FontGlyph>> Instances(IReadOnlyDictionary<string, IReadOnlyDictionary<char, GrayImage>> fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        return fonts.ToDictionary(
            f => f.Key,
            f => (IReadOnlyList<FontGlyph>)[.. f.Value.Select(g => new FontGlyph(g.Key, g.Value))],
            StringComparer.Ordinal);
    }

    private Dictionary<string, Glyph[]> Prepare()
    {
        var result = new Dictionary<string, Glyph[]>(StringComparer.Ordinal);
        foreach (var (style, set) in fonts)
        {
            var list = new List<Glyph>();
            foreach (var (ch, img) in set)
            {
                if (!AllowedGlyphs.Contains(ch, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"digit font '{style}': unsupported glyph '{ch}'");
                }

                var segs = Segment(img);
                if (segs.Count == 0)
                {
                    throw new ArgumentException($"digit font '{style}': glyph '{ch}' has no ink");
                }

                // a glyph image holds one character; merge accidental splits into one box
                var merged = segs.Count == 1 ? segs[0] : Merge(img, segs);
                list.Add(merged with { Char = ch });
            }

            result[style] = [.. list];
        }

        return result;
    }

    /// <summary>Binarises (minority class = ink) and splits into normalised glyphs.</summary>
    private static List<Glyph> Segment(GrayImage img)
    {
        var ink = GlyphSegmenter.InkMask(img);
        return GlyphSegmenter.Boxes(img, ink).Select(b => Normalize(ink, img.Width, b.Box, b.Ink)).ToList();
    }

    private static Glyph Merge(GrayImage img, List<Glyph> parts)
    {
        var ink = GlyphSegmenter.InkMask(img);
        var box = new RectI(parts.Min(p => p.X0), parts.Min(p => p.Y0), parts.Max(p => p.X0 + p.W), parts.Max(p => p.Y0 + p.H));
        return Normalize(ink, img.Width, box, parts.Sum(p => p.Ink));
    }

    private static Glyph Normalize(bool[] ink, int stride, RectI b, int count)
    {
        var w = b.Width;
        var h = b.Height;
        var box = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                box[y * w + x] = ink[(b.Top + y) * stride + b.Left + x] ? (byte)255 : (byte)0;
            }
        }

        var norm = OcrImageOps.Resize(new GrayImage(w, h, box), NormW, NormH);
        var v = new double[norm.Pixels.Length];
        double mean = 0;
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = norm.Pixels[i];
            mean += v[i];
        }

        mean /= v.Length;
        double energy = 0;
        for (var i = 0; i < v.Length; i++)
        {
            v[i] -= mean;
            energy += v[i] * v[i];
        }

        return new Glyph('?', b.Left, b.Top, w, h, count, v, Math.Sqrt(energy));
    }

    private static double Compare(Glyph s, Glyph g)
    {
        double ncc;
        if (s.Norm < 1e-6 && g.Norm < 1e-6)
        {
            ncc = 1.0; // both solid boxes (e.g. a bar-like '1' or '/')
        }
        else if (s.Norm < 1e-6 || g.Norm < 1e-6)
        {
            ncc = 0.0;
        }
        else
        {
            double dot = 0;
            for (var i = 0; i < s.Vector.Length; i++)
            {
                dot += s.Vector[i] * g.Vector[i];
            }

            ncc = dot / (s.Norm * g.Norm);
        }

        var ratio = (s.W / (double)s.H) / (g.W / (double)g.H);
        return ncc - 0.5 * Math.Abs(Math.Log(ratio));
    }

    private sealed record Glyph(char Char, int X0, int Y0, int W, int H, int Ink, double[] Vector, double Norm);
}
