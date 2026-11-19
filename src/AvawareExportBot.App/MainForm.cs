using System.Diagnostics;
using System.Globalization;
using AvawareExportBot.Core;

namespace AvawareExportBot.App;

/// <summary>
/// The app's window. Shows the controller's state and sends it commands; holds no export logic.
/// Database reads for the lists happen on background threads; the UI thread only paints snapshots.
/// </summary>
public sealed class MainForm : Form
{
    private const int QueueLimit = 1000;
    private const int FailureLimit = 5000;

    private readonly AppHost _host;
    private readonly Button _start = new() { Text = "Start", AutoSize = true };
    private readonly Button _pause = new() { Text = "Pause", AutoSize = true };
    private readonly Button _stop = new() { Text = "Stop", AutoSize = true };
    private readonly Button _settingsButton = new() { Text = "Settings…", AutoSize = true };
    private readonly Label _stateLabel = new() { AutoSize = true, Padding = new Padding(12, 8, 0, 0), Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private readonly Label _counters = new() { AutoSize = true, Padding = new Padding(6, 4, 0, 4) };
    private readonly ListView _workers = MakeList(("Worker", 60), ("File", 330), ("Folder", 330), ("Size", 80), ("Step", 100), ("Elapsed", 80));
    private readonly Label _queueInfo = new() { AutoSize = true, Padding = new Padding(4) };
    private readonly ListView _queue = MakeList(("File", 360), ("Folder", 420), ("Size", 80), ("Modified", 130), ("Attempts", 70));
    private readonly TextBox _logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9f), BackColor = SystemColors.Window };
    private readonly Label _failInfo = new() { AutoSize = true, Padding = new Padding(4) };
    private readonly ListView _failures = MakeList(("File", 300), ("Folder", 300), ("Attempts", 70), ("Last error", 420), ("Last attempt", 130));
    private readonly NotifyIcon _tray = new() { Icon = SystemIcons.Application, Text = "Avaware Export Bot", Visible = true };
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };

    private long _logSequence = -1;
    private DateTime _lastListLoad = DateTime.MinValue;
    private int _listLoading;
    private bool _hadWork;
    private bool _exiting;

    public MainForm(AppHost host)
    {
        _host = host;
        Text = "Avaware Export Bot";
        Icon = SystemIcons.Application;
        MinimumSize = new Size(900, 600);
        Size = new Size(1200, 760);
        StartPosition = FormStartPosition.CenterScreen;
        LoadBounds();

        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(4), WrapContents = false };
        top.Controls.AddRange(new Control[] { _start, _pause, _stop, _settingsButton, _stateLabel });

        var workersBox = new GroupBox { Text = "Workers", Dock = DockStyle.Fill, Padding = new Padding(6) };
        _workers.Dock = DockStyle.Fill;
        workersBox.Controls.Add(_workers);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(QueueTab());
        tabs.TabPages.Add(LogTab());
        tabs.TabPages.Add(FailuresTab());

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(top, 0, 0);
        layout.Controls.Add(_counters, 0, 1);
        layout.Controls.Add(workersBox, 0, 2);
        layout.Controls.Add(tabs, 0, 3);
        Controls.Add(layout);

        _start.Click += (_, _) => { _host.Controller.Start(); RefreshNow(); };
        _pause.Click += (_, _) => { _host.Controller.Pause(); RefreshNow(); };
        _stop.Click += (_, _) => { _host.Controller.Stop(); RefreshNow(); };
        _settingsButton.Click += (_, _) => OpenSettings();

        BuildTray();
        _uiTimer.Tick += (_, _) => RefreshNow();
        _uiTimer.Start();
        RefreshNow();
    }

    // ---- layout ----

    private static ListView MakeList(params (string Name, int Width)[] columns)
    {
        var list = new ListView { View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false, Dock = DockStyle.Fill };
        foreach (var (name, width) in columns) list.Columns.Add(name, width);
        return list;
    }

    private TabPage QueueTab()
    {
        var page = new TabPage("Queue");
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(_queueInfo, 0, 0);
        panel.Controls.Add(_queue, 0, 1);
        page.Controls.Add(panel);
        return page;
    }

    private TabPage LogTab()
    {
        var page = new TabPage("Log");
        var open = new Button { Text = "Open log folder", AutoSize = true };
        open.Click += (_, _) => OpenInExplorer(_host.Paths.LogsDir);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        bar.Controls.Add(open);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(bar, 0, 0);
        panel.Controls.Add(_logBox, 0, 1);
        page.Controls.Add(panel);
        return page;
    }

    private TabPage FailuresTab()
    {
        var page = new TabPage("Failures");
        var retry = new Button { Text = "Retry selected", AutoSize = true };
        var retryAll = new Button { Text = "Retry all", AutoSize = true };
        var openFolder = new Button { Text = "Open folder", AutoSize = true };
        var openShot = new Button { Text = "Open screenshot", AutoSize = true };
        retry.Click += (_, _) =>
        {
            var paths = SelectedFailurePaths();
            var n = paths.Count(p => _host.Store.Retry(p));
            _host.Log.Info($"Retry requested for {n} failed file(s).");
            _lastListLoad = DateTime.MinValue;
        };
        retryAll.Click += (_, _) =>
        {
            var n = _host.Store.RetryAllFailed();
            _host.Log.Info($"Retry requested for all {n} failed file(s).");
            _lastListLoad = DateTime.MinValue;
        };
        openFolder.Click += (_, _) =>
        {
            var p = SelectedFailurePaths().FirstOrDefault();
            if (p is not null) OpenInExplorer(Path.GetDirectoryName(p)!);
        };
        openShot.Click += (_, _) =>
        {
            var row = _failures.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.Tag as FileRow;
            var shot = RunController.ScreenshotIn(row?.LastError);
            if (shot is not null && File.Exists(shot)) Process.Start(new ProcessStartInfo(shot) { UseShellExecute = true });
            else MessageBox.Show(this, "No screenshot for the selected failure.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        bar.Controls.AddRange(new Control[] { retry, retryAll, openFolder, openShot, _failInfo });
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(bar, 0, 0);
        panel.Controls.Add(_failures, 0, 1);
        page.Controls.Add(panel);
        return page;
    }

    private List<string> SelectedFailurePaths() =>
        _failures.SelectedItems.Cast<ListViewItem>().Select(i => ((FileRow)i.Tag!).Path).ToList();

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Start", null, (_, _) => _host.Controller.Start());
        menu.Items.Add("Pause", null, (_, _) => _host.Controller.Pause());
        menu.Items.Add("Stop", null, (_, _) => _host.Controller.Stop());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    // ---- refresh ----

    private void RefreshNow()
    {
        var snap = _host.Controller.Snapshot();
        var now = DateTime.UtcNow;

        _start.Enabled = snap.State != RunState.Running;
        _pause.Enabled = snap.State != RunState.Armed;
        _stop.Enabled = snap.State != RunState.Stopped;
        _stateLabel.Text = snap.State switch
        {
            RunState.Stopped => "Stopped",
            RunState.Armed => $"Armed: starts by itself after {snap.IdleThreshold.TotalMinutes:0} min idle (idle {snap.IdleTime.TotalMinutes:0} min)",
            RunState.Running => "Running",
            RunState.IdleRunning => "Running (idle): stops starting new exports on your next input",
            _ => snap.State.ToString(),
        };

        var c = snap.Counts;
        var scan = snap.Scanning
            ? $"scanning… {snap.ScanProgress?.ApjFound ?? 0:N0} .apj in {snap.ScanProgress?.DirectoriesSeen ?? 0:N0} folders"
            : snap.LastScanFinishedUtc is null ? "no scan yet" : $"last scan {snap.LastScanFinishedUtc.Value.ToLocalTime():g}";
        _counters.Text = $".apj found {c.Total:N0}   ·   done {c.Done:N0}   ·   pending {c.Pending:N0}   ·   failed {c.Failed:N0}   ·   exporting {c.Exporting:N0}   ·   {scan}";
        _tray.Text = Truncate($"Avaware Export Bot: {snap.State}, {c.Done:N0} done, {c.Pending:N0} pending", 63);

        _workers.BeginUpdate();
        while (_workers.Items.Count < snap.Workers.Count) _workers.Items.Add(new ListViewItem(new string[6]));
        while (_workers.Items.Count > snap.Workers.Count) _workers.Items.RemoveAt(_workers.Items.Count - 1);
        for (var i = 0; i < snap.Workers.Count; i++)
        {
            var w = snap.Workers[i];
            var cells = w.ApjPath is null
                ? new[] { w.Index.ToString(CultureInfo.InvariantCulture), "idle", "", "", "", "" }
                : new[]
                {
                    w.Index.ToString(CultureInfo.InvariantCulture), Path.GetFileName(w.ApjPath), Path.GetDirectoryName(w.ApjPath) ?? "",
                    FormatSize(w.SizeBytes), w.Step, Elapsed(now - (w.StartedUtc ?? now)),
                };
            var item = _workers.Items[i];
            for (var k = 0; k < cells.Length; k++)
                if (item.SubItems[k].Text != cells[k]) item.SubItems[k].Text = cells[k];
        }
        _workers.EndUpdate();

        RefreshLog();

        if (now - _lastListLoad >= TimeSpan.FromSeconds(5)) LoadListsInBackground(snap);

        // One balloon when a pass of the backlog completes: work was outstanding, and now nothing is pending or exporting.
        var outstanding = c.Pending + c.Exporting;
        if (outstanding > 0) _hadWork = true;
        else if (_hadWork && !snap.Scanning)
        {
            _tray.ShowBalloonTip(5000, "Avaware Export Bot", $"Backlog done: {c.Done:N0} exported, {c.Failed:N0} failed.", ToolTipIcon.Info);
            _hadWork = false;
        }
    }

    private void RefreshLog()
    {
        var (lines, seq) = _host.Log.Tail();
        if (seq == _logSequence) return;
        _logSequence = seq;
        _logBox.Lines = lines.ToArray();
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    private void LoadListsInBackground(ControllerSnapshot snap)
    {
        if (Interlocked.Exchange(ref _listLoading, 1) == 1) return;
        _lastListLoad = DateTime.UtcNow;
        Task.Run(() =>
        {
            try
            {
                var pending = _host.Store.ListByStatus(FileStatus.Pending, QueueLimit);
                var failed = _host.Store.ListByStatus(FileStatus.Failed, FailureLimit);
                if (!IsDisposed) BeginInvoke(() => ShowLists(pending, failed, snap));
            }
            catch (Exception ex)
            {
                _host.Log.Error("Could not load the lists: " + ex.Message);
            }
            finally
            {
                Volatile.Write(ref _listLoading, 0);
            }
        });
    }

    private void ShowLists(IReadOnlyList<FileRow> pending, IReadOnlyList<FileRow> failed, ControllerSnapshot snap)
    {
        var total = snap.Counts.Pending;
        _queueInfo.Text = total == 0
            ? $"Nothing waiting. Next scan {(snap.NextScanUtc is null ? "when started" : "at " + snap.NextScanUtc.Value.ToLocalTime().ToString("t", CultureInfo.CurrentCulture))}."
            : $"Showing newest {pending.Count:N0} of {total:N0} pending.";
        Fill(_queue, pending, r => new[]
        {
            Path.GetFileName(r.Path), Path.GetDirectoryName(r.Path) ?? "", FormatSize(r.SizeBytes),
            r.ApjModifiedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), r.Attempts.ToString(CultureInfo.InvariantCulture),
        });

        _failInfo.Text = failed.Count == 0 ? "No failures." : $"{snap.Counts.Failed:N0} failed.";
        Fill(_failures, failed, r => new[]
        {
            Path.GetFileName(r.Path), Path.GetDirectoryName(r.Path) ?? "", r.Attempts.ToString(CultureInfo.InvariantCulture),
            r.LastError ?? "", r.LastAttemptUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "",
        });
    }

    private static void Fill(ListView list, IReadOnlyList<FileRow> rows, Func<FileRow, string[]> cells)
    {
        var selected = new HashSet<string>(list.SelectedItems.Cast<ListViewItem>().Select(i => ((FileRow)i.Tag!).Path), StringComparer.OrdinalIgnoreCase);
        var top = list.TopItem?.Index ?? 0;
        list.BeginUpdate();
        list.Items.Clear();
        list.Items.AddRange(rows.Select(r => new ListViewItem(cells(r)) { Tag = r, Selected = selected.Contains(r.Path) }).ToArray());
        if (list.Items.Count > 0) list.TopItem = list.Items[Math.Min(top, list.Items.Count - 1)];
        list.EndUpdate();
    }

    // ---- settings ----

    private void OpenSettings()
    {
        using var dialog = new SettingsForm(_host.CurrentSettings().Clone());
        if (dialog.ShowDialog(this) == DialogResult.OK) _host.SaveSettings(dialog.Result);
    }

    // ---- window and exit ----

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; // closing the window hides it to the tray; Exit is in the tray menu
            Hide();
            return;
        }
        if (!_exiting)
        {
            // Logoff, shutdown, End task, Application.Exit: the same shutdown as Exit (cancel, kill AVAproject, rows back
            // to pending), waiting at most 20 s. Run off the UI thread so nothing it awaits can need this thread.
            _exiting = true;
            _uiTimer.Stop();
            _host.Log.Info($"Closing ({e.CloseReason}).");
            try { Task.Run(() => _host.ShutdownAsync()).Wait(TimeSpan.FromSeconds(20)); }
            catch (AggregateException ex) { _host.Log.Error("Shutdown failed: " + ex.GetBaseException().Message); }
            _tray.Visible = false;
        }
        SaveBounds();
        base.OnFormClosing(e);
    }

    private async Task ExitAsync()
    {
        var running = _host.Controller.RunningExports;
        if (running > 0)
        {
            ShowFromTray();
            var answer = MessageBox.Show(this, $"{running} export(s) are running and will be aborted. They go back in the queue.\n\nExit anyway?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }
        _exiting = true;
        _uiTimer.Stop();
        UseWaitCursor = true;
        await _host.ShutdownAsync();
        _tray.Visible = false;
        Close();
        Application.Exit();
    }

    private string BoundsFile => Path.Combine(_host.Paths.DataDir, "window.txt");

    private void LoadBounds()
    {
        try
        {
            if (!File.Exists(BoundsFile)) return;
            var p = File.ReadAllText(BoundsFile).Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            var r = new Rectangle(p[0], p[1], p[2], p[3]);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r)))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = r;
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException or IndexOutOfRangeException) { }
    }

    private void SaveBounds()
    {
        try
        {
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            File.WriteAllText(BoundsFile, string.Join(",", r.X, r.Y, r.Width, r.Height));
        }
        catch (IOException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _uiTimer.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- formatting ----

    private static void OpenInExplorer(string folder)
    {
        if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{Math.Max(1, bytes / 1024):N0} KB";

    private static string Elapsed(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
