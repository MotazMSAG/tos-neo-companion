namespace Companion.TeaHouse;

/// <summary>What one observation changed.</summary>
/// <param name="Stable">True when this reading matched the one before (a settled board, not mid-animation).</param>
/// <param name="Turn">True when a move happened since the last settled board.</param>
/// <param name="Placed">The piece placed in that move (<see cref="TeaKind.Empty"/> when not worked out).</param>
/// <param name="Square">Where it went (-1 when not worked out).</param>
/// <param name="RulesMatch">True when the helper's rules reproduce the new board from the old one (cats left out).</param>
/// <param name="ScoreDelta">The score's change over the move, when both scores were read.</param>
/// <param name="Learned">Codes of what was learned ("points.Sponge:40", "picture.Board.Cupcake", "seen.Cookie").</param>
public sealed record TeaObservation(bool Stable, bool Turn, TeaKind Placed, int Square, bool RulesMatch, double? ScoreDelta, IReadOnlyList<string> Learned);

/// <summary>
/// Follows the readings of one helper session and learns from them: the odds (each new current piece is counted once),
/// the points (the score's change over a move, against the joins the rules say it made), pictures a move proves (a cat
/// seen in some frames but not others; the piece three of a kind joined into; the current piece once it lands), and
/// whether the rules hold (a move the rules cannot reproduce is reported, the evidence for fixing them). A board counts
/// only once two readings in a row agree. Not thread-safe; one per helper window.
/// </summary>
public sealed class TeaSession(TeaModel model, TeaLabels labels)
{
    private TeaReading? _last;
    private TeaReading? _settled;
    private double? _settledScore;

    /// <summary>The model being learned.</summary>
    public TeaModel Model { get; } = model;

    /// <summary>The pictures being learned.</summary>
    public TeaLabels Labels { get; } = labels;

    /// <summary>The last settled reading (what the advice is for), or null.</summary>
    public TeaReading? Settled => _settled;

    /// <summary>The board under a Cutlery preview, as last settled.</summary>
    public TeaBoard? Remembered => _settled?.Board;

    /// <summary>True when <paramref name="reading"/> matches the last reading, so observing it would settle the board.</summary>
    public bool WouldSettle(TeaReading reading) =>
        _last is not null && reading.OnScreen && _last.Board.SameAs(reading.Board) && _last.CurrentPrint == reading.CurrentPrint;

    /// <summary>The preview square of the last reading (the reader keeps it when the pulse is caught at a slow moment).</summary>
    public int LastPreview => _last?.Preview ?? -1;

    /// <summary>Takes in one reading and the score read with it (null when the score could not be read).</summary>
    public TeaObservation Observe(TeaReading reading, double? score)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var learned = new List<string>();
        if (!reading.OnScreen)
        {
            _last = null;
            return new TeaObservation(false, false, TeaKind.Empty, -1, false, null, learned);
        }

        var stable = _last is not null && _last.Board.SameAs(reading.Board) && _last.CurrentPrint == reading.CurrentPrint;
        _last = reading;
        if (!stable)
        {
            return new TeaObservation(false, false, TeaKind.Empty, -1, false, null, learned);
        }

        LearnMovingPictures(reading, learned);

        var previous = _settled;
        var previousScore = _settledScore;
        if (previous is null)
        {
            _settled = reading;
            _settledScore = score;
            CountCurrent(reading, learned);
            return new TeaObservation(true, false, TeaKind.Empty, -1, false, null, learned);
        }

        if (previous.Board.SameAs(reading.Board) && previous.CurrentPrint == reading.CurrentPrint)
        {
            _settled = reading;
            _settledScore = score ?? _settledScore;
            return new TeaObservation(true, false, TeaKind.Empty, -1, false, null, learned);
        }

        // 2026-10-29 live: the preview's spill made a square below it read as a piece for a moment, and that was taken
        // for a move (and its picture learned as Black Tea). A move changes a known piece somewhere or hands out a new
        // current piece; differences on unknown or moving squares alone are not one
        if (!IsMove(previous, reading))
        {
            // a new piece in hand on an unchanged board (2026-10-30 live: a piece bought in the shop jumps the queue): the
            // new state, no move, not counted in the odds; before, the helper kept advising for the old piece
            if (previous.CurrentPrint != reading.CurrentPrint && SameKnownSquares(previous.Board, reading.Board))
            {
                _settled = reading;
                _settledScore = score ?? _settledScore;
                learned.Add("current.changed");
            }

            return new TeaObservation(true, false, TeaKind.Empty, -1, false, null, learned);
        }

        _settled = reading;
        _settledScore = score ?? _settledScore;

        // the tray: storing or swapping changes the tray and the current piece, nothing else (no turn)
        var delta = score is { } s && previousScore is { } p ? s - p : (double?)null;
        if (SameExceptTray(previous.Board, reading.Board))
        {
            // a store or a swap puts the piece that was current into the tray, and a swap hands the tray's piece back as the
            // current one: both pictures are known from the move (2026-10-29 live: the user had to name each piece again
            // in the tray, and a picture caught mid-swap was learned as the wrong piece)
            var stored = previous.Current;
            if (stored is not (TeaKind.Unknown or TeaKind.Empty) && reading.Board.TrayPiece != stored
                && reading.Squares[TeaBoard.Tray].Print is { } trayPrint && Labels.Learn(trayPrint, stored, TeaPictureSource.Tray))
            {
                learned.Add("picture.Tray." + stored);
            }

            var back = previous.Board.TrayPiece;
            if (back is not (TeaKind.Unknown or TeaKind.Empty) && reading.Current == TeaKind.Unknown
                && reading.CurrentPrint is { } currentPrint && Labels.Learn(currentPrint, back, TeaPictureSource.Panel))
            {
                learned.Add("picture.Panel." + back);
            }

            if (stored is not (TeaKind.Unknown or TeaKind.Empty))
            {
                reading.Board.TrayPiece = stored;
            }

            // a store hands out a new piece; a swap only brings back the stored one
            if (previous.Board.TrayPiece == TeaKind.Empty && reading.Board.TrayPiece != TeaKind.Empty)
            {
                CountCurrent(reading, learned);
            }

            return new TeaObservation(true, false, TeaKind.Empty, -1, true, delta, learned);
        }

        // a turn: a score not read now must not let the next move's change cover two moves (2026-10-31: a Cookie join's 45
        // and a Pudding's 100 were learned together as placing a Pudding pays 145)
        _settledScore = score;
        var (placed, square, match) = Explain(previous, reading, learned);
        if (match is { } m && delta is { } d)
        {
            LearnPoints(placed, m, d, learned);
        }

        CountCurrent(reading, learned);
        return new TeaObservation(true, true, placed, square, match is not null, delta, learned);
    }

    /// <summary>
    /// Works out the move between two settled boards: which piece went where so that the rules give the new board (cats,
    /// which move at random, and squares unknown on either side left out). Tries the previous current piece and the tray's
    /// (a swap); an unknown current piece is tried as every kind (the match names it). The tray tells which (2026-10-31: a
    /// Sponge swapped in from the tray was credited to the Oven in hand as "an Oven that joins nothing pays 40", the start
    /// of the stale 9960): when it now holds the piece that was in hand, only the swap; when it is unchanged, only the piece
    /// in hand; either when it cannot be told. Null match when nothing fits.
    /// </summary>
    private (TeaKind Placed, int Square, TeaPlacement? Match) Explain(TeaReading before, TeaReading after, List<string> learned)
    {
        var trayBefore = before.Board.TrayPiece;
        var trayAfter = after.Board.TrayPiece;
        bool? swapped = trayBefore == TeaKind.Unknown || trayAfter == TeaKind.Unknown ? null
            : trayAfter == trayBefore ? false
            : before.Current != TeaKind.Unknown && trayAfter == before.Current ? true
            : null;
        var candidates = new List<(TeaKind Piece, bool Swap)>();
        if (swapped != true)
        {
            if (before.Current != TeaKind.Unknown)
            {
                candidates.Add((before.Current, false));
            }
            else
            {
                candidates.AddRange(TeaKinds.Spawnable.Select(k => (k, false)));
            }
        }

        if (swapped != false && trayBefore is var t && TeaSolver.Placeable(t) && t != before.Current)
        {
            candidates.Add((t, true));
        }

        foreach (var (piece, swap) in candidates)
        {
            for (var s = 1; s < TeaBoard.Squares; s++)
            {
                var trial = before.Board.Clone();
                if (swap)
                {
                    trial.TrayPiece = before.Current;
                }

                var placed = TeaRules.Place(trial, piece, s, Model);
                if (!placed.Legal)
                {
                    continue;
                }

                // the cats' turn without moves: boxed-in cats become teas
                var (catPoints, catJoins, catTop) = TeaRules.Cats(trial, Model, rng: null, TeaKinds.IsCat(piece) ? s : -1);
                if (!Fits(trial, after))
                {
                    continue;
                }

                if (before.Current == TeaKind.Unknown && !swap && before.CurrentPrint is { } cp && Labels.Learn(cp, piece, TeaPictureSource.Panel))
                {
                    learned.Add("picture.Panel." + piece);
                }

                // a square the reader did not know holds what the rules put there: only where the move put something (the
                // placed square, a boxed-in cat's tea), and only a still picture (a cat may move a little)
                for (var i = 1; i < TeaBoard.Squares; i++)
                {
                    var put = i == s || (trial[i] == TeaKind.Tea && before.Board[i] != TeaKind.Tea);
                    var still = after.Squares[i].Change < TeaReader.StillChange || TeaKinds.IsCat(trial[i]);
                    if (put && still && after.Board[i] == TeaKind.Unknown && trial[i] is not (TeaKind.Empty or TeaKind.Unknown) && after.Squares[i].Print is { } print
                        && Labels.Learn(print, trial[i], TeaPictureSource.Board))
                    {
                        learned.Add("picture.Board." + trial[i]);
                    }
                }

                // the points are learned only from moves whose joins were the placement's own (no boxed-in cats' teas)
                return (piece, s, catJoins == 0 ? placed : placed with { Points = placed.Points + catPoints, Joins = -1 });
            }
        }

        return (TeaKind.Empty, -1, null);
    }

    /// <summary>
    /// The trial board matches the read one on every square, cats aside (they move). An unknown square fits a piece the
    /// rules put there, or an empty square when it is moving (a preview the reader missed); never an empty still one.
    /// </summary>
    private static bool Fits(TeaBoard trial, TeaReading after)
    {
        var read = after.Board;
        for (var i = 1; i < TeaBoard.Squares; i++)
        {
            var a = trial[i];
            var b = read[i];
            if (a == b || (b == TeaKind.Unknown && (a != TeaKind.Empty || after.Squares[i].Change >= TeaReader.StillChange)))
            {
                continue;
            }

            // a cat may have walked: its old square is empty or now holds the other cat, its new one was empty or held
            // the other cat (2026-10-31 audit: half of the moves the rules could not explain were a cat leaving a square
            // that a cat of the other colour walked onto)
            if ((TeaKinds.IsCat(a) && (b == TeaKind.Empty || TeaKinds.IsCat(b))) || (a == TeaKind.Empty && TeaKinds.IsCat(b)))
            {
                continue;
            }

            return false;
        }

        // each colour's count must agree (none appear from nowhere, none changes colour); a square read as unknown may hide
        // a cat (2026-10-31: a White Cat just placed read as unknown made the move unexplained)
        var white = 0;
        var orange = 0;
        var unknown = 0;
        for (var i = 1; i < TeaBoard.Squares; i++)
        {
            white += (trial[i] == TeaKind.WhiteCat ? 1 : 0) - (read[i] == TeaKind.WhiteCat ? 1 : 0);
            orange += (trial[i] == TeaKind.OrangeCat ? 1 : 0) - (read[i] == TeaKind.OrangeCat ? 1 : 0);
            unknown += read[i] == TeaKind.Unknown ? 1 : 0;
        }

        return white >= 0 && orange >= 0 && white + orange <= unknown;
    }

    /// <summary>
    /// The points of a move go to the model, which solves all its moves again (placing values, each level's join value,
    /// chains level by level). Moves with boxed-in cats' joins (Joins -1) and a score that went back (a new round) are
    /// left out.
    /// </summary>
    private void LearnPoints(TeaKind placed, TeaPlacement match, double delta, List<string> learned)
    {
        if (delta < 0 || match.Joins < 0)
        {
            return;
        }

        learned.AddRange(Model.Observe(new TeaPointsObservation(placed, match.Became, match.Joins, match.Top, delta)));
    }

    /// <summary>
    /// A cat on the board, known in one frame of a settled reading and unmatched in another, is the same cat in another
    /// pose (they idle): its other pictures are learned. Only cats: any other square that changes between frames is
    /// in the middle of something (a swap, a join), and 2026-10-29 such a frame taught the tray's Orange Cat as an Oven.
    /// </summary>
    private void LearnMovingPictures(TeaReading reading, List<string> learned)
    {
        foreach (var sq in reading.Squares)
        {
            if (sq.Square == TeaBoard.Tray || sq.Square == reading.Preview || !TeaKinds.IsCat(sq.Kind) || sq.Distance > TeaLabels.MaxDistance / 2 || sq.Prints.Count < 2)
            {
                continue;
            }

            foreach (var p in sq.Prints)
            {
                if (Labels.Classify(p, TeaPictureSource.Board).Kind == TeaKind.Unknown && Labels.Learn(p, sq.Kind, TeaPictureSource.Board))
                {
                    learned.Add($"picture.Board.{sq.Kind}");
                }
            }
        }
    }

    /// <summary>Counts a newly handed-out current piece (the first board, after each move, after a store).</summary>
    private void CountCurrent(TeaReading reading, List<string> learned)
    {
        if (reading.Current == TeaKind.Unknown)
        {
            return;
        }

        Model.Seen[(int)reading.Current]++;
        learned.Add("seen." + reading.Current);
    }

    /// <summary>
    /// A known piece changed on a square or in the tray (both readings know it), or a new current piece came with any
    /// change.
    /// </summary>
    private static bool IsMove(TeaReading before, TeaReading after)
    {
        var any = false;
        for (var i = 0; i < TeaBoard.Squares; i++)
        {
            var a = before.Board[i];
            var b = after.Board[i];
            if (a == b)
            {
                continue;
            }

            any = true;
            if (a != TeaKind.Unknown && b != TeaKind.Unknown)
            {
                return true;
            }
        }

        return any && before.CurrentPrint != after.CurrentPrint;
    }

    /// <summary>The boards agree on every square both read as known (the tray included).</summary>
    private static bool SameKnownSquares(TeaBoard a, TeaBoard b)
    {
        for (var i = 0; i < TeaBoard.Squares; i++)
        {
            if (a[i] != b[i] && a[i] != TeaKind.Unknown && b[i] != TeaKind.Unknown)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameExceptTray(TeaBoard a, TeaBoard b)
    {
        for (var i = 1; i < TeaBoard.Squares; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }
}
