using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Sites;
using VideoDownloader.Infrastructure.Detection;

namespace VideoDownloader.Infrastructure.Detection.Sites.YouTube;

/// <summary>Exclusive YouTube detector — owns SABR rejection and videoplayback admission.</summary>
public sealed class YouTubeMediaDetector : IExclusiveSiteMediaDetector
{
    private readonly ILogger<YouTubeMediaDetector> _logger;
    private readonly YouTubeYtDlpExtractor _ytdlp;
    private readonly IProbeMethodStats _probeStats;
    private readonly object _gate = new();
    private Guid _sessionId;
    private Uri? _pageUrl;
    private string? _contentId;
    private string? _caption;
    private string? _author;
    private RequestContext _context = RequestContext.CreateEmpty();
    private readonly List<MediaTrack> _tracks = [];
    private readonly List<MediaVariant> _formats = [];
    private bool _failed;
    private string? _failureReason;
    private CancellationTokenSource _lifetime = new();
    private long _probeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
    private bool _externalAttempted;
    private bool _externalFinished;
    private TaskCompletionSource? _externalCompletion;
    private bool _completionRequested;
    private bool _successCounted;
    private bool _isLive;

    public YouTubeMediaDetector(
        ILogger<YouTubeMediaDetector> logger,
        YouTubeYtDlpExtractor ytdlp,
        IProbeMethodStats probeStats)
    {
        _logger = logger;
        _ytdlp = ytdlp;
        _probeStats = probeStats;
    }

    public string Name => "YouTubeMediaDetector";
    public SiteKind Site => SiteKind.YouTube;
    public bool Failed => _failed;
    public string? FailureReason => _failureReason;
    public event EventHandler<IReadOnlyList<MediaDescriptor>>? DescriptorsReady;

    public bool Matches(Uri pageUrl) =>
        SiteHosts.MatchesAny(pageUrl, "youtube.com", "youtu.be", "youtube-nocookie.com");

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
        ct.ThrowIfCancellationRequested();
        Guid session;
        CancellationToken lifetime;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (_pageUrl is null || _successCounted ||
                (e.SessionId != Guid.Empty && e.SessionId != _sessionId)) return Task.CompletedTask;

            if (e.PageUrl is { } eventPage)
            {
                var eventId = ExtractVideoId(eventPage);
                if (eventId is not null && _contentId is not null &&
                    !string.Equals(eventId, _contentId, StringComparison.Ordinal))
                    return Task.CompletedTask;
            }
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
            _isLive |= YouTubeLiveMedia.IsLiveUrl(e.Url);

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
                ContentIdentity = _contentId is null ? null : "id:" + _contentId,
                ProbeMethod = ProbeMethods.NetworkMedia
            });
            session = _sessionId;
            lifetime = _lifetime.Token;
        }
        PublishAvailable(session, lifetime, ct);
        return Task.CompletedTask;
    }

    public Task ProcessPageObservationAsync(
        Uri pageUrl, string? pageTitle, string? pageScriptJson, RequestContext context, CancellationToken ct) =>
        ProcessPageObservationAsync(pageUrl, pageTitle, pageScriptJson, context, ct, runExternal: true);

    public async Task ProcessPageObservationAsync(
        Uri pageUrl, string? pageTitle, string? pageScriptJson, RequestContext context, CancellationToken ct,
        bool runExternal)
    {
        ct.ThrowIfCancellationRequested();
        var urlContentId = ExtractVideoId(pageUrl);
        var scriptContentId = ReadId(pageScriptJson);
        if (urlContentId is not null && scriptContentId is not null &&
            !string.Equals(urlContentId, scriptContentId, StringComparison.Ordinal)) return;
        var incomingContentId = urlContentId ?? scriptContentId;
        Uri resolveUrl;
        RequestContext enriched;
        Guid session;
        CancellationToken lifetime;
        TaskCompletionSource? completion = null;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (_pageUrl is null) BeginSession(pageUrl, _sessionId == Guid.Empty ? Guid.NewGuid() : _sessionId);
            if (_successCounted) return;
            if (incomingContentId is not null && _contentId is not null &&
                !string.Equals(incomingContentId, _contentId, StringComparison.Ordinal)) return;

            _pageUrl = pageUrl;
            _context = Enrich(context);
            _contentId ??= incomingContentId;
            // Observation caption (player response) beats a lagged DocumentTitle/tab title.
            ApplyMediaJson(pageScriptJson);
            if (string.IsNullOrWhiteSpace(_caption) && !string.IsNullOrWhiteSpace(pageTitle))
                _caption = pageTitle.Trim();
            resolveUrl = _pageUrl;
            enriched = _context;
            session = _sessionId;
            lifetime = _lifetime.Token;
            if (runExternal && !_externalAttempted && !_isLive)
            {
                _externalAttempted = true;
                _externalCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!string.IsNullOrWhiteSpace(_contentId) && _ytdlp.IsAvailable)
                    completion = _externalCompletion;
                else
                {
                    _externalFinished = true;
                    _externalCompletion.TrySetResult();
                }
            }
        }

        PublishAvailable(session, lifetime, ct);
        if (completion is null) return;

        using var resolveCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime);
        var resolveToken = resolveCancellation.Token;
        Diagnostics.HangProbe.Mark("youtube.ytdlp.begin", $"{resolveUrl.AbsoluteUri} cookies={enriched.Cookies.Count}");
        try
        {
            resolveToken.ThrowIfCancellationRequested();
            var videos = await _ytdlp.ResolveAsync(resolveUrl, enriched, resolveToken).ConfigureAwait(false);
            Diagnostics.HangProbe.Mark("youtube.ytdlp.end", $"count={videos.Count}");
            lock (_gate)
            {
                resolveToken.ThrowIfCancellationRequested();
                if (!IsCurrentUnlocked(session, lifetime) || _successCounted) return;

                foreach (var v in videos.Where(v =>
                             string.IsNullOrWhiteSpace(_contentId) ||
                             string.Equals(v.SiteContentId, _contentId, StringComparison.Ordinal) ||
                             string.IsNullOrWhiteSpace(v.SiteContentId)))
                {
                    _isLive |= v.IsLive;
                    if (string.IsNullOrWhiteSpace(_caption) && !string.IsNullOrWhiteSpace(v.DisplayTitle))
                        _caption = v.DisplayTitle;
                    _author = ExclusiveAuthorHints.MergeFromMetadata(_author, v.Metadata, _pageUrl);
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
        catch (OperationCanceledException)
        {
            Diagnostics.HangProbe.Mark("youtube.ytdlp.cancel", resolveUrl.AbsoluteUri);
            throw;
        }
        catch (Exception ex)
        {
            Diagnostics.HangProbe.Mark("youtube.ytdlp.fail", ex.GetType().Name + " " + ex.Message);
            _logger.LogInformation(ex, "YouTube exclusive yt-dlp resolve failed (no Generic fallback)");
        }
        finally
        {
            lock (_gate)
            {
                if (IsCurrentUnlocked(session, lifetime)) _externalFinished = true;
                completion.TrySetResult();
            }
        }

        PublishAvailable(session, lifetime, ct);
    }

    private bool IsCurrentUnlocked(Guid session, CancellationToken lifetime) =>
        _pageUrl is not null && session == _sessionId && lifetime == _lifetime.Token &&
        !lifetime.IsCancellationRequested;

    private static bool IsReady(MediaDescriptor descriptor) =>
        descriptor.IsLive ||
        (!(descriptor.Formats.Count == 0 && descriptor.Video?.Kind == MediaTrackKind.Video && descriptor.Audio is null) &&
         VideoDownloader.Core.Detection.DetectionReadiness.IsUsable(
             MediaDescriptorMapper.ToDetectedVideo(descriptor, descriptor.SessionId)));

    private void PublishAvailable(Guid session, CancellationToken lifetime, CancellationToken ct)
    {
        MediaDescriptor? descriptor;
        lock (_gate)
        {
            if (ct.IsCancellationRequested || !IsCurrentUnlocked(session, lifetime) || _successCounted) return;
            if (!_isLive && (!_completionRequested || !_externalFinished)) return;
            descriptor = Build(out var method);
            if (descriptor is { Formats.Count: > 0 } && !IsReady(descriptor))
                descriptor = Build(out method, includeFormats: false);
            if (descriptor is null || !IsReady(descriptor))
            {
                if (!_failed)
                {
                    _failed = true;
                    _failureReason = _ytdlp.LastError ?? "youtube_no_media";
                    _logger.LogWarning("YouTubeMediaDetector Failed reason={Reason} (no Generic fallback)", _failureReason);
                    if (_tracks.Count > 0)
                        _probeStats.Record(SiteIds.YouTube, ProbeMethods.NetworkMedia, false);
                }
                return;
            }

            _failed = false;
            _failureReason = null;
            if (method == ProbeMethods.NetworkMedia)
                _probeStats.ReportMethod(SiteIds.YouTube, method);
            CountSuccess(method);
        }
        if (!lifetime.IsCancellationRequested)
            DescriptorsReady?.Invoke(this, [descriptor]);
    }

    public async Task CompleteAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Guid session;
        CancellationToken lifetime;
        Task? external;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (_pageUrl is null || _successCounted) return;
            _completionRequested = true;
            session = _sessionId;
            lifetime = _lifetime.Token;
            external = _externalCompletion?.Task;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime);
        if (external is not null)
            await external.WaitAsync(cancellation.Token).ConfigureAwait(false);
        cancellation.Token.ThrowIfCancellationRequested();
        PublishAvailable(session, lifetime, ct);
    }

    private MediaDescriptor? Build(out string? winningMethod, bool includeFormats = true)
    {
        winningMethod = null;
        if (_pageUrl is null) return null;
        if (_isLive)
            return new MediaDescriptor(SiteIds.YouTube, _pageUrl, _contentId, MediaContentType.Video,
                null, null, [], _context, 1, _caption, _author) { SessionId = _sessionId, IsLive = true };

        if (includeFormats && _formats.Count > 0)
        {
            var best = _formats
                .Where(v => v.Tracks.Any(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined))
                .OrderByDescending(v => v.Height ?? 0)
                .ThenByDescending(v => v.TotalContentLength ?? v.Bandwidth ?? 0)
                .FirstOrDefault();
            winningMethod = _ytdlp.LastProbeMethod ?? ProbeMethods.YtDlpDefault;
            return new MediaDescriptor(SiteIds.YouTube, _pageUrl, _contentId, MediaContentType.Video,
                best?.Tracks.FirstOrDefault(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined),
                best?.Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Audio),
                [], _context, 0.95, _caption, _author)
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
        winningMethod = ProbeMethods.NetworkMedia;
        if (video is null)
            return new MediaDescriptor(SiteIds.YouTube, _pageUrl, _contentId, MediaContentType.Audio,
                null, audio, [], _context, 0.7, _caption, _author) { SessionId = _sessionId };
        return new MediaDescriptor(SiteIds.YouTube, _pageUrl, _contentId, MediaContentType.Video,
            video,
            video.Kind == MediaTrackKind.Combined ? null : audio,
            [], _context, video.BrowserObserved ? 0.95 : 0.8, _caption, _author) { SessionId = _sessionId };
    }

    private void ApplyMediaJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("isLive", out var live) && live.ValueKind == JsonValueKind.True)
                _isLive = true;
            if (doc.RootElement.TryGetProperty("caption", out var c) && c.ValueKind == JsonValueKind.String)
                _caption = c.GetString()?.Trim() ?? _caption;
            _author = ExclusiveAuthorHints.Merge(_author, ExclusiveAuthorHints.ReadFromObservationJson(json));
            var combinedUrls = new HashSet<string>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("combinedMedia", out var combined) && combined.ValueKind == JsonValueKind.Array)
                foreach (var value in combined.EnumerateArray())
                    if (value.ValueKind == JsonValueKind.String && value.GetString() is { } address)
                        combinedUrls.Add(address);
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
                    var kind = combinedUrls.Contains(url.AbsoluteUri) ? MediaTrackKind.Combined :
                        audio ? MediaTrackKind.Audio : MediaTrackKind.Video;
                    Upsert(new MediaTrack(audio ? "audio" : "video",
                        kind,
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
        _lifetime.Cancel();
        _lifetime.Dispose();
        _lifetime = new();
        _probeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        _sessionId = Guid.Empty;
        _pageUrl = null;
        _contentId = null;
        _caption = null;
        _author = null;
        _context = RequestContext.CreateEmpty();
        _tracks.Clear();
        _formats.Clear();
        _failed = false;
        _failureReason = null;
        _externalAttempted = false;
        _externalFinished = false;
        _externalCompletion = null;
        _completionRequested = false;
        _successCounted = false;
        _isLive = false;
    }

    private void CountSuccess(string? method)
    {
        if (_successCounted) return;
        _successCounted = true;
        // yt-dlp records each client outcome itself, including its actual process duration.
        if (string.IsNullOrWhiteSpace(method) || ProbeDetectors.IsYtDlpMethod(method)) return;
        _probeStats.RecordTimed(SiteIds.YouTube, method, true, System.Diagnostics.Stopwatch.GetElapsedTime(_probeStarted));
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
        if (SiteHosts.Matches(page, "youtu.be"))
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
