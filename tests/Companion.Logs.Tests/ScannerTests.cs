using System.Text;
using Companion.Contracts.Logs;
using static Companion.Logs.Tests.Synthetic;

namespace Companion.Logs.Tests;

/// <summary>Entry splitting (SPEC 6.1): partial entries, every byte boundary, multibyte UTF-8, CRLF, orphans, caps.</summary>
[TestClass]
public sealed class ScannerTests
{
    private static byte[] AllFixtures() => Fixtures.All.SelectMany(Fixtures.Load).ToArray();

    [TestMethod]
    public void SplitAtEveryByteBoundary_GivesTheSameEventsAsOneRead()
    {
        var bytes = AllFixtures();
        var expected = Parsing.All(bytes);
        Assert.IsGreaterThan(100, expected.Count);
        var parser = new LogEntryParser(new FakeHasher());

        for (var k = 0; k <= bytes.Length; k++)
        {
            var first = EntryScanner.Scan(bytes, k, 0, capped: false);
            Assert.IsTrue(first.NextOffset <= k, $"split {k}: consumed past the data");
            var events = first.Entries.Select(e => parser.Parse(e, Parsing.FixtureFile)).OfType<LogEvent>().ToList();
            var rest = bytes[(int)first.NextOffset..];
            var second = EntryScanner.Scan(rest, rest.Length, first.NextOffset, capped: false);
            events.AddRange(second.Entries.Select(e => parser.Parse(e, Parsing.FixtureFile)).OfType<LogEvent>());
            if (!expected.SequenceEqual(events))
            {
                Assert.Fail($"split at byte {k}: {events.Count} events instead of {expected.Count}");
            }
        }
    }

    [TestMethod]
    public void PartialLastLine_AndUnterminatedEntry_AreNotConsumed()
    {
        var entry = Bytes(Entry(T0, Show("UI_BAG_MAIN")));
        var withoutBlank = entry[..^2];          // "...UI_BAG_MAIN\r\n" (no blank terminator yet)
        var scan = EntryScanner.Scan(withoutBlank, withoutBlank.Length, 100, capped: false);
        Assert.HasCount(0, scan.Entries);
        Assert.AreEqual(100L, scan.NextOffset);

        var halfCrlf = entry[..^1];              // "...\r\n\r" (blank line without its LF)
        scan = EntryScanner.Scan(halfCrlf, halfCrlf.Length, 0, capped: false);
        Assert.HasCount(0, scan.Entries);
        Assert.AreEqual(0L, scan.NextOffset);

        scan = EntryScanner.Scan(entry, entry.Length, 0, capped: false);
        Assert.HasCount(1, scan.Entries);
        Assert.AreEqual((long)entry.Length, scan.NextOffset);
    }

    [TestMethod]
    public void NextTimestampedLine_EndsAnEntryWithoutBlankLine()
    {
        var text = $"{Ts(T0)}\tLog\t{Show("UI_MAIL")}\r\n{Ts(T0)}\tLog\t{HideTop("UI_MAIL")}\r\n";
        var bytes = Bytes(text);
        var scan = EntryScanner.Scan(bytes, bytes.Length, 0, capped: false);
        Assert.HasCount(1, scan.Entries);
        var firstLength = Encoding.UTF8.GetByteCount($"{Ts(T0)}\tLog\t{Show("UI_MAIL")}\r\n");
        Assert.AreEqual((long)firstLength, scan.Entries[0].End);
        Assert.AreEqual((long)firstLength, scan.NextOffset, "the second entry is still open");
    }

    [TestMethod]
    public void OrphanLines_AreConsumedAndDropped()
    {
        var text = "|\torphan pipe\r\nuntimestamped dump line\r\n\r\n" + Entry(T0, Show("UI_MAIL")) + "trailing dump\r\n";
        var bytes = Bytes(text);
        var scan = EntryScanner.Scan(bytes, bytes.Length, 0, capped: false);
        Assert.HasCount(1, scan.Entries);
        Assert.AreEqual((long)bytes.Length, scan.NextOffset);
    }

    [TestMethod]
    public void MultibyteUtf8_SplitInsideTheCharacter_DecodesOnlyWhenComplete()
    {
        var text = Entry(T0, ToHide("UI_GAMEPLAY_COUNT_DOWN"));
        var bytes = Bytes(text);
        var marker = Encoding.UTF8.GetBytes("【");
        var at = bytes.AsSpan().IndexOf(marker);
        Assert.IsGreaterThan(0, at);
        for (var k = at; k <= at + marker.Length; k++)
        {
            var scan = EntryScanner.Scan(bytes, k, 0, capped: false);
            Assert.HasCount(0, scan.Entries);
            Assert.AreEqual(0L, scan.NextOffset);
        }

        var all = Parsing.All(bytes);
        Assert.AreEqual(UiVerb.ToHide, ((UiLogEvent)all.Single()).Verb);
    }

    [TestMethod]
    public void LfOnlyLineEndings_AlsoWork()
    {
        var text = Entry(T0, Show("UI_MAIL")).Replace("\r\n", "\n", StringComparison.Ordinal)
            + Entry(T0, FocusRecover, FocusPipes).Replace("\r\n", "\n", StringComparison.Ordinal);
        var events = Parsing.All(Bytes(text));
        Assert.HasCount(2, events);
        Assert.AreEqual("UI_MAIL", ((UiLogEvent)events[0]).UiName);
    }

    [TestMethod]
    public void CappedRead_WithOneGiantOpenEntry_StillMakesProgress()
    {
        var sb = new StringBuilder($"{Ts(T0)}\tLog\t[LOG] -> some/dump.lua:1    giant dump\r\n");
        for (var i = 0; i < 200; i++)
        {
            sb.Append("|\tdump line ").Append(i).Append("\r\n");
        }

        var bytes = Bytes(sb.ToString());
        var uncapped = EntryScanner.Scan(bytes, bytes.Length, 0, capped: false);
        Assert.AreEqual(0L, uncapped.NextOffset);
        var capped = EntryScanner.Scan(bytes, bytes.Length, 0, capped: true);
        Assert.AreEqual((long)bytes.Length, capped.NextOffset);
        Assert.HasCount(0, capped.Entries);
    }

    [TestMethod]
    public void TimestampShape_IsCheckedByteByByte()
    {
        Assert.AreEqual(28, EntryScanner.TimestampedMessageStart("2026-10-13 23:03:42.314\tLog\tx"u8));
        Assert.AreEqual(32, EntryScanner.TimestampedMessageStart("2026-10-13 23:03:42.314\tWarning\tx"u8));
        Assert.AreEqual(0, EntryScanner.TimestampedMessageStart("2026-10-13 23:03:42.31\tLog\tx"u8));
        Assert.AreEqual(0, EntryScanner.TimestampedMessageStart("2026-10-13 23:03:42.314 Log\tx"u8));
        Assert.AreEqual(0, EntryScanner.TimestampedMessageStart("2026-10-13 23:03:42.314\t\tx"u8));
        Assert.AreEqual(0, EntryScanner.TimestampedMessageStart("|\t2026-10-13 23:03:42.314\tLog\tx"u8));
        Assert.IsNull(EntryScanner.ParseTimestamp("2026-13-13 23:03:42.314"u8));
        Assert.AreEqual(new DateTime(2026, 10, 13, 23, 3, 42, 314), EntryScanner.ParseTimestamp("2026-10-13 23:03:42.314"u8));
    }
}
