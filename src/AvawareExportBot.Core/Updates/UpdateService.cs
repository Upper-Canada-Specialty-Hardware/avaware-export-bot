namespace AvawareExportBot.Core.Updates;

/// <summary>
/// Keeps the app's next version staged. Driven by the host's one-second tick: the first check runs a minute after
/// startup and then every six hours, while the "update automatically" setting is on. A manual check runs whatever
/// the setting. Only one check or download runs at a time. A dev build (one the release workflow did not make)
/// never checks.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly UpdateChecker _checker;
    private readonly UpdateDownloader _downloader;
    private readonly string _stateFile;
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly Func<Settings> _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private DateTime _nextCheck;
    private bool _busy;
    private StagedUpdate? _staged;
    private Version? _failed;
    private string _status;

    public UpdateService(UpdateChecker checker, UpdateDownloader downloader, string stateFile, Version current, bool releaseBuild,
        IClock clock, ILog log, Func<Settings> settings)
    {
        _checker = checker;
        _downloader = downloader;
        _stateFile = stateFile;
        _clock = clock;
        _log = log;
        _settings = settings;
        Current = current;
        ReleaseBuild = releaseBuild;
        _nextCheck = clock.UtcNow + FirstCheck;
        _failed = UpdateStateFile.LoadFailed(stateFile);
        _status = releaseBuild ? "not checked yet" : "dev build, updates off";
        if (releaseBuild)
        {
            try
            {
                _staged = downloader.FindStaged(current, _failed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn("Could not read the staged updates: " + ex.Message);
            }
        }
    }

    public Version Current { get; }

    /// <summary>True for an exe the release workflow built; only those update themselves.</summary>
    public bool ReleaseBuild { get; }

    public StagedUpdate? Staged
    {
        get { lock (_gate) return _staged; }
    }

    public Version? FailedVersion
    {
        get { lock (_gate) return _failed; }
    }

    /// <summary>One line for the status bar: what the last check found.</summary>
    public string Status
    {
        get { lock (_gate) return _status; }
    }

    public DateTime NextCheckUtc
    {
        get { lock (_gate) return _nextCheck; }
    }

    /// <summary>Starts a scheduled check when one is due. Returns the started check (for tests), or null.</summary>
    public Task<string>? Tick()
    {
        if (!ReleaseBuild || !_settings().AutoUpdate) return null;
        lock (_gate)
        {
            if (_busy || _clock.UtcNow < _nextCheck) return null;
            _busy = true;
            _nextCheck = _clock.UtcNow + Interval;
        }
        return Task.Run(() => RunAsync(null));
    }

    /// <summary>The tray's "Check for updates": checks now, whatever the setting. Returns the result for a balloon.</summary>
    /// <param name="progress">Told when a download starts, so the user hears about it before it finishes.</param>
    public async Task<string> CheckNowAsync(Action<string>? progress = null)
    {
        if (!ReleaseBuild) return "This is a dev build; updates are off.";
        lock (_gate)
        {
            if (_busy) return "Already checking for updates.";
            _busy = true;
        }
        return await RunAsync(progress).ConfigureAwait(false);
    }

    private async Task<string> RunAsync(Action<string>? progress)
    {
        try
        {
            var result = await _checker.CheckAsync(Current, FailedVersion, _cts.Token).ConfigureAwait(false);
            if (result.Kind == CheckKind.Error)
            {
                _log.Warn(result.Message);
                SetStatus($"check failed {Now()}");
                return result.Message;
            }
            if (result.Kind == CheckKind.None)
            {
                SetStatus($"up to date, checked {Now()}");
                return result.Message;
            }

            var release = result.Release!;
            var staged = Staged;
            if (staged is not null && staged.Version == release.Version) return $"Update {AppVersion.Format(release.Version)} is ready.";

            var downloading = $"Update {AppVersion.Format(release.Version)} downloading…";
            SetStatus($"update {AppVersion.Format(release.Version)} downloading");
            progress?.Invoke(downloading);
            _log.Info($"Update {AppVersion.Format(release.Version)} found; downloading.");
            var next = await _downloader.StageAsync(release, _cts.Token).ConfigureAwait(false);
            lock (_gate) _staged = next;
            var ready = $"Update {AppVersion.Format(next.Version)} downloaded and verified; it applies when no export is running.";
            _log.Info(ready);
            return ready;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return "Update check cancelled.";
        }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException)
        {
            _log.Error("Update failed: " + ex.Message);
            SetStatus($"update download failed {Now()}");
            return "Update failed: " + ex.Message;
        }
        finally
        {
            lock (_gate) _busy = false;
        }
    }

    /// <summary>
    /// The swapped-in version did not start: never offer it again, and drop its staged copy.
    /// Called by the old process during rollback, and by the restored app when told about it.
    /// </summary>
    public void RecordFailed(Version version)
    {
        lock (_gate)
        {
            _failed = version;
            if (_staged?.Version == version) _staged = null;
        }
        try
        {
            UpdateStateFile.SaveFailed(_stateFile, version);
            _downloader.Prune(Current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Could not record the failed update: " + ex.Message);
        }
    }

    private void SetStatus(string status)
    {
        lock (_gate) _status = status;
    }

    private string Now() => _clock.UtcNow.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Cancels a running check or download. The token source is not disposed: a check finishing late may still read it.</summary>
    public void Dispose() => _cts.Cancel();
}
