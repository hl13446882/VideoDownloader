using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Contracts;

public interface ISiteDetectionRouter
{
    SiteKind Resolve(Uri pageUrl);
    string Describe(SiteKind kind);
}

/// <summary>
/// Exclusive site media detector. Exactly one process-wide instance per site
/// (Douyin / TikTok / YouTube / Bilibili). Never construct a second instance.
/// <see cref="BeginSession"/> destroys any previous session on this instance and
/// starts the only active run; <see cref="Clear"/> / <see cref="HardClear"/> tear it down.
/// </summary>
public interface IExclusiveSiteMediaDetector
{
    string Name { get; }
    SiteKind Site { get; }
    bool Matches(Uri pageUrl);

    /// <summary>Destroy prior session on this singleton, then start the only active run.</summary>
    void BeginSession(Uri pageUrl, Guid sessionId);

    /// <summary>Tear down the active session on this singleton.</summary>
    void Clear();

    /// <summary>
    /// Tear down the active session including any soft-nav parks.
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
