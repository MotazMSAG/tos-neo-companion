using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Vision;
using Companion.Features.Kit;
using Companion.Vision.Internal;

namespace Companion.Features.Tests;

/// <summary>
/// The list scan's image checks on the real captures (read-only, the tests' own copies in tests\TestData\images):
/// live_ability_make_full.png (full scale, 2026-10-14, list scrolled 31 px from its top: 9 favourites, the Recent row),
/// make_fav5_full.png (full scale, 2026-10-15 01:46, list at its top, Vanilla Cream selected) and the half-scale pair of
/// proof p0_c14 taken 2026-10-15 02:15 (c14_before.png at the top of the list, the same view as make_fav5_full.png, then
/// c14_after.png after a 300 px drag up that moved the list 314 px).
/// </summary>
[TestClass]
public sealed class RecipeListImageTests
{
    /// <summary>
    /// tests\TestData\images: byte-identical copies of the captures (provenance in its README.md), so a proof run that
    /// rewrites proofs\out never changes what these checks see.
    /// </summary>
    private static readonly Lazy<string> ImagesDir = new(() => Path.Combine(StaticRuleTests.CompanionRoot(), "tests", "TestData", "images"));

    private static readonly Dictionary<string, Frame> Cache = new(StringComparer.Ordinal);

    public TestContext TestContext { get; set; } = null!;

    internal static Frame Capture(string name)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(name, out var f))
            {
                var layout = name.StartsWith("c14_", StringComparison.Ordinal) ? SourceLayout.ClientHalf : SourceLayout.ClientFull;
                var path = Path.Combine(ImagesDir.Value, name);
                if (!File.Exists(path))
                {
                    Assert.Inconclusive("private capture not included in the public repository: " + name);
                }

                f = SourceImages.ToFrame(File.ReadAllBytes(path), layout);
                Cache[name] = f;
            }

            return f;
        }
    }

    private void Dump(string name, IReadOnlyList<ListCard> cards) =>
        TestContext.WriteLine(name + ": " + string.Join(" ", cards.Select(c => $"c{c.Column + 1}@({c.Center.X},{c.Center.Y})[{c.Top}..{c.Bottom}]")));

    [TestMethod]
    public void FullScale_TheFourteenCardsAreFound_AtTheFavouriteSlotCentres()
    {
        var cards = RecipeList.FindCards(Capture("live_ability_make_full.png"));
        Dump("full", cards);
        Assert.HasCount(14, cards, "9 favourites (the 10th cell is empty) and the 5 Recent cards");

        // The favourites slots CraftLoop clicks (anchor + offset) are these cards' centres.
        for (var c = 0; c < 5; c++)
        {
            AssertCard(cards, c, 364 + (113 * c), 498);
            if (c < 4)
            {
                AssertCard(cards, c, 364 + (113 * c), 638);
            }

            AssertCard(cards, c, 364 + (113 * c), 855, tolerance: 4);
        }

        Assert.IsFalse(cards.Any(k => k.Column == 4 && Math.Abs(k.Center.Y - 638) < 40), "the empty 10th favourite cell is no card");
        Assert.IsTrue(cards.All(k => RecipeList.Clickable(k.Center)), "every card centre is well inside the list");
    }

    private static void AssertCard(IReadOnlyList<ListCard> cards, int column, int x, int y, int tolerance = 2)
    {
        Assert.IsTrue(cards.Any(k => k.Column == column && k.Center.X == x && Math.Abs(k.Center.Y - y) <= tolerance), $"card in column {column + 1} near ({x},{y})");
    }

    /// <summary>Two cards are the same list position when their content rows differ by at most this (ScanRecipeList's rule).</summary>
    private const int SamePositionPx = 20;

    [TestMethod]
    public void HalfScale_TheC14DragIsMeasured_AndEveryCardAfterIsNew_StartingWithTheRowCutBefore()
    {
        var before = Capture("c14_before.png");
        var after = Capture("c14_after.png");
        var b = RecipeList.FindCards(before);
        var a = RecipeList.FindCards(after);
        Dump("before", b);
        Dump("after", a);
        Assert.HasCount(9, b, "the two favourites rows (the 10th cell is empty); the Recent row is cut at the bottom");
        Assert.HasCount(10, a, "the two Recent rows; the Recovery Dish row is cut at the bottom");
        Assert.AreEqual(2, b.Select(k => k.Top).Distinct().Count());
        Assert.AreEqual(2, a.Select(k => k.Top).Distinct().Count());

        // Read on the frames: the first Recent row is at y ~886 before (cut by the list's bottom at 903) and ~573 after.
        var shift = RecipeList.MeasureShift(before, after);
        TestContext.WriteLine(shift.ToString());
        Assert.IsTrue(shift.Known && Math.Abs(shift.Dy + 314) <= 3, shift.ToString());
        Assert.IsFalse(shift.Unchanged);

        // Every card read before left the list at the top (cut there or gone), so none is found again after the drag:
        // all 10 cards after it are new list positions.
        foreach (var old in b)
        {
            Assert.IsTrue(old.Top + shift.Dy < RecipeList.Viewport.Top, $"column {old.Column + 1} at y {old.Center.Y} moved out at the top");
        }

        Assert.AreEqual(10, a.Count(k => !b.Any(o => o.Column == k.Column && Math.Abs(k.Center.Y - shift.Dy - o.Center.Y) <= SamePositionPx)));

        // Nothing is skipped: the first row after the drag is the Recent row the before capture showed cut at the bottom
        // (its top inside the list, its bottom past it), and it lies as far under the last row read before as the
        // Recent row lies under the second favourites row on the full-scale capture (638 -> 855).
        var first = a.Where(k => k.Top == a.Min(c => c.Top)).ToList();
        Assert.HasCount(5, first);
        foreach (var k in first)
        {
            Assert.IsTrue(k.Top - shift.Dy < RecipeList.Viewport.Bottom && k.Bottom - shift.Dy > RecipeList.Viewport.Bottom, $"column {k.Column + 1}: [{k.Top - shift.Dy}..{k.Bottom - shift.Dy}] before the drag");
        }

        var full = RecipeList.FindCards(Capture("live_ability_make_full.png"));
        var sectionGap = full.Max(k => k.Center.Y) - full.Where(k => k.Center.Y < 800).Max(k => k.Center.Y);
        Assert.AreEqual(855 - 638, sectionGap, 2, "favourites row 2 -> Recent row on the full-scale capture");
        Assert.AreEqual(sectionGap, first[0].Center.Y - shift.Dy - b.Max(k => k.Center.Y), 3, "the measured shift puts the Recent row right under the favourites");
    }

    [TestMethod]
    public void TheSameListPosition_IsUnchanged_AcrossCapturesAndScales()
    {
        var before = Capture("c14_before.png");
        Assert.IsTrue(RecipeList.MeasureShift(before, before).Unchanged);

        // make_fav5_full.png (full scale, 01:46) and c14_before (half scale, 02:15) both show the top of the list with
        // Vanilla Cream selected: unchanged either way round, and the same cards at the same places.
        var full = Capture("make_fav5_full.png");
        var same = RecipeList.MeasureShift(full, before);
        TestContext.WriteLine(same.ToString());
        Assert.IsTrue(same.Unchanged, "make_fav5_full and c14_before both show the top of the list: " + same);
        Assert.IsTrue(RecipeList.MeasureShift(before, full).Unchanged);
        var f = RecipeList.FindCards(full);
        var b = RecipeList.FindCards(before);
        Dump("full", f);
        Assert.HasCount(f.Count, b);
        Assert.IsTrue(f.Zip(b).All(p => p.First.Column == p.Second.Column && Math.Abs(p.First.Center.Y - p.Second.Center.Y) <= 2), "same cards, same places");

        // A list scrolled 31 px further is no "same position": live_ability_make_full.png (2026-10-14) shows the
        // favourites 31 px higher (first row y 498, here 529), and the measured shift is exactly that.
        var live = Capture("live_ability_make_full.png");
        var lower = RecipeList.MeasureShift(live, before);
        TestContext.WriteLine(lower.ToString());
        Assert.IsTrue(lower.Known && !lower.Unchanged, lower.ToString());
        Assert.AreEqual(b[0].Center.Y - RecipeList.FindCards(live)[0].Center.Y, lower.Dy, 2, lower.ToString());
        Assert.AreEqual(31, lower.Dy, 2, lower.ToString());

        Assert.IsFalse(RecipeList.MeasureShift(full, full with { Width = 10 }).Known, "an invalid frame is never a measurement");
    }

    private static readonly string[] TopOfList = ["make_fav1_whitetoast_full.png", "make_fav2_full.png", "make_fav3_full.png", "make_fav4_full.png", "make_fav5_full.png"];

    [TestMethod]
    public void CardPictures_MatchTheSameCardStrictly_AcrossCapturesScrollAndSelection_AndNeverAnotherProduct()
    {
        // The nine favourites of make_fav5_full.png learned as nine products (ids 1..9, row by row).
        var reference = Capture("make_fav5_full.png");
        var favourites = RecipeList.FindCards(reference);
        Assert.HasCount(9, favourites);
        var learned = favourites.Select((c, i) => new IconItem(RecipeList.CardKey(reference, c)!, i + 1, "Dish " + (i + 1), DateTimeOffset.UnixEpoch)).ToList();
        Assert.IsTrue(learned.All(i => IconKeys.IsIconKey(i.IconKey)));

        // The same list position in the four other top-of-list captures (another card selected each time): the same product.
        foreach (var file in TopOfList.Take(4))
        {
            var f = Capture(file);
            var cards = RecipeList.FindCards(f);
            Assert.HasCount(9, cards, file);
            for (var i = 0; i < 9; i++)
            {
                var m = RecipeList.MatchCard(RecipeList.CardKeys(f, cards[i]), learned);
                Assert.AreEqual(("known", (long)(i + 1)), (m.Code, m.Item?.ItemId), $"{file} card {i + 1}: {m}");
            }
        }

        // live_ability_make_full.png: the list 31 px further (another day, another scroll phase), and a Recent row.
        var live = Capture("live_ability_make_full.png");
        var liveCards = RecipeList.FindCards(live);
        for (var i = 0; i < 9; i++)
        {
            var m = RecipeList.MatchCard(RecipeList.CardKeys(live, liveCards[i]), learned);
            TestContext.WriteLine($"live favourite {i + 1}: {m.Code} {m.Distance}");
            Assert.AreEqual(("known", (long)(i + 1)), (m.Code, m.Item?.ItemId), $"live favourite {i + 1}: {m}");
            Assert.IsTrue(m.Distance <= 9, $"scrolled: {m.Distance} bits at the best row");
        }

        // Recent (2026-10-14): pink drink, purple dish, White Toast, fish soup, cookies: only White Toast is a favourite.
        var recent = liveCards.Where(c => c.Center.Y > 800).OrderBy(c => c.Column).ToList();
        Assert.HasCount(5, recent);
        var codes = recent.Select(c => RecipeList.MatchCard(RecipeList.CardKeys(live, c), learned)).ToList();
        TestContext.WriteLine(string.Join(" ", codes.Select(m => $"{m.Code}/{m.Distance}")));
        Assert.AreEqual(("known", 1L), (codes[2].Code, codes[2].Item?.ItemId), "White Toast in Recent is the White Toast favourite");
        Assert.IsTrue(codes.Where((_, k) => k != 2).All(m => m.Code == "unknown" && m.Distance > 2 * RecipeList.CardSameMaxBits),
            "other products are far from every learned one (the pink drink from the orange one too): selected and read");

        // A look-alike learned under another product within the margin: ambiguous, never a guess.
        var bytes = Convert.FromHexString(learned[0].IconKey[5..]);
        bytes[3] ^= 0x01;
        var withRival = learned.Append(new IconItem("icon:" + Convert.ToHexString(bytes).ToLowerInvariant(), 99, "Look-alike", DateTimeOffset.UnixEpoch)).ToList();
        Assert.AreEqual("ambiguous", RecipeList.MatchCard(RecipeList.CardKeys(reference, favourites[0]), withRival).Code);
    }

    [TestMethod]
    public void TheSelectedCard_IsTheOneWithTheMintFrame_OnEveryCapture()
    {
        var selected = new Dictionary<string, int>
        {
            ["make_fav1_whitetoast_full.png"] = 0,
            ["make_fav2_full.png"] = 1,
            ["make_fav3_full.png"] = 2,
            ["make_fav4_full.png"] = 3,
            ["make_fav5_full.png"] = 4,
            ["live_ability_make_full.png"] = 0,
        };
        foreach (var (file, index) in selected)
        {
            var f = Capture(file);
            var cards = RecipeList.FindCards(f);
            var marked = cards.Select((c, i) => (c, i)).Where(x => RecipeList.IsSelected(f, x.c)).Select(x => x.i).ToList();
            CollectionAssert.AreEqual(new[] { index }, marked.ToArray(), file);
        }
    }

    [TestMethod]
    public void TheScanProcedure_PressesAndReleasesInsideTheList_InTheGapOfColumnsOneAndTwo()
    {
        var (_, env, _) = T.LoadReal();
        var p = env.Procedures.Get("crafting.scanList")!;
        foreach (var id in new[] { "top", "scroll" })
        {
            var d = (DragAction)p.Steps.Single(s => s.Id == id).Action;
            Assert.AreEqual(RecipeList.AnchorTemplate, d.TemplateId);
            var press = RecipeList.AnchorAt.Offset(d.Offset.X, d.Offset.Y);
            var release = press.Offset(d.Delta.X, d.Delta.Y);
            Assert.IsTrue(RecipeList.Clickable(press) && RecipeList.Clickable(release), $"{id}: {press} -> {release}");
            Assert.AreEqual(420, press.X, "between the cards of columns 1 and 2 (proof c14 pressed at x 420)");
            Assert.AreEqual(2 * RecipeList.RowPitch, Math.Abs(d.Delta.Y), "two rows per drag");
        }

        Assert.IsTrue(((DragAction)p.Steps.Single(s => s.Id == "top").Action).Delta.Y > 0, "to the top: the content moves down");
        Assert.IsTrue(((DragAction)p.Steps.Single(s => s.Id == "scroll").Action).Delta.Y < 0, "scrolling on: the content moves up");
        var card = (ClickAction)p.Steps.Single(s => s.Id == "card").Action;
        Assert.AreEqual((RecipeList.AnchorTemplate, new PointI(364, 498)), (card.TemplateId, RecipeList.AnchorAt.Offset(card.Offset!.Value.X, card.Offset.Value.Y)), "the data's card offset is the first card; the job replaces it");
    }
}
