using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection.Sites.TikTok;
using VideoDownloader.Infrastructure.Download;

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

    [Fact]
    public void FragilePlayHost_IsWebappPrime_NotTiktokcdn()
    {
        Assert.True(TikTokCdn.IsFragilePlayHost(new Uri("https://v16-webapp-prime.tiktok.com/video/tos/x")));
        Assert.False(TikTokCdn.IsFragilePlayHost(new Uri("https://v16.tiktokcdn.com/obj.mp4")));
        Assert.True(TikTokCdn.IsDurablePlayHost(new Uri("https://v16.tiktokcdn.com/obj.mp4")));
        Assert.False(TikTokCdn.IsDurablePlayHost(new Uri("https://v16-webapp-prime.tiktok.com/video/tos/x")));
        Assert.True(TikTokCdn.PlayHostScore(new Uri("https://v16.tiktokcdn.com/obj.mp4")) >
                    TikTokCdn.PlayHostScore(new Uri("https://v16-webapp-prime.tiktok.com/video/tos/x")));
    }

    [Fact]
    public void IsLiveCookieTab_True_ForFeedShellVsReconstructedVideo()
    {
        var feed = new Uri("https://www.tiktok.com/");
        var recovered = new Uri("https://www.tiktok.com/@i/video/7682788705483984135");
        Assert.True(TikTokCdn.IsLiveCookieTab(feed, recovered));
        Assert.False(TikTokCdn.IsLiveCookieTab(
            new Uri("https://www.douyin.com/?recommend=1"),
            recovered));
    }

    [Fact]
    public void DurableHostScore_PrefersTiktokcdnOverWebappPrime_WithoutChangingDouyinOrder()
    {
        var tiktokcdn = MediaVariant.FromCombinedTrack(
            "cdn", new Uri("https://v16.tiktokcdn.com/obj.mp4"), RequestContext.CreateEmpty());
        var prime = MediaVariant.FromCombinedTrack(
            "prime", new Uri("https://v16-webapp-prime.tiktok.com/video/tos/x"), RequestContext.CreateEmpty());
        var zjcdn = MediaVariant.FromCombinedTrack(
            "zj", new Uri("https://v3-dy-o.zjcdn.com/video.mp4"), RequestContext.CreateEmpty());
        var webPrime = MediaVariant.FromCombinedTrack(
            "dy", new Uri("https://v3-web-prime.douyinvod.com/video.mp4"), RequestContext.CreateEmpty());

        Assert.True(MediaAddressRenewal.DurableHostScore(tiktokcdn) > MediaAddressRenewal.DurableHostScore(prime));
        Assert.True(MediaAddressRenewal.DurableHostScore(zjcdn) > MediaAddressRenewal.DurableHostScore(tiktokcdn));
        Assert.True(MediaAddressRenewal.DurableHostScore(zjcdn) > MediaAddressRenewal.DurableHostScore(webPrime));
    }
}
