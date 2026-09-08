using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Contracts;

/// <summary>
/// Site-specific discovery / admission / context enrichment.
/// Does not download media and does not own the pipeline.
/// </summary>
public interface ISiteMediaAdapter
{
    string Name { get; }

    bool Matches(Uri pageUri);

    /// <summary>Optional content identity for the current page (e.g. content:tiktok:123).</summary>
    string? ResolveContentIdentity(PageMediaContext context);

    NetworkCandidateDecision EvaluateNetworkCandidate(
        NormalizedNetworkEvent networkEvent,
        PageMediaContext context);

    NetworkCandidateDecision EvaluateDomCandidate(
        Uri candidateUrl,
        string? mimeHint,
        PageMediaContext context);

    RequestContext EnrichRequestContext(
        RequestContext requestContext,
        Uri? mediaUrl,
        PageMediaContext context);

    ValidationPolicy GetValidationPolicy(
        Uri mediaUrl,
        bool browserObserved,
        PageMediaContext context);

    ExternalResolvePolicy GetExternalResolvePolicy(PageMediaContext context);

    /// <summary>Rewrite feed/root pages to a stable content URL when possible.</summary>
    Uri? CanonicalizeExternalPageUrl(Uri pageUrl, string? observedIdentity);

    int ScoreCandidate(Uri mediaUrl, bool browserObserved, PageMediaContext context);
}

public interface ISiteMediaAdapterResolver
{
    ISiteMediaAdapter Resolve(Uri pageUri);
    ISiteMediaAdapter Generic { get; }
}

public interface ICandidateDecisionPolicy
{
    NetworkCandidateDecision Combine(
        NetworkCandidateDecision site,
        NetworkCandidateDecision generic);
}
