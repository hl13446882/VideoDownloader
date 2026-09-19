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
        SubtitleMode.Bilingual => FormatBilingual(),
        _ => OriginalText
    };

    private string FormatBilingual()
    {
        var chinese = ResolveSide(ChineseText, isChinese: true);
        var english = ResolveSide(EnglishText, isChinese: false);

        if (!string.IsNullOrWhiteSpace(chinese) && !string.IsNullOrWhiteSpace(english))
        {
            if (string.Equals(chinese, english, StringComparison.Ordinal))
                return chinese;
            return chinese + "\n" + english;
        }

        if (!string.IsNullOrWhiteSpace(chinese))
            return chinese;
        if (!string.IsNullOrWhiteSpace(english))
            return english;
        return OriginalText;
    }

    private string? ResolveSide(string? translated, bool isChinese)
    {
        if (!string.IsNullOrWhiteSpace(translated))
            return translated.Trim();
        if (IsLanguage(SourceLanguage, isChinese ? "zh" : "en"))
            return string.IsNullOrWhiteSpace(OriginalText) ? null : OriginalText.Trim();
        return null;
    }

    private static bool IsLanguage(string? source, string target)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;
        source = source.Trim().ToLowerInvariant();
        return target == "zh"
            ? source is "zh" or "zh-cn" or "chinese"
            : source is "en" or "en-us" or "en-gb" or "english";
    }
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
