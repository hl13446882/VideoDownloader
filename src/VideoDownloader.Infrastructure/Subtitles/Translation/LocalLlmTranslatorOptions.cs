namespace VideoDownloader.Infrastructure.Subtitles.Translation;

public sealed class LocalLlmTranslatorOptions
{
    public string Endpoint { get; set; } = "http://127.0.0.1:1234/v1/chat/completions";
    public string Model { get; set; } = "local-model";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);
}
