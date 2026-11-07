using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;

namespace Companion.GameIO.Geometry;

/// <summary>Facts about the game window used to validate home (SPEC 7.6).</summary>
/// <param name="Rect">GetWindowRect.</param>
/// <param name="Zoomed">IsZoomed.</param>
/// <param name="Client">GetClientRect size.</param>
/// <param name="HasCaption">WS_CAPTION.</param>
/// <param name="LoggedResolution">Last '[graphic] Current screen resolution' (null = unknown).</param>
/// <param name="ExpectedMaximized">Live maximized rect of the window's monitor (work area + frame).</param>
/// <param name="StableFor">How long rect and zoom have been unchanged (sampler).</param>
internal sealed record HomeFacts(RectI Rect, bool Zoomed, SizeI Client, bool HasCaption, SizeI? LoggedResolution, RectI ExpectedMaximized, TimeSpan StableFor);

/// <summary>Pure home validation and restore-ladder planning.</summary>
internal static class HomeRules
{
    /// <summary>Minimum time the home rect must have been unchanged.</summary>
    public static readonly TimeSpan HomeStableFor = TimeSpan.FromSeconds(1);

    /// <summary>The expected client size (1920x1009).</summary>
    public static readonly SizeI ExpectedClient = new(HardRules.ExpectedClientWidth, HardRules.ExpectedClientHeight);

    /// <summary>
    /// Validates home: unchanged ≥ 1 s, IsZoomed, client 1920x1009 matching the last logged resolution, equal to the
    /// live maximized rect of its monitor, WS_CAPTION. Returns null when valid, else a short code.
    /// </summary>
    public static string? Validate(HomeFacts f)
    {
        if (!f.Zoomed)
        {
            return "home.notZoomed";
        }

        if (f.Client != ExpectedClient)
        {
            return "home.clientSize";
        }

        if (!f.HasCaption)
        {
            return "home.noCaption";
        }

        if (f.LoggedResolution is null)
        {
            return "home.resolutionUnknown";
        }

        if (f.LoggedResolution.Value != ExpectedClient)
        {
            return "home.resolutionLog";
        }

        if (f.Rect != f.ExpectedMaximized)
        {
            return "home.notMaximizedRect";
        }

        if (f.StableFor < HomeStableFor)
        {
            return "home.notStable";
        }

        return null;
    }

    /// <summary>
    /// Restore ladder (SPEC 7.6): no rung while iconic; never onto a vanished home monitor; move-only first; move+size
    /// only if the size changed during our displacement and at most once; then one posted WM_SIZE(SIZE_MAXIMIZED).
    /// </summary>
    public static IReadOnlyList<RestoreRung> PlanLadder(bool iconic, bool homeMonitorPresent, bool sizeChangedDuringOurs, bool moveSizeAlreadyUsed)
    {
        if (iconic || !homeMonitorPresent)
        {
            return [];
        }

        var rungs = new List<RestoreRung> { RestoreRung.MoveOnly };
        if (sizeChangedDuringOurs && !moveSizeAlreadyUsed)
        {
            rungs.Add(RestoreRung.MoveSize);
        }

        rungs.Add(RestoreRung.PostedWmSize);
        return rungs;
    }

    /// <summary>Incident kind for a deviation seen during our displacement.</summary>
    public static IncidentKind DeviationKind(RectI expected, RectI observed, bool expectedZoomed, bool observedZoomed)
    {
        if (SlideGeometry.SizeChanged(expected, observed))
        {
            return IncidentKind.SelfResize;
        }

        if (expectedZoomed && !observedZoomed)
        {
            return IncidentKind.MaximizedLost;
        }

        return IncidentKind.NotHomeAfterInput;
    }
}

/// <summary>What crash recovery does with a slide marker (SPEC 7.6).</summary>
internal enum MarkerDecision
{
    /// <summary>No marker: nothing to do.</summary>
    NoMarker,
    /// <summary>PID/HWND/start time differ (or the window is gone): delete the marker only.</summary>
    DeleteIdentityDiffers,
    /// <summary>Window already at the marker's home: delete the marker only.</summary>
    DeleteAtHome,
    /// <summary>Window at the marker's slid rect (or on a drag's path): move-only to home, verify, journal LeftoverFromCrash, delete the marker.</summary>
    RestoreMoveOnly,
    /// <summary>Rect matches neither: notify and leave the window alone (marker kept).</summary>
    LeaveAloneNeither,
    /// <summary>Window minimized: no rung runs while iconic (marker kept).</summary>
    WaitIconic,
    /// <summary>Marker unreadable: leave everything (marker kept, reported).</summary>
    MarkerCorrupt,
}

/// <summary>Pure crash-recovery decision.</summary>
internal static class MarkerRecovery
{
    /// <summary>Decides what to do with a marker given the live window facts.</summary>
    /// <param name="marker">Marker (null = none).</param>
    /// <param name="corrupt">Marker file present but unreadable.</param>
    /// <param name="windowExists">IsWindow(marker hwnd).</param>
    /// <param name="livePid">PID owning the marker hwnd.</param>
    /// <param name="liveStartUtc">Start time of that PID.</param>
    /// <param name="liveClassIsGame">Class is UnityWndClass.</param>
    /// <param name="liveRect">Current rect.</param>
    /// <param name="iconic">IsIconic.</param>
    public static MarkerDecision Decide(
        SlideMarkerData? marker,
        bool corrupt,
        bool windowExists,
        int livePid,
        DateTimeOffset? liveStartUtc,
        bool liveClassIsGame,
        RectI? liveRect,
        bool iconic)
    {
        if (corrupt)
        {
            return MarkerDecision.MarkerCorrupt;
        }

        if (marker is null)
        {
            return MarkerDecision.NoMarker;
        }

        var sameStart = liveStartUtc is { } s && Math.Abs((s - marker.ProcessStartUtc).TotalMilliseconds) < 1;
        if (!windowExists || livePid != marker.Pid || !sameStart || !liveClassIsGame || liveRect is null)
        {
            return MarkerDecision.DeleteIdentityDiffers;
        }

        if (liveRect.Value == marker.Home)
        {
            return MarkerDecision.DeleteAtHome;
        }

        if (iconic)
        {
            return MarkerDecision.WaitIconic;
        }

        if (marker.IsOurDisplacement(liveRect.Value))
        {
            return MarkerDecision.RestoreMoveOnly;
        }

        return MarkerDecision.LeaveAloneNeither;
    }
}
