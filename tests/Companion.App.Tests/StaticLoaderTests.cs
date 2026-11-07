using System.Text.RegularExpressions;

namespace Companion.App.Tests;

/// <summary>
/// SPEC 4 "Never reload": install-side code and data are read only by the loader classes (through
/// <c>IFileTrust.ReadTrusted</c>). This scans every *.cs under src\, features\ and tools\ (bin/obj and Contracts
/// skipped): a file that both performs a direct file read and names an install-side path must be a loader class.
/// Also checks the host-only rules of the assembler's own projects.
/// </summary>
[TestClass]
public sealed partial class StaticLoaderTests
{
    /// <summary>The only classes allowed to read install-side files directly.</summary>
    private static readonly string[] LoaderFiles =
    [
        @"src\Companion.App\Hosting\FileTrust.cs",
    ];

    [GeneratedRegex(@"File\.(ReadAll\w*|OpenRead|OpenText|ReadLines|Open)\s*\(|new\s+FileStream\s*\(|new\s+StreamReader\s*\(|File\.Exists\s*\(|Directory\.(Enumerate|Get)\w*\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex DirectRead();

    [GeneratedRegex(@"\.(InstallDir|DataDir|TemplatesDir|FeaturesDataDir)\b|AppContext\.BaseDirectory", RegexOptions.CultureInvariant)]
    private static partial Regex InstallSidePath();

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")) && Directory.Exists(Path.Combine(dir.FullName, "src", "Companion.Contracts")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("companion root not found");
    }

    private static IEnumerable<string> Sources(string root) =>
        new[] { "src", "features", "tools" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) && !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains(@"\src\Companion.Contracts\", StringComparison.OrdinalIgnoreCase));

    [TestMethod]
    public void InstallSideFiles_AreReadOnlyByLoaderClasses()
    {
        var root = Root();
        var offenders = new List<string>();
        var scanned = 0;
        foreach (var file in Sources(root))
        {
            scanned++;
            var text = File.ReadAllText(file);
            var rel = Path.GetRelativePath(root, file);
            if (DirectRead().IsMatch(text) && InstallSidePath().IsMatch(text) && !LoaderFiles.Contains(rel, StringComparer.OrdinalIgnoreCase))
            {
                offenders.Add(rel);
            }
        }

        Assert.IsTrue(scanned > 50, "scanned " + scanned);
        Assert.AreEqual(0, offenders.Count, "direct install-side reads outside the loader: " + string.Join(", ", offenders));
    }

    [TestMethod]
    public void TheCli_ReferencesContractsOnly_AndDeclaresNoPInvoke()
    {
        var root = Root();
        var csproj = File.ReadAllText(Path.Combine(root, "src", "Companion.Cli", "Companion.Cli.csproj"));
        var refs = Regex.Matches(csproj, "ProjectReference Include=\"([^\"]+)\"").Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value)).ToArray();
        CollectionAssert.AreEqual(new[] { "Companion.Contracts" }, refs);
        Assert.IsFalse(csproj.Contains("PackageReference", StringComparison.Ordinal));
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "src", "Companion.Cli"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) && !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)))
        {
            var text = File.ReadAllText(f);
            Assert.IsFalse(text.Contains("DllImport", StringComparison.Ordinal) || text.Contains("LibraryImport", StringComparison.Ordinal), f);
            Assert.IsFalse(text.Contains("Process.Start", StringComparison.Ordinal), f);
        }
    }

    [TestMethod]
    public void Manifests_AppIsElevatedAndPerMonitorV2_CliIsAsInvoker()
    {
        var root = Root();
        var app = File.ReadAllText(Path.Combine(root, "src", "Companion.App", "app.manifest"));
        StringAssert.Contains(app, "level=\"requireAdministrator\"");
        StringAssert.Contains(app, "PerMonitorV2");
        var cli = File.ReadAllText(Path.Combine(root, "src", "Companion.Cli", "app.manifest"));
        StringAssert.Contains(cli, "level=\"asInvoker\"");
        var appProj = File.ReadAllText(Path.Combine(root, "src", "Companion.App", "Companion.App.csproj"));
        StringAssert.Contains(appProj, "<ApplicationManifest>app.manifest</ApplicationManifest>");
        Assert.IsFalse(Regex.IsMatch(appProj, @"Include=""[^""]*staging", RegexOptions.IgnoreCase), "templates\\staging must never be copied");
    }

    [TestMethod]
    public void TheSolution_ContainsEveryProject()
    {
        var root = Root();
        var sln = File.ReadAllText(Path.Combine(root, "Companion.sln"));
        var onDisk = new[] { "src", "features", "tools", "tests" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.csproj", SearchOption.AllDirectories))
            .Where(f => !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) && !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(root, f))
            .ToArray();
        Assert.IsTrue(onDisk.Length >= 27, onDisk.Length.ToString());
        foreach (var p in onDisk)
        {
            StringAssert.Contains(sln, p, "missing from Companion.sln: " + p);
        }
    }
}
