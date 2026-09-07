using System.Collections.Concurrent;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Sites;

public sealed class SiteProbeOrchestrator : ISiteProbeOrchestrator
{
    private readonly ISiteAdapterRouter _router;
    private readonly ConcurrentQueue<NormalizedNetworkEvent> _recentEvents = new();
    private readonly ConcurrentDictionary<Guid, DetectedVideo> _genericVideos = new();
    private const int MaxEvents = 512;

    public SiteProbeOrchestrator(ISiteAdapterRouter router) => _router = router;

    public void RecordNetworkEvent(NormalizedNetworkEvent normalized)
    {
        _recentEvents.Enqueue(normalized);
        while (_recentEvents.Count > MaxEvents && _recentEvents.TryDequeue(out _))
        {
        }
    }

    public void RecordGenericVideo(DetectedVideo video)
    {
        var filtered = MediaResourceSizeFilter.FilterForDisplay(video);
        if (filtered.Variants.Count == 0)
            return;

        _genericVideos[video.VideoId] = filtered with
        {
            SiteId = SiteIds.Generic,
            ProbeSource = ProbeSource.Generic
        };
    }

    public async Task<IReadOnlyList<DetectedVideo>> ProbePageAsync(
        Uri pageUrl,
        string? pageTitle,
        string? pageScriptJson,
        RequestContext context,
        CancellationToken ct)
    {
        var events = _recentEvents.ToArray();
        var generic = _genericVideos.Values.ToList();

        var contextProbe = new SiteProbeContext(
            pageUrl,
            pageTitle,
            pageScriptJson,
            [],
            generic,
            events,
            context);

        return await _router.ProbeAsync(contextProbe, ct);
    }

    public void Clear()
    {
        _recentEvents.Clear();
        _genericVideos.Clear();
    }
}
