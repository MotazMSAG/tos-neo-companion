namespace Companion.Feature.TeaHouse;

/// <summary>What the job did with one press.</summary>
/// <param name="Done">True when the game logged the press on the wanted square (or the Restart closed the end screen).</param>
/// <param name="Code">Plain code: "pressed", "restarted", "notOnBoard:&lt;top ui&gt;", "press.notLogged", a procedure's code...</param>
/// <param name="Piece">The game's number of the piece in hand when the press was logged, else null.</param>
public sealed record TeaPress(bool Done, string Code, int? Piece = null);

/// <summary>One press handed to the job: its id, the square and the token that withdraws it.</summary>
/// <param name="Id">Order id (answers carry it).</param>
/// <param name="Square">0 the tray .. 35, or <see cref="TeaHouseDesk.Restart"/>.</param>
/// <param name="Withdrawn">Cancelled when auto-play goes off or the helper stops waiting: the press must not happen later.</param>
public readonly record struct TeaOrder(int Id, int Square, CancellationToken Withdrawn);

/// <summary>
/// The Kitty Tea House helper's orders to the TeaHousePlay job and the job's answers (2026-10-30). Both live in the tray
/// app's process: the helper posts one press and waits for its answer, the job (engine thread) takes it, presses and
/// answers. One order at a time; an order is withdrawn (its token cancelled) when auto-play goes off or the helper
/// stops waiting, so a press the engine held up never lands late (safety review 2026-10-30). Nothing here touches the
/// game.
/// </summary>
public sealed class TeaHouseDesk
{
    private readonly object _gate = new();
    private (int Id, int Square, CancellationTokenSource Withdraw)? _order;
    private (int Id, CancellationTokenSource Withdraw)? _taken;
    private (int Id, TeaPress Press)? _answer;
    private int _nextId;
    private volatile bool _autoPlay;
    private volatile bool _jobRunning;

    /// <summary>The square number of the end screen's Restart button in an order.</summary>
    public const int Restart = -1;

    /// <summary>How long the helper waits for the answer of a press it withdrew.</summary>
    public static readonly TimeSpan WithdrawWait = TimeSpan.FromSeconds(30);

    /// <summary>The helper's auto-play switch: off withdraws the waiting and the running press, and the job ends.</summary>
    public bool AutoPlay
    {
        get => _autoPlay;
        set
        {
            lock (_gate)
            {
                _autoPlay = value;
                if (!value)
                {
                    _order?.Withdraw.Cancel();
                    _order = null;
                    _taken?.Withdraw.Cancel();
                }

                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>True while the TeaHousePlay job runs.</summary>
    public bool JobRunning => _jobRunning;

    /// <summary>
    /// Helper side: posts a press on <paramref name="square"/> (0 the tray .. 35, or <see cref="Restart"/>) and waits for
    /// the job's answer. After <paramref name="timeout"/> the press is withdrawn: a press no job took returns null at
    /// once; a press in progress is cancelled and its answer awaited (at most <see cref="WithdrawWait"/>).
    /// </summary>
    public TeaPress? Press(int square, TimeSpan timeout, CancellationToken ct)
    {
        if (square is < Restart or > 35)
        {
            throw new ArgumentOutOfRangeException(nameof(square));
        }

        var deadline = DateTime.UtcNow + timeout;
        lock (_gate)
        {
            var id = ++_nextId;
            var withdraw = new CancellationTokenSource();
            _order = (id, square, withdraw);
            _answer = null;
            Monitor.PulseAll(_gate);
            var withdrawn = false;
            while (true)
            {
                if (_answer is { } a && a.Id == id)
                {
                    _answer = null;
                    return a.Press;
                }

                if (!withdrawn && (DateTime.UtcNow >= deadline || ct.IsCancellationRequested || !_autoPlay))
                {
                    withdrawn = true;
                    withdraw.Cancel();
                    if (_order is { } o && o.Id == id)
                    {
                        _order = null;
                        return null;
                    }

                    deadline = DateTime.UtcNow + WithdrawWait;
                }
                else if (withdrawn && DateTime.UtcNow >= deadline)
                {
                    return null;
                }

                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(200));
            }
        }
    }

    /// <summary>Job side: the next order, or null after <paramref name="wait"/> or when auto-play is off.</summary>
    internal TeaOrder? Next(TimeSpan wait, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + wait;
        lock (_gate)
        {
            while (_order is null && _autoPlay && !ct.IsCancellationRequested)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero)
                {
                    return null;
                }

                Monitor.Wait(_gate, left < TimeSpan.FromMilliseconds(200) ? left : TimeSpan.FromMilliseconds(200));
            }

            if (_order is not { } order || !_autoPlay || order.Withdraw.IsCancellationRequested)
            {
                return null;
            }

            _order = null;
            _taken = (order.Id, order.Withdraw);
            return new TeaOrder(order.Id, order.Square, order.Withdraw.Token);
        }
    }

    /// <summary>Job side: the answer to order <paramref name="id"/>.</summary>
    internal void Answer(int id, TeaPress press)
    {
        lock (_gate)
        {
            if (_taken is { } t && t.Id == id)
            {
                _taken = null;
            }

            _answer = (id, press);
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Job side: marks the job running or ended.</summary>
    internal void SetRunning(bool running) => _jobRunning = running;
}
