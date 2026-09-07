using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Browser;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection;

namespace VideoDownloader.Infrastructure.Tests;

public class MediaTrackReconciliationTests
{
    private static readonly Uri Page = new("https://example.test/video/100");
    private static readonly RequestContext Context = RequestContext.CreateEmpty();
    private static UnifiedMediaPipeline Create(params IExternalSiteResolver[] resolvers) =>
        new(Substitute.For<IRequestMessageFactory>(), resolvers, Options.Create(new AppOptions()));

    private static Task Submit(UnifiedMediaPipeline pipeline, string[] urls, bool primary = false) => pipeline.ProbePageAsync(Page, null,
        primary ? JsonSerializer.Serialize(new { identity = "content:100", media = urls }) : JsonSerializer.Serialize(new { media = urls }), Context, default);

    private static UnifiedMediaPipeline.Probed Media(Uri url, MediaTrackKind kind, string? owner = null, int height = 720) =>
        new(Page, new MediaTrack(url.AbsolutePath, kind, url, kind == MediaTrackKind.Audio ? "aac" : "h264", "mp4", 100000, 4096, Context)
            { IsValidated = true, ContentIdentity = owner }, 30, kind == MediaTrackKind.Audio ? null : height);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DomOneTrack_NetworkOtherTrack_IsNotDiscarded(bool domVideo)
    {
        var pipeline = Create();
        pipeline.InspectOverride = (u, _, _, _) => Task.FromResult<UnifiedMediaPipeline.Probed?>(Media(u, u.AbsolutePath.Contains("audio") ? MediaTrackKind.Audio : MediaTrackKind.Video));
        DetectedVideo? latest = null;
        pipeline.VideoDetected += (_, v) => latest = v;
        await Submit(pipeline, [domVideo ? "https://cdn.test/video.mp4" : "https://cdn.test/audio.m4a"], true);
        await Submit(pipeline, [domVideo ? "https://cdn.test/audio.m4a" : "https://cdn.test/video.mp4"]);
        await pipeline.CompleteDiscoveryAsync(default);
        Assert.Contains(latest!.Variants.SelectMany(v => v.Tracks), t => t.Kind == MediaTrackKind.Video);
        Assert.Contains(latest.Variants.SelectMany(v => v.Tracks), t => t.Kind == MediaTrackKind.Audio);
        Assert.DoesNotContain(latest.Variants, v => v.Tracks.Count > 1); // Unknown ownership is not permission to mux.
        Assert.DoesNotContain(latest.Variants, v => v.VariantId.Contains("无音轨", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JsonOwnership_PairsCurrentItemAndRejectsOtherSameDurationItem()
    {
        var pipeline = Create();
        pipeline.InspectOverride = (u, _, _, _) => Task.FromResult<UnifiedMediaPipeline.Probed?>(Media(u, u.AbsolutePath.Contains("audio") ? MediaTrackKind.Audio : MediaTrackKind.Video));
        DetectedVideo? latest = null;
        pipeline.VideoDetected += (_, v) => latest = v;
        await Submit(pipeline, ["https://cdn.test/video.mp4"], true);
        var candidates = MediaAddressScanner.Scan("""
            {"items":[{"id":"100","video":{"audio":"https://cdn.test/audio.m4a"}},
            {"id":"200","video":{"audio":"https://cdn.test/other-audio.m4a"}}]}
            """);
        Assert.Equal(new[] { "id:100", "id:200" }, candidates.Select(c => c.ContentIdentity));
        await pipeline.ProbePageAsync(Page, null, JsonSerializer.Serialize(new { candidates = candidates.Select(c => new { url = c.Url, contentIdentity = c.ContentIdentity }) }), Context, default);
        await pipeline.CompleteDiscoveryAsync(default);
        var paired = Assert.Single(latest!.Variants, v => v.Tracks.Count == 2);
        Assert.All(paired.Tracks, t => Assert.Equal("id:100", t.ContentIdentity));
        Assert.DoesNotContain(latest.Variants.SelectMany(v => v.Tracks), t => t.SourceUrl.AbsolutePath.Contains("other"));
    }

    [Fact]
    public async Task ExternalAudio_CombinesWithDomVideo()
    {
        var external = Substitute.For<IExternalSiteResolver>();
        external.IsAvailable.Returns(true);
        external.ResolveAsync(Arg.Any<Uri>(), Context, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<DetectedVideo>>(
            [new(Guid.NewGuid(), "generic", "100", "caption", Page, MediaFamily.DirectMp4,
                [MediaVariant.FromTracks("audio", null, null, null, "mp4", [Media(new("https://cdn.test/audio.m4a"), MediaTrackKind.Audio).Track])], false)]));
        var pipeline = Create(external);
        pipeline.InspectOverride = (u, _, _, _) => Task.FromResult<UnifiedMediaPipeline.Probed?>(Media(u, MediaTrackKind.Video));
        DetectedVideo? latest = null;
        pipeline.VideoDetected += (_, v) => latest = v;
        await Submit(pipeline, ["https://cdn.test/video.mp4"], true);
        await pipeline.ProbePageAsync(Page, null, null, Context, default, runExternal: true);
        await pipeline.CompleteDiscoveryAsync(default);
        Assert.Contains(latest!.Variants, v => v.Tracks.Count == 2 && v.Tracks.Any(t => t.Kind == MediaTrackKind.Audio));
        Assert.Equal("caption", latest.DisplayTitle);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "Player caption")]
    [InlineData(true, "Player caption")]
    public async Task TrackReconciliation_PreservesCaptionAcrossSourcesAndLaterUpdates(bool localFirst, string? playerCaption)
    {
        var external = Substitute.For<IExternalSiteResolver>();
        external.IsAvailable.Returns(true);
        external.ResolveAsync(Arg.Any<Uri>(), Context, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<DetectedVideo>>(
            [new(Guid.NewGuid(), "generic", "100", "Original extracted caption", Page, MediaFamily.DirectMp4,
                [MediaVariant.FromTracks("audio", null, null, null, "mp4", [Media(new("https://cdn.test/opaque-audio.m4a"), MediaTrackKind.Audio).Track])], false)]));
        var pipeline = Create(external);
        pipeline.InspectOverride = (u, _, _, _) => Task.FromResult<UnifiedMediaPipeline.Probed?>(Media(u, MediaTrackKind.Video));
        DetectedVideo? latest = null;
        pipeline.VideoDetected += (_, v) => latest = v;
        await pipeline.ProbePageAsync(Page, "Wrong page title", JsonSerializer.Serialize(new { identity = "content:100", caption = playerCaption, media = Array.Empty<string>() }), Context, default);
        if (localFirst) await Submit(pipeline, ["https://cdn.test/opaque-video.mp4"], true);
        await pipeline.ProbePageAsync(Page, "Wrong page title", null, Context, default, runExternal: true);
        Assert.Equal(playerCaption ?? "Original extracted caption", latest!.DisplayTitle);

        // A later variant must not regenerate metadata from its CDN filename.
        await Submit(pipeline, ["https://cdn.test/opaque-hd.mp4"], true);
        await pipeline.ProbePageAsync(Page, "Another wrong page title", null, Context, default);
        await pipeline.CompleteDiscoveryAsync(default);
        Assert.Equal(playerCaption ?? "Original extracted caption", latest!.DisplayTitle);
        Assert.Contains(latest.Variants, v => v.Tracks.Count == 2);

        pipeline.UpdateCaption(pipeline.SessionId, "Late player caption");
        Assert.Equal("Late player caption", latest.DisplayTitle);
        Assert.True(pipeline.IsCompleted);

        pipeline.Clear();
        await Submit(pipeline, ["https://cdn.test/new-video.mp4"], true);
        await pipeline.CompleteDiscoveryAsync(default);
        Assert.DoesNotContain("caption", latest.DisplayTitle);
    }

    [Fact]
    public async Task MasterManifest_IsNotDiscardedWhenItsFirstTrackUsesChildUrl()
    {
        var pipeline = Create();
        var child = Media(new("https://cdn.test/720.m3u8"), MediaTrackKind.Video).Track with { Container = "hls" };
        var audio = Media(new("https://cdn.test/audio.m3u8"), MediaTrackKind.Audio).Track with { Container = "hls" };
        var manifest = new ManifestResolutionResult([MediaVariant.FromTracks("720", null, 720, null, "hls", [child, audio])], false);
        pipeline.InspectOverride = (_, _, _, _) => Task.FromResult<UnifiedMediaPipeline.Probed?>(new(Page, child, 0, 720, manifest));
        DetectedVideo? latest = null;
        pipeline.VideoDetected += (_, v) => latest = v;
        await Submit(pipeline, ["https://cdn.test/master.m3u8"], true);
        await pipeline.CompleteDiscoveryAsync(default);
        Assert.Contains(latest!.Variants, v => v.Tracks.Count == 2);
    }

    [Fact]
    public async Task ValidationFailure_HasDiagnosticAndDoesNotLeaveSessionWaiting()
    {
        var pipeline = Create();
        pipeline.InspectOverride = (_, _, _, _) => throw new OperationCanceledException();
        await Submit(pipeline, ["https://cdn.test/video.mp4"]);
        await pipeline.CompleteDiscoveryAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains("timed out", pipeline.LastValidationError);
        pipeline.Clear();
        Assert.Null(pipeline.LastValidationError);
    }

    [Fact]
    public async Task RequestBeforeSeal_HoldsSessionUntilResponseOrReset()
    {
        var pipeline = Create();
        using var host = new WebView2Host(Options.Create(new AppOptions()), Substitute.For<INetworkEventNormalizer>(), pipeline, NullLogger<WebView2Host>.Instance);
        host.ProcessCdpRequest("""{"requestId":"1","type":"Media","request":{"url":"https://cdn.test/audio.m4a","method":"GET","headers":{}}} """);
        var completed = pipeline.CompleteDiscoveryAsync(default);
        Assert.False(completed.IsCompleted);
        host.ResetDetectionSession();
        await completed.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task LateCdpResponse_TransfersReservationToQueue_AndDisposeDrainsIt()
    {
        var pipeline = Create();
        var host = new WebView2Host(Options.Create(new AppOptions()), Substitute.For<INetworkEventNormalizer>(), pipeline, NullLogger<WebView2Host>.Instance);
        try
        {
            host.ProcessCdpRequest("""{"requestId":"1","type":"Media","request":{"url":"https://cdn.test/audio.m4a","method":"GET","headers":{}}}""");
            var completed = pipeline.CompleteDiscoveryAsync(default);
            host.ProcessCdpResponse("""{"requestId":"1","response":{"status":206,"mimeType":"audio/mp4","headers":{"content-length":"2048"}}}""");
            Assert.False(completed.IsCompleted);
            await host.DisposeAsync();
            await completed.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { await host.DisposeAsync(); }
    }

    [Fact]
    public async Task ReservedRequest_CanForkForLateResponseAfterSeal()
    {
        var pipeline = Create();
        using var request = pipeline.BeginDiscovery(pipeline.SessionId)!;
        var completed = pipeline.CompleteDiscoveryAsync(default);
        using var response = request.Fork();
        Assert.NotNull(response);
        request.Dispose();
        Assert.False(completed.IsCompleted);
        var calls = 0;
        pipeline.InspectOverride = (u, _, _, _) => { calls++; return Task.FromResult<UnifiedMediaPipeline.Probed?>(Media(u, MediaTrackKind.Audio)); };
        await response.SubmitAsync(Page, """{"media":["https://cdn.test/audio.m4a"]}""", Context, default);
        response.Dispose();
        await completed.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, calls);
        Assert.Null(pipeline.BeginDiscovery(pipeline.SessionId));
    }

    [Theory]
    [InlineData(MediaTrackKind.Audio)]
    [InlineData(MediaTrackKind.Video)]
    [InlineData(MediaTrackKind.Combined)]
    public async Task UntypedHls_UsesInspectedStreamsNotCombinedAssumption(MediaTrackKind kind)
    {
        var url = new Uri("https://cdn.test/leaf.m3u8");
        var resolver = Substitute.For<IManifestResolver>();
        resolver.ResolveHlsAsync(Arg.Any<MediaResource>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(new ManifestResolutionResult([MediaVariant.FromTracks("leaf", null, null, null, "hls",
                [Media(url, MediaTrackKind.Unknown).Track with { Container = "hls" }])], false)));
        var pipeline = new UnifiedMediaPipeline(Substitute.For<IRequestMessageFactory>(), [], Options.Create(new AppOptions()), resolver);
        pipeline.InspectOverride = (u, _, _, _) => Task.FromResult<UnifiedMediaPipeline.Probed?>(Media(u, kind));
        var result = await pipeline.InspectManifestAsync(url, Page, Context, "hls", default);
        Assert.Equal(kind, result!.Manifest!.Variants[0].Tracks[0].Kind);
    }

    [Fact]
    public async Task TinyMimeCandidate_StillRequiresSuccessfulStreamValidation()
    {
        var pipeline = Create();
        var calls = 0;
        var published = 0;
        pipeline.InspectOverride = (_, _, _, _) => { calls++; return Task.FromResult<UnifiedMediaPipeline.Probed?>(null); };
        pipeline.VideoDetected += (_, _) => published++;
        await pipeline.ProcessAsync(new(new("https://cdn.test/opaque"), "GET", 200, "audio/mp4", 500,
            "Media", null, Page, null, new Dictionary<string,string>(), new Dictionary<string,string>(), Context,
            DateTimeOffset.UtcNow, NetworkEventSource.Cdp) { SessionId = pipeline.SessionId }, default);
        await pipeline.CompleteDiscoveryAsync(default);
        Assert.Equal(1, calls);
        Assert.Equal(0, published);
        Assert.NotNull(pipeline.LastValidationError);
    }

    [Fact]
    public async Task ValidationCanEnrichOwnershipAfterNetworkResultAlreadyArrived()
    {
        var pipeline = Create();
        pipeline.InspectOverride = (u, _, _, _) => Task.FromResult<UnifiedMediaPipeline.Probed?>(Media(u, u.AbsolutePath.Contains("audio") ? MediaTrackKind.Audio : MediaTrackKind.Video));
        DetectedVideo? latest = null;
        pipeline.VideoDetected += (_, v) => latest = v;
        await Submit(pipeline, ["https://cdn.test/audio.m4a"]);
        await Submit(pipeline, ["https://cdn.test/video.mp4"], true);
        Assert.DoesNotContain(latest!.Variants, v => v.Tracks.Count == 2);
        await pipeline.ProbePageAsync(Page, null, """{"candidates":[{"url":"https://cdn.test/audio.m4a","contentIdentity":"id:100"}]}""", Context, default);
        await pipeline.CompleteDiscoveryAsync(default);
        Assert.Contains(latest!.Variants, v => v.Tracks.Count == 2);
    }

    [Fact]
    public async Task SeparateHlsVideoAndAudio_WithSameOwner_ArePaired()
    {
        var pipeline = Create();
        pipeline.InspectOverride = (u, _, _, _) =>
        {
            var kind = u.AbsolutePath.Contains("audio") ? MediaTrackKind.Audio : MediaTrackKind.Video;
            var track = Media(u, kind).Track with { Container = "hls" };
            return Task.FromResult<UnifiedMediaPipeline.Probed?>(new(Page, track, 0, kind == MediaTrackKind.Audio ? null : 720,
                new ManifestResolutionResult([MediaVariant.FromTracks("leaf", null, kind == MediaTrackKind.Audio ? null : 720, null, "hls", [track])], false)));
        };
        DetectedVideo? latest = null;
        pipeline.VideoDetected += (_, v) => latest = v;
        await Submit(pipeline, ["https://cdn.test/video.m3u8", "https://cdn.test/audio.m3u8"], true);
        await pipeline.CompleteDiscoveryAsync(default);
        Assert.Contains(latest!.Variants, v => v.Tracks.Count == 2);
        Assert.Contains(latest.Variants, v => v.Tracks.Count == 1 && v.Tracks[0].Kind == MediaTrackKind.Audio);
    }

    [Fact]
    public void OtherFeedItems_DoNotConsumeCurrentItemsCandidateBudget()
    {
        var items = Enumerable.Range(1, 100).Select(i => new { id = i.ToString(), video = new { url = $"https://cdn.test/{i}.mp4" } });
        var result = MediaAddressScanner.Scan(JsonSerializer.Serialize(items), "id:100");
        Assert.Equal("id:100", Assert.Single(result).ContentIdentity);
    }

    [Fact]
    public void StructuredContentId_IsInheritedThroughDataWrapper()
    {
        var result = MediaAddressScanner.Scan("""{"bvid":"BV123","data":{"dash":{"video":[{"id":80,"baseUrl":"https://cdn.test/video.mp4"}],"audio":[{"id":1,"baseUrl":"https://cdn.test/audio.m4a"}]}}}""");
        Assert.Equal(2, result.Count);
        Assert.All(result, a => Assert.Equal("id:BV123", a.ContentIdentity));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompletePair_DoesNotKeepVideoOnlyDuplicateFromEitherSource(bool externalHasAudio)
    {
        var video = Media(new("https://cdn.test/video.mp4"), MediaTrackKind.Video).Track;
        var audio = Media(new("https://cdn.test/audio.m4a"), MediaTrackKind.Audio).Track;
        var external = Substitute.For<IExternalSiteResolver>();
        external.IsAvailable.Returns(true);
        external.ResolveAsync(Arg.Any<Uri>(), Context, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<DetectedVideo>>(
            [new(Guid.NewGuid(), "generic", "100", "Caption", Page, MediaFamily.DirectMp4,
                [MediaVariant.FromTracks("external", null, 720, null, "mp4", externalHasAudio ? [video, audio] : [video])], false)]));
        var pipeline = Create(external);
        pipeline.InspectOverride = (u, _, _, _) => Task.FromResult<UnifiedMediaPipeline.Probed?>(Media(u,
            u.AbsolutePath.Contains("audio") ? MediaTrackKind.Audio : MediaTrackKind.Video));
        DetectedVideo? latest = null;
        pipeline.VideoDetected += (_, v) => latest = v;
        await Submit(pipeline, [video.SourceUrl.AbsoluteUri]); // Network-first: ownership not yet known.
        if (!externalHasAudio) await Submit(pipeline, [audio.SourceUrl.AbsoluteUri], true);
        await pipeline.ProbePageAsync(Page, null, null, Context, default, runExternal: true);
        await pipeline.CompleteDiscoveryAsync(default);
        Assert.Contains(latest!.Variants, v => v.Tracks.Count == 2);
        Assert.DoesNotContain(latest.Variants, v => v.Tracks.All(t => t.Kind == MediaTrackKind.Video));
        Assert.Equal("Caption", latest.DisplayTitle);
    }

    [Fact]
    public async Task HdVideo_CanUseAudioFromSameContentsCombinedLowResolutionStream()
    {
        var pipeline = Create();
        pipeline.InspectOverride = (u, _, _, _) => Task.FromResult<UnifiedMediaPipeline.Probed?>(
            Media(u, u.AbsolutePath.Contains("low") ? MediaTrackKind.Combined : MediaTrackKind.Video, height: u.AbsolutePath.Contains("low") ? 360 : 1080));
        DetectedVideo? latest = null;
        pipeline.VideoDetected += (_, v) => latest = v;
        await Submit(pipeline, ["https://cdn.test/hd.mp4", "https://cdn.test/low.mp4"], true);
        await pipeline.CompleteDiscoveryAsync(default);
        var hd = Assert.Single(latest!.Variants, v => v.Height == 1080);
        Assert.Equal(2, hd.Tracks.Count);
        Assert.Equal("audio-extract", hd.Tracks[1].TrackId);
        Assert.Equal(MediaTrackKind.Audio, hd.Tracks[1].Kind);
    }
}
