using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Sites;
using VideoDownloader.Infrastructure.Sites.MediaAdapters;

namespace VideoDownloader.Infrastructure.Tests;

public class SiteMediaAdapterPhase1Tests
{
    [Fact]
    public void Resolver_PicksTikTok_ThenFallsBackToGeneric()
    {
        var resolver = new SiteMediaAdapterResolver([new GenericMediaAdapter(), new TikTokMediaAdapter()]);
        Assert.Equal("tiktok", resolver.Resolve(new Uri("https://www.tiktok.com/@a/video/1234567890123456789")).Name);
        Assert.Equal("generic", resolver.Resolve(new Uri("https://example.com/watch")).Name);
    }

    [Fact]
    public void TikTokAdapter_StrongAccepts_BrowserPlayEvidence()
    {
        var adapter = new TikTokMediaAdapter();
        var page = new Uri("https://www.tiktok.com/@a/video/1234567890123456789");
        var ctx = new PageMediaContext(page, null, "content:1234567890123456789", Guid.NewGuid());
        var evt = new NormalizedNetworkEvent(
            new Uri("https://v16-webapp-prime.tiktok.com/video/tos/x"),
            "GET", 206, "video/mp4", 8192, "Fetch", null, page,
            null, new Dictionary<string, string>(), new Dictionary<string, string>(),
            RequestContext.CreateEmpty(), DateTimeOffset.UtcNow, NetworkEventSource.Cdp);

        var decision = adapter.EvaluateNetworkCandidate(evt, ctx);
        Assert.Equal(NetworkCandidateDecisionKind.StrongAccept, decision.Kind);
        Assert.Equal(MediaEvidence.BrowserObserved, decision.Evidence);
        Assert.Equal("tiktok", decision.AdapterName);
    }

    [Fact]
    public void CandidatePolicy_SiteStrongAccept_BeatsGenericReject()
    {
        var policy = new CandidateDecisionPolicy();
        var site = new NetworkCandidateDecision(NetworkCandidateDecisionKind.StrongAccept, "tiktok", "cdp_play_200_206", MediaEvidence.BrowserObserved);
        var generic = new NetworkCandidateDecision(NetworkCandidateDecisionKind.Reject, "generic", "not_candidate");
        var final = policy.Combine(site, generic);
        Assert.Equal(NetworkCandidateDecisionKind.StrongAccept, final.Kind);
        Assert.Equal("tiktok", final.AdapterName);
    }

    [Fact]
    public void TikTokAdapter_CanonicalizesFeedRoot()
    {
        var adapter = new TikTokMediaAdapter();
        var rewritten = adapter.CanonicalizeExternalPageUrl(
            new Uri("https://www.tiktok.com/foryou"),
            "content:1234567890123456789");
        Assert.Equal("https://www.tiktok.com/@i/video/1234567890123456789", rewritten!.AbsoluteUri);
    }

    [Fact]
    public void TikTokAdapter_ResolveContentIdentity_PrefersVideoId()
    {
        var adapter = new TikTokMediaAdapter();
        var id = adapter.ResolveContentIdentity(new PageMediaContext(
            new Uri("https://www.tiktok.com/@user/video/1234567890123456789"),
            null, null, Guid.NewGuid()));
        Assert.Equal("content:tiktok:1234567890123456789", id);
    }
}
