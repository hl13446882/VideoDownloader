using System.Text.Json;
using VideoDownloader.Core.Detection;

namespace VideoDownloader.Infrastructure.Detection;

internal static class MediaAddressScanner
{
    internal sealed record Address(string Url, string? ContentIdentity);
    public static IReadOnlyList<string> FromJson(string json) => Scan(json).Select(a => a.Url).Distinct(StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<Address> Scan(string json, string? currentOwner = null, string? requestOwner = null)
    {
        using var doc = JsonDocument.Parse(json);
        var urls = new HashSet<Address>();
        Visit(doc.RootElement, "", 0, requestOwner);
        return urls.ToArray();

        void Visit(JsonElement value, string key, int depth, string? owner)
        {
            if (depth > 20 || urls.Count >= 64)
                return;

            if (value.ValueKind == JsonValueKind.Object)
            {
                // Generic ids need a media subtree; explicit content ids can own a surrounding data wrapper.
                var hasMedia = new[] { "video", "videoData", "playAddr", "play_addr", "dash", "durl", "formats" }.Any(k => value.TryGetProperty(k, out _));
                foreach (var idKey in new[] { "aweme_id", "itemId", "videoId", "bvid", "id" })
                    if ((idKey != "id" || hasMedia) &&
                        value.TryGetProperty(idKey, out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number && !string.IsNullOrWhiteSpace(id.ToString()))
                    { owner = "id:" + id; break; }
                foreach (var p in value.EnumerateObject())
                    Visit(p.Value, p.Name, depth + 1, owner);
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                    Visit(item, key, depth + 1, owner);
            }
            else if (value.ValueKind == JsonValueKind.String &&
                     Uri.TryCreate(value.GetString(), UriKind.Absolute, out var url) &&
                     url.Scheme is "http" or "https")
            {
                var keyHint =
                    key.Contains("url", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("src", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("play", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("video", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("stream", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("media", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("base", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("backup", StringComparison.OrdinalIgnoreCase);

                if ((currentOwner is null || owner is null || owner == currentOwner) &&
                    (UnifiedMediaPipeline.IsCandidate(url, null) || (keyHint && !IsAssetExtension(url))))
                    urls.Add(new(url.AbsoluteUri, owner));
            }
        }
    }

    private static bool IsAssetExtension(Uri url)
    {
        var ext = Path.GetExtension(url.AbsolutePath).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".svg" or ".webp" or ".ico" or ".css" or ".js" or ".html" or ".htm";
    }
}
