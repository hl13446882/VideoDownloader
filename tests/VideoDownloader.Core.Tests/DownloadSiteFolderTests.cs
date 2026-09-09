using VideoDownloader.Core.Naming;

namespace VideoDownloader.Core.Tests;

public sealed class DownloadSiteFolderTests
{
    [Theory]
    [InlineData("https://www.douyin.com/video/1", "douyin.com")]
    [InlineData("https://m.douyin.com/share/video/1", "douyin.com")]
    [InlineData("https://v.douyin.com/abc/", "douyin.com")]
    [InlineData("https://www.iesdouyin.com/share/video/1", "douyin.com")]
    [InlineData("https://www.bilibili.com/video/BV1", "bilibili.com")]
    [InlineData("https://b23.tv/xxxx", "bilibili.com")]
    [InlineData("https://www.youtube.com/watch?v=1", "youtube.com")]
    [InlineData("https://youtu.be/1", "youtube.com")]
    [InlineData("https://www.tiktok.com/@u/video/1", "tiktok.com")]
    [InlineData("https://example.org/a", "example.org")]
    public void Resolve_BucketsKnownSites(string url, string expected) =>
        Assert.Equal(expected, DownloadSiteFolder.Resolve(new Uri(url)));

    [Fact]
    public void Resolve_NullOrEmpty_ReturnsUnknown()
    {
        Assert.Equal(DownloadSiteFolder.Unknown, DownloadSiteFolder.Resolve(null));
    }

    [Fact]
    public void CombineSaveDirectory_AppendsSiteFolder()
    {
        var dir = DownloadSiteFolder.CombineSaveDirectory(@"D:\Downloads", new Uri("https://www.douyin.com/video/1"));
        Assert.Equal(Path.Combine(@"D:\Downloads", "douyin.com"), dir);
    }
}
