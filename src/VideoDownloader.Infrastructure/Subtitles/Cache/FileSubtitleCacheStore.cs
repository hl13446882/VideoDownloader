using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;

namespace VideoDownloader.Infrastructure.Subtitles.Cache;

public sealed class FileSubtitleCacheStore : ISubtitleCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoDownloader",
        "subtitles");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<SubtitleCacheSnapshot> LoadAsync(
        string mediaIdentity,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaIdentity))
            return SubtitleCacheSnapshot.Empty;

        var path = GetPath(mediaIdentity);
        if (!File.Exists(path))
            return SubtitleCacheSnapshot.Empty;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var cached = await JsonSerializer.DeserializeAsync<CachedSubtitles>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (cached is null)
                return SubtitleCacheSnapshot.Empty;

            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
            var segments = (cached.Segments ?? Array.Empty<SubtitleSegment>())
                .Where(x => x.End > x.Start && !string.IsNullOrWhiteSpace(x.OriginalText))
                .OrderBy(x => x.Start)
                .ToArray();
            var coverage = (cached.Coverage ?? Array.Empty<SubtitleCoverageRange>())
                .Where(x => x.End > x.Start)
                .OrderBy(x => x.Start)
                .ToArray();
            return new SubtitleCacheSnapshot(segments, coverage);
        }
        catch (JsonException)
        {
            return SubtitleCacheSnapshot.Empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        string mediaIdentity,
        SubtitleCacheSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaIdentity) ||
            (snapshot.Segments.Count == 0 && snapshot.Coverage.Count == 0))
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_root);
            var path = GetPath(mediaIdentity);
            var temp = path + ".tmp";
            var payload = new CachedSubtitles(
                mediaIdentity,
                DateTimeOffset.UtcNow,
                snapshot.Segments.OrderBy(x => x.Start).ToArray(),
                snapshot.Coverage.OrderBy(x => x.Start).ToArray());

            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    payload,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
            TrimCache();
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetPath(string mediaIdentity)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(mediaIdentity));
        var name = Convert.ToHexString(bytes).ToLowerInvariant();
        return Path.Combine(_root, name + ".json");
    }

    private void TrimCache()
    {
        try
        {
            var directory = new DirectoryInfo(_root);
            var files = directory.GetFiles("*.json")
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .ToArray();
            foreach (var file in files.Skip(500))
            {
                try { file.Delete(); } catch { }
            }
        }
        catch
        {
            // Cache cleanup is best effort.
        }
    }

    private sealed record CachedSubtitles(
        string MediaIdentity,
        DateTimeOffset UpdatedAt,
        IReadOnlyList<SubtitleSegment>? Segments,
        IReadOnlyList<SubtitleCoverageRange>? Coverage);
}
