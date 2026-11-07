using Companion.Contracts;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;
using Companion.Knowledge;

namespace Companion.Knowledge.Tests;

/// <summary>Fake clock: Sleep advances instantly and is recorded.</summary>
internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = Fx.T0;

    public TimeSpan Monotonic { get; private set; }

    public int Sleeps { get; private set; }

    public void Sleep(TimeSpan duration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Sleeps++;
        Monotonic += duration;
        UtcNow += duration;
    }
}

/// <summary>A throw-away runtime folder under the test output directory (never outside companion\).</summary>
internal sealed class TestEnv : IDisposable
{
    public TestEnv()
    {
        Root = Path.Combine(AppContext.BaseDirectory, "tmp", Guid.NewGuid().ToString("N"));
        Paths = new CompanionPaths(
            Path.Combine(Root, "install"),
            Path.Combine(Root, "install", "data"),
            Path.Combine(Root, "install", "templates"),
            Path.Combine(Root, "install", "features"),
            Path.Combine(Root, "runtime"),
            Path.Combine(Root, "fakegame"),
            Path.Combine(Root, "fakegame", "Log", "log.txt"),
            Path.Combine(Root, "fakegame", "Fake.exe"));
    }

    public string Root { get; }

    public CompanionPaths Paths { get; }

    public FakeClock Clock { get; } = new();

    public KnowledgeStore NewStore() => new(Paths, Clock);

    public string CharacterFile(CharacterKey key) => Path.Combine(Paths.KnowledgeDir, key.Value, "knowledge.json");

    public string BindingsFile => Path.Combine(Paths.KnowledgeDir, "characters.json");

    public string PinFile => Path.Combine(Paths.KnowledgeDir, "profile.json");

    /// <summary>Pins <paramref name="key"/> as the profile folder, as the 4.33 store writes it.</summary>
    public void WritePin(CharacterKey key)
    {
        Directory.CreateDirectory(Paths.KnowledgeDir);
        File.WriteAllText(PinFile, $$"""{ "schemaVersion": 2, "savedUtc": "2026-01-01T12:00:00+00:00", "profile": "{{key.Value}}" }""");
    }

    /// <summary>Removes the 4.33 profile pin and its backups: the disk as a companion before 4.33 left it.</summary>
    public void RemovePin()
    {
        if (Directory.Exists(Paths.KnowledgeDir))
        {
            foreach (var f in Directory.GetFiles(Paths.KnowledgeDir, "profile.json*"))
            {
                File.Delete(f);
            }
        }
    }

    /// <summary>
    /// Writes characters.json as a companion before 4.33 wrote it (camelCase enum names: "unknown", "readOnly",
    /// "allowed"). Since 4.33 the store only reads it, to find the knowledge the user built.
    /// </summary>
    public void WriteLegacyBindings(params (CharacterKey Key, string Permission)[] bindings)
    {
        var entries = bindings.Select(b => $$"""
                {
                  "character": "{{b.Key.Value}}",
                  "permission": "{{b.Permission}}",
                  "firstSeenUtc": "2026-01-01T12:00:00+00:00"
                }
            """);
        Directory.CreateDirectory(Paths.KnowledgeDir);
        File.WriteAllText(BindingsFile, $$"""
            {
              "schemaVersion": 2,
              "savedUtc": "2026-01-01T12:00:00+00:00",
              "characters": [
            {{string.Join(",\n", entries)}}
              ]
            }
            """);
    }

    public void Dispose()
    {
        // A just-deleted junction can stay "delete pending" for a moment (scanner handles), so retry briefly.
        for (var attempt = 0; attempt < 10 && Directory.Exists(Root); attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}

/// <summary>Clearly fake fixture data (no game data).</summary>
internal static class Fx
{
    public static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static readonly LogFileIdentity File1 = new(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 101, "fake-head-1");

    public static readonly LogFileIdentity File2 = new(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 202, "fake-head-2");

    public static readonly CharacterKey Key1 = new("0123456789abcdef");

    public static readonly CharacterKey Key2 = new("fedcba9876543210");

    /// <summary>The profile of a store with nothing on disk (4.33: one profile for every character).</summary>
    public static readonly CharacterKey DefaultKey = KnowledgeStore.DefaultProfile;

    public static ItemKey I(string name) => ItemKey.FromDisplayName(name);

    public static LogPosition P(long offset, LogFileIdentity? file = null) => new(file ?? File1, offset);

    public static Recipe R(string id, string product, int output, params (string Item, int Qty)[] materials) => new(
        id,
        I(product),
        output,
        "fake-station",
        [.. materials.Select(m => new MaterialRequirement(I(m.Item), m.Qty))],
        [],
        null,
        T0,
        "survey/fake-capture.png",
        UserVerified: true,
        RecipeConfidence.Complete,
        "fake-build",
        null);

    public static StockEntry S(string item, long count, bool stale = false, DateTimeOffset? at = null) =>
        new(I(item), count, null, StockSource.Observed, at ?? T0, P(10), 1.0, "fake.coverage", stale);

    public static StockObservation Obs(string item, long? count, long offset, ValueRange? range = null,
        ObservationKind kind = ObservationKind.CraftViewPair, DateTimeOffset? at = null, LogFileIdentity? file = null, double confidence = 0.9) =>
        new(I(item), count, range, kind, at ?? T0.AddSeconds(offset), P(offset, file), confidence, "fake.coverage");
}
