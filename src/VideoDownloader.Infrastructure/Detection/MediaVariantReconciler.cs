using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Detection;

public static class MediaVariantReconciler
{
    public static bool HasCompleteAudio(MediaVariant variant) =>
        variant.Tracks.Any(t => t.Kind == MediaTrackKind.Combined) ||
        (variant.Tracks.Any(t => t.Kind == MediaTrackKind.Video) && variant.Tracks.Any(t => t.Kind == MediaTrackKind.Audio));

    private static IEnumerable<string> VideoKeys(MediaVariant variant) => variant.Tracks
        .Where(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined)
        .Select(t => MediaUrlNormalizer.Normalize(t.SourceUrl) +
            (t.TrackId.StartsWith("dash:", StringComparison.Ordinal) ? "|" + t.TrackId : ""));

    public static IReadOnlyList<MediaVariant> RemoveSupersededVideoOnly(IEnumerable<MediaVariant> variants)
    {
        var all = variants.ToArray();
        var complete = all.Where(HasCompleteAudio).SelectMany(VideoKeys).ToHashSet(StringComparer.Ordinal);
        return all.Where(v => HasCompleteAudio(v) || !VideoKeys(v).Any(complete.Contains)).ToArray();
    }

    public static bool HasAudioCompletion(DetectedVideo candidate, DetectedVideo current)
    {
        var incomplete = current.Variants.Where(v => !HasCompleteAudio(v)).SelectMany(VideoKeys).ToHashSet(StringComparer.Ordinal);
        var alreadyComplete = current.Variants.Where(HasCompleteAudio).SelectMany(VideoKeys).ToHashSet(StringComparer.Ordinal);
        return candidate.Variants.Where(HasCompleteAudio).SelectMany(VideoKeys)
            .Any(key => incomplete.Contains(key) && !alreadyComplete.Contains(key));
    }
}
