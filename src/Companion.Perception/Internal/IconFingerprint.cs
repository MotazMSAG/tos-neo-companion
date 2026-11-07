using Companion.Contracts;
using Companion.Contracts.Vision;

namespace Companion.Perception.Internal;

/// <summary>What an icon-tile region holds.</summary>
internal enum IconTileKind
{
    /// <summary>A tile with its outline at the expected place: fingerprinted.</summary>
    Icon,

    /// <summary>No tile at all (plain background): the slot is empty.</summary>
    Empty,

    /// <summary>Something is there, but not a tile at the expected place (layout differs): never fingerprinted.</summary>
    NotAligned,
}

/// <summary>Result of reading one icon tile.</summary>
/// <param name="Kind">Icon, empty or not aligned.</param>
/// <param name="Key">"icon:" + hex fingerprint (Icon only).</param>
/// <param name="Tile">Tile box found (Icon only), client coordinates.</param>
/// <param name="Code">Detail code.</param>
internal readonly record struct IconTileRead(IconTileKind Kind, string? Key, RectI? Tile, string Code);

/// <summary>
/// Identity of an item icon tile without OCR (material names are not shown on the Make tab). The region is the tile's
/// OUTER box as measured on proofs\out\live_ability_make_full.png (87x89, outline to outline: a dark outline, a white
/// border, an inner outline, then the icon on a rarity-coloured background).
/// <list type="number">
/// <item>The tile must be where expected: the OUTERMOST dark outline column/row (luma &lt; <see cref="OutlineLuma"/> on
/// at least 75% of the middle half; the green tiles' outline is lighter) within <see cref="EdgeTolerance"/> px of each
/// expected edge, and the box size within the same tolerance. Otherwise the region is either plain background (luma
/// standard deviation &lt; <see cref="EmptyStdDev"/>: an empty slot) or something else (not aligned: a different layout,
/// an animation, a popup): the caller reports Unknown, never a guessed identity.</item>
/// <item>The fingerprint uses the interior (the found box inset by <see cref="Inset"/>), so it does not depend on the
/// slot position or a 1-2 px shift: 4 x 64 bits on an 8x8 (9x8) grid of cell means: luma above the tile mean, luma
/// gradient (dHash), and two chroma axes (R-B, G-R) above their tile means.</item>
/// </list>
/// The same icon gives the same (or a very close) key in every slot; two keys are the same material when their Hamming
/// distance is at most <see cref="SameIconMaxDistance"/> bits. Pure; thread-safe.
/// </summary>
internal static class IconFingerprint
{
    /// <summary>Key prefix.</summary>
    public const string Prefix = "icon:";

    /// <summary>Fingerprint size in bits (64 hex characters).</summary>
    public const int Bits = 256;

    /// <summary>Two keys within this many differing bits are the same icon.</summary>
    public const int SameIconMaxDistance = 24;

    /// <summary>Luma of the tile outline (measured 117-133; parchment 229, interior 170-190).</summary>
    internal const int OutlineLuma = 160;

    /// <summary>How far around each expected edge the outline is searched.</summary>
    internal const int EdgeSearch = 8;

    /// <summary>Largest accepted deviation of a found edge from the expected one.</summary>
    internal const int EdgeTolerance = 3;

    /// <summary>Outline + white border + inner outline (measured 7 px); the interior starts inside it.</summary>
    internal const int Inset = 9;

    /// <summary>Below this luma standard deviation the region is plain background (an empty slot).</summary>
    internal const double EmptyStdDev = 8.0;

    /// <summary>Reads one tile whose outer box is expected at <paramref name="expected"/> (client coordinates).</summary>
    public static IconTileRead Read(Frame frame, RectI expected)
    {
        var bounds = new RectI(0, 0, frame.Width, frame.Height);
        if (expected.Width < 3 * Inset || expected.Height < 3 * Inset || bounds.Intersect(expected) != expected
            || frame.Bgra.Length < frame.Stride * frame.Height || frame.Stride < frame.Width * 4)
        {
            return new IconTileRead(IconTileKind.NotAligned, null, null, "icon-outside-frame");
        }

        var window = bounds.Intersect(expected.Inflate(EdgeSearch))!.Value;
        var luma = LumaPlane(frame, window);
        var box = FindTile(luma, window, expected);
        if (box is null)
        {
            var inner = expected.Inflate(-Inset);
            return StdDev(luma, window, inner) < EmptyStdDev
                ? new IconTileRead(IconTileKind.Empty, null, null, "icon-empty")
                : new IconTileRead(IconTileKind.NotAligned, null, null, "icon-not-aligned");
        }

        var interior = box.Value.Inflate(-Inset);
        return new IconTileRead(IconTileKind.Icon, Prefix + Compute(frame, interior), box, "icon");
    }

    /// <summary>True for a well-formed key ("icon:" + 64 lower-case hex characters).</summary>
    public static bool IsKey(string? s)
    {
        if (s is null || s.Length != Prefix.Length + Bits / 4 || !s.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = Prefix.Length; i < s.Length; i++)
        {
            if (!char.IsAsciiHexDigitLower(s[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Number of differing bits between two keys (int.MaxValue when either is not a key).</summary>
    public static int Distance(string a, string b)
    {
        if (!IsKey(a) || !IsKey(b))
        {
            return int.MaxValue;
        }

        static int Nibble(char c) => c <= '9' ? c - '0' : c - 'a' + 10;
        var d = 0;
        for (var i = Prefix.Length; i < a.Length; i++)
        {
            d += System.Numerics.BitOperations.PopCount((uint)(Nibble(a[i]) ^ Nibble(b[i])));
        }

        return d;
    }

    /// <summary>
    /// The 256-bit fingerprint (lower-case hex) of an interior rect (must lie inside the frame): the shared
    /// <see cref="IconPrint.Compute"/> (one fingerprint for the readers and the features' list scans).
    /// </summary>
    internal static string Compute(Frame frame, RectI interior) => IconPrint.Compute(frame, interior);

    /// <summary>
    /// A 256-bit fingerprint (lower-case hex) that also keeps the icon's absolute hue (added 2026-10-15 for the Stock tab,
    /// whose item families share one drawing in several colours: a golden and a sage leaf, red and orange powder bags,
    /// 7-21 bits apart in <see cref="Compute"/>, whose colour bits are relative to the tile's own mean): the shared
    /// <see cref="IconPrint.ComputeWithHue"/> (luma above the tile mean and the luma gradient as in <see cref="Compute"/>,
    /// then per 8x8 cell R &gt; G and B &gt; R).
    /// </summary>
    internal static string ComputeWithHue(Frame frame, RectI interior) => IconPrint.ComputeWithHue(frame, interior);

    /// <summary>The tile box (outline to outline, exclusive right/bottom) or null when it is not where expected.</summary>
    private static RectI? FindTile(int[] luma, RectI window, RectI expected)
    {
        var w = window.Width;
        var bandTop = expected.Top + expected.Height / 4;
        var bandBottom = expected.Bottom - expected.Height / 4;
        var bandLeft = expected.Left + expected.Width / 4;
        var bandRight = expected.Right - expected.Width / 4;

        bool EdgeColumn(int x)
        {
            var dark = 0;
            for (var y = bandTop; y < bandBottom; y++)
            {
                if (luma[(y - window.Top) * w + (x - window.Left)] < OutlineLuma)
                {
                    dark++;
                }
            }

            return dark * 4 >= (bandBottom - bandTop) * 3;
        }

        bool EdgeRow(int y)
        {
            var dark = 0;
            for (var x = bandLeft; x < bandRight; x++)
            {
                if (luma[(y - window.Top) * w + (x - window.Left)] < OutlineLuma)
                {
                    dark++;
                }
            }

            return dark * 4 >= (bandRight - bandLeft) * 3;
        }

        // The outline is the OUTERMOST dark line: dark icon content just inside it must not be taken for it.
        var left = Outermost(expected.Left, -1, window.Left, window.Right - 1, EdgeColumn);
        var right = Outermost(expected.Right - 1, +1, window.Left, window.Right - 1, EdgeColumn);
        var top = Outermost(expected.Top, -1, window.Top, window.Bottom - 1, EdgeRow);
        var bottom = Outermost(expected.Bottom - 1, +1, window.Top, window.Bottom - 1, EdgeRow);
        if (left is null || right is null || top is null || bottom is null)
        {
            return null;
        }

        var box = new RectI(left.Value, top.Value, right.Value + 1, bottom.Value + 1);
        return Math.Abs(box.Width - expected.Width) <= EdgeTolerance && Math.Abs(box.Height - expected.Height) <= EdgeTolerance
            ? box
            : null;
    }

    /// <summary>
    /// The edge line within <see cref="EdgeTolerance"/> of <paramref name="at"/> that lies farthest in direction
    /// <paramref name="outward"/> (-1 = towards lower coordinates), or null.
    /// </summary>
    private static int? Outermost(int at, int outward, int min, int max, Func<int, bool> isEdge)
    {
        for (var p = at + outward * EdgeTolerance; p != at - outward * (EdgeTolerance + 1); p -= outward)
        {
            if (p >= min && p <= max && isEdge(p))
            {
                return p;
            }
        }

        return null;
    }

    private static int[] LumaPlane(Frame frame, RectI r)
    {
        var plane = new int[r.Width * r.Height];
        for (var y = 0; y < r.Height; y++)
        {
            var row = (r.Top + y) * frame.Stride;
            for (var x = 0; x < r.Width; x++)
            {
                var o = row + (r.Left + x) * 4;
                plane[y * r.Width + x] = (frame.Bgra[o] * 29 + frame.Bgra[o + 1] * 150 + frame.Bgra[o + 2] * 77 + 128) >> 8;
            }
        }

        return plane;
    }

    private static double StdDev(int[] luma, RectI window, RectI r)
    {
        double sum = 0, sq = 0;
        var n = 0;
        for (var y = r.Top; y < r.Bottom; y++)
        {
            for (var x = r.Left; x < r.Right; x++)
            {
                var v = luma[(y - window.Top) * window.Width + (x - window.Left)];
                sum += v;
                sq += (double)v * v;
                n++;
            }
        }

        if (n == 0)
        {
            return 0;
        }

        var mean = sum / n;
        return Math.Sqrt(Math.Max(0, sq / n - mean * mean));
    }
}
