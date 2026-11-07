using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;

namespace Companion.GameIO.Geometry;

/// <summary>
/// The two narrow exceptions to the slid template check (SPEC 7.3). Pure functions.
/// <list type="number">
/// <item>Cross-monitor NavFixed rule (EVIDENCE c13): when the slid window's largest overlap is on another monitor,
/// PrintWindow returns only the part of the client on that monitor; the home-monitor part, where the target sits under
/// the cursor, is black, so the slid template check can never pass. A press may then go ahead without it ONLY for a
/// fixed HUD navigation target and ONLY when the slid capture shows exactly that signature.</item>
/// <item>Off-screen anchor patch rule (live run 2026-10-14, ScanRecipeList card 6): an anchor + offset press (a list
/// card, a favourites cell) is verified at its ANCHOR; a slide that puts a far press point under the cursor can push the
/// anchor off every monitor, and PrintWindow draws only the client part on the slid monitor, so the anchor is black.
/// The press may then go ahead ONLY on the engine's opt-in, for a non-commit, ungated screen anchor, when the anchor is
/// really off the drawn monitor and black, and when the client patch around the press point in the slid capture is the
/// same as in GameIO's own located capture (exact client alignment, no search).</item>
/// </list>
/// </summary>
internal static class SlidVerifyRules
{
    /// <summary>Journal value of the <c>verify</c> field when the check was skipped under this rule.</summary>
    public const string SkippedCode = "skipped.crossMonitor.navFixed";

    /// <summary>Journal value of the <c>verify</c> field when the off-screen anchor patch rule let the press go ahead.</summary>
    public const string PatchCode = "patch.anchorOffscreen";

    /// <summary>Journal value of the <c>patch</c> field when the patch rule passed (as the decision or as a shadow score).</summary>
    public const string PatchAllowed = "allowed";

    /// <summary>Largest off-centre aim inside a button (the engine's aim offsets: x up to 18 px, y up to 10 px).</summary>
    public static readonly SizeI MaxAimOffset = new(HardRules.MaxAimOffsetXPx, HardRules.MaxAimOffsetYPx);

    /// <summary>A pixel counts as black when its brightest channel is at most this (c13: the missing part is black).</summary>
    public const int BlackChannelMax = 8;

    /// <summary>Half side of the square checked around the target (HUD icons are about 56 px).</summary>
    public const int TargetHalfSide = 28;

    /// <summary>The target square must be at least this black.</summary>
    public const double TargetBlackMin = 0.98;

    /// <summary>The client part on the slid monitor must have at least this share of non-black pixels (it was rendered).</summary>
    public const double OtherPartNonBlackMin = 0.02;

    /// <summary>
    /// Side of the square patch compared around the press point: smaller than the recipe list's card pitch (113 x 141)
    /// and cards (about 95 px), larger than the aim box (<see cref="MaxAimOffset"/>).
    /// </summary>
    public const int PatchSide = 48;

    /// <summary>
    /// Patch side for a drag's press: a drag may start anywhere in a scrolled list, often in the plain gap between cards
    /// (live 2026-10-15: 15 drags refused as patch.uniform at (420,860)); 128 px always reaches into a card.
    /// </summary>
    public const int DragPatchSide = 128;

    /// <summary>Lowest zero-mean NCC of the slid and located patches (static UI at the same client point is about 1.0).</summary>
    public const double PatchMinNcc = 0.95;

    /// <summary>Both patches need at least this luma standard deviation (population), so the NCC is meaningful.</summary>
    public const double PatchMinStd = 8.0;

    /// <summary>At most this share of a patch may be black (the c13 gap is 0,0,0; the patch must have been drawn).</summary>
    public const double PatchBlackMax = 0.10;

    /// <summary>Largest |mean luma slid - located| of the patches: catches dim overlays, to which the NCC is blind.</summary>
    public const double PatchMaxMeanDelta = 16.0;

    /// <summary>Shortest time from P0 to the slid capture: the focus settle (P2) plus the slide settle (P3).</summary>
    public static readonly TimeSpan MinTimeToSlidCapture = HardRules.FocusSettle + HardRules.SlideSettle;

    /// <summary>The shortest time to the slid capture in <paramref name="mode"/>: foreground mode has no focus settle (SPEC 7.3b).</summary>
    public static TimeSpan MinTimeToSlidCaptureIn(InputMode mode) => mode == InputMode.Foreground ? HardRules.SlideSettle : MinTimeToSlidCapture;

    /// <summary>Sampling step for the other-part check.</summary>
    private const int OtherPartStep = 4;

    /// <summary>True when the slid window's largest overlap (the plan's pure computation) is not the home monitor.</summary>
    public static bool IsCrossMonitor(SlidePlan plan) =>
        !string.Equals(plan.SlidMonitor, plan.HomeMonitor, StringComparison.Ordinal);

    /// <summary>
    /// Null when the request may skip the slid check under the cross-monitor rule; otherwise a refusal code. Requires the
    /// engine's opt-in, a clickable template (Verified, full scale, not suspect, floors) tagged NavFixed and neither Commit
    /// nor Forbidden, no gate, no anchor offset, and a press at the template's centre or an aim inside it (never a grid
    /// offset such as a favourites slot).
    /// </summary>
    /// <param name="request">The click request.</param>
    /// <param name="definition">The template definition from the catalog, or null if unknown.</param>
    /// <param name="clickable">The catalog's IsClickable for the template.</param>
    public static string? NavFixedRefusal(ClickRequest request, TemplateDefinition? definition, bool clickable)
    {
        if (!request.NavFixed)
        {
            return "notRequested";
        }

        if (definition is null)
        {
            return "template.missing";
        }

        if (!clickable || definition.Status != TemplateStatus.Verified)
        {
            return "template.notClickable";
        }

        if (!definition.Tags.HasFlag(TargetTags.NavFixed))
        {
            return "tag.missing";
        }

        if ((definition.Tags & (TargetTags.Commit | TargetTags.Forbidden)) != 0)
        {
            return "commit";
        }

        if (definition.Gate != AutomationSwitch.None)
        {
            return "gated";
        }

        if (definition.AnchorId is not null || definition.OffsetFromAnchor is not null)
        {
            return "anchored";
        }

        if (request.TemplateCenter is { } c
            && (Math.Abs(request.ClientPoint.X - c.X) > MaxAimOffset.Width || Math.Abs(request.ClientPoint.Y - c.Y) > MaxAimOffset.Height))
        {
            return "offset";
        }

        return null;
    }

    /// <summary>
    /// Null when the slid capture shows the c13 signature; otherwise a refusal code. The verify centre must lie on the home
    /// monitor and outside the slid monitor; the square around it must be black; the client part on the slid monitor must
    /// have been rendered (not black too, which would be a black game image rather than the capture gap).
    /// </summary>
    /// <param name="bgra">The slid capture (client, top-down BGRA).</param>
    /// <param name="client">Client size.</param>
    /// <param name="verifyCenter">Client point the template was verified at.</param>
    /// <param name="slidClientOrigin">Client origin on screen while slid.</param>
    /// <param name="homeMonitor">Home monitor bounds.</param>
    /// <param name="slidMonitor">Bounds of the monitor with the slid window's largest overlap, or null if unknown.</param>
    public static string? CaptureGapRefusal(byte[]? bgra, SizeI client, PointI verifyCenter, PointI slidClientOrigin, RectI homeMonitor, RectI? slidMonitor)
    {
        if (!BufferFits(bgra, client))
        {
            return "frame.short";
        }

        if (slidMonitor is not { } other)
        {
            return "slidMonitor.unknown";
        }

        var clientRect = new RectI(0, 0, client.Width, client.Height);
        var onScreen = verifyCenter.Offset(slidClientOrigin.X, slidClientOrigin.Y);
        if (!homeMonitor.Contains(onScreen) || other.Contains(onScreen))
        {
            return "target.notOnHomeMonitor";
        }

        var square = new RectI(verifyCenter.X - TargetHalfSide, verifyCenter.Y - TargetHalfSide, verifyCenter.X + TargetHalfSide, verifyCenter.Y + TargetHalfSide);
        if (square.Intersect(clientRect) is not { } targetArea || BlackFraction(bgra!, client.Width, targetArea, 1) < TargetBlackMin)
        {
            return "target.notBlack";
        }

        var otherPart = other.Offset(-slidClientOrigin.X, -slidClientOrigin.Y).Intersect(clientRect);
        if (otherPart is not { } part)
        {
            return "otherPart.missing";
        }

        if (1.0 - BlackFraction(bgra!, client.Width, part, OtherPartStep) < OtherPartNonBlackMin)
        {
            return "otherPart.black";
        }

        return null;
    }

    /// <summary>
    /// Null when the engine's request qualifies for the off-screen anchor patch rule; otherwise a refusal code (first
    /// failure wins). Requires the engine's opt-in and its located frame, a clickable template (Verified, full scale, not
    /// suspect, floors) tagged ScreenAnchor and neither Commit nor Forbidden, no gate, no anchor of its own, and a press
    /// at a step offset from the anchor's found centre (farther than an aim inside a button), not near a deny zone.
    /// </summary>
    /// <param name="request">The click request (a drag's press as a click).</param>
    /// <param name="definition">The template definition from the catalog, or null if unknown.</param>
    /// <param name="clickable">The catalog's IsClickable for the template.</param>
    public static string? PatchRequestRefusal(ClickRequest request, TemplateDefinition? definition, bool clickable)
    {
        if (!request.PatchVerify)
        {
            return "notRequested";
        }

        if (request.LocatedFrame is null)
        {
            return "located.missing";
        }

        if (definition is null)
        {
            return "template.missing";
        }

        if (!clickable || definition.Status != TemplateStatus.Verified)
        {
            return "template.notClickable";
        }

        if (!definition.Tags.HasFlag(TargetTags.ScreenAnchor))
        {
            return "tag.notAnchor";
        }

        if ((definition.Tags & (TargetTags.Commit | TargetTags.Forbidden)) != 0)
        {
            return "commit";
        }

        if (definition.Gate != AutomationSwitch.None)
        {
            return "gated";
        }

        if (definition.AnchorId is not null || definition.OffsetFromAnchor is not null)
        {
            return "anchored";
        }

        if (request.TemplateCenter is not { } c || c == request.ClientPoint)
        {
            return "notOffset";
        }

        if (Math.Abs(request.ClientPoint.X - c.X) <= MaxAimOffset.Width && Math.Abs(request.ClientPoint.Y - c.Y) <= MaxAimOffset.Height)
        {
            return "aimOnly";
        }

        return request.NearDenyZone ? "nearDenyZone" : null;
    }

    /// <summary>
    /// True when an opted-in press is certain to fail the patch rule's age limit, judged at P0 before anything is sent:
    /// once slid, the anchor centre will lie off the drawn monitor (so only the patch rule could let the press go ahead),
    /// and the located capture would be older than <paramref name="maxAge"/> at the slid capture, which comes at least
    /// <see cref="MinTimeToSlidCapture"/> after P0. False when the drawn monitor is unknown (the slid checks decide).
    /// </summary>
    /// <param name="anchorCenter">Client point the anchor was located at (the request's TemplateCenter).</param>
    /// <param name="press">The client point to press.</param>
    /// <param name="cursor">The resting cursor (screen) the slide is planned for: the slid client origin is cursor - press.</param>
    /// <param name="drawnMonitor">Bounds of the monitor with the planned slid window's largest overlap, or null if unknown.</param>
    /// <param name="locatedAge">Age of the located capture now (P0).</param>
    /// <param name="maxAge">Largest slid-minus-located capture time.</param>
    /// <param name="mode">The input mode (foreground mode reaches the slid capture sooner).</param>
    public static bool PatchLocateTooOld(PointI anchorCenter, PointI press, PointI cursor, RectI? drawnMonitor, TimeSpan locatedAge, TimeSpan maxAge, InputMode mode = InputMode.Cover)
    {
        if (drawnMonitor is not { } drawn)
        {
            return false;
        }

        var anchorOnScreen = anchorCenter.Offset(cursor.X - press.X, cursor.Y - press.Y);
        return !drawn.Contains(anchorOnScreen) && locatedAge + MinTimeToSlidCaptureIn(mode) > maxAge;
    }

    /// <summary>
    /// Null when the slid capture shows the anchor hidden by the capture gap; otherwise a refusal code. The verify centre
    /// must lie off the drawn (slid) monitor, and the part of the square around it that lies off that monitor must be
    /// black: when PrintWindow drew it, the failed template check is a real mismatch.
    /// </summary>
    /// <param name="bgra">The slid capture (client, top-down BGRA).</param>
    /// <param name="client">Client size.</param>
    /// <param name="verifyCenter">Client point the anchor was verified at (its found centre at home).</param>
    /// <param name="slidClientOrigin">Client origin on screen while slid.</param>
    /// <param name="drawnMonitor">Bounds of the monitor with the slid window's largest overlap (the only part PrintWindow
    /// draws, c13), or null if unknown.</param>
    /// <param name="anchorSize">The anchor template's size, when known: its whole box must be drawn to count as visible
    /// (live 2026-10-15: the 110 px Stock tab slid to the screen's right edge passed the 56 px square while its right part
    /// was cut off).</param>
    public static string? AnchorHiddenRefusal(byte[]? bgra, SizeI client, PointI verifyCenter, PointI slidClientOrigin, RectI? drawnMonitor, SizeI? anchorSize = null)
    {
        if (!BufferFits(bgra, client))
        {
            return "frame.short";
        }

        if (drawnMonitor is not { } drawn)
        {
            return "slidMonitor.unknown";
        }

        var clientRect = new RectI(0, 0, client.Width, client.Height);
        if (!clientRect.Contains(verifyCenter))
        {
            return "anchor.outsideClient";
        }

        // The whole square must be drawn for the anchor to count as visible: a half-hidden anchor (live 2026-10-15: the
        // bobbing "Make" title slid to the screen's top edge, centre on screen, top half cut) fails the template check
        // without being a real mismatch. The black check below still proves the missing part was not drawn.
        var halfW = Math.Max(TargetHalfSide, (anchorSize?.Width ?? 0) / 2);
        var halfH = Math.Max(TargetHalfSide, (anchorSize?.Height ?? 0) / 2);
        var onScreen = new RectI(verifyCenter.X - halfW + slidClientOrigin.X, verifyCenter.Y - halfH + slidClientOrigin.Y,
            verifyCenter.X + halfW + slidClientOrigin.X, verifyCenter.Y + halfH + slidClientOrigin.Y);
        if (Within(onScreen, drawn))
        {
            return "anchor.onDrawnMonitor";
        }

        var square = new RectI(verifyCenter.X - halfW, verifyCenter.Y - halfH, verifyCenter.X + halfW, verifyCenter.Y + halfH);
        var area = square.Intersect(clientRect)!.Value;
        long total = 0;
        long black = 0;
        for (var y = area.Top; y < area.Bottom; y++)
        {
            for (var x = area.Left; x < area.Right; x++)
            {
                if (drawn.Contains(new PointI(x + slidClientOrigin.X, y + slidClientOrigin.Y)))
                {
                    continue;
                }

                total++;
                if (IsBlack(bgra!, ((y * client.Width) + x) * 4))
                {
                    black++;
                }
            }
        }

        return total > 0 && (double)black / total >= TargetBlackMin ? null : "anchor.drawn";
    }

    /// <summary>
    /// Compares the client patch around the press point in the slid capture with the same client patch in the located
    /// capture (first failure wins). Both frames must be full client captures; the located one at most
    /// <paramref name="maxAge"/> older than the slid one (measured at the slid capture); the slid client origin must be
    /// the located origin moved by the slide and put the press point exactly under the cursor; the patch must lie inside
    /// the client and on the drawn monitor (half-open containment), be drawn (not black) and textured in both frames,
    /// have the same brightness and match with a zero-mean NCC. Exact alignment: no tolerance search, which would accept
    /// a list that moved.
    /// </summary>
    /// <param name="slid">The slid capture.</param>
    /// <param name="located">The capture the engine located the anchor in (at home).</param>
    /// <param name="press">The client point pressed.</param>
    /// <param name="cursor">The resting cursor (screen) the slide was planned for.</param>
    /// <param name="drawnMonitor">Bounds of the monitor with the slid window's largest overlap, or null if unknown.</param>
    /// <param name="slideDelta">The slide (dx, dy) from home.</param>
    /// <param name="maxAge">Largest slid-minus-located capture time.</param>
    /// <param name="side">Patch side (<see cref="PatchSide"/> for presses, <see cref="DragPatchSide"/> for drags).</param>
    public static PatchVerdict PressPatchVerdict(Frame slid, Frame located, PointI press, PointI cursor, RectI? drawnMonitor, PointI slideDelta, TimeSpan maxAge, int side = PatchSide)
    {
        if (!IsClientFrame(slid))
        {
            return PatchVerdict.Refused("frame.short");
        }

        if (!IsClientFrame(located))
        {
            return PatchVerdict.Refused("located.invalid");
        }

        var age = slid.CapturedAtMonotonic - located.CapturedAtMonotonic;
        var ageMs = (int)Math.Clamp(Math.Round(age.TotalMilliseconds), int.MinValue, int.MaxValue);
        if (age < TimeSpan.Zero)
        {
            return PatchVerdict.Refused("located.future", ageMs);
        }

        if (age > maxAge)
        {
            return PatchVerdict.Refused("located.tooOld", ageMs);
        }

        var origin = slid.ClientOriginOnScreen;
        if (origin != located.ClientOriginOnScreen.Offset(slideDelta.X, slideDelta.Y))
        {
            return PatchVerdict.Refused("origin.mismatch", ageMs);
        }

        if (press.Offset(origin.X, origin.Y) != cursor)
        {
            return PatchVerdict.Refused("press.notUnderCursor", ageMs);
        }

        if (drawnMonitor is not { } drawn)
        {
            return PatchVerdict.Refused("slidMonitor.unknown", ageMs);
        }

        var patch = RectI.CenteredAt(press, side, side);
        if (!Within(patch, new RectI(0, 0, slid.Width, slid.Height)))
        {
            return PatchVerdict.Refused("patch.outsideClient", ageMs);
        }

        if (!Within(patch.Offset(origin.X, origin.Y), drawn))
        {
            return PatchVerdict.Refused("patch.notDrawn", ageMs);
        }

        if (BlackFraction(slid.Bgra, slid.Width, patch, 1) > PatchBlackMax)
        {
            return PatchVerdict.Refused("patch.black", ageMs);
        }

        if (BlackFraction(located.Bgra, located.Width, patch, 1) > PatchBlackMax)
        {
            return PatchVerdict.Refused("located.black", ageMs);
        }

        // Every number is measured before the decision, so each refusal past this point journals all of them (calibration).
        var (slidMean, slidStd) = GrayStats(slid.Bgra, slid.Width, patch);
        var (locatedMean, locatedStd) = GrayStats(located.Bgra, located.Width, patch);
        var minStd = Math.Min(slidStd, locatedStd);
        var delta = Math.Abs(slidMean - locatedMean);
        var score = GrayNcc(slid.Bgra, located.Bgra, slid.Width, patch);
        var refusal = minStd < PatchMinStd ? "patch.uniform"
            : delta > PatchMaxMeanDelta ? "patch.dimmed"
            : score < PatchMinNcc ? "patch.mismatch"
            : null;
        return new PatchVerdict(refusal, score, minStd, delta, ageMs);
    }

    /// <summary>Share (0..1) of pixels in <paramref name="region"/> whose brightest channel is at most <see cref="BlackChannelMax"/>.</summary>
    /// <param name="bgra">Top-down BGRA, stride = width * 4.</param>
    /// <param name="width">Image width.</param>
    /// <param name="region">Region inside the image.</param>
    /// <param name="step">Sampling step on both axes (1 = every pixel).</param>
    public static double BlackFraction(byte[] bgra, int width, RectI region, int step)
    {
        step = Math.Max(1, step);
        long total = 0;
        long black = 0;
        for (var y = region.Top; y < region.Bottom; y += step)
        {
            for (var x = region.Left; x < region.Right; x += step)
            {
                total++;
                if (IsBlack(bgra, ((y * width) + x) * 4))
                {
                    black++;
                }
            }
        }

        return total == 0 ? 0 : (double)black / total;
    }

    /// <summary>Luma of one BGRA pixel (the same integer formula as the vision unit's grayscale).</summary>
    /// <param name="b">Blue.</param>
    /// <param name="g">Green.</param>
    /// <param name="r">Red.</param>
    public static int Luma(byte b, byte g, byte r) => ((b * 29) + (g * 150) + (r * 77) + 128) >> 8;

    /// <summary>Mean and population standard deviation of the luma in <paramref name="region"/>.</summary>
    /// <param name="bgra">Top-down BGRA, stride = width * 4.</param>
    /// <param name="width">Image width.</param>
    /// <param name="region">Non-empty region inside the image.</param>
    public static (double Mean, double Std) GrayStats(byte[] bgra, int width, RectI region)
    {
        long n = 0;
        long sum = 0;
        long sum2 = 0;
        for (var y = region.Top; y < region.Bottom; y++)
        {
            for (var x = region.Left; x < region.Right; x++)
            {
                var i = ((y * width) + x) * 4;
                long l = Luma(bgra[i], bgra[i + 1], bgra[i + 2]);
                n++;
                sum += l;
                sum2 += l * l;
            }
        }

        if (n == 0)
        {
            return (0, 0);
        }

        var mean = (double)sum / n;
        var variance = (double)((n * sum2) - (sum * sum)) / ((double)n * n);
        return (mean, Math.Sqrt(Math.Max(0, variance)));
    }

    /// <summary>
    /// Zero-mean normalized cross-correlation (-1..1) of the luma of two equally sized images over the same
    /// <paramref name="region"/>; 0 when either region has no variance. Integer sums, so identical regions give 1.
    /// </summary>
    /// <param name="a">First image, top-down BGRA, stride = width * 4.</param>
    /// <param name="b">Second image, same layout.</param>
    /// <param name="width">Image width (both).</param>
    /// <param name="region">Region inside both images.</param>
    public static double GrayNcc(byte[] a, byte[] b, int width, RectI region)
    {
        long n = 0;
        long sa = 0;
        long sb = 0;
        long saa = 0;
        long sbb = 0;
        long sab = 0;
        for (var y = region.Top; y < region.Bottom; y++)
        {
            for (var x = region.Left; x < region.Right; x++)
            {
                var i = ((y * width) + x) * 4;
                long la = Luma(a[i], a[i + 1], a[i + 2]);
                long lb = Luma(b[i], b[i + 1], b[i + 2]);
                n++;
                sa += la;
                sb += lb;
                saa += la * la;
                sbb += lb * lb;
                sab += la * lb;
            }
        }

        var va = (n * saa) - (sa * sa);
        var vb = (n * sbb) - (sb * sb);
        if (va <= 0 || vb <= 0)
        {
            return 0;
        }

        var cov = (n * sab) - (sa * sb);
        return Math.Clamp(cov / (Math.Sqrt(va) * Math.Sqrt(vb)), -1.0, 1.0);
    }

    private static bool IsBlack(byte[] bgra, int i) =>
        bgra[i] <= BlackChannelMax && bgra[i + 1] <= BlackChannelMax && bgra[i + 2] <= BlackChannelMax;

    private static bool BufferFits(byte[]? bgra, SizeI client) =>
        bgra is not null && client.Width > 0 && client.Height > 0 && bgra.Length >= (long)client.Width * client.Height * 4;

    // A full 1920x1009 client capture, top-down BGRA without row padding.
    private static bool IsClientFrame(Frame? f) =>
        f is not null && f.Width == HardRules.ExpectedClientWidth && f.Height == HardRules.ExpectedClientHeight && f.Stride == f.Width * 4
        && BufferFits(f.Bgra, new SizeI(f.Width, f.Height));

    // Inner lies entirely inside outer (Right/Bottom exclusive on both).
    private static bool Within(RectI inner, RectI outer) =>
        inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
}

/// <summary>
/// The off-screen anchor patch rule's verdict: allowed or the refusal code, plus what was measured (null when not
/// computed, never NaN: the journal is JSON).
/// </summary>
/// <param name="Refusal">Refusal code, or null when allowed.</param>
/// <param name="Score">Zero-mean NCC of the slid and located patches.</param>
/// <param name="MinStd">The smaller luma standard deviation of the two patches.</param>
/// <param name="MeanDelta">|mean luma slid - located| of the patches.</param>
/// <param name="AgeMs">Slid capture time minus located capture time, in milliseconds.</param>
internal sealed record PatchVerdict(string? Refusal, double? Score, double? MinStd, double? MeanDelta, int? AgeMs)
{
    /// <summary>True when the press may go ahead (or, as a shadow evaluation, would have).</summary>
    public bool Allowed => Refusal is null;

    /// <summary>Journal value of the <c>patch</c> field: <see cref="SlidVerifyRules.PatchAllowed"/> or the refusal code.</summary>
    public string Code => Refusal ?? SlidVerifyRules.PatchAllowed;

    /// <summary>A refusal before anything was measured.</summary>
    /// <param name="code">Refusal code.</param>
    /// <param name="ageMs">Located age, when already known.</param>
    public static PatchVerdict Refused(string code, int? ageMs = null) => new(code, null, null, null, ageMs);
}
