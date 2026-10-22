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

    [Fact]
    public void ResolveFragmentUrl_Prefers_Explicit_Addresses()
    {
        var template = new Uri("https://cdn.example/template?sq=0");
        var explicitFragments = new[]
        {
            new Uri("https://cdn.example/frag/0"),
            new Uri("https://cdn.example/frag/1")
        };

        Assert.Equal("https://cdn.example/frag/0",
            HttpMediaDownloader.ResolveFragmentUrl(template, explicitFragments, 0).AbsoluteUri);
        Assert.Equal("https://cdn.example/frag/1",
            HttpMediaDownloader.ResolveFragmentUrl(template, explicitFragments, 1).AbsoluteUri);
    }

    [Fact]
    public void ResolveFragmentUrl_Falls_Back_To_Template_Without_List()
    {
        var template = new Uri("https://cdn.example/template?sq=0");
        Assert.Equal("https://cdn.example/template?sq=3",
            HttpMediaDownloader.ResolveFragmentUrl(template, null, 3).AbsoluteUri);
    }
}
