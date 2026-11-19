using System.Net;
using System.Text.Json;

namespace AvawareExportBot.Core.Updates;

/// <summary>A published release that carries both assets the updater needs.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string ExeUrl, string ShaUrl);

public enum CheckKind
{
    /// <summary>A newer usable release exists.</summary>
    Update,
    /// <summary>Nothing to do: up to date, no releases yet, or only a version that failed before.</summary>
    None,
    /// <summary>The check itself failed; try again at the next scheduled check.</summary>
    Error,
}

public sealed record CheckResult(CheckKind Kind, ReleaseInfo? Release, string Message);

/// <summary>
/// Asks GitHub for the repository's latest release. The repository is public, so no token is needed.
/// The latest-release endpoint never returns drafts or prereleases, so a prerelease is never installed.
/// </summary>
public sealed class UpdateChecker
{
    public const string LatestUrl = "https://api.github.com/repos/Upper-Canada-Specialty-Hardware/avaware-export-bot/releases/latest";
    public const string ExeAsset = "AvawareExportBot.exe";
    public const string ShaAsset = ExeAsset + ".sha256";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;

    /// <param name="http">Sends the request; it should carry the app's User-Agent (GitHub refuses requests without one).</param>
    public UpdateChecker(HttpClient http) => _http = http;

    /// <param name="current">The running app's version.</param>
    /// <param name="failed">A version that failed to start before; it is never offered again, but a newer one is.</param>
    public async Task<CheckResult> CheckAsync(Version current, Version? failed, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return None("No releases published yet.");
            if (!response.IsSuccessStatusCode) return Error($"Update check failed: HTTP {(int)response.StatusCode}.");
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return Read(json, current, failed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Error($"Update check timed out after {Timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException ex)
        {
            return Error("Update check failed: " + ex.Message);
        }
    }

    private static CheckResult Read(string json, Version current, Version? failed)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Error("Update check failed: GitHub's answer was not valid JSON.");
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Error("Update check failed: unexpected answer from GitHub.");
            var tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var version = AppVersion.ParseTag(tag);
            if (version is null) return Error($"The latest release has the tag '{tag}', which is not vX.Y.Z; it is ignored.");
            if (version <= current) return None($"Up to date ({AppVersion.Format(current)}).");
            if (failed is not null && version == failed)
                return None($"Version {AppVersion.Format(version)} failed to start before; waiting for a newer release.");

            string? exe = null, sha = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    var url = a.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    if (url is null) continue;
                    if (name == ExeAsset) exe = url;
                    else if (name == ShaAsset) sha = url;
                }
            }
            if (exe is null) return Error($"Release {tag} has no {ExeAsset} yet.");
            if (sha is null) return Error($"Release {tag} has no {ShaAsset} yet.");
            return new CheckResult(CheckKind.Update, new ReleaseInfo(version, tag!, exe, sha), $"Version {AppVersion.Format(version)} is available.");
        }
    }

    private static CheckResult None(string message) => new(CheckKind.None, null, message);
    private static CheckResult Error(string message) => new(CheckKind.Error, null, message);
}
