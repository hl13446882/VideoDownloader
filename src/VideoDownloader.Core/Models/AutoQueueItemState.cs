namespace VideoDownloader.Core.Models;

/// <summary>The download engine is authoritative for items already submitted to it.</summary>
public static class AutoQueueItemState
{
    public static AutoQueueItemStatus FromDownload(DownloadStatus status, AutoQueueItemStatus previous) => status switch
    {
        DownloadStatus.Preparing or DownloadStatus.Downloading or DownloadStatus.Muxing => AutoQueueItemStatus.Downloading,
        DownloadStatus.Completed => AutoQueueItemStatus.Completed,
        DownloadStatus.Failed => AutoQueueItemStatus.Failed,
        DownloadStatus.Cancelled or DownloadStatus.Removed => AutoQueueItemStatus.Cancelled,
        DownloadStatus.Pending or DownloadStatus.Paused => AutoQueueItemStatus.Enqueued,
        _ => previous
    };
}
