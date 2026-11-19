using AvawareExportBot.Core;
using Xunit;

namespace AvawareExportBot.Core.Tests;

public sealed class ScannerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ListLog _log = new();

    private string Root => _dir.Combine("root");

    private (StateStore Store, Scanner Scanner) Make()
    {
        var store = new StateStore(_dir.Combine("state.db"));
        return (store, new Scanner(store, _log));
    }

    [Fact]
    public void Finds_apj_recursively_and_skips_ones_with_a_sibling_mdb()
    {
        _dir.File("root/a/one.apj");
        _dir.File("root/a/one.mdb");
        _dir.File("root/a/b/two.APJ");
        _dir.File("root/c/three.apj");
        _dir.File("root/c/other.mdb");
        _dir.File("root/c/notes.txt");
        var (store, scanner) = Make();
        using (store)
        {
            var result = scanner.Run(Root, null, CancellationToken.None);
            Assert.True(result.Completed);
            Assert.Equal(3, result.ApjFound);
            Assert.Equal(2, result.NeedingExport);
            Assert.Equal(new StatusCounts(2, 0, 1, 0), store.GetCounts());
        }
    }

    [Fact]
    public void Mdb_match_ignores_case()
    {
        _dir.File("root/Job.apj");
        _dir.File("root/JOB.MDB");
        var (store, scanner) = Make();
        using (store)
        {
            Assert.Equal(0, scanner.Run(Root, null, CancellationToken.None).NeedingExport);
        }
    }

    [Fact]
    public void Mdb_added_between_scans_marks_the_row_done()
    {
        _dir.File("root/p/x.apj");
        var (store, scanner) = Make();
        using (store)
        {
            scanner.Run(Root, null, CancellationToken.None);
            Assert.Equal(1, store.GetCounts().Pending);
            _dir.File("root/p/x.mdb");
            scanner.Run(Root, null, CancellationToken.None);
            Assert.Equal(new StatusCounts(0, 0, 1, 0), store.GetCounts());
        }
    }

    [Fact]
    public void Vanished_apj_rows_are_removed_after_a_finished_scan()
    {
        var gone = _dir.File("root/gone.apj");
        _dir.File("root/stays.apj");
        var (store, scanner) = Make();
        using (store)
        {
            scanner.Run(Root, null, CancellationToken.None);
            File.Delete(gone);
            scanner.Run(Root, null, CancellationToken.None);
            Assert.Null(store.Get(gone));
            Assert.Equal(1, store.GetCounts().Total);
        }
    }

    [Fact]
    public void Missing_root_ends_early_and_keeps_rows()
    {
        _dir.File("root/a.apj");
        var (store, scanner) = Make();
        using (store)
        {
            scanner.Run(Root, null, CancellationToken.None);
            var result = scanner.Run(_dir.Combine("nope"), null, CancellationToken.None);
            Assert.False(result.Completed);
            Assert.Equal(1, store.GetCounts().Total);
            Assert.Contains(_log.Lines, l => l.StartsWith("ERROR") && l.Contains("not found"));
        }
    }

    [Fact]
    public void Cancelled_scan_is_incomplete()
    {
        _dir.File("root/a.apj");
        var (store, scanner) = Make();
        using (store)
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.False(scanner.Run(Root, null, cts.Token).Completed);
        }
    }

    [Fact]
    public void Unreadable_directory_is_logged_and_skipped()
    {
        if (OperatingSystem.IsWindows()) return; // permission bits below are POSIX; covered by CI on Linux.
        _dir.File("root/ok/a.apj");
        var locked = _dir.Combine("root", "locked");
        Directory.CreateDirectory(locked);
        _dir.File("root/locked/b.apj");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            if (Directory.EnumerateFileSystemEntries(locked).Any()) return; // running as root: permissions not enforced
        }
        catch (UnauthorizedAccessException) { }
        try
        {
            var (store, scanner) = Make();
            using (store)
            {
                var result = scanner.Run(Root, null, CancellationToken.None);
                Assert.True(result.Completed);
                Assert.Equal(1, result.UnreadableDirs);
                Assert.Equal(1, result.ApjFound);
                Assert.Contains(_log.Lines, l => l.StartsWith("WARN") && l.Contains("cannot read"));
            }
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Pending_rows_come_out_newest_first()
    {
        _dir.File("root/old.apj", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _dir.File("root/new.apj", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _dir.File("root/mid.apj", new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var (store, scanner) = Make();
        using (store)
        {
            scanner.Run(Root, null, CancellationToken.None);
            var names = store.ListByStatus(FileStatus.Pending, 10).Select(r => Path.GetFileName(r.Path)).ToArray();
            Assert.Equal(new[] { "new.apj", "mid.apj", "old.apj" }, names);
        }
    }

    public void Dispose() => _dir.Dispose();
}
