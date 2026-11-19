namespace AvawareExportBot.Core;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>Real keyboard/mouse activity of the logged-in user.</summary>
public interface IIdleSource
{
    /// <summary>Time since the last real keyboard or mouse input.</summary>
    TimeSpan IdleTime { get; }

    /// <summary>A marker that changes whenever there is new input (GetLastInputInfo's tick on Windows).</summary>
    long LastInputMarker { get; }
}

public enum ExportOutcomeKind { Success, Failed, Cancelled }

public sealed record ExportOutcome(ExportOutcomeKind Kind, string? Error, TimeSpan Duration, string? ScreenshotPath = null)
{
    public static ExportOutcome Ok(TimeSpan duration) => new(ExportOutcomeKind.Success, null, duration);
    public static ExportOutcome Fail(string error, TimeSpan duration, string? screenshot = null) => new(ExportOutcomeKind.Failed, error, duration, screenshot);
    public static ExportOutcome Cancel(TimeSpan duration) => new(ExportOutcomeKind.Cancelled, "cancelled", duration);
}

/// <summary>Runs one export on a given worker slot. Each slot runs at most one export at a time.</summary>
public interface IExportRunner
{
    Task<ExportOutcome> RunAsync(int worker, string apjPath, IProgress<string> step, CancellationToken ct);
}

/// <summary>Runs one scan of the root.</summary>
public interface IScanRunner
{
    Task<ScanResult> RunAsync(IProgress<ScanProgress> progress, CancellationToken ct);
}
