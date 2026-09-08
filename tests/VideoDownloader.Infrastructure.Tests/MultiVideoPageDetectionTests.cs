using System.Text.Json;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection;

namespace VideoDownloader.Infrastructure.Tests;

public class MultiVideoPageDetectionTests
{
    private static readonly Uri Page = new("https://example.test/archives/274623/");
    private static readonly RequestContext Context = RequestContext.CreateEmpty();

    private static UnifiedMediaPipeline Create() =>
        new(Substitute.For<IRequestMessageFactory>(), [], Options.Create(new AppOptions()));

    private static UnifiedMediaPipeline.Probed Hls(Uri url) =>
        new(Page, new MediaTrack(
            "hls",
            MediaTrackKind.Combined,
            url,
            "h264",
            "hls",
            1_000_000,
            50_000_000,
            Context)
        {
            IsValidated = true,
            BrowserObserved = true,
            Evidence = MediaEvidence.BrowserObserved
        }, 30, null);

    [Fact]
    public async Task TwoDistinctHlsOnSamePage_EmitsTwoDetectedVideos_WithMatchingAudioPerCard()
    {
        var pipeline = Create();
        pipeline.InspectOverride = (u, _, _, _) => Task.FromResult<UnifiedMediaPipeline.Probed?>(Hls(u));

        var pageResults = new List<IReadOnlyList<DetectedVideo>>();
        pipeline.PageProbed += (_, list) => pageResults.Add(list);

        var a = "https://cdn.test/videos5/aaa/aaa.m3u8?auth=1";
        var b = "https://cdn.test/videos5/bbb/bbb.m3u8?auth=2";
        await pipeline.ProbePageAsync(
            Page,
            null,
            JsonSerializer.Serialize(new { media = new[] { a, b } }),
            Context,
            default);
        await pipeline.CompleteDiscoveryAsync(default);

        var final = Assert.Single(pageResults);
        Assert.Equal(2, final.Count);
        Assert.Distinct(final.Select(v => v.VideoId));

        foreach (var video in final)
        {
            var videoVariants = video.Variants
                .Where(v => v.Tracks.Any(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined))
                .ToArray();
            var audioVariants = video.Variants
                .Where(v => v.Tracks.Count > 0 && v.Tracks.All(t => t.Kind == MediaTrackKind.Audio))
                .ToArray();
            Assert.NotEmpty(videoVariants);
            Assert.NotEmpty(audioVariants);

            var videoKey = UnifiedMediaPipeline.VariantMediaGroupKey(videoVariants[0]);
            foreach (var variant in video.Variants)
                Assert.Equal(videoKey, UnifiedMediaPipeline.VariantMediaGroupKey(variant));
        }

        var keys = final.Select(v => UnifiedMediaPipeline.VariantMediaGroupKey(v.Variants[0])).ToArray();
        Assert.Equal(2, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void VariantMediaGroupKey_UsesSessionUrlForDistinctVideos()
    {
        var left = MediaVariant.FromCombinedTrack(
            "v",
            new Uri("https://cdn.test/videos5/aaa/aaa.m3u8"),
            Context);
        var right = MediaVariant.FromCombinedTrack(
            "v",
            new Uri("https://cdn.test/videos5/bbb/bbb.m3u8"),
            Context);
        Assert.NotEqual(
            UnifiedMediaPipeline.VariantMediaGroupKey(left),
            UnifiedMediaPipeline.VariantMediaGroupKey(right));
    }

    [Fact]
    public void SameMediaGroup_MatchesSharedContentIdentityAcrossUrls()
    {
        var video = new MediaTrack("v", MediaTrackKind.Video, new Uri("https://cdn.test/v.mp4"), null, "mp4", null, null, Context)
        {
            ContentIdentity = "id:100"
        };
        var audio = new MediaTrack("a", MediaTrackKind.Audio, new Uri("https://cdn.test/a.m4a"), null, "m4a", null, null, Context)
        {
            ContentIdentity = "id:100"
        };
        Assert.True(UnifiedMediaPipeline.SameMediaGroup(video, audio));
    }
}
