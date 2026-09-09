using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;

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
    public static bool IsMusicPath(Uri url) =>
        url.AbsolutePath.Contains("/ies-music/", StringComparison.OrdinalIgnoreCase) ||
        url.AbsolutePath.Contains("/media-audio-", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(url.AbsolutePath, @"\.(?:m4a|mp3|aac)$", RegexOptions.IgnoreCase);

    /// <summary>
    /// /aweme/v1/play gateways (www/amemv) only 302 to CDN — keep as recovery, never prefer.
    /// </summary>
    public static bool IsPlayGateway(Uri url)
    {
        if (!url.AbsolutePath.Contains("/aweme/v1/play", StringComparison.OrdinalIgnoreCase) &&
            !url.AbsolutePath.Contains("/aweme/v1/playwm", StringComparison.OrdinalIgnoreCase))
            return false;
        var host = url.Host;
        return host.Contains("douyin.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("iesdouyin.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("amemv.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPlayableUrl(Uri url)
    {
        if (IsLivePullHost(url))
            return false;
        if (IsMusicPath(url))
            return true;
        if (IsNonDownloadableHost(url))
            return false;
        return IsStrongVodHost(url) ||
               IsPlayGateway(url) ||
               Regex.IsMatch(url.AbsolutePath, @"\.(?:mp4|webm|m4a|mp3|aac|m3u8|mpd)$", RegexOptions.IgnoreCase) ||
               url.AbsolutePath.Contains("/media-video-", StringComparison.OrdinalIgnoreCase) ||
               url.AbsolutePath.Contains("/video/tos/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNonMediaMime(string? mime) =>
        mime is not null && (mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
            mime.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            mime.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
            mime.Contains("protobuf", StringComparison.OrdinalIgnoreCase));

    public static long? GetEntityLength(NormalizedNetworkEvent e)
    {
        // MSE adaptive tracks: never promote Content-Range TOTAL into ContentLength —
        // that made 1.5MB Range windows look like 332MB complete progressive files.
        if (IsMseAdaptivePath(e.Url))
            return null;

        var range = e.ResponseHeaders.FirstOrDefault(h => h.Key.Equals("Content-Range", StringComparison.OrdinalIgnoreCase)).Value;
        if (e.StatusCode == 206 || !string.IsNullOrWhiteSpace(range))
        {
            if (System.Net.Http.Headers.ContentRangeHeaderValue.TryParse(range, out var parsed) &&
                parsed.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) &&
                parsed.HasRange &&
                parsed.Length is > 0 &&
                parsed.From is not null &&
                parsed.To is not null)
            {
                var window = parsed.To.Value - parsed.From.Value + 1;
                // Tiny MSE Range windows must not advertise the full VOD size.
                if (window <= MediaResourceSizeFilter.MinDisplayBytes)
                    return null;
                // Incomplete partial: body length ≠ entity total. Do not treat TOTAL as ContentLength.
                if (parsed.To.Value + 1 < parsed.Length.Value)
                    return null;
                return parsed.Length;
            }
            return null;
        }
        return e.ContentLength is > 0 ? e.ContentLength : null;
    }

    /// <summary>Douyin/TikTok MSE adaptive fMP4 paths (<c>media-video-*</c> / <c>media-audio-*</c>).</summary>
    public static bool IsMseAdaptivePath(Uri url) => MediaUrlNormalizer.IsByteDanceMseTrack(url);

    public static bool IsMseVideoPath(Uri url) => MediaUrlNormalizer.IsByteDanceMseVideoTrack(url);

    public static bool IsMseAudioPath(Uri url) =>
        url.AbsolutePath.Contains("/media-audio-", StringComparison.OrdinalIgnoreCase);

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

    public static bool IsStrongVodHost(Uri url) =>
        url.Host.Contains("douyinvod", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("zjcdn", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("bytecdn", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("byteicdn", StringComparison.OrdinalIgnoreCase) ||
        (url.Host.Contains("douyincdn", StringComparison.OrdinalIgnoreCase) &&
         !IsLivePullHost(url));

    /// <summary>Effect/overlay/live-pull/static hosts are not downloadable progressive VOD.</summary>
    public static bool IsNonDownloadableHost(Uri url)
    {
        if (IsMusicPath(url))
            return false;
        return url.Host.Contains("effect", StringComparison.OrdinalIgnoreCase) ||
               url.Host.Contains("byteeffect", StringComparison.OrdinalIgnoreCase) ||
               url.Host.Contains("lf3-effectcdn", StringComparison.OrdinalIgnoreCase) ||
               url.Host.Contains("lf3-social", StringComparison.OrdinalIgnoreCase) ||
               url.Host.Contains("douyinstatic", StringComparison.OrdinalIgnoreCase) ||
               url.Host.Contains("live.douyin", StringComparison.OrdinalIgnoreCase) ||
               url.Host.Contains("www-hj.douyin", StringComparison.OrdinalIgnoreCase) ||
               IsLivePullHost(url);
    }

    public static bool IsLivePullHost(Uri url)
    {
        if (url.Host.StartsWith("pull-", StringComparison.OrdinalIgnoreCase) ||
            url.Host.Contains("pull-", StringComparison.OrdinalIgnoreCase) ||
            url.AbsolutePath.Contains("/third/stream-", StringComparison.OrdinalIgnoreCase) ||
            url.AbsolutePath.Contains("/media/stream-", StringComparison.OrdinalIgnoreCase))
            return true;
        var full = url.AbsoluteUri;
        return full.Contains(".flv", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("/flv/", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("mime_type=video_flv", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("media_type=video_flv", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("pull-flv", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("pull-hls", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTinyMseCrumb(Uri url, long? contentLength)
    {
        if (contentLength is null or <= 0 || !DouyinIdentity.IsMediaHost(url))
            return false;
        var audio = IsMusicPath(url) ||
                    url.AbsoluteUri.Contains("mime_type=audio", StringComparison.OrdinalIgnoreCase);
        var min = audio ? MediaResourceSizeFilter.MinStrongMimeBytes : MediaResourceSizeFilter.MinDisplayBytes;
        return contentLength < min;
    }

    public static MediaTrackKind InferKind(Uri url, string? mime)
    {
        var path = url.AbsolutePath;
        var full = url.AbsoluteUri;

        // Adaptive fMP4 MSE tracks — never label as Combined / progressive muxed.
        if (path.Contains("/media-audio-", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/ies-music/", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("mime_type=audio", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("/audio/tos/", StringComparison.OrdinalIgnoreCase) ||
            mime?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
            return MediaTrackKind.Audio;

        if (path.Contains("/media-video-", StringComparison.OrdinalIgnoreCase))
            return MediaTrackKind.Video;

        // Muxed progressive objects (video/tos without media-video).
        if (full.Contains("mime_type=video", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("/video/tos/", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return MediaTrackKind.Combined;

        if (mime?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
            return MediaTrackKind.Video;

        if (IsPlayGateway(url) || LooksLikePlay(url) || DouyinIdentity.IsMediaHost(url))
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

        // Soft identity bind: null → real aweme id for the same work must NOT wipe
        // already-captured browser/CDN candidates (recommend feed resolves id late).
        var hardIdChange = contentId is not null &&
                           CurrentContentId is not null &&
                           !string.Equals(CurrentContentId, contentId, StringComparison.Ordinal);
        var hardModeChange = mode != DouyinContentMode.Unknown &&
                             CurrentMode != DouyinContentMode.Unknown &&
                             mode != CurrentMode;

        if (!hardIdChange && !hardModeChange)
        {
            if (contentId is not null)
                CurrentContentId = contentId;
            if (mode != DouyinContentMode.Unknown)
                CurrentMode = mode;
            return;
        }

        if (hardIdChange)
            Caption = null;
        CurrentContentId = contentId ?? CurrentContentId;
        if (mode != DouyinContentMode.Unknown)
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
