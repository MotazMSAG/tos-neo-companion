using System.Diagnostics;
using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.Engine.Internal;
using Companion.EngineNav.Testing;

namespace Companion.Engine.Tests;

[TestClass]
public sealed class JournalTests
{
    private static JournalRecord Rec(FakeClock clock, params (string, object?)[] fields) =>
        new(clock.UtcNow, JournalKind.Decision, "0123456789abcdef0123456789abcdef", "OpenBagKey", "open",
            fields.ToDictionary(f => f.Item1, f => f.Item2));

    [TestMethod]
    public void WritesJsonlPerDay_CodesKept_FreeTextRedacted()
    {
        var paths = TestPaths.ForTest("journal");
        Directory.CreateDirectory(paths.RuntimeDir);
        var clock = new FakeClock();
        var j = new Journal(paths, clock);
        j.Write(Rec(clock, ("outcome", StepOutcome.Ok), ("count", 3), ("ms", 12.5), ("flag", true), ("ui", "UI_BAG_MAIN"),
            ("chat", "hello <b>world</b>!"), ("role", "12345-678901"), ("obj", new object()), ("when", TimeSpan.FromMilliseconds(70))));
        Assert.AreEqual(0, Directory.Exists(paths.JournalDir) ? Directory.GetFiles(paths.JournalDir).Length : 0, "Write never touches the disk");
        j.Flush();
        var file = Path.Combine(paths.JournalDir, "2026-10-14.jsonl");
        Assert.IsTrue(File.Exists(file));
        var lines = File.ReadAllLines(file);
        Assert.AreEqual(1, lines.Length);
        using var doc = JsonDocument.Parse(lines[0]);
        var f = doc.RootElement.GetProperty("fields");
        Assert.AreEqual("Ok", f.GetProperty("outcome").GetString());
        Assert.AreEqual(3, f.GetProperty("count").GetInt32());
        Assert.IsTrue(f.GetProperty("flag").GetBoolean());
        Assert.AreEqual("UI_BAG_MAIN", f.GetProperty("ui").GetString());
        Assert.AreEqual(JournalSchema.Redacted, f.GetProperty("chat").GetString());
        Assert.AreEqual(JournalSchema.Redacted, f.GetProperty("role").GetString(), "role-id-like values are never written");
        Assert.AreEqual(JournalSchema.Redacted, f.GetProperty("obj").GetString());
        Assert.AreEqual(70, f.GetProperty("when").GetDouble(), 0.001);
        Assert.AreEqual("decision", doc.RootElement.GetProperty("kind").GetString());
        Assert.IsFalse(lines[0].Contains("hello", StringComparison.Ordinal));
        Assert.AreEqual(3, j.RedactedValues);
    }

    [TestMethod]
    public void SplitsByUtcDay_AndAppends()
    {
        var paths = TestPaths.ForTest("journal-days");
        Directory.CreateDirectory(paths.RuntimeDir);
        var clock = new FakeClock { Limit = TimeSpan.FromDays(2) };
        var j = new Journal(paths, clock);
        j.Write(Rec(clock, ("a", 1)));
        clock.Advance(TimeSpan.FromHours(16));
        j.Write(Rec(clock, ("a", 2)));
        j.Flush();
        j.Write(Rec(clock, ("a", 3)));
        j.Flush();
        Assert.AreEqual(1, File.ReadAllLines(Path.Combine(paths.JournalDir, "2026-10-14.jsonl")).Length);
        Assert.AreEqual(2, File.ReadAllLines(Path.Combine(paths.JournalDir, "2026-10-15.jsonl")).Length);
    }

    [TestMethod]
    public void MissingRuntimeDir_DropsWithoutThrowingOrCreatingIt()
    {
        var paths = TestPaths.ForTest("journal-missing");
        var clock = new FakeClock();
        var j = new Journal(paths, clock);
        j.Write(Rec(clock, ("a", 1)));
        j.Flush();
        Assert.IsFalse(Directory.Exists(paths.RuntimeDir), "RuntimeDir is created by the App with its ACL, never by the journal");
        Assert.AreEqual(1, j.Dropped);
    }

    [TestMethod]
    public void BufferCap_DropsOldestAndReportsCount()
    {
        var paths = TestPaths.ForTest("journal-cap");
        Directory.CreateDirectory(paths.RuntimeDir);
        var clock = new FakeClock();
        var j = new Journal(paths, clock);
        for (var i = 0; i < Journal.MaxBuffered + 5; i++)
        {
            j.Write(Rec(clock, ("i", i)));
        }
        Assert.AreEqual(5, j.Dropped);
        j.Flush();
        var lines = File.ReadAllLines(Path.Combine(paths.JournalDir, "2026-10-14.jsonl"));
        Assert.AreEqual(Journal.MaxBuffered + 1, lines.Length);
        Assert.IsTrue(lines.Any(l => l.Contains("journal.dropped", StringComparison.Ordinal)));
        Assert.AreEqual(0, j.Dropped);
    }

    [TestMethod]
    public void ReparsePointJournalDir_Refused()
    {
        var paths = TestPaths.ForTest("journal-reparse");
        Directory.CreateDirectory(paths.RuntimeDir);
        var target = Path.Combine(Path.GetDirectoryName(paths.RuntimeDir)!, "elsewhere-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(target);
        if (!ReparseHelper.TryCreateJunction(paths.JournalDir, target))
        {
            Assert.Inconclusive("could not create a junction in the test folder");
        }
        var clock = new FakeClock();
        var j = new Journal(paths, clock);
        j.Write(Rec(clock, ("a", 1)));
        j.Flush();
        Assert.AreEqual(0, Directory.GetFiles(target).Length, "nothing written through the reparse point");
        Assert.AreEqual(1, j.Dropped);
    }

    [TestMethod]
    public void WriteNeverThrows_OnNull()
    {
        var j = new Journal(TestPaths.ForTest("journal-null"), new FakeClock());
        j.Write(null!);
        j.Flush();
    }
}

[TestClass]
public sealed class ArtifactStoreTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 9, 9];

    private sealed class ChatDroppingFilter : ILogPrivacyFilter
    {
        public bool IsAllowed(string rawLine) => rawLine.Contains("ui_mgr", StringComparison.Ordinal);

        public string? Filter(string rawLine) => IsAllowed(rawLine) ? rawLine.Replace("12345-678901", "<id>", StringComparison.Ordinal) : null;
    }

    private static (ArtifactStore Store, CompanionPaths Paths, FakeClock Clock) New(string name)
    {
        var paths = TestPaths.ForTest(name);
        Directory.CreateDirectory(paths.RuntimeDir);
        var clock = new FakeClock();
        return (new ArtifactStore(paths, clock, new ChatDroppingFilter()), paths, clock);
    }

    [TestMethod]
    public void SaveImage_GroupFolder_SanitisedNames()
    {
        var (store, paths, _) = New("art-img");
        var p = store.SaveImage(ArtifactCategory.Calibration, "20261014T100000Z-Calibrate/Expanded Strip!", Png);
        Assert.IsNotNull(p);
        Assert.IsTrue(p!.StartsWith(paths.CalibrationDir, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("expanded-strip-.png", Path.GetFileName(p));
        Assert.AreEqual("20261014t100000z-calibrate", Path.GetFileName(Path.GetDirectoryName(p)));
        CollectionAssert.AreEqual(Png, File.ReadAllBytes(p));
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(p)!, "*.tmp").Length);
    }

    [TestMethod]
    public void SaveImage_RefusesNonPng()
    {
        var (store, _, _) = New("art-nonpng");
        Assert.IsNull(store.SaveImage(ArtifactCategory.Survey, "x", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17]));
        Assert.IsNull(store.SaveImage(ArtifactCategory.Survey, "x", []));
    }

    [TestMethod]
    public void SameNameTwice_DoesNotOverwrite()
    {
        var (store, _, _) = New("art-dup");
        var a = store.SaveImage(ArtifactCategory.Survey, "g/shot", Png);
        var b = store.SaveImage(ArtifactCategory.Survey, "g/shot", Png);
        Assert.IsNotNull(a);
        Assert.IsNotNull(b);
        Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public void LogExcerpt_IsRefilteredAndCapped()
    {
        var (store, _, _) = New("art-log");
        var lines = new List<string> { "x [LOG] -> module/foundation/ui/ui_mgr.lua:1 show_ui UI_BAG_MAIN 12345-678901", "chat_mgr: hello friend" };
        lines.AddRange(Enumerable.Range(0, 700).Select(i => "ui_mgr line " + i));
        var p = store.SaveLogExcerpt(ArtifactCategory.Survey, "g/log", lines);
        Assert.IsNotNull(p);
        var text = File.ReadAllLines(p!);
        Assert.AreEqual(ArtifactStore.MaxExcerptLines, text.Length);
        Assert.IsFalse(text.Any(l => l.Contains("hello", StringComparison.Ordinal)), "chat dropped");
        Assert.IsFalse(text.Any(l => l.Contains("12345-678901", StringComparison.Ordinal)), "role id scrubbed");
    }

    [TestMethod]
    public void DiagnosticsCap_KeepsAtMost20Bundles()
    {
        var (store, paths, clock) = New("art-cap");
        for (var i = 0; i < 25; i++)
        {
            Assert.IsNotNull(store.SaveImage(ArtifactCategory.Diagnostics, $"2026101{i:00}-bundle/crop", Png));
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        var bundles = Directory.GetDirectories(paths.DiagnosticsDir);
        Assert.AreEqual(20, bundles.Length);
        Assert.IsFalse(bundles.Any(b => Path.GetFileName(b) == "202610100-bundle"), "the oldest are deleted");
    }

    [TestMethod]
    public void ReparsePointCategoryDir_Refused()
    {
        var (store, paths, _) = New("art-reparse");
        var target = Path.Combine(Path.GetDirectoryName(paths.RuntimeDir)!, "elsewhere-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(target);
        if (!ReparseHelper.TryCreateJunction(paths.SurveyDir, target))
        {
            Assert.Inconclusive("could not create a junction in the test folder");
        }
        Assert.IsNull(store.SaveImage(ArtifactCategory.Survey, "g/shot", Png));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(target).Length);
    }

    [TestMethod]
    public void MissingRuntimeDir_Refused()
    {
        var paths = TestPaths.ForTest("art-missing");
        var store = new ArtifactStore(paths, new FakeClock(), new ChatDroppingFilter());
        Assert.IsNull(store.SaveImage(ArtifactCategory.Survey, "g/shot", Png));
        Assert.IsFalse(Directory.Exists(paths.RuntimeDir));
    }
}

/// <summary>Creates a directory junction inside the test output folder (no elevation needed).</summary>
internal static class ReparseHelper
{
    public static bool TryCreateJunction(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch
        {
            // symlinks need a privilege; fall back to a junction
        }
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            return Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }
}

[TestClass]
public sealed class ProcedureCatalogTests
{
    private static ProcedureCatalog NewCatalog(Harness h) => new(h.Templates, h.Readers, h.Map, h.Journal, h.Clock);

    [TestMethod]
    public void CoreProcedures_LoadAndSealWithoutRejections()
    {
        var h = new Harness();
        foreach (var id in new[] { "Reset", "OpenBagKey", "OpenBagClick", "ExpandMenu", "CollapseMenu", "OpenAbility", "Calibrate" })
        {
            Assert.IsNotNull(h.Procedures.Get(id), id);
        }
        Assert.AreEqual(7, h.Procedures.All.Count);
        Assert.AreEqual(0, h.Procedures.Rejected.Count, string.Join(";", h.Procedures.Rejected));
        Assert.AreEqual(10, h.Procedures.Get("Reset")!.Limits.MaxEsc);
        Assert.AreEqual(ProcedureKind.Maintenance, h.Procedures.Get("Calibrate")!.Kind);
        Assert.AreEqual(6000, h.Procedures.Get("OpenAbility")!.Steps.Single(s => s.Id == "open").TimeoutMs);
        var cal = h.Procedures.Get("Calibrate")!;
        Assert.IsFalse(cal.Steps.Any(s => s.Action is RestoreHudAction or ToggleHudAction { Target: HudState.Collapsed }), "Calibrate ends expanded");

        // The menu stays expanded for good: only the explicit CollapseMenu procedure may collapse it.
        foreach (var p in h.Procedures.All)
        {
            var collapses = p.Steps.Any(s => s.Action is RestoreHudAction or ToggleHudAction { Target: HudState.Collapsed });
            Assert.AreEqual(p.Id == "CollapseMenu", collapses, p.Id);
        }
    }

    [TestMethod]
    public void LimitsAboveCeiling_Throw()
    {
        var h = new Harness();
        var c = NewCatalog(h);
        var p = h.Procedures.Get("OpenBagKey")! with { Id = "X1" };
        Assert.ThrowsExactly<InvalidDataException>(() => c.Add(p with { Limits = p.Limits with { MaxInputBatches = 61 } }, "test"));
        Assert.ThrowsExactly<InvalidDataException>(() => c.Add(p with { Limits = p.Limits with { MaxClicks = 21 } }, "test"));
        Assert.ThrowsExactly<InvalidDataException>(() => c.Add(p with { Limits = p.Limits with { MaxEsc = 13 } }, "test"));
        Assert.ThrowsExactly<InvalidDataException>(() => c.Add(p with { Limits = p.Limits with { MaxCountedPerStep = 4 } }, "test"));
        Assert.ThrowsExactly<InvalidDataException>(() => c.Add(p with { Limits = p.Limits with { PatienceMs = 600_001 } }, "test"));
        c.Add(p with { Limits = p.Limits with { MaxInputBatches = 60, MaxEsc = 12 } }, "test");
    }

    [TestMethod]
    public void DuplicateIdAndStructuralErrors_Throw()
    {
        var h = new Harness();
        var c = NewCatalog(h);
        var p = h.Procedures.Get("OpenBagKey")!;
        c.Add(p, "core");
        Assert.ThrowsExactly<InvalidDataException>(() => c.Add(p, "feature"));
        var letterWithoutClean = new ProcedureDefinition("Y1", "x", [new StepDefinition("b", StatePredicate.Any, new KeyAction(GameKey.B), Expectation.Nothing)], ProcedureLimits.Default);
        Assert.ThrowsExactly<InvalidDataException>(() => c.Add(letterWithoutClean, "test"));
        var commitOptional = new ProcedureDefinition("Y2", "x", [new StepDefinition("c", StatePredicate.Any, new ClickAction("hud.bag"), Expectation.Nothing, 3000, StepFlags.Commit | StepFlags.Optional)],
            ProcedureLimits.Default, ProcedureKind.Commit, AutomationSwitch.Crafting);
        Assert.ThrowsExactly<InvalidDataException>(() => c.Add(commitOptional, "test"));
        var commitNoGate = new ProcedureDefinition("Y3", "x", [new StepDefinition("c", StatePredicate.Any, new ClickAction("hud.bag"), Expectation.Nothing, 3000, StepFlags.Commit)], ProcedureLimits.Default);
        Assert.ThrowsExactly<InvalidDataException>(() => c.Add(commitNoGate, "test"));
        var dupSteps = new ProcedureDefinition("Y4", "x", [
            new StepDefinition("s", StatePredicate.Any, new WaitAction(1), Expectation.Nothing),
            new StepDefinition("s", StatePredicate.Any, new WaitAction(1), Expectation.Nothing)], ProcedureLimits.Default);
        Assert.ThrowsExactly<InvalidDataException>(() => c.Add(dupSteps, "test"));
        var badLabel = new ProcedureDefinition("Y5", "x", [new StepDefinition("s", StatePredicate.Any, new CaptureAction("Bad Label", CaptureScope.HudStripRight), Expectation.Nothing)], ProcedureLimits.Default);
        Assert.ThrowsExactly<InvalidDataException>(() => c.Add(badLabel, "test"));

        // Click offsets: bounded by the client size, never on a commit step (a commit lands on its own button).
        var offsetTooFar = new ProcedureDefinition("Y6", "x", [new StepDefinition("c", StatePredicate.Any, new ClickAction("hud.bag", new PointI(0, 1009)), Expectation.Nothing)], ProcedureLimits.Default);
        Assert.ThrowsExactly<InvalidDataException>(() => c.Add(offsetTooFar, "test"));
        var commitOffset = new ProcedureDefinition("Y7", "x", [new StepDefinition("c", StatePredicate.Any, new ClickAction("hud.bag", new PointI(5, 0)), Expectation.Nothing, 3000, StepFlags.Commit)],
            ProcedureLimits.Default, ProcedureKind.Commit, AutomationSwitch.Crafting);
        Assert.ThrowsExactly<InvalidDataException>(() => c.Add(commitOffset, "test"));
        c.Add(new ProcedureDefinition("Y8", "x", [new StepDefinition("c", StatePredicate.Any, new ClickAction("hud.bag", new PointI(-1919, 1008)), Expectation.Nothing)], ProcedureLimits.Default), "test");
    }

    [TestMethod]
    public void DanglingReferences_RejectedAtSeal_CallersToo()
    {
        var h = new Harness();
        var c = NewCatalog(h);
        var missingTemplate = new ProcedureDefinition("Z1", "x", [new StepDefinition("c", StatePredicate.Any, new ClickAction("no.such.template"), Expectation.Nothing)], ProcedureLimits.Default);
        var caller = new ProcedureDefinition("Z2", "x", [new StepDefinition("c", StatePredicate.Any, new CallProcedureAction("Z1"), Expectation.Nothing)], ProcedureLimits.Default);
        var missingReader = new ProcedureDefinition("Z3", "x", [new StepDefinition("r", StatePredicate.Any, new ReadAction("no.reader", "k"), Expectation.Nothing)], ProcedureLimits.Default);
        var missingScreen = new ProcedureDefinition("Z4", "x", [new StepDefinition("n", StatePredicate.Any, new NavigateAction("no.screen"), Expectation.Nothing)], ProcedureLimits.Default);
        var good = h.Procedures.Get("OpenBagKey")!;
        c.Add(caller, "f1");
        c.Add(missingTemplate, "f1");
        c.Add(missingReader, "f2");
        c.Add(missingScreen, "f3");
        c.Add(good, "core");
        c.Seal();
        Assert.IsNull(c.Get("Z1"));
        Assert.IsNull(c.Get("Z2"));
        Assert.IsNull(c.Get("Z3"));
        Assert.IsNull(c.Get("Z4"));
        Assert.IsNotNull(c.Get("OpenBagKey"));
        Assert.AreEqual(4, c.Rejected.Count);
        Assert.IsTrue(h.Journal.Records.Any(r => FakeJournal.Str(r, "code") == "procedure.rejected"));
        Assert.ThrowsExactly<InvalidOperationException>(() => c.Add(good with { Id = "Late" }, "core"));
    }

    [TestMethod]
    public void Clamp_NeverExceedsCeilings()
    {
        var l = StepValidation.Clamp(new ProcedureLimits(9, 9, 9, 9, 9, 999, 999, 999, int.MaxValue));
        Assert.AreEqual(3, l.MaxCountedPerStep);
        Assert.AreEqual(4, l.MaxUncountedPerStep);
        Assert.AreEqual(1, l.MaxStepBacks);
        Assert.AreEqual(2, l.MaxRestarts);
        Assert.AreEqual(2, l.MaxPreconditionLost);
        Assert.AreEqual(HardRules.CeilingInputBatchesPerProcedure, l.MaxInputBatches);
        Assert.AreEqual(HardRules.CeilingClicksPerProcedure, l.MaxClicks);
        Assert.AreEqual(HardRules.CeilingEscPerProcedure, l.MaxEsc);
        Assert.AreEqual(600_000, l.PatienceMs);
    }
}

[TestClass]
public sealed class ScreenServiceTests
{
    [TestMethod]
    public void Capture_RateLimitedToTwoPerSecond()
    {
        var h = new Harness();
        var start = h.Clock.Elapsed;
        for (var i = 0; i < 5; i++)
        {
            Assert.IsTrue(h.Screen.Capture(CancellationToken.None).Ok);
        }
        Assert.IsTrue(h.Clock.Elapsed - start >= TimeSpan.FromMilliseconds(2000), (h.Clock.Elapsed - start).ToString());
        Assert.AreEqual(5, h.Game.Captures);
    }

    [TestMethod]
    public void ClassifyHudStable_TwoAgreeingCaptures_300msApart()
    {
        var h = new Harness();
        var hc = h.Screen.ClassifyHudStable(CancellationToken.None);
        Assert.AreEqual(HudState.Collapsed, hc.State);
        Assert.AreEqual(2, h.Game.Captures);
        h.Vision.HudScript.Enqueue(HudState.Collapsed);
        h.Vision.HudScript.Enqueue(HudState.Expanded);
        var unstable = h.Screen.ClassifyHudStable(CancellationToken.None);
        Assert.AreEqual(HudState.Unknown, unstable.State);
        Assert.AreEqual("hud.unstable", unstable.DetailCode);
    }

    [TestMethod]
    public void Capture_IconicGame_ReportsProblem()
    {
        var h = new Harness();
        h.Game.Iconic = true;
        var c = h.Screen.Capture(CancellationToken.None);
        Assert.IsFalse(c.Ok);
        Assert.AreEqual(CaptureProblem.Iconic, c.Problem);
        Assert.AreEqual(HudState.Unknown, h.Screen.ClassifyHudStable(CancellationToken.None).State);
    }

    [TestMethod]
    public void Read_NeedsSurveyReader_NoCapture()
    {
        var h = new Harness();
        var r = h.Screen.Read("bag.grid", CancellationToken.None);
        Assert.AreEqual(Companion.Contracts.Perception.ReadStatus.NeedsSurvey, r.Status);
        Assert.AreEqual(0, h.Game.Captures);
        var ok = h.Screen.Read("test.ready", CancellationToken.None);
        Assert.AreEqual(Companion.Contracts.Perception.ReadStatus.Ok, ok.Status);
        Assert.AreEqual(1, h.ScreenReader.Calls);
    }
}

[TestClass]
public sealed class EngineStaticRulesTests
{
    private static string CompanionRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SPEC.md")))
        {
            d = d.Parent;
        }
        return d?.FullName ?? throw new DirectoryNotFoundException("companion root");
    }

    /// <summary>Local mirror of SPEC 1 (static check) over this unit's source: forbidden and restricted tokens, no P/Invoke.</summary>
    [TestMethod]
    public void EngineAndNavigationSources_PassStaticRules()
    {
        var root = CompanionRoot();
        var files = new[] { "Companion.Engine", "Companion.Navigation" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();
        Assert.IsTrue(files.Count >= 10);
        var hits = new List<string>();
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            foreach (var t in StaticRules.ForbiddenTokens.Where(t => text.Contains(t, StringComparison.Ordinal)))
            {
                hits.Add(Path.GetFileName(f) + ": forbidden " + t);
            }
            foreach (var (t, _) in StaticRules.RestrictedTokens.Where(kv => text.Contains(kv.Key, StringComparison.Ordinal)))
            {
                hits.Add(Path.GetFileName(f) + ": restricted " + t);
            }
            foreach (var t in new[] { "DllImport", "LibraryImport" }.Where(t => text.Contains(t, StringComparison.Ordinal)))
            {
                hits.Add(Path.GetFileName(f) + ": p/invoke " + t);
            }
        }
        Assert.AreEqual(0, hits.Count, string.Join("; ", hits));
    }

    [TestMethod]
    public void UnitProjects_ReferenceOnlyContracts()
    {
        var root = CompanionRoot();
        foreach (var p in new[] { "Companion.Engine", "Companion.Navigation" })
        {
            var csproj = File.ReadAllText(Path.Combine(root, "src", p, p + ".csproj"));
            var refs = System.Text.RegularExpressions.Regex.Matches(csproj, "ProjectReference Include=\"([^\"]+)\"").Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value)).ToList();
            CollectionAssert.AreEqual(new[] { "Companion.Contracts" }, refs, p);
            Assert.IsFalse(csproj.Contains("PackageReference", StringComparison.Ordinal), p);
        }
    }
}
