using System.Text.Json;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Detection;

/// <summary>yt-dlp <c>http_dash_segments</c> metadata shared by the yt-dlp extractors.</summary>
internal static class YtDlpSegments
{
    public static IReadOnlyList<Uri>? ReadUrls(JsonElement format)
    {
        if (!format.TryGetProperty("fragments", out var fragments) ||
            fragments.ValueKind != JsonValueKind.Array)
            return null;

        var urls = new List<Uri>();
        foreach (var fragment in fragments.EnumerateArray())
        {
            if (fragment.TryGetProperty("url", out var value) &&
                value.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(value.GetString(), UriKind.Absolute, out var url))
                urls.Add(url);
        }

        return urls.Count > 1 ? urls : null;
    }

    public static bool IsHttpDashSegments(JsonElement format) =>
        format.TryGetProperty("protocol", out var protocol) &&
        protocol.ValueKind == JsonValueKind.String &&
        string.Equals(protocol.GetString(), "http_dash_segments", StringComparison.OrdinalIgnoreCase);

    /// <summary>A template URL without fragment list is not a downloadable object.</summary>
    public static bool HasUsableAddress(JsonElement format, string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        (!IsHttpDashSegments(format) || ReadUrls(format) is not null);

    public static MediaTrack Attach(JsonElement format, MediaTrack track)
    {
        var urls = ReadUrls(format);
        if (urls is null)
            return track;

        return track with
        {
            SourceUrl = urls[0],
            HttpDashFragmentCount = urls.Count,
            HttpDashFragmentUrls = urls
        };
    }
}
