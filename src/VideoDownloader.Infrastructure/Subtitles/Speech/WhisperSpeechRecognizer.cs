using System.Text;
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

        using var factory = WhisperFactory.FromPath(modelPath);
        using var processor = factory.CreateBuilder()
            .WithLanguage(string.IsNullOrWhiteSpace(_options.Language) ? "auto" : _options.Language)
            .Build();
        using var wav = CreateWaveStream(audio.Pcm16Mono16Khz);

        var segments = new List<SubtitleSegment>();
        long id = 0;
        await foreach (var result in processor.ProcessAsync(wav, cancellationToken))
        {
            var text = result.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            segments.Add(new SubtitleSegment
            {
                Id = ++id,
                Start = audio.MediaStart + result.Start,
                End = audio.MediaStart + result.End,
                SourceLanguage = context.LanguageHint ?? _options.Language,
                OriginalText = text,
                State = SubtitleSegmentState.Recognized
            });
        }

        return segments;
    }

    private static MemoryStream CreateWaveStream(ReadOnlyMemory<byte> pcm)
    {
        const int sampleRate = 16000;
        const short channels = 1;
        const short bitsPerSample = 16;
        const short blockAlign = channels * (bitsPerSample / 8);
        const int byteRate = sampleRate * blockAlign;

        var stream = new MemoryStream(44 + pcm.Length);
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + pcm.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(pcm.Length);
            writer.Write(pcm.Span);
        }

        stream.Position = 0;
        return stream;
    }
}
