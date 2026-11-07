using Companion.Contracts.Logs;

namespace Companion.Contracts.Vision;

/// <summary>
/// A captured game CLIENT image (PrintWindow PW_RENDERFULLCONTENT, client area cropped), 32bpp BGRA, top-down.
/// Full frames are kept in memory only; anything persisted goes through <see cref="IVision.EncodePng"/> with masks
/// (PRIV-14).
/// </summary>
/// <param name="Width">Width (must be 1920 for a valid frame).</param>
/// <param name="Height">Height (must be 1009 for a valid frame).</param>
/// <param name="Stride">Bytes per row.</param>
/// <param name="Bgra">Pixel data (B, G, R, A per pixel).</param>
/// <param name="CapturedAtMonotonic">IClock.Monotonic at capture.</param>
/// <param name="CapturedUtc">Wall time at capture.</param>
/// <param name="LogAt">Log position read right before the capture (popup gate reference, F08).</param>
/// <param name="ClientOriginOnScreen">Client origin on screen at capture (moves when slid).</param>
/// <param name="WhileSlid">True if captured while the window was slid.</param>
public sealed record Frame(
    int Width,
    int Height,
    int Stride,
    byte[] Bgra,
    TimeSpan CapturedAtMonotonic,
    DateTimeOffset CapturedUtc,
    LogPosition? LogAt,
    PointI ClientOriginOnScreen,
    bool WhileSlid);

/// <summary>8-bit grayscale image (crops for matching and OCR).</summary>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
/// <param name="Pixels">Row-major, no padding.</param>
public sealed record GrayImage(int Width, int Height, byte[] Pixels);

/// <summary>Frame validity (PITFALL-14).</summary>
public enum FrameQuality
{
    /// <summary>Plausible game image.</summary>
    Ok,
    /// <summary>Not 1920x1009.</summary>
    WrongSize,
    /// <summary>All (near) black.</summary>
    Black,
    /// <summary>Very low variance on a sample grid.</summary>
    Uniform,
}

/// <summary>Right-side HUD state (F07, F25).</summary>
public enum HudState
{
    /// <summary>Could not decide: WAIT, never click a toggle.</summary>
    Unknown,
    /// <summary>Menu collapsed: skill buttons; corner toggle = four-petal icon.</summary>
    Collapsed,
    /// <summary>Menu expanded: Ability, Skill, Homestead, ...; corner toggle = X.</summary>
    Expanded,
    /// <summary>A full-screen UI covers the HUD (bag, Lumi's Hut, ...).</summary>
    Covered,
    /// <summary>No HUD signature at all (loading, cinema, login).</summary>
    Absent,
}

/// <summary>Template trust level.</summary>
public enum TemplateStatus
{
    /// <summary>Not clickable (half-scale source, older layout, or not yet confirmed). May be used for classification only.</summary>
    Provisional,
    /// <summary>Clickable.</summary>
    Verified,
}

/// <summary>Whether a button fires on press or release (F18). HUD buttons fire on DOWN (EVIDENCE c3).</summary>
public enum FireOn
{
    /// <summary>Fires on LBUTTONDOWN.</summary>
    Down,
    /// <summary>Fires on LBUTTONUP (P5 movement treated like P4).</summary>
    Up,
}

/// <summary>Target tags. The engine gates actions by them (GATE-01) together with the ui-map button safety.</summary>
[Flags]
public enum TargetTags
{
    /// <summary>None.</summary>
    None = 0,
    /// <summary>Opens a screen (navigation only).</summary>
    Nav = 1,
    /// <summary>HUD expand/collapse toggle: clicked only when vision says the opposite state (2 stable captures).</summary>
    Toggle = 2,
    /// <summary>Commits something (Make, Craft, Confirm, Use, Collect...): gated by the automation switch.</summary>
    Commit = 4,
    /// <summary>Never clickable (deny template used as a guard only).</summary>
    Forbidden = 8,
    /// <summary>Part of the HUD signature (bag, toggles).</summary>
    HudAnchor = 16,
    /// <summary>
    /// Screen anchor (identifies a screen). Also the reference of anchor + offset presses (favourites cells, list cards):
    /// when such an anchor slides off the drawn monitor, GameIO may verify the press point instead (off-screen anchor
    /// patch rule, SPEC 7.3), only on the engine's opt-in and never for a commit, gated or Forbidden target.
    /// </summary>
    ScreenAnchor = 32,
    /// <summary>
    /// Fixed HUD navigation button (hud toggles, hud.bag, menu.*): its place never moves. Only such a target may be
    /// pressed without the slid capture check when the slide is cross-monitor and the slid capture shows the target
    /// black (EVIDENCE c13, SPEC 7.3). Never combined with Commit or Forbidden, a gate or an anchor (catalog validation);
    /// core templates only (features may not declare it).
    /// </summary>
    NavFixed = 64,
}

/// <summary>
/// A named click/anchor target (templates\templates.json). Positions are game CLIENT coordinates at 1920x1009.
/// Hard floors in code: threshold &gt;= 0.85, radius &lt;= 40, provisional never clickable, SourceScale must be 1 to be
/// Verified (SEC-1, OCR-01).
/// </summary>
/// <param name="Id">Template id (e.g. "hud.bag").</param>
/// <param name="ImageFile">PNG file name relative to templates\ (or the feature's templates folder).</param>
/// <param name="MaskFile">Optional mask PNG (alpha 0 or black = ignore).</param>
/// <param name="MaskRects">Ignore rectangles relative to the template's top-left (badge dot, timer text).</param>
/// <param name="ExpectedCenter">Expected centre in client coordinates.</param>
/// <param name="SearchRadius">Search radius in pixels around <paramref name="ExpectedCenter"/>.</param>
/// <param name="Threshold">NCC threshold (raised to the floor if lower).</param>
/// <param name="MinMargin">Required best minus second-best score.</param>
/// <param name="Status">Provisional or Verified.</param>
/// <param name="SourceScale">Scale of the source capture (1 = full scale; 2 = half-scale source).</param>
/// <param name="Hud">HUD state the template belongs to, or null if present in both.</param>
/// <param name="Tags">Tags.</param>
/// <param name="FireOn">Down or Up.</param>
/// <param name="Gate">Automation switch for Commit targets.</param>
/// <param name="AnchorId">Optional anchor template; position then = anchor centre + <paramref name="OffsetFromAnchor"/>.</param>
/// <param name="OffsetFromAnchor">Offset from the anchor centre.</param>
/// <param name="Source">Capture file and crop it was cut from (provenance).</param>
/// <param name="GameBuild">Game build fingerprint when cut.</param>
public sealed record TemplateDefinition(
    string Id,
    string ImageFile,
    string? MaskFile,
    IReadOnlyList<RectI>? MaskRects,
    PointI ExpectedCenter,
    int SearchRadius,
    double Threshold,
    double MinMargin,
    TemplateStatus Status,
    int SourceScale,
    HudState? Hud,
    TargetTags Tags,
    FireOn FireOn,
    AutomationSwitch Gate,
    string? AnchorId,
    PointI? OffsetFromAnchor,
    string Source,
    string? GameBuild)
{
    /// <summary>
    /// How far a screen anchor may drift between two captures and still count as the same place (live 2026-10-14: the
    /// crafting page's hanging "Make" title bobs about 4 px up and down). Only honoured for
    /// <see cref="TargetTags.ScreenAnchor"/> templates; clamped to [AimTolerancePx, MaxAnchorMotionPx].
    /// </summary>
    public int? MotionTolerancePx { get; init; }

    /// <summary>The drift tolerance actually used for this template (see <see cref="MotionTolerancePx"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveMotionTolerancePx => Tags.HasFlag(TargetTags.ScreenAnchor) && MotionTolerancePx is { } m
        ? Math.Clamp(m, Safety.HardRules.AimTolerancePx, Safety.HardRules.MaxAnchorMotionPx)
        : Safety.HardRules.AimTolerancePx;
}

/// <summary>Why a match failed.</summary>
public enum MatchFailure
{
    /// <summary>Found.</summary>
    None,
    /// <summary>Best score below threshold.</summary>
    BelowThreshold,
    /// <summary>Best minus second-best below margin (ambiguous: same circular frame, F25).</summary>
    LowMargin,
    /// <summary>Found only farther than the radius from the expected position: layout changed, HALT for recalibration.</summary>
    OutsideRadius,
    /// <summary>Unknown template id.</summary>
    TemplateMissing,
    /// <summary>Template provisional or suspect: not clickable.</summary>
    NotClickable,
    /// <summary>Frame invalid.</summary>
    FrameInvalid,
}

/// <summary>Result of locating a template.</summary>
/// <param name="TemplateId">Template.</param>
/// <param name="Found">True if accepted.</param>
/// <param name="Center">Found centre (client coordinates).</param>
/// <param name="Score">Best NCC score.</param>
/// <param name="SecondBest">Second-best score outside the NMS radius.</param>
/// <param name="DistanceFromExpected">Chebyshev distance of the best match from the expected centre.</param>
/// <param name="Failure">Failure reason.</param>
public sealed record MatchResult(string TemplateId, bool Found, PointI Center, double Score, double SecondBest, int DistanceFromExpected, MatchFailure Failure)
{
    /// <summary>Best minus second-best.</summary>
    public double Margin => Score - SecondBest;
}

/// <summary>HUD classification of one frame.</summary>
/// <param name="State">State.</param>
/// <param name="CollapsedToggleScore">Score of hud.toggle.collapsed at its place.</param>
/// <param name="ExpandedToggleScore">Score of hud.toggle.expanded at its place (provisional template allowed for classification).</param>
/// <param name="BagFound">hud.bag found.</param>
/// <param name="SignatureOk">HUD signature present (bag + one toggle state).</param>
/// <param name="DetailCode">Short code.</param>
public sealed record HudClassification(HudState State, double CollapsedToggleScore, double ExpandedToggleScore, bool BagFound, bool SignatureOk, string DetailCode);

/// <summary>Read access to the templates loaded at host start (unit "vision-ocr"). Thread-safe.</summary>
public interface ITemplateCatalog
{
    /// <summary>Definition by id, or null.</summary>
    TemplateDefinition? Get(string id);

    /// <summary>All definitions.</summary>
    IReadOnlyCollection<TemplateDefinition> All { get; }

    /// <summary>True if clickable now: Verified, SourceScale 1, not suspect, floors respected.</summary>
    bool IsClickable(string id);

    /// <summary>The template image's size in client pixels, or null when unknown (catalogs without images).</summary>
    SizeI? SizeOf(string id) => null;

    /// <summary>Marks a template suspect for this host session after a verified-aim WRONG_UI (F05).</summary>
    void MarkSuspect(string id, string reasonCode);

    /// <summary>True if marked suspect.</summary>
    bool IsSuspect(string id);
}

/// <summary>Start-up loading of templates (core + features); sealed before the first job. Never reloaded from disk.</summary>
public interface ITemplateCatalogBuilder : ITemplateCatalog
{
    /// <summary>Adds a definition with its image bytes (already integrity-checked by the host). Throws after <see cref="Seal"/>.</summary>
    void Add(TemplateDefinition definition, byte[] png, byte[]? maskPng, string sourceCode);

    /// <summary>Freezes the catalog.</summary>
    void Seal();
}

/// <summary>
/// Template matching and frame analysis (unit "vision-ocr"). NCC on grayscale with integral images, per-template masks,
/// zero-variance guard; accept only if score &gt;= threshold AND margin &gt;= MinMargin AND within radius. Pure functions
/// of their inputs (no capture, no I/O); thread-safe.
/// </summary>
public interface IVision
{
    /// <summary>Locates a template around its expected centre (radius from the definition).</summary>
    MatchResult Locate(Frame frame, string templateId);

    /// <summary>Verifies a template at a given client centre within <paramref name="tolerancePx"/> (slid pre-DOWN check, F08).</summary>
    MatchResult VerifyAt(Frame frame, string templateId, PointI expectedCenter, int tolerancePx);

    /// <summary>Classifies the right-side HUD.</summary>
    HudClassification ClassifyHud(Frame frame);

    /// <summary>Frame validity.</summary>
    FrameQuality Assess(Frame frame);

    /// <summary>Fraction (0..1) of sampled pixels in <paramref name="region"/> that changed noticeably between two frames.</summary>
    double RegionChange(Frame a, Frame b, RectI region);

    /// <summary>Grayscale crop, optionally scaled (OCR uses ~3x).</summary>
    GrayImage Crop(Frame frame, RectI region, double scale = 1.0);

    /// <summary>PNG of a region with blackout rectangles applied (client coordinates). The only way frames are persisted.</summary>
    byte[] EncodePng(Frame frame, RectI region, IReadOnlyList<RectI> blackout);
}
