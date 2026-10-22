using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;
using VideoDownloader.Infrastructure.Detection.Sites.YouTube;

namespace VideoDownloader.Infrastructure.Tests;

public class YouTubeMediaDetectorPublicationTests
{
    [Fact]
    public async Task Passive_Media_Is_Not_Published_Until_External_Formats_Arrive()
    {
        var extractor = new GatedYouTubeExtractor();
        var detector = CreateDetector(extractor);
        var published = Capture(detector);
        var page = Watch("B-id");
        detector.BeginSession(page, Guid.NewGuid());

        var observation = detector.ProcessPageObservationAsync(
            page, "B title", null, RequestContext.CreateEmpty(), CancellationToken.None);

        await detector.ProcessNetworkAsync(Event(page, VideoUrl("b-video")), CancellationToken.None);
        await detector.ProcessNetworkAsync(Event(page, AudioUrl("b-audio")), CancellationToken.None);
        Assert.Empty(published);

        extractor.Complete(Video(page, "B-id", Av(1080, "best"), Av(720, "alt")));
        await observation;
        Assert.Empty(published);
        await detector.CompleteAsync(CancellationToken.None);

        var descriptor = Assert.Single(published);
        Assert.Equal("B-id", descriptor.MediaId);
        Assert.Equal(2, descriptor.Formats.Count);
        Assert.Equal([1080, 720], descriptor.Formats.Select(v => v.Height).ToArray());
        Assert.DoesNotContain(descriptor.Formats, v =>
            v.SourceUrl.AbsoluteUri.Contains("b-video", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Previous_Video_Network_Media_Is_Ignored_After_Content_Switch()
    {
        var extractor = new GatedYouTubeExtractor();
        var detector = CreateDetector(extractor);
        var published = Capture(detector);
        var current = Watch("B-id");
        var previous = Watch("A-id");
        detector.BeginSession(current, Guid.NewGuid());

        var observation = detector.ProcessPageObservationAsync(
            current, "B title", null, RequestContext.CreateEmpty(), CancellationToken.None);

        await detector.ProcessNetworkAsync(Event(previous, VideoUrl("stale-video")), CancellationToken.None);
        await detector.ProcessNetworkAsync(Event(previous, AudioUrl("stale-audio")), CancellationToken.None);

        extractor.Complete();
        await observation;
        Assert.Empty(published);
        await detector.CompleteAsync(CancellationToken.None);
        Assert.True(detector.Failed);

        await detector.ProcessNetworkAsync(Event(current, VideoUrl("current-video")), CancellationToken.None);
        await detector.ProcessNetworkAsync(Event(current, AudioUrl("current-audio")), CancellationToken.None);

        var descriptor = Assert.Single(published);
        Assert.Equal("B-id", descriptor.MediaId);
        Assert.Contains("current-video", descriptor.Video!.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stale", descriptor.Video.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stale_Page_Observation_Cannot_Regress_Current_Video()
    {
        var extractor = new GatedYouTubeExtractor();
        var detector = CreateDetector(extractor);
        var published = Capture(detector);
        var current = Watch("B-id");
        var previous = Watch("A-id");
        detector.BeginSession(current, Guid.NewGuid());

        var observation = detector.ProcessPageObservationAsync(
            current, "B title", null, RequestContext.CreateEmpty(), CancellationToken.None);

        await detector.ProcessPageObservationAsync(
            previous,
            "Old A title",
            """{"identity":"content:youtube:A-id"}""",
            RequestContext.CreateEmpty(),
            CancellationToken.None,
            runExternal: false);
        Assert.Empty(published);

        extractor.Complete(Video(current, "B-id", Av(1080, "best")));
        await observation;
        Assert.Empty(published);
        await detector.CompleteAsync(CancellationToken.None);

        var descriptor = Assert.Single(published);
        Assert.Equal("B-id", descriptor.MediaId);
        Assert.Equal(current, descriptor.PageUrl);
        Assert.Equal("B title", descriptor.DisplayTitle);
    }

    [Fact]
    public async Task Network_Fallback_Publishes_Only_After_External_Is_Unavailable()
    {
        var detector = CreateDetector(new GatedYouTubeExtractor(available: false, completesImmediately: true));
        var published = Capture(detector);
        var page = Watch("B-id");
        detector.BeginSession(page, Guid.NewGuid());

        await detector.ProcessNetworkAsync(Event(page, VideoUrl("current-video")), CancellationToken.None);
        await detector.ProcessNetworkAsync(Event(page, AudioUrl("current-audio")), CancellationToken.None);
        Assert.Empty(published);

        await detector.ProcessPageObservationAsync(
            page, "B title", null, RequestContext.CreateEmpty(), CancellationToken.None);
        Assert.Empty(published);
        await detector.CompleteAsync(CancellationToken.None);

        var descriptor = Assert.Single(published);
        Assert.Equal("B-id", descriptor.MediaId);
        Assert.Empty(descriptor.Formats);
        Assert.Contains("current-video", descriptor.Video!.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(descriptor.Audio);
    }

    [Fact]
    public async Task Complete_Waits_For_InFlight_Discovery_And_Publishes_Once()
    {
        var extractor = new GatedYouTubeExtractor();
        var detector = CreateDetector(extractor);
        var published = Capture(detector);
        var page = Watch("current");
        detector.BeginSession(page, Guid.NewGuid());
        var observation = Observe(detector, page);
        await NetworkPair(detector, page);

        var firstComplete = detector.CompleteAsync(default);
        var secondComplete = detector.CompleteAsync(default);
        Assert.False(firstComplete.IsCompleted);
        Assert.False(secondComplete.IsCompleted);
        Assert.Empty(published);

        await Observe(detector, page, MediaJson(AudioUrl("late-audio")), runExternal: false);
        Assert.Empty(published);
        extractor.Complete(Video(page, "current", Av(1080, "full"), Av(720, "full-alt")));
        await Task.WhenAll(observation, firstComplete, secondComplete).WaitAsync(TimeSpan.FromSeconds(5));

        var result = Assert.Single(published);
        Assert.Equal(2, result.Formats.Count);
        await detector.CompleteAsync(default);
        await NetworkPair(detector, page);
        await Observe(detector, page);
        Assert.Single(published);
        Assert.Equal(1, extractor.ResolveCount);
    }

    [Fact]
    public async Task Complete_Before_External_Start_Does_Not_Publish_Partial_Media()
    {
        var extractor = new GatedYouTubeExtractor();
        var detector = CreateDetector(extractor);
        var published = Capture(detector);
        var page = Watch("current");
        detector.BeginSession(page, Guid.NewGuid());
        await NetworkPair(detector, page);
        await Observe(detector, page, runExternal: false);
        await detector.CompleteAsync(default);
        Assert.Empty(published);
        Assert.Equal(0, extractor.ResolveCount);

        var observation = Observe(detector, page);
        Assert.Empty(published);
        extractor.Complete(Video(page, "current", Av(1080, "full")));
        await observation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(Assert.Single(published).Formats);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("external")]
    [InlineData("network")]
    [InlineData("dom")]
    public async Task All_Entry_Points_Use_The_Same_Final_Publication_Gate(string lastEntry)
    {
        var stats = Substitute.For<IProbeMethodStats>();
        var extractor = new GatedYouTubeExtractor();
        var detector = CreateDetector(extractor, stats);
        var published = Capture(detector);
        var page = Watch("current");
        detector.BeginSession(page, Guid.NewGuid());
        var observation = Observe(detector, page);
        await detector.ProcessNetworkAsync(Event(page, VideoUrl("video")), default);
        if (lastEntry is "complete" or "external")
            await detector.ProcessNetworkAsync(Event(page, AudioUrl("audio")), default);
        Assert.Empty(published);

        Task? complete = null;
        if (lastEntry != "complete") complete = detector.CompleteAsync(default);
        Assert.Empty(published);
        extractor.Complete();
        await observation.WaitAsync(TimeSpan.FromSeconds(5));
        if (complete is not null) await complete.WaitAsync(TimeSpan.FromSeconds(5));

        if (lastEntry != "external") Assert.Empty(published);
        if (lastEntry == "complete") await detector.CompleteAsync(default);
        if (lastEntry is "network" or "dom")
        {
            Assert.True(detector.Failed);
            if (lastEntry == "network")
                await detector.ProcessNetworkAsync(Event(page, AudioUrl("audio")), default);
            else
                await Observe(detector, page, MediaJson(AudioUrl("audio")), runExternal: false);
        }

        var result = Assert.Single(published);
        Assert.False(detector.Failed);
        Assert.Null(detector.FailureReason);
        Assert.True(DetectionReadiness.IsUsable(MediaDescriptorMapper.ToDetectedVideo(result, result.SessionId)));
        await detector.CompleteAsync(default);
        await NetworkPair(detector, page);
        await Observe(detector, page, MediaJson(AudioUrl("extra")), runExternal: false);
        Assert.Single(published);
        stats.Received(1).RecordTimed(SiteIds.YouTube, ProbeMethods.NetworkMedia, true, Arg.Any<TimeSpan>());
    }

    [Theory]
    [InlineData("success")]
    [InlineData("empty")]
    [InlineData("error")]
    public async Task Repeated_External_Observations_Resolve_At_Most_Once(string outcome)
    {
        var extractor = new GatedYouTubeExtractor();
        var detector = CreateDetector(extractor);
        var published = Capture(detector);
        var page = Watch("current");
        detector.BeginSession(page, Guid.NewGuid());
        var observation = Observe(detector, page);
        await Observe(detector, page);
        Assert.Equal(1, extractor.ResolveCount);

        if (outcome == "error") extractor.Fail(new InvalidOperationException("fake extraction failure"));
        else if (outcome == "success") extractor.Complete(Video(page, "current", Av(1080, "full")));
        else extractor.Complete();
        await observation.WaitAsync(TimeSpan.FromSeconds(5));
        await Observe(detector, page);
        Assert.Equal(1, extractor.ResolveCount);
        Assert.Empty(published);

        await detector.CompleteAsync(default);
        await Observe(detector, page);
        Assert.Equal(1, extractor.ResolveCount);
        if (outcome == "success") Assert.Single(published);
        else
        {
            Assert.True(detector.Failed);
            await NetworkPair(detector, page);
            Assert.Single(published);
            Assert.False(detector.Failed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_External_Must_Join_Before_Independent_Complete_Can_Fall_Back(bool completeBeforeJoin)
    {
        var extractor = new GatedYouTubeExtractor(ignoreCancellation: true);
        var detector = CreateDetector(extractor);
        var published = Capture(detector);
        var page = Watch("current");
        detector.BeginSession(page, Guid.NewGuid());
        using var budget = new CancellationTokenSource();
        var observation = Observe(detector, page, ct: budget.Token);
        await NetworkPair(detector, page);
        var cancelledComplete = detector.CompleteAsync(budget.Token);
        budget.Cancel();
        await extractor.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledComplete.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(observation.IsCompleted);
        Assert.Empty(published);

        Task? complete = completeBeforeJoin ? detector.CompleteAsync(default) : null;
        if (complete is not null) Assert.False(complete.IsCompleted);
        await Observe(detector, page, MediaJson(AudioUrl("late")), runExternal: false);
        Assert.Empty(published);
        extractor.Complete(Video(page, "current", Av(1080, "cancelled-result")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observation.WaitAsync(TimeSpan.FromSeconds(5)));
        if (complete is null)
        {
            Assert.Empty(published);
            complete = detector.CompleteAsync(default);
        }
        await complete.WaitAsync(TimeSpan.FromSeconds(5));
        var result = Assert.Single(published);
        Assert.Empty(result.Formats);
        Assert.DoesNotContain("cancelled-result", result.Video!.SourceUrl.AbsoluteUri);
        await Observe(detector, page);
        Assert.Equal(1, extractor.ResolveCount);
    }

    [Fact]
    public async Task Cancelled_Discovery_Does_Not_Reopen_The_Resolve_Slot()
    {
        var extractor = new GatedYouTubeExtractor();
        var detector = CreateDetector(extractor);
        var published = Capture(detector);
        var page = Watch("current");
        detector.BeginSession(page, Guid.NewGuid());
        using var budget = new CancellationTokenSource();
        var observation = Observe(detector, page, ct: budget.Token);
        budget.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observation.WaitAsync(TimeSpan.FromSeconds(5)));
        extractor.Complete();

        await Observe(detector, page).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, extractor.ResolveCount);
        Assert.Empty(published);
        await detector.CompleteAsync(default);
        Assert.True(detector.Failed);
        await NetworkPair(detector, page);
        Assert.Single(published);
        Assert.False(detector.Failed);
    }

    [Fact]
    public async Task Cancelling_Complete_Only_Cancels_The_Wait_Not_The_Extractor()
    {
        var extractor = new GatedYouTubeExtractor();
        var detector = CreateDetector(extractor);
        var published = Capture(detector);
        var page = Watch("current");
        detector.BeginSession(page, Guid.NewGuid());
        var observation = Observe(detector, page);
        using var wait = new CancellationTokenSource();
        var cancelledComplete = detector.CompleteAsync(wait.Token);
        wait.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledComplete.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(extractor.CancellationObserved.IsCompleted);
        Assert.False(observation.IsCompleted);
        Assert.Empty(published);

        var complete = detector.CompleteAsync(default);
        Assert.False(complete.IsCompleted);
        extractor.Complete(Video(page, "current", Av(1080, "full")));
        await Task.WhenAll(observation, complete).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(published);
        Assert.Equal(1, extractor.ResolveCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Retired_Completion_Cannot_Finalize_A_New_Session(bool sameVideo, bool sameSessionId)
    {
        var extractor = new GatedYouTubeExtractor(ignoreCancellation: true);
        var detector = CreateDetector(extractor);
        var published = Capture(detector);
        var oldPage = Watch("old");
        var currentId = sameVideo ? "old" : "new";
        var page = Watch(currentId);
        var oldSession = Guid.NewGuid();
        var session = sameSessionId ? oldSession : Guid.NewGuid();
        detector.BeginSession(oldPage, oldSession);
        var oldObservation = Observe(detector, oldPage);
        var oldComplete = detector.CompleteAsync(default);

        detector.BeginSession(page, session);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldComplete.WaitAsync(TimeSpan.FromSeconds(5)));
        var observation = Observe(detector, page);
        await NetworkPair(detector, page);
        extractor.CompleteCall(1, Video(oldPage, "old", Av(2160, "retired")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldObservation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(published);
        Assert.False(detector.Failed);

        await Observe(detector, page);
        Assert.Equal(2, extractor.ResolveCount);
        var complete = detector.CompleteAsync(default);
        Assert.False(complete.IsCompleted);
        Assert.Empty(published);
        extractor.CompleteCall(2, Video(page, currentId, Av(1080, "current")));
        await Task.WhenAll(observation, complete).WaitAsync(TimeSpan.FromSeconds(5));
        var result = Assert.Single(published);
        Assert.Equal(session, result.SessionId);
        Assert.Equal(currentId, result.MediaId);
        Assert.Equal(1080, Assert.Single(result.Formats).Height);
        Assert.DoesNotContain("retired", result.Video!.SourceUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Matching_Page_Url_With_Stale_Script_Identity_Is_Rejected(bool runExternal)
    {
        var extractor = new GatedYouTubeExtractor();
        var detector = CreateDetector(extractor);
        var published = Capture(detector);
        var page = Watch("current");
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "stale title",
            """{"identity":"content:youtube:old","caption":"stale caption","isLive":true,"media":["https://rr.googlevideo.com/videoplayback?itag=18"],"combinedMedia":["https://rr.googlevideo.com/videoplayback?itag=18"]}""",
            RequestContext.CreateEmpty(), default, runExternal);
        Assert.Empty(published);
        Assert.Equal(0, extractor.ResolveCount);

        var observation = Observe(detector, page);
        extractor.Complete(Video(page, "current", Av(1080, "current")));
        await observation;
        await detector.CompleteAsync(default);
        var result = Assert.Single(published);
        Assert.False(result.IsLive);
        Assert.Equal("current title", result.DisplayTitle);
        Assert.Equal("current", result.MediaId);
    }

    [Fact]
    public async Task Mismatched_Network_Session_Is_Rejected_Even_For_The_Same_Video()
    {
        var detector = CreateDetector(new GatedYouTubeExtractor(available: false));
        var published = Capture(detector);
        var page = Watch("current");
        var oldSession = Guid.NewGuid();
        detector.BeginSession(page, oldSession);
        var session = Guid.NewGuid();
        detector.BeginSession(page, session);
        await Observe(detector, page);
        await detector.CompleteAsync(default);
        await detector.ProcessNetworkAsync(Event(page, VideoUrl("stale")) with { SessionId = oldSession }, default);
        await detector.ProcessNetworkAsync(Event(page, AudioUrl("stale")) with { SessionId = oldSession, PageUrl = null }, default);
        Assert.Empty(published);
        Assert.True(detector.Failed);

        await detector.ProcessNetworkAsync(Event(page, VideoUrl("current")) with { SessionId = session }, default);
        await detector.ProcessNetworkAsync(Event(page, AudioUrl("current")), default);
        var result = Assert.Single(published);
        Assert.Equal(session, result.SessionId);
        Assert.DoesNotContain("stale", result.Video!.SourceUrl.AbsoluteUri);
        Assert.DoesNotContain("stale", result.Audio!.SourceUrl.AbsoluteUri);
    }

    [Fact]
    public async Task Cancelled_Entry_Points_Do_Not_Change_State_Or_Request_Publication()
    {
        var extractor = new GatedYouTubeExtractor(available: false);
        var detector = CreateDetector(extractor);
        var published = Capture(detector);
        var page = Watch("current");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Observe(detector, Watch("old"), """{"isLive":true}""", ct: cancelled.Token));
        await Observe(detector, page);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            detector.ProcessNetworkAsync(Event(page, VideoUrl("cancelled")), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Observe(detector, page, MediaJson(VideoUrl("cancelled-dom")), runExternal: false, ct: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detector.CompleteAsync(cancelled.Token));
        await detector.ProcessNetworkAsync(Event(page, AudioUrl("audio")), default);
        Assert.Empty(published);
        Assert.False(detector.Failed);

        await detector.CompleteAsync(default);
        Assert.Empty(published);
        Assert.True(detector.Failed);
        var reason = detector.FailureReason;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Observe(detector, page, """{"isLive":true}""", ct: cancelled.Token));
        Assert.True(detector.Failed);
        Assert.Equal(reason, detector.FailureReason);
        await detector.ProcessNetworkAsync(Event(page, VideoUrl("valid")), default);
        var result = Assert.Single(published);
        Assert.False(result.IsLive);
        Assert.Contains("valid", result.Video!.SourceUrl.AbsoluteUri);
        Assert.Equal(0, extractor.ResolveCount);
    }

    [Theory]
    [InlineData("empty", "network")]
    [InlineData("audio", "network")]
    [InlineData("video", "network")]
    [InlineData("empty", "dom")]
    [InlineData("audio", "dom")]
    [InlineData("video", "dom")]
    [InlineData("empty", "external")]
    [InlineData("audio", "external")]
    [InlineData("video", "external")]
    public async Task Incomplete_Media_Fails_Complete_But_Can_Be_Filled_By_Late_Network(string kind, string evidence)
    {
        var stats = Substitute.For<IProbeMethodStats>();
        var extractor = new GatedYouTubeExtractor();
        var detector = CreateDetector(extractor, stats);
        var published = Capture(detector);
        var page = Watch("current");
        detector.BeginSession(page, Guid.NewGuid());
        var observation = Observe(detector, page);
        if (kind != "empty")
        {
            if (evidence == "external")
            {
                var track = Av(1080, "partial").Tracks.Single(t =>
                    t.Kind == (kind == "audio" ? MediaTrackKind.Audio : MediaTrackKind.Video));
                extractor.Complete(Video(page, "current",
                    MediaVariant.FromTracks("partial", null, null, null, "mp4", [track])));
            }
            else if (evidence == "dom")
                await Observe(detector, page, MediaJson(kind == "audio" ? AudioUrl("partial") : VideoUrl("partial")), runExternal: false);
            else
                await detector.ProcessNetworkAsync(Event(page, kind == "audio" ? AudioUrl("partial") : VideoUrl("partial")), default);
        }
        extractor.Complete();
        await observation;
        await detector.CompleteAsync(default);
        await detector.CompleteAsync(default);
        Assert.Empty(published);
        Assert.True(detector.Failed);
        Assert.NotNull(detector.FailureReason);
        stats.DidNotReceive().RecordTimed(Arg.Any<string>(), Arg.Any<string>(), true, Arg.Any<TimeSpan>());

        if (kind is "empty" or "audio")
            await detector.ProcessNetworkAsync(Event(page, VideoUrl("late")), default);
        if (kind is "empty" or "video")
            await detector.ProcessNetworkAsync(Event(page, AudioUrl("late")), default);
        var result = Assert.Single(published);
        Assert.True(DetectionReadiness.IsUsable(MediaDescriptorMapper.ToDetectedVideo(result, result.SessionId)));
        Assert.False(detector.Failed);
        stats.Received(1).RecordTimed(SiteIds.YouTube, ProbeMethods.NetworkMedia, true, Arg.Any<TimeSpan>());
    }

    private static Task Observe(YouTubeMediaDetector detector, Uri page, string? json = null,
        bool runExternal = true, CancellationToken ct = default) =>
        detector.ProcessPageObservationAsync(page, "current title", json, RequestContext.CreateEmpty(), ct, runExternal);

    private static string MediaJson(params string[] urls) => JsonSerializer.Serialize(new { media = urls });

    private static async Task NetworkPair(YouTubeMediaDetector detector, Uri page)
    {
        await detector.ProcessNetworkAsync(Event(page, VideoUrl("network-video")), default);
        await detector.ProcessNetworkAsync(Event(page, AudioUrl("network-audio")), default);
    }

    private static YouTubeMediaDetector CreateDetector(YouTubeYtDlpExtractor extractor, IProbeMethodStats? stats = null) =>
        new(NullLogger<YouTubeMediaDetector>.Instance, extractor, stats ?? Substitute.For<IProbeMethodStats>());

    private static List<MediaDescriptor> Capture(YouTubeMediaDetector detector)
    {
        var published = new List<MediaDescriptor>();
        detector.DescriptorsReady += (_, descriptors) => published.AddRange(descriptors);
        return published;
    }

    private static Uri Watch(string id) => new($"https://www.youtube.com/watch?v={id}");

    private static DetectedVideo Video(Uri page, string id, params MediaVariant[] variants) =>
        new(Guid.NewGuid(), SiteIds.YouTube, id, "Video " + id, page, MediaFamily.Dash,
            variants, false, ProbeSource.SiteAdapter);

    private static MediaVariant Av(int height, string marker) =>
        MediaVariant.FromTracks($"{height}p", null, height, null, "mp4",
        [
            new MediaTrack($"video-{height}", MediaTrackKind.Video, new Uri(VideoUrl(marker)),
                null, "mp4", null, 1_000_000, RequestContext.CreateEmpty()),
            new MediaTrack($"audio-{height}", MediaTrackKind.Audio, new Uri(AudioUrl(marker)),
                null, "mp4", null, 100_000, RequestContext.CreateEmpty())
        ]);

    private static string VideoUrl(string marker) =>
        $"https://rr1---sn-test.googlevideo.com/videoplayback?itag=137&mime=video%2Fmp4&marker={marker}";

    private static string AudioUrl(string marker) =>
        $"https://rr1---sn-test.googlevideo.com/videoplayback?itag=140&mime=audio%2Fmp4&marker={marker}";

    private static NormalizedNetworkEvent Event(Uri page, string mediaUrl) =>
        new(
            new Uri(mediaUrl),
            "GET",
            206,
            mediaUrl.Contains("mime=audio", StringComparison.OrdinalIgnoreCase) ? "audio/mp4" : "video/mp4",
            1_000_000,
            "Media",
            null,
            page,
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            RequestContext.CreateEmpty(),
            DateTimeOffset.UtcNow,
            NetworkEventSource.Cdp)
        { SessionId = Guid.Empty };

}
