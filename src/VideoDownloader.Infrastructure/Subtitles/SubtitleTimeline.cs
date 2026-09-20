using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;

namespace VideoDownloader.Infrastructure.Subtitles;

public sealed class SubtitleTimeline : ISubtitleTimeline
{
    private readonly object _sync = new();
    private readonly List<SubtitleSegment> _segments = [];

    public void Replace(IEnumerable<SubtitleSegment> segments)
    {
        lock (_sync)
        {
            _segments.Clear();
            _segments.AddRange(Normalize(segments));
        }
    }

    public void AddOrUpdate(IEnumerable<SubtitleSegment> segments)
    {
        lock (_sync)
        {
            foreach (var segment in segments.Where(IsValid))
            {
                var index = _segments.FindIndex(x => SameSegment(x, segment));
                if (index >= 0)
                    _segments[index] = segment;
                else
                    _segments.Add(segment);
            }

            _segments.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        }
    }

    public SubtitleSegment? Find(TimeSpan mediaTime)
    {
        lock (_sync)
        {
            for (var i = _segments.Count - 1; i >= 0; i--)
            {
                var item = _segments[i];
                if (item.Start > mediaTime)
                    continue;
                return mediaTime < item.End ? item : null;
            }

            return null;
        }
    }

    public SubtitleSegment? FindAdjacent(TimeSpan mediaTime, int delta)
    {
        if (delta == 0)
            return Find(mediaTime);

        lock (_sync)
        {
            if (_segments.Count == 0)
                return null;

            var index = -1;
            for (var i = 0; i < _segments.Count; i++)
            {
                var item = _segments[i];
                if (mediaTime >= item.Start && mediaTime < item.End)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                for (var i = _segments.Count - 1; i >= 0; i--)
                {
                    if (_segments[i].Start <= mediaTime)
                    {
                        index = i;
                        break;
                    }
                }
            }

            if (index < 0)
                index = 0;

            var next = index + delta;
            if (next < 0 || next >= _segments.Count)
                return null;
            return _segments[next];
        }
    }

    public IReadOnlyList<SubtitleSegment> Snapshot()
    {
        lock (_sync)
            return _segments.ToArray();
    }

    public void Clear()
    {
        lock (_sync)
            _segments.Clear();
    }

    private static IEnumerable<SubtitleSegment> Normalize(IEnumerable<SubtitleSegment> segments) =>
        segments.Where(IsValid).OrderBy(x => x.Start).ThenBy(x => x.End);

    private static bool IsValid(SubtitleSegment segment) =>
        segment.End > segment.Start && !string.IsNullOrWhiteSpace(segment.OriginalText);

    private static bool SameSegment(SubtitleSegment left, SubtitleSegment right)
    {
        if (left.Id != 0 && right.Id != 0)
            return left.Id == right.Id;
        return left.Start == right.Start && left.End == right.End;
    }
}
