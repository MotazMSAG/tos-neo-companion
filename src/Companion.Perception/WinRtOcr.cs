using System.Diagnostics;
using Companion.Contracts;
using Companion.Contracts.Perception;
using Companion.Contracts.Vision;
using Companion.Perception.Internal;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using WinOcrEngine = Windows.Media.Ocr.OcrEngine;
using WinOcrResult = Windows.Media.Ocr.OcrResult;

namespace Companion.Perception;

/// <summary>
/// <see cref="IOcr"/> over the built-in Windows OCR (WinRT projection; offline, no package). Each call preprocesses the
/// crop (upscale, optional Otsu binarisation to dark-on-white, padding), runs the recogniser and waits at most the
/// given timeout: past it the call returns <see cref="OcrStatus.Timeout"/> and the operation is cancelled (OCR-02).
/// Windows OCR gives no confidence; readers synthesise one. Thread-safe (one engine per call); the constructor does
/// nothing.
/// </summary>
public sealed class WinRtOcr : IOcr
{
    /// <summary>Padding (pixels, after upscaling) added around every crop.</summary>
    internal const int Padding = 16;

    private readonly Lazy<bool> available;

    /// <summary>Creates the wrapper (no work until first use).</summary>
    public WinRtOcr()
    {
        available = new Lazy<bool>(() => CreateEngine("en-US") is not null, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public bool IsAvailable => available.Value;

    /// <summary>BCP-47 tags of the recogniser languages installed on this PC.</summary>
    internal static IReadOnlyList<string> AvailableLanguageTags()
    {
        try
        {
            return WinOcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToArray();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>Maximum image dimension the recogniser accepts.</summary>
    internal static uint MaxImageDimension => WinOcrEngine.MaxImageDimension;

    /// <inheritdoc />
    public OcrResult Recognize(GrayImage image, OcrOptions options, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        var sw = Stopwatch.StartNew();
        if (timeout <= TimeSpan.Zero)
        {
            return new OcrResult(OcrStatus.Timeout, string.Empty, [], sw.Elapsed);
        }

        if (image.Width <= 0 || image.Height <= 0 || image.Pixels.Length < image.Width * image.Height)
        {
            return new OcrResult(OcrStatus.Error, string.Empty, [], sw.Elapsed);
        }

        var prepared = Prepare(image, options, out var scale);
        return RecognizePrepared(prepared, scale, options.LanguageTag, timeout, sw);
    }

    /// <summary>Runs the recogniser on an already prepared image (dark text on light, padded by <see cref="Padding"/>).</summary>
    internal static OcrResult RecognizePrepared(GrayImage prepared, double scale, string? languageTag, TimeSpan timeout, Stopwatch sw)
    {
        var engine = CreateEngine(string.IsNullOrWhiteSpace(languageTag) ? "en-US" : languageTag);
        if (engine is null)
        {
            return new OcrResult(OcrStatus.Unavailable, string.Empty, [], sw.Elapsed);
        }

        if (prepared.Width > MaxImageDimension || prepared.Height > MaxImageDimension)
        {
            return new OcrResult(OcrStatus.Error, string.Empty, [], sw.Elapsed);
        }

        SoftwareBitmap? bitmap = null;
        try
        {
            bitmap = ToBitmap(prepared);
            using var cts = new CancellationTokenSource();
            var task = engine.RecognizeAsync(bitmap).AsTask(cts.Token);
            var remaining = timeout - sw.Elapsed;
            if (remaining <= TimeSpan.Zero || !task.Wait(remaining))
            {
                cts.Cancel();
                var orphan = bitmap;
                bitmap = null; // disposed when the cancelled operation completes
                _ = task.ContinueWith(_ => orphan.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return new OcrResult(OcrStatus.Timeout, string.Empty, [], sw.Elapsed);
            }

            return Convert(task.Result, scale, sw.Elapsed);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new OcrResult(OcrStatus.Error, string.Empty, [], sw.Elapsed);
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    /// <summary>
    /// Upscale, polarity + contrast normalisation, pad. Returns the image handed to the recogniser and the upscale
    /// factor used. The two "binarisations" normalise to dark text on white with a percentile contrast stretch that
    /// keeps anti-aliasing: on the proof crops Windows OCR read hard Otsu-thresholded images worse (5/14 vs 7/14).
    /// </summary>
    internal static GrayImage Prepare(GrayImage image, OcrOptions options, out double scale)
    {
        scale = double.IsFinite(options.Upscale) ? Math.Clamp(options.Upscale, 1.0, 8.0) : 1.0;
        var up = OcrImageOps.Upscale(image, scale);
        return options.Binarization switch
        {
            Binarization.LightOnDark => OcrImageOps.Pad(OcrImageOps.Stretch(up, invert: true), Padding, 255),
            Binarization.DarkOnLight => OcrImageOps.Pad(OcrImageOps.Stretch(up, invert: false), Padding, 255),
            _ => OcrImageOps.Pad(up, Padding, OcrImageOps.BorderMedian(up)),
        };
    }

    private static WinOcrEngine? CreateEngine(string languageTag)
    {
        try
        {
            var lang = new Language(languageTag);
            return WinOcrEngine.IsLanguageSupported(lang) ? WinOcrEngine.TryCreateFromLanguage(lang) : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static SoftwareBitmap ToBitmap(GrayImage g)
    {
        var bgra = new byte[g.Width * g.Height * 4];
        for (var i = 0; i < g.Pixels.Length; i++)
        {
            var v = g.Pixels[i];
            bgra[i * 4] = v;
            bgra[i * 4 + 1] = v;
            bgra[i * 4 + 2] = v;
            bgra[i * 4 + 3] = 255;
        }

        var buffer = CryptographicBuffer.CreateFromByteArray(bgra);
        return SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, g.Width, g.Height, BitmapAlphaMode.Premultiplied);
    }

    private static OcrResult Convert(WinOcrResult r, double scale, TimeSpan elapsed)
    {
        var lines = new List<OcrLine>();
        foreach (var line in r.Lines)
        {
            var words = line.Words
                .Select(w => new OcrWord(w.Text, ToInput(w.BoundingRect, scale)))
                .ToArray();
            lines.Add(new OcrLine(line.Text, words));
        }

        var text = string.Join("\n", lines.Select(l => l.Text));
        return new OcrResult(OcrStatus.Ok, text, lines, elapsed);
    }

    /// <summary>Maps a rect in the prepared image back to the caller's crop coordinates.</summary>
    private static RectI ToInput(Windows.Foundation.Rect r, double scale)
    {
        var l = (int)Math.Floor((r.X - Padding) / scale);
        var t = (int)Math.Floor((r.Y - Padding) / scale);
        var rr = (int)Math.Ceiling((r.X + r.Width - Padding) / scale);
        var b = (int)Math.Ceiling((r.Y + r.Height - Padding) / scale);
        return new RectI(l, t, rr, b);
    }
}
