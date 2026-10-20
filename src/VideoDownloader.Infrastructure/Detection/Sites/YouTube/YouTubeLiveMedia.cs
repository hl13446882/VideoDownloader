using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Detection.Sites.YouTube;

public static class YouTubeLiveMedia
{
    public static bool IsYouTubePage(Uri page) =>
        page.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        page.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
        page.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
        page.Host.Equals("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase) ||
        page.Host.EndsWith(".youtube-nocookie.com", StringComparison.OrdinalIgnoreCase);

    // Live DVR manifests explicitly carry live/1. Archived DASH formats may still contain
    // source=yt_live_broadcast, so that discriminator alone must not reject recordings.
    public static bool IsLiveUrl(Uri url) =>
        (url.Host.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
         url.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase)) &&
        url.AbsolutePath.Contains("/live/1/", StringComparison.OrdinalIgnoreCase) &&
        url.AbsolutePath.Contains("/source/yt_live_broadcast/", StringComparison.OrdinalIgnoreCase);

    public static bool IsLiveVariant(MediaVariant variant) => variant.Tracks.Any(t => IsLiveUrl(t.SourceUrl));
}
