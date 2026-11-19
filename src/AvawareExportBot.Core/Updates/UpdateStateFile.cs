using System.Text.Json;

namespace AvawareExportBot.Core.Updates;

/// <summary>
/// <c>update.json</c> in the data folder: the version that failed to start after a swap and was rolled back.
/// The checker never offers that version again; a newer release replaces it.
/// </summary>
public static class UpdateStateFile
{
    private sealed class Data
    {
        public string? FailedVersion { get; set; }
    }

    public static Version? LoadFailed(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return AppVersion.TryParse(JsonSerializer.Deserialize<Data>(File.ReadAllText(path))?.FailedVersion);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void SaveFailed(string path, Version failed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Data { FailedVersion = AppVersion.Format(failed) }));
        File.Move(tmp, path, overwrite: true);
    }
}
