namespace Companion.Contracts;

/// <summary>Integer point. The coordinate space (screen or game client) is stated wherever it is used.</summary>
/// <param name="X">Horizontal coordinate in pixels.</param>
/// <param name="Y">Vertical coordinate in pixels (down is positive).</param>
public readonly record struct PointI(int X, int Y)
{
    /// <summary>Returns this point moved by (dx, dy).</summary>
    public PointI Offset(int dx, int dy) => new(X + dx, Y + dy);

    /// <summary>Chebyshev distance (max of |dx|, |dy|), used for "within N px" checks.</summary>
    public int ChebyshevDistanceTo(PointI other) => Math.Max(Math.Abs(X - other.X), Math.Abs(Y - other.Y));
}

/// <summary>Double-precision point, used for values the game logs with decimals (RaycastResult screenPosition).</summary>
/// <param name="X">Horizontal coordinate.</param>
/// <param name="Y">Vertical coordinate.</param>
public readonly record struct PointD(double X, double Y);

/// <summary>Integer size in pixels.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct SizeI(int Width, int Height);

/// <summary>
/// Integer rectangle with exclusive Right/Bottom (Win32 RECT semantics). The coordinate space is stated where used.
/// </summary>
/// <param name="Left">Left edge (inclusive).</param>
/// <param name="Top">Top edge (inclusive).</param>
/// <param name="Right">Right edge (exclusive).</param>
/// <param name="Bottom">Bottom edge (exclusive).</param>
public readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Width (Right - Left).</summary>
    public int Width => Right - Left;

    /// <summary>Height (Bottom - Top).</summary>
    public int Height => Bottom - Top;

    /// <summary>Area in square pixels; 0 for empty or inverted rectangles.</summary>
    public long Area => Width > 0 && Height > 0 ? (long)Width * Height : 0;

    /// <summary>True when the rectangle has no area.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Integer centre.</summary>
    public PointI Center => new(Left + Width / 2, Top + Height / 2);

    /// <summary>Size of the rectangle.</summary>
    public SizeI Size => new(Width, Height);

    /// <summary>Creates a rectangle from position and size.</summary>
    public static RectI FromXywh(int x, int y, int width, int height) => new(x, y, x + width, y + height);

    /// <summary>Creates a rectangle of the given size centred on a point.</summary>
    public static RectI CenteredAt(PointI center, int width, int height) =>
        FromXywh(center.X - width / 2, center.Y - height / 2, width, height);

    /// <summary>True if the point lies inside (Right/Bottom exclusive).</summary>
    public bool Contains(PointI p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

    /// <summary>True if the point lies inside with at least <paramref name="margin"/> pixels to every edge.</summary>
    public bool ContainsWithMargin(PointI p, int margin) =>
        p.X >= Left + margin && p.X < Right - margin && p.Y >= Top + margin && p.Y < Bottom - margin;

    /// <summary>Returns this rectangle moved by (dx, dy).</summary>
    public RectI Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    /// <summary>Returns this rectangle grown by <paramref name="d"/> on every side (negative shrinks).</summary>
    public RectI Inflate(int d) => new(Left - d, Top - d, Right + d, Bottom + d);

    /// <summary>Intersection, or null when the rectangles do not overlap.</summary>
    public RectI? Intersect(RectI other)
    {
        var r = new RectI(Math.Max(Left, other.Left), Math.Max(Top, other.Top), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
        return r.IsEmpty ? null : r;
    }

    /// <summary>True if the rectangles overlap with a non-empty area.</summary>
    public bool IntersectsWith(RectI other) => Intersect(other) is not null;
}
