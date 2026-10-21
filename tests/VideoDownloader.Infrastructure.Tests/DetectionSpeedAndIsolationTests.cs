using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Sites;
using VideoDownloader.Infrastructure.Browser;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection;
using VideoDownloader.Infrastructure.Detection.Sites.YouTube;

namespace VideoDownloader.Infrastructure.Tests;

public sealed class DetectionSpeedAndIsolationTests
{
    [Fact]
    public async Task SameYouTubeIdWithDurationJitterDoesNotRetireOrRestartTheProbe()
    {
        var pipeline = new UnifiedMediaPipeline(Substitute.For<IRequestMessageFactory>(), [], Options.Create(new AppOptions()));
        using var host = new WebView2Host(Options.Create(new AppOptions()), Substitute.For<INetworkEventNormalizer>(), pipeline, NullLogger<WebView2Host>.Instance);
        var page = new Uri("https://www.youtube.com/watch?v=abcdefghijk");
        host.ObserveVideoIdentity("www.youtube.com:content:youtube:abcdefghijk", page, 30);
        await Task.Delay(450);
        var changing = 0;
        var restarted = 0;
        host.MediaSessionChanging += (_, _) => changing++;
        host.MediaSessionChanged += (_, _) => restarted++;
        host.ObserveVideoIdentity("content:youtube:abcdefghijk", page, 90);
        await Task.Delay(450);
        Assert.Equal(0, changing);
        Assert.Equal(0, restarted);
        host.ObserveVideoIdentity("content:youtube:lmnopqrstuv", new Uri("https://www.youtube.com/watch?v=lmnopqrstuv"), 90);
        await Task.Delay(450);
        Assert.Equal(1, changing);
        Assert.Equal(1, restarted);
    }

    [Fact]
    public async Task GenericValidatedVideo_IsPublishedBeforeFinalPageResult()
    {
        var pipeline = new UnifiedMediaPipeline(Substitute.For<IRequestMessageFactory>(), [], Options.Create(new AppOptions()));
        var page = new Uri("https://example.test/watch");
        var inspected = new TaskCompletionSource<UnifiedMediaPipeline.Probed?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var visible = new TaskCompletionSource<DetectedVideo>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.InspectOverride = (_, _, _, _) => inspected.Task;
        var finalPages = 0;
        pipeline.VideoDetected += (_, video) => visible.TrySetResult(video);
        pipeline.PageProbed += (_, _) => finalPages++;
        var url = new Uri("https://cdn.test/movie.mp4");
        await pipeline.ProbePageAsync(page, "current", """{"media":["https://cdn.test/movie.mp4"]}""",
            RequestContext.CreateEmpty(), default);
        inspected.SetResult(new(page, MediaVariant.FromCombinedTrack("video", url,
            RequestContext.CreateEmpty(), contentLength: 3_000_000).Tracks[0], 30, 720));
        var result = await visible.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(pipeline.IsCompleted);
        Assert.Equal(0, finalPages);
        Assert.Equal(url, result.Variants[0].SourceUrl);
        await pipeline.CompleteDiscoveryAsync(default);
        Assert.Equal(1, finalPages);
    }

    private static RoutedMediaDetectionPipeline Route(IExclusiveSiteMediaDetector detector) => new(
        new UnifiedMediaPipeline(Substitute.For<IRequestMessageFactory>(), [], Options.Create(new AppOptions())),
        new SiteDetectionRouter(), new ExclusiveSiteMediaDetectorResolver([detector]),
        NullLogger<RoutedMediaDetectionPipeline>.Instance);

    [Fact]
    public async Task BrowserObservation_ForwardsExternalFalse_IncludingDiscoveryScope()
    {
        var detector = Substitute.For<IExclusiveSiteMediaDetector>();
        detector.Matches(Arg.Any<Uri>()).Returns(true);
        var pipeline = Route(detector);
        var page = new Uri("https://www.youtube.com/watch?v=first");
        var context = RequestContext.CreateEmpty();
        await pipeline.ProbePageAsync(page, "title", "{}", context, default, runExternal: false);
        await detector.Received(1).ProcessPageObservationAsync(page, "title", "{}", context, default, false);
        using var scope = pipeline.BeginDiscovery(pipeline.SessionId);
        await scope!.SubmitAsync(page, "{}", context, default);
        await detector.Received(1).ProcessPageObservationAsync(page, null, "{}", context, default, false);
    }

    [Fact]
    public void QueuedOldDescriptor_CannotBeRestampedAsTheNewSession()
    {
        var detector = Substitute.For<IExclusiveSiteMediaDetector>();
        detector.Matches(Arg.Any<Uri>()).Returns(true);
        var pipeline = Route(detector);
        var page = new Uri("https://www.youtube.com/watch?v=first");
        pipeline.Reenter(page);
        var retired = pipeline.SessionId;
        pipeline.Reenter(new Uri("https://www.youtube.com/watch?v=second"));
        var published = new List<DetectedVideo>();
        pipeline.VideoDetected += (_, value) => published.Add(value);
        var descriptor = new MediaDescriptor(SiteIds.YouTube, page, "first", MediaContentType.Video,
            null, null, [], RequestContext.CreateEmpty(), 1, "old") { SessionId = retired };
        detector.DescriptorsReady += Raise.Event<EventHandler<IReadOnlyList<MediaDescriptor>>>(detector, new[] { descriptor });
        Assert.Empty(published);
        Assert.False(pipeline.IsCompleted);
    }

    [Fact]
    public async Task OldNetworkEvent_CannotRouteBackToTheRetiredWork()
    {
        var detector = Substitute.For<IExclusiveSiteMediaDetector>();
        detector.Matches(Arg.Any<Uri>()).Returns(true);
        var pipeline = Route(detector);
        var first = new Uri("https://www.youtube.com/watch?v=first");
        var second = new Uri("https://www.youtube.com/watch?v=second");
        pipeline.Reenter(first);
        var retired = pipeline.SessionId;
        pipeline.Reenter(second);
        var current = pipeline.SessionId;
        var network = new NormalizedNetworkEvent(new Uri("https://cdn.test/old.mp4"), "GET", 200,
            "video/mp4", 2_000_000, "Media", null, first, null, new Dictionary<string, string>(),
            new Dictionary<string, string>(), RequestContext.CreateEmpty(), DateTimeOffset.UtcNow,
            NetworkEventSource.Cdp) { SessionId = retired };
        await pipeline.ProcessAsync(network, default);
        Assert.Equal(current, pipeline.SessionId);
        detector.DidNotReceive().BeginSession(first, Arg.Is<Guid>(id => id != retired));
    }

    [Fact]
    public async Task YouTubePlayerCombinedFormat_IsUsableWithoutSeparateAudio()
    {
        var stats = Substitute.For<IProbeMethodStats>();
        var detector = new YouTubeMediaDetector(NullLogger<YouTubeMediaDetector>.Instance,
            new YouTubeYtDlpExtractor(Options.Create(new AppOptions()), NullLogger<YouTubeYtDlpExtractor>.Instance, stats), stats);
        var page = new Uri("https://www.youtube.com/watch?v=current");
        detector.BeginSession(page, Guid.NewGuid());
        var results = new List<MediaDescriptor>();
        detector.DescriptorsReady += (_, rows) => results.AddRange(rows);
        await detector.ProcessPageObservationAsync(page, "current",
            """{"media":["https://rr.googlevideo.com/videoplayback?itag=18&mime=video"],"combinedMedia":["https://rr.googlevideo.com/videoplayback?itag=18&mime=video"]}""",
            RequestContext.CreateEmpty(), default, runExternal: false);
        Assert.Single(results);
        Assert.Equal(MediaTrackKind.Combined, results[0].Video!.Kind);
        Assert.True(VideoDownloader.Core.Detection.DetectionReadiness.IsUsable(
            MediaDescriptorMapper.ToDetectedVideo(results[0], results[0].SessionId)));
    }

    [Fact]
    public async Task YouTubeNetworkPair_IsPublishedWithoutWaitingForFinalCompletion()
    {
        var options = Options.Create(new AppOptions());
        var detector = new YouTubeMediaDetector(NullLogger<YouTubeMediaDetector>.Instance,
            new YouTubeYtDlpExtractor(options, NullLogger<YouTubeYtDlpExtractor>.Instance, Substitute.For<IProbeMethodStats>()),
            Substitute.For<IProbeMethodStats>());
        var page = new Uri("https://www.youtube.com/watch?v=current");
        detector.BeginSession(page, Guid.NewGuid());
        var results = new List<MediaDescriptor>();
        detector.DescriptorsReady += (_, rows) => results.AddRange(rows);
        await detector.ProcessPageObservationAsync(page, "current",
            """{"media":["https://rr.googlevideo.com/videoplayback?mime=video&itag=137"]}""",
            RequestContext.CreateEmpty(), default, runExternal: false);
        Assert.Empty(results); // Silent/incomplete adaptive track is not a fast success.
        await detector.ProcessPageObservationAsync(page, "current",
            """{"media":["https://rr.googlevideo.com/videoplayback?mime=audio&itag=140"]}""",
            RequestContext.CreateEmpty(), default, runExternal: false);
        Assert.NotEmpty(results);
        Assert.NotNull(results[^1].Video);
        Assert.NotNull(results[^1].Audio);
        var published = results.Count;
        await detector.CompleteAsync(default);
        await detector.ProcessPageObservationAsync(page, "current",
            """{"media":["https://rr.googlevideo.com/videoplayback?mime=audio&itag=141"]}""",
            RequestContext.CreateEmpty(), default, runExternal: false);
        Assert.Equal(published, results.Count);
    }

    [Fact]
    public async Task InPageSwipe_ClearsSynchronouslyBeforeTheDebouncedSessionCommit()
    {
        var pipeline = new UnifiedMediaPipeline(Substitute.For<IRequestMessageFactory>(), [], Options.Create(new AppOptions()));
        using var host = new WebView2Host(Options.Create(new AppOptions()), Substitute.For<INetworkEventNormalizer>(),
            pipeline, NullLogger<WebView2Host>.Instance);
        var page = new Uri("https://example.test/feed");
        host.ObserveVideoIdentity("video:A", page);
        await Task.Delay(450);
        var cleared = false;
        var committed = false;
        host.MediaSessionChanging += (_, _) => cleared = true;
        host.MediaSessionChanged += (_, _) => committed = true;
        host.ObserveVideoIdentity("video:B", page);
        Assert.True(cleared);
        Assert.False(committed);
        await Task.Delay(450);
        Assert.True(committed);
        Assert.Equal("video:B", host.CurrentMediaSessionKey);
    }

    [Fact]
    public async Task QuickSwipeBack_ReopensOldIdentityAfterItsDisplayWasCleared()
    {
        var pipeline = new UnifiedMediaPipeline(Substitute.For<IRequestMessageFactory>(), [], Options.Create(new AppOptions()));
        using var host = new WebView2Host(Options.Create(new AppOptions()), Substitute.For<INetworkEventNormalizer>(),
            pipeline, NullLogger<WebView2Host>.Instance);
        var page = new Uri("https://example.test/feed");
        host.ObserveVideoIdentity("video:A", page);
        await Task.Delay(450);
        MediaSessionChangedEventArgs? committed = null;
        host.MediaSessionChanged += (_, value) => committed = value;
        host.ObserveVideoIdentity("video:B", page);
        host.ObserveVideoIdentity("video:A", page);
        await Task.Delay(600);
        Assert.True(committed?.ForceRestart);
        Assert.Equal("video:A", host.CurrentMediaSessionKey);
    }

    [Fact]
    public async Task RepeatedSameIdentity_DoesNotPostponePendingDurationRevision()
    {
        var pipeline = new UnifiedMediaPipeline(Substitute.For<IRequestMessageFactory>(), [], Options.Create(new AppOptions()));
        using var host = new WebView2Host(Options.Create(new AppOptions()), Substitute.For<INetworkEventNormalizer>(),
            pipeline, NullLogger<WebView2Host>.Instance);
        var page = new Uri("https://example.test/feed");
        host.ObserveVideoIdentity("video:A", page, 30);
        await Task.Delay(450);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.MediaSessionChanged += (_, value) => { if (value.ForceRestart) committed.TrySetResult(); };
        host.ObserveVideoIdentity("video:A", page, 90);
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(100);
            host.ObserveVideoIdentity("video:A", page, 90);
        }
        Assert.True(committed.Task.IsCompleted);
    }
}
