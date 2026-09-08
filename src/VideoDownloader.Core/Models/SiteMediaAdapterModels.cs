using VideoDownloader.Core.Contracts;

namespace VideoDownloader.Core.Models;

/// <summary>Per-candidate admission from a site or generic adapter.</summary>
public enum NetworkCandidateDecisionKind
{
    /// <summary>Defer to the other adapter / generic rules.</summary>
    Default = 0,
    /// <summary>Site-confirmed strong media; do not drop for weak URL morphology.</summary>
    StrongAccept = 1,
    /// <summary>Prefer accept; still subject to shared pipeline checks.</summary>
    Accept = 2,
    /// <summary>Site knows this is junk for this page.</summary>
    Reject = 3
}

public sealed record NetworkCandidateDecision(
    NetworkCandidateDecisionKind Kind,
    string AdapterName,
    string? Reason = null,
    MediaEvidence Evidence = MediaEvidence.Unknown);

/// <summary>How strongly we trust that a URL is real playable media.</summary>
public enum MediaEvidence
{
    Unknown = 0,
    Heuristic = 1,
    DomObserved = 2,
    ExternalResolved = 3,
    BrowserObserved = 4
}

public sealed record PageMediaContext(
    Uri PageUrl,
    string? PageTitle,
    string? ObservedIdentity,
    Guid SessionId,
    string? Author = null);

public sealed record ValidationPolicy(
    bool SkipIndependentHttpSample = false,
    bool PreferBrowserObservedUrl = false,
    string? Reason = null);

public sealed record ExternalResolvePolicy(
    bool AllowExternalResolve = true,
    bool PreferBrowserOverExternal = false,
    string? Reason = null);
