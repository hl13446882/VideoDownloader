using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Sites;
using VideoDownloader.Infrastructure.Sites.MediaAdapters;

namespace VideoDownloader.Infrastructure.Tests;

public class SiteMediaAdapterPhase1Tests
{
    private static SiteMediaAdapterResolver CreateResolver() =>
        new([
            new GenericMediaAdapter(),
            new TikTokMediaAdapter(),
            new DouyinMediaAdapter(),
            new YouTubeMediaAdapter(),
            new BilibiliMediaAdapter()
        ]);

    [Theory]
    [InlineData("https://www.tiktok.com/@a/video/1234567890123456789", "tiktok")]
    [InlineData("https://www.douyin.com/video/1234567890123456789", "douyin")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "youtube")]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD", "bilibili")]
    [InlineData("https://example.com/watch", "generic")]
    public void Resolver_PicksDedicatedAdapter(string page, string expected)
    {
        Assert.Equal(expected, CreateResolver().Resolve(new Uri(page)).Name);
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
    public void YouTubeAdapter_RejectsSabr_AndAcceptsVideoplayback()
    {
        var adapter = new YouTubeMediaAdapter();
        var page = new Uri("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        var ctx = new PageMediaContext(page, null, "content:youtube:dQw4w9WgXcQ", Guid.NewGuid());

        var sabr = new NormalizedNetworkEvent(
            new Uri("https://rr1---sn.googlevideo.com/videoplayback?sabr=1&id=x"),
            "GET", 200, null, null, "XHR", null, page,
            null, new Dictionary<string, string>(), new Dictionary<string, string>(),
            RequestContext.CreateEmpty(), DateTimeOffset.UtcNow, NetworkEventSource.Cdp);
        Assert.Equal(NetworkCandidateDecisionKind.Reject, adapter.EvaluateNetworkCandidate(sabr, ctx).Kind);

        var playback = sabr with
        {
            Url = new Uri("https://rr1---sn.googlevideo.com/videoplayback?mime=video/mp4&itag=137")
        };
        Assert.Equal(NetworkCandidateDecisionKind.StrongAccept, adapter.EvaluateNetworkCandidate(playback, ctx).Kind);
    }

    [Fact]
    public void DouyinAdapter_StrongAccepts_PlayAddr()
    {
        var adapter = new DouyinMediaAdapter();
        var page = new Uri("https://www.douyin.com/video/1234567890123456789");
        var ctx = new PageMediaContext(page, null, null, Guid.NewGuid());
        var evt = new NormalizedNetworkEvent(
            new Uri("https://v3-web.douyinvod.com/playAddr/obj?video_id=abc"),
            "GET", 206, "video/mp4", 100000, "Media", null, page,
            null, new Dictionary<string, string>(), new Dictionary<string, string>(),
            RequestContext.CreateEmpty(), DateTimeOffset.UtcNow, NetworkEventSource.Cdp);
        Assert.Equal(NetworkCandidateDecisionKind.StrongAccept, adapter.EvaluateNetworkCandidate(evt, ctx).Kind);
        Assert.Equal("content:douyin:1234567890123456789", adapter.ResolveContentIdentity(ctx));
    }

    [Fact]
    public void BilibiliAdapter_StrongAccepts_UposM4s()
    {
        var adapter = new BilibiliMediaAdapter();
        var page = new Uri("https://www.bilibili.com/video/BV1xx411c7mD");
        var ctx = new PageMediaContext(page, null, null, Guid.NewGuid());
        var evt = new NormalizedNetworkEvent(
            new Uri("https://upos-sz-mirrorcos.bilivideo.com/upos/xxx.m4s"),
            "GET", 206, "video/iso.segment", 200000, "Media", null, page,
            null, new Dictionary<string, string>(), new Dictionary<string, string>(),
            RequestContext.CreateEmpty(), DateTimeOffset.UtcNow, NetworkEventSource.Cdp);
        Assert.Equal(NetworkCandidateDecisionKind.StrongAccept, adapter.EvaluateNetworkCandidate(evt, ctx).Kind);
        Assert.Equal("content:bilibili:BV1XX411C7MD", adapter.ResolveContentIdentity(ctx));
    }

    [Fact]
    public void CandidatePolicy_SiteOpinion_OverridesGeneric()
    {
        var policy = new CandidateDecisionPolicy();
        var site = new NetworkCandidateDecision(NetworkCandidateDecisionKind.StrongAccept, "tiktok", "cdp_play_200_206", MediaEvidence.BrowserObserved);
        var generic = new NetworkCandidateDecision(NetworkCandidateDecisionKind.Reject, "generic", "not_candidate");
        var final = policy.Combine(site, generic);
        Assert.Equal(NetworkCandidateDecisionKind.StrongAccept, final.Kind);
        Assert.Equal("tiktok", final.AdapterName);

        var siteDefault = new NetworkCandidateDecision(NetworkCandidateDecisionKind.Default, "youtube");
        var genericAccept = new NetworkCandidateDecision(NetworkCandidateDecisionKind.Accept, "generic", "morphology");
        Assert.Equal("generic", policy.Combine(siteDefault, genericAccept).AdapterName);
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
}
