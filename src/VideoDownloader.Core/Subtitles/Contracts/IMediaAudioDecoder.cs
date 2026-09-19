namespace VideoDownloader.Core.Subtitles.Contracts;

public interface IMediaAudioDecoder
{
    Task<AudioChunk> DecodeLocalFileAsync(
        string filePath,
        TimeSpan start,
        TimeSpan duration,
        CancellationToken cancellationToken = default);
}
