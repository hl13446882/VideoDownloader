using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Download;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Naming;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection;
using VideoDownloader.Infrastructure.Detection.Sites.Bilibili;
using VideoDownloader.Infrastructure.Detection.Sites.Douyin;
using VideoDownloader.Infrastructure.Detection.Sites.TikTok;
using VideoDownloader.Infrastructure.Diagnostics;
using VideoDownloader.Infrastructure.Http;
using VideoDownloader.Infrastructure.Licensing;
using VideoDownloader.Infrastructure.LocalLibrary;
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
    private readonly LocalVideoThumbnailStore _thumbs;
    private readonly IReadOnlyList<IExternalSiteResolver> _resolvers;
    private readonly MediaAvailabilityValidator? _availability;
    private readonly IMediaAddressRediscoverer? _rediscoverer;
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
        LocalVideoThumbnailStore thumbs,
        IEnumerable<IExternalSiteResolver>? resolvers = null,
        MediaAvailabilityValidator? availability = null,
        IMediaAddressRediscoverer? rediscoverer = null)
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
        _thumbs = thumbs;
        _resolvers = (resolvers ?? []).ToArray();
        _availability = availability;
        _rediscoverer = rediscoverer;
        _concurrency = new SemaphoreSlim(Math.Clamp(_options.Download.MaxConcurrentDownloads, 1, 5));
        _failedRetryLoop = Task.Run(() => FailedRetryLoopAsync(_lifetime.Token));
    }

    public async Task<Guid> EnqueueAsync(
        MediaVariant variant,
        string displayName,
        Uri? pageUrl = null,
        string? caption = null,
        double? durationSec = null,
        IReadOnlyList<MediaVariant>? siblingVariants = null,
        string? author = null,
        CancellationToken ct = default)
    {
        RejectMseOrdinaryDownload(variant);

        // Persist the full quality ladder so 403 recovery can resume the same object.
        variant = MediaVariantAlternatives.WithLadder(variant, siblingVariants);

        if (_license.DownloadLimitBytes is int demoLimit && variant.TotalContentLength is long total && total > demoLimit)
            throw new DownloadException(ErrorCodes.LicenseLimit, "DEMO download limit is 10 MiB.");
        if (_license.DownloadLimitBytes is not null && variant.TotalContentLength is null &&
            _backendRouter.Resolve(variant) != DownloadBackendKind.DirectHttp)
            throw new DownloadException(ErrorCodes.LicenseLimit, "DEMO cannot start an external download with an unverified size.");

        var rootSaveDir = PathExpander.Expand(_options.Download.DefaultSavePath);
        if (!PathValidator.IsValidSaveDirectory(rootSaveDir))
            throw new DownloadException(ErrorCodes.InvalidSavePath, "Invalid or inaccessible save directory.");

        // Site-domain subfolder from page URL (not CDN). Rename stays in this directory.
        var saveDir = DownloadSiteFolder.CombineSaveDirectory(rootSaveDir, pageUrl);
        Directory.CreateDirectory(saveDir);

        // Title stays within MaxStemLength; duration/resolution/size meta is preserved.
        var stem = DownloadFileNameBuilder.FinalizeEnqueueStem(SanitizeFileName(displayName));
        var extension = ResolveDownloadExtension(variant);
        DownloadJob job;
        lock (_targetPathSync)
        {
            var targetPath = Path.Combine(saveDir, stem + extension);
            var sequence = 2;
            while (File.Exists(targetPath) || _jobs.Values.Any(job =>
                       string.Equals(job.TargetPath, targetPath, StringComparison.OrdinalIgnoreCase)))
            {
                stem = DownloadFileNameBuilder.WithSequenceSuffix(stem, sequence++);
                targetPath = Path.Combine(saveDir, stem + extension);
            }

            job = new DownloadJob
            {
                Id = Guid.NewGuid(),
                DisplayName = stem,
                Caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim(),
                Author = string.IsNullOrWhiteSpace(author) ? null : author.Trim(),
                DurationSec = durationSec is > 0 ? durationSec : null,
                Variant = variant,
                PageUrl = pageUrl,
                TargetPath = targetPath,
                Status = DownloadStatus.Pending,
                ExpectedTotalBytes = variant.TotalContentLength is > 0 ? variant.TotalContentLength : null,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _jobs[job.Id] = job;
        }
        await _repository.SaveAsync(job, ct);
        _ = RunJobAsync(job);
        return job.Id;
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
        DownloadQueueOrder.Sort(
                _jobs.Values.Where(j => j.Status is not (DownloadStatus.Removed or DownloadStatus.Cancelled)))
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
            job.EditedAt = DateTimeOffset.UtcNow;
        }

        await _repository.SaveAsync(job, ct);
        return stem;
    }

    public async Task<DownloadMigrateResult> MigrateCompletedToSaveRootAsync(
        string newRoot,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newRoot))
            return new DownloadMigrateResult(0, 0, 0, "empty-root");

        string root;
        try
        {
            root = Path.GetFullPath(PathExpander.Expand(newRoot.Trim()));
        }
        catch
        {
            return new DownloadMigrateResult(0, 0, 0, "invalid-root");
        }

        if (!PathValidator.IsValidSaveDirectory(root))
            return new DownloadMigrateResult(0, 0, 0, "invalid-root");

        Directory.CreateDirectory(root);

        var inFlight = _jobs.Values.Any(j =>
            j.Status is DownloadStatus.Pending
                or DownloadStatus.Preparing
                or DownloadStatus.Downloading
                or DownloadStatus.Muxing
                or DownloadStatus.Paused);
        if (inFlight)
            return new DownloadMigrateResult(0, 0, 0, "in-flight");

        var moved = 0;
        var skipped = 0;
        var failed = 0;
        var candidates = _jobs.Values
            .Where(j => j.Status == DownloadStatus.Completed &&
                        !string.IsNullOrWhiteSpace(j.TargetPath) &&
                        File.Exists(j.TargetPath))
            .ToList();

        foreach (var job in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var oldPath = Path.GetFullPath(job.TargetPath);
                if (IsPathUnderRoot(oldPath, root))
                {
                    skipped++;
                    continue;
                }

                var fileName = Path.GetFileName(oldPath);
                var extension = Path.GetExtension(fileName);
                var stem = Path.GetFileNameWithoutExtension(fileName);
                if (string.IsNullOrWhiteSpace(stem))
                {
                    failed++;
                    continue;
                }

                var saveDir = DownloadSiteFolder.CombineSaveDirectory(root, job.PageUrl);
                Directory.CreateDirectory(saveDir);

                string targetPath;
                lock (_targetPathSync)
                {
                    var candidateStem = stem;
                    var sequence = 2;
                    while (true)
                    {
                        targetPath = Path.Combine(saveDir, candidateStem + extension);
                        var conflict = File.Exists(targetPath) ||
                                       File.Exists(targetPath + ".part") ||
                                       _jobs.Values.Any(j =>
                                           j.Id != job.Id &&
                                           string.Equals(j.TargetPath, targetPath, StringComparison.OrdinalIgnoreCase));
                        if (!conflict)
                            break;
                        candidateStem = DownloadFileNameBuilder.WithSequenceSuffix(stem, sequence++);
                    }

                    if (string.Equals(oldPath, Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }

                    var oldPartsDir = Path.Combine(
                        Path.GetDirectoryName(oldPath) ?? string.Empty,
                        ".parts",
                        job.Id.ToString("N"));

                    MoveFileAcrossVolumes(oldPath, targetPath);
                    TryDeleteFile(oldPath + ".part");
                    TryDeleteDirectory(oldPartsDir);
                    TryDeleteEmptyPartsRoot(Path.GetDirectoryName(oldPath) ?? string.Empty);

                    job.TargetPath = targetPath;
                    job.DisplayName = Path.GetFileNameWithoutExtension(targetPath);
                    job.EditedAt = DateTimeOffset.UtcNow;
                }

                await _repository.SaveAsync(job, ct);
                moved++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Migrate failed for job {JobId}", job.Id);
                failed++;
            }
        }

        return new DownloadMigrateResult(moved, skipped, failed);
    }

    private static bool IsPathUnderRoot(string fullFilePath, string fullRoot)
    {
        var root = Path.GetFullPath(fullRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var file = Path.GetFullPath(fullFilePath);
        if (string.Equals(file, root, StringComparison.OrdinalIgnoreCase))
            return true;
        var prefix = root + Path.DirectorySeparatorChar;
        return file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void MoveFileAcrossVolumes(string source, string destination)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException("源文件不存在。", source);
        if (File.Exists(destination))
            throw new IOException($"目标已存在：{Path.GetFileName(destination)}");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            File.Move(source, destination);
            return;
        }
        catch (IOException)
        {
            // Cross-volume or locked briefly — copy then delete source.
        }

        File.Copy(source, destination, overwrite: false);
        var srcLen = new FileInfo(source).Length;
        var dstLen = new FileInfo(destination).Length;
        if (srcLen != dstLen)
        {
            TryDeleteFile(destination);
            throw new IOException("复制后文件大小不一致。");
        }

        File.Delete(source);
    }

    public async Task UpdateLibraryItemAsync(
        Guid jobId,
        string? titleHead = null,
        string? caption = null,
        string? videoKind = null,
        CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            throw new InvalidOperationException("任务不存在。");

        if (job.Status != DownloadStatus.Completed)
            throw new InvalidOperationException("仅已完成任务可编辑。");

        if (titleHead is not null)
        {
            var currentStem = Path.GetFileNameWithoutExtension(job.TargetPath);
            if (string.IsNullOrWhiteSpace(currentStem))
                currentStem = job.DisplayName;
            var newStem = DownloadFileNameBuilder.ReplaceTitleHead(currentStem, titleHead);
            await RenameAsync(jobId, newStem, ct);
        }

        var dirty = false;
        if (caption is not null)
        {
            job.Caption = caption;
            dirty = true;
        }

        if (videoKind is not null)
        {
            if (!LibraryVideoKinds.IsKnown(videoKind))
                throw new InvalidOperationException("无效的视频类型。");
            job.VideoKind = LibraryVideoKinds.Normalize(videoKind);
            dirty = true;
        }

        if (dirty)
        {
            job.EditedAt = DateTimeOffset.UtcNow;
            await _repository.SaveAsync(job, ct);
        }
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
        TryDeleteFile(job.TargetPath + ".part");

        var saveDir = Path.GetDirectoryName(job.TargetPath) ?? string.Empty;
        var partsDir = Path.Combine(saveDir, ".parts", job.Id.ToString("N"));
        TryDeleteDirectory(partsDir);
        TryDeleteEmptyPartsRoot(saveDir);

        if (deleteTarget)
            TryDeleteFile(job.TargetPath);
    }

    private static bool JobScratchExists(DownloadJob job)
    {
        if (File.Exists(job.TargetPath + ".part"))
            return true;

        var saveDir = Path.GetDirectoryName(job.TargetPath) ?? string.Empty;
        var partsDir = Path.Combine(saveDir, ".parts", job.Id.ToString("N"));
        return Directory.Exists(partsDir);
    }

    private async Task WipeCancelledScratchAsync(DownloadJob job)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            CleanupJobScratch(job, deleteTarget: false);
            if (!JobScratchExists(job))
                return;

            await Task.Delay(80);
        }

        if (JobScratchExists(job))
            _logger.LogWarning("Temp files still present after cancel job={JobId}", job.Id);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
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
        var maxAutoStart = Math.Clamp(_options.Download.MaxConcurrentDownloads, 1, 5);
        var autoStarted = 0;
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

            if (job.Status == DownloadStatus.Completed && job.CompletedAt is null)
                job.CompletedAt = job.UpdatedAt;

            _jobs[job.Id] = job;
            await _repository.SaveAsync(job, ct);

            if (!_options.Download.AutoRecoverDownloads)
                continue;

            if (recovered == DownloadStatus.Paused)
            {
                // Do NOT Fail(CONTEXT_EXPIRED) here. Exceeding concurrency (or looking
                // "stale") only means defer auto-start — the user can still Resume, and
                // PumpAutoRecoverQueue will start more when a slot frees.
                if (IsStaleForAutoRecover(job))
                {
                    _logger.LogInformation(
                        "Deferred auto-recover for job {JobId} (stale signature/age; kept Paused)",
                        job.Id);
                    continue;
                }

                if (autoStarted >= maxAutoStart)
                {
                    _logger.LogInformation(
                        "Deferred auto-recover for job {JobId} (concurrency budget {Budget})",
                        job.Id,
                        maxAutoStart);
                    continue;
                }

                autoStarted++;
                _ = RunJobAsync(job);
            }
            else if (recovered == DownloadStatus.Failed && ShouldRetryBilibiliFailedOnStartup(job))
            {
                if (autoStarted >= maxAutoStart)
                    continue;

                job.LastErrorCode = null;
                autoStarted++;
                _ = RunJobAsync(job);
            }
        }

        CleanupOrphanedScratch(_jobs.Values);

        _logger.LogInformation("Recovered {Count} download jobs from persistence.", persisted.Count);
        _ = BackfillCompletedFileNamesAsync(_lifetime.Token);
        _ = BackfillMissingThumbnailsAsync(_lifetime.Token);
    }

    private async Task BackfillCompletedFileNamesAsync(CancellationToken ct)
    {
        try
        {
            var jobs = _jobs.Values
                .Where(j => j.Status == DownloadStatus.Completed && File.Exists(j.TargetPath))
                .ToArray();
            foreach (var job in jobs)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await ApplyCompletedFileNameAsync(job, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Completed-name backfill failed for {JobId}", job.Id);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Completed-name backfill stopped");
        }
    }

    private Task BackfillMissingThumbnailsAsync(CancellationToken ct)
    {
        foreach (var job in _jobs.Values)
        {
            if (ct.IsCancellationRequested)
                break;
            if (job.Status != DownloadStatus.Completed)
                continue;
            _thumbs.EnsureAsyncFireAndForget(job.Id, job.TargetPath);
        }

        return Task.CompletedTask;
    }

    private async Task ApplyCompletedFileNameAsync(DownloadJob job, CancellationToken ct)
    {
        if (job.Status != DownloadStatus.Completed || !File.Exists(job.TargetPath))
            return;

        UpdateCompletedFileSize(job);
        var fileStem = Path.GetFileNameWithoutExtension(job.TargetPath);
        var source = string.IsNullOrWhiteSpace(fileStem) ? job.DisplayName : fileStem;

        DownloadFileNameBuilder.TryParseMetaParts(source, out _, out var existing);
        double? durationSec = existing.Minutes is > 0
            ? existing.Minutes.Value * 60.0
            : job.DurationSec is > 0 ? job.DurationSec : null;
        int? height = existing.Height ?? (job.Variant.Height is > 0 ? job.Variant.Height : null);
        var bytes = existing.Bytes ?? job.ExpectedTotalBytes ?? job.TotalBytes ?? new FileInfo(job.TargetPath).Length;

        if (durationSec is null || height is null)
        {
            try
            {
                var probed = await _ffmpegAdapter.ProbeLocalFileAsync(job.TargetPath, ct);
                durationSec ??= probed.DurationSec is > 0 ? probed.DurationSec : null;
                height ??= probed.Height is > 0 ? probed.Height : null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Completed-name probe failed for {JobId}", job.Id);
            }
        }

        if (job.DurationSec is not > 0 && durationSec is > 0)
            job.DurationSec = durationSec;

        var merged = DownloadFileNameBuilder.MergeMissingMeta(source, durationSec, height, bytes);
        if (string.Equals(merged, job.DisplayName, StringComparison.Ordinal) &&
            string.Equals(merged, fileStem, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            await RenameAsync(job.Id, merged, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not apply completed filename meta for {JobId}", job.Id);
        }
    }

    internal static bool IsStaleForAutoRecover(DownloadJob job)
    {
        // Prefer deferring auto-start over failing. Bilibili / YouTube always attempt resume.
        if (BilibiliCdnPreference.IsMediaHost(job.Variant.SourceUrl) ||
            MediaAddressRenewal.IsYouTubePlayback(job.Variant.SourceUrl))
            return false;

        if (job.UpdatedAt < DateTimeOffset.UtcNow - TimeSpan.FromMinutes(30))
            return true;

        var query = job.Variant.SourceUrl.Query;
        if (string.IsNullOrEmpty(query))
            return false;

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            if (!part.AsSpan(0, eq).Equals("expire", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!long.TryParse(part.AsSpan(eq + 1), out var epoch))
                continue;
            var expireAt = DateTimeOffset.FromUnixTimeSeconds(epoch);
            if (expireAt < DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1))
                return true;
        }

        return false;
    }

    private static bool ShouldRetryBilibiliFailedOnStartup(DownloadJob job) =>
        (BilibiliCdnPreference.IsMediaHost(job.Variant.SourceUrl) ||
         MediaAddressRenewal.IsYouTubePlayback(job.Variant.SourceUrl)) &&
        string.Equals(job.LastErrorCode, ErrorCodes.ContextExpired, StringComparison.OrdinalIgnoreCase);

    private async Task RunJobAsync(DownloadJob job)
    {
        var runLock = _runLocks.GetOrAdd(job.Id, _ => new SemaphoreSlim(1));
        await runLock.WaitAsync(_lifetime.Token);

        // A slot-pump may schedule the same Paused job twice; skip if already active/done.
        if (job.Status is DownloadStatus.Completed or DownloadStatus.Cancelled or DownloadStatus.Removed ||
            job.Status is DownloadStatus.Downloading or DownloadStatus.Muxing or DownloadStatus.Preparing ||
            _ctsMap.ContainsKey(job.Id))
        {
            runLock.Release();
            return;
        }

        await _concurrency.WaitAsync(_lifetime.Token);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ctsMap[job.Id] = cts;

        try
        {
            if (_removedJobs.ContainsKey(job.Id))
                return;

            if (job.Status is DownloadStatus.Completed or DownloadStatus.Cancelled or DownloadStatus.Removed)
                return;

            BeginExecution(job);
            await _repository.SaveAsync(job, cts.Token);

            Func<Task> checkpoint = () => _repository.SaveAsync(job, CancellationToken.None);
            var cookieRetried = false;
            var gatewayRenewed = false;
            var addressRenewed = false;
            var alternatesTried = false;
            var browserRediscovered = false;

            while (true)
            {
                try
                {
                    await ExecuteDownloadAsync(job, checkpoint, cts.Token);
                    break;
                }
                catch (DownloadException ex) when (
                    ex.ErrorCode == ErrorCodes.Http403 &&
                    !(cookieRetried && gatewayRenewed && addressRenewed && browserRediscovered) &&
                    !cts.IsCancellationRequested)
                {
                    _logger.LogWarning("Job {JobId} media HTTP_403; starting same-content recovery", job.Id);
                    HangProbe.Mark(
                        "download.http403",
                        $"job={job.Id:N} cookies={job.Variant.RequestContext.Cookies.Count} host={job.Variant.SourceUrl.Host} id={job.Variant.ContentIdentity} recovery={job.Variant.RecoveryPageUrl}");
                    var pageUrl = job.Variant.RecoveryPageUrl ?? ResolvePageUrl(job)
                        ?? throw new DownloadException(ErrorCodes.ContextExpired, "Original page address is unavailable.");
                    if (!MediaAddressRenewal.HasStableContentAddress(pageUrl))
                        pageUrl = MediaAddressRenewal.RecoveryAddress(pageUrl, job.Variant.ContentIdentity) ?? pageUrl;

                    var browserObserved = job.Variant.Tracks.Any(t => t.BrowserObserved);
                    var fragileSigned = MediaAddressRenewal.IsFragileSignedHost(job.Variant.SourceUrl);
                    var tiktokFragile = TikTokCdn.IsFragilePlayHost(job.Variant.SourceUrl);
                    var refreshed = await _contextProvider.RefreshContextAsync(
                        pageUrl,
                        job.Variant.SourceUrl,
                        job.Variant.RequestContext,
                        cts.Token,
                        forceCookies: browserObserved || !cookieRetried || tiktokFragile);

                    // Rejected address: try sibling formats / other hosts before retrying the same URL.
                    if (!alternatesTried && job.Variant.Alternatives.Count > 0)
                    {
                        alternatesTried = true;
                        var switched = await MediaAddressRenewal.TryAlternativesAsync(
                            job.Variant,
                            _availability is null ? null : _availability.ValidateAsync,
                            cts.Token,
                            expectedTotalBytes: job.ExpectedTotalBytes ?? job.TotalBytes);
                        if (switched is not null)
                        {
                            ApplyRenewedVariant(job, switched.WithRequestContext(refreshed));
                            await checkpoint();
                            _logger.LogInformation(
                                "Switched to alternate media address for job {JobId} host={Host}",
                                job.Id, job.Variant.SourceUrl.Host);
                            continue;
                        }
                    }

                    // Prefer cookie retry once for WebView-sourced URLs — skip fragile Douyin
                    // signed CDNs that already 403'd (web-prime). TikTok webapp-prime signatures
                    // are bound to the browser play session: retrying the same URL never helps.
                    if (!cookieRetried &&
                        TikTokCdn.IsSignedProgressiveHost(job.Variant.SourceUrl) &&
                        !tiktokFragile)
                    {
                        cookieRetried = true;
                        job.Variant = job.Variant.WithRequestContext(refreshed);
                        _logger.LogInformation(
                            "Retrying TikTok signed CDN for job {JobId} with refreshed cookies version={Version}",
                            job.Id,
                            refreshed.Version);
                        continue;
                    }

                    if (!cookieRetried && tiktokFragile)
                    {
                        cookieRetried = true;
                        _logger.LogInformation(
                            "Skipping same-URL cookie retry for TikTok webapp-prime job {JobId}; renewing address",
                            job.Id);
                    }

                    if (browserObserved && !cookieRetried && !fragileSigned)
                    {
                        cookieRetried = true;
                        job.Variant = job.Variant.WithRequestContext(refreshed);
                        _logger.LogInformation(
                            "Retrying browser-observed URL for job {JobId} with refreshed cookies version={Version}",
                            job.Id,
                            refreshed.Version);
                        continue;
                    }

                    cookieRetried = true;

                    // Douyin signed CDN: follow /aweme/v1/play gateway to a fresh progressive object.
                    if (!gatewayRenewed &&
                        _availability is not null &&
                        TryBuildDouyinPlayGateway(job.Variant, pageUrl, out var gateway))
                    {
                        gatewayRenewed = true;
                        try
                        {
                            var seed = job.Variant.Tracks.First(t =>
                                t.Kind is MediaTrackKind.Combined or MediaTrackKind.Video);
                            var probe = seed with
                            {
                                RequestContext = refreshed,
                                BrowserObserved = false,
                                IsValidated = false
                            };
                            var finalUrl = await _availability.ResolveFinalUrlAsync(probe, gateway, cts.Token);
                            // HTTP 200 on the gateway is not enough — ResolveFinalUrlAsync already
                            // samples bytes; still reject landing back on the same fragile CDN family.
                            if (fragileSigned && MediaAddressRenewal.IsFragileSignedHost(finalUrl))
                            {
                                _logger.LogWarning(
                                    "Job {JobId} Douyin gateway renew landed on fragile host={Host}; treating as failure",
                                    job.Id, finalUrl.Host);
                            }
                            else if (!string.Equals(finalUrl.AbsoluteUri, job.Variant.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                            {
                                job.Variant = ReplaceVariantSource(job.Variant, finalUrl, refreshed);
                                ResetTransferState(job);
                                await checkpoint();
                                _logger.LogInformation(
                                    "Renewed Douyin play gateway for job {JobId} host={Host}",
                                    job.Id, finalUrl.Host);
                                continue;
                            }
                        }
                        catch (Exception gatewayError)
                        {
                            _logger.LogWarning(
                                "Job {JobId} Douyin gateway renew failed: {Reason}",
                                job.Id,
                                VideoDownloader.Infrastructure.Logging.SanitizedLogger.SanitizeMessage(gatewayError.Message));
                        }
                    }

                    // Douyin has no yt-dlp renewer: reopen the detail page in WebView and re-detect.
                    // Do not run this on TikTok — webapp-prime is also "fragile signed" but the
                    // rediscoverer is Douyin-only and restarts the exclusive detector.
                    if (!browserRediscovered &&
                        _rediscoverer is not null &&
                        IsDouyinRecoveryPage(pageUrl))
                    {
                        browserRediscovered = true;
                        try
                        {
                            var rediscovered = await _rediscoverer.RediscoverAsync(
                                pageUrl, job.Variant, cts.Token);
                            if (rediscovered is not null &&
                                !string.Equals(
                                    rediscovered.SourceUrl.AbsoluteUri,
                                    job.Variant.SourceUrl.AbsoluteUri,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                job.Variant = rediscovered.WithRequestContext(
                                    rediscovered.RequestContext.Cookies.Count > 0
                                        ? rediscovered.RequestContext
                                        : refreshed);
                                ResetTransferState(job);
                                await checkpoint();
                                _logger.LogInformation(
                                    "Douyin browser rediscover switched job {JobId} host={Host}",
                                    job.Id, job.Variant.SourceUrl.Host);
                                continue;
                            }
                        }
                        catch (Exception rediscoverError)
                        {
                            _logger.LogWarning(
                                "Job {JobId} Douyin browser rediscover failed: {Reason}",
                                job.Id,
                                VideoDownloader.Infrastructure.Logging.SanitizedLogger.SanitizeMessage(rediscoverError.Message));
                        }
                    }

                    if (addressRenewed)
                        throw;

                    addressRenewed = true;
                    try
                    {
                        if (job.ExpectedTotalBytes is not > 0 && job.TotalBytes is > 0)
                            job.ExpectedTotalBytes = job.TotalBytes;
                        var renewed = await MediaAddressRenewal.ResolveAsync(
                            pageUrl,
                            job.Variant,
                            refreshed,
                            _resolvers,
                            cts.Token,
                            _availability is null ? null : _availability.ValidateAsync,
                            job.ExpectedTotalBytes ?? job.TotalBytes);
                        ApplyRenewedVariant(job, renewed);
                    }
                    catch (Exception recoveryError)
                    {
                        _logger.LogWarning("Job {JobId} original=HTTP_403 recovery={Reason}", job.Id,
                            VideoDownloader.Infrastructure.Logging.SanitizedLogger.SanitizeMessage(recoveryError.Message));
                        throw;
                    }
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
            await ApplyCompletedFileNameAsync(job, CancellationToken.None);
            await _repository.SaveAsync(job, CancellationToken.None);
            // Poster once on complete; library reuses the cached jpg thereafter.
            _thumbs.EnsureAsyncFireAndForget(job.Id, job.TargetPath);
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
            _logger.LogWarning("Download failed {JobId}: {Error} detail={Detail}", job.Id, ex.ErrorCode,
                VideoDownloader.Infrastructure.Logging.SanitizedLogger.SanitizeMessage(ex.Message));
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
            try { cts.Dispose(); } catch { /* ignore */ }
            _concurrency.Release();
            runLock.Release();

            // CancelAsync may run while ffmpeg/HTTP still hold files; wipe after handles close.
            if (_removedJobs.ContainsKey(job.Id) || job.Status == DownloadStatus.Cancelled)
                await WipeCancelledScratchAsync(job);

            PumpAutoRecoverQueue();
        }
    }

    /// <summary>
    /// After a concurrency slot frees, start another deferred Paused job (if auto-recover is on).
    /// </summary>
    private void PumpAutoRecoverQueue()
    {
        if (!_options.Download.AutoRecoverDownloads || _lifetime.IsCancellationRequested)
            return;

        var available = _concurrency.CurrentCount;
        if (available <= 0)
            return;

        var candidates = _jobs.Values
            .Where(j =>
                j.Status == DownloadStatus.Paused &&
                !_ctsMap.ContainsKey(j.Id) &&
                !_removedJobs.ContainsKey(j.Id) &&
                !IsStaleForAutoRecover(j))
            .OrderByDescending(j => j.UpdatedAt)
            .Take(available)
            .ToArray();

        foreach (var next in candidates)
            _ = RunJobAsync(next);
    }

    private async Task ExecuteDownloadAsync(
        DownloadJob job,
        Func<Task> checkpoint,
        CancellationToken ct)
    {
        job.Variant = EnsureDownloadContext(job);
        RejectMseOrdinaryDownload(job.Variant);

        if ((job.Variant.Tracks.Any(t => t.BrowserObserved) ||
             TikTokCdn.IsSignedProgressiveHost(job.Variant.SourceUrl)) &&
            job.Variant.RequestContext.Cookies.Count == 0)
        {
            var pageUrl = ResolvePageUrl(job);
            if (pageUrl is not null)
            {
                var refreshed = await _contextProvider.RefreshContextAsync(
                    pageUrl,
                    job.Variant.SourceUrl,
                    job.Variant.RequestContext,
                    ct,
                    forceCookies: true);
                if (refreshed.Cookies.Count > 0)
                {
                    job.Variant = job.Variant.WithRequestContext(refreshed);
                    _logger.LogInformation(
                        "Attached live cookies for browser-observed download job={JobId} count={Count}",
                        job.Id,
                        refreshed.Cookies.Count);
                }
            }
        }

        var backend = _backendRouter.Resolve(job.Variant);
        switch (backend)
        {
            case DownloadBackendKind.DirectHttp:
            {
                // The downloader owns this job's byte count; asynchronous progress must not write it back.
                await _httpDownloader.DownloadDirectAsync(job, null, checkpoint, ct);
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
            case DownloadBackendKind.FfmpegAlbumSlideshow:
            {
                var (tracks, tempDir) = await DownloadTracksToTempFilesAsync(job, checkpoint, ct);
                var muxed = false;
                try
                {
                    job.Status = DownloadStatus.Muxing;
                    await checkpoint();
                    var images = tracks
                        .Where(t => t.Kind == MediaTrackKind.Image)
                        .Select(LocalPathOf)
                        .Where(p => File.Exists(p) && new FileInfo(p).Length >= 1024)
                        .ToArray();
                    var audio = tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Audio);
                    var expectedImages = job.Variant.Tracks.Count(t => t.Kind == MediaTrackKind.Image);
                    if (images.Length == 0 || audio is null)
                        throw new DownloadException(ErrorCodes.FfmpegFailed, "Album download requires images and audio.");
                    if (expectedImages > 0 && images.Length < expectedImages)
                        throw new DownloadException(
                            ErrorCodes.IncompleteDownload,
                            $"Album incomplete: downloaded {images.Length} of {expectedImages} images.");
                    var staging = Path.Combine(tempDir, "album" + Path.GetExtension(job.TargetPath));
                    await _ffmpegAdapter.RunAlbumSlideshowAsync(images, LocalPathOf(audio), staging, ct);
                    EnforceFinalDemoLimit(staging);
                    File.Move(staging, job.TargetPath, overwrite: false);
                    UpdateCompletedFileSize(job);
                    muxed = true;
                }
                finally
                {
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

    private static string LocalPathOf(MediaTrack track) =>
        track.SourceUrl.IsFile ? track.SourceUrl.LocalPath : track.SourceUrl.AbsolutePath;

    private static void UpdateCompletedFileSize(DownloadJob job)
    {
        if (!File.Exists(job.TargetPath))
            return;

        var length = new FileInfo(job.TargetPath).Length;
        job.DownloadedBytes = length;
        job.TotalBytes = length;
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

        var localTracks = new MediaTrack?[tracks.Count];
        var trackBytes = new long[tracks.Count];

        // Resume: count already-finished track files toward progress.
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var extension = ResolveTrackExtension(track);
            var trackPath = Path.Combine(tempDir, $"{i:00}-{track.Kind.ToString().ToLowerInvariant()}{extension}");
            if (File.Exists(trackPath) && new FileInfo(trackPath).Length > 0)
            {
                var existingLength = new FileInfo(trackPath).Length;
                trackBytes[i] = existingLength;
                localTracks[i] = track with
                {
                    SourceUrl = new Uri(trackPath),
                    ContentLength = existingLength,
                    RequestContext = RequestContext.CreateEmpty()
                };
            }
        }

        var progressGate = new object();
        void PublishProgress()
        {
            lock (progressGate)
            {
                long sum = 0;
                for (var i = 0; i < trackBytes.Length; i++)
                    sum += Volatile.Read(ref trackBytes[i]);
                job.DownloadedBytes = sum;
            }
        }

        PublishProgress();
        await checkpoint();

        var parallelAv = _options.Download.ParallelAudioVideoTracks &&
                         tracks.Count(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Audio or MediaTrackKind.Combined) >= 2 &&
                         tracks.All(t => t.Kind is not MediaTrackKind.Image);

        if (parallelAv)
        {
            var checkpointGate = new SemaphoreSlim(1, 1);
            async Task SafeCheckpoint()
            {
                await checkpointGate.WaitAsync(ct);
                try
                {
                    await checkpoint();
                }
                finally
                {
                    checkpointGate.Release();
                }
            }

            var tasks = new Task[tracks.Count];
            for (var i = 0; i < tracks.Count; i++)
            {
                var index = i;
                tasks[i] = DownloadOneTrackToTempAsync(
                    job,
                    tracks[index],
                    index,
                    tempDir,
                    localTracks,
                    trackBytes,
                    PublishProgress,
                    SafeCheckpoint,
                    ct);
            }

            await Task.WhenAll(tasks);
        }
        else
        {
            for (var i = 0; i < tracks.Count; i++)
            {
                await DownloadOneTrackToTempAsync(
                    job,
                    tracks[i],
                    i,
                    tempDir,
                    localTracks,
                    trackBytes,
                    PublishProgress,
                    checkpoint,
                    ct);
            }
        }

        PublishProgress();
        await checkpoint();
        if (localTracks.Any(t => t is null))
            throw new DownloadException(ErrorCodes.IncompleteDownload, "One or more media tracks failed to download.");
        return (localTracks!, tempDir);
    }

    private async Task DownloadOneTrackToTempAsync(
        DownloadJob job,
        MediaTrack track,
        int index,
        string tempDir,
        MediaTrack?[] localTracks,
        long[] trackBytes,
        Action publishProgress,
        Func<Task> checkpoint,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (localTracks[index] is not null)
            return;

        var extension = ResolveTrackExtension(track);
        var trackPath = Path.Combine(tempDir, $"{index:00}-{track.Kind.ToString().ToLowerInvariant()}{extension}");

        var trackVariant = MediaVariant.FromTracks(
            track.TrackId,
            width: null,
            height: null,
            bandwidth: track.Bandwidth,
            container: track.Container,
            tracks: [track]);

        var trackJob = new DownloadJob
        {
            Id = Guid.NewGuid(),
            DisplayName = $"{job.DisplayName}:{track.TrackId}",
            Variant = trackVariant,
            PageUrl = job.PageUrl,
            TargetPath = trackPath,
            Status = DownloadStatus.Downloading,
            // Album image/BGM CDNs lie about Content-Length; let the downloader learn size from the body.
            TotalBytes = track.Kind is MediaTrackKind.Image or MediaTrackKind.Audio
                ? null
                : track.ContentLength,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        // Audio for YouTube is also googlevideo — keep ContentLength so parallel Range can run.
        if (HttpMediaDownloader.IsParallelRangeHost(track.SourceUrl) && track.ContentLength is > 0)
            trackJob.TotalBytes = track.ContentLength;

        var progress = new InlineProgress(bytes =>
        {
            Volatile.Write(ref trackBytes[index], bytes);
            publishProgress();
        });

        await _httpDownloader.DownloadDirectAsync(trackJob, progress, checkpoint, ct);
        var length = new FileInfo(trackPath).Length;
        Volatile.Write(ref trackBytes[index], length);
        publishProgress();

        localTracks[index] = track with
        {
            SourceUrl = new Uri(trackPath),
            ContentLength = length,
            RequestContext = RequestContext.CreateEmpty()
        };
    }

    private sealed class InlineProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
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
        }
        catch
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    TryDeleteFile(file);
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // locked files / concurrent access
            }
        }

        try
        {
            var parent = Path.GetDirectoryName(dir);
            if (!string.IsNullOrWhiteSpace(parent) &&
                string.Equals(Path.GetFileName(parent), ".parts", StringComparison.OrdinalIgnoreCase))
                TryDeleteEmptyPartsRoot(Path.GetDirectoryName(parent) ?? string.Empty);
        }
        catch
        {
            // ignore
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

    private static bool IsDouyinRecoveryPage(Uri pageUrl) =>
        pageUrl.Host.Contains("douyin.com", StringComparison.OrdinalIgnoreCase) ||
        pageUrl.Host.Contains("iesdouyin.com", StringComparison.OrdinalIgnoreCase);

    private static bool TryBuildDouyinPlayGateway(MediaVariant variant, Uri pageUrl, out Uri gateway)
    {
        gateway = null!;
        var host = pageUrl.Host;
        if (!(host.Equals("douyin.com", StringComparison.OrdinalIgnoreCase) ||
              host.EndsWith(".douyin.com", StringComparison.OrdinalIgnoreCase) ||
              host.Equals("iesdouyin.com", StringComparison.OrdinalIgnoreCase) ||
              host.EndsWith(".iesdouyin.com", StringComparison.OrdinalIgnoreCase)))
            return false;

        string? id = null;
        if (variant.ContentIdentity is { Length: > 3 } identity &&
            identity.StartsWith("id:", StringComparison.Ordinal))
            id = identity[3..];
        id ??= DouyinIdentity.ExtractIdFromQuery(variant.SourceUrl) ??
               DouyinIdentity.ExtractAwemeId(pageUrl);
        if (string.IsNullOrWhiteSpace(id) || !id.All(char.IsDigit))
            return false;

        gateway = new Uri(
            $"https://www.douyin.com/aweme/v1/play/?video_id={Uri.EscapeDataString(id)}&ratio=1080p&line=0");
        return true;
    }

    private static MediaVariant ReplaceVariantSource(MediaVariant variant, Uri source, RequestContext context) =>
        variant with
        {
            Tracks = variant.Tracks
                .Select(t => t.Kind is MediaTrackKind.Combined or MediaTrackKind.Video
                    ? t with
                    {
                        SourceUrl = source,
                        RequestContext = context,
                        ContentLength = null,
                        BrowserObserved = false,
                        IsValidated = false,
                        Evidence = MediaEvidence.Heuristic
                    }
                    : t with { RequestContext = context })
                .ToArray()
        };

    private void ApplyRenewedVariant(DownloadJob job, MediaVariant renewed)
    {
        var previous = job.Variant;
        var keep = ResumeObjectTrust.ShouldKeepPartialProgress(
            previous,
            renewed,
            job.ExpectedTotalBytes ?? job.TotalBytes,
            storedETag: job.ETag,
            renewedETag: null,
            storedPrefixHash: _options.Download.VerifyPrefixHash ? job.ContentPrefixHash : null,
            renewedPrefixHash: null);

        job.Variant = renewed;
        if (keep)
        {
            job.LastErrorCode = null;
            if (job.ExpectedTotalBytes is not > 0 && renewed.TotalContentLength is > 0)
                job.ExpectedTotalBytes = renewed.TotalContentLength;
            // CDN rotates often change weak validators; size+identity already decided Keep.
            if (!string.Equals(previous.SourceUrl.Host, renewed.SourceUrl.Host, StringComparison.OrdinalIgnoreCase))
            {
                job.ETag = null;
                job.LastModified = null;
            }

            _logger.LogInformation(
                "Keeping partial progress for {JobId} after URL renew {OldHost} -> {NewHost} (expectedTotal={Expected})",
                job.Id,
                previous.SourceUrl.Host,
                renewed.SourceUrl.Host,
                job.ExpectedTotalBytes);
            return;
        }

        ResetTransferState(job);
    }

    private void ResetTransferState(DownloadJob job)
    {
        var scratch = Path.Combine(Path.GetDirectoryName(job.TargetPath)!, ".parts", job.Id.ToString("N"));
        if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        if (File.Exists(job.TargetPath + ".part")) File.Delete(job.TargetPath + ".part");
        job.DownloadedBytes = 0;
        job.TotalBytes = job.Variant.TotalContentLength;
        job.ExpectedTotalBytes = job.Variant.TotalContentLength is > 0 ? job.Variant.TotalContentLength : null;
        job.ContentPrefixHash = null;
        job.ETag = null;
        job.LastModified = null;
        job.LastErrorCode = null;
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
                // Browser-observed downloads may carry cookies from a one-shot jar read
                // even when CaptureCookies is off for general browsing.
                var browserObserved = variant.Tracks.Any(t => t.BrowserObserved);
                var tiktokSigned = TikTokCdn.IsSignedProgressiveHost(variant.SourceUrl);
                if (cookies.Count == 0 && fresh.Cookies.Count > 0 &&
                    (_options.Browser.CaptureCookies || browserObserved || tiktokSigned))
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
        track.Kind == MediaTrackKind.Image
            ? Path.GetExtension(track.SourceUrl.AbsolutePath).ToLowerInvariant() switch
            {
                ".png" => ".png",
                ".webp" => ".webp",
                ".gif" => ".gif",
                ".bmp" => ".bmp",
                _ => ".jpg"
            }
            : track.Container?.ToLowerInvariant() switch
            {
                "webm" => ".webm",
                "m4a" => ".m4a",
                "mp4" => ".mp4",
                "m4v" => ".m4v",
                "image" => ".jpg",
                _ => track.Kind == MediaTrackKind.Audio ? ".m4a" : ".mp4"
            };

    /// <summary>
    /// Last-line defense: MSE adaptive tracks must never enter ordinary HTTP/ffmpeg remux.
    /// </summary>
    private static void RejectMseOrdinaryDownload(MediaVariant variant)
    {
        if (variant.Tracks.Any(t => t.IsMseTrack || MediaUrlNormalizer.IsByteDanceMseTrack(t.SourceUrl)))
        {
            throw new DownloadException(
                ErrorCodes.MseTrackNotDownloadable,
                "MSE adaptive media-video/media-audio tracks are not ordinary download sources.");
        }
    }

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
            // CONTEXT_EXPIRED means the signed URL/session is dead; replaying the same job
            // cannot recover without a fresh probe. Skip auto-retry.
            if (string.Equals(job.LastErrorCode, ErrorCodes.ContextExpired, StringComparison.OrdinalIgnoreCase))
                continue;
            // Timed-out / incomplete transfers usually need a fresh probe, not a 10s replay.
            if (string.Equals(job.LastErrorCode, ErrorCodes.NetTimeout, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(job.LastErrorCode, ErrorCodes.IncompleteDownload, StringComparison.OrdinalIgnoreCase))
                continue;
            if (now - job.UpdatedAt > TimeSpan.FromMinutes(30))
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
        foreach (var cts in _ctsMap.Values)
        {
            try { cts.Cancel(); }
            catch { /* shutting down */ }
        }

        try { _failedRetryLoop.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* shutting down */ }

        _lifetime.Dispose();
        _concurrency.Dispose();
        foreach (var cts in _ctsMap.Values)
            cts.Dispose();
        _ctsMap.Clear();
    }
}
