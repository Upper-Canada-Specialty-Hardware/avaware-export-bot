using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AvawareExportBot.Core.Updates;
using Xunit;

namespace AvawareExportBot.Core.Tests;

/// <summary>Answers requests from a table of URLs, and counts them. Anything not in the table is a 404.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, byte[] Body)> _routes = new();
    private readonly Dictionary<string, int> _hits = new();

    public void Set(string url, string body, HttpStatusCode status = HttpStatusCode.OK) => _routes[url] = (status, Encoding.UTF8.GetBytes(body));
    public void Set(string url, byte[] body) => _routes[url] = (HttpStatusCode.OK, body);
    public int Hits(string url) { lock (_hits) return _hits.GetValueOrDefault(url); }
    public bool Throw { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        lock (_hits) _hits[url] = _hits.GetValueOrDefault(url) + 1;
        if (Throw) throw new HttpRequestException("network down");
        var (status, body) = _routes.TryGetValue(url, out var r) ? r : (HttpStatusCode.NotFound, Array.Empty<byte>());
        return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
    }
}

public static class Releases
{
    public const string ExeUrl = "https://example.test/AvawareExportBot.exe";
    public const string ShaUrl = "https://example.test/AvawareExportBot.exe.sha256";

    public static string Json(string tag, bool exe = true, bool sha = true)
    {
        var assets = new List<object>();
        if (exe) assets.Add(new { name = UpdateChecker.ExeAsset, browser_download_url = ExeUrl });
        if (sha) assets.Add(new { name = UpdateChecker.ShaAsset, browser_download_url = ShaUrl });
        return JsonSerializer.Serialize(new { tag_name = tag, assets });
    }

    public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>A release with real assets: the exe bytes and a sha256sum-style checksum file.</summary>
    public static byte[] Publish(FakeHttp http, string tag, string content = "new exe")
    {
        var bytes = Encoding.UTF8.GetBytes(content + " " + tag);
        http.Set(UpdateChecker.LatestUrl, Json(tag));
        http.Set(ExeUrl, bytes);
        http.Set(ShaUrl, $"{Sha(bytes)}  AvawareExportBot.exe\n");
        return bytes;
    }
}

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("v0.10.0", "0.10.0")]
    [InlineData("1.2.3", null)]
    [InlineData("v1.2", null)]
    [InlineData("v1.2.3-rc1", null)]
    [InlineData("v1.2.3.4", null)]
    [InlineData("", null)]
    public void Tags_must_be_v_major_minor_patch(string tag, string? expected) =>
        Assert.Equal(expected, AppVersion.ParseTag(tag) is { } v ? AppVersion.Format(v) : null);

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+4f2a9c1d", "1.2.3")]
    [InlineData("0.0.0", "0.0.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("garbage", "0.0.0")]
    [InlineData(null, "0.0.0")]
    public void Informational_versions_ignore_a_build_suffix(string? informational, string expected) =>
        Assert.Equal(expected, AppVersion.Format(AppVersion.ParseInformational(informational)));

    [Fact]
    public void Versions_compare_by_number_not_text()
    {
        Assert.True(AppVersion.ParseTag("v0.10.0") > AppVersion.ParseTag("v0.9.9"));
        Assert.True(AppVersion.ParseInformational("1.2.3.0") == AppVersion.ParseTag("v1.2.3"));
        Assert.False(AppVersion.ParseTag("v1.2.3") > AppVersion.ParseInformational("1.2.3+abc"));
    }
}

public sealed class UpdateCheckerTests
{
    private readonly FakeHttp _http = new();
    private readonly UpdateChecker _checker;
    private static readonly Version Current = new(1, 2, 0);

    public UpdateCheckerTests() => _checker = new UpdateChecker(new HttpClient(_http));

    private CheckResult Check(Version? failed = null) => _checker.CheckAsync(Current, failed, CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void A_newer_release_with_both_assets_is_an_update()
    {
        _http.Set(UpdateChecker.LatestUrl, Releases.Json("v1.3.0"));
        var r = Check();
        Assert.Equal(CheckKind.Update, r.Kind);
        Assert.Equal(new Version(1, 3, 0), r.Release!.Version);
        Assert.Equal(Releases.ExeUrl, r.Release.ExeUrl);
        Assert.Equal(Releases.ShaUrl, r.Release.ShaUrl);
    }

    [Theory]
    [InlineData("v1.2.0")]
    [InlineData("v1.1.9")]
    public void The_same_or_an_older_release_is_nothing_to_do(string tag)
    {
        _http.Set(UpdateChecker.LatestUrl, Releases.Json(tag));
        Assert.Equal(CheckKind.None, Check().Kind);
    }

    [Fact]
    public void No_releases_yet_is_nothing_to_do()
    {
        Assert.Equal(CheckKind.None, Check().Kind); // FakeHttp answers 404
    }

    [Fact]
    public void A_server_error_is_an_error()
    {
        _http.Set(UpdateChecker.LatestUrl, "{}", HttpStatusCode.InternalServerError);
        Assert.Equal(CheckKind.Error, Check().Kind);
    }

    [Fact]
    public void A_network_failure_is_an_error()
    {
        _http.Throw = true;
        Assert.Equal(CheckKind.Error, Check().Kind);
    }

    [Fact]
    public void Bad_json_or_a_bad_tag_is_an_error()
    {
        _http.Set(UpdateChecker.LatestUrl, "not json");
        Assert.Equal(CheckKind.Error, Check().Kind);
        _http.Set(UpdateChecker.LatestUrl, Releases.Json("release-2"));
        Assert.Equal(CheckKind.Error, Check().Kind);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void A_release_missing_an_asset_is_an_error(bool exe, bool sha)
    {
        _http.Set(UpdateChecker.LatestUrl, Releases.Json("v1.3.0", exe, sha));
        Assert.Equal(CheckKind.Error, Check().Kind);
    }

    [Fact]
    public void A_version_that_failed_before_is_skipped_but_a_newer_one_is_offered()
    {
        _http.Set(UpdateChecker.LatestUrl, Releases.Json("v1.3.0"));
        Assert.Equal(CheckKind.None, Check(failed: new Version(1, 3, 0)).Kind);
        _http.Set(UpdateChecker.LatestUrl, Releases.Json("v1.3.1"));
        Assert.Equal(CheckKind.Update, Check(failed: new Version(1, 3, 0)).Kind);
    }
}

public sealed class UpdateDownloaderTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeHttp _http = new();
    private readonly UpdateDownloader _downloader;

    public UpdateDownloaderTests() => _downloader = new UpdateDownloader(new HttpClient(_http), _dir.Combine("updates"));

    public void Dispose() => _dir.Dispose();

    private static ReleaseInfo Release(string version) => new(AppVersion.TryParse(version)!, "v" + version, Releases.ExeUrl, Releases.ShaUrl);

    private StagedUpdate Stage(string version) => _downloader.StageAsync(Release(version), CancellationToken.None).GetAwaiter().GetResult();

    private string[] Files() => Directory.GetFiles(_dir.Combine("updates")).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

    [Fact]
    public void A_download_matching_its_checksum_is_staged()
    {
        var bytes = Releases.Publish(_http, "v1.3.0");
        var staged = Stage("1.3.0");
        Assert.Equal(bytes, File.ReadAllBytes(staged.Path));
        Assert.Equal(new[] { "AvawareExportBot-1.3.0.exe", "AvawareExportBot-1.3.0.exe.sha256" }, Files());
    }

    [Fact]
    public void A_download_not_matching_its_checksum_is_discarded()
    {
        Releases.Publish(_http, "v1.3.0");
        _http.Set(Releases.ShaUrl, new string('0', 64) + "  AvawareExportBot.exe");
        Assert.Throws<UpdateException>(() => Stage("1.3.0"));
        Assert.Empty(Files());
    }

    [Fact]
    public void A_bad_checksum_file_stages_nothing()
    {
        Releases.Publish(_http, "v1.3.0");
        _http.Set(Releases.ShaUrl, "not a hash");
        Assert.Throws<UpdateException>(() => Stage("1.3.0"));
        Assert.Equal(0, _http.Hits(Releases.ExeUrl));
    }

    [Fact]
    public void A_staged_copy_that_still_verifies_is_reused()
    {
        Releases.Publish(_http, "v1.3.0");
        Stage("1.3.0");
        Stage("1.3.0");
        Assert.Equal(1, _http.Hits(Releases.ExeUrl));
    }

    [Fact]
    public void Staging_a_newer_version_deletes_the_older_one()
    {
        Releases.Publish(_http, "v1.3.0");
        Stage("1.3.0");
        Releases.Publish(_http, "v1.4.0");
        Stage("1.4.0");
        Assert.Equal(new[] { "AvawareExportBot-1.4.0.exe", "AvawareExportBot-1.4.0.exe.sha256" }, Files());
    }

    [Fact]
    public void Find_staged_returns_a_newer_verified_copy()
    {
        Releases.Publish(_http, "v1.3.0");
        var staged = Stage("1.3.0");
        Assert.Equal(staged, _downloader.FindStaged(new Version(1, 2, 0), failed: null));
    }

    [Fact]
    public void Find_staged_deletes_copies_that_are_not_newer_failed_or_tampered()
    {
        Releases.Publish(_http, "v1.3.0");
        var staged = Stage("1.3.0");
        Assert.Null(_downloader.FindStaged(new Version(1, 3, 0), failed: null)); // already running it
        Assert.Empty(Files());

        Stage("1.3.0");
        Assert.Null(_downloader.FindStaged(new Version(1, 2, 0), failed: new Version(1, 3, 0)));
        Assert.Empty(Files());

        Stage("1.3.0");
        File.AppendAllText(staged.Path, "tampered");
        Assert.Null(_downloader.FindStaged(new Version(1, 2, 0), failed: null));
        Assert.Empty(Files());
    }

    [Theory]
    [InlineData("ABCDEF0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789  AvawareExportBot.exe", true)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", true)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    public void Checksum_files_are_read_in_sha256sum_format(string text, bool valid) =>
        Assert.Equal(valid, UpdateDownloader.ParseSha(text) is not null);
}

public sealed class UpdateServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeHttp _http = new();
    private readonly FakeClock _clock = new();
    private readonly ListLog _log = new();
    private readonly Settings _settings = new();

    public void Dispose() => _dir.Dispose();

    private UpdateService Service(bool releaseBuild = true)
    {
        var client = new HttpClient(_http);
        return new UpdateService(new UpdateChecker(client), new UpdateDownloader(client, _dir.Combine("updates")), _dir.Combine("update.json"),
            new Version(1, 2, 0), releaseBuild, _clock, _log, () => _settings);
    }

    private int Checks => _http.Hits(UpdateChecker.LatestUrl);

    [Fact]
    public void First_check_is_a_minute_after_startup_then_every_six_hours()
    {
        Releases.Publish(_http, "v1.2.0");
        var s = Service();
        Assert.Null(s.Tick());
        _clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(s.Tick());
        _clock.Advance(TimeSpan.FromSeconds(1));
        s.Tick()!.Wait();
        Assert.Equal(1, Checks);

        _clock.Advance(UpdateService.Interval - TimeSpan.FromSeconds(1));
        Assert.Null(s.Tick());
        _clock.Advance(TimeSpan.FromSeconds(1));
        s.Tick()!.Wait();
        Assert.Equal(2, Checks);
    }

    [Fact]
    public void A_newer_release_is_downloaded_and_staged()
    {
        Releases.Publish(_http, "v1.3.0");
        var s = Service();
        _clock.Advance(UpdateService.FirstCheck);
        var message = s.Tick()!.Result;
        Assert.Equal(new Version(1, 3, 0), s.Staged!.Version);
        Assert.Contains("1.3.0", message);
    }

    [Fact]
    public void Nothing_is_checked_while_auto_update_is_off_but_a_manual_check_still_works()
    {
        Releases.Publish(_http, "v1.3.0");
        _settings.AutoUpdate = false;
        var s = Service();
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(s.Tick());
        Assert.Equal(0, Checks);

        var steps = new List<string>();
        s.CheckNowAsync(steps.Add).Wait();
        Assert.Equal(1, Checks);
        Assert.Equal(new Version(1, 3, 0), s.Staged!.Version);
        Assert.Contains(steps, m => m.Contains("downloading"));
    }

    [Fact]
    public void A_dev_build_never_checks()
    {
        Releases.Publish(_http, "v1.3.0");
        var s = Service(releaseBuild: false);
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(s.Tick());
        Assert.Contains("dev build", s.CheckNowAsync().Result);
        Assert.Equal(0, Checks);
    }

    [Fact]
    public void A_failed_check_is_logged_and_waits_for_the_next_one()
    {
        _http.Throw = true;
        var s = Service();
        _clock.Advance(UpdateService.FirstCheck);
        s.Tick()!.Wait();
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN Update check failed"));
        Assert.Null(s.Tick());
        Assert.Null(s.Staged);
    }

    [Fact]
    public void A_recorded_failure_drops_the_staged_copy_and_survives_a_restart()
    {
        Releases.Publish(_http, "v1.3.0");
        var s = Service();
        s.CheckNowAsync().Wait();
        s.RecordFailed(new Version(1, 3, 0));
        Assert.Null(s.Staged);

        var again = Service();
        Assert.Equal(new Version(1, 3, 0), again.FailedVersion);
        Assert.Null(again.Staged);
        Assert.Contains("failed to start before", again.CheckNowAsync().Result);
    }

    [Fact]
    public void A_staged_update_is_found_again_after_a_restart()
    {
        Releases.Publish(_http, "v1.3.0");
        Service().CheckNowAsync().Wait();
        Assert.Equal(new Version(1, 3, 0), Service().Staged!.Version);
    }
}

public sealed class RestartArgsTests
{
    [Theory]
    [InlineData(RunState.Running, RunState.Running)]
    [InlineData(RunState.IdleRunning, RunState.Armed)]
    [InlineData(RunState.Armed, RunState.Armed)]
    [InlineData(RunState.Stopped, RunState.Stopped)]
    public void The_state_after_an_update_follows_the_ruling(RunState before, RunState after) =>
        Assert.Equal(after, RestartArgs.StartStateAfter(before));

    [Fact]
    public void Arguments_round_trip()
    {
        var args = new RestartArgs(RunState.Running, Tray: true, UpdatedFrom: new Version(1, 2, 0));
        Assert.Equal(args, RestartArgs.Parse(args.ToArgs()));
        var failed = new RestartArgs(RunState.Stopped, UpdateFailed: new Version(1, 3, 0));
        Assert.Equal(failed, RestartArgs.Parse(failed.ToArgs()));
    }

    [Fact]
    public void A_plain_launch_is_armed_with_the_window_shown()
    {
        Assert.Equal(new RestartArgs(), RestartArgs.Parse(Array.Empty<string>()));
        Assert.Equal(RunState.Armed, RestartArgs.Parse(new[] { "--state", "idlerunning" }).State);
    }
}

public sealed class UpdateSwapperTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ListLog _log = new();
    private readonly List<(string Exe, IReadOnlyList<string> Args)> _starts = new();
    private readonly List<Version> _failed = new();
    private readonly string _exe;
    private readonly StagedUpdate _staged;

    private sealed class FakeProcess : IStartedProcess
    {
        public bool HasExited { get; set; }
        public bool Killed { get; private set; }
        public void Kill() { Killed = true; HasExited = true; }
    }

    public UpdateSwapperTests()
    {
        _exe = _dir.Combine("install", "AvawareExportBot.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(_exe)!);
        File.WriteAllBytes(_exe, FakeExe("old exe"));
        var stagedPath = _dir.Combine("AvawareExportBot-1.3.0.exe");
        File.WriteAllBytes(stagedPath, FakeExe("new exe"));
        _staged = new StagedUpdate(new Version(1, 3, 0), stagedPath);
    }

    public void Dispose() => _dir.Dispose();

    /// <summary>The smallest file that passes the Windows program check (an MZ header pointing at a PE signature), then a marker.</summary>
    private static byte[] FakeExe(string marker)
    {
        var header = new byte[64];
        header[0] = (byte)'M';
        header[1] = (byte)'Z';
        BitConverter.GetBytes(64).CopyTo(header, 0x3C);
        return header.Concat(new byte[] { (byte)'P', (byte)'E', 0, 0 }).Concat(System.Text.Encoding.UTF8.GetBytes(marker)).ToArray();
    }

    private static string Marker(string path) => System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path).Skip(68).ToArray());

    private SwapResult Apply(bool ready, FakeProcess? process = null)
    {
        process ??= new FakeProcess();
        var swapper = new UpdateSwapper((exe, args) => { _starts.Add((exe, args)); return process; }, (_, _) => ready, _log);
        return swapper.Apply(_exe, _staged, new Version(1, 2, 0), new RestartArgs(RunState.Running, Tray: true), _failed.Add);
    }

    [Fact]
    public void A_new_exe_that_reports_ready_stays_in_place()
    {
        Assert.Equal(SwapResult.Started, Apply(ready: true));
        Assert.Equal("new exe", Marker(_exe));
        Assert.False(File.Exists(UpdateSwapper.NewPath(_exe)));
        Assert.Equal("old exe", Marker(UpdateSwapper.OldPath(_exe))); // the new app deletes it once the old one exits
        var (exe, args) = Assert.Single(_starts);
        Assert.Equal(_exe, exe);
        Assert.Equal(new RestartArgs(RunState.Running, true, UpdatedFrom: new Version(1, 2, 0)), RestartArgs.Parse(args));
        Assert.Empty(_failed);
    }

    [Fact]
    public void A_new_exe_that_never_reports_ready_is_killed_and_the_old_one_restored()
    {
        var process = new FakeProcess();
        Assert.Equal(SwapResult.RolledBack, Apply(ready: false, process));
        Assert.True(process.Killed);
        Assert.Equal("old exe", Marker(_exe));
        Assert.False(File.Exists(UpdateSwapper.NewPath(_exe)));
        Assert.False(File.Exists(UpdateSwapper.OldPath(_exe)));
        Assert.Equal(new[] { new Version(1, 3, 0) }, _failed);
        Assert.Equal(2, _starts.Count);
        Assert.Equal(new RestartArgs(RunState.Running, true, UpdateFailed: new Version(1, 3, 0)), RestartArgs.Parse(_starts[1].Args));
    }

    [Fact]
    public void A_swap_that_cannot_move_the_exe_changes_nothing_and_is_not_retried()
    {
        File.Delete(_staged.Path); // the copy step fails
        Assert.Equal(SwapResult.RolledBack, Apply(ready: true));
        Assert.Equal("old exe", Marker(_exe));
        Assert.False(File.Exists(UpdateSwapper.NewPath(_exe)));
        Assert.Equal(new[] { new Version(1, 3, 0) }, _failed);
        var (_, args) = Assert.Single(_starts);
        Assert.Equal(new Version(1, 3, 0), RestartArgs.Parse(args).UpdateFailed);
    }

    [Fact]
    public void A_leftover_old_exe_is_replaced()
    {
        File.WriteAllBytes(UpdateSwapper.OldPath(_exe), FakeExe("older exe"));
        Assert.Equal(SwapResult.Started, Apply(ready: true));
        Assert.Equal("old exe", Marker(UpdateSwapper.OldPath(_exe)));
    }

    [Fact]
    public void A_staged_file_that_is_not_a_windows_program_is_never_started()
    {
        File.WriteAllText(_staged.Path, "this is not a program");
        Assert.Equal(SwapResult.RolledBack, Apply(ready: true));
        Assert.Equal("old exe", Marker(_exe));
        Assert.False(File.Exists(UpdateSwapper.NewPath(_exe)));
        Assert.Equal(new[] { new Version(1, 3, 0) }, _failed);
        var (_, args) = Assert.Single(_starts); // only the old exe, started again
        Assert.Equal(new Version(1, 3, 0), RestartArgs.Parse(args).UpdateFailed);
    }

    [Fact]
    public void The_windows_program_check_reads_the_pe_signature()
    {
        Assert.True(UpdateSwapper.IsWindowsExe(_exe));
        var path = _dir.Combine("mz-only.exe");
        File.WriteAllBytes(path, FakeExe("x").Take(64).Concat(new byte[] { (byte)'N', (byte)'E', 0, 0 }).ToArray());
        Assert.False(UpdateSwapper.IsWindowsExe(path));
        File.WriteAllText(path, "MZ");
        Assert.False(UpdateSwapper.IsWindowsExe(path));
        Assert.False(UpdateSwapper.IsWindowsExe(_dir.Combine("missing.exe")));
    }
}
