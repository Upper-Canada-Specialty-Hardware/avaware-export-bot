using AvawareExportBot.Core;
using Xunit;

namespace AvawareExportBot.Core.Tests;

public sealed class StateStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly DateTime _now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    private StateStore Open() => new(_dir.Combine("state.db"));

    private static ScannedApj Apj(string path, int daysOld = 0, bool hasMdb = false) =>
        new(path, 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-daysOld), hasMdb);

    private void Seen(StateStore store, params ScannedApj[] rows)
    {
        var id = store.BeginScan(_now);
        store.UpsertSeen(id, rows, _now);
        store.FinishScan(id, _now, rows.Length, rows.Count(r => !r.HasMdb), 0, completed: true);
    }

    [Fact]
    public void New_rows_are_pending_or_done_by_mdb()
    {
        using var store = Open();
        Seen(store, Apj(@"T:\a.apj"), Apj(@"T:\b.apj", hasMdb: true));
        Assert.Equal(FileStatus.Pending, store.Get(@"T:\a.apj")!.Status);
        Assert.Equal(FileStatus.Done, store.Get(@"T:\b.apj")!.Status);
        Assert.Equal(new StatusCounts(1, 0, 1, 0), store.GetCounts());
    }

    [Fact]
    public void Mdb_appearing_between_scans_marks_done_and_disappearing_makes_pending_again()
    {
        using var store = Open();
        Seen(store, Apj(@"T:\a.apj"));
        Seen(store, Apj(@"T:\a.apj", hasMdb: true));
        Assert.Equal(FileStatus.Done, store.Get(@"T:\a.apj")!.Status);
        Seen(store, Apj(@"T:\a.apj"));
        var row = store.Get(@"T:\a.apj")!;
        Assert.Equal(FileStatus.Pending, row.Status);
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.DoneUtc);
    }

    [Fact]
    public void Paths_compare_case_insensitively()
    {
        using var store = Open();
        Seen(store, Apj(@"T:\Proj\A.apj"));
        Seen(store, Apj(@"t:\proj\a.APJ", hasMdb: true));
        Assert.Equal(1, store.GetCounts().Total);
        Assert.Equal(FileStatus.Done, store.Get(@"T:\PROJ\A.APJ")!.Status);
    }

    [Fact]
    public void Claim_takes_newest_first_and_is_exclusive()
    {
        using var store = Open();
        Seen(store, Apj(@"T:\old.apj", daysOld: 10), Apj(@"T:\new.apj", daysOld: 1), Apj(@"T:\mid.apj", daysOld: 5));
        Assert.Equal(@"T:\new.apj", store.ClaimNext()!.Path);
        Assert.Equal(@"T:\mid.apj", store.ClaimNext()!.Path);
        Assert.False(store.TryClaim(@"T:\mid.apj"));
        Assert.Equal(@"T:\old.apj", store.ClaimNext()!.Path);
        Assert.Null(store.ClaimNext());
    }

    [Fact]
    public void Concurrent_claims_of_one_row_have_one_winner()
    {
        using var store = Open();
        Seen(store, Apj(@"T:\a.apj"));
        var wins = 0;
        Parallel.For(0, 16, _ => { if (store.TryClaim(@"T:\a.apj")) Interlocked.Increment(ref wins); });
        Assert.Equal(1, wins);
    }

    [Fact]
    public void Failures_count_attempts_until_failed_and_retry_resets()
    {
        using var store = Open();
        Seen(store, Apj(@"T:\a.apj"));
        for (var i = 1; i <= 2; i++)
        {
            store.ClaimNext();
            Assert.Equal(FileStatus.Pending, store.RecordFailure(@"T:\a.apj", "boom " + i, _now, maxAttempts: 3));
        }
        store.ClaimNext();
        Assert.Equal(FileStatus.Failed, store.RecordFailure(@"T:\a.apj", "boom 3", _now, maxAttempts: 3));
        var row = store.Get(@"T:\a.apj")!;
        Assert.Equal(3, row.Attempts);
        Assert.Equal("boom 3", row.LastError);
        Assert.Null(store.ClaimNext());

        Assert.True(store.Retry(@"T:\a.apj"));
        row = store.Get(@"T:\a.apj")!;
        Assert.Equal(FileStatus.Pending, row.Status);
        Assert.Equal(0, row.Attempts);
    }

    [Fact]
    public void Retry_all_resets_every_failed_row()
    {
        using var store = Open();
        Seen(store, Apj(@"T:\a.apj"), Apj(@"T:\b.apj"));
        foreach (var p in new[] { @"T:\a.apj", @"T:\b.apj" })
        {
            store.TryClaim(p);
            store.RecordFailure(p, "x", _now, maxAttempts: 1);
        }
        Assert.Equal(2, store.GetCounts().Failed);
        Assert.Equal(2, store.RetryAllFailed());
        Assert.Equal(2, store.GetCounts().Pending);
    }

    [Fact]
    public void Success_and_cancel()
    {
        using var store = Open();
        Seen(store, Apj(@"T:\a.apj"), Apj(@"T:\b.apj"));
        store.TryClaim(@"T:\a.apj");
        store.RecordSuccess(@"T:\a.apj", _now);
        Assert.Equal(FileStatus.Done, store.Get(@"T:\a.apj")!.Status);

        store.TryClaim(@"T:\b.apj");
        store.RecordCancelled(@"T:\b.apj");
        var b = store.Get(@"T:\b.apj")!;
        Assert.Equal(FileStatus.Pending, b.Status);
        Assert.Equal(0, b.Attempts);
    }

    [Fact]
    public void Failure_after_a_scan_saw_the_mdb_leaves_it_done()
    {
        using var store = Open();
        Seen(store, Apj(@"T:\a.apj"));
        store.ClaimNext();
        Seen(store, Apj(@"T:\a.apj", hasMdb: true));
        Assert.Equal(FileStatus.Done, store.RecordFailure(@"T:\a.apj", "late", _now, 3));
        Assert.Equal(FileStatus.Done, store.Get(@"T:\a.apj")!.Status);
    }

    [Fact]
    public void Exporting_rows_return_to_pending_on_reopen()
    {
        using (var store = Open())
        {
            Seen(store, Apj(@"T:\a.apj"));
            store.ClaimNext();
        }
        using var reopened = Open();
        Assert.Equal(1, reopened.RecoveredExporting);
        Assert.Equal(FileStatus.Pending, reopened.Get(@"T:\a.apj")!.Status);
    }

    [Fact]
    public void Completed_scan_removes_unseen_rows_but_incomplete_scan_keeps_them()
    {
        using var store = Open();
        Seen(store, Apj(@"T:\a.apj"), Apj(@"T:\b.apj"), Apj(@"T:\c.apj"));
        store.TryClaim(@"T:\c.apj");

        var partial = store.BeginScan(_now);
        store.UpsertSeen(partial, new[] { Apj(@"T:\a.apj") }, _now);
        store.FinishScan(partial, _now, 1, 1, 0, completed: false);
        Assert.Equal(3, store.GetCounts().Total);

        Seen(store, Apj(@"T:\a.apj"));
        Assert.NotNull(store.Get(@"T:\a.apj"));
        Assert.Null(store.Get(@"T:\b.apj"));
        Assert.NotNull(store.Get(@"T:\c.apj")); // being exported: kept
    }

    [Fact]
    public void Last_finished_scan_is_reported()
    {
        using var store = Open();
        Assert.Null(store.LastFinishedScan());
        Seen(store, Apj(@"T:\a.apj"));
        var scan = store.LastFinishedScan()!;
        Assert.True(scan.Completed);
        Assert.Equal(1, scan.ApjFound);
        Assert.Equal(_now, scan.FinishedUtc);
    }

    public void Dispose() => _dir.Dispose();
}
