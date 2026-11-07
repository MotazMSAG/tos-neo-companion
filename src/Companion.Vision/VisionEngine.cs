using Companion.Contracts;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.Vision.Internal;

namespace Companion.Vision;

/// <summary>
/// <see cref="IVision"/> implementation (unit "vision-ocr"): masked NCC on grayscale, zero-variance guard, threshold +
/// margin + radius acceptance, HUD classifier, frame checks, region change, crops and the PNG encoder with blackouts.
/// Pure functions of their inputs (no capture, no I/O, no window access); thread-safe.
/// <para>
/// Needs the <see cref="TemplateCatalog"/> of this assembly (it holds the template pixels the contract interface does
/// not expose); any other <see cref="ITemplateCatalog"/> is rejected in the constructor.
/// </para>
/// </summary>
public sealed class VisionEngine : IVision
{
    /// <summary>Template id of the HUD bag anchor.</summary>
    internal const string HudBag = "hud.bag";

    /// <summary>Template id of the collapsed-state toggle (four-petal icon).</summary>
    internal const string HudToggleCollapsed = "hud.toggle.collapsed";

    /// <summary>Template id of the expanded-state toggle (X).</summary>
    internal const string HudToggleExpanded = "hud.toggle.expanded";

    /// <summary>A changed sample: |dB|+|dG|+|dR| above this (same rule as lib.ps1 Get-FrameChange).</summary>
    internal const int ChangeThreshold = 60;

    private readonly TemplateCatalog catalog;

    /// <summary>Creates the vision service over a template catalog (stores it, nothing else).</summary>
    /// <param name="catalog">The <see cref="TemplateCatalog"/> built at host start.</param>
    public VisionEngine(ITemplateCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        this.catalog = catalog as TemplateCatalog
            ?? throw new ArgumentException("VisionEngine needs Companion.Vision.TemplateCatalog (it holds the template pixels)", nameof(catalog));
    }

    /// <inheritdoc />
    public MatchResult Locate(Frame frame, string templateId) => LocateCore(frame, templateId, 0);

    /// <inheritdoc />
    public MatchResult VerifyAt(Frame frame, string templateId, PointI expectedCenter, int tolerancePx)
    {
        var t = catalog.GetImage(templateId);
        if (t is null)
        {
            return Fail(templateId, expectedCenter, MatchFailure.TemplateMissing);
        }

        if (!catalog.IsClickable(templateId))
        {
            return Fail(templateId, expectedCenter, MatchFailure.NotClickable);
        }

        if (Assess(frame) != FrameQuality.Ok)
        {
            return Fail(templateId, expectedCenter, MatchFailure.FrameInvalid);
        }

        var tolerance = Math.Clamp(tolerancePx, 0, HardRules.MaxSearchRadiusPx);
        var outer = Math.Max(tolerance + t.NmsRadius + 2, t.Definition.SearchRadius);
        return Search(BgraImage.FromFrame(frame), t, expectedCenter, tolerance, outer);
    }

    /// <inheritdoc />
    public HudClassification ClassifyHud(Frame frame)
    {
        var quality = Assess(frame);
        if (quality != FrameQuality.Ok)
        {
            var state = quality == FrameQuality.WrongSize ? HudState.Unknown : HudState.Absent;
            return new HudClassification(state, 0, 0, false, false, "frame-" + quality.ToString().ToLowerInvariant());
        }

        if (catalog.GetImage(HudBag) is null || catalog.GetImage(HudToggleCollapsed) is null)
        {
            return new HudClassification(HudState.Unknown, 0, 0, false, false, "templates-missing");
        }

        var bag = Locate(frame, HudBag);
        var collapsed = Locate(frame, HudToggleCollapsed);
        var expanded = catalog.GetImage(HudToggleExpanded) is null
            ? new MatchResult(HudToggleExpanded, false, default, 0, 0, 0, MatchFailure.TemplateMissing)
            : Locate(frame, HudToggleExpanded);

        var c = collapsed.Found;
        var e = expanded.Found;
        var signature = bag.Found && c != e;
        (HudState state2, string code) = (bag.Found, c, e) switch
        {
            (true, true, false) => (HudState.Collapsed, "collapsed"),
            (true, false, true) => (HudState.Expanded, "expanded"),
            (true, true, true) => (HudState.Unknown, "both-toggles"),
            (true, false, false) => (HudState.Unknown, "bag-without-toggle"),
            (false, true, _) or (false, _, true) => (HudState.Unknown, "toggle-without-bag"),
            _ => (HudState.Covered, "no-signature"),
        };

        return new HudClassification(state2, collapsed.Score, expanded.Score, bag.Found, signature, code);
    }

    /// <inheritdoc />
    public FrameQuality Assess(Frame frame)
    {
        if (frame is null || frame.Bgra is null)
        {
            return FrameQuality.WrongSize;
        }

        var img = BgraImage.FromFrame(frame);
        if (frame.Width != HardRules.ExpectedClientWidth || frame.Height != HardRules.ExpectedClientHeight || !img.IsConsistent)
        {
            return FrameQuality.WrongSize;
        }

        const int cols = 96;
        const int rows = 54;
        var black = 0;
        double sum = 0, sum2 = 0;
        for (var j = 0; j < rows; j++)
        {
            var y = (int)((j + 0.5) * frame.Height / rows);
            for (var i = 0; i < cols; i++)
            {
                var x = (int)((i + 0.5) * frame.Width / cols);
                var o = y * frame.Stride + x * 4;
                var b = frame.Bgra[o];
                var g = frame.Bgra[o + 1];
                var r = frame.Bgra[o + 2];
                if (Math.Max(b, Math.Max(g, r)) <= 12)
                {
                    black++;
                }

                double l = GrayOps.Luma(b, g, r);
                sum += l;
                sum2 += l * l;
            }
        }

        const int n = cols * rows;
        if (black >= n * 0.98)
        {
            return FrameQuality.Black;
        }

        var mean = sum / n;
        var std = Math.Sqrt(Math.Max(0, sum2 / n - mean * mean));
        return std < 3.0 ? FrameQuality.Uniform : FrameQuality.Ok;
    }

    /// <inheritdoc />
    public double RegionChange(Frame a, Frame b, RectI region)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var ia = BgraImage.FromFrame(a);
        var ib = BgraImage.FromFrame(b);
        if (!ia.IsConsistent || !ib.IsConsistent)
        {
            return 1.0;
        }

        if (ia.Bounds.Intersect(ib.Bounds) is not { } common || common.Intersect(region) is not { } r)
        {
            return 0.0;
        }

        // at most ~64x64 samples, pixel-centred
        var stepX = Math.Max(1, (int)Math.Ceiling(r.Width / 64.0));
        var stepY = Math.Max(1, (int)Math.Ceiling(r.Height / 64.0));
        int changed = 0, total = 0;
        for (var y = r.Top + stepY / 2; y < r.Bottom; y += stepY)
        {
            for (var x = r.Left + stepX / 2; x < r.Right; x += stepX)
            {
                var oa = y * ia.Stride + x * 4;
                var ob = y * ib.Stride + x * 4;
                var d = Math.Abs(ia.Data[oa] - ib.Data[ob]) + Math.Abs(ia.Data[oa + 1] - ib.Data[ob + 1]) + Math.Abs(ia.Data[oa + 2] - ib.Data[ob + 2]);
                if (d > ChangeThreshold)
                {
                    changed++;
                }

                total++;
            }
        }

        return total == 0 ? 0.0 : (double)changed / total;
    }

    /// <inheritdoc />
    public GrayImage Crop(Frame frame, RectI region, double scale = 1.0)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!double.IsFinite(scale) || scale <= 0 || scale > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "scale must be in (0, 16]");
        }

        var img = BgraImage.FromFrame(frame);
        if (!img.IsConsistent || img.Bounds.Intersect(region) is not { } r)
        {
            return new GrayImage(0, 0, []);
        }

        var gray = GrayOps.ToGray(img, r);
        if (Math.Abs(scale - 1.0) < 1e-9)
        {
            return gray;
        }

        var w = Math.Max(1, (int)Math.Round(r.Width * scale));
        var h = Math.Max(1, (int)Math.Round(r.Height * scale));
        return GrayOps.Resize(gray, w, h);
    }

    /// <inheritdoc />
    public byte[] EncodePng(Frame frame, RectI region, IReadOnlyList<RectI> blackout)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var img = BgraImage.FromFrame(frame);
        if (!img.IsConsistent || img.Bounds.Intersect(region) is not { } r)
        {
            throw new ArgumentException("region does not overlap the frame", nameof(region));
        }

        var crop = img.CropCopy(r);
        foreach (var b in blackout ?? [])
        {
            crop.Blackout(b.Offset(-r.Left, -r.Top));
        }

        return PngCodec.Encode24(crop);
    }

    private MatchResult LocateCore(Frame frame, string templateId, int depth)
    {
        var t = catalog.GetImage(templateId);
        if (t is null)
        {
            return Fail(templateId, default, MatchFailure.TemplateMissing);
        }

        var d = t.Definition;
        var expected = d.ExpectedCenter;
        if (d.AnchorId is { } anchorId)
        {
            if (depth >= 3)
            {
                return Fail(templateId, expected, MatchFailure.TemplateMissing);
            }

            var anchor = LocateCore(frame, anchorId, depth + 1);
            if (!anchor.Found)
            {
                // the anchor's failure says why the dependent template cannot be placed
                return anchor with { TemplateId = templateId, Score = 0, SecondBest = 0 };
            }

            var off = d.OffsetFromAnchor ?? default;
            expected = anchor.Center.Offset(off.X, off.Y);
        }

        if (Assess(frame) != FrameQuality.Ok)
        {
            return Fail(templateId, expected, MatchFailure.FrameInvalid);
        }

        var radius = d.SearchRadius;
        var outer = Math.Min(radius + Math.Max(radius, 16), 2 * HardRules.MaxSearchRadiusPx + 8);
        return Search(BgraImage.FromFrame(frame), t, expected, radius, outer);
    }

    /// <summary>
    /// Scores centres within <paramref name="outer"/> of <paramref name="expected"/> and decides:
    /// best within <paramref name="inner"/> &gt;= threshold and margin ok = found; a match &gt;= threshold only farther away
    /// = OutsideRadius; otherwise BelowThreshold / LowMargin.
    /// </summary>
    private static MatchResult Search(BgraImage img, TemplateImage t, PointI expected, int inner, int outer)
    {
        var d = t.Definition;
        var wanted = new RectI(expected.X - outer, expected.Y - outer, expected.X + outer + 1, expected.Y + outer + 1);
        var map = NccMatcher.Compute(img, t, wanted);
        if (map is null)
        {
            return Fail(d.Id, expected, MatchFailure.BelowThreshold);
        }

        var bestIn = NccMatcher.Best(map, expected, inner);
        var bestAll = NccMatcher.BestAnywhere(map, expected);
        if (bestIn is { } bi && bi.Score >= d.Threshold)
        {
            var second = NccMatcher.SecondBest(map, bi.Center, t.NmsRadius);
            var found = bi.Score - second >= d.MinMargin;
            return new MatchResult(d.Id, found, bi.Center, bi.Score, second, bi.Center.ChebyshevDistanceTo(expected), found ? MatchFailure.None : MatchFailure.LowMargin);
        }

        if (bestAll is { } ba && ba.Score >= d.Threshold)
        {
            var second = NccMatcher.SecondBest(map, ba.Center, t.NmsRadius);
            return new MatchResult(d.Id, false, ba.Center, ba.Score, second, ba.Center.ChebyshevDistanceTo(expected), MatchFailure.OutsideRadius);
        }

        var p = bestIn ?? bestAll ?? new Peak(expected, 0);
        var s2 = NccMatcher.SecondBest(map, p.Center, t.NmsRadius);
        return new MatchResult(d.Id, false, p.Center, p.Score, s2, p.Center.ChebyshevDistanceTo(expected), MatchFailure.BelowThreshold);
    }

    private static MatchResult Fail(string id, PointI at, MatchFailure why) => new(id ?? string.Empty, false, at, 0, 0, 0, why);
}
