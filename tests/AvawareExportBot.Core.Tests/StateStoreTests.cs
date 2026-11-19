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
        store.FinishScan(id, _now, rows.Length, rows.Count(r => !r.HasMdb), Array.Empty<string>(), completed: true);
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
        Assert.Equal(@"T:\new.apj", store.ClaimNext(_now)!.Path);
        Assert.Equal(@"T:\mid.apj", store.ClaimNext(_now)!.Path);
        Assert.False(store.TryClaim(@"T:\mid.apj"));
        Assert.Equal(@"T:\old.apj", store.ClaimNext(_now)!.Path);
        Assert.Null(store.ClaimNext(_now));
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
            store.ClaimNext(_now);
            Assert.Equal(FileStatus.Pending, store.RecordFailure(@"T:\a.apj", "boom " + i, _now, maxAttempts: 3, _now));
        }
        store.ClaimNext(_now);
        Assert.Equal(FileStatus.Failed, store.RecordFailure(@"T:\a.apj", "boom 3", _now, maxAttempts: 3, _now));
        var row = store.Get(@"T:\a.apj")!;
        Assert.Equal(3, row.Attempts);
        Assert.Equal("boom 3", row.LastError);
        Assert.Null(store.ClaimNext(_now));

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
            store.RecordFailure(p, "x", _now, maxAttempts: 1, _now);
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
        store.ClaimNext(_now);
        Seen(store, Apj(@"T:\a.apj", hasMdb: true));
        Assert.Equal(FileStatus.Done, store.RecordFailure(@"T:\a.apj", "late", _now, 3, _now));
        Assert.Equal(FileStatus.Done, store.Get(@"T:\a.apj")!.Status);
    }

    [Fact]
    public void Exporting_rows_return_to_pending_on_reopen()
    {
        using (var store = Open())
        {
            Seen(store, Apj(@"T:\a.apj"));
            store.ClaimNext(_now);
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
        store.FinishScan(partial, _now, 1, 1, Array.Empty<string>(), completed: false);
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

    [Fact]
    public void Failed_row_is_not_claimed_again_before_its_retry_time()
    {
        using var store = Open();
        Seen(store, Apj(@"T:\a.apj"));
        store.ClaimNext(_now);
        store.RecordFailure(@"T:\a.apj", "share blip", _now, 3, _now.AddMinutes(60));
        Assert.Null(store.ClaimNext(_now.AddMinutes(59)));
        Assert.Equal(@"T:\a.apj", store.ClaimNext(_now.AddMinutes(60))!.Path);
    }

    [Fact]
    public void Deferred_counts_no_attempt_and_waits()
    {
        using var store = Open();
        Seen(store, Apj(@"T:\a.apj"));
        store.ClaimNext(_now);
        store.RecordDeferred(@"T:\a.apj", "share unreachable", _now.AddMinutes(5));
        var row = store.Get(@"T:\a.apj")!;
        Assert.Equal(FileStatus.Pending, row.Status);
        Assert.Equal(0, row.Attempts);
        Assert.Equal("share unreachable", row.LastError);
        Assert.Null(store.ClaimNext(_now.AddMinutes(4)));
        Assert.NotNull(store.ClaimNext(_now.AddMinutes(5)));
    }

    [Fact]
    public void User_retry_is_due_at_once()
    {
        using var store = Open();
        Seen(store, Apj(@"T:\a.apj"), Apj(@"T:\b.apj"));
        foreach (var p in new[] { @"T:\a.apj", @"T:\b.apj" })
        {
            store.TryClaim(p);
            store.RecordFailure(p, "x", _now, 1, _now.AddHours(1));
        }
        Assert.Equal(2, store.Retry(new[] { @"T:\a.apj", @"T:\b.apj" }));
        Assert.NotNull(store.ClaimNext(_now));
        Assert.NotNull(store.ClaimNext(_now));
    }

    [Fact]
    public void Completed_scan_keeps_rows_under_unreadable_folders()
    {
        using var store = Open();
        var locked = System.IO.Path.Combine("T:", "locked");
        var inside = System.IO.Path.Combine(locked, "x.apj");
        var lockedTwin = System.IO.Path.Combine("T:", "lockedTwin", "y.apj"); // same prefix text, different folder
        Seen(store, Apj(@"T:\a.apj"), Apj(inside), Apj(lockedTwin));
        store.TryClaim(inside);
        store.RecordFailure(inside, "x", _now, 3, _now);

        var id = store.BeginScan(_now);
        store.UpsertSeen(id, new[] { Apj(@"T:\a.apj") }, _now);
        store.FinishScan(id, _now, 1, 1, new[] { locked }, completed: true);

        var kept = store.Get(inside)!;
        Assert.Equal(1, kept.Attempts);
        Assert.Null(store.Get(lockedTwin));
    }

    [Fact]
    public void Last_completed_scan_ignores_incomplete_ones()
    {
        using var store = Open();
        Seen(store, Apj(@"T:\a.apj"));
        var later = store.BeginScan(_now.AddHours(1));
        store.FinishScan(later, _now.AddHours(1), 0, 0, Array.Empty<string>(), completed: false);
        Assert.Equal(_now.AddHours(1), store.LastFinishedScan()!.FinishedUtc);
        Assert.Equal(_now, store.LastFinishedScan(completedOnly: true)!.FinishedUtc);
    }

    [Fact]
    public void Version_1_database_is_upgraded_in_place()
    {
        var path = _dir.Combine("v1.db");
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE files (path TEXT NOT NULL PRIMARY KEY COLLATE NOCASE, size_bytes INTEGER NOT NULL,
                    apj_modified_utc TEXT NOT NULL, status TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0,
                    last_error TEXT NULL, last_attempt_utc TEXT NULL, done_utc TEXT NULL, seen_scan_id INTEGER NOT NULL);
                CREATE TABLE scans (id INTEGER PRIMARY KEY AUTOINCREMENT, started_utc TEXT NOT NULL, finished_utc TEXT NULL,
                    apj_found INTEGER NOT NULL DEFAULT 0, needing_export INTEGER NOT NULL DEFAULT 0,
                    unreadable_dirs INTEGER NOT NULL DEFAULT 0, completed INTEGER NOT NULL DEFAULT 0);
                INSERT INTO files VALUES ('T:\old.apj', 1, '2026-01-01T00:00:00.000Z', 'pending', 2, 'x', NULL, NULL, 1);
                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
        }
        using var store = new StateStore(path);
        var row = store.Get(@"T:\old.apj")!;
        Assert.Equal(2, row.Attempts);
        Assert.Null(row.RetryAfterUtc);
        Assert.NotNull(store.ClaimNext(_now));
    }

    public void Dispose() => _dir.Dispose();
}
