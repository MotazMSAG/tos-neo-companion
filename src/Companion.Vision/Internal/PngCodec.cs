using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Companion.Vision.Internal;

/// <summary>PNG decode/encode and high-quality resampling through System.Drawing (LockBits + Marshal.Copy, no unsafe code).</summary>
internal static class PngCodec
{
    /// <summary>Decodes PNG (or any GDI+ image) bytes into straight-alpha BGRA.</summary>
    public static BgraImage Decode(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length == 0)
        {
            throw new ArgumentException("empty image", nameof(png));
        }

        using var ms = new MemoryStream(png, writable: false);
        using var bmp = new Bitmap(ms);
        return FromBitmap(bmp);
    }

    /// <summary>Encodes an image as a 24bpp PNG (alpha dropped). Frames are only ever persisted this way.</summary>
    public static byte[] Encode24(BgraImage img)
    {
        using var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format24bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, img.Width, img.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[data.Stride];
            for (var y = 0; y < img.Height; y++)
            {
                var src = img.Data.AsSpan(y * img.Stride, img.Width * 4);
                for (int x = 0, o = 0; x < img.Width; x++, o += 3)
                {
                    row[o] = src[x * 4];
                    row[o + 1] = src[x * 4 + 1];
                    row[o + 2] = src[x * 4 + 2];
                }

                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, data.Stride);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>Resamples with GDI+ HighQualityBicubic (the same method the proofs used to save half-scale captures).</summary>
    public static BgraImage ResizeHighQuality(BgraImage src, int width, int height)
    {
        using var source = ToBitmap(src);
        using var dst = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        using (var attrs = new ImageAttributes())
        {
            attrs.SetWrapMode(WrapMode.TileFlipXY);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        }

        return FromBitmap(dst);
    }

    private static BgraImage FromBitmap(Bitmap bmp)
    {
        var img = BgraImage.Create(bmp.Width, bmp.Height);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < img.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, img.Data, y * img.Stride, img.Width * 4);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return img;
    }

    private static Bitmap ToBitmap(BgraImage img)
    {
        var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, img.Width, img.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < img.Height; y++)
            {
                Marshal.Copy(img.Data, y * img.Stride, data.Scan0 + y * data.Stride, img.Width * 4);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return bmp;
    }
}
