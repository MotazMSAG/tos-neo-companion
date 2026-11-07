using System.Drawing;
namespace Companion.App.Tray.Today;

/// <summary>
/// The window's home page (DAILIES_MODULE.md, approved 2026-11-04): the daily checklist. Left: "10 left", progress, the
/// reset countdown, Edit list and the list itself; right (below on a narrow window): Coming up today and This week.
/// Same tokens as the rest of the window (Segoe UI, 8 px grid, the accent blue, CardBack cards). Reads the checklist
/// service only; never touches the game.
/// </summary>
internal sealed class TodayPage : TabPage
{
    private static readonly Color Muted = Color.FromArgb(96, 96, 96);
    private static readonly Color CardBack = Color.FromArgb(242, 245, 250);
    private const int SideWidth = 300;

    private readonly ITodayBackend _backend;
    private readonly Label _left = new() { AutoSize = true, Font = new Font("Segoe UI", 14f, FontStyle.Bold), Location = new Point(0, 0) };
    private readonly Label _done = new() { AutoSize = true, Font = new Font("Segoe UI", 9f), ForeColor = Muted, Location = new Point(2, 30) };
    private readonly Label _reset = new() { AutoSize = true, Font = new Font("Segoe UI", 9f), ForeColor = Muted };
    private readonly Button _edit = new() { Text = "Edit list", Width = 104, Height = 36, Font = new Font("Segoe UI", 10f) };
    private readonly Panel _progress = new() { Dock = DockStyle.Top, Height = 8 };
    private readonly Label _banner = new()
    {
        Dock = DockStyle.Top, AutoSize = false, Height = 36, Visible = false, Font = new Font("Segoe UI", 10f),
        ForeColor = Color.FromArgb(20, 64, 110), BackColor = Color.FromArgb(238, 244, 251), Padding = new Padding(10, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft,
    };
    private readonly ChecklistView _list = new() { Dock = DockStyle.Fill };
    private readonly Panel _side = new() { Dock = DockStyle.Right, Width = SideWidth, Padding = new Padding(16, 0, 0, 0), AutoScroll = true };
    private readonly FlowLayoutPanel _cards = new() { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly ToolTip _tips = new() { InitialDelay = 400 };
    private TodayView? _view;
    private long _version = -1;
    private DailyKey _shown;

    /// <summary>Creates the page.</summary>
    public TodayPage(ITodayBackend backend)
    {
        _backend = backend;
        Text = "Today";
        Name = "Today";
        Padding = new Padding(16);
        BackColor = Color.White;

        var header = new Panel { Dock = DockStyle.Top, Height = 56 };
        header.Controls.Add(_left);
        header.Controls.Add(_done);
        header.Controls.Add(_reset);
        header.Controls.Add(_edit);
        header.Resize += (_, _) => LayoutHeader(header);
        _edit.Click += (_, _) => EditList();

        var gap1 = new Panel { Dock = DockStyle.Top, Height = 8 };
        var gap2 = new Panel { Dock = DockStyle.Top, Height = 12 };
        _progress.Paint += (_, e) => PaintProgress(e.Graphics);

        var main = new Panel { Dock = DockStyle.Fill };
        main.Controls.Add(_list);
        main.Controls.Add(gap2);
        main.Controls.Add(_banner);
        main.Controls.Add(_progress);
        main.Controls.Add(gap1);
        main.Controls.Add(header);

        _side.Controls.Add(_cards);
        Controls.Add(main);
        Controls.Add(_side);
        Resize += (_, _) => Arrange();
        _list.Toggle += (id, done) =>
        {
            _backend.SetDone(id, done);
            Refresh(force: true);
        };
        _list.StackSet += (id, value) =>
        {
            _backend.SetStack(id, value);
            Refresh(force: true);
        };
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Called every second by the window: repaints when the list, the minute or the choices changed.</summary>
    public void Tick() => Refresh(force: false);

    /// <summary>Items left now (for the tray), or null before the first refresh.</summary>
    public int? ItemsLeft => _view?.Left;

    private void Refresh(bool force)
    {
        DailyKey key;
        Companion.Dailies.DailySnapshot snap;
        Companion.Dailies.DailyPrefs prefs;
        bool replaying;
        try
        {
            snap = _backend.Snapshot();
            prefs = _backend.Prefs;
            replaying = _backend.Replaying;
            key = new DailyKey(snap.Version, snap.AsOf.Hour * 60 + snap.AsOf.Minute, prefs, replaying);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return; // the next tick tries again
        }

        if (!force && key == _shown)
        {
            return;
        }

        _shown = key;
        var view = TodayModel.Build(snap, prefs, replaying);
        _view = view;
        Text = view.TabText;
        _left.Text = view.LeftText;
        _done.Text = view.DoneText;
        _reset.Text = view.ResetText;
        _banner.Text = view.Banner ?? string.Empty;
        _banner.Visible = view.Banner is not null;
        _progress.Invalidate();
        _list.SetView(view);
        if (snap.Version != _version || force || key.Replaying)
        {
            _version = snap.Version;
        }

        BuildCards(view);
        LayoutHeader((Panel)_left.Parent!);
    }

    private void EditList()
    {
        try
        {
            using var dialog = new EditListDialog(_backend.Snapshot(), _backend.Prefs);
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK && dialog.Result is { } prefs)
            {
                _backend.SetPrefs(prefs);
                Refresh(force: true);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // the list stays as it was
        }
    }

    private void Arrange()
    {
        var narrow = ClientSize.Width < 820;
        _side.Dock = narrow ? DockStyle.Bottom : DockStyle.Right;
        _side.Width = SideWidth;
        _side.Height = narrow ? 200 : _side.Height;
        _side.Padding = narrow ? new Padding(0, 12, 0, 0) : new Padding(16, 0, 0, 0);
        _cards.FlowDirection = narrow ? FlowDirection.LeftToRight : FlowDirection.TopDown; // side by side under a narrow list
    }

    private void LayoutHeader(Panel header)
    {
        _edit.Location = new Point(header.Width - _edit.Width, 10);
        _reset.Location = new Point(_edit.Left - _reset.PreferredWidth - 12, 20);
    }

    private void PaintProgress(Graphics g)
    {
        var r = _progress.ClientRectangle;
        using var back = new SolidBrush(Color.FromArgb(230, 234, 240));
        g.FillRectangle(back, r);
        if (_view is { Progress: > 0 } v)
        {
            using var fill = new SolidBrush(Color.FromArgb(46, 160, 67));
            g.FillRectangle(fill, new Rectangle(r.Left, r.Top, (int)(r.Width * v.Progress), r.Height));
        }
    }

    private void BuildCards(TodayView view)
    {
        _cards.SuspendLayout();
        foreach (Control c in _cards.Controls)
        {
            c.Dispose();
        }

        _cards.Controls.Clear();
        var width = SideWidth - 16;
        {
            var card = Card("Coming up today", width);
            foreach (var e in view.Events)
            {
                card.Controls.Add(EventRow(e, width - 24));
            }

            var note = view.Events.Count > 0 ? "Click an event to mark it done." : "Nothing more today. Done and missed events are in Done today.";
            card.Controls.Add(new Label { Text = note, AutoSize = true, MaximumSize = new Size(width - 24, 0), Font = new Font("Segoe UI", 8.5f), ForeColor = Muted, Margin = new Padding(0, 6, 0, 0) });
            _cards.Controls.Add(card);
        }

        if (view.Cards is { Count: > 0 } producers)
        {
            var card = Card("Homestead", width);
            foreach (var c in producers)
            {
                card.Controls.Add(ProducerRow(c, width - 24));
            }

            _cards.Controls.Add(card);
        }

        if (view.Week.Count > 0)
        {
            var card = Card("This week", width);
            foreach (var (name, text) in view.Week)
            {
                card.Controls.Add(TwoLines(name, text, Muted, width - 24));
            }

            card.Controls.Add(new Label { Text = view.WeekReset, AutoSize = true, MaximumSize = new Size(width - 24, 0), Font = new Font("Segoe UI", 9f), ForeColor = Muted, Margin = new Padding(0, 6, 0, 0) });
            _cards.Controls.Add(card);
        }

        _cards.ResumeLayout();
    }

    private static FlowLayoutPanel Card(string title, int width)
    {
        var card = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Width = width, MinimumSize = new Size(width, 0),
            BackColor = CardBack, Padding = new Padding(12, 10, 12, 12), Margin = new Padding(0, 0, 0, 16),
        };
        card.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) });
        return card;
    }

    private Control EventRow(TodayEvent e, int width)
    {
        var ink = e.Open ? Color.FromArgb(24, 110, 40) : Muted;
        var row = TwoLines((e.Open ? "● " : string.Empty) + e.Name + (e.Progress is { } pr ? "  " + pr : string.Empty), e.When, ink, width);
        row.Cursor = Cursors.Hand;
        _tips.SetToolTip(row, e.How + " Click to mark it done.");
        foreach (Control c in row.Controls)
        {
            c.Cursor = Cursors.Hand;
            _tips.SetToolTip(c, e.How + " Click to mark it done.");
            c.Click += (_, _) => MarkEventDone(e.Id);
        }

        row.Click += (_, _) => MarkEventDone(e.Id);
        return row;
    }

    private void MarkEventDone(string id)
    {
        if (id.Length > 0)
        {
            _backend.SetDone(id, true);
            Refresh(force: true);
        }
    }

    private Control ProducerRow(TodayCard c, int width)
    {
        var ink = c.Dot ? Color.FromArgb(196, 43, 28) : Muted;
        var row = TwoLines((c.Dot ? "● " : string.Empty) + c.Name, c.Text, ink, width);
        if (c.Dot)
        {
            row.Controls[0].ForeColor = Color.FromArgb(196, 43, 28);
        }

        _tips.SetToolTip(row, c.How);
        foreach (Control child in row.Controls)
        {
            _tips.SetToolTip(child, c.How);
        }

        return row;
    }

    private static Control TwoLines(string top, string bottom, Color bottomInk, int width)
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 4, 0, 4), Width = width };
        panel.Controls.Add(new Label { Text = top, AutoSize = true, MaximumSize = new Size(width, 0), Font = new Font("Segoe UI", 10f), Margin = Padding.Empty });
        panel.Controls.Add(new Label { Text = bottom, AutoSize = true, MaximumSize = new Size(width, 0), Font = new Font("Segoe UI", 9f), ForeColor = bottomInk, Margin = Padding.Empty });
        return panel;
    }

    private readonly record struct DailyKey(long Version, int Minute, Companion.Dailies.DailyPrefs? Prefs, bool Replaying);
}
