using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Sites;

public sealed class SiteDetectionRouter : ISiteDetectionRouter
{
    public SiteKind Resolve(Uri pageUrl)
    {
        var host = pageUrl.Host;
        if (host.Contains("douyin.com", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("iesdouyin.com", StringComparison.OrdinalIgnoreCase))
            return SiteKind.Douyin;

        if (host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase))
            return SiteKind.TikTok;

        if (host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase))
            return SiteKind.YouTube;

        if (host.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("b23.tv", StringComparison.OrdinalIgnoreCase))
            return SiteKind.Bilibili;

        return SiteKind.Other;
    }

    public string Describe(SiteKind kind) => kind switch
    {
        SiteKind.Douyin => "Douyin",
        SiteKind.TikTok => "TikTok",
        SiteKind.YouTube => "YouTube",
        SiteKind.Bilibili => "Bilibili",
        _ => "Other"
    };
}

public sealed class ExclusiveSiteMediaDetectorResolver : IExclusiveSiteMediaDetectorResolver
{
    private readonly IReadOnlyList<IExclusiveSiteMediaDetector> _detectors;

    public ExclusiveSiteMediaDetectorResolver(IEnumerable<IExclusiveSiteMediaDetector> detectors)
    {
        _detectors = (detectors ?? []).ToArray();
    }

    public IReadOnlyList<IExclusiveSiteMediaDetector> All => _detectors;

    public IExclusiveSiteMediaDetector? Resolve(Uri pageUrl)
    {
        foreach (var detector in _detectors)
        {
            if (detector.Matches(pageUrl))
                return detector;
        }

        return null;
    }
}
