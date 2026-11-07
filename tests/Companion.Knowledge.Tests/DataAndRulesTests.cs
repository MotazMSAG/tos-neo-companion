using System.Text.RegularExpressions;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Safety;
using Companion.Knowledge.Storage;

namespace Companion.Knowledge.Tests;

[TestClass]
public sealed class DataAndRulesTests
{
    private static string CompanionRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }
        }
        throw new AssertInconclusiveException("companion root not found");
    }

    [TestMethod]
    public void SeedFilesAreValidAndEmpty()
    {
        var seed = Path.Combine(AppContext.BaseDirectory, "seed");
        var (bindings, e1) = KnowledgeSchema.ParseBindings(File.ReadAllBytes(Path.Combine(seed, "characters.seed.json")));
        Assert.IsNull(e1, e1);
        Assert.IsEmpty(bindings!.Characters!);

        var (character, e2) = KnowledgeSchema.ParseCharacter(
            File.ReadAllBytes(Path.Combine(seed, "knowledge.seed.json")), new CharacterKey("0000000000000000"), null);
        Assert.IsNull(e2, e2);
        Assert.IsEmpty(character!.Stock!.Items!);
        Assert.IsEmpty(character.Recipes!);
        Assert.IsEmpty(character.KeepStock!);
    }

    [TestMethod]
    public void SourceObeysTheStaticRules()
    {
        var project = Path.Combine(CompanionRoot(), "src", "Companion.Knowledge");
        var files = Directory.GetFiles(project, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.IsGreaterThanOrEqualTo(5, files.Count);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var token in StaticRules.ForbiddenTokens)
            {
                Assert.IsFalse(text.Contains(token, StringComparison.Ordinal), $"{Path.GetFileName(file)}: forbidden '{token}'");
            }
            foreach (var (token, owners) in StaticRules.RestrictedTokens)
            {
                if (!owners.Contains("Companion.Knowledge"))
                {
                    Assert.IsFalse(text.Contains(token, StringComparison.Ordinal), $"{Path.GetFileName(file)}: restricted '{token}'");
                }
            }
            Assert.IsFalse(Regex.IsMatch(text, @"\[(DllImport|LibraryImport)\b"), $"{Path.GetFileName(file)}: P/Invoke");
        }
    }

    [TestMethod]
    public void ProjectReferencesOnlyContracts()
    {
        var csproj = File.ReadAllText(Path.Combine(CompanionRoot(), "src", "Companion.Knowledge", "Companion.Knowledge.csproj"));
        var refs = Regex.Matches(csproj, "ProjectReference Include=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        CollectionAssert.AreEqual(new[] { @"..\Companion.Contracts\Companion.Contracts.csproj" }, refs);
        Assert.IsFalse(csproj.Contains("PackageReference", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PublicSurfaceIsTheContractImplementationsOnly()
    {
        var types = typeof(KnowledgeStore).Assembly.GetExportedTypes().Select(t => t.FullName).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(
            new[] { "Companion.Knowledge.CharacterKeyHasher", "Companion.Knowledge.KnowledgeStore", "Companion.Knowledge.Planning.Planner" },
            types);
    }
}
