using System.Text.RegularExpressions;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;

namespace VideoDownloader.Infrastructure.Sites.MediaAdapters;

/// <summary>Obsolete shared-pipeline filter. Replaced by BilibiliMediaDetector.</summary>
[Obsolete("Use BilibiliMediaDetector via SiteDetectionRouter; not registered in DI.")]
public sealed class BilibiliMediaAdapter : ISiteMediaAdapter
{
    public string Name => "bilibili";

    public bool Matches(Uri pageUri) =>
        pageUri.Host.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
        pageUri.Host.Contains("b23.tv", StringComparison.OrdinalIgnoreCase);

    public string? ResolveContentIdentity(PageMediaContext context)
    {
        var id = SiteNetworkHelper.ExtractBilibiliContentId(context.PageUrl);
        if (id is not null)
            return "content:bilibili:" + id;

        if (!string.IsNullOrWhiteSpace(context.ObservedIdentity) &&
            context.ObservedIdentity.StartsWith("content:", StringComparison.OrdinalIgnoreCase))
            return NormalizeIdentity(context.ObservedIdentity);

        return context.ObservedIdentity;
    }

    public NetworkCandidateDecision EvaluateNetworkCandidate(
        NormalizedNetworkEvent networkEvent,
        PageMediaContext context)
    {
        if (networkEvent.StatusCode is not (200 or 206 or null))
            return new(NetworkCandidateDecisionKind.Default, Name, "bad_status");

        if (UnifiedMediaPipeline.IsBrowserPlayEvidence(networkEvent))
        {
            return new(
                NetworkCandidateDecisionKind.StrongAccept,
                Name,
                "cdp_play_200_206",
                MediaEvidence.BrowserObserved);
        }

        var url = networkEvent.Url;
        var full = url.AbsoluteUri;
        var isBiliCdn = SiteNetworkHelper.IsBilibiliHost(url);
        var isDashPiece = url.AbsolutePath.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) ||
                          url.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) ||
                          full.Contains("playurl", StringComparison.OrdinalIgnoreCase) ||
                          full.Contains("/upos", StringComparison.OrdinalIgnoreCase);

        if (!isBiliCdn && !isDashPiece)
            return new(NetworkCandidateDecisionKind.Default, Name, "defer_generic");

        if (IsStrongMime(networkEvent.MimeType) ||
            string.Equals(networkEvent.ResourceType, "Media", StringComparison.OrdinalIgnoreCase) ||
            isDashPiece ||
            networkEvent.MimeType?.Contains("dash", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new(
                NetworkCandidateDecisionKind.StrongAccept,
                Name,
                isDashPiece ? "bilibili_dash_or_playurl" : "bilibili_cdn_media",
                MediaEvidence.Heuristic);
        }

        if (networkEvent.ContentLength is >= 64 * 1024)
            return new(NetworkCandidateDecisionKind.Accept, Name, "bilibili_sized", MediaEvidence.Heuristic);

        return new(NetworkCandidateDecisionKind.Default, Name, "defer_generic");
    }

    public NetworkCandidateDecision EvaluateDomCandidate(
        Uri candidateUrl,
        string? mimeHint,
        PageMediaContext context)
    {
        if (SiteNetworkHelper.IsBilibiliHost(candidateUrl) ||
            candidateUrl.AbsoluteUri.Contains("playurl", StringComparison.OrdinalIgnoreCase))
            return new(NetworkCandidateDecisionKind.Accept, Name, "bilibili_dom", MediaEvidence.DomObserved);
        return new(NetworkCandidateDecisionKind.Default, Name);
    }

    public RequestContext EnrichRequestContext(
        RequestContext requestContext,
        Uri? mediaUrl,
        PageMediaContext context)
    {
        var referer = string.IsNullOrWhiteSpace(requestContext.Referer)
            ? "https://www.bilibili.com/"
            : requestContext.Referer;
        var origin = string.IsNullOrWhiteSpace(requestContext.Origin)
            ? "https://www.bilibili.com"
            : requestContext.Origin;
        return requestContext with { Referer = referer, Origin = origin };
    }

    public ValidationPolicy GetValidationPolicy(
        Uri mediaUrl,
        bool browserObserved,
        PageMediaContext context) =>
        browserObserved
            ? new(true, true, "bilibili_browser_observed")
            : new(Reason: "bilibili_sample_ok");

    public ExternalResolvePolicy GetExternalResolvePolicy(PageMediaContext context) =>
        new(AllowExternalResolve: true, PreferBrowserOverExternal: false, Reason: "bilibili_ytdlp_or_playinfo");

    public Uri? CanonicalizeExternalPageUrl(Uri pageUrl, string? observedIdentity)
    {
        var id = SiteNetworkHelper.ExtractBilibiliContentId(pageUrl) ?? ExtractIdFromIdentity(observedIdentity);
        if (id is null)
            return null;
        if (id.StartsWith("BV", StringComparison.OrdinalIgnoreCase))
            return new Uri("https://www.bilibili.com/video/" + id);
        if (id.StartsWith("av", StringComparison.OrdinalIgnoreCase))
            return new Uri("https://www.bilibili.com/video/" + id);
        return null;
    }

    public int ScoreCandidate(Uri mediaUrl, bool browserObserved, PageMediaContext context)
    {
        if (browserObserved) return 95;
        if (mediaUrl.AbsolutePath.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase)) return 85;
        return 60;
    }

    private static string NormalizeIdentity(string identity)
    {
        if (identity.StartsWith("content:bilibili:", StringComparison.OrdinalIgnoreCase))
            return identity;
        var id = ExtractIdFromIdentity(identity);
        return id is null ? identity : "content:bilibili:" + id;
    }

    private static string? ExtractIdFromIdentity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return null;
        var bv = Regex.Match(identity, @"BV[\w]+", RegexOptions.IgnoreCase);
        if (bv.Success) return bv.Value.ToUpperInvariant();
        var av = Regex.Match(identity, @"av\d+", RegexOptions.IgnoreCase);
        return av.Success ? av.Value.ToLowerInvariant() : null;
    }

    private static bool IsStrongMime(string? mime) =>
        mime is not null &&
        (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
         mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
         mime.Contains("dash", StringComparison.OrdinalIgnoreCase));
}
