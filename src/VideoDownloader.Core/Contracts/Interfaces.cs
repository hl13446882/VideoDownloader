using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

using System.Net.Http;

namespace VideoDownloader.Core.Contracts;

public interface INetworkEventNormalizer
{
    ValueTask<NormalizedNetworkEvent?> NormalizeAsync(
        RawNetworkEvent input,
        CancellationToken cancellationToken);
}

public interface IMediaDetector
{
    MediaCandidate? Detect(MediaResource resource);
}

public interface IMediaAggregator
{
    IReadOnlyList<DetectedVideo> Aggregate(IEnumerable<MediaCandidate> candidates);

    DetectedVideo? UpdateManifestVideo(
        Uri manifestUrl,
        Uri pageUrl,
        MediaFamily family,
        IReadOnlyList<MediaVariant> variants,
        bool isDrmProtected,
        string displayTitle);

    DetectedVideo? GetByManifestUrl(Uri manifestUrl);

    void Clear();
}

public interface IManifestContentFetcher
{
    Task<string> FetchAsync(Uri url, RequestContext context, CancellationToken ct);
}

public interface IManifestResolver
{
    Task<ManifestResolutionResult> ResolveHlsAsync(
        MediaResource manifest,
        CancellationToken ct);

    Task<ManifestResolutionResult> ResolveDashAsync(
        MediaResource manifest,
        CancellationToken ct);
}

public sealed record ManifestResolutionResult(
    IReadOnlyList<MediaVariant> Variants,
    bool IsDrmProtected);

public interface IRequestMessageFactory
{
    HttpRequestMessage Create(MediaResource resource, HttpMethod method);

    HttpRequestMessage Create(MediaVariant variant, HttpMethod method, Uri url);
}

public interface IDownloadEngine
{
    Task EnqueueAsync(
        MediaVariant variant,
        string displayName,
        Uri? pageUrl = null,
        CancellationToken ct = default);

    Task PauseAsync(Guid jobId, CancellationToken ct = default);

    Task ResumeAsync(Guid jobId, CancellationToken ct = default);

    Task CancelAsync(Guid jobId, CancellationToken ct = default);

    /// <param name="deleteFile">When true, also delete the completed target file on disk.</param>
    Task RemoveAsync(Guid jobId, bool deleteFile = false, CancellationToken ct = default);

    /// <summary>Renames the job stem (not extension). Keeps queue name and on-disk file in sync.</summary>
    Task<string> RenameAsync(Guid jobId, string newStem, CancellationToken ct = default);

    Task RecoverOnStartupAsync(CancellationToken ct = default);

    IReadOnlyList<DownloadJob> GetActiveJobs();
}

public interface IDownloadJobStateMachine
{
    void StartPreparing(DownloadJob job);
    void StartDownloading(DownloadJob job);
    void Pause(DownloadJob job);
    void Resume(DownloadJob job);
    void StartMuxing(DownloadJob job);
    void Complete(DownloadJob job);
    void Fail(DownloadJob job, string errorCode);
    void Cancel(DownloadJob job);
}

public interface IDownloadRepository
{
    Task SaveAsync(DownloadJob job, CancellationToken ct = default);
    Task<DownloadJob?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<DownloadJob>> GetAllAsync(CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task InitializeAsync(CancellationToken ct = default);
}

public interface IRequestContextProvider
{
    RequestContext CaptureCurrentContext(Uri? pageUrl, Uri resourceUrl);

    Task<RequestContext> RefreshContextAsync(
        Uri? pageUrl,
        Uri resourceUrl,
        RequestContext? previousContext,
        CancellationToken ct,
        bool forceCookies = false);
}

/// <summary>
/// Re-open a stable page in the active browser and wait for a fresh downloadable media address.
/// Used when signed CDN URLs 403 and offline resolvers cannot renew (e.g. Douyin).
/// </summary>
public interface IMediaAddressRediscoverer
{
    Task<MediaVariant?> RediscoverAsync(
        Uri recoveryPage,
        MediaVariant previous,
        CancellationToken ct);
}

public interface IMediaDetectionPipeline
{
    IDiscoveryScope? BeginDiscovery(Guid sessionId) => null;
    Guid SessionId => Guid.Empty;
    bool IsCompleted => false;
    void UpdateCaption(Guid sessionId, string caption) { }
    Task CompleteDiscoveryAsync(CancellationToken ct) => Task.CompletedTask;
    Task ProcessAsync(NormalizedNetworkEvent normalized, CancellationToken ct);

    Task ProbePageAsync(
        Uri pageUrl,
        string? pageTitle,
        string? pageScriptJson,
        RequestContext context,
        CancellationToken ct,
        bool runExternal = false);

    void Clear();

    event EventHandler<DetectedVideo>? VideoDetected;
    event EventHandler<DetectedVideo>? VideoUpdated;
    event EventHandler<IReadOnlyList<DetectedVideo>>? PageProbed;
}

public interface IDiscoveryScope : IDisposable
{
    IDiscoveryScope? Fork() => null;
    Task ProcessAsync(NormalizedNetworkEvent network, CancellationToken ct);
    Task SubmitAsync(Uri page, string json, RequestContext context, CancellationToken ct);
}

public interface IFfmpegAdapter
{
    Task RunRemuxAsync(
        Uri inputUrl,
        RequestContext context,
        string outputPath,
        CancellationToken ct);

    Task RunMultiInputRemuxAsync(
        IReadOnlyList<MediaTrack> tracks,
        string outputPath,
        CancellationToken ct);

    /// <summary>
    /// Equal-duration image slideshow + audio mux (Douyin/TikTok photo mode).
    /// Each still gets audioDuration / imageCount seconds.
    /// </summary>
    Task RunAlbumSlideshowAsync(
        IReadOnlyList<string> imagePaths,
        string audioPath,
        string outputPath,
        CancellationToken ct);
}
