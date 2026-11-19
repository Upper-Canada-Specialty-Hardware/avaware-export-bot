using AvawareExportBot.Core;
using Xunit;

namespace AvawareExportBot.Core.Tests;

public sealed class RunControllerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly StateStore _store;
    private readonly FakeClock _clock = new();
    private readonly FakeIdle _idle = new();
    private readonly FakeRunner _runner = new();
    private readonly FakeScanner _scanner;
    private readonly ListLog _log = new();
    private readonly Settings _settings = new() { Workers = 2, IdleMinutes = 60, RescanMinutes = 60, MaxAttempts = 3 };
    private readonly RunController _c;

    public RunControllerTests()
    {
        _store = new StateStore(_dir.Combine("state.db"));
        _scanner = new FakeScanner(_store, _clock);
        _c = new RunController(_store, _runner, _scanner, _idle, _clock, _log, () => _settings);
    }

    private void Files(int n)
    {
        _scanner.NextResult = Enumerable.Range(0, n)
            .Select(i => new ScannedApj($@"T:\p{i}.apj", 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i), false))
            .ToList();
    }

    /// <summary>Advances past the idle poll interval and ticks.</summary>
    private void Step(TimeSpan? by = null)
    {
        _clock.Advance(by ?? RunController.IdlePoll);
        _c.Tick();
    }

    [Fact]
    public void Starts_armed_and_does_nothing_until_started_or_idle()
    {
        Files(3);
        Step();
        Assert.Equal(RunState.Armed, _c.State);
        Assert.Equal(0, _scanner.Runs);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public void Start_scans_then_fills_at_most_the_configured_workers()
    {
        Files(5);
        _c.Start();
        Step();
        Assert.Equal(RunState.Running, _c.State);
        Assert.Equal(1, _scanner.Runs);
        Assert.Equal(2, _runner.Calls.Count);
        Step();
        Assert.Equal(2, _runner.Calls.Count);
        Assert.Equal(2, _runner.MaxConcurrent);
    }

    [Fact]
    public void Exports_newest_first()
    {
        Files(3);
        _c.Start();
        Step();
        Assert.Equal(@"T:\p2.apj", _runner.Calls[0].Path);
        Assert.Equal(@"T:\p1.apj", _runner.Calls[1].Path);
    }

    [Fact]
    public void A_finished_export_frees_its_slot_for_the_next()
    {
        Files(3);
        _c.Start();
        Step();
        _runner.Finish(0, ExportOutcome.Ok(TimeSpan.FromSeconds(5)));
        Assert.Equal(FileStatus.Done, _store.Get(@"T:\p2.apj")!.Status);
        Step();
        Assert.Equal(3, _runner.Calls.Count);
        Assert.Equal(0, _runner.Calls[2].Worker);
    }

    [Fact]
    public void Input_is_ignored_while_running_from_start()
    {
        Files(1);
        _c.Start();
        Step();
        _idle.Input();
        Step();
        Assert.Equal(RunState.Running, _c.State);
    }

    [Fact]
    public void Idle_threshold_auto_runs_and_input_returns_to_armed_without_cancelling()
    {
        Files(5);
        _idle.IdleTime = TimeSpan.FromMinutes(59);
        Step();
        Assert.Equal(RunState.Armed, _c.State);

        _idle.IdleTime = TimeSpan.FromMinutes(60);
        Step();
        Assert.Equal(RunState.IdleRunning, _c.State);
        Assert.Equal(2, _runner.Calls.Count);

        _idle.Input();
        Step();
        Assert.Equal(RunState.Armed, _c.State);
        Assert.All(_runner.Calls, c => Assert.False(c.Ct.IsCancellationRequested));
        Assert.Equal(2, _runner.Running);

        _runner.Finish(0, ExportOutcome.Ok(TimeSpan.FromMinutes(1)));
        Step();
        Assert.Equal(2, _runner.Calls.Count); // no new claims in Armed
        Assert.Equal(1, _store.GetCounts().Done);
    }

    [Fact]
    public void Stopped_never_idle_runs()
    {
        Files(1);
        _c.Stop();
        _idle.IdleTime = TimeSpan.FromHours(5);
        Step();
        Assert.Equal(RunState.Stopped, _c.State);
        Assert.Empty(_runner.Calls);
    }

    [Theory]
    [InlineData("pause", RunState.Armed)]
    [InlineData("stop", RunState.Stopped)]
    public void Pause_and_stop_let_running_exports_finish(string button, RunState expected)
    {
        Files(4);
        _c.Start();
        Step();
        if (button == "pause") _c.Pause(); else _c.Stop();
        Step();
        Assert.Equal(expected, _c.State);
        Assert.All(_runner.Calls, c => Assert.False(c.Ct.IsCancellationRequested));
        _runner.Finish(0, ExportOutcome.Ok(TimeSpan.Zero));
        _runner.Finish(1, ExportOutcome.Ok(TimeSpan.Zero));
        Step();
        Assert.Equal(2, _runner.Calls.Count);
        Assert.Equal(2, _store.GetCounts().Done);
    }

    [Fact]
    public void Pause_keeps_idle_auto_run_armed()
    {
        Files(1);
        _c.Start();
        _c.Pause();
        _idle.IdleTime = TimeSpan.FromMinutes(61);
        Step();
        Assert.Equal(RunState.IdleRunning, _c.State);
    }

    [Fact]
    public void Failures_retry_then_give_up_and_cancel_counts_nothing()
    {
        Files(1);
        _settings.Workers = 1;
        _c.Start();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Step(attempt == 1 ? null : TimeSpan.FromMinutes(_settings.RescanMinutes));
            Assert.Equal(attempt, _runner.Calls.Count);
            _runner.Finish(attempt - 1, ExportOutcome.Fail("boom", TimeSpan.Zero));
            Step();
            Assert.Equal(attempt, _runner.Calls.Count); // not retried back to back
        }
        var row = _store.Get(@"T:\p0.apj")!;
        Assert.Equal(FileStatus.Failed, row.Status);
        Assert.Equal(3, row.Attempts);
        Step();
        Assert.Equal(3, _runner.Calls.Count);

        _store.Retry(@"T:\p0.apj");
        Step();
        Assert.Equal(4, _runner.Calls.Count);
        _runner.Finish(3, ExportOutcome.Cancel(TimeSpan.Zero));
        row = _store.Get(@"T:\p0.apj")!;
        Assert.Equal(FileStatus.Pending, row.Status);
        Assert.Equal(0, row.Attempts);
    }

    [Fact]
    public void Engine_exception_counts_as_a_failure()
    {
        Files(1);
        _settings.Workers = 1;
        _c.Start();
        Step();
        _runner.Calls[0].Done.SetException(new InvalidOperationException("kaboom"));
        var row = _store.Get(@"T:\p0.apj")!;
        Assert.Equal(1, row.Attempts);
        Assert.Contains("kaboom", row.LastError);
    }

    [Fact]
    public void Scans_run_on_entry_then_hourly_and_never_while_stopped_or_armed()
    {
        Files(0);
        Step();
        Assert.Equal(0, _scanner.Runs);
        _c.Start();
        Step();
        Assert.Equal(1, _scanner.Runs);
        Step(TimeSpan.FromMinutes(59));
        Assert.Equal(1, _scanner.Runs);
        Step(TimeSpan.FromMinutes(1));
        Assert.Equal(2, _scanner.Runs);
        _c.Pause();
        Step(TimeSpan.FromHours(3));
        Assert.Equal(2, _scanner.Runs);
    }

    [Fact]
    public void Failed_scan_waits_for_the_interval_and_keeps_rows()
    {
        Files(2);
        _c.Start();
        Step();
        Assert.Equal(2, _store.GetCounts().Total);
        _scanner.Fail = true;
        Step(TimeSpan.FromMinutes(60));
        Assert.Equal(2, _scanner.Runs);
        Step();
        Assert.Equal(2, _scanner.Runs);
        Assert.Equal(2, _store.GetCounts().Total); // an incomplete scan that saw nothing deletes nothing
    }

    [Fact]
    public void Scanner_exception_is_logged_and_waits_for_the_interval()
    {
        Files(1);
        _scanner.Throw = true;
        _c.Start();
        Step();
        Step();
        Assert.Equal(1, _scanner.Runs);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR") && l.Contains("scanner crashed"));
    }

    [Fact]
    public void Deferred_counts_no_attempt_and_rests_the_slot()
    {
        Files(3);
        _settings.Workers = 1;
        _c.Start();
        Step();
        _runner.Finish(0, ExportOutcome.Defer("share unreachable", TimeSpan.Zero));
        var row = _store.Get(@"T:\p2.apj")!;
        Assert.Equal(FileStatus.Pending, row.Status);
        Assert.Equal(0, row.Attempts);

        Step();
        Assert.Single(_runner.Calls); // slot resting
        Step(RunController.SlotCooldown);
        Assert.Equal(2, _runner.Calls.Count);
        Assert.Equal(@"T:\p1.apj", _runner.Calls[1].Path); // p2 waits out its retry time
    }

    [Fact]
    public void First_scan_is_scheduled_from_the_last_completed_scan()
    {
        Files(1);
        _c.Start();
        Step();
        Assert.Equal(1, _scanner.Runs);
        // An incomplete scan recorded after it (e.g. cut short by exit) must not delay a relaunch's scan.
        var cut = _store.BeginScan(_clock.UtcNow.AddMinutes(30));
        _store.FinishScan(cut, _clock.UtcNow.AddMinutes(30), 0, 0, Array.Empty<string>(), completed: false);

        _clock.Advance(TimeSpan.FromMinutes(61));
        var relaunched = new RunController(_store, _runner, _scanner, _idle, _clock, _log, () => _settings);
        relaunched.Start();
        relaunched.Tick();
        Assert.Equal(2, _scanner.Runs);
    }

    [Fact]
    public async Task Shutdown_cancels_running_exports_back_to_pending()
    {
        Files(3);
        _c.Start();
        Step();
        await _c.ShutdownAsync(TimeSpan.FromSeconds(5));
        Assert.All(_runner.Calls, c => Assert.True(c.Ct.IsCancellationRequested));
        Assert.Equal(0, _store.GetCounts().Exporting);
        Assert.Equal(3, _store.GetCounts().Pending);
        Assert.All(new[] { @"T:\p2.apj", @"T:\p1.apj" }, p => Assert.Equal(0, _store.Get(p)!.Attempts));
    }

    [Fact]
    public void Snapshot_shows_workers_and_counts()
    {
        Files(3);
        _c.Start();
        Step();
        var snap = _c.Snapshot();
        Assert.Equal(RunState.Running, snap.State);
        Assert.Equal(2, snap.Workers.Count);
        Assert.All(snap.Workers, w => Assert.NotNull(w.ApjPath));
        Assert.Equal(1, snap.Counts.Pending);
        Assert.Equal(2, snap.Counts.Exporting);
        Assert.NotNull(snap.LastScanFinishedUtc);
    }

    [Fact]
    public void Update_hold_starts_nothing_new_and_drains_the_running_exports()
    {
        Files(5);
        _c.Start();
        Step();
        Assert.Equal(2, _runner.Calls.Count);

        _c.UpdateHold = true;
        _runner.Finish(0, ExportOutcome.Ok(TimeSpan.FromSeconds(1)));
        Step();
        Assert.Equal(2, _runner.Calls.Count); // the free slot stays free
        Assert.False(_c.IsDrained);            // one export is still running, so the update must wait

        _runner.Finish(1, ExportOutcome.Ok(TimeSpan.FromSeconds(1)));
        Step();
        Assert.True(_c.IsDrained);
        Assert.Equal(2, _runner.Calls.Count);
        Assert.True(_c.Snapshot().UpdateHold);

        Step(TimeSpan.FromMinutes(_settings.RescanMinutes + 1));
        Assert.Equal(1, _scanner.Runs); // no scan starts during the hold either
    }

    [Fact]
    public void Update_hold_leaves_the_users_state_alone_and_lifting_it_resumes()
    {
        Files(5);
        _c.UpdateHold = true;
        _c.Start();
        Step();
        Assert.Equal(RunState.Running, _c.State);
        Assert.Empty(_runner.Calls);
        _c.Pause();
        Assert.Equal(RunState.Armed, _c.State);
        _c.Stop();
        Assert.Equal(RunState.Stopped, _c.State);

        _c.Start();
        _c.UpdateHold = false;
        Step();
        Assert.Equal(2, _runner.Calls.Count);
    }

    [Fact]
    public void Drained_means_no_export_and_no_scan()
    {
        Assert.True(_c.IsDrained);
        Files(1);
        _c.Start();
        Step();
        Assert.False(_c.IsDrained);
        _runner.Finish(0, ExportOutcome.Ok(TimeSpan.FromSeconds(1)));
        Assert.True(_c.IsDrained);
    }

    [Theory]
    [InlineData(RunState.Running, RunState.Running)]
    [InlineData(RunState.Stopped, RunState.Stopped)]
    [InlineData(RunState.IdleRunning, RunState.Armed)]
    public void An_updated_app_starts_in_the_state_it_was_given(RunState given, RunState expected)
    {
        var c = new RunController(_store, _runner, _scanner, _idle, _clock, _log, () => _settings, given);
        Assert.Equal(expected, c.State);
    }

    [Fact]
    public void An_updated_app_started_running_exports_straight_away()
    {
        Files(3);
        var c = new RunController(_store, _runner, _scanner, _idle, _clock, _log, () => _settings, RunState.Running);
        _clock.Advance(RunController.IdlePoll);
        c.Tick();
        Assert.Equal(2, _runner.Calls.Count);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }
}
