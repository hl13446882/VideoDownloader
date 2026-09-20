using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.LocalLibrary;

namespace VideoDownloader.Infrastructure.Subtitles;

public sealed record LocalPlaybackMediaSource(
    Guid JobId,
    string FilePath,
    string CacheIdentity);

/// <summary>
/// Resolves the application's own /play/{jobId} local-player page back to the completed file.
/// Subtitle recognition must read this file directly instead of re-downloading it through the
/// loopback streaming endpoint.
/// </summary>
public sealed class LocalPlaybackMediaSourceResolver
{
    private readonly IDownloadRepository _repository;

    public LocalPlaybackMediaSourceResolver(IDownloadRepository repository)
    {
        _repository = repository;
    }

    public async Task<LocalPlaybackMediaSource?> ResolveAsync(
        string? pageUrl,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) ||
            !LocalLibraryHost.IsLocalPlayerUrl(uri))
            return null;

        var idText = uri.AbsolutePath["/play/".Length..].Trim('/');
        if (!Guid.TryParseExact(idText, "N", out var jobId) &&
            !Guid.TryParse(idText, out jobId))
            return null;

        var job = await _repository.GetByIdAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null || job.Status != DownloadStatus.Completed ||
            string.IsNullOrWhiteSpace(job.TargetPath))
            return null;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(job.TargetPath);
        }
        catch
        {
            return null;
        }

        if (!File.Exists(fullPath))
            return null;

        var info = new FileInfo(fullPath);
        // asr2: FFmpeg coarse+fine seek for Whisper timestamps (invalidates prior keyframe-seek caches).
        var identity = $"local:{jobId:N}:{info.Length}:{info.LastWriteTimeUtc.Ticks}:asr2";
        return new LocalPlaybackMediaSource(jobId, fullPath, identity);
    }
}
