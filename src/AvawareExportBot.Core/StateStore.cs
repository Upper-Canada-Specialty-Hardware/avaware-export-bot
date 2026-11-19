using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AvawareExportBot.Core;

public enum FileStatus { Pending, Exporting, Done, Failed }

public sealed record FileRow(
    string Path,
    long SizeBytes,
    DateTime ApjModifiedUtc,
    FileStatus Status,
    int Attempts,
    string? LastError,
    DateTime? LastAttemptUtc,
    DateTime? DoneUtc,
    DateTime? RetryAfterUtc = null);

/// <summary>One .apj seen by a scan.</summary>
public sealed record ScannedApj(string Path, long SizeBytes, DateTime ModifiedUtc, bool HasMdb);

public sealed record ScanInfo(long Id, DateTime StartedUtc, DateTime? FinishedUtc, int ApjFound, int NeedingExport, int UnreadableDirs, bool Completed);

public sealed record StatusCounts(int Pending, int Exporting, int Done, int Failed)
{
    public int Total => Pending + Exporting + Done + Failed;
}

/// <summary>
/// SQLite store of every .apj the scans have seen and of export attempts, at %LOCALAPPDATA%\AvawareExportBot\state.db.
/// The file system is the truth for whether a .mdb exists; this store remembers attempts and failures across runs.
/// One connection guarded by a lock, so it is safe to call from any thread.
/// </summary>
public sealed class StateStore : IDisposable
{
    public const int SchemaVersion = 2;

    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public StateStore(string dbPath)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(dbPath))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode = WAL;");
        Exec("PRAGMA busy_timeout = 5000;");
        Migrate();
        RecoveredExporting = ResetExporting();
    }

    /// <summary>Rows left in 'exporting' by a crash or kill, put back to 'pending' when the store opened.</summary>
    public int RecoveredExporting { get; }

    private void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version < 1)
        {
            Exec("""
                CREATE TABLE IF NOT EXISTS files (
                    path             TEXT    NOT NULL PRIMARY KEY COLLATE NOCASE,
                    size_bytes       INTEGER NOT NULL,
                    apj_modified_utc TEXT    NOT NULL,
                    status           TEXT    NOT NULL CHECK (status IN ('pending', 'exporting', 'done', 'failed')),
                    attempts         INTEGER NOT NULL DEFAULT 0,
                    last_error       TEXT    NULL,
                    last_attempt_utc TEXT    NULL,
                    done_utc         TEXT    NULL,
                    seen_scan_id     INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_files_status_modified ON files (status, apj_modified_utc DESC);
                CREATE TABLE IF NOT EXISTS scans (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    started_utc     TEXT    NOT NULL,
                    finished_utc    TEXT    NULL,
                    apj_found       INTEGER NOT NULL DEFAULT 0,
                    needing_export  INTEGER NOT NULL DEFAULT 0,
                    unreadable_dirs INTEGER NOT NULL DEFAULT 0,
                    completed       INTEGER NOT NULL DEFAULT 0
                );
                PRAGMA user_version = 1;
                """);
        }
        if (version < 2)
        {
            // Retry backoff: a failed or deferred row is not claimed again before this time.
            Exec("""
                ALTER TABLE files ADD COLUMN retry_after_utc TEXT NULL;
                PRAGMA user_version = 2;
                """);
        }
    }

    // ---- scans ----

    public long BeginScan(DateTime utcNow)
    {
        lock (_gate)
        {
            using var cmd = Cmd("INSERT INTO scans (started_utc) VALUES ($t); SELECT last_insert_rowid();");
            cmd.Parameters.AddWithValue("$t", Iso(utcNow));
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>
    /// Records what a scan saw. New .apj: 'pending', or 'done' when its .mdb exists. Known .apj: a .mdb present makes it
    /// 'done'; a .mdb gone from a 'done' row makes it 'pending' again with no attempts; 'pending', 'exporting' and
    /// 'failed' rows without a .mdb keep their status.
    /// </summary>
    public void UpsertSeen(long scanId, IReadOnlyCollection<ScannedApj> batch, DateTime utcNow)
    {
        if (batch.Count == 0) return;
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = Cmd("""
                INSERT INTO files (path, size_bytes, apj_modified_utc, status, attempts, done_utc, seen_scan_id)
                VALUES ($path, $size, $mod, CASE WHEN $hasMdb THEN 'done' ELSE 'pending' END, 0,
                        CASE WHEN $hasMdb THEN $now END, $scan)
                ON CONFLICT (path) DO UPDATE SET
                    size_bytes       = excluded.size_bytes,
                    apj_modified_utc = excluded.apj_modified_utc,
                    seen_scan_id     = excluded.seen_scan_id,
                    status = CASE
                        WHEN $hasMdb THEN 'done'
                        WHEN files.status = 'done' THEN 'pending'
                        ELSE files.status END,
                    attempts = CASE WHEN NOT $hasMdb AND files.status = 'done' THEN 0 ELSE files.attempts END,
                    retry_after_utc = CASE WHEN $hasMdb OR files.status = 'done' THEN NULL ELSE files.retry_after_utc END,
                    done_utc = CASE
                        WHEN $hasMdb THEN COALESCE(files.done_utc, $now)
                        ELSE NULL END;
                """, tx);
            var path = cmd.Parameters.Add("$path", SqliteType.Text);
            var size = cmd.Parameters.Add("$size", SqliteType.Integer);
            var mod = cmd.Parameters.Add("$mod", SqliteType.Text);
            var hasMdb = cmd.Parameters.Add("$hasMdb", SqliteType.Integer);
            cmd.Parameters.AddWithValue("$now", Iso(utcNow));
            cmd.Parameters.AddWithValue("$scan", scanId);
            foreach (var apj in batch)
            {
                path.Value = apj.Path;
                size.Value = apj.SizeBytes;
                mod.Value = Iso(apj.ModifiedUtc);
                hasMdb.Value = apj.HasMdb ? 1 : 0;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Closes a scan. Only a completed scan deletes the rows it did not see (their .apj is gone), and never rows under a
    /// directory it could not list (<paramref name="unreadableDirs"/>): those were not seen, not proven gone. A failed or
    /// cancelled scan keeps every row. Rows being exported are never deleted here.
    /// </summary>
    public void FinishScan(long scanId, DateTime utcNow, int apjFound, int needingExport, IReadOnlyCollection<string> unreadableDirs, bool completed)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using (var cmd = Cmd("""
                UPDATE scans SET finished_utc = $t, apj_found = $f, needing_export = $n, unreadable_dirs = $u, completed = $c
                WHERE id = $id;
                """, tx))
            {
                cmd.Parameters.AddWithValue("$t", Iso(utcNow));
                cmd.Parameters.AddWithValue("$f", apjFound);
                cmd.Parameters.AddWithValue("$n", needingExport);
                cmd.Parameters.AddWithValue("$u", unreadableDirs.Count);
                cmd.Parameters.AddWithValue("$c", completed ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", scanId);
                cmd.ExecuteNonQuery();
            }
            if (completed)
            {
                var stale = new List<string>();
                using (var find = Cmd("SELECT path FROM files WHERE seen_scan_id < $id AND status <> 'exporting';", tx))
                {
                    find.Parameters.AddWithValue("$id", scanId);
                    using var r = find.ExecuteReader();
                    while (r.Read()) stale.Add(r.GetString(0));
                }
                var prefixes = unreadableDirs.Select(d => d.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar).ToList();
                using var del = Cmd("DELETE FROM files WHERE path = $p;", tx);
                var p = del.Parameters.Add("$p", SqliteType.Text);
                foreach (var path in stale)
                {
                    if (prefixes.Any(pre => path.StartsWith(pre, StringComparison.OrdinalIgnoreCase))) continue;
                    p.Value = path;
                    del.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    /// <summary>The last scan that finished; with <paramref name="completedOnly"/>, the last one that saw the whole root.</summary>
    public ScanInfo? LastFinishedScan(bool completedOnly = false)
    {
        lock (_gate)
        {
            using var cmd = Cmd($"""
                SELECT id, started_utc, finished_utc, apj_found, needing_export, unreadable_dirs, completed
                FROM scans WHERE finished_utc IS NOT NULL {(completedOnly ? "AND completed = 1" : "")} ORDER BY id DESC LIMIT 1;
                """);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new ScanInfo(r.GetInt64(0), ParseIso(r.GetString(1)), ParseIso(r.GetString(2)), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6) == 1);
        }
    }

    // ---- exports ----

    /// <summary>
    /// Claims the newest pending .apj that is due (no retry_after, or it has passed) for export; null when none is due.
    /// </summary>
    public FileRow? ClaimNext(DateTime utcNow)
    {
        lock (_gate)
        {
            while (true)
            {
                string? path;
                using (var pick = Cmd("""
                    SELECT path FROM files
                    WHERE status = 'pending' AND (retry_after_utc IS NULL OR retry_after_utc <= $now)
                    ORDER BY apj_modified_utc DESC LIMIT 1;
                    """))
                {
                    pick.Parameters.AddWithValue("$now", Iso(utcNow));
                    path = pick.ExecuteScalar() as string;
                }
                if (path is null) return null;
                if (TryClaimLocked(path)) return GetLocked(path);
            }
        }
    }

    /// <summary>Moves one row from 'pending' to 'exporting'. False when it was not pending (someone else has it).</summary>
    public bool TryClaim(string path)
    {
        lock (_gate) return TryClaimLocked(path);
    }

    private bool TryClaimLocked(string path)
    {
        using var cmd = Cmd("UPDATE files SET status = 'exporting' WHERE path = $p AND status = 'pending';");
        cmd.Parameters.AddWithValue("$p", path);
        return cmd.ExecuteNonQuery() == 1;
    }

    public void RecordSuccess(string path, DateTime utcNow)
    {
        lock (_gate)
        {
            using var cmd = Cmd("""
                UPDATE files SET status = 'done', done_utc = $t, last_attempt_utc = $t, last_error = NULL, retry_after_utc = NULL
                WHERE path = $p;
                """);
            cmd.Parameters.AddWithValue("$t", Iso(utcNow));
            cmd.Parameters.AddWithValue("$p", path);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Counts a failed attempt. Returns the new status: 'pending' to retry no sooner than <paramref name="retryAfterUtc"/>,
    /// or 'failed' at the max. A row no longer 'exporting' (a scan saw its .mdb) is left as it is.
    /// </summary>
    public FileStatus RecordFailure(string path, string error, DateTime utcNow, int maxAttempts, DateTime retryAfterUtc)
    {
        lock (_gate)
        {
            using var cmd = Cmd("""
                UPDATE files SET
                    attempts = attempts + 1,
                    last_error = $e,
                    last_attempt_utc = $t,
                    retry_after_utc = $ra,
                    status = CASE WHEN attempts + 1 >= $max THEN 'failed' ELSE 'pending' END
                WHERE path = $p AND status = 'exporting'
                RETURNING status;
                """);
            cmd.Parameters.AddWithValue("$e", error);
            cmd.Parameters.AddWithValue("$t", Iso(utcNow));
            cmd.Parameters.AddWithValue("$ra", Iso(retryAfterUtc));
            cmd.Parameters.AddWithValue("$max", maxAttempts);
            cmd.Parameters.AddWithValue("$p", path);
            if (cmd.ExecuteScalar() is string s) return ParseStatus(s);
            // A scan already saw its .mdb (made 'done') or the .apj vanished: leave the row as it is.
            return GetLocked(path)?.Status ?? FileStatus.Done;
        }
    }

    /// <summary>
    /// Not the file's fault (share unreachable, AVAproject missing, worker desktop failed): back to 'pending' with no
    /// attempt counted, not claimed again before <paramref name="retryAfterUtc"/>.
    /// </summary>
    public void RecordDeferred(string path, string error, DateTime retryAfterUtc)
    {
        lock (_gate)
        {
            using var cmd = Cmd("""
                UPDATE files SET status = 'pending', last_error = $e, retry_after_utc = $ra
                WHERE path = $p AND status = 'exporting';
                """);
            cmd.Parameters.AddWithValue("$e", error);
            cmd.Parameters.AddWithValue("$ra", Iso(retryAfterUtc));
            cmd.Parameters.AddWithValue("$p", path);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>An export cancelled by the app (exit): back to 'pending' without counting an attempt.</summary>
    public void RecordCancelled(string path)
    {
        lock (_gate)
        {
            using var cmd = Cmd("UPDATE files SET status = 'pending' WHERE path = $p AND status = 'exporting';");
            cmd.Parameters.AddWithValue("$p", path);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The user's retry of a failed .apj: back to 'pending' with no attempts, due at once.</summary>
    public bool Retry(string path)
    {
        lock (_gate)
        {
            using var cmd = Cmd("UPDATE files SET status = 'pending', attempts = 0, retry_after_utc = NULL WHERE path = $p AND status = 'failed';");
            cmd.Parameters.AddWithValue("$p", path);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>The user's retry of several failed .apj in one statement.</summary>
    public int Retry(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return 0;
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = Cmd("UPDATE files SET status = 'pending', attempts = 0, retry_after_utc = NULL WHERE path = $p AND status = 'failed';", tx);
            var p = cmd.Parameters.Add("$p", SqliteType.Text);
            var n = 0;
            foreach (var path in paths)
            {
                p.Value = path;
                n += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return n;
        }
    }

    public int RetryAllFailed()
    {
        lock (_gate)
        {
            using var cmd = Cmd("UPDATE files SET status = 'pending', attempts = 0, retry_after_utc = NULL WHERE status = 'failed';");
            return cmd.ExecuteNonQuery();
        }
    }

    private int ResetExporting()
    {
        lock (_gate)
        {
            using var cmd = Cmd("UPDATE files SET status = 'pending' WHERE status = 'exporting';");
            return cmd.ExecuteNonQuery();
        }
    }

    // ---- reads ----

    public StatusCounts GetCounts()
    {
        lock (_gate)
        {
            int p = 0, e = 0, d = 0, f = 0;
            using var cmd = Cmd("SELECT status, COUNT(*) FROM files GROUP BY status;");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var n = r.GetInt32(1);
                switch (ParseStatus(r.GetString(0)))
                {
                    case FileStatus.Pending: p = n; break;
                    case FileStatus.Exporting: e = n; break;
                    case FileStatus.Done: d = n; break;
                    case FileStatus.Failed: f = n; break;
                }
            }
            return new StatusCounts(p, e, d, f);
        }
    }

    /// <summary>Rows with a status, newest .apj first.</summary>
    public IReadOnlyList<FileRow> ListByStatus(FileStatus status, int limit, int offset = 0)
    {
        lock (_gate)
        {
            using var cmd = Cmd($"""
                SELECT {Columns} FROM files WHERE status = $s ORDER BY apj_modified_utc DESC LIMIT $l OFFSET $o;
                """);
            cmd.Parameters.AddWithValue("$s", StatusText(status));
            cmd.Parameters.AddWithValue("$l", limit);
            cmd.Parameters.AddWithValue("$o", offset);
            using var r = cmd.ExecuteReader();
            var rows = new List<FileRow>();
            while (r.Read()) rows.Add(ReadRow(r));
            return rows;
        }
    }

    public FileRow? Get(string path)
    {
        lock (_gate) return GetLocked(path);
    }

    private FileRow? GetLocked(string path)
    {
        using var cmd = Cmd($"SELECT {Columns} FROM files WHERE path = $p;");
        cmd.Parameters.AddWithValue("$p", path);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadRow(r) : null;
    }

    public void Dispose()
    {
        lock (_gate) _db.Dispose();
    }

    // ---- helpers ----

    private const string Columns = "path, size_bytes, apj_modified_utc, status, attempts, last_error, last_attempt_utc, done_utc, retry_after_utc";

    private static FileRow ReadRow(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetInt64(1),
        ParseIso(r.GetString(2)),
        ParseStatus(r.GetString(3)),
        r.GetInt32(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : ParseIso(r.GetString(6)),
        r.IsDBNull(7) ? null : ParseIso(r.GetString(7)),
        r.IsDBNull(8) ? null : ParseIso(r.GetString(8)));

    private SqliteCommand Cmd(string sql, SqliteTransaction? tx = null)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private void Exec(string sql)
    {
        lock (_gate)
        {
            using var cmd = Cmd(sql);
            cmd.ExecuteNonQuery();
        }
    }

    private object? Scalar(string sql)
    {
        lock (_gate)
        {
            using var cmd = Cmd(sql);
            return cmd.ExecuteScalar();
        }
    }

    internal static string Iso(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    internal static DateTime ParseIso(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static string StatusText(FileStatus s) => s switch
    {
        FileStatus.Pending => "pending",
        FileStatus.Exporting => "exporting",
        FileStatus.Done => "done",
        FileStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    private static FileStatus ParseStatus(string s) => s switch
    {
        "pending" => FileStatus.Pending,
        "exporting" => FileStatus.Exporting,
        "done" => FileStatus.Done,
        "failed" => FileStatus.Failed,
        _ => throw new InvalidDataException($"Unknown status '{s}'."),
    };
}
