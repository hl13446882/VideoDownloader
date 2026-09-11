using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Tests;

public class MediaVariantDisplayTests
{
    [Fact]
    public void BuildLabel_Includes_Height_And_Exact_Size()
    {
        var variant = MediaVariant.FromCombinedTrack(
            "stream",
            new Uri("https://cdn.example/v.mp4"),
            RequestContext.CreateEmpty(),
            height: 720,
            contentLength: 5_242_880);
        var label = MediaVariantDisplay.BuildLabel(variant);
        Assert.Contains("720p", label, StringComparison.Ordinal);
        Assert.Contains("5.0 MB", label, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveDisplaySize_Sums_Partial_Track_Lengths()
    {
        var videoTrack = new MediaTrack(
            "v", MediaTrackKind.Video, new Uri("https://cdn.example/v.mp4"),
            null, "mp4", null, 4_000_000, RequestContext.CreateEmpty());
        var audioTrack = new MediaTrack(
            "a", MediaTrackKind.Audio, new Uri("https://cdn.example/a.m4a"),
            null, "m4a", null, null, RequestContext.CreateEmpty());
        var variant = MediaVariant.FromTracks("720p", null, 720, null, "mp4", [videoTrack, audioTrack]);

        var (bytes, approx) = MediaVariantDisplay.ResolveDisplaySize(variant, 120);
        Assert.Equal(4_000_000, bytes);
        Assert.True(approx);
    }

    [Fact]
    public void ResolveDisplaySize_Estimates_From_Bitrate_And_Duration()
    {
        var variant = MediaVariant.FromCombinedTrack(
            "1080p",
            new Uri("https://cdn.example/v.mp4"),
            RequestContext.CreateEmpty(),
            height: 1080,
            bandwidth: 8_000_000);
        var (bytes, approx) = MediaVariantDisplay.ResolveDisplaySize(variant, 10);
        Assert.Equal(10_000_000, bytes);
        Assert.True(approx);
    }

    [Fact]
    public void ResolveDisplaySize_Ignores_Hls_Playlist_And_Ts_Lengths()
    {
        var playlist = new MediaTrack(
            "hls", MediaTrackKind.Combined, new Uri("https://cdn.example/index.m3u8"),
            null, "hls", 2_000_000, 123_016, RequestContext.CreateEmpty());
        var hlsVariant = MediaVariant.FromTracks("media", null, null, 2_000_000, "hls", [playlist]);
        var (fromPlaylist, _) = MediaVariantDisplay.ResolveDisplaySize(hlsVariant, 100);
        Assert.Equal(25_000_000, fromPlaylist); // bandwidth × duration only

        var ts = new MediaTrack(
            "seg", MediaTrackKind.Combined, new Uri("https://cdn.example/1000k_00000.ts"),
            null, "mp4", null, 2_000_000, RequestContext.CreateEmpty());
        var tsVariant = MediaVariant.FromTracks("slice", null, null, null, "mp4", [ts]);
        var (fromTs, _) = MediaVariantDisplay.ResolveDisplaySize(tsVariant, 100);
        Assert.Null(fromTs);
    }

    [Fact]
    public void BuildLabel_Uses_Height_Alone_When_Id_Is_Generic()
    {
        var variant = MediaVariant.FromCombinedTrack(
            "视频",
            new Uri("https://cdn.example/v.mp4"),
            RequestContext.CreateEmpty(),
            height: 1080,
            contentLength: 1_048_576);
        Assert.Equal("1080p [1.0 MB]", MediaVariantDisplay.BuildLabel(variant));
    }
}
