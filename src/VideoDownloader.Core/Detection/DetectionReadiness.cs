using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Detection;

public static class DetectionReadiness
{
    public static bool IsUsable(DetectedVideo video) =>
        !video.IsLive && !video.IsDrmProtected &&
        video.Availability == MediaAvailabilityKind.Complete &&
        video.Variants.Any(v => v.Tracks.Any(t => t.Kind is MediaTrackKind.Combined or MediaTrackKind.Image) ||
            (v.Tracks.Any(t => t.Kind == MediaTrackKind.Video) &&
             (video.SiteId is not (SiteIds.YouTube or SiteIds.Bilibili) ||
              v.Tracks.Any(t => t.Kind == MediaTrackKind.Audio))));
}
