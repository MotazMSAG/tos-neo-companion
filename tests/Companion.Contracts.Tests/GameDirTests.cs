using Companion.Contracts;

namespace Companion.Contracts.Tests;

[TestClass]
public sealed class GameDirTests
{
    [TestMethod]
    public void NoSetting_UsesTheDefaultFolder()
    {
        Assert.AreEqual(CompanionPaths.DefaultGameDir, CompanionPaths.ResolveGameDir(null));
        Assert.AreEqual(CompanionPaths.DefaultGameDir, CompanionPaths.ResolveGameDir("  "));
    }

    [TestMethod]
    public void AFolderWithTheGame_IsUsed_AndTheLogAndExeFollowIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tosdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "TOS.exe"), "x");
            var resolved = CompanionPaths.ResolveGameDir("\"" + dir + "\\\"");
            Assert.AreEqual(Path.GetFullPath(dir), resolved);

            var p = CompanionPaths.ForGame(@"C:\app", resolved);
            Assert.AreEqual(Path.Combine(resolved, "Log", "log.txt"), p.GameLogPath);
            Assert.AreEqual(Path.Combine(resolved, "TOS.exe"), p.GameExePath);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void AFolderWithoutTheGame_OrARelativePath_FallsBackToTheDefault()
    {
        Assert.AreEqual(CompanionPaths.DefaultGameDir, CompanionPaths.ResolveGameDir(Path.GetTempPath()));
        Assert.AreEqual(CompanionPaths.DefaultGameDir, CompanionPaths.ResolveGameDir(@"Games\TOS"));
        Assert.AreEqual(CompanionPaths.DefaultGameDir, CompanionPaths.ResolveGameDir("C:\\bad|path"));
    }

    [TestMethod]
    public void Normalize_GivesNullWithoutTheGame_AndTheFullPathWithIt()
    {
        Assert.IsNull(CompanionPaths.NormalizeGameDir(null));
        Assert.IsNull(CompanionPaths.NormalizeGameDir(Path.GetTempPath()));
        Assert.IsNull(CompanionPaths.NormalizeGameDir(@"Games\TOS"));

        var dir = Path.Combine(Path.GetTempPath(), "tosdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "TOS.exe"), "x");
            Assert.AreEqual(Path.GetFullPath(dir), CompanionPaths.NormalizeGameDir(" \"" + dir + "\\\" "));
            Assert.IsFalse(CompanionPaths.IsCompleteGameDir(dir), "TOS.exe alone is not a complete install");
            File.WriteAllText(Path.Combine(dir, "GameAssembly.dll"), "x");
            Assert.IsTrue(CompanionPaths.IsCompleteGameDir(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
