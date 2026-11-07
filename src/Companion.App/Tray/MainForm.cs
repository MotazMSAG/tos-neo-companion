using System.Drawing;
using System.Globalization;
using System.Text.Json;
using Companion.Contracts.Host;

namespace Companion.App.Tray;

/// <summary>A labelled on/off setting of the main window (the tray's former menu toggles).</summary>
internal sealed record SettingToggle(string Text, string Help, Func<bool> Get, Action<bool> Set);

/// <summary>What the main window may do through the tray app (pause, stop, halt, settings).</summary>
internal sealed record MainActions(
    Func<bool> IsPaused,
    Action Pause,
    Action Resume,
    Action Stop,
    Func<bool> IsHalted,
    Action ClearHalt,
    IReadOnlyList<SettingToggle> Settings,
    Action TrustBuild,
    string JournalDir,
    Func<bool> IsStopped,
    Action ClearStop,
    Func<Companion.Contracts.Knowledge.ICharacterKnowledge?> OpenKnowledgeForEdit,
    Func<bool> IsJobRunning,
    Func<bool> RuleModeOn,
    Func<bool, string> SetRuleMode,
    Func<string?>? AwayCard = null,
    Func<string?>? RuleModeStatus = null,
    Today.ITodayBackend? Today = null);

/// <summary>
/// The companion's one window (2026-10-16 redesign, the user: "a proper UX/UI redesign for human use"), opened by a left
/// click on the tray icon. Design rules (usability heuristics):
/// <list type="bullet">
/// <item>visibility of status: a status card on top, always shown: colour (green working, amber needs you, red stopped,
/// grey idle), what the companion is doing now in plain words, and the one control that matters (Pause/Resume, Stop,
/// Clear halt only when halted);</item>
/// <item>one primary action per tab (a blue button), secondary actions plain, nothing that cannot work enabled;</item>
/// <item>recognition over recall: every recipe choice is a live-filtered list with a search box;</item>
/// <item>minimalism and progressive disclosure: Craft shows only the chosen recipe; history, settings and the advanced
/// tray items live in their own tabs or menu;</item>
/// <item>consistency: one font (Segoe UI), one 8 px spacing grid, the same button sizes and colours everywhere.</item>
/// </list>
/// It only talks to the job channel and reads the journal; it never touches the game window.
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly Font Base = new("Segoe UI", 10f);
    private static readonly Font Headline = new("Segoe UI", 13f, FontStyle.Bold);
    private static readonly Font Title = new("Segoe UI", 14f, FontStyle.Bold);
    private static readonly Font Small = new("Segoe UI", 9f);
    private static readonly Color Accent = Color.FromArgb(0, 95, 184);
    private static readonly Color Muted = Color.FromArgb(96, 96, 96);

    private readonly CraftingViewModel _vm;
    private readonly ICraftingBackend _backend;
    private readonly MainActions _actions;
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(18, 6), Font = Base };

    // status card
    private readonly Panel _strip = new() { Dock = DockStyle.Left, Width = 8 };
    private readonly Label _headline = new() { Dock = DockStyle.Top, Height = 30, Font = Headline, AutoEllipsis = true };
    private readonly Label _subline = new() { Dock = DockStyle.Top, Height = 22, Font = Small, ForeColor = Muted, AutoEllipsis = true };

    /// <summary>A red line on the status card for something the user should fix in the game (2026-10-18: worn-out gear).</summary>
    private readonly Label _alert = new() { Dock = DockStyle.Top, Height = 24, Font = new Font("Segoe UI", 10f, FontStyle.Bold), ForeColor = Color.Firebrick, AutoEllipsis = true, Visible = false };

    private Panel? _card;
    private readonly Button _pause = Secondary("Pause", 100);
    private readonly Button _stop = Secondary("Stop", 90);
    private readonly Button _clearHalt = Secondary("Continue...", 110);
    private readonly Button _allow = Secondary("Allow again", 120);

    // craft tab
    private readonly TextBox _search = new() { PlaceholderText = "Search recipes...", Dock = DockStyle.Top, Font = Base };
    private readonly ListBox _recipes = new() { Dock = DockStyle.Fill, Font = Base, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
    private readonly Label _recipeTitle = new() { AutoSize = true, Font = Title, Text = "Choose a recipe" };
    private readonly Label _details = new() { AutoSize = true, MaximumSize = new Size(420, 0), Font = Base, ForeColor = Muted };
    private readonly NumericUpDown _count = new() { Minimum = 1, Maximum = CraftingRules.MaxPlanQty, Value = 50, Width = 90, Font = Base };
    private readonly CheckBox _forever = new() { Text = "Forever (until you press Stop)", AutoSize = true, Font = Base };
    private readonly CheckBox _untilOut = new() { Text = "Only from my stock (no gathering)", AutoSize = true, Font = Base };
    private readonly Button _start = Primary("Start crafting", 170);
    private readonly Button _addToQueue = Secondary("Add to queue", 130);
    private readonly Label _message = new() { AutoSize = true, MaximumSize = new Size(420, 0), Font = Base };
    private readonly Label _hint = new() { AutoSize = true, MaximumSize = new Size(420, 0), Font = Small, ForeColor = Color.FromArgb(150, 80, 0) };
    private List<int> _visible = [];
    private string _shownFilter = "\0";
    private int _shownVersion = -1;
    private bool _binding;

    // queue tab
    private readonly DataGridView _queue = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle, Font = Base,
    };
    private readonly Label _queueResult = new() { AutoSize = true, MaximumSize = new Size(640, 0), Font = Small, ForeColor = Muted };

    // activity tab
    private readonly Label _madeToday = Tile();
    private readonly Label _tripsToday = Tile();
    private readonly Label _pausesToday = Tile();

    private static Control TileCard(Label number, string caption)
    {
        var card = new Panel { Width = 180, Height = 64, BackColor = CardBack, Margin = new Padding(0, 0, 8, 8) };
        number.Dock = DockStyle.Fill;
        number.Font = Headline;
        number.Text = "-";
        card.Controls.Add(number);
        card.Controls.Add(new Label { Text = caption, Dock = DockStyle.Bottom, Height = 22, Font = Small, ForeColor = Muted, TextAlign = ContentAlignment.TopCenter });
        return card;
    }
    private readonly ListView _activity = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Font = Base };

    /// <summary>Roadmap C: the stats page (the last 7 days from the journal).</summary>
    private readonly ListView _stats = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Font = Base };

    private readonly Panel _chart = new() { Dock = DockStyle.Top, Height = 150, BackColor = Color.White };

    private readonly Label _statsNote = new() { Dock = DockStyle.Bottom, AutoSize = false, Height = 64, Font = Base, ForeColor = Color.FromArgb(90, 90, 90) };

    private SortedDictionary<DateTime, JournalStats> _days = new();

    /// <summary>Roadmap A: "while you were away" (the host's summary of the latest absence from the game, or the current one).</summary>
    private readonly Label _away = new() { Dock = DockStyle.Top, AutoSize = false, Height = 44, Font = Base, ForeColor = Color.FromArgb(20, 70, 120), Padding = new Padding(0, 4, 0, 4), Visible = false };

    private readonly List<CheckBox> _settingBoxes = [];

    /// <summary>The home page: the daily checklist (2026-11-04); null when the checklist could not start.</summary>
    private readonly Today.TodayPage? _today;
    private int _ticks;

    public MainForm(CraftingViewModel viewModel, ICraftingBackend backend, MainActions actions)
    {
        _vm = viewModel;
        _backend = backend;
        _actions = actions;
        Text = "TOS companion";
        Font = Base;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 640);
        MinimumSize = new Size(760, 540);
        BackColor = Color.White;

        var tabs = _tabs;
        if (actions.Today is { } today)
        {
            _today = new Today.TodayPage(today);
            tabs.TabPages.Add(_today); // first: the window opens on it (the user, 2026-11-04: "the main page ... this daily checklist view")
        }

        tabs.TabPages.Add(CraftPage());
        tabs.TabPages.Add(QueuePage());
        tabs.TabPages.Add(RulesPage());
        tabs.TabPages.Add(ActivityPage());
        tabs.TabPages.Add(StatsPage());
        tabs.TabPages.Add(SettingsPage());
        Controls.Add(tabs);
        Controls.Add(StatusCard());

        _timer.Tick += (_, _) => Tick();
        Shown += (_, _) => { _timer.Start(); Tick(); LoadActivity(); };
        FormClosed += (_, _) => _timer.Stop();
        Activated += (_, _) => Tick();
    }

    /// <summary>Shows the tab with this title (case does not matter); unknown names change nothing.</summary>
    public void SelectTab(string name)
    {
        foreach (TabPage page in _tabs.TabPages)
        {
            if (string.Equals(page.Text, name, StringComparison.OrdinalIgnoreCase) || string.Equals(page.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                _tabs.SelectedTab = page;
                return;
            }
        }
    }

    private static Button Primary(string text, int width) => new()
    {
        Text = text, Width = width, Height = 40, BackColor = Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Margin = new Padding(0, 8, 8, 0),
    };

    private static Button Secondary(string text, int width) => new() { Text = text, Width = width, Height = 36, Font = Base, Margin = new Padding(0, 8, 8, 0) };

    private static readonly Color CardBack = Color.FromArgb(242, 245, 250);

    private static Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Margin = new Padding(0, 16, 0, 4) };

    private static Label Help(string text, int width = 760) => new() { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), Font = Small, ForeColor = Muted, Margin = new Padding(0, 0, 0, 4) };

    private static Label Tile() => new() { AutoSize = false, Width = 180, Height = 64, Font = Headline, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.FromArgb(242, 245, 250), Margin = new Padding(0, 0, 8, 8) };

    private Control StatusCard()
    {
        var card = _card = new Panel { Dock = DockStyle.Top, Height = 72, Padding = new Padding(0, 0, 8, 0) };
        var text = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 0, 0) };
        text.Controls.Add(_alert);
        text.Controls.Add(_subline);
        text.Controls.Add(_headline);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(_clearHalt);
        buttons.Controls.Add(_allow);
        buttons.Controls.Add(_pause);
        buttons.Controls.Add(_stop);
        _pause.Click += (_, _) => { if (_actions.IsPaused()) { _actions.Resume(); } else { _actions.Pause(); } Tick(); };
        _stop.Click += (_, _) => { _actions.Stop(); Tick(); };
        _clearHalt.Click += (_, _) => { _actions.ClearHalt(); Tick(); };
        _allow.Click += (_, _) => { _actions.ClearStop(); Tick(); };
        _tips.SetToolTip(_stop, "Stops everything at once and cancels the jobs (also Ctrl+Alt+Pause). Allow again lets it work afterwards.");
        _tips.SetToolTip(_pause, "Holds the current job after its current click; Resume goes on where it was.");
        _tips.SetToolTip(_allow, "After a Stop: lets the companion work again (start your job again).");
        _tips.SetToolTip(_clearHalt, "After a safety stop: look at the game first, then continue.");
        card.Controls.Add(text);
        card.Controls.Add(buttons);
        card.Controls.Add(_strip);
        return card;
    }

    private TabPage CraftPage()
    {
        var page = new TabPage("Craft") { Padding = new Padding(16), BackColor = Color.White };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // left: find a recipe
        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 16, 0) };
        var leftTools = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 92 };
        var scan = Secondary("Scan favourites", 140);
        var learn = Secondary("Learn selected", 140);
        var scanAll = Secondary("Scan all recipes", 140);
        _tips.SetToolTip(scan, "Reads your My Favorites in the game's Make tab (recipes, slots and materials). About 10 s per favourite.");
        _tips.SetToolTip(scanAll, "Walks the game's whole Make list and reads every recipe (scrolls the list). Takes 5-20 minutes; run it after you learn new recipes.");
        _tips.SetToolTip(learn, "Reads the recipe you selected in the game's Make tab.");
        scan.Click += (_, _) => { if (Blocked() is { } why) { _message.Text = why; return; } _vm.Scan(); Tick(); };
        learn.Click += (_, _) => { if (Blocked() is { } why) { _message.Text = why; return; } _vm.Learn(); Tick(); };
        scanAll.Click += (_, _) => { if (Blocked() is { } why) { _message.Text = why; return; } _vm.ScanAll(); Tick(); };
        leftTools.Controls.Add(scan);
        leftTools.Controls.Add(learn);
        leftTools.Controls.Add(scanAll);
        var gap = new Panel { Dock = DockStyle.Top, Height = 8 };
        left.Controls.Add(_recipes);
        left.Controls.Add(gap);
        left.Controls.Add(_search);
        left.Controls.Add(leftTools);
        _search.TextChanged += (_, _) => Tick();
        _recipes.SelectedIndexChanged += (_, _) =>
        {
            if (!_binding)
            {
                var i = _recipes.SelectedIndex;
                _vm.Selected = i >= 0 && i < _visible.Count && _visible[i] < _vm.Recipes.Count ? _vm.Recipes[_visible[i]] : null;
                Tick();
            }
        };

        // right: the chosen recipe and the one action
        var right = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        right.Controls.Add(_recipeTitle);
        right.Controls.Add(_details);
        var amount = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        amount.Controls.Add(new Label { Text = "How many", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
        amount.Controls.Add(_count);
        right.Controls.Add(amount);
        right.Controls.Add(_forever);
        right.Controls.Add(_untilOut);
        var actions = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        actions.Controls.Add(_start);
        actions.Controls.Add(_addToQueue);
        right.Controls.Add(actions);
        right.Controls.Add(_message);
        right.Controls.Add(_hint);
        _forever.CheckedChanged += (_, _) => { _count.Enabled = !_forever.Checked; if (_forever.Checked) { _untilOut.Checked = false; } Tick(); };
        _untilOut.CheckedChanged += (_, _) => { if (_untilOut.Checked) { _forever.Checked = false; } Tick(); };
        _count.ValueChanged += (_, _) => Tick();
        _start.Click += (_, _) =>
        {
            ApplyAmount();
            _vm.Start();
            Tick();
        };
        _addToQueue.Click += (_, _) =>
        {
            if (_vm.Selected is { ViaPlan: true } row)
            {
                _queue.Rows.Add(row.Title, _forever.Checked ? "0" : ((int)_count.Value).ToString(CultureInfo.InvariantCulture));
                _message.Text = row.Title + " added to the queue (Queue tab).";
            }
        };

        grid.Controls.Add(left, 0, 0);
        grid.Controls.Add(right, 1, 0);
        page.Controls.Add(grid);
        return page;
    }

    /// <summary>Why nothing can start right now (null when a job may start), in plain words.</summary>
    private string? Blocked()
    {
        var host = _vm.Host;
        return host is null ? "The companion is not reachable."
            : host.Halt is not null ? "Stopped for safety: look at the game, then press Continue at the top."
            : host.State == HostState.Stopped ? "Stopped: press Allow again at the top first."
            : host.State == HostState.Paused ? "Paused: press Resume at the top first."
            : host.State == HostState.Untrusted ? "This version is not trusted yet (Settings tab)."
            : !host.GameConnected ? "The game is not running."
            : null;
    }

    private void ApplyAmount()
    {
        _vm.Count = _forever.Checked ? 0 : (int)_count.Value;
        _vm.UntilMaterialsRunOut = _untilOut.Checked;
        _vm.BatchMax = true; // one Make per batch
    }

    private TabPage QueuePage()
    {
        var page = new TabPage("Queue") { Padding = new Padding(16), BackColor = Color.White };
        _queue.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Recipe", FillWeight = 70, ReadOnly = true });
        _queue.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "How many", FillWeight = 30 });

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // left: pick a goal
        var search = new TextBox { PlaceholderText = "Search recipes...", Dock = DockStyle.Top, Font = Base };
        var matches = new ListBox { Dock = DockStyle.Fill, Font = Base, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        var count = new NumericUpDown { Minimum = 0, Maximum = CraftingRules.MaxPlanQty, Value = 50, Width = 90, Font = Base };
        var add = Secondary("Add", 80);
        void Fill()
        {
            var f = search.Text.Trim();
            matches.BeginUpdate();
            matches.Items.Clear();
            foreach (var t in _vm.Recipes.Where(r => r.ViaPlan).Select(r => r.Title).Distinct().Where(t => f.Length == 0 || t.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                matches.Items.Add(t);
            }

            matches.EndUpdate();
        }

        search.TextChanged += (_, _) => Fill();
        page.Enter += (_, _) => Fill();
        add.Click += (_, _) =>
        {
            if (matches.SelectedItem is string title)
            {
                _queue.Rows.Add(title, ((int)count.Value).ToString(CultureInfo.InvariantCulture));
            }
        };
        matches.DoubleClick += (_, _) => add.PerformClick();
        var addRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52 };
        addRow.Controls.Add(new Label { Text = "How many", AutoSize = true, Margin = new Padding(0, 16, 8, 0) });
        addRow.Controls.Add(count);
        addRow.Controls.Add(add);
        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 16, 0) };
        left.Controls.Add(matches);
        left.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 });
        left.Controls.Add(search);
        left.Controls.Add(addRow);

        // right: the queue, its one action, and gather everything
        var right = new Panel { Dock = DockStyle.Fill };
        var heading = new Label { Dock = DockStyle.Top, Height = 28, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Text = "Goals, made one after another (0 = forever)" };
        var start = Primary("Start the queue", 170);
        var remove = Secondary("Remove", 90);
        var clear = Secondary("Clear", 80);
        start.Click += (_, _) => StartQueue();
        remove.Click += (_, _) =>
        {
            foreach (DataGridViewRow r in _queue.SelectedRows)
            {
                _queue.Rows.Remove(r);
            }
        };
        clear.Click += (_, _) => _queue.Rows.Clear();
        var queueButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56 };
        queueButtons.Controls.Add(start);
        queueButtons.Controls.Add(remove);
        queueButtons.Controls.Add(clear);

        var gather = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 96, ColumnCount = 2, BackColor = CardBack, Padding = new Padding(10, 8, 8, 8), Margin = new Padding(0, 8, 0, 0) };
        gather.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gather.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var gatherText = new Label
        {
            Dock = DockStyle.Fill, Font = Small, ForeColor = Muted,
            Text = "No recipe in mind? Gather everything: every raw material your known recipes use, as much as each stock limit allows (at most 200 per material and trip), 4 per trip. Materials the Gathering Helper does not offer (homestead crops) are skipped.",
        };
        var gatherAll = Secondary("Gather everything", 160);
        gatherAll.Anchor = AnchorStyles.Right;
        gatherAll.Click += (_, _) =>
        {
            if (Blocked() is { } why)
            {
                _queueResult.Text = why;
                return;
            }

            // a second press while one runs queued a second full run (live 2026-10-23: pressed twice when nothing seemed
            // to happen); the status line above says what it is doing
            if (_backend.RunningJob?.JobName == "GatherAll" || _backend.QueuedJobNames.Contains("GatherAll"))
            {
                _queueResult.Text = "Gather everything is already running: the status line at the top says what it is doing.";
                return;
            }

            var answer = _backend.Send(new PipeRequest(PipeOp.Submit, Guid.NewGuid().ToString("N"), "GatherAll"));
            _queueResult.Text = "Gather everything: " + (answer.Ok ? "started." : "refused (" + answer.Code + ").");
        };
        gather.Controls.Add(gatherText, 0, 0);
        gather.Controls.Add(gatherAll, 1, 0);

        right.Controls.Add(_queue);
        right.Controls.Add(heading);
        right.Controls.Add(queueButtons);
        right.Controls.Add(_queueResult);
        right.Controls.Add(gather);
        _queueResult.Dock = DockStyle.Bottom;

        grid.Controls.Add(left, 0, 0);
        grid.Controls.Add(right, 1, 0);
        page.Controls.Add(grid);
        return page;
    }

    private void StartQueue()
    {
        if (Blocked() is { } why)
        {
            _queueResult.Text = why;
            return;
        }

        var lines = new List<string>();
        foreach (DataGridViewRow row in _queue.Rows)
        {
            if (row.Cells[0].Value is not string title)
            {
                continue;
            }

            var recipe = _vm.Recipes.FirstOrDefault(r => r.ViaPlan && r.Title == title);
            if (recipe is null || !int.TryParse(Convert.ToString(row.Cells[1].Value, CultureInfo.InvariantCulture), out var n) || n is < 0 or > CraftingRules.MaxPlanQty)
            {
                lines.Add(title + ": skipped (a count of 0-" + CraftingRules.MaxPlanQty.ToString(CultureInfo.InvariantCulture) + ")");
                continue;
            }

            var answer = _backend.Send(CraftingRules.CraftRequest(Guid.NewGuid().ToString("N"), recipe, n));
            lines.Add(title + " x" + (n == 0 ? "forever" : n.ToString(CultureInfo.InvariantCulture)) + ": " + (answer.Ok ? "queued" : "refused (" + answer.Code + ")"));
        }

        _queueResult.Text = lines.Count == 0 ? "Nothing to start: add a recipe on the left." : string.Join("   ", lines);
    }

    private readonly DataGridView _rules = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle, Font = Base,
    };
    private readonly Label _ruleState = new() { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 11f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _ruleToggle = Primary("Turn rule mode ON", 190);
    private readonly Label _rulesResult = new() { Dock = DockStyle.Bottom, Height = 40, Font = Small, ForeColor = Muted };

    /// <summary>
    /// Keep-stock rules ("keep Salad Savory at 120") and the rule mode: while it is on, the KeepStockLoop job reads each
    /// recipe's owned badge and makes the difference (gathering included), then checks again every 10 minutes.
    /// </summary>
    private TabPage RulesPage()
    {
        var page = new TabPage("Rules") { Padding = new Padding(16), BackColor = Color.White };
        _rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Recipe", FillWeight = 50, ReadOnly = true });
        _rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Keep at", FillWeight = 20 });
        _rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "You have (last check)", FillWeight = 30, ReadOnly = true });

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var search = new TextBox { PlaceholderText = "Search recipes...", Dock = DockStyle.Top, Font = Base };
        var matches = new ListBox { Dock = DockStyle.Fill, Font = Base, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        var keepAt = new NumericUpDown { Minimum = 1, Maximum = 100_000, Value = 100, Width = 90, Font = Base };
        var add = Secondary("Add / update", 120);
        void Fill()
        {
            var f = search.Text.Trim();
            matches.BeginUpdate();
            matches.Items.Clear();
            foreach (var t in _vm.Recipes.Where(r => r.ViaPlan).Select(r => r.Title).Distinct().Where(t => f.Length == 0 || t.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                matches.Items.Add(t);
            }

            matches.EndUpdate();
        }

        search.TextChanged += (_, _) => Fill();
        page.Enter += (_, _) => { Fill(); LoadRules(); };
        add.Click += (_, _) =>
        {
            if (matches.SelectedItem is not string title)
            {
                return;
            }

            foreach (DataGridViewRow r in _rules.Rows)
            {
                if (r.Cells[0].Value is string t && t == title)
                {
                    r.Cells[1].Value = ((int)keepAt.Value).ToString(CultureInfo.InvariantCulture);
                    return;
                }
            }

            _rules.Rows.Add(title, ((int)keepAt.Value).ToString(CultureInfo.InvariantCulture), "not checked yet");
        };
        matches.DoubleClick += (_, _) => add.PerformClick();
        var addRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52 };
        addRow.Controls.Add(new Label { Text = "Keep at", AutoSize = true, Margin = new Padding(0, 16, 8, 0) });
        addRow.Controls.Add(keepAt);
        addRow.Controls.Add(add);
        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 16, 0) };
        left.Controls.Add(matches);
        left.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 });
        left.Controls.Add(search);
        left.Controls.Add(addRow);

        var right = new Panel { Dock = DockStyle.Fill };
        var heading = new Label { Dock = DockStyle.Top, Height = 28, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Text = "Keep these in stock" };
        var save = Secondary("Save rules", 110);
        var remove = Secondary("Remove", 90);
        save.Click += (_, _) => SaveRules();
        remove.Click += (_, _) =>
        {
            foreach (DataGridViewRow r in _rules.SelectedRows)
            {
                _rules.Rows.Remove(r);
            }
        };
        var ruleButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52 };
        ruleButtons.Controls.Add(save);
        ruleButtons.Controls.Add(remove);

        var mode = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 64, ColumnCount = 2, BackColor = Color.FromArgb(242, 245, 250), Padding = new Padding(10, 6, 6, 6) };
        mode.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mode.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        mode.Controls.Add(_ruleState, 0, 0);
        mode.Controls.Add(_ruleToggle, 1, 0);
        _ruleToggle.Click += (_, _) => ToggleRuleMode();

        right.Controls.Add(_rules);
        right.Controls.Add(heading);
        right.Controls.Add(ruleButtons);
        right.Controls.Add(_rulesResult);
        right.Controls.Add(mode);

        grid.Controls.Add(left, 0, 0);
        grid.Controls.Add(right, 1, 0);
        page.Controls.Add(grid);
        return page;
    }

    /// <summary>Rule mode is on (kept on across restarts by the tray's keeper; the rule job may be between runs).</summary>
    private bool RuleModeOn => _actions.RuleModeOn();

    private bool RuleJobRunning => _vm.RunningJobName == "KeepStockLoop";

    private void LoadRules()
    {
        if (_actions.OpenKnowledgeForEdit() is not { } knowledge)
        {
            return;
        }

        _rules.Rows.Clear();
        var checks = LastRuleChecks();
        foreach (var rule in knowledge.GetKeepStockRules())
        {
            var title = _vm.Recipes.FirstOrDefault(r => r.ViaPlan && string.Equals(r.ProductKey, rule.Item.Value, StringComparison.Ordinal))?.Title ?? rule.Item.Value;
            var have = checks.TryGetValue(rule.Item.Value, out var c) ? c : "not checked yet";
            _rules.Rows.Add(title, rule.Min.ToString(CultureInfo.InvariantCulture), have);
        }
    }

    /// <summary>The last owned count rule mode read per item today ("349 at 21:20"), from the journal.</summary>
    private Dictionary<string, string> LastRuleChecks()
    {
        var last = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var file = new DirectoryInfo(_actions.JournalDir).GetFiles("*.jsonl").OrderByDescending(f => f.Name).FirstOrDefault();
            if (file is null)
            {
                return last;
            }

            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!line.Contains("\"event\":\"keepStock.check\"", StringComparison.Ordinal))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var f = r.GetProperty("fields");
                var at = r.TryGetProperty("utc", out var u) && DateTimeOffset.TryParse(u.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "?";
                if (f.TryGetProperty("item", out var item) && f.TryGetProperty("owned", out var owned))
                {
                    last[item.GetString() ?? string.Empty] = owned.ToString() + " at " + at;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException)
        {
            // shown as "not checked yet"
        }

        return last;
    }

    private void SaveRules()
    {
        if (_actions.IsJobRunning())
        {
            _rulesResult.Text = "Stop the running job (or turn rule mode off) before changing the rules.";
            return;
        }

        if (_actions.OpenKnowledgeForEdit() is not { } knowledge)
        {
            _rulesResult.Text = "The companion's knowledge could not be opened; try again in a moment.";
            return;
        }

        var rules = new List<Companion.Contracts.Knowledge.KeepStockRule>();
        foreach (DataGridViewRow r in _rules.Rows)
        {
            if (r.Cells[0].Value is string title && _vm.Recipes.FirstOrDefault(x => x.ViaPlan && x.Title == title) is { } recipe
                && int.TryParse(Convert.ToString(r.Cells[1].Value, CultureInfo.InvariantCulture), out var n) && n > 0)
            {
                rules.Add(new Companion.Contracts.Knowledge.KeepStockRule(new Companion.Contracts.Knowledge.ItemKey(recipe.ProductKey), n));
            }
        }

        knowledge.SetKeepStockRules(rules);
        knowledge.Save();
        _rulesResult.Text = rules.Count.ToString(CultureInfo.InvariantCulture) + " rule(s) saved.";
    }

    private void ToggleRuleMode()
    {
        if (RuleModeOn)
        {
            _rulesResult.Text = "Rule mode: " + (_actions.SetRuleMode(false) == "off" ? "off." : "could not turn off.");
            return;
        }

        if (Blocked() is { } why)
        {
            _rulesResult.Text = why;
            return;
        }

        SaveRules();
        var answer = _actions.SetRuleMode(true);
        _rulesResult.Text = "Rule mode: " + (answer is "started" or "on"
            ? "on. It stays on (also after a restart) until you turn it off. Click another window and leave the game alone."
            : answer.StartsWith("refused", StringComparison.Ordinal) ? "on, but the start was refused (" + answer + "); it tries again every minute." : "on (" + answer + ").");
    }

    private TabPage ActivityPage()
    {
        var page = new TabPage("Activity") { Padding = new Padding(16), BackColor = Color.White };
        var tiles = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 76 };
        tiles.Controls.Add(TileCard(_madeToday, "crafted today"));
        tiles.Controls.Add(TileCard(_tripsToday, "gathering trips today"));
        tiles.Controls.Add(TileCard(_pausesToday, "pauses today"));
        _activity.Columns.Add("Time", 90);
        _activity.Columns.Add("What happened", 620);
        _activity.ShowGroups = false;
        var refresh = Secondary("Refresh", 100);
        refresh.Dock = DockStyle.Bottom;
        refresh.Click += (_, _) => LoadActivity();
        page.Controls.Add(_activity);
        page.Controls.Add(_away);
        page.Controls.Add(tiles);
        page.Controls.Add(refresh);
        return page;
    }

    /// <summary>
    /// Roadmap C (the user's pick, 2026-10-20): production stats for the last 7 days, counted from the journal: crafts and
    /// crafts per hour of crafting, gathering trips and items per trip, pauses, safety stops and finished jobs, with a
    /// crafts-per-day chart and the week's top items. Map names are not in the journal yet, so there is no map ranking.
    /// </summary>
    private TabPage StatsPage()
    {
        var page = new TabPage("Stats") { Padding = new Padding(16), BackColor = Color.White };
        foreach (var (title, width) in new[] { ("Day", 110), ("Crafted", 70), ("Craft time", 80), ("Per hour", 70), ("Trips", 55), ("Gathered", 75), ("Per trip", 65), ("Pauses", 60), ("Safety stops", 90), ("Jobs ok", 70) })
        {
            _stats.Columns.Add(title, width);
        }

        _chart.Paint += (_, e) => DrawCraftsChart(e.Graphics);
        var heading = new Label { Dock = DockStyle.Top, Height = 28, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Text = "The last 7 days" };
        var refresh = Secondary("Refresh", 100);
        refresh.Dock = DockStyle.Bottom;
        refresh.Click += (_, _) => LoadStats();
        page.Controls.Add(_stats);
        page.Controls.Add(_chart);
        page.Controls.Add(heading);
        page.Controls.Add(_statsNote);
        page.Controls.Add(refresh);
        page.Enter += (_, _) => LoadStats();
        return page;
    }

    /// <summary>Counts the last 7 days off the UI thread, then fills the table, the chart and the note.</summary>
    private void LoadStats()
    {
        var dir = _actions.JournalDir;
        var firstDay = DateTime.Now.Date.AddDays(-6);
        var names = new Dictionary<long, string>();
        if (_backend.Knowledge is { } knowledge)
        {
            foreach (var i in knowledge.GetIconItems())
            {
                names.TryAdd(i.ItemId, i.Name);
            }

            foreach (var r in knowledge.GetRecipes().Where(r => r.ProductItemId is not null))
            {
                names.TryAdd(r.ProductItemId!.Value, r.Product.Value);
            }
        }

        _ = Task.Run(() =>
        {
            try
            {
                var from = new DateTimeOffset(firstDay);
                return JournalStats.ByDay(JournalStats.Lines(dir, from, DateTimeOffset.Now.AddMinutes(1)), firstDay);
            }
            catch (Exception)
            {
                return new SortedDictionary<DateTime, JournalStats>();
            }
        }).ContinueWith(t =>
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            BeginInvoke(() => ShowStats(t.Result, firstDay, names));
        }, TaskScheduler.Default);
    }

    private void ShowStats(SortedDictionary<DateTime, JournalStats> days, DateTime firstDay, Dictionary<long, string> names)
    {
        _days = days;
        _stats.BeginUpdate();
        _stats.Items.Clear();
        static string Hours(TimeSpan t) => t.TotalMinutes < 1 ? "-" : t.TotalHours >= 1 ? t.TotalHours.ToString("0.0", CultureInfo.InvariantCulture) + " h" : ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
        for (var day = DateTime.Now.Date; day >= firstDay; day = day.AddDays(-1))
        {
            var s = days.GetValueOrDefault(day) ?? new JournalStats();
            var perHour = s.CraftingTime.TotalHours > 0.05 ? (s.MadeTotal / s.CraftingTime.TotalHours).ToString("0", CultureInfo.InvariantCulture) : "-";
            var gathered = s.Gathered.Values.Sum();
            var perTrip = s.Trips > 0 ? (gathered / s.Trips).ToString(CultureInfo.InvariantCulture) : "-";
            _stats.Items.Add(new ListViewItem([
                day == DateTime.Now.Date ? "Today" : day.ToString("ddd dd MMM", CultureInfo.InvariantCulture),
                s.MadeTotal.ToString(CultureInfo.InvariantCulture), Hours(s.CraftingTime), perHour,
                s.Trips.ToString(CultureInfo.InvariantCulture), gathered.ToString(CultureInfo.InvariantCulture), perTrip,
                s.Pauses.ToString(CultureInfo.InvariantCulture), s.SafetyStops.ToString(CultureInfo.InvariantCulture),
                s.JobsEnded.Count == 0 ? "-" : s.JobsSucceeded.ToString(CultureInfo.InvariantCulture) + " of " + s.JobsEnded.Count.ToString(CultureInfo.InvariantCulture),
            ]));
        }

        _stats.EndUpdate();
        var made = new Dictionary<long, long>();
        var got = new Dictionary<long, long>();
        foreach (var s in days.Values)
        {
            foreach (var (k, v) in s.Made)
            {
                made[k] = made.GetValueOrDefault(k) + v;
            }

            foreach (var (k, v) in s.Gathered)
            {
                got[k] = got.GetValueOrDefault(k) + v;
            }
        }

        string Top(Dictionary<long, long> d) => d.Count == 0 ? "none" : string.Join(", ", d.OrderByDescending(kv => kv.Value).Take(3).Select(kv => names.GetValueOrDefault(kv.Key, "an item") + " " + kv.Value.ToString(CultureInfo.InvariantCulture)));
        _statsNote.Text = "This week's most crafted: " + Top(made) + ". Most gathered: " + Top(got) + "."
            + Environment.NewLine + "Counted from the companion's own journal. Gathering maps are not recorded yet, so there is no map ranking.";
        _chart.Invalidate();
    }

    /// <summary>Crafts per day as bars, oldest on the left (plain drawing, no chart library).</summary>
    private void DrawCraftsChart(Graphics g)
    {
        g.Clear(Color.White);
        var first = DateTime.Now.Date.AddDays(-6);
        var values = Enumerable.Range(0, 7).Select(i => (Day: first.AddDays(i), Made: _days.GetValueOrDefault(first.AddDays(i))?.MadeTotal ?? 0)).ToList();
        var max = Math.Max(1, values.Max(v => v.Made));
        var w = _chart.ClientSize.Width;
        var h = _chart.ClientSize.Height;
        var slot = Math.Max(1, w / 7);
        using var bar = new SolidBrush(Color.FromArgb(52, 120, 200));
        using var text = new SolidBrush(Color.FromArgb(70, 70, 70));
        using var font = new Font("Segoe UI", 8.5f);
        for (var i = 0; i < values.Count; i++)
        {
            var barH = (int)((h - 40) * (values[i].Made / (double)max));
            var x = i * slot + slot / 4;
            g.FillRectangle(bar, x, h - 20 - barH, slot / 2, barH);
            g.DrawString(values[i].Made.ToString(CultureInfo.InvariantCulture), font, text, x, Math.Max(0, h - 36 - barH));
            g.DrawString(values[i].Day.ToString("ddd", CultureInfo.InvariantCulture), font, text, x, h - 18);
        }
    }

    private TabPage SettingsPage()
    {
        var page = new TabPage("Settings") { Padding = new Padding(16), BackColor = Color.White };
        var list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        var first = Heading("What the companion may do");
        first.Margin = new Padding(0, 0, 0, 4);
        list.Controls.Add(first);
        foreach (var s in _actions.Settings)
        {
            var box = new CheckBox { Text = s.Text, AutoSize = true, Checked = s.Get(), Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(0, 8, 0, 0), Tag = s };
            box.Click += (_, _) =>
            {
                s.Set(box.Checked); // the tray asks for confirmation; the box then shows what really is set
                box.Checked = s.Get();
            };
            _settingBoxes.Add(box);
            list.Controls.Add(box);
            var help = Help(s.Help);
            help.Margin = new Padding(20, 0, 0, 4);
            list.Controls.Add(help);
        }

        list.Controls.Add(Heading("What the companion knows"));
        var knowledgeLine = new Label { AutoSize = true, Font = Base, Margin = new Padding(0, 0, 0, 4) };
        list.Controls.Add(knowledgeLine);
        list.Controls.Add(Help("What it learns (recipes, stock, item pictures) is shared by all your characters, and it works for whichever one you play."));

        list.Controls.Add(Heading("After an update"));
        list.Controls.Add(Help("A new build must be trusted once before it may act."));
        var trust = Secondary("Trust this build...", 170);
        trust.Click += (_, _) => _actions.TrustBuild();
        list.Controls.Add(trust);

        page.Controls.Add(list);
        page.Enter += (_, _) => knowledgeLine.Text = _vm.KnowledgeLine;
        knowledgeLine.Text = _vm.KnowledgeLine;
        return page;
    }

    private void Tick()
    {
        _today?.Tick();
        _vm.Refresh();
        // the companion cannot click while this window covers the mouse (a slide needs another window under it)
        _vm.WindowBlocksClicks = Visible && WindowState != FormWindowState.Minimized && Bounds.Contains(MousePosition);

        var (level, text) = _vm.Health;
        _headline.Text = text;
        _subline.Text = _vm.GateHint ?? _vm.KnowledgeLine;
        var (strip, ink) = level switch
        {
            HealthLevel.Green => (Color.FromArgb(46, 160, 67), Color.FromArgb(24, 110, 40)),
            HealthLevel.Amber => (Color.FromArgb(230, 150, 30), Color.FromArgb(150, 80, 0)),
            HealthLevel.Red => (Color.FromArgb(205, 50, 50), Color.Firebrick),
            _ => (Color.Silver, Muted),
        };
        _strip.BackColor = strip;
        _headline.ForeColor = ink;
        var halted = _actions.IsHalted();
        var stopped = !halted && _actions.IsStopped();
        var paused = _actions.IsPaused();
        var working = _vm.RunningJobName is not null || _vm.Host?.State is HostState.Running or HostState.Parked;
        _clearHalt.Visible = halted;
        _allow.Visible = stopped;
        _pause.Visible = !halted && !stopped && (working || paused);
        _stop.Visible = !halted && !stopped && (working || paused);
        _pause.Text = paused ? "Resume" : "Pause";
        _ruleState.Text = !RuleModeOn ? "Rule mode is OFF."
            : RuleJobRunning ? "Rule mode is ON: keeping these in stock (checks again every 10 minutes)."
            : _actions.RuleModeStatus?.Invoke() ?? "Rule mode is ON: starts again within a minute when the companion is free.";
        _ruleState.ForeColor = RuleModeOn ? Color.FromArgb(24, 110, 40) : Muted;
        _ruleToggle.Text = RuleModeOn ? "Turn rule mode OFF" : "Turn rule mode ON";

        RefreshRecipes();
        var row = _vm.Selected;
        _recipeTitle.Text = row?.Title ?? "Choose a recipe";
        _details.Text = row is null ? "Pick a recipe on the left (type to search)." : _vm.Details;
        _untilOut.Enabled = row is { Slot: > 0 } || row is { ViaPlan: false };
        _forever.Enabled = row is { ViaPlan: true };
        if (!_forever.Enabled)
        {
            _forever.Checked = false;
        }

        ApplyAmount();
        var b = _vm.Buttons;
        var blocked = Blocked();
        _start.Enabled = b.Start && blocked is null;
        _start.BackColor = _start.Enabled ? Accent : Color.FromArgb(160, 175, 195);
        _tips.SetToolTip(_start, blocked ?? "Makes the count: sub-recipes first, missing materials gathered.");
        _addToQueue.Enabled = row is { ViaPlan: true } && !_untilOut.Checked;
        _message.Text = _vm.Message;
        _hint.Text = _vm.ClickHint ?? string.Empty;

        foreach (var box in _settingBoxes)
        {
            if (box.Tag is SettingToggle s && box.Checked != s.Get())
            {
                box.Checked = s.Get();
            }
        }

        if (++_ticks % 30 == 0)
        {
            LoadActivity();
        }
    }

    private void RefreshRecipes()
    {
        var filter = _search.Text.Trim();
        if (_vm.RecipesVersion == _shownVersion && filter == _shownFilter)
        {
            return;
        }

        _binding = true;
        _recipes.BeginUpdate();
        try
        {
            _recipes.Items.Clear();
            _visible = [.. Enumerable.Range(0, _vm.Recipes.Count).Where(i => filter.Length == 0 || _vm.Recipes[i].Title.Contains(filter, StringComparison.OrdinalIgnoreCase))];
            foreach (var i in _visible)
            {
                var row = _vm.Recipes[i];
                _recipes.Items.Add((row.Slot > 0 && !row.SlotOutdated ? "★ " : string.Empty) + row.Title); // a favourite (the fast path: its slot)
            }

            var selectedAt = _vm.Selected is { } s ? _vm.Recipes.ToList().FindIndex(r => r.RecipeId == s.RecipeId && r.Slot == s.Slot) : -1;
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

    /// <summary>A job's name for people.</summary>
    private static string JobName(string job) => job switch
    {
        "Craft" => "Crafting goal",
        "KeepStockLoop" => "Rule mode",
        "GatherAll" => "Gather everything",
        "Gather" => "Gathering",
        "ScanFavorites" => "Favourites scan",
        "ScanRecipeList" => "Recipe list scan",
        "LearnRecipe" => "Learn recipe",
        "CraftLoop" => "Crafting",
        "ScanStock" => "Stock scan",
        _ => job,
    };

    /// <summary>How a job ended, in plain words (never a code).</summary>
    private static string JobEnd(string state, string code)
    {
        if (state == "Halted")
        {
            return "stopped for safety (see the top of the window)";
        }

        if (code is "plan.done" or "gatherAll.done" or "favorites.scanned" or "recipe.learned" or "loop.countReached" or "gather.done" or "ok" or "list.found" or "list.scanned")
        {
            return "finished";
        }

        if (code.Contains("gameExited", StringComparison.Ordinal))
        {
            return "the game was closed";
        }

        if (code.Contains("stopped", StringComparison.Ordinal) || code == "stop")
        {
            return "you pressed Stop";
        }

        if (code.Contains("cancel", StringComparison.Ordinal))
        {
            return "cancelled";
        }

        if (code.Contains("deferred", StringComparison.Ordinal))
        {
            return "paused too long (" + CraftingMessages.CauseText(code) + ")";
        }

        if (code.StartsWith("refused", StringComparison.Ordinal) || code.Contains(".refused", StringComparison.Ordinal) || state == "Refused")
        {
            return "could not start (a switch is off)";
        }

        return code switch
        {
            "plan.partial" => "partly done",
            "plan.shortfall" => "materials are missing",
            "plan.blocked" => "blocked (a recipe or material is missing)",
            "plan.exhausted" => "a step kept failing; stopped before it became a safety stop (details in the journal)",
            "plan.noProgress" => "the same plan repeated with nothing made; stopped",
            "gatherAll.nothingGathered" => "nothing could be gathered",
            "gatherAll.incomplete" => "stopped with materials left untried (see the last trip's reason)",
            "keepStock.noRules" => "no rules set",
            "loop.materialsOut" => "the materials ran out",
            "loop.noCraft" => "nothing could be crafted (materials missing?)",
            _ => "ended",
        };
    }

    private sealed record ActivityEntry(DateTime At, string Text, string? Collapse);

    /// <summary>
    /// The journal of the last two local days as plain sentences, grouped by day, newest first. Runs of rule mode's
    /// waiting (held off, all rules met, nothing to make) or of pauses become one line with their count and time span.
    /// The tiles count the local day.
    /// </summary>
    private void LoadActivity()
    {
        try
        {
            var files = new DirectoryInfo(_actions.JournalDir).GetFiles("*.jsonl").OrderByDescending(f => f.Name).Take(2).Reverse().ToList();
            if (files.Count == 0)
            {
                return;
            }

            var names = new Dictionary<long, string>();
            if (_backend.Knowledge is { } knowledge)
            {
                foreach (var i in knowledge.GetIconItems())
                {
                    names.TryAdd(i.ItemId, i.Name);
                }
            }

            string Name(JsonElement f) => f.TryGetProperty("itemId", out var id) && id.TryGetInt64(out var v) ? names.GetValueOrDefault(v, "a material") : f.TryGetProperty("item", out var it) ? it.ToString() : "?";
            string Str(JsonElement f, string key) => f.TryGetProperty(key, out var v) ? v.ToString() : "?";
            long Num(JsonElement f, string key) => f.TryGetProperty(key, out var v) && v.TryGetInt64(out var n) ? n : 0;
            var today = DateTime.Now.Date;
            var since = today.AddDays(-1);
            var entries = new List<ActivityEntry>();
            long made = 0;
            int trips = 0, pauses = 0;
            DateTime? gearWorn = null;
            DateTime? lastDismissed = null;
            (DateTime At, bool Seen)? lastGearCheck = null;
            foreach (var file in files)
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (!line.Contains("\"event\":\"", StringComparison.Ordinal) && !line.Contains("\"kind\":\"jobFinished\"", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    if (!r.TryGetProperty("fields", out var f) || f.ValueKind != JsonValueKind.Object
                        || !r.TryGetProperty("utc", out var u) || !DateTimeOffset.TryParse(u.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
                    {
                        continue;
                    }

                    var at = t.ToLocalTime().DateTime;
                    if (at < since)
                    {
                        continue;
                    }

                    var kind = r.TryGetProperty("kind", out var k) ? k.GetString() : null;
                    var ev = f.TryGetProperty("event", out var e) ? e.GetString() : null;
                    string? text = null;
                    string? collapse = null;
                    if (kind == "jobFinished")
                    {
                        text = JobName(Str(f, "job")) + " ended: " + JobEnd(Str(f, "state"), Str(f, "code"));
                    }
                    else if (ev == "craftLoop.round" && Num(f, "produced") is var pn and > 0)
                    {
                        made += at >= today ? pn : 0;
                        text = "Crafted " + pn.ToString(CultureInfo.InvariantCulture);
                    }
                    else if (ev == "gather.queued")
                    {
                        text = "Asked the Gathering Helper for " + Name(f);
                    }
                    else if (ev == "gather.clamped")
                    {
                        text = "The Helper took " + Str(f, "accepted") + " " + Name(f);
                    }
                    else if (ev == "gather.unqueued")
                    {
                        text = "Took " + Name(f) + " out of the Helper's queue (its stock is full)";
                    }
                    else if (ev == "plan.gather" && Num(f, "gathered") > 0)
                    {
                        trips += at >= today ? 1 : 0;
                        text = "Gathering trip done";
                    }
                    else if (ev == "gear.check")
                    {
                        // the screen check for the gear notice runs every 30 s while gathering: the latest one is the state
                        lastGearCheck = (at, Str(f, "status") == "Ok");
                        continue;
                    }
                    else if (ev == "gear.notice")
                    {
                        gearWorn = at >= today ? at : gearWorn;
                        lastDismissed = Str(f, "code") == "dismissed" ? at : lastDismissed;
                        // the text follows what the run did (audit R1.9): "pressed Ignore" only when it was pressed
                        text = Str(f, "code") == "dismissed"
                            ? "Your gathering gear wore out (its bonus no longer works): ticked 'Don't show again today' and pressed Ignore. Replace the gear in the game."
                            : "Your gathering gear wore out and its notice is still on screen (not dismissed: " + Str(f, "code") + "). Press Ignore in the game, or the companion tries again.";
                    }
                    else if (ev == "gatherAll.start")
                    {
                        text = "Gather everything started: " + Str(f, "materials") + " materials to fill";
                    }
                    else if (ev == "gatherAll.survey")
                    {
                        text = "Gather everything read " + Str(f, "read") + " materials' stock: " + Str(f, "full") + " already full, " + Str(f, "notGatherable") + " not in the Gathering Helper"
                            + (Num(f, "notSeen") > 0 ? ", " + Str(f, "notSeen") + " not shown by it now (greyed when full, or not offered here)" : string.Empty);
                    }
                    else if (ev == "gatherAll.trip")
                    {
                        text = "Gather everything, trip " + Str(f, "trip") + (f.TryGetProperty("picked", out var picked) ? " (emptiest: " + picked + ")" : string.Empty) + ": "
                            + (Num(f, "gathered") > 0 ? "gathered " + Str(f, "queued") : "nothing gathered (" + Str(f, "code") + ")")
                            + "; " + Str(f, "left") + " still to fill";
                    }
                    else if (ev == "gatherAll.deferred")
                    {
                        pauses += at >= today ? 1 : 0;
                        var cause = f.TryGetProperty("cause", out var c) ? c.ToString() : null;
                        text = "Gather everything paused (" + CraftingMessages.CauseText(cause) + "): trying again in " + Str(f, "pauseS") + " s";
                        collapse = "pause:" + CraftingMessages.CauseText(cause);
                    }
                    else if (ev == "gatherAll.end")
                    {
                        text = "Gather everything ended after " + Str(f, "trips") + " trips: " + Str(f, "full") + " full, " + Str(f, "gathered") + " gathered, "
                            + Str(f, "notGatherable") + " not in the Gathering Helper, " + (Num(f, "notSeen") > 0 ? Str(f, "notSeen") + " not shown by it, " : string.Empty) + Str(f, "left") + " left";
                    }
                    else if (ev == "host.previousUnclean")
                    {
                        text = "The companion's previous run ended without a clean exit (closed by Task Manager or Windows, or a crash); see the diagnostics folder";
                    }
                    else if (ev == "plan.exhausted")
                    {
                        text = "A step kept failing (" + Str(f, "cause") + "): the goal stopped before it became a safety stop";
                    }
                    else if (ev == "gather.notSeen")
                    {
                        text = "Could not see " + Name(f) + " in the Gathering Helper list this trip (" + Str(f, "walk") + " walk): trying again next trip";
                    }
                    else if (ev == "gather.atLimit")
                    {
                        text = Name(f) + " is at its stock limit in the Gathering Helper (" + Str(f, "reason") + ")";
                    }
                    else if (ev == "plan.helperRefuses")
                    {
                        text = "The Gathering Helper won't gather " + Str(f, "item") + " although you have none (is your Crab full?); stopped making what needs it";
                    }
                    else if (ev == "gather.resumePaused")
                    {
                        text = "Continued a paused gathering trip";
                    }
                    else if (ev == "plan.gatheringBlocked")
                    {
                        text = "Gathering failed twice the same way (" + Str(f, "code") + "): crafting from stock only (check your gathering tool)";
                    }
                    else if (ev == "plan.noProgress")
                    {
                        text = "The same plan came up again with nothing made: the goal stopped instead of repeating it";
                    }
                    else if (ev == "plan.stockNotFresh")
                    {
                        text = "Could not read your counts on the Make tab now (" + Str(f, "cause") + "): waiting instead of planning from old counts";
                    }
                    else if (ev == "plan.replan")
                    {
                        text = "Read a fresh count of " + Str(f, "product") + " (" + Str(f, "owned") + "): planned again with it";
                    }
                    else if (ev == "craftLoop.reselect")
                    {
                        text = "The wrong recipe was selected (a click was disturbed): selected it once more";
                    }
                    else if (ev == "keepStock.blocked")
                    {
                        text = "Rule mode: " + Str(f, "item") + " could not be made (" + Str(f, "reason") + "): checking it again in " + Str(f, "backoffMin") + " min";
                    }
                    else if (ev == "plan.deferred")
                    {
                        pauses += at >= today ? 1 : 0;
                        var cause = f.TryGetProperty("cause", out var c) ? c.ToString() : null;
                        text = "Paused (" + CraftingMessages.CauseText(cause) + "): trying again in " + Str(f, "pauseS") + " s";
                        collapse = "pause:" + CraftingMessages.CauseText(cause);
                    }
                    else if (ev == "plan.idle")
                    {
                        // a forever round that made nothing: not a pause for the user (audit R3.2 #15)
                        text = "Nothing was made this round (no materials, or nothing craftable): trying again in " + Str(f, "pauseS") + " s";
                        collapse = "idle";
                    }
                    else if (ev == "plan.end")
                    {
                        // the end code decides the words (audit R3.2 #30: every end read "Goal finished")
                        var code = Str(f, "code");
                        text = code == "plan.done" ? "Goal finished: made " + Str(f, "made") : "Goal ended (" + JobEnd("Failed", code) + "): made " + Str(f, "made");
                    }
                    else if (ev == "keepStock.check")
                    {
                        var met = Num(f, "owned") >= Num(f, "keepAt");
                        text = "Rule check: " + Str(f, "item") + ", you have " + Str(f, "owned") + ", keep at " + Str(f, "keepAt") + (met ? " (met)" : ": making the rest");
                        collapse = met ? "rule" : null;
                    }
                    else if (ev == "keepStock.waiting")
                    {
                        // "all rules met" only when none is blocked (audit R3.2 #18)
                        text = Num(f, "blocked") > 0
                            ? "Rule mode: " + Str(f, "blocked") + " rule(s) could not be made (" + Str(f, "reason") + "); checking again in " + Str(f, "minutes") + " min"
                            : Str(f, "heldOff") == "True" ? "Rule mode waited (it could not work just now); checking again in " + Str(f, "minutes") + " min"
                            : "Rule mode: all rules met; checking again in " + Str(f, "minutes") + " min";
                        collapse = "rule";
                    }
                    else if (ev == "keepStock.deferred")
                    {
                        pauses += at >= today ? 1 : 0;
                        text = "Rule mode held off (" + CraftingMessages.CauseText(f.TryGetProperty("cause", out var dc) ? dc.ToString() : null) + ")";
                        collapse = "rule";
                    }
                    else if (ev is "keepStock.notFound" or "keepStock.noRecipe")
                    {
                        text = "Rule mode: could not find the recipe for " + Str(f, "item");
                    }
                    else if (ev == "keepStock.ownedUnreadable")
                    {
                        text = "Rule mode: could not read how many " + Str(f, "item") + " you have";
                    }

                    if (text is not null)
                    {
                        entries.Add(new ActivityEntry(at, text, collapse));
                    }
                }
            }

            // one line per run of waiting
            var shown = new List<(DateTime At, string Time, string Text)>();
            for (var i = 0; i < entries.Count; i++)
            {
                var first = entries[i];
                if (first.Collapse is null)
                {
                    // the same line again and again (four scans that ended the same way) is one line with its count
                    var same = i;
                    while (same + 1 < entries.Count && entries[same + 1].Collapse is null && entries[same + 1].Text == first.Text)
                    {
                        same++;
                    }

                    var times = same - i + 1;
                    var when = first.At.ToString("HH:mm", CultureInfo.InvariantCulture) + (times > 1 && entries[same].At.ToString("HH:mm", CultureInfo.InvariantCulture) is var last && last != first.At.ToString("HH:mm", CultureInfo.InvariantCulture) ? "-" + last : string.Empty);
                    shown.Add((entries[same].At, when, times > 1 ? first.Text + " (" + times.ToString(CultureInfo.InvariantCulture) + " times)" : first.Text));
                    i = same;
                    continue;
                }

                var j = i;
                while (j + 1 < entries.Count && entries[j + 1].Collapse == first.Collapse)
                {
                    j++;
                }

                var run = entries.Skip(i).Take(j - i + 1).ToList();
                var span = first.At.ToString("HH:mm", CultureInfo.InvariantCulture) + (j > i ? "-" + entries[j].At.ToString("HH:mm", CultureInfo.InvariantCulture) : string.Empty);
                // a run of the same kind is one line, with its own cause (audit R3.2 #17, #18: the summary guessed the cause)
                var count = run.Count.ToString(CultureInfo.InvariantCulture);
                var text = run.Count == 1 ? first.Text
                    : first.Collapse!.StartsWith("pause:", StringComparison.Ordinal) ? "Paused " + count + " times (" + first.Collapse["pause:".Length..] + ")"
                    : first.Collapse == "idle" ? "Nothing was made in " + count + " rounds (no materials, or nothing craftable)"
                    : run.Any(x => x.Text.StartsWith("Rule mode held off", StringComparison.Ordinal)) ? "Rule mode held off " + count + " times (" + first.Text.Split('(', ')').ElementAtOrDefault(1) + ")"
                    : run.Any(x => x.Text.Contains("could not be made", StringComparison.Ordinal)) ? run[^1].Text + " (" + count + " checks)"
                    : "Rule mode: all rules met, nothing to make (" + count + " checks)";
                shown.Add((entries[j].At, span, text));
                i = j;
            }

            // state, not history (2026-10-23: the line stayed all day after the gloves were changed): shown only while the
            // latest screen check saw the notice, and for at most 30 minutes after it
            var noticeNow = lastGearCheck is { Seen: true } check && check.At >= DateTime.Now.AddMinutes(-30);
            // the companion answers the notice right after it sees it, so the next check no longer sees it: the dismissal is
            // the state for 30 minutes (audit R3.3 #42: the alert vanished within 30 s, before anyone could read it)
            var dismissedRecently = lastDismissed is { } d && d >= DateTime.Now.AddMinutes(-30) && !noticeNow;
            gearWorn = noticeNow ? lastGearCheck!.Value.At : dismissedRecently ? lastDismissed : null;
            _alert.Text = gearWorn is { } worn
                ? "Gear worn out (" + worn.ToString("HH:mm", CultureInfo.InvariantCulture) + "): its bonus no longer works"
                    + (dismissedRecently ? "; the notice was dismissed for today. Replace the gear in the game." : ". Replace it in the game.")
                : string.Empty;
            _alert.Visible = gearWorn is not null;
            if (_card is not null)
            {
                _card.Height = gearWorn is null ? 72 : 96;
            }

            _madeToday.Text = made.ToString(CultureInfo.InvariantCulture);
            var away = _actions.AwayCard?.Invoke();
            _away.Text = away ?? string.Empty;
            _away.Visible = away is not null;
            _tripsToday.Text = trips.ToString(CultureInfo.InvariantCulture);
            _pausesToday.Text = pauses.ToString(CultureInfo.InvariantCulture);
            _activity.BeginUpdate();
            _activity.Items.Clear();
            DateTime? lastDay = null;
            foreach (var (at, time, text) in Enumerable.Reverse(shown).Take(400))
            {
                if (at.Date != lastDay)
                {
                    // a day header row
                    lastDay = at.Date;
                    var title = at.Date == today ? "Today" : at.Date == today.AddDays(-1) ? "Yesterday" : at.Date.ToString("dddd d MMMM", CultureInfo.InvariantCulture);
                    _activity.Items.Add(new ListViewItem([title, string.Empty]) { Font = new Font(Base, FontStyle.Bold), BackColor = CardBack });
                }

                _activity.Items.Add(new ListViewItem([time, text]));
            }

            if (shown.Count == 0)
            {
                _activity.Items.Add(new ListViewItem([string.Empty, "Nothing happened yet today or yesterday."]));
            }

            _activity.EndUpdate();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            // the next refresh tries again
        }
    }
}
