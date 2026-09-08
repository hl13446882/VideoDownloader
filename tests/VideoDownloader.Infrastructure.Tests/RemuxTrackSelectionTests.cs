using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Ffmpeg;

namespace VideoDownloader.Infrastructure.Tests;

public sealed class RemuxTrackSelectionTests
{
    private static MediaTrack Track(string id, MediaTrackKind kind, long? length = null) =>
        new(id, kind, new Uri("file:///tmp/" + id), null, null, null, length, RequestContext.CreateEmpty());

    [Fact]
    public void DropsExtraAudio_WhenPrimaryAlreadyHasAudio()
    {
        var video = Track("v", MediaTrackKind.Combined, 80_000_000);
        var audio = Track("a", MediaTrackKind.Audio, 1_000_000);
        var selected = FfmpegAdapter.SelectTracksForRemux(
        [
            (video, HasVideo: true, HasAudio: true),
            (audio, HasVideo: false, HasAudio: true)
        ]);
        Assert.Equal([video], selected);
    }

    [Fact]
    public void UsesSelfContainedAlone_WhenVideoPrimaryUnreadable()
    {
        var crumb = Track("crumb", MediaTrackKind.Video, 900_000);
        var muxed = Track("hls", MediaTrackKind.Audio, 78_000_000);
        var selected = FfmpegAdapter.SelectTracksForRemux(
        [
            (crumb, HasVideo: false, HasAudio: false),
            (muxed, HasVideo: true, HasAudio: true)
        ]);
        Assert.Equal([muxed], selected);
    }

    [Fact]
    public void UsesCombinedAlone_WhenSiblingVideoCrumbHasNoStreams()
    {
        var crumb = Track("crumb", MediaTrackKind.Video, 900_000);
        var combined = Track("hls", MediaTrackKind.Combined, 78_000_000);
        var selected = FfmpegAdapter.SelectTracksForRemux(
        [
            (crumb, HasVideo: false, HasAudio: false),
            (combined, HasVideo: true, HasAudio: true)
        ]);
        Assert.Equal([combined], selected);
    }

    [Fact]
    public void KeepsVideoPlusAudio_WhenVideoIsVideoOnly()
    {
        var video = Track("v", MediaTrackKind.Video, 50_000_000);
        var audio = Track("a", MediaTrackKind.Audio, 5_000_000);
        var selected = FfmpegAdapter.SelectTracksForRemux(
        [
            (video, HasVideo: true, HasAudio: false),
            (audio, HasVideo: false, HasAudio: true)
        ]);
        Assert.Equal(2, selected.Count);
        Assert.Contains(video, selected);
        Assert.Contains(audio, selected);
    }

    [Fact]
    public void KeepsHqVideoPlusAudioExtract_FromProgressiveCombinedDonor()
    {
        var hd = Track("hd", MediaTrackKind.Video, 100_000_000);
        var donor = Track("audio-extract", MediaTrackKind.Audio, 20_000_000);
        var selected = FfmpegAdapter.SelectTracksForRemux(
        [
            (hd, HasVideo: true, HasAudio: false),
            (donor, HasVideo: true, HasAudio: true)
        ]);
        Assert.Equal(2, selected.Count);
        Assert.Contains(hd, selected);
        Assert.Contains(donor, selected);
    }
}
