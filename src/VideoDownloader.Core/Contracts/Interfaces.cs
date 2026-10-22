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
    bool IsDrmProtected,
    double? DurationSec = null,
    Uri? FirstSegmentUrl = null);

public interface IRequestMessageFactory
{
    HttpRequestMessage Create(MediaResource resource, HttpMethod method);

    HttpRequestMessage Create(MediaVariant variant, HttpMethod method, Uri url);
}

public interface IDownloadEngine
{
    Task<Guid> EnqueueAsync(
        MediaVariant variant,
        string displayName,
        Uri? pageUrl = null,
        string? caption = null,
        double? durationSec = null,
        IReadOnlyList<MediaVariant>? siblingVariants = null,
        string? author = null,
        CancellationToken ct = default);

    Task PauseAsync(Guid jobId, CancellationToken ct = default);

    Task ResumeAsync(Guid jobId, CancellationToken ct = default);

    Task CancelAsync(Guid jobId, CancellationToken ct = default);

    /// <param name="deleteFile">When true, also delete the completed target file on disk.</param>
    Task RemoveAsync(Guid jobId, bool deleteFile = false, CancellationToken ct = default);

    /// <summary>Renames the job stem (not extension). Keeps queue name and on-disk file in sync.</summary>
    Task<string> RenameAsync(Guid jobId, string newStem, CancellationToken ct = default);

    /// <summary>
    /// Moves completed on-disk files under <paramref name="newRoot"/> (preserving site subfolders),
    /// updates TargetPath, and deletes sources. Refuses when any job is in-flight.
    /// </summary>
    Task<DownloadMigrateResult> MigrateCompletedToSaveRootAsync(string newRoot, CancellationToken ct = default);

    /// <summary>
    /// Updates completed-library fields: optional title head (meta suffix preserved), caption, video kind.
    /// </summary>
    Task UpdateLibraryItemAsync(
        Guid jobId,
        string? titleHead = null,
        string? caption = null,
        string? videoKind = null,
        CancellationToken ct = default);

    /// <summary>Reconnects private library records to uniquely matched encrypted files after legacy renames.</summary>
    Task RepairPrivateLibraryAsync(string kindValue, CancellationToken ct = default);

    /// <summary>
    /// Persists whether sequential subtitle recognition finished for a completed library job.
    /// Optionally refreshes <see cref="DownloadJob.DurationSec"/> when probed.
    /// </summary>
    Task SetSubtitleRecognizedAsync(
        Guid jobId,
        bool recognized,
        double? durationSec = null,
        CancellationToken ct = default);

    Task RecoverOnStartupAsync(CancellationToken ct = default);

    /// <summary>
    /// Registers an already-copied local file as a completed library job (import).
    /// Applies filename meta, persists, and kicks off thumbnail generation.
    /// </summary>
    Task RegisterImportedCompletedJobAsync(DownloadJob job, CancellationToken ct = default);

    IReadOnlyList<DownloadJob> GetActiveJobs();

    /// <summary>Concurrency permits still free. This is the download gate, not the queue length.</summary>
    int FreeDownloadSlots { get; }

    /// <summary>Permits configured on the download gate (1–5). Changes apply after restart.</summary>
    int MaxDownloadSlots { get; }
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
    Task UpdateThumbReadyAsync(Guid id, bool ready, CancellationToken ct = default);
}

/// <summary>Persists automatic-mode sessions and probed download-address queues.</summary>
public interface IAutoSessionRepository
{
    Task InitializeAsync(CancellationToken ct = default);
    Task SaveSessionAsync(AutoSession session, CancellationToken ct = default);
    Task<AutoSession?> GetSessionAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<AutoSession>> GetSessionsAsync(CancellationToken ct = default);
    Task<AutoSession?> GetRunningSessionAsync(CancellationToken ct = default);
    Task PauseAllRunningAsync(CancellationToken ct = default);
    Task DeleteSessionAsync(Guid id, CancellationToken ct = default);

    Task<AutoQueueItem?> TryAddQueuedItemAsync(AutoQueueItem item, CancellationToken ct = default);
    Task SaveItemAsync(AutoQueueItem item, CancellationToken ct = default);
    Task<IReadOnlyList<AutoQueueItem>> GetItemsAsync(Guid sessionId, CancellationToken ct = default);
    Task<AutoQueueItem?> TryClaimNextQueuedAsync(Guid sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetActiveJobIdsAsync(Guid sessionId, CancellationToken ct = default);
    Task DeleteItemAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Local-DB library categories. Seeds are inserted only when missing; never overwrite local rows.
/// </summary>
public interface ILibraryKindCatalog
{
    Task InitializeAsync(CancellationToken ct = default);
    IReadOnlyList<LibraryKindEntry> GetAll();
    string Normalize(string? value);
    string LabelOf(string? value);
    bool IsKnown(string? value);
    Task<LibraryKindEntry> AddAsync(string label, bool isPrivate = false, string? password = null, CancellationToken ct = default);
    Task<bool> VerifyPasswordAsync(string kindValue, string password, CancellationToken ct = default);
    Task RemovePrivacyAsync(string kindValue, string password, CancellationToken ct = default);
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

    /// <summary>Reads duration (seconds) and video height from a local completed file.</summary>
    Task<(double? DurationSec, int? Height)> ProbeLocalFileAsync(string path, CancellationToken ct);

    /// <summary>Probes duration, video height, stream kinds, and common author tags from a local file.</summary>
    Task<LocalMediaProbeResult> ProbeLocalMediaAsync(string path, CancellationToken ct);

    /// <summary>Extract a JPEG poster frame for local library cards. Returns false on soft failure.</summary>
    Task<bool> TryExtractThumbnailAsync(string videoPath, string jpegPath, CancellationToken ct);
}

/// <summary>ffprobe summary for a local media file.</summary>
public sealed record LocalMediaProbeResult(
    double? DurationSec,
    int? Height,
    bool HasVideo,
    bool HasAudio,
    string? Author);
