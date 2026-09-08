using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Download;

public sealed class DownloadJobStateMachine : IDownloadJobStateMachine
{
    private static readonly Dictionary<DownloadStatus, HashSet<DownloadStatus>> Allowed = new()
    {
        [DownloadStatus.Pending] = [DownloadStatus.Preparing, DownloadStatus.Cancelled],
        [DownloadStatus.Preparing] = [DownloadStatus.Downloading, DownloadStatus.Failed, DownloadStatus.Cancelled],
        [DownloadStatus.Downloading] = [DownloadStatus.Paused, DownloadStatus.Muxing, DownloadStatus.Completed, DownloadStatus.Failed, DownloadStatus.Cancelled],
        [DownloadStatus.Paused] = [DownloadStatus.Downloading, DownloadStatus.Cancelled, DownloadStatus.Failed],
        [DownloadStatus.Muxing] = [DownloadStatus.Completed, DownloadStatus.Failed, DownloadStatus.Cancelled],
        [DownloadStatus.Completed] = [],
        [DownloadStatus.Failed] = [DownloadStatus.Pending, DownloadStatus.Preparing],
        [DownloadStatus.Cancelled] = []
    };

    public void StartPreparing(DownloadJob job) => Transition(job, DownloadStatus.Preparing);
    public void StartDownloading(DownloadJob job) => Transition(job, DownloadStatus.Downloading);
    public void Pause(DownloadJob job) => Transition(job, DownloadStatus.Paused);
    public void Resume(DownloadJob job) => Transition(job, DownloadStatus.Downloading);
    public void StartMuxing(DownloadJob job) => Transition(job, DownloadStatus.Muxing);
    public void Complete(DownloadJob job) => Transition(job, DownloadStatus.Completed);
    public void Fail(DownloadJob job, string errorCode)
    {
        job.LastErrorCode = errorCode;
        Transition(job, DownloadStatus.Failed);
    }

    public void Cancel(DownloadJob job)
    {
        job.LastErrorCode = ErrorCodes.Cancelled;
        Transition(job, DownloadStatus.Cancelled);
    }

    private static void Transition(DownloadJob job, DownloadStatus target)
    {
        if (!Allowed.TryGetValue(job.Status, out var allowed) || !allowed.Contains(target))
            throw new InvalidOperationException($"Invalid transition from {job.Status} to {target} for job {job.Id}");

        job.Status = target;
        // Queue order uses the first UpdatedAt; do not refresh it on Preparing/Downloading/Muxing/Pause.
        if (target is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled)
            job.UpdatedAt = DateTimeOffset.UtcNow;
    }
}