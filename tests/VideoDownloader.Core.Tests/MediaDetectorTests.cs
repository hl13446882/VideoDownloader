using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Tests;

public class MediaDetectorTests
{
    [Fact]
    public void Detect_Mp4Url_ReturnsCandidate()
    {
        var detector = new MediaDetector();
        var resource = CreateResource("https://example.com/video.mp4", "video/mp4", 2 * 1024 * 1024);
        var result = detector.Detect(resource);
        Assert.NotNull(result);
        Assert.Equal(CandidateKind.DirectMedia, result!.Kind);
    }

    [Fact]
    public void Detect_JpgImage_ReturnsNull()
    {
        var detector = new MediaDetector();
        var resource = CreateResource("https://example.com/image.jpg", "image/jpeg", 100_000);
        Assert.Null(detector.Detect(resource));
    }

    [Fact]
    public void Detect_M3u8_ReturnsHlsManifest()
    {
        var detector = new MediaDetector();
        var resource = CreateResource("https://example.com/master.m3u8", "application/vnd.apple.mpegurl", null);
        var result = detector.Detect(resource);
        Assert.NotNull(result);
        Assert.Equal(CandidateKind.HlsManifest, result!.Kind);
    }

    private static MediaResource CreateResource(string url, string mime, long? length) =>
        new(
            Guid.NewGuid(),
            new Uri(url),
            MediaType.Unknown,
            mime,
            "GET",
            200,
            length,
            "Media",
            null,
            new Uri("https://example.com/"),
            null,
            RequestContext.CreateEmpty(),
            new Dictionary<string, string>(),
            DateTimeOffset.UtcNow);
}
