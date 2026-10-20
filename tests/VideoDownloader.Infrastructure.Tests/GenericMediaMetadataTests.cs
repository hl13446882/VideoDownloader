using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;

namespace VideoDownloader.Infrastructure.Tests;

public class GenericMediaMetadataTests
{
    [Fact]
    public void DurationIsMatchedToEachVideoRatherThanPageMaximum()
    {
        var page = new Uri("https://example.com/watch");
        var first = MediaVariant.FromCombinedTrack("stream", new Uri("https://cdn.example/first.mp4"), RequestContext.CreateEmpty());
        var second = MediaVariant.FromCombinedTrack("stream", new Uri("https://cdn.example/second.mp4"), RequestContext.CreateEmpty());
        UnifiedMediaPipeline.Probed[] probes =
        [
            new(page, first.Tracks[0], 12, 720),
            new(page, second.Tracks[0], 125, 1080)
        ];

        Assert.Equal(12d, UnifiedMediaPipeline.ResolveGroupDuration([first], probes));
        Assert.Equal(125d, UnifiedMediaPipeline.ResolveGroupDuration([second], probes));
        Assert.Null(UnifiedMediaPipeline.ResolveGroupDuration([first], [probes[1]]));
    }

    [Fact]
    public void FormatLabelIncludesDimensions()
    {
        var variant = MediaVariant.FromCombinedTrack("stream", new Uri("https://cdn.example/video.mp4"),
            RequestContext.CreateEmpty(), width: 1920, height: 1080);
        Assert.Contains("1920×1080", MediaVariantDisplay.BuildLabel(variant));
        Assert.Contains("1080p", MediaVariantDisplay.BuildLabel(variant));
    }
}
