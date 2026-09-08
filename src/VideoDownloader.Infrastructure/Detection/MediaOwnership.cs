using System.Text.RegularExpressions;

namespace VideoDownloader.Infrastructure.Detection;

internal static class MediaOwnership
{
    public static string? ForPage(Uri page, string? observation)
    {
        if (observation is not null)
        {
            var match = Regex.Match(observation, @"(?:content:|data-(?:video|aweme|item)-id:|data-bvid:)([A-Za-z0-9_-]+)");
            if (match.Success) return "id:" + match.Groups[1].Value;
        }
        foreach (var part in page.Query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0] is "v" or "id" or "video_id" or "videoId" or "modal_id" or "aweme_id" or "bvid" or "item_id" or "itemId")
                return "id:" + Uri.UnescapeDataString(pair[1]);
        }
        var path = Regex.Match(page.AbsolutePath, @"/(?:video|shorts|note|archives)/([^/]+)");
        if (path.Success) return "id:" + Uri.UnescapeDataString(path.Groups[1].Value);
        var macId = Regex.Match(page.AbsolutePath, @"/(?:id|vod)/(\d{3,})", RegexOptions.IgnoreCase);
        if (macId.Success) return "id:" + macId.Groups[1].Value;
        return string.IsNullOrWhiteSpace(observation) ? null : "observation:" + observation;
    }
}
