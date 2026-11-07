using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Companion.Contracts.Safety;

namespace Companion.GameIO.Tests;

/// <summary>
/// The static rules check (SPEC 1, data in <see cref="StaticRules"/>). Reads source files only; nothing is executed.
/// <list type="number">
/// <item>ForbiddenTokens: ordinal, case-sensitive, over every *.cs under src\, features\ and tools\ (code, strings and
/// comments), skipping src\Companion.Contracts and tests\.</item>
/// <item>RestrictedTokens: each token only in its listed projects (same file set).</item>
/// <item>PInvokeAllowlist: every DllImport / LibraryImport in every project, Contracts and tests included; key
/// "dll!EntryPoint" (EntryPoint from the attribute, else the method name); unlisted projects may declare none.</item>
/// <item>Feature projects (features\**): FeatureForbiddenTokens, and their csproj may only ProjectReference
/// Companion.Contracts.</item>
/// </list>
/// Runtime asserts (item 5) are covered by ChokePointTests.
/// </summary>
[TestClass]
public sealed class StaticRulesTests
{
    private static readonly Regex AttributeLoose = new(
        @"\[\s*(?:System\.Runtime\.InteropServices\.)?(?:DllImport|LibraryImport)(?:Attribute)?\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex AttributeStrict = new(
        @"\[\s*(?:System\.Runtime\.InteropServices\.)?(?<kind>DllImport|LibraryImport)(?:Attribute)?\s*\(\s*""(?<dll>[^""]*)""(?<args>[^\]]*)\]",
        RegexOptions.Compiled);

    private static readonly Regex EntryPointArg = new(@"EntryPoint\s*=\s*""(?<ep>[^""]+)""", RegexOptions.Compiled);

    private static readonly Regex Brackets = new(@"\[[^\[\]]*\]", RegexOptions.Compiled);

    private static readonly Regex FirstCall = new(@"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);

    private static readonly Regex ProjectReference = new(@"<ProjectReference\s+Include\s*=\s*""(?<path>[^""]+)""", RegexOptions.Compiled);

    private static readonly Lazy<string> RootLazy = new(FindCompanionRoot);

    private static string Root => RootLazy.Value;

    [TestMethod]
    public void ScanFindsTheSourceTree()
    {
        var files = TokenScanFiles().ToList();
        Assert.IsTrue(files.Any(f => f.EndsWith(@"Companion.GameIO\GameIo.cs", StringComparison.OrdinalIgnoreCase)), "GameIO sources must be scanned (non-vacuous)");
        Assert.IsFalse(files.Any(f => f.Contains(@"\src\Companion.Contracts\", StringComparison.OrdinalIgnoreCase)), "Contracts are skipped by the token scans");
        Assert.IsFalse(files.Any(f => f.Contains(@"\tests\", StringComparison.OrdinalIgnoreCase)), "tests are skipped by the token scans");
        Assert.IsTrue(PInvokeScanFiles().Any(f => f.Contains(@"\src\Companion.Contracts\", StringComparison.OrdinalIgnoreCase)), "the P/Invoke scan covers Contracts");
    }

    [TestMethod]
    public void ForbiddenTokensAppearNowhere()
    {
        var violations = new List<string>();
        foreach (var file in TokenScanFiles())
        {
            violations.AddRange(FindForbidden(File.ReadAllText(file), StaticRules.ForbiddenTokens).Select(t => $"{Rel(file)}: '{t}'"));
        }

        Assert.AreEqual(0, violations.Count, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void RestrictedTokensOnlyInTheirProjects()
    {
        var violations = new List<string>();
        foreach (var file in TokenScanFiles())
        {
            var project = ProjectOf(file);
            violations.AddRange(FindRestricted(File.ReadAllText(file), project, StaticRules.RestrictedTokens).Select(t => $"{Rel(file)} ({project}): '{t}'"));
        }

        Assert.AreEqual(0, violations.Count, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void PInvokesAreAllowlistedPerProject()
    {
        var violations = new List<string>();
        foreach (var file in PInvokeScanFiles())
        {
            var project = ProjectOf(file);
            var (keys, unparsed) = ParsePInvokes(File.ReadAllText(file));
            if (unparsed > 0)
            {
                violations.Add($"{Rel(file)}: {unparsed} P/Invoke attribute(s) the scanner cannot parse (use a string literal dll name)");
            }

            var allowed = StaticRules.PInvokeAllowlist.TryGetValue(project, out var list) ? list : [];
            violations.AddRange(keys.Where(k => !allowed.Contains(k)).Select(k => $"{Rel(file)} ({project}): {k}"));
        }

        Assert.AreEqual(0, violations.Count, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void GameIoAssemblyDeclaresOnlyAllowlistedPInvokes()
    {
        var allowed = StaticRules.PInvokeAllowlist["Companion.GameIO"];
        var asm = typeof(GameIo).Assembly;
        var found = 0;
        foreach (var t in asm.GetTypes())
        {
            foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!m.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
                {
                    continue;
                }

                var di = m.GetCustomAttribute<DllImportAttribute>();
                Assert.IsNotNull(di, m.Name);
                var key = DllKey(di.Value) + "!" + (string.IsNullOrEmpty(di.EntryPoint) ? m.Name : di.EntryPoint);
                CollectionAssert.Contains(allowed.ToList(), key, $"{t.Name}.{m.Name} -> {key}");
                found++;
            }
        }

        Assert.IsTrue(found > 30, "GameIO declares its Win32 surface");
    }

    [TestMethod]
    public void FeatureProjectsUseContractsOnly()
    {
        var featuresDir = Path.Combine(Root, "features");
        if (!Directory.Exists(featuresDir))
        {
            return;
        }

        var violations = new List<string>();
        foreach (var file in EnumerateCs(featuresDir))
        {
            violations.AddRange(FindForbidden(File.ReadAllText(file), StaticRules.FeatureForbiddenTokens).Select(t => $"{Rel(file)}: '{t}'"));
        }

        foreach (var csproj in Directory.EnumerateFiles(featuresDir, "*.csproj", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
        {
            foreach (Match m in ProjectReference.Matches(File.ReadAllText(csproj)))
            {
                var name = Path.GetFileNameWithoutExtension(m.Groups["path"].Value.Replace('/', '\\'));
                if (!StaticRules.FeatureAllowedReferences.Contains(name))
                {
                    violations.Add($"{Rel(csproj)}: ProjectReference {name}");
                }
            }
        }

        Assert.AreEqual(0, violations.Count, string.Join(Environment.NewLine, violations));
    }

    // ------------------------------------------------------------------ scanner self-tests (the scan is not vacuous)

    [TestMethod]
    public void ScannerCatchesPlantedForbiddenTokens()
    {
        foreach (var token in StaticRules.ForbiddenTokens)
        {
            var planted = "class X { void M() { /* " + token + " */ } }";
            CollectionAssert.Contains(FindForbidden(planted, StaticRules.ForbiddenTokens).ToList(), token, token);
        }

        Assert.AreEqual(0, FindForbidden("var r = NativeMethods.GetClipCursor(out var c); WM_SETFOCUS; proc.StartTime;", StaticRules.ForbiddenTokens).Count());
    }

    [TestMethod]
    public void ScannerCatchesRestrictedTokensOutsideTheirProject()
    {
        var text = "x.PostMessage(); y.SetWindowPos(); Process.Start(z);";
        var inEngine = FindRestricted(text, "Companion.Engine", StaticRules.RestrictedTokens).ToList();
        CollectionAssert.Contains(inEngine, "PostMessage");
        CollectionAssert.Contains(inEngine, "SetWindowPos");
        CollectionAssert.Contains(inEngine, "Process.Start");
        var inGameIo = FindRestricted(text, "Companion.GameIO", StaticRules.RestrictedTokens).ToList();
        CollectionAssert.AreEqual(new[] { "Process.Start" }, inGameIo);
    }

    [TestMethod]
    public void ScannerParsesPInvokeDeclarations()
    {
        // "@@" keeps this sample from being a declaration of THIS file for the real scan; it is removed at run time.
        const string sample = """
            [@@DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
            [@@DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
            internal static extern int DwmRect(IntPtr h, int a, out RECT r, int s);
            [@@LibraryImport("USER32.DLL")]
            internal static partial short GetAsyncKeyState(int vk);
            [@@DllImport(LibName)] static extern void Hidden();
            """;
        var (keys, unparsed) = ParsePInvokes(sample.Replace("@@", string.Empty, StringComparison.Ordinal));
        CollectionAssert.AreEquivalent(new[] { "user32!PostMessageW", "dwmapi!DwmGetWindowAttribute", "user32!GetAsyncKeyState" }, keys.ToList());
        Assert.AreEqual(1, unparsed);
    }

    // ------------------------------------------------------------------ scanner

    internal static IEnumerable<string> FindForbidden(string text, IReadOnlyList<string> tokens) =>
        tokens.Where(t => text.Contains(t, StringComparison.Ordinal));

    internal static IEnumerable<string> FindRestricted(string text, string project, IReadOnlyDictionary<string, IReadOnlyList<string>> restricted) =>
        restricted.Where(kv => !kv.Value.Contains(project) && text.Contains(kv.Key, StringComparison.Ordinal)).Select(kv => kv.Key);

    internal static (IReadOnlyList<string> Keys, int Unparsed) ParsePInvokes(string text)
    {
        var keys = new List<string>();
        var loose = AttributeLoose.Matches(text).Count;
        var strict = AttributeStrict.Matches(text);
        foreach (Match m in strict)
        {
            var dll = DllKey(m.Groups["dll"].Value);
            var ep = EntryPointArg.Match(m.Groups["args"].Value);
            string? entry = ep.Success ? ep.Groups["ep"].Value : null;
            if (entry is null)
            {
                var rest = text[(m.Index + m.Length)..];
                var semi = rest.IndexOf(';');
                var decl = Brackets.Replace(semi >= 0 ? rest[..semi] : rest, " ");
                var call = FirstCall.Match(decl);
                entry = call.Success ? call.Groups["name"].Value : "?";
            }

            keys.Add(dll + "!" + entry);
        }

        return (keys, loose - strict.Count);
    }

    private static string DllKey(string dll)
    {
        var d = dll.Trim().ToLowerInvariant();
        return d.EndsWith(".dll", StringComparison.Ordinal) ? d[..^4] : d;
    }

    private static IEnumerable<string> TokenScanFiles() =>
        new[] { "src", "features", "tools" }
            .Select(t => Path.Combine(Root, t))
            .Where(Directory.Exists)
            .SelectMany(EnumerateCs)
            .Where(f => !f.Contains(Path.Combine(Root, "src", "Companion.Contracts") + "\\", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> PInvokeScanFiles() =>
        new[] { "src", "features", "tools", "tests" }
            .Select(t => Path.Combine(Root, t))
            .Where(Directory.Exists)
            .SelectMany(EnumerateCs);

    private static IEnumerable<string> EnumerateCs(string dir) =>
        Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f));

    private static bool IsBuildOutput(string f) =>
        f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Project folder: the first directory under src\, features\, tools\ or tests\.</summary>
    private static string ProjectOf(string file)
    {
        var rel = Path.GetRelativePath(Root, file).Split('\\', '/');
        return rel.Length >= 2 ? rel[1] : rel[0];
    }

    private static string Rel(string file) => Path.GetRelativePath(Root, file);

    private static string FindCompanionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "Companion.Contracts")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("companion root not found above " + AppContext.BaseDirectory);
    }
}
