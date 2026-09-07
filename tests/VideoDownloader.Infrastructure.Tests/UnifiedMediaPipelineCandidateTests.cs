using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;

namespace VideoDownloader.Infrastructure.Tests;

public class UnifiedMediaPipelineCandidateTests
{
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
