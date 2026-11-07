using System.Text.RegularExpressions;

namespace Companion.Knowledge.Tests;

[TestClass]
public sealed class CharacterKeyHasherTests
{
    private TestEnv _env = null!;

    [TestInitialize]
    public void Init() => _env = new TestEnv();

    [TestCleanup]
    public void Cleanup() => _env.Dispose();

    private string SaltPath => Path.Combine(_env.Paths.KnowledgeDir, "salt.bin");

    [TestMethod]
    public void HashIsSixteenLowerHexAndStableAcrossInstances()
    {
        var a = new CharacterKeyHasher(_env.Paths).Hash("fake-role-1");
        var b = new CharacterKeyHasher(_env.Paths).Hash("fake-role-1");
        Assert.AreEqual(a, b);
        Assert.IsTrue(Regex.IsMatch(a.Value, "^[0-9a-f]{16}$"), a.Value);
        Assert.AreEqual(a.Value[..4].ToUpperInvariant(), a.Short);
    }

    [TestMethod]
    public void TokensAreTrimmedAndDifferentTokensDiffer()
    {
        var h = new CharacterKeyHasher(_env.Paths);
        Assert.AreEqual(h.Hash("fake-role-1"), h.Hash("  fake-role-1\t"));
        Assert.AreNotEqual(h.Hash("fake-role-1"), h.Hash("fake-role-2"));
    }

    [TestMethod]
    public void DifferentSaltsGiveDifferentKeys()
    {
        using var other = new TestEnv();
        Assert.AreNotEqual(new CharacterKeyHasher(_env.Paths).Hash("fake-role-1"), new CharacterKeyHasher(other.Paths).Hash("fake-role-1"));
    }

    [TestMethod]
    public void SaltIsCreatedOnFirstHashWithABackup()
    {
        var h = new CharacterKeyHasher(_env.Paths);
        Assert.IsFalse(File.Exists(SaltPath));
        h.Hash("fake-role-1");
        Assert.HasCount(32, File.ReadAllBytes(SaltPath));
        CollectionAssert.AreEqual(File.ReadAllBytes(SaltPath), File.ReadAllBytes(SaltPath + ".bak1"));
    }

    [TestMethod]
    public void CorruptSaltFallsBackToItsBackup()
    {
        var before = new CharacterKeyHasher(_env.Paths).Hash("fake-role-1");
        File.WriteAllBytes(SaltPath, [1, 2, 3]);
        Assert.AreEqual(before, new CharacterKeyHasher(_env.Paths).Hash("fake-role-1"));
    }

    [TestMethod]
    public void LostSaltCreatesANewOneAndKeepsTheBrokenFileAside()
    {
        var before = new CharacterKeyHasher(_env.Paths).Hash("fake-role-1");
        File.WriteAllBytes(SaltPath, [1, 2, 3]);
        File.Delete(SaltPath + ".bak1");
        var after = new CharacterKeyHasher(_env.Paths).Hash("fake-role-1");
        Assert.AreNotEqual(before, after);
        Assert.IsTrue(File.Exists(SaltPath + ".corrupt"));
        Assert.HasCount(32, File.ReadAllBytes(SaltPath));
    }

    [TestMethod]
    public void EmptyTokenIsRefused()
    {
        var h = new CharacterKeyHasher(_env.Paths);
        Assert.ThrowsExactly<ArgumentException>(() => h.Hash(" "));
        Assert.ThrowsExactly<ArgumentException>(() => h.Hash(""));
        Assert.IsFalse(Directory.Exists(_env.Paths.RuntimeDir));
    }
}
