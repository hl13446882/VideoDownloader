using System.Diagnostics;
using VideoDownloader.Core.Errors;
using VideoDownloader.Infrastructure.Download;

namespace VideoDownloader.Infrastructure.Tests;

public class ProcessProgressTests
{
    [Fact]
    public async Task StalledProcess_IsKilledAndReportsTimeout()
    {
        using var process = StartSleeper();
        var error = await Assert.ThrowsAsync<DownloadException>(() => ProcessProgress.WaitForExitAsync(process, () => 0,
            TimeSpan.FromMilliseconds(150), default, TimeSpan.FromMilliseconds(30)));
        Assert.Equal(ErrorCodes.NetTimeout, error.ErrorCode);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task Cancellation_KillsChildAndRemainsCancellation()
    {
        using var process = StartSleeper();
        using var ct = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessProgress.WaitForExitAsync(process, () => 0, TimeSpan.FromSeconds(30), ct.Token));
        Assert.True(process.HasExited);
    }

    private static Process StartSleeper()
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("Start-Sleep -Seconds 30");
        return Process.Start(start)!;
    }

    [Fact]
    public async Task ProgressReadFailure_DoesNotLeakProcessOrBecomeNetworkTimeout()
    {
        using var process = StartSleeper();
        await Assert.ThrowsAsync<IOException>(() => ProcessProgress.WaitForExitAsync(process,
            () => throw new IOException("test"), TimeSpan.FromSeconds(30), default));
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task AdvancingProcess_DoesNotHitIdleDeadline()
    {
        using var process = StartSleeper();
        var progress = 0L;
        using var ct = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessProgress.WaitForExitAsync(process,
            () => Interlocked.Increment(ref progress), TimeSpan.FromMilliseconds(100), ct.Token, TimeSpan.FromMilliseconds(20)));
        Assert.True(process.HasExited);
    }
}
