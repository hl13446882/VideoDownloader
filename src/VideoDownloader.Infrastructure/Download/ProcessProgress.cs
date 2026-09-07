using System.Diagnostics;
using VideoDownloader.Core.Errors;

namespace VideoDownloader.Infrastructure.Download;

internal static class ProcessProgress
{
    internal static async Task WaitForExitAsync(Process process, Func<long> progress,
        TimeSpan idleTimeout, CancellationToken ct, TimeSpan? pollInterval = null)
    {
        try
        {
            var last = progress();
            var idle = Stopwatch.StartNew();
            while (!process.HasExited)
            {
                ct.ThrowIfCancellationRequested();
                var current = progress();
                if (current != last) { last = current; idle.Restart(); }
                if (idle.Elapsed >= idleTimeout)
                    throw new DownloadException(ErrorCodes.NetTimeout, "Media process made no progress before the timeout.");
                await Task.Delay(pollInterval ?? TimeSpan.FromMilliseconds(300), ct);
            }
            await process.WaitForExitAsync(ct);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(cleanup.Token); }
            catch (OperationCanceledException) { }
            throw;
        }
    }

    internal static long FileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
    }
}
