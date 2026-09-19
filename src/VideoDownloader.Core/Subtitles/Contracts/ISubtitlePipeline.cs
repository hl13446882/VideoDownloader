namespace VideoDownloader.Core.Subtitles.Contracts;

public interface ISubtitlePipeline
{
    string? ActiveSessionId { get; }

    Task StartSessionAsync(
        string sessionId,
        string? mediaIdentity,
        CancellationToken cancellationToken = default);

    Task SubmitAudioAsync(
        AudioChunk chunk,
        CancellationToken cancellationToken = default);

    SubtitleSegment? GetCurrent(TimeSpan mediaTime, SubtitleMode mode);

    Task StopSessionAsync(CancellationToken cancellationToken = default);
}
