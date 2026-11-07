using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Logs;
using Companion.Contracts.Perception;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;

namespace Companion.Engine;

/// <summary>
/// Capture/read service (SPEC 3, 6.4): captures through IGameIo (rate-limited to 2 per second), validates frames with
/// IVision.Assess, classifies the HUD from 2 agreeing captures at least 300 ms apart, and applies readers (identity is
/// checked by the reader against the log state and anchors in the same capture). Passive: never sends input.
/// Thread-safe (calls are serialised).
/// </summary>
public sealed class ScreenService : IScreenService
{
    internal static readonly TimeSpan MinCaptureGap = TimeSpan.FromMilliseconds(500);

    private readonly IGameIo _gameIo;
    private readonly IVision _vision;
    private readonly IScreenReader _screenReader;
    private readonly IReaderCatalog _readers;
    private readonly ILogTracker _log;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private TimeSpan? _lastCapture;

    /// <summary>Creates the service (no side effects).</summary>
    public ScreenService(IGameIo gameIo, IVision vision, IScreenReader screenReader, IReaderCatalog readers, ILogTracker log, IClock clock)
    {
        _gameIo = gameIo;
        _vision = vision;
        _screenReader = screenReader;
        _readers = readers;
        _log = log;
        _clock = clock;
    }

    /// <inheritdoc />
    public CaptureResult Capture(CancellationToken ct) => Capture(MinCaptureGap, ct);

    /// <summary>
    /// The second capture of a stability pair (the engine's two-capture locate, <see cref="ClassifyHudStable"/>): waits
    /// <see cref="HardRules.StableCaptureGap"/> (F07) after the previous capture instead of <see cref="MinCaptureGap"/>
    /// (2026-10-31 sweep: every locate cost ~0.59 s). Every other capture keeps the 2/s throttle.
    /// </summary>
    internal CaptureResult CapturePairSecond(CancellationToken ct) => Capture(HardRules.StableCaptureGap, ct);

    private CaptureResult Capture(TimeSpan minGap, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_lastCapture is { } last)
            {
                var wait = last + minGap - _clock.Monotonic;
                if (wait > TimeSpan.Zero)
                {
                    _clock.Sleep(wait, ct);
                }
            }
            var r = _gameIo.Capture(ct);
            _lastCapture = _clock.Monotonic;
            if (!r.Ok)
            {
                return r;
            }
            var f = r.Frame!;
            if (f.Width != HardRules.ExpectedClientWidth || f.Height != HardRules.ExpectedClientHeight)
            {
                return new CaptureResult(CaptureProblem.WrongSize, null, r.Elapsed);
            }
            return _vision.Assess(f) switch
            {
                FrameQuality.Ok => r,
                FrameQuality.WrongSize => new CaptureResult(CaptureProblem.WrongSize, null, r.Elapsed),
                _ => new CaptureResult(CaptureProblem.InvalidImage, null, r.Elapsed),
            };
        }
    }

    /// <inheritdoc />
    public HudClassification ClassifyHudStable(CancellationToken ct)
    {
        var a = Capture(ct);
        if (!a.Ok)
        {
            return Unknown("capture." + a.Problem);
        }
        var ca = _vision.ClassifyHud(a.Frame!);
        var gap = HardRules.StableCaptureGap - (_clock.Monotonic - a.Frame!.CapturedAtMonotonic);
        if (gap > TimeSpan.Zero)
        {
            _clock.Sleep(gap, ct);
        }
        var b = CapturePairSecond(ct);
        if (!b.Ok)
        {
            return Unknown("capture." + b.Problem);
        }
        var cb = _vision.ClassifyHud(b.Frame!);
        if (ca.State == cb.State)
        {
            return cb;
        }
        return new HudClassification(HudState.Unknown, cb.CollapsedToggleScore, cb.ExpandedToggleScore, cb.BagFound, false, "hud.unstable");
    }

    /// <inheritdoc />
    public ScreenReadResult Read(string readerId, CancellationToken ct)
    {
        var def = _readers.Get(readerId);
        if (def is null || def.Status != ReaderStatus.Ready)
        {
            return new ScreenReadResult(readerId, ReadStatus.NeedsSurvey, [], _log.Position, _clock.UtcNow);
        }
        var cap = Capture(ct);
        if (!cap.Ok)
        {
            return new ScreenReadResult(readerId, ReadStatus.Unknown, [], _log.Position, _clock.UtcNow);
        }
        return _screenReader.Read(def, cap.Frame!, _log.State);
    }

    private static HudClassification Unknown(string code) => new(HudState.Unknown, 0, 0, false, false, code);
}
