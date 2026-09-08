using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Sites;

public sealed class CandidateDecisionPolicy : ICandidateDecisionPolicy
{
    public NetworkCandidateDecision Combine(
        NetworkCandidateDecision site,
        NetworkCandidateDecision generic)
    {
        // Explicit site reject wins.
        if (site.Kind == NetworkCandidateDecisionKind.Reject)
            return site;

        // Strong site accept beats weak generic morphology.
        if (site.Kind == NetworkCandidateDecisionKind.StrongAccept)
            return site;

        if (site.Kind == NetworkCandidateDecisionKind.Accept &&
            generic.Kind is NetworkCandidateDecisionKind.Default or NetworkCandidateDecisionKind.Accept
                or NetworkCandidateDecisionKind.StrongAccept)
            return site with
            {
                Evidence = site.Evidence > generic.Evidence ? site.Evidence : generic.Evidence
            };

        if (generic.Kind == NetworkCandidateDecisionKind.Reject &&
            site.Kind == NetworkCandidateDecisionKind.Default)
            return generic;

        if (generic.Kind is NetworkCandidateDecisionKind.StrongAccept or NetworkCandidateDecisionKind.Accept)
            return generic;

        return site.Kind == NetworkCandidateDecisionKind.Default ? generic : site;
    }
}

public sealed class SiteMediaAdapterResolver : ISiteMediaAdapterResolver
{
    private readonly IReadOnlyList<ISiteMediaAdapter> _special;
    private readonly ISiteMediaAdapter _generic;

    public SiteMediaAdapterResolver(IEnumerable<ISiteMediaAdapter> adapters)
    {
        var list = (adapters ?? []).ToArray();
        _generic = list.FirstOrDefault(a => a.Name.Equals("generic", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("GenericMediaAdapter is required.");
        _special = list.Where(a => !ReferenceEquals(a, _generic)).ToArray();
    }

    public ISiteMediaAdapter Generic => _generic;

    public ISiteMediaAdapter Resolve(Uri pageUri)
    {
        foreach (var adapter in _special)
        {
            if (adapter.Matches(pageUri))
                return adapter;
        }

        return _generic;
    }
}
