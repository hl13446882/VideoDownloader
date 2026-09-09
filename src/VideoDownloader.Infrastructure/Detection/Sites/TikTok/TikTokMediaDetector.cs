using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Detection.Sites.TikTok;

/// <summary>Exclusive TikTok detector — no shared Douyin/Generic detection methods.</summary>
public sealed class TikTokMediaDetector : IExclusiveSiteMediaDetector
{
    private static readonly Regex VideoIdPath = new(
        @"/@[^/]+/video/(?<id>\d{10,})|/video/(?<id>\d{10,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<TikTokMediaDetector> _logger;
    private readonly object _gate = new();
    private Guid _sessionId;
    private Uri? _pageUrl;
    private string? _contentId;
    private string? _caption;
    private RequestContext _context = RequestContext.CreateEmpty();
    private readonly List<MediaTrack> _videos = [];
    private readonly List<MediaTrack> _audios = [];
    private readonly List<AlbumImageItem> _images = [];
    private bool _album;
    private bool _failed;
    private string? _failureReason;

    public TikTokMediaDetector(ILogger<TikTokMediaDetector> logger) => _logger = logger;

    public string Name => "TikTokMediaDetector";
    public SiteKind Site => SiteKind.TikTok;
    public bool Failed => _failed;
    public string? FailureReason => _failureReason;
    public event EventHandler<IReadOnlyList<MediaDescriptor>>? DescriptorsReady;

    public bool Matches(Uri pageUrl) =>
        pageUrl.Host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase);

    public void BeginSession(Uri pageUrl, Guid sessionId)
    {
        lock (_gate)
        {
            ClearUnlocked();
            _sessionId = sessionId;
            _pageUrl = pageUrl;
            _contentId = ExtractVideoId(pageUrl);
            _logger.LogInformation(
                "[DetectionRouter] Site=TikTok Detector={Detector} Exclusive=true GenericPipeline=Bypassed page={Path}",
                Name, pageUrl.AbsolutePath);
        }
    }

    public void Clear()
    {
        lock (_gate) ClearUnlocked();
    }

    public Task ProcessNetworkAsync(NormalizedNetworkEvent e, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_pageUrl is null || _failed) return Task.CompletedTask;
            if (e.StatusCode is not (200 or 206 or null)) return Task.CompletedTask;

            if (_album)
            {
                if (IsAlbumImage(e.Url, e.MimeType) && !IsExcludedImage(e.Url))
                    AddImage(e.Url, e.RequestContext);
                else if (IsAudio(e))
                    Upsert(_audios, MakeTrack(e, MediaTrackKind.Audio));
                return Task.CompletedTask;
            }

            if (IsTinyCrumb(e) && !IsBrowserPlay(e)) return Task.CompletedTask;
            if (!IsTikTokMediaHost(e.Url) && !LooksLikePlay(e.Url) && !IsBrowserPlay(e))
                return Task.CompletedTask;

            var otherId = ExtractIdFromQuery(e.Url);
            if (_contentId is not null && otherId is not null &&
                !string.Equals(_contentId, otherId, StringComparison.Ordinal))
                return Task.CompletedTask;

            var kind = IsAudio(e) ? MediaTrackKind.Audio :
                IsBrowserPlay(e) || LooksLikePlay(e.Url) ? MediaTrackKind.Combined : MediaTrackKind.Video;
            if (kind == MediaTrackKind.Audio) Upsert(_audios, MakeTrack(e, kind));
            else Upsert(_videos, MakeTrack(e, kind));
        }
        return Task.CompletedTask;
    }

    public Task ProcessPageObservationAsync(
        Uri pageUrl, string? pageTitle, string? pageScriptJson, RequestContext context, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_pageUrl is null) BeginSession(pageUrl, _sessionId == Guid.Empty ? Guid.NewGuid() : _sessionId);
            _pageUrl = pageUrl;
            _context = Enrich(context, pageUrl);
            var id = ExtractVideoId(pageUrl) ?? ReadIdentityId(pageScriptJson);
            if (id is not null && !string.Equals(id, _contentId, StringComparison.Ordinal))
            {
                _videos.Clear();
                _audios.Clear();
                _images.Clear();
                _album = false;
                _contentId = id;
            }
            _contentId ??= id;
            if (!string.IsNullOrWhiteSpace(pageTitle)) _caption ??= pageTitle.Trim();
            ApplyJson(pageScriptJson);
        }
        return Task.CompletedTask;
    }

    public Task CompleteAsync(CancellationToken ct)
    {
        MediaDescriptor? d;
        lock (_gate)
        {
            d = Build();
            if (d is null)
            {
                _failed = true;
                _failureReason = _album ? "tiktok_album_no_images" : "tiktok_no_media";
                _logger.LogWarning("TikTokMediaDetector Failed reason={Reason} (no Generic fallback)", _failureReason);
                return Task.CompletedTask;
            }
        }
        DescriptorsReady?.Invoke(this, [d]);
        return Task.CompletedTask;
    }

    private MediaDescriptor? Build()
    {
        if (_pageUrl is null) return null;
        if (_album)
        {
            if (_images.Count == 0) return null;
            return new MediaDescriptor(SiteIds.TikTok, _pageUrl, _contentId, MediaContentType.Album,
                null, Best(_audios), _images.OrderBy(i => i.Index).ToArray(), _context, 0.9, _caption)
            { SessionId = _sessionId };
        }
        var video = Best(_videos);
        if (video is null) return null;
        var audio = video.Kind == MediaTrackKind.Combined ? null : Best(_audios);
        return new MediaDescriptor(SiteIds.TikTok, _pageUrl, _contentId, MediaContentType.Video,
            video, audio, [], _context, video.BrowserObserved ? 0.95 : 0.75, _caption)
        { SessionId = _sessionId };
    }

    private void ApplyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("caption", out var c) && c.ValueKind == JsonValueKind.String)
                _caption = c.GetString()?.Trim() ?? _caption;
            if (root.TryGetProperty("album", out var a) && a.ValueKind is JsonValueKind.True)
                _album = true;
            if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
            {
                var list = new List<AlbumImageItem>();
                foreach (var item in images.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    if (!Uri.TryCreate(item.GetString(), UriKind.Absolute, out var url)) continue;
                    if (IsExcludedImage(url)) continue;
                    list.Add(new AlbumImageItem(list.Count, url, null, null, "jpeg", _context));
                }
                if (list.Count > 0)
                {
                    _album = true;
                    _images.Clear();
                    foreach (var img in list.DistinctBy(i => i.Url.GetLeftPart(UriPartial.Path), StringComparer.OrdinalIgnoreCase))
                        _images.Add(img with { Index = _images.Count });
                }
            }
            if (root.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in media.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    if (!Uri.TryCreate(item.GetString(), UriKind.Absolute, out var url)) continue;
                    if (IsExcludedImage(url) || IsAlbumImage(url, null)) continue;
                    var audio = url.AbsoluteUri.Contains("audio", StringComparison.OrdinalIgnoreCase);
                    Upsert(audio || _album ? _audios : _videos,
                        new MediaTrack(audio ? "audio" : "media",
                            audio || _album ? MediaTrackKind.Audio : MediaTrackKind.Combined,
                            url, null, "mp4", null, null, _context)
                        {
                            IsValidated = true,
                            Evidence = MediaEvidence.DomObserved,
                            ContentIdentity = _contentId is null ? null : "id:" + _contentId
                        });
                }
            }
        }
        catch (JsonException) { }
    }

    private void ClearUnlocked()
    {
        _sessionId = Guid.Empty;
        _pageUrl = null;
        _contentId = null;
        _caption = null;
        _context = RequestContext.CreateEmpty();
        _videos.Clear();
        _audios.Clear();
        _images.Clear();
        _album = false;
        _failed = false;
        _failureReason = null;
    }

    private void AddImage(Uri url, RequestContext ctx)
    {
        if (_images.Any(i => string.Equals(i.Url.GetLeftPart(UriPartial.Path), url.GetLeftPart(UriPartial.Path), StringComparison.OrdinalIgnoreCase)))
            return;
        _images.Add(new AlbumImageItem(_images.Count, url, null, null, "jpeg", Enrich(ctx, _pageUrl!)));
    }

    private MediaTrack MakeTrack(NormalizedNetworkEvent e, MediaTrackKind kind) =>
        new(kind == MediaTrackKind.Audio ? "audio" : "video", kind, e.Url, null, "mp4", null,
            IsTinyCrumb(e) ? null : e.ContentLength, Enrich(e.RequestContext, _pageUrl!, e.RequestHeaders, e.Url))
        {
            BrowserObserved = IsBrowserPlay(e),
            IsValidated = IsBrowserPlay(e),
            Evidence = IsBrowserPlay(e) ? MediaEvidence.BrowserObserved : MediaEvidence.Heuristic,
            ContentIdentity = _contentId is null ? null : "id:" + _contentId
        };

    private static void Upsert(List<MediaTrack> list, MediaTrack track)
    {
        var key = track.SourceUrl.GetLeftPart(UriPartial.Path);
        var idx = list.FindIndex(t => string.Equals(t.SourceUrl.GetLeftPart(UriPartial.Path), key, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            if ((track.ContentLength ?? 0) >= (list[idx].ContentLength ?? 0) || (track.BrowserObserved && !list[idx].BrowserObserved))
                list[idx] = track;
            return;
        }
        list.Add(track);
    }

    private static MediaTrack? Best(IEnumerable<MediaTrack> tracks) =>
        tracks.OrderByDescending(t => t.BrowserObserved).ThenByDescending(t => t.ContentLength ?? 0).FirstOrDefault();

    private static RequestContext Enrich(
        RequestContext ctx,
        Uri page,
        IReadOnlyDictionary<string, string>? requestHeaders = null,
        Uri? mediaUrl = null)
    {
        var referer = string.IsNullOrWhiteSpace(ctx.Referer) ? page.AbsoluteUri : ctx.Referer;
        var origin = string.IsNullOrWhiteSpace(ctx.Origin) ? page.GetLeftPart(UriPartial.Authority) : ctx.Origin;
        var cookies = ctx.Cookies;
        if (cookies.Count == 0 && requestHeaders is not null && mediaUrl is not null)
        {
            if (requestHeaders.TryGetValue("cookie", out var header) ||
                requestHeaders.TryGetValue("Cookie", out header))
            {
                cookies = ParseCookieHeader(header, mediaUrl);
            }
        }

        return ctx with { Referer = referer, Origin = origin, Cookies = cookies };
    }

    private static IReadOnlyList<BrowserCookie> ParseCookieHeader(string? header, Uri url)
    {
        if (string.IsNullOrWhiteSpace(header)) return [];
        var list = new List<BrowserCookie>();
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;
            list.Add(new BrowserCookie(part[..idx].Trim(), part[(idx + 1)..].Trim(), url.Host, "/", null, url.Scheme == "https", false));
        }
        return list;
    }

    private static string? ExtractVideoId(Uri page)
    {
        var m = VideoIdPath.Match(page.AbsolutePath);
        return m.Success ? m.Groups["id"].Value : ExtractIdFromQuery(page);
    }

    private static string? ExtractIdFromQuery(Uri url)
    {
        foreach (var key in new[] { "item_id=", "aweme_id=", "video_id=" })
        {
            var idx = url.Query.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var start = idx + key.Length;
            var end = url.Query.IndexOf('&', start);
            var raw = end < 0 ? url.Query[start..] : url.Query[start..end];
            if (Regex.IsMatch(raw, @"^\d{10,}$")) return raw;
        }
        return null;
    }

    private static string? ReadIdentityId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("identity", out var id) && id.ValueKind == JsonValueKind.String)
            {
                var m = Regex.Match(id.GetString() ?? "", @"(?<id>\d{10,})");
                return m.Success ? m.Groups["id"].Value : null;
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static bool IsTikTokMediaHost(Uri url) =>
        url.Host.Contains("tiktok", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("tiktokcdn", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("byteoversea", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("ibyteimg", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikePlay(Uri url)
    {
        var u = url.AbsoluteUri;
        return u.Contains("/media-", StringComparison.OrdinalIgnoreCase) ||
               u.Contains("playAddr", StringComparison.OrdinalIgnoreCase) ||
               u.Contains("/play/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBrowserPlay(NormalizedNetworkEvent e) =>
        e.StatusCode is 200 or 206 &&
        (string.Equals(e.ResourceType, "Media", StringComparison.OrdinalIgnoreCase) ||
         e.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true ||
         e.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsAudio(NormalizedNetworkEvent e) =>
        e.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ||
        e.Url.AbsoluteUri.Contains("mime_type=audio", StringComparison.OrdinalIgnoreCase);

    private static bool IsTinyCrumb(NormalizedNetworkEvent e)
    {
        if (e.ContentLength is null or <= 0 || !IsTikTokMediaHost(e.Url)) return false;
        var min = IsAudio(e) ? MediaResourceSizeFilter.MinStrongMimeBytes : MediaResourceSizeFilter.MinDisplayBytes;
        return e.ContentLength < min;
    }

    private static bool IsAlbumImage(Uri url, string? mime) =>
        mime?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ||
        Regex.IsMatch(url.AbsolutePath, @"\.(?:jpg|jpeg|png|webp)(?:$|\?)", RegexOptions.IgnoreCase) ||
        url.Host.Contains("tiktokcdn", StringComparison.OrdinalIgnoreCase) &&
        url.AbsolutePath.Contains("image", StringComparison.OrdinalIgnoreCase);

    private static bool IsExcludedImage(Uri url) =>
        Regex.IsMatch(url.AbsoluteUri, @"avatar|emoji|logo|icon|badge|sprite", RegexOptions.IgnoreCase);
}
