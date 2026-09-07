using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Download;

public static class DownloadRecoveryRules
{
    public static DownloadStatus ResolveStartupStatus(DownloadStatus persisted)
    {
        return persisted switch
        {
            DownloadStatus.Pending => DownloadStatus.Pending,
            DownloadStatus.Preparing => DownloadStatus.Pending,
            DownloadStatus.Downloading => DownloadStatus.Paused,
            DownloadStatus.Muxing => DownloadStatus.Failed,
            DownloadStatus.Paused => DownloadStatus.Paused,
            DownloadStatus.Completed => DownloadStatus.Completed,
            DownloadStatus.Failed => DownloadStatus.Failed,
            DownloadStatus.Cancelled => DownloadStatus.Cancelled,
            DownloadStatus.Removed => DownloadStatus.Removed,
            _ => DownloadStatus.Failed
        };
    }

    public static string? ResolveStartupError(DownloadStatus persisted)
    {
        if (persisted == DownloadStatus.Muxing)
            return ErrorCodes.MuxInterrupted;

        return null;
    }
}
