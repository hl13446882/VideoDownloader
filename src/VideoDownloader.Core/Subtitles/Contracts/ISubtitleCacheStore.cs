using VideoDownloader.Core.Subtitles;

namespace VideoDownloader.Core.Subtitles.Contracts;

public interface ISubtitleCacheStore
{
    Task<IReadOnlyList<SubtitleSegment>> LoadAsync(
        string mediaIdentity,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        string mediaIdentity,
        IReadOnlyList<SubtitleSegment> segments,
        CancellationToken cancellationToken = default);
}
