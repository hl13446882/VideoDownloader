using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Browser;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection;

namespace VideoDownloader.Infrastructure.Tests;

public sealed class DetectionAcceptanceTests
{
    private static readonly Uri Page = new("https://example.test/feed");
    private static UnifiedMediaPipeline Create() => new(Substitute.For<IRequestMessageFactory>(), [], Options.Create(new AppOptions()));

    [Fact]
    public async Task InitialVideoArrivingAfterEmptyCompletion_StartsDiscoveryOnce()
    {
        var pipeline=Create();
        await pipeline.CompleteDiscoveryAsync(default);
        using var host=new WebView2Host(Options.Create(new AppOptions()),Substitute.For<INetworkEventNormalizer>(),pipeline,NullLogger<WebView2Host>.Instance);
        var count=0;
        host.MediaSessionChanged+=(_,_)=>{count++;pipeline.Clear();};
        host.ObserveVideoIdentity("late-first-video",Page);
        await Task.Delay(1800);
        host.ObserveVideoIdentity("late-first-video",Page);
        Assert.Equal(1,count);
    }

    [Fact]
    public async Task CaptionEnrichmentAfterCompletion_DoesNotReopenDiscovery()
    {
        var pipeline=Create();
        var id=pipeline.SessionId;
        await pipeline.CompleteDiscoveryAsync(default);
        pipeline.UpdateCaption(id,"late caption");
        Assert.True(pipeline.IsCompleted);
        Assert.Equal(id,pipeline.SessionId);
    }

    [Fact]
    public async Task A1_SamePageVideoChange_CommitsExactlyOnce_DespiteRepeatedObservations()
    {
        var pipeline = Create();
        using var host = new WebView2Host(Options.Create(new AppOptions()), Substitute.For<INetworkEventNormalizer>(),
            pipeline, NullLogger<WebView2Host>.Instance);
        var sessions = 0;
        host.MediaSessionChanged += (_, _) => { sessions++; pipeline.Clear(); };
        host.ObserveVideoIdentity("video:A", Page);
        await Task.Delay(1700);
        host.ObserveVideoIdentity("video:B", Page);
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(200);
            host.ObserveVideoIdentity("video:B", Page);
        }
        Assert.Equal("video:B", host.CurrentMediaSessionKey);
        Assert.Equal(1, sessions);
    }

    [Fact]
    public async Task A2_TransportChanges_DoNotReplaceSession()
    {
        var pipeline = Create();
        using var host = new WebView2Host(Options.Create(new AppOptions()), Substitute.For<INetworkEventNormalizer>(),
            pipeline, NullLogger<WebView2Host>.Instance);
        host.ObserveVideoIdentity("video:A", Page);
        await Task.Delay(1700);
        var session = pipeline.SessionId;
        var changes = 0;
        host.MediaSessionChanged += (_, _) => changes++;
        foreach (var address in new[] { "https://cdn1.test/720.mp4?token=1", "https://cdn2.test/1080.mp4?token=2", "https://cdn2.test/1080.mp4?token=3" })
            host.ObserveMediaCandidate(new Uri(address), "dom-player");
        await Task.Delay(1700);
        Assert.Equal(0, changes);
        Assert.Equal(session, pipeline.SessionId);
        Assert.Equal("video:A", host.CurrentMediaSessionKey);
    }

    [Fact]
    public async Task A3_LateValidationAndQueuedNetwork_AreDiscardedAfterSamePageSwitch()
    {
        var pipeline = Create();
        var completion = new TaskCompletionSource<UnifiedMediaPipeline.Probed?>();
        var entered = new TaskCompletionSource();
        pipeline.InspectOverride = (_, _, _, _) => { entered.SetResult(); return completion.Task; };
        var old = pipeline.SessionId;
        var events = new List<DetectedVideo>();
        pipeline.VideoDetected += (_, v) => events.Add(v);
        await pipeline.ProbePageAsync(Page, null, "{\"caption\":\"A\",\"media\":[\"https://cdn.test/movie.mp4\"]}", RequestContext.CreateEmpty(), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        pipeline.Clear();
        var variant = MediaVariant.FromCombinedTrack("A", new Uri("https://cdn.test/movie.mp4"), RequestContext.CreateEmpty(), contentLength: 2_000_000);
        completion.SetResult(new(Page, variant.Tracks[0], 30, 720));
        await pipeline.ProcessAsync(new(new Uri("https://cdn.test/late.mp4"), "GET", 200, "video/mp4", 2_000_000,
            "Media", null, Page, null, new Dictionary<string,string>(), new Dictionary<string,string>(),
            RequestContext.CreateEmpty(), DateTimeOffset.UtcNow, NetworkEventSource.Cdp) {SessionId=old}, default);
        await pipeline.CompleteDiscoveryAsync(default);
        Assert.Empty(events);
    }

    [Fact]
    public async Task A4_CaptionSurvivesSubsequentPageTitle_AndSignedRequestIsUnmodified()
    {
        var pipeline = Create();
        var url = new Uri("https://cdn.test/movie.mp4?x-signature=secret&range=0-9&n=token");
        Uri? requested = null;
        pipeline.InspectOverride = (u, p, c, _) =>
        {
            requested = u;
            return Task.FromResult<UnifiedMediaPipeline.Probed?>(new(p, MediaVariant.FromCombinedTrack("video", u,c,contentLength:2_000_000).Tracks[0],30,720));
        };
        DetectedVideo? latest = null;
        pipeline.VideoDetected += (_, v) => latest = v;
        await pipeline.ProbePageAsync(Page, "Wrong", System.Text.Json.JsonSerializer.Serialize(new {caption="Actual caption",media=new[]{url.AbsoluteUri}}), RequestContext.CreateEmpty(), default);
        await pipeline.ProbePageAsync(Page, "Wrong again", null, RequestContext.CreateEmpty(), default);
        await pipeline.CompleteDiscoveryAsync(default);
        Assert.Equal("Actual caption", latest?.DisplayTitle);
        Assert.Equal(url, requested);
    }

    [Fact]
    public async Task SameUrlRefresh_ClearWhileValidationRunning_CreatesNewSessionAndDiscardsOldWork()
    {
        var pipeline = Create();
        var validation = new TaskCompletionSource<UnifiedMediaPipeline.Probed?>();
        var entered = new TaskCompletionSource();
        pipeline.InspectOverride = (_, _, _, _) => { entered.SetResult(); return validation.Task; };
        var events = new List<DetectedVideo>();
        pipeline.VideoDetected += (_, v) => events.Add(v);
        var old = pipeline.SessionId;
        await pipeline.ProbePageAsync(Page, null, "{\"caption\":\"A\",\"media\":[\"https://cdn.test/movie.mp4\"]}", RequestContext.CreateEmpty(), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // Document refresh semantics: Clear cancels the in-flight session and opens a new one.
        pipeline.Clear();
        Assert.NotEqual(old, pipeline.SessionId);
        validation.SetResult(new(Page, MediaVariant.FromCombinedTrack("A", new Uri("https://cdn.test/movie.mp4"), RequestContext.CreateEmpty(), contentLength: 2_000_000).Tracks[0], 30, 720));
        await Task.Delay(300);
        Assert.Empty(events);
        await pipeline.CompleteDiscoveryAsync(default);
        Assert.True(pipeline.IsCompleted);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A5_DiscoveryLeaseKeepsSessionOpen_AndRejectsOldSource(bool switchVideo)
    {
        var pipeline = Create();
        var calls = 0;
        pipeline.InspectOverride = (_,_,_,_) => { calls++; return Task.FromResult<UnifiedMediaPipeline.Probed?>(null); };
        using var discovery = pipeline.BeginDiscovery(pipeline.SessionId)!;
        var completed = pipeline.CompleteDiscoveryAsync(default);
        Assert.False(completed.IsCompleted);
        if (switchVideo) pipeline.Clear();
        await discovery.SubmitAsync(Page,"{\"media\":[\"https://cdn.test/opaque?id=42\"]}",RequestContext.CreateEmpty(),default);
        discovery.Dispose();
        if (switchVideo) await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>completed);
        else await completed.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(switchVideo ? 0 : 1,calls);
    }

    [Theory]
    [InlineData("https://cdn.test/tiny.m3u8", null)]
    [InlineData("https://cdn.test/data.json", "application/dash+xml")]
    public void SmallManifest_IsCandidate(string url, string? mime) => Assert.True(UnifiedMediaPipeline.IsCandidate(new(url),mime,null,100));
}
