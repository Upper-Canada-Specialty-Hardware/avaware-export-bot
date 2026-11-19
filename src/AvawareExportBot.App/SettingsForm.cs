using AvawareExportBot.Core;

namespace AvawareExportBot.App;

/// <summary>Edits the settings. Save stays disabled until every value is valid.</summary>
public sealed class SettingsForm : Form
{
    private readonly TextBox _root = new() { Width = 380 };
    private readonly TextBox _exe = new() { Width = 380 };
    private readonly NumericUpDown _workers = new() { Minimum = 1, Maximum = Settings.MaxWorkers, Width = 80 };
    private readonly NumericUpDown _idle = new() { Minimum = 1, Maximum = 24 * 60, Width = 80 };
    private readonly NumericUpDown _rescan = new() { Minimum = 1, Maximum = 7 * 24 * 60, Width = 80 };
    private readonly NumericUpDown _attempts = new() { Minimum = 1, Maximum = 100, Width = 80 };
    private readonly Label _problems = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(560, 0) };
    private readonly Button _save = new() { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };

    public SettingsForm(Settings current)
    {
        Result = current;
        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _root.Text = current.RootFolder;
        _exe.Text = current.AvaprojectExe;
        _workers.Value = Math.Clamp(current.Workers, 1, Settings.MaxWorkers);
        _idle.Value = Math.Clamp(current.IdleMinutes, 1, 24 * 60);
        _rescan.Value = Math.Clamp(current.RescanMinutes, 1, 7 * 24 * 60);
        _attempts.Value = Math.Clamp(current.MaxAttempts, 1, 100);

        var browseRoot = new Button { Text = "Browse…", AutoSize = true };
        browseRoot.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = _root.Text, UseDescriptionForTitle = true, Description = "Folder to scan for .apj files" };
            if (d.ShowDialog(this) == DialogResult.OK) _root.Text = d.SelectedPath;
        };
        var browseExe = new Button { Text = "Browse…", AutoSize = true };
        browseExe.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "AVAproject.exe|AVAproject.exe|Programs (*.exe)|*.exe", FileName = _exe.Text };
            if (d.ShowDialog(this) == DialogResult.OK) _exe.Text = d.FileName;
        };

        var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control input, Control? extra = null)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 8, 0) });
            grid.Controls.Add(input);
            grid.Controls.Add(extra ?? new Label { AutoSize = true });
        }
        Row("Root folder", _root, browseRoot);
        Row("AVAproject.exe", _exe, browseExe);
        Row($"Workers (1–{Settings.MaxWorkers})", _workers, new Label { Text = $"default {Settings.DefaultWorkers}", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        Row("Idle minutes before auto-start", _idle);
        Row("Rescan every (minutes)", _rescan);
        Row("Give up after attempts", _attempts);

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { cancel, _save });
        AcceptButton = _save;
        CancelButton = cancel;

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(grid);
        layout.Controls.Add(_problems);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        foreach (var c in new Control[] { _root, _exe, _workers, _idle, _rescan, _attempts }) c.TextChanged += (_, _) => Validate();
        foreach (var n in new[] { _workers, _idle, _rescan, _attempts }) n.ValueChanged += (_, _) => Validate();
        Validate();
    }

    /// <summary>The edited settings, valid when the dialog returns OK.</summary>
    public Settings Result { get; private set; }

    private new void Validate()
    {
        var candidate = new Settings
        {
            RootFolder = _root.Text.Trim(),
            AvaprojectExe = _exe.Text.Trim(),
            Workers = (int)_workers.Value,
            IdleMinutes = (int)_idle.Value,
            RescanMinutes = (int)_rescan.Value,
            MaxAttempts = (int)_attempts.Value,
        };
        var problems = candidate.Validate(Directory.Exists, File.Exists);
        _problems.Text = string.Join(Environment.NewLine, problems);
        _save.Enabled = problems.Count == 0;
        if (problems.Count == 0) Result = candidate;
    }
}
