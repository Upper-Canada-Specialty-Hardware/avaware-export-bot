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
    private const int FileMenuX = 47, FileMenuY = 11;
    private const int SaveDialogFileNameId = 1001, SaveDialogSaveId = 1, SaveProjectNoId = 7;

    /// <summary>Visible window classes AVAproject (or Windows' text input in its process) shows in normal work.</summary>
    private static readonly HashSet<string> KnownClasses = new(StringComparer.Ordinal)
    {
        MdiClass, "ThunderRT6Main", "Static", "AfxOleControl42", PopupClass, "ABSxShadow", "ABSxToolShadow",
        "UAC_InputIndicatorOverlayWnd", "UAC Input Indicator", "tooltips_class32", "Touch Tooltip Window",
        "CiceroUIWndFrame", "IME", "MSCTFIME UI",
    };

    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UnknownWindowGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    private readonly HiddenDesktop _desktop;
    private readonly string _exePath;
    private readonly string _workDir;
    private readonly string _snapsDir;
    private readonly IntPtr _userDesktop;

    public AvaprojectExporter(HiddenDesktop desktop, string avaprojectExe, string workDir, string snapsDir)
    {
        _desktop = desktop;
        _exePath = avaprojectExe;
        _workDir = workDir;
        _snapsDir = snapsDir;
        _userDesktop = Native.OpenDesktop("Default", 0, false, 0x0040 /* DESKTOP_ENUMERATE */ | 0x0001 /* DESKTOP_READOBJECTS */);
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
            if (File.Exists(_target)) return; // made by someone else since the scan: nothing to do
            ResetWorkDir();
            if (!File.Exists(_apj)) throw new ExportFailure("copy: source .apj missing");
            File.Copy(_apj, _localApj, overwrite: true);
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
            var file = WaitFor(StepTimeout, wins => Popups(wins).FirstOrDefault(), onWait: attempt =>
            {
                if (attempt % 20 == 0) Windows.PostClick(dock, FileMenuX, FileMenuY); // first try, then every 5 s
            });
            Windows.PostKey(file, Native.VK_E);
            var export = WaitFor(StepTimeout, wins => Popups(wins).FirstOrDefault(h => h != file));
            Windows.PostKey(export, Native.VK_DOWN);
            Thread.Sleep(300);
            Windows.PostKey(export, Native.VK_RETURN);

            Enter("save dialog");
            var dlg = WaitFor(StepTimeout, wins => wins.FirstOrDefault(w => w.Class == DialogClass && w.Title == SaveDialogTitle).Handle);
            var edit = Windows.ChildById(dlg, SaveDialogFileNameId, "Edit");
            var save = Windows.ChildById(dlg, SaveDialogSaveId, "Button");
            if (edit == IntPtr.Zero || save == IntPtr.Zero) throw new ExportFailure("save dialog: file name box or Save button not found");
            Native.SendMessage(edit, Native.WM_SETTEXT, IntPtr.Zero, _localMdb);
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

        /// <summary>After Save: answer the metric dialog once if it appears, then wait until the .mdb is complete.</summary>
        private void WaitForExport(long apjBytes)
        {
            var metricAnswered = false;
            var stable = 0;
            long lastSize = -1;
            var ldb = Path.ChangeExtension(_localMdb, ".ldb");
            WaitFor(ExportTimeout(apjBytes), wins =>
            {
                var metric = wins.FirstOrDefault(w => w.Class == FormClass && w.Title == MetricTitle);
                if (metric.Handle != IntPtr.Zero)
                {
                    if (metricAnswered) return IntPtr.Zero; // still closing
                    ChooseMetric(metric.Handle);
                    metricAnswered = true;
                    return IntPtr.Zero;
                }
                var busy = wins.Any(w => (w.Class == FormClass && w.Title == ProgressTitle) || (w.Class == DialogClass && w.Title == SaveDialogTitle));
                var size = File.Exists(_localMdb) ? new FileInfo(_localMdb).Length : 0;
                if (busy || size == 0 || File.Exists(ldb) || size != lastSize)
                {
                    stable = 0;
                    lastSize = size;
                    return IntPtr.Zero;
                }
                return ++stable >= 3 ? (IntPtr)1 : IntPtr.Zero; // three quiet polls in a row
            }, extraKnown: w => (w.Class == FormClass && (w.Title == ProgressTitle || w.Title == MetricTitle)) || (w.Class == DialogClass && w.Title == SaveDialogTitle));
        }

        private static void ChooseMetric(IntPtr dialog)
        {
            var kids = Windows.Children(dialog);
            var metric = kids.FirstOrDefault(k => k.Visible && k.Class == "ThunderRT6OptionButton" && k.Title.StartsWith("Metric", StringComparison.Ordinal)).Handle;
            var ok = kids.FirstOrDefault(k => k.Visible && k.Class == "ThunderRT6CommandButton" && k.Title == "OK").Handle;
            if (metric == IntPtr.Zero || ok == IntPtr.Zero) throw new ExportFailure("export: metric dialog controls not found");
            Native.SendMessage(metric, Native.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            var check = (int)Native.SendMessage(metric, Native.BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
            if (check != 1) throw new ExportFailure("export: Metric (Millimeters) did not become selected");
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
            File.Copy(_localMdb, _partial, overwrite: true);
            try
            {
                File.Move(_partial, _target, overwrite: false);
            }
            catch (IOException) when (File.Exists(_target))
            {
                // Lost the race to another export of the same file: keep the existing one.
            }
            finally
            {
                TryDelete(_partial);
            }
        }

        /// <summary>
        /// Polls the process's windows on the hidden desktop until <paramref name="check"/> returns a handle.
        /// Fails on timeout, on the process exiting, on a window leaking to the user's desktop, or on an unknown window
        /// that stays up for <see cref="UnknownWindowGrace"/> (snapshot saved).
        /// </summary>
        private IntPtr WaitFor(TimeSpan timeout, Func<List<Win>, IntPtr> check, Action<int>? onWait = null, Func<Win, bool>? extraKnown = null)
        {
            var deadline = DateTime.UtcNow + timeout;
            for (var attempt = 0; ; attempt++)
            {
                _ct.ThrowIfCancellationRequested();
                if (_proc!.HasExited)
                    throw new ExportFailure($"{Step}: AVAproject exited (code 0x{_proc.ExitCode:X8})");
                if (_x._userDesktop != IntPtr.Zero && Windows.HasVisibleWindow(_x._userDesktop, _proc.Id))
                    throw new ExportFailure($"{Step}: AVAproject window leaked to the user desktop");

                var wins = Windows.TopLevel(_x._desktop.Handle, _proc.Id).Where(w => w.Width >= 20).ToList();
                CheckUnknown(wins, extraKnown);
                var found = check(wins);
                if (found != IntPtr.Zero) return found;
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
            if (w.Class == DialogClass) return w.Title == SaveDialogTitle || w.Title == SaveProjectTitle;
            return false;
        }

        private string? Snap(IntPtr h, string why)
        {
            if (h == IntPtr.Zero) return null;
            var name = $"{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{why}-{SafeName(Path.GetFileNameWithoutExtension(_apj))}.png";
            var path = Path.Combine(_x._snapsDir, name);
            return Windows.Snapshot(h, path) ? path : null;
        }

        private static string SafeName(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            var cleaned = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray());
            return cleaned.Length > 80 ? cleaned[..80] : cleaned;
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

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
