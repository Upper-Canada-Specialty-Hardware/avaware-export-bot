using System.Text.Json;

namespace AvawareExportBot.Core;

/// <summary>User settings, stored as JSON in the data folder. Defaults follow the product decisions ruling.</summary>
public sealed class Settings
{
    public const string DefaultRoot = @"T:\";
    public const string DefaultAvaprojectExe = @"C:\Program Files (x86)\AVAware\AVAproject\AVAproject.exe";

    public string RootFolder { get; set; } = DefaultRoot;
    public string AvaprojectExe { get; set; } = DefaultAvaprojectExe;
    public int Workers { get; set; } = DefaultWorkers;
    public int IdleMinutes { get; set; } = 60;
    public int RescanMinutes { get; set; } = 60;
    public int MaxAttempts { get; set; } = 3;

    /// <summary>One worker per logical core minus one, at least one.</summary>
    public static int DefaultWorkers => Math.Max(1, Environment.ProcessorCount - 1);

    public static int MaxWorkers => Math.Max(1, Environment.ProcessorCount);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public Settings Clone() => (Settings)MemberwiseClone();

    /// <summary>Returns the problems with these settings; empty when valid.</summary>
    public IReadOnlyList<string> Validate(Func<string, bool> directoryExists, Func<string, bool> fileExists)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(RootFolder) || !directoryExists(RootFolder))
            problems.Add("Root folder does not exist.");
        if (string.IsNullOrWhiteSpace(AvaprojectExe) || !fileExists(AvaprojectExe))
            problems.Add("AVAproject.exe was not found at that path.");
        if (Workers < 1 || Workers > MaxWorkers)
            problems.Add($"Workers must be between 1 and {MaxWorkers}.");
        if (IdleMinutes < 1 || IdleMinutes > 24 * 60)
            problems.Add("Idle minutes must be between 1 and 1440.");
        if (RescanMinutes < 1 || RescanMinutes > 7 * 24 * 60)
            problems.Add("Rescan minutes must be between 1 and 10080.");
        if (MaxAttempts < 1 || MaxAttempts > 100)
            problems.Add("Max attempts must be between 1 and 100.");
        return problems;
    }

    /// <summary>Loads settings, or the defaults when the file is missing or unreadable.</summary>
    public static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return (JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings()).Normalized();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall back to defaults; a broken settings file must not stop the app.
        }
        return new Settings();
    }

    /// <summary>A hand-edited file can hold anything: numbers are clamped into range, blank paths get their defaults.</summary>
    public Settings Normalized()
    {
        var s = Clone();
        if (string.IsNullOrWhiteSpace(s.RootFolder)) s.RootFolder = DefaultRoot;
        if (string.IsNullOrWhiteSpace(s.AvaprojectExe)) s.AvaprojectExe = DefaultAvaprojectExe;
        s.Workers = Math.Clamp(s.Workers, 1, MaxWorkers);
        s.IdleMinutes = Math.Clamp(s.IdleMinutes, 1, 24 * 60);
        s.RescanMinutes = Math.Clamp(s.RescanMinutes, 1, 7 * 24 * 60);
        s.MaxAttempts = Math.Clamp(s.MaxAttempts, 1, 100);
        return s;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
