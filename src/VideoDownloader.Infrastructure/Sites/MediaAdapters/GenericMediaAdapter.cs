using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;

namespace VideoDownloader.Infrastructure.Sites.MediaAdapters;

/// <summary>
/// Cross-site morphological admission. Fallback when no special site matches.
/// </summary>
public sealed class GenericMediaAdapter : ISiteMediaAdapter
{
    public string Name => "generic";

    public bool Matches(Uri pageUri) => true;

    public string? ResolveContentIdentity(PageMediaContext context) =>
        context.ObservedIdentity;

    public NetworkCandidateDecision EvaluateNetworkCandidate(
        NormalizedNetworkEvent networkEvent,
        PageMediaContext context)
    {
        if (networkEvent.Url.Scheme is not ("http" or "https"))
            return new(NetworkCandidateDecisionKind.Reject, Name, "non_http", MediaEvidence.Heuristic);

        if (UnifiedMediaPipeline.IsCandidate(
                networkEvent.Url,
                networkEvent.MimeType,
                networkEvent.ResourceType,
                networkEvent.ContentLength))
        {
            var evidence = UnifiedMediaPipeline.IsBrowserPlayEvidence(networkEvent)
                ? MediaEvidence.BrowserObserved
                : MediaEvidence.Heuristic;
            var kind = evidence == MediaEvidence.BrowserObserved
                ? NetworkCandidateDecisionKind.StrongAccept
                : NetworkCandidateDecisionKind.Accept;
            return new(kind, Name, evidence == MediaEvidence.BrowserObserved ? "browser_play" : "morphology", evidence);
        }

        return new(NetworkCandidateDecisionKind.Reject, Name, "not_candidate", MediaEvidence.Heuristic);
    }

    public NetworkCandidateDecision EvaluateDomCandidate(
        Uri candidateUrl,
        string? mimeHint,
        PageMediaContext context)
    {
        if (UnifiedMediaPipeline.IsCandidate(candidateUrl, mimeHint))
            return new(NetworkCandidateDecisionKind.Accept, Name, "dom_morphology", MediaEvidence.DomObserved);
        return new(NetworkCandidateDecisionKind.Default, Name, "dom_unknown");
    }

    public RequestContext EnrichRequestContext(
        RequestContext requestContext,
        Uri? mediaUrl,
        PageMediaContext context) =>
        requestContext;

    public ValidationPolicy GetValidationPolicy(
        Uri mediaUrl,
        bool browserObserved,
        PageMediaContext context) =>
        browserObserved
            ? new ValidationPolicy(SkipIndependentHttpSample: true, PreferBrowserObservedUrl: true, Reason: "browser_observed")
            : new ValidationPolicy();

    public ExternalResolvePolicy GetExternalResolvePolicy(PageMediaContext context) =>
        new(AllowExternalResolve: true);

    public Uri? CanonicalizeExternalPageUrl(Uri pageUrl, string? observedIdentity) => null;

    public int ScoreCandidate(Uri mediaUrl, bool browserObserved, PageMediaContext context) =>
        browserObserved ? 80 : 40;
}
