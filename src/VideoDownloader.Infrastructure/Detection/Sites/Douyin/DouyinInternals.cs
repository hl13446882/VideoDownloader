using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Detection.Sites.Douyin;

internal static class DouyinIdentity
{
    private static readonly Regex VideoIdPath = new(
        @"/(?:video|note|share/video|share/note)/(?<id>\d{10,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsPageHost(Uri page) =>
        page.Host.Contains("douyin.com", StringComparison.OrdinalIgnoreCase) ||
        page.Host.Contains("iesdouyin.com", StringComparison.OrdinalIgnoreCase);

    public static bool IsMediaHost(Uri url) =>
        url.Host.Contains("douyin", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("douyinvod", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("snssdk", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("bytecdn", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("zjcdn", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("byteicdn", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("iesdouyin", StringComparison.OrdinalIgnoreCase);

    public static string? ExtractAwemeId(Uri pageUrl)
    {
        var m = VideoIdPath.Match(pageUrl.AbsolutePath);
        if (m.Success) return m.Groups["id"].Value;
        return ExtractIdFromQuery(pageUrl);
    }

    public static string? ExtractIdFromQuery(Uri pageUrl)
    {
        var q = pageUrl.Query;
        foreach (var key in new[] { "modal_id=", "aweme_id=", "item_id=", "video_id=" })
        {
            var idx = q.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var start = idx + key.Length;
            var end = q.IndexOf('&', start);
            var raw = end < 0 ? q[start..] : q[start..end];
            if (Regex.IsMatch(raw, @"^\d{10,}$"))
                return raw;
        }
        return null;
    }

    public static string? ExtractIdFromIdentity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return null;
        var m = Regex.Match(identity, @"(?:content:(?:douyin:)?)?(?<id>\d{10,})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["id"].Value : null;
    }

    public static string? ResolveContentId(Uri pageUrl, string? observedIdentity)
    {
        return ExtractAwemeId(pageUrl) ??
               ExtractIdFromIdentity(observedIdentity) ??
               ExtractIdFromQuery(pageUrl);
    }
}

internal static class DouyinPlayEvidence
{
    public static bool IsBrowserPlay(NormalizedNetworkEvent e) =>
        e.StatusCode is 200 or 206 &&
        (string.Equals(e.ResourceType, "Media", StringComparison.OrdinalIgnoreCase) ||
         IsStrongMime(e.MimeType));

    public static bool IsStrongMime(string? mime) =>
        mime is not null &&
        (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
         mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));

    public static bool LooksLikePlay(Uri url)
    {
        var full = url.AbsoluteUri;
        return full.Contains("playAddr", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("play_addr", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("downloadAddr", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("/aweme/", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("video_id=", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("/play/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTinyMseCrumb(Uri url, long? contentLength)
    {
        if (contentLength is null or <= 0 || !DouyinIdentity.IsMediaHost(url))
            return false;
        var audio = url.AbsolutePath.Contains("/media-audio-", StringComparison.OrdinalIgnoreCase) ||
                    url.AbsolutePath.Contains("/ies-music/", StringComparison.OrdinalIgnoreCase) ||
                    url.AbsoluteUri.Contains("mime_type=audio", StringComparison.OrdinalIgnoreCase);
        var min = audio ? MediaResourceSizeFilter.MinStrongMimeBytes : MediaResourceSizeFilter.MinDisplayBytes;
        return contentLength < min;
    }

    public static MediaTrackKind InferKind(Uri url, string? mime)
    {
        if (mime?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
            return MediaTrackKind.Audio;
        if (mime?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
            return MediaTrackKind.Video;
        var full = url.AbsoluteUri;
        if (full.Contains("mime_type=audio", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("/audio/tos/", StringComparison.OrdinalIgnoreCase) ||
            url.AbsolutePath.Contains("/ies-music/", StringComparison.OrdinalIgnoreCase) ||
            url.AbsolutePath.Contains("/media-audio-", StringComparison.OrdinalIgnoreCase))
            return MediaTrackKind.Audio;
        if (LooksLikePlay(url) || DouyinIdentity.IsMediaHost(url))
            return MediaTrackKind.Combined;
        return MediaTrackKind.Unknown;
    }
}

internal static class DouyinContentModeResolver
{
    public static DouyinContentMode Resolve(string? pageScriptJson, Uri pageUrl)
    {
        if (string.IsNullOrWhiteSpace(pageScriptJson))
        {
            if (pageUrl.AbsolutePath.Contains("/note/", StringComparison.OrdinalIgnoreCase))
                return DouyinContentMode.Album;
            return DouyinContentMode.Unknown;
        }

        try
        {
            using var doc = JsonDocument.Parse(pageScriptJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("album", out var album) &&
                album.ValueKind is JsonValueKind.True or JsonValueKind.String)
                return DouyinContentMode.Album;
            if (root.TryGetProperty("images", out var images) &&
                images.ValueKind == JsonValueKind.Array &&
                images.GetArrayLength() > 0)
                return DouyinContentMode.Album;
            if (root.TryGetProperty("media", out var media) &&
                media.ValueKind == JsonValueKind.Array &&
                media.GetArrayLength() > 0)
                return DouyinContentMode.Video;
        }
        catch (JsonException)
        {
        }

        if (pageUrl.AbsolutePath.Contains("/note/", StringComparison.OrdinalIgnoreCase))
            return DouyinContentMode.Album;
        return DouyinContentMode.Unknown;
    }
}

internal sealed class DouyinDetectionSession
{
    public Guid SessionId { get; set; }
    public Uri? PageUrl { get; set; }
    public string? CurrentContentId { get; set; }
    public DouyinContentMode CurrentMode { get; private set; } = DouyinContentMode.Unknown;
    public string? Caption { get; set; }
    public RequestContext Context { get; set; } = RequestContext.CreateEmpty();

    public List<MediaTrack> VideoCandidates { get; } = [];
    public List<MediaTrack> AudioCandidates { get; } = [];
    public List<AlbumImageItem> AlbumImages { get; } = [];

    public void SwitchContent(string? contentId, DouyinContentMode mode)
    {
        if (string.Equals(CurrentContentId, contentId, StringComparison.Ordinal) &&
            CurrentMode == mode &&
            mode != DouyinContentMode.Unknown)
            return;

        CurrentContentId = contentId;
        CurrentMode = mode;
        VideoCandidates.Clear();
        AudioCandidates.Clear();
        AlbumImages.Clear();
    }

    public void Reset()
    {
        SessionId = Guid.Empty;
        PageUrl = null;
        CurrentContentId = null;
        CurrentMode = DouyinContentMode.Unknown;
        Caption = null;
        Context = RequestContext.CreateEmpty();
        VideoCandidates.Clear();
        AudioCandidates.Clear();
        AlbumImages.Clear();
    }
}
