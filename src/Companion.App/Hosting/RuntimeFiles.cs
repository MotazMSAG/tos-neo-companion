namespace Companion.App.Hosting;

/// <summary>Reads of the host's own runtime files (RuntimeDir only; never install-side files, which the loader reads).</summary>
internal static class RuntimeFiles
{
    /// <summary>The bytes of <paramref name="path"/>, or null when it is missing, a reparse point or unreadable.</summary>
    public static byte[]? ReadIfExists(string path)
    {
        try
        {
            return File.Exists(path) && !SafeFile.IsReparsePoint(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
