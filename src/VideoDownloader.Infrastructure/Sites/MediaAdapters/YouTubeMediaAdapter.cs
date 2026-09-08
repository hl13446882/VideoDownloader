using System.Text.RegularExpressions;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;

namespace VideoDownloader.Infrastructure.Sites.MediaAdapters;

/// <summary>YouTube-only admission: videoplayback / itag / sabr / shorts identity.</summary>
public sealed class YouTubeMediaAdapter : ISiteMediaAdapter
{
    public string Name => "youtube";

    public bool Matches(Uri pageUri) =>
        pageUri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        pageUri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
        pageUri.Host.Contains("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase);

    public string? ResolveContentIdentity(PageMediaContext context)
    {
        var id = SiteNetworkHelper.ExtractYouTubeVideoId(context.PageUrl);
        if (id is not null)
            return "content:youtube:" + id;

        if (!string.IsNullOrWhiteSpace(context.ObservedIdentity) &&
            context.ObservedIdentity.StartsWith("content:", StringComparison.OrdinalIgnoreCase))
            return NormalizeIdentity(context.ObservedIdentity);

        return context.ObservedIdentity;
    }

    public NetworkCandidateDecision EvaluateNetworkCandidate(
        NormalizedNetworkEvent networkEvent,
        PageMediaContext context)
    {
        var url = networkEvent.Url.AbsoluteUri;

        // SABR adaptive transport is not a progressive download candidate.
        if (url.Contains("sabr=1", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("mime=video", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("mime=audio", StringComparison.OrdinalIgnoreCase))
        {
            return new(NetworkCandidateDecisionKind.Reject, Name, "sabr", MediaEvidence.Heuristic);
        }

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

        var isPlayback = networkEvent.Url.Host.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
                         networkEvent.Url.AbsolutePath.Contains("/videoplayback", StringComparison.OrdinalIgnoreCase);
        if (!isPlayback)
            return new(NetworkCandidateDecisionKind.Default, Name, "defer_generic");

        var hasMime = url.Contains("mime=video", StringComparison.OrdinalIgnoreCase) ||
                      url.Contains("mime=audio", StringComparison.OrdinalIgnoreCase);
        var hasItag = url.Contains("itag=", StringComparison.OrdinalIgnoreCase);

        if (hasMime || hasItag ||
            string.Equals(networkEvent.ResourceType, "Media", StringComparison.OrdinalIgnoreCase) ||
            IsStrongMime(networkEvent.MimeType))
        {
            return new(
                NetworkCandidateDecisionKind.StrongAccept,
                Name,
                hasMime ? "youtube_videoplayback_mime" : "youtube_videoplayback",
                MediaEvidence.Heuristic);
        }

        // Opaque googlevideo with size still worth probing.
        if (networkEvent.ContentLength is >= 64 * 1024)
            return new(NetworkCandidateDecisionKind.Accept, Name, "youtube_playback_sized", MediaEvidence.Heuristic);

        return new(NetworkCandidateDecisionKind.Default, Name, "defer_generic");
    }

    public NetworkCandidateDecision EvaluateDomCandidate(
        Uri candidateUrl,
        string? mimeHint,
        PageMediaContext context)
    {
        if (candidateUrl.AbsolutePath.Contains("/videoplayback", StringComparison.OrdinalIgnoreCase) ||
            candidateUrl.Host.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase))
            return new(NetworkCandidateDecisionKind.Accept, Name, "youtube_dom", MediaEvidence.DomObserved);
        return new(NetworkCandidateDecisionKind.Default, Name);
    }

    public RequestContext EnrichRequestContext(
        RequestContext requestContext,
        Uri? mediaUrl,
        PageMediaContext context)
    {
        var referer = string.IsNullOrWhiteSpace(requestContext.Referer)
            ? "https://www.youtube.com/"
            : requestContext.Referer;
        var origin = string.IsNullOrWhiteSpace(requestContext.Origin)
            ? "https://www.youtube.com"
            : requestContext.Origin;
        return requestContext with { Referer = referer, Origin = origin };
    }

    public ValidationPolicy GetValidationPolicy(
        Uri mediaUrl,
        bool browserObserved,
        PageMediaContext context) =>
        browserObserved
            ? new(true, true, "youtube_browser_observed")
            : new(Reason: "youtube_sample_ok");

    public ExternalResolvePolicy GetExternalResolvePolicy(PageMediaContext context) =>
        new(AllowExternalResolve: true, PreferBrowserOverExternal: false, Reason: "youtube_ytdlp_primary");

    public Uri? CanonicalizeExternalPageUrl(Uri pageUrl, string? observedIdentity)
    {
        var id = SiteNetworkHelper.ExtractYouTubeVideoId(pageUrl) ?? ExtractIdFromIdentity(observedIdentity);
        if (id is null)
            return null;
        return new Uri("https://www.youtube.com/watch?v=" + id);
    }

    public int ScoreCandidate(Uri mediaUrl, bool browserObserved, PageMediaContext context)
    {
        if (browserObserved) return 95;
        if (mediaUrl.AbsolutePath.Contains("/videoplayback", StringComparison.OrdinalIgnoreCase)) return 90;
        return 60;
    }

    private static string NormalizeIdentity(string identity)
    {
        if (identity.StartsWith("content:youtube:", StringComparison.OrdinalIgnoreCase))
            return identity;
        var id = ExtractIdFromIdentity(identity);
        return id is null ? identity : "content:youtube:" + id;
    }

    private static string? ExtractIdFromIdentity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return null;
        var m = Regex.Match(identity, @"(?:content:(?:youtube:)?)?(?<id>[A-Za-z0-9_-]{6,})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["id"].Value : null;
    }

    private static bool IsStrongMime(string? mime) =>
        mime is not null &&
        (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
         mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));
}
