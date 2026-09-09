namespace VideoDownloader.Core.Models;

/// <summary>Exclusive site routing kind — decided before any media detection starts.</summary>
public enum SiteKind
{
    Other = 0,
    Douyin = 1,
    TikTok = 2,
    YouTube = 3,
    Bilibili = 4
}

/// <summary>Detected content shape for exclusive site detectors.</summary>
public enum MediaContentType
{
    Unknown = 0,
    Video = 1,
    Album = 2,
    Audio = 3
}

public enum DouyinContentMode
{
    Unknown = 0,
    Video = 1,
    Album = 2
}

/// <summary>Ordered still for album/image-post works.</summary>
public sealed record AlbumImageItem(
    int Index,
    Uri Url,
    int? Width,
    int? Height,
    string? Format,
    RequestContext RequestContext);

/// <summary>Standard exclusive-detector output before mapping to DetectedVideo.</summary>
public sealed record MediaDescriptor(
    string Site,
    Uri PageUrl,
    string? MediaId,
    MediaContentType ContentType,
    MediaTrack? Video,
    MediaTrack? Audio,
    IReadOnlyList<AlbumImageItem> Images,
    RequestContext RequestContext,
    double Confidence,
    string? DisplayTitle = null,
    string? Author = null)
{
    public Guid SessionId { get; init; }

    /// <summary>
    /// Pre-built format ladder (e.g. yt-dlp heights). When set, mapper prefers these over Video/Audio.
    /// </summary>
    public IReadOnlyList<MediaVariant> Formats { get; init; } = [];
}
