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
    public void TrackPathOverridesGenericContainerMime(string url, string mime, MediaTrackKind expected)
    {
        Assert.Equal(expected, UnifiedMediaPipeline.InferKindFromMime(mime, new Uri(url)));
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
}
