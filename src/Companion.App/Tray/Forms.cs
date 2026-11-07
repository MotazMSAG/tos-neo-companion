using System.Drawing;

namespace Companion.App.Tray;

/// <summary>
/// Small forms opened by the user from the tray (SPEC 10.3): confirmations, a text prompt and the trust dialog. They are
/// ordinary windows of the companion, opened only by an explicit user click; notifications never use them.
/// </summary>
internal static class TrayForms
{
    /// <summary>Asks for a confirmation; the default button is Cancel.</summary>
    public static bool Confirm(string title, string text, string yesText = "Yes")
    {
        using var form = Build(title, text, yesText, out _);
        return form.ShowDialog() == DialogResult.Yes;
    }

    /// <summary>Asks for one line of text (max <paramref name="maxLength"/> chars); null when cancelled.</summary>
    public static string? Prompt(string title, string text, string initial, int maxLength)
    {
        using var form = Build(title, text, "OK", out var body);
        var box = new TextBox { Text = initial, MaxLength = maxLength, Width = 360, Margin = new Padding(0, 6, 0, 6) };
        body.Controls.Add(box);
        return form.ShowDialog() == DialogResult.Yes ? box.Text : null;
    }

    /// <summary>Builds a fixed dialog with a text, a body panel and Yes/Cancel buttons.</summary>
    internal static Form Build(string title, string text, string yesText, out FlowLayoutPanel body)
    {
        var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
            ShowInTaskbar = true,
        };
        body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        body.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(0, 0, 0, 8) });
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Bottom };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var yes = new Button { Text = yesText, DialogResult = DialogResult.Yes, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(yes);
        var outer = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        outer.Controls.Add(body);
        outer.Controls.Add(buttons);
        form.Controls.Add(outer);
        form.AcceptButton = cancel;
        form.CancelButton = cancel;
        return form;
    }
}
