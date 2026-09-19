using VideoDownloader.Core.Subtitles;

namespace VideoDownloader.Core.Subtitles.Contracts;

public interface ISubtitleTimeline
{
    void Replace(IEnumerable<SubtitleSegment> segments);
    void AddOrUpdate(IEnumerable<SubtitleSegment> segments);
    SubtitleSegment? Find(TimeSpan mediaTime);
    IReadOnlyList<SubtitleSegment> Snapshot();
    void Clear();
}
