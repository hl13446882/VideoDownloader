namespace VideoDownloader.Core.Subtitles;

public enum SubtitleMode
{
    Original = 0,
    Chinese = 1,
    English = 2
}

public enum SubtitleSegmentState
{
    Recognized = 0,
    Translating = 1,
    Ready = 2,
    Failed = 3
}
