using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Contracts;

public interface ISiteDetectionRouter
{
    SiteKind Resolve(Uri pageUrl);
    string Describe(SiteKind kind);
}

/// <summary>
/// Exclusive site media detector. Owns all discovery for its site; never shares
/// detection logic with Generic/Unified or other site detectors.
/// </summary>
public interface IExclusiveSiteMediaDetector
{
    string Name { get; }
    SiteKind Site { get; }
    bool Matches(Uri pageUrl);

    void BeginSession(Uri pageUrl, Guid sessionId);
    void Clear();

    /// <summary>
    /// Manual probe兜底: wipe all session state including soft-nav parks.
    /// Soft <see cref="Clear"/> may preserve parked progressive for feed adopt.
    /// </summary>
    void HardClear() => Clear();

    Task ProcessNetworkAsync(NormalizedNetworkEvent networkEvent, CancellationToken ct);

    Task ProcessPageObservationAsync(
        Uri pageUrl,
        string? pageTitle,
        string? pageScriptJson,
        RequestContext context,
        CancellationToken ct);

    Task CompleteAsync(CancellationToken ct);

    bool Failed { get; }
    string? FailureReason { get; }

    event EventHandler<IReadOnlyList<MediaDescriptor>>? DescriptorsReady;
}

public interface IExclusiveSiteMediaDetectorResolver
{
    IExclusiveSiteMediaDetector? Resolve(Uri pageUrl);
    IReadOnlyList<IExclusiveSiteMediaDetector> All { get; }
}
