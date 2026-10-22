using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Download;

public enum AutoModeAdvanceReason { None, Live, AddressTimeout, DownloadStarted }

/// <summary>The only three reasons automatic playback may advance to the next item.</summary>
public sealed class AutoModeAdvanceGate(DateTimeOffset itemStartedAt)
{
    public static readonly TimeSpan AddressTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DownloadSwitchDelay = TimeSpan.FromSeconds(5);

    public DateTimeOffset AddressDeadline { get; private set; } = itemStartedAt + AddressTimeout;
    public DateTimeOffset? DownloadStartedAt { get; private set; }
    private DateTimeOffset _lastObservedAt = itemStartedAt;
    private bool _wasWaiting;

    // Douyin live previews can retain the preceding VOD media key. A live/VOD transition
    // must release the switch wait even when that key has not changed.
    public static bool HasPlaybackChanged(string? previousIdentity, string? currentIdentity,
        bool previousWasLive, bool currentIsLive) =>
        previousWasLive != currentIsLive ||
        (!string.IsNullOrEmpty(currentIdentity) &&
         !string.Equals(previousIdentity, currentIdentity, StringComparison.Ordinal));

    public AutoModeAdvanceReason Observe(DateTimeOffset now, bool isLive, DownloadStatus? jobStatus,
        bool wasAlreadyCompleted = false, bool waitingForSlot = false, bool waitingForDetection = false)
    {
        if (isLive)
            return AutoModeAdvanceReason.Live;

        var waiting = waitingForSlot || waitingForDetection;
        // Discovery has its own budget; the auto deadline must not cut it short.
        if (DownloadStartedAt is null && (waiting || _wasWaiting) && now > _lastObservedAt)
            AddressDeadline += now - _lastObservedAt;
        _lastObservedAt = now;
        _wasWaiting = waiting;

        // Preparing and Pending have not started transferring media yet.
        if (DownloadStartedAt is null && !wasAlreadyCompleted &&
            jobStatus is DownloadStatus.Downloading or DownloadStatus.Muxing or DownloadStatus.Completed)
            DownloadStartedAt = now;

        if (DownloadStartedAt is { } started)
            return now - started >= DownloadSwitchDelay
                ? AutoModeAdvanceReason.DownloadStarted
                : AutoModeAdvanceReason.None;

        return !waiting && now >= AddressDeadline
            ? AutoModeAdvanceReason.AddressTimeout : AutoModeAdvanceReason.None;
    }
}
