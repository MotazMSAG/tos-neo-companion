using System.Text.Json;

namespace Companion.TeaHouse;

/// <summary>
/// The learned model as the helper saves it in teahouse\model.json (kind names as keys, so the file reads plainly),
/// loaded the same way by the helper and the trainer (2026-10-31: they loaded it differently, and both let a stored
/// value beat a confirmed one; the Strawberry Cake's 9960, learned before the Oven fix, stayed after the fix because its
/// moves had left the 300-move window and every save wrote it back). Loading starts from <see cref="TeaModel.Default"/>;
/// a stored points value is taken only for a kind whose built-in value is not confirmed (today the Luxury Cake); the
/// confirmed values change only through <see cref="TeaModel.Solve"/> on the moves still kept. Piece counts and moves are
/// taken as stored.
/// </summary>
/// <param name="Version">File version (3 since 2026-10-31; older files load the same way).</param>
/// <param name="JoinPoints">Points for creating each kind by a join.</param>
/// <param name="Measured">Kinds whose join points were measured.</param>
/// <param name="PlacePoints">Points for placing each kind.</param>
/// <param name="Seen">Current pieces counted per kind.</param>
public sealed record TeaModelFile(int Version, Dictionary<string, double> JoinPoints, Dictionary<string, bool> Measured, Dictionary<string, double> PlacePoints, Dictionary<string, long> Seen)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Placing values that were measured.</summary>
    public Dictionary<string, bool>? PlaceMeasured { get; init; }

    /// <summary>The moves the points are solved from.</summary>
    public List<TeaPointsObservation>? Observations { get; init; }

    /// <summary>The file for <paramref name="m"/>.</summary>
    public static TeaModelFile From(TeaModel m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var kinds = Enum.GetValues<TeaKind>();
        return new TeaModelFile(
            3,
            kinds.Where(k => m.JoinPoints[(int)k] != 0).ToDictionary(k => k.ToString(), k => m.JoinPoints[(int)k]),
            kinds.Where(k => m.JoinPointsMeasured[(int)k]).ToDictionary(k => k.ToString(), _ => true),
            kinds.Where(k => m.PlacePoints[(int)k] != 0).ToDictionary(k => k.ToString(), k => m.PlacePoints[(int)k]),
            kinds.Where(k => m.Seen[(int)k] != 0).ToDictionary(k => k.ToString(), k => m.Seen[(int)k]))
        {
            PlaceMeasured = kinds.Where(k => m.PlacePointsMeasured[(int)k]).ToDictionary(k => k.ToString(), _ => true),
            Observations = [.. m.Observations],
        };
    }

    /// <summary>The model this file holds, on top of <see cref="TeaModel.Default"/> (see the class remarks).</summary>
    public TeaModel ToModel()
    {
        var m = TeaModel.Default();
        foreach (var (name, v) in JoinPoints ?? [])
        {
            if (Enum.TryParse<TeaKind>(name, out var k) && (Measured?.ContainsKey(name) ?? false) && !m.JoinPointsMeasured[(int)k])
            {
                m.JoinPoints[(int)k] = v;
                m.JoinPointsMeasured[(int)k] = true;
            }
        }

        foreach (var (name, v) in PlacePoints ?? [])
        {
            if (Enum.TryParse<TeaKind>(name, out var k) && (PlaceMeasured?.ContainsKey(name) ?? false) && !m.PlacePointsMeasured[(int)k])
            {
                m.PlacePoints[(int)k] = v;
                m.PlacePointsMeasured[(int)k] = true;
            }
        }

        foreach (var (name, v) in Seen ?? [])
        {
            if (Enum.TryParse<TeaKind>(name, out var k))
            {
                m.Seen[(int)k] = v;
            }
        }

        m.Observations.AddRange(Observations ?? []);
        if (m.Observations.Count > 0)
        {
            m.Solve();
        }

        return m;
    }

    /// <summary>The file's text.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The model saved in <paramref name="text"/>; <see cref="TeaModel.Default"/> when there is none or it is damaged.</summary>
    public static TeaModel Load(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TeaModel.Default();
        }

        try
        {
            return JsonSerializer.Deserialize<TeaModelFile>(text, Json)?.ToModel() ?? TeaModel.Default();
        }
        catch (JsonException)
        {
            return TeaModel.Default();
        }
    }
}
