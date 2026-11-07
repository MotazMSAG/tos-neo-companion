using System.Text;

namespace Companion.TeaHouse;

/// <summary>
/// The 6x6 board, row by row from the top-left; square 0 (top-left) is the food tray: it holds one stored piece, is
/// never a place for a piece and never joins. Squares are numbered row * 6 + column. Mutable and cheap to copy (the
/// move finder copies it for every trial).
/// </summary>
public sealed class TeaBoard
{
    /// <summary>Squares per side.</summary>
    public const int Side = 6;

    /// <summary>Squares in all (tray included).</summary>
    public const int Squares = Side * Side;

    /// <summary>The tray's square.</summary>
    public const int Tray = 0;

    /// <summary>The up, down, left and right neighbours of each square, the tray left out.</summary>
    internal static readonly int[][] Neighbours = BuildNeighbours();

    private readonly TeaKind[] _cells;

    /// <summary>An empty board with an empty tray.</summary>
    public TeaBoard()
    {
        _cells = new TeaKind[Squares];
    }

    private TeaBoard(TeaKind[] cells)
    {
        _cells = cells;
    }

    /// <summary>The square's content (square 0: the tray's piece).</summary>
    public TeaKind this[int square]
    {
        get => _cells[square];
        set => _cells[square] = value;
    }

    /// <summary>The tray's piece (<see cref="TeaKind.Empty"/> when nothing is stored).</summary>
    public TeaKind TrayPiece
    {
        get => _cells[Tray];
        set => _cells[Tray] = value;
    }

    /// <summary>Empty play squares (the tray not counted).</summary>
    public int EmptyCount
    {
        get
        {
            var n = 0;
            for (var i = 1; i < Squares; i++)
            {
                if (_cells[i] == TeaKind.Empty)
                {
                    n++;
                }
            }

            return n;
        }
    }

    /// <summary>A copy.</summary>
    public TeaBoard Clone() => new((TeaKind[])_cells.Clone());

    /// <summary>Copies this board into <paramref name="target"/> (no allocation).</summary>
    public void CopyTo(TeaBoard target) => Array.Copy(_cells, target._cells, Squares);

    /// <summary>True when both boards hold the same pieces (tray included).</summary>
    public bool SameAs(TeaBoard other) => _cells.AsSpan().SequenceEqual(other._cells);

    /// <summary>Row of a square.</summary>
    public static int Row(int square) => square / Side;

    /// <summary>Column of a square.</summary>
    public static int Column(int square) => square % Side;

    /// <summary>"row 3, column 4" (1-based, for the user).</summary>
    public static string Where(int square) => $"row {Row(square) + 1}, column {Column(square) + 1}";

    /// <summary>
    /// Parses six groups of six <see cref="TeaKinds.Code"/> characters separated by '/' (the first character is the
    /// tray's piece), e.g. "v.w.../......".
    /// </summary>
    public static TeaBoard Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var rows = text.Split('/');
        if (rows.Length != Side || rows.Any(r => r.Length != Side))
        {
            throw new FormatException("a board is 6 rows of 6 squares separated by '/'");
        }

        var board = new TeaBoard();
        for (var r = 0; r < Side; r++)
        {
            for (var c = 0; c < Side; c++)
            {
                if (!TeaKinds.TryParse(rows[r][c], out var kind))
                {
                    throw new FormatException($"unknown square '{rows[r][c]}'");
                }

                board[(r * Side) + c] = kind;
            }
        }

        return board;
    }

    /// <summary>The board for the journal, whose codes allow no '?': an unknown square is written 'u'.</summary>
    public string ToJournal() => ToString().Replace('?', 'u');

    /// <inheritdoc />
    public override string ToString()
    {
        var sb = new StringBuilder((Side + 1) * Side);
        for (var i = 0; i < Squares; i++)
        {
            if (i > 0 && i % Side == 0)
            {
                sb.Append('/');
            }

            sb.Append(TeaKinds.Code(_cells[i]));
        }

        return sb.ToString();
    }

    private static int[][] BuildNeighbours()
    {
        var all = new int[Squares][];
        for (var i = 0; i < Squares; i++)
        {
            var r = Row(i);
            var c = Column(i);
            var list = new List<int>(4);
            if (r > 0)
            {
                list.Add(i - Side);
            }

            if (r < Side - 1)
            {
                list.Add(i + Side);
            }

            if (c > 0)
            {
                list.Add(i - 1);
            }

            if (c < Side - 1)
            {
                list.Add(i + 1);
            }

            list.Remove(Tray);
            all[i] = [.. list];
        }

        return all;
    }
}
