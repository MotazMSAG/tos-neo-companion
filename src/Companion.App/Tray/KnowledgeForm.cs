using System.Drawing;
using System.Globalization;
using Companion.Contracts;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;

namespace Companion.App.Tray;

/// <summary>
/// Tray "Knowledge" (SPEC 10.3, 11.2): the user enters or corrects stock (source UserEntered), sets keep-stock minimums,
/// and confirms recipes read from the screen (userVerified, required before real commits). Opened by the user; refused
/// while a job runs so the engine thread stays the only writer during a job.
/// </summary>
internal sealed class KnowledgeForm : Form
{
    private readonly ICharacterKnowledge _knowledge;
    private readonly IClock _clock;
    private readonly Func<LogPosition?> _logPosition;
    private readonly Func<bool> _jobRunning;
    private readonly ListView _stock = new() { View = View.Details, FullRowSelect = true, Width = 520, Height = 180 };
    private readonly ListView _recipes = new() { View = View.Details, FullRowSelect = true, Width = 520, Height = 140 };
    private readonly TextBox _item = new() { Width = 260, MaxLength = 64 };
    private readonly NumericUpDown _count = new() { Minimum = 0, Maximum = 10_000_000, Width = 120 };
    private readonly CheckBox _keepStock = new() { Text = "Keep at least this many (keep-stock)", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(520, 0) };

    /// <summary>Creates the form for one character.</summary>
    public KnowledgeForm(ICharacterKnowledge knowledge, IClock clock, Func<LogPosition?> logPosition, Func<bool> jobRunning)
    {
        _knowledge = knowledge;
        _clock = clock;
        _logPosition = logPosition;
        _jobRunning = jobRunning;
        Text = "Companion knowledge";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _stock.Columns.Add("Item", 220);
        _stock.Columns.Add("Count", 90);
        _stock.Columns.Add("Source", 100);
        _stock.Columns.Add("Stale", 60);
        _recipes.Columns.Add("Recipe", 200);
        _recipes.Columns.Add("Product", 160);
        _recipes.Columns.Add("Verified", 70);
        _recipes.Columns.Add("Fields", 70);

        var set = new Button { Text = "Save count", AutoSize = true };
        set.Click += (_, _) => SaveCount();
        var verify = new Button { Text = "Confirm selected recipe (I checked every field)", AutoSize = true };
        verify.Click += (_, _) => VerifyRecipe();
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true };

        var entry = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        entry.Controls.Add(new Label { Text = "Item name", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        entry.Controls.Add(_item);
        entry.Controls.Add(_count);
        entry.Controls.Add(set);

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.Add(new Label { Text = "Stock (unseen items are unknown, never 0)", AutoSize = true });
        layout.Controls.Add(_stock);
        layout.Controls.Add(entry);
        layout.Controls.Add(_keepStock);
        layout.Controls.Add(new Label { Text = "Recipes read from the screen", AutoSize = true, Margin = new Padding(0, 10, 0, 0) });
        layout.Controls.Add(_recipes);
        layout.Controls.Add(verify);
        layout.Controls.Add(_status);
        layout.Controls.Add(close);
        Controls.Add(layout);
        CancelButton = close;
        Refill();
    }

    private void Refill()
    {
        _stock.Items.Clear();
        var view = _knowledge.GetStock();
        foreach (var e in view.Items.Values.OrderBy(e => e.Item.Value, StringComparer.Ordinal))
        {
            var count = e.Count?.ToString(CultureInfo.InvariantCulture)
                ?? (e.Range is { } r ? r.Min.ToString(CultureInfo.InvariantCulture) + ".." + r.Max.ToString(CultureInfo.InvariantCulture) : "?");
            _stock.Items.Add(new ListViewItem([e.Item.Value, count, e.Source.ToString(), e.Stale ? "yes" : ""]));
        }

        _recipes.Items.Clear();
        foreach (var r in _knowledge.GetRecipes().OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            _recipes.Items.Add(new ListViewItem([r.Id, r.Product.Value, r.UserVerified ? "yes" : "no", r.Confidence.ToString()]) { Tag = r.Id });
        }
    }

    private bool Guard()
    {
        if (_jobRunning())
        {
            _status.Text = "A job is running. Try again when it has finished.";
            return false;
        }

        return true;
    }

    private void SaveCount()
    {
        if (!Guard())
        {
            return;
        }

        var key = ItemKey.FromDisplayName(_item.Text ?? string.Empty);
        if (key.Value.Length == 0 || key.Value.Any(char.IsControl))
        {
            _status.Text = "Type the item name as the game shows it.";
            return;
        }

        var position = _logPosition();
        if (position is null)
        {
            _status.Text = "The game log is not readable yet; try again in a moment.";
            return;
        }

        var count = (long)_count.Value;
        try
        {
            if (_keepStock.Checked)
            {
                var rules = _knowledge.GetKeepStockRules().Where(r => r.Item != key).ToList();
                if (count > 0)
                {
                    rules.Add(new KeepStockRule(key, (int)Math.Min(count, int.MaxValue)));
                }

                _knowledge.SetKeepStockRules(rules);
            }
            else
            {
                _knowledge.ApplyObservations([new StockObservation(key, count, null, ObservationKind.UserEntered, _clock.UtcNow, position, 1.0, "user")]);
            }

            _knowledge.Save();
            _status.Text = "Saved.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _status.Text = "Not saved (the value was refused or the file could not be written).";
        }

        Refill();
    }

    private void VerifyRecipe()
    {
        if (!Guard() || _recipes.SelectedItems.Count != 1 || _recipes.SelectedItems[0].Tag is not string id)
        {
            return;
        }

        var recipe = _knowledge.GetRecipes().FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
        if (recipe is null)
        {
            return;
        }

        try
        {
            _knowledge.UpsertRecipe(recipe with { UserVerified = true, Confidence = RecipeConfidence.Complete });
            _knowledge.Save();
            _status.Text = "Recipe confirmed.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _status.Text = "Not saved (the recipe was refused or the file could not be written).";
        }

        Refill();
    }
}
