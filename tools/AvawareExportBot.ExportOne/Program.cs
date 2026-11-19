using AvawareExportBot.Automation;
using AvawareExportBot.Core;

// ExportOne <apj> <outdir> [worker]
// Exports one .apj to <outdir>\<base>.mdb with the real engine on a hidden desktop. For manual checks of the engine;
// writes nothing beside the .apj.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: ExportOne <apj> <outdir> [worker]");
    return 2;
}

var apj = Path.GetFullPath(args[0]);
var outDir = Path.GetFullPath(args[1]);
var worker = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 90;
Directory.CreateDirectory(outDir);
var target = Path.Combine(outDir, Path.GetFileNameWithoutExtension(apj) + ".mdb");

var paths = AppPaths.Default;
paths.EnsureCreated();
var settings = Settings.Load(paths.SettingsFile);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

ExportOutcome outcome;
var thread = new Thread(() =>
{
    using var desktop = new HiddenDesktop($"avaware-export-one-{worker}");
    desktop.AttachCurrentThread();
    var userDesktop = AvaprojectExporter.OpenUserDesktop();
    try
    {
        var exporter = new AvaprojectExporter(desktop, userDesktop, settings.AvaprojectExe, Path.Combine(paths.WorkDir, "one-" + worker), paths.SnapsDir);
        outcome = exporter.Export(apj, target, new StepPrinter(), cts.Token);
    }
    finally
    {
        AvaprojectExporter.CloseUserDesktop(userDesktop);
    }
});
outcome = ExportOutcome.Fail("did not run", TimeSpan.Zero);
thread.Start();
thread.Join();

Console.WriteLine($"{outcome.Kind} in {outcome.Duration:hh\\:mm\\:ss}{(outcome.Error is null ? "" : ": " + outcome.Error)}");
if (outcome.ScreenshotPath is not null) Console.WriteLine($"screenshot: {outcome.ScreenshotPath}");
if (File.Exists(target)) Console.WriteLine($"{target} ({new FileInfo(target).Length:N0} bytes)");
return outcome.Kind == ExportOutcomeKind.Success ? 0 : 1;

internal sealed class StepPrinter : IProgress<string>
{
    private readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();
    public void Report(string value) => Console.WriteLine($"  {_watch.Elapsed:mm\\:ss} {value}");
}
