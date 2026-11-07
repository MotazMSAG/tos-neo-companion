using Companion.Contracts.Logs;

namespace Companion.Logs;

/// <summary>
/// In-memory buffer of recent events of the current generation (SPEC 6.1: last 15 min or 50 000 events), ordered by
/// end offset so range queries are binary searches. Age is measured with the monotonic clock at arrival. Not
/// thread-safe: the tracker serialises access.
/// </summary>
internal sealed class EventRing
{
    internal const int DefaultCapacity = 50_000;
    internal static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(15);

    private readonly int _capacity;
    private readonly TimeSpan _maxAge;
    private readonly List<(LogEvent Event, TimeSpan Arrived)> _items = [];
    private int _head;

    internal EventRing(int capacity, TimeSpan maxAge)
    {
        _capacity = Math.Max(1, capacity);
        _maxAge = maxAge;
    }

    internal int Count => _items.Count - _head;

    internal void Clear()
    {
        _items.Clear();
        _head = 0;
    }

    internal void Add(LogEvent e, TimeSpan now)
    {
        _items.Add((e, now));
        Evict(now);
    }

    /// <summary>Drops events older than the maximum age or beyond the capacity.</summary>
    internal void Evict(TimeSpan now)
    {
        while (Count > 0 && (Count > _capacity || now - _items[_head].Arrived > _maxAge))
        {
            _head++;
        }

        if (_head > 4096 && _head > _items.Count / 2)
        {
            _items.RemoveRange(0, _head);
            _head = 0;
        }
    }

    /// <summary>Events with fromOffset &lt; end offset &lt;= toOffset, in order.</summary>
    internal IReadOnlyList<LogEvent> Between(long fromOffset, long toOffset)
    {
        if (toOffset <= fromOffset || Count == 0)
        {
            return [];
        }

        var i = FirstAfter(fromOffset);
        var result = new List<LogEvent>();
        for (; i < _items.Count; i++)
        {
            var e = _items[i].Event;
            if (e.Position.Offset > toOffset)
            {
                break;
            }

            result.Add(e);
        }

        return result;
    }

    /// <summary>Index of the first event whose end offset is &gt; <paramref name="offset"/>.</summary>
    private int FirstAfter(long offset)
    {
        int lo = _head, hi = _items.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (_items[mid].Event.Position.Offset > offset)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo;
    }
}
