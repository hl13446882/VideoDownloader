using System.Text.RegularExpressions;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;

namespace VideoDownloader.Infrastructure.Sites.MediaAdapters;

/// <summary>
/// TikTok discovery / admission / refresh hints. Does not download.
/// </summary>
public sealed class TikTokMediaAdapter : ISiteMediaAdapter
{
    private static readonly Regex VideoIdPath = new(
        @"/@[^/]+/video/(?<id>\d{10,})|/video/(?<id>\d{10,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Name => "tiktok";

    public bool Matches(Uri pageUri) =>
        pageUri.Host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase);

    public string? ResolveContentIdentity(PageMediaContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.ObservedIdentity) &&
            context.ObservedIdentity.StartsWith("content:", StringComparison.OrdinalIgnoreCase))
            return NormalizeIdentity(context.ObservedIdentity);

        var fromPage = ExtractVideoId(context.PageUrl);
        return fromPage is null ? context.ObservedIdentity : "content:tiktok:" + fromPage;
    }

    public NetworkCandidateDecision EvaluateNetworkCandidate(
        NormalizedNetworkEvent networkEvent,
        PageMediaContext context)
    {
        if (networkEvent.StatusCode is not (200 or 206 or null))
            return new(NetworkCandidateDecisionKind.Default, Name, "bad_status");

        // Browser already delivered media bytes — strongest TikTok evidence.
        if (UnifiedMediaPipeline.IsBrowserPlayEvidence(networkEvent))
        {
            return new(
                NetworkCandidateDecisionKind.StrongAccept,
                Name,
                "cdp_play_200_206",
                MediaEvidence.BrowserObserved);
        }

        // Signed CDN play endpoints without neat extensions.
        if (IsTikTokMediaHost(networkEvent.Url) &&
            (LooksLikePlayPayload(networkEvent.Url) ||
             IsStrongMime(networkEvent.MimeType) ||
             string.Equals(networkEvent.ResourceType, "Media", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(networkEvent.ResourceType, "Fetch", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(networkEvent.ResourceType, "XHR", StringComparison.OrdinalIgnoreCase)))
        {
            if (IsStrongMime(networkEvent.MimeType) ||
                string.Equals(networkEvent.ResourceType, "Media", StringComparison.OrdinalIgnoreCase))
            {
                return new(
                    NetworkCandidateDecisionKind.StrongAccept,
                    Name,
                    "tiktok_cdn_media",
                    MediaEvidence.Heuristic);
            }

            return new(
                NetworkCandidateDecisionKind.Accept,
                Name,
                "tiktok_cdn_hint",
                MediaEvidence.Heuristic);
        }

        return new(NetworkCandidateDecisionKind.Default, Name, "defer_generic");
    }

    public NetworkCandidateDecision EvaluateDomCandidate(
        Uri candidateUrl,
        string? mimeHint,
        PageMediaContext context)
    {
        if (IsTikTokMediaHost(candidateUrl) || LooksLikePlayPayload(candidateUrl))
            return new(NetworkCandidateDecisionKind.Accept, Name, "tiktok_dom", MediaEvidence.DomObserved);
        return new(NetworkCandidateDecisionKind.Default, Name);
    }

    public RequestContext EnrichRequestContext(
        RequestContext requestContext,
        Uri? mediaUrl,
        PageMediaContext context)
    {
        var referer = requestContext.Referer;
        var origin = requestContext.Origin;
        var changed = false;

        if (string.IsNullOrWhiteSpace(referer))
        {
            referer = context.PageUrl.AbsoluteUri;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(origin))
        {
            origin = context.PageUrl.GetLeftPart(UriPartial.Authority);
            changed = true;
        }

        return changed
            ? requestContext with { Referer = referer, Origin = origin }
            : requestContext;
    }

    public ValidationPolicy GetValidationPolicy(
        Uri mediaUrl,
        bool browserObserved,
        PageMediaContext context)
    {
        if (browserObserved)
        {
            return new ValidationPolicy(
                SkipIndependentHttpSample: true,
                PreferBrowserObservedUrl: true,
                Reason: "tiktok_browser_observed");
        }

        // Independent GETs often 403 on TikTok CDN without the live WebView2 jar.
        return new ValidationPolicy(
            SkipIndependentHttpSample: false,
            PreferBrowserObservedUrl: true,
            Reason: "tiktok_prefer_browser");
    }

    public ExternalResolvePolicy GetExternalResolvePolicy(PageMediaContext context) =>
        new(AllowExternalResolve: true, PreferBrowserOverExternal: true, Reason: "tiktok_browser_first");

    public Uri? CanonicalizeExternalPageUrl(Uri pageUrl, string? observedIdentity)
    {
        var id = ExtractVideoId(pageUrl) ?? ExtractIdFromIdentity(observedIdentity);
        if (id is null)
            return null;

        var path = pageUrl.AbsolutePath.TrimEnd('/');
        var isFeedRoot = path is "" or "/" or "/recommend" ||
                         path.Equals("/foryou", StringComparison.OrdinalIgnoreCase) ||
                         path.Equals("/jingxuan", StringComparison.OrdinalIgnoreCase);
        if (!isFeedRoot && ExtractVideoId(pageUrl) is not null)
            return pageUrl;

        if (!isFeedRoot)
            return null;

        return new Uri($"https://www.tiktok.com/@i/video/{id}");
    }

    public int ScoreCandidate(Uri mediaUrl, bool browserObserved, PageMediaContext context)
    {
        if (browserObserved) return 100;
        if (IsTikTokMediaHost(mediaUrl)) return 70;
        return 50;
    }

    private static string? NormalizeIdentity(string identity)
    {
        if (identity.StartsWith("content:tiktok:", StringComparison.OrdinalIgnoreCase))
            return identity;
        var id = ExtractIdFromIdentity(identity);
        return id is null ? identity : "content:tiktok:" + id;
    }

    private static string? ExtractIdFromIdentity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return null;
        var m = Regex.Match(identity, @"(?:content:(?:tiktok:)?)?(?<id>\d{10,})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["id"].Value : null;
    }

    internal static string? ExtractVideoId(Uri pageUrl)
    {
        var m = VideoIdPath.Match(pageUrl.AbsolutePath);
        return m.Success ? m.Groups["id"].Value : null;
    }

    private static bool IsTikTokMediaHost(Uri url) =>
        url.Host.Contains("tiktok", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("tiktokv", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("byteoversea", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("ibyteimg", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikePlayPayload(Uri url)
    {
        var full = url.AbsoluteUri;
        return full.Contains("playAddr", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("play_addr", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("/aweme/v1/play", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("/video/tos/", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("mime_type=video", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStrongMime(string? mime) =>
        mime is not null &&
        (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
         mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));
}
