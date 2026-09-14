using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Download;

namespace VideoDownloader.Infrastructure.Tests;

public class CompletedDownloadMatcherTests
{
    [Fact]
    public void Match_WhenSameContentIdentity_AndCompatibleSize()
    {
        var page = new Uri("https://www.douyin.com/video/123");
        var stored = Progressive("https://cdn-a.example/v.mp4?sign=old", 10_000_000, "id:123", page);
        var incoming = Progressive("https://cdn-b.example/v.mp4?sign=new", 10_000_000, "id:123", page);

        Assert.True(CompletedDownloadMatcher.MatchesCompletedObject(
            stored, incoming, storedExpectedTotal: 10_000_000, onDiskBytes: 10_000_000));
    }

    [Fact]
    public void NoMatch_WhenSameContentIdentity_ButSizeMismatch()
    {
        var page = new Uri("https://www.douyin.com/video/123");
        var stored = Progressive("https://cdn-a.example/v.mp4", 10_000_000, "id:123", page);
        var incoming = Progressive("https://cdn-b.example/v.mp4", 5_000_000, "id:123", page);

        Assert.False(CompletedDownloadMatcher.MatchesCompletedObject(
            stored, incoming, 10_000_000, 10_000_000));
    }

    [Fact]
    public void NoMatch_WhenSizeMatches_ButNoIdentityOrStableUrl()
    {
        var stored = Progressive("https://cdn-a.example/a.mp4", 10_000_000, null, null);
        var incoming = Progressive("https://cdn-b.example/b.mp4", 10_000_000, null, null);

        Assert.False(CompletedDownloadMatcher.MatchesCompletedObject(
            stored, incoming, 10_000_000, 10_000_000));
    }

    [Fact]
    public void Match_WhenSameHostAndPath_AfterSignedQueryChanges()
    {
        var stored = Progressive(
            "https://v.example.com/obj/video123.mp4?expire=1&sign=aaa",
            8_000_000,
            null,
            null);
        var incoming = Progressive(
            "https://v.example.com/obj/video123.mp4?expire=2&sign=bbb",
            8_000_000,
            null,
            null);

        Assert.True(CompletedDownloadMatcher.MatchesCompletedObject(
            stored, incoming, 8_000_000, 8_000_000));
    }

    [Fact]
    public void NoMatch_WhenIncomingSizeUnknown()
    {
        var page = new Uri("https://www.douyin.com/video/123");
        var stored = Progressive("https://cdn-a.example/v.mp4", 10_000_000, "id:123", page);
        var incoming = Progressive("https://cdn-b.example/v.mp4", null, "id:123", page);

        Assert.False(CompletedDownloadMatcher.MatchesCompletedObject(
            stored, incoming, 10_000_000, 10_000_000));
    }

    [Fact]
    public void NoMatch_WhenOnDiskSizeDiverges()
    {
        var page = new Uri("https://www.douyin.com/video/123");
        var stored = Progressive("https://cdn-a.example/v.mp4", 10_000_000, "id:123", page);
        var incoming = Progressive("https://cdn-b.example/v.mp4", 10_000_000, "id:123", page);

        Assert.False(CompletedDownloadMatcher.MatchesCompletedObject(
            stored, incoming, 10_000_000, onDiskBytes: 1_000_000));
    }

    private static MediaVariant Progressive(string url, long? length, string? identity, Uri? recovery) =>
        MediaVariant.FromCombinedTrack(
            "v1",
            new Uri(url),
            RequestContext.CreateEmpty(),
            container: "mp4",
            contentLength: length) with
        {
            ContentIdentity = identity,
            RecoveryPageUrl = recovery
        };
}
