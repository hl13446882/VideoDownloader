using VideoDownloader.Core.Subtitles;

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

    Task PrepareTranslationsAsync(
        SubtitleMode mode,
        TimeSpan start,
        TimeSpan end,
        CancellationToken cancellationToken = default);

    SubtitleSegment? GetCurrent(TimeSpan mediaTime, SubtitleMode mode);

    TimeSpan? GetCoveredUntil(TimeSpan mediaTime);

    Task StopSessionAsync(CancellationToken cancellationToken = default);
}
