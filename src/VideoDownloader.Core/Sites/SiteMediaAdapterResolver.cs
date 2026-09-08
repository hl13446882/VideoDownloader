using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Sites;

public sealed class CandidateDecisionPolicy : ICandidateDecisionPolicy
{
    public NetworkCandidateDecision Combine(
        NetworkCandidateDecision site,
        NetworkCandidateDecision generic)
    {
        // Special-site adapters own admission when they express an opinion.
        // Generic only fills gaps (Default) so TikTok/Douyin/YouTube/Bilibili
        // each walk their own probe rules instead of a shared morphology soup.
        if (site.Kind != NetworkCandidateDecisionKind.Default)
            return site;

        return generic;
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
