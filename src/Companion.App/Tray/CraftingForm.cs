using System.Drawing;

namespace Companion.App.Tray;

/// <summary>
/// Tray "Crafting..." (SPEC 10.3, 11.4): a plain window over <see cref="CraftingViewModel"/>. Recipes of the current
/// character, count (or until the materials run out), batch 1 or max, Start / Stop / Scan favourites / Learn selected recipe
/// (the recipe the user selected in the game's Make tab), and a status line
/// refreshed about once a second. It only talks to the job channel; it never touches the game window.
/// </summary>
internal sealed class CraftingForm : Form
{
    private const int ContentWidth = 600;

    private readonly CraftingViewModel _vm;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly TextBox _search = new() { PlaceholderText = "Search recipes...", Width = ContentWidth };
    private List<int> _visible = [];
    private string _shownFilter = string.Empty;
    private readonly Label _knowledgeLine = new() { AutoSize = true, MaximumSize = new Size(ContentWidth, 0) };
    private readonly Label _gates = new() { AutoSize = true, MaximumSize = new Size(ContentWidth, 0), ForeColor = Color.Firebrick };
    private readonly ListBox _recipes = new() { Width = ContentWidth, Height = 180, IntegralHeight = false };
    private readonly Label _details = new() { AutoSize = true, MaximumSize = new Size(ContentWidth, 0), MinimumSize = new Size(ContentWidth, 110) };
    private readonly NumericUpDown _count = new() { Minimum = 0, Maximum = CraftingRules.MaxCount, Value = 1, Width = 100 }; // 0 = forever (goal loop)
    private readonly CheckBox _untilOut = new() { Text = "Until materials run out", AutoSize = true, Margin = new Padding(12, 6, 0, 0) };
    private readonly RadioButton _batchOne = new() { Text = "1 per Make", AutoSize = true, Checked = true };
    private readonly RadioButton _batchMax = new() { Text = "Max per Make", AutoSize = true };
    private readonly Button _start = new() { Text = "Start", AutoSize = true };
    private readonly Button _stop = new() { Text = "Stop", AutoSize = true };
    private readonly Button _scan = new() { Text = "Scan favourites", AutoSize = true };
    private readonly Button _learn = new() { Text = "Learn selected recipe", AutoSize = true };
    private readonly Label _message = new() { AutoSize = true, MaximumSize = new Size(ContentWidth, 0), MinimumSize = new Size(ContentWidth, 48) };
    private readonly Label _clickHint = new() { AutoSize = true, MaximumSize = new Size(ContentWidth, 0), ForeColor = Color.Firebrick, Visible = false };
    private readonly Label _health = new() { AutoSize = true, MaximumSize = new Size(ContentWidth, 0), MinimumSize = new Size(ContentWidth, 30), Font = new Font("Segoe UI", 11f, FontStyle.Bold), Padding = new Padding(6), Margin = new Padding(0, 0, 0, 8) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(ContentWidth, 0), MinimumSize = new Size(ContentWidth, 36), ForeColor = SystemColors.GrayText };
    private int _shownVersion = -1;
    private bool _binding;
    private bool _active;

    /// <summary>Creates the window (refreshes when shown).</summary>
    public CraftingForm(CraftingViewModel viewModel)
    {
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Text = "Companion crafting";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var count = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        count.Controls.Add(new Label { Text = "Count", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        count.Controls.Add(_count);
        count.Controls.Add(_untilOut);

        var batch = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        batch.Controls.Add(new Label { Text = "Batch", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        batch.Controls.Add(_batchOne);
        batch.Controls.Add(_batchMax);

        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        buttons.Controls.Add(_start);
        buttons.Controls.Add(_stop);
        buttons.Controls.Add(_scan);
        buttons.Controls.Add(_learn);
        buttons.Controls.Add(close);

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.Add(_health);
        layout.Controls.Add(_knowledgeLine);
        layout.Controls.Add(_gates);
        layout.Controls.Add(new Label { Text = "Recipes (from 'Scan favourites'; materials are shown by position, the game shows no names)", AutoSize = true, Margin = new Padding(0, 8, 0, 0) });
        layout.Controls.Add(_search);
        layout.Controls.Add(_recipes);
        layout.Controls.Add(_details);
        layout.Controls.Add(count);
        layout.Controls.Add(batch);
        layout.Controls.Add(buttons);
        layout.Controls.Add(_message);
        layout.Controls.Add(_clickHint);
        layout.Controls.Add(_status);
        layout.Controls.Add(new Label
        {
            Text = "Closing this window does not stop a job. Emergency stop: 'Stop now' in the tray menu or Ctrl+Alt+Pause.",
            AutoSize = true,
            MaximumSize = new Size(ContentWidth, 0),
            ForeColor = SystemColors.GrayText,
        });
        Controls.Add(layout);
        CancelButton = close;

        _recipes.SelectedIndexChanged += (_, _) => OnSelection();
        _search.TextChanged += (_, _) => UpdateView();
        _count.ValueChanged += (_, _) => Apply(() => _vm.Count = (int)_count.Value);
        _untilOut.CheckedChanged += (_, _) => Apply(() => _vm.UntilMaterialsRunOut = _untilOut.Checked);
        _batchMax.CheckedChanged += (_, _) => Apply(() => _vm.BatchMax = _batchMax.Checked);
        _start.Click += (_, _) => Apply(_vm.Start);
        _stop.Click += (_, _) => Apply(_vm.Stop);
        _scan.Click += (_, _) => Apply(_vm.Scan);
        _learn.Click += (_, _) => Apply(_vm.Learn);
        _timer.Tick += (_, _) => Apply(_vm.Refresh);
    }

    /// <inheritdoc />
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Apply(_vm.Refresh);
        _timer.Start();
    }

    /// <inheritdoc />
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _active = true;
        if (!IsDisposed && !Disposing)
        {
            UpdateView();
        }
    }

    /// <inheritdoc />
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _active = false;
        if (!IsDisposed && !Disposing)
        {
            UpdateView();
        }
    }

    /// <inheritdoc />
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        base.OnFormClosed(e);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnSelection()
    {
        if (_binding)
        {
            return;
        }

        var index = _recipes.SelectedIndex;
        _vm.Selected = index >= 0 && index < _visible.Count && _visible[index] < _vm.Recipes.Count ? _vm.Recipes[_visible[index]] : null;
        UpdateView();
    }

    private void Apply(Action action)
    {
        action();
        UpdateView();
    }

    private void UpdateView()
    {
        // Never minimized or hidden on purpose: Windows would then activate the next window, which may be the game.
        // Reading the pointer position only (no cursor move).
        _vm.WindowBlocksClicks = _active || (Visible && WindowState != FormWindowState.Minimized && Bounds.Contains(MousePosition));
        var filter = _search.Text.Trim();
        if (_vm.RecipesVersion != _shownVersion || filter != _shownFilter)
        {
            _binding = true;
            _recipes.BeginUpdate();
            try
            {
                _recipes.Items.Clear();
                _visible = [.. Enumerable.Range(0, _vm.Recipes.Count).Where(i => filter.Length == 0 || _vm.Recipes[i].Title.Contains(filter, StringComparison.OrdinalIgnoreCase))];
                foreach (var i in _visible)
                {
                    _recipes.Items.Add(CraftingRules.ListText(_vm.Recipes[i]));
                }

                var selectedAt = _vm.Selected is { } s ? IndexOf(s) : -1;
                _recipes.SelectedIndex = _visible.IndexOf(selectedAt);
                _shownVersion = _vm.RecipesVersion;
                _shownFilter = filter;
            }
            finally
            {
                _recipes.EndUpdate();
                _binding = false;
            }
        }

        SetText(_knowledgeLine, _vm.KnowledgeLine);
        SetText(_gates, _vm.GateHint ?? string.Empty);
        _gates.Visible = _vm.GateHint is not null;
        SetText(_details, _vm.Details);
        SetText(_message, _vm.Message);
        SetText(_clickHint, _vm.ClickHint ?? string.Empty);
        _clickHint.Visible = _vm.ClickHint is not null;
        SetText(_status, _vm.StatusLine);
        SetText(_health, _vm.Health.Text);
        (_health.BackColor, _health.ForeColor) = _vm.Health.Level switch
        {
            HealthLevel.Green => (Color.FromArgb(220, 245, 220), Color.DarkGreen),
            HealthLevel.Amber => (Color.FromArgb(255, 238, 200), Color.FromArgb(150, 80, 0)),
            HealthLevel.Red => (Color.FromArgb(255, 220, 220), Color.Firebrick),
            _ => (SystemColors.Control, SystemColors.GrayText),
        };
        var b = _vm.Buttons;
        _start.Enabled = b.Start;
        _stop.Enabled = b.Stop;
        _scan.Enabled = b.Scan;
        _learn.Enabled = b.Scan;
        _count.Enabled = b.Count;
    }

    private int IndexOf(CraftRecipeRow row)
    {
        for (var i = 0; i < _vm.Recipes.Count; i++)
        {
            if (ReferenceEquals(_vm.Recipes[i], row))
            {
                return i;
            }
        }

        return -1;
    }

    private static void SetText(Control control, string text)
    {
        if (!string.Equals(control.Text, text, StringComparison.Ordinal))
        {
            control.Text = text;
        }
    }
}
