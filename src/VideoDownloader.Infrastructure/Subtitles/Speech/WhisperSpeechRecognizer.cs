using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;
using VideoDownloader.Infrastructure.Configuration;
using Whisper.net;

namespace VideoDownloader.Infrastructure.Subtitles.Speech;

public sealed class WhisperSpeechRecognizer : ISpeechRecognizer, IDisposable
{
    private readonly WhisperSpeechRecognizerOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;
    private string? _loadedModelPath;

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

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var modelPath = PathExpander.Expand(_options.ModelPath);
            if (!File.Exists(modelPath))
                throw new FileNotFoundException(
                    "Whisper model is not installed. Configure or install the local speech model first.",
                    modelPath);

            EnsureFactory(modelPath);
            var samples = ConvertPcm16ToFloat(audio.Pcm16Mono16Khz.Span);
            using var processor = _factory!.CreateBuilder()
                .WithLanguage(string.IsNullOrWhiteSpace(_options.Language) ? "auto" : _options.Language)
                .Build();

            var detectedLanguage = !string.IsNullOrWhiteSpace(context.LanguageHint) &&
                                   !string.Equals(context.LanguageHint, "auto", StringComparison.OrdinalIgnoreCase)
                ? context.LanguageHint!
                : processor.DetectLanguage(samples);
            if (string.IsNullOrWhiteSpace(detectedLanguage))
                detectedLanguage = "auto";

            var segments = new List<SubtitleSegment>();
            await foreach (var result in processor.ProcessAsync(samples, cancellationToken))
            {
                var text = result.Text?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var absoluteStart = audio.MediaStart + result.Start;
                var absoluteEnd = audio.MediaStart + result.End;
                segments.Add(new SubtitleSegment
                {
                    // Stable across windows and re-recognition of the same absolute segment start.
                    Id = absoluteStart.Ticks,
                    Start = absoluteStart,
                    End = absoluteEnd,
                    SourceLanguage = detectedLanguage,
                    OriginalText = text,
                    State = SubtitleSegmentState.Recognized
                });
            }

            return segments;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureFactory(string modelPath)
    {
        if (_factory is not null &&
            string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            return;

        _factory?.Dispose();
        _factory = WhisperFactory.FromPath(modelPath);
        _loadedModelPath = modelPath;
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

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
        _loadedModelPath = null;
        _gate.Dispose();
    }
}
