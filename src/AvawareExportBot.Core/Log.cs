using System.Globalization;

namespace AvawareExportBot.Core;

public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

/// <summary>
/// Daily log files (yyyy-MM-dd.log) kept for 30 days, plus an in-memory tail of the last 1,000 lines for the UI.
/// Thread-safe.
/// </summary>
public sealed class FileLog : ILog
{
    public const int TailSize = 1000;
    public const int KeepDays = 30;

    private readonly string _dir;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private readonly LinkedList<string> _tail = new();
    private long _sequence;

    public FileLog(string dir, Func<DateTime>? now = null)
    {
        _dir = dir;
        _now = now ?? (() => DateTime.Now);
        Directory.CreateDirectory(dir);
        Prune();
    }

    /// <summary>Raised after each line is written, on the writing thread.</summary>
    public event Action<string>? LineWritten;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    /// <summary>The last lines, oldest first, and a sequence number that grows with every line.</summary>
    public (IReadOnlyList<string> Lines, long Sequence) Tail()
    {
        lock (_gate) return (_tail.ToList(), _sequence);
    }

    private void Write(string level, string message)
    {
        var now = _now();
        var line = $"{now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {level,-5} {message}";
        lock (_gate)
        {
            try
            {
                File.AppendAllText(Path.Combine(_dir, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log"), line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the bot down; the tail still has the line.
            }
            _tail.AddLast(line);
            while (_tail.Count > TailSize) _tail.RemoveFirst();
            _sequence++;
        }
        LineWritten?.Invoke(line);
    }

    private void Prune()
    {
        var cutoff = _now().Date.AddDays(-KeepDays);
        foreach (var file in Directory.EnumerateFiles(_dir, "*.log"))
        {
            if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < cutoff)
                TryDelete(file);
        }
    }

    /// <summary>Deletes files in a folder last written more than <see cref="KeepDays"/> days ago (failure screenshots).</summary>
    public static void PruneOlderThanKeepDays(string dir, DateTime now)
    {
        if (!Directory.Exists(dir)) return;
        var cutoff = now.AddDays(-KeepDays);
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff) TryDelete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
