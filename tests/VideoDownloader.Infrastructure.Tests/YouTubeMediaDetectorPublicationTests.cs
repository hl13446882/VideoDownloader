using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
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

        var descriptor = Assert.Single(published);
        Assert.Equal("B-id", descriptor.MediaId);
        Assert.Empty(descriptor.Formats);
        Assert.Contains("current-video", descriptor.Video!.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(descriptor.Audio);
    }

    private static YouTubeMediaDetector CreateDetector(YouTubeYtDlpExtractor extractor) =>
        new(NullLogger<YouTubeMediaDetector>.Instance, extractor, Substitute.For<IProbeMethodStats>());

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
