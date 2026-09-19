using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;
using VideoDownloader.Infrastructure.Configuration;
using Whisper.net;

namespace VideoDownloader.Infrastructure.Subtitles.Speech;

public sealed class WhisperSpeechRecognizer : ISpeechRecognizer
{
    private readonly WhisperSpeechRecognizerOptions _options;

    public WhisperSpeechRecognizer(WhisperSpeechRecognizerOptions options)
    {
        _options = options;
    }

    public string EngineId => "whisper.net-1.9.1";

    public async Task<IReadOnlyList<SubtitleSegment>> RecognizeAsync(
        AudioChunk audio,
        SpeechRecognitionContext context,
        CancellationToken cancellationToken = default)
    {
        if (audio.Pcm16Mono16Khz.IsEmpty)
            return Array.Empty<SubtitleSegment>();

        var modelPath = PathExpander.Expand(_options.ModelPath);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException(
                "Whisper model is not installed. Configure or install the local speech model first.",
                modelPath);

        var samples = ConvertPcm16ToFloat(audio.Pcm16Mono16Khz.Span);
        using var factory = WhisperFactory.FromPath(modelPath);
        using var processor = factory.CreateBuilder()
            .WithLanguage(string.IsNullOrWhiteSpace(_options.Language) ? "auto" : _options.Language)
            .Build();

        var detectedLanguage = !string.IsNullOrWhiteSpace(context.LanguageHint) &&
                               !string.Equals(context.LanguageHint, "auto", StringComparison.OrdinalIgnoreCase)
            ? context.LanguageHint!
            : processor.DetectLanguage(samples);
        if (string.IsNullOrWhiteSpace(detectedLanguage))
            detectedLanguage = "auto";

        var segments = new List<SubtitleSegment>();
        long id = 0;
        await foreach (var result in processor.ProcessAsync(samples, cancellationToken))
        {
            var text = result.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            segments.Add(new SubtitleSegment
            {
                Id = ++id,
                Start = audio.MediaStart + result.Start,
                End = audio.MediaStart + result.End,
                SourceLanguage = detectedLanguage,
                OriginalText = text,
                State = SubtitleSegmentState.Recognized
            });
        }

        return segments;
    }

    private static float[] ConvertPcm16ToFloat(ReadOnlySpan<byte> pcm)
    {
        var sampleCount = pcm.Length / 2;
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var lo = pcm[i * 2];
            var hi = pcm[i * 2 + 1];
            var value = (short)(lo | (hi << 8));
            samples[i] = value / 32768f;
        }
        return samples;
    }
}
