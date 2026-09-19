using VideoDownloader.Core.Subtitles;

namespace VideoDownloader.Core.Subtitles.Contracts;

public interface ISubtitleCacheStore
{
    Task<SubtitleCacheSnapshot> LoadAsync(
        string mediaIdentity,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        string mediaIdentity,
        SubtitleCacheSnapshot snapshot,
        CancellationToken cancellationToken = default);
}
