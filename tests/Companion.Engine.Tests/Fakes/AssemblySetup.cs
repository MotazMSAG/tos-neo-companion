namespace Companion.EngineNav.Testing;

/// <summary>Removes the runtime folders earlier test runs created under the test output folder (links are removed, never followed).</summary>
[TestClass]
public static class AssemblySetup
{
    [AssemblyInitialize]
    public static void CleanOldTestRuntime(TestContext _)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-runtime");
        try
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }
        catch
        {
            // leftovers in bin are harmless
        }
    }

    private static void DeleteTree(string dir)
    {
        foreach (var sub in Directory.GetDirectories(dir))
        {
            if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(sub, recursive: false); // removes the junction/link only
            }
            else
            {
                DeleteTree(sub);
            }
        }
        foreach (var f in Directory.GetFiles(dir))
        {
            File.Delete(f);
        }
        Directory.Delete(dir, recursive: false);
    }
}
