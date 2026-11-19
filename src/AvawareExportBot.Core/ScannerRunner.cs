namespace AvawareExportBot.Core;

/// <summary>Runs <see cref="Scanner"/> on a background thread against the configured root.</summary>
public sealed class ScannerRunner : IScanRunner
{
    private readonly Scanner _scanner;
    private readonly Func<Settings> _settings;

    public ScannerRunner(Scanner scanner, Func<Settings> settings)
    {
        _scanner = scanner;
        _settings = settings;
    }

    public Task<ScanResult> RunAsync(IProgress<ScanProgress> progress, CancellationToken ct)
    {
        var root = _settings().RootFolder;
        return Task.Factory.StartNew(() => _scanner.Run(root, progress, ct), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
}
