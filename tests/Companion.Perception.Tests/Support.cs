using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Companion.Contracts;
using Companion.Contracts.Logs;
using Companion.Contracts.Perception;
using Companion.Contracts.Vision;

namespace Companion.Perception.Tests;

/// <summary>Paths, snapshots and synthetic text rendering for the tests.</summary>
internal static class Support
{
    private static readonly Lazy<string> Root = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SPEC.md")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("companion root not found");
    });

    public static string CompanionRoot => Root.Value;

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


    public static UiStateSnapshot Ui(params string[] stack) => new(
        null, true, BusyKind.None, stack, null, FocusState.Unknown, null, 0, null, null, null, null, null, null, null, null,
        CraftSessionInfo.None, []);

    /// <summary>Renders text (dark on light by default) into a gray image with GDI+.</summary>
    public static GrayImage Render(string text, float sizePx = 20f, bool lightOnDark = false, string family = "Arial", FontStyle style = FontStyle.Bold, int pad = 4)
    {
        using var font = new Font(family, sizePx, style, GraphicsUnit.Pixel);
        SizeF size;
        using (var probe = new Bitmap(1, 1))
        using (var pg = Graphics.FromImage(probe))
        {
            size = pg.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
        }

        var w = (int)Math.Ceiling(size.Width) + 2 * pad;
        var h = (int)Math.Ceiling(size.Height) + 2 * pad;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(lightOnDark ? Color.FromArgb(40, 60, 40) : Color.FromArgb(240, 228, 200));
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(lightOnDark ? Color.White : Color.FromArgb(70, 40, 30));
            g.DrawString(text, font, brush, pad, pad, StringFormat.GenericTypographic);
        }

        return ToGray(bmp);
    }

    public static GrayImage ToGray(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[bmp.Width * 4];
            var px = new byte[bmp.Width * bmp.Height];
            for (var y = 0; y < bmp.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = 0; x < bmp.Width; x++)
                {
                    px[y * bmp.Width + x] = (byte)((row[x * 4] * 29 + row[x * 4 + 1] * 150 + row[x * 4 + 2] * 77 + 128) >> 8);
                }
            }

            return new GrayImage(bmp.Width, bmp.Height, px);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>A synthetic digit font: one rendered image per glyph.</summary>
    public static IReadOnlyDictionary<char, GrayImage> SyntheticFont(string family = "Arial", float sizePx = 20f, bool lightOnDark = false) =>
        DigitTemplateReader.AllowedGlyphs.ToDictionary(c => c, c => Render(c.ToString(), sizePx, lightOnDark, family));

    public static ReaderDefinition Reader(string id, ReaderStatus status, string? logUi, IReadOnlyList<string> anchors, params RegionDefinition[] regions) =>
        new(id, new ScreenIdentity(logUi, anchors), regions, status, null);

    public static RegionDefinition Region(string id, ParseRule rule, PairSemantics pair = PairSemantics.None, string? font = "parchment-dark", long? min = null, long? max = null, RectI? rect = null, string? anchor = null) =>
        new(id, rect ?? new RectI(100, 100, 160, 130), anchor, rule, pair, font, min, max);
}

/// <summary>Scripted IVision: every frame is Ok; anchors found per a set; crops are small blank images.</summary>
internal sealed class FakeVision(params string[] foundAnchors) : IVision
{
    public List<string> Located { get; } = [];

    public MatchResult Locate(Frame frame, string templateId)
    {
        Located.Add(templateId);
        var found = foundAnchors.Contains(templateId);
        return new MatchResult(templateId, found, new PointI(500, 400), found ? 0.97 : 0.3, 0.2, 0, found ? MatchFailure.None : MatchFailure.BelowThreshold);
    }

    public MatchResult VerifyAt(Frame frame, string templateId, PointI expectedCenter, int tolerancePx) => Locate(frame, templateId);

    public HudClassification ClassifyHud(Frame frame) => new(HudState.Unknown, 0, 0, false, false, "fake");

    public FrameQuality Assess(Frame frame) => frame.Width == 1920 ? FrameQuality.Ok : FrameQuality.WrongSize;

    public double RegionChange(Frame a, Frame b, RectI region) => 0;

    public GrayImage Crop(Frame frame, RectI region, double scale = 1) => new(region.Width, region.Height, new byte[region.Width * region.Height]);

    public byte[] EncodePng(Frame frame, RectI region, IReadOnlyList<RectI> blackout) => [];

    public static Frame AnyFrame() => new(1920, 1009, 1920 * 4, new byte[1920 * 1009 * 4], TimeSpan.Zero, DateTimeOffset.UnixEpoch, null, default, false);
}

/// <summary>Scripted IOcr: text per binarisation (or a fixed status).</summary>
internal sealed class FakeOcr : IOcr
{
    public Dictionary<Binarization, string> Text { get; } = [];

    public OcrStatus Status { get; set; } = OcrStatus.Ok;

    public List<Binarization> Calls { get; } = [];

    public bool IsAvailable => true;

    public OcrResult Recognize(GrayImage image, OcrOptions options, TimeSpan timeout)
    {
        Calls.Add(options.Binarization);
        if (Status != OcrStatus.Ok)
        {
            return new OcrResult(Status, string.Empty, [], TimeSpan.Zero);
        }

        var t = Text.GetValueOrDefault(options.Binarization, string.Empty);
        return new OcrResult(OcrStatus.Ok, t, [new OcrLine(t, [new OcrWord(t, new RectI(0, 0, 10, 10))])], TimeSpan.Zero);
    }
}

/// <summary>Scripted IDigitReader.</summary>
internal sealed class FakeDigits(string text, bool ok = true, double score = 0.95, params string[] styles) : IDigitReader
{
    public IReadOnlyCollection<string> FontStyles => styles.Length == 0 ? ["parchment-dark", "bag-count-outlined"] : styles;

    public DigitReadResult Read(GrayImage image, string fontStyle) => new(ok, text, score);
}
