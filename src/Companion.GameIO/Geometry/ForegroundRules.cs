using Companion.Contracts;
using Companion.Contracts.Safety;

namespace Companion.GameIO.Geometry;

/// <summary>Why the resting cursor is not a valid foreground-mode spot (SPEC 7.3b).</summary>
internal enum ForegroundReject
{
    /// <summary>Valid.</summary>
    None,
    /// <summary>The window under the cursor is not the game (a topmost window, a toast, a window on another monitor).</summary>
    NotGameUnderCursor,
    /// <summary>Another window at one of the 8 probe points around the cursor.</summary>
    RootMismatch,
    /// <summary>The game's client origin or size is unknown.</summary>
    NoClient,
    /// <summary>The cursor is on the caption or the frame, or closer than the margin to the client edge.</summary>
    CursorNearEdge,
}

/// <summary>
/// Foreground mode (SPEC 7.3b, EVIDENCE c15): the game is the foreground window, so the cover rules (which reject the game
/// itself) do not apply; instead the cursor must rest well inside the game's client area, with the game under the cursor
/// and at the 8 probe points around it (the cover rules' margin and probes, applied to the game). The slide keeps the
/// target under the same cursor point, so the game stays under the cursor for the whole input. Pure: GameIO reads the facts.
/// </summary>
internal static class ForegroundRules
{
    /// <summary>The first failing rule, or <see cref="ForegroundReject.None"/>.</summary>
    /// <param name="game">The game window (the foreground window, checked by the caller).</param>
    /// <param name="top">Root window under the cursor.</param>
    /// <param name="probeRoots">Root windows at <see cref="SlideGeometry.ProbePoints"/>.</param>
    /// <param name="cursor">Resting cursor (screen).</param>
    /// <param name="clientOrigin">The game's client origin (screen) at home.</param>
    /// <param name="clientSize">The game's client size.</param>
    public static ForegroundReject Evaluate(long game, long top, IReadOnlyList<long> probeRoots, PointI cursor, PointI? clientOrigin, SizeI? clientSize)
    {
        if (game == 0 || top != game)
        {
            return ForegroundReject.NotGameUnderCursor;
        }

        if (probeRoots.Count == 0 || probeRoots.Any(r => r != game))
        {
            return ForegroundReject.RootMismatch;
        }

        if (clientOrigin is not { } o || clientSize is not { } s)
        {
            return ForegroundReject.NoClient;
        }

        var m = HardRules.CoverMarginPx;
        var x = cursor.X - o.X;
        var y = cursor.Y - o.Y;
        return x >= m && y >= m && x < s.Width - m && y < s.Height - m ? ForegroundReject.None : ForegroundReject.CursorNearEdge;
    }
}
