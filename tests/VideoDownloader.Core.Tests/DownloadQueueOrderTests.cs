using VideoDownloader.Core.Download;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Tests;

public sealed class DownloadQueueOrderTests
{
    [Fact]
    public void Sort_Failed_ThenActive_ThenNewestCompleted()
    {
        var failedOld = Job(DownloadStatus.Failed, daysAgo: 2);
        var failedNew = Job(DownloadStatus.Failed, daysAgo: 0);
        var downloading = Job(DownloadStatus.Downloading, daysAgo: 1);
        var muxing = Job(DownloadStatus.Muxing, daysAgo: 0);
        var completedOld = Job(DownloadStatus.Completed, daysAgo: 5);
        var completedNew = Job(DownloadStatus.Completed, daysAgo: 1);

        var sorted = DownloadQueueOrder.Sort(
            [completedOld, downloading, failedOld, completedNew, muxing, failedNew]).ToArray();

        Assert.Equal([failedNew.Id, failedOld.Id, muxing.Id, downloading.Id, completedNew.Id, completedOld.Id],
            sorted.Select(j => j.Id));
    }

    private static DownloadJob Job(DownloadStatus status, int daysAgo) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = status.ToString(),
        TargetPath = "x.mp4",
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo - 1),
        UpdatedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo)
    };
}
