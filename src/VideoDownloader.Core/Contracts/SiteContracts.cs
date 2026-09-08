using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Contracts;

public interface ISiteAdapter
{
    string SiteId { get; }
    int Priority { get; }
    bool CanHandle(Uri pageUrl);
    Task<SiteProbeResult> ProbeAsync(SiteProbeContext context, CancellationToken ct);
}

public interface ISiteAdapterRouter
{
    Task<IReadOnlyList<DetectedVideo>> ProbeAsync(SiteProbeContext context, CancellationToken ct);
}

public interface ISiteEnablementPolicy
{
    bool PreferSiteAdapters { get; }
    bool FallbackToGeneric { get; }
    bool IsSiteEnabled(string siteId);
}

public interface IExternalSiteResolver
{
    bool IsAvailable { get; }
    bool SupportsSite(string siteId);
    string? LastError { get; }
    /// <summary>True when the last failure is human/bot verification (not a generic miss).</summary>
    bool LastFailureIsHumanVerification { get; }
    Task<IReadOnlyList<DetectedVideo>> ResolveAsync(
        Uri pageUrl,
        RequestContext context,
        CancellationToken ct);
}

public interface ISiteProbeOrchestrator
{
    void RecordNetworkEvent(NormalizedNetworkEvent normalized);
    void RecordGenericVideo(DetectedVideo video);
    Task<IReadOnlyList<DetectedVideo>> ProbePageAsync(
        Uri pageUrl,
        string? pageTitle,
        string? pageScriptJson,
        RequestContext context,
        CancellationToken ct);
    void Clear();
}

public interface IDownloadBackendRouter
{
    DownloadBackendKind Resolve(MediaVariant variant);
}

public enum DownloadBackendKind
{
    DirectHttp,
    FfmpegRemux,
    FfmpegMultiInput
}
