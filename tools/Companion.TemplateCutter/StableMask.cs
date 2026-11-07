using Companion.Contracts;
using Companion.Contracts.Vision;
using Companion.Vision.Internal;

namespace Companion.TemplateCutter;

/// <summary>A per-pixel mask to derive for a committed template.</summary>
/// <param name="TemplateId">Template id; the mask file is <c>&lt;id&gt;.mask.png</c>.</param>
/// <param name="Captures">Full-scale right-HUD strips (client x 1440) that show the template over DIFFERENT scenes.</param>
internal sealed record MaskRequest(string TemplateId, IReadOnlyList<string> Captures)
{
    /// <summary>File name of the mask PNG next to the template.</summary>
    public string MaskFile => TemplateId + ".mask.png";
}

/// <summary>
/// Per-pixel masks for HUD icons drawn on SEE-THROUGH discs (live 2026-10-14: <c>menu.ability</c> cut over one scene
/// scored 0.877 over another). A pixel is kept only when it shows the icon itself: it agrees across the template and
/// every aligned capture of the same HUD state, and it lies on or right next to the bright glyph/label (the opaque
/// outline). Everything else (the scene seen through the disc) and the badge-dot rects are masked out. Pure: bytes
/// in, flags out; no file access.
/// </summary>
internal static class StableMask
{
    /// <summary>Client x/y of the full-scale right-HUD strips (Calibrate HudStripRight, proofs\out\*_x1440.png).</summary>
    public static readonly PointI HudStripOffset = new(1440, 0);

    /// <summary>Alignment search (+- px) of each capture around the template's expected centre.</summary>
    public const int AlignRadius = 6;

    /// <summary>A pixel is stable when every colour channel varies by at most this much across the images.</summary>
    public const int StableRange = 10;

    /// <summary>Minimum luma of the glyph/label core (cream icon fill, white label text).</summary>
    public const int GlyphLuma = 150;

    /// <summary>Stable pixels are kept only within this distance (disc) of the stable glyph core.</summary>
    public const int OutlineRadius = 2;

    /// <summary>Kept 8-connected islands smaller than this are dropped (coincidental agreement).</summary>
    public const int MinIsland = 8;

    /// <summary>The committed per-pixel masks (2026-10-14): each HUD template against the captures of its HUD state.</summary>
    public static IReadOnlyList<MaskRequest> Requests() =>
    [
        // The bag sits in both HUD states: all four scenes.
        new("hud.bag", ["c3b_hud_right_x1440.png", "cal_collapsed_street_x1440.png", "cal_expanded_street_x1440.png", "live_hud_expanded_barrels_x1440.png"]),
        new("hud.toggle.collapsed", ["c3b_hud_right_x1440.png", "cal_collapsed_street_x1440.png"]),
        new("hud.toggle.expanded", ["cal_expanded_street_x1440.png", "live_hud_expanded_barrels_x1440.png"]),
        new("menu.ability", ["cal_expanded_street_x1440.png", "live_hud_expanded_barrels_x1440.png"]),
    ];

    /// <summary>
    /// Crops the template-sized patch of a right-HUD strip at the best NCC centre (template with its rect masks only)
    /// within <see cref="AlignRadius"/> of the expected centre: HUD icons can sit a few px apart between captures.
    /// </summary>
    public static (BgraImage Crop, PointI Center, double Score) AlignedCrop(byte[] stripPng, TemplateDefinition def, BgraImage template)
    {
        var client = SourceImages.ToClient(stripPng, SourceLayout.ClientCropFull, HudStripOffset, out _);
        var t = TemplateImage.Build(def with { MaskFile = null }, template, null, "cutter");
        var e = def.ExpectedCenter;
        var window = new RectI(e.X - AlignRadius, e.Y - AlignRadius, e.X + AlignRadius + 1, e.Y + AlignRadius + 1);
        var map = NccMatcher.Compute(client, t, window) ?? throw new InvalidOperationException("refused: " + def.Id + " lies outside the strip");
        var best = NccMatcher.Best(map, e, AlignRadius) ?? throw new InvalidOperationException("refused: " + def.Id + " has no candidate");
        var tl = t.TopLeftFor(best.Center);
        return (client.CropCopy(RectI.FromXywh(tl.X, tl.Y, t.Width, t.Height)), best.Center, best.Score);
    }

    /// <summary>Derives the keep flags (row-major, template size) from the template and its aligned captures.</summary>
    public static bool[] Derive(BgraImage template, IReadOnlyList<BgraImage> alignedCrops, IReadOnlyList<RectI> maskRects)
    {
        var w = template.Width;
        var h = template.Height;
        if (alignedCrops.Count == 0 || alignedCrops.Any(c => c.Width != w || c.Height != h))
        {
            throw new ArgumentException("every aligned crop must have the template's size", nameof(alignedCrops));
        }

        var images = alignedCrops.Prepend(template).ToArray();
        var stable = new bool[w * h];
        var core = new bool[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var range = 0;
                for (var c = 0; c < 3; c++)
                {
                    int lo = 255, hi = 0;
                    foreach (var img in images)
                    {
                        var v = img.Data[y * img.Stride + x * 4 + c];
                        lo = Math.Min(lo, v);
                        hi = Math.Max(hi, v);
                    }

                    range = Math.Max(range, hi - lo);
                }

                var i = y * w + x;
                stable[i] = range <= StableRange && !InRects(maskRects, x, y);
                var o = y * template.Stride + x * 4;
                core[i] = stable[i] && GrayOps.Luma(template.Data[o], template.Data[o + 1], template.Data[o + 2]) >= GlyphLuma;
            }
        }

        var keep = new bool[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                keep[y * w + x] = stable[y * w + x] && NearCore(core, w, h, x, y);
            }
        }

        DropSmallIslands(keep, w, h);
        return keep;
    }

    /// <summary>Encodes keep flags as a 24bpp mask PNG (white = compare, black = ignore).</summary>
    public static byte[] Encode(bool[] keep, int width, int height)
    {
        var img = BgraImage.Create(width, height);
        for (var i = 0; i < keep.Length; i++)
        {
            var v = keep[i] ? (byte)255 : (byte)0;
            img.Data[i * 4] = v;
            img.Data[i * 4 + 1] = v;
            img.Data[i * 4 + 2] = v;
            img.Data[i * 4 + 3] = 255;
        }

        return PngCodec.Encode24(img);
    }

    /// <summary>Reads a mask PNG back into keep flags (the rule of TemplateImage: black or transparent = ignore).</summary>
    public static bool[] Decode(byte[] maskPng, out int width, out int height)
    {
        var img = PngCodec.Decode(maskPng);
        width = img.Width;
        height = img.Height;
        var keep = new bool[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = y * img.Stride + x * 4;
                var maxc = Math.Max(img.Data[o], Math.Max(img.Data[o + 1], img.Data[o + 2]));
                keep[y * width + x] = img.Data[o + 3] != 0 && maxc >= 16;
            }
        }

        return keep;
    }

    private static bool InRects(IReadOnlyList<RectI> rects, int x, int y)
    {
        foreach (var r in rects)
        {
            if (r.Contains(new PointI(x, y)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool NearCore(bool[] core, int w, int h, int x, int y)
    {
        const int r = OutlineRadius;
        for (var dy = -r; dy <= r; dy++)
        {
            for (var dx = -r; dx <= r; dx++)
            {
                var nx = x + dx;
                var ny = y + dy;
                if (dx * dx + dy * dy <= r * r && nx >= 0 && ny >= 0 && nx < w && ny < h && core[ny * w + nx])
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void DropSmallIslands(bool[] keep, int w, int h)
    {
        var seen = new bool[keep.Length];
        var island = new List<int>();
        var stack = new Stack<int>();
        for (var start = 0; start < keep.Length; start++)
        {
            if (!keep[start] || seen[start])
            {
                continue;
            }

            island.Clear();
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                island.Add(p);
                int px = p % w, py = p / w;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = px + dx, ny = py + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                        {
                            continue;
                        }

                        var q = ny * w + nx;
                        if (keep[q] && !seen[q])
                        {
                            seen[q] = true;
                            stack.Push(q);
                        }
                    }
                }
            }

            if (island.Count < MinIsland)
            {
                foreach (var p in island)
                {
                    keep[p] = false;
                }
            }
        }
    }
}
