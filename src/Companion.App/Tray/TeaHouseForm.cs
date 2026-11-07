using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Companion.App.Hosting;
using Companion.Contracts;
using Companion.Contracts.Vision;
using Companion.TeaHouse;

namespace Companion.App.Tray;

/// <summary>
/// The Kitty Tea House helper window (2026-10-29): a copy of the game's board with the best square outlined in green
/// (the second best in amber), the advice in words, the current piece, the tray and the score. A piece the helper does
/// not know yet shows a "?"; clicking it (or the current piece) lets the user name it once. It only watches: the user
/// places every piece in the game.
/// </summary>
internal sealed class TeaHouseForm : Form
{
    private const int Cell = 74;

    /// <summary>Expected points within which the top two options count as about equal (the learned values' noise).</summary>
    private const double CloseCall = 300;

    private readonly TeaHouseHelper _helper;
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 8, 8, 0) };
    private readonly Label _advice = new() { AutoSize = false, Dock = DockStyle.Top, Height = 58, Padding = new Padding(8, 6, 8, 0) };
    private readonly BoardView _board = new() { Dock = DockStyle.Top, Height = (Cell * TeaBoard.Side) + 16 };
    private readonly Label _info = new() { AutoSize = false, Dock = DockStyle.Top, Height = 64, Padding = new Padding(8, 4, 8, 0) };
    private readonly Label _learned = new() { AutoSize = false, Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8), ForeColor = Color.DimGray };
    private readonly Button _played = new() { Text = "I played: read again", Dock = DockStyle.Top, Height = 44 };
    private readonly CheckBox _keep = new() { Text = "Keep watching (no click needed; no deeper thinking)", Dock = DockStyle.Top, Height = 26, Padding = new Padding(8, 0, 0, 0) };
    private readonly CheckBox _explore = new() { Text = "Find out points: aim for cakes and teas whose points are not known yet", Dock = DockStyle.Top, Height = 26, Padding = new Padding(8, 0, 0, 0) };
    private readonly CheckBox _fast = new() { Text = "Fast: no look-ahead (the learned brain picks each move at once)", Dock = DockStyle.Top, Height = 26, Padding = new Padding(8, 0, 0, 0) };
    private readonly CheckBox _auto = new() { Text = "Auto-play: the companion presses each move (keep a window over the game under your mouse pointer)", Dock = DockStyle.Top, Height = 26, Padding = new Padding(8, 0, 0, 0) };
    private bool _syncingAuto;
    private readonly TeaHouseHands? _hands;
    private TeaHouseView? _view;

    public TeaHouseForm(HostServices services, Func<bool> jobRunning, TeaHouseHands? hands = null)
    {
        _hands = hands;
        Text = "Kitty Tea House helper";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size((Cell * TeaBoard.Side) + 260, (Cell * TeaBoard.Side) + 336);
        MinimumSize = new Size(560, 520);
        Font = new Font("Segoe UI", 10f);
        _advice.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
        _advice.ForeColor = Color.DarkGreen;
        _played.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        Controls.Add(_learned);
        Controls.Add(_explore);
        Controls.Add(_keep);
        Controls.Add(_fast);
        if (hands is not null)
        {
            Controls.Add(_auto);
        }

        Controls.Add(_info);
        Controls.Add(_board);
        Controls.Add(_played);
        Controls.Add(_advice);
        Controls.Add(_status);
        _board.SquareClicked += OnSquareClicked;
        _helper = new TeaHouseHelper(services, jobRunning, v =>
        {
            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke(() => Render(v));
            }
        }, hands);
        _status.Text = "Starting...";
        _played.Click += (_, _) => Played();
        _keep.CheckedChanged += (_, _) => _helper.KeepWatching = _keep.Checked;
        _explore.CheckedChanged += (_, _) => _helper.Explore = _explore.Checked;
        _fast.CheckedChanged += (_, _) => _helper.Fast = _fast.Checked;
        _auto.CheckedChanged += (_, _) =>
        {
            if (!_syncingAuto)
            {
                _helper.AutoPlay = _auto.Checked;
            }
        };
        Shown += (_, _) => _helper.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // closing the window ends auto-play (the job ends after the press in progress)
        _helper.AutoPlay = false;
        _helper.Dispose();
        _hands?.Dispose();
        _board.DisposeImage();
        base.OnFormClosed(e);
    }

    private void Render(TeaHouseView v)
    {
        if (IsDisposed)
        {
            return;
        }

        _view = v;
        _status.Text = v.Status;
        if (_auto.Checked != _helper.AutoPlay)
        {
            // auto-play switched itself off (presses that did not land, a refused job): the box follows
            _syncingAuto = true;
            _auto.Checked = _helper.AutoPlay;
            _syncingAuto = false;
        }

        _played.BackColor = v.Waiting ? Color.FromArgb(200, 240, 200) : SystemColors.Control;
        _played.Text = v.Waiting ? "I played: read again" : "Reading the board...";
        var r = v.Reading;
        if (r is null || !r.OnScreen)
        {
            _advice.Text = string.Empty;
            _info.Text = string.Empty;
            _board.Set(null, null, []);
        }
        else
        {
            _board.Set(v.Frame, r, v.Advice);
            var unknown = r.Current == TeaKind.Unknown || r.UnknownCount > 0;
            _advice.ForeColor = unknown ? Color.Firebrick : Color.DarkGreen;
            _advice.Text = unknown
                ? "STOP: name the red ? first (click it). No advice until every piece is known."
                : v.Thinking ? "Thinking..." : AdviceText(r, v.Advice);
            if (v.Thinking && !unknown)
            {
                _advice.ForeColor = Color.DimGray;
            }
            var tray = r.Board.TrayPiece;
            _info.Text = $"Current: {TeaKinds.Name(r.Current)}   Tray: {(tray == TeaKind.Empty ? "empty" : TeaKinds.Name(tray))}   Score: {(v.Score is { } s ? s.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "?")}"
                + Environment.NewLine + (v.Outlook ?? "Outlook: none yet (it needs the score on screen and the forecast file)")
                + Environment.NewLine + "Click a ? square to name it; Shift+click any square to rename it; click elsewhere on the board after your move.";
        }

        _learned.Text = $"Advice by the {_helper.Brain}. Pieces counted: {v.Counted}" + (v.Learned.Count > 0 ? Environment.NewLine + "Learned: " + string.Join(", ", v.Learned.Take(6)) : string.Empty);
    }

    /// <summary>"I played": the old advice leaves the window at once, the new one comes with the next reading.</summary>
    private void Played()
    {
        if (_view is { } v)
        {
            Render(v with { Advice = [], Thinking = true, Waiting = false, Status = "Reading the board..." });
        }

        _helper.RequestRead();
    }

    private static string AdviceText(TeaReading r, IReadOnlyList<TeaAdvice> advice)
    {
        if (advice.Count == 0)
        {
            return r.Current == TeaKind.Unknown ? "Name the current piece to get advice." : "No move fits.";
        }

        var a = advice[0];
        var what = a.Action switch
        {
            TeaAction.Store => $"Put the {TeaKinds.Name(a.Piece)} in the tray (top-left)",
            TeaAction.Swap => $"1) Tap the tray to swap, then 2) place the {TeaKinds.Name(a.Piece)} at {TeaBoard.Where(a.Square)}",
            _ => $"Place the {TeaKinds.Name(a.Piece)} at {TeaBoard.Where(a.Square)}",
        };
        if (a.Piece == TeaKind.Oven)
        {
            what += a.Became == TeaKind.BlackTea ? " (it turns into Black Tea)" : $" (as a {TeaKinds.Name(a.Became)})";
        }

        if (a.Joins > 0 && a.Top != TeaKind.Empty)
        {
            what += $": makes a {TeaKinds.Name(a.Top)}";
        }

        if (a.Reason is { } why)
        {
            what += $"   ({why})";
        }

        // the user (2026-10-29) questioned a choice that was 0.2 % ahead: say when the top two are about equal
        else if (advice.Count > 1 && advice[1].Depth == a.Depth && Math.Abs(a.Value - advice[1].Value) < CloseCall)
        {
            what += $"   (close call: option 2 is about as good, {a.Value - advice[1].Value:+0;-0} points)";
        }

        return what;
    }

    private void OnSquareClicked(int square, Point screen)
    {
        var r = _view?.Reading;
        if (r is null)
        {
            return;
        }

        IReadOnlyList<string> prints;
        TeaPictureSource source;
        if (square == BoardView.CurrentBox)
        {
            prints = r.CurrentPrint is { } cp ? [cp] : [];
            source = TeaPictureSource.Panel;
        }
        else
        {
            var sq = r.Squares[square];
            prints = sq.Prints.Count > 0 ? sq.Prints : sq.Print is { } p ? [p] : [];
            source = square == TeaBoard.Tray ? TeaPictureSource.Tray : TeaPictureSource.Board;
            if (square == r.Preview || (sq.Kind != TeaKind.Unknown && Control.ModifierKeys != Keys.Shift))
            {
                // a known square (or the preview): the user tells the helper they played (Shift+click still lets them rename it)
                Played();
                return;
            }
        }

        if (prints.Count == 0)
        {
            return;
        }

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("This piece is...") { Enabled = false });
        var kinds = Enum.GetValues<TeaKind>().Where(k => k is not TeaKind.Unknown && (k != TeaKind.Empty || source == TeaPictureSource.Tray));
        foreach (var k in kinds)
        {
            var kind = k;
            menu.Items.Add(TeaKinds.Name(kind), null, (_, _) => _helper.Name(prints, kind, source));
        }

        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(screen);
    }

    /// <summary>The drawn board: square pictures from the capture, the advice outlined, unknown squares marked.</summary>
    private sealed class BoardView : Control
    {
        /// <summary>The click target number of the current-piece box.</summary>
        public const int CurrentBox = -2;

        private Bitmap? _image;
        private TeaReading? _reading;
        private IReadOnlyList<TeaAdvice> _advice = [];
        private Bitmap? _current;

        public BoardView()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(60, 45, 35);
        }

        public event Action<int, Point>? SquareClicked;

        public void Set(Frame? frame, TeaReading? reading, IReadOnlyList<TeaAdvice> advice)
        {
            DisposeImage();
            _reading = reading;
            _advice = advice;
            if (frame is not null && reading is not null)
            {
                var first = TeaGeometry.Square(0);
                var last = TeaGeometry.Square(TeaBoard.Squares - 1);
                _image = ToBitmap(frame, new RectI(first.Left, first.Top, last.Right, last.Bottom));
                _current = ToBitmap(frame, TeaGeometry.CurrentIcon);
            }

            Invalidate();
        }

        public void DisposeImage()
        {
            _image?.Dispose();
            _image = null;
            _current?.Dispose();
            _current = null;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            var screen = PointToScreen(e.Location);
            if (CurrentRect().Contains(e.Location))
            {
                SquareClicked?.Invoke(CurrentBox, screen);
                return;
            }

            var col = (e.X - 8) / Cell;
            var row = (e.Y - 8) / Cell;
            if (col is >= 0 and < TeaBoard.Side && row is >= 0 and < TeaBoard.Side && e.X >= 8 && e.Y >= 8)
            {
                SquareClicked?.Invoke((row * TeaBoard.Side) + col, screen);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            if (_image is null || _reading is null)
            {
                using var hint = new SolidBrush(Color.Gainsboro);
                g.DrawString("The board appears here once Kitty Tea House is open in the game.", Font, hint, new RectangleF(12, 12, Width - 24, 60));
                return;
            }

            var first = TeaGeometry.Square(0);
            for (var i = 0; i < TeaBoard.Squares; i++)
            {
                var src = TeaGeometry.Square(i);
                var dst = SquareRect(i);
                g.DrawImage(_image, dst, new Rectangle(src.Left - first.Left, src.Top - first.Top, src.Width, src.Height), GraphicsUnit.Pixel);
                var sq = _reading.Squares[i];
                if (i == _reading.Preview)
                {
                    using var shade = new SolidBrush(Color.FromArgb(150, 255, 250, 235));
                    g.FillRectangle(shade, dst);
                }
                else if (sq.Kind == TeaKind.Unknown)
                {
                    using var red = new SolidBrush(Color.FromArgb(220, 200, 30, 30));
                    using var border = new Pen(Color.Red, 5f);
                    using var big = new Font(Font.FontFamily, 26f, FontStyle.Bold);
                    var box = dst;
                    box.Inflate(-3, -3);
                    g.DrawRectangle(border, box);
                    g.DrawString("?", big, red, dst.X + (dst.Width / 2f) - 12, dst.Y + (dst.Height / 2f) - 22);
                }
            }

            // the user (2026-10-29): "my eyes always go to the no.1 green square, which should be on the tray if it wants me
            // to swap; the dotted square should be the decision after the swap"
            for (var n = Math.Min(2, _advice.Count) - 1; n >= 0; n--)
            {
                var a = _advice[n];
                var colour = n == 0 ? Color.LimeGreen : Color.Orange;
                var target = a.Action is TeaAction.Store or TeaAction.Swap ? TeaBoard.Tray : a.Square;
                Mark(g, target, colour, n == 0 ? 6f : 3f, (n + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), dashed: false);
                if (a.Action == TeaAction.Swap)
                {
                    Mark(g, a.Square, colour, 3f, "then", dashed: true);
                }
            }

            var cr = CurrentRect();
            if (_current is not null)
            {
                g.DrawImage(_current, cr);
            }

            using var white = new SolidBrush(Color.WhiteSmoke);
            g.DrawString("Current", Font, white, cr.X, cr.Bottom + 4);
            if (_reading.Current == TeaKind.Unknown)
            {
                using var red = new Pen(Color.Red, 3f);
                g.DrawRectangle(red, cr);
            }
        }

        private void Mark(Graphics g, int square, Color colour, float width, string tag, bool dashed)
        {
            var r = SquareRect(square);
            r.Inflate(-3, -3);
            using var pen = new Pen(colour, width) { DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid };
            g.DrawRectangle(pen, r);
            using var fill = new SolidBrush(colour);
            using var bold = new Font(Font.FontFamily, dashed ? 9f : 12f, FontStyle.Bold);
            var size = g.MeasureString(tag, bold);
            g.FillRectangle(fill, r.X + 2, r.Y + 2, size.Width + 4, size.Height);
            g.DrawString(tag, bold, Brushes.Black, r.X + 4, r.Y + 2);
        }

        private static Rectangle SquareRect(int square) =>
            new(8 + (TeaBoard.Column(square) * Cell), 8 + (TeaBoard.Row(square) * Cell), Cell - 2, Cell - 2);

        private static Rectangle CurrentRect() => new(8 + (Cell * TeaBoard.Side) + 24, 16, 96, 96);

        private static Bitmap ToBitmap(Frame f, RectI r)
        {
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, r.Width, r.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[r.Width * 4];
                for (var y = 0; y < r.Height; y++)
                {
                    Buffer.BlockCopy(f.Bgra, ((r.Top + y) * f.Stride) + (r.Left * 4), row, 0, row.Length);
                    for (var x = 3; x < row.Length; x += 4)
                    {
                        row[x] = 255;
                    }

                    Marshal.Copy(row, 0, data.Scan0 + (y * data.Stride), row.Length);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            return bmp;
        }
    }
}
