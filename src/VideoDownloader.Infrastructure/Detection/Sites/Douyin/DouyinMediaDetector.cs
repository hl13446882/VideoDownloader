using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Detection.Sites.Douyin;

/// <summary>
/// Exclusive Douyin detector. Owns Video and Album modes; never falls back to Generic/Unified.
/// </summary>
public sealed class DouyinMediaDetector : IExclusiveSiteMediaDetector
{
    private readonly ILogger<DouyinMediaDetector> _logger;
    private readonly DouyinDetectionSession _session = new();
    private readonly object _gate = new();
    private bool _failed;
    private string? _failureReason;

    public DouyinMediaDetector(ILogger<DouyinMediaDetector> logger)
    {
        _logger = logger;
    }

    public string Name => "DouyinMediaDetector";
    public SiteKind Site => SiteKind.Douyin;
    public bool Failed => _failed;
    public string? FailureReason => _failureReason;

    public event EventHandler<IReadOnlyList<MediaDescriptor>>? DescriptorsReady;

    public bool Matches(Uri pageUrl) => DouyinIdentity.IsPageHost(pageUrl);

    public void BeginSession(Uri pageUrl, Guid sessionId)
    {
        lock (_gate)
        {
            _session.Reset();
            _session.SessionId = sessionId;
            _session.PageUrl = pageUrl;
            _failed = false;
            _failureReason = null;
            var id = DouyinIdentity.ExtractAwemeId(pageUrl);
            _session.SwitchContent(id, DouyinContentMode.Unknown);
            _logger.LogInformation(
                "[DetectionRouter] Site=Douyin Detector={Detector} Exclusive=true GenericPipeline=Bypassed page={Path}",
                Name, pageUrl.AbsolutePath);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _session.Reset();
            _failed = false;
            _failureReason = null;
        }
    }

    public Task ProcessNetworkAsync(NormalizedNetworkEvent networkEvent, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_session.PageUrl is null || _failed)
                return Task.CompletedTask;
            if (networkEvent.StatusCode is not (200 or 206 or null))
                return Task.CompletedTask;

            if (_session.CurrentMode == DouyinContentMode.Album)
            {
                TryAcceptAlbumNetworkImage(networkEvent);
                TryAcceptAlbumNetworkAudio(networkEvent);
                return Task.CompletedTask;
            }

            // Video / Unknown: collect playable media for current work only.
            if (DouyinPlayEvidence.IsTinyMseCrumb(networkEvent.Url, networkEvent.ContentLength) &&
                !DouyinPlayEvidence.IsBrowserPlay(networkEvent))
                return Task.CompletedTask;

            if (!DouyinIdentity.IsMediaHost(networkEvent.Url) &&
                !DouyinPlayEvidence.LooksLikePlay(networkEvent.Url) &&
                !DouyinPlayEvidence.IsBrowserPlay(networkEvent))
                return Task.CompletedTask;

            // Reject candidates that encode a different aweme id than the current work.
            var urlId = DouyinIdentity.ExtractIdFromQuery(networkEvent.Url) ??
                        ExtractIdFromUrlPath(networkEvent.Url);
            if (_session.CurrentContentId is not null &&
                urlId is not null &&
                !string.Equals(urlId, _session.CurrentContentId, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Douyin skip preload/other work media current={Current} other={Other} host={Host}",
                    _session.CurrentContentId, urlId, networkEvent.Url.Host);
                return Task.CompletedTask;
            }

            var kind = DouyinPlayEvidence.InferKind(networkEvent.Url, networkEvent.MimeType);
            var ctx = EnrichContext(networkEvent.RequestContext);
            var length = DouyinPlayEvidence.IsTinyMseCrumb(networkEvent.Url, networkEvent.ContentLength)
                ? null
                : networkEvent.ContentLength;
            var track = new MediaTrack(
                kind == MediaTrackKind.Audio ? "audio" : "video",
                kind is MediaTrackKind.Audio or MediaTrackKind.Video ? kind : MediaTrackKind.Combined,
                networkEvent.Url,
                null,
                InferContainer(networkEvent.Url, networkEvent.MimeType),
                null,
                length,
                ctx)
            {
                IsValidated = DouyinPlayEvidence.IsBrowserPlay(networkEvent),
                BrowserObserved = DouyinPlayEvidence.IsBrowserPlay(networkEvent),
                Evidence = DouyinPlayEvidence.IsBrowserPlay(networkEvent)
                    ? MediaEvidence.BrowserObserved
                    : MediaEvidence.Heuristic,
                ContentIdentity = _session.CurrentContentId is null ? null : "id:" + _session.CurrentContentId
            };

            if (track.Kind == MediaTrackKind.Audio)
                Upsert(_session.AudioCandidates, track);
            else
                Upsert(_session.VideoCandidates, track);

            if (_session.CurrentMode == DouyinContentMode.Unknown &&
                track.Kind is MediaTrackKind.Video or MediaTrackKind.Combined)
                _session.SwitchContent(_session.CurrentContentId, DouyinContentMode.Video);
        }

        return Task.CompletedTask;
    }

    public Task ProcessPageObservationAsync(
        Uri pageUrl,
        string? pageTitle,
        string? pageScriptJson,
        RequestContext context,
        CancellationToken ct)
    {
        lock (_gate)
        {
            if (_session.PageUrl is null)
                BeginSession(pageUrl, _session.SessionId == Guid.Empty ? Guid.NewGuid() : _session.SessionId);

            _session.PageUrl = pageUrl;
            _session.Context = EnrichContext(context);

            var observedId = TryReadIdentity(pageScriptJson);
            var contentId = DouyinIdentity.ResolveContentId(pageUrl, observedId);
            var mode = DouyinContentModeResolver.Resolve(pageScriptJson, pageUrl);
            if (mode == DouyinContentMode.Unknown && contentId is not null)
                mode = DouyinContentMode.Video;

            var modeChanged = mode != DouyinContentMode.Unknown && mode != _session.CurrentMode;
            var idChanged = contentId is not null &&
                            !string.Equals(contentId, _session.CurrentContentId, StringComparison.Ordinal);
            if (modeChanged || idChanged)
                _session.SwitchContent(contentId, mode == DouyinContentMode.Unknown ? _session.CurrentMode : mode);
            else if (contentId is not null)
                _session.CurrentContentId ??= contentId;

            if (!string.IsNullOrWhiteSpace(pageTitle) && string.IsNullOrWhiteSpace(_session.Caption))
                _session.Caption = pageTitle.Trim();

            ApplyObservationJson(pageScriptJson);

            _logger.LogInformation(
                "Douyin observation contentId={Id} mode={Mode} videos={Videos} audios={Audios} images={Images}",
                _session.CurrentContentId, _session.CurrentMode,
                _session.VideoCandidates.Count, _session.AudioCandidates.Count, _session.AlbumImages.Count);
        }

        return Task.CompletedTask;
    }

    public Task CompleteAsync(CancellationToken ct)
    {
        MediaDescriptor? descriptor;
        lock (_gate)
        {
            descriptor = BuildDescriptor();
            if (descriptor is null)
            {
                _failed = true;
                _failureReason = _session.CurrentMode == DouyinContentMode.Album
                    ? "douyin_album_no_images"
                    : "douyin_no_media";
                _logger.LogWarning(
                    "DouyinMediaDetector Failed reason={Reason} (no Generic fallback)",
                    _failureReason);
                return Task.CompletedTask;
            }
        }

        DescriptorsReady?.Invoke(this, [descriptor]);
        return Task.CompletedTask;
    }

    private MediaDescriptor? BuildDescriptor()
    {
        var page = _session.PageUrl;
        if (page is null) return null;
        var ctx = _session.Context;

        if (_session.CurrentMode == DouyinContentMode.Album)
        {
            if (_session.AlbumImages.Count == 0)
                return null;
            var audio = SelectBest(_session.AudioCandidates);
            return new MediaDescriptor(
                SiteIds.Douyin,
                page,
                _session.CurrentContentId,
                MediaContentType.Album,
                null,
                audio,
                _session.AlbumImages.OrderBy(i => i.Index).ToArray(),
                ctx,
                Confidence: 0.9,
                DisplayTitle: _session.Caption)
            {
                SessionId = _session.SessionId
            };
        }

        var video = SelectBest(_session.VideoCandidates);
        if (video is null)
            return null;
        var pairedAudio = SelectBest(_session.AudioCandidates);
        // Prefer combined when audio not separate.
        if (video.Kind == MediaTrackKind.Combined)
            pairedAudio = null;

        return new MediaDescriptor(
            SiteIds.Douyin,
            page,
            _session.CurrentContentId,
            MediaContentType.Video,
            video,
            pairedAudio,
            [],
            ctx,
            Confidence: video.BrowserObserved ? 0.95 : 0.75,
            DisplayTitle: _session.Caption)
        {
            SessionId = _session.SessionId
        };
    }

    private void ApplyObservationJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("caption", out var caption) &&
                caption.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(caption.GetString()))
                _session.Caption = caption.GetString()!.Trim();

            if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
            {
                var list = new List<AlbumImageItem>();
                var index = 0;
                foreach (var item in images.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var raw = item.GetString();
                    if (!Uri.TryCreate(raw, UriKind.Absolute, out var url)) continue;
                    if (IsExcludedAlbumImage(url)) continue;
                    list.Add(new AlbumImageItem(
                        index++,
                        url,
                        null,
                        null,
                        GuessImageFormat(url),
                        _session.Context));
                }

                if (list.Count > 0)
                {
                    if (_session.CurrentMode != DouyinContentMode.Album)
                        _session.SwitchContent(_session.CurrentContentId, DouyinContentMode.Album);
                    _session.AlbumImages.Clear();
                    // Deduplicate by host+path (ignore query noise) keeping first index order.
                    foreach (var img in list.DistinctBy(i => i.Url.GetLeftPart(UriPartial.Path), StringComparer.OrdinalIgnoreCase))
                        _session.AlbumImages.Add(img with { Index = _session.AlbumImages.Count });
                }
            }

            if (root.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in media.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    if (!Uri.TryCreate(item.GetString(), UriKind.Absolute, out var url)) continue;
                    if (IsExcludedAlbumImage(url)) continue;
                    var kind = DouyinPlayEvidence.InferKind(url, null);
                    var track = new MediaTrack(
                        kind == MediaTrackKind.Audio ? "audio" : "media",
                        kind == MediaTrackKind.Audio ? MediaTrackKind.Audio : MediaTrackKind.Combined,
                        url,
                        null,
                        InferContainer(url, null),
                        null,
                        null,
                        _session.Context)
                    {
                        IsValidated = true,
                        Evidence = MediaEvidence.DomObserved,
                        ContentIdentity = _session.CurrentContentId is null ? null : "id:" + _session.CurrentContentId
                    };
                    if (track.Kind == MediaTrackKind.Audio)
                        Upsert(_session.AudioCandidates, track);
                    else if (_session.CurrentMode != DouyinContentMode.Album)
                        Upsert(_session.VideoCandidates, track);
                    else
                        Upsert(_session.AudioCandidates, track with { Kind = MediaTrackKind.Audio, TrackId = "bgm" });
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    private void TryAcceptAlbumNetworkImage(NormalizedNetworkEvent e)
    {
        if (!IsLikelyAlbumImage(e.Url, e.MimeType)) return;
        if (IsExcludedAlbumImage(e.Url)) return;
        if (_session.AlbumImages.Any(i =>
                string.Equals(
                    i.Url.GetLeftPart(UriPartial.Path),
                    e.Url.GetLeftPart(UriPartial.Path),
                    StringComparison.OrdinalIgnoreCase)))
            return;

        _session.AlbumImages.Add(new AlbumImageItem(
            _session.AlbumImages.Count,
            e.Url,
            null,
            null,
            GuessImageFormat(e.Url),
            EnrichContext(e.RequestContext)));
    }

    private void TryAcceptAlbumNetworkAudio(NormalizedNetworkEvent e)
    {
        if (DouyinPlayEvidence.InferKind(e.Url, e.MimeType) != MediaTrackKind.Audio &&
            !DouyinPlayEvidence.IsStrongMime(e.MimeType))
            return;
        if (!DouyinIdentity.IsMediaHost(e.Url) && !DouyinPlayEvidence.LooksLikePlay(e.Url))
            return;
        if (DouyinPlayEvidence.IsTinyMseCrumb(e.Url, e.ContentLength) &&
            !DouyinPlayEvidence.IsBrowserPlay(e))
            return;

        Upsert(_session.AudioCandidates, new MediaTrack(
            "bgm",
            MediaTrackKind.Audio,
            e.Url,
            null,
            InferContainer(e.Url, e.MimeType),
            null,
            e.ContentLength,
            EnrichContext(e.RequestContext))
        {
            BrowserObserved = DouyinPlayEvidence.IsBrowserPlay(e),
            IsValidated = DouyinPlayEvidence.IsBrowserPlay(e),
            Evidence = DouyinPlayEvidence.IsBrowserPlay(e)
                ? MediaEvidence.BrowserObserved
                : MediaEvidence.Heuristic,
            ContentIdentity = _session.CurrentContentId is null ? null : "id:" + _session.CurrentContentId
        });
    }

    private RequestContext EnrichContext(RequestContext context)
    {
        var page = _session.PageUrl;
        if (page is null) return context;
        var referer = string.IsNullOrWhiteSpace(context.Referer) ? page.AbsoluteUri : context.Referer;
        var origin = string.IsNullOrWhiteSpace(context.Origin)
            ? page.GetLeftPart(UriPartial.Authority)
            : context.Origin;
        return context with { Referer = referer, Origin = origin };
    }

    private static void Upsert(List<MediaTrack> list, MediaTrack track)
    {
        var key = track.SourceUrl.GetLeftPart(UriPartial.Path);
        var idx = list.FindIndex(t =>
            string.Equals(t.SourceUrl.GetLeftPart(UriPartial.Path), key, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            var existing = list[idx];
            if ((track.ContentLength ?? 0) >= (existing.ContentLength ?? 0) ||
                (track.BrowserObserved && !existing.BrowserObserved))
                list[idx] = track;
            return;
        }

        list.Add(track);
    }

    private static MediaTrack? SelectBest(IReadOnlyList<MediaTrack> tracks) =>
        tracks
            .OrderByDescending(t => t.BrowserObserved)
            .ThenByDescending(t => t.ContentLength ?? 0)
            .FirstOrDefault();

    private static string? TryReadIdentity(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("identity", out var id) &&
                id.ValueKind == JsonValueKind.String)
                return id.GetString();
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static string? ExtractIdFromUrlPath(Uri url)
    {
        var m = Regex.Match(url.AbsolutePath, @"/(?:video|note)/(?<id>\d{10,})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["id"].Value : null;
    }

    private static string InferContainer(Uri url, string? mime)
    {
        if (url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) return "hls";
        if (url.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)) return "dash";
        if (mime?.Contains("webm", StringComparison.OrdinalIgnoreCase) == true) return "webm";
        if (mime?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true) return "m4a";
        return "mp4";
    }

    private static bool IsLikelyAlbumImage(Uri url, string? mime)
    {
        if (mime?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        return Regex.IsMatch(url.AbsolutePath, @"\.(?:jpg|jpeg|png|webp|heic|avif)(?:$|\?)", RegexOptions.IgnoreCase) ||
               url.Host.Contains("byteimg", StringComparison.OrdinalIgnoreCase) ||
               url.Host.Contains("douyinpic", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcludedAlbumImage(Uri url)
    {
        var full = url.AbsoluteUri;
        return Regex.IsMatch(full,
            @"avatar|emoji|emoticon|badge|logo|sprite|icon|favicon|cover_thumb|thumbnail|aweme-image-basic",
            RegexOptions.IgnoreCase);
    }

    private static string? GuessImageFormat(Uri url)
    {
        var path = url.AbsolutePath.ToLowerInvariant();
        if (path.EndsWith(".webp")) return "webp";
        if (path.EndsWith(".png")) return "png";
        if (path.EndsWith(".avif")) return "avif";
        if (path.EndsWith(".heic")) return "heic";
        return "jpeg";
    }
}
