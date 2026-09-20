using VideoDownloader.Core.Subtitles;

namespace VideoDownloader.Infrastructure.Configuration;

public sealed class SubtitleOptions
{
    public bool Enabled { get; set; } = true;
    public SubtitleMode Mode { get; set; } = SubtitleMode.Chinese;
    public int PreloadAheadSeconds { get; set; } = 45;
    public string TranslationProvider { get; set; } = "local";
    public string LocalTranslationEndpoint { get; set; } = "http://127.0.0.1:1234/v1/chat/completions";
    public string LocalTranslationModel { get; set; } = "local-model";
    public string WhisperModelPath { get; set; } = "%LOCALAPPDATA%\\VideoDownloader\\models\\speech\\whisper\\ggml-base.bin";
    public string FontFamily { get; set; } = "Microsoft YaHei";
    public int FontSize { get; set; } = 28;
    public bool Bold { get; set; } = true;
    public string TextColor { get; set; } = "#FFFF00";
    public string OutlineColor { get; set; } = "#000000";
    public int OutlineSize { get; set; } = 2;
    public string BackgroundColor { get; set; } = "#000000";
    /// <summary>Background box opacity level 0–10 (0 = transparent, 10 = opaque). Default 5.</summary>
    public int BackgroundOpacityLevel { get; set; } = 5;
    public int BottomOffsetPx { get; set; } = 60;
    public int MaxLines { get; set; } = 2;
    public int MaxWidthPercent { get; set; } = 85;
    public int SubtitleOffsetMs { get; set; }
}
