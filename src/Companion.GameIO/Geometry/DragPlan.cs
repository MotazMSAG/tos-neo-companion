using Companion.Contracts;
using Companion.Contracts.Safety;

namespace Companion.GameIO.Geometry;

/// <summary>
/// Pure geometry of a slide drag (SPEC 7.3a, proof p0_c14): the pointer goes from <see cref="Start"/> to
/// <see cref="End"/> in <see cref="Steps"/> equal steps of at most <see cref="HardRules.DragStepPx"/>. The real cursor
/// never moves: each step moves the game window by the opposite amount, so the client point under the resting cursor
/// advances by the step (c14: window +20 px down per step = pointer 20 px up in the client).
/// </summary>
/// <param name="Start">Client point pressed.</param>
/// <param name="Delta">Pointer movement (client pixels).</param>
/// <param name="Steps">Number of steps.</param>
internal sealed record DragPlan(PointI Start, PointI Delta, int Steps)
{
    /// <summary>Client point of the release.</summary>
    public PointI End => Start.Offset(Delta.X, Delta.Y);

    /// <summary>
    /// The plan for a delta, or null when the delta is zero or longer than <see cref="HardRules.MaxDragDistancePx"/> on
    /// either axis (the step count is then at most <see cref="HardRules.MaxDragSteps"/>).
    /// </summary>
    public static DragPlan? Create(PointI start, PointI delta)
    {
        var longest = Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y));
        if (longest == 0 || Math.Abs(delta.X) > HardRules.MaxDragDistancePx || Math.Abs(delta.Y) > HardRules.MaxDragDistancePx)
        {
            return null;
        }

        var steps = (longest + HardRules.DragStepPx - 1) / HardRules.DragStepPx;
        return steps is >= 1 and <= HardRules.MaxDragSteps ? new DragPlan(start, delta, steps) : null;
    }

    /// <summary>Client point under the cursor after step <paramref name="k"/> (0 = the press point, <see cref="Steps"/> = <see cref="End"/>).</summary>
    public PointI PointAt(int k)
    {
        var i = Math.Clamp(k, 0, Steps);
        return Start.Offset((int)((long)Delta.X * i / Steps), (int)((long)Delta.Y * i / Steps));
    }

    /// <summary>Window rect after step <paramref name="k"/>, from the first slid rect: moved opposite to the pointer.</summary>
    public RectI SlidAt(RectI firstSlid, int k)
    {
        var p = PointAt(k);
        return firstSlid.Offset(Start.X - p.X, Start.Y - p.Y);
    }
}
