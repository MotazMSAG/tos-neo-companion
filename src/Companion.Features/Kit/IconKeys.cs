using Companion.Contracts.Knowledge;

namespace Companion.Features.Kit;

/// <summary>
/// Item keys of items known only by their icon (the Make tab shows material icons, not names): "icon:" + 64 lower-case
/// hex characters, the 256-bit fingerprint the vision-ocr unit's IconFingerprint reader puts in a ReadValue's Text.
/// Two keys whose fingerprints differ in at most <see cref="SameIconMaxDistance"/> bits are the same icon: a fresh
/// reading is resolved to the key already in the store, so one material keeps one key. The item behind an icon is
/// known only once the game logged it in an item tip (the store's icon items, 2026-10-15); no name is ever guessed or
/// taken from game files.
/// </summary>
internal static class IconKeys
{
    /// <summary>Key prefix.</summary>
    public const string Prefix = "icon:";

    /// <summary>Hex characters after the prefix (256 bits).</summary>
    public const int HexLength = 64;

    /// <summary>Largest bit distance between two readings of the same icon (distinct materials measured 90-187 bits apart).</summary>
    public const int SameIconMaxDistance = 24;

    /// <summary>True for a well-formed icon key.</summary>
    public static bool IsIconKey(string? s)
    {
        if (s is null || s.Length != Prefix.Length + HexLength || !s.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = Prefix.Length; i < s.Length; i++)
        {
            if (!char.IsAsciiHexDigitLower(s[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Differing bits between two icon keys (int.MaxValue when either is not one).</summary>
    public static int Distance(string a, string b)
    {
        if (!IsIconKey(a) || !IsIconKey(b))
        {
            return int.MaxValue;
        }

        static int Nibble(char c) => c <= '9' ? c - '0' : c - 'a' + 10;
        var d = 0;
        for (var i = Prefix.Length; i < a.Length; i++)
        {
            d += System.Numerics.BitOperations.PopCount((uint)(Nibble(a[i]) ^ Nibble(b[i])));
        }

        return d;
    }

    /// <summary>
    /// The known key of the same icon (nearest within <see cref="SameIconMaxDistance"/>, ties by ordinal order), or
    /// <paramref name="key"/> itself when the icon is new.
    /// </summary>
    public static string Resolve(string key, IEnumerable<string> known)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var k in known.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            var d = Distance(key, k);
            if (d <= SameIconMaxDistance && d < bestDistance)
            {
                best = k;
                bestDistance = d;
            }
        }

        return best ?? key;
    }

    /// <summary>Icon keys already in a character's store (learned icon items, stock items and recipe materials).</summary>
    public static IReadOnlyList<string> Known(ICharacterKnowledge knowledge)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var icon in knowledge.GetIconItems())
        {
            if (IsIconKey(icon.IconKey))
            {
                keys.Add(icon.IconKey);
            }
        }

        foreach (var item in knowledge.GetStock().Items.Keys)
        {
            if (IsIconKey(item.Value))
            {
                keys.Add(item.Value);
            }
        }

        foreach (var r in knowledge.GetRecipes())
        {
            foreach (var m in r.Materials)
            {
                if (IsIconKey(m.Item.Value))
                {
                    keys.Add(m.Item.Value);
                }
            }
        }

        return [.. keys];
    }
}
