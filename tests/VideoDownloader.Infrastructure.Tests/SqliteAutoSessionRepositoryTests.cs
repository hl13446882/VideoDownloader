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
