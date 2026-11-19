namespace AvawareExportBot.Core;

/// <summary>
/// Where the bot keeps its own data. Everything lives under %LOCALAPPDATA%\AvawareExportBot,
/// which is never under the scanned root, so the bot never scans its own files.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string dataDir)
    {
        DataDir = dataDir;
    }

    public static AppPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvawareExportBot"));

    public string DataDir { get; }
    public string StateDb => Path.Combine(DataDir, "state.db");
    public string SettingsFile => Path.Combine(DataDir, "settings.json");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string SnapsDir => Path.Combine(LogsDir, "snaps");
    public string WorkDir => Path.Combine(DataDir, "work");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(SnapsDir);
        Directory.CreateDirectory(WorkDir);
    }
}
