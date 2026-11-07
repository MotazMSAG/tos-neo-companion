using Companion.Contracts;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;

namespace Companion.Vision.Internal;

/// <summary>
/// A template prepared for masked NCC: zero-mean values over the active (unmasked) pixels, the mask as 0/1 floats and
/// the template energy. Immutable once built.
/// </summary>
internal sealed class TemplateImage
{
    /// <summary>Minimum active pixels for a usable template.</summary>
    public const int MinActivePixels = 64;

    /// <summary>Minimum template standard deviation over the active pixels (zero-variance guard).</summary>
    public const double MinTemplateStd = 4.0;

    private TemplateImage(TemplateDefinition definition, string sourceCode, GrayImage gray, float[] zeroMean, float[] mask, int active, double energy, double std)
    {
        Definition = definition;
        SourceCode = sourceCode;
        Gray = gray;
        ZeroMean = zeroMean;
        Mask = mask;
        ActiveCount = active;
        Energy = energy;
        Std = std;
    }

    /// <summary>Definition with the code floors applied.</summary>
    public TemplateDefinition Definition { get; }

    /// <summary>Who added it (core or a feature id).</summary>
    public string SourceCode { get; }

    /// <summary>Gray template pixels.</summary>
    public GrayImage Gray { get; }

    /// <summary>Width.</summary>
    public int Width => Gray.Width;

    /// <summary>Height.</summary>
    public int Height => Gray.Height;

    /// <summary>Template minus its mean over active pixels; 0 where masked.</summary>
    public float[] ZeroMean { get; }

    /// <summary>1 for active pixels, 0 for masked ones.</summary>
    public float[] Mask { get; }

    /// <summary>Number of active pixels.</summary>
    public int ActiveCount { get; }

    /// <summary>Sum of squared zero-mean values.</summary>
    public double Energy { get; }

    /// <summary>Standard deviation over active pixels.</summary>
    public double Std { get; }

    /// <summary>Non-maximum-suppression radius for the second-best search.</summary>
    public int NmsRadius => Math.Max(2, Math.Min(Width, Height) / 2);

    /// <summary>Top-left of the template when its centre is at <paramref name="center"/>.</summary>
    public PointI TopLeftFor(PointI center) => new(center.X - Width / 2, center.Y - Height / 2);

    /// <summary>Builds a prepared template. Throws <see cref="ArgumentException"/> for unusable images.</summary>
    public static TemplateImage Build(TemplateDefinition definition, BgraImage image, BgraImage? maskImage, string sourceCode)
    {
        if (image.Width < 8 || image.Height < 8 || image.Width > HardRules.ExpectedClientWidth || image.Height > HardRules.ExpectedClientHeight)
        {
            throw new ArgumentException($"template.size:{definition.Id}");
        }

        if (maskImage is not null && (maskImage.Width != image.Width || maskImage.Height != image.Height))
        {
            throw new ArgumentException($"template.maskSize:{definition.Id}");
        }

        var gray = GrayOps.ToGray(image, image.Bounds);
        var n = gray.Width * gray.Height;
        var mask = new float[n];
        for (var y = 0; y < gray.Height; y++)
        {
            for (var x = 0; x < gray.Width; x++)
            {
                var on = true;
                var o = y * image.Stride + x * 4;
                if (image.Data[o + 3] == 0)
                {
                    on = false; // transparent template pixels are ignored
                }

                if (maskImage is not null)
                {
                    var mo = y * maskImage.Stride + x * 4;
                    var maxc = Math.Max(maskImage.Data[mo], Math.Max(maskImage.Data[mo + 1], maskImage.Data[mo + 2]));
                    if (maskImage.Data[mo + 3] == 0 || maxc < 16)
                    {
                        on = false; // alpha 0 or black = ignore
                    }
                }

                mask[y * gray.Width + x] = on ? 1f : 0f;
            }
        }

        foreach (var r in definition.MaskRects ?? [])
        {
            if (new RectI(0, 0, gray.Width, gray.Height).Intersect(r) is not { } c)
            {
                continue;
            }

            for (var y = c.Top; y < c.Bottom; y++)
            {
                for (var x = c.Left; x < c.Right; x++)
                {
                    mask[y * gray.Width + x] = 0f;
                }
            }
        }

        var active = 0;
        double sum = 0;
        for (var i = 0; i < n; i++)
        {
            if (mask[i] > 0)
            {
                active++;
                sum += gray.Pixels[i];
            }
        }

        if (active < MinActivePixels)
        {
            throw new ArgumentException($"template.maskedOut:{definition.Id}");
        }

        var mean = sum / active;
        var zm = new float[n];
        double energy = 0;
        for (var i = 0; i < n; i++)
        {
            if (mask[i] > 0)
            {
                var d = gray.Pixels[i] - mean;
                zm[i] = (float)d;
                energy += d * d;
            }
        }

        var std = Math.Sqrt(energy / active);
        if (std < MinTemplateStd)
        {
            throw new ArgumentException($"template.zeroVariance:{definition.Id}");
        }

        return new TemplateImage(definition, sourceCode, gray, zm, mask, active, energy, std);
    }
}

/// <summary>Code floors for template definitions (SEC-1): data may tighten, never loosen.</summary>
internal static class TemplateFloors
{
    /// <summary>Applies the floors: threshold and margin raised, radius capped, non-full-scale never Verified.</summary>
    public static TemplateDefinition Apply(TemplateDefinition d)
    {
        var threshold = double.IsFinite(d.Threshold) ? Math.Min(Math.Max(d.Threshold, HardRules.MinNccThreshold), 0.999) : HardRules.MinNccThreshold;
        var margin = double.IsFinite(d.MinMargin) ? Math.Min(Math.Max(d.MinMargin, HardRules.MinMatchMargin), 0.9) : HardRules.MinMatchMargin;
        var radius = Math.Clamp(d.SearchRadius, 1, HardRules.MaxSearchRadiusPx);
        var status = d.Status == TemplateStatus.Verified && d.SourceScale == 1 ? TemplateStatus.Verified : TemplateStatus.Provisional;
        return d with { Threshold = threshold, MinMargin = margin, SearchRadius = radius, Status = status };
    }
}
