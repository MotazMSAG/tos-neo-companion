using System.Diagnostics;
using Companion.Contracts;
using Companion.Contracts.Vision;
using Companion.Vision;
using Companion.Vision.Internal;

namespace Companion.Vision.Tests;

/// <summary>Matcher rules on synthetic images: margin, masks, zero variance, floors, anchors, frame checks.</summary>
[TestClass]
public sealed class SyntheticVisionTests
{
    private static readonly PointI Expected = new(1000, 500);

    public TestContext TestContext { get; set; } = null!;

    internal static BgraImage Pattern(int w, int h, int seed)
    {
        var img = BgraImage.Create(w, h);
        var rnd = new Random(seed);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                // blocky texture: strong structure, not noise-like
                var v = (byte)(((x / 5 + y / 7 + rnd.Next(0, 2)) % 3) * 100 + rnd.Next(0, 20));
                var o = y * img.Stride + x * 4;
                img.Data[o] = v;
                img.Data[o + 1] = v;
                img.Data[o + 2] = v;
                img.Data[o + 3] = 255;
            }
        }

        return img;
    }

    internal static BgraImage NoiseFrame(int seed = 7)
    {
        var img = BgraImage.Create(1920, 1009);
        var rnd = new Random(seed);
        rnd.NextBytes(img.Data);
        for (var i = 0; i < img.Data.Length; i += 4)
        {
            // low-contrast noise around mid gray
            var v = (byte)(100 + img.Data[i] % 40);
            img.Data[i] = v;
            img.Data[i + 1] = v;
            img.Data[i + 2] = v;
            img.Data[i + 3] = 255;
        }

        return img;
    }

    internal static TemplateDefinition Def(string id, PointI center, IReadOnlyList<RectI>? masks = null, double threshold = 0.9, double margin = 0.1, int radius = 24, TemplateStatus status = TemplateStatus.Verified, int scale = 1) =>
        new(id, id + ".png", null, masks, center, radius, threshold, margin, status, scale, null, TargetTags.Nav, FireOn.Down, AutomationSwitch.None, null, null, "synthetic", null);

    private static void PasteCentered(BgraImage frame, BgraImage t, PointI center) => frame.Paste(t, center.X - t.Width / 2, center.Y - t.Height / 2);

    [TestMethod]
    public void TwoCopiesWithinTheWindow_LowMargin()
    {
        var t = Pattern(40, 40, 1);
        var c = new TemplateCatalog();
        c.Add(Def("syn.a", Expected), PngCodec.Encode24(t), null, "test");
        c.Seal();
        var v = new VisionEngine(c);

        var frame = NoiseFrame();
        PasteCentered(frame, t, Expected);
        var single = v.Locate(frame.ToFrame(), "syn.a");
        Assert.IsTrue(single.Found, single.ToString());

        // a second, identical copy outside the radius (24) but inside the scored window: ambiguous
        PasteCentered(frame, t, Expected.Offset(44, 0));
        var both = v.Locate(frame.ToFrame(), "syn.a");
        TestContext.WriteLine(both.ToString());
        Assert.IsFalse(both.Found);
        Assert.AreEqual(MatchFailure.LowMargin, both.Failure);
        Assert.IsTrue(both.Score > 0.99 && both.SecondBest > 0.99, both.ToString());
    }

    [TestMethod]
    public void MaskRects_IgnoreTheMaskedArea()
    {
        var t = Pattern(40, 40, 2);
        var c = new TemplateCatalog();
        c.Add(Def("syn.masked", Expected, [new RectI(28, 0, 40, 12)]), PngCodec.Encode24(t), null, "test");
        c.Add(Def("syn.plain", Expected), PngCodec.Encode24(t), null, "test");
        c.Seal();
        var v = new VisionEngine(c);

        var frame = NoiseFrame();
        PasteCentered(frame, t, Expected);
        // a badge appears in the masked corner
        var tl = new PointI(Expected.X - 20, Expected.Y - 20);
        for (var y = 0; y < 12; y++)
        {
            for (var x = 28; x < 40; x++)
            {
                var o = (tl.Y + y) * frame.Stride + (tl.X + x) * 4;
                frame.Data[o] = 30;
                frame.Data[o + 1] = 60;
                frame.Data[o + 2] = 250;
            }
        }

        var masked = v.Locate(frame.ToFrame(), "syn.masked");
        var plain = v.Locate(frame.ToFrame(), "syn.plain");
        TestContext.WriteLine($"masked {masked}\nplain {plain}");
        Assert.IsTrue(masked.Found);
        Assert.IsTrue(masked.Score > 0.999, masked.ToString());
        Assert.IsTrue(plain.Score < masked.Score - 0.02, plain.ToString());
    }

    [TestMethod]
    public void MaskFile_BlackAndTransparentAreIgnored()
    {
        var t = Pattern(40, 40, 3);
        var mask = BgraImage.Create(40, 40);
        mask.Fill(255, 255, 255);
        mask.Blackout(new RectI(0, 0, 40, 10)); // black rows = ignore
        var c = new TemplateCatalog();
        c.Add(Def("syn.maskfile", Expected) with { MaskFile = "syn.maskfile.mask.png" }, PngCodec.Encode24(t), PngCodec.Encode24(mask), "test");
        c.Seal();
        var v = new VisionEngine(c);

        var frame = NoiseFrame();
        PasteCentered(frame, t, Expected);
        frame.Blackout(new RectI(Expected.X - 20, Expected.Y - 20, Expected.X + 20, Expected.Y - 10));
        var m = v.Locate(frame.ToFrame(), "syn.maskfile");
        Assert.IsTrue(m.Found, m.ToString());
        Assert.IsTrue(m.Score > 0.999, m.ToString());
    }

    [TestMethod]
    public void ZeroVariance_TemplateRejected_FlatWindowScoresZero()
    {
        var flat = BgraImage.Create(40, 40);
        flat.Fill(128, 128, 128);
        var c = new TemplateCatalog();
        var ex = Assert.ThrowsExactly<ArgumentException>(() => c.Add(Def("syn.flat", Expected), PngCodec.Encode24(flat), null, "test"));
        StringAssert.Contains(ex.Message, "zeroVariance");

        var t = Pattern(40, 40, 4);
        c.Add(Def("syn.t", Expected), PngCodec.Encode24(t), null, "test");
        c.Seal();
        var v = new VisionEngine(c);
        var frame = NoiseFrame();
        var area = new RectI(Expected.X - 120, Expected.Y - 120, Expected.X + 120, Expected.Y + 120);
        for (var y = area.Top; y < area.Bottom; y++)
        {
            for (var x = area.Left; x < area.Right; x++)
            {
                var o = y * frame.Stride + x * 4;
                frame.Data[o] = frame.Data[o + 1] = frame.Data[o + 2] = 90;
            }
        }

        var m = v.Locate(frame.ToFrame(), "syn.t");
        Assert.IsFalse(m.Found);
        Assert.AreEqual(MatchFailure.BelowThreshold, m.Failure);
        Assert.AreEqual(0.0, m.Score);
        Assert.IsFalse(double.IsNaN(m.SecondBest));
    }

    [TestMethod]
    public void Floors_AreApplied()
    {
        var t = Pattern(40, 40, 5);
        var c = new TemplateCatalog();
        c.Add(Def("syn.loose", Expected, threshold: 0.5, margin: 0.01, radius: 100), PngCodec.Encode24(t), null, "test");
        c.Add(Def("syn.halfscale", Expected, status: TemplateStatus.Verified, scale: 2), PngCodec.Encode24(t), null, "test");
        c.Seal();
        var d = c.Get("syn.loose")!;
        Assert.AreEqual(0.85, d.Threshold);
        Assert.AreEqual(0.05, d.MinMargin);
        Assert.AreEqual(40, d.SearchRadius);
        Assert.IsTrue(c.IsClickable("syn.loose"));
        Assert.AreEqual(TemplateStatus.Provisional, c.Get("syn.halfscale")!.Status);
        Assert.IsFalse(c.IsClickable("syn.halfscale"));
    }

    [TestMethod]
    public void Catalog_SealDuplicatesSuspectAndAnchors()
    {
        var a = Pattern(40, 40, 6);
        var b = Pattern(40, 40, 8);
        var c = new TemplateCatalog();
        c.Add(Def("syn.a", Expected), PngCodec.Encode24(a), null, "test");
        Assert.ThrowsExactly<ArgumentException>(() => c.Add(Def("syn.a", Expected), PngCodec.Encode24(a), null, "test"));
        Assert.ThrowsExactly<ArgumentException>(() => c.Add(Def("Bad Id", Expected), PngCodec.Encode24(a), null, "test"));
        c.Add(Def("syn.b", default) with { AnchorId = "syn.a", OffsetFromAnchor = new PointI(60, 10) }, PngCodec.Encode24(b), null, "test");
        c.Seal();
        Assert.ThrowsExactly<InvalidOperationException>(() => c.Add(Def("syn.c", Expected), PngCodec.Encode24(a), null, "test"));

        var v = new VisionEngine(c);
        var frame = NoiseFrame();
        var aAt = Expected.Offset(7, -5);
        PasteCentered(frame, a, aAt);
        PasteCentered(frame, b, aAt.Offset(60, 10));
        var mb = v.Locate(frame.ToFrame(), "syn.b");
        Assert.IsTrue(mb.Found, mb.ToString());
        Assert.AreEqual(aAt.Offset(60, 10), mb.Center);

        Assert.IsTrue(c.IsClickable("syn.a"));
        c.MarkSuspect("syn.a", "wrongUiVerifiedAim");
        Assert.IsTrue(c.IsSuspect("syn.a"));
        Assert.IsFalse(c.IsClickable("syn.a"));
        Assert.AreEqual(MatchFailure.NotClickable, v.VerifyAt(frame.ToFrame(), "syn.a", aAt, 2).Failure);

        var broken = new TemplateCatalog();
        broken.Add(Def("syn.orphan", default) with { AnchorId = "syn.none", OffsetFromAnchor = new PointI(1, 1) }, PngCodec.Encode24(a), null, "test");
        Assert.ThrowsExactly<InvalidOperationException>(broken.Seal);
    }

    [TestMethod]
    public void VisionEngine_RejectsForeignCatalogs()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new VisionEngine(new ForeignCatalog()));
    }

    [TestMethod]
    public void Assess_SizeBlackUniformOk()
    {
        var v = new VisionEngine(new TemplateCatalog());
        Assert.AreEqual(FrameQuality.WrongSize, v.Assess(BgraImage.Create(100, 100).ToFrame()));
        Assert.AreEqual(FrameQuality.WrongSize, v.Assess(new Frame(1920, 1009, 7680, new byte[10], TimeSpan.Zero, DateTimeOffset.UnixEpoch, null, default, false)));
        Assert.AreEqual(FrameQuality.Black, v.Assess(BgraImage.Create(1920, 1009).ToFrame()));
        var gray = BgraImage.Create(1920, 1009);
        gray.Fill(128, 128, 128);
        Assert.AreEqual(FrameQuality.Uniform, v.Assess(gray.ToFrame()));
        Assert.AreEqual(FrameQuality.Ok, v.Assess(NoiseFrame().ToFrame()));
        Assert.AreEqual(FrameQuality.Ok, v.Assess(TestData.C3bStripFrame()));
        var h = v.ClassifyHud(gray.ToFrame());
        Assert.AreEqual(HudState.Absent, h.State);
    }

    [TestMethod]
    public void RegionChange_CountsChangedSamples()
    {
        var v = new VisionEngine(new TemplateCatalog());
        var a = TestData.C3bStripFrame();
        var bImg = BgraImage.FromFrame(a) with { Data = (byte[])a.Bgra.Clone() };
        var changed = new RectI(1500, 100, 1700, 300);
        for (var y = changed.Top; y < changed.Bottom; y++)
        {
            for (var x = changed.Left; x < changed.Right; x++)
            {
                var o = y * bImg.Stride + x * 4;
                for (var ch = 0; ch < 3; ch++)
                {
                    bImg.Data[o + ch] = bImg.Data[o + ch] < 128 ? (byte)255 : (byte)0;
                }
            }
        }

        var b = bImg.ToFrame();
        Assert.AreEqual(0.0, v.RegionChange(a, a, new RectI(1440, 0, 1920, 1009)));
        Assert.IsTrue(v.RegionChange(a, b, changed) > 0.9);
        Assert.AreEqual(0.0, v.RegionChange(a, b, new RectI(1440, 400, 1920, 1009)));
        var half = v.RegionChange(a, b, new RectI(1500, 100, 1900, 300));
        Assert.IsTrue(half is > 0.3 and < 0.7, half.ToString());
        Assert.AreEqual(0.0, v.RegionChange(a, b, new RectI(3000, 3000, 3100, 3100)));
    }

    [TestMethod]
    public void EncodePng_AppliesBlackoutsAndKeepsTheRest()
    {
        var v = new VisionEngine(new TemplateCatalog());
        var f = TestData.C3bStripFrame();
        var region = new RectI(1440, 0, 1920, 400);
        var blackout = new RectI(1600, 0, 1680, 100);
        var png = v.EncodePng(f, region, [blackout, new RectI(0, 0, 10, 10)]);
        var back = PngCodec.Decode(png);
        Assert.AreEqual(480, back.Width);
        Assert.AreEqual(400, back.Height);
        long inside = 0, outside = 0;
        for (var y = 0; y < back.Height; y++)
        {
            for (var x = 0; x < back.Width; x++)
            {
                var o = y * back.Stride + x * 4;
                var s = back.Data[o] + back.Data[o + 1] + back.Data[o + 2];
                if (blackout.Contains(new PointI(x + 1440, y)))
                {
                    inside += s;
                }
                else
                {
                    outside += s;
                }
            }
        }

        Assert.AreEqual(0, inside);
        Assert.IsTrue(outside > 0);
        Assert.ThrowsExactly<ArgumentException>(() => v.EncodePng(f, new RectI(5000, 5000, 5100, 5100), []));
    }

    [TestMethod]
    public void Crop_ScalesAndClips()
    {
        var v = new VisionEngine(new TemplateCatalog());
        var f = TestData.C3bStripFrame();
        var g1 = v.Crop(f, new RectI(1700, 280, 1720, 290));
        Assert.AreEqual((20, 10), (g1.Width, g1.Height));
        var g3 = v.Crop(f, new RectI(1700, 280, 1720, 290), 3.0);
        Assert.AreEqual((60, 30), (g3.Width, g3.Height));
        var clipped = v.Crop(f, new RectI(1900, 1000, 1950, 1050));
        Assert.AreEqual((20, 9), (clipped.Width, clipped.Height));
        Assert.AreEqual(0, v.Crop(f, new RectI(3000, 0, 3010, 10)).Width);
    }

    [TestMethod]
    public void ClassifyHud_TimingAfterWarmup()
    {
        var v = new VisionEngine(TestData.LoadCatalog());
        var f = TestData.C3bStripFrame();
        v.ClassifyHud(f);
        var sw = Stopwatch.StartNew();
        const int n = 5;
        for (var i = 0; i < n; i++)
        {
            v.ClassifyHud(f);
        }

        TestContext.WriteLine($"ClassifyHud: {sw.Elapsed.TotalMilliseconds / n:F1} ms per frame (this build configuration)");
    }

    private sealed class ForeignCatalog : ITemplateCatalog
    {
        public IReadOnlyCollection<TemplateDefinition> All => [];

        public TemplateDefinition? Get(string id) => null;

        public bool IsClickable(string id) => false;

        public bool IsSuspect(string id) => false;

        public void MarkSuspect(string id, string reasonCode)
        {
        }
    }
}
