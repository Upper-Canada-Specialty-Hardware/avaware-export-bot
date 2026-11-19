using System.Diagnostics;

namespace AvawareExportBot.Core;

public sealed record ScanProgress(int DirectoriesSeen, int ApjFound, int NeedingExport);

public sealed record ScanResult(long ScanId, int ApjFound, int NeedingExport, int UnreadableDirs, bool Completed, DateTime FinishedUtc);

/// <summary>
/// Walks the root for .apj files and records them in the state store. A .apj needs an export when no file named
/// &lt;same base name&gt;.mdb is in the same folder (names compared case-insensitively, as Windows does).
/// The walk is done one directory at a time so an unreadable directory is logged and skipped instead of ending the scan,
/// and directory links (reparse points) are not followed, so a link loop cannot trap it.
/// </summary>
public sealed class Scanner
{
    public const int BatchSize = 500;
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(2);

    private static readonly EnumerationOptions OneLevel = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    private readonly StateStore _store;
    private readonly ILog _log;
    private readonly Func<DateTime> _utcNow;

    public Scanner(StateStore store, ILog log, Func<DateTime>? utcNow = null)
    {
        _store = store;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public ScanResult Run(string root, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var scanId = _store.BeginScan(_utcNow());
        _log.Info($"Scan {scanId} started: {root}");
        var watch = Stopwatch.StartNew();
        int dirs = 0, found = 0, needing = 0;
        var unreadable = new List<string>();
        var batch = new List<ScannedApj>(BatchSize);
        var lastReport = Stopwatch.StartNew();
        var completed = false;

        void Flush()
        {
            _store.UpsertSeen(scanId, batch, _utcNow());
            batch.Clear();
        }

        try
        {
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException($"Root folder not found: {root}");

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = pending.Pop();
                dirs++;

                List<FileInfo> files;
                List<DirectoryInfo> subdirs;
                try
                {
                    var info = new DirectoryInfo(dir);
                    files = info.EnumerateFiles("*", OneLevel).ToList();
                    subdirs = info.EnumerateDirectories("*", OneLevel).ToList();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    // The root itself unreadable: nothing was seen, so the scan cannot count as complete.
                    if (dirs == 1) throw new IOException($"cannot read the root {dir}: {ex.Message}", ex);
                    unreadable.Add(dir);
                    _log.Warn($"Scan {scanId}: cannot read {dir}: {ex.Message}");
                    continue;
                }

                var names = new HashSet<string>(files.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    if (!string.Equals(file.Extension, ".apj", StringComparison.OrdinalIgnoreCase)) continue;
                    var hasMdb = names.Contains(Path.GetFileNameWithoutExtension(file.Name) + ".mdb");
                    found++;
                    if (!hasMdb) needing++;
                    batch.Add(new ScannedApj(file.FullName, file.Length, file.LastWriteTimeUtc, hasMdb));
                    if (batch.Count >= BatchSize) Flush();
                }

                foreach (var sub in subdirs)
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        _log.Info($"Scan {scanId}: not following folder link {sub.FullName}");
                        continue;
                    }
                    pending.Push(sub.FullName);
                }

                if (lastReport.Elapsed >= ProgressInterval)
                {
                    progress?.Report(new ScanProgress(dirs, found, needing));
                    lastReport.Restart();
                }
            }

            Flush();
            completed = true;
        }
        catch (OperationCanceledException)
        {
            _log.Info($"Scan {scanId} cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.Error($"Scan {scanId} failed: {ex.Message}");
        }
        finally
        {
            if (!completed && batch.Count > 0)
            {
                // Keep what was seen so far; an incomplete scan never deletes rows.
                try { Flush(); } catch (Exception ex) { _log.Error($"Scan {scanId}: could not save partial results: {ex.Message}"); }
            }
        }

        var finished = _utcNow();
        _store.FinishScan(scanId, finished, found, needing, unreadable, completed);
        progress?.Report(new ScanProgress(dirs, found, needing));
        _log.Info($"Scan {scanId} {(completed ? "finished" : "ended early")} in {watch.Elapsed:hh\\:mm\\:ss}: " +
                  $"{found} .apj, {needing} need a .mdb, {unreadable.Count} unreadable folders.");
        return new ScanResult(scanId, found, needing, unreadable.Count, completed, finished);
    }
}
