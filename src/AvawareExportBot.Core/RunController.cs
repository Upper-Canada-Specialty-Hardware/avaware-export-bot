namespace AvawareExportBot.Core;

public enum RunState
{
    /// <summary>Starts no exports; idle auto-run off.</summary>
    Stopped,
    /// <summary>Starts no exports; idle auto-run on. The state at app launch.</summary>
    Armed,
    /// <summary>Started by the user's Start; starts exports; user input does not stop it.</summary>
    Running,
    /// <summary>Started by idle auto-run; starts exports until the user's next input.</summary>
    IdleRunning,
}

public sealed record WorkerSnapshot(int Index, string? ApjPath, long SizeBytes, string Step, DateTime? StartedUtc);

public sealed record ControllerSnapshot(
    RunState State,
    TimeSpan IdleTime,
    TimeSpan IdleThreshold,
    IReadOnlyList<WorkerSnapshot> Workers,
    StatusCounts Counts,
    bool Scanning,
    ScanProgress? ScanProgress,
    DateTime? LastScanFinishedUtc,
    DateTime? NextScanUtc);

/// <summary>
/// Decides when exports and scans start. Call <see cref="Tick"/> about once a second from a background timer.
/// Leaving Running or IdleRunning never cancels exports already running: they finish and their results are recorded;
/// only new claims stop. Only <see cref="ShutdownAsync"/> (app exit) cancels running exports.
/// </summary>
public sealed class RunController
{
    public static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(5);

    /// <summary>A failure's screenshot path is kept in its last error as "[screenshot: path]".</summary>
    public const string ScreenshotTag = "[screenshot: ";

    /// <summary>The screenshot path inside a stored error, or null.</summary>
    public static string? ScreenshotIn(string? error)
    {
        if (error is null) return null;
        var i = error.LastIndexOf(ScreenshotTag, StringComparison.Ordinal);
        if (i < 0) return null;
        var start = i + ScreenshotTag.Length;
        var end = error.IndexOf(']', start);
        return end > start ? error[start..end] : null;
    }

    private readonly StateStore _store;
    private readonly IExportRunner _runner;
    private readonly IScanRunner _scanner;
    private readonly IIdleSource _idle;
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly Func<Settings> _settings;
    private readonly object _gate = new();
    private readonly List<Slot?> _slots = new();

    private RunState _state = RunState.Armed;
    private DateTime _lastIdleCheck = DateTime.MinValue;
    private long _lastInputMarker;
    private TimeSpan _idleTime;
    private Task? _scanTask;
    private CancellationTokenSource? _scanCts;
    private ScanProgress? _scanProgress;
    private DateTime? _lastScanFinished;
    private StatusCounts _counts = new(0, 0, 0, 0);
    private bool _shuttingDown;

    private sealed class Slot
    {
        public required string Path { get; init; }
        public required long SizeBytes { get; init; }
        public required DateTime StartedUtc { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public volatile string Step = "starting";
        public Task? Task { get; set; }
    }

    public RunController(StateStore store, IExportRunner runner, IScanRunner scanner, IIdleSource idle, IClock clock, ILog log, Func<Settings> settings)
    {
        _store = store;
        _runner = runner;
        _scanner = scanner;
        _idle = idle;
        _clock = clock;
        _log = log;
        _settings = settings;
        _lastInputMarker = idle.LastInputMarker;
        _lastScanFinished = store.LastFinishedScan()?.FinishedUtc;
        _counts = store.GetCounts();
    }

    public RunState State
    {
        get { lock (_gate) return _state; }
    }

    public int RunningExports
    {
        get { lock (_gate) return _slots.Count(s => s is not null); }
    }

    /// <summary>The user's Start button: run now, whatever the input.</summary>
    public void Start() => SetState(RunState.Running, "start pressed");

    /// <summary>The user's Pause button: no new exports, idle auto-run stays armed.</summary>
    public void Pause() => SetState(RunState.Armed, "pause pressed");

    /// <summary>The user's Stop button: no new exports, idle auto-run off.</summary>
    public void Stop() => SetState(RunState.Stopped, "stop pressed");

    private void SetState(RunState next, string why)
    {
        lock (_gate)
        {
            if (_shuttingDown || _state == next) return;
            _log.Info($"State {_state} -> {next} ({why}).");
            _state = next;
            _lastInputMarker = _idle.LastInputMarker;
        }
    }

    private static bool IsActive(RunState s) => s is RunState.Running or RunState.IdleRunning;

    /// <summary>One step of the controller: idle rules, scan scheduling, and filling free worker slots.</summary>
    public void Tick()
    {
        var settings = _settings();
        lock (_gate)
        {
            if (_shuttingDown) return;
            var now = _clock.UtcNow;

            if (now - _lastIdleCheck >= IdlePoll)
            {
                _lastIdleCheck = now;
                var marker = _idle.LastInputMarker;
                _idleTime = _idle.IdleTime;
                if (_state == RunState.IdleRunning && marker != _lastInputMarker)
                {
                    _log.Info("State IdleRunning -> Armed (user input; running exports will finish).");
                    _state = RunState.Armed;
                }
                else if (_state == RunState.Armed && _idleTime >= TimeSpan.FromMinutes(settings.IdleMinutes))
                {
                    _log.Info($"State Armed -> IdleRunning (no input for {(int)_idleTime.TotalMinutes} min).");
                    _state = RunState.IdleRunning;
                }
                _lastInputMarker = marker;
            }

            if (IsActive(_state))
            {
                if (_scanTask is null && ScanDue(now, settings)) StartScanLocked();
                FillSlotsLocked(settings);
            }
        }

        var counts = _store.GetCounts();
        lock (_gate) _counts = counts;
    }

    private bool ScanDue(DateTime now, Settings settings) =>
        _lastScanFinished is null || now - _lastScanFinished.Value >= TimeSpan.FromMinutes(settings.RescanMinutes);

    private void StartScanLocked()
    {
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        _scanProgress = null;
        var progress = new SyncProgress<ScanProgress>(p => { lock (_gate) _scanProgress = p; });
        Task<ScanResult> task;
        try
        {
            task = _scanner.RunAsync(progress, cts.Token);
        }
        catch (Exception ex)
        {
            task = Task.FromException<ScanResult>(ex);
        }
        var done = task.ContinueWith(t =>
        {
            lock (_gate)
            {
                // A failed scan still counts as a scan, so an unreachable root is retried each interval, not each tick.
                _lastScanFinished = t.Status == TaskStatus.RanToCompletion ? t.Result.FinishedUtc : _clock.UtcNow;
                if (t.IsFaulted) _log.Error($"Scan failed: {t.Exception!.GetBaseException().Message}");
                _scanTask = null;
                _scanCts = null;
                cts.Dispose();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        // A scan that already finished (its continuation ran inline above) must not be recorded as running.
        // Otherwise the continuation runs later on another thread and needs _gate, which this caller holds,
        // so it clears _scanTask only after this assignment.
        if (!done.IsCompleted) _scanTask = done;
    }

    private void FillSlotsLocked(Settings settings)
    {
        var workers = Math.Max(1, settings.Workers);
        while (_slots.Count < workers) _slots.Add(null);
        for (var i = 0; i < workers; i++)
        {
            if (_slots[i] is not null) continue;
            var row = _store.ClaimNext();
            if (row is null) return;
            StartExportLocked(i, row);
        }
    }

    private void StartExportLocked(int index, FileRow row)
    {
        var slot = new Slot { Path = row.Path, SizeBytes = row.SizeBytes, StartedUtc = _clock.UtcNow, Cts = new CancellationTokenSource() };
        _slots[index] = slot;
        _log.Info($"Worker {index}: exporting {row.Path} ({row.SizeBytes / 1024} KB, attempt {row.Attempts + 1}).");
        var step = new SyncProgress<string>(s => slot.Step = s);
        Task<ExportOutcome> task;
        try
        {
            task = _runner.RunAsync(index, row.Path, step, slot.Cts.Token);
        }
        catch (Exception ex)
        {
            task = Task.FromException<ExportOutcome>(ex);
        }
        slot.Task = task.ContinueWith(t => Complete(index, slot, t), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void Complete(int index, Slot slot, Task<ExportOutcome> t)
    {
        var elapsed = _clock.UtcNow - slot.StartedUtc;
        var outcome = t.Status switch
        {
            TaskStatus.RanToCompletion => t.Result,
            TaskStatus.Canceled => ExportOutcome.Cancel(elapsed),
            _ => ExportOutcome.Fail("engine error: " + t.Exception!.GetBaseException().Message, elapsed),
        };

        try
        {
            switch (outcome.Kind)
            {
                case ExportOutcomeKind.Success:
                    _store.RecordSuccess(slot.Path, _clock.UtcNow);
                    _log.Info($"Worker {index}: done in {Format(outcome.Duration)}: {slot.Path}");
                    break;
                case ExportOutcomeKind.Cancelled:
                    _store.RecordCancelled(slot.Path);
                    _log.Info($"Worker {index}: cancelled: {slot.Path}");
                    break;
                default:
                    var shot = outcome.ScreenshotPath is null ? "" : $" {ScreenshotTag}{outcome.ScreenshotPath}]";
                    var status = _store.RecordFailure(slot.Path, (outcome.Error ?? "unknown error") + shot, _clock.UtcNow, _settings().MaxAttempts);
                    _log.Warn($"Worker {index}: failed after {Format(outcome.Duration)} ({(status == FileStatus.Failed ? "giving up" : "will retry")}): {slot.Path}: {outcome.Error}{shot}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Worker {index}: could not record the result for {slot.Path}: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                if (index < _slots.Count && ReferenceEquals(_slots[index], slot)) _slots[index] = null;
            }
            slot.Cts.Dispose();
        }
    }

    public ControllerSnapshot Snapshot()
    {
        var settings = _settings();
        lock (_gate)
        {
            var workers = Enumerable.Range(0, Math.Max(settings.Workers, _slots.Count)).Select(i =>
            {
                var s = i < _slots.Count ? _slots[i] : null;
                return s is null
                    ? new WorkerSnapshot(i, null, 0, "idle", null)
                    : new WorkerSnapshot(i, s.Path, s.SizeBytes, s.Step, s.StartedUtc);
            }).ToList();
            DateTime? next = _lastScanFinished?.AddMinutes(settings.RescanMinutes);
            return new ControllerSnapshot(_state, _idleTime, TimeSpan.FromMinutes(settings.IdleMinutes), workers, _counts,
                _scanTask is not null, _scanProgress, _lastScanFinished, next);
        }
    }

    /// <summary>App exit: stop everything, cancel the scan and every running export, and wait for them to clean up.</summary>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        List<Task> waits = new();
        CancellationTokenSource? scanCts;
        Slot[] running;
        lock (_gate)
        {
            _shuttingDown = true;
            _state = RunState.Stopped;
            scanCts = _scanCts;
            if (_scanTask is not null) waits.Add(_scanTask);
            running = _slots.OfType<Slot>().ToArray();
            waits.AddRange(running.Select(s => s.Task).OfType<Task>());
        }
        _log.Info($"Shutting down: cancelling {waits.Count} running task(s).");
        // Cancel outside the lock: a cancelled export can complete inline and clear its own slot.
        try { scanCts?.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var slot in running)
        {
            try { slot.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        await Task.WhenAny(Task.WhenAll(waits), Task.Delay(timeout)).ConfigureAwait(false);
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    /// <summary>IProgress that reports on the caller's thread (Progress&lt;T&gt; would post to a captured context).</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;
        public SyncProgress(Action<T> report) => _report = report;
        public void Report(T value) => _report(value);
    }
}
