using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Download;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Naming;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Http;
using VideoDownloader.Infrastructure.Licensing;
using VideoDownloader.Infrastructure.Security;

namespace VideoDownloader.Infrastructure.Download;

public sealed class DownloadEngine : IDownloadEngine, IDisposable
{
    private readonly HttpMediaDownloader _httpDownloader;
    private readonly M3u8DownloadAdapter _m3u8;
    private readonly IFfmpegAdapter _ffmpegAdapter;
    private readonly IDownloadJobStateMachine _stateMachine;
    private readonly IDownloadRepository _repository;
    private readonly IRequestContextProvider _contextProvider;
    private readonly IDownloadBackendRouter _backendRouter;
    private readonly AppOptions _options;
    private readonly ILogger<DownloadEngine> _logger;
    private readonly LicenseService _license;
    private readonly IReadOnlyList<IExternalSiteResolver> _resolvers;
    private readonly MediaAvailabilityValidator? _availability;
    private readonly ConcurrentDictionary<Guid, DownloadJob> _jobs = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _ctsMap = new();
    private readonly ConcurrentDictionary<Guid, byte> _removedJobs = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _runLocks = new();
    private readonly SemaphoreSlim _concurrency;
    private readonly object _targetPathSync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _failedRetryLoop;

    public DownloadEngine(
        HttpMediaDownloader httpDownloader,
        M3u8DownloadAdapter m3u8,
        IFfmpegAdapter ffmpegAdapter,
        IDownloadJobStateMachine stateMachine,
        IDownloadRepository repository,
        IRequestContextProvider contextProvider,
        IDownloadBackendRouter backendRouter,
        IOptions<AppOptions> options,
        ILogger<DownloadEngine> logger,
        LicenseService license,
        IEnumerable<IExternalSiteResolver>? resolvers = null,
        MediaAvailabilityValidator? availability = null)
    {
        _httpDownloader = httpDownloader;
        _m3u8 = m3u8;
        _ffmpegAdapter = ffmpegAdapter;
        _stateMachine = stateMachine;
        _repository = repository;
        _contextProvider = contextProvider;
        _backendRouter = backendRouter;
        _options = options.Value;
        _logger = logger;
        _license = license;
        _resolvers = (resolvers ?? []).ToArray();
        _availability = availability;
        _concurrency = new SemaphoreSlim(_options.Download.MaxConcurrentDownloads);
        _failedRetryLoop = Task.Run(() => FailedRetryLoopAsync(_lifetime.Token));
    }

    public async Task EnqueueAsync(
        MediaVariant variant,
        string displayName,
        Uri? pageUrl = null,
        CancellationToken ct = default)
    {
        if (_license.DownloadLimitBytes is int demoLimit && variant.TotalContentLength is long total && total > demoLimit)
            throw new DownloadException(ErrorCodes.LicenseLimit, "DEMO download limit is 10 MiB.");
        if (_license.DownloadLimitBytes is not null && variant.TotalContentLength is null &&
            _backendRouter.Resolve(variant) != DownloadBackendKind.DirectHttp)
            throw new DownloadException(ErrorCodes.LicenseLimit, "DEMO cannot start an external download with an unverified size.");

        var saveDir = PathExpander.Expand(_options.Download.DefaultSavePath);
        if (!PathValidator.IsValidSaveDirectory(saveDir))
            throw new DownloadException(ErrorCodes.InvalidSavePath, "Invalid or inaccessible save directory.");

        Directory.CreateDirectory(saveDir);

        // Clamp again to the shared filename limit before reserving the target path.
        var stem = DownloadFileNameBuilder.ClampStem(SanitizeFileName(displayName));
        var extension = ResolveDownloadExtension(variant);
        DownloadJob job;
        lock (_targetPathSync)
        {
            var targetPath = Path.Combine(saveDir, stem + extension);
            var sequence = 2;
            while (File.Exists(targetPath) || _jobs.Values.Any(job =>
                       string.Equals(job.TargetPath, targetPath, StringComparison.OrdinalIgnoreCase)))
            {
                stem = DownloadFileNameBuilder.WithSequenceSuffix(displayName, sequence++);
                targetPath = Path.Combine(saveDir, stem + extension);
            }

            job = new DownloadJob
            {
                Id = Guid.NewGuid(),
                DisplayName = stem,
                Variant = variant,
                PageUrl = pageUrl,
                TargetPath = targetPath,
                Status = DownloadStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _jobs[job.Id] = job;
        }
        await _repository.SaveAsync(job, ct);
        _ = RunJobAsync(job);
    }

    public async Task PauseAsync(Guid jobId, CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return;

        if (job.Status != DownloadStatus.Downloading)
            return;

        _stateMachine.Pause(job);
        if (_ctsMap.TryGetValue(jobId, out var cts))
            cts.Cancel();

        await _repository.SaveAsync(job, ct);
    }

    public Task ResumeAsync(Guid jobId, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var job) &&
            job.Status is DownloadStatus.Paused or DownloadStatus.Failed)
        {
            if (job.Status == DownloadStatus.Failed)
                job.LastErrorCode = null;
            _ = RunJobAsync(job);
        }

        return Task.CompletedTask;
    }

    public async Task CancelAsync(Guid jobId, CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return;

        if (job.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Removed)
            return;

        _removedJobs[jobId] = 0;
        if (_ctsMap.TryGetValue(jobId, out var cts))
            cts.Cancel();

        _stateMachine.Cancel(job);
        CleanupJobScratch(job, deleteTarget: false);
        _jobs.TryRemove(jobId, out _);
        await _repository.DeleteAsync(jobId, ct);
    }

    public IReadOnlyList<DownloadJob> GetActiveJobs() =>
        _jobs.Values
            .Where(j => j.Status is not (DownloadStatus.Removed or DownloadStatus.Cancelled))
            // Incomplete first; then newest UpdatedAt (completion time for finished jobs).
            .OrderBy(j => j.Status == DownloadStatus.Completed ? 1 : 0)
            .ThenByDescending(j => j.UpdatedAt)
            .ToList();

    public async Task RemoveAsync(Guid jobId, bool deleteFile = false, CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return;

        if (job.Status is not (DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled))
            return;

        _removedJobs[jobId] = 0;
        CleanupJobScratch(job, deleteTarget: deleteFile && job.Status == DownloadStatus.Completed);
        _jobs.TryRemove(jobId, out _);
        await _repository.DeleteAsync(jobId, ct);
    }

    public async Task<string> RenameAsync(Guid jobId, string newStem, CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            throw new InvalidOperationException("任务不存在。");

        if (job.Status is DownloadStatus.Cancelled or DownloadStatus.Removed)
            throw new InvalidOperationException("该任务不可重命名。");

        var extension = Path.GetExtension(job.TargetPath);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ResolveDownloadExtension(job.Variant);

        var stem = DownloadFileNameBuilder.NormalizeRenameStem(newStem, extension);
        if (string.IsNullOrWhiteSpace(stem))
            throw new InvalidOperationException("名称不能为空。");

        lock (_targetPathSync)
        {
            if (string.Equals(stem, job.DisplayName, StringComparison.Ordinal) &&
                string.Equals(
                    Path.GetFileNameWithoutExtension(job.TargetPath),
                    stem,
                    StringComparison.OrdinalIgnoreCase))
                return stem;

            var saveDir = Path.GetDirectoryName(job.TargetPath) ?? string.Empty;
            var candidateStem = stem;
            var sequence = 2;
            string targetPath;
            while (true)
            {
                targetPath = Path.Combine(saveDir, candidateStem + extension);
                var conflict = File.Exists(targetPath) ||
                               File.Exists(targetPath + ".part") ||
                               _jobs.Values.Any(j =>
                                   j.Id != job.Id &&
                                   string.Equals(j.TargetPath, targetPath, StringComparison.OrdinalIgnoreCase));
                if (!conflict ||
                    string.Equals(targetPath, job.TargetPath, StringComparison.OrdinalIgnoreCase))
                    break;
                candidateStem = DownloadFileNameBuilder.WithSequenceSuffix(stem, sequence++);
            }

            stem = candidateStem;
            var oldPath = job.TargetPath;
            var oldPart = oldPath + ".part";
            var newPart = targetPath + ".part";

            if (!string.Equals(oldPath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                TryMoveFile(oldPath, targetPath);
                TryMoveFile(oldPart, newPart);
            }
            else if (!string.Equals(oldPath, targetPath, StringComparison.Ordinal))
            {
                var temp = targetPath + ".rename-tmp";
                TryMoveFile(oldPath, temp);
                TryMoveFile(temp, targetPath);
                if (File.Exists(oldPart))
                {
                    var tempPart = newPart + ".rename-tmp";
                    TryMoveFile(oldPart, tempPart);
                    TryMoveFile(tempPart, newPart);
                }
            }

            job.DisplayName = stem;
            job.TargetPath = targetPath;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _repository.SaveAsync(job, ct);
        return stem;
    }

    private static void TryMoveFile(string source, string destination)
    {
        if (string.Equals(source, destination, StringComparison.Ordinal))
            return;
        if (!File.Exists(source))
            return;
        if (File.Exists(destination))
            throw new IOException($"目标已存在：{Path.GetFileName(destination)}");

        try
        {
            File.Move(source, destination);
        }
        catch (IOException)
        {
            // Locked by an in-flight writer; Finalize reconciles after the handle closes.
        }
    }

    private static void CleanupJobScratch(DownloadJob job, bool deleteTarget)
    {
        try
        {
            var partPath = job.TargetPath + ".part";
            if (File.Exists(partPath))
                File.Delete(partPath);

            var saveDir = Path.GetDirectoryName(job.TargetPath) ?? string.Empty;
            var partsDir = Path.Combine(saveDir, ".parts", job.Id.ToString("N"));
            if (Directory.Exists(partsDir))
                Directory.Delete(partsDir, recursive: true);

            TryDeleteEmptyPartsRoot(saveDir);

            if (deleteTarget && File.Exists(job.TargetPath))
                File.Delete(job.TargetPath);
        }
        catch
        {
            // Best-effort cleanup; queue removal must not fail on locked temp files.
        }
    }

    private static void TryDeleteEmptyPartsRoot(string saveDir)
    {
        try
        {
            var root = Path.Combine(saveDir, ".parts");
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Removes leftover <c>.part</c> / <c>.parts</c> for finished jobs only.
    /// Failed/paused jobs keep scratch so Resume can continue.
    /// </summary>
    private void CleanupOrphanedScratch(IEnumerable<DownloadJob> jobs)
    {
        var list = jobs.ToList();
        foreach (var job in list)
        {
            if (job.Status is not DownloadStatus.Completed)
                continue;
            CleanupJobScratch(job, deleteTarget: false);
        }

        var keepIds = new HashSet<string>(
            list.Where(j => j.Status is DownloadStatus.Downloading
                or DownloadStatus.Paused
                or DownloadStatus.Muxing
                or DownloadStatus.Failed
                or DownloadStatus.Pending
                or DownloadStatus.Preparing)
                .Select(j => j.Id.ToString("N")),
            StringComparer.OrdinalIgnoreCase);

        foreach (var saveDir in list
                     .Select(j => Path.GetDirectoryName(j.TargetPath))
                     .Where(d => !string.IsNullOrWhiteSpace(d))
                     .Distinct(StringComparer.OrdinalIgnoreCase)!)
        {
            var root = Path.Combine(saveDir!, ".parts");
            if (!Directory.Exists(root))
                continue;

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    if (keepIds.Contains(name))
                        continue;
                    try { Directory.Delete(dir, recursive: true); }
                    catch { /* locked */ }
                }

                TryDeleteEmptyPartsRoot(saveDir!);
            }
            catch
            {
                // ignore
            }
        }
    }

    public async Task RecoverOnStartupAsync(CancellationToken ct = default)
    {
        var persisted = await _repository.GetAllAsync(ct);
        foreach (var job in persisted)
        {
            var original = job.Status;
            if (original == DownloadStatus.Removed || original == DownloadStatus.Cancelled)
            {
                await _repository.DeleteAsync(job.Id, ct);
                continue;
            }

            var recovered = DownloadRecoveryRules.ResolveStartupStatus(original);
            job.Status = recovered;

            var startupError = DownloadRecoveryRules.ResolveStartupError(original);
            if (startupError is not null)
                job.LastErrorCode = startupError;

            if (job.Status == DownloadStatus.Completed && File.Exists(job.TargetPath))
                UpdateCompletedFileSize(job);

            job.UpdatedAt = DateTimeOffset.UtcNow;
            _jobs[job.Id] = job;
            await _repository.SaveAsync(job, ct);

            if (recovered == DownloadStatus.Paused && _options.Download.AutoRecoverDownloads)
                _ = RunJobAsync(job);
        }

        CleanupOrphanedScratch(_jobs.Values);

        _logger.LogInformation("Recovered {Count} download jobs from persistence.", persisted.Count);
    }

    private async Task RunJobAsync(DownloadJob job)
    {
        var runLock = _runLocks.GetOrAdd(job.Id, _ => new SemaphoreSlim(1));
        await runLock.WaitAsync();
        await _concurrency.WaitAsync();
        var cts = new CancellationTokenSource();
        _ctsMap[job.Id] = cts;

        try
        {
            if (_removedJobs.ContainsKey(job.Id))
                return;

            BeginExecution(job);
            await _repository.SaveAsync(job, cts.Token);

            Func<Task> checkpoint = () => _repository.SaveAsync(job, CancellationToken.None);
            var contextRefreshed = false;

            while (true)
            {
                try
                {
                    await ExecuteDownloadAsync(job, checkpoint, cts.Token);
                    break;
                }
                catch (DownloadException ex) when (
                    ex.ErrorCode == ErrorCodes.Http403 &&
                    !contextRefreshed &&
                    !cts.IsCancellationRequested)
                {
                    contextRefreshed = true;
                    _logger.LogWarning("Job {JobId} media HTTP_403; starting same-content recovery", job.Id);
                    var pageUrl = job.Variant.RecoveryPageUrl ?? ResolvePageUrl(job) ?? throw new DownloadException(ErrorCodes.ContextExpired,"Original page address is unavailable.");
                    var refreshed = await _contextProvider.RefreshContextAsync(
                        pageUrl,
                        job.Variant.SourceUrl,
                        job.Variant.RequestContext,
                        cts.Token);

                    try
                    {
                        job.Variant = await MediaAddressRenewal.ResolveAsync(pageUrl, job.Variant, refreshed, _resolvers, cts.Token,
                            _availability is null ? null : _availability.ValidateAsync);
                    }
                    catch (Exception recoveryError)
                    {
                        _logger.LogWarning("Job {JobId} original=HTTP_403 recovery={Reason}", job.Id,
                            VideoDownloader.Infrastructure.Logging.SanitizedLogger.SanitizeMessage(recoveryError.Message));
                        throw;
                    }
                    // Signed-address renewal starts a fresh transfer; old partial bytes are not assumed compatible.
                    var scratch = Path.Combine(Path.GetDirectoryName(job.TargetPath)!, ".parts", job.Id.ToString("N"));
                    if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
                    if (File.Exists(job.TargetPath + ".part")) File.Delete(job.TargetPath + ".part");
                    job.DownloadedBytes = 0;
                    job.TotalBytes = job.Variant.TotalContentLength;
                    job.ETag = null;
                    job.LastModified = null;
                    job.LastErrorCode = null;
                    await checkpoint();
                    _logger.LogInformation(
                        "Renewed media address for job {JobId}, context version={Version}",
                        job.Id,
                        refreshed.Version);
                }
            }

            if (_removedJobs.ContainsKey(job.Id)) return;
            _stateMachine.Complete(job);
            CleanupJobScratch(job, deleteTarget: false);
            await _repository.SaveAsync(job, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (_removedJobs.ContainsKey(job.Id))
                return;

            if (job.Status == DownloadStatus.Paused)
                await _repository.SaveAsync(job, CancellationToken.None);
            else
            {
                _stateMachine.Cancel(job);
                CleanupJobScratch(job, deleteTarget: false);
                await _repository.SaveAsync(job, CancellationToken.None);
            }
        }
        catch (DownloadException ex)
        {
            if (_removedJobs.ContainsKey(job.Id))
                return;

            _stateMachine.Fail(job, ex.ErrorCode);
            // Keep .part / .parts so Resume can continue; cleanup happens on cancel/remove/complete.
            await _repository.SaveAsync(job, CancellationToken.None);
            _logger.LogWarning("Download failed {JobId}: {Error}", job.Id, ex.ErrorCode);
        }
        catch (Exception ex)
        {
            if (_removedJobs.ContainsKey(job.Id))
                return;

            _stateMachine.Fail(job, ClassifyError(ex));
            await _repository.SaveAsync(job, CancellationToken.None);
            _logger.LogError(ex, "Download failed {JobId}", job.Id);
        }
        finally
        {
            _ctsMap.TryRemove(job.Id, out _);
            _concurrency.Release();
            runLock.Release();
        }
    }

    private async Task ExecuteDownloadAsync(
        DownloadJob job,
        Func<Task> checkpoint,
        CancellationToken ct)
    {
        job.Variant = EnsureDownloadContext(job);
        var backend = _backendRouter.Resolve(job.Variant);
        switch (backend)
        {
            case DownloadBackendKind.DirectHttp:
            {
                var progress = new Progress<long>(bytes =>
                {
                    job.DownloadedBytes = bytes;
                    job.UpdatedAt = DateTimeOffset.UtcNow;
                });
                await _httpDownloader.DownloadDirectAsync(job, progress, checkpoint, ct);
                UpdateCompletedFileSize(job);
                break;
            }
            case DownloadBackendKind.FfmpegMultiInput:
            {
                var (tracks, tempDir) = await DownloadTracksToTempFilesAsync(job, checkpoint, ct);
                var muxed = false;
                try
                {
                    job.Status = DownloadStatus.Muxing;
                    await checkpoint();
                    var staging = Path.Combine(tempDir, "mux" + Path.GetExtension(job.TargetPath));
                    await _ffmpegAdapter.RunMultiInputRemuxAsync(tracks, staging, ct);
                    EnforceFinalDemoLimit(staging);
                    File.Move(staging, job.TargetPath, overwrite: false);
                    UpdateCompletedFileSize(job);
                    muxed = true;
                }
                finally
                {
                    // Keep track files when mux fails/cancels so Resume can skip re-download.
                    if (muxed)
                    {
                        DeleteTempTracks(tracks);
                        TryDeleteDirectory(tempDir);
                    }
                }

                break;
            }
            default:
                await _m3u8.DownloadAsync(job, checkpoint, ct);
                break;
        }
    }

    private static void UpdateCompletedFileSize(DownloadJob job)
    {
        if (!File.Exists(job.TargetPath))
            return;

        var length = new FileInfo(job.TargetPath).Length;
        job.DownloadedBytes = length;
        job.TotalBytes = length;
        job.UpdatedAt = DateTimeOffset.UtcNow;
    }

    internal static string ClassifyError(Exception ex) => ex switch
    {
        DownloadException download => download.ErrorCode,
        UnauthorizedAccessException => ErrorCodes.PermissionDenied,
        InvalidDataException or FormatException or System.Xml.XmlException => ErrorCodes.InvalidFormat,
        IOException io when (io.HResult & 0xffff) is 112 or 39 => ErrorCodes.DiskFull,
        IOException => ErrorCodes.FileIo,
        HttpRequestException or TimeoutException or OperationCanceledException => ErrorCodes.NetTimeout,
        _ => ErrorCodes.Unexpected
    };

    private void EnforceFinalDemoLimit(string path)
    {
        if (_license.DownloadLimitBytes is not int demoLimit || !File.Exists(path) || new FileInfo(path).Length <= demoLimit)
            return;
        File.Delete(path);
        throw new DownloadException(ErrorCodes.LicenseLimit, "DEMO download limit is 10 MiB.");
    }

    private async Task<(IReadOnlyList<MediaTrack> Tracks, string TempDir)> DownloadTracksToTempFilesAsync(
        DownloadJob job,
        Func<Task> checkpoint,
        CancellationToken ct)
    {
        var tracks = job.Variant.Tracks
            .OrderBy(t => t.Kind == MediaTrackKind.Audio ? 1 : 0)
            .ToList();

        var tempDir = Path.Combine(
            Path.GetDirectoryName(job.TargetPath)!,
            ".parts",
            job.Id.ToString("N"));
        Directory.CreateDirectory(tempDir);

        job.TotalBytes = tracks.All(t => t.ContentLength is not null)
            ? tracks.Sum(t => t.ContentLength!.Value)
            : null;
        job.DownloadedBytes = 0;
        await checkpoint();

        var completedBytes = 0L;
        var localTracks = new List<MediaTrack>();

        for (var i = 0; i < tracks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var track = tracks[i];
            var extension = ResolveTrackExtension(track);
            var trackPath = Path.Combine(tempDir, $"{i:00}-{track.Kind.ToString().ToLowerInvariant()}{extension}");
            if (File.Exists(trackPath) && new FileInfo(trackPath).Length > 0)
            {
                var existingLength = new FileInfo(trackPath).Length;
                completedBytes += existingLength;
                job.DownloadedBytes = completedBytes;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await checkpoint();
                localTracks.Add(track with
                {
                    SourceUrl = new Uri(trackPath),
                    ContentLength = existingLength,
                    RequestContext = RequestContext.CreateEmpty()
                });
                continue;
            }

            var trackVariant = MediaVariant.FromCombinedTrack(
                track.TrackId,
                track.SourceUrl,
                track.RequestContext,
                bandwidth: track.Bandwidth,
                codec: track.Codec,
                container: track.Container,
                contentLength: track.ContentLength);

            var trackJob = new DownloadJob
            {
                Id = Guid.NewGuid(),
                DisplayName = $"{job.DisplayName}:{track.TrackId}",
                Variant = trackVariant,
                PageUrl = job.PageUrl,
                TargetPath = trackPath,
                Status = DownloadStatus.Downloading,
                TotalBytes = track.ContentLength,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            var progress = new Progress<long>(bytes =>
            {
                job.DownloadedBytes = completedBytes + bytes;
                job.UpdatedAt = DateTimeOffset.UtcNow;
            });

            await _httpDownloader.DownloadDirectAsync(trackJob, progress, checkpoint, ct);
            completedBytes += new FileInfo(trackPath).Length;
            job.DownloadedBytes = completedBytes;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await checkpoint();

            localTracks.Add(track with
            {
                SourceUrl = new Uri(trackPath),
                ContentLength = new FileInfo(trackPath).Length,
                RequestContext = RequestContext.CreateEmpty()
            });
        }

        return (localTracks, tempDir);
    }

    private static void DeleteTempTracks(IEnumerable<MediaTrack> tracks)
    {
        string? tempDir = null;
        foreach (var track in tracks)
        {
            if (!track.SourceUrl.IsFile)
                continue;

            var path = track.SourceUrl.LocalPath;
            tempDir ??= Path.GetDirectoryName(path);
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                var part = path + ".part";
                if (File.Exists(part))
                    File.Delete(part);
            }
            catch
            {
                // best-effort
            }
        }

        TryDeleteDirectory(tempDir);
    }

    private static void DeleteTempDirectoryIfEmpty(string? tempDir)
    {
        if (!string.IsNullOrWhiteSpace(tempDir) &&
            Directory.Exists(tempDir) &&
            !Directory.EnumerateFileSystemEntries(tempDir).Any())
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static void TryDeleteDirectory(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return;

        try
        {
            Directory.Delete(dir, recursive: true);
            var parent = Path.GetDirectoryName(dir);
            if (!string.IsNullOrWhiteSpace(parent) &&
                string.Equals(Path.GetFileName(parent), ".parts", StringComparison.OrdinalIgnoreCase))
                TryDeleteEmptyPartsRoot(Path.GetDirectoryName(parent) ?? string.Empty);
        }
        catch
        {
            // locked files / concurrent access
        }
    }

    private static Uri? ResolvePageUrl(DownloadJob job)
    {
        if (job.PageUrl is not null)
            return job.PageUrl;

        var referer = job.Variant.RequestContext.Referer;
        if (!string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var uri))
            return uri;

        return null;
    }

    /// <summary>
    /// TikTok/Douyin CDN often rejects bare GETs. Fill Referer/Origin from the page URL
    /// and refresh cookies from the live browser when the captured context is empty.
    /// </summary>
    private MediaVariant EnsureDownloadContext(DownloadJob job)
    {
        var variant = job.Variant;
        var pageUrl = ResolvePageUrl(job);
        var ctx = variant.RequestContext;
        var headers = new Dictionary<string, string>(ctx.Headers, StringComparer.OrdinalIgnoreCase);
        var referer = ctx.Referer;
        var origin = ctx.Origin;
        var userAgent = ctx.UserAgent;
        var cookies = ctx.Cookies;
        var changed = false;

        if (pageUrl is not null)
        {
            if (string.IsNullOrWhiteSpace(referer))
            {
                referer = pageUrl.AbsoluteUri;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(origin))
            {
                origin = pageUrl.GetLeftPart(UriPartial.Authority);
                changed = true;
            }

            if (cookies.Count == 0 || string.IsNullOrWhiteSpace(userAgent))
            {
                var fresh = _contextProvider.CaptureCurrentContext(pageUrl, variant.SourceUrl);
                // T3: never pull live cookies when capture is disabled.
                if (_options.Browser.CaptureCookies &&
                    cookies.Count == 0 && fresh.Cookies.Count > 0)
                {
                    cookies = fresh.Cookies;
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(userAgent) && !string.IsNullOrWhiteSpace(fresh.UserAgent))
                {
                    userAgent = fresh.UserAgent;
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(referer) && !string.IsNullOrWhiteSpace(fresh.Referer))
                {
                    referer = fresh.Referer;
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(origin) && !string.IsNullOrWhiteSpace(fresh.Origin))
                {
                    origin = fresh.Origin;
                    changed = true;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(referer) &&
            !headers.ContainsKey("Referer"))
        {
            headers["Referer"] = referer!;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(origin) &&
            !headers.ContainsKey("Origin"))
        {
            headers["Origin"] = origin!;
            changed = true;
        }

        if (!changed)
            return variant;

        return variant.WithRequestContext(ctx with
        {
            Referer = referer,
            Origin = origin,
            UserAgent = userAgent,
            Headers = headers,
            Cookies = cookies,
            Version = ctx.Version + 1,
            CapturedAt = DateTimeOffset.UtcNow
        });
    }

    private static bool IsCompletedFileConsistent(DownloadJob job)
    {
        if (!File.Exists(job.TargetPath))
            return false;

        if (job.TotalBytes is not > 0)
            return true;

        var fileLength = new FileInfo(job.TargetPath).Length;
        return fileLength == job.TotalBytes.Value && job.DownloadedBytes >= job.TotalBytes.Value;
    }

    private void BeginExecution(DownloadJob job)
    {
        switch (job.Status)
        {
            case DownloadStatus.Pending:
                _stateMachine.StartPreparing(job);
                _stateMachine.StartDownloading(job);
                break;
            case DownloadStatus.Paused:
                _stateMachine.Resume(job);
                break;
            case DownloadStatus.Failed:
                job.LastErrorCode = null;
                _stateMachine.StartPreparing(job);
                _stateMachine.StartDownloading(job);
                break;
            case DownloadStatus.Downloading:
                break;
            default:
                throw new InvalidOperationException($"Job {job.Id} cannot run from status {job.Status}.");
        }
    }

    private static string ResolveDownloadExtension(MediaVariant variant)
    {
        if (variant.Tracks.Count > 0 &&
            variant.Tracks.All(t => t.Kind == MediaTrackKind.Audio))
            return ".m4a";

        return ResolveOutputExtension(variant);
    }

    private static bool IsStreamingContainer(string? container) =>
        string.Equals(container, "hls", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(container, "dash", StringComparison.OrdinalIgnoreCase);

    private static string ResolveOutputExtension(MediaVariant variant) =>
        string.Equals(variant.Container, "webm", StringComparison.OrdinalIgnoreCase)
            ? ".webm"
            : ".mp4";

    private static string ResolveTrackExtension(MediaTrack track) =>
        track.Container?.ToLowerInvariant() switch
        {
            "webm" => ".webm",
            "m4a" => ".m4a",
            "mp4" => ".mp4",
            "m4v" => ".m4v",
            _ => track.Kind == MediaTrackKind.Audio ? ".m4a" : ".mp4"
        };

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "download" : cleaned;
    }

    /// <summary>
    /// Periodically resumes Failed queue items after <see cref="DownloadOptions.FailedRetryIntervalSeconds"/>.
    /// </summary>
    private async Task FailedRetryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            PickupFailedJobs();
        }
    }

    private void PickupFailedJobs()
    {
        var intervalSeconds = _options.Download.FailedRetryIntervalSeconds;
        if (intervalSeconds <= 0)
            return;

        var minAge = TimeSpan.FromSeconds(intervalSeconds);
        var now = DateTimeOffset.UtcNow;
        foreach (var job in _jobs.Values)
        {
            if (job.Status != DownloadStatus.Failed)
                continue;
            if (_removedJobs.ContainsKey(job.Id) || _ctsMap.ContainsKey(job.Id))
                continue;
            if (now - job.UpdatedAt < minAge)
                continue;

            _logger.LogInformation(
                "Auto-picking failed job {JobId} after {Seconds}s (error={Error})",
                job.Id,
                intervalSeconds,
                job.LastErrorCode);
            _ = ResumeAsync(job.Id);
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        try { _failedRetryLoop.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* shutting down */ }
        _lifetime.Dispose();
        _concurrency.Dispose();
        foreach (var cts in _ctsMap.Values)
            cts.Dispose();
    }
}
