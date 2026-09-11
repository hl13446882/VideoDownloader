using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Download;

public static class DownloadQueueOrder
{
    /// <summary>
    /// Failed first, then in-progress (including paused/queued), then completed.
    /// Within a band, newest <see cref="DownloadJob.UpdatedAt"/> is first.
    /// </summary>
    public static int Rank(DownloadStatus status) => status switch
    {
        DownloadStatus.Failed => 0,
        DownloadStatus.Completed => 2,
        _ => 1
    };

    public static IOrderedEnumerable<DownloadJob> Sort(IEnumerable<DownloadJob> jobs) =>
        jobs.OrderBy(Rank).ThenByDescending(j => j.UpdatedAt);

    private static int Rank(DownloadJob job) => Rank(job.Status);
}
