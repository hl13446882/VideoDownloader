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
    /// Prefer progressive/muxed VOD over MSE/partial tracks; size only ranks within the same delivery class.
    /// </summary>
    public static IReadOnlyList<MediaVariant> Rank(IEnumerable<MediaVariant> variants) =>
        variants
            .OrderBy(v => IsFlvLike(v) ? 1 : 0)
            .ThenByDescending(v =>
                string.Equals(v.Container, "album", StringComparison.OrdinalIgnoreCase) &&
                v.Tracks.Count(t => t.Kind == MediaTrackKind.Image) >= 2
                    ? 1 : 0)
            .ThenByDescending(v => HasVideo(v) ? 1 : 0)
            .ThenByDescending(v => MediaVariantReconciler.HasCompleteAudio(v) ? 1 : 0)
            // Ordinary progressive Combined first; MSE adaptive tracks last (never default).
            .ThenBy(v => IsMseOrPartialVariant(v) ? 1 : 0)
            // Prefer real playlists over bare fMP4/TS segments observed via MSE.
            .ThenByDescending(v =>
                string.Equals(v.Container, "hls", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v.Container, "dash", StringComparison.OrdinalIgnoreCase)
                    ? 1 : 0)
            .ThenBy(v => v.Tracks.Any(t => MediaUrlNormalizer.IsLikelySegment(t.SourceUrl)) ? 1 : 0)
            // Prefer CDN objects over Douyin /aweme/v1/play gateways that only 302.
            .ThenBy(v => v.Tracks.Any(t => UnifiedMediaPipeline.IsDouyinPlayGateway(t.SourceUrl)) ? 1 : 0)
            // Demote Douyin/TikTok MSE Range windows that somehow remain with a tiny known length.
            .ThenBy(v => v.Tracks.Any(t =>
                UnifiedMediaPipeline.IsInsufficientByteDanceDownloadObject(t.SourceUrl, t.ContentLength)) ? 1 : 0)
            .ThenByDescending(v => v.TotalContentLength ?? 0)
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
