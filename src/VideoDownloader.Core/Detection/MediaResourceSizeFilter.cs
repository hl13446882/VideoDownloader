using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Detection;

public static class MediaResourceSizeFilter
{
    public const long MinDisplayBytes = 64 * 1024;
    public const long MinStrongMimeBytes = 1024;

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

    public static DetectedVideo FilterForDisplay(DetectedVideo video)
    {
        var variants = video.Variants.Where(v => !ShouldExcludeVariant(v)).ToList();
        if (variants.Count == 0)
            return video with { Variants = [] };

        return video with { Variants = variants };
    }
}
