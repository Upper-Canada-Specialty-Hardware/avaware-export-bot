using System.Collections.Concurrent;
using System.Diagnostics;
using AvawareExportBot.Core;

namespace AvawareExportBot.Automation;

/// <summary>
/// Runs exports for the run controller. Worker n owns one long-lived thread attached to its own hidden desktop
/// "avaware-export-bot-n", its own handle to the user's desktop for the leak guard, and its own work folder, so
/// workers share nothing and can run in parallel. A worker whose desktop could not be made is dropped and made again
/// for its next job.
/// </summary>
public sealed class WorkerPool : IExportRunner, IDisposable
{
    private readonly AppPaths _paths;
    private readonly Func<Settings> _settings;
    private readonly ConcurrentDictionary<int, Worker> _workers = new();

    public WorkerPool(AppPaths paths, Func<Settings> settings)
    {
        _paths = paths;
        _settings = settings;
    }

    public Task<ExportOutcome> RunAsync(int worker, string apjPath, IProgress<string> step, CancellationToken ct)
    {
        var job = new Job(apjPath, Path.ChangeExtension(apjPath, ".mdb"), _settings().AvaprojectExe, step, ct);
        while (true)
        {
            var w = _workers.GetOrAdd(worker, i => new Worker(i, _paths));
            if (w.TryEnqueue(job)) return job.Done.Task;
            _workers.TryRemove(new KeyValuePair<int, Worker>(worker, w)); // dead: make a new one
        }
    }

    public void Dispose()
    {
        foreach (var w in _workers.Values) w.Dispose();
    }

    private sealed record Job(string ApjPath, string TargetMdb, string ExePath, IProgress<string> Step, CancellationToken Ct)
    {
        public TaskCompletionSource<ExportOutcome> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Worker : IDisposable
    {
        private readonly BlockingCollection<Job> _queue = new();
        private readonly Thread _thread;
        private readonly int _index;
        private readonly AppPaths _paths;
        private volatile bool _dead;

        public Worker(int index, AppPaths paths)
        {
            _index = index;
            _paths = paths;
            _thread = new Thread(Loop) { IsBackground = true, Name = $"export-worker-{index}" };
            _thread.Start();
        }

        /// <summary>False when this worker is dead (its desktop failed) or closing; the caller makes a new one.</summary>
        public bool TryEnqueue(Job job)
        {
            if (_dead) return false;
            try { return _queue.TryAdd(job); }
            catch (InvalidOperationException) { return false; } // adding completed
        }

        private void Loop()
        {
            HiddenDesktop? desktop = null;
            IntPtr userDesktop = IntPtr.Zero;
            try
            {
                desktop = new HiddenDesktop($"avaware-export-bot-{_index}");
                desktop.AttachCurrentThread();
                userDesktop = AvaprojectExporter.OpenUserDesktop();
                if (userDesktop == IntPtr.Zero)
                    throw new InvalidOperationException("cannot open the user's desktop for the leak guard");
            }
            catch (Exception ex)
            {
                // Not the files' fault: defer whatever is queued, and let the pool make a new worker next time.
                _dead = true;
                _queue.CompleteAdding();
                desktop?.Dispose();
                AvaprojectExporter.CloseUserDesktop(userDesktop);
                foreach (var job in _queue.GetConsumingEnumerable())
                    job.Done.TrySetResult(ExportOutcome.Defer($"worker {_index}: {ex.Message}", TimeSpan.Zero));
                return;
            }

            try
            {
                foreach (var job in _queue.GetConsumingEnumerable())
                {
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        if (job.Ct.IsCancellationRequested)
                        {
                            job.Done.TrySetResult(ExportOutcome.Cancel(TimeSpan.Zero));
                            continue;
                        }
                        var exporter = new AvaprojectExporter(desktop, userDesktop, job.ExePath,
                            Path.Combine(_paths.WorkDir, _index.ToString(System.Globalization.CultureInfo.InvariantCulture)), _paths.SnapsDir);
                        job.Done.TrySetResult(exporter.Export(job.ApjPath, job.TargetMdb, job.Step, job.Ct));
                    }
                    catch (Exception ex)
                    {
                        job.Done.TrySetResult(ExportOutcome.Fail("engine error: " + ex.Message, watch.Elapsed));
                    }
                }
            }
            finally
            {
                AvaprojectExporter.CloseUserDesktop(userDesktop);
                desktop.Dispose();
            }
        }

        public void Dispose()
        {
            try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { }
            // A worker still exporting keeps reading the queue; never dispose it under the thread. The process exit and
            // the kill-on-close job end AVAproject either way.
            if (_thread.Join(TimeSpan.FromSeconds(5))) _queue.Dispose();
        }
    }
}
