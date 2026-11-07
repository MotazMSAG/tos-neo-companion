using System.Text.Json;
using System.Text.Json.Serialization;
using Companion.Contracts.Vision;

namespace Companion.TeaHouse;

/// <summary>Where a picture was seen: a board square or the side panel's "Current" box (drawn at another size).</summary>
public enum TeaPictureSource
{
    /// <summary>A board square.</summary>
    Board,

    /// <summary>The side panel's current piece.</summary>
    Panel,

    /// <summary>The tray square (its piece sits on a plate, drawn larger; an empty tray is a picture too).</summary>
    Tray,
}

/// <summary>
/// The pictures the helper knows: 256-bit <see cref="IconPrint"/> fingerprints of a board square or of the panel's
/// current piece, each with its kind. A fresh picture is the kind of its nearest known picture when that is close
/// enough and clearly closer than any other kind's; otherwise it is unknown and the user names it once in the helper
/// window (or a join names it: three Puddings make a Chocolate Cake). Seeded from the board of 2026-10-29; plain data
/// the host stores as JSON. Not thread-safe.
/// </summary>
public sealed class TeaLabels
{
    /// <summary>
    /// Largest bit distance to a known picture of the same kind. Measured 2026-10-29 with <see cref="IconPrint.Compute"/>:
    /// one still piece within 13 bits of itself, different kinds 51 or more apart; a cat's idle animation moves more, so
    /// each cat keeps several pictures (both ways it faces).
    /// </summary>
    public const int MaxDistance = 28;

    /// <summary>The nearest other kind must be at least this many bits further than the best one.</summary>
    public const int MinMargin = 12;

    /// <summary>Pictures kept per kind and source (the oldest learned ones go first; the seed stays).</summary>
    public const int MaxPerKind = 24;

    /// <summary>Pictures kept per cat kind and source: the cats idle in many poses (Orange Cat poses measured 10-156 bits apart).</summary>
    public const int MaxPerCat = 160;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly List<Entry> _entries = [];

    /// <summary>Every known picture.</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>The kind of <paramref name="print"/>, its distance to the nearest known picture, and the margin to the next kind.</summary>
    public (TeaKind Kind, int Distance, int Margin) Classify(string print, TeaPictureSource source)
    {
        var best = int.MaxValue;
        var bestKind = TeaKind.Unknown;
        Span<int> nearestPerKind = stackalloc int[TeaKinds.Count];
        nearestPerKind.Fill(int.MaxValue);
        foreach (var e in _entries)
        {
            if (e.Source != source)
            {
                continue;
            }

            var d = Distance(print, e.Print);
            if (d < nearestPerKind[(int)e.Kind])
            {
                nearestPerKind[(int)e.Kind] = d;
            }

            if (d < best)
            {
                best = d;
                bestKind = e.Kind;
            }
        }

        var second = int.MaxValue;
        for (var k = 0; k < TeaKinds.Count; k++)
        {
            if (k != (int)bestKind && nearestPerKind[k] < second)
            {
                second = nearestPerKind[k];
            }
        }

        var margin = second == int.MaxValue ? IconPrint.Bits : second - best;
        return best <= MaxDistance && margin >= MinMargin ? (bestKind, best, margin) : (TeaKind.Unknown, best, margin);
    }

    /// <summary>
    /// Adds a picture of a kind (the user named it, or a join proved it). A picture within a few bits of one already known
    /// for that kind adds nothing; a kind keeps at most <see cref="MaxPerKind"/> learned pictures per source. True when added.
    /// </summary>
    public bool Learn(string print, TeaKind kind, TeaPictureSource source, bool seed = false)
    {
        if (kind == TeaKind.Unknown || (kind == TeaKind.Empty && source != TeaPictureSource.Tray) || print.Length != IconPrint.Bits / 4)
        {
            return false;
        }

        // a picture named again as another kind: the user's latest word wins
        _entries.RemoveAll(e => e.Source == source && e.Kind != kind && Distance(e.Print, print) <= 4);
        if (_entries.Any(e => e.Source == source && e.Kind == kind && Distance(e.Print, print) <= 6))
        {
            return false;
        }

        _entries.Add(new Entry(print, kind, source, seed));
        var learned = _entries.Where(e => e.Source == source && e.Kind == kind && !e.Seed).ToList();
        if (learned.Count > (TeaKinds.IsCat(kind) ? MaxPerCat : MaxPerKind))
        {
            _entries.Remove(learned[0]);
        }

        return true;
    }

    /// <summary>The labels as JSON (for the host's file).</summary>
    public string ToJson() => JsonSerializer.Serialize(new FileShape(1, _entries.Where(e => !e.Seed).ToList()), Json);

    /// <summary>Labels from JSON, the seed added; bad or empty text gives the seed only.</summary>
    public static TeaLabels FromJson(string? json)
    {
        var labels = Seeded();
        if (string.IsNullOrWhiteSpace(json))
        {
            return labels;
        }

        try
        {
            var file = JsonSerializer.Deserialize<FileShape>(json, Json);
            foreach (var e in file?.Entries ?? [])
            {
                if (!e.Seed)
                {
                    labels.Learn(e.Print, e.Kind, e.Source);
                }
            }
        }
        catch (JsonException)
        {
            // a damaged file: the seed only (the host keeps the bad file aside)
        }

        return labels;
    }

    /// <summary>The seed: the pictures of the board of 2026-10-29 (<see cref="TeaSeed"/>).</summary>
    public static TeaLabels Seeded()
    {
        var labels = new TeaLabels();
        foreach (var (print, kind, source) in TeaSeed.Pictures)
        {
            labels.Learn(print, kind, source, seed: true);
        }

        return labels;
    }

    /// <summary>Differing bits between two fingerprints (int.MaxValue for a malformed one).</summary>
    public static int Distance(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return int.MaxValue;
        }

        var d = 0;
        for (var i = 0; i < a.Length; i++)
        {
            d += System.Numerics.BitOperations.PopCount((uint)(Nibble(a[i]) ^ Nibble(b[i])));
        }

        return d;
    }

    private static int Nibble(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    /// <summary>One known picture.</summary>
    /// <param name="Print">The fingerprint (64 lower-case hex characters).</param>
    /// <param name="Kind">Its kind.</param>
    /// <param name="Source">Board square or panel.</param>
    /// <param name="Seed">True for a seed picture (never dropped, never saved).</param>
    public sealed record Entry(string Print, TeaKind Kind, TeaPictureSource Source, bool Seed);

    private sealed record FileShape(int Version, IReadOnlyList<Entry> Entries);
}
