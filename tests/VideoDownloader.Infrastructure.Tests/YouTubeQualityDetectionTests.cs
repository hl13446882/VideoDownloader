using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection;
using VideoDownloader.Infrastructure.Detection.Sites.YouTube;

namespace VideoDownloader.Infrastructure.Tests;

public class YouTubeQualityDetectionTests
{
    private const string Low = """
        {"identity":"content:youtube:quality-id","id":"quality-id","title":"Quality test","duration":120,
         "formats":[{"format_id":"18","url":"https://rr1.googlevideo.com/videoplayback?itag=18","ext":"mp4","height":360,"vcodec":"avc1","acodec":"mp4a","filesize":50000000}]}
        """;
    private const string Ladder = """
        {"identity":"content:youtube:quality-id","id":"quality-id","title":"Quality test","duration":120,"availableHeights":[360,720,1080,2160],
         "formats":[
          {"format_id":"18","url":"https://rr1.googlevideo.com/videoplayback?itag=18","ext":"mp4","height":360,"vcodec":"avc1","acodec":"mp4a","filesize":50000000},
          {"format_id":"136","url":"https://rr1.googlevideo.com/videoplayback?itag=136","ext":"mp4","height":720,"vcodec":"avc1","acodec":"none","filesize":40000000},
          {"format_id":"137","url":"https://rr1.googlevideo.com/videoplayback?itag=137","ext":"mp4","height":1080,"vcodec":"avc1","acodec":"none","filesize":30000000},
          {"format_id":"313","url":"https://rr1.googlevideo.com/videoplayback?itag=313","ext":"webm","height":2160,"vcodec":"vp9","acodec":"none"},
          {"format_id":"140","url":"https://rr1.googlevideo.com/videoplayback?itag=140","ext":"m4a","vcodec":"none","acodec":"mp4a","filesize":2000000}]
        }
        """;

    [Fact]
    public async Task BrowserMethodReturnsEntireAdaptiveLadderBeforeStopping()
    {
        var detector = Detector();
        MediaDescriptor? descriptor = null;
        detector.DescriptorsReady += (_, list) => descriptor = list.Single();
        await detector.ProcessPageObservationAsync(Page, "Old title", Ladder, RequestContext.CreateEmpty(), default, runExternal: false);
        var video = MediaDescriptorMapper.ToDetectedVideo(Assert.IsType<MediaDescriptor>(descriptor), Guid.NewGuid());
        Assert.Equal(new[] { 360, 720, 1080, 2160 }, video.Variants.Where(MediaVariantRanking.HasVideo).Select(v => v.Height!.Value).Distinct().Order().ToArray());
        Assert.All(video.Variants.Where(v => v.Height >= 720), v => Assert.Contains(v.Tracks, t => t.Kind == MediaTrackKind.Audio));
        Assert.Equal(2160, MediaVariantRanking.SelectPreferredVideo(video.Variants)!.Height);
        Assert.Equal(120, video.DurationSec);
    }

    [Fact]
    public async Task LaterSameContentFormatsEnrichFirstSuccessWithoutRepeatedIdenticalEmits()
    {
        var detector = Detector();
        var descriptors = new List<MediaDescriptor>();
        detector.DescriptorsReady += (_, list) => descriptors.AddRange(list);
        await detector.ProcessPageObservationAsync(Page, null, Low, RequestContext.CreateEmpty(), default, false);
        await detector.ProcessPageObservationAsync(Page, null, Ladder, RequestContext.CreateEmpty(), default, false);
        await detector.ProcessPageObservationAsync(Page, null, Ladder, RequestContext.CreateEmpty(), default, false);
        Assert.Equal(2, descriptors.Count);
        Assert.Equal(2160, descriptors.Last().Formats.Max(f => f.Height));
    }

    [Fact]
    public async Task LowAddressCannotCompleteDiscoveryWhileAdvertisedHdIsMissing()
    {
        var detector = Detector();
        var descriptors = new List<MediaDescriptor>();
        detector.DescriptorsReady += (_, list) => descriptors.AddRange(list);
        var partial = Low.Replace("\"duration\":120", "\"duration\":120,\"availableHeights\":[360,1080]");
        await detector.ProcessPageObservationAsync(Page, null, partial, RequestContext.CreateEmpty(), default, false);
        Assert.Empty(descriptors);
        await detector.CompleteAsync(default);
        Assert.Empty(descriptors);
        Assert.True(detector.Failed);
        await detector.ProcessPageObservationAsync(Page, null, Ladder, RequestContext.CreateEmpty(), default, false);
        var complete = MediaDescriptorMapper.ToDetectedVideo(descriptors.Last(), Guid.NewGuid());
        Assert.False(complete.IsFormatDiscoveryPending);
        Assert.True(VideoDownloader.Core.Detection.DetectionReadiness.IsUsable(complete));
        Assert.Equal(2160, MediaVariantRanking.SelectPreferredVideo(complete.Variants)!.Height);
    }

    [Fact]
    public void VideoOnlyHdCannotPretendToContainAudio()
    {
        var extractor = new YouTubeYtDlpExtractor(Options.Create(new AppOptions()), NullLogger<YouTubeYtDlpExtractor>.Instance, Substitute.For<IProbeMethodStats>());
        var video = Assert.Single(extractor.ParseJson("""{"id":"quality-id","formats":[{"format_id":"137","url":"https://rr1.googlevideo.com/videoplayback?itag=137","ext":"mp4","height":1080,"vcodec":"avc1","acodec":"none"}]}""", Page, RequestContext.CreateEmpty(), SiteIds.YouTube));
        Assert.DoesNotContain(video.Variants.SelectMany(v => v.Tracks), t => t.Kind == MediaTrackKind.Combined);
        Assert.False(VideoDownloader.Core.Detection.DetectionReadiness.IsUsable(video));
    }

    [Fact]
    public void HighestResolutionWinsOverLargerFileAndHigherBitrate()
    {
        var large = MediaVariant.FromCombinedTrack("large-low", new Uri("https://cdn.example/low.mp4"), RequestContext.CreateEmpty(), height: 720, bandwidth: 9000000, contentLength: 90000000);
        var high = MediaVariant.FromCombinedTrack("small-high", new Uri("https://cdn.example/high.mp4"), RequestContext.CreateEmpty(), height: 1080, bandwidth: 1000000, contentLength: 10000000);
        var unknownSize4k = MediaVariant.FromCombinedTrack("unknown-size-4k", new Uri("https://cdn.example/4k.mp4"), RequestContext.CreateEmpty(), height: 2160);
        Assert.Equal(unknownSize4k, MediaVariantRanking.SelectPreferredVideo(new[] { large, high, unknownSize4k }));
        Assert.Equal(new[] { unknownSize4k, high, large }, MediaVariantRanking.Rank(new[] { large, high, unknownSize4k }));
    }

    private static readonly Uri Page = new("https://www.youtube.com/watch?v=quality-id");
    private static YouTubeMediaDetector Detector()
    {
        var extractor = new YouTubeYtDlpExtractor(Options.Create(new AppOptions { ExternalResolvers = new ExternalResolversOptions { Enabled = false } }), NullLogger<YouTubeYtDlpExtractor>.Instance, Substitute.For<IProbeMethodStats>());
        var detector = new YouTubeMediaDetector(NullLogger<YouTubeMediaDetector>.Instance, extractor, Substitute.For<IProbeMethodStats>());
        detector.BeginSession(Page, Guid.NewGuid());
        return detector;
    }
}
