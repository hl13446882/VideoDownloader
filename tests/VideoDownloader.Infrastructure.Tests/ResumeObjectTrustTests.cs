using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Download;

namespace VideoDownloader.Infrastructure.Tests;

public class ResumeObjectTrustTests
{
    [Fact]
    public void Keep_WhenSameContentIdentity_AndMatchingSize()
    {
        var page = new Uri("https://www.douyin.com/video/123");
        var previous = Progressive("https://cdn-a.example/v.mp4", 10_000_000, "id:123", page);
        var renewed = Progressive("https://cdn-b.example/v.mp4", 10_000_000, "id:123", page);

        Assert.True(ResumeObjectTrust.ShouldKeepPartialProgress(previous, renewed, 10_000_000));
    }

    [Fact]
    public void Reset_WhenSameContentIdentity_ButSizeMismatch()
    {
        var page = new Uri("https://www.douyin.com/video/123");
        var previous = Progressive("https://cdn-a.example/v.mp4", 10_000_000, "id:123", page);
        var renewed = Progressive("https://cdn-b.example/v.mp4", 5_000_000, "id:123", page);

        Assert.False(ResumeObjectTrust.ShouldKeepPartialProgress(previous, renewed, 10_000_000));
    }

    [Fact]
    public void Reset_WhenSizeMatches_ButNoIdentity()
    {
        var previous = Progressive("https://cdn-a.example/v.mp4", 10_000_000, null, null);
        var renewed = Progressive("https://cdn-b.example/other.mp4", 10_000_000, null, null);

        Assert.False(ResumeObjectTrust.ShouldKeepPartialProgress(previous, renewed, 10_000_000));
    }

    [Fact]
    public void Keep_WhenBilibiliSameDashObject_EvenIfTotalsUnknown()
    {
        var previous = DashTrack(
            "https://upos-hz-mirrorakam.akamaized.net/upgcxcode/a/b/41747222317/41747222317-1-30080.m4s?os=akam",
            contentLength: null);
        var renewed = DashTrack(
            "https://upos-sz-mirrorbos.bilivideo.com/upgcxcode/a/b/41747222317/41747222317-1-30080.m4s?os=bos",
            contentLength: null);

        Assert.True(ResumeObjectTrust.ShouldKeepPartialProgress(previous, renewed, expectedTotalBytes: null));
    }

    [Fact]
    public void Reset_WhenBilibiliSameDashObject_ButTotalsDivergeBeyondTolerance()
    {
        var previous = DashTrack(
            "https://upos-hz-mirrorakam.akamaized.net/upgcxcode/a/b/41747222317/41747222317-1-30080.m4s?os=akam",
            contentLength: 100_000_000);
        var renewed = DashTrack(
            "https://upos-sz-mirrorbos.bilivideo.com/upgcxcode/a/b/41747222317/41747222317-1-30080.m4s?os=bos",
            contentLength: 50_000_000);

        Assert.False(ResumeObjectTrust.ShouldKeepPartialProgress(previous, renewed, 100_000_000));
    }

    [Fact]
    public void Keep_WhenMatchingETags()
    {
        var previous = Progressive("https://cdn-a.example/v.mp4", 1_000, null, null);
        var renewed = Progressive("https://cdn-b.example/other.mp4", 9_999, null, null);

        Assert.True(ResumeObjectTrust.ShouldKeepPartialProgress(
            previous,
            renewed,
            expectedTotalBytes: 1_000,
            storedETag: "\"abc\"",
            renewedETag: "abc"));
    }

    [Fact]
    public void Reset_WhenPrefixHashesConflict()
    {
        var page = new Uri("https://www.douyin.com/video/123");
        var previous = Progressive("https://cdn-a.example/v.mp4", 10_000_000, "id:123", page);
        var renewed = Progressive("https://cdn-b.example/v.mp4", 10_000_000, "id:123", page);

        Assert.False(ResumeObjectTrust.ShouldKeepPartialProgress(
            previous,
            renewed,
            10_000_000,
            storedPrefixHash: "sha256:aaa",
            renewedPrefixHash: "sha256:bbb"));
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

    private static MediaVariant DashTrack(string url, long? contentLength) =>
        MediaVariant.FromTracks(
            "dash",
            width: 1920,
            height: 1080,
            bandwidth: null,
            container: "mp4",
            tracks:
            [
                new MediaTrack(
                    "v",
                    MediaTrackKind.Video,
                    new Uri(url),
                    "avc1",
                    "mp4",
                    null,
                    contentLength,
                    RequestContext.CreateEmpty())
            ]);
}
