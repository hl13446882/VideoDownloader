namespace VideoDownloader.Core.Subtitles;

public sealed record SubtitlePlaybackState(
    string? MediaKey,
    string? PageUrl,
    TimeSpan CurrentTime,
    TimeSpan? Duration,
    bool Paused,
    bool Seeking,
    double PlaybackRate);

public sealed class SubtitleStyleOptions
{
    public string FontFamily { get; set; } = "Microsoft YaHei";
    public int FontSize { get; set; } = 28;
    public bool Bold { get; set; } = true;
    public string TextColor { get; set; } = "#FFFF00";
    public string OutlineColor { get; set; } = "#000000";
    public int OutlineSize { get; set; } = 2;
    public string BackgroundColor { get; set; } = "#000000";
    /// <summary>0–1 alpha used by the overlay CSS.</summary>
    public double BackgroundOpacity { get; set; } = 0.5;
    public int BottomOffsetPx { get; set; } = 60;
    public int MaxLines { get; set; } = 2;
    public int MaxWidthPercent { get; set; } = 85;
}
