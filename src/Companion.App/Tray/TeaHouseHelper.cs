using System.Globalization;
using System.Text.RegularExpressions;
using Companion.App.Hosting;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Perception;
using Companion.Contracts.Vision;
using Companion.Feature.TeaHouse;
using Companion.TeaHouse;

namespace Companion.App.Tray;

/// <summary>What the helper window shows after one look.</summary>
/// <param name="Status">A sentence for the user (what the helper is doing, or why it waits).</param>
/// <param name="Frame">The capture the board was read from (null while nothing was read).</param>
/// <param name="Reading">The reading (null while nothing was read).</param>
/// <param name="Advice">The options, best first (empty while there is nothing to advise).</param>
/// <param name="Score">The score read on screen, or null.</param>
/// <param name="Learned">The latest things learned, newest first.</param>
/// <param name="Counted">Current pieces counted so far.</param>
internal sealed record TeaHouseView(string Status, Frame? Frame, TeaReading? Reading, IReadOnlyList<TeaAdvice> Advice, double? Score, IReadOnlyList<string> Learned, long Counted)
{
    /// <summary>True while the helper waits for the user's move (it does not look at the game then).</summary>
    public bool Waiting { get; init; }

    /// <summary>True while it thinks: no advice is shown until the deepest look is done.</summary>
    public bool Thinking { get; init; }

    /// <summary>The round's outlook in words (<see cref="TeaForecast"/>), or null when there is none.</summary>
    public string? Outlook { get; init; }
}

/// <summary>
/// The Kitty Tea House helper's eyes (2026-10-29, the user: "Helper"): while its window is open and no job runs, it
/// looks at the game a few times a second (window captures only; the user places every piece unless auto-play is on,
/// 2026-10-30: then <see cref="TeaHouseHands"/> asks the TeaHousePlay job to press the final answer), reads the board (<see cref="TeaReader"/>), learns from each move (<see cref="TeaSession"/>), asks the move
/// finder (<see cref="TeaSolver"/>) and hands the result to the window. What it learns (piece pictures, points, piece
/// counts) is kept in RuntimeDir\teahouse\; moves, advice and rule mismatches go to the journal as codes and board
/// strings only. One instance per open window.
/// </summary>
internal sealed class TeaHouseHelper : IDisposable
{
    /// <summary>Frames per look (the pulsing preview needs a few, ~0.15 s apart).</summary>
    private const int FramesPerLook = 4;

    private static readonly TimeSpan FrameGap = TimeSpan.FromMilliseconds(80);

    /// <summary>Seconds from "I played" to a settled board, for the window's timing line.</summary>
    private double _readSeconds;
    private static readonly TimeSpan LookGap = TimeSpan.FromMilliseconds(150);

    private readonly HostServices _s;
    private readonly Func<bool> _jobRunning;
    private readonly TeaHouseHands? _hands;

    /// <summary>The Tea House round's end screen in the log (shown after the press that fills the board).</summary>
    private const string EndScreenUi = "UI_TRIPLE_TOWN_SETTLE";

    /// <summary>Auto-play stops after this many presses in a row that did not land.</summary>
    private const int MaxAutoFailures = 3;

    /// <summary>The chat panel, blacked out in any picture the helper keeps (the engine's capture rule).</summary>
    private static readonly RectI ChatPanel = new(660, 590, 1270, 1009);

    /// <summary>The top strip where the game's broadcast banners scroll, also blacked out in a kept picture.</summary>
    private static readonly RectI BannerStrip = new(0, 0, 1920, 140);

    private int _autoFailures;
    private bool _endSaved;

    private int _phantomShots;

    /// <summary>At most this many phantom-cat pictures are kept per helper window.</summary>
    private const int MaxPhantomShots = 6;

    private int _missShots;

    /// <summary>The Tea House page's static pictures the board presses are anchored on (feature teahouse's templates).</summary>
    private static readonly string[] Anchors = ["teahouse.title", "teahouse.score", "teahouse.current", "teahouse.frame.topright", "teahouse.frame.bottomright"];

    /// <summary>A note for the advice: the square the game's Cutlery hint hides (left out of the Cutlery's targets).</summary>
    private string? _hiddenNote;

    private TeaForecast? _forecast;
    private DateTime _forecastWritten;
    private double? _lastScore;
    private string? _outlook;

    /// <summary>The board and piece of the last press that landed: the same again means the press changed nothing.</summary>
    private string? _pressedKey;

    /// <summary>
    /// The move's last press while the engine finishes it (the next board is read meanwhile), with its move and whether the
    /// game had logged it when the reading started.
    /// </summary>
    private (Task<TeaPress> Press, TeaAdvice Move, bool Logged)? _inFlight;

    /// <summary>How long the helper waits for the game to log the move's last press before it reads the board anyway.</summary>
    private static readonly TimeSpan LoggedPressWait = TimeSpan.FromSeconds(3);
    private readonly Action<TeaHouseView> _publish;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly string _dir;
    private readonly List<string> _learned = [];
    private readonly TeaSession _session;
    private Task? _loop;
    private string? _lastAdviceKey;
    private IReadOnlyList<TeaAdvice> _advice = [];
    private bool _dirty;

    // the user (2026-10-29): "after recognizing a state, it shouldn't keep updating, it's a waste; I can click on the tool
    // to let it know I played, then it should recognize again; that way it can think deeper"
    private volatile bool _watching = true;
    private volatile bool _keepWatching;
    private string? _keyAtRequest;
    private DateTime _requestedUtc = DateTime.UtcNow;
    private CancellationTokenSource? _think;
    private (TeaBoard Board, TeaKind Current, TeaHouseView View)? _pendingThink;

    /// <summary>Settled board this long after "I played" is accepted even when unchanged (the click came early or by mistake).</summary>
    private static readonly TimeSpan SameBoardAfter = TimeSpan.FromSeconds(4);

    /// <summary>The deepest look while waiting (3 pieces ahead: ~1 s on 8 cores with the search's board cache).</summary>
    private const int DeepestLook = 4;

    /// <summary>
    /// With an Oven or Cutlery in hand or in the tray, one more piece ahead for the best two options (~15 s): such a
    /// choice is worth thousands of points (the user, 2026-10-29: "rules like Oven thinks deeper").
    /// </summary>
    private const int JokerLook = 5;

    /// <summary>Cores the move finder uses (half the PC's).</summary>
    private static readonly int Threads = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>A learned network (RuntimeDir\teahouse\net.bin, written by the trainer) is used once it has this many rounds.</summary>
    private const long NetMinRounds = 50_000;

    private TeaNet? _net;
    private DateTime _netWritten;
    private volatile bool _explore;
    private volatile bool _fast;

    /// <summary>
    /// Fast (2026-10-31, the user: "as fast as possible using our best brain ... no look ahead needed at all"): the learned
    /// brain picks each move from the board alone, the way it plays in training, and auto-play presses it at once (no
    /// deeper look, no joker look, no outlook: the forecast samples were made with a one-piece look).
    /// </summary>
    public bool Fast
    {
        get => _fast;
        set
        {
            _fast = value;
            lock (_gate)
            {
                _lastAdviceKey = null;
            }

            Journal("teahouse.fast", ("on", value));
            RequestRead();
        }
    }

    /// <summary>"Find out points": aim for the pieces whose points are not measured yet (the top cakes, the teas).</summary>
    public bool Explore
    {
        get => _explore;
        set
        {
            _explore = value;
            lock (_gate)
            {
                _lastAdviceKey = null;
            }

            RequestRead();
        }
    }

    /// <summary>Which brain advises, for the window ("learned from 1,234,567 practice rounds" or "hand-made").</summary>
    public string Brain => _explore ? "hand-made brain aiming for the highest cake (find out points)"
        : _net is { } quick && _fast ? $"learned brain ({quick.Trained:N0} practice rounds), fast: it picks each move from the board alone, no look-ahead"
        : _net is { } n ? $"learned brain ({n.Trained:N0} practice rounds; its training average was {n.RecentScore / 1000:0}k playing alone without looking ahead; the helper looks {DeepestLook - 1} pieces ahead, {JokerLook - 1} when keeping or using an Oven or Cutlery is a close call)" : "hand-made brain";

    /// <summary>The move finder's settings: the learned network when trained enough, "find out points" when on.</summary>
    private (TeaModel Model, TeaSolverOptions Options) Planning(int depth, int deepOptions, CancellationToken cancel)
    {
        ReloadNet();

        // "find out points" is a run for the highest cake (the user, 2026-10-29): the learned brain plays for the round's
        // score and knows nothing of the boost, so the hand-made weights, whose pair values follow the boosted points, steer
        var model = _explore ? _session.Model.ForExploring() : _session.Model;
        return (model, new TeaSolverOptions { Depth = depth, DeepOptions = deepOptions, Cancel = cancel, Net = _explore ? null : _net, Threads = Threads });
    }

    /// <summary>The gap between the best option spending the joker and the best keeping it, when under 600 points; else null.</summary>
    private static double? CloseKeepOrSpend(IReadOnlyList<TeaAdvice> found, TeaBoard board, TeaKind current)
    {
        var top = found.Count > 0 ? found[0].Depth : 0;
        TeaAdvice? spend = null;
        TeaAdvice? keep = null;
        foreach (var o in found.Where(o => o.Depth == top))
        {
            var spends = TeaSolver.IsJoker(o.Piece) && o.Action != TeaAction.Store;
            var keeps = (o.Action == TeaAction.Store && TeaSolver.IsJoker(current))
                || (o.Action == TeaAction.Swap && TeaSolver.IsJoker(current) && !TeaSolver.IsJoker(o.Piece))
                || (o.Action == TeaAction.Place && !TeaSolver.IsJoker(current) && TeaSolver.IsJoker(board.TrayPiece));
            spend ??= spends ? o : null;
            keep ??= keeps ? o : null;
        }

        return spend is not null && keep is not null && Math.Abs(spend.Value - keep.Value) < 600 ? Math.Abs(spend.Value - keep.Value) : null;
    }

    /// <summary>Loads net.bin when the trainer wrote a newer one.</summary>
    /// <summary>The forecast samples (teahouse\forecast.csv, made by ForecastProbe for the installed brain), reloaded when the file changes.</summary>
    private void ReloadForecast()
    {
        var path = Path.Combine(_dir, "forecast.csv");
        try
        {
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
            if (written == _forecastWritten)
            {
                return;
            }

            _forecastWritten = written;
            _forecast = written == default ? null : TeaForecast.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // being written: tried again next time
        }
    }

    /// <summary>
    /// A move adds at most one Orange Cat, yet live readings showed two where only one could be (2026-10-30, the user
    /// asked whether a "stayed put" Orange Cat could be another one jumping in): one is a phantom of the reading. When it
    /// happens again the squares read as Orange Cats (by picture or by colour, with their distance) go to the journal and
    /// two of the frames are kept (chat panel and banner strip blacked out, at most <see cref="MaxPhantomShots"/>) so the
    /// phantom can be seen and the reader fixed.
    /// </summary>
    private void PhantomCheck(TeaBoard before, TeaBoard after, TeaReading reading, IReadOnlyList<Frame> frames)
    {
        static int Oranges(TeaBoard b) => Enumerable.Range(1, TeaBoard.Squares - 1).Count(i => b[i] == TeaKind.OrangeCat);
        if (Oranges(after) <= Oranges(before) + 1)
        {
            return;
        }

        var squares = string.Join(",", Enumerable.Range(1, TeaBoard.Squares - 1).Where(i => after[i] == TeaKind.OrangeCat)
            .Select(i => $"{i}:{(reading.Squares[i].ByColour ? "colour" : "picture")}:{reading.Squares[i].Distance}:{(i == reading.Preview ? "preview" : "-")}"));
        string? shot = null;
        if (_phantomShots < MaxPhantomShots)
        {
            _phantomShots++;
            shot = $"phantom_{_phantomShots}";
            try
            {
                Directory.CreateDirectory(_dir);
                foreach (var (frame, tag) in new[] { (frames[0], "a"), (frames[^1], "b") })
                {
                    File.WriteAllBytes(Path.Combine(_dir, $"{shot}{tag}.png"), _s.Vision.EncodePng(frame, new RectI(0, 0, frame.Width, frame.Height), [ChatPanel, BannerStrip]));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                shot = null;
            }
        }

        Journal("teahouse.phantomCat", ("before", before.ToJournal()), ("after", after.ToJournal()), ("oranges", squares),
            ("preview", (long)reading.Preview), ("shot", shot));
    }

    /// <summary>The outlook in plain words.</summary>
    private static string OutlookText(TeaOutlook o, double earlyScore)
    {
        if (o.TooEarly)
        {
            return $"Outlook: too early to tell (practice rounds start to differ after about {earlyScore / 1000:0}k points)";
        }

        var verdict = o.Share160 >= 0.85 ? "looking good" : o.Share160 <= 0.40 ? "looking bad" : "could go either way";
        return $"Outlook: {verdict}. Practice rounds like this ended around {o.TypicalFinal / 1000:0}k: {o.Share160:P0} reached 160k, {o.Share200:P0} reached 200k";
    }

    private void ReloadNet()
    {
        var path = Path.Combine(_dir, "net.bin");
        try
        {
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
            if (written == _netWritten)
            {
                return;
            }

            _netWritten = written;
            using var f = File.OpenRead(path);
            var net = TeaNet.Load(f);
            _net = net is { Trained: >= NetMinRounds } ? net : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // being written: tried again next time
        }
    }

    public TeaHouseHelper(HostServices services, Func<bool> jobRunning, Action<TeaHouseView> publish, TeaHouseHands? hands = null)
    {
        _s = services;
        _jobRunning = jobRunning;
        _hands = hands;
        _publish = publish;
        _dir = Path.Combine(services.Paths.RuntimeDir, "teahouse");
        _session = new TeaSession(LoadModel(), TeaLabels.FromJson(ReadText("labels.json")));
    }

    /// <summary>Starts looking.</summary>
    public void Start() => _loop ??= Task.Run(() => Loop(_cts.Token));

    /// <summary>The user played (or wants a fresh look): read the game again; any deeper thinking stops.</summary>
    public void RequestRead()
    {
        lock (_gate)
        {
            _keyAtRequest = _lastAdviceKey;
            _requestedUtc = DateTime.UtcNow;
            _pendingThink = null;
            _watching = true;
        }

        _think?.Cancel();
    }

    /// <summary>
    /// Auto-play (2026-10-30, the user: "make the helper click while I'm afk"): after each final answer the companion
    /// presses it (through the TeaHousePlay job: slide clicks, confirmed from the game log) and reads again; at the end
    /// screen it presses Restart once that button is known. Off by itself after <see cref="MaxAutoFailures"/> presses in
    /// a row that did not land, or when the companion refuses to start the job (a Stop, a safety stop, another job).
    /// </summary>
    public bool AutoPlay
    {
        get => _hands?.On ?? false;
        set
        {
            if (_hands is null)
            {
                return;
            }

            _hands.On = value;
            _autoFailures = 0;
            _pressedKey = null;
            _inFlight = null;
            Journal("teahouse.autoPlay", ("on", value));
            if (value)
            {
                RequestRead();
            }
        }
    }

    /// <summary>When on, the helper keeps reading the game without waiting for "I played" (no deeper thinking then).</summary>
    public bool KeepWatching
    {
        get => _keepWatching;
        set
        {
            _keepWatching = value;
            if (value)
            {
                RequestRead();
            }
        }
    }

    /// <summary>
    /// The user named a picture (an unknown square, the tray or the current piece): learned at once, saved, and the board
    /// is advised again on the next look.
    /// </summary>
    public void Name(IReadOnlyList<string> prints, TeaKind kind, TeaPictureSource source)
    {
        lock (_gate)
        {
            // every frame's picture of that square: a cat's poses come in several (2026-10-29 live: one pose per naming was
            // not enough for the Orange Cat)
            var any = false;
            foreach (var print in prints.Distinct())
            {
                any |= _session.Labels.Learn(print, kind, source);
            }

            if (any)
            {
                Note($"named.{source}.{kind}");
                _dirty = true;
                _lastAdviceKey = null;
                Journal("teahouse.named", ("kind", kind.ToString()), ("source", source.ToString()));
            }
        }

        RequestRead();
    }

    public void Dispose()
    {
        _think?.Cancel();
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // cancelled
        }

        Save();
        _cts.Dispose();
    }

    private void Loop(CancellationToken ct)
    {
        TeaHouseView? last = null;
        while (!ct.IsCancellationRequested)
        {
            if (!_watching && !_keepWatching)
            {
                // waiting for the user's move: no captures; the time goes into looking further ahead
                if (_pendingThink is { } job)
                {
                    _pendingThink = null;
                    ThinkDeeper(job.Board, job.Current, job.View, ct);
                    if (_hands is { On: true } && !_watching && !ct.IsCancellationRequested)
                    {
                        AutoPress(job.View, job.Board + "|" + job.Current, ct);
                    }
                }
                else
                {
                    ct.WaitHandle.WaitOne(LookGap);
                }

                continue;
            }

            TeaHouseView view;
            try
            {
                view = Look(ct) ?? last ?? new TeaHouseView("Looking for the game...", null, null, [], null, [], 0);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                view = new TeaHouseView("The helper hit a problem and keeps trying (" + ex.GetType().Name + ").", last?.Frame, last?.Reading, last?.Advice ?? [], last?.Score, [], 0);
            }

            last = view;
            _publish(view);
            if (_dirty)
            {
                Save();
            }

            // while reading (after "I played"), the next look follows at once; the pause is only for the keep-watching mode
            if (_keepWatching)
            {
                ct.WaitHandle.WaitOne(LookGap);
            }
        }
    }

    /// <summary>
    /// Looks 3, then 4 pieces ahead while the user plays (each finished look replaces the advice; "I played" stops it).
    /// </summary>
    private void ThinkDeeper(TeaBoard board, TeaKind current, TeaHouseView view, CancellationToken ct)
    {
        using var think = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _think = think;
        try
        {
            // each look ranks the options for the next, deeper one; only the deepest is shown
            // fast: the look's own advice (the brain alone) is final
            IReadOnlyList<TeaAdvice> found = [];
            if (_fast)
            {
                lock (_gate)
                {
                    found = _advice;
                }
            }

            var started = DateTime.UtcNow;
            var joker = TeaSolver.IsJoker(current) || TeaSolver.IsJoker(board.TrayPiece);
            var deepest = _fast ? 1 : DeepestLook;
            for (var depth = 2; depth <= deepest; depth++)
            {
                var (model, options) = Planning(depth, depth >= 5 ? 2 : 8, think.Token);
                var deeper = TeaSolver.Advise(board, current, model, options with { Prior = found.Count > 0 ? found : null });
                if (deeper.Count > 0)
                {
                    if (found.Count > 0 && (deeper[0].Action != found[0].Action || deeper[0].Square != found[0].Square))
                    {
                        Journal("teahouse.deeper", ("depth", (long)depth), ("board", board.ToJournal()), ("current", current.ToString()),
                            ("was", found[0].Action + ":" + found[0].Square), ("now", deeper[0].Action + ":" + deeper[0].Square));
                    }

                    found = deeper;
                }

                // rule 3 of the rule design: one piece further only when keeping the joker and spending it are close (within
                // twice the values' noise); a clear choice needs no 15 s wait
                if (depth == DeepestLook && joker && CloseKeepOrSpend(found, board, current) is { } gap)
                {
                    deepest = JokerLook;
                    _publish(view with { Status = $"Keep the joker or use it? Only {gap:0} points apart: thinking {JokerLook - 1} pieces ahead (about 15 s)..." });
                }
            }

            lock (_gate)
            {
                _advice = found;
            }

            var took = (DateTime.UtcNow - started).TotalSeconds;
            _publish(view with
            {
                Status = (_fast ? $"Your move (read in {_readSeconds:0.0} s, fast: no look-ahead)." : $"Your move (read in {_readSeconds:0.0} s, looked {deepest - 1} pieces ahead in {took:0.0} s).") + " Click \"I played\" (or the board) after placing it." + _hiddenNote,
                Advice = found,
                Waiting = true,
                Thinking = false,
            });
        }
        catch (OperationCanceledException)
        {
            // the user played: back to reading
        }
        finally
        {
            _think = null;
        }
    }

    /// <summary>
    /// Auto-play's press of the final answer (a swap or a store presses the tray first), then a fresh read; a press that
    /// did not land is retried after a new read, and <see cref="MaxAutoFailures"/> in a row (or a refused job) turn
    /// auto-play off with the reason shown.
    /// </summary>
    private void AutoPress(TeaHouseView view, string key, CancellationToken ct)
    {
        // the previous move's last press may still be finishing in the engine (its late window check): its answer first
        // (2026-10-31, the user: "figure something out"; the board was read while it finished)
        if (_inFlight is { } flight)
        {
            _inFlight = null;
            var last = Answer(flight.Press);
            if (!last.Done)
            {
                // the press never landed: its board is still the board, and the same move may be pressed again (2026-10-31:
                // kept as pressed, the unchanged board was taken for "press.noChange" and auto-play went off twice)
                _pressedKey = null;
                AutoFailed(view, last.Code, ct);
                return;
            }

            _autoFailures = 0;
            Journal("teahouse.auto", ("action", flight.Move.Action.ToString()), ("square", (long)flight.Move.Square), ("piece", flight.Move.Piece.ToString()));
            if (key == _pressedKey || !flight.Logged)
            {
                // the read settled on the old board before the press landed, or it began before the game logged the press
                // (the board it saw may be the one before it): read once more
                RequestRead();
                return;
            }
        }

        // a press the game logged but that left the board as it was (a misread square?) must not repeat forever
        if (key == _pressedKey)
        {
            AutoFailed(view, "press.noChange", ct);
            return;
        }

        TeaAdvice a;
        lock (_gate)
        {
            if (_advice.Count == 0)
            {
                return;
            }

            a = _advice[0];
        }

        int[] squares = a.Action switch
        {
            TeaAction.Store => [TeaBoard.Tray],
            TeaAction.Swap => [TeaBoard.Tray, a.Square],
            _ => [a.Square],
        };
        _publish(view with { Status = "Auto-play: pressing the move...", Advice = [a], Waiting = true, Thinking = false });
        for (var k = 0; k < squares.Length - 1; k++)
        {
            var press = _hands!.Press(squares[k], ct);
            if (!press.Done)
            {
                AutoFailed(view, press.Code, ct);
                return;
            }
        }

        // the move's last press: once the game logs it, the next board is read while the engine finishes the press; the
        // next move waits for its answer (above), and the engine sends nothing before its full window check
        var before = _s.Log.State;
        var square = squares[^1];
        var hands = _hands!;
        _pressedKey = key;
        var lastPress = Task.Run(() => hands.Press(square, ct));
        bool Logged() => _s.Log.State.LastGridClick is { } g && g.LoggedAfter(before.AsOf);
        var until = DateTime.UtcNow + LoggedPressWait;
        while (!lastPress.IsCompleted && DateTime.UtcNow < until && !ct.IsCancellationRequested && !Logged())
        {
            ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(30));
        }

        _inFlight = (lastPress, a, Logged());
        RequestRead();
    }

    /// <summary>The press task's answer (a cancelled or failed task is a press that did not land).</summary>
    private static TeaPress Answer(Task<TeaPress> press)
    {
        try
        {
            return press.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return new TeaPress(false, "press.withdrawn");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new TeaPress(false, "press.error:" + ex.GetType().Name);
        }
    }

    /// <summary>A press that did not land: read again and retry, or stop auto-play (with the reason) when it keeps failing.</summary>
    private void AutoFailed(TeaHouseView view, string code, CancellationToken ct)
    {
        _autoFailures++;
        Journal("teahouse.autoFailed", ("code", code), ("failures", (long)_autoFailures));
        if (code.Contains("template.notFound", StringComparison.Ordinal))
        {
            AnchorMiss(ct);
        }

        var refused = code == "autoPlay.off" || code.StartsWith("busy:", StringComparison.Ordinal) || code.StartsWith("refused:", StringComparison.Ordinal);
        if (refused || _autoFailures >= MaxAutoFailures)
        {
            if (_hands is not null)
            {
                _hands.On = false;
            }

            _publish(view with { Status = "Auto-play stopped: " + AutoReason(code) + ". Tick Auto-play again to go on.", Waiting = true, Thinking = false });
            return;
        }

        _publish(view with { Status = "Auto-play: the press did not land (" + AutoReason(code) + "); reading the board again...", Waiting = true, Thinking = false });
        ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
        RequestRead();
    }

    /// <summary>
    /// A press whose anchor pictures were hidden (2026-10-30 live: the frame corner was not found, and the engine's Esc
    /// reset closed the minigame; the board procedures no longer reset): how well each picture is found on a fresh capture
    /// goes to the journal, and the capture is kept (chat panel and banner strip blacked out, at most
    /// <see cref="MaxPhantomShots"/>) so what covered it can be seen.
    /// </summary>
    private void AnchorMiss(CancellationToken ct)
    {
        var cap = _s.GameIo.Capture(ct);
        if (!cap.Ok)
        {
            Journal("teahouse.anchorMiss", ("capture", cap.Problem.ToString()));
            return;
        }

        var frame = cap.Frame!;
        var anchors = string.Join(",", Anchors.Select(id =>
        {
            var m = _s.Vision.Locate(frame, id);
            return $"{id["teahouse.".Length..]}:{m.Score:0.00}:{(m.Found ? "found" : m.Failure.ToString())}";
        }));
        string? shot = null;
        if (_missShots < MaxPhantomShots)
        {
            _missShots++;
            shot = $"anchor_miss_{_missShots}";
            try
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllBytes(Path.Combine(_dir, shot + ".png"), _s.Vision.EncodePng(frame, new RectI(0, 0, frame.Width, frame.Height), [ChatPanel, BannerStrip]));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                shot = null;
            }
        }

        Journal("teahouse.anchorMiss", ("anchors", anchors), ("shot", shot));
    }

    /// <summary>A press code in plain words; a code with the engine's reason ("...|ready.CoverRejected") says what to do.</summary>
    private static string AutoReason(string code)
    {
        var at = code.IndexOf('|', StringComparison.Ordinal);
        if (at >= 0 && EngineReason(code[(at + 1)..]) is { } why)
        {
            return why;
        }

        return BaseReason(at >= 0 ? code[..at] : code);
    }

    /// <summary>What the engine was waiting for, in words the user can act on; null for the rest.</summary>
    private static string? EngineReason(string detail) => detail switch
    {
        _ when detail.StartsWith("ready.CoverRejected", StringComparison.Ordinal) =>
            "your mouse must rest on another program's window that is in front of the game: click that window once (not the helper) and leave the mouse still in its middle",
        _ when detail.StartsWith("ready.CursorMoving", StringComparison.Ordinal) || detail.StartsWith("ready.UserButtonDown", StringComparison.Ordinal) =>
            "the mouse was moving: leave it still",
        _ when detail.StartsWith("ready.CrossMonitor", StringComparison.Ordinal) || detail.StartsWith("ready.MonitorRule", StringComparison.Ordinal) =>
            "the mouse is on another monitor: rest it on the game's monitor",
        _ when detail.StartsWith("ready.GameForeground", StringComparison.Ordinal) || detail.StartsWith("ready.UserActive", StringComparison.Ordinal) =>
            "a game dialog is the active window (or playing with the game in front is switched off): close it, or click another program's window in front of the game",
        _ when detail.StartsWith("ready.CursorNotOnGame", StringComparison.Ordinal) =>
            "the game is in front: rest the mouse on the game itself, away from its edges and title bar",
        _ when detail.StartsWith("ready.UserInputRecent", StringComparison.Ordinal) || detail.StartsWith("ready.ForegroundSettling", StringComparison.Ordinal) =>
            "the game is in front: keep your hands off the mouse and keyboard for a few seconds",
        _ when detail.Contains("UserInput", StringComparison.Ordinal) || detail.Contains("ForegroundLost", StringComparison.Ordinal) =>
            "you used the mouse or keyboard (or switched away) while the game was in front, so that press stopped: leave them still",
        _ when detail.StartsWith("aborted.SlidTemplateMismatch", StringComparison.Ordinal) =>
            "the board could not be checked while the game was slid under the mouse: rest the mouse nearer the middle of the screen",
        _ when detail.StartsWith("busy.", StringComparison.Ordinal) =>
            "the game is busy (loading or not ready yet)",
        _ when detail.StartsWith("template.notFound", StringComparison.Ordinal) =>
            "part of the Tea House page was hidden for a moment (a banner scrolling across the top?), so nothing was pressed",
        _ => null,
    };

    /// <summary>A press code without the engine's reason, in plain words.</summary>
    private static string BaseReason(string code) => code switch
    {
        "autoPlay.off" => "auto-play was switched off",
        "job.noAnswer" => "the auto-play job did not answer",
        "press.notLogged" => "the game did not register the press",
        "press.noChange" => "the press did not change the board",
        "restart.needsSurvey" => "the helper does not know the Restart button yet",
        _ when code.StartsWith("busy:", StringComparison.Ordinal) => "another companion job is running (" + code[5..] + ")",
        _ when code.StartsWith("refused:", StringComparison.Ordinal) => "the companion would not start auto-play (" + code[8..] + ")",
        _ when code.StartsWith("notOnBoard:", StringComparison.Ordinal) => "the Tea House board is not the game's top window",
        _ when code.StartsWith("press.otherSquare:", StringComparison.Ordinal) => "the game logged a press on another square",
        _ => "the companion could not press safely (" + code + ")",
    };

    /// <summary>
    /// The round's end screen (log top UI <see cref="EndScreenUi"/>): its picture is kept once (chat panel and banner strip blacked out)
    /// so the Restart button can be taught; with auto-play on, Restart is pressed when the companion knows it.
    /// </summary>
    private TeaHouseView EndScreen(Frame frame, CancellationToken ct)
    {
        if (!_endSaved)
        {
            _endSaved = true;
            try
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllBytes(Path.Combine(_dir, "end_screen.png"), _s.Vision.EncodePng(frame, new RectI(0, 0, frame.Width, frame.Height), [ChatPanel, BannerStrip]));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // kept next time
                _endSaved = false;
            }

            Journal("teahouse.endScreen", ("saved", _endSaved));
        }

        var view = new TeaHouseView("Round over: press Restart in the game, and the helper reads the new board.", frame, null, [], null, Recent(), Counted());
        if (_hands is not { On: true })
        {
            return view;
        }

        var press = _hands.Press(TeaHouseDesk.Restart, ct);
        if (press.Done)
        {
            _autoFailures = 0;
            _pressedKey = null;
            Journal("teahouse.restarted");
            RequestRead();
            return view with { Status = "Auto-play: Restart pressed, reading the new board..." };
        }

        if (press.Code == "restart.needsSurvey")
        {
            _hands.On = false;
            Journal("teahouse.autoFailed", ("code", press.Code), ("failures", 0L));
            return view with { Status = "Round over. Auto-play stopped: it does not know the Restart button yet. Press Restart yourself; the helper kept a picture of the end screen (end_screen.png) so it can learn the button." };
        }

        AutoFailed(view, press.Code, ct);
        return view with { Status = "Round over: the Restart press did not land (" + AutoReason(press.Code) + ")." };
    }

    /// <summary>One look: captures, the reading, learning and advice. Null when nothing new should be shown.</summary>
    private TeaHouseView? Look(CancellationToken ct)
    {
        // the helper's own auto-play job is the one job it keeps watching through
        if (_jobRunning() && _hands is not { JobRunning: true })
        {
            return new TeaHouseView("Paused while the companion runs a job (the helper only watches when nothing else runs).", null, null, [], null, [], 0);
        }

        var io = _s.GameIo;
        if (io.Identity is null || !io.GetState().Exists)
        {
            var c = io.Connect(ct); // finds the game window; sends nothing
            if (!c.Ok)
            {
                return new TeaHouseView("Waiting for the game to run.", null, null, [], null, [], 0);
            }
        }

        var frames = new List<Frame>(FramesPerLook);
        for (var i = 0; i < FramesPerLook; i++)
        {
            if (i > 0)
            {
                ct.WaitHandle.WaitOne(FrameGap);
            }

            var cap = io.Capture(ct);
            if (!cap.Ok)
            {
                return new TeaHouseView(cap.Problem switch
                {
                    Contracts.GameIo.CaptureProblem.Iconic => "The game is minimised: restore it and the helper reads the board.",
                    _ => "Cannot capture the game right now (" + cap.Problem + ").",
                }, null, null, [], null, [], 0);
            }

            frames.Add(cap.Frame!);
        }

        if (string.Equals(_s.Log.State.AdvisoryTop, EndScreenUi, StringComparison.Ordinal))
        {
            return EndScreen(frames[0], ct);
        }

        _endSaved = false;

        lock (_gate)
        {
            var reading = TeaReader.Read(frames, _session.Labels, _session.Remembered, _session.LastPreview, _session.Settled?.Current ?? TeaKind.Unknown);
            if (!reading.OnScreen)
            {
                _session.Observe(reading, null);
                return new TeaHouseView("Open Kitty Tea House in the game (What's On); the helper then reads the board.", frames[0], null, [], null, Recent(), Counted());
            }

            // the Score's text recognition only when this look can settle the board (it matches the last one): mid-animation
            // looks skip it (2026-10-30, the user: "faster but not worse")
            var score = _session.WouldSettle(reading) ? ReadScore(frames[0]) : null;
            var settledBefore = _session.Settled?.Board.Clone();
            var obs = _session.Observe(reading, score);
            foreach (var code in obs.Learned)
            {
                Note(code);
                _dirty = true;
            }

            if (obs.Turn && settledBefore is not null && _session.Settled is { } after)
            {
                PhantomCheck(settledBefore, after.Board, reading, frames);
            }

            if (obs.Turn)
            {
                Journal("teahouse.move", ("placed", obs.Placed.ToString()), ("square", (long)obs.Square), ("rulesMatch", obs.RulesMatch),
                    ("scoreDelta", obs.ScoreDelta), ("board", reading.Board.ToJournal()), ("learned", string.Join(",", obs.Learned)));
                if (!obs.RulesMatch && _session.Settled is not null)
                {
                    Note("rules.mismatch");
                }
            }

            // the advice changes only when the settled board does (2026-10-29, the user: "keeps changing its decision"); with a
            // piece the helper does not know there is no advice at all (the user: "if the current state has a ? it should be
            // clear to me ... maybe stop the decision entirely")
            var settled = _session.Settled ?? reading;
            var key = settled.Board + "|" + settled.Current;
            var blocked = settled.Current == TeaKind.Unknown || settled.UnknownCount > 0;
            if (score is { } read)
            {
                _lastScore = read;
            }

            if (key != _lastAdviceKey)
            {
                var hidden = Enumerable.Range(1, TeaBoard.Squares - 1).Where(i => settled.Board[i] == TeaKind.Unknown).ToList();
                _hiddenNote = hidden.Count > 0 && (settled.Current == TeaKind.Cutlery || settled.Board.TrayPiece == TeaKind.Cutlery)
                    ? $" The game's Cutlery hint hides {string.Join(" and ", hidden.Select(TeaBoard.Where))}: the helper cannot see that piece, so the Cutlery is not aimed there."
                    : null;
                var (model, options) = Planning(_fast ? 1 : 2, 8, CancellationToken.None);
                _advice = blocked ? [] : TeaSolver.Advise(settled.Board, settled.Current, model, options);

                // the outlook from the practice rounds that stood here (same score so far, same value of the best move at
                // the quick look the samples were made with); no samples or no score read yet: no outlook
                ReloadForecast();
                _outlook = _forecast is { } fc && _lastScore is { } sc && _advice.Count > 0 && !_explore && !_fast
                    ? OutlookText(fc.Predict(sc, _advice[0].Value), fc.EarlyScore)
                    : null;
            }

            var advice = _advice;
            if (key != _lastAdviceKey && advice.Count > 0)
            {
                _lastAdviceKey = key;
                var a = advice[0];
                Journal("teahouse.advice", ("board", settled.Board.ToJournal()), ("current", settled.Current.ToString()), ("action", a.Action.ToString()),
                    ("square", (long)a.Square), ("piece", a.Piece.ToString()), ("value", Math.Round(a.Value)), ("score", score), ("unknown", (long)settled.UnknownCount),
                    ("outlook", _outlook is null ? null : "shown"));
            }

            // never advise for a state the screen no longer shows (2026-10-30 live: after a shop purchase the helper kept the
            // old piece's advice): the settled board and piece must be what was just read
            var current = settled.Board.SameAs(reading.Board) && settled.CurrentPrint == reading.CurrentPrint;
            var done = !blocked && advice.Count > 0 && obs.Stable && current
                && (key != _keyAtRequest || DateTime.UtcNow - _requestedUtc >= SameBoardAfter);
            if (!current && obs.Stable && DateTime.UtcNow - _requestedUtc >= SameBoardAfter)
            {
                return new TeaHouseView("The board changed in a way the helper cannot follow (two moves at once?). Reading again...", frames[0], reading, [], score, Recent(), Counted());
            }
            if (done && !_keepWatching)
            {
                _watching = false;
                _readSeconds = (DateTime.UtcNow - _requestedUtc).TotalSeconds;
                // the user (2026-10-29): "I want it to finish the 3 move lookup before suggesting": nothing is shown until then
                var waiting = new TeaHouseView(_fast ? "Choosing..." : $"Thinking {DeepestLook - 1} pieces ahead...", frames[0], reading, [], score, Recent(), Counted()) { Waiting = true, Thinking = true, Outlook = _outlook };
                _pendingThink = (settled.Board.Clone(), settled.Current, waiting);
                return waiting;
            }

            var status = settled.Current == TeaKind.Unknown
                ? "The current piece is new to the helper: click it below to name it."
                : settled.UnknownCount > 0
                    ? $"{settled.UnknownCount} piece(s) on the board are new to the helper (marked ?): click one to name it."
                    : obs.Stable ? "Watching. Place the piece and the advice follows." : "Reading the board...";
            // while it reads after "I played" there is no advice on screen (the user, 2026-10-30: "the green no.1 staying there
            // is confusing me, is it the next suggestion or the previous one"); keep-watching shows it all along
            return new TeaHouseView(status, frames[0], reading, _keepWatching ? advice : [], score, Recent(), Counted()) { Thinking = !_keepWatching, Outlook = _outlook };
        }
    }

    /// <summary>The Score number (white digits on brown), or null when it cannot be read.</summary>
    private double? ReadScore(Frame frame)
    {
        if (!_s.Ocr.IsAvailable)
        {
            return null;
        }

        var r = TeaGeometry.Score;
        var pixels = new byte[r.Width * r.Height];
        for (var y = 0; y < r.Height; y++)
        {
            for (var x = 0; x < r.Width; x++)
            {
                var p = ((r.Top + y) * frame.Stride) + ((r.Left + x) * 4);
                pixels[(y * r.Width) + x] = (byte)((frame.Bgra[p] * 0.114) + (frame.Bgra[p + 1] * 0.587) + (frame.Bgra[p + 2] * 0.299));
            }
        }

        var result = _s.Ocr.Recognize(new GrayImage(r.Width, r.Height, pixels), new OcrOptions(Binarization: Binarization.LightOnDark), TimeSpan.FromSeconds(1));
        var digits = Regex.Replace(result.Text ?? string.Empty, "[^0-9]", string.Empty);
        return digits.Length is > 0 and <= 7 && double.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private void Note(string code)
    {
        _learned.Insert(0, code);
        if (_learned.Count > 12)
        {
            _learned.RemoveAt(_learned.Count - 1);
        }
    }

    private IReadOnlyList<string> Recent() => [.. _learned];

    private long Counted() => TeaKinds.Spawnable.Sum(k => _session.Model.Seen[(int)k]);

    private void Journal(string ev, params (string Key, object? Value)[] fields)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal) { ["event"] = ev };
        foreach (var (k, v) in fields)
        {
            data[k] = v;
        }

        try
        {
            _s.Journal.Write(new JournalRecord(DateTimeOffset.UtcNow, JournalKind.Decision, null, "teahouse", null, data));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the journal is best effort here; the helper keeps working
        }
    }

    private void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                WriteText("labels.json", _session.Labels.ToJson());
                WriteText("model.json", TeaModelFile.From(_session.Model).ToJson());
                _dirty = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // kept dirty: tried again after the next look
            }
        }
    }

    // the same loader as the trainer: a stored value never beats a confirmed one (TeaModelFile)
    private TeaModel LoadModel() => TeaModelFile.Load(ReadText("model.json"));

    private string? ReadText(string name)
    {
        var path = Path.Combine(_dir, name);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteText(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
