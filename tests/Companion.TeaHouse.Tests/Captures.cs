using Companion.Contracts.Vision;
using Companion.Vision.Internal;

namespace Companion.TeaHouse.Tests;

/// <summary>
/// The test captures in TestData (the user's board of 2026-10-29 at score 4475, a White Cat current, the Oven in the tray,
/// the cat preview pulsing on row 6 column 2; four frames ~0.2 s apart). Board and panel come from look-only captures;
/// the rest of the chat area is blacked out.
/// </summary>
internal static class Captures
{
    private static readonly Dictionary<string, Frame> Cache = new(StringComparer.Ordinal);

    /// <summary>The board as the user's screenshot shows it (the preview square empty underneath).</summary>
    public const string Board = "vww.../.2ww.w/2tt.24/w.24../w.332t/2.1t1t";

    public static Frame Get(string name)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(name, out var f))
            {
                var path = Path.Combine(AppContext.BaseDirectory, "TestData", name);
                if (!File.Exists(path))
                {
                    Assert.Inconclusive("private capture not included in the public repository: " + name);
                }

                f = SourceImages.ToFrame(File.ReadAllBytes(path), SourceLayout.ClientFull);
                Cache[name] = f;
            }

            return f;
        }
    }

    public static Frame[] All() => [.. Enumerable.Range(0, 4).Select(i => Get($"teahouse_t{i}_full.png"))];
}
