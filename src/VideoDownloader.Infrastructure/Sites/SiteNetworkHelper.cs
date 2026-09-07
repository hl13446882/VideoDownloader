using System.Text.Json;
using System.Text.RegularExpressions;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Sites;

internal static partial class SiteNetworkHelper
{
    public static IReadOnlyList<Uri> FilterEvents(
        IEnumerable<NormalizedNetworkEvent> events,
        Func<Uri, bool> hostMatcher)
    {
        return events
            .Where(e => hostMatcher(e.Url))
            .Select(e => e.Url)
            .Distinct()
            .ToList();
    }

    public static bool IsYouTubeHost(Uri url) =>
        url.Host.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);

    public static bool IsBilibiliHost(Uri url) =>
        url.Host.Contains("bilivideo", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("hdslb.com", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("upos", StringComparison.OrdinalIgnoreCase) ||
        url.AbsolutePath.Contains(".m4s", StringComparison.OrdinalIgnoreCase);

    public static bool IsDouyinHost(Uri url) =>
        url.Host.Contains("douyin", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("douyinvod", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("snssdk", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("bytecdn", StringComparison.OrdinalIgnoreCase);

    public static bool IsTikTokHost(Uri url) =>
        url.Host.Contains("tiktok", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("tiktokcdn", StringComparison.OrdinalIgnoreCase);

    public static string? ExtractYouTubeVideoId(Uri pageUrl)
    {
        if (pageUrl.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
            return pageUrl.AbsolutePath.Trim('/').Split('/').FirstOrDefault();

        var v = Regex.Match(pageUrl.Query, @"[?&]v=([^&]+)", RegexOptions.IgnoreCase);
        if (v.Success)
            return v.Groups[1].Value;

        var shorts = Regex.Match(pageUrl.AbsolutePath, @"/shorts/([^/?#]+)", RegexOptions.IgnoreCase);
        if (shorts.Success)
            return shorts.Groups[1].Value;

        return null;
    }

    public static string? ExtractBilibiliContentId(Uri pageUrl)
    {
        var bv = Regex.Match(pageUrl.AbsolutePath, @"/video/(BV[\w]+)", RegexOptions.IgnoreCase);
        if (bv.Success)
            return bv.Groups[1].Value.ToUpperInvariant();

        var av = Regex.Match(pageUrl.AbsolutePath, @"/video/av(\d+)", RegexOptions.IgnoreCase);
        if (av.Success)
            return $"av{av.Groups[1].Value}";

        return null;
    }

    public static long? ParseContentLength(NormalizedNetworkEvent e) => e.ContentLength;

    public static JsonDocument? TryParseScriptJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null")
            return null;

        try
        {
            return JsonDocument.Parse(json);
        }
        catch
        {
            return null;
        }
    }
}

internal static class SiteVideoBuilder
{
    public static DetectedVideo Build(
        string siteId,
        string? contentId,
        string title,
        Uri pageUrl,
        MediaFamily family,
        IReadOnlyList<MediaVariant> variants,
        bool isDrm,
        ProbeSource source,
        string? hint = null,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(
            DeterministicGuid($"{siteId}:{contentId}:{pageUrl}"),
            siteId,
            contentId,
            title,
            pageUrl,
            family,
            variants,
            isDrm,
            source,
            hint,
            metadata);

    private static Guid DeterministicGuid(string input)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }
}
