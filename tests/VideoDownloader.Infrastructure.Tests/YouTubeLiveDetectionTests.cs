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
        MediaDescriptor? result = null;
        detector.DescriptorsReady += (_, descriptors) => result = descriptors.Single();
        detector.BeginSession(page, Guid.NewGuid());
        await detector.ProcessPageObservationAsync(page, "Live",
            """{"identity":"content:youtube:live-id","isLive":true,"durationSec":3600}""", RequestContext.CreateEmpty(), default);
        await detector.CompleteAsync(default);
        Assert.NotNull(result);
        Assert.True(result.IsLive);
        var video = MediaDescriptorMapper.ToDetectedVideo(result, Guid.NewGuid());
        Assert.True(video.IsLive);
        Assert.Empty(video.Variants);
    }
}
