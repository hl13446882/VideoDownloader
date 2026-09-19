using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Subtitles.Contracts;

public interface IMediaAudioDecoder
{
    Task<AudioChunk> DecodeAsync(
        MediaVariant variant,
        TimeSpan start,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    Task<AudioChunk> DecodeLocalFileAsync(
        string filePath,
        TimeSpan start,
        TimeSpan duration,
        CancellationToken cancellationToken = default);
}
