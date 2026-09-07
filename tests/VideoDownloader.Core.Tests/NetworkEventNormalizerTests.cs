using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;

namespace VideoDownloader.Core.Tests;

public class NetworkEventNormalizerTests
{
    [Fact]
    public async Task NormalizeAsync_DuplicateEvents_Deduplicates()
    {
        var normalizer = new NetworkEventNormalizer(maxCacheEntries: 100, cacheTtl: TimeSpan.FromMinutes(1));
        var raw = RawNetworkEvent.FromWebResource(
            new Uri("https://example.com/a.mp4"),
            "GET",
            200,
            "video/mp4",
            1024,
            "Media",
            new Uri("https://example.com/"),
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        var first = await normalizer.NormalizeAsync(raw, CancellationToken.None);
        var second = await normalizer.NormalizeAsync(raw, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task NormalizeAsync_BlobUrl_ReturnsNull()
    {
        var normalizer = new NetworkEventNormalizer();
        var raw = RawNetworkEvent.FromWebResource(
            new Uri("blob:https://example.com/abc"),
            "GET",
            200,
            "video/mp4",
            null,
            "Media",
            new Uri("https://example.com/"),
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        var result = await normalizer.NormalizeAsync(raw, CancellationToken.None);
        Assert.Null(result);
    }
}
