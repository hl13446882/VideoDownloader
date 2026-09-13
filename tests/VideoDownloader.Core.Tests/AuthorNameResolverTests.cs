using VideoDownloader.Core.Models;
using VideoDownloader.Core.Naming;

namespace VideoDownloader.Core.Tests;

public sealed class AuthorNameResolverTests
{
    [Fact]
    public void FromMetadata_PrefersAuthorThenUploader()
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["uploader"] = "Channel",
            ["author"] = "Alice"
        };
        Assert.Equal("Alice", AuthorNameResolver.FromMetadata(meta, null));
    }

    [Fact]
    public void FromPageUrl_ParsesTikTokHandle()
    {
        var url = new Uri("https://www.tiktok.com/@some_creator/video/1234567890123");
        Assert.Equal("some_creator", AuthorNameResolver.FromPageUrl(url));
    }

    [Fact]
    public void DisplayOrUnknown_EmptyIsUnknown()
    {
        Assert.Equal("未知", AuthorNameResolver.DisplayOrUnknown(null));
        Assert.Equal("未知", AuthorNameResolver.DisplayOrUnknown("  "));
        Assert.Equal("Bob", AuthorNameResolver.DisplayOrUnknown("Bob"));
    }

    [Fact]
    public void FromDetectedVideo_UsesMetadataAndUrlFallback()
    {
        var video = new DetectedVideo(
            Guid.NewGuid(),
            "tiktok",
            "1",
            "title",
            new Uri("https://www.tiktok.com/@fallback_user/video/1234567890123"),
            MediaFamily.DirectMp4,
            [],
            false)
        {
            Metadata = new Dictionary<string, string> { ["channel"] = "FromMeta" }
        };
        Assert.Equal("FromMeta", AuthorNameResolver.FromDetectedVideo(video));
    }
}
