namespace VideoDownloader.Infrastructure.Subtitles.Browser;

public sealed class SubtitleEditRequestEventArgs : EventArgs
{
    public SubtitleEditRequestEventArgs(TimeSpan currentTime)
    {
        CurrentTime = currentTime;
    }

    public TimeSpan CurrentTime { get; }
}

public sealed class SubtitleEditCommitEventArgs : EventArgs
{
    public SubtitleEditCommitEventArgs(
        TimeSpan currentTime,
        string originalText,
        string? chineseText,
        string? englishText,
        bool multilingual)
    {
        CurrentTime = currentTime;
        OriginalText = originalText;
        ChineseText = chineseText;
        EnglishText = englishText;
        Multilingual = multilingual;
    }

    public TimeSpan CurrentTime { get; }
    public string OriginalText { get; }
    public string? ChineseText { get; }
    public string? EnglishText { get; }
    public bool Multilingual { get; }
}

public sealed class SubtitleEditorOpenModel
{
    public required string OriginalText { get; init; }
    public string? ChineseText { get; init; }
    public string? EnglishText { get; init; }
    public bool Multilingual { get; init; }
    public double CurrentTimeSeconds { get; init; }
}
