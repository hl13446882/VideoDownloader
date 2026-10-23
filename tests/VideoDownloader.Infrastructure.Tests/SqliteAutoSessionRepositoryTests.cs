using Microsoft.Extensions.Options;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Persistence;

namespace VideoDownloader.Infrastructure.Tests;

public sealed class SqliteAutoSessionRepositoryTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteAutoSessionRepository _repo;

    public SqliteAutoSessionRepositoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vd-auto-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var options = Options.Create(new AppOptions
        {
            Database = { Path = Path.Combine(_root, "downloads.db") }
        });
        _repo = new SqliteAutoSessionRepository(options);
        _repo.InitializeAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task TryAddQueuedItem_AssignsSeqAndRejectsDuplicateFingerprint()
    {
        var session = new AutoSession
        {
            Id = Guid.NewGuid(),
            Name = "2026-10-22 14:00:00",
            Status = AutoSessionStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _repo.SaveSessionAsync(session);

        var first = await _repo.TryAddQueuedItemAsync(MakeItem(session.Id, "fp-a", "https://a.test/1"));
        var dup = await _repo.TryAddQueuedItemAsync(MakeItem(session.Id, "fp-a", "https://a.test/1b"));
        var second = await _repo.TryAddQueuedItemAsync(MakeItem(session.Id, "fp-b", "https://a.test/2"));

        Assert.NotNull(first);
        Assert.Null(dup);
        Assert.NotNull(second);
        Assert.Equal(1, first!.Seq);
        Assert.Equal(2, second!.Seq);

        var stored = await _repo.GetSessionAsync(session.Id);
        Assert.Equal(2, stored!.EnqueuedCount);
    }

    [Fact]
    public async Task TryClaimNextQueued_ReturnsInSeqOrder()
    {
        var sessionId = Guid.NewGuid();
        await _repo.SaveSessionAsync(new AutoSession
        {
            Id = sessionId,
            Name = "2026-10-22 15:00:00",
            Status = AutoSessionStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _repo.TryAddQueuedItemAsync(MakeItem(sessionId, "a", "https://a.test/1"));
        await _repo.TryAddQueuedItemAsync(MakeItem(sessionId, "b", "https://a.test/2"));

        var first = await _repo.TryClaimNextQueuedAsync(sessionId);
        var second = await _repo.TryClaimNextQueuedAsync(sessionId);
        var third = await _repo.TryClaimNextQueuedAsync(sessionId);

        Assert.Equal(1, first!.Seq);
        Assert.Equal(AutoQueueItemStatus.Enqueued, first.Status);
        Assert.Equal(2, second!.Seq);
        Assert.Null(third);
    }

    [Fact]
    public async Task Initialize_PausesRunningSessions()
    {
        var session = new AutoSession
        {
            Id = Guid.NewGuid(),
            Name = "2026-10-22 16:00:00",
            Status = AutoSessionStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _repo.SaveSessionAsync(session);

        await _repo.InitializeAsync();
        var loaded = await _repo.GetSessionAsync(session.Id);
        Assert.Equal(AutoSessionStatus.Paused, loaded!.Status);
    }

    [Theory]
    [InlineData(AutoSessionStatus.Paused, AutoQueueItemStatus.Completed, true)]
    [InlineData(AutoSessionStatus.Stopped, AutoQueueItemStatus.Completed, true)]
    [InlineData(AutoSessionStatus.Running, AutoQueueItemStatus.Completed, false)]
    [InlineData(AutoSessionStatus.Paused, AutoQueueItemStatus.Queued, false)]
    [InlineData(AutoSessionStatus.Paused, AutoQueueItemStatus.Failed, false)]
    [InlineData(AutoSessionStatus.Paused, AutoQueueItemStatus.Cancelled, false)]
    public async Task Cleanup_OnlyDeletesFinishedSessions(AutoSessionStatus status, AutoQueueItemStatus itemStatus, bool removed)
    {
        var sid = Guid.NewGuid();
        await _repo.SaveSessionAsync(new AutoSession { Id = sid, Name = "cleanup", Status = status,
            StartedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        var item = await _repo.TryAddQueuedItemAsync(MakeItem(sid, "cleanup", "https://a.test/video"));
        item!.Status = itemStatus;
        await _repo.SaveItemAsync(item);
        Assert.Equal(removed, await _repo.TryDeleteCompletedSessionAsync(sid));
        Assert.Equal(removed, await _repo.GetSessionAsync(sid) is null);
        Assert.Equal(removed ? 0 : 1, (await _repo.GetItemsAsync(sid)).Count);
    }

    [Fact]
    public async Task Cleanup_PreservesDownloadHistoryAndVideoFile()
    {
        var downloadRepo = new SqliteDownloadRepository(Options.Create(new AppOptions
        {
            Database = { Path = Path.Combine(_root, "downloads.db") }
        }));
        await downloadRepo.InitializeAsync();
        var jobId = Guid.NewGuid();
        var video = Path.Combine(_root, "keep.mp4");
        await File.WriteAllBytesAsync(video, [1, 2, 3, 4]);
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_root, "downloads.db")}");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO download_jobs(id,display_name,source_url,target_path,media_family,status,
                request_context_meta_json,created_at,updated_at)
            VALUES ($id,'keep','https://a.test/video',$path,0,5,'{}','2026-10-23','2026-10-23')
            """;
        cmd.Parameters.AddWithValue("$id", jobId.ToString());
        cmd.Parameters.AddWithValue("$path", video);
        await cmd.ExecuteNonQueryAsync();
        var sid = Guid.NewGuid();
        await _repo.SaveSessionAsync(new AutoSession { Id = sid, Name = "done", Status = AutoSessionStatus.Paused,
            StartedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        var item = await _repo.TryAddQueuedItemAsync(MakeItem(sid, "done", "https://a.test/video"));
        item!.Status = AutoQueueItemStatus.Completed;
        item.DownloadJobId = jobId;
        await _repo.SaveItemAsync(item);
        Assert.True(await _repo.TryDeleteCompletedSessionAsync(sid));
        cmd.CommandText = "SELECT count(*) FROM download_jobs WHERE id=$id";
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(video));
    }

    [Fact]
    public async Task StartupCleanup_DropsCompletedVideosAndKeepsUnfinishedTasks()
    {
        var downloadRepo = new SqliteDownloadRepository(Options.Create(new AppOptions
        {
            Database = { Path = Path.Combine(_root, "downloads.db") }
        }));
        await downloadRepo.InitializeAsync();
        var finishedJob = Guid.NewGuid();
        var partialJob = Guid.NewGuid();
        var video = Path.Combine(_root, "done.mp4");
        var partial = Path.Combine(_root, "partial.mp4.part");
        await File.WriteAllBytesAsync(video, [9, 9]);
        await File.WriteAllBytesAsync(partial, [1]);
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_root, "downloads.db")}");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO download_jobs(id,display_name,source_url,target_path,media_family,status,
                request_context_meta_json,created_at,updated_at)
            VALUES ($id,'done','https://a.test/done',$path,0,5,'{}','2026-10-23','2026-10-23')
            """;
        cmd.Parameters.AddWithValue("$id", finishedJob.ToString());
        cmd.Parameters.AddWithValue("$path", video);
        await cmd.ExecuteNonQueryAsync();

        var mixed = Guid.NewGuid();
        var doneOnly = Guid.NewGuid();
        foreach (var id in new[] { mixed, doneOnly })
            await _repo.SaveSessionAsync(new AutoSession { Id = id, Name = id.ToString(), Status = AutoSessionStatus.Paused,
                StartedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

        var completed = await _repo.TryAddQueuedItemAsync(MakeItem(mixed, "done", "https://a.test/done"));
        completed!.Status = AutoQueueItemStatus.Completed;
        completed.DownloadJobId = finishedJob;
        await _repo.SaveItemAsync(completed);
        var pending = await _repo.TryAddQueuedItemAsync(MakeItem(mixed, "partial", "https://a.test/partial"));
        pending!.Status = AutoQueueItemStatus.Downloading;
        pending.DownloadJobId = partialJob;
        await _repo.SaveItemAsync(pending);
        var onlyDone = await _repo.TryAddQueuedItemAsync(MakeItem(doneOnly, "only", "https://a.test/only"));
        onlyDone!.Status = AutoQueueItemStatus.Completed;
        onlyDone.DownloadJobId = finishedJob;
        await _repo.SaveItemAsync(onlyDone);

        Assert.Equal(2, await _repo.DeleteCompletedItemsAsync());
        Assert.Equal(1, await _repo.DeleteEmptySessionsAsync());

        Assert.Null(await _repo.GetSessionAsync(doneOnly));
        var kept = await _repo.GetItemsAsync(mixed);
        Assert.Single(kept);
        Assert.Equal(pending.Id, kept[0].Id);
        cmd.CommandText = "SELECT count(*) FROM download_jobs WHERE id=$id";
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
        Assert.Equal(new byte[] { 9, 9 }, await File.ReadAllBytesAsync(video));
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(partial));
    }

    [Fact]
    public async Task StartupCleanup_UsesActualItemCount_AndKeepsNonemptySessions()
    {
        var empty = Guid.NewGuid();
        var nonempty = Guid.NewGuid();
        foreach (var id in new[] { empty, nonempty })
            await _repo.SaveSessionAsync(new AutoSession { Id = id, Name = "startup",
                Status = AutoSessionStatus.Paused, EnqueuedCount = id == empty ? 10 : 0,
                StartedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _repo.TryAddQueuedItemAsync(MakeItem(nonempty, "video", "https://a.test/video"));
        Assert.Equal(1, await _repo.DeleteEmptySessionsAsync());
        Assert.Null(await _repo.GetSessionAsync(empty));
        Assert.NotNull(await _repo.GetSessionAsync(nonempty));
        Assert.Equal(0, await _repo.DeleteEmptySessionsAsync());
    }

    private static AutoQueueItem MakeItem(Guid sessionId, string fingerprint, string url)
    {
        var now = DateTimeOffset.UtcNow;
        return new AutoQueueItem
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            PageUrl = url,
            SourceUrl = url,
            VariantMetaJson = "{}",
            Fingerprint = fingerprint,
            Status = AutoQueueItemStatus.Queued,
            ProbedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* ignore */ }
    }
}
