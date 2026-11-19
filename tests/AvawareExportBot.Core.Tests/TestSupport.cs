using AvawareExportBot.Core;

namespace AvawareExportBot.Core.Tests;

/// <summary>A temp folder with a state db, deleted after the test.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aeb-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    /// <summary>Creates a file (and its folders) with some bytes and a given last write time.</summary>
    public string File(string relative, DateTime? modifiedUtc = null, int bytes = 10)
    {
        var full = Combine(relative.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, new byte[bytes]);
        if (modifiedUtc is not null) System.IO.File.SetLastWriteTimeUtc(full, modifiedUtc.Value);
        return full;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class ListLog : ILog
{
    private readonly List<string> _lines = new();
    public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToList(); } }
    public void Info(string message) { lock (_lines) _lines.Add("INFO " + message); }
    public void Warn(string message) { lock (_lines) _lines.Add("WARN " + message); }
    public void Error(string message) { lock (_lines) _lines.Add("ERROR " + message); }
}

public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    public void Advance(TimeSpan by) => UtcNow += by;
}

public sealed class FakeIdle : IIdleSource
{
    public TimeSpan IdleTime { get; set; }
    public long LastInputMarker { get; set; }

    /// <summary>Simulates the user touching the mouse or keyboard.</summary>
    public void Input()
    {
        LastInputMarker++;
        IdleTime = TimeSpan.Zero;
    }
}

/// <summary>An export runner whose exports finish only when the test says so.</summary>
public sealed class FakeRunner : IExportRunner
{
    private readonly object _gate = new();
    public List<(int Worker, string Path, TaskCompletionSource<ExportOutcome> Done, CancellationToken Ct)> Calls { get; } = new();
    public int MaxConcurrent { get; private set; }
    private int _running;

    public Task<ExportOutcome> RunAsync(int worker, string apjPath, IProgress<string> step, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ExportOutcome>();
        lock (_gate)
        {
            Calls.Add((worker, apjPath, tcs, ct));
            _running++;
            MaxConcurrent = Math.Max(MaxConcurrent, _running);
        }
        ct.Register(() => tcs.TrySetResult(ExportOutcome.Cancel(TimeSpan.Zero)));
        return tcs.Task.ContinueWith(t => { lock (_gate) _running--; return t.Result; }, TaskContinuationOptions.ExecuteSynchronously);
    }

    public int Running { get { lock (_gate) return _running; } }

    public void Finish(int call, ExportOutcome outcome) => Calls[call].Done.SetResult(outcome);
}

/// <summary>A scan runner that records a finished scan in the store, optionally adding .apj rows first.</summary>
public sealed class FakeScanner : IScanRunner
{
    private readonly StateStore _store;
    private readonly FakeClock _clock;
    public int Runs { get; private set; }
    public List<ScannedApj> NextResult { get; set; } = new();
    public bool Fail { get; set; }

    public FakeScanner(StateStore store, FakeClock clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<ScanResult> RunAsync(IProgress<ScanProgress> progress, CancellationToken ct)
    {
        Runs++;
        if (Fail) return Task.FromException<ScanResult>(new IOException("share unavailable"));
        var id = _store.BeginScan(_clock.UtcNow);
        _store.UpsertSeen(id, NextResult, _clock.UtcNow);
        _store.FinishScan(id, _clock.UtcNow, NextResult.Count, NextResult.Count(r => !r.HasMdb), 0, completed: true);
        return Task.FromResult(new ScanResult(id, NextResult.Count, NextResult.Count(r => !r.HasMdb), 0, true, _clock.UtcNow));
    }
}
