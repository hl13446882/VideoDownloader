namespace VideoDownloader.Core.Subtitles;

public sealed record SubtitleCoverageRange(
    TimeSpan Start,
    TimeSpan End);

public sealed record SubtitleCacheSnapshot(
    IReadOnlyList<SubtitleSegment> Segments,
    IReadOnlyList<SubtitleCoverageRange> Coverage)
{
    public static SubtitleCacheSnapshot Empty { get; } = new(
        Array.Empty<SubtitleSegment>(),
        Array.Empty<SubtitleCoverageRange>());
}
