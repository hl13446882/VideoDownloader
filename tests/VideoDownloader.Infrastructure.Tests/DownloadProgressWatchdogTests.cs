using VideoDownloader.Core.Errors;
using VideoDownloader.Infrastructure.Download;

namespace VideoDownloader.Infrastructure.Tests;

public class DownloadProgressWatchdogTests
{
    [Fact]
    public async Task StalledTransferIsCancelledAndJoinedBeforeTimeoutIsReturned()
    {
        var stopped = false;
        var error = await Assert.ThrowsAsync<DownloadException>(() => DownloadProgressWatchdog.RunAsync(async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { stopped = true; }
        }, () => 0, () => true, TimeSpan.FromMilliseconds(80), default));
        Assert.True(stopped);
        Assert.Equal(ErrorCodes.NoProgress, error.ErrorCode);
    }

    [Fact]
    public async Task AdvancingTransferResetsIdleDeadline()
    {
        long bytes = 0;
        await DownloadProgressWatchdog.RunAsync(async ct =>
        {
            for (var i = 0; i < 15; i++)
            {
                await Task.Delay(100, ct);
                Interlocked.Add(ref bytes, 10);
            }
        }, () => Interlocked.Read(ref bytes), () => true, TimeSpan.FromSeconds(1), default);
        Assert.Equal(150, bytes);
    }

    [Fact]
    public async Task MuxingIsNotMistakenForStalledNetworkTransfer()
    {
        await DownloadProgressWatchdog.RunAsync(ct => Task.Delay(180, ct),
            () => 0, () => false, TimeSpan.FromMilliseconds(60), default);
    }

    [Fact]
    public async Task UserCancellationStaysCancellationRatherThanBecomingIdleFailure()
    {
        using var user = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadProgressWatchdog.RunAsync(
            ct => Task.Delay(Timeout.Infinite, ct), () => 0, () => true, TimeSpan.FromSeconds(10), user.Token));
    }

    [Fact]
    public async Task ReopenedRequestCanExpose403ForExistingRenewalPipeline()
    {
        await Assert.ThrowsAsync<DownloadException>(() => DownloadProgressWatchdog.RunAsync(
            ct => Task.Delay(Timeout.Infinite, ct), () => 10, () => true, TimeSpan.FromMilliseconds(60), default));
        var error = await Assert.ThrowsAsync<DownloadException>(() => DownloadProgressWatchdog.RunAsync(
            _ => Task.FromException(new DownloadException(ErrorCodes.Http403, "Expired URL")),
            () => 10, () => true, TimeSpan.FromSeconds(1), default));
        Assert.Equal(ErrorCodes.Http403, error.ErrorCode);
    }
}
