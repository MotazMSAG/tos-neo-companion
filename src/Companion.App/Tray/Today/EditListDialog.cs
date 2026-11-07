using System.Drawing;
using Companion.Dailies;

namespace Companion.App.Tray.Today;

/// <summary>
/// "Edit my list" (DAILIES_MODULE.md): every known daily grouped by section, with a search box (recognition over
/// recall), a Show checkbox, an editable name and how it is seen; "Add your own" for hand-ticked items. Save returns the
/// new <see cref="DailyPrefs"/> in <see cref="Result"/>; nothing changes until Save.
/// </summary>
internal sealed class EditListDialog : Form
{
    private static readonly Color Muted = Color.FromArgb(96, 96, 96);
    private static readonly Color CardBack = Color.FromArgb(242, 245, 250);
    private static readonly Color Accent = Color.FromArgb(0, 95, 184);

    private readonly DailySnapshot _snapshot;
    private readonly List<Entry> _entries;
    private readonly TextBox _search = new() { PlaceholderText = "Search dailies...", Dock = DockStyle.Top, Font = new Font("Segoe UI", 10f) };
    private readonly Label _count = new() { Dock = DockStyle.Top, Height = 24, Font = new Font("Segoe UI", 9f), ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, Font = new Font("Segoe UI", 10f), EditMode = DataGridViewEditMode.EditOnEnter,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize, GridColor = Color.FromArgb(238, 241, 245),
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
    };
    private readonly TextBox _own = new() { PlaceholderText = "For example: Daily bounty", Width = 360, Font = new Font("Segoe UI", 10f) };

    /// <summary>Creates the dialog from the list now and the current choices.</summary>
    public EditListDialog(DailySnapshot snapshot, DailyPrefs prefs)
    {
        _snapshot = snapshot;
        _entries = [.. snapshot.Items.Select(i => new Entry(i.Id, i.Section, i.Name, prefs.NameOf(i), prefs.IsShown(i), i.How, i.PerDay, i.Section == DailyTracker.OwnSection))];
        Text = "Edit my list";
        Font = new Font("Segoe UI", 10f);
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 640);
        MinimumSize = new Size(560, 480);
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Color.White;
        Padding = new Padding(16);

        var intro = new Label
        {
            Dock = DockStyle.Top, Height = 44, Font = new Font("Segoe UI", 10f), ForeColor = Muted,
            Text = "Pick what belongs on your list. Ticks still come from the game by themselves, and the names are yours to change.",
        };

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Show", FillWeight = 10, Name = "show" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", FillWeight = 34, Name = "name" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "How it is seen", FillWeight = 42, Name = "how", ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Per day", FillWeight = 14, Name = "per", ReadOnly = true });
        _grid.Columns["how"]!.DefaultCellStyle.ForeColor = Muted;
        _grid.Columns["how"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.Columns["per"]!.DefaultCellStyle.ForeColor = Muted;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _grid.CellValueChanged += (_, e) => TakeRow(e.RowIndex);

        var addRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 76, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 10, 0, 0) };
        addRow.Controls.Add(new Label { Text = "Add your own", AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(0, 8, 12, 0) });
        addRow.Controls.Add(_own);
        var add = new Button { Text = "Add", Width = 88, Height = 32, Margin = new Padding(8, 2, 0, 0) };
        add.Click += (_, _) => AddOwn();
        addRow.Controls.Add(add);
        addRow.Controls.Add(new Label { Text = "Your own items are ticked by hand.", AutoSize = true, Font = new Font("Segoe UI", 9f), ForeColor = Muted, Margin = new Padding(0, 6, 0, 0) });

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        var save = new Button { Text = "Save", Width = 120, Height = 40, BackColor = Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 96, Height = 36, DialogResult = DialogResult.Cancel, Margin = new Padding(0, 2, 8, 0) };
        save.Click += (_, _) => Result = BuildPrefs();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;

        Controls.Add(_grid);
        Controls.Add(_count);
        Controls.Add(_search);
        Controls.Add(intro);
        Controls.Add(addRow);
        Controls.Add(buttons);
        _search.TextChanged += (_, _) => Fill();
        Fill();
    }

    /// <summary>The new choices after Save; null otherwise.</summary>
    public DailyPrefs? Result { get; private set; }

    private void Fill()
    {
        _grid.Rows.Clear();
        var q = _search.Text.Trim();
        var sections = _snapshot.Sections.Select(s => (s.Id, s.Title)).ToList();
        if (_entries.Any(e => e.Own) && sections.All(s => s.Id != DailyTracker.OwnSection))
        {
            sections.Add((DailyTracker.OwnSection, "Your own"));
        }

        foreach (var (id, title) in sections)
        {
            var hits = _entries.Where(e => e.Section == id && (q.Length == 0
                || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || e.How.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
            if (hits.Count == 0)
            {
                continue;
            }

            var head = _grid.Rows[_grid.Rows.Add(false, title.ToUpperInvariant(), string.Empty, string.Empty)];
            head.ReadOnly = true;
            head.DefaultCellStyle.BackColor = CardBack;
            head.DefaultCellStyle.SelectionBackColor = CardBack;
            head.DefaultCellStyle.SelectionForeColor = Color.FromArgb(60, 68, 80);
            head.DefaultCellStyle.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            head.Cells[0] = new DataGridViewTextBoxCell { Value = string.Empty };

            foreach (var e in hits)
            {
                var row = _grid.Rows[_grid.Rows.Add(e.Shown, e.Name, e.How, e.PerDay > 1 ? e.PerDay + " a day" : e.PerDay == 0 ? "count" : string.Empty)];
                row.Tag = e;
            }
        }

        _count.Text = _entries.Count(e => e.Shown && e.Section != "clock") + " on your list" + (q.Length > 0 ? " · showing matches for \"" + q + "\"" : string.Empty);
    }

    private void TakeRow(int rowIndex)
    {
        if (rowIndex < 0 || _grid.Rows[rowIndex].Tag is not Entry e)
        {
            return;
        }

        var row = _grid.Rows[rowIndex];
        e.Shown = row.Cells["show"].Value is true;
        var name = (row.Cells["name"].Value as string)?.Trim();
        e.Name = string.IsNullOrEmpty(name) ? e.DefaultName : name;
        _count.Text = _entries.Count(x => x.Shown && x.Section != "clock") + " on your list";
    }

    private void AddOwn()
    {
        var name = _own.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }

        var n = 1;
        while (_entries.Any(e => e.Id == "own" + n))
        {
            n++;
        }

        _entries.Add(new Entry("own" + n, DailyTracker.OwnSection, name, name, true, "Ticked by hand", 1, true));
        _own.Clear();
        _search.Clear();
        Fill();
    }

    private DailyPrefs BuildPrefs()
    {
        _grid.EndEdit();
        var defaults = _snapshot.Items.ToDictionary(i => i.Id);
        var shown = new Dictionary<string, bool>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in _entries.Where(e => !e.Own))
        {
            if (defaults.TryGetValue(e.Id, out var d))
            {
                if (e.Shown != d.Shown)
                {
                    shown[e.Id] = e.Shown;
                }

                if (e.Name != d.Name)
                {
                    names[e.Id] = e.Name;
                }
            }
        }

        foreach (var e in _entries.Where(e => e.Own && !e.Shown))
        {
            shown[e.Id] = false;
        }

        return new DailyPrefs
        {
            Shown = shown,
            Names = names,
            Custom = [.. _entries.Where(e => e.Own && e.Shown).Select(e => new DailyCustomItem { Id = e.Id, Name = e.Name })],
        };
    }

    private sealed class Entry(string id, string section, string defaultName, string name, bool shown, string how, int perDay, bool own)
    {
        public string Id { get; } = id;

        public string Section { get; } = section;

        public string DefaultName { get; } = defaultName;

        public string Name { get; set; } = name;

        public bool Shown { get; set; } = shown;

        public string How { get; } = how;

        public int PerDay { get; } = perDay;

        public bool Own { get; } = own;
    }
}
