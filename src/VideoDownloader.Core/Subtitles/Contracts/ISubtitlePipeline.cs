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

    /// <summary>Replace timeline + coverage from cache/transcript without resetting the session id.</summary>
    void ApplyCacheSnapshot(SubtitleCacheSnapshot snapshot);

    /// <summary>
    /// Updates the segment at <paramref name="mediaTime"/> and marks that range as covered so
    /// Whisper will not re-recognize it. Non-null Chinese/English values are stored as translation
    /// cache and will skip machine translation on later playback.
    /// </summary>
    Task<SubtitleSegment?> ApplyManualEditAsync(
        TimeSpan mediaTime,
        string originalText,
        string? chineseText = null,
        string? englishText = null,
        bool preserveExistingTranslations = false,
        CancellationToken cancellationToken = default);

    Task StopSessionAsync(CancellationToken cancellationToken = default);
}
