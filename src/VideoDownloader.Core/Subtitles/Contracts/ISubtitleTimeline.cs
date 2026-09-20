using VideoDownloader.Core.Subtitles;

namespace VideoDownloader.Core.Subtitles.Contracts;

public interface ISubtitleTimeline
{
    void Replace(IEnumerable<SubtitleSegment> segments);
    void AddOrUpdate(IEnumerable<SubtitleSegment> segments);
    SubtitleSegment? Find(TimeSpan mediaTime);

    /// <summary>
    /// Adjacent cue relative to the segment covering <paramref name="mediaTime"/>
    /// (or the latest cue that has started by that time). <paramref name="delta"/> is -1 / +1.
    /// </summary>
    SubtitleSegment? FindAdjacent(TimeSpan mediaTime, int delta);

    IReadOnlyList<SubtitleSegment> Snapshot();
    void Clear();
}
