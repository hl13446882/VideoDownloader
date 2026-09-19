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

        var path = GetJsonPath(mediaIdentity);
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

            try
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                var transcriptPath = GetTranscriptPath(mediaIdentity);
                if (File.Exists(transcriptPath))
                    File.SetLastWriteTimeUtc(transcriptPath, DateTime.UtcNow);
            }
            catch { }

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
            var orderedSegments = snapshot.Segments.OrderBy(x => x.Start).ToArray();
            var jsonPath = GetJsonPath(mediaIdentity);
            var jsonTemp = jsonPath + ".tmp";
            var payload = new CachedSubtitles(
                mediaIdentity,
                DateTimeOffset.UtcNow,
                orderedSegments,
                snapshot.Coverage.OrderBy(x => x.Start).ToArray());

            await using (var stream = File.Create(jsonTemp))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    payload,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }
            File.Move(jsonTemp, jsonPath, overwrite: true);

            // Human-readable recognition transcript. Deliberately contains source recognition only:
            // translations are optional/replaceable and remain in the JSON machine cache.
            var transcriptPath = GetTranscriptPath(mediaIdentity);
            var transcriptTemp = transcriptPath + ".tmp";
            await File.WriteAllTextAsync(
                transcriptTemp,
                BuildRecognitionTranscript(orderedSegments),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                cancellationToken).ConfigureAwait(false);
            File.Move(transcriptTemp, transcriptPath, overwrite: true);

            TrimCache();
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetJsonPath(string mediaIdentity) =>
        Path.Combine(_root, GetCacheName(mediaIdentity) + ".json");

    private string GetTranscriptPath(string mediaIdentity) =>
        Path.Combine(_root, GetCacheName(mediaIdentity) + ".speech.txt");

    private static string GetCacheName(string mediaIdentity)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(mediaIdentity));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string BuildRecognitionTranscript(IEnumerable<SubtitleSegment> segments)
    {
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            if (segment.End <= segment.Start || string.IsNullOrWhiteSpace(segment.OriginalText))
                continue;

            builder.Append('[')
                .Append(FormatTimestamp(segment.Start))
                .Append(" --> ")
                .Append(FormatTimestamp(segment.End))
                .Append(']');
            if (!string.IsNullOrWhiteSpace(segment.SourceLanguage))
                builder.Append(" [").Append(segment.SourceLanguage.Trim()).Append(']');
            builder.AppendLine();
            builder.AppendLine(segment.OriginalText.Trim());
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static string FormatTimestamp(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;
        return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
    }

    private void TrimCache()
    {
        try
        {
            var directory = new DirectoryInfo(_root);
            var jsonFiles = directory.GetFiles("*.json")
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .ToArray();
            foreach (var file in jsonFiles.Skip(500))
            {
                try
                {
                    var stem = Path.GetFileNameWithoutExtension(file.Name);
                    file.Delete();
                    var transcript = Path.Combine(_root, stem + ".speech.txt");
                    if (File.Exists(transcript))
                        File.Delete(transcript);
                }
                catch { }
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
