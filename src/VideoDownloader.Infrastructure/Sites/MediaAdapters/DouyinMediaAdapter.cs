using System.Text.RegularExpressions;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;

namespace VideoDownloader.Infrastructure.Sites.MediaAdapters;

/// <summary>Douyin-only network admission and page identity. Separate from TikTok.</summary>
public sealed class DouyinMediaAdapter : ISiteMediaAdapter
{
    private static readonly Regex VideoIdPath = new(
        @"/(?:video|note|share/video|share/note)/(?<id>\d{10,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Name => "douyin";

    public bool Matches(Uri pageUri) =>
        pageUri.Host.Contains("douyin.com", StringComparison.OrdinalIgnoreCase) ||
        pageUri.Host.Contains("iesdouyin.com", StringComparison.OrdinalIgnoreCase);

    public string? ResolveContentIdentity(PageMediaContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.ObservedIdentity) &&
            context.ObservedIdentity.Contains("content:", StringComparison.OrdinalIgnoreCase))
            return NormalizeIdentity(context.ObservedIdentity);

        var id = ExtractVideoId(context.PageUrl) ?? ExtractIdFromQuery(context.PageUrl);
        return id is null ? context.ObservedIdentity : "content:douyin:" + id;
    }

    public NetworkCandidateDecision EvaluateNetworkCandidate(
        NormalizedNetworkEvent networkEvent,
        PageMediaContext context)
    {
        if (networkEvent.StatusCode is not (200 or 206 or null))
            return new(NetworkCandidateDecisionKind.Default, Name, "bad_status");

        // MSE Range windows are not downloadable progressive objects.
        if (UnifiedMediaPipeline.IsInsufficientByteDanceDownloadObject(
                networkEvent.Url, networkEvent.ContentLength))
            return new(NetworkCandidateDecisionKind.Reject, Name, "tiny_mse_slice");

        if (UnifiedMediaPipeline.IsBrowserPlayEvidence(networkEvent))
        {
            return new(
                NetworkCandidateDecisionKind.StrongAccept,
                Name,
                "cdp_play_200_206",
                MediaEvidence.BrowserObserved);
        }

        if (!SiteNetworkHelper.IsDouyinHost(networkEvent.Url) &&
            !LooksLikeDouyinPlay(networkEvent.Url))
            return new(NetworkCandidateDecisionKind.Default, Name, "defer_generic");

        if (IsStrongMime(networkEvent.MimeType) ||
            string.Equals(networkEvent.ResourceType, "Media", StringComparison.OrdinalIgnoreCase) ||
            LooksLikeDouyinPlay(networkEvent.Url))
        {
            var strong = IsStrongMime(networkEvent.MimeType) ||
                         string.Equals(networkEvent.ResourceType, "Media", StringComparison.OrdinalIgnoreCase) ||
                         LooksLikeDouyinPlay(networkEvent.Url);
            return new(
                strong ? NetworkCandidateDecisionKind.StrongAccept : NetworkCandidateDecisionKind.Accept,
                Name,
                strong ? "douyin_cdn_media" : "douyin_cdn_hint",
                MediaEvidence.Heuristic);
        }

        return new(NetworkCandidateDecisionKind.Default, Name, "defer_generic");
    }

    public NetworkCandidateDecision EvaluateDomCandidate(
        Uri candidateUrl,
        string? mimeHint,
        PageMediaContext context)
    {
        if (SiteNetworkHelper.IsDouyinHost(candidateUrl) || LooksLikeDouyinPlay(candidateUrl))
            return new(NetworkCandidateDecisionKind.Accept, Name, "douyin_dom", MediaEvidence.DomObserved);
        return new(NetworkCandidateDecisionKind.Default, Name);
    }

    public RequestContext EnrichRequestContext(
        RequestContext requestContext,
        Uri? mediaUrl,
        PageMediaContext context)
    {
        var referer = string.IsNullOrWhiteSpace(requestContext.Referer)
            ? context.PageUrl.AbsoluteUri
            : requestContext.Referer;
        var origin = string.IsNullOrWhiteSpace(requestContext.Origin)
            ? context.PageUrl.GetLeftPart(UriPartial.Authority)
            : requestContext.Origin;
        return requestContext with { Referer = referer, Origin = origin };
    }

    public ValidationPolicy GetValidationPolicy(
        Uri mediaUrl,
        bool browserObserved,
        PageMediaContext context) =>
        browserObserved
            ? new(true, true, "douyin_browser_observed")
            : new(PreferBrowserObservedUrl: true, Reason: "douyin_prefer_browser");

    public ExternalResolvePolicy GetExternalResolvePolicy(PageMediaContext context) =>
        new(true, PreferBrowserOverExternal: true, Reason: "douyin_browser_first");

    public Uri? CanonicalizeExternalPageUrl(Uri pageUrl, string? observedIdentity)
    {
        var id = ExtractVideoId(pageUrl) ?? ExtractIdFromQuery(pageUrl) ?? ExtractIdFromIdentity(observedIdentity);
        if (id is null)
            return null;

        var path = pageUrl.AbsolutePath.TrimEnd('/');
        var isFeedRoot = path is "" or "/" or "/recommend" ||
                         path.Equals("/jingxuan", StringComparison.OrdinalIgnoreCase);
        var isNote = path.Contains("/note/", StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith("/note/", StringComparison.OrdinalIgnoreCase);
        if (!isFeedRoot && ExtractVideoId(pageUrl) is not null)
            return new Uri(isNote ? $"https://www.douyin.com/note/{id}" : $"https://www.douyin.com/video/{id}");
        if (!isFeedRoot)
            return null;

        return new Uri(isNote ? $"https://www.douyin.com/note/{id}" : $"https://www.douyin.com/video/{id}");
    }

    public int ScoreCandidate(Uri mediaUrl, bool browserObserved, PageMediaContext context) =>
        browserObserved ? 100 : SiteNetworkHelper.IsDouyinHost(mediaUrl) ? 75 : 50;

    private static string NormalizeIdentity(string identity)
    {
        if (identity.StartsWith("content:douyin:", StringComparison.OrdinalIgnoreCase))
            return identity;
        var id = ExtractIdFromIdentity(identity);
        return id is null ? identity : "content:douyin:" + id;
    }

    private static string? ExtractIdFromIdentity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return null;
        var m = Regex.Match(identity, @"(?:content:(?:douyin:)?)?(?<id>\d{10,})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["id"].Value : null;
    }

    private static string? ExtractVideoId(Uri pageUrl)
    {
        var m = VideoIdPath.Match(pageUrl.AbsolutePath);
        return m.Success ? m.Groups["id"].Value : null;
    }

    private static string? ExtractIdFromQuery(Uri pageUrl)
    {
        var q = pageUrl.Query;
        foreach (var key in new[] { "modal_id=", "aweme_id=", "item_id=", "video_id=" })
        {
            var idx = q.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var start = idx + key.Length;
            var end = q.IndexOf('&', start);
            var raw = end < 0 ? q[start..] : q[start..end];
            if (Regex.IsMatch(raw, @"^\d{10,}$"))
                return raw;
        }
        return null;
    }

    private static bool LooksLikeDouyinPlay(Uri url)
    {
        var full = url.AbsoluteUri;
        return full.Contains("playAddr", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("play_addr", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("downloadAddr", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("/aweme/", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("video_id=", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("/play/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStrongMime(string? mime) =>
        mime is not null &&
        (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
         mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));
}
