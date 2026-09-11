namespace VideoDownloader.Core.Models;

public sealed record DetectedVideo(
    Guid VideoId,
    string SiteId,
    string? SiteContentId,
    string DisplayTitle,
    Uri PageUrl,
    MediaFamily Family,
    IReadOnlyList<MediaVariant> Variants,
    bool IsDrmProtected,
    ProbeSource ProbeSource = ProbeSource.Generic,
    string? StatusHint = null,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public Guid SessionId { get; init; }

    /// <summary>Complete A/V vs audio-only / video-denied partial results (T2).</summary>
    public MediaAvailabilityKind Availability { get; init; } = MediaAvailabilityKind.Complete;

    /// <summary>Content duration in seconds when known (player / yt-dlp / ffprobe).</summary>
    public double? DurationSec { get; init; }
}
