using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Naming;
using VideoDownloader.Infrastructure.Configuration;

namespace VideoDownloader.Infrastructure.LocalLibrary;

public sealed class LocalFolderImportProgress
{
    public int Total { get; init; }
    public int Completed { get; init; }
    public int Failed { get; init; }
    public string? CurrentFileName { get; init; }
}

public sealed class LocalFolderImportResult
{
    public int Imported { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public string FolderName { get; init; } = "导入";
}

/// <summary>
/// Copies media from an arbitrary folder into the download root and registers Completed library jobs.
/// </summary>
public sealed class LocalFolderImportService
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v", ".flv", ".wmv", ".ts", ".m2ts",
        ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus", ".wma"
    };

    private readonly AppOptions _options;
    private readonly IDownloadEngine _engine;
    private readonly IFfmpegAdapter _ffmpeg;
    private readonly ILogger<LocalFolderImportService> _logger;

    public LocalFolderImportService(
        AppOptions options,
        IDownloadEngine engine,
        IFfmpegAdapter ffmpeg,
        ILogger<LocalFolderImportService> logger)
    {
        _options = options;
        _engine = engine;
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    public async Task<LocalFolderImportResult> ImportAsync(
        string sourceDirectory,
        string folderName,
        IProgress<LocalFolderImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException(sourceDirectory);

        var safeFolder = DownloadSiteFolder.SanitizeImportFolderName(folderName);
        var pageUrl = DownloadSiteFolder.CreateImportPageUrl(safeFolder);
        var rootSaveDir = PathExpander.Expand(_options.Download.DefaultSavePath);
        var saveDir = DownloadSiteFolder.CombineSaveDirectory(rootSaveDir, pageUrl);
        Directory.CreateDirectory(saveDir);

        var files = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Where(IsMediaFile)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var imported = 0;
        var failed = 0;
        var skipped = 0;
        var total = files.Length;

        for (var i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = files[i];
            var fileName = Path.GetFileName(sourcePath);
            progress?.Report(new LocalFolderImportProgress
            {
                Total = total,
                Completed = imported + failed + skipped,
                Failed = failed,
                CurrentFileName = fileName
            });

            try
            {
                var info = new FileInfo(sourcePath);
                if (!info.Exists || info.Length <= 0)
                {
                    skipped++;
                    continue;
                }

                // Validate playability on the source before copying into the library.
                var probed = await _ffmpeg.ProbeLocalMediaAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                if (!IsPlayable(probed))
                {
                    skipped++;
                    _logger.LogInformation("Skipped unplayable import candidate {Path}", sourcePath);
                    continue;
                }

                var destPath = AllocateDestinationPath(saveDir, fileName);
                await Task.Run(() => File.Copy(sourcePath, destPath, overwrite: false), cancellationToken)
                    .ConfigureAwait(false);

                string? copiedPath = destPath;
                try
                {
                    var destInfo = new FileInfo(destPath);
                    var fileUri = new Uri(destPath);
                    var container = Path.GetExtension(destPath).TrimStart('.').ToLowerInvariant();
                    var trackKind = probed.HasVideo
                        ? MediaTrackKind.Combined
                        : MediaTrackKind.Audio;
                    var variant = MediaVariant.FromTracks(
                        "import-" + Guid.NewGuid().ToString("N"),
                        null,
                        probed.Height is > 0 ? probed.Height : null,
                        null,
                        string.IsNullOrWhiteSpace(container) ? null : container,
                        [
                            new MediaTrack(
                                "primary",
                                trackKind,
                                fileUri,
                                null,
                                string.IsNullOrWhiteSpace(container) ? null : container,
                                null,
                                destInfo.Length,
                                RequestContext.CreateEmpty())
                            {
                                IsValidated = true
                            }
                        ]);

                    var stem = Path.GetFileNameWithoutExtension(destPath);
                    if (string.IsNullOrWhiteSpace(stem))
                        stem = "media";

                    var now = DateTimeOffset.UtcNow;
                    var job = new DownloadJob
                    {
                        Id = Guid.NewGuid(),
                        DisplayName = stem,
                        Caption = fileName,
                        Author = string.IsNullOrWhiteSpace(probed.Author) ? null : probed.Author.Trim(),
                        VideoKind = probed.HasVideo ? LibraryVideoKinds.Unspecified : "song",
                        DurationSec = probed.DurationSec is > 0 ? probed.DurationSec : null,
                        Variant = variant,
                        PageUrl = pageUrl,
                        TargetPath = destPath,
                        Status = DownloadStatus.Completed,
                        DownloadedBytes = destInfo.Length,
                        TotalBytes = destInfo.Length,
                        ExpectedTotalBytes = destInfo.Length,
                        CompletedAt = now,
                        CreatedAt = now,
                        UpdatedAt = now
                    };

                    await _engine.RegisterImportedCompletedJobAsync(job, cancellationToken).ConfigureAwait(false);
                    copiedPath = null;
                    imported++;
                }
                finally
                {
                    if (copiedPath is not null)
                    {
                        try { File.Delete(copiedPath); } catch { /* ignore */ }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Failed to import {Path}", sourcePath);
            }
        }

        progress?.Report(new LocalFolderImportProgress
        {
            Total = total,
            Completed = imported + failed + skipped,
            Failed = failed,
            CurrentFileName = null
        });

        return new LocalFolderImportResult
        {
            Imported = imported,
            Failed = failed,
            Skipped = skipped,
            FolderName = safeFolder
        };
    }

    /// <summary>
    /// Playable = ffprobe sees at least one audio or video stream.
    /// Corrupted/empty containers with neither stream are skipped before copy.
    /// </summary>
    private static bool IsPlayable(LocalMediaProbeResult probed) =>
        probed.HasVideo || probed.HasAudio;

    private static bool IsMediaFile(string path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && MediaExtensions.Contains(ext);
    }

    private static string AllocateDestinationPath(string saveDir, string fileName)
    {
        var safeName = SanitizeFileName(fileName);
        var stem = Path.GetFileNameWithoutExtension(safeName);
        var ext = Path.GetExtension(safeName);
        if (string.IsNullOrWhiteSpace(stem))
            stem = "media";

        var candidate = Path.Combine(saveDir, stem + ext);
        var sequence = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(saveDir, $"{stem}_{sequence}{ext}");
            sequence++;
        }

        return candidate;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "media" : cleaned;
    }
}
