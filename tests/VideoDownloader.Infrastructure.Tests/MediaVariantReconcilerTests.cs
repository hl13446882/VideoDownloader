using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;

namespace VideoDownloader.Infrastructure.Tests;

public class MediaVariantReconcilerTests
{
    private static readonly RequestContext Context = RequestContext.CreateEmpty();
    private static readonly MediaTrack Video = new("video", MediaTrackKind.Video, new("https://cdn.test/video.mp4"), "h264", "mp4", null, 100_000_000, Context);
    private static readonly MediaTrack Audio = new("audio", MediaTrackKind.Audio, new("https://cdn.test/audio.m4a"), "aac", "m4a", null, null, Context);
    private static MediaVariant Variant(params MediaTrack[] tracks) => MediaVariant.FromTracks("test", null, 1080, 100_000, "mkv", tracks);
    private static DetectedVideo Detection(params MediaVariant[] variants) => new(Guid.NewGuid(), "generic", "100", "Caption", new("https://example.test/video/100"), MediaFamily.DirectMp4, variants, false);

    [Fact]
    public void CompletingExistingVideo_IsUpgradeEvenIfAudioSizeIsUnknown()
    {
        var current = Detection(Variant(Video), Variant(Audio));
        var candidate = Detection(Variant(Video, Audio), Variant(Audio));
        Assert.Null(candidate.Variants[0].TotalContentLength);
        Assert.True(MediaVariantReconciler.HasAudioCompletion(candidate, current));
        Assert.False(MediaVariantReconciler.HasAudioCompletion(current, candidate));
    }

    [Fact]
    public void StandaloneAudioOrAnotherVideo_IsNotPairCompletion()
    {
        var current = Detection(Variant(Video));
        Assert.False(MediaVariantReconciler.HasAudioCompletion(Detection(Variant(Video), Variant(Audio)), current));
        Assert.False(MediaVariantReconciler.HasAudioCompletion(Detection(Variant(Video with { SourceUrl = new("https://cdn.test/other.mp4") }, Audio)), current));
    }

    [Fact]
    public void PruningKeepsAudioAndOtherResolutions()
    {
        var other = Video with { SourceUrl = new("https://cdn.test/4k.mp4") };
        var result = MediaVariantReconciler.RemoveSupersededVideoOnly([Variant(Video), Variant(Video, Audio), Variant(Audio), Variant(other)]);
        Assert.Equal(3, result.Count);
        Assert.DoesNotContain(result, v => v.Tracks.Count == 1 && v.Tracks[0] == Video);
        Assert.Contains(result, v => v.Tracks.Count == 1 && v.Tracks[0] == Audio);
        Assert.Contains(result, v => v.Tracks.Count == 1 && v.Tracks[0] == other);
    }

    [Fact]
    public void DashRepresentationsSharingManifestUrl_AreNotTheSameVideoTrack()
    {
        var low = Video with { SourceUrl = new("https://cdn.test/master.mpd"), TrackId = "dash:720", Container = "dash" };
        var high = low with { TrackId = "dash:1080" };
        var result = MediaVariantReconciler.RemoveSupersededVideoOnly([Variant(low), Variant(low, Audio), Variant(high)]);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, v => v.Tracks.Count == 1 && v.Tracks[0] == high);
        Assert.False(MediaVariantReconciler.HasAudioCompletion(Detection(Variant(low, Audio)), Detection(Variant(high))));
    }
}
