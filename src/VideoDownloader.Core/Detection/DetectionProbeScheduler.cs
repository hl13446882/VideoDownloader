using System.Diagnostics;

namespace VideoDownloader.Core.Detection;

/// <summary>One external extraction alongside lightweight browser observations, per page session.</summary>
public static class DetectionProbeScheduler
{
    public static async Task RunAsync(
        Func<CancellationToken, Task> observe,
        Func<CancellationToken, Task>? extract,
        Func<bool> hasUsableResult,
        Func<bool> shouldStop,
        TimeSpan externalDelay,
        TimeSpan budget,
        bool enrichFormats,
        CancellationToken ct,
        TimeSpan? pollInterval = null,
        TimeSpan? trailingObservation = null)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        run.CancelAfter(budget);
        var token = run.Token;
        var watch = Stopwatch.StartNew();
        Task? external = null;
        TimeSpan? finishedAt = null;
        var poll = pollInterval ?? TimeSpan.FromMilliseconds(400);
        var trailing = trailingObservation ?? TimeSpan.FromSeconds(3);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (shouldStop() || hasUsableResult()) break;
                await observe(token);
                if (shouldStop()) break;
                var ready = hasUsableResult();
                if (ready) break;

                if (extract is not null && external is null &&
                    (ready || watch.Elapsed >= externalDelay))
                    external = extract(token);

                if (external?.IsCompleted == true)
                {
                    await external;
                    finishedAt ??= watch.Elapsed;
                    if (hasUsableResult() || watch.Elapsed - finishedAt >= trailing) break;
                }
                await Task.Delay(poll, token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && run.IsCancellationRequested)
        {
            // Exhausting this page's budget is not cancellation of the next page session.
        }
        finally
        {
            // Join the old process before letting the caller finish this session.
            run.Cancel();
            if (external is not null)
            {
                try { await external; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
        }
        ct.ThrowIfCancellationRequested();
    }
}
