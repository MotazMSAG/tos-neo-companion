using Companion.Contracts;
using Companion.Contracts.Vision;
using Companion.GameIO.Geometry;

namespace Companion.GameIO.Tests.Fakes;

/// <summary>
/// The recipe list scene of the live run 2026-10-14 (ScanRecipeList, job 35e59bca): the cursor rests at (1327,477); the
/// list anchor (the "Make" title) is at (485,123); the 2nd-row card 6 is pressed at (364,669), which needs the slide
/// dx 963, dy -215 (slid window (955,-223), slid client origin (963,-192)), so the anchor ends up above the top of every
/// monitor. Captures render a seeded client-coordinate texture where the client lies on the monitor with the window's
/// largest overlap and black elsewhere (c13); the properties script what the slid capture shows.
/// </summary>
internal sealed class CardScene
{
    public const string AnchorId = "craft.list.anchor";
    public static readonly PointI Cursor = new(1327, 477);
    public static readonly PointI Anchor = new(485, 123);
    public static readonly PointI Card6 = new(364, 669);

    /// <summary>A first-row card: the slide (dy -74) keeps the anchor on screen.</summary>
    public static readonly PointI Card1 = new(364, 528);

    /// <summary>The list scan's scroll drag start (anchor + (-65,737)): slide dx 907, dy -406.</summary>
    public static readonly PointI ScrollStart = new(420, 860);

    /// <summary>The slid capture shows the content moved left by this many pixels (the list moved).</summary>
    public int SlidShiftX { get; set; }

    /// <summary>The slid capture is dimmed by this factor (an overlay).</summary>
    public double SlidGain { get; set; } = 1.0;

    /// <summary>Client rect rendered black in the slid capture.</summary>
    public RectI? SlidBlack { get; set; }

    /// <summary>PrintWindow draws the client parts off the drawn monitor too (it does not, c13).</summary>
    public bool DrawEverything { get; set; }

    /// <summary>The scene on a rig: cursor set before the connect (stillness), captures rendered, calls cleared.</summary>
    /// <param name="cursor">Resting cursor (default <see cref="Cursor"/>).</param>
    /// <param name="slidAnchorFails">The slid template check fails (the anchor is not visible while slid).</param>
    public static (Rig Rig, CardScene Scene) Create(PointI? cursor = null, bool slidAnchorFails = true)
    {
        var rig = new Rig();
        var scene = new CardScene();
        rig.Win32.Cursor = cursor ?? Cursor;
        rig.Win32.PrintPixels = (rect, origin, size) => scene.Render(rig, rect, origin, size);
        if (slidAnchorFails)
        {
            rig.Vision.VerifyHook = (f, id, c) => f.WhileSlid ? Miss(id, c) : null;
        }

        rig.ConnectAndSettle();
        rig.Win32.ClearCalls();
        return (rig, scene);
    }

    /// <summary>GameIO's own capture at home (the located frame), with the capture call cleared from the acting list.</summary>
    public static Frame Locate(Rig rig)
    {
        var cap = rig.Io.Capture(CancellationToken.None);
        Assert.IsTrue(cap.Ok, cap.Problem.ToString());
        rig.Win32.ClearCalls();
        return cap.Frame!;
    }

    /// <summary>A failed template check.</summary>
    public static MatchResult Miss(string id, PointI center, MatchFailure failure = MatchFailure.BelowThreshold) =>
        new(id, false, center, 0.12, 0.05, 0, failure);

    /// <summary>The seeded texture at client (x, y), channel 0-2: never near black, about 60 levels of deviation.</summary>
    public static byte Texel(int x, int y, int channel)
    {
        unchecked
        {
            var h = ((uint)x * 0x9E3779B1u) ^ ((uint)y * 0x85EBCA77u) ^ ((uint)(channel + 1) * 0xC2B2AE3Du);
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            h *= 0x297A2D39u;
            h ^= h >> 15;
            return (byte)(32 + (h % 208));
        }
    }

    /// <summary>A full client image of the texture (every pixel drawn), as GameIO's home capture shows it.</summary>
    public static byte[] FullTexture(int shiftX = 0)
    {
        var px = new byte[1920 * 1009 * 4];
        for (var y = 0; y < 1009; y++)
        {
            for (var x = 0; x < 1920; x++)
            {
                var i = ((y * 1920) + x) * 4;
                px[i] = Texel(x + shiftX, y, 0);
                px[i + 1] = Texel(x + shiftX, y, 1);
                px[i + 2] = Texel(x + shiftX, y, 2);
                px[i + 3] = 255;
            }
        }

        return px;
    }

    /// <summary>PrintWindow as c13 saw it: only the client part on the window's largest-overlap monitor is drawn.</summary>
    public byte[] Render(Rig rig, RectI windowRect, PointI origin, SizeI size)
    {
        var slid = windowRect != Rig.HomeRect;
        var drawn = SlideGeometry.NearestMonitor(windowRect, rig.Win32.Monitors)!.Bounds;
        var gain = slid ? SlidGain : 1.0;
        var shift = slid ? SlidShiftX : 0;
        var px = new byte[size.Width * size.Height * 4];
        for (var y = 0; y < size.Height; y++)
        {
            for (var x = 0; x < size.Width; x++)
            {
                var i = ((y * size.Width) + x) * 4;
                px[i + 3] = 255;
                if (!DrawEverything && !drawn.Contains(new PointI(origin.X + x, origin.Y + y)))
                {
                    continue;
                }

                if (slid && SlidBlack is { } black && black.Contains(new PointI(x, y)))
                {
                    continue;
                }

                for (var c = 0; c < 3; c++)
                {
                    px[i + c] = (byte)(Texel(x + shift, y, c) * gain);
                }
            }
        }

        return px;
    }
}
