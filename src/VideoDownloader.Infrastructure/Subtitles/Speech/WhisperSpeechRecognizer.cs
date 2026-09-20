using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;
using VideoDownloader.Infrastructure.Configuration;
using Whisper.net;

namespace VideoDownloader.Infrastructure.Subtitles.Speech;

public sealed class WhisperSpeechRecognizer : ISpeechRecognizer, IDisposable
{
    private readonly WhisperSpeechRecognizerOptions _options;
    private readonly ILogger<WhisperSpeechRecognizer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;
    private string? _loadedModelPath;

    public WhisperSpeechRecognizer(
        WhisperSpeechRecognizerOptions options,
        ILogger<WhisperSpeechRecognizer> logger)
    {
        _options = options;
        _logger = logger;
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
            {
                _logger.LogWarning("Whisper model missing path={Path}", modelPath);
                throw new FileNotFoundException(
                    "Whisper model is not installed. Configure or install the local speech model first.",
                    modelPath);
            }

            EnsureFactory(modelPath);

            // Prefer an explicit language only when configured / hinted. Otherwise detect per window
            // so mixed-language media is not stuck on the first window's language.
            var language = !string.IsNullOrWhiteSpace(context.LanguageHint) &&
                           !string.Equals(context.LanguageHint, "auto", StringComparison.OrdinalIgnoreCase)
                ? context.LanguageHint!.Trim()
                : null;
            if (language is null &&
                !string.IsNullOrWhiteSpace(_options.Language) &&
                !string.Equals(_options.Language, "auto", StringComparison.OrdinalIgnoreCase))
                language = _options.Language.Trim();

            var samples = ConvertPcm16ToFloat(audio.Pcm16Mono16Khz.Span);
            var builder = _factory!.CreateBuilder()
                // Lower than whisper.cpp default (~0.6) so soft / brief speech is less often dropped.
                .WithNoSpeechThreshold(0.35f)
                .WithProbabilities();

            if (string.IsNullOrWhiteSpace(language))
                builder.WithLanguageDetection();
            else
                builder.WithLanguage(language);

            using var processor = builder.Build();

            var segments = new List<SubtitleSegment>();
            var detectedLanguage = language ?? "auto";
            await foreach (var result in processor.ProcessAsync(samples, cancellationToken))
            {
                var text = result.Text?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                // Drop near-empty Whisper hallucinations / noise tokens.
                if (text is "." or "。" or "?" or "？" or "!" or "！")
                    continue;

                if (!string.IsNullOrWhiteSpace(result.Language))
                    detectedLanguage = result.Language.Trim();

                var absoluteStart = audio.MediaStart + result.Start;
                var absoluteEnd = audio.MediaStart + result.End;
                if (absoluteEnd <= absoluteStart)
                    absoluteEnd = absoluteStart + TimeSpan.FromMilliseconds(200);

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

            _logger.LogInformation(
                "Whisper recognize session={SessionId} lang={Lang} segments={Count} window={Start:g}-{End:g}",
                context.SessionId,
                detectedLanguage,
                segments.Count,
                audio.MediaStart,
                audio.MediaEnd);
            return segments;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            _logger.LogWarning(
                ex,
                "Whisper recognize failed session={SessionId} window={Start:g}-{End:g}",
                context.SessionId,
                audio.MediaStart,
                audio.MediaEnd);
            throw;
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

        _logger.LogInformation("Whisper loading model path={Path}", modelPath);
        _factory?.Dispose();
        _factory = WhisperFactory.FromPath(modelPath);
        _loadedModelPath = modelPath;
        _logger.LogInformation("Whisper model ready path={Path}", modelPath);
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
