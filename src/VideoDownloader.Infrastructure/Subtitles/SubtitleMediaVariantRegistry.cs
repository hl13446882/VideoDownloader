using System.Collections.Concurrent;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Subtitles;

/// <summary>
/// Reuses variants already discovered by the normal media-detection pipeline so subtitle audio
/// decoding does not create a second site-specific detector.
/// </summary>
public sealed class SubtitleMediaVariantRegistry : IDisposable
{
    private readonly IMediaDetectionPipeline _pipeline;
    private readonly ConcurrentDictionary<string, Entry> _byPage = new(StringComparer.OrdinalIgnoreCase);

    public SubtitleMediaVariantRegistry(IMediaDetectionPipeline pipeline)
    {
        _pipeline = pipeline;
        _pipeline.VideoDetected += OnVideo;
        _pipeline.VideoUpdated += OnVideo;
        _pipeline.PageProbed += OnPageProbed;
    }

    public MediaVariant? Resolve(string? pageUrl, string? mediaKey = null)
    {
        if (!string.IsNullOrWhiteSpace(mediaKey) && Uri.TryCreate(mediaKey, UriKind.Absolute, out var mediaUri))
        {
            foreach (var entry in _byPage.Values.OrderByDescending(x => x.UpdatedAt))
            {
                var exact = entry.Video.Variants
                    .FirstOrDefault(v => v.Tracks.Any(t => UriEquals(t.SourceUrl, mediaUri)));
                if (exact is not null)
                    return ChooseAudioCapable(exact);
            }
        }

        if (string.IsNullOrWhiteSpace(pageUrl))
            return null;

        var exactKey = NormalizePage(pageUrl, keepQuery: true);
        if (exactKey is not null && _byPage.TryGetValue(exactKey, out var exactEntry))
            return ChooseBest(exactEntry.Video);

        var pathKey = NormalizePage(pageUrl, keepQuery: false);
        if (pathKey is not null && _byPage.TryGetValue(pathKey, out var pathEntry))
            return ChooseBest(pathEntry.Video);

        return null;
    }

    private void OnVideo(object? sender, DetectedVideo video) => Store(video);

    private void OnPageProbed(object? sender, IReadOnlyList<DetectedVideo> videos)
    {
        foreach (var video in videos)
            Store(video);
    }

    private void Store(DetectedVideo video)
    {
        if (video.IsDrmProtected || video.Variants.Count == 0)
            return;

        var entry = new Entry(video, DateTimeOffset.UtcNow);
        var exact = NormalizePage(video.PageUrl.AbsoluteUri, keepQuery: true);
        var path = NormalizePage(video.PageUrl.AbsoluteUri, keepQuery: false);
        if (exact is not null)
            _byPage[exact] = entry;
        if (path is not null)
            _byPage[path] = entry;

        if (_byPage.Count > 256)
        {
            foreach (var stale in _byPage.OrderBy(x => x.Value.UpdatedAt).Take(_byPage.Count - 192).ToArray())
                _byPage.TryRemove(stale.Key, out _);
        }
    }

    private static MediaVariant? ChooseBest(DetectedVideo video)
    {
        return video.Variants
            .Select(ChooseAudioCapable)
            .Where(v => v is not null)
            .OrderByDescending(v => Score(v!))
            .FirstOrDefault();
    }

    private static MediaVariant? ChooseAudioCapable(MediaVariant variant)
    {
        if (variant.Tracks.Any(t => t.Kind == MediaTrackKind.Audio && !t.IsMseTrack))
            return variant;
        if (variant.Tracks.Any(t => t.Kind == MediaTrackKind.Combined && !t.IsMseTrack))
            return variant;
        if (variant.Tracks.Any(t => t.Kind is MediaTrackKind.Audio or MediaTrackKind.Combined))
            return variant;
        return null;
    }

    private static int Score(MediaVariant variant)
    {
        var score = 0;
        if (variant.Tracks.Any(t => t.Kind == MediaTrackKind.Audio && !t.IsMseTrack)) score += 100;
        if (variant.Tracks.Any(t => t.Kind == MediaTrackKind.Combined && !t.IsMseTrack)) score += 80;
        if (variant.Tracks.Any(t => t.BrowserObserved)) score += 30;
        if (variant.Tracks.All(t => !t.IsMseTrack)) score += 20;
        if (variant.Bandwidth is > 0) score += 5;
        return score;
    }

    private static string? NormalizePage(string value, bool keepQuery)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return null;
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        if (!keepQuery)
            builder.Query = string.Empty;
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static bool UriEquals(Uri left, Uri right) =>
        string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.Ordinal);

    public void Dispose()
    {
        _pipeline.VideoDetected -= OnVideo;
        _pipeline.VideoUpdated -= OnVideo;
        _pipeline.PageProbed -= OnPageProbed;
    }

    private sealed record Entry(DetectedVideo Video, DateTimeOffset UpdatedAt);
}
