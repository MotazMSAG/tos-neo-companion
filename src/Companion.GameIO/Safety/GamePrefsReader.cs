using System.Globalization;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;
using Companion.GameIO.Native;

namespace Companion.GameIO.Safety;

/// <summary>
/// Read-only access to the game's Unity PlayerPrefs (HKCU\Software\TOS\TOS, F13, KS-02). Only values whose names start
/// with a prefix of <see cref="StaticRules.RegistryReadWhitelistPrefixes"/> are read, and the key is never enumerated:
/// Unity stores a pref "Name" under the value "Name_h&lt;djb2-xor hash of Name&gt;", so the exact value names are computed.
/// Values are never written.
/// </summary>
internal static class GamePrefsReader
{
    /// <summary>Unity PlayerPrefs value name: name + "_h" + djb2-xor hash (uint).</summary>
    public static string UnityValueName(string prefName)
    {
        uint h = 5381;
        foreach (var c in prefName)
        {
            h = unchecked((h * 33) ^ c);
        }

        return prefName + "_h" + h.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>True if the value name starts with a whitelisted prefix (ordinal).</summary>
    public static bool IsWhitelisted(string valueName) =>
        StaticRules.RegistryReadWhitelistPrefixes.Any(p => valueName.StartsWith(p, StringComparison.Ordinal));

    /// <summary>Reads the whitelisted prefs. Missing or non-integer values are null.</summary>
    public static GameWindowPrefs Read(IWin32 win32)
    {
        int? Get(string prefName)
        {
            var name = UnityValueName(prefName);
            if (!IsWhitelisted(name))
            {
                throw new HardRuleViolationException("registry.notWhitelisted");
            }

            return ToInt(win32.ReadGameRegistryValue(name));
        }

        return new GameWindowPrefs(
            FullscreenMode: Get("Screenmanager Fullscreen mode"),
            WindowPositionX: Get("Screenmanager Window Position X"),
            WindowPositionY: Get("Screenmanager Window Position Y"),
            SelectMonitor: Get("UnitySelectMonitor"),
            ResolutionWidth: Get("Resolution Window Width") ?? Get("Screenmanager Resolution Width"),
            ResolutionHeight: Get("Resolution Window Height") ?? Get("Screenmanager Resolution Height"),
            LogOpen: Get("neo_log_save_openLog"),
            LogWrite: Get("neo_log_save_openWrite"));
    }

    /// <summary>Unity stores ints as REG_DWORD; tolerate 4/8-byte binaries.</summary>
    public static int? ToInt(object? value) => value switch
    {
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        byte[] { Length: 4 } b => BitConverter.ToInt32(b, 0),
        byte[] { Length: 8 } b8 when BitConverter.ToInt64(b8, 0) is var v and >= int.MinValue and <= int.MaxValue => (int)v,
        _ => null,
    };

    /// <summary>Compares prefs with a baseline; returns null if equal, else a short code naming the first difference.</summary>
    public static string? Difference(GameWindowPrefs baseline, GameWindowPrefs current)
    {
        if (baseline.FullscreenMode != current.FullscreenMode)
        {
            return "prefs.fullscreenMode";
        }

        if (baseline.WindowPositionX != current.WindowPositionX || baseline.WindowPositionY != current.WindowPositionY)
        {
            return "prefs.position";
        }

        if (baseline.SelectMonitor != current.SelectMonitor)
        {
            return "prefs.monitor";
        }

        if (baseline.ResolutionWidth != current.ResolutionWidth || baseline.ResolutionHeight != current.ResolutionHeight)
        {
            return "prefs.resolution";
        }

        return null;
    }
}
