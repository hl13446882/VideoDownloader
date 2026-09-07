using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Download;

public sealed class DownloadBackendRouter : IDownloadBackendRouter
{
    public DownloadBackendKind Resolve(MediaVariant variant)
    {
        if (variant.Tracks.Any(t => t.Container is "hls" or "dash" || t.TrackId == "audio-extract"))
            return DownloadBackendKind.FfmpegRemux;
        if (variant.Tracks.Count > 1)
            return DownloadBackendKind.FfmpegMultiInput;

        var container = variant.Container?.ToLowerInvariant();
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
}
