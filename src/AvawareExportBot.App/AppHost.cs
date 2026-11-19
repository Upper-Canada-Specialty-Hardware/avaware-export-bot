using AvawareExportBot.Automation;
using AvawareExportBot.Core;

namespace AvawareExportBot.App;

/// <summary>
/// Owns the bot's parts for the app's lifetime: settings, log, state store, scanner, worker pool and run controller,
/// and the background timer that ticks the controller once a second (never on the UI thread).
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly object _settingsGate = new();
    private Settings _settings;
    private readonly System.Threading.Timer _timer;
    private int _ticking;

    public AppHost(AppPaths paths)
    {
        Paths = paths;
        paths.EnsureCreated();
        Log = new FileLog(paths.LogsDir);
        _settings = Settings.Load(paths.SettingsFile);
        Store = new StateStore(paths.StateDb);
        if (Store.RecoveredExporting > 0)
            Log.Info($"{Store.RecoveredExporting} export(s) interrupted last time put back in the queue.");
        Pool = new WorkerPool(paths, CurrentSettings);
        var scanner = new ScannerRunner(new Scanner(Store, Log), CurrentSettings);
        Controller = new RunController(Store, Pool, scanner, new WindowsIdleSource(), new SystemClock(), Log, CurrentSettings);
        Log.Info($"App started. Root {_settings.RootFolder}, {_settings.Workers} workers, idle {_settings.IdleMinutes} min.");
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public AppPaths Paths { get; }
    public FileLog Log { get; }
    public StateStore Store { get; }
    public WorkerPool Pool { get; }
    public RunController Controller { get; }

    public Settings CurrentSettings()
    {
        lock (_settingsGate) return _settings;
    }

    public void SaveSettings(Settings next)
    {
        next.Save(Paths.SettingsFile);
        lock (_settingsGate) _settings = next;
        Log.Info($"Settings saved. Root {next.RootFolder}, {next.Workers} workers, idle {next.IdleMinutes} min, rescan {next.RescanMinutes} min, max attempts {next.MaxAttempts}.");
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return; // the previous tick is still running
        try
        {
            Controller.Tick();
        }
        catch (Exception ex)
        {
            Log.Error("Controller tick failed: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <summary>Stops the timer, cancels running work and waits for it (up to a minute).</summary>
    public async Task ShutdownAsync()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        await Controller.ShutdownAsync(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        Log.Info("App exited.");
    }

    public void Dispose()
    {
        _timer.Dispose();
        Pool.Dispose();
        Store.Dispose();
    }
}
