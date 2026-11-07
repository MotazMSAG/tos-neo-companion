using System.Text;
using Companion.Contracts;
using Companion.Contracts.Perception;
using Companion.Contracts.Vision;
using Companion.Perception.Internal;
using Companion.Vision;
using Companion.Vision.Internal;

namespace Companion.Perception.Tests;

/// <summary>
/// Real Windows OCR (offline, built into Windows) on synthetic text and on the proof captures. The capture tests
/// REPORT accuracy (TestContext output) and assert only what is safe to promise: OCR runs, timeouts are honoured, and
/// the strict screen reader never returns a value without digit-template agreement.
/// </summary>
[TestClass]
public sealed class OcrTests
{
    private static readonly WinRtOcr Ocr = new();

    public TestContext TestContext { get; set; } = null!;

    private void RequireOcr()
    {
        if (!Ocr.IsAvailable)
        {
            Assert.Inconclusive("Windows OCR for en-US is not installed on this PC; languages: " + string.Join(",", WinRtOcr.AvailableLanguageTags()));
        }
    }

    [TestMethod]
    public void Languages_AreListed_AndEnUsIsAvailable()
    {
        var tags = WinRtOcr.AvailableLanguageTags();
        TestContext.WriteLine("OCR recogniser languages: " + string.Join(", ", tags) + "; max dimension " + WinRtOcr.MaxImageDimension);
        RequireOcr();
        Assert.IsTrue(tags.Count > 0);
    }

    [TestMethod]
    public void ZeroTimeout_GivesTimeout_WithoutBlocking()
    {
        var r = Ocr.Recognize(Support.Render("18/18"), new OcrOptions(), TimeSpan.Zero);
        Assert.AreEqual(OcrStatus.Timeout, r.Status);
        Assert.AreEqual(string.Empty, r.Text);
    }

    [TestMethod]
    public void TinyTimeoutOnALargeImage_ReturnsInTime()
    {
        RequireOcr();
        var big = Support.Render(string.Join(" ", Enumerable.Range(0, 40).Select(i => (i * 37).ToString())), 30f);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = Ocr.Recognize(big, new OcrOptions(Upscale: 3.0), TimeSpan.FromMilliseconds(5));
        sw.Stop();
        TestContext.WriteLine($"{r.Status} after {sw.ElapsedMilliseconds} ms");
        Assert.IsTrue(r.Status is OcrStatus.Timeout or OcrStatus.Ok);
        Assert.IsTrue(sw.ElapsedMilliseconds < 1500, sw.ElapsedMilliseconds.ToString());
    }

    [TestMethod]
    public void UnknownLanguage_IsUnavailable()
    {
        var r = Ocr.Recognize(Support.Render("12"), new OcrOptions(LanguageTag: "xx-XX"), TimeSpan.FromSeconds(2));
        Assert.AreEqual(OcrStatus.Unavailable, r.Status);
    }

    [TestMethod]
    public void SyntheticPairs_AreRead()
    {
        RequireOcr();
        var hits = 0;
        var cases = new[] { "18/18", "12/12", "16/16", "5/60", "4/10", "697", "55156", "14M" };
        foreach (var s in cases)
        {
            var r = Ocr.Recognize(Support.Render(s, 16f), new OcrOptions(Binarization: Binarization.DarkOnLight), TimeSpan.FromSeconds(3));
            var got = ValueParser.NormalizeNumeric(r.Text);
            TestContext.WriteLine($"{s,-8} -> '{r.Text}' ({r.Status}, {r.Elapsed.TotalMilliseconds:F0} ms)");
            if (got == s)
            {
                hits++;
            }
        }

        TestContext.WriteLine($"synthetic accuracy {hits}/{cases.Length}");
        Assert.IsTrue(hits >= cases.Length - 1, $"synthetic accuracy {hits}/{cases.Length}");
    }

    /// <summary>Lumi's Hut (c3_Ability.png, HALF-scale capture upscaled x2): product pairs and Managing pairs.</summary>
    [TestMethod]
    public void Accuracy_LumiHutPairs_c3Ability()
    {
        RequireOcr();
        var frame = SourceImages.ToFrame(Support.Proof("c3_Ability.png"), SourceLayout.ClientHalf);
        var cases = new (string Truth, RectI Rect)[]
        {
            ("18/18", new RectI(400, 934, 496, 968)),
            ("12/12", new RectI(554, 934, 650, 968)),
            ("16/16", new RectI(704, 934, 800, 968)),
            ("16/16", new RectI(856, 934, 954, 968)),
            ("16/16", new RectI(1010, 934, 1106, 968)),
            ("16/16", new RectI(1162, 934, 1258, 968)),
            ("5/60", new RectI(190, 608, 274, 644)),
            ("4/10", new RectI(684, 592, 770, 624)),
            ("2/4", new RectI(1204, 612, 1276, 648)),
        };
        Report("c3_Ability Lumi's Hut pairs", frame, cases, ParseRule.Pair, PairSemantics.StoredCapacity);
    }

    /// <summary>Bag (b_after_B.png, HALF-scale WINDOW capture upscaled x2): cell counts, currencies, combat power.</summary>
    [TestMethod]
    public void Accuracy_BagCounts_bAfterB()
    {
        RequireOcr();
        var frame = SourceImages.ToFrame(Support.Proof("b_after_B.png"), SourceLayout.WindowHalf);
        int[][] cols = [[508, 563], [571, 625], [633, 687], [695, 749], [757, 812]];
        int[][] rows = [[115, 170], [182, 238], [250, 305], [318, 373], [386, 442]];
        string[][] truth =
        [
            ["", "2", "", "56", "91"],
            ["", "", "", "4", "4"],
            ["6", "4", "2", "3", "8"],
            ["4", "15", "10", "", "10"],
            ["Lv60", "Lv65", "Lv65", "697", "17"],
        ];
        var cells = new List<(string, RectI)>();
        for (var r = 0; r < 5; r++)
        {
            for (var c = 0; c < 5; c++)
            {
                // half-window coords -> client: x*2-8, y*2-31; band = bottom 16 half px of the cell
                var rect = new RectI(cols[c][0] * 2 - 8, (rows[r][1] - 16) * 2 - 31, cols[c][1] * 2 - 8, (rows[r][1] + 1) * 2 - 31);
                cells.Add((truth[r][c], rect));
            }
        }

        Report("b_after_B bag cell counts", frame, cells, ParseRule.ItemCellCount, PairSemantics.None);

        var top = new (string Truth, RectI Rect)[]
        {
            ("55156", new RectI(498 * 2 - 8, 25 * 2 - 31, 546 * 2 - 8, 43 * 2 - 31)),
            ("14M", new RectI(600 * 2 - 8, 25 * 2 - 31, 634 * 2 - 8, 43 * 2 - 31)),
            ("542", new RectI(702 * 2 - 8, 25 * 2 - 31, 733 * 2 - 8, 43 * 2 - 31)),
            ("4865", new RectI(805 * 2 - 8, 25 * 2 - 31, 843 * 2 - 8, 43 * 2 - 31)),
            ("2749134", new RectI(256 * 2 - 8, 436 * 2 - 31, 360 * 2 - 8, 459 * 2 - 31)),
        };
        Report("b_after_B currencies and CP", frame, top, ParseRule.Abbreviated, PairSemantics.None);
    }

    /// <summary>The strict reader on the real Lumi capture: with no digit font it must say Unknown, never a value.</summary>
    [TestMethod]
    public void StrictReader_OnRealCapture_NeverGuesses()
    {
        RequireOcr();
        var frame = SourceImages.ToFrame(Support.Proof("c3_Ability.png"), SourceLayout.ClientHalf);
        var reader = Support.Reader("lumi.products.test", ReaderStatus.Ready, "UI_HOME_PET_VIEW", [],
            Support.Region("p1", ParseRule.Pair, PairSemantics.StoredCapacity, "parchment-dark", 0, 999, new RectI(400, 934, 496, 968)));
        var sr = new ScreenReader(new VisionEngine(new TemplateCatalog()), Ocr, new DigitTemplateReader());
        var r = sr.Read(reader, frame, Support.Ui("UI_HOME_PET_VIEW"));
        TestContext.WriteLine(string.Join("; ", r.Values.Select(v => $"{v.RegionId} {v.Status} {v.DetailCode}")));
        Assert.AreEqual(ReadStatus.Unknown, r.Status);
        Assert.IsNull(r.Values[0].Value);
        Assert.AreEqual("digit-font-missing", r.Values[0].DetailCode);
    }

    private void Report(string title, Frame frame, IReadOnlyList<(string Truth, RectI Rect)> cases, ParseRule rule, PairSemantics pair)
    {
        var vision = new VisionEngine(new TemplateCatalog());
        var modes = new[] { Binarization.None, Binarization.DarkOnLight, Binarization.LightOnDark };
        var hits = modes.ToDictionary(m => m, _ => 0);
        var parsedOk = modes.ToDictionary(m => m, _ => 0);
        var wrongButParsed = modes.ToDictionary(m => m, _ => 0);
        var anyHit = 0;
        var sb = new StringBuilder();
        sb.AppendLine($"== {title} ({cases.Count} regions; OCR upscale 3, per binarisation) ==");
        foreach (var (truth, rect) in cases)
        {
            var crop = vision.Crop(frame, rect);
            var line = new StringBuilder($"  truth '{truth}'".PadRight(18));
            var hitHere = false;
            foreach (var m in modes)
            {
                var r = Ocr.Recognize(crop, new OcrOptions("en-US", 3.0, m), TimeSpan.FromSeconds(3));
                var got = r.Status == OcrStatus.Ok ? ValueParser.NormalizeNumeric(r.Text) : "<" + r.Status + ">";
                var p = ValueParser.Parse(got, rule, pair);
                var good = got == truth;
                if (good)
                {
                    hits[m]++;
                    hitHere = true;
                }

                if (p.Ok)
                {
                    parsedOk[m]++;
                    if (!good)
                    {
                        wrongButParsed[m]++;
                    }
                }

                line.Append($" {m}:'{got}'{(good ? "+" : "")}".PadRight(26));
            }

            if (hitHere)
            {
                anyHit++;
            }

            sb.AppendLine(line.ToString());
        }

        foreach (var m in modes)
        {
            sb.AppendLine($"  {m}: exact {hits[m]}/{cases.Count} = {100.0 * hits[m] / cases.Count:F0}%; parsed by the rule {parsedOk[m]}, of which WRONG {wrongButParsed[m]}");
        }

        sb.AppendLine($"  any binarisation exact: {anyHit}/{cases.Count}");
        TestContext.WriteLine(sb.ToString());
    }
}
