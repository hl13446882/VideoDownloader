using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;

namespace VideoDownloader.Infrastructure.LocalLibrary;

/// <summary>
/// Persistent JPEG posters under %LOCALAPPDATA%\VideoDownloader\thumbs\{jobId}.jpg.
/// Generate once; reuse forever until the file is deleted.
/// </summary>
public sealed class LocalVideoThumbnailStore
{
    private readonly IFfmpegAdapter _ffmpeg;
    private readonly ILogger<LocalVideoThumbnailStore> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly string _root;

    public LocalVideoThumbnailStore(IFfmpegAdapter ffmpeg, ILogger<LocalVideoThumbnailStore> logger)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoDownloader",
            "thumbs");
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public string GetPath(Guid jobId) => Path.Combine(_root, jobId.ToString("N") + ".jpg");

    public bool Exists(Guid jobId)
    {
        var path = GetPath(jobId);
        try
        {
            return File.Exists(path) && new FileInfo(path).Length >= 32;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>No-op when a valid thumb already exists. Otherwise extract once (deduped).</summary>
    public void EnsureAsyncFireAndForget(Guid jobId, string? videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return;
        if (Exists(jobId))
            return;
        if (!_inFlight.TryAdd(jobId, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureAsync(jobId, videoPath, CancellationToken.None);
            }
            finally
            {
                _inFlight.TryRemove(jobId, out _);
            }
        });
    }

    public async Task<bool> EnsureAsync(Guid jobId, string videoPath, CancellationToken ct)
    {
        if (Exists(jobId))
            return true;
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return false;

        var thumb = GetPath(jobId);
        try
        {
            var ok = await _ffmpeg.TryExtractThumbnailAsync(videoPath, thumb, ct);
            if (ok && Exists(jobId))
                return true;

            _logger.LogDebug("Thumbnail extract soft-failed for {JobId}", jobId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Thumbnail extract failed for {JobId}", jobId);
            return false;
        }
    }
}
