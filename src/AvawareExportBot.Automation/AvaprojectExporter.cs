using System.Diagnostics;
using System.Globalization;
using AvawareExportBot.Core;

namespace AvawareExportBot.Automation;

/// <summary>Raised to end an export with a message naming the step that failed.</summary>
public sealed class ExportFailure : Exception
{
    public ExportFailure(string message, string? screenshot = null) : base(message) => Screenshot = screenshot;
    public string? Screenshot { get; }
}

/// <summary>Raised when an export cannot run for a reason that is not the file's: no attempt is counted.</summary>
public sealed class ExportDeferred : Exception
{
    public ExportDeferred(string message) : base(message) { }
}

/// <summary>
/// Turns one .apj into a .mdb by driving AVAproject on a hidden desktop with posted window messages only
/// (export approach ruling, #3; spec #8). Nothing is shown on the user's desktop, and the .apj on the share is never
/// opened: a local copy is. Must run on the thread attached to <see cref="HiddenDesktop"/>.
/// </summary>
public sealed class AvaprojectExporter
{
    // Window classes and titles (AVAproject 22.01.0003, VB6), from the spike.
    private const string MdiClass = "ThunderRT6MDIForm";
    private const string MdiTitlePrefix = "AVAproject - [";
    private const string FormClass = "ThunderRT6FormDC";
    private const string DockClass = "ABSActiveBarDockWnd";
    private const string PopupClass = "ABSxPopup";
    private const string DialogClass = "#32770";
    private const string SaveDialogTitle = "Export Project to Microsoft Access";
    private const string ProgressTitle = "Export Project to Microsoft Access";
    private const string MetricTitle = "Export Dimensional Values";
    private const string SaveProjectTitle = "Save Project";
    private const string ExportDialogPrefix = "Export Project to ";
    private const int FileMenuX = 47, FileMenuY = 11;
    private const int SaveDialogFileNameId = 1001, SaveDialogSaveId = 1, SaveProjectNoId = 7, CancelId = 2;
    private const int MenuAttempts = 3;
    private const int KeyDelay = 500;

    /// <summary>Visible window classes AVAproject (or Windows' text input in its process) shows in normal work.</summary>
    private static readonly HashSet<string> KnownClasses = new(StringComparer.Ordinal)
    {
        MdiClass, "ThunderRT6Main", "Static", "AfxOleControl42", PopupClass, "ABSxShadow", "ABSxToolShadow",
        "UAC_InputIndicatorOverlayWnd", "UAC Input Indicator", "tooltips_class32", "Touch Tooltip Window",
        "CiceroUIWndFrame", "IME", "MSCTFIME UI",
    };

    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PopupWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DialogWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UnknownWindowGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    private readonly HiddenDesktop _desktop;
    private readonly IntPtr _userDesktop;
    private readonly string _exePath;
    private readonly string _workDir;
    private readonly string _snapsDir;

    /// <param name="userDesktop">The user's desktop, opened for enumeration by the caller, for the leak guard.</param>
    public AvaprojectExporter(HiddenDesktop desktop, IntPtr userDesktop, string avaprojectExe, string workDir, string snapsDir)
    {
        if (userDesktop == IntPtr.Zero) throw new ArgumentException("The user's desktop handle is required for the leak guard.", nameof(userDesktop));
        _desktop = desktop;
        _userDesktop = userDesktop;
        _exePath = avaprojectExe;
        _workDir = workDir;
        _snapsDir = snapsDir;
    }

    /// <summary>Opens the user's desktop for the leak guard; zero when it cannot be opened.</summary>
    public static IntPtr OpenUserDesktop() =>
        Native.OpenDesktop("Default", 0, false, 0x0040 /* DESKTOP_ENUMERATE */ | 0x0001 /* DESKTOP_READOBJECTS */);

    public static void CloseUserDesktop(IntPtr handle)
    {
        if (handle != IntPtr.Zero) Native.CloseDesktop(handle);
    }

    /// <summary>Time allowed for the export itself, after Save: 10 min plus 2 min per MB of .apj.</summary>
    public static TimeSpan ExportTimeout(long apjBytes) => TimeSpan.FromMinutes(10 + 2.0 * apjBytes / (1024 * 1024));

    /// <summary>Exports <paramref name="apjPath"/> to <paramref name="targetMdbPath"/> (normally beside the .apj).</summary>
    public ExportOutcome Export(string apjPath, string targetMdbPath, IProgress<string>? step, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var run = new Run(this, apjPath, targetMdbPath, step, ct);
        try
        {
            run.Execute();
            return ExportOutcome.Ok(watch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return ExportOutcome.Cancel(watch.Elapsed);
        }
        catch (ExportDeferred d)
        {
            return ExportOutcome.Defer($"{run.Step}: {d.Message}", watch.Elapsed);
        }
        catch (ExportFailure f)
        {
            return ExportOutcome.Fail(f.Message, watch.Elapsed, f.Screenshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ExportOutcome.Fail($"{run.Step}: {ex.Message}", watch.Elapsed);
        }
        finally
        {
            run.Cleanup();
        }
    }

    /// <summary>One export's state.</summary>
    private sealed class Run
    {
        private readonly AvaprojectExporter _x;
        private readonly string _apj;
        private readonly string _target;
        private readonly IProgress<string>? _step;
        private readonly CancellationToken _ct;
        private readonly string _localApj;
        private readonly string _localMdb;
        private readonly string _partial;
        private Process? _proc;
        private readonly Dictionary<IntPtr, DateTime> _unknownSince = new();

        public Run(AvaprojectExporter x, string apj, string target, IProgress<string>? step, CancellationToken ct)
        {
            _x = x;
            _apj = apj;
            _target = target;
            _step = step;
            _ct = ct;
            _localApj = Path.Combine(x._workDir, "export.apj");
            _localMdb = Path.Combine(x._workDir, "export.mdb");
            _partial = target + ".partial";
        }

        public string Step { get; private set; } = "starting";

        private void Enter(string name)
        {
            Step = name;
            _step?.Report(name);
        }

        public void Execute()
        {
            Enter("copy");
            if (!File.Exists(_x._exePath)) throw new ExportDeferred($"AVAproject not found at {_x._exePath}");
            if (File.Exists(_target)) return; // made by someone else since the scan: nothing to do
            if (!File.Exists(_apj))
            {
                if (!Directory.Exists(Path.GetDirectoryName(_apj))) throw new ExportDeferred("the .apj's folder or share is not reachable");
                throw new ExportFailure("copy: source .apj missing");
            }
            ResetWorkDir();
            File.Copy(_apj, _localApj, overwrite: true);
            // A read-only source copies read-only; left that way it could never be deleted or overwritten.
            File.SetAttributes(_localApj, FileAttributes.Normal);
            var apjBytes = new FileInfo(_localApj).Length;

            Enter("launch");
            _proc = _x._desktop.Launch(_x._exePath, $"\"{_localApj}\"", Path.GetDirectoryName(_x._exePath)!);

            Enter("load");
            var mdi = WaitFor(LoadTimeout, wins =>
            {
                var m = wins.FirstOrDefault(w => w.Class == MdiClass && w.Title.StartsWith(MdiTitlePrefix, StringComparison.Ordinal));
                var splash = wins.Any(w => w.Class == FormClass && w.Title.Length == 0);
                return m.Handle != IntPtr.Zero && !splash ? m.Handle : IntPtr.Zero;
            });

            Enter("menu");
            var dock = Windows.Children(mdi).FirstOrDefault(c => c.Class == DockClass && c.Title == "DockTop").Handle;
            if (dock == IntPtr.Zero) throw new ExportFailure("menu: menu bar not found");
            var dlg = OpenAccessExportDialog(dock);

            Enter("save dialog");
            var edit = Windows.ChildById(dlg, SaveDialogFileNameId, "Edit");
            var save = Windows.ChildById(dlg, SaveDialogSaveId, "Button");
            if (edit == IntPtr.Zero || save == IntPtr.Zero) throw new ExportFailure("save dialog: file name box or Save button not found");
            if (!Windows.SetText(edit, _localMdb)) throw new ExportFailure("save dialog: AVAproject did not respond (setting the file name)");
            var typed = Windows.Text(edit);
            if (!string.Equals(typed, _localMdb, StringComparison.Ordinal))
                throw new ExportFailure($"save dialog: file name reads back as '{typed}'");
            Native.PostMessage(save, Native.BM_CLICK, IntPtr.Zero, IntPtr.Zero);

            Enter("export");
            WaitForExport(apjBytes);

            Enter("quit");
            Quit(mdi);

            Enter("move");
            Move();
            Enter("done");
        }

        /// <summary>
        /// File → Export Project → Microsoft Access (.mdb), retried up to <see cref="MenuAttempts"/> times. Posted keys
        /// can be dropped while a popup is still opening (a dropped Down picks "Ascii Text"); any step that does not
        /// happen — a popup not showing, a wrong export dialog, no dialog — closes the menus and retries the whole menu.
        /// An Access dialog that opens late is picked up while cleaning up.
        /// </summary>
        private IntPtr OpenAccessExportDialog(IntPtr dock)
        {
            for (var attempt = 1; ; attempt++)
            {
                var found = TryMenuOnce(dock);
                if (found != IntPtr.Zero && IsSaveDialog(Windows.Describe(found))) return found;

                // Clean up: cancel a wrong export dialog, close menus, and wait until the main window is alone,
                // taking the Access dialog if it turns up meanwhile.
                if (found != IntPtr.Zero)
                {
                    var cancel = Windows.ChildById(found, CancelId, "Button");
                    Native.PostMessage(cancel != IntPtr.Zero ? cancel : found, cancel != IntPtr.Zero ? Native.BM_CLICK : Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
                IntPtr late = IntPtr.Zero;
                WaitFor(StepTimeout, wins =>
                {
                    var access = wins.FirstOrDefault(IsSaveDialog);
                    if (access.Handle != IntPtr.Zero) { late = access.Handle; return late; }
                    var popups = wins.Where(w => w.Class == PopupClass).ToList();
                    foreach (var p in popups) Windows.PostKey(p.Handle, Native.VK_ESCAPE);
                    return popups.Count == 0 && !wins.Any(IsAnyExportDialog) ? (IntPtr)1 : IntPtr.Zero;
                }, extraKnown: IsAnyExportDialog);
                if (late != IntPtr.Zero) return late;

                if (attempt >= MenuAttempts)
                    throw new ExportFailure(found == IntPtr.Zero
                        ? $"menu: export dialog did not open after {MenuAttempts} tries"
                        : $"menu: wrong export dialog '{Windows.Text(found)}' after {MenuAttempts} tries");
            }
        }

        /// <summary>One pass through the menu. Returns the export dialog that opened, or zero when a step did not happen.</summary>
        private IntPtr TryMenuOnce(IntPtr dock)
        {
            var file = WaitFor(PopupWait, wins => Popups(wins).FirstOrDefault(), onWait: n =>
            {
                if (n == 0) Windows.PostClick(dock, FileMenuX, FileMenuY);
            }, timeoutReturnsZero: true);
            if (file == IntPtr.Zero) return IntPtr.Zero;
            Thread.Sleep(KeyDelay);
            Windows.PostKey(file, Native.VK_E);
            var export = WaitFor(PopupWait, wins => Popups(wins).FirstOrDefault(h => h != file), timeoutReturnsZero: true);
            if (export == IntPtr.Zero) return IntPtr.Zero;
            Thread.Sleep(KeyDelay);
            Windows.PostKey(export, Native.VK_DOWN);
            Thread.Sleep(KeyDelay);
            Windows.PostKey(export, Native.VK_RETURN);
            return WaitFor(DialogWait, wins => wins.FirstOrDefault(IsAnyExportDialog).Handle, extraKnown: IsAnyExportDialog, timeoutReturnsZero: true);
        }

        /// <summary>The Access save dialog: the #32770 with that caption that has the file name box.</summary>
        private static bool IsSaveDialog(Win w) =>
            w.Class == DialogClass && w.Title == SaveDialogTitle && Windows.ChildById(w.Handle, SaveDialogFileNameId, "Edit") != IntPtr.Zero;

        private static bool IsAnyExportDialog(Win w) =>
            w.Class == DialogClass && w.Title.StartsWith(ExportDialogPrefix, StringComparison.Ordinal) &&
            Windows.ChildById(w.Handle, SaveDialogFileNameId, "Edit") != IntPtr.Zero;

        /// <summary>
        /// After Save: answer the metric dialog once if it appears, then wait until the .mdb is complete. Done needs the
        /// export to have been seen running (its .ldb or the progress form, at least once) and then three quiet polls:
        /// absence alone could be the moment before the export starts.
        /// </summary>
        private void WaitForExport(long apjBytes)
        {
            var metricAnswered = false;
            var sawRunning = false;
            var stable = 0;
            long lastSize = -1;
            var ldb = Path.ChangeExtension(_localMdb, ".ldb");
            WaitFor(ExportTimeout(apjBytes), wins =>
            {
                var metric = wins.FirstOrDefault(w => w.Class == FormClass && w.Title == MetricTitle);
                if (metric.Handle != IntPtr.Zero)
                {
                    if (!metricAnswered)
                    {
                        ChooseMetric(metric.Handle);
                        metricAnswered = true;
                    }
                    return IntPtr.Zero;
                }
                var progress = wins.Any(w => w.Class == FormClass && w.Title == ProgressTitle);
                var saveDialog = wins.Any(IsSaveDialog);
                var locked = File.Exists(ldb);
                if (progress || locked) sawRunning = true;
                var size = File.Exists(_localMdb) ? new FileInfo(_localMdb).Length : 0;
                if (!sawRunning || progress || saveDialog || locked || size == 0 || size != lastSize)
                {
                    stable = 0;
                    lastSize = size;
                    return IntPtr.Zero;
                }
                return ++stable >= 3 ? (IntPtr)1 : IntPtr.Zero;
            }, extraKnown: w => (w.Class == FormClass && (w.Title == ProgressTitle || w.Title == MetricTitle)) || IsSaveDialog(w));
        }

        private static void ChooseMetric(IntPtr dialog)
        {
            var kids = Windows.Children(dialog);
            var metric = kids.FirstOrDefault(k => k.Visible && k.Class == "ThunderRT6OptionButton" && k.Title.StartsWith("Metric", StringComparison.Ordinal)).Handle;
            var ok = kids.FirstOrDefault(k => k.Visible && k.Class == "ThunderRT6CommandButton" && k.Title == "OK").Handle;
            if (metric == IntPtr.Zero || ok == IntPtr.Zero) throw new ExportFailure("export: metric dialog controls not found");
            if (!Windows.Send(metric, Native.BM_CLICK, IntPtr.Zero, IntPtr.Zero, out _) ||
                !Windows.Send(metric, Native.BM_GETCHECK, IntPtr.Zero, IntPtr.Zero, out var check))
                throw new ExportFailure("export: AVAproject did not respond to the metric dialog");
            if ((int)check != 1) throw new ExportFailure("export: Metric (Millimeters) did not become selected");
            Native.PostMessage(ok, Native.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Close AVAproject and always answer No to "Save changes?": the .apj copy is never written.</summary>
        private void Quit(IntPtr mdi)
        {
            Native.PostMessage(mdi, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            var deadline = DateTime.UtcNow + QuitTimeout;
            while (DateTime.UtcNow < deadline)
            {
                _ct.ThrowIfCancellationRequested();
                if (_proc!.HasExited) return;
                GuardAgainstLeak();
                var prompt = Windows.TopLevel(_x._desktop.Handle, _proc.Id).FirstOrDefault(w => w.Class == DialogClass && w.Title == SaveProjectTitle);
                if (prompt.Handle != IntPtr.Zero)
                {
                    var no = Windows.ChildById(prompt.Handle, SaveProjectNoId, "Button");
                    if (no != IntPtr.Zero && Windows.Text(no) == "&No") Native.PostMessage(no, Native.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                }
                Thread.Sleep(Poll);
            }
            // The .mdb is complete; a stuck quit is not a failed export. Cleanup kills the process.
        }

        /// <summary>Copy to &lt;base&gt;.mdb.partial beside the .apj, then rename. Never overwrites an existing .mdb.</summary>
        private void Move()
        {
            if (File.Exists(_target)) return; // someone else's .mdb appeared meanwhile: keep theirs
            var folder = Path.GetDirectoryName(_target)!;
            try
            {
                File.Copy(_localMdb, _partial, overwrite: true);
                File.Move(_partial, _target, overwrite: false);
            }
            catch (IOException) when (File.Exists(_target))
            {
                // Lost the race to another export of the same file: keep the existing one.
            }
            catch (IOException) when (!Directory.Exists(folder))
            {
                throw new ExportDeferred("the .apj's folder or share is not reachable");
            }
            finally
            {
                TryDelete(_partial);
            }
        }

        private void GuardAgainstLeak()
        {
            if (Windows.HasVisibleWindow(_x._userDesktop, _proc!.Id))
                throw new ExportFailure($"{Step}: AVAproject window leaked to the user desktop");
        }

        /// <summary>
        /// Polls the process's windows on the hidden desktop until <paramref name="check"/> returns a handle.
        /// Fails on timeout (or returns zero with <paramref name="timeoutReturnsZero"/>), on the process exiting, on a
        /// window leaking to the user's desktop, or on an unknown window that stays up for <see cref="UnknownWindowGrace"/>.
        /// </summary>
        private IntPtr WaitFor(TimeSpan timeout, Func<List<Win>, IntPtr> check, Action<int>? onWait = null, Func<Win, bool>? extraKnown = null,
            bool timeoutReturnsZero = false)
        {
            var deadline = DateTime.UtcNow + timeout;
            for (var attempt = 0; ; attempt++)
            {
                _ct.ThrowIfCancellationRequested();
                if (_proc!.HasExited)
                    throw new ExportFailure($"{Step}: AVAproject exited ({ExitCodeText(_proc)})");
                GuardAgainstLeak();

                var wins = Windows.TopLevel(_x._desktop.Handle, _proc.Id).Where(w => w.Width >= 20).ToList();
                CheckUnknown(wins, extraKnown);
                var found = check(wins);
                if (found != IntPtr.Zero) return found;
                if (DateTime.UtcNow >= deadline && timeoutReturnsZero) return IntPtr.Zero;
                if (DateTime.UtcNow >= deadline)
                    throw new ExportFailure($"{Step}: timed out after {timeout.TotalMinutes:0.#} min", Snap(wins.FirstOrDefault().Handle, "timeout"));
                onWait?.Invoke(attempt);
                Thread.Sleep(Poll);
            }
        }

        private void CheckUnknown(List<Win> wins, Func<Win, bool>? extraKnown)
        {
            var now = DateTime.UtcNow;
            var current = new HashSet<IntPtr>();
            foreach (var w in wins)
            {
                if (IsKnown(w) || (extraKnown?.Invoke(w) ?? false)) continue;
                current.Add(w.Handle);
                if (!_unknownSince.TryGetValue(w.Handle, out var since)) _unknownSince[w.Handle] = now;
                else if (now - since >= UnknownWindowGrace)
                    throw new ExportFailure($"{Step}: unexpected window: {w}", Snap(w.Handle, "unexpected"));
            }
            foreach (var gone in _unknownSince.Keys.Where(h => !current.Contains(h)).ToList()) _unknownSince.Remove(gone);
        }

        private static bool IsKnown(Win w)
        {
            if (KnownClasses.Contains(w.Class)) return true;
            if (w.Class == FormClass) return w.Title.Length == 0; // the splash; titled forms are matched per step
            // A same-captioned message box (overwrite prompt, path error) is not the save dialog: unexpected.
            if (w.Class == DialogClass) return IsSaveDialog(w) || w.Title == SaveProjectTitle;
            return false;
        }

        private string? Snap(IntPtr h, string why)
        {
            if (h == IntPtr.Zero) return null;
            var name = $"{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{why}-{SafeName(Path.GetFileNameWithoutExtension(_apj))}.png";
            var path = Path.Combine(_x._snapsDir, name);
            return Windows.SnapshotWithTimeout(_x._desktop.Handle, h, path, SnapshotTimeout) ? path : null;
        }

        private static string SafeName(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            var cleaned = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray());
            return cleaned.Length > 80 ? cleaned[..80] : cleaned;
        }

        private static string ExitCodeText(Process p)
        {
            try { return $"code 0x{p.ExitCode:X8}"; }
            catch (InvalidOperationException) { return "exit code unknown"; }
        }

        private static IEnumerable<IntPtr> Popups(List<Win> wins) => wins.Where(w => w.Class == PopupClass).Select(w => w.Handle);

        private void ResetWorkDir()
        {
            Directory.CreateDirectory(_x._workDir);
            foreach (var f in Directory.EnumerateFiles(_x._workDir)) TryDelete(f);
        }

        public void Cleanup()
        {
            if (_proc is not null)
            {
                try
                {
                    if (!_proc.HasExited)
                    {
                        _proc.Kill(entireProcessTree: true);
                        _proc.WaitForExit(10_000);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                _proc.Dispose();
            }
            TryDelete(_partial);
            try { ResetWorkDir(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>Deletes a file, clearing read-only first. Never throws.</summary>
        private static void TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
