using System.Diagnostics;
using System.Globalization;
using System.Text;
using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.GameIO.Diagnostics;
using Companion.Vision;

namespace Companion.CaptureProbe;

/// <summary>Exit codes of the probe.</summary>
internal static class ExitCodes
{
    /// <summary>All captures taken, window verified home.</summary>
    public const int Ok = 0;

    /// <summary>Bad command line.</summary>
    public const int Usage = 1;

    /// <summary>Refused before anything moved.</summary>
    public const int Refused = 2;

    /// <summary>Stopped early while moved; window verified home.</summary>
    public const int Stopped = 3;

    /// <summary>Window NOT verified home: the operator must check it.</summary>
    public const int NotHome = 4;
}

/// <summary>One capture of the probe.</summary>
/// <param name="Phase">"home" or "slid".</param>
/// <param name="Method">"printwindow" (PW_RENDERFULLCONTENT) or "wgc" (Windows.Graphics.Capture).</param>
/// <param name="RequestedMs">Requested delay after the move (0 at home).</param>
/// <param name="ActualMs">Actual time after the move when the capture started (0 at home).</param>
/// <param name="CaptureMs">Duration of the capture.</param>
/// <param name="Client">Client pixels (1920x1009 BGRA), or null when the capture failed.</param>
/// <param name="ClientOrigin">Client origin on screen at capture time.</param>
/// <param name="Status">ok, failed, timeout, noFrame, crop or error.</param>
/// <param name="Note">Extra detail (WGC content size, client offset basis, frame lag).</param>
internal sealed record ProbeCapture(string Phase, string Method, int RequestedMs, double ActualMs, double CaptureMs, byte[]? Client, PointI ClientOrigin, string Status, string Note);

/// <summary>
/// The probe sequence: checks, home captures, the move, captures at each delay under a stop guard, the move back home
/// (always, in finally, verified and repeated if needed), then PNGs and the report. Only read-only window calls, the
/// move-only placement through GameIO's choke point and captures; never a window message.
/// </summary>
internal sealed class ProbeRunner(ProbeOptions options, TextWriter output, CancellationToken ct)
{
    private const string HostMutexName = @"Local\TOS-companion.host";
    private static readonly SizeI Client = new(HardRules.ExpectedClientWidth, HardRules.ExpectedClientHeight);
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StillFor = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinimizedWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExtraDisplacement = TimeSpan.FromMilliseconds(1500);

    private readonly StringBuilder _report = new();

    /// <summary>Runs the probe; returns an <see cref="ExitCodes"/> value.</summary>
    public int Run()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        // Default next to the exe, never the current directory (an elevated prompt starts in System32).
        var outDir = Path.GetFullPath(options.OutDir ?? Path.Combine(AppContext.BaseDirectory, "probe-out", "capture-probe-" + stamp));
        Line($"CaptureProbe {DateTime.Now:yyyy-MM-dd HH:mm:ss}: dx {options.Dx} dy {options.Dy}, delays {string.Join(",", options.DelaysMs)} ms, out {outDir}");
        Line("No clicks, no focus or key messages: the window is only moved behind the window under the cursor and back.");

        if (HostRunning())
        {
            return Refuse("the companion host is running (it watches the game window): stop it first");
        }

        var window = ProbeWindow.Open(CompanionPaths.Default(Environment.CurrentDirectory).GameExePath, out var problem);
        if (window is null)
        {
            return Refuse("game window: " + problem);
        }

        if (window.IsForeground)
        {
            return Refuse("the game is foreground (the user is playing)");
        }

        if (window.IsMinimized)
        {
            return Refuse("the game is minimized");
        }

        if (window.Rect is not { } home || window.ClientOrigin is not { } homeOrigin || window.ClientSize is not { } client)
        {
            return Refuse("the game window state cannot be read");
        }

        if (client != Client)
        {
            return Refuse($"client is {client.Width}x{client.Height}, expected {Client.Width}x{Client.Height}");
        }

        var cursor = WaitStillCursor(window, out var cursorProblem);
        if (cursor is not { } c)
        {
            return Refuse(cursorProblem);
        }

        var top = window.TopWindowAt(c);
        var cover = ProbeGuards.CoverRefusal(c, top, window.Hwnd, window.IsAboveGame(top), window.FrameOf(top));
        if (cover is not null)
        {
            return Refuse(cover);
        }

        var monitors = window.Monitors();
        var slid = home.Offset(options.Dx, options.Dy);
        var homeMonitor = ProbeWindow.LargestOverlap(home, monitors);
        var slidMonitor = ProbeWindow.LargestOverlap(slid, monitors);
        var win32Slid = window.MonitorOf(slid);
        var monitorProblem = ProbeGuards.MonitorRefusal(homeMonitor, slidMonitor, win32Slid, monitors);
        if (monitorProblem is not null)
        {
            return Refuse(monitorProblem);
        }

        foreach (var m in monitors)
        {
            Line($"monitor {m.DeviceName} bounds {R(m.Bounds)} dpi {m.Dpi}{(m.Primary ? " primary" : string.Empty)}");
        }

        Line($"home {R(home)} maximized {window.IsMaximized} client {client.Width}x{client.Height} at {P(homeOrigin)} frame {(window.FrameBounds is { } fb ? R(fb) : "?")} monitor {homeMonitor!.DeviceName}");
        Line($"moved {R(slid)} largest overlap {slidMonitor!.DeviceName} (win32 {win32Slid ?? "?"}), cross-monitor {!string.Equals(slidMonitor.DeviceName, homeMonitor.DeviceName, StringComparison.Ordinal)}");
        Line($"cursor {P(c)} over another window above the game");

        WgcCapture? wgc = null;
        try
        {
            wgc = WgcCapture.Start(window.Hwnd);
            Line($"wgc session: item {wgc.Width}x{wgc.Height}, border {wgc.Border}, cursor capture off");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Line("wgc session failed: " + Describe(ex) + " (printwindow only)");
        }

        var captures = new List<ProbeCapture>();
        string? stopReason = null;
        (bool Home, int Moves, string Note) back = (false, 0, "not run");
        var displaced = TimeSpan.Zero;
        try
        {
            captures.Add(CapturePw(window, "home", 0, 0));
            if (wgc is not null)
            {
                captures.Add(CaptureWgc(wgc, window, "home", 0, 0));
            }

            var sw = new Stopwatch();
            try
            {
                if (ct.IsCancellationRequested)
                {
                    stopReason = "cancelled";
                }
                else if (!captures.Any(x => x.Client is not null))
                {
                    stopReason = "no home capture succeeded, so the window was not moved";
                }
                else if (window.Cursor() != c || window.AnyMouseButtonDown() || window.TopWindowAt(c) != top || window.IsForeground || window.Rect != home)
                {
                    stopReason = "cursor, button, window under the cursor, foreground or window rect changed before the move";
                }
                else if (!window.MoveTo(slid.Left, slid.Top))
                {
                    stopReason = "the move call failed";
                }
                else
                {
                    sw.Start();
                    stopReason = SlidSequence(window, wgc, c, slid, sw, captures);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                stopReason = "exception " + Describe(ex);
            }
            finally
            {
                back = MoveHome(window, home);
                displaced = sw.Elapsed;
            }
        }
        finally
        {
            wgc?.Dispose();
        }

        Line($"moved for {displaced.TotalMilliseconds:0} ms; back home: {(back.Home ? "yes" : "NO")} ({back.Note}, {back.Moves} move(s) back)");
        if (stopReason is not null)
        {
            Line("stopped early: " + stopReason);
        }

        WriteResults(outDir, captures, homeMonitor, slidMonitor);
        if (!back.Home)
        {
            Line($"WARNING: the game window is not verified at its home rect {R(home)}. Check it now.");
            return ExitCodes.NotHome;
        }

        return stopReason is null ? ExitCodes.Ok : ExitCodes.Stopped;
    }

    /// <summary>Checks the window landed, then waits for each delay under the stop guard and captures with both methods.</summary>
    private string? SlidSequence(ProbeWindow window, WgcCapture? wgc, PointI cursor, RectI slid, Stopwatch sw, List<ProbeCapture> captures)
    {
        if (window.Rect is not { } landed || landed != slid)
        {
            return "the window did not land at the moved rect (observed " + (window.Rect is { } o ? R(o) : "?") + ")";
        }

        var deadline = TimeSpan.FromMilliseconds(options.DelaysMs[^1]) + ExtraDisplacement;
        foreach (var delay in options.DelaysMs)
        {
            while (sw.Elapsed.TotalMilliseconds < delay)
            {
                if (ct.IsCancellationRequested)
                {
                    return "cancelled";
                }

                var reason = ProbeGuards.StopReason(Sample(window, cursor), cursor, slid);
                if (reason is not null)
                {
                    return reason;
                }

                Thread.Sleep(2);
            }

            if (sw.Elapsed > deadline)
            {
                return "displacement deadline reached";
            }

            var pw = CapturePw(window, "slid", delay, sw.Elapsed.TotalMilliseconds);
            captures.Add(pw);
            if (pw.Status == "timeout")
            {
                return "printwindow capture timed out";
            }

            if (wgc is not null)
            {
                captures.Add(CaptureWgc(wgc, window, "slid", delay, sw.Elapsed.TotalMilliseconds));
            }

            var after = ProbeGuards.StopReason(Sample(window, cursor), cursor, slid);
            if (after is not null)
            {
                return after;
            }
        }

        return null;
    }

    private static ProbeSample Sample(ProbeWindow w, PointI cursor) =>
        new(w.Exists, w.IsForeground, w.IsMinimized, w.Rect, w.Cursor(), w.AnyMouseButtonDown(), w.TopWindowAt(cursor) == w.Hwnd);

    /// <summary>Moves the window home (move-only), verifies the rect and repeats up to 3 times. Never moves a minimized window.</summary>
    private static (bool Home, int Moves, string Note) MoveHome(ProbeWindow window, RectI home)
    {
        var waited = Stopwatch.StartNew();
        while (window.IsMinimized && waited.Elapsed < MinimizedWait)
        {
            Thread.Sleep(100);
        }

        if (window.IsMinimized)
        {
            return (false, 0, "game minimized while moved: restore it and check its position");
        }

        var moves = 0;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (window.Rect == home)
            {
                return (true, moves, "verified");
            }

            try
            {
                moves++;
                window.MoveTo(home.Left, home.Top);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Try again (verified below); the exception detail does not change what to do.
            }

            Thread.Sleep(50 * attempt);
        }

        return window.Rect == home ? (true, moves, "verified") : (false, moves, "rect " + (window.Rect is { } r ? R(r) : "?"));
    }

    private static ProbeCapture CapturePw(ProbeWindow window, string phase, int requested, double actual)
    {
        var sw = Stopwatch.StartNew();
        if (window.Rect is not { } rect || window.ClientOrigin is not { } origin)
        {
            return new ProbeCapture(phase, "printwindow", requested, actual, 0, null, default, "failed", "window state unreadable");
        }

        var task = Task.Run(() => window.CaptureClient(rect, origin, Client));
        try
        {
            if (!task.Wait(CaptureTimeout))
            {
                return new ProbeCapture(phase, "printwindow", requested, actual, sw.Elapsed.TotalMilliseconds, null, origin, "timeout", string.Empty);
            }

            var bytes = task.Result;
            return new ProbeCapture(phase, "printwindow", requested, actual, sw.Elapsed.TotalMilliseconds, bytes, origin, bytes is null ? "failed" : "ok", string.Empty);
        }
        catch (AggregateException ex)
        {
            return new ProbeCapture(phase, "printwindow", requested, actual, sw.Elapsed.TotalMilliseconds, null, origin, "error", Describe(ex.InnerException ?? ex));
        }
    }

    private static ProbeCapture CaptureWgc(WgcCapture wgc, ProbeWindow window, string phase, int requested, double actual)
    {
        var sw = Stopwatch.StartNew();
        var rect = window.Rect;
        var origin = window.ClientOrigin;
        var frame = window.FrameBounds;
        if (rect is null || origin is null)
        {
            return new ProbeCapture(phase, "wgc", requested, actual, 0, null, default, "failed", "window state unreadable");
        }

        var task = Task.Run(() => wgc.Grab(CaptureTimeout));
        try
        {
            if (!task.Wait(CaptureTimeout + TimeSpan.FromMilliseconds(500)))
            {
                return new ProbeCapture(phase, "wgc", requested, actual, sw.Elapsed.TotalMilliseconds, null, origin.Value, "timeout", string.Empty);
            }

            if (task.Result is not { } img)
            {
                return new ProbeCapture(phase, "wgc", requested, actual, sw.Elapsed.TotalMilliseconds, null, origin.Value, "noFrame", "no new frame within the timeout");
            }

            var (offset, basis) = ProbeMetrics.ClientOffsetInCapture(new SizeI(img.Width, img.Height), rect.Value, frame, origin.Value);
            var pixels = ProbeMetrics.CropClient(img.Bgra, img.Width, img.Height, img.Stride, offset, Client);
            var note = FormattableString.Invariant($"content {img.Width}x{img.Height}, client at ({offset.X},{offset.Y}) by {basis}, frame lag {img.LagMs:0.0} ms{(img.Fresh ? string.Empty : " (frame time not after the request: unverified)")}");
            return new ProbeCapture(phase, "wgc", requested, actual, sw.Elapsed.TotalMilliseconds, pixels, origin.Value, pixels is null ? "crop" : "ok", note);
        }
        catch (AggregateException ex)
        {
            return new ProbeCapture(phase, "wgc", requested, actual, sw.Elapsed.TotalMilliseconds, null, origin.Value, "error", Describe(ex.InnerException ?? ex));
        }
    }

    /// <summary>Metrics per capture, PNGs (chat blacked out, through IVision.EncodePng) and report.txt; after the move back.</summary>
    private void WriteResults(string outDir, List<ProbeCapture> captures, MonitorInfo? homeMonitor, MonitorInfo? slidMonitor)
    {
        var cross = homeMonitor is not null && slidMonitor is not null && !string.Equals(homeMonitor.DeviceName, slidMonitor.DeviceName, StringComparison.Ordinal);
        Line(string.Empty);
        Line("phase method   req_ms  at_ms  cap_ms status  toggleDiff toggleBlack clientBlack homePartBlack otherPartBlack png / note");
        try
        {
            Directory.CreateDirectory(outDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Line("cannot create the output folder: " + Describe(ex));
        }

        var vision = new VisionEngine(new TemplateCatalog());
        var full = new RectI(0, 0, Client.Width, Client.Height);
        foreach (var cap in captures)
        {
            var home = captures.FirstOrDefault(h => h.Phase == "home" && h.Method == cap.Method && h.Client is not null);
            string toggleDiff = "-", toggleBlack = "-", clientBlack = "-", homePart = "-", otherPart = "-", png = "-";
            if (cap.Client is { } px)
            {
                toggleDiff = home?.Client is { } hp ? F(ProbeMetrics.MeanAbsDiff(hp, px, Client.Width, Client.Height, ProbeMetrics.ToggleArea)) : "-";
                toggleBlack = F(ProbeMetrics.PureBlackFraction(px, Client.Width, Client.Height, ProbeMetrics.ToggleArea));
                clientBlack = F(ProbeMetrics.PureBlackFraction(px, Client.Width, Client.Height, full));
                if (homeMonitor is not null && ProbeMetrics.ClientPartOn(homeMonitor.Bounds, cap.ClientOrigin, Client) is { } hpart)
                {
                    homePart = F(ProbeMetrics.PureBlackFraction(px, Client.Width, Client.Height, hpart));
                }

                if (cross && ProbeMetrics.ClientPartOn(slidMonitor!.Bounds, cap.ClientOrigin, Client) is { } opart)
                {
                    otherPart = F(ProbeMetrics.PureBlackFraction(px, Client.Width, Client.Height, opart));
                }

                png = cap.Phase == "home" ? $"{cap.Phase}_{cap.Method}.png" : $"{cap.Phase}_{cap.Method}_{cap.RequestedMs}ms.png";
                try
                {
                    var frame = new Frame(Client.Width, Client.Height, Client.Width * 4, px, TimeSpan.Zero, DateTimeOffset.UtcNow, null, cap.ClientOrigin, cap.Phase == "slid");
                    File.WriteAllBytes(Path.Combine(outDir, png), vision.EncodePng(frame, full, [ProbeMetrics.ChatPanel]));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    png = "(png not written: " + ex.GetType().Name + ")";
                }
            }

            Line(FormattableString.Invariant(
                $"{cap.Phase,-5} {cap.Method,-11} {cap.RequestedMs,5} {cap.ActualMs,6:0} {cap.CaptureMs,6:0} {cap.Status,-7} {toggleDiff,10} {toggleBlack,11} {clientBlack,11} {homePart,13} {otherPart,14} {png} {cap.Note}"));
        }

        Line(string.Empty);
        foreach (var method in new[] { "printwindow", "wgc" })
        {
            var slid = captures.Where(x => x.Phase == "slid" && x.Method == method && x.Client is not null).ToList();
            if (slid.Count == 0)
            {
                Line($"{method}: no slid capture");
                continue;
            }

            var shown = slid.Count(x => ProbeMetrics.PureBlackFraction(x.Client!, Client.Width, Client.Height, ProbeMetrics.ToggleArea) < 0.5);
            Line($"{method}: toggle area drawn (not black) in {shown}/{slid.Count} moved captures");
        }

        Line("toggleDiff = mean |difference| per channel (0..255) vs the home capture of the same method in client (1827,290)-(1883,346);");
        Line("*Black = share of pure-black pixels (B=G=R=0); homePart = client part on the home monitor, otherPart = part on the moved-to monitor.");
        try
        {
            File.WriteAllText(Path.Combine(outDir, "report.txt"), _report.ToString());
            output.WriteLine("report: " + Path.Combine(outDir, "report.txt"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            output.WriteLine("report not written: " + Describe(ex));
        }
    }

    /// <summary>Waits until the cursor has been still for <see cref="StillFor"/> with no button held (up to 10 s).</summary>
    private PointI? WaitStillCursor(ProbeWindow window, out string problem)
    {
        var total = Stopwatch.StartNew();
        var still = Stopwatch.StartNew();
        var last = window.Cursor();
        while (total.Elapsed < TimeSpan.FromSeconds(10) && !ct.IsCancellationRequested)
        {
            Thread.Sleep(50);
            var now = window.Cursor();
            if (now is null)
            {
                problem = "cursor position unavailable (locked or secure desktop?)";
                return null;
            }

            if (now != last || window.AnyMouseButtonDown())
            {
                last = now;
                still.Restart();
                continue;
            }

            if (still.Elapsed >= StillFor)
            {
                problem = string.Empty;
                return now;
            }
        }

        problem = ct.IsCancellationRequested ? "cancelled" : "the mouse did not rest still for 1 s within 10 s";
        return null;
    }

    private static bool HostRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(HostMutexName, out var m))
            {
                m.Dispose();
                return true;
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private int Refuse(string reason)
    {
        Line("REFUSED (nothing moved): " + reason);
        return ExitCodes.Refused;
    }

    private void Line(string text)
    {
        _report.AppendLine(text);
        output.WriteLine(text);
    }

    private static string Describe(Exception ex) =>
        ex.GetType().Name + " 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + " " + ex.Message;

    private static string F(double v) => double.IsNaN(v) ? "-" : v.ToString("0.000", CultureInfo.InvariantCulture);

    private static string R(RectI r) => FormattableString.Invariant($"({r.Left},{r.Top})-({r.Right},{r.Bottom})");

    private static string P(PointI p) => FormattableString.Invariant($"({p.X},{p.Y})");
}
