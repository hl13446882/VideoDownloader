using VideoDownloader.Infrastructure.Persistence;

namespace VideoDownloader.Infrastructure.Tests;

public class DownloadJobMapperTests
{
    [Fact]
    public void DeserializeVariant_WhenTracksNull_FallsBackToCombinedTrack()
    {
        var metaJson = """
            {
              "VariantId": "720p",
              "Width": 1280,
              "Height": 720,
              "Bandwidth": 2800000,
              "Container": "mp4",
              "ContextId": "00000000-0000-0000-0000-000000000001",
              "ContextVersion": 1,
              "Referer": null,
              "Origin": null,
              "UserAgent": null,
              "Tracks": null
            }
            """;

        var variant = DownloadJobMapper.DeserializeVariant(
            "http://localhost:5088/media/public.mp4",
            metaJson,
            null);

        Assert.Single(variant.Tracks);
        Assert.Equal("restored", variant.Tracks[0].TrackId);
    }
}
