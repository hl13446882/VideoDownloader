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

    /// <summary>
    /// True when WebView2 CDP observed a successful Media/200/206 fetch for this URL.
    /// Stronger than a later independent sample GET (TikTok CDN often 403s the latter).
    /// </summary>
    public bool BrowserObserved { get; init; }

    /// <summary>
    /// Present only for clear-key encrypted HLS media playlists. Null for all other media.
    /// </summary>
    public HlsMedia? Hls { get; init; }
}
