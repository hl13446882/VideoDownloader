using VideoDownloader.Infrastructure.Http;

namespace VideoDownloader.Infrastructure.Tests;

public class HttpDashFragmentUrlTests
{
    [Fact]
    public void WithFragmentSequence_ReplacesExistingSq()
    {
        var url = new Uri("https://rr.example/videoplayback?id=1&sq=9&hang=1");
        var next = HttpMediaDownloader.WithFragmentSequence(url, 42);
        Assert.Equal("https://rr.example/videoplayback?id=1&sq=42&hang=1", next.AbsoluteUri);
    }

    [Fact]
    public void WithFragmentSequence_AppendsWhenMissing()
    {
        var url = new Uri("https://rr.example/videoplayback?id=1&hang=1");
        var next = HttpMediaDownloader.WithFragmentSequence(url, 0);
        Assert.Equal("https://rr.example/videoplayback?id=1&hang=1&sq=0", next.AbsoluteUri);
    }
}
