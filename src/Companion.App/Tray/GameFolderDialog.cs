using System.Drawing;
using Companion.App.Hosting;

namespace Companion.App.Tray;

/// <summary>
/// The game folder dialogs, in plain words: the start dialog shown BEFORE the tray when the game could not be found
/// (Try again / Choose folder... / Exit), and the tray's Advanced > Game folder... dialog. Ordinary windows of the
/// companion, opened only at start or by a user click; the guardian never shows them. They never touch the game.
/// </summary>
internal static class GameFolderDialog
{
    /// <summary>The start dialog's text.</summary>
    public const string NotFoundText =
        "Tree of Savior: NEO was not found. Start the game and press Try again, or choose the game folder (the one with TOS.exe).";

    /// <summary>Shown when a chosen folder holds no game.</summary>
    public const string RejectedText =
        "That folder does not hold TOS.exe. Choose the game folder, the one with TOS.exe in it.";

    /// <summary>
    /// The start dialog. <paramref name="detect"/> runs the automatic search again (Try again). Returns the game folder,
    /// or null when the user pressed Exit or closed the window.
    /// </summary>
    public static string? AskAtStart(Func<GameFolderChoice?> detect)
    {
        ArgumentNullException.ThrowIfNull(detect);
        string? result = null;
        using var form = NewForm("TOS companion: find the game", out var text, out var status, out var buttons);
        text.Text = NotFoundText;

        var exit = new Button { Text = "Exit", DialogResult = DialogResult.Cancel, AutoSize = true };
        var choose = new Button { Text = "Choose folder...", AutoSize = true };
        var retry = new Button { Text = "Try again", AutoSize = true };
        buttons.Controls.Add(exit);
        buttons.Controls.Add(choose);
        buttons.Controls.Add(retry);
        form.CancelButton = exit;
        form.AcceptButton = retry;

        retry.Click += (_, _) =>
        {
            form.Cursor = Cursors.WaitCursor;
            GameFolderChoice? found;
            try
            {
                found = detect();
            }
            finally
            {
                form.Cursor = Cursors.Default;
            }

            if (found is not null)
            {
                result = found.Folder;
                form.DialogResult = DialogResult.OK;
                return;
            }

            status.Text = "Still not found. Is the game running and showing its window? You can also choose the folder.";
        };
        choose.Click += (_, _) =>
        {
            if (Browse(form, null) is { } picked)
            {
                if (GameFolderResolver.AcceptChosen(picked) is { } accepted)
                {
                    result = accepted;
                    form.DialogResult = DialogResult.OK;
                    return;
                }

                status.Text = RejectedText;
            }
        };

        return form.ShowDialog() == DialogResult.OK ? result : null;
    }

    /// <summary>
    /// The tray dialog: shows the folder in use and lets the user choose another one (same validation). Returns the new
    /// folder, or null when nothing changed. <paramref name="runningElsewhere"/> is the running game's folder when it differs
    /// from <paramref name="current"/>; <paramref name="variableWins"/> tells that TOS_GAME_DIR overrides a saved choice.
    /// </summary>
    public static string? AskFromTray(string current, string? runningElsewhere, bool variableWins)
    {
        string? result = null;
        using var form = NewForm("TOS companion: game folder", out var text, out var status, out var buttons);
        var lines = "The companion uses this game folder:" + Environment.NewLine + current;
        if (runningElsewhere is not null)
        {
            lines += Environment.NewLine + Environment.NewLine + "The game is running from another folder:" + Environment.NewLine + runningElsewhere;
        }

        lines += Environment.NewLine + Environment.NewLine + "A new folder is used after you restart the companion (tray > Exit, then start it again).";
        if (variableWins)
        {
            lines += Environment.NewLine + "Note: the TOS_GAME_DIR setting on this PC names a game folder, and it wins over a folder chosen here.";
        }

        text.Text = lines;

        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true };
        var choose = new Button { Text = "Choose folder...", AutoSize = true };
        buttons.Controls.Add(close);
        buttons.Controls.Add(choose);
        form.CancelButton = close;
        form.AcceptButton = close;
        if (runningElsewhere is not null)
        {
            var useRunning = new Button { Text = "Use the running game's folder", AutoSize = true };
            useRunning.Click += (_, _) =>
            {
                result = runningElsewhere;
                form.DialogResult = DialogResult.OK;
            };
            buttons.Controls.Add(useRunning);
        }

        choose.Click += (_, _) =>
        {
            if (Browse(form, current) is { } picked)
            {
                if (GameFolderResolver.AcceptChosen(picked) is { } accepted)
                {
                    result = accepted;
                    form.DialogResult = DialogResult.OK;
                    return;
                }

                status.Text = RejectedText;
            }
        };

        return form.ShowDialog() == DialogResult.OK && result is not null && !GameFolderResolver.SamePath(result, current) ? result : null;
    }

    /// <summary>The folder picker; null when cancelled.</summary>
    private static string? Browse(IWin32Window owner, string? start)
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose the game folder (the one with TOS.exe)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (start is not null && Directory.Exists(start))
        {
            picker.InitialDirectory = start;
        }

        return picker.ShowDialog(owner) == DialogResult.OK && !string.IsNullOrWhiteSpace(picker.SelectedPath) ? picker.SelectedPath : null;
    }

    /// <summary>A fixed dialog with a text, a status line (for "not found" answers) and a button row.</summary>
    private static Form NewForm(string title, out Label text, out Label status, out FlowLayoutPanel buttons)
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
            Icon = SystemIcons.Shield,
        };
        text = new Label { AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(0, 0, 0, 8) };
        status = new Label { AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(0, 0, 0, 8), ForeColor = Color.Firebrick };
        buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Bottom };
        var outer = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        outer.Controls.Add(text);
        outer.Controls.Add(status);
        outer.Controls.Add(buttons);
        form.Controls.Add(outer);
        return form;
    }
}
