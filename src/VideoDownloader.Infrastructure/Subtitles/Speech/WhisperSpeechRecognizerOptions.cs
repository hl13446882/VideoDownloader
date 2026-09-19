namespace VideoDownloader.Infrastructure.Subtitles.Speech;

public sealed class WhisperSpeechRecognizerOptions
{
    public string ModelPath { get; set; } = "%LOCALAPPDATA%\\VideoDownloader\\models\\speech\\whisper\\ggml-base.bin";
    public string Language { get; set; } = "auto";
}
