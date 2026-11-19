using AvawareExportBot.Automation;
using AvawareExportBot.Core;
using AvawareExportBot.Core.Updates;

namespace AvawareExportBot.App;

/// <summary>A staged update the window has started applying: the app exits and Program swaps the exe.</summary>
public sealed record PendingUpdate(StagedUpdate Staged, RestartArgs Restart);

/// <summary>
/// Owns the bot's parts for the app's lifetime: settings, log, state store, scanner, worker pool, run controller and
/// updater, and the background timer that ticks them once a second (never on the UI thread).
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly object _settingsGate = new();
    private Settings _settings;
    private readonly System.Threading.Timer _timer;
    private readonly HttpClient _http;
    private readonly bool _canSwap;
    private int _ticking;
    private bool _holdLogged;
    private int _applyRaised;

    public AppHost(AppPaths paths, FileLog log, RestartArgs start)
    {
        Paths = paths;
        paths.EnsureCreated();
        Log = log;
        FileLog.PruneOlderThanKeepDays(paths.SnapsDir, DateTime.Now);
        _settings = Settings.Load(paths.SettingsFile);
        Store = new StateStore(paths.StateDb);
        if (Store.RecoveredExporting > 0)
            Log.Info($"{Store.RecoveredExporting} export(s) interrupted last time put back in the queue.");
        Pool = new WorkerPool(paths, CurrentSettings);
        var scanner = new ScannerRunner(new Scanner(Store, Log), CurrentSettings);
        Controller = new RunController(Store, Pool, scanner, new WindowsIdleSource(), new SystemClock(), Log, CurrentSettings, start.State);

        _http = MakeHttpClient();
        Updates = new UpdateService(new UpdateChecker(_http), new UpdateDownloader(_http, paths.UpdatesDir), paths.UpdateStateFile,
            AppInfo.Version, AppInfo.IsReleaseBuild, new SystemClock(), Log, CurrentSettings);
        _canSwap = AppInfo.IsReleaseBuild && UpdateSwapper.CanWriteBeside(AppInfo.ExePath);
        if (AppInfo.IsReleaseBuild && !_canSwap)
            Log.Warn($"Updates are off: {Path.GetDirectoryName(AppInfo.ExePath)} is not writable. Install with the line in the README.");
        if (start.UpdateFailed is not null) Updates.RecordFailed(start.UpdateFailed);

        Log.Info($"App {AppVersion.Format(AppInfo.Version)}{(AppInfo.IsReleaseBuild ? "" : " (dev build)")} started in {Controller.State}. " +
                 $"Root {_settings.RootFolder}, {_settings.Workers} workers, idle {_settings.IdleMinutes} min.");
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public AppPaths Paths { get; }
    public FileLog Log { get; }
    public StateStore Store { get; }
    public WorkerPool Pool { get; }
    public RunController Controller { get; }
    public UpdateService Updates { get; }

    /// <summary>Raised once, on the timer thread, when a staged update can be applied: it is ready and nothing is running.</summary>
    public event Action<StagedUpdate>? UpdateReadyToApply;

    /// <summary>Set by the window when it exits to apply an update; Program swaps the exe after the app has shut down.</summary>
    public PendingUpdate? PendingUpdate { get; set; }

    /// <summary>The staged update the app is holding work for, or null when there is none or applying is off.</summary>
    public StagedUpdate? HoldingFor =>
        _canSwap && CurrentSettings().AutoUpdate ? Updates.Staged : null;

    public static HttpClient MakeHttpClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // each call sets its own limit
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"AvawareExportBot/{AppVersion.Format(AppInfo.Version)}");
        return http;
    }

    public Settings CurrentSettings()
    {
        lock (_settingsGate) return _settings;
    }

    public void SaveSettings(Settings next)
    {
        next.Save(Paths.SettingsFile);
        lock (_settingsGate) _settings = next;
        Log.Info($"Settings saved. Root {next.RootFolder}, {next.Workers} workers, idle {next.IdleMinutes} min, rescan {next.RescanMinutes} min, " +
                 $"max attempts {next.MaxAttempts}, auto update {(next.AutoUpdate ? "on" : "off")}.");
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return; // the previous tick is still running
        try
        {
            Updates.Tick();
            // Set the hold before the controller ticks, so it claims nothing in the tick an update becomes ready.
            var holdFor = HoldingFor;
            Controller.UpdateHold = holdFor is not null;
            if (holdFor is null) _holdLogged = false;
            else if (!_holdLogged)
            {
                _holdLogged = true;
                var running = Controller.RunningExports;
                Log.Info($"Update {AppVersion.Format(holdFor.Version)} is ready: starting no new exports" +
                         (running > 0 ? $" until the {running} running finish." : "; applying now."));
            }
            Controller.Tick();
            if (holdFor is not null && Controller.IsDrained && Interlocked.Exchange(ref _applyRaised, 1) == 0)
                UpdateReadyToApply?.Invoke(holdFor);
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
        Updates.Dispose();
        await Controller.ShutdownAsync(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        Log.Info("App exited.");
    }

    public void Dispose()
    {
        _timer.Dispose();
        Updates.Dispose();
        _http.Dispose();
        Pool.Dispose();
        Store.Dispose();
    }
}
