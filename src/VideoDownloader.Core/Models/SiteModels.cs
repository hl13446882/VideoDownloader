namespace VideoDownloader.Core.Models;

using VideoDownloader.Core.Contracts;

public static class SiteIds
{
    public const string Generic = "generic";
    public const string YouTube = "youtube";
    public const string Bilibili = "bilibili";
    public const string Douyin = "douyin";
    public const string TikTok = "tiktok";
}

public enum SiteProbeStatus
{
    Success,
    RecoverableFailure,
    NotApplicable
}

public enum ProbeSource
{
    Generic,
    SiteAdapter,
    GenericFallback
}

public sealed record SiteProbeContext(
    Uri PageUrl,
    string? PageTitle,
    string? PageScriptJson,
    IReadOnlyList<MediaCandidate> GenericCandidates,
    IReadOnlyList<DetectedVideo> GenericVideos,
    IReadOnlyList<NormalizedNetworkEvent> RecentNetworkEvents,
    RequestContext RequestContext);

public sealed record SiteProbeResult(
    SiteProbeStatus Status,
    string SiteId,
    IReadOnlyList<DetectedVideo> Videos,
    bool AllowGenericFallback,
    string? ErrorCode);
