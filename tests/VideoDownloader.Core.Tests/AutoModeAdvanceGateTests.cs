using VideoDownloader.Core.Download;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Tests;

public sealed class AutoModeAdvanceGateTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData(DownloadStatus.Pending)]
    public void WaitingForCapacityNeverAdvancesAndResumesCurrentItem(DownloadStatus? status)
    {
        var gate = new AutoModeAdvanceGate(Start);
        for (var second = 0; second <= 600; second++)
            Assert.Equal(AutoModeAdvanceReason.None,
                gate.Observe(Start.AddSeconds(second), false, status, waitingForSlot: true));
        Assert.Equal(AutoModeAdvanceReason.None,
            gate.Observe(Start.AddSeconds(601), false, status));
        Assert.Equal(Start.AddSeconds(661), gate.AddressDeadline);
        Assert.Equal(AutoModeAdvanceReason.None,
            gate.Observe(Start.AddSeconds(602), false, DownloadStatus.Downloading));
        Assert.Equal(AutoModeAdvanceReason.None,
            gate.Observe(Start.AddSeconds(606.999), false, DownloadStatus.Downloading));
        Assert.Equal(AutoModeAdvanceReason.DownloadStarted,
            gate.Observe(Start.AddSeconds(607), false, DownloadStatus.Downloading));
    }

    [Fact]
    public void WaitingForCapacityPreservesRemainingAddressBudget()
    {
        var gate = new AutoModeAdvanceGate(Start);
        gate.Observe(Start.AddSeconds(20), false, null);
        gate.Observe(Start.AddSeconds(20), false, null, waitingForSlot: true);
        gate.Observe(Start.AddSeconds(120), false, null, waitingForSlot: true);
        Assert.Equal(AutoModeAdvanceReason.None, gate.Observe(Start.AddSeconds(120), false, null));
        Assert.Equal(AutoModeAdvanceReason.None, gate.Observe(Start.AddSeconds(159.999), false, null));
        Assert.Equal(AutoModeAdvanceReason.AddressTimeout, gate.Observe(Start.AddSeconds(160), false, null));
    }

    [Fact]
    public void DouyinLivePreviewWithPreviousVodKey_DoesNotWaitForSixtySecondTimeout()
    {
        const string staleVodKey = "content:7685230354555060058";
        Assert.True(AutoModeAdvanceGate.HasPlaybackChanged(staleVodKey, staleVodKey, false, true));
        var gate = new AutoModeAdvanceGate(Start);
        Assert.Equal(AutoModeAdvanceReason.Live, gate.Observe(Start.AddMilliseconds(200), true, null));
    }

    [Fact]
    public void PreviousLiveStillVisible_DoesNotConfirmSwitchOrSendAnotherNext()
    {
        Assert.False(AutoModeAdvanceGate.HasPlaybackChanged("same", "same", true, true));
    }

    [Fact]
    public void LiveToVodWithUnchangedMediaKey_ReleasesSwitchWait()
    {
        Assert.True(AutoModeAdvanceGate.HasPlaybackChanged("same", "same", true, false));
    }

    [Fact]
    public void SlowVodDetector_DoesNotConfirmSwitchPrematurely()
    {
        Assert.False(AutoModeAdvanceGate.HasPlaybackChanged("same", null, false, false));
        Assert.False(AutoModeAdvanceGate.HasPlaybackChanged("same", "same", false, false));
        Assert.True(AutoModeAdvanceGate.HasPlaybackChanged("same", "new", false, false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(59.999)]
    public void MissingAddressOrStaleResult_DoesNotPermitEarlyAdvance(double seconds)
    {
        var gate = new AutoModeAdvanceGate(Start);
        Assert.Equal(AutoModeAdvanceReason.None, gate.Observe(Start.AddSeconds(seconds), false, null));
    }

    [Theory]
    [InlineData(DownloadStatus.Pending)]
    [InlineData(DownloadStatus.Preparing)]
    [InlineData(DownloadStatus.Failed)]
    [InlineData(DownloadStatus.Cancelled)]
    public void JobNotStarted_WaitsForFullSixtySeconds(DownloadStatus status)
    {
        var gate = new AutoModeAdvanceGate(Start);
        Assert.Equal(AutoModeAdvanceReason.None, gate.Observe(Start.AddSeconds(5), false, status));
        Assert.Equal(AutoModeAdvanceReason.None, gate.Observe(Start.AddSeconds(59.999), false, status));
        Assert.Equal(AutoModeAdvanceReason.AddressTimeout, gate.Observe(Start.AddSeconds(60), false, status));
    }

    [Fact]
    public void LivePlayback_AdvancesImmediately()
    {
        var gate = new AutoModeAdvanceGate(Start);
        Assert.Equal(AutoModeAdvanceReason.Live, gate.Observe(Start, true, null));
    }

    [Fact]
    public void TransferStart_UsesFiveSecondsAfterStartRatherThanEnqueueOrPreparing()
    {
        var gate = new AutoModeAdvanceGate(Start);
        Assert.Equal(AutoModeAdvanceReason.None, gate.Observe(Start.AddSeconds(1), false, DownloadStatus.Pending));
        Assert.Equal(AutoModeAdvanceReason.None, gate.Observe(Start.AddSeconds(8), false, DownloadStatus.Preparing));
        Assert.Equal(AutoModeAdvanceReason.None, gate.Observe(Start.AddSeconds(12), false, DownloadStatus.Downloading));
        Assert.Equal(AutoModeAdvanceReason.None, gate.Observe(Start.AddSeconds(15), false, DownloadStatus.Downloading));
        Assert.Equal(AutoModeAdvanceReason.None, gate.Observe(Start.AddSeconds(16.999), false, DownloadStatus.Muxing));
        Assert.Equal(AutoModeAdvanceReason.DownloadStarted, gate.Observe(Start.AddSeconds(17), false, DownloadStatus.Completed));
    }

    [Fact]
    public void TransferStartsNearDeadline_StillReceivesFullFiveSecondDelay()
    {
        var gate = new AutoModeAdvanceGate(Start);
        gate.Observe(Start.AddSeconds(58), false, DownloadStatus.Downloading);
        Assert.Equal(AutoModeAdvanceReason.None, gate.Observe(Start.AddSeconds(60), false, DownloadStatus.Downloading));
        Assert.Equal(AutoModeAdvanceReason.DownloadStarted, gate.Observe(Start.AddSeconds(63), false, DownloadStatus.Downloading));
    }

    [Fact]
    public void FastDownloadCompletion_DoesNotLoseStartOrRestartDelay()
    {
        var gate = new AutoModeAdvanceGate(Start);
        gate.Observe(Start.AddSeconds(2), false, DownloadStatus.Completed);
        Assert.Equal(AutoModeAdvanceReason.None, gate.Observe(Start.AddSeconds(6), false, DownloadStatus.Completed));
        Assert.Equal(AutoModeAdvanceReason.DownloadStarted, gate.Observe(Start.AddSeconds(7), false, DownloadStatus.Completed));
    }

    [Fact]
    public void RepeatedObservations_DoNotResetAddressDeadline()
    {
        var gate = new AutoModeAdvanceGate(Start);
        for (var second = 0; second < 60; second++)
            Assert.Equal(AutoModeAdvanceReason.None, gate.Observe(Start.AddSeconds(second), false, null));
        Assert.Equal(AutoModeAdvanceReason.AddressTimeout, gate.Observe(Start.AddSeconds(60), false, null));
    }

    [Fact]
    public void ExistingCompletedDuplicate_DoesNotPretendToStartANewDownload()
    {
        var gate = new AutoModeAdvanceGate(Start);
        Assert.Equal(AutoModeAdvanceReason.None, gate.Observe(Start, false, DownloadStatus.Completed, true));
        Assert.Equal(AutoModeAdvanceReason.None, gate.Observe(Start.AddSeconds(5), false, DownloadStatus.Completed, true));
        Assert.Equal(AutoModeAdvanceReason.AddressTimeout, gate.Observe(Start.AddSeconds(60), false, DownloadStatus.Completed, true));
    }
}
