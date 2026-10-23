using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;

namespace VideoDownloader.Infrastructure.Persistence;

public sealed class SqliteAutoSessionRepository : IAutoSessionRepository
{
    private readonly string _connectionString;

    public SqliteAutoSessionRepository(IOptions<AppOptions> options)
    {
        var dbPath = PathExpander.Expand(options.Value.Database.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS auto_sessions (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                status INTEGER NOT NULL,
                site TEXT NULL,
                max_videos INTEGER NOT NULL DEFAULT 0,
                idle_minutes INTEGER NOT NULL DEFAULT 0,
                enqueued_count INTEGER NOT NULL DEFAULT 0,
                tab_hint TEXT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_auto_sessions_status ON auto_sessions(status);
            CREATE INDEX IF NOT EXISTS ix_auto_sessions_started ON auto_sessions(started_at);

            CREATE TABLE IF NOT EXISTS auto_queue_items (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                seq INTEGER NOT NULL,
                page_url TEXT NOT NULL,
                content_id TEXT NULL,
                caption TEXT NULL,
                author TEXT NULL,
                source_url TEXT NOT NULL,
                variant_meta_json TEXT NOT NULL,
                variant_secret BLOB NULL,
                selected_variant_id TEXT NULL,
                fingerprint TEXT NULL,
                status INTEGER NOT NULL,
                download_job_id TEXT NULL,
                error_code TEXT NULL,
                probed_at TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(session_id, fingerprint),
                UNIQUE(session_id, seq)
            );
            CREATE INDEX IF NOT EXISTS ix_auto_queue_session_status
                ON auto_queue_items(session_id, status, seq);
            """);

        // Crash recovery: a Running session cannot outlive the process.
        await conn.ExecuteAsync("""
            UPDATE auto_sessions
            SET status = @Paused, updated_at = @Now
            WHERE status = @Running
            """, new
        {
            Paused = (int)AutoSessionStatus.Paused,
            Running = (int)AutoSessionStatus.Running,
            Now = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    public async Task SaveSessionAsync(AutoSession session, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO auto_sessions (
                id, name, status, site, max_videos, idle_minutes, enqueued_count, tab_hint,
                started_at, ended_at, updated_at)
            VALUES (
                @Id, @Name, @Status, @Site, @MaxVideos, @IdleMinutes, @EnqueuedCount, @TabHint,
                @StartedAt, @EndedAt, @UpdatedAt)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                status = excluded.status,
                site = excluded.site,
                max_videos = excluded.max_videos,
                idle_minutes = excluded.idle_minutes,
                enqueued_count = excluded.enqueued_count,
                tab_hint = excluded.tab_hint,
                ended_at = excluded.ended_at,
                updated_at = excluded.updated_at;
            """, MapSessionParams(session));
    }

    public async Task<AutoSession?> GetSessionAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<SessionRow>(
            "SELECT * FROM auto_sessions WHERE id = @Id", new { Id = id.ToString() });
        return row is null ? null : MapSession(row);
    }

    public async Task<IReadOnlyList<AutoSession>> GetSessionsAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<SessionRow>(
            "SELECT * FROM auto_sessions ORDER BY started_at DESC, id DESC");
        return rows.Select(MapSession).ToList();
    }

    public async Task<AutoSession?> GetRunningSessionAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<SessionRow>(
            "SELECT * FROM auto_sessions WHERE status = @Running ORDER BY started_at DESC LIMIT 1",
            new { Running = (int)AutoSessionStatus.Running });
        return row is null ? null : MapSession(row);
    }

    public async Task PauseAllRunningAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE auto_sessions
            SET status = @Paused, updated_at = @Now
            WHERE status = @Running
            """, new
        {
            Paused = (int)AutoSessionStatus.Paused,
            Running = (int)AutoSessionStatus.Running,
            Now = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    public async Task DeleteSessionAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM auto_queue_items WHERE session_id = @Id",
            new { Id = id.ToString() }, tx);
        await conn.ExecuteAsync(
            "DELETE FROM auto_sessions WHERE id = @Id",
            new { Id = id.ToString() }, tx);
        await tx.CommitAsync(ct);
    }

    public async Task<int> DeleteEmptySessionsAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return await conn.ExecuteAsync("""
            DELETE FROM auto_sessions WHERE NOT EXISTS
                (SELECT 1 FROM auto_queue_items WHERE session_id = auto_sessions.id)
            """);
    }

    public async Task<int> DeleteCompletedItemsAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return await conn.ExecuteAsync(
            "DELETE FROM auto_queue_items WHERE status = @Completed",
            new { Completed = (int)AutoQueueItemStatus.Completed });
    }

    public async Task<IReadOnlyList<Guid>> GetDownloadJobIdsAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var ids = await conn.QueryAsync<string>(
            "SELECT DISTINCT download_job_id FROM auto_queue_items WHERE download_job_id IS NOT NULL");
        return ids.Select(Guid.Parse).ToArray();
    }

    public async Task<bool> TryDeleteCompletedSessionAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var deleted = await conn.ExecuteAsync("""
            DELETE FROM auto_sessions
            WHERE id = @Id AND status != @Running
              AND EXISTS (SELECT 1 FROM auto_queue_items WHERE session_id = @Id)
              AND NOT EXISTS (SELECT 1 FROM auto_queue_items WHERE session_id = @Id AND status != @Completed)
            """, new { Id = id.ToString(), Running = (int)AutoSessionStatus.Running,
                Completed = (int)AutoQueueItemStatus.Completed }, tx);
        if (deleted > 0)
            await conn.ExecuteAsync("DELETE FROM auto_queue_items WHERE session_id = @Id",
                new { Id = id.ToString() }, tx);
        await tx.CommitAsync(ct);
        return deleted > 0;
    }

    public async Task<AutoQueueItem?> TryAddQueuedItemAsync(AutoQueueItem item, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (!string.IsNullOrEmpty(item.Fingerprint))
        {
            var existing = await conn.ExecuteScalarAsync<string?>(
                """
                SELECT id FROM auto_queue_items
                WHERE session_id = @SessionId AND fingerprint = @Fingerprint
                LIMIT 1
                """,
                new { SessionId = item.SessionId.ToString(), item.Fingerprint }, tx);
            if (existing is not null)
                return null;
        }

        var nextSeq = await conn.ExecuteScalarAsync<int>(
            "SELECT COALESCE(MAX(seq), 0) + 1 FROM auto_queue_items WHERE session_id = @SessionId",
            new { SessionId = item.SessionId.ToString() }, tx);
        item.Seq = nextSeq;
        item.Status = AutoQueueItemStatus.Queued;
        item.UpdatedAt = DateTimeOffset.UtcNow;

        await conn.ExecuteAsync("""
            INSERT INTO auto_queue_items (
                id, session_id, seq, page_url, content_id, caption, author, source_url,
                variant_meta_json, variant_secret, selected_variant_id, fingerprint, status,
                download_job_id, error_code, probed_at, created_at, updated_at)
            VALUES (
                @Id, @SessionId, @Seq, @PageUrl, @ContentId, @Caption, @Author, @SourceUrl,
                @VariantMetaJson, @VariantSecret, @SelectedVariantId, @Fingerprint, @Status,
                @DownloadJobId, @ErrorCode, @ProbedAt, @CreatedAt, @UpdatedAt)
            """, MapItemParams(item), tx);

        await conn.ExecuteAsync("""
            UPDATE auto_sessions
            SET enqueued_count = enqueued_count + 1, updated_at = @Now
            WHERE id = @SessionId
            """, new { SessionId = item.SessionId.ToString(), Now = item.UpdatedAt.ToString("O") }, tx);

        await tx.CommitAsync(ct);
        return item;
    }

    public async Task SaveItemAsync(AutoQueueItem item, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await conn.ExecuteAsync("""
            UPDATE auto_queue_items SET
                page_url = @PageUrl,
                content_id = @ContentId,
                caption = @Caption,
                author = @Author,
                source_url = @SourceUrl,
                variant_meta_json = @VariantMetaJson,
                variant_secret = @VariantSecret,
                selected_variant_id = @SelectedVariantId,
                fingerprint = @Fingerprint,
                status = @Status,
                download_job_id = @DownloadJobId,
                error_code = @ErrorCode,
                updated_at = @UpdatedAt
            WHERE id = @Id
            """, MapItemParams(item));
    }

    public async Task<IReadOnlyList<AutoQueueItem>> GetItemsAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<ItemRow>(
            "SELECT * FROM auto_queue_items WHERE session_id = @SessionId ORDER BY seq ASC",
            new { SessionId = sessionId.ToString() });
        return rows.Select(MapItem).ToList();
    }

    public async Task<AutoQueueItem?> TryClaimNextQueuedAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<ItemRow>("""
            SELECT * FROM auto_queue_items
            WHERE session_id = @SessionId AND status = @Queued
            ORDER BY seq ASC
            LIMIT 1
            """, new { SessionId = sessionId.ToString(), Queued = (int)AutoQueueItemStatus.Queued }, tx);
        if (row is null)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var changed = await conn.ExecuteAsync("""
            UPDATE auto_queue_items
            SET status = @Enqueued, updated_at = @Now
            WHERE id = @Id AND status = @Queued
            """, new
        {
            Id = row.id,
            Enqueued = (int)AutoQueueItemStatus.Enqueued,
            Queued = (int)AutoQueueItemStatus.Queued,
            Now = now
        }, tx);
        await tx.CommitAsync(ct);
        if (changed == 0)
            return null;

        var item = MapItem(row);
        item.Status = AutoQueueItemStatus.Enqueued;
        item.UpdatedAt = DateTimeOffset.Parse(now);
        return item;
    }

    public async Task<IReadOnlyList<Guid>> GetActiveJobIdsAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var ids = await conn.QueryAsync<string>("""
            SELECT download_job_id FROM auto_queue_items
            WHERE session_id = @SessionId
              AND download_job_id IS NOT NULL
              AND status IN (@Enqueued, @Downloading)
            """, new
        {
            SessionId = sessionId.ToString(),
            Enqueued = (int)AutoQueueItemStatus.Enqueued,
            Downloading = (int)AutoQueueItemStatus.Downloading
        });
        return ids
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();
    }

    public async Task DeleteItemAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM auto_queue_items WHERE id = @Id",
            new { Id = id.ToString() });
    }

    private static object MapSessionParams(AutoSession session) => new
    {
        Id = session.Id.ToString(),
        session.Name,
        Status = (int)session.Status,
        session.Site,
        session.MaxVideos,
        session.IdleMinutes,
        session.EnqueuedCount,
        session.TabHint,
        StartedAt = session.StartedAt.ToString("O"),
        EndedAt = session.EndedAt?.ToString("O"),
        UpdatedAt = session.UpdatedAt.ToString("O")
    };

    private static object MapItemParams(AutoQueueItem item) => new
    {
        Id = item.Id.ToString(),
        SessionId = item.SessionId.ToString(),
        item.Seq,
        item.PageUrl,
        item.ContentId,
        item.Caption,
        item.Author,
        item.SourceUrl,
        item.VariantMetaJson,
        item.VariantSecret,
        item.SelectedVariantId,
        item.Fingerprint,
        Status = (int)item.Status,
        DownloadJobId = item.DownloadJobId?.ToString(),
        item.ErrorCode,
        ProbedAt = item.ProbedAt.ToString("O"),
        CreatedAt = item.CreatedAt.ToString("O"),
        UpdatedAt = item.UpdatedAt.ToString("O")
    };

    private static AutoSession MapSession(SessionRow row) => new()
    {
        Id = Guid.Parse(row.id),
        Name = row.name,
        Status = (AutoSessionStatus)row.status,
        Site = row.site,
        MaxVideos = row.max_videos,
        IdleMinutes = row.idle_minutes,
        EnqueuedCount = row.enqueued_count,
        TabHint = row.tab_hint,
        StartedAt = DateTimeOffset.Parse(row.started_at),
        EndedAt = string.IsNullOrEmpty(row.ended_at) ? null : DateTimeOffset.Parse(row.ended_at),
        UpdatedAt = DateTimeOffset.Parse(row.updated_at)
    };

    private static AutoQueueItem MapItem(ItemRow row) => new()
    {
        Id = Guid.Parse(row.id),
        SessionId = Guid.Parse(row.session_id),
        Seq = row.seq,
        PageUrl = row.page_url,
        ContentId = row.content_id,
        Caption = row.caption,
        Author = row.author,
        SourceUrl = row.source_url,
        VariantMetaJson = row.variant_meta_json,
        VariantSecret = row.variant_secret,
        SelectedVariantId = row.selected_variant_id,
        Fingerprint = row.fingerprint,
        Status = (AutoQueueItemStatus)row.status,
        DownloadJobId = Guid.TryParse(row.download_job_id, out var jobId) ? jobId : null,
        ErrorCode = row.error_code,
        ProbedAt = DateTimeOffset.Parse(row.probed_at),
        CreatedAt = DateTimeOffset.Parse(row.created_at),
        UpdatedAt = DateTimeOffset.Parse(row.updated_at)
    };

    private sealed class SessionRow
    {
        public string id { get; init; } = "";
        public string name { get; init; } = "";
        public int status { get; init; }
        public string? site { get; init; }
        public int max_videos { get; init; }
        public int idle_minutes { get; init; }
        public int enqueued_count { get; init; }
        public string? tab_hint { get; init; }
        public string started_at { get; init; } = "";
        public string? ended_at { get; init; }
        public string updated_at { get; init; } = "";
    }

    private sealed class ItemRow
    {
        public string id { get; init; } = "";
        public string session_id { get; init; } = "";
        public int seq { get; init; }
        public string page_url { get; init; } = "";
        public string? content_id { get; init; }
        public string? caption { get; init; }
        public string? author { get; init; }
        public string source_url { get; init; } = "";
        public string variant_meta_json { get; init; } = "";
        public byte[]? variant_secret { get; init; }
        public string? selected_variant_id { get; init; }
        public string? fingerprint { get; init; }
        public int status { get; init; }
        public string? download_job_id { get; init; }
        public string? error_code { get; init; }
        public string probed_at { get; init; } = "";
        public string created_at { get; init; } = "";
        public string updated_at { get; init; } = "";
    }
}
