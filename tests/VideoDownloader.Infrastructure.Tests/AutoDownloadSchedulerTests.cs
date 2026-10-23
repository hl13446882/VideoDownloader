using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Download;
using VideoDownloader.Infrastructure.Persistence;

namespace VideoDownloader.Infrastructure.Tests;

public sealed class AutoDownloadSchedulerTests
{
    [Fact]
    public async Task ReconcilesAllSessionsAtStartupAndAfterStop_WithoutClaimingQueuedItems()
    {
        var root = Path.Combine(Path.GetTempPath(), "vd-scheduler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repo = new SqliteAutoSessionRepository(Options.Create(new AppOptions
            {
                Database = { Path = Path.Combine(root, "downloads.db") }
            }));
            await repo.InitializeAsync();
            var engine = Substitute.For<IDownloadEngine>();
            engine.FreeDownloadSlots.Returns(3);
            var jobs = new List<DownloadJob>();
            engine.GetActiveJobs().Returns(_ => jobs.ToArray());
            var items = new List<AutoQueueItem>();
            foreach (var status in new[] { AutoSessionStatus.Paused, AutoSessionStatus.Stopped })
            {
                var sid = Guid.NewGuid();
                await repo.SaveSessionAsync(new AutoSession { Id = sid, Name = sid.ToString(), Status = status,
                    StartedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
                var item = await repo.TryAddQueuedItemAsync(new AutoQueueItem { Id = Guid.NewGuid(), SessionId = sid,
                    PageUrl = "https://example.test/video", SourceUrl = "https://example.test/video", VariantMetaJson = "{}",
                    Fingerprint = "video", ProbedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow });
                item!.DownloadJobId = Guid.NewGuid();
                item.Status = AutoQueueItemStatus.Downloading;
                item.ErrorCode = "old_error";
                await repo.SaveItemAsync(item);
                items.Add(item);
                await repo.TryAddQueuedItemAsync(new AutoQueueItem { Id = Guid.NewGuid(), SessionId = sid,
                    PageUrl = "https://example.test/next", SourceUrl = "https://example.test/next", VariantMetaJson = "{}",
                    Fingerprint = "next", ProbedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow });
            }
            using var scheduler = new AutoDownloadScheduler(repo, engine, NullLogger<AutoDownloadScheduler>.Instance);
            scheduler.Start();
            scheduler.Stop();
            // Jobs finish after the producer stops. Reconciliation must still run for both sessions.
            foreach (var item in items)
            {
                var job = new DownloadJob { Id = item.DownloadJobId!.Value, DisplayName = "test", TargetPath = "test.mp4" };
                typeof(DownloadJob).GetProperty(nameof(DownloadJob.Status))!.SetValue(job, DownloadStatus.Completed);
                jobs.Add(job);
            }
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                var stored = new List<AutoQueueItem>();
                foreach (var item in items) stored.AddRange(await repo.GetItemsAsync(item.SessionId));
                if (stored.Where(i => i.DownloadJobId is not null).All(i => i.Status == AutoQueueItemStatus.Completed && i.ErrorCode is null)) break;
                Assert.True(DateTime.UtcNow < deadline, "Stopped sessions were not reconciled");
                await Task.Delay(50);
            }
            Assert.Null(scheduler.ActiveSessionId);
            foreach (var item in items)
                Assert.NotEqual(AutoSessionStatus.Running, (await repo.GetSessionAsync(item.SessionId))!.Status);
            Assert.DoesNotContain(engine.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IDownloadEngine.EnqueueAsync));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
    }

    [Theory]
    [InlineData(DownloadStatus.Completed, AutoQueueItemStatus.Completed)]
    [InlineData(DownloadStatus.Failed, AutoQueueItemStatus.Failed)]
    [InlineData(DownloadStatus.Paused, AutoQueueItemStatus.Enqueued)]
    [InlineData(DownloadStatus.Pending, AutoQueueItemStatus.Enqueued)]
    [InlineData(DownloadStatus.Cancelled, AutoQueueItemStatus.Cancelled)]
    [InlineData(DownloadStatus.Removed, AutoQueueItemStatus.Cancelled)]
    [InlineData(DownloadStatus.Muxing, AutoQueueItemStatus.Downloading)]
    public void UsesActualDownloadState(DownloadStatus actual, AutoQueueItemStatus expected) =>
        Assert.Equal(expected, AutoQueueItemState.FromDownload(actual, AutoQueueItemStatus.Downloading));
}
