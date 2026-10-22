using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Sites;

public sealed class SiteDetectionRouter : ISiteDetectionRouter
{
    public SiteKind Resolve(Uri pageUrl)
    {
        if (SiteHosts.MatchesAny(pageUrl, "douyin.com", "iesdouyin.com"))
            return SiteKind.Douyin;

        if (SiteHosts.Matches(pageUrl, "tiktok.com"))
            return SiteKind.TikTok;

        if (SiteHosts.MatchesAny(pageUrl, "youtube.com", "youtu.be", "youtube-nocookie.com"))
            return SiteKind.YouTube;

        if (SiteHosts.MatchesAny(pageUrl, "bilibili.com", "b23.tv"))
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
