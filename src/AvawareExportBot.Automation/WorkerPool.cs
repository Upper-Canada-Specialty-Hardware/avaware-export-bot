using System.Collections.Concurrent;
using System.Diagnostics;
using AvawareExportBot.Core;

namespace AvawareExportBot.Automation;

/// <summary>
/// Runs exports for the run controller. Worker n owns one long-lived thread attached to its own hidden desktop
/// "avaware-export-bot-n" and its own work folder, so workers share nothing and can run in parallel.
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
        var w = _workers.GetOrAdd(worker, i => new Worker(i, _paths));
        var job = new Job(apjPath, Path.ChangeExtension(apjPath, ".mdb"), _settings().AvaprojectExe, step, ct);
        w.Enqueue(job);
        return job.Done.Task;
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

        public Worker(int index, AppPaths paths)
        {
            _index = index;
            _paths = paths;
            _thread = new Thread(Loop) { IsBackground = true, Name = $"export-worker-{index}" };
            _thread.Start();
        }

        public void Enqueue(Job job) => _queue.Add(job);

        private void Loop()
        {
            HiddenDesktop? desktop = null;
            try
            {
                desktop = new HiddenDesktop($"avaware-export-bot-{_index}");
                desktop.AttachCurrentThread();
            }
            catch (Exception ex)
            {
                foreach (var job in _queue.GetConsumingEnumerable())
                    job.Done.TrySetResult(ExportOutcome.Fail("hidden desktop: " + ex.Message, TimeSpan.Zero));
                return;
            }

            using (desktop)
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
                        var exporter = new AvaprojectExporter(desktop, job.ExePath, Path.Combine(_paths.WorkDir, _index.ToString()), _paths.SnapsDir);
                        job.Done.TrySetResult(exporter.Export(job.ApjPath, job.TargetMdb, job.Step, job.Ct));
                    }
                    catch (Exception ex)
                    {
                        job.Done.TrySetResult(ExportOutcome.Fail("engine error: " + ex.Message, watch.Elapsed));
                    }
                }
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(30));
            _queue.Dispose();
        }
    }
}
