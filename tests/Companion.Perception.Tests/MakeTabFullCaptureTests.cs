using System.Globalization;
using Companion.Contracts;
using Companion.Contracts.Perception;
using Companion.Contracts.Vision;
using Companion.Perception.Internal;
using Companion.Vision.Internal;

namespace Companion.Perception.Tests;

/// <summary>
/// The Make-tab readers (craft.pairs, craft.product) on every FULL-scale Make capture (tests\TestData\images, copies
/// of proofs\out; ground truth read off the images): favourites 1-5 of 2026-10-15 01:46 and the live White Toast of
/// 2026-10-14, with the real templates, the real Windows OCR and the built-in digit fonts. Every value is right or
/// Unknown, never wrong; the values that read Ok today are asserted one by one, so a font or reader change that loses
/// one fails here. Read-only; no window is touched.
/// </summary>
[TestClass]
public sealed class MakeTabFullCaptureTests
{
    private const string MakeView = "UI_LIFESKILL_MAKE_VIEW";

    /// <summary>Ground truth of the six full-scale Make captures (read off the images).</summary>
    internal static readonly IReadOnlyList<Truth> Captures =
    [
        new("make_fav1_whitetoast_full.png", [1088, 1220, 1352], [(40, 1), (1, 3), (340, 3)], 25, null, "White Toast"),
        new("make_fav2_full.png", [1088, 1220, 1352], [(406, 3), (614, 3), (13, 1)], 26, null, "Lime Water"),
        new("make_fav3_full.png", [1022, 1154, 1286, 1418], [(340, 6), (97, 1), (19, 1), (300, 5)], 1, (0, 300), null),
        new("make_fav4_full.png", [1022, 1154, 1286, 1418], [(500, 6), (406, 6), (97, 1), (157, 5)], 11, (6, 300), null),
        new("make_fav5_full.png", [1022, 1154, 1286, 1418], [(406, 6), (614, 3), (1, 1), (300, 5)], 0, (5, 300), "Vanilla Cream"),
        new("live_ability_make_full.png", [1088, 1220, 1352], [(40, 1), (45, 3), (340, 3)], 25, null, "White Toast"),
    ];

    public TestContext TestContext { get; set; } = null!;

    private static Frame Frame(string file) => BuiltInFontTests.Capture(file);

    private static ReadValue V(ScreenReadResult r, string region) => MakeTabReaderTests.V(r, region);

    private void RequireOcr()
    {
        if (!MakeTabReaderTests.Ocr.IsAvailable)
        {
            Assert.Inconclusive("Windows OCR for en-US is not installed on this PC");
        }
    }

    private string Dump(string file, ScreenReadResult r)
    {
        var s = string.Join("; ", r.Values.Select(v => $"{v.RegionId} {v.Status} {v.Value}/{v.Second} '{v.Text}' c{v.Confidence} g{v.GlyphScore} {v.DetailCode}"));
        TestContext.WriteLine($"{file} {r.ReaderId} {r.Status}: {s}");
        return s;
    }

    /// <summary>Reads both readers on a capture and checks every value against the truth; returns the Ok value ids.</summary>
    private List<string> ReadAndCheck(Truth t, ScreenReader reader, Frame frame)
    {
        var pairs = reader.Read(MakeTabReaderTests.Reader("craft.pairs"), frame, Support.Ui(MakeView));
        var product = reader.Read(MakeTabReaderTests.Reader("craft.product"), frame, Support.Ui(MakeView));
        Dump(t.File, pairs);
        Dump(t.File, product);
        var ok = new List<string>();

        // the tile row is found where it is, with one key per material
        Assert.AreEqual((ReadStatus.Ok, (long?)t.Lefts.Length), (V(pairs, "materials").Status, V(pairs, "materials").Value), t.File);
        for (var k = 1; k <= t.Lefts.Length; k++)
        {
            var icon = V(pairs, "icon." + k);
            Assert.IsTrue(icon.Status == ReadStatus.Ok && IconFingerprint.IsKey(icon.Text), $"{t.File} icon.{k}");
            Assert.IsTrue(icon.Rect is { } tile && Math.Abs(tile.Left - t.Lefts[k - 1]) <= 2 && tile.Width is >= 85 and <= 90, $"{t.File} icon.{k} rect {icon.Rect}");
            var pair = V(pairs, "pair." + k);
            Assert.AreEqual(ScreenReader.PairUnder(icon.Rect!.Value), pair.Rect, $"{t.File} pair.{k} is read under its tile");
            Assert.IsNotNull(pair.GlyphScore, $"{t.File} pair.{k} carries its glyph score");
            if (pair.Status == ReadStatus.Ok)
            {
                Assert.AreEqual(t.Pairs[k - 1], (pair.Value!.Value, pair.Second!.Value), $"{t.File} pair.{k}: never a wrong number");
                ok.Add("pair." + k);
            }
        }

        Assert.IsFalse(pairs.Values.Any(v => v.RegionId == "icon." + (t.Lefts.Length + 1)), t.File);
        Assert.AreEqual(t.Lefts.Length, pairs.Values.Where(v => v.RegionId.StartsWith("icon.", StringComparison.Ordinal)).Select(v => v.Text).Distinct(StringComparer.Ordinal).Count(), $"{t.File}: distinct materials");

        // no capture shows the "Requires" line: its place is plain parchment
        var requires = V(pairs, "requires");
        Assert.AreEqual((ReadStatus.Ok, (long?)null, "absent"), (requires.Status, requires.Value, requires.DetailCode), t.File);

        var name = V(product, "product.name");
        if (t.Title is null)
        {
            Assert.AreEqual((ReadStatus.Unknown, (string?)null, "text-truncated"), (name.Status, name.Text, name.DetailCode), $"{t.File}: a scrolled title is cut by its box: never a part of a name");
        }
        else if (name.Status == ReadStatus.Ok)
        {
            Assert.AreEqual(t.Title, name.Text, t.File);
            ok.Add("product.name");
        }

        var owned = V(product, "product.owned");
        Assert.IsNotNull(owned.GlyphScore, t.File);
        if (owned.Status == ReadStatus.Ok)
        {
            Assert.AreEqual((long?)t.Owned, owned.Value, $"{t.File} owned");
            ok.Add("product.owned");
        }

        var counter = V(product, "product.counter");
        if (t.Counter is null)
        {
            Assert.AreEqual((ReadStatus.Ok, (long?)null, "absent"), (counter.Status, counter.Value, counter.DetailCode), $"{t.File}: no counter bar");
        }
        else
        {
            Assert.AreNotEqual("absent", counter.DetailCode, $"{t.File}: the bar is there");
            if (counter.Status == ReadStatus.Ok)
            {
                Assert.AreEqual(t.Counter.Value, (counter.Value!.Value, counter.Second!.Value), $"{t.File} counter");
                ok.Add("product.counter");
            }
        }

        return ok;
    }

    [TestMethod]
    public void EveryValueOnEveryFullCapture_IsRightOrUnknown_AndTheseReadOk()
    {
        RequireOcr();
        var reader = MakeTabReaderTests.RealReader();
        var report = new List<string>();
        foreach (var t in Captures)
        {
            var ok = ReadAndCheck(t, reader, Frame(t.File));
            report.Add($"{t.File}: {string.Join(", ", ok)}");

            // every pair, the owned count and the counter read Ok; the title when it is not scrolled
            var expected = Enumerable.Range(1, t.Lefts.Length).Select(k => "pair." + k.ToString(CultureInfo.InvariantCulture)).ToList();
            expected.Add("product.owned");
            if (t.Title is not null)
            {
                expected.Add("product.name");
            }

            if (t.Counter is not null)
            {
                expected.Add("product.counter");
            }

            CollectionAssert.AreEquivalent(expected, ok, $"{t.File}: Ok values");
        }

        TestContext.WriteLine(string.Join(Environment.NewLine, report));
    }

    [TestMethod]
    public void GarbledOcr_EveryNumberOfEveryCapture_IsReadFromStrongRealGlyphs()
    {
        // OCR that reads nothing useful: only the digits-only rule (every glyph >= 0.80 and 0.25 above any other
        // character) can accept a number. The real instances make every pair, badge and counter of the captures pass.
        var ocr = new FakeOcr { Text = { [Binarization.DarkOnLight] = "Aqn", [Binarization.LightOnDark] = string.Empty } };
        var reader = MakeTabReaderTests.RealReader(ocr);
        foreach (var t in Captures)
        {
            var pairs = reader.Read(MakeTabReaderTests.Reader("craft.pairs"), Frame(t.File), Support.Ui(MakeView));
            var product = reader.Read(MakeTabReaderTests.Reader("craft.product"), Frame(t.File), Support.Ui(MakeView));
            Dump(t.File, pairs);
            Dump(t.File, product);
            for (var k = 1; k <= t.Lefts.Length; k++)
            {
                var v = V(pairs, "pair." + k);
                Assert.AreEqual((ReadStatus.Ok, "digits-only"), (v.Status, v.DetailCode), $"{t.File} pair.{k}");
                Assert.AreEqual(t.Pairs[k - 1], (v.Value!.Value, v.Second!.Value), $"{t.File} pair.{k}");
                Assert.IsTrue(v.GlyphScore >= DigitTemplateReader.StrongGlyphScore, $"{t.File} pair.{k} g{v.GlyphScore}");
            }

            var owned = V(product, "product.owned");
            Assert.AreEqual((ReadStatus.Ok, (long?)t.Owned, "digits-only"), (owned.Status, owned.Value, owned.DetailCode), $"{t.File} owned");
            if (t.Counter is { } c)
            {
                var counter = V(product, "product.counter");
                Assert.AreEqual((ReadStatus.Ok, (long?)c.A, (long?)c.B, "digits-only"), (counter.Status, counter.Value, counter.Second, counter.DetailCode), $"{t.File} counter");
            }

            Assert.AreNotEqual(ReadStatus.Ok, V(product, "product.name").Status, "text never falls back to templates");
        }
    }

    [TestMethod]
    public void EveryRealGlyphInstance_ReadsItsOwnCharacter_Strongly_InEveryFont()
    {
        // The pair, badge and counter texts of every capture, read with their own font: every glyph is its character,
        // at >= 0.89 and at least 0.35 above any other character (measured 2026-10-15; the digits-only rule needs 0.80 / 0.25).
        var digits = new DigitTemplateReader();
        var vision = MakeTabReaderTests.RealVision.Value;
        var cases = new List<(string File, string Font, RectI Rect, string Text)>();
        foreach (var t in Captures)
        {
            cases.AddRange(t.Lefts.Select((left, i) => (t.File, BuiltInDigitFonts.ParchmentPair, ScreenReader.PairUnder(new RectI(left, 674, left + 87, 763)), $"{t.Pairs[i].Owned}/{t.Pairs[i].Required}")));
            cases.Add((t.File, BuiltInDigitFonts.BadgeDark, new RectI(1198, 416, 1262, 444), t.Owned.ToString(CultureInfo.InvariantCulture)));
            if (t.Counter is { } c)
            {
                cases.Add((t.File, BuiltInDigitFonts.CounterDark, new RectI(1086, 506, 1206, 532), $"{c.A}/{c.B}"));
            }
        }

        foreach (var (file, font, rect, text) in cases)
        {
            var d = digits.ReadDetailed(vision.Crop(Frame(file), rect), font);
            TestContext.WriteLine($"{file} {font} '{text}': '{d.Text}' min {d.MinGlyphScore:F3} margin {d.MinMargin:F3}");
            Assert.AreEqual((true, text), (d.Ok, d.Text), $"{file} {font} '{text}'");
            Assert.IsTrue(d.MinGlyphScore >= 0.89 && d.MinMargin >= 0.35, $"{file} {font} '{text}': {d}");
        }
    }

    [TestMethod]
    public void AShiftedFourMaterialPanel_GivesTheSameValuesAndKeys()
    {
        RequireOcr();
        var reader = MakeTabReaderTests.RealReader();
        var t = Captures.Single(c => c.File == "make_fav5_full.png");
        var at = reader.Read(MakeTabReaderTests.Reader("craft.pairs"), Frame(t.File), Support.Ui(MakeView));
        foreach (var (dx, dy) in new[] { (2, -1), (-3, 2) })
        {
            var frame = SourceImages.ToFrame(Support.Image(t.File), SourceLayout.ClientCropFull, new PointI(dx, dy));
            var pairs = reader.Read(MakeTabReaderTests.Reader("craft.pairs"), frame, Support.Ui(MakeView));
            Dump($"{t.File} ({dx},{dy})", pairs);
            for (var k = 1; k <= 4; k++)
            {
                Assert.AreEqual(0, IconFingerprint.Distance(V(at, "icon." + k).Text!, V(pairs, "icon." + k).Text!), $"icon.{k}");
                Assert.AreEqual(V(at, "icon." + k).Rect!.Value.Offset(dx, dy), V(pairs, "icon." + k).Rect, $"icon.{k} rect follows the panel");
                var pair = V(pairs, "pair." + k);
                Assert.AreEqual((ReadStatus.Ok, t.Pairs[k - 1]), (pair.Status, (pair.Value!.Value, pair.Second!.Value)), $"pair.{k} ({dx},{dy})");
            }

            Assert.AreEqual(new PointI(1270 + dx, 641 + dy), pairs.Anchors!["craft.materials.header"], "the located anchor is reported");
        }
    }

    [TestMethod]
    public void TheFontsAndRegionsOfTheProductReader_AreTheMeasuredOnes()
    {
        var product = MakeTabReaderTests.Reader("craft.product");
        var header = new PointI(1270, 641);
        var name = product.Regions.Single(r => r.Id == "product.name");
        Assert.AreEqual(new RectI(1325, 220, 1580, 256), name.ClientRect.Offset(header.X, header.Y), "the title box starts on the column that cuts a scrolled title");
        var counter = product.Regions.Single(r => r.Id == "product.counter");
        Assert.AreEqual(BuiltInDigitFonts.CounterDark, counter.FontStyle);
        Assert.AreEqual(new RectI(1086, 506, 1206, 532), counter.ClientRect.Offset(header.X, header.Y), "the counter text, outline rows included (the outline is lighter than the ink)");
        Assert.AreEqual(BuiltInDigitFonts.BadgeDark, product.Regions.Single(r => r.Id == "product.owned").FontStyle);
        Assert.AreEqual(Binarization.DarkOnLight, ScreenReader.PrimaryBinarization(BuiltInDigitFonts.CounterDark));
        var requires = MakeTabReaderTests.Reader("craft.pairs").Regions.Single(r => r.Id == "requires");
        Assert.AreEqual(BuiltInDigitFonts.ParchmentPair, requires.FontStyle, "no capture shows the Requires line: it keeps the pair font");
    }

    [TestMethod]
    public void AScrolledTitleCutBetweenTwoWords_IsUnknown_NeverAShorterName()
    {
        // Lime Water's own title ("Lime Water", ink x 1329..1507) moved right so that "Lime" ends just before the box's
        // right edge and "Water" lies beyond it: the box shows one whole word, but it ends within the margin.
        var f = Frame("make_fav2_full.png");
        var bgra = (byte[])f.Bgra.Clone();
        var blank = f.Bgra.AsSpan((240 * f.Stride) + (1560 * 4), 4).ToArray(); // parchment right of the title
        for (var y = 218; y < 258; y++)
        {
            for (var x = 1325; x < 1580; x++)
            {
                Array.Copy(blank, 0, bgra, (y * f.Stride) + (x * 4), 4);
            }

            // "Lime" (x 1329..1395) placed at x 1500..1566
            Array.Copy(f.Bgra, (y * f.Stride) + (1329 * 4), bgra, (y * f.Stride) + (1500 * 4), 67 * 4);
        }

        // an OCR that would agree on the visible word: the title must be refused before it is asked
        var ocr = new FakeOcr { Text = { [Binarization.DarkOnLight] = "Lime", [Binarization.LightOnDark] = "Lime" } };
        var product = MakeTabReaderTests.Reader("craft.product");
        var titleOnly = product with { Regions = [.. product.Regions.Where(g => g.Id == "product.name")] };
        var r = MakeTabReaderTests.RealReader(ocr).Read(titleOnly, f with { Bgra = bgra }, Support.Ui(MakeView));
        var name = V(r, "product.name");
        Assert.AreEqual((ReadStatus.Unknown, "text-truncated"), (name.Status, name.DetailCode), Dump("lime-shifted", r));
        Assert.IsNull(name.Text);
        Assert.IsEmpty(ocr.Calls, "the title is refused before any OCR");

        // the same word at its normal place reads (the margin only refuses text near the edge)
        var normal = (byte[])bgra.Clone();
        for (var y = 218; y < 258; y++)
        {
            Array.Copy(bgra, (y * f.Stride) + (1500 * 4), normal, (y * f.Stride) + (1329 * 4), 67 * 4);
            for (var x = 1500; x < 1580; x++)
            {
                Array.Copy(blank, 0, normal, (y * f.Stride) + (x * 4), 4);
            }
        }

        var back = MakeTabReaderTests.RealReader(ocr).Read(titleOnly, f with { Bgra = normal }, Support.Ui(MakeView));
        Assert.AreEqual((ReadStatus.Ok, "Lime"), (V(back, "product.name").Status, V(back, "product.name").Text), Dump("lime-normal", back));
    }

    /// <summary>One capture's ground truth.</summary>
    /// <param name="File">Capture in tests\TestData\images.</param>
    /// <param name="Lefts">Tile left edges (3 or 4 materials).</param>
    /// <param name="Pairs">Owned/required per material.</param>
    /// <param name="Owned">Owned count of the product.</param>
    /// <param name="Counter">The counter under the product picture, or null when none is shown.</param>
    /// <param name="Title">The title, or null when it is scrolled (cut by its box).</param>
    internal sealed record Truth(string File, int[] Lefts, (long Owned, long Required)[] Pairs, long Owned, (long A, long B)? Counter, string? Title);
}
