using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Manifests;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Tests;

public class TrackClassificationTests
{
    [Theory]
    [InlineData("avc1.640028", MediaTrackKind.Video)]
    [InlineData("mp4a.40.2", MediaTrackKind.Audio)]
    [InlineData("av01.0.08M.08,opus", MediaTrackKind.Combined)]
    [InlineData("", MediaTrackKind.Unknown)]
    public void HlsCodecEvidence_DeterminesTrackKind(string codec, MediaTrackKind expected)
    {
        var result = HlsManifestParser.Parse($"#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=100000,CODECS=\"{codec}\"\nchild.m3u8", new("https://cdn.test/master.m3u8"), RequestContext.CreateEmpty());
        Assert.Equal(expected, Assert.Single(Assert.Single(result.Variants).Tracks).Kind);
    }

    [Fact]
    public void LeafPlaylist_WithSegments_IsCombined()
    {
        var result = HlsManifestParser.Parse("#EXTM3U\n#EXTINF:5,\na.ts\n#EXT-X-ENDLIST", new("https://cdn.test/a.m3u8"), RequestContext.CreateEmpty());
        var track = Assert.Single(Assert.Single(result.Variants).Tracks);
        Assert.Equal(MediaTrackKind.Combined, track.Kind);
        Assert.True(track.IsValidated);
    }

    [Fact]
    public void ValidatedSmallAudio_IsNotRemovedByDisplayHeuristic()
    {
        var track = new MediaTrack("a", MediaTrackKind.Audio, new("https://cdn.test/audio.m4a"), "aac", "m4a", null, 2048, RequestContext.CreateEmpty());
        var variant = MediaVariant.FromTracks("audio", null, null, null, "m4a", [track]);
        Assert.True(MediaResourceSizeFilter.ShouldExcludeVariant(variant));
        Assert.False(MediaResourceSizeFilter.ShouldExcludeVariant(variant with { Tracks = [track with { IsValidated = true }] }));
    }
}
