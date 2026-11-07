using Companion.App.Hosting;
using Companion.Contracts;

namespace Companion.App.Tests;

/// <summary>
/// The game folder without environment variables (<see cref="GameFolderResolver"/>, <see cref="GameFolderStore"/>):
/// resolution order, validation, the saved file, the guardian's quiet order and the parent-folder acceptance.
/// </summary>
[TestClass]
public sealed class GameFolderTests
{
    private static string MakeGame(string dir, bool withAssembly = true)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "TOS.exe"), "x");
        if (withAssembly)
        {
            File.WriteAllText(Path.Combine(dir, "GameAssembly.dll"), "x");
        }

        return Path.GetFullPath(dir);
    }

    private static string? NoDialog(Func<GameFolderChoice?> detect) => throw new AssertFailedException("the dialog must not be shown");

    private static GameFolderChoice? NoSearch() => throw new AssertFailedException("nothing must be searched");

    [TestMethod]
    public void TheVariable_WinsOverTheSavedFolder_AndNothingIsSearched()
    {
        var a = MakeGame(TestDirs.NewPath("var"));
        var b = MakeGame(TestDirs.NewPath("saved"));
        var r = GameFolderResolver.Resolve(a, b, NoSearch, NoDialog);
        Assert.AreEqual(new GameFolderChoice(a, GameFolderSource.Variable), r);
        Assert.IsFalse(r!.ShouldSave);
    }

    [TestMethod]
    public void AVariableWithoutTheGame_FallsBackToTheSavedFolder()
    {
        var b = MakeGame(TestDirs.NewPath("saved"));
        var r = GameFolderResolver.Resolve(TestDirs.NewPath("empty"), b, NoSearch, NoDialog);
        Assert.AreEqual(new GameFolderChoice(b, GameFolderSource.Saved), r);
        Assert.IsFalse(r!.ShouldSave);
    }

    [TestMethod]
    public void ASavedFolderThatLostTheGame_IsSearchedAgain_AndTheFindIsSaved()
    {
        var gone = TestDirs.NewPath("gone");
        var found = MakeGame(TestDirs.NewPath("found"));
        var r = GameFolderResolver.Resolve(null, gone, () => new GameFolderChoice(found, GameFolderSource.RunningGame), NoDialog);
        Assert.AreEqual(GameFolderSource.RunningGame, r!.Source);
        Assert.IsTrue(r.ShouldSave);
    }

    [TestMethod]
    public void NothingFound_AsksTheUser_WhoseTryAgainRunsTheSearch()
    {
        var chosen = MakeGame(TestDirs.NewPath("chosen"));
        var searches = 0;
        GameFolderChoice? Detect()
        {
            searches++;
            return null;
        }

        var r = GameFolderResolver.Resolve(null, null, Detect, detect =>
        {
            Assert.IsNull(detect()); // Try again
            return chosen;
        });
        Assert.AreEqual(new GameFolderChoice(chosen, GameFolderSource.UserChoice), r);
        Assert.AreEqual(2, searches);
        Assert.IsTrue(r!.ShouldSave);
    }

    [TestMethod]
    public void ExitInTheDialog_OrAFolderWithoutTheGame_MeansNoStart()
    {
        Assert.IsNull(GameFolderResolver.Resolve(null, null, () => null, _ => null));
        Assert.IsNull(GameFolderResolver.Resolve(null, null, () => null, _ => TestDirs.NewPath("nothing")));
    }

    [TestMethod]
    public void Detect_PrefersTheRunningGame_ThenTheFirstCompleteUsualPlace()
    {
        var running = MakeGame(TestDirs.NewPath("running"));
        var incomplete = MakeGame(TestDirs.NewPath("incomplete"), withAssembly: false);
        var first = MakeGame(TestDirs.NewPath("first"));
        var second = MakeGame(TestDirs.NewPath("second"));
        var places = new[] { TestDirs.NewPath("missing"), incomplete, first, second };

        Assert.AreEqual(new GameFolderChoice(running, GameFolderSource.RunningGame),
            GameFolderResolver.Detect(() => running, places, CompanionPaths.IsCompleteGameDir));
        Assert.AreEqual(new GameFolderChoice(first, GameFolderSource.UsualPlace),
            GameFolderResolver.Detect(() => null, places, CompanionPaths.IsCompleteGameDir));
        Assert.AreEqual(new GameFolderChoice(first, GameFolderSource.UsualPlace),
            GameFolderResolver.Detect(() => incomplete, places, CompanionPaths.IsCompleteGameDir), "a running game folder without GameAssembly.dll is not taken");
        Assert.IsNull(GameFolderResolver.Detect(() => null, [incomplete], CompanionPaths.IsCompleteGameDir));
    }

    [TestMethod]
    public void UsualPlaces_StartWithTheDefault_ThenEveryDriveInOrder_WithoutDuplicates()
    {
        var places = GameFolderResolver.UsualPlaces([@"C:\", "D:"]);
        CollectionAssert.AreEqual(
            new[]
            {
                @"D:\Games\TOS",
                @"C:\Games\TOS", @"C:\TOS", @"C:\Program Files\TOS", @"C:\Program Files (x86)\TOS",
                @"D:\TOS", @"D:\Program Files\TOS", @"D:\Program Files (x86)\TOS",
            },
            places.ToArray());
    }

    [TestMethod]
    public void AChosenFolder_IsAcceptedWhenItHoldsTheGame()
    {
        var game = MakeGame(TestDirs.NewPath("pick"), withAssembly: false);
        Assert.AreEqual(game, GameFolderResolver.AcceptChosen(game + "\\"));
        Assert.IsNull(GameFolderResolver.AcceptChosen(TestDirs.NewPath("none")));
        Assert.IsNull(GameFolderResolver.AcceptChosen(@"Games\TOS"));
        Assert.IsNull(GameFolderResolver.AcceptChosen(null));
    }

    [TestMethod]
    public void AParentWithExactlyOneTosFolderBelow_IsAccepted()
    {
        var parent = TestDirs.NewPath("parent");
        var game = MakeGame(Path.Combine(parent, "Games", "TOS"));
        Directory.CreateDirectory(Path.Combine(parent, "Other", "Stuff"));
        Assert.AreEqual(game, GameFolderResolver.AcceptChosen(parent));
        Assert.AreEqual(game, GameFolderResolver.AcceptChosen(Path.Combine(parent, "Games")));
    }

    [TestMethod]
    public void AParentWithTwoGamesBelow_OrATosFolderWithoutTheExe_IsRefused()
    {
        var two = TestDirs.NewPath("two");
        MakeGame(Path.Combine(two, "A", "TOS"));
        MakeGame(Path.Combine(two, "B", "TOS"));
        Assert.IsNull(GameFolderResolver.AcceptChosen(two));

        var empty = TestDirs.NewPath("emptyTos");
        Directory.CreateDirectory(Path.Combine(empty, "TOS"));
        Assert.IsNull(GameFolderResolver.AcceptChosen(empty));
    }

    [TestMethod]
    public void TheSavedFile_RoundTrips_AndIsValidatedOnRead()
    {
        var runtime = TestDirs.NewPath("runtime");
        var game = MakeGame(TestDirs.NewPath("game"));
        var store = new GameFolderStore(runtime);
        Assert.IsNull(store.Read());
        Assert.IsTrue(store.Save(game + "\\"));
        Assert.AreEqual(game, store.Read());
        Assert.AreEqual((CompanionPaths.ForGame(@"C:\app", game) with { RuntimeDir = runtime }).GameFolderPath, store.FilePath);

        File.Delete(Path.Combine(game, "TOS.exe"));
        Assert.AreEqual(game, store.ReadRaw());
        Assert.IsNull(store.Read(), "a folder that lost TOS.exe is not used");
    }

    [TestMethod]
    public void TheStore_RefusesANonGameFolder_AndIgnoresABrokenFile()
    {
        var runtime = TestDirs.NewPath("runtime");
        var store = new GameFolderStore(runtime);
        Assert.IsFalse(store.Save(TestDirs.NewPath("nogame")));
        Assert.IsFalse(File.Exists(store.FilePath));

        Directory.CreateDirectory(runtime);
        File.WriteAllText(store.FilePath, "{ not json");
        Assert.IsNull(store.ReadRaw());
        File.WriteAllText(store.FilePath, """{"schemaVersion":2,"gameDir":"C:\\x"}""");
        Assert.IsNull(store.ReadRaw());
    }

    [TestMethod]
    public void TheGuardian_UsesTheVariable_ThenTheSavedFolder_ThenTheDefault()
    {
        var a = MakeGame(TestDirs.NewPath("var"));
        var b = MakeGame(TestDirs.NewPath("saved"));
        Assert.AreEqual(a, GameFolderResolver.ForGuardian(a, b));
        Assert.AreEqual(b, GameFolderResolver.ForGuardian(null, b));
        Assert.AreEqual(b, GameFolderResolver.ForGuardian(TestDirs.NewPath("bad"), b));
        Assert.AreEqual(CompanionPaths.DefaultGameDir, GameFolderResolver.ForGuardian(null, TestDirs.NewPath("gone")));
    }

    [TestMethod]
    public void TheMismatchHint_NeedsARunningGameInAnotherFolder()
    {
        Assert.IsFalse(GameFolderResolver.IsOtherGameFolder(null, @"D:\Games\TOS"));
        Assert.IsFalse(GameFolderResolver.IsOtherGameFolder(@"d:\games\tos\", @"D:\Games\TOS"));
        Assert.IsTrue(GameFolderResolver.IsOtherGameFolder(@"E:\TOS", @"D:\Games\TOS"));
    }
}
