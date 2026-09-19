namespace VideoDownloader.Core.Subtitles;

public sealed class SubtitleSegment
{
    public long Id { get; init; }
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public string SourceLanguage { get; init; } = string.Empty;
    public string OriginalText { get; init; } = string.Empty;
    public string? ChineseText { get; set; }
    public string? EnglishText { get; set; }
    public SubtitleSegmentState State { get; set; } = SubtitleSegmentState.Recognized;

    public string GetDisplayText(SubtitleMode mode) => mode switch
    {
        SubtitleMode.Chinese => string.IsNullOrWhiteSpace(ChineseText) ? OriginalText : ChineseText,
        SubtitleMode.English => string.IsNullOrWhiteSpace(EnglishText) ? OriginalText : EnglishText,
        _ => OriginalText
    };
}

public sealed record AudioChunk(
    ReadOnlyMemory<byte> Pcm16Mono16Khz,
    TimeSpan MediaStart,
    TimeSpan MediaEnd);

public sealed record SpeechRecognitionContext(
    string SessionId,
    string? MediaIdentity,
    string? LanguageHint = null);

public sealed record TranslationRequest(
    string Text,
    string SourceLanguage,
    string TargetLanguage);

public sealed record TranslationResult(
    string Text,
    string ProviderId,
    string ProviderVersion);
