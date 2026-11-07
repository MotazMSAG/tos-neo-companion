namespace Companion.TeaHouse;

/// <summary>
/// What a square of the Kitty Tea House board holds, or what the current piece is (the user's rules, 2026-10-29):
/// <list type="bullet">
/// <item>the cake ladder: three touching pieces of one level join into the next, Cookie up to Luxury Cake (the top);</item>
/// <item>the tea ladder: a boxed-in cat turns into Catscuit Tea; three teas join into a Sundae, three Sundaes into a Kitty
/// Tea House (the top);</item>
/// <item>Black Tea: a blocker from an Oven that joined nothing; never joins;</item>
/// <item>cats: a White Cat walks one square up, down, left or right per turn, an Orange Cat jumps to any empty square;</item>
/// <item>the Oven (counts as whichever piece makes a join) and the Cutlery (removes any piece; a cat becomes a tea) are only
/// ever the current piece or the tray's.</item>
/// </list>
/// </summary>
public enum TeaKind : byte
{
    /// <summary>An empty square.</summary>
    Empty = 0,

    /// <summary>Cake level 1.</summary>
    Cookie = 1,

    /// <summary>Cake level 2.</summary>
    Sponge = 2,

    /// <summary>Cake level 3.</summary>
    Pudding = 3,

    /// <summary>Cake level 4.</summary>
    Choco = 4,

    /// <summary>Cake level 5.</summary>
    Cupcake = 5,

    /// <summary>Cake level 6.</summary>
    Shortcake = 6,

    /// <summary>Cake level 7.</summary>
    WholeCake = 7,

    /// <summary>Cake level 8, the top: never joins.</summary>
    Luxury = 8,

    /// <summary>Tea level 1 (a boxed-in cat).</summary>
    Tea = 9,

    /// <summary>Tea level 2.</summary>
    Sundae = 10,

    /// <summary>Tea level 3, the top: never joins.</summary>
    TeaHouse = 11,

    /// <summary>The blocker an Oven leaves when it joins nothing; never joins.</summary>
    BlackTea = 12,

    /// <summary>Walks one square (up, down, left, right) to an empty square each turn.</summary>
    WhiteCat = 13,

    /// <summary>Jumps to any empty square each turn.</summary>
    OrangeCat = 14,

    /// <summary>Wild piece: becomes the piece that makes the best join, else Black Tea.</summary>
    Oven = 15,

    /// <summary>Removes any piece; a cat it meets becomes a tea.</summary>
    Cutlery = 16,

    /// <summary>A picture the reader does not know yet (treated as a blocker that never joins).</summary>
    Unknown = 17,
}

/// <summary>The ladders and names of <see cref="TeaKind"/>.</summary>
public static class TeaKinds
{
    /// <summary>Number of kinds (array size for per-kind tables).</summary>
    public const int Count = 18;

    /// <summary>The kind three of these join into, or <see cref="TeaKind.Empty"/> when it never joins.</summary>
    public static TeaKind Next(TeaKind k) => k switch
    {
        >= TeaKind.Cookie and <= TeaKind.WholeCake => k + 1,
        TeaKind.Tea => TeaKind.Sundae,
        TeaKind.Sundae => TeaKind.TeaHouse,
        _ => TeaKind.Empty,
    };

    /// <summary>True for a kind three of which join (cakes below the top, Tea and Sundae).</summary>
    public static bool Joins(TeaKind k) => Next(k) != TeaKind.Empty;

    /// <summary>True for the two cats.</summary>
    public static bool IsCat(TeaKind k) => k is TeaKind.WhiteCat or TeaKind.OrangeCat;

    /// <summary>The level on its ladder (cakes 1..8, teas 1..3), 0 for the rest.</summary>
    public static int Level(TeaKind k) => k switch
    {
        >= TeaKind.Cookie and <= TeaKind.Luxury => (int)k,
        >= TeaKind.Tea and <= TeaKind.TeaHouse => k - TeaKind.Tea + 1,
        _ => 0,
    };

    /// <summary>The name the game uses (for the helper window).</summary>
    public static string Name(TeaKind k) => k switch
    {
        TeaKind.Empty => "empty",
        TeaKind.Cookie => "Cookie",
        TeaKind.Sponge => "Sponge Cake",
        TeaKind.Pudding => "Pudding",
        TeaKind.Choco => "Chocolate Cake",
        TeaKind.Cupcake => "Cupcake",
        TeaKind.Shortcake => "Shortcake Slice",
        TeaKind.WholeCake => "Strawberry Cake",
        TeaKind.Luxury => "Luxury Cake",
        TeaKind.Tea => "Catscuit Tea",
        TeaKind.Sundae => "Sundae",
        TeaKind.TeaHouse => "Kitty Tea House",
        TeaKind.BlackTea => "Black Tea",
        TeaKind.WhiteCat => "White Cat",
        TeaKind.OrangeCat => "Orange Cat",
        TeaKind.Oven => "Oven",
        TeaKind.Cutlery => "Cutlery",
        _ => "unknown piece",
    };

    /// <summary>One character per kind for the journal and the tests ('.' empty, '1'..'8' cakes, t s h teas, b w o v x ?).</summary>
    public static char Code(TeaKind k) => k switch
    {
        TeaKind.Empty => '.',
        >= TeaKind.Cookie and <= TeaKind.Luxury => (char)('0' + (int)k),
        TeaKind.Tea => 't',
        TeaKind.Sundae => 's',
        TeaKind.TeaHouse => 'h',
        TeaKind.BlackTea => 'b',
        TeaKind.WhiteCat => 'w',
        TeaKind.OrangeCat => 'o',
        TeaKind.Oven => 'v',
        TeaKind.Cutlery => 'x',
        _ => '?',
    };

    /// <summary>The kind of a <see cref="Code"/> character; false for an unknown character.</summary>
    public static bool TryParse(char c, out TeaKind kind)
    {
        kind = c switch
        {
            '.' => TeaKind.Empty,
            >= '1' and <= '8' => (TeaKind)(c - '0'),
            't' => TeaKind.Tea,
            's' => TeaKind.Sundae,
            'h' => TeaKind.TeaHouse,
            'b' => TeaKind.BlackTea,
            'w' => TeaKind.WhiteCat,
            'o' => TeaKind.OrangeCat,
            'v' => TeaKind.Oven,
            'x' => TeaKind.Cutlery,
            '?' => TeaKind.Unknown,
            _ => (TeaKind)255,
        };
        return (byte)kind != 255;
    }

    /// <summary>The kinds the game hands out as the current piece (the pieces the odds table covers).</summary>
    public static readonly TeaKind[] Spawnable =
    [
        TeaKind.Cookie, TeaKind.Sponge, TeaKind.Pudding, TeaKind.Choco, TeaKind.WhiteCat, TeaKind.OrangeCat,
        TeaKind.Oven, TeaKind.Cutlery,
    ];
}
