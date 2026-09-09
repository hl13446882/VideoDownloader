using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Sites;
using VideoDownloader.Infrastructure.Diagnostics;

namespace VideoDownloader.Infrastructure.Detection;

/// <summary>
/// Routes page detection to an exclusive site detector or Generic Unified pipeline.
/// Exclusive sites never fall back to Unified/Generic.
/// </summary>
public sealed class RoutedMediaDetectionPipeline : IMediaDetectionPipeline
{
    private readonly UnifiedMediaPipeline _unified;
    private readonly ISiteDetectionRouter _router;
    private readonly IExclusiveSiteMediaDetectorResolver _detectors;
    private readonly ILogger<RoutedMediaDetectionPipeline> _logger;
    private readonly object _gate = new();

    private DetectionSession _session = new();
    private IExclusiveSiteMediaDetector? _active;
    private SiteKind _kind = SiteKind.Other;
    private Uri? _page;
    private IReadOnlyList<DetectedVideo> _lastBuilt = [];
    private bool _exclusiveCompleted;

    public RoutedMediaDetectionPipeline(
        UnifiedMediaPipeline unified,
        ISiteDetectionRouter router,
        IExclusiveSiteMediaDetectorResolver detectors,
        ILogger<RoutedMediaDetectionPipeline> logger)
    {
        _unified = unified;
        _router = router;
        _detectors = detectors;
        _logger = logger;
        _unified.VideoDetected += (_, v) =>
        {
            if (_active is null) VideoDetected?.Invoke(this, v);
        };
        _unified.VideoUpdated += (_, v) =>
        {
            if (_active is null) VideoUpdated?.Invoke(this, v);
        };
        _unified.PageProbed += (_, list) =>
        {
            if (_active is null) PageProbed?.Invoke(this, list);
        };
    }

    public Guid SessionId
    {
        get
        {
            lock (_gate)
                return _active is not null ? _session.Id : _unified.SessionId;
        }
    }

    public bool IsCompleted
    {
        get
        {
            lock (_gate)
                return _active is not null
                    ? _exclusiveCompleted || _session.Phase == DetectionPhase.Completed
                    : _unified.IsCompleted;
        }
    }

    public event EventHandler<DetectedVideo>? VideoDetected;
    public event EventHandler<DetectedVideo>? VideoUpdated;
    public event EventHandler<IReadOnlyList<DetectedVideo>>? PageProbed;

    public void Clear()
    {
        lock (_gate)
        {
            ClearExclusiveUnlocked();
            _session.Cancel();
            _session = new();
            _page = null;
            _kind = SiteKind.Other;
            _lastBuilt = [];
            _exclusiveCompleted = false;
            _unified.Clear();
        }
    }

    public void UpdateCaption(Guid sessionId, string caption)
    {
        lock (_gate)
        {
            if (_active is null)
                _unified.UpdateCaption(sessionId, caption);
        }
    }

    public IDiscoveryScope? BeginDiscovery(Guid sessionId)
    {
        lock (_gate)
        {
            if (_active is not null)
            {
                if (sessionId != _session.Id) return null;
                var lease = _session.TryEnter();
                return lease is null ? null : new ExclusiveScope(this, sessionId, lease);
            }

            return _unified.BeginDiscovery(sessionId);
        }
    }

    public Task ProcessAsync(NormalizedNetworkEvent normalized, CancellationToken ct)
    {
        IExclusiveSiteMediaDetector? exclusive;
        lock (_gate)
        {
            var page = normalized.PageUrl ?? _page;
            if (page is not null)
                EnsureRouteUnlocked(page);

            exclusive = _active;
            if (exclusive is not null)
            {
                if (normalized.SessionId != Guid.Empty && normalized.SessionId != _session.Id)
                    return Task.CompletedTask;
            }
        }

        if (exclusive is not null)
            return exclusive.ProcessNetworkAsync(normalized, ct);
        return _unified.ProcessAsync(normalized, ct);
    }

    public Task ProbePageAsync(
        Uri pageUrl,
        string? pageTitle,
        string? pageScriptJson,
        RequestContext context,
        CancellationToken ct,
        bool runExternal = false)
    {
        IExclusiveSiteMediaDetector? exclusive;
        lock (_gate)
        {
            EnsureRouteUnlocked(pageUrl);
            exclusive = _active;
        }

        if (exclusive is not null)
            return exclusive.ProcessPageObservationAsync(pageUrl, pageTitle, pageScriptJson, context, ct);
        return _unified.ProbePageAsync(pageUrl, pageTitle, pageScriptJson, context, ct, runExternal);
    }

    public async Task CompleteDiscoveryAsync(CancellationToken ct)
    {
        IExclusiveSiteMediaDetector? exclusive;
        DetectionSession session;
        lock (_gate)
        {
            exclusive = _active;
            session = _session;
        }

        HangProbe.Mark("route.complete.begin", exclusive?.Name ?? "unified");
        if (exclusive is null)
        {
            await _unified.CompleteDiscoveryAsync(ct).ConfigureAwait(false);
            HangProbe.Mark("route.complete.unified.end");
            return;
        }

        // Finish detector first so UI gets results even if CDP leases linger.
        HangProbe.Mark("route.complete.detector.begin", exclusive.Name);
        await exclusive.CompleteAsync(ct).ConfigureAwait(false);
        HangProbe.Mark("route.complete.detector.end", exclusive.Name);

        lock (_gate)
        {
            if (!ReferenceEquals(session, _session) || !ReferenceEquals(exclusive, _active))
            {
                HangProbe.Mark("route.complete.staleSession");
                return;
            }
            if (!_exclusiveCompleted)
            {
                _exclusiveCompleted = true;
                if (exclusive.Failed)
                {
                    _logger.LogWarning(
                        "Exclusive detection failed site={Site} detector={Detector} reason={Reason} (GenericPipeline=NotUsed)",
                        exclusive.Site, exclusive.Name, exclusive.FailureReason);
                    _lastBuilt = [];
                    PageProbed?.Invoke(this, []);
                }
                else if (_lastBuilt.Count == 0)
                {
                    _logger.LogInformation(
                        "Exclusive detection completed with no descriptors site={Site} detector={Detector}",
                        exclusive.Site, exclusive.Name);
                    PageProbed?.Invoke(this, []);
                }
                else
                {
                    PageProbed?.Invoke(this, _lastBuilt);
                }
            }
        }

        // Best-effort seal; never block forever on open discovery leases.
        try
        {
            Task idle;
            lock (_gate)
                idle = session.CompleteAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            HangProbe.Mark("route.complete.idle.begin");
            await idle.WaitAsync(timeout.Token).ConfigureAwait(false);
            HangProbe.Mark("route.complete.idle.end");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            HangProbe.Mark("route.complete.idle.timeout");
            _logger.LogInformation("Exclusive session idle wait ended; results already emitted or session replaced");
            lock (_gate)
                session.Cancel();
        }
        HangProbe.Mark("route.complete.end");
    }

    private void EnsureRouteUnlocked(Uri pageUrl)
    {
        var kind = _router.Resolve(pageUrl);
        var detector = kind == SiteKind.Other ? null : _detectors.Resolve(pageUrl);
        var sameHost = _page is not null &&
                       string.Equals(_page.Host, pageUrl.Host, StringComparison.OrdinalIgnoreCase);
        var sameRoute = sameHost && _kind == kind && ReferenceEquals(_active, detector);
        var sameWork = sameRoute && SameExclusiveWork(_page!, pageUrl, kind);

        if (sameWork)
        {
            _page = pageUrl;
            return;
        }

        // Host / site / work identity changed — reset exclusive state.
        if (_active is not null || detector is not null || !sameHost)
        {
            ClearExclusiveUnlocked();
            _session.Cancel();
            _session = new();
            _lastBuilt = [];
            _exclusiveCompleted = false;
            _unified.Clear();
        }

        _page = pageUrl;
        _kind = kind;
        _active = detector;

        if (_active is not null)
        {
            _active.DescriptorsReady -= OnDescriptorsReady;
            _active.DescriptorsReady += OnDescriptorsReady;
            _active.BeginSession(pageUrl, _session.Id);
            _logger.LogInformation(
                "[DetectionRouter] Site={Site} Detector={Detector} Exclusive=true GenericPipeline=Bypassed page={Path}",
                _router.Describe(kind), _active.Name, pageUrl.AbsolutePath);
        }
        else
        {
            _logger.LogInformation(
                "[DetectionRouter] Site=Other Detector=UnifiedMediaPipeline Exclusive=false GenericPipeline=Active page={Path}",
                pageUrl.AbsolutePath);
        }
    }

    private static bool SameExclusiveWork(Uri previous, Uri next, SiteKind kind)
    {
        if (kind == SiteKind.Other)
            return string.Equals(previous.AbsoluteUri, next.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

        return kind switch
        {
            SiteKind.YouTube => string.Equals(ExtractYouTubeId(previous), ExtractYouTubeId(next), StringComparison.Ordinal),
            SiteKind.Bilibili => string.Equals(ExtractBilibiliId(previous), ExtractBilibiliId(next), StringComparison.OrdinalIgnoreCase),
            SiteKind.Douyin => string.Equals(ExtractDigitId(previous), ExtractDigitId(next), StringComparison.Ordinal),
            SiteKind.TikTok => string.Equals(ExtractDigitId(previous), ExtractDigitId(next), StringComparison.Ordinal),
            _ => string.Equals(previous.AbsoluteUri, next.AbsoluteUri, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static string? ExtractYouTubeId(Uri page)
    {
        if (page.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
            return page.AbsolutePath.Trim('/').Split('/').FirstOrDefault();
        var v = System.Text.RegularExpressions.Regex.Match(page.Query, @"[?&]v=([^&]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (v.Success) return v.Groups[1].Value;
        var shorts = System.Text.RegularExpressions.Regex.Match(page.AbsolutePath, @"/shorts/([^/?#]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return shorts.Success ? shorts.Groups[1].Value : null;
    }

    private static string? ExtractBilibiliId(Uri page)
    {
        var bv = System.Text.RegularExpressions.Regex.Match(page.AbsolutePath, @"/video/(BV[\w]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return bv.Success ? bv.Groups[1].Value : null;
    }

    private static string? ExtractDigitId(Uri page)
    {
        var path = System.Text.RegularExpressions.Regex.Match(page.AbsolutePath, @"/(?:video|note)/(?<id>\d{10,})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (path.Success) return path.Groups["id"].Value;
        foreach (var key in new[] { "modal_id=", "item_id=", "aweme_id=" })
        {
            var idx = page.Query.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var start = idx + key.Length;
            var end = page.Query.IndexOf('&', start);
            var raw = end < 0 ? page.Query[start..] : page.Query[start..end];
            if (System.Text.RegularExpressions.Regex.IsMatch(raw, @"^\d{10,}$"))
                return raw;
        }
        return null;
    }

    private void ClearExclusiveUnlocked()
    {
        if (_active is not null)
        {
            _active.DescriptorsReady -= OnDescriptorsReady;
            _active.Clear();
            _active = null;
        }

        foreach (var d in _detectors.All)
            d.Clear();
    }

    private void OnDescriptorsReady(object? sender, IReadOnlyList<MediaDescriptor> descriptors)
    {
        List<DetectedVideo> videos;
        lock (_gate)
        {
            videos = descriptors
                .Select(d => MediaDescriptorMapper.ToDetectedVideo(d, _session.Id))
                .ToList();
            _lastBuilt = videos;
        }

        foreach (var video in videos)
            VideoDetected?.Invoke(this, video);
    }

    private sealed class ExclusiveScope(RoutedMediaDetectionPipeline owner, Guid sessionId, IDisposable lease) : IDiscoveryScope
    {
        private bool _disposed;

        public IDiscoveryScope? Fork()
        {
            lock (owner._gate)
            {
                if (_disposed || sessionId != owner._session.Id || owner._active is null) return null;
                var child = owner._session.TryEnter(continuation: true);
                return child is null ? null : new ExclusiveScope(owner, sessionId, child);
            }
        }

        public Task ProcessAsync(NormalizedNetworkEvent network, CancellationToken ct)
        {
            lock (owner._gate)
            {
                if (_disposed || sessionId != owner._session.Id || owner._active is null)
                    return Task.CompletedTask;
                return owner._active.ProcessNetworkAsync(network, ct);
            }
        }

        public Task SubmitAsync(Uri page, string json, RequestContext context, CancellationToken ct)
        {
            lock (owner._gate)
            {
                if (_disposed || sessionId != owner._session.Id || owner._active is null)
                    return Task.CompletedTask;
                return owner._active.ProcessPageObservationAsync(page, null, json, context, ct);
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_disposed) return;
                _disposed = true;
                lease.Dispose();
            }
        }
    }
}
