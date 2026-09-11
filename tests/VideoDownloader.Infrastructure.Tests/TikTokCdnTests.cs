using VideoDownloader.Infrastructure.Detection.Sites.TikTok;

namespace VideoDownloader.Infrastructure.Tests;

public class TikTokCdnTests
{
    [Fact]
    public void IsSameWork_True_ForFeedShellsWithDifferentQuery()
    {
        var a = new Uri("https://www.tiktok.com/?lang=en");
        var b = new Uri("https://www.tiktok.com/?_r=1&timestamp=2");
        Assert.True(TikTokCdn.IsFeedShell(a));
        Assert.True(TikTokCdn.IsSameWork(a, b));
        Assert.True(TikTokCdn.IsSameWork(
            new Uri("https://www.tiktok.com/foryou"),
            new Uri("https://www.tiktok.com/")));
    }

    [Fact]
    public void IsSameWork_True_ForSameVideoIdOnDifferentHandles()
    {
        var a = new Uri("https://www.tiktok.com/@tiktok/video/7666011938798832917");
        var b = new Uri("https://www.tiktok.com/@qingyue_official/video/7666011938798832917");
        Assert.True(TikTokCdn.IsSameWork(a, b));
    }

    [Fact]
    public void IsSameWork_False_ForDifferentVideoIds()
    {
        var a = new Uri("https://www.tiktok.com/@u/video/7666011938798832917");
        var b = new Uri("https://www.tiktok.com/@u/video/7681541062887820561");
        Assert.False(TikTokCdn.IsSameWork(a, b));
    }

    [Fact]
    public void IsSignedProgressiveHost_DoesNotMatchDouyinOrBilibili()
    {
        Assert.True(TikTokCdn.IsSignedProgressiveHost(new Uri("https://v16-webapp-prime.tiktok.com/video/tos/x")));
        Assert.True(TikTokCdn.IsSignedProgressiveHost(new Uri("https://v16.tiktokcdn.com/obj.mp4")));
        Assert.False(TikTokCdn.IsSignedProgressiveHost(new Uri("https://v3-dy-o.zjcdn.com/video.mp4")));
        Assert.False(TikTokCdn.IsSignedProgressiveHost(new Uri("https://upos-sz-mirrorbos.bilivideo.com/a.m4s")));
        Assert.False(TikTokCdn.IsSignedProgressiveHost(new Uri("https://rr1---sn-abc.googlevideo.com/videoplayback")));
    }
}
