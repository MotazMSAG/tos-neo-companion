using System.Drawing;
using System.Drawing.Drawing2D;

namespace Companion.App.Tray.Today;

/// <summary>
/// The Today list (DAILIES_MODULE.md, UX): what is left grouped by collapsible section, then one "Done today" group.
/// Owner-drawn and double-buffered. A click on a row's circle (or Space on the focused row) ticks or unticks it;
/// the tooltip says how the item is detected. State is shown by shape and fill, never by colour alone.
/// </summary>
internal sealed class ChecklistView : Control
{
    private const int HeaderH = 36;
    private const int RowH = 52;
    private const int DoneRowH = 44;
    private const int Hit = 44;

    private static readonly Color Ink = Color.FromArgb(27, 27, 27);
    private static readonly Color Muted = Color.FromArgb(96, 96, 96);
    private static readonly Color LineColor = Color.FromArgb(238, 241, 245);
    private static readonly Color HeaderLine = Color.FromArgb(227, 231, 237);
    private static readonly Color Hover = Color.FromArgb(246, 248, 251);
    private static readonly Color Ring = Color.FromArgb(138, 148, 166);
    private static readonly Color Accent = Color.FromArgb(0, 95, 184);
    private static readonly Color AccentSoft = Color.FromArgb(213, 227, 243);
    private static readonly Color ChipBack = Color.FromArgb(232, 241, 251);
    private static readonly Color ChipInk = Color.FromArgb(0, 83, 159);
    private static readonly Color Green = Color.FromArgb(46, 160, 67);
    private static readonly Color GreenInk = Color.FromArgb(24, 110, 40);
    private static readonly Color LearnBack = Color.FromArgb(255, 244, 224);
    private static readonly Color LearnInk = Color.FromArgb(138, 75, 0);

    private readonly Font _name = new("Segoe UI", 10f);
    private readonly Font _small = new("Segoe UI", 9f);
    private readonly Font _caps = new("Segoe UI", 8.5f, FontStyle.Bold);
    private readonly Font _bold = new("Segoe UI", 10f, FontStyle.Bold);
    private readonly ToolTip _tip = new() { InitialDelay = 500 };
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private readonly VScrollBar _scroll = new() { Dock = DockStyle.Right, Visible = false };
    private List<Line> _lines = [];
    private TodayView? _view;
    private bool _doneOpen;
    private int _hover = -1;
    private int _focus = -1;
    private string? _tipFor;

    /// <summary>Creates the list.</summary>
    public ChecklistView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.White;
        TabStop = true;
        AccessibleName = "Today's dailies";
        AccessibleRole = AccessibleRole.List;
        Controls.Add(_scroll);
        _scroll.Scroll += (_, _) => Invalidate();
        MouseWheel += (_, e) => ScrollBy(-e.Delta / 120 * RowH);
    }

    /// <summary>The user ticked (true) or unticked an item.</summary>
    public event Action<string, bool>? Toggle;

    /// <summary>The user set a stored count from a row's menu (right-click).</summary>
    public event Action<string, int>? StackSet;

    /// <summary>Shows a new view (keeps collapsed sections, the Done group's state and the scroll position).</summary>
    public void SetView(TodayView view)
    {
        _view = view;
        Rebuild();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _name.Dispose();
            _small.Dispose();
            _caps.Dispose();
            _bold.Dispose();
            _tip.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScroll();
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var width = ClientSize.Width - (_scroll.Visible ? _scroll.Width : 0);
        var y = -Offset;
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            if (y + line.Height >= 0 && y <= ClientSize.Height)
            {
                var r = new Rectangle(0, y, width, line.Height);
                if (i == _hover && line.Kind != LineKind.Empty)
                {
                    using var hb = new SolidBrush(Hover);
                    g.FillRectangle(hb, r);
                }

                switch (line.Kind)
                {
                    case LineKind.Section:
                        DrawHeader(g, r, line.Section!.Title.ToUpperInvariant(), $"{line.Section.Todo.Count} left of {line.Section.Total}", !_collapsed.Contains(line.Section.Id), Color.FromArgb(60, 68, 80), _caps);
                        break;
                    case LineKind.DoneHeader:
                        DrawHeader(g, r, $"Done today ({_view!.Done.Count})", _view.Done.Count > 0 ? "Click a tick to undo it" : string.Empty, _doneOpen, GreenInk, _bold);
                        break;
                    case LineKind.Row:
                        DrawRow(g, r, line.Row!, done: false);
                        break;
                    case LineKind.DoneRow:
                        DrawRow(g, r, line.Row!, done: true);
                        break;
                    default:
                        TextRenderer.DrawText(g, "Everything on your list is done. Edit list adds more.", _name, new Rectangle(8, y, width - 16, line.Height), Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                        break;
                }

                if (i == _focus && Focused)
                {
                    ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(r, -2, -2));
                }
            }

            y += line.Height;
        }
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var i = LineAt(e.Y);
        if (i != _hover)
        {
            _hover = i;
            Invalidate();
        }

        var line = i >= 0 ? _lines[i] : null;
        Cursor = line is { Kind: LineKind.Section or LineKind.DoneHeader } || (line?.Row is not null && e.X < Hit) ? Cursors.Hand : Cursors.Default;
        var tip = line?.Row is { } row ? row.Name + ": " + row.How + (row.Learning ? " (still learning)" : string.Empty)
            + (row.StackCap > 0 ? ". Right-click to set the stored count." : string.Empty) : null;
        if (tip != _tipFor)
        {
            _tipFor = tip;
            _tip.SetToolTip(this, tip);
        }
    }

    /// <inheritdoc />
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        Focus();
        var i = LineAt(e.Y);
        if (i < 0)
        {
            return;
        }

        _focus = i;
        if (e.Button == MouseButtons.Right)
        {
            if (_lines[i].Row is { StackCap: > 0 } row)
            {
                ShowStackMenu(row, e.Location);
            }

            return;
        }

        Activate(i, e.X < Hit);
    }

    private void ShowStackMenu(TodayRow row, Point at)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Set the stored count for " + row.Name) { Enabled = false });
        for (var n = 0; n <= row.StackCap; n++)
        {
            var value = n;
            menu.Items.Add(new ToolStripMenuItem(n + " of " + row.StackCap, null, (_, _) => StackSet?.Invoke(row.Id, value)));
        }

        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(this, at);
    }

    /// <inheritdoc />
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_lines.Count == 0)
        {
            return;
        }

        if (e.KeyCode is Keys.Up or Keys.Down)
        {
            _focus = Math.Clamp(_focus + (e.KeyCode == Keys.Up ? -1 : 1), 0, _lines.Count - 1);
            EnsureVisible(_focus);
            Invalidate();
        }
        else if (e.KeyCode is Keys.Space or Keys.Enter && _focus >= 0)
        {
            Activate(_focus, onCircle: true);
        }
    }

    /// <inheritdoc />
    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (_focus < 0 && _lines.Count > 0)
        {
            _focus = 0;
        }

        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    private int Offset => _scroll.Visible ? _scroll.Value : 0;

    private void Activate(int i, bool onCircle)
    {
        var line = _lines[i];
        switch (line.Kind)
        {
            case LineKind.Section:
                if (!_collapsed.Remove(line.Section!.Id))
                {
                    _collapsed.Add(line.Section.Id);
                }

                Rebuild();
                break;
            case LineKind.DoneHeader:
                _doneOpen = !_doneOpen;
                Rebuild();
                break;
            case LineKind.Row when onCircle:
                Toggle?.Invoke(line.Row!.Id, true);
                break;
            case LineKind.DoneRow when onCircle:
                Toggle?.Invoke(line.Row!.Id, false);
                break;
        }
    }

    private void Rebuild()
    {
        var lines = new List<Line>();
        if (_view is { } v)
        {
            foreach (var s in v.Sections.Where(s => s.Todo.Count > 0))
            {
                lines.Add(new Line(LineKind.Section, HeaderH, s, null));
                if (!_collapsed.Contains(s.Id))
                {
                    lines.AddRange(s.Todo.Select(r => new Line(LineKind.Row, RowH, s, r)));
                }
            }

            if (v.Left == 0 && v.Done.Count > 0)
            {
                lines.Add(new Line(LineKind.Empty, RowH, null, null));
            }

            if (v.Done.Count > 0)
            {
                lines.Add(new Line(LineKind.DoneHeader, HeaderH + 4, null, null));
                if (_doneOpen)
                {
                    lines.AddRange(v.Done.Select(r => new Line(LineKind.DoneRow, DoneRowH, null, r)));
                }
            }
        }

        _lines = lines;
        _focus = Math.Min(_focus, _lines.Count - 1);
        UpdateScroll();
        Invalidate();
    }

    private void UpdateScroll()
    {
        var total = _lines.Sum(l => l.Height);
        var need = total > ClientSize.Height;
        _scroll.Visible = need;
        if (need)
        {
            _scroll.Maximum = total;
            _scroll.LargeChange = Math.Max(1, ClientSize.Height);
            _scroll.SmallChange = RowH;
            _scroll.Value = Math.Clamp(_scroll.Value, 0, Math.Max(0, total - ClientSize.Height));
        }
    }

    private void ScrollBy(int delta)
    {
        if (!_scroll.Visible)
        {
            return;
        }

        _scroll.Value = Math.Clamp(_scroll.Value + delta, 0, Math.Max(0, _scroll.Maximum - _scroll.LargeChange + 1));
        Invalidate();
    }

    private void EnsureVisible(int index)
    {
        var top = _lines.Take(index).Sum(l => l.Height);
        var bottom = top + _lines[index].Height;
        if (top < Offset)
        {
            ScrollBy(top - Offset);
        }
        else if (bottom > Offset + ClientSize.Height)
        {
            ScrollBy(bottom - Offset - ClientSize.Height);
        }
    }

    private int LineAt(int y)
    {
        var at = y + Offset;
        var top = 0;
        for (var i = 0; i < _lines.Count; i++)
        {
            if (at >= top && at < top + _lines[i].Height)
            {
                return i;
            }

            top += _lines[i].Height;
        }

        return -1;
    }

    private void DrawHeader(Graphics g, Rectangle r, string title, string right, bool open, Color ink, Font font)
    {
        using var pen = new Pen(HeaderLine);
        g.DrawLine(pen, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
        var cx = r.Left + 12;
        var cy = r.Top + r.Height / 2;
        using var chev = new Pen(Color.FromArgb(91, 100, 114), 1.6f);
        if (open)
        {
            g.DrawLines(chev, [new Point(cx - 4, cy - 2), new Point(cx, cy + 2), new Point(cx + 4, cy - 2)]);
        }
        else
        {
            g.DrawLines(chev, [new Point(cx - 2, cy - 4), new Point(cx + 2, cy), new Point(cx - 2, cy + 4)]);
        }

        TextRenderer.DrawText(g, title, font, new Rectangle(r.Left + 26, r.Top, r.Width - 230, r.Height), ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, right, _small, new Rectangle(r.Right - 210, r.Top, 200, r.Height), Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }

    private void DrawRow(Graphics g, Rectangle r, TodayRow row, bool done)
    {
        using (var pen = new Pen(LineColor))
        {
            g.DrawLine(pen, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
        }

        var circle = new Rectangle(r.Left + 11, r.Top + (r.Height - 22) / 2, 22, 22);
        if (done && row.Missed)
        {
            // missed: a grey ring with a dash (shape, not colour alone)
            using var ring = new Pen(Ring, 2f);
            g.DrawEllipse(ring, Rectangle.Inflate(circle, -1, -1));
            using var dash = new Pen(Muted, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(dash, circle.Left + 7, circle.Top + 11, circle.Right - 7, circle.Top + 11);
        }
        else if (done)
        {
            using var fill = new SolidBrush(Green);
            g.FillEllipse(fill, circle);
            using var tick = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(tick, [new Point(circle.Left + 6, circle.Top + 11), new Point(circle.Left + 10, circle.Top + 15), new Point(circle.Left + 16, circle.Top + 7)]);
        }
        else if (row.Fill > 0)
        {
            using var back = new SolidBrush(AccentSoft);
            using var part = new SolidBrush(Accent);
            using var inner = new SolidBrush(BackColor);
            g.FillEllipse(back, circle);
            g.FillPie(part, circle, -90, (float)(360 * row.Fill));
            g.FillEllipse(inner, Rectangle.Inflate(circle, -5, -5));
        }
        else
        {
            using var pen = new Pen(Ring, 2f);
            g.DrawEllipse(pen, Rectangle.Inflate(circle, -1, -1));
        }

        var textLeft = r.Left + Hit + 4;
        var right = r.Right - 12;
        if (row.Chip is { } chip)
        {
            var size = TextRenderer.MeasureText(chip, _bold);
            var chipRect = new Rectangle(right - size.Width - 8, r.Top + (r.Height - 20) / 2, size.Width + 8, 20);
            using var cb = new SolidBrush(ChipBack);
            using var path = Rounded(chipRect, 10);
            g.FillPath(cb, path);
            TextRenderer.DrawText(g, chip, _bold, chipRect, ChipInk, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            right = chipRect.Left - 8;
        }

        if (done)
        {
            var detailSize = TextRenderer.MeasureText(row.Detail, _small);
            TextRenderer.DrawText(g, row.Detail, _small, new Rectangle(right - detailSize.Width, r.Top, detailSize.Width, r.Height), Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            TextRenderer.DrawText(g, row.Name, _name, new Rectangle(textLeft, r.Top, right - detailSize.Width - textLeft - 8, r.Height), Color.FromArgb(60, 68, 80), TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            return;
        }

        var nameSize = TextRenderer.MeasureText(row.Name, _name);
        TextRenderer.DrawText(g, row.Name, _name, new Rectangle(textLeft, r.Top + 7, right - textLeft, 20), Ink, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        if (row.Learning)
        {
            var learn = new Rectangle(textLeft + nameSize.Width + 4, r.Top + 9, TextRenderer.MeasureText("learning", _small).Width + 6, 17);
            if (learn.Right < right)
            {
                using var lb = new SolidBrush(LearnBack);
                using var path = Rounded(learn, 8);
                g.FillPath(lb, path);
                TextRenderer.DrawText(g, "learning", _small, learn, LearnInk, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        TextRenderer.DrawText(g, row.Detail, _small, new Rectangle(textLeft, r.Top + 27, right - textLeft, 18), Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private enum LineKind
    {
        Section,
        Row,
        DoneHeader,
        DoneRow,
        Empty,
    }

    private sealed record Line(LineKind Kind, int Height, TodaySection? Section, TodayRow? Row);
}
