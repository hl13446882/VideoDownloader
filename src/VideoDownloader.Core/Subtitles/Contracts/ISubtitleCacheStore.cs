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

    /// <summary>Path of the human-editable recognition transcript (.speech.txt).</summary>
    string GetTranscriptPath(string mediaIdentity);

    /// <summary>
    /// Ensures a transcript file exists (exports current cache / empty template), then returns its path.
    /// </summary>
    Task<string> EnsureTranscriptFileAsync(
        string mediaIdentity,
        CancellationToken cancellationToken = default);

    /// <summary>Last write time of the transcript file, if present.</summary>
    DateTime? GetTranscriptLastWriteUtc(string mediaIdentity);

    /// <summary>Deletes cached recognition JSON and transcript for this media identity.</summary>
    Task DeleteAsync(string mediaIdentity, CancellationToken cancellationToken = default);
}
