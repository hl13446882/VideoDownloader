using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Detection;

public static class MediaResourceSizeFilter
{
    public const long MinDisplayBytes = 64 * 1024;
    public const long MinStrongMimeBytes = 1024;
    /// <summary>UI dropdown: hide known objects smaller than this (crumbs / stubs).</summary>
    public const long MinDropdownBytes = 200 * 1024;
    /// <summary>
    /// Soft heuristic for ranking progressive Douyin/TikTok candidates (not a hard download gate).
    /// </summary>
    public const long MinProgressiveVideoBytes = 512 * 1024;
    /// <summary>Generic multi-video pages: objects below 2 MiB are teaser/junk, not downloadable cards.</summary>
    public const long MinGenericVideoBytes = 2L * 1024 * 1024;

    public static bool ShouldExcludeFromDisplay(MediaResource resource, CandidateKind kind)
    {
        if (kind is CandidateKind.HlsManifest or CandidateKind.DashManifest)
            return false;

        var length = resource.ContentLength;
        if (length is null or 0)
            return kind is CandidateKind.DirectMedia or CandidateKind.HlsSegment or CandidateKind.DashRepresentation;

        if (length < MinStrongMimeBytes)
            return true;

        var mime = resource.MimeType?.ToLowerInvariant() ?? string.Empty;
        var hasStrongMime = mime.StartsWith("video/", StringComparison.Ordinal) ||
                            mime.StartsWith("audio/", StringComparison.Ordinal);

        if (!hasStrongMime && length < MinDisplayBytes)
            return true;

        return false;
    }

    public static bool ShouldExcludeVariant(MediaVariant variant)
    {
        var total = variant.TotalContentLength;
        if (total is 0)
            return true;

        if (variant.Tracks.Count > 0 && variant.Tracks.All(t => t.IsValidated))
            return false;

        if (total is null)
            return false;

        return total < MinDisplayBytes;
    }

    /// <summary>
    /// Dropdown list: drop known undersized crumbs (&lt;200 KiB). Unknown size stays visible.
    /// </summary>
    public static bool ShouldExcludeFromDropdown(MediaVariant variant)
    {
        if (ShouldExcludeVariant(variant))
            return true;

        var size = EffectiveSize(variant);
        return size is > 0 and < MinDropdownBytes;
    }

    /// <summary>
    /// Generic-site progressive/combined video under 2 MiB is invalid (covers multi-video teaser crumbs).
    /// HLS/DASH manifests and albums are kept.
    /// </summary>
    public static bool ShouldExcludeGenericVideoVariant(MediaVariant variant)
    {
        if (variant.Tracks.Any(t => t.Kind == MediaTrackKind.Image))
            return false;
        if (string.Equals(variant.Container, "hls", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(variant.Container, "dash", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(variant.Container, "album", StringComparison.OrdinalIgnoreCase))
            return false;
        if (variant.Tracks.Any(t =>
                t.SourceUrl.AbsolutePath.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                t.SourceUrl.AbsolutePath.Contains(".mpd", StringComparison.OrdinalIgnoreCase)))
            return false;

        if (variant.TotalContentLength is > 0 and < MinGenericVideoBytes)
            return true;

        return variant.Tracks.Any(t =>
            t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined &&
            t.ContentLength is > 0 and < MinGenericVideoBytes);
    }

    public static DetectedVideo FilterForDisplay(DetectedVideo video)
    {
        var variants = video.Variants.Where(v => !ShouldExcludeVariant(v)).ToList();
        if (variants.Count == 0)
            return video with { Variants = [] };

        return video with { Variants = variants };
    }

    public static DetectedVideo FilterGenericVideos(DetectedVideo video)
    {
        var variants = video.Variants
            .Where(v => !ShouldExcludeVariant(v) && !ShouldExcludeGenericVideoVariant(v))
            .ToList();
        if (variants.Count == 0)
            return video with { Variants = [] };

        return video with { Variants = variants };
    }

    public static long EffectiveSize(MediaVariant variant) =>
        variant.TotalContentLength
        ?? variant.Tracks.Select(t => t.ContentLength ?? 0).DefaultIfEmpty(0).Max();
}
