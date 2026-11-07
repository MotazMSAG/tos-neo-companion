using System.Diagnostics;
using Companion.Contracts;

namespace Companion.App.Hosting;

/// <summary>
/// Production clock (SPEC 3, F19/PITFALL-12): wall time from the system, monotonic time from the high-resolution
/// Stopwatch counter (never jumps), cancellable sleeps.
/// </summary>
internal sealed class SystemClock : IClock
{
    private static readonly TimeSpan MaxWaitChunk = TimeSpan.FromMilliseconds(int.MaxValue - 1);
    private readonly long _origin = Stopwatch.GetTimestamp();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public TimeSpan Monotonic => Stopwatch.GetElapsedTime(_origin);

    /// <inheritdoc />
    public void Sleep(TimeSpan duration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var remaining = duration;
        while (remaining > TimeSpan.Zero)
        {
            var chunk = remaining > MaxWaitChunk ? MaxWaitChunk : remaining;
            if (ct.CanBeCanceled)
            {
                if (ct.WaitHandle.WaitOne(chunk))
                {
                    ct.ThrowIfCancellationRequested();
                }
            }
            else
            {
                Thread.Sleep(chunk);
            }

            remaining -= chunk;
        }
    }
}
