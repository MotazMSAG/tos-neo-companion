using System.Buffers;
using Companion.Contracts;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;

namespace Companion.Logs;

/// <summary>Tunables of the tracker (SPEC 6.1 values by default; tests lower them).</summary>
/// <param name="MaxReadBytes">At most this many bytes are read per poll (8 MB).</param>
/// <param name="BootstrapTailBytes">The bootstrap fully parses this many trailing bytes (2 MB).</param>
/// <param name="NeedleChunkBytes">Chunk size of the bootstrap needle search.</param>
/// <param name="RingCapacity">Ring buffer capacity (50 000 events).</param>
/// <param name="RingMaxAge">Ring buffer age limit (15 min, monotonic arrival time).</param>
internal sealed record LogTrackerOptions(int MaxReadBytes, int BootstrapTailBytes, int NeedleChunkBytes, int RingCapacity, TimeSpan RingMaxAge)
{
    internal static LogTrackerOptions Default { get; } =
        new(8 * 1024 * 1024, 2 * 1024 * 1024, 4 * 1024 * 1024, EventRing.DefaultCapacity, EventRing.DefaultMaxAge);
}

/// <summary>
/// Incremental reader, parser and UI model of the game log (SPEC 6.1, 6.2). Thread-safe: every member takes one lock,
/// so GameIO can poll during a click while the host polls on its timer. The constructor only stores its dependencies;
/// nothing is read before <see cref="Bootstrap"/> or <see cref="Poll"/>. Each read opens the file with
/// FileShare.ReadWrite|Delete and closes it before returning. Never writes anything.
/// </summary>
public sealed class LogTracker : ILogTracker
{
    /// <summary>ASCII needles of the bootstrap byte search (SPEC 6.1).</summary>
    private static readonly byte[][] Needles =
    [
        "set_auto_collect_status"u8.ToArray(),
        "req_login_role"u8.ToArray(),
        "[graphic] Current screen resolution"u8.ToArray(),
        "[scene_load]"u8.ToArray(),
        "open const ui"u8.ToArray(),
        "hide top ui"u8.ToArray(),
        "OnApplicationFocus"u8.ToArray(),
    ];

    private static readonly byte[] PressPrefix = "UpdateModule LostFocuse"u8.ToArray();

    private readonly string _path;
    private readonly IClock _clock;
    private readonly ILogPrivacyFilter _filter;
    private readonly LogEntryParser _parser;
    private readonly UiModel _model;
    private readonly EventRing _ring;
    private readonly LogTrackerOptions _options;
    private readonly object _gate = new();

    private LogFileIdentity? _file;
    private long _offset;
    private bool _pendingNewGeneration;
    private long _lastLength = -1;
    private DateTimeOffset? _lastGrowth;
    private bool _readable;
    private int _rotations;
    private DateTimeOffset? _lastPoll;

    /// <summary>
    /// A rotation is waiting for the new file's first line: the model is kept (not reset) and the game counts as not
    /// available until that line shows whether the game started anew (incident 32, 2026-10-30).
    /// </summary>
    private bool _carry;

    /// <summary>The game's first log line when it starts (the start-up marker of <see cref="SessionMarker.LogCallbackAdded"/>).</summary>
    private const string LaunchLine = "--->>> add to unity log callback";

    /// <summary>The previous generation the game keeps beside the log when it starts a new file.</summary>
    private const string PreviousLog = "log.bak";

    /// <summary>
    /// A new file continues the session only when it began this soon after the old one's last write (safety review
    /// 2026-10-30: mid-session rotations measured 0.08 s and 0.9 s; a real game start 13.7 s).
    /// </summary>
    private static readonly TimeSpan ContinueGap = TimeSpan.FromSeconds(5);

    /// <summary>When the file the game moved aside was last written (local time), while a rotation is carried.</summary>
    private DateTime? _previousEnded;

    /// <summary>The old file's last events, read after a rotation from log.bak; delivered with the next poll result.</summary>
    private readonly List<LogEvent> _carriedTail = [];

    /// <summary>Creates the tracker for <see cref="CompanionPaths.GameLogPath"/>. No side effects.</summary>
    /// <param name="paths">Paths (only the game log path is used).</param>
    /// <param name="clock">Clock (monotonic for ages and durations, wall time for health and time windows).</param>
    /// <param name="hasher">Hashes the login role token at once; the raw token is never kept.</param>
    /// <param name="classifier">UI classifier; when it is the Logs unit's <see cref="UiClassifier"/>, 'hide top ui' names are taught to it.</param>
    /// <param name="privacyFilter">Filter applied to every line returned by <see cref="ReadFilteredExcerpt"/>.</param>
    public LogTracker(CompanionPaths paths, IClock clock, ICharacterKeyHasher hasher, IUiClassifier classifier, ILogPrivacyFilter privacyFilter)
        : this(paths, clock, hasher, classifier, privacyFilter, LogTrackerOptions.Default)
    {
    }

    internal LogTracker(CompanionPaths paths, IClock clock, ICharacterKeyHasher hasher, IUiClassifier classifier, ILogPrivacyFilter privacyFilter, LogTrackerOptions options)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(privacyFilter);
        ArgumentNullException.ThrowIfNull(options);
        _path = paths.GameLogPath;
        _clock = clock;
        _filter = privacyFilter;
        _parser = new LogEntryParser(hasher);
        _model = new UiModel(classifier);
        _ring = new EventRing(options.RingCapacity, options.RingMaxAge);
        _options = options;
    }

    /// <inheritdoc />
    public LogPosition? Position
    {
        get
        {
            lock (_gate)
            {
                return CurrentPosition();
            }
        }
    }

    /// <inheritdoc />
    public UiStateSnapshot State
    {
        get
        {
            lock (_gate)
            {
                var snapshot = _model.Snapshot(CurrentPosition(), _clock.UtcNow.LocalDateTime);
                return _carry ? snapshot with { GameAvailable = false } : snapshot;
            }
        }
    }

    /// <inheritdoc />
    public LogHealth Health
    {
        get
        {
            lock (_gate)
            {
                return new LogHealth(_lastGrowth, Math.Max(0, _lastLength), _readable, _rotations, _lastPoll);
            }
        }
    }

    /// <inheritdoc />
    public LogBootstrapReport Bootstrap(CancellationToken ct)
    {
        lock (_gate)
        {
            var started = _clock.Monotonic;
            _lastPoll = _clock.UtcNow;
            LogFileIdentity? id = null;
            long length = 0;
            try
            {
                using var fs = LogFileAccess.OpenShared(_path);
                length = fs.Length;
                id = LogFileAccess.TryGetIdentity(fs, length);
                _readable = true;
                NoteLength(length, grew: false);
                if (id is null)
                {
                    return Report(false, null, length, started);
                }

                StartGeneration(id);
                _pendingNewGeneration = false;
                _carry = false;
                _carriedTail.Clear();

                // a file that continues a session (no start-up marker): the session's start is in the file the game just
                // moved aside (incident 32)
                if (!StartsWithLaunch(fs, length))
                {
                    BootstrapPrevious(fs, length);
                }

                BootstrapCore(fs, id, length, ct);
                return Report(true, id, length, started);
            }
            catch (OperationCanceledException)
            {
                ForgetGeneration();
                return Report(false, id, length, started);
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                _readable = false;
                ForgetGeneration();
                return Report(false, id, length, started);
            }
        }
    }

    /// <inheritdoc />
    public LogPollResult Poll()
    {
        lock (_gate)
        {
            var now = _clock.UtcNow;
            _lastPoll = now;
            var rotated = false;
            try
            {
                using var fs = LogFileAccess.OpenShared(_path);
                var length = fs.Length;
                var (created, index) = LogFileAccess.GetHandleInfo(fs);
                var fingerprint = LogFileAccess.TryHeadFingerprint(fs, length);
                _readable = true;

                if (_file is not null
                    && (index != _file.FileIndex
                        || created != _file.CreationUtc
                        || (fingerprint is not null && fingerprint != _file.HeadFingerprint)
                        || length < _offset))
                {
                    // Rotation (SPEC 6.1): mark and re-bootstrap from byte 0. The game also moves its log aside in the middle
                    // of a session once it grows to ~100 MB (incident 32, 2026-10-30: the game ran since 10-27, the new file
                    // began with a scene load), so the model is kept until the new file's first line tells: the start-up
                    // marker resets it (a new game session), anything else continues the session. Until then the game counts
                    // as not available.
                    // The old file is read to its end first (the game moved it to log.bak; safety review: up to one poll of
                    // events, a loading screen among them, was lost); when log.bak is not that file the model is reset as
                    // before. A kept model forgets its old-file positions (a 'hide top ui nil' there proves nothing now).
                    rotated = true;
                    _rotations++;
                    _carriedTail.Clear();
                    if (TryFinishPrevious(_carriedTail))
                    {
                        ForgetGeneration(keepModel: true);
                        _model.ForgetPositions();
                        _carry = true;
                    }
                    else
                    {
                        ForgetGeneration();
                        _carry = false;
                        _previousEnded = null;
                    }

                    _pendingNewGeneration = true;
                    _lastGrowth = now;
                }

                NoteLength(length, grew: rotated);

                if (_file is null)
                {
                    if (fingerprint is null)
                    {
                        // A brand-new file without a complete first line: nothing to read yet.
                        return new LogPollResult(TakeCarriedTail(), CurrentPosition(), rotated, LogReadProblem.None);
                    }

                    var id = new LogFileIdentity(created, index, fingerprint);
                    var launch = StartsWithLaunch(fs, length);
                    StartGeneration(id, keepModel: _carry && !launch && SoonAfterPrevious(fs, length));
                    List<LogEvent> tail;
                    try
                    {
                        // a file that continues a session (no start-up marker) seen first at our own start: the session's
                        // earlier state (in the world, the character, open windows) is in the file the game just moved aside
                        if (!_carry && !launch)
                        {
                            BootstrapPrevious(fs, length);
                        }

                        tail = BootstrapCore(fs, id, length, CancellationToken.None);
                    }
                    catch
                    {
                        // Never keep a half-built generation: the next poll starts the bootstrap again from byte 0.
                        ForgetGeneration(keepModel: _carry);
                        throw;
                    }

                    _carry = false;
                    _previousEnded = null;
                    var carried = TakeCarriedTail();
                    IReadOnlyList<LogEvent> fresh = _pendingNewGeneration ? [.. carried, .. tail] : carried;
                    _pendingNewGeneration = false;
                    return new LogPollResult(fresh, CurrentPosition(), rotated, LogReadProblem.None);
                }

                var events = new List<LogEvent>();
                _offset = ReadAndApply(fs, _file, _offset, length, events, singleRead: true, CancellationToken.None);
                _model.AsOf = CurrentPosition();
                _ring.Evict(_clock.Monotonic);
                return new LogPollResult(events, CurrentPosition(), rotated, LogReadProblem.None);
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                _readable = false;
                return new LogPollResult([], CurrentPosition(), rotated, LogFileAccess.ProblemOf(ex));
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<LogEvent> EventsSince(LogPosition from)
    {
        ArgumentNullException.ThrowIfNull(from);
        lock (_gate)
        {
            return _file is not null && from.File == _file ? _ring.Between(from.Offset, long.MaxValue) : [];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<LogEvent> EventsBetween(LogPosition from, LogPosition to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        lock (_gate)
        {
            return _file is not null && from.File == _file && to.File == _file ? _ring.Between(from.Offset, to.Offset) : [];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ReadFilteredExcerpt(LogPosition from, LogPosition to, int maxLines)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        lock (_gate)
        {
            if (maxLines <= 0 || _file is null || from.File != _file || to.File != _file || to.Offset <= from.Offset)
            {
                return [];
            }

            try
            {
                using var fs = LogFileAccess.OpenShared(_path);
                var length = fs.Length;
                if (LogFileAccess.TryGetIdentity(fs, length) is not { } id || id != _file)
                {
                    return [];
                }

                var end = Math.Min(to.Offset, length);
                var start = Math.Max(from.Offset, end - _options.MaxReadBytes);
                if (end <= start)
                {
                    return [];
                }

                var count = (int)(end - start);
                var buffer = new byte[count];
                var n = LogFileAccess.ReadAt(fs, start, buffer, count);
                var scan = EntryScanner.Scan(buffer, n, start, capped: false);
                var lines = new List<string>();
                foreach (var entry in scan.Entries)
                {
                    if (_filter.Filter(entry.FirstLine) is not { } kept)
                    {
                        continue;
                    }

                    lines.Add(kept);
                    if (entry.MessageStartsWith(PressPrefix))
                    {
                        foreach (var pipe in entry.PipeLines())
                        {
                            if (_filter.Filter(pipe) is { } keptPipe)
                            {
                                lines.Add(keptPipe);
                            }
                        }
                    }
                }

                return lines.Count <= maxLines ? lines : lines.GetRange(lines.Count - maxLines, maxLines);
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                return [];
            }
        }
    }

    private LogPosition? CurrentPosition() => _file is null ? null : new LogPosition(_file, _offset);

    private void StartGeneration(LogFileIdentity id, bool keepModel = false)
    {
        if (!keepModel)
        {
            _model.ResetAll();
        }

        _ring.Clear();
        _file = id;
        _offset = 0;
    }

    private void ForgetGeneration(bool keepModel = false)
    {
        if (!keepModel)
        {
            _model.ResetAll();
        }

        _ring.Clear();
        _file = null;
        _offset = 0;
    }

    /// <summary>True when the file's first line is the game's start-up marker (a new game session begins in it).</summary>
    private static bool StartsWithLaunch(FileStream fs, long length)
    {
        var head = new byte[(int)Math.Min(length, 256)];
        var n = LogFileAccess.ReadAt(fs, 0, head, head.Length);
        return n > 0 && System.Text.Encoding.UTF8.GetString(head, 0, n).Split('\n')[0].Contains(LaunchLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// Feeds the model the previous generation (log.bak) when the current file continues a session: only when the game
    /// moved it aside as the current file began (its last write within 2 minutes of the current file's first line), so a
    /// stale one from another day is never used. Its events update the model only (not buffered, never "since" anything).
    /// </summary>
    private void BootstrapPrevious(FileStream current, long length)
    {
        var previous = Path.Combine(Path.GetDirectoryName(_path) ?? ".", PreviousLog);
        var (file, offset) = (_file, _offset);
        try
        {
            if (!File.Exists(previous) || FirstLineTime(current, length) is not { } begun
                || (File.GetLastWriteTime(previous) - begun).Duration() > ContinueGap)
            {
                return;
            }

            using var fs = LogFileAccess.OpenShared(previous);
            var size = fs.Length;
            var (created, index) = LogFileAccess.GetHandleInfo(fs);
            if (LogFileAccess.TryHeadFingerprint(fs, size) is not { } fingerprint)
            {
                return;
            }

            var id = new LogFileIdentity(created, index, fingerprint);
            _file = id;
            BootstrapCore(fs, id, size, CancellationToken.None);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            // log.bak unreadable: the current file alone (the session state stays unknown, as before incident 32)
        }
        finally
        {
            _ring.Clear();
            _model.ForgetPositions();
            (_file, _offset) = (file, offset);
        }
    }

    /// <summary>
    /// After a rotation: reads the rest of the old file (from where the last poll stopped) when log.bak is that file (same
    /// file index and creation time); true then, and the old file's last write time is kept for <see cref="SoonAfterPrevious"/>.
    /// </summary>
    private bool TryFinishPrevious(List<LogEvent> sink)
    {
        if (_file is not { } old)
        {
            return false;
        }

        var previous = Path.Combine(Path.GetDirectoryName(_path) ?? ".", PreviousLog);
        try
        {
            if (!File.Exists(previous))
            {
                return false;
            }

            using var fs = LogFileAccess.OpenShared(previous);
            var (created, index) = LogFileAccess.GetHandleInfo(fs);
            if (index != old.FileIndex || created != old.CreationUtc)
            {
                return false;
            }

            ReadAndApply(fs, old, _offset, fs.Length, sink, singleRead: false, CancellationToken.None);
            _previousEnded = File.GetLastWriteTime(previous);
            return true;
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return false;
        }
    }

    /// <summary>True when the new file's first line came within <see cref="ContinueGap"/> of the old file's last write.</summary>
    private bool SoonAfterPrevious(FileStream fs, long length) =>
        _previousEnded is { } ended && FirstLineTime(fs, length) is { } begun && (begun - ended).Duration() <= ContinueGap;

    /// <summary>The carried old-file events, once.</summary>
    private List<LogEvent> TakeCarriedTail()
    {
        var events = new List<LogEvent>(_carriedTail);
        _carriedTail.Clear();
        return events;
    }

    /// <summary>The local time stamp of the file's first line ("yyyy-MM-dd HH:mm:ss.fff"), or null.</summary>
    private static DateTime? FirstLineTime(FileStream fs, long length)
    {
        var head = new byte[(int)Math.Min(length, 23)];
        var n = LogFileAccess.ReadAt(fs, 0, head, head.Length);
        return n == 23 && DateTime.TryParseExact(System.Text.Encoding.UTF8.GetString(head, 0, n), "yyyy-MM-dd HH:mm:ss.fff",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t : null;
    }

    private void NoteLength(long length, bool grew)
    {
        if (grew || (_lastLength >= 0 && length > _lastLength))
        {
            _lastGrowth = _clock.UtcNow;
        }

        _lastLength = length;
    }

    private LogBootstrapReport Report(bool ok, LogFileIdentity? id, long length, TimeSpan started) => new(
        ok,
        id,
        ok ? length : 0,
        _model.AutoCollectStatus,
        _model.Character,
        _model.LastResolution,
        ok && _model.InWorld,
        _clock.Monotonic - started);

    /// <summary>
    /// Bootstrap of generation <paramref name="id"/> from byte 0: a forward byte search for the change-only needles
    /// over everything but the last <see cref="LogTrackerOptions.BootstrapTailBytes"/>, then a full parse of that tail
    /// (which rebuilds the advisory stack). Leaves the read offset after the last complete entry. Returns the tail events.
    /// </summary>
    private List<LogEvent> BootstrapCore(FileStream fs, LogFileIdentity id, long length, CancellationToken ct)
    {
        var tailStart = Math.Max(0, length - _options.BootstrapTailBytes);
        var parseFrom = tailStart > 0 ? NeedleScan(fs, id, tailStart, length, ct) : 0;
        var events = new List<LogEvent>();
        _offset = ReadAndApply(fs, id, parseFrom, length, events, singleRead: false, ct);
        _model.AsOf = CurrentPosition();
        return events;
    }

    /// <summary>Reads complete entries from <paramref name="from"/>, applies and buffers their events; returns the next offset.</summary>
    private long ReadAndApply(FileStream fs, LogFileIdentity id, long from, long length, List<LogEvent> sink, bool singleRead, CancellationToken ct)
    {
        var offset = from;
        while (offset < length)
        {
            ct.ThrowIfCancellationRequested();
            var want = (int)Math.Min(_options.MaxReadBytes, length - offset);
            var buffer = ArrayPool<byte>.Shared.Rent(want);
            try
            {
                var n = LogFileAccess.ReadAt(fs, offset, buffer, want);
                if (n <= 0)
                {
                    break;
                }

                var capped = n == _options.MaxReadBytes;
                var scan = EntryScanner.Scan(buffer, n, offset, capped);
                var arrived = _clock.Monotonic;
                foreach (var entry in scan.Entries)
                {
                    if (_parser.Parse(entry, id) is { } e)
                    {
                        _model.Apply(e);
                        _ring.Add(e, arrived);
                        sink.Add(e);
                    }
                }

                var progressed = scan.NextOffset > offset;
                offset = scan.NextOffset;
                if (singleRead || !progressed)
                {
                    break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return offset;
    }

    /// <summary>
    /// Forward byte search for the bootstrap needles over the lines that start before <paramref name="limit"/>. Only
    /// timestamped lines that contain a needle are decoded and parsed (first line only); their events update the
    /// model but are not buffered. Returns the first line start at or after <paramref name="limit"/>.
    /// </summary>
    private long NeedleScan(FileStream fs, LogFileIdentity id, long limit, long length, CancellationToken ct)
    {
        const int maxCarry = 1024 * 1024;
        var chunk = Math.Max(4 * 1024, _options.NeedleChunkBytes);
        var buffer = new byte[chunk + maxCarry];
        var carry = 0;
        long bufStart = 0;
        long readPos = 0;
        var hits = new List<int>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var toRead = (int)Math.Min(chunk, length - readPos);
            var n = toRead > 0 ? LogFileAccess.ReadAt(fs, readPos, buffer.AsSpan(carry, toRead)) : 0;
            readPos += n;
            var dataLen = carry + n;
            var eof = n == 0 || readPos >= length;
            var completeEnd = buffer.AsSpan(0, dataLen).LastIndexOf((byte)'\n') + 1;

            // Where does the tail start? The first line start at or after 'limit', once its preceding LF is in view.
            var boundary = limit - bufStart;
            var prefixEnd = -1;
            if (boundary <= completeEnd)
            {
                if (boundary <= 0)
                {
                    prefixEnd = 0;
                }
                else
                {
                    var k = buffer.AsSpan((int)(boundary - 1), completeEnd - (int)(boundary - 1)).IndexOf((byte)'\n');
                    prefixEnd = (int)(boundary - 1) + k + 1;
                }
            }

            var processEnd = prefixEnd >= 0 ? prefixEnd : completeEnd;
            ParseNeedleHits(buffer, processEnd, completeEnd, bufStart, id, hits);

            if (prefixEnd >= 0)
            {
                return bufStart + prefixEnd;
            }

            if (eof)
            {
                return bufStart + completeEnd;
            }

            carry = dataLen - completeEnd;
            if (carry > maxCarry)
            {
                // A single line longer than 1 MB: drop it, it cannot be an event we keep.
                bufStart += dataLen;
                carry = 0;
            }
            else
            {
                Buffer.BlockCopy(buffer, completeEnd, buffer, 0, carry);
                bufStart += completeEnd;
            }
        }
    }

    /// <summary>Finds needle hits in buffer[0..processEnd), parses each hit line once (file order) and applies it.</summary>
    private void ParseNeedleHits(byte[] buffer, int processEnd, int completeEnd, long bufStart, LogFileIdentity id, List<int> hits)
    {
        hits.Clear();
        var region = buffer.AsSpan(0, processEnd);
        foreach (var needle in Needles)
        {
            var from = 0;
            while (from < processEnd)
            {
                var i = region[from..].IndexOf(needle);
                if (i < 0)
                {
                    break;
                }

                var at = from + i;
                var lineStart = region[..at].LastIndexOf((byte)'\n') + 1;
                hits.Add(lineStart);
                var lineEnd = region[at..].IndexOf((byte)'\n');
                from = lineEnd < 0 ? processEnd : at + lineEnd + 1;
            }
        }

        if (hits.Count == 0)
        {
            return;
        }

        hits.Sort();
        var previous = -1;
        foreach (var lineStart in hits)
        {
            if (lineStart == previous)
            {
                continue;
            }

            previous = lineStart;
            var lf = buffer.AsSpan(lineStart, processEnd - lineStart).IndexOf((byte)'\n');
            if (lf < 0)
            {
                continue;
            }

            var contentLength = lf > 0 && buffer[lineStart + lf - 1] == (byte)'\r' ? lf - 1 : lf;
            var msgStart = EntryScanner.TimestampedMessageStart(buffer.AsSpan(lineStart, contentLength));
            if (msgStart == 0)
            {
                continue;
            }

            // Entry end: after the line, plus a blank terminator line when it follows directly.
            var end = lineStart + lf + 1;
            if (end + 1 < completeEnd && buffer[end] == (byte)'\r' && buffer[end + 1] == (byte)'\n')
            {
                end += 2;
            }
            else if (end < completeEnd && buffer[end] == (byte)'\n')
            {
                end += 1;
            }

            var entry = new RawEntry(buffer, bufStart + lineStart, bufStart + end, new LineRange(lineStart, contentLength), msgStart, null);
            if (_parser.Parse(entry, id) is { } e)
            {
                _model.Apply(e);
            }
        }
    }

    private static bool IsReadFailure(Exception ex) => ex is IOException or UnauthorizedAccessException;
}
