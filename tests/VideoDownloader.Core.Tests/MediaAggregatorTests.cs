using VideoDownloader.Core.Aggregation;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Tests;

public class MediaAggregatorTests
{
    [Fact]
    public void Aggregate_DirectMediaWithoutExtension_UsesMimeTypeContainer()
    {
        var aggregator = new MediaAggregator();
        var resource = new MediaResource(
            Guid.NewGuid(),
            new Uri("https://cdn.example.com/videoplayback?id=abc"),
            MediaType.Unknown,
            "video/mp4; codecs=\"avc1\"",
            "GET",
            200,
            2_000_000,
            "Media",
            null,
            new Uri("https://example.com/watch"),
            null,
            RequestContext.CreateEmpty(),
            new Dictionary<string, string>(),
            DateTimeOffset.UtcNow);

        var videos = aggregator.Aggregate([new MediaCandidate(resource, CandidateKind.DirectMedia, 100, "test")]);

        Assert.Single(videos);
        Assert.Equal("mp4", videos[0].Variants[0].Container);
    }
}
