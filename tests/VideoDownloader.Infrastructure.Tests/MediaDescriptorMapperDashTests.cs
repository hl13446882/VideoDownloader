using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;

namespace VideoDownloader.Infrastructure.Tests;

public class MediaDescriptorMapperDashTests
{
    private static readonly RequestContext Context = RequestContext.CreateEmpty();
    private static readonly Uri Manifest = new("https://cdn.test/master.mpd");
    private static readonly Uri Page = new("https://example.test/watch/1");

    private static MediaVariant Video(string repId, int height, long bandwidth, string codec = "avc1") =>
        MediaVariant.FromTracks(
            $"{height}p-{repId}",
            null,
            height,
            bandwidth,
            "dash",
            [new MediaTrack($"dash:{repId}", MediaTrackKind.Video, Manifest, codec, "dash", bandwidth, null, Context)]);

    private static MediaVariant Audio(string repId, long bandwidth) =>
        MediaVariant.FromTracks(
            "audio-" + repId,
            null,
            null,
            bandwidth,
            "dash",
            [new MediaTrack($"dash:{repId}", MediaTrackKind.Audio, Manifest, "mp4a", "dash", bandwidth, null, Context)]);

    private static MediaVariant Pair(MediaVariant video, MediaVariant audio) =>
        video with
        {
            VariantId = video.VariantId + "+" + audio.VariantId,
            Tracks = [video.Tracks[0], audio.Tracks[0]]
        };

    private static MediaDescriptor Descriptor(params MediaVariant[] formats) =>
        new("generic", Page, "1", MediaContentType.Video, null, null, [], Context, 1)
        {
            Formats = formats
        };

    [Fact]
    public void DashRepresentationsSharingManifestUrl_KeepEveryQuality()
    {
        var v1080 = Video("v1080", 1080, 5_000_000);
        var v720 = Video("v720", 720, 2_500_000);
        var aHigh = Audio("a128", 128_000);
        var aLow = Audio("a64", 64_000);

        var detected = MediaDescriptorMapper.ToDetectedVideo(
            Descriptor(
                Pair(v1080, aHigh),
                Pair(v1080, aLow),
                Pair(v720, aHigh),
                Pair(v720, aLow),
                aHigh,
                aLow),
            Guid.NewGuid());

        Assert.Equal(2, detected.Variants.Count(v => v.Height is 1080 or 720));

        var uhd = detected.Variants.Single(v => v.Height == 1080);
        Assert.Equal(2, uhd.Tracks.Count);
        Assert.Contains(uhd.Tracks, t => t.Kind == MediaTrackKind.Audio && t.Bandwidth == 128_000);
        Assert.Contains(uhd.Alternatives, v => v.Height == 720);

        Assert.Contains(detected.Variants, v =>
            v.Tracks.Count == 1 &&
            v.Tracks[0].Kind == MediaTrackKind.Audio &&
            v.Tracks[0].Bandwidth == 128_000);
        Assert.Contains(detected.Variants, v =>
            v.Tracks.Count == 1 &&
            v.Tracks[0].Kind == MediaTrackKind.Audio &&
            v.Tracks[0].Bandwidth == 64_000);
    }

    [Fact]
    public void DashSameHeightDifferentCodecs_StayDistinct()
    {
        var avc = Video("v1080avc", 1080, 5_000_000, "avc1");
        var av1 = Video("v1080av1", 1080, 4_800_000, "av01");

        var detected = MediaDescriptorMapper.ToDetectedVideo(
            Descriptor(avc, av1),
            Guid.NewGuid());

        Assert.Equal(2, detected.Variants.Count(v => v.Height == 1080));
        Assert.Contains(detected.Variants, v => v.VideoCodec == "avc1");
        Assert.Contains(detected.Variants, v => v.VideoCodec == "av01");
    }

    [Fact]
    public void HlsVariantsWithDistinctPlaylistUrls_AreNotCollapsed()
    {
        var low = MediaVariant.FromTracks(
            "360p",
            null,
            360,
            800_000,
            "hls",
            [new MediaTrack("hls-media", MediaTrackKind.Combined, new Uri("https://cdn.test/360.m3u8"), "avc1", "hls", 800_000, null, Context)]);
        var high = MediaVariant.FromTracks(
            "720p",
            null,
            720,
            2_500_000,
            "hls",
            [new MediaTrack("hls-media", MediaTrackKind.Combined, new Uri("https://cdn.test/720.m3u8"), "avc1", "hls", 2_500_000, null, Context)]);

        var detected = MediaDescriptorMapper.ToDetectedVideo(
            Descriptor(low, high),
            Guid.NewGuid());

        Assert.Equal(2, detected.Variants.Count(v =>
            v.Tracks.Any(t => t.Kind == MediaTrackKind.Combined)));
        Assert.Contains(detected.Variants, v => v.SourceUrl.AbsolutePath.EndsWith("720.m3u8"));
    }
}
