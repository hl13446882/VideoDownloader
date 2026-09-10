using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Detection.Sites.YouTube;

/// <summary>Exclusive YouTube detector — owns SABR rejection and videoplayback admission.</summary>
public sealed class YouTubeMediaDetector : IExclusiveSiteMediaDetector
{
    private readonly ILogger<YouTubeMediaDetector> _logger;
    private readonly YouTubeYtDlpExtractor _ytdlp;
    private readonly object _gate = new();
    private Guid _sessionId;
    private Uri? _pageUrl;
    private string? _contentId;
    private string? _caption;
    private RequestContext _context = RequestContext.CreateEmpty();
    private readonly List<MediaTrack> _tracks = [];
    private readonly List<MediaVariant> _formats = [];
    private bool _failed;
    private string? _failureReason;
    private bool _externalAttempted;

    public YouTubeMediaDetector(
        ILogger<YouTubeMediaDetector> logger,
        YouTubeYtDlpExtractor ytdlp)
    {
        _logger = logger;
        _ytdlp = ytdlp;
    }

    public string Name => "YouTubeMediaDetector";
    public SiteKind Site => SiteKind.YouTube;
    public bool Failed => _failed;
    public string? FailureReason => _failureReason;
    public event EventHandler<IReadOnlyList<MediaDescriptor>>? DescriptorsReady;

    public bool Matches(Uri pageUrl) =>
        pageUrl.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        pageUrl.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
        pageUrl.Host.Contains("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase);

    public void BeginSession(Uri pageUrl, Guid sessionId)
    {
        lock (_gate)
        {
            ClearUnlocked();
            _sessionId = sessionId;
            _pageUrl = pageUrl;
            _contentId = ExtractVideoId(pageUrl);
            _logger.LogInformation(
                "[DetectionRouter] Site=YouTube Detector={Detector} Exclusive=true GenericPipeline=Bypassed page={Path}",
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
            var url = e.Url.AbsoluteUri;
            if (url.Contains("sabr=1", StringComparison.OrdinalIgnoreCase) &&
                !url.Contains("mime=video", StringComparison.OrdinalIgnoreCase) &&
                !url.Contains("mime=audio", StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;
            if (e.StatusCode is not (200 or 206 or null)) return Task.CompletedTask;

            // Only googlevideo / videoplayback — never admit youtube.com page documents as media.
            var isPlayback = e.Url.Host.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
                             e.Url.AbsolutePath.Contains("/videoplayback", StringComparison.OrdinalIgnoreCase);
            if (!isPlayback) return Task.CompletedTask;

            var hasMime = url.Contains("mime=video", StringComparison.OrdinalIgnoreCase) ||
                          url.Contains("mime=audio", StringComparison.OrdinalIgnoreCase);
            var hasItag = url.Contains("itag=", StringComparison.OrdinalIgnoreCase);
            if (!IsBrowserPlay(e) && !hasMime && !hasItag && e.ContentLength is < 64 * 1024)
                return Task.CompletedTask;

            var audio = url.Contains("mime=audio", StringComparison.OrdinalIgnoreCase);
            Upsert(new MediaTrack(
                audio ? "audio" : "video",
                audio ? MediaTrackKind.Audio : MediaTrackKind.Video,
                e.Url, null, "mp4", null, e.ContentLength, Enrich(e.RequestContext))
            {
                BrowserObserved = IsBrowserPlay(e),
                IsValidated = IsBrowserPlay(e),
                Evidence = IsBrowserPlay(e) ? MediaEvidence.BrowserObserved : MediaEvidence.Heuristic,
                ContentIdentity = _contentId is null ? null : "id:" + _contentId
            });
        }
        return Task.CompletedTask;
    }

    public async Task ProcessPageObservationAsync(
        Uri pageUrl, string? pageTitle, string? pageScriptJson, RequestContext context, CancellationToken ct)
    {
        string? contentId;
        Uri resolveUrl;
        RequestContext enriched;
        lock (_gate)
        {
            if (_pageUrl is null) BeginSession(pageUrl, _sessionId == Guid.Empty ? Guid.NewGuid() : _sessionId);
            _pageUrl = pageUrl;
            _context = Enrich(context);
            contentId = ExtractVideoId(pageUrl) ?? ReadId(pageScriptJson);
            if (!string.IsNullOrWhiteSpace(contentId) &&
                !string.Equals(_contentId, contentId, StringComparison.OrdinalIgnoreCase))
            {
                _contentId = contentId;
                _formats.Clear();
                _tracks.Clear();
                _failed = false;
                _failureReason = null;
                _externalAttempted = false;
            }
            else
                _contentId ??= contentId;

            if (!string.IsNullOrWhiteSpace(pageTitle)) _caption ??= pageTitle.Trim();
            ApplyMediaJson(pageScriptJson);
            resolveUrl = _pageUrl;
            enriched = _context;
        }

        // Home/feed without concrete v= — wait; do not burn the external resolve slot.
        if (string.IsNullOrWhiteSpace(contentId))
            return;

        bool alreadyAttempted;
        lock (_gate) alreadyAttempted = _externalAttempted;
        if (!_ytdlp.IsAvailable || alreadyAttempted)
            return;

        var hasCookies = enriched.Cookies.Count > 0;
        Diagnostics.HangProbe.Mark("youtube.ytdlp.begin", $"{resolveUrl.AbsoluteUri} cookies={enriched.Cookies.Count}");
        try
        {
            var videos = await _ytdlp.ResolveAsync(resolveUrl, enriched, ct);
            Diagnostics.HangProbe.Mark("youtube.ytdlp.end", $"count={videos.Count}");
            lock (_gate)
            {
                // REDUNDANT(pending-delete after confirm): only latch after cookied attempt or success,
                // which re-ran yt-dlp on every grace probe when cookies were empty.
                // if (videos.Count > 0 || hasCookies) _externalAttempted = true;
                _externalAttempted = true;

                foreach (var v in videos.Where(v =>
                             string.IsNullOrWhiteSpace(_contentId) ||
                             string.Equals(v.SiteContentId, _contentId, StringComparison.OrdinalIgnoreCase) ||
                             string.IsNullOrWhiteSpace(v.SiteContentId)))
                {
                    if (string.IsNullOrWhiteSpace(_caption) && !string.IsNullOrWhiteSpace(v.DisplayTitle))
                        _caption = v.DisplayTitle;
                    foreach (var variant in v.Variants)
                    {
                        if (variant.Tracks.Any(t => t.Kind == MediaTrackKind.Combined) &&
                            variant.Tracks.Any(t => t.Kind == MediaTrackKind.Audio))
                            continue;
                        _formats.Add(variant with
                        {
                            ContentIdentity = _contentId is null ? variant.ContentIdentity : "id:" + _contentId,
                            RecoveryPageUrl = _pageUrl
                        });
                    }
                    foreach (var track in v.Variants.SelectMany(x => x.Tracks))
                        Upsert(track with
                        {
                            ContentIdentity = _contentId is null ? track.ContentIdentity : "id:" + _contentId
                        });
                }
            }
        }
        catch (Exception ex)
        {
            Diagnostics.HangProbe.Mark("youtube.ytdlp.fail", ex.GetType().Name + " " + ex.Message);
            _logger.LogInformation(ex, "YouTube exclusive yt-dlp resolve failed (no Generic fallback)");
            lock (_gate)
            {
                // REDUNDANT(pending-delete after confirm): if (hasCookies) _externalAttempted = true;
                _externalAttempted = true;
            }
        }
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
                _failureReason = "youtube_no_media";
                _logger.LogWarning("YouTubeMediaDetector Failed reason={Reason} (no Generic fallback)", _failureReason);
                return Task.CompletedTask;
            }
        }
        DescriptorsReady?.Invoke(this, [d]);
        return Task.CompletedTask;
    }

    private MediaDescriptor? Build()
    {
        if (_pageUrl is null) return null;

        if (_formats.Count > 0)
        {
            var best = _formats
                .Where(v => v.Tracks.Any(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined))
                .OrderByDescending(v => v.Height ?? 0)
                .ThenByDescending(v => v.TotalContentLength ?? v.Bandwidth ?? 0)
                .FirstOrDefault();
            return new MediaDescriptor(SiteIds.YouTube, _pageUrl, _contentId, MediaContentType.Video,
                best?.Tracks.FirstOrDefault(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined),
                best?.Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Audio),
                [], _context, 0.95, _caption)
            {
                SessionId = _sessionId,
                Formats = _formats.ToArray()
            };
        }

        var video = _tracks
            .Where(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined)
            .OrderByDescending(t => t.BrowserObserved)
            .ThenByDescending(t => t.ContentLength ?? 0)
            .FirstOrDefault();
        var audio = _tracks
            .Where(t => t.Kind == MediaTrackKind.Audio)
            .OrderByDescending(t => t.ContentLength ?? 0)
            .FirstOrDefault();
        if (video is null && audio is null) return null;
        if (video is null)
            return new MediaDescriptor(SiteIds.YouTube, _pageUrl, _contentId, MediaContentType.Audio,
                null, audio, [], _context, 0.7, _caption) { SessionId = _sessionId };
        return new MediaDescriptor(SiteIds.YouTube, _pageUrl, _contentId, MediaContentType.Video,
            video.Kind == MediaTrackKind.Combined ? video : video,
            video.Kind == MediaTrackKind.Combined ? null : audio,
            [], _context, video.BrowserObserved ? 0.95 : 0.8, _caption) { SessionId = _sessionId };
    }

    private void ApplyMediaJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("caption", out var c) && c.ValueKind == JsonValueKind.String)
                _caption = c.GetString()?.Trim() ?? _caption;
            if (doc.RootElement.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in media.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    if (!Uri.TryCreate(item.GetString(), UriKind.Absolute, out var url)) continue;
                    if (!url.Host.Contains("googlevideo", StringComparison.OrdinalIgnoreCase) &&
                        !url.AbsolutePath.Contains("/videoplayback", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var audio = url.AbsoluteUri.Contains("mime=audio", StringComparison.OrdinalIgnoreCase);
                    Upsert(new MediaTrack(audio ? "audio" : "video",
                        audio ? MediaTrackKind.Audio : MediaTrackKind.Video,
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

    private void Upsert(MediaTrack track)
    {
        var key = MediaUrlNormalizer.Normalize(track.SourceUrl);
        var idx = _tracks.FindIndex(t =>
            string.Equals(MediaUrlNormalizer.Normalize(t.SourceUrl), key, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            if ((track.ContentLength ?? 0) >= (_tracks[idx].ContentLength ?? 0) ||
                (track.BrowserObserved && !_tracks[idx].BrowserObserved))
                _tracks[idx] = track;
            return;
        }
        _tracks.Add(track);
    }

    private void ClearUnlocked()
    {
        _sessionId = Guid.Empty;
        _pageUrl = null;
        _contentId = null;
        _caption = null;
        _context = RequestContext.CreateEmpty();
        _tracks.Clear();
        _formats.Clear();
        _failed = false;
        _failureReason = null;
        _externalAttempted = false;
    }

    private RequestContext Enrich(RequestContext ctx)
    {
        if (_pageUrl is null) return ctx;
        return ctx with
        {
            Referer = string.IsNullOrWhiteSpace(ctx.Referer) ? _pageUrl.AbsoluteUri : ctx.Referer,
            Origin = string.IsNullOrWhiteSpace(ctx.Origin) ? "https://www.youtube.com" : ctx.Origin
        };
    }

    private static bool IsBrowserPlay(NormalizedNetworkEvent e) =>
        e.StatusCode is 200 or 206 &&
        (string.Equals(e.ResourceType, "Media", StringComparison.OrdinalIgnoreCase) ||
         e.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true ||
         e.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true);

    private static string? ExtractVideoId(Uri page)
    {
        if (page.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
            return page.AbsolutePath.Trim('/').Split('/').FirstOrDefault();
        var v = Regex.Match(page.Query, @"[?&]v=([^&]+)", RegexOptions.IgnoreCase);
        if (v.Success) return v.Groups[1].Value;
        var shorts = Regex.Match(page.AbsolutePath, @"/shorts/([^/?#]+)", RegexOptions.IgnoreCase);
        return shorts.Success ? shorts.Groups[1].Value : null;
    }

    private static string? ReadId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("identity", out var id) && id.ValueKind == JsonValueKind.String)
            {
                var s = id.GetString() ?? "";
                var m = Regex.Match(s, @"content:(?:youtube:)?(?<id>[^:/]+)", RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups["id"].Value;
                m = Regex.Match(s, @"[?&]v=(?<id>[^&]+)");
                if (m.Success) return m.Groups["id"].Value;
            }
        }
        catch (JsonException) { }
        return null;
    }
}
