namespace Companion.TeaHouse;

/// <summary>
/// A learned board value (an n-tuple network, the method behind the strongest 2048 and Threes players): the board is seen
/// through many small windows of 4 squares (every row stretch, column stretch and 2x2 block), and each window's exact
/// contents have a learned weight; a board's value is the sum of its windows' weights, plus small tables for the tray's
/// piece and the number of empty squares. The weights are learned by self-play (<see cref="TeaTrainer"/>): after every
/// move the value of the board before is nudged toward the points that came plus the value of the board after (temporal
/// difference learning), so the network finds out by itself which shapes lead to big joins: stairs of levels, pairs with
/// room, cats penned together. Values are in hundreds of points. Reads are thread-safe; updates race by design (lock-free
/// training, the usual practice).
/// </summary>
public sealed class TeaNet
{
    /// <summary>Symbols per square (4 bits).</summary>
    public const int Symbols = 16;

    /// <summary>Squares per window.</summary>
    public const int TupleSize = 4;

    /// <summary>Points per value unit (the network counts in hundreds of points).</summary>
    public const double Scale = 100;

    private const int TableSize = Symbols * Symbols * Symbols * Symbols;
    private const uint Magic = 0x54454131; // "TEA1": windows, tray and room only
    private const uint MagicFeatures = 0x54454132; // "TEA2": also the expert features (rules 5, 6, 7, 9)

    /// <summary>The windows: 4 squares each (rows, columns, 2x2 blocks).</summary>
    internal static readonly int[][] Tuples = BuildTuples();

    private readonly float[][] _tables;
    private readonly float[] _tray = new float[Symbols * 8];
    private readonly float[] _empty = new float[TeaBoard.Squares + 1];
    private readonly float[][] _features = [.. TeaFeatures.Sizes.Select(n => new float[n])];

    /// <summary>
    /// True for a network that also sees the expert features (<see cref="TeaFeatures"/>: tea sockets and cat pens, lifelines
    /// and chains, endgame coverage, joker readiness), whose values self-play learns. A TEA1 network does not.
    /// </summary>
    public bool UsesFeatures { get; init; }

    /// <summary>A network with all weights 0.</summary>
    public TeaNet()
    {
        _tables = new float[Tuples.Length][];
        for (var i = 0; i < Tuples.Length; i++)
        {
            _tables[i] = new float[TableSize];
        }
    }

    /// <summary>Training rounds played so far (for the file and the helper's window).</summary>
    public long Trained { get; set; }

    /// <summary>Average points of the last training rounds (for the file and the helper's window).</summary>
    public double RecentScore { get; set; }

    /// <summary>The square's symbol: the kind's own number for the ladders, one symbol for any blocker, the tray square a wall.</summary>
    internal static int Symbol(TeaKind k, int square) => square == TeaBoard.Tray ? 15 : k switch
    {
        TeaKind.Empty => 0,
        >= TeaKind.Cookie and <= TeaKind.TeaHouse => (int)k,
        TeaKind.WhiteCat => 13,
        TeaKind.OrangeCat => 14,
        _ => 12, // Black Tea, unknown
    };

    /// <summary>The board's value in points (what it promises for the rest of the round, by the learned weights).</summary>
    public double Value(TeaBoard board) => Raw(board) * Scale;

    /// <summary>Moves the board's value toward <paramref name="target"/> points by the share <paramref name="rate"/> of the gap.</summary>
    public void Learn(TeaBoard board, double target, double rate)
    {
        Span<(int Table, int Index)> features = stackalloc (int, int)[TeaFeatures.Tables + TeaBoard.Squares];
        var count = UsesFeatures ? TeaFeatures.Indices(board, features) : 0;
        var error = (target / Scale) - Raw(board, features[..count]);
        var step = (float)(rate * error / (Tuples.Length + 2 + count));
        for (var t = 0; t < Tuples.Length; t++)
        {
            _tables[t][Index(board, Tuples[t])] += step;
        }

        _tray[TrayIndex(board)] += step;
        _empty[board.EmptyCount] += step;
        foreach (var (table, index) in features[..count])
        {
            _features[table][index] += step;
        }
    }

    /// <summary>Writes the weights (binary, little-endian).</summary>
    public void Save(Stream stream)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(UsesFeatures ? MagicFeatures : Magic);
        w.Write(Tuples.Length);
        w.Write(Trained);
        w.Write(RecentScore);
        foreach (var table in _tables)
        {
            Write(w, table);
        }

        Write(w, _tray);
        Write(w, _empty);
        if (UsesFeatures)
        {
            foreach (var table in _features)
            {
                Write(w, table);
            }
        }
    }

    /// <summary>Reads weights written by <see cref="Save"/>; null when the file is of another layout or damaged.</summary>
    public static TeaNet? Load(Stream stream)
    {
        try
        {
            using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            var magic = r.ReadUInt32();
            if ((magic != Magic && magic != MagicFeatures) || r.ReadInt32() != Tuples.Length)
            {
                return null;
            }

            var net = new TeaNet { Trained = r.ReadInt64(), RecentScore = r.ReadDouble(), UsesFeatures = magic == MagicFeatures };
            foreach (var table in net._tables)
            {
                Read(r, table);
            }

            Read(r, net._tray);
            Read(r, net._empty);
            if (net.UsesFeatures)
            {
                foreach (var table in net._features)
                {
                    Read(r, table);
                }
            }

            return net;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    private double Raw(TeaBoard board)
    {
        if (!UsesFeatures)
        {
            return Raw(board, []);
        }

        Span<(int Table, int Index)> features = stackalloc (int, int)[TeaFeatures.Tables + TeaBoard.Squares];
        var count = TeaFeatures.Indices(board, features);
        return Raw(board, features[..count]);
    }

    private double Raw(TeaBoard board, ReadOnlySpan<(int Table, int Index)> features)
    {
        double v = 0;
        for (var t = 0; t < Tuples.Length; t++)
        {
            v += _tables[t][Index(board, Tuples[t])];
        }

        foreach (var (table, index) in features)
        {
            v += _features[table][index];
        }

        return v + _tray[TrayIndex(board)] + _empty[board.EmptyCount];
    }

    private static int Index(TeaBoard board, int[] tuple) =>
        (Symbol(board[tuple[0]], tuple[0]) << 12) | (Symbol(board[tuple[1]], tuple[1]) << 8) | (Symbol(board[tuple[2]], tuple[2]) << 4) | Symbol(board[tuple[3]], tuple[3]);

    /// <summary>The tray's piece with the room left (8 bands of empty squares).</summary>
    private static int TrayIndex(TeaBoard board)
    {
        var tray = board.TrayPiece switch
        {
            TeaKind.Empty => 0,
            TeaKind.Oven => 1,
            TeaKind.Cutlery => 2,
            TeaKind.WhiteCat => 3,
            TeaKind.OrangeCat => 4,
            >= TeaKind.Cookie and <= TeaKind.Luxury => 4 + (int)board.TrayPiece,
            _ => 15,
        };
        return (tray * 8) + Math.Min(7, board.EmptyCount / 5);
    }

    private static int[][] BuildTuples()
    {
        var list = new List<int[]>();
        const int n = TeaBoard.Side;
        for (var r = 0; r < n; r++)
        {
            for (var c = 0; c + TupleSize <= n; c++)
            {
                list.Add([(r * n) + c, (r * n) + c + 1, (r * n) + c + 2, (r * n) + c + 3]);
                list.Add([(c * n) + r, ((c + 1) * n) + r, ((c + 2) * n) + r, ((c + 3) * n) + r]);
            }
        }

        for (var r = 0; r + 1 < n; r++)
        {
            for (var c = 0; c + 1 < n; c++)
            {
                list.Add([(r * n) + c, (r * n) + c + 1, ((r + 1) * n) + c, ((r + 1) * n) + c + 1]);
            }
        }

        return [.. list];
    }

    private static void Write(BinaryWriter w, float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        w.Write(bytes);
    }

    private static void Read(BinaryReader r, float[] values)
    {
        var bytes = r.ReadBytes(values.Length * sizeof(float));
        if (bytes.Length != values.Length * sizeof(float))
        {
            throw new EndOfStreamException();
        }

        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
    }
}
