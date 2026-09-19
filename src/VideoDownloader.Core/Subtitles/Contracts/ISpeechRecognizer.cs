using VideoDownloader.Core.Subtitles;

namespace VideoDownloader.Core.Subtitles.Contracts;

public interface ISpeechRecognizer
{
    string EngineId { get; }

    Task<IReadOnlyList<SubtitleSegment>> RecognizeAsync(
        AudioChunk audio,
        SpeechRecognitionContext context,
        CancellationToken cancellationToken = default);
}
