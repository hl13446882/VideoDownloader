using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;

namespace VideoDownloader.Infrastructure.Persistence;

public sealed class SqliteDownloadRepository : IDownloadRepository
{
    private readonly string _connectionString;

    public SqliteDownloadRepository(IOptions<AppOptions> options)
    {
        var dbPath = PathExpander.Expand(options.Value.Database.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        const string sql = """
            CREATE TABLE IF NOT EXISTS download_jobs (
                id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                source_url TEXT NOT NULL,
                target_path TEXT NOT NULL,
                media_family INTEGER NOT NULL,
                status INTEGER NOT NULL,
                downloaded_bytes INTEGER NOT NULL DEFAULT 0,
                total_bytes INTEGER NULL,
                etag TEXT NULL,
                last_modified TEXT NULL,
                context_version INTEGER NOT NULL DEFAULT 1,
                request_context_meta_json TEXT NOT NULL,
                request_context_secret BLOB NULL,
                page_url TEXT NULL,
                last_error_code TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_download_jobs_status ON download_jobs(status);
            """;
        await conn.ExecuteAsync(sql);
        await TryAddColumnAsync(conn, "page_url", "TEXT NULL");
        await TryAddColumnAsync(conn, "caption", "TEXT NULL");
        await TryAddColumnAsync(conn, "duration_sec", "REAL NULL");
        await TryAddColumnAsync(conn, "expected_total_bytes", "INTEGER NULL");
        await TryAddColumnAsync(conn, "content_prefix_hash", "TEXT NULL");
        await TryAddColumnAsync(conn, "video_kind", "TEXT NULL");
        await TryAddColumnAsync(conn, "completed_at", "TEXT NULL");
        await TryAddColumnAsync(conn, "edited_at", "TEXT NULL");
        await TryAddColumnAsync(conn, "author", "TEXT NULL");
        await TryAddColumnAsync(conn, "subtitle_recognized", "INTEGER NOT NULL DEFAULT 0");
        // One-time backfill: treat updated_at as completion time for already-finished rows.
        await conn.ExecuteAsync("""
            UPDATE download_jobs
            SET completed_at = updated_at
            WHERE status = @Completed
              AND (completed_at IS NULL OR completed_at = '')
            """, new { Completed = (int)DownloadStatus.Completed });
    }

    private static async Task TryAddColumnAsync(SqliteConnection conn, string name, string definition)
    {
        try
        {
            await conn.ExecuteAsync($"ALTER TABLE download_jobs ADD COLUMN {name} {definition}");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // duplicate column
        }
    }

    public async Task SaveAsync(DownloadJob job, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var (meta, secret) = DownloadJobMapper.SerializeVariant(job.Variant);
        var mediaFamily = ResolveMediaFamily(job.Variant.Container);

        const string sql = """
            INSERT INTO download_jobs (
                id, display_name, source_url, target_path, media_family, status,
                downloaded_bytes, total_bytes, etag, last_modified, context_version,
                request_context_meta_json, request_context_secret, page_url, last_error_code,
                caption, duration_sec, expected_total_bytes, content_prefix_hash,
                video_kind, completed_at, edited_at, author, subtitle_recognized,
                created_at, updated_at)
            VALUES (
                @Id, @DisplayName, @SourceUrl, @TargetPath, @MediaFamily, @Status,
                @DownloadedBytes, @TotalBytes, @ETag, @LastModified, @ContextVersion,
                @MetaJson, @Secret, @PageUrl, @LastErrorCode,
                @Caption, @DurationSec, @ExpectedTotalBytes, @ContentPrefixHash,
                @VideoKind, @CompletedAt, @EditedAt, @Author, @SubtitleRecognized,
                @CreatedAt, @UpdatedAt)
            ON CONFLICT(id) DO UPDATE SET
                display_name = excluded.display_name,
                source_url = excluded.source_url,
                target_path = excluded.target_path,
                media_family = excluded.media_family,
                status = excluded.status,
                downloaded_bytes = excluded.downloaded_bytes,
                total_bytes = excluded.total_bytes,
                etag = excluded.etag,
                last_modified = excluded.last_modified,
                context_version = excluded.context_version,
                request_context_meta_json = excluded.request_context_meta_json,
                request_context_secret = excluded.request_context_secret,
                page_url = excluded.page_url,
                last_error_code = excluded.last_error_code,
                caption = excluded.caption,
                duration_sec = excluded.duration_sec,
                expected_total_bytes = excluded.expected_total_bytes,
                content_prefix_hash = excluded.content_prefix_hash,
                video_kind = excluded.video_kind,
                completed_at = excluded.completed_at,
                edited_at = excluded.edited_at,
                author = COALESCE(excluded.author, download_jobs.author),
                subtitle_recognized = excluded.subtitle_recognized,
                updated_at = excluded.updated_at;
            """;

        await conn.ExecuteAsync(sql, new
        {
            Id = job.Id.ToString(),
            job.DisplayName,
            SourceUrl = job.Variant.SourceUrl.ToString(),
            job.TargetPath,
            MediaFamily = (int)mediaFamily,
            Status = (int)job.Status,
            job.DownloadedBytes,
            job.TotalBytes,
            job.ETag,
            job.LastModified,
            ContextVersion = job.Variant.RequestContext.Version,
            MetaJson = meta,
            Secret = secret,
            PageUrl = job.PageUrl?.ToString(),
            job.LastErrorCode,
            job.Caption,
            job.DurationSec,
            job.ExpectedTotalBytes,
            job.ContentPrefixHash,
            VideoKind = job.VideoKind,
            CompletedAt = job.CompletedAt?.ToString("O"),
            EditedAt = job.EditedAt?.ToString("O"),
            Author = job.Author,
            SubtitleRecognized = job.SubtitleRecognized ? 1 : 0,
            CreatedAt = job.CreatedAt.ToString("O"),
            UpdatedAt = job.UpdatedAt.ToString("O")
        });
    }

    public async Task<DownloadJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        return all.FirstOrDefault(j => j.Id == id);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM download_jobs WHERE id = @Id",
            new { Id = id.ToString() });
    }

    public async Task<IReadOnlyList<DownloadJob>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<JobRow>("SELECT * FROM download_jobs ORDER BY updated_at DESC");
        return rows.Select(MapRow).Where(j => j is not null).Cast<DownloadJob>().ToList();
    }

    private static DownloadJob? MapRow(JobRow row)
    {
        if (!Uri.TryCreate(row.source_url, UriKind.Absolute, out _))
            return null;

        var variant = DownloadJobMapper.DeserializeVariant(
            row.source_url,
            row.request_context_meta_json,
            row.request_context_secret);

        Uri? pageUrl = null;
        if (!string.IsNullOrWhiteSpace(row.page_url) &&
            Uri.TryCreate(row.page_url, UriKind.Absolute, out var parsedPage))
            pageUrl = parsedPage;

        return new DownloadJob
        {
            Id = Guid.Parse(row.id),
            DisplayName = row.display_name,
            Caption = row.caption,
            Author = row.author,
            DurationSec = row.duration_sec,
            VideoKind = row.video_kind,
            Variant = variant,
            TargetPath = row.target_path,
            PageUrl = pageUrl,
            Status = (DownloadStatus)row.status,
            DownloadedBytes = row.downloaded_bytes,
            TotalBytes = row.total_bytes,
            ExpectedTotalBytes = row.expected_total_bytes,
            ContentPrefixHash = row.content_prefix_hash,
            ETag = row.etag,
            LastModified = row.last_modified,
            LastErrorCode = row.last_error_code,
            CreatedAt = DateTimeOffset.Parse(row.created_at),
            UpdatedAt = DateTimeOffset.Parse(row.updated_at),
            CompletedAt = ParseOptionalOffset(row.completed_at),
            EditedAt = ParseOptionalOffset(row.edited_at),
            SubtitleRecognized = row.subtitle_recognized != 0
        };
    }

    private static DateTimeOffset? ParseOptionalOffset(string? value) =>
        !string.IsNullOrWhiteSpace(value) && DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : null;

    private static MediaFamily ResolveMediaFamily(string? container) =>
        container?.ToLowerInvariant() switch
        {
            "hls" => MediaFamily.Hls,
            "dash" => MediaFamily.Dash,
            _ => MediaFamily.DirectMp4
        };

    private sealed class JobRow
    {
        public string id { get; set; } = string.Empty;
        public string display_name { get; set; } = string.Empty;
        public string source_url { get; set; } = string.Empty;
        public string target_path { get; set; } = string.Empty;
        public int media_family { get; set; }
        public int status { get; set; }
        public long downloaded_bytes { get; set; }
        public long? total_bytes { get; set; }
        public string? etag { get; set; }
        public string? last_modified { get; set; }
        public int context_version { get; set; }
        public string request_context_meta_json { get; set; } = string.Empty;
        public byte[]? request_context_secret { get; set; }
        public string? page_url { get; set; }
        public string? last_error_code { get; set; }
        public string? caption { get; set; }
        public double? duration_sec { get; set; }
        public long? expected_total_bytes { get; set; }
        public string? content_prefix_hash { get; set; }
        public string? video_kind { get; set; }
        public string? completed_at { get; set; }
        public string? edited_at { get; set; }
        public string? author { get; set; }
        public int subtitle_recognized { get; set; }
        public string created_at { get; set; } = string.Empty;
        public string updated_at { get; set; } = string.Empty;
    }
}
