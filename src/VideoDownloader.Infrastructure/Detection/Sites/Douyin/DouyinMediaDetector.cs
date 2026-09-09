using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Detection.Sites.Douyin;

/// <summary>
/// Exclusive Douyin detector — one process-wide instance, one active session.
/// Interception/bind state lives only inside the current session.
/// <see cref="BeginSession"/> atomically destroys the previous session on this instance.
/// Network/DOM/async results must match <see cref="DouyinDetectionSession.SessionId"/>.
/// </summary>
public sealed class DouyinMediaDetector : IExclusiveSiteMediaDetector
{
    private readonly ILogger<DouyinMediaDetector> _logger;
    private readonly DouyinDetectionSession _session = new();
    private readonly object _gate = new();
    private bool _failed;
    private string? _failureReason;
    /// <summary>True after a matching page observation sealed the current aweme (not merely page URL id).</summary>
    private bool _observationSealed;

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
            BeginSessionUnlocked(pageUrl, sessionId);
    }

    private void BeginSessionUnlocked(Uri pageUrl, Guid sessionId)
    {
        // 1–6: cancel/wipe old session. 7–9: open the only active run on this singleton.
        DestroySessionUnlocked();
        _session.SessionId = sessionId;
        _session.PageUrl = pageUrl;
        var id = DouyinIdentity.ExtractAwemeId(pageUrl);
        _session.SwitchContent(id, DouyinContentMode.Unknown);
        _logger.LogInformation(
            "[DetectionRouter] Site=Douyin Detector={Detector} Exclusive=true GenericPipeline=Bypassed session={Session} page={Path}",
            Name, sessionId, pageUrl.AbsolutePath);
    }

    public void Clear()
    {
        lock (_gate)
            DestroySessionUnlocked();
    }

    /// <inheritdoc />
    public void HardClear()
    {
        lock (_gate)
            DestroySessionUnlocked();
    }

    private void DestroySessionUnlocked()
    {
        _session.Destroy();
        _failed = false;
        _failureReason = null;
        _observationSealed = false;
    }

    private bool IsCurrentSession(Guid eventSessionId) =>
        _session.SessionId != Guid.Empty &&
        (eventSessionId == Guid.Empty || eventSessionId == _session.SessionId);

    public Task ProcessNetworkAsync(NormalizedNetworkEvent networkEvent, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_session.PageUrl is null || !IsCurrentSession(networkEvent.SessionId))
                return Task.CompletedTask;
            if (networkEvent.StatusCode is not (200 or 206 or null))
                return Task.CompletedTask;

            if (_session.CurrentMode != DouyinContentMode.Album &&
                (DouyinPlayEvidence.IsNonMediaMime(networkEvent.MimeType) ||
                 !DouyinPlayEvidence.IsPlayableUrl(networkEvent.Url)))
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

            if (DouyinPlayEvidence.IsNonDownloadableHost(networkEvent.Url))
                return Task.CompletedTask;

            if (!DouyinIdentity.IsMediaHost(networkEvent.Url) &&
                !DouyinPlayEvidence.LooksLikePlay(networkEvent.Url) &&
                !DouyinPlayEvidence.IsBrowserPlay(networkEvent))
                return Task.CompletedTask;

            // Reject (but park progressive) candidates that encode a different aweme id.
            var urlId = DouyinIdentity.ExtractIdFromQuery(networkEvent.Url) ??
                        ExtractIdFromUrlPath(networkEvent.Url);
            if (_session.CurrentContentId is not null &&
                urlId is not null &&
                !string.Equals(urlId, _session.CurrentContentId, StringComparison.Ordinal))
            {
                ParkOtherWorkProgressive(networkEvent, urlId);
                _logger.LogInformation(
                    "Douyin skip preload/other work media current={Current} other={Other} host={Host}",
                    _session.CurrentContentId, urlId, networkEvent.Url.Host);
                return Task.CompletedTask;
            }

            var kind = DouyinPlayEvidence.InferKind(networkEvent.Url, networkEvent.MimeType);
            var isMse = DouyinPlayEvidence.IsMseAdaptivePath(networkEvent.Url);
            var length = DouyinPlayEvidence.GetEntityLength(networkEvent);

            // Prior work's progressive must not be claimed by the new session after Reenter.
            if (!isMse &&
                kind is MediaTrackKind.Combined or MediaTrackKind.Unknown &&
                TryGetForeignProgressiveOwner(networkEvent.Url, out var foreignOwner))
            {
                ParkOtherWorkProgressive(networkEvent, foreignOwner);
                _logger.LogInformation(
                    "[DouyinOwnership] reject foreign progressive current={Current} owner={Owner} host={Host} path={Path}",
                    _session.CurrentContentId, foreignOwner, networkEvent.Url.Host,
                    TruncatePath(networkEvent.Url.AbsolutePath));
                return Task.CompletedTask;
            }

            var ownerTag = ResolveNetworkContentIdentity(urlId, isMse, length, networkEvent.Url);
            // Session-local only: after this run owns a progressive, ignore further anonymous preloads.
            if (ownerTag is null &&
                urlId is null &&
                _observationSealed &&
                HasOwnedProgressive())
            {
                var key = ResourceKey(networkEvent.Url);
                var known = _session.VideoCandidates.Any(t =>
                    string.Equals(ResourceKey(t.SourceUrl), key, StringComparison.Ordinal));
                if (!known)
                {
                    _logger.LogInformation(
                        "Douyin skip unbound preload after owned progressive host={Host} path={Path}",
                        networkEvent.Url.Host, TruncatePath(networkEvent.Url.AbsolutePath));
                    return Task.CompletedTask;
                }
            }

            if (ownerTag is null &&
                urlId is null &&
                _observationSealed &&
                !isMse &&
                _session.ObservedDurationSec is > 0 &&
                length is > 0 &&
                !FitsObservedDuration(length))
            {
                _logger.LogInformation(
                    "[DouyinOwnership] reject unbound progressive duration mismatch duration={Duration}s len={Len} host={Host} path={Path}",
                    _session.ObservedDurationSec, length, networkEvent.Url.Host,
                    TruncatePath(networkEvent.Url.AbsolutePath));
                return Task.CompletedTask;
            }

            var ctx = EnrichContext(networkEvent.RequestContext);
            // Never promote MSE tracks to Combined — even if mime says video.
            var trackKind = kind switch
            {
                MediaTrackKind.Audio => MediaTrackKind.Audio,
                MediaTrackKind.Video => MediaTrackKind.Video,
                _ when isMse => MediaTrackKind.Video,
                _ => MediaTrackKind.Combined
            };
            var track = new MediaTrack(
                trackKind == MediaTrackKind.Audio ? "audio" : "video",
                trackKind,
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
                // Prefer explicit URL aweme id; otherwise bind only after observation sealed
                // the work (feed CDN progressive rarely embeds aweme ids).
                ContentIdentity = ownerTag,
                IsMseTrack = isMse
            };

            _logger.LogInformation(
                "[DouyinDetect] session={Session} host={Host} path={Path} candidateKind={Kind} mse={Mse} status={Status} len={Len}",
                _session.SessionId,
                networkEvent.Url.Host,
                TruncatePath(networkEvent.Url.AbsolutePath),
                isMse ? (trackKind == MediaTrackKind.Audio ? "MseAudioTrack" : "MseVideoTrack")
                      : (trackKind == MediaTrackKind.Combined ? "ProgressiveMuxed" : trackKind.ToString()),
                isMse,
                isMse ? "deferred" : "accepted",
                length);

            if (track.Kind == MediaTrackKind.Audio)
                Upsert(_session.AudioCandidates, track);
            else
            {
                Upsert(_session.VideoCandidates, track);
                if (!isMse &&
                    track.Kind == MediaTrackKind.Combined &&
                    track.ContentIdentity is { Length: > 0 } identity &&
                    identity.StartsWith("id:", StringComparison.Ordinal))
                {
                    RememberProgressiveOwner(networkEvent.Url, identity["id:".Length..]);
                }
            }

            // MSE-only observations must not force Video mode away from Album.
            if (_session.CurrentMode == DouyinContentMode.Unknown &&
                track.Kind is MediaTrackKind.Video or MediaTrackKind.Combined &&
                !isMse)
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
                BeginSessionUnlocked(pageUrl, _session.SessionId == Guid.Empty ? Guid.NewGuid() : _session.SessionId);

            // Stale observation after Reenter must not mutate the new session.
            if (_session.SessionId == Guid.Empty)
                return Task.CompletedTask;

            _session.PageUrl = pageUrl;
            _session.Context = EnrichContext(context);

            var observedId = TryReadIdentity(pageScriptJson);
            var pathId = DouyinIdentity.ExtractAwemeIdFromPath(pageUrl);
            var observationId = DouyinIdentity.ExtractIdFromIdentity(observedId);
            if (pathId is not null && observationId is not null && pathId != observationId)
            {
                _logger.LogInformation(
                    "[DouyinOwnership] reject observation on dedicated page path={Path} observed={Observed}",
                    pathId, observationId);
                return Task.CompletedTask;
            }

            var queryId = DouyinIdentity.ExtractIdFromQuery(pageUrl);
            if (pathId is null &&
                queryId is not null &&
                observationId is not null &&
                queryId != observationId)
            {
                _logger.LogInformation(
                    "[DouyinOwnership] prefer observation over stamped queryId query={Query} observed={Observed}",
                    queryId, observationId);
            }

            var contentId = DouyinIdentity.ResolveContentId(pageUrl, observedId);
            var mode = DouyinContentModeResolver.Resolve(pageScriptJson, pageUrl);
            if (mode == DouyinContentMode.Unknown && contentId is not null)
                mode = DouyinContentMode.Video;

            var modeChanged = mode != DouyinContentMode.Unknown && mode != _session.CurrentMode;
            var idChanged = contentId is not null &&
                            !string.Equals(contentId, _session.CurrentContentId, StringComparison.Ordinal);
            if (modeChanged || idChanged)
            {
                _session.SwitchContent(contentId, mode == DouyinContentMode.Unknown ? _session.CurrentMode : mode);
                // New work / mode: allow another Complete attempt and keep collecting.
                _failed = false;
                _failureReason = null;
                _observationSealed = false;
            }
            else if (contentId is not null)
                _session.CurrentContentId ??= contentId;

            var durationSec = TryReadDurationSec(pageScriptJson);
            if (durationSec is > 0)
                _session.ObservedDurationSec = durationSec;

            // Matching observation for the active aweme — unlocks binding of id-less CDN progressive.
            if (contentId is not null &&
                string.Equals(contentId, _session.CurrentContentId, StringComparison.Ordinal))
            {
                _observationSealed = true;
                AdoptParked(contentId);
            }

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
        Guid sessionId;
        lock (_gate)
        {
            sessionId = _session.SessionId;
            if (sessionId == Guid.Empty)
                return Task.CompletedTask;

            descriptor = BuildDescriptor();
            if (descriptor is null)
            {
                _failed = true;
                var hasMseOnly = _session.CurrentMode != DouyinContentMode.Album &&
                                 _session.VideoCandidates.Any(t =>
                                     t.IsMseTrack || DouyinPlayEvidence.IsMseVideoPath(t.SourceUrl));
                _failureReason = _session.CurrentMode == DouyinContentMode.Album
                    ? "douyin_album_no_images"
                    : hasMseOnly
                        ? "douyin_mse_only"
                        : "douyin_no_media";
                _logger.LogWarning(
                    "[DouyinSelect] session={Session} selected=none reason={Reason} candidates={Count} (no Generic fallback)",
                    _session.SessionId, _failureReason, _session.VideoCandidates.Count);
                return Task.CompletedTask;
            }

            _failed = false;
            _failureReason = null;
            if (descriptor.MediaId is { Length: > 0 } mediaId && descriptor.Video is not null)
                RememberProgressiveOwner(descriptor.Video.SourceUrl, mediaId);
            _logger.LogInformation(
                "[DouyinSelect] session={Session} selected={Host}{Path} kind={Kind} formats={Formats} reason=progressive_muxed owner={Owner}",
                _session.SessionId,
                descriptor.Video?.SourceUrl.Host,
                TruncatePath(descriptor.Video?.SourceUrl.AbsolutePath ?? ""),
                descriptor.Video?.Kind,
                descriptor.Formats.Count, descriptor.MediaId);
        }

        // Drop result if a newer BeginSession already replaced this run.
        lock (_gate)
        {
            if (_session.SessionId != sessionId)
                return Task.CompletedTask;
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

        var video = SelectBestVideo(_session.VideoCandidates.Where(BelongsToCurrent).ToArray(), _session.AudioCandidates.Where(BelongsToCurrent).ToArray());
        if (video is null)
            return null;
        var pairedAudio = SelectBest(_session.AudioCandidates.Where(BelongsToCurrent).ToArray());
        // Muxed progressive already carries audio — do not remux a second track.
        if (video.Kind == MediaTrackKind.Combined)
            pairedAudio = null;

        _logger.LogInformation(
            "Douyin selected video host={Host} kind={Kind} gateway={Gateway} browser={Browser} len={Len} audio={Audio}",
            video.SourceUrl.Host, video.Kind,
            DouyinPlayEvidence.IsPlayGateway(video.SourceUrl),
            video.BrowserObserved, video.ContentLength,
            pairedAudio?.SourceUrl.Host);

        var formats = BuildFormatLadder(_session.VideoCandidates.Where(BelongsToCurrent).ToArray(), page);
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
            SessionId = _session.SessionId,
            Formats = formats
        };
    }

    private IReadOnlyList<MediaVariant> BuildFormatLadder(IReadOnlyList<MediaTrack> videos, Uri page)
    {
        var recovery = page;
        if (_session.CurrentContentId is { Length: > 0 } contentId &&
            !page.AbsolutePath.Contains("/video/", StringComparison.OrdinalIgnoreCase) &&
            !page.AbsolutePath.Contains("/note/", StringComparison.OrdinalIgnoreCase) &&
            DouyinIdentity.ExtractIdFromQuery(page) is null)
            recovery = new Uri($"https://www.douyin.com/video/{Uri.EscapeDataString(contentId)}");

        var list = new List<MediaVariant>();
        foreach (var track in videos
                     .Where(t => !DouyinPlayEvidence.IsNonDownloadableHost(t.SourceUrl) &&
                                 !DouyinPlayEvidence.IsLivePullHost(t.SourceUrl) &&
                                 !DouyinPlayEvidence.IsPlayGateway(t.SourceUrl) &&
                                 !t.IsMseTrack &&
                                 !DouyinPlayEvidence.IsMseVideoPath(t.SourceUrl) &&
                                 t.Kind == MediaTrackKind.Combined &&
                                 !DouyinPlayEvidence.IsSuspiciousTinyProgressive(t))
                     .OrderByDescending(t => ScoreTrack(t)))
        {
            list.Add(new MediaVariant(
                track.TrackId,
                null,
                null,
                null,
                track.Container,
                [track])
            {
                RecoveryPageUrl = recovery,
                ContentIdentity = track.ContentIdentity
            });

            _logger.LogInformation(
                "[DouyinVariant] host={Host} path={Path} downloadable=true priority=progressive contentLength={Len}",
                track.SourceUrl.Host, TruncatePath(track.SourceUrl.AbsolutePath), track.ContentLength);
        }
        return list;
    }

    private MediaTrack? SelectBestVideo(IReadOnlyList<MediaTrack> videos, IReadOnlyList<MediaTrack> audios)
    {
        // Hard rule: media-video / MSE tracks are never ordinary download sources.
        var progressive = videos
            .Where(t => !DouyinPlayEvidence.IsNonDownloadableHost(t.SourceUrl) &&
                        !DouyinPlayEvidence.IsLivePullHost(t.SourceUrl) &&
                        !t.IsMseTrack &&
                        !DouyinPlayEvidence.IsMseVideoPath(t.SourceUrl) &&
                        !DouyinPlayEvidence.IsPlayGateway(t.SourceUrl) &&
                        t.Kind == MediaTrackKind.Combined &&
                        !DouyinPlayEvidence.IsSuspiciousTinyProgressive(t) &&
                        FitsObservedDuration(t.ContentLength))
            .OrderByDescending(t => ScoreTrack(t, audios.Count > 0))
            .ToList();

        // When a durable CDN exists, never default to fragile web-prime for the primary pick.
        var durable = progressive
            .Where(t => !t.SourceUrl.Host.Contains("web-prime", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (durable.Count > 0)
            progressive = durable;

        // Prefer known large objects over unknown-length crumbs when both exist.
        var sized = progressive.Where(t => t.ContentLength is >= MediaResourceSizeFilter.MinProgressiveVideoBytes).ToList();
        if (sized.Count > 0)
            return sized[0];
        return progressive.FirstOrDefault();
    }

    private static int ScoreTrack(MediaTrack t, bool hasAudioPair = true)
    {
        if (DouyinPlayEvidence.IsLivePullHost(t.SourceUrl))
            return int.MinValue / 4;
        if (t.IsMseTrack || DouyinPlayEvidence.IsMseAdaptivePath(t.SourceUrl))
            return int.MinValue / 8;
        if (DouyinPlayEvidence.IsSuspiciousTinyProgressive(t))
            return int.MinValue / 16;

        var score = 0;
        if (DouyinPlayEvidence.IsPlayGateway(t.SourceUrl)) score -= 2000;
        if (DouyinPlayEvidence.IsStrongVodHost(t.SourceUrl)) score += 1000;
        // zjcdn progressive downloads reliably; web-prime douyinvod often 403s outside WebView.
        if (t.SourceUrl.Host.Contains("zjcdn", StringComparison.OrdinalIgnoreCase)) score += 500;
        if (t.SourceUrl.Host.Contains("web-prime", StringComparison.OrdinalIgnoreCase)) score -= 900;
        if (t.BrowserObserved) score += 500;
        if (t.Kind == MediaTrackKind.Combined) score += 800;
        if (t.Kind == MediaTrackKind.Video)
            score += hasAudioPair ? 150 : -400;
        if (t.ContentLength is >= MediaResourceSizeFilter.MinProgressiveVideoBytes) score += 200;
        if (t.ContentLength is >= 1L * 1024 * 1024) score += 50;
        if (t.ContentLength is > 0 and < MediaResourceSizeFilter.MinDisplayBytes) score -= 500;
        // Demote ultra-low Douyin quality crumbs (br/qs) that often yield ~200KB shells.
        score += DouyinPlayEvidence.ScorePlayQualityHint(t.SourceUrl);
        // Prefer network-sized objects over unsigned observation crumbs.
        if (t.Evidence == MediaEvidence.DomObserved && t.ContentLength is null) score -= 80;
        if (t.Evidence == MediaEvidence.BrowserObserved) score += 100;
        score += (int)Math.Min(t.ContentLength ?? 0, int.MaxValue) / (1024 * 1024);
        return score;
    }

    private static string TruncatePath(string path) =>
        path.Length <= 96 ? path : path[..96];

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
                    var mediaId = DouyinIdentity.ExtractIdFromQuery(url) ?? ExtractIdFromUrlPath(url);
                    if (mediaId is not null && mediaId != _session.CurrentContentId)
                    {
                        _logger.LogInformation("[DouyinOwnership] reject observation media current={Current} other={Other}", _session.CurrentContentId, mediaId);
                        continue;
                    }
                    if (_session.CurrentMode != DouyinContentMode.Album && mediaId is null &&
                        DouyinIdentity.ExtractIdFromIdentity(TryReadIdentity(json)) != _session.CurrentContentId)
                        continue;
                    if (IsExcludedAlbumImage(url)) continue;
                    if (DouyinPlayEvidence.IsNonDownloadableHost(url)) continue;
                    if (!DouyinPlayEvidence.IsPlayableUrl(url)) continue;
                    var kind = DouyinPlayEvidence.InferKind(url, null);
                    var isMse = DouyinPlayEvidence.IsMseAdaptivePath(url);
                    // Do NOT force media-video into Combined — that was the main fallback bug.
                    var trackKind = kind switch
                    {
                        MediaTrackKind.Audio => MediaTrackKind.Audio,
                        MediaTrackKind.Video => MediaTrackKind.Video,
                        _ when isMse => MediaTrackKind.Video,
                        _ => MediaTrackKind.Combined
                    };
                    var track = new MediaTrack(
                        trackKind == MediaTrackKind.Audio ? "audio" : "media",
                        trackKind,
                        url,
                        null,
                        InferContainer(url, null),
                        null,
                        null,
                        _session.Context)
                    {
                        // Page-sourced CDN needs WebView cookies on download; keep IsValidated
                        // false so availability sampling is not silently skipped.
                        BrowserObserved = !isMse && DouyinPlayEvidence.IsStrongVodHost(url),
                        IsValidated = false,
                        Evidence = MediaEvidence.DomObserved,
                        ContentIdentity = _session.CurrentContentId is null ? null : "id:" + _session.CurrentContentId,
                        IsMseTrack = isMse
                    };
                    if (track.Kind == MediaTrackKind.Audio)
                        Upsert(_session.AudioCandidates, track);
                    else if (_session.CurrentMode == DouyinContentMode.Album)
                    {
                        // Album: ignore stray media-video; keep non-MSE media as possible BGM only when audio.
                    }
                    else if (!isMse)
                        Upsert(_session.VideoCandidates, track);
                    else
                    {
                        Upsert(_session.VideoCandidates, track);
                        _logger.LogInformation(
                            "[DouyinDetect] session={Session} path={Path} candidateKind=MseVideoTrack status=deferred reason=observation_mse",
                            _session.SessionId, TruncatePath(url.AbsolutePath));
                    }
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
            DouyinPlayEvidence.GetEntityLength(e),
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

    private void ParkOtherWorkProgressive(NormalizedNetworkEvent networkEvent, string urlId)
    {
        // Only park muxed progressive — MSE/audio stay out of the adopt path.
        if (DouyinPlayEvidence.IsMseAdaptivePath(networkEvent.Url))
            return;
        var kind = DouyinPlayEvidence.InferKind(networkEvent.Url, networkEvent.MimeType);
        if (kind is not (MediaTrackKind.Combined or MediaTrackKind.Unknown))
            return;

        var track = new MediaTrack(
            "video",
            MediaTrackKind.Combined,
            networkEvent.Url,
            null,
            InferContainer(networkEvent.Url, networkEvent.MimeType),
            null,
            DouyinPlayEvidence.GetEntityLength(networkEvent),
            EnrichContext(networkEvent.RequestContext))
        {
            Evidence = MediaEvidence.Heuristic,
            ContentIdentity = "id:" + urlId,
            IsMseTrack = false
        };

        if (!_session.ParkedByContentId.TryGetValue(urlId, out var list))
        {
            list = [];
            _session.ParkedByContentId[urlId] = list;
        }

        Upsert(list, track);
        // Bound memory within this session.
        if (_session.ParkedByContentId.Count > 24)
        {
            foreach (var stale in _session.ParkedByContentId.Keys.Take(_session.ParkedByContentId.Count - 16).ToList())
                _session.ParkedByContentId.Remove(stale);
        }
    }

    private void AdoptParked(string contentId)
    {
        if (!_session.ParkedByContentId.Remove(contentId, out var parked) || parked.Count == 0)
            return;

        foreach (var track in parked)
        {
            var owned = track with { ContentIdentity = "id:" + contentId };
            if (owned.Kind == MediaTrackKind.Audio)
                Upsert(_session.AudioCandidates, owned);
            else
                Upsert(_session.VideoCandidates, owned);
        }

        _logger.LogInformation(
            "[DouyinOwnership] adopted parked progressive contentId={Id} count={Count}",
            contentId, parked.Count);
    }

    /// <summary>
    /// Keep tracks for the active aweme. Unbound CDN objects stay selectable only after they were
    /// explicitly attributed (URL id or post-observation bind). Pre-identity preloads stay out.
    /// </summary>
    private bool BelongsToCurrent(MediaTrack track)
    {
        if (_session.CurrentContentId is null)
            return true;
        if (string.IsNullOrWhiteSpace(track.ContentIdentity))
            return false;
        return track.ContentIdentity == "id:" + _session.CurrentContentId;
    }

    private string? ResolveNetworkContentIdentity(string? urlId, bool isMse, long? contentLength, Uri url)
    {
        if (urlId is not null)
        {
            RememberProgressiveOwner(url, urlId);
            return "id:" + urlId;
        }

        // Page URL alone is not enough — wait for a matching observation so /video/{id}
        // does not inherit anonymous ad preloads. Empty observation media then allows
        // ONE feed progressive CDN object that omits aweme ids.
        if (!_observationSealed || _session.CurrentContentId is null || isMse)
            return null;

        if (TryGetForeignProgressiveOwner(url, out _))
            return null;

        // Never bind a second anonymous progressive — next-feed preloads often omit __vid
        // and would otherwise inherit the active work (wrong-id downloads).
        if (HasOwnedProgressive())
            return null;

        // Require a real size before claiming ownership — null-length accepts were rebinding
        // the previous work's CDN object immediately after Reenter/dedupe reset.
        if (contentLength is null or < MediaResourceSizeFilter.MinProgressiveVideoBytes)
            return null;

        // Player duration is a strong cross-check for id-less CDN objects.
        if (!FitsObservedDuration(contentLength))
            return null;

        RememberProgressiveOwner(url, _session.CurrentContentId);
        return "id:" + _session.CurrentContentId;
    }

    private void RememberProgressiveOwner(Uri url, string contentId)
    {
        if (string.IsNullOrWhiteSpace(contentId))
            return;
        var key = ResourceKey(url);
        _session.ProgressiveOwnerByResourceKey[key] = contentId;
        if (_session.ProgressiveOwnerByResourceKey.Count <= 64)
            return;
        foreach (var stale in _session.ProgressiveOwnerByResourceKey.Keys.Take(_session.ProgressiveOwnerByResourceKey.Count - 48).ToList())
            _session.ProgressiveOwnerByResourceKey.Remove(stale);
    }

    private bool TryGetForeignProgressiveOwner(Uri url, out string foreignOwner)
    {
        foreignOwner = "";
        if (_session.CurrentContentId is null)
            return false;
        if (!_session.ProgressiveOwnerByResourceKey.TryGetValue(ResourceKey(url), out var owner))
            return false;
        if (string.Equals(owner, _session.CurrentContentId, StringComparison.Ordinal))
            return false;
        foreignOwner = owner;
        return true;
    }

    /// <summary>
    /// When the active player duration is known, reject byte sizes that imply an absurd bitrate
    /// for that length (typical wrong-work progressive preload).
    /// </summary>
    private bool FitsObservedDuration(long? contentLength)
    {
        var duration = _session.ObservedDurationSec;
        if (duration is null or < 1.0)
            return true;
        // Unknown length (typical observation playAddr) is allowed; network anonymous bind
        // already requires a real Content-Length before claiming ownership.
        if (contentLength is null or <= 0)
            return true;
        if (contentLength < 50_000)
            return false;

        var bitsPerSec = contentLength.Value * 8.0 / duration.Value;
        // Douyin web progressive is usually ~0.3–16 Mbps. Far outside ⇒ different work.
        return bitsPerSec is >= 250_000 and <= 18_000_000;
    }

    private bool HasOwnedProgressive()
    {
        if (_session.CurrentContentId is null)
            return false;
        var tag = "id:" + _session.CurrentContentId;
        return _session.VideoCandidates.Any(t =>
            t.ContentIdentity == tag &&
            !t.IsMseTrack &&
            t.Kind == MediaTrackKind.Combined &&
            !DouyinPlayEvidence.IsSuspiciousTinyProgressive(t));
    }

    private static string ResourceKey(Uri url)
    {
        var index = url.AbsolutePath.IndexOf("/video/tos/", StringComparison.Ordinal);
        return index >= 0 ? url.AbsolutePath[index..] : url.GetLeftPart(UriPartial.Path);
    }

    private static void Upsert(List<MediaTrack> list, MediaTrack track)
    {
        var key = ResourceKey(track.SourceUrl);
        var idx = list.FindIndex(t =>
            string.Equals(ResourceKey(t.SourceUrl), key, StringComparison.Ordinal));
        if (idx >= 0)
        {
            var existing = list[idx];
            if (track.ContentIdentity is not null && existing.ContentIdentity is null)
                existing = existing with { ContentIdentity = track.ContentIdentity };
            list[idx] = existing;
            if (track.ContentIdentity is null)
                track = track with { ContentIdentity = existing.ContentIdentity };
            if ((track.ContentLength ?? 0) >= (existing.ContentLength ?? 0) ||
                (track.BrowserObserved && !existing.BrowserObserved))
                list[idx] = track;
            return;
        }

        list.Add(track);
    }

    private static MediaTrack? SelectBest(IReadOnlyList<MediaTrack> tracks) =>
        tracks
            .Where(t => (!DouyinPlayEvidence.IsNonDownloadableHost(t.SourceUrl) ||
                         DouyinPlayEvidence.IsMusicPath(t.SourceUrl)) &&
                        !DouyinPlayEvidence.IsLivePullHost(t.SourceUrl))
            .OrderByDescending(t => ScoreTrack(t))
            .FirstOrDefault()
        ?? tracks
            .Where(t => !DouyinPlayEvidence.IsLivePullHost(t.SourceUrl))
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

    private static double? TryReadDurationSec(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("durationSec", out var d))
                return null;
            if (d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var sec) &&
                double.IsFinite(sec) && sec > 0)
                return sec;
            if (d.ValueKind == JsonValueKind.String &&
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out sec) &&
                double.IsFinite(sec) && sec > 0)
                return sec;
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
            @"avatar|emoji|emoticon|badge|logo|sprite|icon|favicon|cover_thumb|(?:^|[?&_/])thumbnail(?:[?&_/]|$)|aweme-image-basic",
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
