# 抖音探测器代码打包

- 分支 / 提交：`clean-main` / `7bb7282`
- 生成时间：2026-09-09 17:36:55
- 身份规则：详情 pathId 最高；信息流/弹层/SPA 以 playerId 最高，query 仅 fallback。禁止 pageId||playerId。

## 文件清单

- `src/VideoDownloader.Infrastructure/Detection/Sites/Douyin/DouyinMediaDetector.cs`
- `src/VideoDownloader.Infrastructure/Detection/Sites/Douyin/DouyinInternals.cs`
- `src/VideoDownloader.Infrastructure/Browser/DouyinObservationScript.cs`

---

## DouyinMediaDetector.cs

路径：`src/VideoDownloader.Infrastructure/Detection/Sites/Douyin/DouyinMediaDetector.cs`

```csharp
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
                RecoveryPageUrl = page,
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
        if (t.BrowserObserved) score += 500;
        if (t.Kind == MediaTrackKind.Combined) score += 800;
        if (t.Kind == MediaTrackKind.Video)
            score += hasAudioPair ? 150 : -400;
        if (t.ContentLength is >= MediaResourceSizeFilter.MinProgressiveVideoBytes) score += 200;
        if (t.ContentLength is >= 1L * 1024 * 1024) score += 50;
        if (t.ContentLength is > 0 and < MediaResourceSizeFilter.MinDisplayBytes) score -= 500;
        // Demote ultra-low Douyin quality crumbs (br/qs) that often yield ~200KB shells.
        score += DouyinPlayEvidence.ScorePlayQualityHint(t.SourceUrl);
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
                        IsValidated = true,
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
        // Unknown size cannot prove ownership when duration is known — wait for Content-Length.
        if (contentLength is null or < 50_000)
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
```n
---

## DouyinInternals.cs

路径：`src/VideoDownloader.Infrastructure/Detection/Sites/Douyin/DouyinInternals.cs`

```csharp
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

    public static string? ExtractAwemeIdFromPath(Uri pageUrl)
    {
        var m = VideoIdPath.Match(pageUrl.AbsolutePath);
        return m.Success ? m.Groups["id"].Value : null;
    }

    public static string? ExtractAwemeId(Uri pageUrl)
    {
        return ExtractAwemeIdFromPath(pageUrl) ?? ExtractIdFromQuery(pageUrl);
    }

    public static string? ExtractIdFromQuery(Uri pageUrl)
    {
        foreach (var pair in pageUrl.Query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2) continue;
            var key = Uri.UnescapeDataString(parts[0]);
            if (!new[] { "modal_id", "aweme_id", "item_id", "video_id", "__vid" }
                .Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            var raw = Uri.UnescapeDataString(parts[1]);
            if (Regex.IsMatch(raw, @"^\d{10,}$")) return raw;
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
        var pathId = ExtractAwemeIdFromPath(pageUrl);
        var observed = ExtractIdFromIdentity(observedIdentity);
        // Dedicated /video|/note pages: path id wins (block ad-player observation hijack).
        if (pathId is not null)
            return pathId;
        // Feed / stamped modal_id: prefer live player observation over lagging query id.
        return observed ?? ExtractIdFromQuery(pageUrl);
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

    /// <summary>
    /// Known-too-small progressive objects (~200KB shells without moov) must not be download variants.
    /// </summary>
    public static bool IsSuspiciousTinyProgressive(MediaTrack track) =>
        !track.IsMseTrack &&
        track.Kind == MediaTrackKind.Combined &&
        track.ContentLength is > 0 and < MediaResourceSizeFilter.MinProgressiveVideoBytes;

    /// <summary>Soft quality hint from Douyin CDN query (br/qs). Higher is better.</summary>
    public static int ScorePlayQualityHint(Uri url)
    {
        var q = url.Query;
        var score = 0;
        var br = MatchQueryLong(q, "br");
        if (br is > 0)
        {
            if (br < 600) score -= 400;
            else if (br >= 1200) score += 80;
        }

        var qs = MatchQueryLong(q, "qs");
        // qs=12 often accompanies tiny preview objects in feed.
        if (qs is >= 10) score -= 300;
        return score;
    }

    private static long? MatchQueryLong(string query, string key)
    {
        var m = Regex.Match(query, $@"[?&]{key}=(\d+)", RegexOptions.IgnoreCase);
        return m.Success && long.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

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
    /// <summary>Active player duration in seconds from page observation (when known).</summary>
    public double? ObservedDurationSec { get; set; }
    public RequestContext Context { get; set; } = RequestContext.CreateEmpty();
    public CancellationTokenSource Lifetime { get; private set; } = new();

    public List<MediaTrack> VideoCandidates { get; } = [];
    public List<MediaTrack> AudioCandidates { get; } = [];
    public List<AlbumImageItem> AlbumImages { get; } = [];
    /// <summary>Other-aweme progressive seen in this session only.</summary>
    public Dictionary<string, List<MediaTrack>> ParkedByContentId { get; } =
        new(StringComparer.Ordinal);
    /// <summary>Progressive CDN path → aweme id within this session only.</summary>
    public Dictionary<string, string> ProgressiveOwnerByResourceKey { get; } =
        new(StringComparer.Ordinal);

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
        {
            Caption = null;
            ObservedDurationSec = null;
        }
        CurrentContentId = contentId ?? CurrentContentId;
        if (mode != DouyinContentMode.Unknown)
            CurrentMode = mode;
        VideoCandidates.Clear();
        AudioCandidates.Clear();
        AlbumImages.Clear();
    }

    /// <summary>Cancel async work and wipe every field belonging to this session.</summary>
    public void Destroy()
    {
        try { Lifetime.Cancel(); }
        catch (ObjectDisposedException) { /* already torn down */ }
        Lifetime.Dispose();
        Lifetime = new CancellationTokenSource();
        Reset();
    }

    public void Reset()
    {
        SessionId = Guid.Empty;
        PageUrl = null;
        CurrentContentId = null;
        CurrentMode = DouyinContentMode.Unknown;
        Caption = null;
        ObservedDurationSec = null;
        Context = RequestContext.CreateEmpty();
        VideoCandidates.Clear();
        AudioCandidates.Clear();
        AlbumImages.Clear();
        ParkedByContentId.Clear();
        ProgressiveOwnerByResourceKey.Clear();
    }
}
```n
---

## DouyinObservationScript.cs

路径：`src/VideoDownloader.Infrastructure/Browser/DouyinObservationScript.cs`

```csharp
namespace VideoDownloader.Infrastructure.Browser;

/// <summary>Douyin-only page observation (album + video). Not shared with TikTok/Generic.</summary>
internal static class DouyinObservationScript
{
    internal const string Body = """
          const visible = el => {
            const style=getComputedStyle(el);
            if(style.visibility==='hidden'||style.display==='none') return 0;
            const r = el.getBoundingClientRect();
            const area = Math.max(0, Math.min(r.bottom, innerHeight) - Math.max(r.top, 0)) *
                   Math.max(0, Math.min(r.right, innerWidth) - Math.max(r.left, 0));
            if(area<=0) return 0;
            if(style.opacity==='0'){
              if((el.tagName==='VIDEO'||el.tagName==='AUDIO') && (el.currentSrc||el.src)) return area;
              return 0;
            }
            return area;
          };
          const ids = ['videoId','video_id','aweme_id','modal_id','itemId','item_id','id'];
          const pageKey = url => {
            const u = new URL(url, location.href);
            const query = ids.filter(k => u.searchParams.has(k)).map(k => [k,u.searchParams.get(k)]);
            return u.origin + u.pathname + JSON.stringify(query);
          };
          const playerRecord = active => {
            const seen=new WeakSet();let budget=160;
            const find=(value,depth)=>{
              if(!value||typeof value!=='object'||value instanceof Node||seen.has(value)||depth>4||--budget<0)return null;
              seen.add(value);
              if((value.id||value.aweme_id||value.itemId||value.videoId) &&
                 (value.video||value.playAddr||value.play_addr||value.images||value.image_list) &&
                 (value.desc||value.description||value.title||value.images||value.image_list)) return value;
              for(const key of ['item','itemInfo','aweme','awemeInfo','data','videoData','props','children']){
                const child=value[key];
                if(Array.isArray(child)){for(const c of child.slice(0,8)){const r=find(c,depth+1);if(r)return r;}}
                else {const r=find(child,depth+1);if(r)return r;}
              }
              return null;
            };
            for(let el=active,i=0;el&&i<12;el=el.parentElement,i++){
              for(const key of Object.keys(el)){
                let props=null;
                if(key.startsWith('__reactProps$'))props=el[key];
                else if(key.startsWith('__reactFiber$'))props=el[key]?.memoizedProps;
                else if(key==='__vueParentComponent')props=el[key]?.props;
                const record=find(props,0);if(record)return record;
              }
            }
            return null;
          };
          const isLiveMedia = el => {
            if(!(el instanceof HTMLMediaElement)) return false;
            const src=String(el.currentSrc||el.src||'');
            if(/^https?:/i.test(src) && (/\.flv([?#]|$)/i.test(src) || /\/flv\//i.test(src))) return true;
            const duration=el.duration;
            if(Number.isFinite(duration) && duration>0) return false;
            if(duration===Infinity) return true;
            let seekEnd=0;
            try{ if(el.seekable && el.seekable.length>0) seekEnd=el.seekable.end(el.seekable.length-1); }catch{}
            if(Number.isFinite(seekEnd) && seekEnd>0) return false;
            return false;
          };
          const activePlayer=()=>{
            const players=[...document.querySelectorAll('video,audio')].filter(e=>visible(e)>0 && !isLiveMedia(e));
            players.sort((a,b)=>Number(!b.paused)-Number(!a.paused)||visible(b)-visible(a));
            return players[0];
          };
          const collectDouyinImages = record => {
            const urls=[];
            const push=v=>{
              if(typeof v==='string' && /^https?:/i.test(v) &&
                 (/\.(jpg|jpeg|png|webp)([?#]|$)/i.test(v) || /(?:byteimg|douyinpic).*\/(?:tos-|obj\/|image)/i.test(v)))
                urls.push(v);
              else if(v&&typeof v==='object'){
                for(const k of ['urlList','url_list','download_url_list','display_image','origin','url']){
                  const child=v[k];
                  if(Array.isArray(child)) { const first=child.find(u=>typeof u==='string' && /^https?:/i.test(u)); if(first) push(first); }
                  else if(typeof child==='string') push(child);
                }
              }
            };
            for(const key of ['images','image_list','imageList','image_post_info','imagePost','photos','image_infos']){
              const block=record?.[key];
              if(Array.isArray(block)) block.forEach(push);
              else if(block?.images) block.images.forEach(push);
              else if(block?.image_list) block.image_list.forEach(push);
            }
            return [...new Set(urls)];
          };
          const collectDouyinPlayUrls = record => {
            const urls=[];
            const push=v=>{
              if(typeof v==='string' && /^https?:/i.test(v) &&
                 !/\.(jpg|jpeg|png|webp|gif|svg)([?#]|$)/i.test(v) &&
                 !/(?:byteimg|douyinpic)\./i.test(v))
                urls.push(v);
              else if(v&&typeof v==='object'){
                for(const k of ['urlList','url_list','playAddr','play_addr','downloadAddr','download_addr','uri','url']){
                  const child=v[k];
                  if(Array.isArray(child)) child.forEach(push);
                  else push(child);
                }
              }
            };
            if(!record) return urls;
            push(record.video||{});
            push(record.video?.play_addr||record.video?.playAddr);
            push(record.video?.download_addr||record.video?.downloadAddr);
            push(record.music||{});
            return [...new Set(urls)];
          };
          const findAwemeRecordById = expectedId => {
            if(!expectedId) return null;
            const roots=[];
            const push=v=>{if(v&&typeof v==='object')roots.push(v);};
            try{
              const el=document.getElementById('__UNIVERSAL_DATA_FOR_REHYDRATION__');
              if(el?.textContent) push(JSON.parse(el.textContent));
            }catch{}
            try{push(window.__UNIVERSAL_DATA_FOR_REHYDRATION__);}catch{}
            const seen=new WeakSet(); let budget=1200;
            const find=(value,depth)=>{
              if(!value||typeof value!=='object'||value instanceof Node||seen.has(value)||depth>12||--budget<0) return null;
              seen.add(value);
              const id = value.aweme_id||value.itemId||value.videoId||value.id||value.modal_id||value.note_id;
              const hasVideo = !!(value.video||value.playAddr||value.play_addr);
              if(hasVideo && String(id)===String(expectedId)) return value;
              if(Array.isArray(value)){ for(const c of value.slice(0,40)){const r=find(c,depth+1); if(r) return r;} }
              else { for(const c of Object.values(value)){const r=find(c,depth+1); if(r) return r;} }
              return null;
            };
            for(const root of roots){ const r=find(root,0); if(r) return r; }
            return null;
          };
          const pathAwemeId = () => (location.pathname.match(/\/(?:video|note)\/(\d{10,})/)||[])[1] || null;
          const queryAwemeId = () => (location.search.match(/(?:modal_id|aweme_id|item_id)=(\d{10,})/)||[])[1] || null;
          const playerAwemeId = (active, record) => {
            let explicit = '';
            const recordId = record ? String(record.aweme_id||record.itemId||record.videoId||record.id||'') : '';
            if (active) {
              for (let scope=active,i=0; scope && i<8; i++, scope=scope.parentElement) {
                for (const attr of ['data-e2e-vid','data-video-id','data-aweme-id','data-item-id']) {
                  const value = scope.getAttribute(attr);
                  if (value && !explicit) explicit = value;
                }
                const wrap = (scope.id||'').match(/^xgwrapper-\d+-(\d{10,})$/);
                if (wrap && !explicit) explicit = wrap[1];
                if (explicit) break;
              }
            }
            const fromExplicit = (String(explicit).match(/(\d{10,})/)||[])[1] || '';
            const fromRecord = (recordId.match(/(\d{10,})/)||[])[1] || '';
            return fromExplicit || fromRecord || null;
          };
          // Dedicated /video|/note: pathId wins. Feed/search/home/modal/SPA: playerId wins; query is fallback only.
          // Never prefer query/page id over the active player (stale modal_id freezes identity across swipes).
          const resolveAwemeId = (active, record) => {
            const pathId = pathAwemeId();
            if (pathId) return pathId;
            return playerAwemeId(active, record) || queryAwemeId() || null;
          };
          const observeDouyinAlbum = () => {
            let record=null;
            const active = activePlayer();
            const currentRecord=playerRecord(active);
            const expectedId=resolveAwemeId(active, currentRecord);
            if(!expectedId) return null;
            const roots=[];
            const push=v=>{if(v&&typeof v==='object')roots.push(v);};
            try{
              const el=document.getElementById('__UNIVERSAL_DATA_FOR_REHYDRATION__');
              if(el?.textContent) push(JSON.parse(el.textContent));
            }catch{}
            try{push(window.__UNIVERSAL_DATA_FOR_REHYDRATION__);}catch{}
            const seen=new WeakSet(); let budget=900;
            const find=(value,depth)=>{
              if(!value||typeof value!=='object'||value instanceof Node||seen.has(value)||depth>12||--budget<0) return null;
              seen.add(value);
              const hasImages = !!(value.images||value.image_list||value.image_post_info||value.imagePost||value.image_infos);
              const id = value.aweme_id||value.itemId||value.videoId||value.id||value.modal_id||value.note_id;
              if(hasImages && String(id)===expectedId) return value;
              if(Array.isArray(value)){ for(const c of value.slice(0,40)){const r=find(c,depth+1); if(r) return r;} }
              else { for(const c of Object.values(value)){const r=find(c,depth+1); if(r) return r;} }
              return null;
            };
            for(const root of roots){ record=find(root,0); if(record) break; }
            if(!record){
              if(!/\/note\//i.test(location.pathname)) return null;
              const imgs=[...document.querySelectorAll('img')].filter(e=>{
                const r=e.getBoundingClientRect();
                const src=e.currentSrc||e.src||'';
                return r.width>120 && r.height>120 && visible(e)>0 && /^https?:/i.test(src) &&
                  !/avatar|emoji|emoticon|badge|logo/i.test(src);
              }).slice(0,24);
              const audio=[...document.querySelectorAll('audio,video')].find(e=>/^https?:/i.test(e.currentSrc||e.src||''));
              if(imgs.length<1) return null;
              const id=expectedId;
              const caption=(document.querySelector('[data-e2e="browse-video-desc"],.desc')?.textContent
                || document.title || '').trim().replace(/\s*[_|].*抖音.*$/u,'').trim();
              const media=[]; if(audio) media.push(audio.currentSrc||audio.src);
              return { type:'vd-video-identity', identity: id ? (location.host+':content:'+id) : pageKey(location.href),
                caption, href:location.href, media, images:imgs.map(e=>e.currentSrc||e.src), album:true };
            }
            const images=collectDouyinImages(record);
            if(images.length<1) return null;
            const music=record.music||{};
            const audioUrls=[];
            const pushAudio=v=>{
              if(typeof v==='string' && /^https?:/i.test(v) && !/\.(jpg|jpeg|png|webp|gif)([?#]|$)/i.test(v)) audioUrls.push(v);
              else if(v&&typeof v==='object'){
                for(const k of ['playUrl','play_url','uri','url','urlList','url_list']){
                  const c=v[k]; if(Array.isArray(c)) c.forEach(pushAudio); else pushAudio(c);
                }
              }
            };
            pushAudio(music.play_url||music.playUrl||music);
            const id=String(record.aweme_id||record.itemId||record.videoId||record.id||expectedId||'');
            const caption=String(record.desc||record.description||record.title||'').trim();
            return { type:'vd-video-identity', identity: id ? (location.host+':content:'+id) : pageKey(location.href),
              caption, href:location.href, media:[...new Set(audioUrls)], images, album:true };
          };
          window.__vdObserve = () => {
            const album = observeDouyinAlbum();
            if (album?.images?.length > 0) return album;
            const active = activePlayer();
            if (!active) return null;
            const record=playerRecord(active);
            const recordId=record ? String(record.aweme_id||record.itemId||record.videoId||record.id||'') : '';
            let caption = record ? String(record.desc||record.description||record.title||'').trim() : '';
            for (let scope=active,i=0; scope && i<8; i++, scope=scope.parentElement) {
              const desc = scope.querySelector('[data-e2e="browse-video-desc"],[data-e2e="video-desc"],[data-e2e="detail-desc"]');
              if (desc && !caption) caption = (desc.textContent||'').trim();
              if (caption) break;
            }
            const pathId = pathAwemeId();
            const playerId = playerAwemeId(active, record);
            const queryId = queryAwemeId();
            const awemeId = resolveAwemeId(active, record);
            if(!awemeId) return null;
            // Feed without player/query and without dedicated path cannot claim a stable work id.
            if(!pathId && !playerId && !queryId) return null;
            const samePlayer = !playerId || playerId === awemeId;
            const dataRecord=findAwemeRecordById(awemeId) || (recordId===awemeId ? record : null);
            const fromData=collectDouyinPlayUrls(dataRecord);
            const resourceKey=value=>{try{const u=new URL(value);const i=u.pathname.indexOf('/video/tos/');return i>=0 ? u.pathname.slice(i) : u.origin+u.pathname;}catch{return '';}};
            const fromPlayer=samePlayer ? [active.currentSrc,active.src,...[...active.querySelectorAll('source')].map(e=>e.src)].filter(value=>{
              if(!/^https?:/i.test(value||'')) return false;
              const id=new URL(value).searchParams.get('__vid');
              if(id) return id===awemeId;
              return fromData.some(u=>resourceKey(u)===resourceKey(value));
            }) : [];
            caption=dataRecord ? String(dataRecord.desc||dataRecord.description||dataRecord.title||'').trim() : (samePlayer ? caption : '');
            const durationSec=(Number.isFinite(active.duration)&&active.duration>0)?active.duration:null;
            return { type:'vd-video-identity', identity:'content:'+awemeId, caption, href:location.href,
              media:[...new Set([...fromPlayer, ...fromData])], durationSec };
          };
          window.__vdProbe=()=>{
            const observation=window.__vdObserve(); if(!observation) return null;
            const record=playerRecord(activePlayer());
            const visit=(value,depth,seen,budget)=>{
              if(--budget.n<0||depth>10||value==null)return;
              if(typeof value==='string'){
                if(/^https?:\/\//i.test(value) && !/\.(jpg|jpeg|png|webp|gif|svg)([?#]|$)/i.test(value) &&
                   !/(?:byteimg|douyinpic)\./i.test(value)) observation.media.push(value);
                return;
              }
              if(typeof value!=='object'||value instanceof Node||seen.has(value))return;
              seen.add(value);
              for(const [key,child] of Object.entries(value)){
                if(/cover|avatar|thumbnail|subtitle|music_cover|dynamic_cover/i.test(key))continue;
                visit(child,depth+1,seen,budget);
              }
            };
            const observedId=(observation.identity.match(/(\d{10,})/)||[])[1];
            const recordId=record ? String(record.aweme_id||record.itemId||record.videoId||record.id||'') : '';
            if(record && observedId && recordId===observedId){
              if(!observation.caption) observation.caption=String(record.desc||record.description||record.title||'').trim();
              visit(record.video||record,0,new WeakSet(),{n:800});
              visit(record.music||{},0,new WeakSet(),{n:200});
            }
            if(!observation.images?.length){
              const album=observeDouyinAlbum();
              if(album?.images?.length && album.identity===observation.identity){
                observation.images=album.images; observation.album=true;
                for(const u of album.media||[]) observation.media.push(u);
              }
            }
            observation.media=[...new Set(observation.media)];
            return observation;
          };
          let scheduled=false;
          const scan=()=>{ if(scheduled) return; scheduled=true; queueMicrotask(()=>{ scheduled=false; const r=window.__vdObserve(); if(r){ try{ chrome.webview.postMessage(r);}catch{}} }); };
          new MutationObserver(scan).observe(document,{childList:true,subtree:true,attributes:true,characterData:true});
          document.addEventListener('playing',scan,true);
          document.addEventListener('loadedmetadata',scan,true);
          setInterval(scan,1500); scan();
        """;
}
```n