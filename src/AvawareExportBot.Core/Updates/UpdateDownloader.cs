using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AvawareExportBot.Core.Updates;

/// <summary>A downloaded, checksum-verified exe waiting to be swapped in.</summary>
public sealed record StagedUpdate(Version Version, string Path);

/// <summary>A download that must not be staged: a bad checksum file, a hash mismatch, a short or failed download.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(string message) : base(message) { }
}

/// <summary>
/// Downloads a release's exe into the updates folder and stages it only when its SHA-256 matches the release's
/// checksum file. A staged file keeps its expected hash beside it (<c>.sha256</c>), so it can be checked again later
/// without the network. Only the newest staged update is kept.
/// </summary>
public sealed class UpdateDownloader
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    private const string Prefix = "AvawareExportBot-";
    private static readonly Regex Sha = new(@"^\s*([0-9a-fA-F]{64})(\s|$)", RegexOptions.CultureInvariant);
    private static readonly Regex StagedName = new(@"^AvawareExportBot-([0-9]+\.[0-9]+\.[0-9]+)\.exe$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private readonly string _dir;

    public UpdateDownloader(HttpClient http, string updatesDir)
    {
        _http = http;
        _dir = updatesDir;
    }

    public static string FileName(Version v) => $"{Prefix}{AppVersion.Format(v)}.exe";

    /// <summary>The hash in a checksum file: its first 64 hex characters (sha256sum's format), lowercased; null otherwise.</summary>
    public static string? ParseSha(string? text)
    {
        if (text is null) return null;
        var m = Sha.Match(text);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    public static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Downloads and verifies the release's exe, or reuses a staged copy of the same version that still verifies.</summary>
    /// <exception cref="UpdateException">The checksum file is unreadable or the download does not match it.</exception>
    public async Task<StagedUpdate> StageAsync(ReleaseInfo release, CancellationToken ct)
    {
        Directory.CreateDirectory(_dir);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var expected = ParseSha(await GetTextAsync(release.ShaUrl, timeout.Token).ConfigureAwait(false))
                ?? throw new UpdateException($"The checksum file of {release.Tag} is not a SHA-256.");

            var target = Path.Combine(_dir, FileName(release.Version));
            if (File.Exists(target) && HashOf(target) == expected)
            {
                await File.WriteAllTextAsync(target + ".sha256", expected, CancellationToken.None).ConfigureAwait(false);
                Prune(release.Version);
                return new StagedUpdate(release.Version, target);
            }

            var partial = target + ".partial";
            try
            {
                using (var response = await _http.GetAsync(release.ExeUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new UpdateException($"Downloading {release.Tag} failed: HTTP {(int)response.StatusCode}.");
                    await using var file = File.Create(partial);
                    await response.Content.CopyToAsync(file, timeout.Token).ConfigureAwait(false);
                }
                var actual = HashOf(partial);
                if (actual != expected)
                    throw new UpdateException($"The download of {release.Tag} does not match its checksum; it was discarded.");
                File.Move(partial, target, overwrite: true);
                await File.WriteAllTextAsync(target + ".sha256", expected, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(partial);
            }
            Prune(release.Version);
            return new StagedUpdate(release.Version, target);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException($"Downloading {release.Tag} timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"Downloading {release.Tag} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The newest staged update that is newer than <paramref name="current"/>, is not the failed version, and still
    /// matches its saved hash. Everything else in the folder (older, failed, broken or partial files) is deleted.
    /// </summary>
    public StagedUpdate? FindStaged(Version current, Version? failed)
    {
        if (!Directory.Exists(_dir)) return null;
        StagedUpdate? best = null;
        foreach (var path in Directory.EnumerateFiles(_dir, Prefix + "*.exe"))
        {
            var m = StagedName.Match(Path.GetFileName(path));
            var version = m.Success ? AppVersion.TryParse(m.Groups[1].Value) : null;
            if (version is null || version <= current || version == failed || !Verifies(path)) continue;
            if (best is null || version > best.Version) best = new StagedUpdate(version, path);
        }
        if (best is null) DeleteAll();
        else Prune(best.Version);
        return best;
    }

    /// <summary>Deletes every staged, partial and checksum file except those of <paramref name="keep"/>.</summary>
    public void Prune(Version keep)
    {
        if (!Directory.Exists(_dir)) return;
        var keepExe = FileName(keep);
        foreach (var path in Directory.EnumerateFiles(_dir, Prefix + "*"))
        {
            var name = Path.GetFileName(path);
            if (name.Equals(keepExe, StringComparison.OrdinalIgnoreCase) || name.Equals(keepExe + ".sha256", StringComparison.OrdinalIgnoreCase)) continue;
            TryDelete(path);
        }
    }

    public void DeleteAll()
    {
        if (!Directory.Exists(_dir)) return;
        foreach (var path in Directory.EnumerateFiles(_dir, Prefix + "*")) TryDelete(path);
    }

    private static bool Verifies(string exe)
    {
        try
        {
            var sidecar = exe + ".sha256";
            return File.Exists(sidecar) && ParseSha(File.ReadAllText(sidecar)) is { } expected && HashOf(exe) == expected;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new UpdateException($"Downloading the checksum file failed: HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
