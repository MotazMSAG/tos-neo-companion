using Companion.Contracts;
using Companion.Contracts.Perception;
using Companion.Contracts.Vision;

namespace Companion.Perception.Tests;

/// <summary>ScreenReader decisions with scripted vision/OCR/digits (SPEC 11.1, 13).</summary>
[TestClass]
public sealed class ScreenReaderTests
{
    private static readonly Frame AnyFrame = FakeVision.AnyFrame();

    [TestMethod]
    public void NeedsSurveyReader_GivesNeedsSurvey_WithoutReading()
    {
        var ocr = new FakeOcr();
        var vision = new FakeVision();
        var sr = new ScreenReader(vision, ocr, new FakeDigits("1"));
        var r = sr.Read(Support.Reader("lumi.products", ReaderStatus.NeedsSurvey, "UI_HOME_PET_VIEW", []), AnyFrame, Support.Ui("UI_HOME_PET_VIEW"));
        Assert.AreEqual(ReadStatus.NeedsSurvey, r.Status);
        Assert.AreEqual(0, ocr.Calls.Count);
        Assert.AreEqual(0, vision.Located.Count);
    }

    [TestMethod]
    public void LogTopMismatch_GivesScreenMismatch()
    {
        var ocr = new FakeOcr();
        var sr = new ScreenReader(new FakeVision("lumi.anchor"), ocr, new FakeDigits("18/18"));
        var reader = Support.Reader("lumi", ReaderStatus.Ready, "UI_HOME_PET_VIEW", ["lumi.anchor"], Support.Region("p1", ParseRule.Pair, PairSemantics.StoredCapacity));
        var r = sr.Read(reader, AnyFrame, Support.Ui("UI_BAG_MAIN"));
        Assert.AreEqual(ReadStatus.ScreenMismatch, r.Status);
        Assert.AreEqual("log-top-mismatch", r.Values.Single().DetailCode);
        Assert.AreEqual(0, ocr.Calls.Count);

        // HUD readers (no log UI) need nothing open
        var hud = Support.Reader("hud", ReaderStatus.Ready, null, ["hud.bag"], Support.Region("x", ParseRule.Int));
        Assert.AreEqual(ReadStatus.ScreenMismatch, new ScreenReader(new FakeVision("hud.bag"), ocr, new FakeDigits("1")).Read(hud, AnyFrame, Support.Ui("UI_BAG_MAIN")).Status);
    }

    [TestMethod]
    public void AnchorMissing_GivesScreenMismatch()
    {
        var sr = new ScreenReader(new FakeVision(), new FakeOcr(), new FakeDigits("18/18"));
        var reader = Support.Reader("lumi", ReaderStatus.Ready, "UI_HOME_PET_VIEW", ["lumi.anchor"], Support.Region("p1", ParseRule.Pair, PairSemantics.StoredCapacity));
        var r = sr.Read(reader, AnyFrame, Support.Ui("UI_HOME_PET_VIEW"));
        Assert.AreEqual(ReadStatus.ScreenMismatch, r.Status);
        Assert.AreEqual("anchor-not-found", r.Values.Single().DetailCode);
    }

    [TestMethod]
    public void OcrAndDigitsAgree_PairAccepted()
    {
        var ocr = new FakeOcr { Text = { [Binarization.DarkOnLight] = "18 / 18" } };
        var sr = new ScreenReader(new FakeVision("lumi.anchor"), ocr, new FakeDigits("18/18"));
        var reader = Support.Reader("lumi", ReaderStatus.Ready, "HOME_PET_VIEW", ["lumi.anchor"], Support.Region("p1", ParseRule.Pair, PairSemantics.StoredCapacity, max: 999));
        var r = sr.Read(reader, AnyFrame, Support.Ui("UI_HOME_PET_VIEW"));
        var v = r.Values.Single();
        Assert.AreEqual(ReadStatus.Ok, r.Status, v.DetailCode);
        Assert.AreEqual(18L, v.Value);
        Assert.AreEqual(18L, v.Second);
        Assert.IsTrue(v.Confidence > 0.9);
        CollectionAssert.AreEqual(new[] { Binarization.DarkOnLight }, ocr.Calls);
    }

    [TestMethod]
    public void Disagreement_ReReadsOnce_ThenUnknown()
    {
        var ocr = new FakeOcr { Text = { [Binarization.DarkOnLight] = "16/18", [Binarization.LightOnDark] = "16/16" } };
        var sr = new ScreenReader(new FakeVision(), ocr, new FakeDigits("18/18"));
        var reader = Support.Reader("lumi", ReaderStatus.Ready, "UI_HOME_PET_VIEW", [], Support.Region("p1", ParseRule.Pair, PairSemantics.StoredCapacity));
        var r = sr.Read(reader, AnyFrame, Support.Ui("UI_HOME_PET_VIEW"));
        var v = r.Values.Single();
        Assert.AreEqual(ReadStatus.Unknown, r.Status);
        Assert.AreEqual(ReadStatus.Unknown, v.Status);
        Assert.IsNull(v.Value);
        Assert.AreEqual(0.0, v.Confidence);
        Assert.AreEqual("ocr-digits-disagree", v.DetailCode);

        // each binarisation tries the OCR scale ladder (3x, 4x, 5x) before the single re-read with the other one
        CollectionAssert.AreEqual(new[] { Binarization.DarkOnLight, Binarization.LightOnDark }, ocr.Calls.Distinct().ToArray());
        Assert.HasCount(2 * ScreenReader.OcrUpscales.Count, ocr.Calls);
    }

    [TestMethod]
    public void TheScaleLadder_StopsAtTheFirstAgreement()
    {
        var ocr = new ScaleOcr { [4.0] = "18/18", [3.0] = "1818", [5.0] = "18/16" };
        var sr = new ScreenReader(new FakeVision(), ocr, new FakeDigits("18/18"));
        var reader = Support.Reader("lumi", ReaderStatus.Ready, "UI_HOME_PET_VIEW", [], Support.Region("p1", ParseRule.Pair, PairSemantics.StoredCapacity));
        var v = sr.Read(reader, AnyFrame, Support.Ui("UI_HOME_PET_VIEW")).Values.Single();
        Assert.AreEqual((ReadStatus.Ok, 18L, 18L), (v.Status, v.Value, v.Second), v.DetailCode);
        CollectionAssert.AreEqual(new[] { 3.0, 4.0 }, ocr.Scales);
    }

    [TestMethod]
    public void WithoutTheTemplateReader_ThereIsNoDigitsOnlyFallback()
    {
        // A scripted digit reader has no glyph margins: unreadable OCR stays Unknown.
        var ocr = new FakeOcr { Text = { [Binarization.DarkOnLight] = "Aon", [Binarization.LightOnDark] = "" } };
        var reader = Support.Reader("lumi", ReaderStatus.Ready, "UI_HOME_PET_VIEW", [], Support.Region("p1", ParseRule.Pair, PairSemantics.StoredCapacity));
        var v = new ScreenReader(new FakeVision(), ocr, new FakeDigits("18/18", score: 1.0)).Read(reader, AnyFrame, Support.Ui("UI_HOME_PET_VIEW")).Values.Single();
        Assert.AreEqual((ReadStatus.Unknown, "ocr-digits-disagree"), (v.Status, v.DetailCode));
    }

    /// <summary>OCR text per upscale factor (any binarisation).</summary>
    private sealed class ScaleOcr : Dictionary<double, string>, IOcr
    {
        public List<double> Scales { get; } = [];

        public bool IsAvailable => true;

        public OcrResult Recognize(GrayImage image, OcrOptions options, TimeSpan timeout)
        {
            Scales.Add(options.Upscale);
            var t = this.GetValueOrDefault(options.Upscale, string.Empty);
            return new OcrResult(OcrStatus.Ok, t, [], TimeSpan.Zero);
        }
    }

    [TestMethod]
    public void ReReadWithOtherBinarisation_CanAgree_WithLowerConfidence()
    {
        var ocr = new FakeOcr { Text = { [Binarization.LightOnDark] = "697", [Binarization.DarkOnLight] = "69" } };
        var sr = new ScreenReader(new FakeVision(), ocr, new FakeDigits("697"));
        var reader = Support.Reader("bag", ReaderStatus.Ready, "UI_BAG_MAIN", [], Support.Region("c", ParseRule.ItemCellCount, font: "bag-count-outlined"));
        var v = sr.Read(reader, AnyFrame, Support.Ui("UI_BAG_MAIN")).Values.Single();
        Assert.AreEqual(ReadStatus.Ok, v.Status, v.DetailCode);
        Assert.AreEqual(697L, v.Value);

        var ocr2 = new FakeOcr { Text = { [Binarization.DarkOnLight] = "697", [Binarization.LightOnDark] = "69" } };
        var v2 = new ScreenReader(new FakeVision(), ocr2, new FakeDigits("697")).Read(reader, AnyFrame, Support.Ui("UI_BAG_MAIN")).Values.Single();
        Assert.AreEqual(ReadStatus.Ok, v2.Status, v2.DetailCode);
        Assert.IsTrue(v2.Confidence < v.Confidence, $"{v2.Confidence} vs {v.Confidence}");
    }

    [TestMethod]
    public void NoDigitFont_IsUnknown_NeverAnOcrOnlyGuess()
    {
        var ocr = new FakeOcr { Text = { [Binarization.DarkOnLight] = "18/18", [Binarization.LightOnDark] = "18/18" } };
        var sr = new ScreenReader(new FakeVision(), ocr, new DigitTemplateReader());
        var reader = Support.Reader("lumi", ReaderStatus.Ready, "UI_HOME_PET_VIEW", [], Support.Region("p1", ParseRule.Pair, PairSemantics.StoredCapacity));
        var v = sr.Read(reader, AnyFrame, Support.Ui("UI_HOME_PET_VIEW")).Values.Single();
        Assert.AreEqual(ReadStatus.Unknown, v.Status);
        Assert.AreEqual("digit-font-missing", v.DetailCode);
    }

    [TestMethod]
    public void SemanticAndBoundChecks_GiveUnknown()
    {
        var reader = Support.Reader("lumi", ReaderStatus.Ready, "UI_HOME_PET_VIEW", [], Support.Region("p1", ParseRule.Pair, PairSemantics.StoredCapacity, max: 99));
        var over = new FakeOcr { Text = { [Binarization.DarkOnLight] = "19/18", [Binarization.LightOnDark] = "19/18" } };
        var v = new ScreenReader(new FakeVision(), over, new FakeDigits("19/18")).Read(reader, AnyFrame, Support.Ui("UI_HOME_PET_VIEW")).Values.Single();
        Assert.AreEqual(ReadStatus.Unknown, v.Status);
        Assert.AreEqual("pair-stored-exceeds-capacity", v.DetailCode);

        var big = new FakeOcr { Text = { [Binarization.DarkOnLight] = "180/180", [Binarization.LightOnDark] = "180/180" } };
        var b = new ScreenReader(new FakeVision(), big, new FakeDigits("180/180")).Read(reader, AnyFrame, Support.Ui("UI_HOME_PET_VIEW")).Values.Single();
        Assert.AreEqual("out-of-bounds", b.DetailCode);
    }

    [TestMethod]
    public void Timeout_GivesUnknown()
    {
        var ocr = new FakeOcr { Status = OcrStatus.Timeout };
        var reader = Support.Reader("lumi", ReaderStatus.Ready, "UI_HOME_PET_VIEW", [], Support.Region("p1", ParseRule.Pair, PairSemantics.StoredCapacity));
        var v = new ScreenReader(new FakeVision(), ocr, new FakeDigits("18/18")).Read(reader, AnyFrame, Support.Ui("UI_HOME_PET_VIEW")).Values.Single();
        Assert.AreEqual(ReadStatus.Unknown, v.Status);
        Assert.AreEqual("ocr-timeout", v.DetailCode);
    }

    [TestMethod]
    public void EquipmentLevel_IsClassifiedNotCounted()
    {
        var ocr = new FakeOcr { Text = { [Binarization.LightOnDark] = "Lv62" } };
        var reader = Support.Reader("bag", ReaderStatus.Ready, "UI_BAG_MAIN", [], Support.Region("c", ParseRule.ItemCellCount, font: "bag-count-outlined"));
        var v = new ScreenReader(new FakeVision(), ocr, new FakeDigits("")).Read(reader, AnyFrame, Support.Ui("UI_BAG_MAIN")).Values.Single();
        Assert.AreEqual(ReadStatus.Ok, v.Status);
        Assert.IsNull(v.Value);
        Assert.AreEqual("Lv62", v.Text);
        Assert.AreEqual("cell-equipment", v.DetailCode);
    }

    [TestMethod]
    public void Text_NeedsBothBinarisationsToAgree()
    {
        var reader = Support.Reader("name", ReaderStatus.Ready, "UI_LIFESKILL_MAKE_VIEW", [], Support.Region("n", ParseRule.Text, font: null));
        var same = new FakeOcr { Text = { [Binarization.None] = " Gold  Ore ", [Binarization.LightOnDark] = "Gold Ore" } };
        var v = new ScreenReader(new FakeVision(), same, new FakeDigits("")).Read(reader, AnyFrame, Support.Ui("UI_LIFESKILL_MAKE_VIEW")).Values.Single();
        Assert.AreEqual(ReadStatus.Ok, v.Status, v.DetailCode);
        Assert.AreEqual("Gold Ore", v.Text);

        var differ = new FakeOcr { Text = { [Binarization.None] = "Gold Ore", [Binarization.LightOnDark] = "Cold Ore", [Binarization.DarkOnLight] = "Gold Ure" } };
        var u = new ScreenReader(new FakeVision(), differ, new FakeDigits("")).Read(reader, AnyFrame, Support.Ui("UI_LIFESKILL_MAKE_VIEW")).Values.Single();
        Assert.AreEqual(ReadStatus.Unknown, u.Status);
    }

    [TestMethod]
    public void AnchorRelativeRegion_IsOffsetByTheAnchor_AndOutsideFrameIsUnknown()
    {
        var ocr = new FakeOcr { Text = { [Binarization.DarkOnLight] = "5" } };
        var sr = new ScreenReader(new FakeVision("a"), ocr, new FakeDigits("5"));
        var inside = Support.Region("r", ParseRule.Int, rect: new RectI(-20, -10, 20, 10), anchor: "a");
        var outside = Support.Region("o", ParseRule.Int, rect: new RectI(1500, 700, 1600, 800), anchor: "a");
        var r = sr.Read(Support.Reader("x", ReaderStatus.Ready, "UI_X", ["a"], inside, outside), AnyFrame, Support.Ui("UI_X"));
        Assert.AreEqual(ReadStatus.Ok, r.Values[0].Status, r.Values[0].DetailCode);
        Assert.AreEqual(5L, r.Values[0].Value);
        Assert.AreEqual(ReadStatus.Unknown, r.Values[1].Status);
        Assert.AreEqual("region-outside-frame", r.Values[1].DetailCode);
        Assert.AreEqual(ReadStatus.Unknown, r.Status);
    }

    [TestMethod]
    public void BinarisationFollowsTheFontStyle()
    {
        Assert.AreEqual(Binarization.LightOnDark, ScreenReader.PrimaryBinarization("bag-count-outlined"));
        Assert.AreEqual(Binarization.DarkOnLight, ScreenReader.PrimaryBinarization("parchment-dark"));
        Assert.AreEqual(Binarization.None, ScreenReader.PrimaryBinarization(null));
        Assert.AreEqual(Binarization.DarkOnLight, ScreenReader.OtherBinarization(Binarization.LightOnDark));
        Assert.AreEqual(Binarization.LightOnDark, ScreenReader.OtherBinarization(Binarization.DarkOnLight));
    }
}
