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

    /// <summary>
    /// When <paramref name="mediaTime"/> is inside a covered range, returns that range's end.
    /// When playback has run past coverage (ASR lag / small seeks), returns the latest coverage
    /// end at or before the playhead so the gap is filled instead of skipped.
    /// </summary>
    TimeSpan? GetRecognitionCursor(TimeSpan mediaTime);

    Task StopSessionAsync(CancellationToken cancellationToken = default);
}
