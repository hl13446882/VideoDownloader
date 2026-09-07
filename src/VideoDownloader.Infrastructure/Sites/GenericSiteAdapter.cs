using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Sites;

public sealed class GenericSiteAdapter : ISiteAdapter
{
    public string SiteId => SiteIds.Generic;
    public int Priority => 0;

    public bool CanHandle(Uri pageUrl) => true;

    public Task<SiteProbeResult> ProbeAsync(SiteProbeContext context, CancellationToken ct)
    {
        var videos = context.GenericVideos
            .Select(v => v with { SiteId = SiteIds.Generic, ProbeSource = ProbeSource.Generic })
            .ToList();

        return Task.FromResult(new SiteProbeResult(
            videos.Count > 0 ? SiteProbeStatus.Success : SiteProbeStatus.NotApplicable,
            SiteIds.Generic,
            videos,
            AllowGenericFallback: true,
            ErrorCode: null));
    }
}
