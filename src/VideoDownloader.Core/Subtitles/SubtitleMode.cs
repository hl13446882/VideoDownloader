namespace VideoDownloader.Core.Subtitles;

public enum SubtitleMode
{
    Original = 0,
    Chinese = 1,
    English = 2,
    /// <summary>中英对照：同时显示中文与英文两行。</summary>
    Bilingual = 3
}

public enum SubtitleSegmentState
{
    Recognized = 0,
    Translating = 1,
    Ready = 2,
    Failed = 3
}
