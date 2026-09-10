using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Detection.Sites.Bilibili;

/// <summary>Exclusive Bilibili detector — owns playurl/upos/DASH admission and playinfo.</summary>
public sealed class BilibiliMediaDetector : IExclusiveSiteMediaDetector
{
    private readonly ILogger<BilibiliMediaDetector> _logger;
    private readonly BilibiliYtDlpExtractor _ytdlp;
    private readonly IProbeMethodStats _probeStats;
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

    public BilibiliMediaDetector(
        ILogger<BilibiliMediaDetector> logger,
        BilibiliYtDlpExtractor ytdlp,
        IProbeMethodStats probeStats)
    {
        _logger = logger;
        _ytdlp = ytdlp;
        _probeStats = probeStats;
    }

    public string Name => "BilibiliMediaDetector";
    public SiteKind Site => SiteKind.Bilibili;
    public bool Failed => _failed;
    public string? FailureReason => _failureReason;
    public event EventHandler<IReadOnlyList<MediaDescriptor>>? DescriptorsReady;

    public bool Matches(Uri pageUrl) =>
        pageUrl.Host.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
        pageUrl.Host.Contains("b23.tv", StringComparison.OrdinalIgnoreCase);

    public void BeginSession(Uri pageUrl, Guid sessionId)
    {
        lock (_gate)
        {
            ClearUnlocked();
            _sessionId = sessionId;
            _pageUrl = pageUrl;
            _contentId = ExtractContentId(pageUrl);
            _logger.LogInformation(
                "[DetectionRouter] Site=Bilibili Detector={Detector} Exclusive=true GenericPipeline=Bypassed page={Path}",
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
            // Do not drop late CDN after a failed Complete — late-retry must still accept media.
            if (_pageUrl is null) return Task.CompletedTask;
            if (e.StatusCode is not (200 or 206 or null)) return Task.CompletedTask;

            var url = e.Url;
            var full = url.AbsoluteUri;
            var isBili = IsBiliHost(url);
            var isDash = url.AbsolutePath.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) ||
                         url.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) ||
                         full.Contains("playurl", StringComparison.OrdinalIgnoreCase) ||
                         full.Contains("/upos", StringComparison.OrdinalIgnoreCase);
            if (!isBili && !isDash && !IsBrowserPlay(e)) return Task.CompletedTask;

            var audio = e.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ||
                        full.Contains("audio", StringComparison.OrdinalIgnoreCase) &&
                        url.AbsolutePath.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase);
            Upsert(new MediaTrack(
                audio ? "audio" : "video",
                audio ? MediaTrackKind.Audio : MediaTrackKind.Video,
                url,
                null,
                url.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) ? "dash" : "mp4",
                null,
                e.ContentLength,
                Enrich(e.RequestContext))
            {
                BrowserObserved = IsBrowserPlay(e),
                IsValidated = IsBrowserPlay(e),
                Evidence = IsBrowserPlay(e) ? MediaEvidence.BrowserObserved : MediaEvidence.Heuristic,
                ContentIdentity = _contentId is null ? null : "id:" + _contentId,
                ProbeMethod = ProbeMethods.NetworkPlayurl
            });
            if (_failed)
            {
                _failed = false;
                _failureReason = null;
            }
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
            contentId = ExtractContentId(pageUrl) ?? ReadId(pageScriptJson);
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
            ApplyJson(pageScriptJson);
            resolveUrl = _pageUrl;
            enriched = _context;
        }

        if (string.IsNullOrWhiteSpace(contentId))
            return;

        var hasCookies = enriched.Cookies.Count > 0;
        lock (_gate)
        {
            if (!_ytdlp.IsAvailable || _externalAttempted)
                return;
            _externalAttempted = true;
        }

        try
        {
            var videos = await _ytdlp.ResolveAsync(resolveUrl, enriched, ct);
            lock (_gate)
            {
                if (videos.Count == 0 && !hasCookies)
                    _externalAttempted = false;

                foreach (var v in videos)
                {
                    if (_contentId is not null &&
                        !string.IsNullOrWhiteSpace(v.SiteContentId) &&
                        !string.Equals(v.SiteContentId, _contentId, StringComparison.OrdinalIgnoreCase))
                        continue;
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

                if (videos.Count > 0)
                {
                    _failed = false;
                    _failureReason = null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Bilibili exclusive external resolve failed (no Generic fallback)");
            lock (_gate)
            {
                if (!hasCookies)
                    _externalAttempted = false;
            }
        }
    }

    public Task CompleteAsync(CancellationToken ct)
    {
        MediaDescriptor? d;
        string? winMethod = null;
        lock (_gate)
        {
            d = Build(out winMethod);
            if (d is null)
            {
                _failed = true;
                _failureReason = "bilibili_no_media";
                _logger.LogWarning("BilibiliMediaDetector Failed reason={Reason} (no Generic fallback)", _failureReason);
                if (_externalAttempted)
                    _probeStats.Record(SiteIds.Bilibili, ProbeMethods.YtDlp, false);
                if (_tracks.Count > 0)
                    _probeStats.Record(SiteIds.Bilibili, ProbeMethods.NetworkPlayurl, false);
                return Task.CompletedTask;
            }

            _failed = false;
            _failureReason = null;
            if (!string.IsNullOrWhiteSpace(winMethod))
                _probeStats.Record(SiteIds.Bilibili, winMethod!, true);
        }
        DescriptorsReady?.Invoke(this, [d]);
        return Task.CompletedTask;
    }

    private MediaDescriptor? Build() => Build(out _);

    private MediaDescriptor? Build(out string? winningMethod)
    {
        winningMethod = null;
        if (_pageUrl is null) return null;
        if (_formats.Count > 0)
        {
            var best = _formats
                .Where(v => v.Tracks.Any(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined))
                .OrderByDescending(v => v.Height ?? 0)
                .ThenByDescending(v => v.TotalContentLength ?? v.Bandwidth ?? 0)
                .FirstOrDefault();
            // Direct playurl_api not implemented yet — yt-dlp path still hits wbi playurl under the hood.
            winningMethod = ProbeMethods.YtDlp;
            return new MediaDescriptor(SiteIds.Bilibili, _pageUrl, _contentId, MediaContentType.Video,
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
        winningMethod = ProbeMethods.NetworkPlayurl;
        if (video is null)
            return new MediaDescriptor(SiteIds.Bilibili, _pageUrl, _contentId, MediaContentType.Audio,
                null, audio, [], _context, 0.7, _caption) { SessionId = _sessionId };
        return new MediaDescriptor(SiteIds.Bilibili, _pageUrl, _contentId, MediaContentType.Video,
            video, video.Kind == MediaTrackKind.Combined ? null : audio, [], _context,
            video.BrowserObserved ? 0.95 : 0.8, _caption) { SessionId = _sessionId };
    }

    private void ApplyJson(string? json)
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
                    if (!IsBiliHost(url) && !url.AbsoluteUri.Contains("playurl", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var audio = url.AbsolutePath.Contains("audio", StringComparison.OrdinalIgnoreCase);
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

    private RequestContext Enrich(RequestContext ctx) =>
        ctx with
        {
            Referer = string.IsNullOrWhiteSpace(ctx.Referer) ? "https://www.bilibili.com/" : ctx.Referer,
            Origin = string.IsNullOrWhiteSpace(ctx.Origin) ? "https://www.bilibili.com" : ctx.Origin
        };

    private static bool IsBiliHost(Uri url) =>
        url.Host.Contains("bilivideo", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("hdslb.com", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("upos", StringComparison.OrdinalIgnoreCase);

    private static bool IsBrowserPlay(NormalizedNetworkEvent e) =>
        e.StatusCode is 200 or 206 &&
        (string.Equals(e.ResourceType, "Media", StringComparison.OrdinalIgnoreCase) ||
         e.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true ||
         e.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true);

    private static string? ExtractContentId(Uri page)
    {
        var bv = Regex.Match(page.AbsolutePath, @"/video/(BV[\w]+)", RegexOptions.IgnoreCase);
        if (bv.Success) return bv.Groups[1].Value.ToUpperInvariant();
        var av = Regex.Match(page.AbsolutePath, @"/video/av(\d+)", RegexOptions.IgnoreCase);
        return av.Success ? $"av{av.Groups[1].Value}" : null;
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
                var m = Regex.Match(s, @"(BV[\w]+|av\d+)", RegexOptions.IgnoreCase);
                return m.Success ? m.Value : null;
            }
        }
        catch (JsonException) { }
        return null;
    }
}
