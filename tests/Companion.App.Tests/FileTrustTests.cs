using System.Text;
using Companion.App.Hosting;

namespace Companion.App.Tests;

[TestClass]
public sealed class FileTrustTests
{
    private static string MakeInstall()
    {
        var root = TestDirs.NewPath("install");
        Directory.CreateDirectory(Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.Combine(root, "templates"));
        Directory.CreateDirectory(Path.Combine(root, "features", "survey"));
        Directory.CreateDirectory(Path.Combine(root, "runtimes", "win-x64"));
        File.WriteAllText(Path.Combine(root, "Companion.App.exe"), "exe");
        File.WriteAllText(Path.Combine(root, "Companion.Engine.dll"), "dll");
        File.WriteAllText(Path.Combine(root, "runtimes", "win-x64", "native.dll"), "native");
        File.WriteAllText(Path.Combine(root, "data", "ui-map.json"), "{\"schemaVersion\":1}");
        File.WriteAllText(Path.Combine(root, "templates", "hud.bag.png"), "png");
        File.WriteAllText(Path.Combine(root, "features", "survey", "procedures.json"), "[]");
        File.WriteAllText(Path.Combine(root, "readme.txt"), "not install-side code or data");
        return root;
    }

    [TestMethod]
    public void Scan_CoversCodeAndDataFiles_Deterministically()
    {
        var root = MakeInstall();
        var m1 = FileTrust.Scan(root);
        var m2 = FileTrust.Scan(root);
        Assert.AreEqual(m1.BuildHash, m2.BuildHash);
        Assert.AreEqual(64, m1.BuildHash.Length);
        Assert.AreEqual(16, m1.ShortHash.Length);
        var names = m1.Files.Select(f => f.RelativePath).ToArray();
        CollectionAssert.AreEquivalent(
            new[] { "Companion.App.exe", "Companion.Engine.dll", @"runtimes\win-x64\native.dll", @"data\ui-map.json", @"templates\hud.bag.png", @"features\survey\procedures.json" },
            names);
    }

    [TestMethod]
    public void TrustList_Compare_Trust_AndTamperDetection()
    {
        var root = MakeInstall();
        var paths = TestDirs.Paths(installDir: root);
        var clock = new FakeClock();
        var dataFile = Path.Combine(root, "data", "ui-map.json");

        var ft = new FileTrust(paths, clock);
        Assert.IsFalse(ft.BuildTrusted);
        Assert.IsNull(ft.ReadTrusted(dataFile), "nothing is readable before the start scan");
        var cmp = ft.ScanAndCompare();
        Assert.IsFalse(cmp.Trusted);
        Assert.IsTrue(cmp.NoTrustList);
        Assert.AreEqual("untrusted.noList", cmp.Code);
        Assert.IsFalse(ft.BuildTrusted);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("{\"schemaVersion\":1}"), ft.ReadTrusted(dataFile), "data of the scanned build loads (jobs stay refused)");
        Assert.IsNull(ft.ReadTrusted(Path.Combine(root, "readme.txt")), "not an install-side file");
        Assert.IsNull(ft.ReadTrusted(Path.Combine(root, "..", "elsewhere.json")), "outside the install folder");
        Assert.IsNull(ft.ReadTrusted(Path.Combine(root, "data", "missing.json")));

        Assert.AreEqual("trust.ok", ft.TrustCurrentBuild());
        Assert.IsTrue(ft.BuildTrusted);
        Assert.IsTrue(File.Exists(paths.TrustedBuildPath));

        var ft2 = new FileTrust(paths, clock);
        Assert.IsTrue(ft2.ScanAndCompare().Trusted);
        Assert.IsTrue(ft2.BuildTrusted);
        Assert.IsNotNull(ft2.ReadTrusted(dataFile));

        // Changed after the start scan: the read is refused (the loaded data must be the trusted build).
        File.WriteAllText(dataFile, "{\"schemaVersion\":1,\"x\":1}");
        Assert.IsNull(ft2.ReadTrusted(dataFile));

        // Next start: the change is detected.
        var ft3 = new FileTrust(paths, clock);
        var c3 = ft3.ScanAndCompare();
        Assert.IsFalse(c3.Trusted);
        Assert.AreEqual(1, c3.Changed);
        Assert.AreEqual(0, c3.Missing);
        Assert.AreEqual(0, c3.Extra);

        File.WriteAllText(Path.Combine(root, "Injected.dll"), "x");
        File.Delete(Path.Combine(root, "templates", "hud.bag.png"));
        var c4 = new FileTrust(paths, clock).ScanAndCompare();
        Assert.AreEqual(1, c4.Extra);
        Assert.AreEqual(1, c4.Missing);
        Assert.AreEqual("untrusted.missing1.changed1.extra1", c4.Code);

        // A build that changes between the start scan and the click is never trusted.
        var ft5 = new FileTrust(paths, clock);
        ft5.ScanAndCompare();
        File.WriteAllText(Path.Combine(root, "Injected.dll"), "y");
        Assert.AreEqual("trust.changedSinceStart", ft5.TrustCurrentBuild());
        Assert.IsFalse(ft5.BuildTrusted);
    }

    [TestMethod]
    public void HandEditedTrustList_IsNotTrusted()
    {
        var root = MakeInstall();
        var paths = TestDirs.Paths(installDir: root);
        var ft = new FileTrust(paths, new FakeClock());
        ft.ScanAndCompare();
        Assert.AreEqual("trust.ok", ft.TrustCurrentBuild());

        // Someone edits a file AND its hash in the list without recomputing the build hash.
        var json = File.ReadAllText(paths.TrustedBuildPath);
        var scanned = ft.Scanned!;
        var entry = scanned.Files.First(f => f.RelativePath == @"data\ui-map.json");
        File.WriteAllText(Path.Combine(root, "data", "ui-map.json"), "evil");
        var evilHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("evil")));
        File.WriteAllText(paths.TrustedBuildPath, json.Replace(entry.Sha256, evilHash, StringComparison.Ordinal));

        var cmp = new FileTrust(paths, new FakeClock()).ScanAndCompare();
        Assert.IsFalse(cmp.Trusted);
        Assert.IsTrue(cmp.NoTrustList, "a list whose build hash does not match its files is ignored");
    }
}
