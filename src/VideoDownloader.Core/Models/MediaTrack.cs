namespace VideoDownloader.Core.Models;

public enum MediaTrackKind
{
    Video,
    Audio,
    Combined,
    Unknown
}

public sealed record MediaTrack(
    string TrackId,
    MediaTrackKind Kind,
    Uri SourceUrl,
    string? Codec,
    string? Container,
    long? Bandwidth,
    long? ContentLength,
    RequestContext RequestContext)
{
    public string? ContentIdentity { get; init; }
    public bool IsValidated { get; init; }
}
