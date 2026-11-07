using Companion.Contracts;
using Companion.Contracts.Vision;
using Companion.TemplateCutter;
using Companion.Vision.Internal;

namespace Companion.Vision.Tests;

/// <summary>TemplateCutter rules (pure core; nothing is written by these tests).</summary>
[TestClass]
public sealed class TemplateCutterTests
{
    private static CutRequest Request(SourceLayout layout, TemplateStatus status, RectI rect, PointI offset = default) =>
        new("test.cut", "test.png", layout, offset, rect, status, [], null, TargetTags.Nav);

    [TestMethod]
    public void VerifiedFromHalfScaleSource_IsRefused()
    {
        var png = TestData.Proof("c1_ability.png");
        var r = TemplateCutterCore.Cut(png, Request(SourceLayout.ClientHalf, TemplateStatus.Verified, new RectI(1827, 292, 1883, 348)));
        Assert.IsFalse(r.Ok);
        Assert.AreEqual("verified-needs-full-scale-source", r.Refusal);

        var w = TemplateCutterCore.Cut(TestData.Proof("g_after_Esc.png"), Request(SourceLayout.WindowHalf, TemplateStatus.Verified, new RectI(1827, 292, 1883, 348)));
        Assert.AreEqual("verified-needs-full-scale-source", w.Refusal);
    }

    [TestMethod]
    public void ProvisionalFromHalfScaleSource_IsCutWithScale2()
    {
        var r = TemplateCutterCore.Cut(TestData.Proof("c1_ability.png"), Request(SourceLayout.ClientHalf, TemplateStatus.Provisional, new RectI(1827, 292, 1883, 348)));
        Assert.IsTrue(r.Ok, r.Refusal);
        Assert.AreEqual(2, r.Definition!.SourceScale);
        Assert.AreEqual(TemplateStatus.Provisional, r.Definition.Status);
        var centre = r.Definition.ExpectedCenter;
        Assert.AreEqual(new PointI(1855, 320), centre);
        var img = PngCodec.Decode(r.Png!);
        Assert.AreEqual((56, 56), (img.Width, img.Height));
    }

    [TestMethod]
    public void VerifiedFromFullScaleCrop_IsCut_RectOutsideTheCropIsRefused()
    {
        var png = TestData.Proof("c3b_hud_right_x1440.png");
        var ok = TemplateCutterCore.Cut(png, Request(SourceLayout.ClientCropFull, TemplateStatus.Verified, new RectI(1718, 282, 1778, 342), new PointI(1440, 0)));
        Assert.IsTrue(ok.Ok, ok.Refusal);
        Assert.AreEqual(1, ok.Definition!.SourceScale);
        var outside = TemplateCutterCore.Cut(png, Request(SourceLayout.ClientCropFull, TemplateStatus.Verified, new RectI(1400, 282, 1460, 342), new PointI(1440, 0)));
        Assert.AreEqual("rect-outside-source", outside.Refusal);
        var wrongLayout = TemplateCutterCore.Cut(png, Request(SourceLayout.ClientHalf, TemplateStatus.Provisional, new RectI(10, 10, 50, 50)));
        Assert.AreEqual("source-layout-mismatch", wrongLayout.Refusal);
    }

    [TestMethod]
    public void CommitWithoutGate_AndBadIds_AreRefused()
    {
        var png = TestData.Proof("c3b_hud_right_x1440.png");
        var commit = Request(SourceLayout.ClientCropFull, TemplateStatus.Verified, new RectI(1718, 282, 1778, 342), new PointI(1440, 0)) with { Tags = TargetTags.Commit };
        Assert.AreEqual("commit-needs-gate", TemplateCutterCore.Cut(png, commit).Refusal);
        var bad = Request(SourceLayout.ClientCropFull, TemplateStatus.Verified, new RectI(1718, 282, 1778, 342), new PointI(1440, 0)) with { Id = "..\\x" };
        Assert.AreEqual("bad-id", TemplateCutterCore.Cut(png, bad).Refusal);
    }

    [TestMethod]
    public void StagingPath_StaysInsideStaging()
    {
        var root = Path.Combine(Path.GetTempPath(), "never-written");
        StringAssert.EndsWith(TemplateCutterCore.StagingPath(root, "a.png"), Path.Combine("staging", "a.png"));
        Assert.ThrowsExactly<InvalidOperationException>(() => TemplateCutterCore.StagingPath(root, "..\\a.png"));
        Assert.ThrowsExactly<InvalidOperationException>(() => TemplateCutterCore.StagingPath(root, "..\\..\\data\\readers.json"));
    }

    /// <summary>The committed templates are exactly what the seed recipe cuts from the proofs (provenance check).</summary>
    [TestMethod]
    public void CommittedTemplates_MatchTheSeedRecipe()
    {
        var committed = TestData.LoadDefinitions().ToDictionary(d => d.Id);
        foreach (var (file, req) in TemplateCutterCore.SeedRequests())
        {
            var r = TemplateCutterCore.Cut(TestData.Proof(file), req);
            Assert.IsTrue(r.Ok, r.Refusal);
            var c = committed[req.Id];
            if (c.Source?.StartsWith("Calibrate", StringComparison.Ordinal) == true)
            {
                continue; // replaced by a full-scale cut from a live Calibrate capture (2026-10-14)
            }
            Assert.AreEqual(r.Definition!.ExpectedCenter, c.ExpectedCenter, req.Id);
            Assert.AreEqual(r.Definition.Status, c.Status, req.Id);
            Assert.AreEqual(r.Definition.SourceScale, c.SourceScale, req.Id);
            Assert.AreEqual(r.Definition.Source, c.Source, req.Id);
            CollectionAssert.AreEqual((r.Definition.MaskRects ?? []).ToArray(), (c.MaskRects ?? []).ToArray(), req.Id);
            var a = PngCodec.Decode(r.Png!);
            var b = PngCodec.Decode(File.ReadAllBytes(Path.Combine(TestData.TemplatesDir, c.ImageFile)));
            Assert.AreEqual((a.Width, a.Height), (b.Width, b.Height), req.Id);
            var maxDiff = a.Data.Zip(b.Data, (x, y) => Math.Abs(x - y)).Max();
            Assert.IsTrue(maxDiff <= 1, $"{req.Id}: pixel diff {maxDiff}");
        }
    }

    [TestMethod]
    public void Json_RoundTripsWithTheContractOptions()
    {
        var defs = TestData.LoadDefinitions();
        var json = TemplateCutterCore.ToJson(defs);
        Assert.IsFalse(json.Contains("\"isEmpty\"", StringComparison.Ordinal));
        var back = TemplateCutterCore.FromJson(json);
        Assert.AreEqual(defs.Length, back.Length);
        foreach (var d in defs)
        {
            var e = back.Single(x => x.Id == d.Id);
            Assert.AreEqual(d.ExpectedCenter, e.ExpectedCenter);
            Assert.AreEqual(d.Tags, e.Tags);
            Assert.AreEqual(d.Hud, e.Hud);
        }
    }
}
