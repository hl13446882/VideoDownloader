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
}
