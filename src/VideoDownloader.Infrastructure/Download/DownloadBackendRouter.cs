using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Download;

public sealed class DownloadBackendRouter : IDownloadBackendRouter
{
    public DownloadBackendKind Resolve(MediaVariant variant)
    {
        if (variant.Tracks.Any(t => t.Kind == MediaTrackKind.Image) ||
            string.Equals(variant.Container, "album", StringComparison.OrdinalIgnoreCase))
            return DownloadBackendKind.FfmpegAlbumSlideshow;

        if (variant.Tracks.Any(t => MediaContainer.IsManifest(t.Container, t.SourceUrl) ||
                                    t.TrackId == "audio-extract") ||
            MediaContainer.IsManifest(variant.Container, variant.SourceUrl) ||
            variant.Tracks.Any(t => IsStreamingManifest(t.SourceUrl)))
            return DownloadBackendKind.FfmpegRemux;
        if (variant.Tracks.Count > 1)
            return DownloadBackendKind.FfmpegMultiInput;

        var container = MediaContainer.Normalize(variant.Container, variant.SourceUrl);
        if (container is "hls" or "dash")
            return DownloadBackendKind.FfmpegRemux;

        // Single progressive track (video-only, audio-only, or combined) → direct HTTP.
        if (variant.Tracks.Count == 1)
        {
            var kind = variant.Tracks[0].Kind;
            if (kind is MediaTrackKind.Combined or MediaTrackKind.Video or MediaTrackKind.Audio)
                return DownloadBackendKind.DirectHttp;
        }

        return DownloadBackendKind.FfmpegRemux;
    }

    internal static bool IsStreamingManifest(Uri? url)
    {
        if (url is null)
            return false;
        if (MediaContainer.IsManifest(null, url))
            return true;
        var text = url.AbsoluteUri;
        return text.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
               text.Contains(".mpd", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("/hls_playlist", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("/manifest/hls", StringComparison.OrdinalIgnoreCase) ||
               (text.Contains("/dash/", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("manifest", StringComparison.OrdinalIgnoreCase));
    }
}
