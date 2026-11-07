using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;

namespace Companion.GameIO.Geometry;

/// <summary>
/// Pure slide geometry (SPEC 7.3, 7.5, 7.6). No Win32 calls: every input is passed in, so the rules are unit-tested on
/// synthetic layouts (e.g. two 1920x1080 monitors, the primary at (0,0) and a second at (-1920,0)).
/// </summary>
internal static class SlideGeometry
{
    /// <summary>
    /// Window position that puts client point <paramref name="target"/> under the resting cursor:
    /// (C.x - T.x - (O.x - H.left), C.y - T.y - (O.y - H.top)). c3 run 3: C=(918,672), T=(1748,314), offset (8,31)
    /// gives (-838,327).
    /// </summary>
    public static PointI SlidPosition(RectI home, PointI clientOrigin, PointI cursor, PointI target) =>
        new(cursor.X - target.X - (clientOrigin.X - home.Left), cursor.Y - target.Y - (clientOrigin.Y - home.Top));

    /// <summary>True if the rect is exactly at (x, y) with the given size (p0_c3 Test-Rect).</summary>
    public static bool IsAt(RectI rect, int x, int y, SizeI size) =>
        rect.Left == x && rect.Top == y && rect.Width == size.Width && rect.Height == size.Height;

    /// <summary>True if both rects are identical (position and size).</summary>
    public static bool SameRect(RectI a, RectI b) => a == b;

    /// <summary>True if the sizes differ (a self-resize, not just a move).</summary>
    public static bool SizeChanged(RectI expected, RectI observed) =>
        expected.Width != observed.Width || expected.Height != observed.Height;

    /// <summary>Live maximized rect of a window on a monitor: the work area grown by the hidden frame.</summary>
    public static RectI ExpectedMaximizedRect(RectI workArea, int frameThickness) => workArea.Inflate(frameThickness);

    /// <summary>The monitor with the largest overlap (MonitorFromRect NEAREST semantics), else the nearest one.</summary>
    public static MonitorInfo? NearestMonitor(RectI rect, IReadOnlyList<MonitorInfo> monitors)
    {
        MonitorInfo? best = null;
        long bestArea = 0;
        foreach (var m in monitors)
        {
            var a = m.Bounds.Intersect(rect)?.Area ?? 0;
            if (a > bestArea)
            {
                bestArea = a;
                best = m;
            }
        }

        if (best is not null)
        {
            return best;
        }

        long bestDist = long.MaxValue;
        foreach (var m in monitors)
        {
            var dx = Math.Max(0, Math.Max(m.Bounds.Left - rect.Right, rect.Left - m.Bounds.Right));
            var dy = Math.Max(0, Math.Max(m.Bounds.Top - rect.Bottom, rect.Top - m.Bounds.Bottom));
            var d = ((long)dx * dx) + ((long)dy * dy);
            if (d < bestDist)
            {
                bestDist = d;
                best = m;
            }
        }

        return best;
    }

    /// <summary>The monitor containing the point, or null.</summary>
    public static MonitorInfo? MonitorAt(PointI p, IReadOnlyList<MonitorInfo> monitors) =>
        monitors.FirstOrDefault(m => m.Bounds.Contains(p));

    /// <summary>
    /// Exposed area = (slid ∩ ∪monitors) − ∪covers, in square pixels (coordinate compression; exact for rect unions).
    /// </summary>
    public static long ExposedArea(RectI slid, IReadOnlyList<RectI> monitors, IReadOnlyList<RectI> covers)
    {
        var visible = monitors.Select(m => m.Intersect(slid)).Where(r => r is not null).Select(r => r!.Value).ToList();
        if (visible.Count == 0)
        {
            return 0;
        }

        var cov = covers.Select(c => c.Intersect(slid)).Where(r => r is not null).Select(r => r!.Value).ToList();
        var xs = new SortedSet<int>();
        var ys = new SortedSet<int>();
        foreach (var r in visible.Concat(cov))
        {
            xs.Add(r.Left);
            xs.Add(r.Right);
            ys.Add(r.Top);
            ys.Add(r.Bottom);
        }

        var xa = xs.ToArray();
        var ya = ys.ToArray();
        long area = 0;
        for (var i = 0; i + 1 < xa.Length; i++)
        {
            for (var j = 0; j + 1 < ya.Length; j++)
            {
                var cell = new RectI(xa[i], ya[j], xa[i + 1], ya[j + 1]);
                if (cell.IsEmpty)
                {
                    continue;
                }

                var probe = new PointI(cell.Left, cell.Top);
                if (!visible.Any(v => v.Contains(probe)))
                {
                    continue;
                }

                if (cov.Any(c => c.Contains(probe)))
                {
                    continue;
                }

                area += cell.Area;
            }
        }

        return area;
    }

    /// <summary>Builds the slide plan for a target (all inputs sampled by the caller just before the slide).</summary>
    public static SlidePlan BuildPlan(
        RectI home,
        PointI clientOrigin,
        PointI cursor,
        PointI target,
        IReadOnlyList<MonitorInfo> monitors,
        MonitorInfo homeMonitor,
        IReadOnlyList<RectI> coversAboveGame)
    {
        var pos = SlidPosition(home, clientOrigin, cursor, target);
        var dx = pos.X - home.Left;
        var dy = pos.Y - home.Top;
        var slid = home.Offset(dx, dy);
        var slidMonitor = NearestMonitor(slid, monitors);
        var centerInside = homeMonitor.Bounds.Contains(slid.Center);
        var exposed = ExposedArea(slid, monitors.Select(m => m.Bounds).ToList(), coversAboveGame);
        return new SlidePlan(home, slid, dx, dy, homeMonitor.DeviceName, slidMonitor?.DeviceName ?? string.Empty, centerInside, exposed);
    }

    /// <summary>
    /// Checks a plan (SPEC 7.5): distance cap; MonitorFromRect(slid) (pure largest-overlap and, when given, Win32) is the
    /// home monitor or another monitor at 96 DPI; 96 DPI on the home and cursor monitors. Exposed area is journaled, not
    /// refused.
    /// </summary>
    /// <param name="plan">The slide plan.</param>
    /// <param name="homeMonitor">The game's home monitor.</param>
    /// <param name="cursorMonitor">The monitor under the cursor, or null.</param>
    /// <param name="win32SlidMonitor">MonitorFromRect(slid, NEAREST) device from Win32, or null when not sampled.</param>
    /// <param name="monitors">All monitors (EnumDisplayMonitors, re-read before the slide), to look up the slid monitor's DPI.</param>
    public static NotReadyReason CheckPlan(
        SlidePlan plan, MonitorInfo homeMonitor, MonitorInfo? cursorMonitor, string? win32SlidMonitor, IReadOnlyList<MonitorInfo> monitors)
    {
        if (Math.Abs(plan.Dx) > HardRules.MaxSlideDistancePx || Math.Abs(plan.Dy) > HardRules.MaxSlideDistancePx)
        {
            return NotReadyReason.SlideTooFar;
        }

        // A slid window may leave the home monitor's top or bottom edge where no monitor exists: its centre can then be
        // off screen while MonitorFromRect still names the home monitor (the old centre rule made every lower-half target
        // unreachable, live run 2026-10-14). Its largest overlap may also land on another monitor: c12 showed the game keeps
        // its size and maximized state and only re-targets its frame rate to that monitor's refresh rate (self-reverting),
        // which the user accepted on 2026-10-14. Only a DPI change would make Windows resize the game, so the other monitor
        // must be at the expected DPI.
        if (!SlidMonitorAllowed(plan.SlidMonitor, homeMonitor, monitors)
            || (win32SlidMonitor is not null && !SlidMonitorAllowed(win32SlidMonitor, homeMonitor, monitors)))
        {
            return NotReadyReason.MonitorRule;
        }

        if (homeMonitor.Dpi != HardRules.ExpectedDpi || cursorMonitor is null || cursorMonitor.Dpi != HardRules.ExpectedDpi)
        {
            return NotReadyReason.DpiUnsupported;
        }

        // Exposed area elsewhere (e.g. on the other monitor) is allowed: the user's rule is that the game never shows
        // under the cursor, which the cover probes around the cursor enforce. A zero-exposure rule made every useful
        // slide impossible on the user's layout (live run 2026-10-14). The exposed area is still journaled per click.
        return NotReadyReason.None;
    }

    // The home monitor, or a known monitor at the expected DPI (an unknown device name is refused).
    private static bool SlidMonitorAllowed(string device, MonitorInfo homeMonitor, IReadOnlyList<MonitorInfo> monitors) =>
        string.Equals(device, homeMonitor.DeviceName, StringComparison.Ordinal)
        || monitors.FirstOrDefault(m => string.Equals(m.DeviceName, device, StringComparison.Ordinal)) is { Dpi: HardRules.ExpectedDpi };

    /// <summary>The 8 probe points ±<see cref="HardRules.CoverProbeOffsetPx"/> around the cursor (PITFALL-11).</summary>
    public static IReadOnlyList<PointI> ProbePoints(PointI cursor)
    {
        var d = HardRules.CoverProbeOffsetPx;
        return
        [
            cursor.Offset(-d, -d), cursor.Offset(0, -d), cursor.Offset(d, -d),
            cursor.Offset(-d, 0), cursor.Offset(d, 0),
            cursor.Offset(-d, d), cursor.Offset(0, d), cursor.Offset(d, d),
        ];
    }

    /// <summary>Packs a client point into a mouse lParam after checking it lies inside the client.</summary>
    public static long MouseLParamChecked(PointI client, SizeI clientSize)
    {
        if (client.X < 0 || client.Y < 0 || client.X >= clientSize.Width || client.Y >= clientSize.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(client), "client point outside the client area");
        }

        return HardRules.MouseLParam(client);
    }
}
