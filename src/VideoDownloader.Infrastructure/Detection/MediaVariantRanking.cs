using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Detection;

/// <summary>Shared variant preference for UI focus and live acceptance sampling.</summary>
public static class MediaVariantRanking
{
    public static bool IsFlvLike(MediaVariant variant) =>
        variant.Tracks.Any(IsFlvLike);

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
        variant.Tracks.Any(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined);

    public static bool HasAudio(MediaVariant variant) =>
        variant.Tracks.Any(t => t.Kind is MediaTrackKind.Audio or MediaTrackKind.Combined);

    /// <summary>
    /// Prefer non-FLV progressive/adaptive VOD, then largest byte size / bandwidth / height.
    /// </summary>
    public static IReadOnlyList<MediaVariant> Rank(IEnumerable<MediaVariant> variants) =>
        variants
            .OrderBy(v => IsFlvLike(v) ? 1 : 0)
            .ThenByDescending(v => HasVideo(v) ? 1 : 0)
            .ThenByDescending(v => v.TotalContentLength ?? 0)
            .ThenByDescending(v => v.Bandwidth ?? 0)
            .ThenByDescending(v => v.Height ?? 0)
            .ToArray();

    public static MediaVariant? SelectPreferredVideo(IEnumerable<MediaVariant> variants) =>
        Rank(variants).FirstOrDefault(HasVideo);

    public static MediaVariant? SelectPreferredAudio(IEnumerable<MediaVariant> variants) =>
        Rank(variants).FirstOrDefault(v => HasAudio(v) && !HasVideo(v))
        ?? Rank(variants).FirstOrDefault(HasAudio);
}
