using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Browser;
using VideoDownloader.Infrastructure.Detection;
using VideoDownloader.Infrastructure.Download;
using VideoDownloader.Infrastructure.Http;
using VideoDownloader.Infrastructure.Persistence;
using VideoDownloader.Infrastructure.Sites.ExternalResolvers;

namespace VideoDownloader.Infrastructure.Tests;

public class RepairMatrixTests
{
    private static MediaVariant Variant(string url = "https://cdn.test/a.mp4") =>
        MediaVariant.FromCombinedTrack("720", new(url), RequestContext.CreateEmpty(), height: 720) with { ContentIdentity = "id:100" };

    [Fact]
    public async Task ExternalPipeline_RejectsDeniedResourceButPreservesCaptionAndRecoveryIdentity()
    {
        var clients = Substitute.For<IHttpClientFactory>();
        var handler = new Handler(r => new(r.RequestUri!.AbsolutePath.Contains("denied") ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[] { 0,0,0,16,102,116,121,112,105,115,111,109 }) });
        clients.CreateClient("media-primary").Returns(_ => new HttpClient(handler, false));
        var factory = new RequestMessageFactory();
        var validator = new MediaAvailabilityValidator(clients, factory, NullLogger<MediaAvailabilityValidator>.Instance);
        var resolver = Substitute.For<IExternalSiteResolver>();
        resolver.IsAvailable.Returns(true);
        var page = new Uri("https://www.tiktok.com/@creator/video/100");
        resolver.ResolveAsync(Arg.Any<Uri>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DetectedVideo>>([new(Guid.NewGuid(), "tiktok", "100", "external caption", page, MediaFamily.DirectMp4,
                [Variant(), Variant("https://cdn.test/denied.mp4")], false)]));
        var pipeline = new UnifiedMediaPipeline(factory, [resolver], Microsoft.Extensions.Options.Options.Create(new VideoDownloader.Infrastructure.Configuration.AppOptions()), availability: validator);
        DetectedVideo? found = null;
        pipeline.VideoDetected += (_, v) => found = v;
        await pipeline.ProbePageAsync(page, "page title", "{\"caption\":\"original caption\",\"identity\":\"content:100\"}", RequestContext.CreateEmpty(), default, true);
        await pipeline.CompleteDiscoveryAsync(default);
        Assert.NotNull(found);
        Assert.Equal("original caption", found.DisplayTitle);
        Assert.All(found.Variants, v =>
        {
            Assert.DoesNotContain(v.Tracks, t => t.SourceUrl.AbsolutePath.Contains("denied"));
            Assert.Equal("id:100", v.ContentIdentity);
            Assert.Equal(page, v.RecoveryPageUrl);
        });
    }

    [Theory]
    [InlineData(403, false, ErrorCodes.Http403)]
    [InlineData(200, false, ErrorCodes.InvalidFormat)]
    [InlineData(200, true, null)]
    public async Task DirectSample_Rejects403AndHtml(int status, bool media, string? error)
    {
        var handler = new Handler(request =>
        {
            Assert.Equal(65535, request.Headers.Range!.Ranges.Single().To);
            return new((HttpStatusCode)status) { Content = new ByteArrayContent(media ? new byte[] {0,0,0,16,102,116,121,112,105,115,111,109} : "<html>blocked</html>"u8.ToArray()) };
        });
        var clients = Substitute.For<IHttpClientFactory>();
        clients.CreateClient("media-primary").Returns(_ => new HttpClient(handler, false));
        var validator = new MediaAvailabilityValidator(clients, new RequestMessageFactory(), NullLogger<MediaAvailabilityValidator>.Instance);
        if (error is null) await validator.ValidateAsync(Variant(), default);
        else Assert.Equal(error, (await Assert.ThrowsAsync<DownloadException>(() => validator.ValidateAsync(Variant(), default))).ErrorCode);
    }

    [Fact]
    public async Task Recovery_UsesSameContentBackupBeforeResolver()
    {
        var old = Variant();
        var backup = Variant("https://other-cdn.test/a.mp4");
        var resolver = Substitute.For<IExternalSiteResolver>();
        resolver.IsAvailable.Returns(true);
        var resolved = await MediaAddressRenewal.ResolveAsync(new("https://www.tiktok.com/"), old with { Alternatives = [backup] },
            old.RequestContext, [resolver], default, (_,_) => Task.CompletedTask);
        Assert.Equal(backup.SourceUrl, resolved.SourceUrl);
        await resolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default!, default);
    }

    [Fact]
    public async Task Recovery_UsesOriginalPermalinkAndRejectsDifferentContent()
    {
        var old = Variant() with { RecoveryPageUrl = new("https://www.tiktok.com/@creator/video/100") };
        var resolver = Substitute.For<IExternalSiteResolver>();
        resolver.IsAvailable.Returns(true);
        resolver.ResolveAsync(old.RecoveryPageUrl, old.RequestContext, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DetectedVideo>>([new(Guid.NewGuid(), "tiktok", "101", "unchanged caption", old.RecoveryPageUrl, MediaFamily.DirectMp4, [Variant("https://cdn.test/b.mp4")], false)]));
        var ex = await Assert.ThrowsAsync<DownloadException>(() => MediaAddressRenewal.ResolveAsync(new("https://www.tiktok.com/"), old, old.RequestContext,
            [resolver], default, (_,_) => Task.CompletedTask));
        Assert.Equal(ErrorCodes.ContextExpired, ex.ErrorCode);
        await resolver.Received(1).ResolveAsync(old.RecoveryPageUrl, old.RequestContext, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Persistence_RoundTripsIdentityRecoveryAndBackup()
    {
        var value = Variant() with { RecoveryPageUrl = new("https://www.tiktok.com/@creator/video/100"), Alternatives = [Variant("https://backup.test/a.mp4")] };
        var saved = DownloadJobMapper.SerializeVariant(value);
        var restored = DownloadJobMapper.DeserializeVariant(value.SourceUrl.AbsoluteUri, saved.MetaJson, saved.Secret);
        Assert.Equal(value.ContentIdentity, restored.ContentIdentity);
        Assert.Equal(value.RecoveryPageUrl, restored.RecoveryPageUrl);
        Assert.Equal(value.Alternatives[0].SourceUrl, Assert.Single(restored.Alternatives).SourceUrl);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task FrameDiscovery_DiscardsReplacedLoader(bool changed, int expected)
    {
        var reads = 0;
        Task<string> Call(string method, string args) => Task.FromResult(method switch
        {
            "Page.getFrameTree" => JsonSerializer.Serialize(new { frameTree = new { frame = new { id = "root", loaderId = "r" }, childFrames = new[] { new { frame = new { id = "child", loaderId = ++reads > 1 && changed ? "new" : "old" } } } } }),
            "Page.createIsolatedWorld" => "{\"executionContextId\":2}",
            "Runtime.evaluate" => "{\"result\":{\"value\":[{\"url\":\"https://cdn.test/a.mp4\"}]}}",
            _ => throw new InvalidOperationException(method)
        });
        var addresses = await FrameAddressDiscovery.CollectAsync(Call, () => true, default);
        Assert.Equal(expected, addresses.Count);
    }

    [Fact]
    public async Task FrameDiscovery_DiscardsOldSession()
    {
        var result = await FrameAddressDiscovery.CollectAsync((_,_) => Task.FromResult("{\"frameTree\":{\"frame\":{\"id\":\"root\",\"loaderId\":\"r\"}}}"), () => false, default);
        Assert.Empty(result);
    }

    [Fact]
    public void ScriptConfig_ExtractsAddressWithoutCaptionOrExecution()
    {
        const string script = "player({file:'https://cdn.test/a.m3u8?token=1',title:'DO NOT USE'}); evil();";
        Assert.Equal("https://cdn.test/a.m3u8?token=1", Assert.Single(MediaAddressScanner.ScanResponse(script, null, null)).Url);
        Assert.Empty(MediaAddressScanner.ScanResponse(script, "id:100", null));
    }

    [Fact]
    public void Headers_PreserveSingleTypedValuesAndBlockRawCookies()
    {
        var variant = Variant();
        var context = variant.RequestContext with { UserAgent = "typed", Headers = new Dictionary<string,string> { ["User-Agent"] = "raw", ["Referer"] = "https://origin.test/", ["Cookie"] = "secret=1" } };
        using var request = new RequestMessageFactory().Create(variant with { Tracks = [variant.Tracks[0] with { RequestContext = context }] }, HttpMethod.Get, variant.SourceUrl);
        Assert.Equal("typed", Assert.Single(request.Headers.GetValues("User-Agent")));
        Assert.Equal("https://origin.test/", request.Headers.Referrer!.AbsoluteUri);
        Assert.False(request.Headers.Contains("Cookie"));
    }

    [Fact]
    public void Y1_HumanVerificationError_IsClassified()
    {
        Assert.True(YtDlpResolver.IsHumanVerificationError("ERROR: Sign in to confirm you're not a bot"));
        Assert.True(YtDlpResolver.IsHumanVerificationError("HTTP Error 412: Precondition Failed"));
        Assert.False(YtDlpResolver.IsHumanVerificationError("ERROR: Unable to extract video"));
        Assert.False(YtDlpResolver.IsHumanVerificationError(null));
    }

    [Fact]
    public async Task Y1_HumanVerification_StopsClientPollingAndSurfacesReason()
    {
        var calls = 0;
        var resolver = Substitute.For<IExternalSiteResolver>();
        resolver.IsAvailable.Returns(true);
        resolver.LastFailureIsHumanVerification.Returns(true);
        resolver.LastError.Returns("Sign in to confirm you're not a bot");
        resolver.ResolveAsync(Arg.Any<Uri>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                return Task.FromResult<IReadOnlyList<DetectedVideo>>([]);
            });
        var pipeline = new UnifiedMediaPipeline(new RequestMessageFactory(), [resolver],
            Microsoft.Extensions.Options.Options.Create(new VideoDownloader.Infrastructure.Configuration.AppOptions()));
        await pipeline.ProbePageAsync(new("https://www.youtube.com/watch?v=abc12345678"), null, null, RequestContext.CreateEmpty(), default, true);
        await pipeline.CompleteDiscoveryAsync(default);
        Assert.Equal(1, calls);
        Assert.Contains(ErrorCodes.HumanVerification, pipeline.LastExternalError);
        Assert.Contains("not a bot", pipeline.LastExternalError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(pipeline.LastProbeDecisions, d => d.Reason == ErrorCodes.HumanVerification);
    }

    [Theory]
    [InlineData("https://www.youtube.com/", null, false)]
    [InlineData("https://www.youtube.com/watch?v=abc12345678", null, true)]
    [InlineData("https://www.tiktok.com/foryou", null, false)]
    [InlineData("https://www.tiktok.com/foryou", "content:7123456789012345678", true)]
    [InlineData("https://www.tiktok.com/@u/video/7123456789012345678", null, true)]
    public void Y3_ExternalResolve_OnlyForConcreteVideo(string page, string? identity, bool expected) =>
        Assert.Equal(expected, UnifiedMediaPipeline.ShouldRunExternalResolve(new(page), identity));

    [Fact]
    public async Task T1_PreferredVideo403_UsesAlternateAndKeepsAudio()
    {
        var clients = Substitute.For<IHttpClientFactory>();
        var handler = new Handler(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path.Contains("primary"))
                return new(HttpStatusCode.Forbidden) { Content = new ByteArrayContent([1]) };
            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0, 0, 0, 16, 102, 116, 121, 112, 105, 115, 111, 109])
            };
        });
        clients.CreateClient("media-primary").Returns(_ => new HttpClient(handler, false));
        var factory = new RequestMessageFactory();
        var validator = new MediaAvailabilityValidator(clients, factory, NullLogger<MediaAvailabilityValidator>.Instance);
        var page = new Uri("https://www.tiktok.com/@creator/video/100");
        var empty = RequestContext.CreateEmpty();
        var primary = MediaVariant.FromTracks("720", null, 720, null, "mp4",
            [new MediaTrack("v", MediaTrackKind.Video, new("https://cdn.test/primary.mp4"), "h264", "mp4", 1, 2_000_000, empty)]);
        var backup = MediaVariant.FromTracks("720b", null, 720, null, "mp4",
            [new MediaTrack("v", MediaTrackKind.Video, new("https://cdn.test/backup.mp4"), "h264", "mp4", 1, 2_000_000, empty)]);
        var audio = MediaVariant.FromTracks("audio", null, null, null, "mp4",
            [new MediaTrack("a", MediaTrackKind.Audio, new("https://cdn.test/audio.m4a"), "aac", "mp4", 1, 200_000, empty)]);
        var resolver = Substitute.For<IExternalSiteResolver>();
        resolver.IsAvailable.Returns(true);
        resolver.LastFailureIsHumanVerification.Returns(false);
        resolver.ResolveAsync(Arg.Any<Uri>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DetectedVideo>>([
                new(Guid.NewGuid(), "tiktok", "100", "caption", page, MediaFamily.DirectMp4,
                    [primary with { ContentIdentity = "id:100" }, backup with { ContentIdentity = "id:100" }, audio with { ContentIdentity = "id:100" }], false)
            ]));
        var pipeline = new UnifiedMediaPipeline(factory, [resolver],
            Microsoft.Extensions.Options.Options.Create(new VideoDownloader.Infrastructure.Configuration.AppOptions()), availability: validator);
        DetectedVideo? found = null;
        pipeline.VideoDetected += (_, v) => found = v;
        await pipeline.ProbePageAsync(page, null, "{\"caption\":\"caption\",\"identity\":\"content:100\"}", empty, default, true);
        await pipeline.CompleteDiscoveryAsync(default);
        Assert.NotNull(found);
        Assert.Contains(found!.Variants, v => v.Tracks.Any(t => t.SourceUrl.AbsolutePath.Contains("backup")));
        Assert.Contains(found.Variants, v => v.Tracks.Any(t => t.Kind == MediaTrackKind.Audio));
        Assert.DoesNotContain(found.Variants.SelectMany(v => v.Tracks), t => t.SourceUrl.AbsolutePath.Contains("primary"));
        Assert.Equal(MediaAvailabilityKind.Complete, found.Availability);
        Assert.Contains(pipeline.LastProbeDecisions, d => d.Outcome == "accepted" && d.Reason == "alternate_ok");
    }

    [Fact]
    public async Task T2_VideoAllDenied_AudioOnlyIsPartialSuccess()
    {
        var clients = Substitute.For<IHttpClientFactory>();
        var handler = new Handler(r =>
        {
            if (r.RequestUri!.AbsolutePath.Contains("video"))
                return new(HttpStatusCode.Forbidden) { Content = new ByteArrayContent([1]) };
            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0, 0, 0, 16, 102, 116, 121, 112, 105, 115, 111, 109])
            };
        });
        clients.CreateClient("media-primary").Returns(_ => new HttpClient(handler, false));
        var factory = new RequestMessageFactory();
        var validator = new MediaAvailabilityValidator(clients, factory, NullLogger<MediaAvailabilityValidator>.Instance);
        var page = new Uri("https://www.tiktok.com/@creator/video/100");
        var empty = RequestContext.CreateEmpty();
        var video = MediaVariant.FromTracks("720", null, 720, null, "mp4",
            [new MediaTrack("v", MediaTrackKind.Video, new("https://cdn.test/video.mp4"), "h264", "mp4", 1, 2_000_000, empty)]) with { ContentIdentity = "id:100" };
        var audio = MediaVariant.FromTracks("audio", null, null, null, "mp4",
            [new MediaTrack("a", MediaTrackKind.Audio, new("https://cdn.test/audio.m4a"), "aac", "mp4", 1, 200_000, empty)]) with { ContentIdentity = "id:100" };
        var resolver = Substitute.For<IExternalSiteResolver>();
        resolver.IsAvailable.Returns(true);
        resolver.LastFailureIsHumanVerification.Returns(false);
        resolver.ResolveAsync(Arg.Any<Uri>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DetectedVideo>>([
                new(Guid.NewGuid(), "tiktok", "100", "caption", page, MediaFamily.DirectMp4, [video, audio], false)
            ]));
        var pipeline = new UnifiedMediaPipeline(factory, [resolver],
            Microsoft.Extensions.Options.Options.Create(new VideoDownloader.Infrastructure.Configuration.AppOptions()), availability: validator);
        DetectedVideo? found = null;
        pipeline.VideoDetected += (_, v) => found = v;
        await pipeline.ProbePageAsync(page, null, "{\"caption\":\"caption\",\"identity\":\"content:100\"}", empty, default, true);
        await pipeline.CompleteDiscoveryAsync(default);
        Assert.NotNull(found);
        Assert.Equal(MediaAvailabilityKind.VideoDenied, found!.Availability);
        Assert.All(found.Variants, v => Assert.All(v.Tracks, t => Assert.Equal(MediaTrackKind.Audio, t.Kind)));
        Assert.Contains(ErrorCodes.AudioOnly, pipeline.LastExternalError);
        Assert.Contains(ErrorCodes.Http403, found.Metadata!["videoFailure"]);
        Assert.DoesNotContain(pipeline.LastProbeDecisions, d => d.Outcome == "accepted" && d.MediaKind is "video" or "av");
        Assert.Contains(pipeline.LastProbeDecisions, d => d.MediaKind == "audio_only" && d.Reason == nameof(MediaAvailabilityKind.VideoDenied));
    }

    [Fact]
    public async Task C1_RecoveryAfterClear_DoesNotPolluteNewSession()
    {
        var gate = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var clients = Substitute.For<IHttpClientFactory>();
        clients.CreateClient("media-primary").Returns(_ => new HttpClient(new Handler(_ =>
        {
            gate.TrySetResult();
            release.Task.Wait(TimeSpan.FromSeconds(5));
            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0, 0, 0, 16, 102, 116, 121, 112, 105, 115, 111, 109])
            };
        }), false));
        var pageA = new Uri("https://www.tiktok.com/@creator/video/100");
        var pageB = new Uri("https://www.tiktok.com/@creator/video/200");
        var empty = RequestContext.CreateEmpty();
        var resolver = Substitute.For<IExternalSiteResolver>();
        resolver.IsAvailable.Returns(true);
        resolver.LastFailureIsHumanVerification.Returns(false);
        resolver.ResolveAsync(Arg.Any<Uri>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var page = (Uri)ci[0];
                var id = page.AbsolutePath.Contains("100") ? "100" : "200";
                return Task.FromResult<IReadOnlyList<DetectedVideo>>([
                    new(Guid.NewGuid(), "tiktok", id, "caption-" + id, page, MediaFamily.DirectMp4,
                        [Variant($"https://cdn.test/{id}.mp4") with { ContentIdentity = "id:" + id }], false)
                ]);
            });
        var pipeline = new UnifiedMediaPipeline(new RequestMessageFactory(), [resolver],
            Microsoft.Extensions.Options.Options.Create(new VideoDownloader.Infrastructure.Configuration.AppOptions()),
            availability: new MediaAvailabilityValidator(clients, new RequestMessageFactory(), NullLogger<MediaAvailabilityValidator>.Instance));
        DetectedVideo? found = null;
        pipeline.VideoDetected += (_, v) => found = v;
        var probeA = pipeline.ProbePageAsync(pageA, null, "{\"identity\":\"content:100\"}", empty, default, true);
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        pipeline.Clear();
        release.TrySetResult();
        await probeA;
        await pipeline.ProbePageAsync(pageB, null, "{\"identity\":\"content:200\",\"caption\":\"B\"}", empty, default, true);
        await pipeline.CompleteDiscoveryAsync(default);
        Assert.NotNull(found);
        Assert.Equal("id:200", found!.Variants[0].ContentIdentity);
        Assert.DoesNotContain("100", found.Variants[0].SourceUrl.AbsoluteUri);
    }

    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
