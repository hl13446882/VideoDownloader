using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;

namespace VideoDownloader.Infrastructure.Subtitles.Cache;

public sealed class FileSubtitleCacheStore : ISubtitleCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private static readonly Regex TimestampLine = new(
        @"^\[(?<sh>\d{2}):(?<sm>\d{2}):(?<ss>\d{2})\.(?<sms>\d{3})\s*-->\s*(?<eh>\d{2}):(?<em>\d{2}):(?<es>\d{2})\.(?<ems>\d{3})\](?:\s*\[(?<lang>[^\]]+)\])?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jsonSnapshot = await LoadJsonUnlockedAsync(mediaIdentity, cancellationToken).ConfigureAwait(false);
            var transcriptSnapshot = await LoadTranscriptUnlockedAsync(mediaIdentity, cancellationToken)
                .ConfigureAwait(false);
            return MergePreferringTranscript(jsonSnapshot, transcriptSnapshot);
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

    public string GetTranscriptPath(string mediaIdentity) =>
        Path.Combine(_root, GetCacheName(mediaIdentity) + ".speech.txt");

    public async Task<string> EnsureTranscriptFileAsync(
        string mediaIdentity,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaIdentity))
            throw new ArgumentException("Media identity is required.", nameof(mediaIdentity));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_root);
            var path = GetTranscriptPath(mediaIdentity);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                return path;

            var json = await LoadJsonUnlockedAsync(mediaIdentity, cancellationToken).ConfigureAwait(false);
            var text = json.Segments.Count > 0
                ? BuildRecognitionTranscript(json.Segments)
                : BuildEmptyTranscriptTemplate();
            await File.WriteAllTextAsync(
                path,
                text,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                cancellationToken).ConfigureAwait(false);
            return path;
        }
        finally
        {
            _gate.Release();
        }
    }

    public DateTime? GetTranscriptLastWriteUtc(string mediaIdentity)
    {
        if (string.IsNullOrWhiteSpace(mediaIdentity))
            return null;
        try
        {
            var path = GetTranscriptPath(mediaIdentity);
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch
        {
            return null;
        }
    }

    internal static IReadOnlyList<SubtitleSegment> ParseRecognitionTranscript(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<SubtitleSegment>();

        var segments = new List<SubtitleSegment>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//"))
                continue;

            var match = TimestampLine.Match(line);
            if (!match.Success)
                continue;

            var start = ParseTimestamp(
                match.Groups["sh"].Value,
                match.Groups["sm"].Value,
                match.Groups["ss"].Value,
                match.Groups["sms"].Value);
            var end = ParseTimestamp(
                match.Groups["eh"].Value,
                match.Groups["em"].Value,
                match.Groups["es"].Value,
                match.Groups["ems"].Value);
            if (end <= start)
                continue;

            var language = match.Groups["lang"].Success ? match.Groups["lang"].Value.Trim() : string.Empty;
            var body = new StringBuilder();
            for (var j = i + 1; j < lines.Length; j++)
            {
                var content = lines[j];
                if (TimestampLine.IsMatch(content.Trim()))
                {
                    i = j - 1;
                    break;
                }

                if (j == lines.Length - 1)
                    i = j;

                if (string.IsNullOrWhiteSpace(content))
                {
                    if (body.Length > 0)
                    {
                        i = j;
                        break;
                    }
                    continue;
                }

                if (content.TrimStart().StartsWith('#') || content.TrimStart().StartsWith("//"))
                    continue;

                if (body.Length > 0)
                    body.AppendLine();
                body.Append(content.TrimEnd());
            }

            var original = body.ToString().Trim();
            if (string.IsNullOrWhiteSpace(original))
                continue;

            segments.Add(new SubtitleSegment
            {
                Id = start.Ticks,
                Start = start,
                End = end,
                SourceLanguage = language,
                OriginalText = original,
                State = SubtitleSegmentState.Ready
            });
        }

        return segments
            .OrderBy(x => x.Start)
            .ThenBy(x => x.End)
            .ToArray();
    }

    private async Task<SubtitleCacheSnapshot> LoadJsonUnlockedAsync(
        string mediaIdentity,
        CancellationToken cancellationToken)
    {
        var path = GetJsonPath(mediaIdentity);
        if (!File.Exists(path))
            return SubtitleCacheSnapshot.Empty;

        try
        {
            await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var cached = await JsonSerializer.DeserializeAsync<CachedSubtitles>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (cached is null)
                return SubtitleCacheSnapshot.Empty;

            try
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
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
    }

    private async Task<SubtitleCacheSnapshot> LoadTranscriptUnlockedAsync(
        string mediaIdentity,
        CancellationToken cancellationToken)
    {
        var path = GetTranscriptPath(mediaIdentity);
        if (!File.Exists(path))
            return SubtitleCacheSnapshot.Empty;

        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var segments = ParseRecognitionTranscript(text);
            if (segments.Count == 0)
                return SubtitleCacheSnapshot.Empty;

            var coverage = segments
                .Select(x => new SubtitleCoverageRange(x.Start, x.End))
                .ToArray();
            return new SubtitleCacheSnapshot(segments, coverage);
        }
        catch
        {
            return SubtitleCacheSnapshot.Empty;
        }
    }

    private static SubtitleCacheSnapshot MergePreferringTranscript(
        SubtitleCacheSnapshot json,
        SubtitleCacheSnapshot transcript)
    {
        if (transcript.Segments.Count == 0)
            return json;
        if (json.Segments.Count == 0)
            return new SubtitleCacheSnapshot(transcript.Segments, MergeCoverage(transcript.Coverage));

        // Transcript overrides original text / presence for its time ranges; JSON keeps translations.
        var byId = json.Segments.ToDictionary(x => x.Id);
        foreach (var spoken in transcript.Segments)
        {
            if (byId.TryGetValue(spoken.Id, out var existing))
            {
                existing.OriginalText = spoken.OriginalText;
                existing.SourceLanguage = string.IsNullOrWhiteSpace(spoken.SourceLanguage)
                    ? existing.SourceLanguage
                    : spoken.SourceLanguage;
                if (existing.State == SubtitleSegmentState.Recognized)
                    existing.State = SubtitleSegmentState.Ready;
            }
            else
            {
                byId[spoken.Id] = spoken;
            }
        }

        var segments = byId.Values.OrderBy(x => x.Start).ToArray();
        var coverage = MergeCoverage(json.Coverage.Concat(transcript.Coverage));
        return new SubtitleCacheSnapshot(segments, coverage);
    }

    private static IReadOnlyList<SubtitleCoverageRange> MergeCoverage(
        IEnumerable<SubtitleCoverageRange> ranges)
    {
        var ordered = ranges
            .Where(x => x.End > x.Start)
            .OrderBy(x => x.Start)
            .ToList();
        if (ordered.Count == 0)
            return Array.Empty<SubtitleCoverageRange>();

        var merged = new List<SubtitleCoverageRange>();
        var currentStart = ordered[0].Start;
        var currentEnd = ordered[0].End;
        var tolerance = TimeSpan.FromMilliseconds(750);
        for (var i = 1; i < ordered.Count; i++)
        {
            var next = ordered[i];
            if (next.Start <= currentEnd + tolerance)
            {
                if (next.End > currentEnd)
                    currentEnd = next.End;
                continue;
            }

            merged.Add(new SubtitleCoverageRange(currentStart, currentEnd));
            currentStart = next.Start;
            currentEnd = next.End;
        }

        merged.Add(new SubtitleCoverageRange(currentStart, currentEnd));
        return merged;
    }

    public async Task DeleteAsync(string mediaIdentity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaIdentity))
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TryDeleteFile(GetJsonPath(mediaIdentity));
            TryDeleteFile(GetTranscriptPath(mediaIdentity));
            TryDeleteFile(GetJsonPath(mediaIdentity) + ".tmp");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Cache delete is best effort.
        }
    }

    private string GetJsonPath(string mediaIdentity) =>
        Path.Combine(_root, GetCacheName(mediaIdentity) + ".json");

    private static string GetCacheName(string mediaIdentity)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(mediaIdentity));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string BuildEmptyTranscriptTemplate() =>
        """
        # VideoDownloader speech transcript
        # Format:
        # [HH:MM:SS.mmm --> HH:MM:SS.mmm] [lang]
        # subtitle text
        #
        # Ranges present here are treated as already recognized and will not be re-run through Whisper.

        """;

    private static string BuildRecognitionTranscript(IEnumerable<SubtitleSegment> segments)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# VideoDownloader speech transcript");
        builder.AppendLine("# Format: [start --> end] [lang] then text. Edited ranges skip Whisper.");
        builder.AppendLine();
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

    private static TimeSpan ParseTimestamp(string h, string m, string s, string ms) =>
        new(
            0,
            int.Parse(h, CultureInfo.InvariantCulture),
            int.Parse(m, CultureInfo.InvariantCulture),
            int.Parse(s, CultureInfo.InvariantCulture),
            int.Parse(ms, CultureInfo.InvariantCulture));

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
