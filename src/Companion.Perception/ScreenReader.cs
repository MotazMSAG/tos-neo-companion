using Companion.Contracts;
using Companion.Contracts.Logs;
using Companion.Contracts.Perception;
using Companion.Contracts.Vision;
using Companion.Perception.Internal;

namespace Companion.Perception;

/// <summary>
/// <see cref="IScreenReader"/> (unit "vision-ocr", SPEC 11.1). Order of checks:
/// <list type="number">
/// <item>reader NeedsSurvey → NeedsSurvey (nothing is read);</item>
/// <item>screen identity: the log's advisory top UI equals the reader's LogUiName (null = HUD = nothing open) AND
/// every anchor is found in this frame → otherwise ScreenMismatch;</item>
/// <item>per region: crop (anchor-relative if set), OCR with the font style's binarisation, digit templates for the
/// same crop; numbers are accepted only when OCR and the digit reader agree AND the rule's regex, bounds and pair
/// semantics pass. OCR runs at <see cref="OcrUpscales"/> (3x, then 4x, then 5x, stopping at the first agreement:
/// Windows OCR misreads small game digits at one scale and not at another, live run 2026-10-14); otherwise one
/// re-read with the other binarisation (same scales); still no agreement → Unknown. Text needs the two binarisations
/// to agree.</item>
/// <item>digits-only (the digit-template fallback, 2026-10-14): when no OCR pass agreed, the value is still accepted
/// from the template reader alone if every OCR pass ran (no timeout) and NONE of them parsed to a different valid
/// value, and every glyph matched a built-in template strongly (score &gt;= 0.80 and at least 0.25 above any other
/// character; the same glyph at another position of the capture scores 0.84-1.00 with margins of 0.39-0.62).
/// Lower confidence, detail "digits-only". Anything else stays Unknown. Never a guess.</item>
/// <item><see cref="ParseRule.IconFingerprint"/> regions: no OCR; the tile outline must be at the expected place (else
/// Unknown), an empty slot is Ok with no text, a tile gives "icon:" + its fingerprint.</item>
/// <item><see cref="ParseRule.TileRow"/> regions (2026-10-14): the tiles are DETECTED along the band (3 or 4 materials
/// shift the slots); the region id gives the tile count (Ok) or the reason the row could not be read (Unknown), then
/// "icon.k" and "pair.k" per tile (the pair read like a Pair region in the 120x24 box under the tile).</item>
/// <item><see cref="ParseRule.LabeledPair"/> regions (2026-10-14): the pair is the last word of the line (after the last
/// gap wider than a quarter of the text height), read like a Pair region.</item>
/// <item>Optional regions (2026-10-14): nothing drawn in the region reads Ok with no value, detail "absent".</item>
/// <item><see cref="ParseRule.IconGrid"/> regions (2026-10-15, the Stock tab): the whole tiles are DETECTED in the
/// region; per tile "icon.k" and "count.k" (the count's glyphs, separated from the icon by their white halo, read on a
/// clean image like any number; a faded tile without a count is 0).</item>
/// <item>Text (2026-10-15): ink in the first column or within <see cref="TextEdgeMarginPx"/> of the right edge is
/// "text-truncated" (Unknown): a recipe title longer than its box scrolls, and its box cuts it anywhere, even in the
/// gap between two words (make_fav3/4_full.png: the cut glyph sits on the box's first column).</item>
/// </list>
/// Every number carries the weakest glyph score of its digit read (<see cref="ReadValue.GlyphScore"/>) and every value
/// the client rect it was read from (<see cref="ReadValue.Rect"/>; the tile for "icon.k"); the result carries the
/// anchors it located (<see cref="ScreenReadResult.Anchors"/>). Confidence is synthesised (Windows OCR gives none).
/// Pure (no capture, no I/O); thread-safe.
/// </summary>
public sealed class ScreenReader : IScreenReader
{
    /// <summary>
    /// Text regions: the ink must keep this many blank columns before the region's right edge, more than the gap between
    /// two words of a title (about 8-10 px at full scale), so a title cut by its box between two words is never read as
    /// a shorter name.
    /// </summary>
    internal const int TextEdgeMarginPx = 16;

    /// <summary>OCR upscale factor of the first pass (SPEC 11.1: about 3x).</summary>
    internal const double OcrUpscale = 3.0;

    /// <summary>OCR upscale ladder for numbers (per binarisation, stops at the first agreement with the digit reader).</summary>
    internal static readonly IReadOnlyList<double> OcrUpscales = [OcrUpscale, 4.0, 5.0];

    private readonly IVision vision;
    private readonly IOcr ocr;
    private readonly IDigitReader digits;
    private readonly TimeSpan ocrTimeout;

    /// <summary>Creates the reader (stores its dependencies only).</summary>
    /// <param name="vision">Anchors and crops.</param>
    /// <param name="ocr">OCR.</param>
    /// <param name="digits">Digit templates.</param>
    /// <param name="ocrTimeoutMs">Per OCR call; a timeout makes that read Unknown (OCR-02).</param>
    public ScreenReader(IVision vision, IOcr ocr, IDigitReader digits, int ocrTimeoutMs = 2000)
    {
        ArgumentNullException.ThrowIfNull(vision);
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(digits);
        this.vision = vision;
        this.ocr = ocr;
        this.digits = digits;
        ocrTimeout = TimeSpan.FromMilliseconds(Math.Clamp(ocrTimeoutMs, 1, 10_000));
    }

    /// <inheritdoc />
    public ScreenReadResult Read(ReaderDefinition reader, Frame frame, UiStateSnapshot ui)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(ui);

        if (reader.Status != ReaderStatus.Ready)
        {
            var stub = reader.Regions.Select(r => Value(r.Id, ReadStatus.NeedsSurvey, "reader-needs-survey")).ToArray();
            return new ScreenReadResult(reader.Id, ReadStatus.NeedsSurvey, stub, frame.LogAt, frame.CapturedUtc);
        }

        var identity = CheckIdentity(reader.Screen, frame, ui, out var anchors);
        if (identity is not null)
        {
            var mismatch = reader.Regions.Select(r => Value(r.Id, ReadStatus.ScreenMismatch, identity)).ToArray();
            return new ScreenReadResult(reader.Id, ReadStatus.ScreenMismatch, mismatch, frame.LogAt, frame.CapturedUtc);
        }

        var values = new List<ReadValue>(reader.Regions.Count);
        foreach (var region in reader.Regions)
        {
            values.AddRange(ReadRegion(region, frame, anchors));
        }

        var overall = values.Any(v => v.Status == ReadStatus.ScreenMismatch) ? ReadStatus.ScreenMismatch
            : values.Any(v => v.Status == ReadStatus.NeedsSurvey) ? ReadStatus.NeedsSurvey
            : values.Any(v => v.Status == ReadStatus.Unknown) ? ReadStatus.Unknown
            : ReadStatus.Ok;
        var located = anchors.Count > 0 ? new Dictionary<string, PointI>(anchors, StringComparer.Ordinal) : null;
        return new ScreenReadResult(reader.Id, overall, values, frame.LogAt, frame.CapturedUtc, located);
    }

    /// <summary>Primary binarisation per font style name.</summary>
    internal static Binarization PrimaryBinarization(string? fontStyle)
    {
        var s = fontStyle ?? string.Empty;
        if (s.Contains("outlined", StringComparison.OrdinalIgnoreCase) || s.Contains("light", StringComparison.OrdinalIgnoreCase))
        {
            return Binarization.LightOnDark;
        }

        if (s.Contains("dark", StringComparison.OrdinalIgnoreCase) || s.Contains("parchment", StringComparison.OrdinalIgnoreCase))
        {
            return Binarization.DarkOnLight;
        }

        return Binarization.None;
    }

    /// <summary>The binarisation used for the single re-read.</summary>
    internal static Binarization OtherBinarization(Binarization b) => b == Binarization.LightOnDark ? Binarization.DarkOnLight : Binarization.LightOnDark;

    /// <summary>Reader screen "logUiName" for an unlogged window: any log top matches, the anchors decide.</summary>
    public const string AnyTopUi = "*";

    private string? CheckIdentity(ScreenIdentity screen, Frame frame, UiStateSnapshot ui, out Dictionary<string, PointI> anchors)
    {
        anchors = new Dictionary<string, PointI>(StringComparer.Ordinal);
        if (vision.Assess(frame) != FrameQuality.Ok)
        {
            return "frame-invalid";
        }

        // "*" (2026-10-18): a window the game never logs (the gear durability Notice) can sit above any screen; its
        // identity is the anchors alone (ReaderCatalog requires anchors for it)
        var top = ui.AdvisoryTop;
        if (screen.LogUiName != AnyTopUi && (screen.LogUiName is null ? top is not null : !SameUi(top, screen.LogUiName)))
        {
            return "log-top-mismatch";
        }

        foreach (var id in screen.AnchorTemplateIds ?? [])
        {
            var m = vision.Locate(frame, id);
            if (!m.Found)
            {
                return "anchor-not-found";
            }

            anchors[id] = m.Center;
        }

        return null;
    }

    private static bool SameUi(string? a, string b)
    {
        static string Strip(string s) => s.StartsWith("UI_", StringComparison.Ordinal) ? s[3..] : s;
        return a is not null && string.Equals(Strip(a), Strip(b), StringComparison.Ordinal);
    }

    private IReadOnlyList<ReadValue> ReadRegion(RegionDefinition region, Frame frame, Dictionary<string, PointI> anchors)
    {
        var rect = region.ClientRect;
        if (region.AnchorId is { } anchorId)
        {
            if (!anchors.TryGetValue(anchorId, out var c))
            {
                var m = vision.Locate(frame, anchorId);
                if (!m.Found)
                {
                    return [Value(region.Id, ReadStatus.Unknown, "region-anchor-not-found")];
                }

                c = m.Center;
                anchors[anchorId] = c;
            }

            rect = rect.Offset(c.X, c.Y);
        }

        var bounds = new RectI(0, 0, frame.Width, frame.Height);
        if (rect.IsEmpty || bounds.Intersect(rect) != rect)
        {
            return [Value(region.Id, ReadStatus.Unknown, "region-outside-frame")];
        }

        if (region.Rule == ParseRule.IconFingerprint)
        {
            return [ReadIcon(region, frame, rect)];
        }

        if (region.Rule == ParseRule.TileRow)
        {
            return ReadTileRow(region, frame, rect);
        }

        if (region.Rule == ParseRule.IconGrid)
        {
            return ReadIconGrid(region, frame, rect);
        }

        if (region.Optional && IsPlain(frame, rect))
        {
            return [new ReadValue(region.Id, ReadStatus.Ok, null, null, null, null, 0.9, "absent")];
        }

        if (region.Rule == ParseRule.LabeledPair)
        {
            var (value, code) = LastWord(frame, rect);
            return value is { } v
                ? [ReadNumber(region with { Rule = ParseRule.Pair }, frame, v)]
                : [Value(region.Id, ReadStatus.Unknown, code)];
        }

        return [ReadNumber(region, frame, rect)];
    }

    /// <summary>
    /// A row of tiles (<see cref="ParseRule.TileRow"/>): the row value (tile count, or why it is Unknown), then per tile
    /// "icon.k" (fingerprint of the interior, as <see cref="IconFingerprint"/>) and "pair.k" (the 'a/b' under it).
    /// </summary>
    private List<ReadValue> ReadTileRow(RegionDefinition region, Frame frame, RectI band)
    {
        var row = TileRow.Detect(frame, band);
        if (!row.Ok)
        {
            return [Value(region.Id, ReadStatus.Unknown, row.Code)];
        }

        var values = new List<ReadValue> { new(region.Id, ReadStatus.Ok, row.Tiles.Count, null, null, null, 0.9, row.Code, Rect: band) };
        var pairRule = region with { Rule = ParseRule.Pair, AnchorId = null };
        for (var k = 1; k <= row.Tiles.Count; k++)
        {
            var tile = row.Tiles[k - 1];
            var key = IconFingerprint.Prefix + IconFingerprint.Compute(frame, tile.Inflate(-IconFingerprint.Inset));
            values.Add(new ReadValue("icon." + k, ReadStatus.Ok, null, null, key, null, 0.9, "icon", Rect: tile));
            values.Add(ReadNumber(pairRule with { Id = "pair." + k }, frame, PairUnder(tile)));
        }

        return values;
    }

    /// <summary>
    /// A grid of item tiles (<see cref="ParseRule.IconGrid"/>, the Stock tab, 2026-10-15): the region value (whole tile
    /// count, detail "tiles" or "no-tiles", with "-end" when nothing is drawn below the last row: the list ends on screen;
    /// or why the grid is Unknown), then per tile "icon.k" (fingerprint above the count line; rect = the tile) and
    /// "count.k": the count word read like an Int region (OCR and the digit templates on a clean image of its glyphs,
    /// rect = the word), 0 with detail "faded" for a faded tile without a count, else Unknown ("count-absent": a tile in
    /// colour with no count; "count-on-faded": a faded tile with one).
    /// </summary>
    private List<ReadValue> ReadIconGrid(RegionDefinition region, Frame frame, RectI rect)
    {
        var grid = IconGrid.Detect(frame, rect, IconGridGeometry.ForStyle(region.FontStyle));
        if (!grid.Ok)
        {
            return [Value(region.Id, ReadStatus.Unknown, grid.Code)];
        }

        var detail = grid.EndVisible ? grid.Code + "-end" : grid.Code;
        var values = new List<ReadValue> { new(region.Id, ReadStatus.Ok, grid.Tiles.Count, null, null, null, 0.9, detail, Rect: rect) };
        var countRule = region with { Rule = ParseRule.Int, AnchorId = null };
        for (var k = 1; k <= grid.Tiles.Count; k++)
        {
            var tile = grid.Tiles[k - 1];
            var id = "count." + k;
            values.Add(new ReadValue("icon." + k, ReadStatus.Ok, null, null, tile.Key, null, 0.9, tile.Faded ? "icon-faded" : "icon", Rect: tile.Box));
            values.Add((tile.Count, tile.Faded) switch
            {
                ({ } word, false) => ReadCrop(countRule with { Id = id }, word.Image) with { Rect = word.Box },
                ({ } word, true) => Value(id, ReadStatus.Unknown, "count-on-faded") with { Rect = word.Box },
                (null, true) => new ReadValue(id, ReadStatus.Ok, 0, null, null, null, 0.8, "faded", Rect: tile.Box),
                _ => Value(id, ReadStatus.Unknown, "count-absent") with { Rect = tile.Box },
            });
        }

        return values;
    }

    /// <summary>
    /// The 'a/b' box under a tile: 16 px left of it to 17 px right of it, 3..27 px under it (120x24 for an 87 px tile;
    /// measured on live_ability_make_full.png: "40/1" in (1072,766)-(1192,790) under the tile (1088,674)-(1175,763)).
    /// </summary>
    internal static RectI PairUnder(RectI tile) => new(tile.Left - 16, tile.Bottom + 3, tile.Right + 17, tile.Bottom + 27);

    /// <summary>
    /// The last word of a labelled line (<see cref="ParseRule.LabeledPair"/>): glyph boxes (Otsu ink, split at empty
    /// columns); the word starts after the last gap of at least max(5, height / 4) px; the box keeps a blank margin.
    /// Null and a code when there is no label/value split or the value runs into the right edge.
    /// </summary>
    internal (RectI? Value, string Code) LastWord(Frame frame, RectI line)
    {
        var crop = vision.Crop(frame, line, 1.0);
        if (crop.Width < 3 || crop.Pixels.Length < crop.Width * crop.Height)
        {
            return (null, "label-no-ink");
        }

        var boxes = GlyphSegmenter.Boxes(crop, GlyphSegmenter.InkMask(crop));
        if (boxes.Count == 0)
        {
            return (null, "label-no-ink");
        }

        var gap = Math.Max(5, boxes.Max(b => b.Box.Height) / 4);
        var first = 0;
        for (var i = 1; i < boxes.Count; i++)
        {
            if (boxes[i].Box.Left - boxes[i - 1].Box.Right >= gap)
            {
                first = i;
            }
        }

        if (first == 0)
        {
            return (null, "label-no-value");
        }

        const int margin = 3;
        var x0 = boxes[first].Box.Left - margin;
        var x1 = boxes[^1].Box.Right + margin;
        if (x1 > crop.Width)
        {
            return (null, "text-truncated");
        }

        return (new RectI(line.Left + x0, line.Top, line.Left + x1, line.Bottom), "label-value");
    }

    /// <summary>Nothing drawn: fewer than 0.3 % of the pixels differ from the region's median by more than 40.</summary>
    internal bool IsPlain(Frame frame, RectI rect)
    {
        var crop = vision.Crop(frame, rect, 1.0);
        if (crop.Pixels.Length == 0)
        {
            return false;
        }

        var sorted = (byte[])crop.Pixels.Clone();
        Array.Sort(sorted);
        var median = sorted[sorted.Length / 2];
        var off = crop.Pixels.Count(p => Math.Abs(p - median) > 40);
        return off < Math.Max(1, crop.Pixels.Length * 3 / 1000);
    }

    /// <summary>
    /// A number (Int, Pair, Abbreviated, ItemCellCount) or Text read in a client rect (inside the frame); the value
    /// carries the rect.
    /// </summary>
    private ReadValue ReadNumber(RegionDefinition region, Frame frame, RectI rect) => ReadNumberIn(region, frame, rect) with { Rect = rect };

    private ReadValue ReadNumberIn(RegionDefinition region, Frame frame, RectI rect)
    {
        var bounds = new RectI(0, 0, frame.Width, frame.Height);
        if (rect.IsEmpty || bounds.Intersect(rect) != rect)
        {
            return Value(region.Id, ReadStatus.Unknown, "region-outside-frame");
        }

        var crop = vision.Crop(frame, rect, 1.0);
        if (InkTouchesSides(crop) || (region.Rule == ParseRule.Text && InkNearRightEdge(crop, TextEdgeMarginPx)))
        {
            // text running into the left or right edge may be cut (a longer number, a longer name, a scrolling title
            // cut between two words): never read a part
            return Value(region.Id, ReadStatus.Unknown, "text-truncated");
        }

        return ReadCrop(region, crop);
    }

    /// <summary>
    /// A number or text read in a gray crop: OCR with the font style's binarisation and the digit templates must agree
    /// (the other binarisation once more), then the digits-only rule; anything else is Unknown.
    /// </summary>
    private ReadValue ReadCrop(RegionDefinition region, GrayImage crop)
    {
        var ocrPasses = new List<OcrPass>();
        var first = PrimaryBinarization(region.FontStyle);
        var attempt1 = Attempt(region, crop, first, ocrPasses);
        if (attempt1.Status == ReadStatus.Ok)
        {
            return attempt1;
        }

        var attempt2 = Attempt(region, crop, OtherBinarization(first), ocrPasses);
        if (attempt2.Status == ReadStatus.Ok)
        {
            return attempt2 with { Confidence = Math.Round(attempt2.Confidence * 0.9, 3) };
        }

        if (region.Rule != ParseRule.Text && DigitsOnly(region, crop, ocrPasses) is { } digitsOnly)
        {
            return digitsOnly;
        }

        return attempt2 with
        {
            DetailCode = attempt1.DetailCode == attempt2.DetailCode ? attempt1.DetailCode : attempt1.DetailCode + "+" + attempt2.DetailCode,
            GlyphScore = attempt2.GlyphScore ?? attempt1.GlyphScore,
        };
    }

    private static ReadValue ReadIcon(RegionDefinition region, Frame frame, RectI rect)
    {
        var icon = IconFingerprint.Read(frame, rect);
        return icon.Kind switch
        {
            IconTileKind.Icon => new ReadValue(region.Id, ReadStatus.Ok, null, null, icon.Key, null, 0.9, icon.Code),
            IconTileKind.Empty => new ReadValue(region.Id, ReadStatus.Ok, null, null, null, null, 0.9, icon.Code),
            _ => Value(region.Id, ReadStatus.Unknown, icon.Code),
        };
    }

    private ReadValue Attempt(RegionDefinition region, GrayImage crop, Binarization binarization, List<OcrPass> passes)
    {
        if (region.Rule == ParseRule.Text)
        {
            return AttemptText(region, crop, binarization);
        }

        DigitReadResult? d = null;
        var code = "ocr-digits-disagree";

        // every number carries the weakest glyph score of its digit read once it ran (live diagnosis of Unknown values)
        ReadValue Scored(ReadValue v) => d is null ? v : v with { GlyphScore = Math.Round(Math.Max(0, d.MinGlyphScore), 3) };

        foreach (var scale in OcrUpscales)
        {
            var o = ocr.Recognize(crop, new OcrOptions("en-US", scale, binarization), ocrTimeout);
            if (o.Status != OcrStatus.Ok)
            {
                passes.Add(new OcrPass(false, string.Empty));
                return Scored(Value(region.Id, ReadStatus.Unknown, "ocr-" + o.Status.ToString().ToLowerInvariant()));
            }

            var ocrText = ValueParser.NormalizeNumeric(o.Text);
            passes.Add(new OcrPass(true, ocrText));
            var parsed = ValueParser.Parse(ocrText, region.Rule, region.Pair);

            // equipment level labels ('Lv62') are letters: OCR alone classifies the cell as "not stock"
            if (region.Rule == ParseRule.ItemCellCount && parsed is { Ok: true, Code: "cell-equipment" })
            {
                return new ReadValue(region.Id, ReadStatus.Ok, null, null, parsed.Text, null, 0.6, parsed.Code);
            }

            if (region.FontStyle is null || !digits.FontStyles.Contains(region.FontStyle))
            {
                return Value(region.Id, ReadStatus.Unknown, "digit-font-missing");
            }

            d ??= digits.Read(crop, region.FontStyle);
            if (!d.Ok)
            {
                return Scored(Value(region.Id, ReadStatus.Unknown, "digit-low-score"));
            }

            var digitText = ValueParser.NormalizeNumeric(d.Text);
            if (!string.Equals(ocrText, digitText, StringComparison.Ordinal))
            {
                // another scale may read it right; agreement is still required
                code = "ocr-digits-disagree";
                continue;
            }

            if (!parsed.Ok)
            {
                return Scored(Value(region.Id, ReadStatus.Unknown, parsed.Code));
            }

            if (!ValueParser.WithinBounds(parsed, region.Min, region.Max))
            {
                return Scored(Value(region.Id, ReadStatus.Unknown, "out-of-bounds"));
            }

            var confidence = Math.Round(Math.Clamp(0.5 + 0.5 * d.MinGlyphScore, 0, 1), 3);
            return Scored(new ReadValue(region.Id, ReadStatus.Ok, parsed.Value, parsed.Second, parsed.Text, parsed.Range, confidence, parsed.Code));
        }

        return Scored(Value(region.Id, ReadStatus.Unknown, code));
    }

    private ReadValue AttemptText(RegionDefinition region, GrayImage crop, Binarization binarization)
    {
        // text: the two binarisations must agree (no digit templates for words)
        var o = ocr.Recognize(crop, new OcrOptions("en-US", OcrUpscale, binarization), ocrTimeout);
        if (o.Status != OcrStatus.Ok)
        {
            return Value(region.Id, ReadStatus.Unknown, "ocr-" + o.Status.ToString().ToLowerInvariant());
        }

        var t = ValueParser.Parse(o.Text, ParseRule.Text, PairSemantics.None);
        if (!t.Ok)
        {
            return Value(region.Id, ReadStatus.Unknown, t.Code);
        }

        var o2 = ocr.Recognize(crop, new OcrOptions("en-US", OcrUpscale, OtherBinarization(binarization)), ocrTimeout);
        if (o2.Status != OcrStatus.Ok)
        {
            return Value(region.Id, ReadStatus.Unknown, "ocr-" + o2.Status.ToString().ToLowerInvariant());
        }

        var t2 = ValueParser.Parse(o2.Text, ParseRule.Text, PairSemantics.None);
        return t2.Ok && string.Equals(t.Text, t2.Text, StringComparison.OrdinalIgnoreCase)
            ? new ReadValue(region.Id, ReadStatus.Ok, null, null, t.Text, null, 0.6, "text-agree")
            : Value(region.Id, ReadStatus.Unknown, "text-disagree");
    }

    /// <summary>
    /// The digit-template fallback: OCR ran on every pass (no timeout, no error) without agreeing, none of its readings
    /// is a valid value of the rule (so it does not contradict), and every glyph matched a built-in template strongly.
    /// Null when any of that does not hold.
    /// </summary>
    private ReadValue? DigitsOnly(RegionDefinition region, GrayImage crop, IReadOnlyList<OcrPass> passes)
    {
        if (passes.Count == 0 || passes.Any(p => !p.Ran) || region.FontStyle is null || digits is not DigitTemplateReader templates
            || !templates.FontStyles.Contains(region.FontStyle))
        {
            return null;
        }

        var d = templates.ReadDetailed(crop, region.FontStyle);
        if (!d.Ok || d.MinGlyphScore < DigitTemplateReader.StrongGlyphScore || d.MinMargin < DigitTemplateReader.StrongGlyphMargin)
        {
            return null;
        }

        var digitText = ValueParser.NormalizeNumeric(d.Text);
        var parsed = ValueParser.Parse(digitText, region.Rule, region.Pair);
        if (!parsed.Ok || (parsed.Value is null && parsed.Range is null) || !ValueParser.WithinBounds(parsed, region.Min, region.Max))
        {
            return null;
        }

        foreach (var p in passes)
        {
            var other = ValueParser.Parse(p.Text, region.Rule, region.Pair);
            if (other.Ok && ValueParser.WithinBounds(other, region.Min, region.Max) && !string.Equals(p.Text, digitText, StringComparison.Ordinal))
            {
                return null; // OCR read a different plausible value: the readers disagree
            }
        }

        var confidence = Math.Round(Math.Clamp(0.5 * d.MinGlyphScore, 0, 1), 3);
        return new ReadValue(region.Id, ReadStatus.Ok, parsed.Value, parsed.Second, parsed.Text, parsed.Range, confidence, "digits-only", Math.Round(d.MinGlyphScore, 3));
    }

    /// <summary>True when ink (Otsu minority class) lies in the last <paramref name="margin"/> columns of the crop.</summary>
    internal static bool InkNearRightEdge(GrayImage crop, int margin)
    {
        if (crop.Width < 3 || crop.Height < 1 || crop.Pixels.Length < crop.Width * crop.Height)
        {
            return false;
        }

        var ink = GlyphSegmenter.InkMask(crop);
        var from = Math.Max(0, crop.Width - margin);
        for (var y = 0; y < crop.Height; y++)
        {
            for (var x = from; x < crop.Width; x++)
            {
                if (ink[y * crop.Width + x])
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>True when ink (Otsu minority class) reaches the first or last column of the crop.</summary>
    internal static bool InkTouchesSides(GrayImage crop)
    {
        if (crop.Width < 3 || crop.Height < 1 || crop.Pixels.Length < crop.Width * crop.Height)
        {
            return false;
        }

        var ink = GlyphSegmenter.InkMask(crop);
        for (var y = 0; y < crop.Height; y++)
        {
            if (ink[y * crop.Width] || ink[y * crop.Width + crop.Width - 1])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One OCR pass of a number: whether it ran (Ok status) and its normalised text.</summary>
    private readonly record struct OcrPass(bool Ran, string Text);

    private static ReadValue Value(string regionId, ReadStatus status, string code) =>
        new(regionId, status, null, null, null, null, 0, code);
}
