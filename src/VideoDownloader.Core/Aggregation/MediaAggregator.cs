using System.Collections.Concurrent;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Aggregation;

public sealed class MediaAggregator : IMediaAggregator
{
    private readonly ConcurrentDictionary<Guid, DetectedVideo> _videos = new();
    private readonly ConcurrentDictionary<string, Guid> _manifestIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _segmentPaths = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DetectedVideo> Aggregate(IEnumerable<MediaCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            switch (candidate.Kind)
            {
                case CandidateKind.DirectMedia:
                    AggregateDirect(candidate);
                    break;
                case CandidateKind.HlsManifest:
                case CandidateKind.DashManifest:
                    AggregateManifestPlaceholder(candidate);
                    break;
                case CandidateKind.HlsSegment:
                case CandidateKind.DashRepresentation:
                case CandidateKind.HlsVariant:
                    TrackSegment(candidate);
                    break;
            }
        }

        return _videos.Values.OrderByDescending(v => v.Variants.FirstOrDefault()?.Bandwidth ?? 0).ToList();
    }

    public DetectedVideo? UpdateManifestVideo(
        Uri manifestUrl,
        Uri pageUrl,
        MediaFamily family,
        IReadOnlyList<MediaVariant> variants,
        bool isDrmProtected,
        string displayTitle)
    {
        var key = manifestUrl.AbsoluteUri;
        var videoId = _manifestIndex.GetOrAdd(key, _ => DeterministicGuid($"manifest:{key}"));

        var video = new DetectedVideo(
            videoId,
            SiteIds.Generic,
            null,
            displayTitle,
            pageUrl,
            family,
            variants,
            isDrmProtected,
            ProbeSource.Generic);

        _videos[videoId] = video;
        return video;
    }

    public DetectedVideo? GetByManifestUrl(Uri manifestUrl)
    {
        if (_manifestIndex.TryGetValue(manifestUrl.AbsoluteUri, out var id) &&
            _videos.TryGetValue(id, out var video))
            return video;

        return null;
    }

    public void Clear()
    {
        _videos.Clear();
        _manifestIndex.Clear();
        _segmentPaths.Clear();
    }

    private void AggregateDirect(MediaCandidate candidate)
    {
        if (MediaResourceSizeFilter.ShouldExcludeFromDisplay(candidate.Resource, candidate.Kind))
            return;

        var resource = candidate.Resource;
        var pageKey = resource.PageUrl?.AbsoluteUri ?? resource.Url.GetLeftPart(UriPartial.Authority);
        var videoId = DeterministicGuid($"direct:{pageKey}:{resource.Url.AbsolutePath}");

        var variant = MediaVariant.FromCombinedTrack(
            "default",
            resource.Url,
            resource.RequestContext,
            bandwidth: resource.ContentLength,
            container: GetContainer(resource),
            contentLength: resource.ContentLength);

        _videos[videoId] = new DetectedVideo(
            videoId,
            SiteIds.Generic,
            null,
            BuildTitle(resource),
            resource.PageUrl ?? resource.Url,
            MediaFamily.DirectMp4,
            [variant],
            false,
            ProbeSource.Generic);
    }

    private void AggregateManifestPlaceholder(MediaCandidate candidate)
    {
        var resource = candidate.Resource;
        var family = candidate.Kind == CandidateKind.HlsManifest ? MediaFamily.Hls : MediaFamily.Dash;
        var key = resource.Url.AbsoluteUri;
        if (_manifestIndex.ContainsKey(key))
            return;

        var videoId = DeterministicGuid($"manifest:{key}");
        _manifestIndex[key] = videoId;

        _videos[videoId] = new DetectedVideo(
            videoId,
            SiteIds.Generic,
            null,
            BuildTitle(resource),
            resource.PageUrl ?? resource.Url,
            family,
            [],
            false,
            ProbeSource.Generic);
    }

    private void TrackSegment(MediaCandidate candidate) =>
        _segmentPaths.TryAdd(candidate.Resource.Url.AbsolutePath, 0);

    public bool IsKnownSegment(Uri url) =>
        _segmentPaths.ContainsKey(url.AbsolutePath);

    private static string BuildTitle(MediaResource resource)
    {
        var fileName = Path.GetFileName(resource.Url.AbsolutePath);
        if (!string.IsNullOrWhiteSpace(fileName))
            return fileName;

        return resource.PageUrl is not null
            ? resource.PageUrl.Host
            : "Detected Video";
    }

    private static string? GetContainer(MediaResource resource)
    {
        var ext = Path.GetExtension(resource.Url.AbsolutePath).TrimStart('.');
        if (!string.IsNullOrEmpty(ext))
            return ext;

        return resource.MimeType?.Split(';', 2)[0].Trim().ToLowerInvariant() switch
        {
            "video/mp4" or "audio/mp4" => "mp4",
            "video/webm" or "audio/webm" => "webm",
            "video/x-m4v" => "m4v",
            _ => null
        };
    }

    private static Guid DeterministicGuid(string input)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }
}
