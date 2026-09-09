using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;

namespace VideoDownloader.Infrastructure.Tests;

public class UnifiedMediaPipelineCandidateTests
{
    [Theory]
    [InlineData("https://v3.douyinvod.com/video/tos/cn/item/media-audio-und-mp4a/?mime_type=video_mp4", "video/mp4", MediaTrackKind.Audio)]
    [InlineData("https://v3.douyinvod.com/video/tos/cn/item/media-video-avc1/?mime_type=video_mp4", "video/mp4", MediaTrackKind.Video)]
    [InlineData("https://v3-dy-o.zjcdn.com/abc/video/tos/cn/tos-cn-ve-15/obj/?mime_type=video_mp4&__vid=7682715203741568283", "video/mp4", MediaTrackKind.Combined)]
    public void TrackPathOverridesGenericContainerMime(string url, string mime, MediaTrackKind expected)
    {
        Assert.Equal(expected, UnifiedMediaPipeline.InferKindFromMime(mime, new Uri(url)));
    }

    [Theory]
    [InlineData("http://pull-hls-f26.douyinliving.com/media/stream-1.m3u8", true)]
    [InlineData("http://pull-flv-f26.douyinliving.com/media/stream-1.flv", true)]
    [InlineData("http://pull-t5.douyincdn.com/third/stream-120.flv", true)]
    [InlineData("https://v3-dy-o.zjcdn.com/video/tos/obj/?mime_type=video_mp4", false)]
    public void DetectsDouyinLiveStreamUrls(string url, bool expected) =>
        Assert.Equal(expected, UnifiedMediaPipeline.IsDouyinLiveStream(new Uri(url)));

    [Fact]
    public void DetectsDouyinPlayGateway()
    {
        Assert.True(UnifiedMediaPipeline.IsDouyinPlayGateway(
            new Uri("https://www.douyin.com/aweme/v1/play/?video_id=x&is_play_url=1")));
        Assert.False(UnifiedMediaPipeline.IsDouyinPlayGateway(
            new Uri("https://v3-web-prime.douyinvod.com/video/tos/obj")));
    }

    [Theory]
    [InlineData("id:7682715203741568283", "7682715203741568283")]
    [InlineData("content:douyin:7682715203741568283", "7682715203741568283")]
    [InlineData("www.douyin.com:content:7682715203741568283", "7682715203741568283")]
    public void NormalizeContentId_CollapsesPrefixes(string identity, string expected) =>
        Assert.Equal(expected, UnifiedMediaPipeline.NormalizeContentId(identity));

    [Fact]
    public void ExtractContentIdFromUrl_ReadsVidQuery()
    {
        var url = new Uri("https://v3-dy-o.zjcdn.com/x/?mime_type=video_mp4&__vid=7682715203741568283");
        Assert.Equal("7682715203741568283", UnifiedMediaPipeline.ExtractContentIdFromUrl(url));
    }

    [Theory]
    [InlineData("https://cdn.example.com/play/videoplayback?mime=video/mp4", null, null, null, true)]
    [InlineData("https://cdn.example.com/a/b/c.m4s", null, null, 2_000_000L, true)]
    [InlineData("https://cdn.example.com/a/b/c.m4s", null, null, 8_000L, false)]
    [InlineData("https://cdn.example.com/cover/thumb.jpg", "image/jpeg", null, 2_000_000L, false)]
    [InlineData("https://cdn.example.com/stream", "video/mp4", null, 8_000L, true)]
    [InlineData("https://cdn.example.com/stream", "video/mp4", null, 500L, true)]
    [InlineData("https://cdn.example.com/master.m3u8", null, null, null, true)]
    [InlineData("https://cdn.example.com/static/image/foo", null, null, 2_000_000L, false)]
    [InlineData("https://cdn.example.com/upos-sz/encode/item", null, null, null, true)]
    [InlineData("https://cdn.example.com/api/playurl?fnval=80", null, null, null, true)]
    [InlineData("https://api.example.com/aweme/v1/play/?video_id=abc&file_id=def&is_play_url=1", null, null, null, true)]
    [InlineData("https://api.example.com/service/play/?operation=preview", null, null, null, false)]
    [InlineData("https://cdn.example.com/opaque", "application/octet-stream", "Media", 2_000_000L, true)]
    [InlineData("https://cdn.example.com/opaque-range", "application/octet-stream", "Media", 8_000L, true)]
    [InlineData("https://cdn.tiktok.com/playAddr/v", null, "Media", 16_384L, true)]
    public void IsCandidate_MorphologicalRules(
        string url,
        string? mime,
        string? resourceType,
        long? length,
        bool expected)
    {
        Assert.Equal(expected, UnifiedMediaPipeline.IsCandidate(new Uri(url), mime, resourceType, length));
    }

    [Fact]
    public void IsCandidate_MediaResourceType_IgnoresTinyRangeLength()
    {
        Assert.True(UnifiedMediaPipeline.IsCandidate(
            new Uri("https://v16.tiktokcdn.com/playAddr/obj"),
            "application/octet-stream",
            "Media",
            8_192L));
    }

    [Fact]
    public void IsBrowserPlayEvidence_RequiresSuccessAndMediaOrStrongMime()
    {
        var ctx = RequestContext.CreateEmpty();
        var mediaOk = new NormalizedNetworkEvent(
            new Uri("https://v16.tiktokcdn.com/x"),
            "GET", 206, "video/mp4", 8192, "Media", null, new Uri("https://www.tiktok.com/@a/video/1"),
            null, new Dictionary<string, string>(), new Dictionary<string, string>(), ctx, DateTimeOffset.UtcNow,
            NetworkEventSource.Cdp);
        Assert.True(UnifiedMediaPipeline.IsBrowserPlayEvidence(mediaOk));

        var forbidden = mediaOk with { StatusCode = 403 };
        Assert.False(UnifiedMediaPipeline.IsBrowserPlayEvidence(forbidden));

        var xhr = mediaOk with { ResourceType = "XHR", MimeType = "application/json" };
        Assert.False(UnifiedMediaPipeline.IsBrowserPlayEvidence(xhr));
    }

    [Fact]
    public void SizeFilter_ExcludesTinyVariant()
    {
        var variant = MediaVariant.FromCombinedTrack(
            "tiny",
            new Uri("https://cdn.example.com/x.mp4"),
            RequestContext.CreateEmpty(),
            contentLength: 8 * 1024);
        Assert.True(MediaResourceSizeFilter.ShouldExcludeVariant(variant));
    }

    [Theory]
    [InlineData("https://v3.douyinvod.com/video/tos/obj", 1024L, true)]
    [InlineData("https://v3-dy-o.zjcdn.com/video/tos/obj", 8192L, true)]
    [InlineData("https://v16.tiktokcdn.com/playAddr/obj", 16_384L, true)]
    [InlineData("https://v3.douyinvod.com/video/tos/cn/item/media-audio-und-mp4a/", 8192L, true)]
    [InlineData("https://v3.douyinvod.com/video/tos/cn/item/media-audio-und-mp4a/", 512L, true)]
    [InlineData("https://v3.douyinvod.com/video/tos/cn/item/media-video-avc1/", 50_000_000L, true)]
    [InlineData("https://v3.douyinvod.com/video/tos/obj", 262144L, false)]
    [InlineData("https://v3.douyinvod.com/video/tos/obj", null, false)]
    [InlineData("https://upos-sz-mirrorcos.bilivideo.com/upos/x.m4s", 1024L, false)]
    [InlineData("https://googlevideo.com/videoplayback", 1024L, false)]
    [InlineData("https://cdn.example.com/stream.mp4", 1024L, false)]
    public void InsufficientByteDanceObject_IsScopedToDouyinTikTokCdn(
        string url, long? length, bool expected) =>
        Assert.Equal(expected, UnifiedMediaPipeline.IsInsufficientByteDanceDownloadObject(new Uri(url), length));

    [Fact]
    public void Ranking_DemotesTinyDouyinSlice_OverLargeProgressive()
    {
        var tiny = MediaVariant.FromCombinedTrack(
            "mse",
            new Uri("https://v3.douyinvod.com/video/tos/tiny"),
            RequestContext.CreateEmpty(),
            contentLength: 999);
        var full = MediaVariant.FromCombinedTrack(
            "full",
            new Uri("https://v3-dy-o.zjcdn.com/video/tos/full"),
            RequestContext.CreateEmpty(),
            contentLength: 2_000_000);
        var preferred = MediaVariantRanking.SelectPreferredVideo([tiny, full]);
        Assert.NotNull(preferred);
        Assert.Equal(full.SourceUrl, preferred!.SourceUrl);
    }
}
