using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Download;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection;
using VideoDownloader.Infrastructure.Detection.Sites.YouTube;

namespace VideoDownloader.Infrastructure.Tests;

public class YouTubeLiveDetectionTests
{
    [Theory]
    [InlineData("https://manifest.googlevideo.com/api/manifest/hls_playlist/source/yt_live_broadcast/live/1/playlist_type/DVR/index.m3u8", true)]
    [InlineData("https://manifest.googlevideo.com/api/manifest/hls_playlist/source/yt_live_broadcast/live/0/index.m3u8", false)]
    [InlineData("https://rr1.googlevideo.com/videoplayback?source=yt_live_broadcast&hang=1", false)]
    public void OnlyExplicitLiveManifestsAreBlockedNotArchivedDashFormats(string url, bool live) =>
        Assert.Equal(live, YouTubeLiveMedia.IsLiveUrl(new Uri(url)));

    private static YouTubeYtDlpExtractor Extractor() => new(Options.Create(new AppOptions
        { ExternalResolvers = new ExternalResolversOptions { Enabled = false } }),
        NullLogger<YouTubeYtDlpExtractor>.Instance, Substitute.For<IProbeMethodStats>());

    [Theory]
    [InlineData("""{"is_live":true,"duration":3600}""", true)]
    [InlineData("""{"live_status":"is_live","duration":7200}""", true)]
    [InlineData("""{"live_status":"is_upcoming"}""", true)]
    [InlineData("""{"is_live":false,"was_live":true,"live_status":"was_live"}""", false)]
    [InlineData("""{"live_status":"post_live"}""", false)]
    [InlineData("""{"live_status":"not_live"}""", false)]
    public void LiveMetadataDistinguishesLiveDvrFromFinishedRecordings(string json, bool live)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(live, YouTubeYtDlpExtractor.IsLiveMetadata(doc.RootElement));
    }

    [Fact]
    public void LiveWithoutFormatsStillProducesExplicitSkipSignal()
    {
        var video = Assert.Single(Extractor().ParseJson("""{"id":"live-id","title":"Live","is_live":true,"duration":3600}""",
            new Uri("https://www.youtube.com/watch?v=live-id"), RequestContext.CreateEmpty(), SiteIds.YouTube));
        Assert.True(video.IsLive);
        Assert.Empty(video.Variants);
        Assert.Equal(AutoModeAdvanceReason.Live, new AutoModeAdvanceGate(DateTimeOffset.UtcNow)
            .Observe(DateTimeOffset.UtcNow, video.IsLive, null));
    }

    [Fact]
    public async Task ObservationCarriesLiveStateThroughExclusiveDetectorAndMapper()
    {
        var detector = new YouTubeMediaDetector(NullLogger<YouTubeMediaDetector>.Instance, Extractor(), Substitute.For<IProbeMethodStats>());
        var page = new Uri("https://www.youtube.com/watch?v=live-id");
        var results = new List<MediaDescriptor>();
        detector.DescriptorsReady += (_, descriptors) => results.AddRange(descriptors);
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "Live",
            """{"identity":"content:youtube:live-id","isLive":true,"durationSec":3600}""", RequestContext.CreateEmpty(), default,
            runExternal: false);
        Assert.Single(results);
        await detector.CompleteAsync(default);
        await detector.ProcessPageObservationAsync(page, "Live", """{"isLive":true}""", RequestContext.CreateEmpty(), default);
        var result = Assert.Single(results);
        Assert.True(result.IsLive);
        var video = MediaDescriptorMapper.ToDetectedVideo(result, Guid.NewGuid());
        Assert.True(video.IsLive);
        Assert.Empty(video.Variants);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("dom")]
    [InlineData("external")]
    public async Task Live_Is_A_Single_Timely_Terminal_Signal_Without_Complete(string source)
    {
        var extractor = new GatedYouTubeExtractor();
        var detector = new YouTubeMediaDetector(NullLogger<YouTubeMediaDetector>.Instance, extractor, Substitute.For<IProbeMethodStats>());
        var page = new Uri("https://www.youtube.com/watch?v=live-id");
        var session = Guid.NewGuid();
        var results = new List<MediaDescriptor>();
        detector.DescriptorsReady += (_, descriptors) => results.AddRange(descriptors);
        detector.BeginSession(page, session);
        var observation = detector.ProcessPageObservationAsync(page, "Live", null, RequestContext.CreateEmpty(), default);
        Assert.Empty(results);

        if (source == "network")
            await detector.ProcessNetworkAsync(new NormalizedNetworkEvent(
                new Uri("https://manifest.googlevideo.com/api/manifest/hls_playlist/source/yt_live_broadcast/live/1/index.m3u8"),
                "GET", 200, "application/vnd.apple.mpegurl", null, "Media", null, page, null,
                new Dictionary<string, string>(), new Dictionary<string, string>(), RequestContext.CreateEmpty(),
                DateTimeOffset.UtcNow, NetworkEventSource.Cdp) { SessionId = session }, default);
        else if (source == "dom")
            await detector.ProcessPageObservationAsync(page, "Live", """{"isLive":true}""", RequestContext.CreateEmpty(), default,
                runExternal: false);

        if (source != "external")
        {
            Assert.False(observation.IsCompleted);
            Assert.True(Assert.Single(results).IsLive);
        }
        extractor.Complete(new DetectedVideo(Guid.NewGuid(), SiteIds.YouTube, "live-id", "Live", page,
            MediaFamily.Hls, [], false, ProbeSource.SiteAdapter) { IsLive = true });
        await observation.WaitAsync(TimeSpan.FromSeconds(5));
        var result = Assert.Single(results);
        Assert.True(result.IsLive);
        Assert.Equal(session, result.SessionId);
        await detector.CompleteAsync(default);
        await detector.CompleteAsync(default);
        await detector.ProcessPageObservationAsync(page, "Live", """{"isLive":true}""", RequestContext.CreateEmpty(), default);
        Assert.Single(results);
        Assert.Equal(1, extractor.ResolveCount);
    }
}
