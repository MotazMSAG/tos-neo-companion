using Companion.Contracts.Logs;
using Companion.Contracts.Vision;

namespace Companion.Contracts.Perception;

/// <summary>Status of an OCR call.</summary>
public enum OcrStatus
{
    /// <summary>Recognised (text may be empty).</summary>
    Ok,
    /// <summary>Did not finish within the timeout (OCR-02): treat as unknown.</summary>
    Timeout,
    /// <summary>Windows.Media.Ocr not available for the language.</summary>
    Unavailable,
    /// <summary>Failed.</summary>
    Error,
}

/// <summary>Binarisation before OCR (re-read with the other preprocessing, OCR-01).</summary>
public enum Binarization
{
    /// <summary>None.</summary>
    None,
    /// <summary>Light text on dark/coloured background (bag counts: white outlined digits).</summary>
    LightOnDark,
    /// <summary>Dark text on light background (parchment panels).</summary>
    DarkOnLight,
}

/// <summary>OCR options.</summary>
/// <param name="LanguageTag">BCP-47 tag (en-US).</param>
/// <param name="Upscale">Upscale factor before recognition (~3).</param>
/// <param name="Binarization">Binarisation.</param>
public sealed record OcrOptions(string LanguageTag = "en-US", double Upscale = 3.0, Binarization Binarization = Binarization.None);

/// <summary>An OCR word (Windows.Media.Ocr has no confidence; confidence is synthesised by readers).</summary>
/// <param name="Text">Text.</param>
/// <param name="Bounds">Bounds in the input image.</param>
public sealed record OcrWord(string Text, RectI Bounds);

/// <summary>An OCR line.</summary>
/// <param name="Text">Text.</param>
/// <param name="Words">Words.</param>
public sealed record OcrLine(string Text, IReadOnlyList<OcrWord> Words);

/// <summary>OCR result.</summary>
/// <param name="Status">Status.</param>
/// <param name="Text">Full text.</param>
/// <param name="Lines">Lines.</param>
/// <param name="Elapsed">Duration.</param>
public sealed record OcrResult(OcrStatus Status, string Text, IReadOnlyList<OcrLine> Lines, TimeSpan Elapsed);

/// <summary>Windows.Media.Ocr wrapper (unit "vision-ocr", Companion.Perception). Offline, built into Windows. Thread-safe; every call bounded by its timeout.</summary>
public interface IOcr
{
    /// <summary>True if an OCR engine for en-US is available.</summary>
    bool IsAvailable { get; }

    /// <summary>Recognises text; returns Timeout instead of blocking past <paramref name="timeout"/>.</summary>
    OcrResult Recognize(GrayImage image, OcrOptions options, TimeSpan timeout);
}

/// <summary>Digit-template reader result.</summary>
/// <param name="Ok">True if every glyph matched above threshold.</param>
/// <param name="Text">Digits and separators read ('/', 'K', 'M').</param>
/// <param name="MinGlyphScore">Weakest glyph score.</param>
public sealed record DigitReadResult(bool Ok, string Text, double MinGlyphScore);

/// <summary>Digit templates for the game's number fonts, per font style (OCR-01). Full-scale sources only.</summary>
public interface IDigitReader
{
    /// <summary>Font styles available (e.g. "bag-count-outlined", "parchment-dark").</summary>
    IReadOnlyCollection<string> FontStyles { get; }

    /// <summary>Reads digits in a crop.</summary>
    DigitReadResult Read(GrayImage image, string fontStyle);
}

/// <summary>Reader readiness.</summary>
public enum ReaderStatus
{
    /// <summary>Regions measured from full-scale captures.</summary>
    Ready,
    /// <summary>Explicit stub: the screen was never surveyed; reading returns NeedsSurvey (no invented coordinates).</summary>
    NeedsSurvey,
}

/// <summary>Parse rules.</summary>
public enum ParseRule
{
    /// <summary>A single integer.</summary>
    Int,
    /// <summary>'a/b' pair with named semantics (<see cref="PairSemantics"/>).</summary>
    Pair,
    /// <summary>Free text (item names).</summary>
    Text,
    /// <summary>Abbreviated number ('14M', '7146K'): stored as a range.</summary>
    Abbreviated,
    /// <summary>Bag cell: empty = 1 for known stackables, 'Lv\d+' = equipment (not stock), else digits (SCAN-03).</summary>
    ItemCellCount,
    /// <summary>
    /// Item icon tile (no OCR): the region is the tile's outer box. Text = "icon:" + a perceptual fingerprint (hex) of
    /// the tile interior, identical for the same icon in any slot; Ok with no text = the slot is empty; Unknown when
    /// the tile is not where expected (never a guessed identity). Added 2026-10-14 for the Make tab's material slots,
    /// whose names are not shown on screen.
    /// </summary>
    IconFingerprint,

    /// <summary>
    /// A row of item tiles DETECTED along the region (a horizontal band as high as the tiles), not read at fixed slots
    /// (added 2026-10-14: the Make tab shows 3 or 4 materials, centred, so the slots shift with the count). Each tile
    /// (dark outline, white border, as tall as the band) gives "icon.k" (k = 1, 2, ... from the left; Text as
    /// <see cref="IconFingerprint"/>) and the 'a/b' text under it gives "pair.k", read like <see cref="Pair"/> with the
    /// region's semantics, font style and bounds. A band with no tile at all reads as no materials (no values); tiles of
    /// unequal size or spacing, or ink that is not a tile, give the region id with Unknown (never a guessed layout).
    /// </summary>
    TileRow,

    /// <summary>
    /// An 'a/b' pair at the end of a labelled line (added 2026-10-14, e.g. "Requires: 86/5" under the materials): the
    /// region covers the line; the value is its last word (the ink after the last gap wider than a quarter of the text
    /// height), read like <see cref="Pair"/>. A label without such a value, or a value touching the region's right edge,
    /// is Unknown.
    /// </summary>
    LabeledPair,

    /// <summary>
    /// A scrolling grid of item tiles with a count at each tile's bottom right (added 2026-10-15 for the Ability view's
    /// Stock tab): the region is the grid's visible area; only WHOLE tiles (outline on every side, inside the region) are
    /// read. The region id gives the tile count (Ok; detail "tiles" or "no-tiles", with "-end" when nothing is drawn below
    /// the last row, so the end of the list is on screen) or why the grid could not be read (Unknown); then per tile, row by
    /// row and left to right, "icon.k" (Text as <see cref="IconFingerprint"/>, from the icon above the count line; the
    /// value's rect is the tile) and "count.k": Ok with the count (the region's font style, bounds; read like
    /// <see cref="Int"/>), Ok with 0 and detail "faded" for a faded tile without a count, or Unknown (a tile that is not
    /// faded but shows no readable count, a count that does not read, a faded tile with a count).
    /// </summary>
    IconGrid,
}

/// <summary>Meaning of an 'a/b' pair (PAIR-01). Bound to a screen; never generic.</summary>
public enum PairSemantics
{
    /// <summary>Not a pair.</summary>
    None,
    /// <summary>Owned / required (crafting view; order not yet observed: needsSurvey).</summary>
    OwnedRequired,
    /// <summary>Stored / capacity (Lumi's Hut products, e.g. 18/18).</summary>
    StoredCapacity,
    /// <summary>Assigned / slots (Lumi's Hut 'Managing: 5/60').</summary>
    AssignedSlots,

    /// <summary>Current / target of a progress counter (the Make tab's small '5/300' under the product picture); current never exceeds the target.</summary>
    Progress,
}

/// <summary>Screen identity a reader is bound to: log UI name AND anchor templates in the same capture.</summary>
/// <param name="LogUiName">Expected top UI from the log (null for HUD).</param>
/// <param name="AnchorTemplateIds">Anchors that must be found.</param>
public sealed record ScreenIdentity(string? LogUiName, IReadOnlyList<string> AnchorTemplateIds);

/// <summary>A region to read.</summary>
/// <param name="Id">Region id.</param>
/// <param name="ClientRect">Rect in client coordinates (or relative to <paramref name="AnchorId"/>).</param>
/// <param name="AnchorId">Optional anchor template; rect is relative to its found centre.</param>
/// <param name="Rule">Parse rule.</param>
/// <param name="Pair">Pair semantics for <see cref="ParseRule.Pair"/>.</param>
/// <param name="FontStyle">Digit font style.</param>
/// <param name="Min">Plausible minimum.</param>
/// <param name="Max">Plausible maximum.</param>
/// <param name="Optional">A value only some screens show (added 2026-10-14): when nothing is drawn in the region (plain
/// background) it reads Ok with no value and detail "absent" instead of Unknown. Anything drawn is read as usual.</param>
public sealed record RegionDefinition(
    string Id,
    RectI ClientRect,
    string? AnchorId,
    ParseRule Rule,
    PairSemantics Pair,
    string? FontStyle,
    long? Min,
    long? Max,
    bool Optional = false);

/// <summary>A declarative screen reader (data: readers.json or a feature's readers file).</summary>
/// <param name="Id">Reader id.</param>
/// <param name="Screen">Screen identity.</param>
/// <param name="Regions">Regions.</param>
/// <param name="Status">Ready or NeedsSurvey.</param>
/// <param name="Notes">What must be surveyed.</param>
public sealed record ReaderDefinition(string Id, ScreenIdentity Screen, IReadOnlyList<RegionDefinition> Regions, ReaderStatus Status, string? Notes);

/// <summary>Status of one read value.</summary>
public enum ReadStatus
{
    /// <summary>OCR and digit reader agree and semantic checks pass.</summary>
    Ok,
    /// <summary>Could not read with confidence (never a guess).</summary>
    Unknown,
    /// <summary>Reader or region not surveyed.</summary>
    NeedsSurvey,
    /// <summary>Screen identity not confirmed by log AND anchors in this capture.</summary>
    ScreenMismatch,
}

/// <summary>One read value.</summary>
/// <param name="RegionId">Region.</param>
/// <param name="Status">Status.</param>
/// <param name="Value">First number (or the only one).</param>
/// <param name="Second">Second number of a pair.</param>
/// <param name="Text">Text for Text rules (item names; never chat), or the "icon:..." key of an IconFingerprint region.</param>
/// <param name="Range">Range for Abbreviated.</param>
/// <param name="Confidence">Synthesised 0..1.</param>
/// <param name="DetailCode">Short code (e.g. "ocr-digits-disagree").</param>
/// <param name="GlyphScore">Optional (added 2026-10-15): the weakest glyph's template score of the digit read behind a
/// number (Ok or not; null when no digit template ran), for live diagnosis of an Unknown value.</param>
/// <param name="Rect">Optional (added 2026-10-15): the client rect the value was read from (e.g. a detected material
/// tile for "icon.k"); null when not measured.</param>
public sealed record ReadValue(
    string RegionId,
    ReadStatus Status,
    long? Value,
    long? Second,
    string? Text,
    Knowledge.ValueRange? Range,
    double Confidence,
    string DetailCode,
    double? GlyphScore = null,
    RectI? Rect = null);

/// <summary>Result of reading a screen.</summary>
/// <param name="ReaderId">Reader.</param>
/// <param name="Status">Overall status (worst of identity / values).</param>
/// <param name="Values">Values by region.</param>
/// <param name="LogAt">Log position of the frame (for stock observations).</param>
/// <param name="CapturedUtc">Capture time.</param>
/// <param name="Anchors">Optional (added 2026-10-15): the client centre of every anchor template found for this read
/// (template id → centre), so a caller can turn a value's <see cref="ReadValue.Rect"/> into an anchor-relative
/// offset; null when none was located.</param>
public sealed record ScreenReadResult(
    string ReaderId,
    ReadStatus Status,
    IReadOnlyList<ReadValue> Values,
    LogPosition? LogAt,
    DateTimeOffset CapturedUtc,
    IReadOnlyDictionary<string, PointI>? Anchors = null);

/// <summary>Readers loaded at start (core + features). Sealed before the first job.</summary>
public interface IReaderCatalog
{
    /// <summary>Reader by id, or null.</summary>
    ReaderDefinition? Get(string id);

    /// <summary>All readers.</summary>
    IReadOnlyCollection<ReaderDefinition> All { get; }
}

/// <summary>Start-up loading of readers.</summary>
public interface IReaderCatalogBuilder : IReaderCatalog
{
    /// <summary>Adds a reader. Throws after <see cref="Seal"/>.</summary>
    void Add(ReaderDefinition reader, string sourceCode);

    /// <summary>Freezes the catalog.</summary>
    void Seal();
}

/// <summary>
/// Applies a reader to a frame (unit "vision-ocr", Companion.Perception): confirms screen identity (log top UI AND
/// anchors), crops regions, reads with OCR + digit templates (re-read once with the other preprocessing), applies
/// semantic checks, returns Unknown rather than a guess. Pure (no capture); thread-safe.
/// </summary>
public interface IScreenReader
{
    /// <summary>Reads a screen.</summary>
    ScreenReadResult Read(ReaderDefinition reader, Frame frame, UiStateSnapshot ui);
}
