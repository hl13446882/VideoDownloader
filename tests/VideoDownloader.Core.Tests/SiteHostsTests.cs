using VideoDownloader.Core.Models;
using VideoDownloader.Core.Sites;

namespace VideoDownloader.Core.Tests;

public class SiteHostsTests
{
    [Theory]
    [InlineData("youtube.com", "youtube.com")]
    [InlineData("www.youtube.com", "youtube.com")]
    [InlineData("m.youtube.com", "youtube.com")]
    [InlineData("WWW.YouTube.COM", "youtube.com")]
    [InlineData("youtu.be", "youtu.be")]
    [InlineData("www.bilibili.com", "bilibili.com")]
    [InlineData("b23.tv", "b23.tv")]
    public void Matches_Exact_And_Subdomain(string host, string domain)
    {
        Assert.True(SiteHosts.Matches(host, domain));
    }

    [Theory]
    [InlineData("notyoutube.com", "youtube.com")]
    [InlineData("evilyoutube.com", "youtube.com")]
    [InlineData("youtube.com.evil.example", "youtube.com")]
    [InlineData("youtu.be.evil.example", "youtu.be")]
    [InlineData("fake-tiktok.com", "tiktok.com")]
    [InlineData("bilibili.com.cn.evil", "bilibili.com")]
    [InlineData("cdn.example.com", "b23.tv")]
    public void Matches_Rejects_Substring_And_Suffix_Spoof(string host, string domain)
    {
        Assert.False(SiteHosts.Matches(host, domain));
    }

    [Fact]
    public void MatchesAny_Uses_Dot_Delimited_Domains_Only()
    {
        var page = new Uri("https://www.youtube-nocookie.com/embed/x");
        Assert.True(SiteHosts.MatchesAny(page, "youtube.com", "youtu.be", "youtube-nocookie.com"));
        Assert.False(SiteHosts.MatchesAny(new Uri("https://youtube-nocookie.com.evil.example/"), "youtube-nocookie.com"));
    }

    [Theory]
    [InlineData("https://www.douyin.com/video/1", SiteKind.Douyin)]
    [InlineData("https://www.iesdouyin.com/share/video/1", SiteKind.Douyin)]
    [InlineData("https://m.tiktok.com/@a/video/1", SiteKind.TikTok)]
    [InlineData("https://m.youtube.com/watch?v=abc", SiteKind.YouTube)]
    [InlineData("https://youtu.be/abc", SiteKind.YouTube)]
    [InlineData("https://www.youtube-nocookie.com/embed/abc", SiteKind.YouTube)]
    [InlineData("https://m.bilibili.com/video/BV1", SiteKind.Bilibili)]
    [InlineData("https://b23.tv/BV1", SiteKind.Bilibili)]
    public void Router_Accepts_Real_Subdomains(string url, SiteKind expected)
    {
        Assert.Equal(expected, new SiteDetectionRouter().Resolve(new Uri(url)));
    }

    [Theory]
    [InlineData("https://notyoutube.com/watch?v=abc")]
    [InlineData("https://youtube.com.evil.example/watch?v=abc")]
    [InlineData("https://eviltiktok.com/@a/video/1")]
    [InlineData("https://bilibili.com.evil.example/video/BV1")]
    [InlineData("https://douyin.com.evil.example/video/1")]
    [InlineData("https://youtu.be.evil.example/abc")]
    public void Router_Rejects_Spoofed_Hosts(string url)
    {
        Assert.Equal(SiteKind.Other, new SiteDetectionRouter().Resolve(new Uri(url)));
    }
}
