using System.Text.Json;
using Companion.Contracts;

namespace Companion.GameIO.Geometry;

/// <summary>Content of RuntimeDir\run\slide.json (PITFALL-02): written and flushed before every slide, deleted after a verified slide-back.</summary>
/// <param name="Pid">Game PID.</param>
/// <param name="Hwnd">Game window.</param>
/// <param name="ProcessStartUtc">Game process start time.</param>
/// <param name="Home">Home rect.</param>
/// <param name="Slid">Slid rect (a drag's first slid rect).</param>
/// <param name="Utc">Written at.</param>
/// <param name="PathEnd">A drag's last slid rect (SPEC 7.3a): the window may be anywhere on the straight path from
/// <paramref name="Slid"/> to it; null for a click.</param>
/// <param name="Foreground">A foreground-mode input (SPEC 7.3b): our DOWN/UP may be unread, so recovery drains the game
/// before it moves the window.</param>
internal sealed record SlideMarkerData(
    int Pid, long Hwnd, DateTimeOffset ProcessStartUtc, RectI Home, RectI Slid, DateTimeOffset Utc, RectI? PathEnd = null, bool Foreground = false)
{
    /// <summary>
    /// True if <paramref name="rect"/> is where our slide may have left the window: the slid rect, or (drag) a rect of the
    /// same size whose top-left lies on the segment from the slid rect to <see cref="PathEnd"/> (within 1 px).
    /// </summary>
    public bool IsOurDisplacement(RectI rect)
    {
        if (rect == Slid)
        {
            return true;
        }

        if (PathEnd is not { } end || rect.Width != Slid.Width || rect.Height != Slid.Height)
        {
            return false;
        }

        long dx = end.Left - Slid.Left, dy = end.Top - Slid.Top;
        long px = rect.Left - Slid.Left, py = rect.Top - Slid.Top;
        var len2 = (dx * dx) + (dy * dy);
        var dot = (px * dx) + (py * dy);
        var cross = (px * dy) - (py * dx);
        return len2 > 0 && dot >= 0 && dot <= len2 && cross * cross <= len2;
    }
}

/// <summary>
/// Slide marker file. Writes go to a fresh temp file (FileMode.CreateNew, Flush(true)) in the same folder, then a
/// rename; the folder chain is checked for reparse points before every write (SEC-2). Never written while displaced.
/// </summary>
internal sealed class SlideMarkerStore(string runtimeDir, string runDir, string markerPath)
{
    /// <summary>Marker path.</summary>
    public string Path { get; } = markerPath;

    /// <summary>Writes the marker durably. Returns false (nothing written) if the folder is unsafe or the write failed.</summary>
    public bool TryWrite(SlideMarkerData data)
    {
        string? tmp = null;
        try
        {
            if (IsReparsePoint(runtimeDir))
            {
                return false;
            }

            Directory.CreateDirectory(runDir);
            if (IsReparsePoint(runDir) || (File.Exists(Path) && IsReparsePoint(Path)))
            {
                return false;
            }

            var dto = new SlideMarkerDto(
                data.Pid, data.Hwnd, data.ProcessStartUtc,
                [data.Home.Left, data.Home.Top, data.Home.Right, data.Home.Bottom],
                [data.Slid.Left, data.Slid.Top, data.Slid.Right, data.Slid.Bottom],
                data.Utc,
                data.PathEnd is { } e ? [e.Left, e.Top, e.Right, e.Bottom] : null,
                data.Foreground);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, ContractJson.Options);
            tmp = System.IO.Path.Combine(runDir, "slide.json." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }

            File.Move(tmp, Path, overwrite: true);
            tmp = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
        finally
        {
            if (tmp is not null)
            {
                TryDeleteFile(tmp);
            }
        }
    }

    /// <summary>Reads the marker: (null, false) if absent, (null, true) if present but unreadable.</summary>
    public (SlideMarkerData? Data, bool Corrupt) TryRead()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return (null, false);
            }

            var bytes = File.ReadAllBytes(Path);
            var dto = JsonSerializer.Deserialize<SlideMarkerDto>(bytes, ContractJson.Options);
            if (dto is null || dto.Home is not { Length: 4 } h || dto.Slid is not { Length: 4 } s || dto.Hwnd == 0 || dto.Pid <= 0)
            {
                return (null, true);
            }

            RectI? pathEnd = null;
            if (dto.PathEnd is { } pe)
            {
                if (pe.Length != 4)
                {
                    return (null, true);
                }

                pathEnd = new RectI(pe[0], pe[1], pe[2], pe[3]);
            }

            return (new SlideMarkerData(dto.Pid, dto.Hwnd, dto.ProcessStartUtc, new RectI(h[0], h[1], h[2], h[3]), new RectI(s[0], s[1], s[2], s[3]), dto.Utc, pathEnd, dto.Foreground), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return (null, true);
        }
    }

    /// <summary>True if a marker file exists.</summary>
    public bool Exists()
    {
        try
        {
            return File.Exists(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Deletes the marker (best effort).</summary>
    public bool TryDelete() => TryDeleteFile(Path);

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return false;
        }

        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private sealed record SlideMarkerDto(int Pid, long Hwnd, DateTimeOffset ProcessStartUtc, int[] Home, int[] Slid, DateTimeOffset Utc, int[]? PathEnd = null, bool Foreground = false);
}
