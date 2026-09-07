using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Detection;

public sealed class MediaDetector : IMediaDetector
{
    private const long MinMediaBytes = 64 * 1024;
    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg",
        ".js", ".css", ".woff", ".woff2", ".ttf", ".ico", ".html"
    };

    public MediaCandidate? Detect(MediaResource resource)
    {
        var url = resource.Url.AbsolutePath.ToLowerInvariant();
        var mime = resource.MimeType?.ToLowerInvariant() ?? string.Empty;

        if (IsExcluded(url, mime))
            return null;

        if (mime.Contains("application/vnd.apple.mpegurl", StringComparison.Ordinal) ||
            mime.Contains("application/x-mpegurl", StringComparison.Ordinal) ||
            url.EndsWith(".m3u8", StringComparison.Ordinal))
            return Candidate(resource, CandidateKind.HlsManifest, 95, "HLS MIME/extension");

        if (mime.Contains("application/dash+xml", StringComparison.Ordinal) ||
            url.EndsWith(".mpd", StringComparison.Ordinal))
            return Candidate(resource, CandidateKind.DashManifest, 95, "DASH MIME/extension");

        if (url.EndsWith(".ts", StringComparison.Ordinal) || url.EndsWith(".m4s", StringComparison.Ordinal))
            return Candidate(resource, CandidateKind.HlsSegment, 60, "Segment extension");

        if (mime.StartsWith("video/", StringComparison.Ordinal) ||
            url.EndsWith(".mp4", StringComparison.Ordinal) ||
            url.EndsWith(".webm", StringComparison.Ordinal) ||
            url.EndsWith(".m4v", StringComparison.Ordinal))
        {
            var confidence = 80;
            if (resource.ContentLength is > 1024 * 1024)
                confidence += 10;
            if (HasRangeHeader(resource))
                confidence += 10;
            if (resource.ContentLength is > 0 and < MinMediaBytes && !mime.StartsWith("video/", StringComparison.Ordinal))
                confidence -= 30;
            if (confidence < 50)
                return null;
            return Candidate(resource, CandidateKind.DirectMedia, confidence, "Video MIME/extension");
        }

        if (mime.StartsWith("audio/", StringComparison.Ordinal))
            return Candidate(resource, CandidateKind.DirectMedia, 70, "Audio MIME");

        return null;
    }

    private static bool IsExcluded(string url, string mime)
    {
        if (mime.StartsWith("image/", StringComparison.Ordinal) ||
            mime.StartsWith("text/", StringComparison.Ordinal) ||
            mime.Contains("javascript", StringComparison.Ordinal) ||
            mime.Contains("json", StringComparison.Ordinal))
            return true;

        return ExcludedExtensions.Any(ext => url.EndsWith(ext, StringComparison.Ordinal));
    }

    private static bool HasRangeHeader(MediaResource resource) =>
        resource.RequestContext.Headers.ContainsKey("range") ||
        resource.ResponseHeaders.Keys.Any(k => k.Equals("content-range", StringComparison.OrdinalIgnoreCase));

    private static MediaCandidate Candidate(
        MediaResource resource,
        CandidateKind kind,
        int confidence,
        string reason) =>
        new(resource, kind, confidence, reason);
}
