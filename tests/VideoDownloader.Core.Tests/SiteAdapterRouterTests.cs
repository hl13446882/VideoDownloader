using VideoDownloader.Core.Models;
using VideoDownloader.Core.Sites;

namespace VideoDownloader.Core.Tests;

public class SiteAdapterRouterTests
{
    [Fact]
    public void FilterForDisplay_ExcludesZeroByteVariant()
    {
        var variant = MediaVariant.FromCombinedTrack(
            "tiny",
            new Uri("http://localhost/media/tiny.mp4"),
            RequestContext.CreateEmpty(),
            contentLength: 0);

        Assert.True(Detection.MediaResourceSizeFilter.ShouldExcludeVariant(variant));
    }

    [Fact]
    public void FilterForDisplay_KeepsLargeVariant()
    {
        var variant = MediaVariant.FromCombinedTrack(
            "ok",
            new Uri("http://localhost/media/public.mp4"),
            RequestContext.CreateEmpty(),
            contentLength: 256 * 1024);

        Assert.False(Detection.MediaResourceSizeFilter.ShouldExcludeVariant(variant));
    }
}

public class SiteAdapterRouterMergeTests
{
    [Fact]
    public void MergeAndDeduplicate_PrefersSiteOverGeneric()
    {
        var site = new DetectedVideo(
            Guid.NewGuid(),
            SiteIds.Bilibili,
            "BV1",
            "Site",
            new Uri("https://bilibili.com/v"),
            MediaFamily.Dash,
            [MediaVariant.FromCombinedTrack("1080p", new Uri("http://cdn/video.m4s"), RequestContext.CreateEmpty())],
            false,
            ProbeSource.SiteAdapter);

        var generic = site with { SiteId = SiteIds.Generic, ProbeSource = ProbeSource.Generic };

        var merged = SiteAdapterRouter.MergeAndDeduplicate([site], [generic]);
        Assert.Single(merged);
        Assert.Equal(SiteIds.Bilibili, merged[0].SiteId);
    }
}
