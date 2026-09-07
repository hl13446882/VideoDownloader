namespace VideoDownloader.Core.Models;

public enum MediaType
{
    Unknown,
    DirectVideo,
    HlsManifest,
    HlsSegment,
    DashManifest,
    DashSegment,
    Audio
}

public enum CandidateKind
{
    DirectMedia,
    HlsManifest,
    HlsVariant,
    HlsSegment,
    DashManifest,
    DashRepresentation
}

public enum MediaFamily
{
    DirectMp4,
    Hls,
    Dash
}

public enum DownloadStatus
{
    Pending,
    Preparing,
    Downloading,
    Paused,
    Muxing,
    Completed,
    Failed,
    Cancelled,
    Removed
}

public enum NetworkEventSource
{
    Cdp,
    WebResource
}
