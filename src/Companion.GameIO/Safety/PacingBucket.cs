using Companion.Contracts;
using Companion.Contracts.GameIo;
using Companion.Contracts.Safety;

namespace Companion.GameIO.Safety;

/// <summary>
/// Human pace at the choke point (R7, PACE-7), limits from <see cref="HardRules"/>: at least
/// <see cref="HardRules.MinGapBetweenInputBatches"/> between input batches, at most
/// <see cref="HardRules.MaxInputBatchesPerMinute"/> batches and <see cref="HardRules.MaxSlidesPerMinute"/> slides per
/// rolling minute, <see cref="HardRules.MaxInputBatchesPerHour"/> batches per rolling hour and
/// <see cref="HardRules.MaxFocusPairsPerMinute"/> focus pairs per minute (raised on purpose 2026-10-14 to about one click
/// every 3 s, 2026-10-15 to about one every 1.5 s: human browsing speed). A commit input (the Make button and the other
/// targets of a commit round) keeps the 2026-10-14 pace: it starts only while every input of the rolling windows stays
/// under the commit caps (<see cref="HardRules.MaxCommitInputBatchesPerMinute"/>,
/// <see cref="HardRules.MaxCommitSlidesPerMinute"/>, <see cref="HardRules.MaxCommitInputBatchesPerHour"/>,
/// <see cref="HardRules.MaxCommitFocusPairsPerMinute"/>). A key, a slide click and a slide drag are one batch each; clicks
/// and drags are one slide each. Uses <see cref="IClock.Monotonic"/> only. Thread-safe.
/// </summary>
internal sealed class PacingBucket(IClock clock)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly object _lock = new();
    private readonly Queue<TimeSpan> _batches = new();
    private readonly Queue<TimeSpan> _slides = new();
    private readonly Queue<TimeSpan> _focusPairs = new();
    private TimeSpan? _lastBatch;

    /// <summary>Time until a batch of this kind may start (zero = now); a <paramref name="commit"/> input at the commit pace.</summary>
    public TimeSpan NextAllowedIn(bool slide, bool focusPair, bool commit = false)
    {
        lock (_lock)
        {
            var now = clock.Monotonic;
            Trim(now);
            return Wait(now, slide, focusPair, commit);
        }
    }

    /// <summary>
    /// Takes a token if available (records the batch, slide and focus pair); a <paramref name="commit"/> input at the
    /// commit pace. False = rate limited, nothing recorded.
    /// </summary>
    public bool TryAcquire(bool slide, bool focusPair, bool commit = false)
    {
        lock (_lock)
        {
            var now = clock.Monotonic;
            Trim(now);
            if (Wait(now, slide, focusPair, commit) > TimeSpan.Zero)
            {
                return false;
            }

            _lastBatch = now;
            _batches.Enqueue(now);
            if (slide)
            {
                _slides.Enqueue(now);
            }

            if (focusPair)
            {
                _focusPairs.Enqueue(now);
            }

            return true;
        }
    }

    /// <summary>Snapshot for <see cref="IGameIo.Pacing"/>.</summary>
    public PacingStatus Status()
    {
        lock (_lock)
        {
            var now = clock.Monotonic;
            Trim(now);
            var lastMinute = _batches.Count(t => now - t < Minute);
            return new PacingStatus(Wait(now, slide: false, focusPair: false, commit: false), lastMinute, _slides.Count, _batches.Count, _focusPairs.Count);
        }
    }

    private TimeSpan Wait(TimeSpan now, bool slide, bool focusPair, bool commit)
    {
        var (batchesPerMinute, slidesPerMinute, batchesPerHour, pairsPerMinute) = commit
            ? (HardRules.MaxCommitInputBatchesPerMinute, HardRules.MaxCommitSlidesPerMinute, HardRules.MaxCommitInputBatchesPerHour, HardRules.MaxCommitFocusPairsPerMinute)
            : (HardRules.MaxInputBatchesPerMinute, HardRules.MaxSlidesPerMinute, HardRules.MaxInputBatchesPerHour, HardRules.MaxFocusPairsPerMinute);
        var wait = TimeSpan.Zero;
        if (_lastBatch is { } last)
        {
            wait = Max(wait, last + HardRules.MinGapBetweenInputBatches - now);
        }

        var inMinute = _batches.Where(t => now - t < Minute).ToList();
        if (inMinute.Count >= batchesPerMinute)
        {
            wait = Max(wait, inMinute[inMinute.Count - batchesPerMinute] + Minute - now);
        }

        if (_batches.Count >= batchesPerHour)
        {
            wait = Max(wait, _batches.ElementAt(_batches.Count - batchesPerHour) + Hour - now);
        }

        if (slide && _slides.Count >= slidesPerMinute)
        {
            wait = Max(wait, _slides.ElementAt(_slides.Count - slidesPerMinute) + Minute - now);
        }

        if (focusPair && _focusPairs.Count >= pairsPerMinute)
        {
            wait = Max(wait, _focusPairs.ElementAt(_focusPairs.Count - pairsPerMinute) + Minute - now);
        }

        return wait;
    }

    private void Trim(TimeSpan now)
    {
        while (_batches.Count > 0 && now - _batches.Peek() >= Hour)
        {
            _batches.Dequeue();
        }

        while (_slides.Count > 0 && now - _slides.Peek() >= Minute)
        {
            _slides.Dequeue();
        }

        while (_focusPairs.Count > 0 && now - _focusPairs.Peek() >= Minute)
        {
            _focusPairs.Dequeue();
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
