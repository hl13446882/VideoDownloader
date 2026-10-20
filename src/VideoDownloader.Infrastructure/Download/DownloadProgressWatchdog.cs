using System.Diagnostics;
using VideoDownloader.Core.Errors;

namespace VideoDownloader.Infrastructure.Download;

internal static class DownloadProgressWatchdog
{
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(1);

    internal static async Task RunAsync(Func<CancellationToken, Task> transfer, Func<long> bytes,
        Func<bool> monitor, TimeSpan idleTimeout, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var last = bytes();
        var clock = Stopwatch.StartNew();
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(idleTimeout.TotalMilliseconds / 4, 10, 1000));
        async Task Watch()
        {
            while (true)
            {
                await Task.Delay(interval, observation.Token);
                var current = bytes();
                if (!monitor() || current != last) { clock.Restart(); last = current; }
                if (clock.Elapsed >= idleTimeout) return;
            }
        }
        var operation = transfer(stop.Token);
        var stalled = Watch();
        try
        {
            if (await Task.WhenAny(operation, stalled) == stalled && !ct.IsCancellationRequested)
            {
                await stalled;
                stop.Cancel();
                // Join all cancelled requests before retrying; two runs must never write the same parts.
                try { await operation; } catch (Exception) when (!ct.IsCancellationRequested) { }
                throw new DownloadException(ErrorCodes.NoProgress, "Download transferred no bytes for one minute.");
            }
            await operation;
        }
        finally
        {
            observation.Cancel();
            try { await stalled; } catch (OperationCanceledException) { }
        }
    }
}
