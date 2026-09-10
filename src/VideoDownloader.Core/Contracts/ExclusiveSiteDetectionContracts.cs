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
/// One active session at a time: <see cref="BeginSession"/> atomically destroys the
/// previous session on this instance. All interception/candidates are session-scoped;
/// async results must be dropped when their SessionId is no longer current.
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

    /// <summary>Same as <see cref="Clear"/> for exclusive detectors (full session destroy).</summary>
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

    /// <summary>Current work content id when known (e.g. Douyin aweme id).</summary>
    string? ActiveContentId => null;

    event EventHandler<IReadOnlyList<MediaDescriptor>>? DescriptorsReady;
}

public interface IExclusiveSiteMediaDetectorResolver
{
    IExclusiveSiteMediaDetector? Resolve(Uri pageUrl);
    IReadOnlyList<IExclusiveSiteMediaDetector> All { get; }
}
