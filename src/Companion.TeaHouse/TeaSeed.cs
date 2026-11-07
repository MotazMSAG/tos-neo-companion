namespace Companion.TeaHouse;

/// <summary>
/// The pictures known from the start: fingerprints (<see cref="Companion.Contracts.Vision.IconPrint.Compute"/>) measured
/// on the captures of 2026-10-29 (tests\Companion.TeaHouse.Tests\TestData	eahouse_t0..t3_full.png, the user's board at
/// score 4475, board squares at the margin <see cref="TeaGeometry.Inset"/>), plus the panel pictures and the empty tray
/// the user named in the helper that evening.
/// </summary>
internal static class TeaSeed
{
    /// <summary>The "Kitty Tea House" title banner.</summary>
    public const string Title = "7f0b0000007fffff212d9535061505034000c4ddffbfed41ffff202000443aff";

    /// <summary>Board, tray and panel pictures with their kinds.</summary>
    public static readonly (string Print, TeaKind Kind, TeaPictureSource Source)[] Pictures =
    [
        ("fffe808098b881ffb1c3abb5858fdccd1c7fc78183c37f1cff80307e7cb880fb", TeaKind.Oven, TeaPictureSource.Tray),
        ("dfddbda7290590dc4ec68eaa96c6de6e247a4641c6e34723dfddb983090490dc", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("ffddb9a7290999dc5ac68eaa9496ce6e14224e41c7e34623ffddb9af090199dc", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("ffddb9afa11999dc58c68eaa94c6ce6e14225e41c7c34623ffddb9af810199dd", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("ffddb9a7290d91dc4ac68eaa94c6ce6a047a4641c7e34623ffddb9af090199dc", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("efdd99af810199dc68c68eaa94d6ce6e14225e40f6fe6622efdd99af810199dd", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("dfddb9a7290191dc4ac69eaa96c6ce66143a4640d6fa6623ffddb9a7090199dc", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("dfddbdbf290190dc4ec69e8e1686de66243e4240c6fb6702dfddbd99090090dd", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("dfddbda7298190dc4ac68eaa96c6de66143e4640c6fb6723dfddbd83090190dc", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("ffef8080f89cebff2d25d4be9c862c2d00007e7f067b0400ffeb8080fc9cebfb", TeaKind.Sponge, TeaPictureSource.Board),
        ("ffb99daa8490dc9b69dcc6b5aacdc6d434467e417b6b6362efb98daa8490dc9f", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("ffb99daa94909d9b4ddcc6b5eaddc694104e72416b636362ffb98da88490dd9f", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("ff999dbe9490959b4cdc86abcbddd6d4165e61416b6b6362ffb99dbc84909d9f", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("ff999daa94909d9b4cdc86b5cbdfc6d4165e62516b6b6262ffb99da884909d9f", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("fff98daa8490dc9b7ddcc695eacdc6d414067e413b6b2360effd81aa8480dc9f", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("fff99daa8490dd9b5cdcc695eaddc694140e32513b6f2260fff98daa8490dd9f", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("ffd99daa9490959b44dcc6b5cbdfc6d4143e32533b6f6260ffd99da8c490959f", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("ff999da89490859b4cdcc6b5cbddc6d4143e22133b6f6260ffd99da884909d9f", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("ffff8080f89ce9ff2d2de4bebc862c2500007e7f06730400ffeb8080fc9ce9fb", TeaKind.Sponge, TeaPictureSource.Board),
        ("dbfb8b8000fcbcb94d8dcddc5a4e8e9c0101c0efff8303c7fbff8b00027cbc39", TeaKind.Tea, TeaPictureSource.Board),
        ("ffffffff8480c1eb2d469352d2d2f025000000007b7f3e14ffffffff8480c0eb", TeaKind.Choco, TeaPictureSource.Board),
        ("fff98daa8490dc9969d4c695aacdc6d434427e4173636362effd81ba8480dc9d", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("ffb99daa8490dd9b4ddcc695eaddc694144672416b6b6362fff98daa8490dd9f", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("ffb99daa94909d9b4cdcc6b5ebdfc6d4104e73416b6b6362fff99da884909d9f", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("ffdd99af810199dc784aceaa94d6ce6e14207e4076de6622efdf91af810199dd", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("ffddb9a7210591dc78c2ceaa9486ce6e143a4e40e6fa6623ffddb9af090199dc", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("dfddbda3210490dc4ec68eaa9686de6e143a4640c6fb6723dfddb983090490dc", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("dfddb9a7290590dc4ac6ceaa9686d66e143a4640c6fb6723ffddb983090490dc", TeaKind.WhiteCat, TeaPictureSource.Board),
        ("ffe3c1c1ffffffff2d71f0f0a5adad2d00003e7e7e7e1c00ffe3c181c3ffffff", TeaKind.Pudding, TeaPictureSource.Board),
        ("dbfb8b8040fcbd99cdadcddc5a4e8e8c0001c0ffff8343e7fbff9b00027cbc39", TeaKind.Tea, TeaPictureSource.Board),
        ("ffffcb8181c1e3ff2d3965d2da40312d00001c3e3e3e1c00fffbefe381c1e3ff", TeaKind.Cookie, TeaPictureSource.Board),
        ("dbfb8b8000fcbc994d8dcddc5a4e8e8e8401e0efff8303c7fbff8b00027cbc39", TeaKind.Tea, TeaPictureSource.Board),
        ("ffcddd811d81d0e5ace6ceda8eceeeaa54fe2626e27ea3c0ffcddd811981dcfd", TeaKind.WhiteCat, TeaPictureSource.Panel),
        ("ab03aa2a2aaaaaebad2cadadadadad6d1c7e7fffff7f7e1c6b83aa2aaaaaabeb", TeaKind.Empty, TeaPictureSource.Tray),
        ("ffff800018b891ffabd28ab28e8ed8a9c07fcf8387077e95ff80307c787c80ea", TeaKind.Oven, TeaPictureSource.Panel),
        ("ffc1c0c01481deddafe2e64d4fb2cba6003e3f3fbe1e0212ffc1c0c048a1fffd", TeaKind.OrangeCat, TeaPictureSource.Panel),
        ("ff7fff43c3c3e7ffaa89b26151eab3ab0000183c3c3c0000fffeffe2c3c3eeff", TeaKind.Cookie, TeaPictureSource.Panel),
        ("fff3838988c0d9fba55cedeeccdae46d00047e777f3c0004fff3838888c1f9fb", TeaKind.OrangeCat, TeaPictureSource.Tray),
        ("ff3f83017b493fbfaa8be55c3d648aab00003c7e043e0100ffb0c2c0fa683caa", TeaKind.Sponge, TeaPictureSource.Panel),
    ];
}
