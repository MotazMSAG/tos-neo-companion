using Companion.Contracts;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;

namespace Companion.Vision.Internal;

/// <summary>How an offline capture PNG maps onto the 1920x1009 game client.</summary>
internal enum SourceLayout
{
    /// <summary>Full-scale client image (1920x1009).</summary>
    ClientFull,

    /// <summary>Full-scale crop of the client pasted at an offset (e.g. c3b_hud_right_x1440.png at x=1440).</summary>
    ClientCropFull,

    /// <summary>Half-scale client image (960x504, lib.ps1 Save-Frame default).</summary>
    ClientHalf,

    /// <summary>Half-scale WINDOW image including the frame (968x524; client offset (8,31) at full scale).</summary>
    WindowHalf,
}

/// <summary>
/// Turns offline captures (proofs\out, Calibrate strips) into client-sized frames. Takes bytes, never paths
/// (no install-side file reads in this assembly). Used by the template cutter and the tests.
/// </summary>
internal static class SourceImages
{
    /// <summary>Full-scale client offset inside the window image (proof reports: offset (8,31)).</summary>
    public static readonly PointI WindowClientOffset = new(8, 31);

    /// <summary>Fill colour for client areas a crop does not cover (neutral dark gray).</summary>
    public const byte FillGray = 40;

    /// <summary>Scale of the source relative to the client (1 = full scale).</summary>
    public static int ScaleOf(SourceLayout layout) => layout is SourceLayout.ClientHalf or SourceLayout.WindowHalf ? 2 : 1;

    /// <summary>Builds a 1920x1009 client image from a capture.</summary>
    /// <param name="png">Capture bytes.</param>
    /// <param name="layout">Layout.</param>
    /// <param name="cropOffset">Client position of the crop's top-left (ClientCropFull only).</param>
    /// <param name="covered">Client area covered by real pixels.</param>
    public static BgraImage ToClient(byte[] png, SourceLayout layout, PointI cropOffset, out RectI covered)
    {
        var src = PngCodec.Decode(png);
        const int w = HardRules.ExpectedClientWidth;
        const int h = HardRules.ExpectedClientHeight;
        switch (layout)
        {
            case SourceLayout.ClientFull:
                if (src.Width != w || src.Height != h)
                {
                    throw new ArgumentException($"expected a {w}x{h} client image, got {src.Width}x{src.Height}");
                }

                covered = new RectI(0, 0, w, h);
                return src;

            case SourceLayout.ClientCropFull:
            {
                var dst = BgraImage.Create(w, h);
                dst.Fill(FillGray, FillGray, FillGray);
                dst.Paste(src, cropOffset.X, cropOffset.Y);
                covered = dst.Bounds.Intersect(RectI.FromXywh(cropOffset.X, cropOffset.Y, src.Width, src.Height))
                    ?? throw new ArgumentException("crop lies outside the client");
                return dst;
            }

            case SourceLayout.ClientHalf:
                if (Math.Abs(src.Width * 2 - w) > 2 || Math.Abs(src.Height * 2 - h) > 2)
                {
                    throw new ArgumentException($"expected a half-scale client image (960x504), got {src.Width}x{src.Height}");
                }

                covered = new RectI(0, 0, w, h);
                return PngCodec.ResizeHighQuality(src, w, h);

            case SourceLayout.WindowHalf:
            {
                var ww = src.Width * 2;
                var wh = src.Height * 2;
                if (ww < w + WindowClientOffset.X || wh < h + WindowClientOffset.Y)
                {
                    throw new ArgumentException($"half-scale window image too small: {src.Width}x{src.Height}");
                }

                var full = PngCodec.ResizeHighQuality(src, ww, wh);
                covered = new RectI(0, 0, w, h);
                return full.CropCopy(RectI.FromXywh(WindowClientOffset.X, WindowClientOffset.Y, w, h));
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(layout));
        }
    }

    /// <summary>Same as <see cref="ToClient"/>, wrapped as a frame.</summary>
    public static Frame ToFrame(byte[] png, SourceLayout layout, PointI cropOffset = default) =>
        ToClient(png, layout, cropOffset, out _).ToFrame();
}
