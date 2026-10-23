using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Detection;

/// <summary>Automatic consumption can wait for output, but cannot control the producer.</summary>
public sealed class PublishedProbeSubscription(IPageProbePublications publications)
{
    public async Task<PageProbeResult?> WaitAsync(DateTime deadlineUtc, CancellationToken ct)
    {
        while (DateTime.UtcNow < deadlineUtc)
        {
            ct.ThrowIfCancellationRequested();
            if (publications.Current is { } result) return result;
            await Task.Delay(200, ct);
        }
        ct.ThrowIfCancellationRequested();
        return null;
    }
}
