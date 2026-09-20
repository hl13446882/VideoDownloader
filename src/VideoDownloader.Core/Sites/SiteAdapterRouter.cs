using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Sites;

public sealed class SiteAdapterRouter : ISiteAdapterRouter
{
    private readonly IReadOnlyList<ISiteAdapter> _adapters;
    private readonly ISiteAdapter _generic;
    private readonly ISiteEnablementPolicy _enablement;

    public SiteAdapterRouter(IEnumerable<ISiteAdapter> adapters, ISiteEnablementPolicy enablement)
    {
        _enablement = enablement;
        var list = (adapters ?? []).ToList();
        _generic = list.First(a => a.SiteId == SiteIds.Generic);
        _adapters = list.Where(a => a.SiteId != SiteIds.Generic).OrderByDescending(a => a.Priority).ToList();
    }

    public async Task<IReadOnlyList<DetectedVideo>> ProbeAsync(SiteProbeContext context, CancellationToken ct)
    {
        var genericFiltered = context.GenericVideos
            .Select(MediaResourceSizeFilter.FilterForDisplay)
            .Where(v => v.Variants.Count > 0)
            .Select(v => v with { SiteId = SiteIds.Generic, ProbeSource = ProbeSource.Generic })
            .ToList();

        if (!_enablement.PreferSiteAdapters)
            return genericFiltered;

        var adapter = _adapters.FirstOrDefault(a =>
            a.CanHandle(context.PageUrl) && _enablement.IsSiteEnabled(a.SiteId));
        if (adapter is null)
            return genericFiltered;

        var siteResult = await adapter.ProbeAsync(context, ct);
        if (siteResult.Status == SiteProbeStatus.Success && siteResult.Videos.Count > 0)
        {
            var siteVideos = siteResult.Videos
                .Select(MediaResourceSizeFilter.FilterForDisplay)
                .Where(v => v.Variants.Count > 0)
                .ToList();

            return MergeAndDeduplicate(siteVideos, genericFiltered);
        }

        if (siteResult.AllowGenericFallback && _enablement.FallbackToGeneric)
        {
            if (genericFiltered.Count > 0)
            {
                return genericFiltered
                    .Select(v => v with
                    {
                        ProbeSource = ProbeSource.GenericFallback,
                        StatusHint = $"站点适配未命中，已使用通用探测 ({siteResult.SiteId}) / Site adapter missed; using generic probe ({siteResult.SiteId})"
                    })
                    .ToList();
            }
        }

        return genericFiltered;
    }

    internal static IReadOnlyList<DetectedVideo> MergeAndDeduplicate(
        IReadOnlyList<DetectedVideo> siteVideos,
        IReadOnlyList<DetectedVideo> genericVideos)
    {
        var result = new List<DetectedVideo>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var video in siteVideos)
        {
            var key = BuildKey(video);
            if (keys.Add(key))
                result.Add(video);
        }

        foreach (var video in genericVideos)
        {
            var key = BuildKey(video);
            if (keys.Add(key))
                result.Add(video);
        }

        return result;
    }

    private static string BuildKey(DetectedVideo video)
    {
        var trackSig = string.Join("|", video.Variants.SelectMany(v =>
            v.Tracks.Select(t => t.SourceUrl.GetLeftPart(UriPartial.Path))));
        return $"{video.SiteContentId}:{video.PageUrl}:{trackSig}";
    }
}
