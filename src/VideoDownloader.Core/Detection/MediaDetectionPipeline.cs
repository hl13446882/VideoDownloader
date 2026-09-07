using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Detection;

public sealed class MediaDetectionPipeline : IMediaDetectionPipeline
{
    private readonly IMediaDetector _detector;
    private readonly IMediaAggregator _aggregator;
    private readonly IManifestResolver _manifestResolver;
    private readonly ISiteProbeOrchestrator _siteProbe;
    private readonly object _sync = new();

    public MediaDetectionPipeline(
        IMediaDetector detector,
        IMediaAggregator aggregator,
        IManifestResolver manifestResolver,
        ISiteProbeOrchestrator siteProbe)
    {
        _detector = detector;
        _aggregator = aggregator;
        _manifestResolver = manifestResolver;
        _siteProbe = siteProbe;
    }

    public event EventHandler<DetectedVideo>? VideoDetected;
    public event EventHandler<DetectedVideo>? VideoUpdated;
    public event EventHandler<IReadOnlyList<DetectedVideo>>? PageProbed;

    public Task ProcessAsync(NormalizedNetworkEvent normalized, CancellationToken ct)
    {
        if (normalized.StatusCode is not (200 or 206 or null))
            return Task.CompletedTask;

        _siteProbe.RecordNetworkEvent(normalized);

        var resource = new MediaResource(
            Guid.NewGuid(),
            normalized.Url,
            MediaType.Unknown,
            normalized.MimeType,
            normalized.Method,
            normalized.StatusCode,
            normalized.ContentLength,
            normalized.ResourceType,
            normalized.Initiator,
            normalized.PageUrl,
            normalized.FrameId,
            normalized.RequestContext,
            normalized.ResponseHeaders,
            normalized.Timestamp);

        var candidate = _detector.Detect(resource);
        if (candidate is null)
            return Task.CompletedTask;

        if (MediaResourceSizeFilter.ShouldExcludeFromDisplay(resource, candidate.Kind))
            return Task.CompletedTask;

        if (candidate.Kind is CandidateKind.HlsSegment or CandidateKind.DashRepresentation)
            return Task.CompletedTask;

        DetectedVideo? video;
        lock (_sync)
        {
            var videos = _aggregator.Aggregate([candidate]);
            video = candidate.Kind is CandidateKind.HlsManifest or CandidateKind.DashManifest
                ? _aggregator.GetByManifestUrl(resource.Url)
                : videos.FirstOrDefault(v => v.Variants.Any(x => x.SourceUrl == resource.Url));
        }

        if (video is null)
            return Task.CompletedTask;

        var display = MediaResourceSizeFilter.FilterForDisplay(video);
        if (display.Variants.Count == 0)
            return Task.CompletedTask;

        _siteProbe.RecordGenericVideo(display);
        VideoDetected?.Invoke(this, display);

        if (candidate.Kind is CandidateKind.HlsManifest or CandidateKind.DashManifest)
            _ = ResolveManifestAsync(candidate, resource, ct);

        return Task.CompletedTask;
    }

    public async Task ProbePageAsync(
        Uri pageUrl,
        string? pageTitle,
        string? pageScriptJson,
        RequestContext context,
        CancellationToken ct,
        bool runExternal = false)
    {
        var results = await _siteProbe.ProbePageAsync(pageUrl, pageTitle, pageScriptJson, context, ct);
        PageProbed?.Invoke(this, results);

        foreach (var video in results)
        {
            var display = MediaResourceSizeFilter.FilterForDisplay(video);
            if (display.Variants.Count == 0)
                continue;

            VideoDetected?.Invoke(this, display);
        }

        _ = runExternal;
    }

    public void Clear()
    {
        _aggregator.Clear();
        _siteProbe.Clear();
    }

    private async Task ResolveManifestAsync(
        MediaCandidate candidate,
        MediaResource resource,
        CancellationToken ct)
    {
        try
        {
            ManifestResolutionResult result = candidate.Kind == CandidateKind.HlsManifest
                ? await _manifestResolver.ResolveHlsAsync(resource, ct)
                : await _manifestResolver.ResolveDashAsync(resource, ct);

            var family = candidate.Kind == CandidateKind.HlsManifest ? MediaFamily.Hls : MediaFamily.Dash;
            DetectedVideo? updated;
            lock (_sync)
            {
                updated = _aggregator.UpdateManifestVideo(
                    resource.Url,
                    resource.PageUrl ?? resource.Url,
                    family,
                    result.Variants,
                    result.IsDrmProtected,
                    BuildTitle(resource));
            }

            if (updated is not null)
            {
                var display = MediaResourceSizeFilter.FilterForDisplay(updated);
                if (display.Variants.Count > 0)
                {
                    _siteProbe.RecordGenericVideo(display);
                    VideoUpdated?.Invoke(this, display);
                }
            }
        }
        catch
        {
            // Manifest fetch/parse failures should not break the detection pipeline.
        }
    }

    private static string BuildTitle(MediaResource resource)
    {
        var fileName = Path.GetFileName(resource.Url.AbsolutePath);
        return string.IsNullOrWhiteSpace(fileName) ? "Detected Video" : fileName;
    }
}
