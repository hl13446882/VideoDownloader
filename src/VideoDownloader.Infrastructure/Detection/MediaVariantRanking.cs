using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Detection;

/// <summary>Shared variant preference for UI focus and live acceptance sampling.</summary>
public static class MediaVariantRanking
{
    public static bool IsFlvLike(MediaVariant variant) =>
        variant.Tracks
            .Where(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined)
            .DefaultIfEmpty()
            .Any(t => t is not null && IsFlvLike(t));

    public static bool IsFlvLike(MediaTrack track)
    {
        if (string.Equals(track.Container, "flv", StringComparison.OrdinalIgnoreCase))
            return true;
        var url = track.SourceUrl.AbsoluteUri;
        return url.Contains(".flv", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/flv/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("mime_type=video_flv", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("media_type=video_flv", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasVideo(MediaVariant variant) =>
        variant.Tracks.Any(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined or MediaTrackKind.Image) ||
        string.Equals(variant.Container, "album", StringComparison.OrdinalIgnoreCase);

    public static bool HasAudio(MediaVariant variant) =>
        variant.Tracks.Any(t => t.Kind is MediaTrackKind.Audio or MediaTrackKind.Combined);

    /// <summary>
    /// Rank for UI dropdown / default selection: largest file size first (after excluding junk classes).
    /// </summary>
    public static IReadOnlyList<MediaVariant> Rank(IEnumerable<MediaVariant> variants) =>
        variants
            .OrderBy(v => IsFlvLike(v) ? 1 : 0)
            .ThenBy(v => IsMseOrPartialVariant(v) ? 1 : 0)
            .ThenByDescending(v =>
                string.Equals(v.Container, "album", StringComparison.OrdinalIgnoreCase) &&
                v.Tracks.Count(t => t.Kind == MediaTrackKind.Image) >= 2
                    ? 1 : 0)
            .ThenByDescending(v => HasVideo(v) ? 1 : 0)
            .ThenByDescending(v => MediaVariantReconciler.HasCompleteAudio(v) ? 1 : 0)
            .ThenBy(v => v.Tracks.Any(t =>
                UnifiedMediaPipeline.IsInsufficientByteDanceDownloadObject(t.SourceUrl, t.ContentLength)) ? 1 : 0)
            // Primary: known file size (largest first). Bandwidth / height only break ties.
            .ThenByDescending(v => MediaResourceSizeFilter.EffectiveSize(v))
            .ThenByDescending(v => v.Bandwidth ?? 0)
            .ThenByDescending(v => v.Height ?? 0)
            .ToArray();

    public static bool IsMseOrPartialVariant(MediaVariant variant) =>
        variant.Tracks.Any(t =>
            t.IsMseTrack ||
            MediaUrlNormalizer.IsByteDanceMseTrack(t.SourceUrl));

    public static MediaVariant? SelectPreferredVideo(IEnumerable<MediaVariant> variants) =>
        Rank(variants).FirstOrDefault(v => HasVideo(v) && !IsMseOrPartialVariant(v));

    public static MediaVariant? SelectPreferredAudio(IEnumerable<MediaVariant> variants) =>
        Rank(variants).FirstOrDefault(v => HasAudio(v) && !HasVideo(v))
        ?? Rank(variants).FirstOrDefault(HasAudio);
}
